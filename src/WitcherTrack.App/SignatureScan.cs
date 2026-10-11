using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WitcherTrack.App;

/// <summary>
/// Finds the game's "not loading" flag by the code that reads it, the way the <c>init</c>
/// block of Meta's LiveSplit autosplitter (metasr on Discord) does, rather than by a fixed
/// offset per build.
/// </summary>
/// <remarks>
/// <para>
/// A per-build offset is exactly as good as the list of builds it was written for, and the
/// game keeps being patched: the Remastered release moved the flag, and a tracker holding
/// only the old table could say nothing better than "unrecognised build". The instructions
/// that touch the flag survive a rebuild far better than its address does, so the
/// autosplitter looks for them instead - a short run of machine code with the bytes that
/// change between builds left as wildcards - and reads the flag's address out of the
/// instruction it found.
/// </para>
/// <para>
/// Everything here is plain arithmetic over bytes handed to it, with no platform attribute
/// and no process involved, for the same reason <see cref="GameBuildDetector"/> is: the
/// matching and the address arithmetic decide which byte of the game's memory the clock
/// believes, and <c>WitcherTrack selftest</c> has to be able to check them on any platform
/// with no game running. <see cref="GameClock"/> supplies the one thing that is not
/// portable, a function that reads the game's memory.
/// </para>
/// </remarks>
internal static class NotLoadingFlag
{
    /// <summary>
    /// The autosplitter's two signatures, in its order. The first one that matches anywhere
    /// wins, which is how LiveSplit's own scanner treats a target with several signatures.
    /// </summary>
    /// <remarks>
    /// Both end on an instruction that addresses the flag relative to the instruction
    /// pointer - <c>mov [rip+x], cl</c> in the older builds, <c>cmp [rip+x], dl</c> in the
    /// Remastered one - and the offset given with each is where that 32-bit displacement
    /// sits inside the match.
    /// </remarks>
    internal static readonly IReadOnlyList<CodeSignature> Signatures =
    [
        CodeSignature.Parse("1.06, 1.32", 9, "48 89 1D ?? ?? ?? ?? 88 0D ?? ?? ?? ?? 84 C0 74"),
        CodeSignature.Parse("Remastered", 24,
            "48 8B 0D ?? ?? ?? ?? 49 3B CD 74 ?? 4D 85 ED 74 ?? 48 85 C9 74 ?? 38 15 ?? ?? ?? ?? 74"),
    ];

    /// <summary>
    /// How many bytes to read for the image's headers: the first page, which holds the DOS
    /// stub, the PE header and the section table of any ordinary executable.
    /// </summary>
    private const int HeaderBytes = 0x1000;

    /// <summary>
    /// Scans a loaded image for <see cref="Signatures"/> and resolves the flag's address.
    /// </summary>
    /// <param name="read">Reads memory; false when any byte of the request is unreadable.</param>
    /// <param name="imageBase">Where the image is loaded.</param>
    /// <param name="imageSize">How many bytes the loaded image spans.</param>
    /// <returns>
    /// The flag's address and which signature found it, or null when neither matched or the
    /// one that did pointed somewhere a flag cannot be.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Only the executable sections are scanned when the headers can be read: the
    /// signatures are machine code, and a match in data would be a coincidence. That is
    /// also forty-odd megabytes instead of a hundred. If the headers cannot be read the
    /// whole image is scanned instead, which is what the autosplitter does every time.
    /// </para>
    /// <para>
    /// The address found is checked before it is believed: it has to land inside the image
    /// and, when the section table is known, inside a writable section, because a loading
    /// flag the game sets at run time cannot live in code or in read-only data. A match
    /// that fails that test is a coincidence, and reading through it would report some
    /// unrelated byte as the loading state without any sign that anything was wrong.
    /// </para>
    /// </remarks>
    internal static LocatedFlag? Locate(MemoryReader read, long imageBase, long imageSize) =>
        Locate(read, imageBase, imageSize, out _);

