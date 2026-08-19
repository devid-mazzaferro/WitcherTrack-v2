using System.Buffers.Binary;

namespace WitcherTrack.SaveFormat;

/// <summary>
/// Reads the outer container of a Witcher 3 <c>.sav</c> file.
/// </summary>
/// <remarks>
/// <para>
/// The container is a small uncompressed header followed by one or more LZ4 blocks:
/// </para>
/// <code>
/// offset  size  content
/// 0       4     "SNFH"                 container magic
/// 4       4     "FZLC"                 chunked-LZ4 magic
/// 8       4     int32  chunk count
/// 12      4     int32  header size, i.e. the offset where compressed data begins
/// 16      12*n  chunk table: (compressed size, decompressed size, end offset)
/// ...           the uncompressed header region continues up to <c>header size</c>
/// header  ...   the compressed chunks, back to back
/// </code>
/// <para>
/// The decompressed payload is laid out as if it started at <c>header size</c>, because
/// the offsets stored inside the savegame are absolute within the original file. The
/// header bytes are therefore preserved verbatim at the front of the returned buffer.
/// </para>
/// <para>
/// Container layout established from the MIT-licensed
/// <see href="https://github.com/Atvaark/W3SavegameEditor">W3SavegameEditor</see>
/// and verified against real savegames from The Witcher 3 GOTY (next-gen).
/// </para>
/// </remarks>
public static class SaveArchive
{
    private static ReadOnlySpan<byte> ContainerMagic => "SNFH"u8;
    private static ReadOnlySpan<byte> CompressionMagic => "FZLC"u8;

    /// <summary>Fixed size of the container header that precedes the chunk table.</summary>
    private const int FixedHeaderSize = 16;

    /// <summary>Size of a single chunk-table entry.</summary>
    private const int ChunkEntrySize = 12;

    /// <summary>
    /// Describes one compressed chunk.
    /// </summary>
    /// <param name="CompressedSize">Size of the chunk as stored on disk.</param>
    /// <param name="DecompressedSize">Size of the chunk once expanded.</param>
    /// <param name="EndOffset">
    /// End-of-chunk offset recorded by the game. Not needed for decoding and not
    /// validated, because it is zero in some savegames.
    /// </param>
    public readonly record struct ChunkInfo(int CompressedSize, int DecompressedSize, int EndOffset);

    /// <summary>
    /// The result of opening a savegame container.
    /// </summary>
    /// <param name="Payload">
    /// The full savegame image: the uncompressed header region followed by every
    /// decompressed chunk. Offsets stored inside the savegame index into this buffer.
    /// </param>
    /// <param name="HeaderSize">Offset at which the decompressed savegame body begins.</param>
    /// <param name="Chunks">The chunk table as read from the file.</param>
    public sealed record SaveImage(byte[] Payload, int HeaderSize, ChunkInfo[] Chunks);

    /// <summary>
    /// Reads and decompresses a savegame container from disk.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a Witcher 3 savegame.</exception>
    public static SaveImage Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Reads and decompresses a savegame container already held in memory.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a Witcher 3 savegame.</exception>
    public static SaveImage Open(ReadOnlySpan<byte> file)
    {
        if (file.Length < FixedHeaderSize)
        {
            throw new InvalidDataException("File is too small to be a Witcher 3 savegame.");
        }

        if (!file[..4].SequenceEqual(ContainerMagic))
        {
            throw new InvalidDataException("Missing 'SNFH' magic: this is not a Witcher 3 savegame.");
        }

        if (!file.Slice(4, 4).SequenceEqual(CompressionMagic))
        {
            throw new InvalidDataException("Missing 'FZLC' magic: unsupported savegame compression.");
        }

        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(8, 4));
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(12, 4));

        if (chunkCount <= 0 || chunkCount > 4096)
        {
            throw new InvalidDataException($"Implausible chunk count {chunkCount}.");
        }

        int tableEnd = FixedHeaderSize + (chunkCount * ChunkEntrySize);
        if (headerSize < tableEnd || headerSize > file.Length)
        {
            throw new InvalidDataException($"Implausible header size {headerSize} for a {file.Length} byte file.");
        }

        var chunks = new ChunkInfo[chunkCount];
        long totalDecompressed = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            ReadOnlySpan<byte> entry = file.Slice(FixedHeaderSize + (i * ChunkEntrySize), ChunkEntrySize);
            chunks[i] = new ChunkInfo(
                BinaryPrimitives.ReadInt32LittleEndian(entry[..4]),
                BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4)));

            totalDecompressed += chunks[i].DecompressedSize;
        }

        // The savegame body is addressed by absolute offsets, so the uncompressed
        // header region has to stay in place ahead of the decompressed chunks.
        var payload = new byte[headerSize + totalDecompressed];
        file[..headerSize].CopyTo(payload);

        int sourcePosition = headerSize;
        int destinationPosition = headerSize;

        for (int i = 0; i < chunkCount; i++)
        {
            ChunkInfo chunk = chunks[i];

            if (sourcePosition + chunk.CompressedSize > file.Length)
            {
                throw new InvalidDataException($"Chunk {i} runs past the end of the file.");
            }

            int written = Lz4BlockDecoder.Decode(
                file.Slice(sourcePosition, chunk.CompressedSize),
                payload.AsSpan(destinationPosition, chunk.DecompressedSize));

            if (written != chunk.DecompressedSize)
            {
                throw new InvalidDataException(
                    $"Chunk {i} expanded to {written} bytes but the table declares {chunk.DecompressedSize}.");
            }

            sourcePosition += chunk.CompressedSize;
            destinationPosition += chunk.DecompressedSize;
        }

        return new SaveImage(payload, headerSize, chunks);
    }
}
