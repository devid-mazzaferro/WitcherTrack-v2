using WitcherTrack.Core.Model;

namespace WitcherTrack.Core;

/// <summary>
/// Compares the catalogue against a list kept by hand, and reports what each one has that
/// the other does not.
/// </summary>
/// <remarks>
/// <para>
/// This exists to answer a question the game cannot: which entries are missing. The game
/// only reports quests the player has encountered, so a hunt completed without ever
/// picking up the note that opens its journal entry is invisible — and looking for
/// something absent by staring at a list of what is present does not work.
/// </para>
/// <para>
/// A community list has the names. The catalogue has what the game actually reported.
/// Lining them up by name turns "six are missing" into six names.
/// </para>
/// <para>
/// This only works once the reporter sends the game's localised names: the internal
/// identifiers share nothing with display names — <c>Arbitrator schematic</c> against
/// <c>Diagram: Arbitrator</c> — so a comparison run against identifiers matches nothing at
/// all, which the report says plainly rather than presenting as a total mismatch.
/// </para>
/// </remarks>
public static class ReferenceList
{
    /// <summary>The outcome of a comparison.</summary>
    /// <param name="Matched">Names present on both sides.</param>
    /// <param name="MissingFromCatalog">In the reference list, never reported by the game.</param>
    /// <param name="MissingFromReference">Reported by the game, absent from the reference list.</param>
    public sealed record Comparison(
        IReadOnlyList<string> Matched,
        IReadOnlyList<string> MissingFromCatalog,
        IReadOnlyList<string> MissingFromReference);

    /// <summary>
    /// Reads a delimited list and returns the values of its name column.
    /// </summary>
    /// <remarks>
    /// The first column is used unless a header names one of the usual suspects. Quoted
    /// fields are handled because quest names contain commas.
    /// </remarks>
    public static IReadOnlyList<string> ReadNames(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        string[] preferred = ["questname", "name", "quest", "title", "item"];
        var names = new List<string>();
        int column = 0;
        bool first = true;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] fields = SplitDelimited(line);

            if (first)
            {
                first = false;

                int found = Array.FindIndex(fields, f => preferred.Contains(f.Trim().ToLowerInvariant()));
                if (found >= 0)
                {
                    column = found;
                    continue;
                }
            }

            if (column < fields.Length && !string.IsNullOrWhiteSpace(fields[column]))
            {
                names.Add(fields[column].Trim());
            }
        }

        return names;
    }

    /// <summary>Compares reference names against the display names in the catalogue.</summary>
    public static Comparison Compare(
        IEnumerable<string> referenceNames,
        IEnumerable<CatalogEntry> catalog,
        TrackedKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(referenceNames);
        ArgumentNullException.ThrowIfNull(catalog);

        Dictionary<string, string> reference = Index(referenceNames);

        Dictionary<string, string> mine = Index(catalog
            .Where(e => kind is null || e.Kind == kind)
            .Select(e => e.DisplayName));

        List<string> matched = [.. reference.Where(p => mine.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.OrdinalIgnoreCase)];
        List<string> missingHere = [.. reference.Where(p => !mine.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.OrdinalIgnoreCase)];
        List<string> missingThere = [.. mine.Where(p => !reference.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.OrdinalIgnoreCase)];

        return new Comparison(matched, missingHere, missingThere);
    }

    /// <summary>
    /// Keys names by a form that ignores punctuation, spacing and case, so that
    /// "Diagram: Bear Armor" and "Diagram - Bear armour" line up. The original spelling is
    /// kept as the value, because that is what gets reported back.
    /// </summary>
    private static Dictionary<string, string> Index(IEnumerable<string> names)
    {
        var indexed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string name in names)
        {
            string key = Normalise(name);

            if (key.Length > 0)
            {
                indexed.TryAdd(key, name);
            }
        }

        return indexed;
    }

    private static string Normalise(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;

        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>Splits a comma or semicolon separated line, honouring double quotes.</summary>
    private static string[] SplitDelimited(string line)
    {
        char delimiter = line.Contains(';') && !line.Contains(',') ? ';' : ',';

        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        foreach (char character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == delimiter && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString());
        return [.. fields];
    }
}