    /// <inheritdoc cref="Locate(MemoryReader, long, long)"/>
    /// <param name="read">Reads memory; false when any byte of the request is unreadable.</param>
    /// <param name="imageBase">Where the image is loaded.</param>
    /// <param name="imageSize">How many bytes the loaded image spans.</param>
    /// <param name="rejected">
    /// The match that was found and then refused because of where it led, or null. Kept
    /// apart from "nothing matched" because the two call for different fixes: a pattern
    /// that no longer matches wants a new signature, a pattern that matches somewhere a
    /// flag cannot be wants the match looked at - and a report saying "no signature
    /// matched" would send whoever reads it looking for the wrong one.
    /// </param>
    internal static LocatedFlag? Locate(MemoryReader read, long imageBase, long imageSize, out SignatureMatch? rejected)
    {
        rejected = null;
        IReadOnlyList<ImageSection>? sections = null;
        byte[] headers = new byte[(int)Math.Min(HeaderBytes, imageSize)];

        if (read(imageBase, headers))
        {
            sections = ImageSection.ReadTable(headers);
        }

        var ranges = new List<MemoryRange>();

        if (sections is not null)
        {
            foreach (ImageSection section in sections)
            {
                if (section.IsExecutable && section.Rva < imageSize)
                {
                    ranges.Add(new MemoryRange(imageBase + section.Rva, Math.Min(section.Size, imageSize - section.Rva)));
                }
            }
        }

        if (ranges.Count == 0)
        {
            ranges.Add(new MemoryRange(imageBase, imageSize));
        }

        SignatureMatch? match = SignatureScanner.Scan(Signatures, ranges, read);
        if (match is not { } found)
        {
            return null;
        }

        long target = found.Target;
        long rva = target - imageBase;

        bool plausible = rva >= 0 && rva < imageSize &&
            (sections is null || sections.Any(s => s.IsWritable && rva >= s.Rva && rva < s.Rva + s.Size));

        if (!plausible)
        {
            rejected = found;
            return null;
        }

        return new LocatedFlag(target, found.Signature.Label);
    }
}

/// <summary>Where the "not loading" flag is, and which signature said so.</summary>
internal readonly record struct LocatedFlag(long Address, string Label);

/// <summary>Reads <paramref name="buffer"/>.Length bytes at <paramref name="address"/>.</summary>
/// <returns>True only when every byte was read.</returns>
internal delegate bool MemoryReader(long address, Span<byte> buffer);

/// <summary>A span of addresses to scan.</summary>
internal readonly record struct MemoryRange(long Start, long Length)
{
    public long End => Start + Length;
}

/// <summary>
/// One autosplitter signature: a byte pattern with <c>??</c> wildcards, plus where in a
/// match the 32-bit displacement that leads to the target sits.
/// </summary>
internal sealed class CodeSignature
{
    private readonly byte[] _bytes;
    private readonly bool[] _fixed;
    private readonly byte[] _anchor;
    private readonly int _anchorStart;

    private CodeSignature(string label, int offset, byte[] bytes, bool[] isFixed)
    {
        Label = label;
        Offset = offset;
        _bytes = bytes;
        _fixed = isFixed;

        // The longest run of concrete bytes is searched for first, with the vectorised
        // IndexOf the runtime provides, and only where it lands is the whole pattern
        // compared. Most of a forty-megabyte code section never reaches the slow path.
        int bestStart = 0, bestLength = 0;
        for (int i = 0; i < bytes.Length;)
        {
            if (!isFixed[i])
            {
                i++;
                continue;
            }

            int start = i;
            while (i < bytes.Length && isFixed[i])
            {
                i++;
            }

            if (i - start > bestLength)
            {
                bestStart = start;
                bestLength = i - start;
            }
        }

        _anchorStart = bestStart;
        _anchor = bytes[bestStart..(bestStart + bestLength)];
    }

    /// <summary>A name for the builds this signature is known to match, for display.</summary>
    public string Label { get; }

    /// <summary>Where the 32-bit displacement sits, counted from the start of a match.</summary>
    public int Offset { get; }

