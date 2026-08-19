namespace WitcherTrack.SaveFormat;

/// <summary>
/// Minimal decoder for the LZ4 *block* format (not the framed format).
/// </summary>
/// <remarks>
/// <para>
/// The Witcher 3 stores its savegames as a sequence of raw LZ4 blocks, so a full
/// LZ4 frame implementation is not needed. Decoding is about sixty lines, which is
/// why this is implemented here instead of taking a dependency: the release binary
/// stays free of third-party code and anyone can audit the whole decompression path.
/// </para>
/// <para>
/// Block format, repeated until the input is consumed:
/// <list type="number">
///   <item>one token byte: high nibble = literal length, low nibble = match length</item>
///   <item>if a nibble is 15, additional length bytes follow, summed until one is not 255</item>
///   <item>that many literal bytes, copied verbatim</item>
///   <item>a 16-bit little-endian back-reference offset (absent in the final sequence)</item>
///   <item>the match length plus 4, copied from earlier in the output</item>
/// </list>
/// Matches may overlap the current write position, so the match copy must be
/// byte-by-byte rather than a block copy.
/// </para>
/// </remarks>
public static class Lz4BlockDecoder
{
    /// <summary>The LZ4 minimum match length, which is implicit in the encoded value.</summary>
    private const int MinMatchLength = 4;

    /// <summary>
    /// Decodes one LZ4 block into <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The compressed block.</param>
    /// <param name="destination">
    /// The output buffer. Must be at least as large as the known decompressed size.
    /// </param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    /// <exception cref="InvalidDataException">The block is malformed or truncated.</exception>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int sourceIndex = 0;
        int destinationIndex = 0;

        while (sourceIndex < source.Length)
        {
            byte token = source[sourceIndex++];

            // --- literals -------------------------------------------------
            int literalLength = token >> 4;
            if (literalLength == 15)
            {
                literalLength += ReadLengthExtension(source, ref sourceIndex);
            }

            if (literalLength > 0)
            {
                if (sourceIndex + literalLength > source.Length)
                {
                    throw new InvalidDataException("Truncated LZ4 block: literal run runs past the end of the input.");
                }

                if (destinationIndex + literalLength > destination.Length)
                {
                    throw new InvalidDataException("LZ4 output buffer is too small for the literal run.");
                }

                source.Slice(sourceIndex, literalLength).CopyTo(destination[destinationIndex..]);
                sourceIndex += literalLength;
                destinationIndex += literalLength;
            }

            // The last sequence of a block carries literals only and stops here.
            if (sourceIndex >= source.Length)
            {
                break;
            }

            // --- match ----------------------------------------------------
            if (sourceIndex + 1 >= source.Length)
            {
                throw new InvalidDataException("Truncated LZ4 block: missing match offset.");
            }

            int offset = source[sourceIndex] | (source[sourceIndex + 1] << 8);
            sourceIndex += 2;

            if (offset == 0 || offset > destinationIndex)
            {
                throw new InvalidDataException($"Invalid LZ4 back-reference offset {offset} at output position {destinationIndex}.");
            }

            int matchLength = token & 0x0F;
            if (matchLength == 15)
            {
                matchLength += ReadLengthExtension(source, ref sourceIndex);
            }

            matchLength += MinMatchLength;

            if (destinationIndex + matchLength > destination.Length)
            {
                throw new InvalidDataException("LZ4 output buffer is too small for the match run.");
            }

            // Matches are allowed to overlap the bytes being written, which is how
            // LZ4 encodes runs. A byte-by-byte copy is therefore required.
            int matchIndex = destinationIndex - offset;
            for (int i = 0; i < matchLength; i++)
            {
                destination[destinationIndex++] = destination[matchIndex++];
            }
        }

        return destinationIndex;
    }

    /// <summary>
    /// Reads a variable-length length extension: successive bytes are summed while
    /// each one is 255.
    /// </summary>
    private static int ReadLengthExtension(ReadOnlySpan<byte> source, ref int index)
    {
        int total = 0;
        byte current;

        do
        {
            if (index >= source.Length)
            {
                throw new InvalidDataException("Truncated LZ4 block: incomplete length extension.");
            }

            current = source[index++];
            total += current;
        }
        while (current == byte.MaxValue);

        return total;
    }
}
