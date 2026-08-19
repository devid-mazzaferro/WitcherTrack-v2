using System.Buffers.Binary;
using System.Text;

namespace WitcherTrack.SaveFormat;

/// <summary>
/// Navigational index of a decompressed Witcher 3 savegame.
/// </summary>
/// <remarks>
/// <para>
/// Once the container is expanded (see <see cref="SaveArchive"/>) the payload is a
/// <c>SAV3</c> image whose structure is discovered from the back:
/// </para>
/// <code>
/// end - 6   int32  offset of the variable table
/// end - 2   "SE"   end-of-savegame marker
///
/// variableTableOffset - 10   int32  offset of the "NM" section (the name table)
/// variableTableOffset -  6   int32  offset of the "RB" section
///
/// NM section:  "NM" then a MANU block holding every variable name
/// variable table: int32 count, then (int32 offset, int32 size) per variable
/// </code>
/// <para>
/// This type stops at the index: it gives you every variable name and where each
/// variable starts. Decoding the variable tree itself (BS/VL/SS/BLCK tokens) is the
/// next layer and is what turns offsets into quest states, map pins and Gwent cards.
/// </para>
/// <para>
/// Layout established from the MIT-licensed
/// <see href="https://github.com/Atvaark/W3SavegameEditor">W3SavegameEditor</see>
/// and verified against real savegames from The Witcher 3 GOTY (next-gen).
/// </para>
/// </remarks>
public sealed class SavegameIndex
{
    private static ReadOnlySpan<byte> SavegameMagic => "SAV3"u8;

    private SavegameIndex(
        int variableTableOffset,
        int nameSectionOffset,
        int resourceSectionOffset,
        string[] variableNames,
        VariableSlot[] variables)
    {
        VariableTableOffset = variableTableOffset;
        NameSectionOffset = nameSectionOffset;
        ResourceSectionOffset = resourceSectionOffset;
        VariableNames = variableNames;
        Variables = variables;
    }

    /// <summary>Offset of the variable table within the payload.</summary>
    public int VariableTableOffset { get; }

    /// <summary>Offset of the "NM" name-table section.</summary>
    public int NameSectionOffset { get; }

    /// <summary>Offset of the "RB" resource section.</summary>
    public int ResourceSectionOffset { get; }

    /// <summary>
    /// Every variable name used by this savegame, in the order the game stored them.
    /// Variable tokens refer to these by index.
    /// </summary>
    public string[] VariableNames { get; }

    /// <summary>Offset and size of every top-level variable.</summary>
    public VariableSlot[] Variables { get; }

    /// <summary>Position and length of a single variable inside the payload.</summary>
    /// <param name="Offset">Absolute offset within the decompressed payload.</param>
    /// <param name="Size">Declared size in bytes, including any nested variables.</param>
    public readonly record struct VariableSlot(int Offset, int Size);

    /// <summary>
    /// Builds the index for an expanded savegame payload.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload is not a readable SAV3 image.</exception>
    public static SavegameIndex Read(SaveArchive.SaveImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Read(image.Payload, image.HeaderSize);
    }

    /// <summary>
    /// Builds the index for an expanded savegame payload.
    /// </summary>
    /// <param name="payload">The full decompressed image.</param>
    /// <param name="bodyOffset">Offset at which the SAV3 body begins.</param>
    /// <exception cref="InvalidDataException">The payload is not a readable SAV3 image.</exception>
    public static SavegameIndex Read(byte[] payload, int bodyOffset)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var span = payload.AsSpan();

        if (bodyOffset + 4 > payload.Length || !span.Slice(bodyOffset, 4).SequenceEqual(SavegameMagic))
        {
            throw new InvalidDataException("Missing 'SAV3' magic: the decompressed payload is not a savegame body.");
        }

        // --- footer: where the variable table lives ---------------------------
        if (payload.Length < 6)
        {
            throw new InvalidDataException("Payload is too small to contain a savegame footer.");
        }

        int variableTableOffset = BinaryPrimitives.ReadInt32LittleEndian(span[^6..^2]);

        if (span[^2] != (byte)'S' || span[^1] != (byte)'E')
        {
            throw new InvalidDataException("Missing 'SE' end marker: the savegame is truncated or corrupt.");
        }

        if (variableTableOffset < 10 || variableTableOffset >= payload.Length)
        {
            throw new InvalidDataException($"Implausible variable table offset {variableTableOffset}.");
        }

        // --- string table header sits just before the variable table ----------
        int stringTableFooterOffset = variableTableOffset - 10;
        int nameSectionOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(stringTableFooterOffset, 4));
        int resourceSectionOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(stringTableFooterOffset + 4, 4));

        Expect(span, nameSectionOffset, "NM"u8, "name section");
        Expect(span, resourceSectionOffset, "RB"u8, "resource section");

        string[] variableNames = ReadNameTable(span, nameSectionOffset + 2);
        VariableSlot[] variables = ReadVariableTable(span, variableTableOffset);

        return new SavegameIndex(
            variableTableOffset,
            nameSectionOffset,
            resourceSectionOffset,
            variableNames,
            variables);
    }

    /// <summary>
    /// Reads the MANU block that holds every variable name.
    /// </summary>
    /// <remarks>
    /// Layout: <c>"MANU"</c>, int32 count, int32 (unknown), then for each entry a
    /// single length byte followed by that many ASCII characters, and finally an
    /// int32 (unknown) plus the <c>"ENOD"</c> terminator.
    /// </remarks>
    private static string[] ReadNameTable(ReadOnlySpan<byte> span, int offset)
    {
        Expect(span, offset, "MANU"u8, "name table");
        int position = offset + 4;

        int count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(position, 4));
        position += 8; // count, then one unknown int32

        if (count < 0 || count > 1_000_000)
        {
            throw new InvalidDataException($"Implausible variable name count {count}.");
        }

        var names = new string[count];

        for (int i = 0; i < count; i++)
        {
            byte length = span[position++];
            names[i] = Encoding.ASCII.GetString(span.Slice(position, length));
            position += length;
        }

        position += 4; // one unknown int32
        Expect(span, position, "ENOD"u8, "name table terminator");

        return names;
    }

    /// <summary>
    /// Reads the variable table: a count followed by an offset/size pair per variable.
    /// Entries are returned sorted by offset so that consecutive entries describe
    /// consecutive regions of the payload.
    /// </summary>
    private static VariableSlot[] ReadVariableTable(ReadOnlySpan<byte> span, int offset)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));

        if (count < 0 || count > 5_000_000)
        {
            throw new InvalidDataException($"Implausible variable count {count}.");
        }

        var slots = new VariableSlot[count];
        int position = offset + 4;

        for (int i = 0; i < count; i++)
        {
            slots[i] = new VariableSlot(
                BinaryPrimitives.ReadInt32LittleEndian(span.Slice(position, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(span.Slice(position + 4, 4)));

            position += 8;
        }

        Array.Sort(slots, static (left, right) => left.Offset.CompareTo(right.Offset));
        return slots;
    }

    private static void Expect(ReadOnlySpan<byte> span, int offset, ReadOnlySpan<byte> magic, string what)
    {
        if (offset < 0 || offset + magic.Length > span.Length || !span.Slice(offset, magic.Length).SequenceEqual(magic))
        {
            throw new InvalidDataException($"Expected {Encoding.ASCII.GetString(magic)} marker for the {what} at offset {offset}.");
        }
    }
}