    /// <summary>The pattern's length in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>
    /// Parses the autosplitter's notation: hexadecimal bytes separated by spaces, with
    /// <c>??</c> for a byte that may be anything.
    /// </summary>
    /// <remarks>
    /// The displacement has to lie inside the pattern, which both of the autosplitter's do.
    /// That lets a match be resolved from the bytes the scan already holds, with no second
    /// read of the game's memory between finding the instruction and believing it.
    /// </remarks>
    public static CodeSignature Parse(string label, int offset, string pattern)
    {
        string[] tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var isFixed = new bool[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "??" or "?")
            {
                continue;
            }

            if (tokens[i].Length != 2 ||
                !byte.TryParse(tokens[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new FormatException($"'{tokens[i]}' in signature '{label}' is neither a hexadecimal byte nor ??");
            }

            isFixed[i] = true;
        }

        if (Array.IndexOf(isFixed, true) < 0)
        {
            throw new FormatException($"signature '{label}' has no fixed byte to look for");
        }

        if (offset < 0 || offset + sizeof(int) > tokens.Length)
        {
            throw new FormatException($"signature '{label}' puts its displacement outside the pattern");
        }

        return new CodeSignature(label, offset, bytes, isFixed);
    }

    /// <summary>Whether the pattern matches <paramref name="data"/> at <paramref name="index"/>.</summary>
    public bool MatchesAt(ReadOnlySpan<byte> data, int index)
    {
        if (index < 0 || index > data.Length - _bytes.Length)
        {
            return false;
        }

        for (int k = 0; k < _bytes.Length; k++)
        {
            if (_fixed[k] && data[index + k] != _bytes[k])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first index at or after <paramref name="start"/> where the whole pattern matches, or -1.</summary>
    public int IndexOf(ReadOnlySpan<byte> data, int start = 0)
    {
        int last = data.Length - _bytes.Length;

        for (int i = Math.Max(start, 0); i <= last;)
        {
            // Every position from i to last, seen through where the anchor would sit.
            int found = data.Slice(i + _anchorStart, last - i + _anchor.Length).IndexOf(_anchor);
            if (found < 0)
            {
                return -1;
            }

            int candidate = i + found;
            if (MatchesAt(data, candidate))
            {
                return candidate;
            }

            i = candidate + 1;
        }

        return -1;
    }

    /// <summary>The displacement inside a match at <paramref name="index"/>, little-endian and signed.</summary>
    public int ReadDisplacement(ReadOnlySpan<byte> data, int index) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.Slice(index + Offset, sizeof(int)));

    /// <summary>
    /// The autosplitter's <c>OnFound</c>: <c>ptr + 4 + rel32</c>, where <c>ptr</c> is the
    /// address of the displacement itself.
    /// </summary>
    /// <remarks>
    /// x64 addresses a RIP-relative operand from the end of the instruction. Both
    /// instructions these signatures stop on end with their displacement, so the end is
    /// four bytes past it. An instruction carrying an immediate after the displacement
    /// would need those bytes added too - the autosplitter's new-game signature uses
    /// <c>+ 5</c> for exactly that reason - and does not apply here.
    /// </remarks>
    public static long ResolveRipRelative(long displacementAddress, int displacement) =>
        displacementAddress + sizeof(int) + displacement;
}

/// <summary>The first match of one signature.</summary>
/// <param name="Index">The signature's position in the list it was scanned with.</param>
/// <param name="Signature">The signature itself.</param>
/// <param name="Address">Where the match starts.</param>
/// <param name="Displacement">The 32-bit displacement read out of the match.</param>
internal readonly record struct SignatureMatch(int Index, CodeSignature Signature, long Address, int Displacement)
{
    /// <summary>The address the matched instruction refers to.</summary>
    public long Target => CodeSignature.ResolveRipRelative(Address + Signature.Offset, Displacement);
}

/// <summary>
/// Scans memory for a list of signatures a chunk at a time, the way LiveSplit's
/// <c>SignatureScanner</c> answers a target with several: the first signature in the list
/// that matches anywhere wins, at its lowest address.
/// </summary>
/// <remarks>
/// <para>
/// One pass serves every signature. Each chunk is searched for every signature still
/// unanswered, and the pass ends early as soon as the first signature is found, since
/// nothing found later could outrank it.
/// </para>
/// <para>
/// Consecutive chunks overlap by one byte less than the longest pattern, so a match that
/// straddles two reads is still seen whole. A chunk that cannot be read is retried one page
/// at a time, so a single guard page in a section costs only itself; an unreadable page is
/// a gap, and nothing is matched across it, because bytes that could not be read are not
/// bytes to compare against.
/// </para>
/// </remarks>
internal static class SignatureScanner
{
    /// <summary>The read size: big enough that a code section is a few dozen reads.</summary>
    internal const int DefaultChunkSize = 1 << 20;

    /// <summary>The size of the pages an unreadable chunk is retried in.</summary>
    internal const int DefaultPageSize = 0x1000;

    /// <summary>Scans <paramref name="ranges"/>, in order, for <paramref name="signatures"/>.</summary>
    /// <returns>The winning match, or null when no signature matched.</returns>
    internal static SignatureMatch? Scan(
        IReadOnlyList<CodeSignature> signatures,
        IEnumerable<MemoryRange> ranges,
        MemoryReader read,
        int chunkSize = DefaultChunkSize,
        int pageSize = DefaultPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var search = new Search(signatures, chunkSize);
        byte[] buffer = new byte[chunkSize];

        foreach (MemoryRange range in ranges)
        {
            for (long at = range.Start; at < range.End && !search.Decided;)
            {
                int length = (int)Math.Min(chunkSize, range.End - at);
                Span<byte> chunk = buffer.AsSpan(0, length);

                if (read(at, chunk))
                {
                    search.Feed(at, chunk);
                }
                else
                {
                    for (long page = at; page < at + length;)
                    {
                        long next = Math.Min(page - (page % pageSize) + pageSize, at + length);
                        Span<byte> piece = buffer.AsSpan(0, (int)(next - page));

                        if (read(page, piece))
                        {
                            search.Feed(page, piece);
                        }

                        page = next;
                    }
                }

                at += length;
            }
        }

        return search.Winner;
    }

    /// <summary>The state of one pass: the first match per signature, and the bytes carried between reads.</summary>
    private sealed class Search
    {
        private readonly IReadOnlyList<CodeSignature> _signatures;
        private readonly SignatureMatch?[] _first;
        private readonly byte[] _window;
        private readonly int _overlap;
        private int _tailLength;
        private long _tailEnd = -1;

        public Search(IReadOnlyList<CodeSignature> signatures, int chunkSize)
        {
            _signatures = signatures;
            _first = new SignatureMatch?[signatures.Count];
            _overlap = signatures.Count == 0 ? 0 : signatures.Max(s => s.Length) - 1;
            _window = new byte[_overlap + chunkSize];
        }

        /// <summary>True once nothing still to be read could change the answer.</summary>
        public bool Decided => _first.Length == 0 || _first[0] is not null;

        public SignatureMatch? Winner => Array.Find(_first, m => m is not null);

        /// <summary>
        /// Searches bytes read at <paramref name="address"/>, continuing the previous read
        /// when they follow on from it directly and starting afresh when they do not.
        /// </summary>
        public void Feed(long address, ReadOnlySpan<byte> bytes)
        {
            int carried = address == _tailEnd ? _tailLength : 0;

            // The carried tail already sits at the front of the window; see the end of this method.
            bytes.CopyTo(_window.AsSpan(carried));
            ReadOnlySpan<byte> window = _window.AsSpan(0, carried + bytes.Length);
            long windowStart = address - carried;

            for (int s = 0; s < _signatures.Count; s++)
            {
                if (_first[s] is not null)
                {
                    continue;
                }

                CodeSignature signature = _signatures[s];
                int at = signature.IndexOf(window);
                if (at >= 0)
                {
                    _first[s] = new SignatureMatch(s, signature, windowStart + at, signature.ReadDisplacement(window, at));
                }
            }

            // A match lying wholly inside the carried bytes was already searched for in the
            // previous read; carrying them again can only ever find it again, and a signature
            // already answered is not searched any more.
            int keep = Math.Min(_overlap, window.Length);
            window[^keep..].CopyTo(_window);
            _tailLength = keep;
            _tailEnd = address + bytes.Length;
        }
    }
}

/// <summary>One entry of a PE image's section table, as loaded.</summary>
/// <param name="Name">The section's name, such as <c>.text</c>.</param>
/// <param name="Rva">Where it starts, relative to the image base.</param>
/// <param name="Size">How many bytes it spans once loaded.</param>
/// <param name="Characteristics">The section's flags.</param>
internal readonly record struct ImageSection(string Name, long Rva, long Size, uint Characteristics)
{
    private const uint ContainsCode = 0x00000020;
    private const uint MemoryExecute = 0x20000000;
    private const uint MemoryWrite = 0x80000000;

    public bool IsExecutable => (Characteristics & (ContainsCode | MemoryExecute)) != 0;

    public bool IsWritable => (Characteristics & MemoryWrite) != 0;

    /// <summary>
    /// Reads the section table out of an image's first bytes, or null when they are not the
    /// headers of a PE image or the table does not fit in them.
    /// </summary>
    /// <remarks>
    /// The size kept is the in-memory one (<c>VirtualSize</c>), not the size on disk: a
    /// data section is routinely longer once loaded than in the file, the zero-filled
    /// remainder being where uninitialised globals live - and the Remastered build's loading
    /// flag is one of them.
    /// </remarks>
    public static IReadOnlyList<ImageSection>? ReadTable(ReadOnlySpan<byte> headers)
    {
        if (headers.Length < 0x40 || headers[0] != (byte)'M' || headers[1] != (byte)'Z')
        {
            return null;
        }

        int pe = BinaryPrimitives.ReadInt32LittleEndian(headers[0x3C..]);
        if (pe < 0 || pe > headers.Length - 24 ||
            BinaryPrimitives.ReadUInt32LittleEndian(headers[pe..]) != 0x00004550) // "PE\0\0"
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(headers[(pe + 6)..]);
        int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(headers[(pe + 20)..]);
        int table = pe + 24 + optionalHeaderSize;

        if (count == 0 || table + (40L * count) > headers.Length)
        {
            return null;
        }

        var sections = new ImageSection[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = headers.Slice(table + (40 * i), 40);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]);

            int nameLength = entry[..8].IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(entry[..(nameLength < 0 ? 8 : nameLength)]);

            sections[i] = new ImageSection(name, rva, virtualSize != 0 ? virtualSize : rawSize, characteristics);
        }

        return sections;
    }
}
