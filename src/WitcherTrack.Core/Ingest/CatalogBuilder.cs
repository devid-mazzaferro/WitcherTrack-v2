using WitcherTrack.Core.Model;

namespace WitcherTrack.Core.Ingest;

/// <summary>
/// Builds the catalogue of trackable entries from a reference dump.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is the denominator: the set of everything a completion run can obtain.
/// It cannot come from an ordinary run, because the game only reports quests the player
/// has already encountered — the totals would grow as you played, which is exactly
/// backwards.
/// </para>
/// <para>
/// So it is built once, from a dump taken on a savegame where everything is unlocked, and
/// then shipped as data. Rebuilding it after a game update is running one command against
/// a fresh log rather than editing a list by hand.
/// </para>
/// <para>
/// Diagrams and formulae need this treatment most: the game reports only what the player
/// knows, so an entry that is absent is either not obtained or does not exist, and only
/// the reference dump can tell the two apart.
/// </para>
/// </remarks>
public static class CatalogBuilder
{
    /// <summary>The result of scanning one or more logs.</summary>
    /// <param name="Entries">The catalogue, sorted by kind then identifier.</param>
    /// <param name="DumpsFound">How many complete dumps were read.</param>
    public sealed record Result(IReadOnlyList<CatalogEntry> Entries, int DumpsFound);

    /// <summary>
    /// Extracts a catalogue from one or more game script logs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every dump found is merged rather than the largest being picked, because a
    /// catalogue is a union by nature: it is the set of everything that exists, and a
    /// single dump does not always contain all of it.
    /// </para>
    /// <para>
    /// Map pins are the reason. The game only enumerates the areas it currently knows
    /// about, so a dump taken outside Toussaint contains no Toussaint pins at all. Loading
    /// a savegame in each region and merging the results is how the map gets covered.
    /// </para>
    /// </remarks>
    public static Result FromScriptLogs(IEnumerable<IEnumerable<string>> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        var merged = new List<ReporterProtocol.Record>();
        int dumps = 0;

        foreach (IEnumerable<string> lines in logs)
        {
            foreach (List<ReporterProtocol.Record> dump in SplitIntoDumps(lines))
            {
                dumps++;
                merged.AddRange(dump);
            }
        }

        return dumps == 0 ? new Result([], 0) : new Result(BuildEntries(merged), dumps);
    }

    /// <summary>Extracts a catalogue from a single game script log.</summary>
    public static Result FromScriptLog(IEnumerable<string> lines) => FromScriptLogs([lines]);

    /// <summary>Groups reporter records into the dumps they belong to.</summary>
    private static List<List<ReporterProtocol.Record>> SplitIntoDumps(IEnumerable<string> lines)
    {
        var dumps = new List<List<ReporterProtocol.Record>>();
        List<ReporterProtocol.Record>? current = null;

        foreach (string line in lines)
        {
            if (!ReporterProtocol.TryParse(line, out ReporterProtocol.Record record))
            {
                continue;
            }

            if (record.IsMeta)
            {
                if (record.Id == "begin")
                {
                    current = [];
                }
                else if (record.Id == "end" && current is not null)
                {
                    dumps.Add(current);
                    current = null;
                }
            }
            else
            {
                current?.Add(record);
            }
        }

        return dumps;
    }

    private static List<CatalogEntry> BuildEntries(List<ReporterProtocol.Record> dump)
    {
        var byId = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

        // Gwent asks for one of each card type rather than one of every card, so the
        // catalogue holds types and each type keeps the indices that satisfy it.
        var gwentTypes = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

        foreach (ReporterProtocol.Record record in dump)
        {
            if (record.Kind is not { } kind)
            {
                continue;
            }

            if (kind == TrackedKind.GwentCard)
            {
                AddGwentType(gwentTypes, record);
                continue;
            }

            // An entry seen in an earlier dump is kept, with one exception: a dump taken
            // with a newer reporter carries the game's own localised names, and an older
            // one does not. Merging in log order would otherwise let whichever dump
            // happened to be listed first decide, silently leaving the whole catalogue on
            // internal identifiers - "Arbitrator schematic" instead of "Diagram:
            // Arbitrator" - purely because of argument order.
            if (byId.TryGetValue(record.Id, out CatalogEntry? existing))
            {
                CatalogEntry updated = existing;

                if (record.DisplayName is { } better && IsIdentifierLike(existing.DisplayName, existing.Id))
                {
                    updated = updated with { DisplayName = better };
                }

                // Same story as the name: an earlier dump might predate the reporter
                // sending coordinates at all, so a later dump's position fills the gap
                // rather than being discarded because something is already on file.
                if (updated.X is null && record.Position is { } position)
                {
                    updated = updated with { X = position.X, Y = position.Y };
                }

                // And again for the world path: an earlier dump might predate the reporter
                // sending it even though it already had coordinates.
                if (updated.World is null && record.WorldPath is { } world)
                {
                    updated = updated with { World = world };
                }

                if (updated != existing)
                {
                    byId[record.Id] = updated;
                }

                continue;
            }

            // Rules are matched against the identifier with only its trailing GUID
            // removed, not against the display name: humanising replaces underscores, so
            // matching on it would silently miss every rule keyed on a real tag.
            string ruleKey = StripGuid(record.Id);
            string displayName = Humanise(record.Id, kind);
            string dlc = record.Dlc ?? PoiDlc(record) ?? SchematicDlc(record) ?? "base";
            string? pinType = PinType(record);

            byId[record.Id] = new CatalogEntry(
                Id: record.Id,
                Kind: kind,
                // The game's own localised name when the reporter sends one, otherwise the
                // identifier tidied up.
                DisplayName: record.DisplayName ?? displayName,
                Dlc: dlc,
                Region: record.Category ?? pinType,
                // Most map pins are not something you finish: signposts, notice boards,
                // blacksmiths. They are tracked but must not sit in a total the run can
                // never reach. The same goes for internal journal entries reported as
                // quests. A pin type can also be clearable in general but not within one
                // particular content pack - Blood and Wine's treasure-hunt map pins being
                // the one case of that so far.
                CountsToward: kind switch
                {
                    TrackedKind.PointOfInterest =>
                        GameData.ClearablePinTypes.Contains(pinType ?? string.Empty)
                        && !GameData.ClearableExceptForDlc.Contains((pinType ?? string.Empty, dlc))
                        && !GameData.NonCountingPoiIds.Contains(record.Id),
                    TrackedKind.Quest =>
                        !GameData.NonCountingQuestTags.Contains(ruleKey),
                    _ => true,
                },
                GroupId: kind == TrackedKind.Quest
                         && GameData.QuestExclusionGroups.TryGetValue(ruleKey, out string? group)
                    ? group
                    : null,
                X: record.Position?.X,
                Y: record.Position?.Y,
                World: record.WorldPath);
        }

        foreach ((string type, CatalogEntry entry) in gwentTypes)
        {
            byId[entry.Id] = entry;
        }

        return
        [
            .. byId.Values
                .OrderBy(static entry => entry.Kind)
                .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Whether a display name is really just the identifier tidied up, rather than a name
    /// the game supplied.
    /// </summary>
    /// <remarks>
    /// <see cref="Humanise"/> only strips a trailing GUID and swaps underscores for
    /// spaces, so comparing on that basis identifies its own output without having to
    /// record a flag alongside every entry.
    /// </remarks>
    private static bool IsIdentifierLike(string displayName, string id) =>
        string.Equals(displayName, Humanise(id, TrackedKind.Quest), StringComparison.Ordinal);

    /// <summary>The pin type a point-of-interest record carries, if any.</summary>
    private static string? PinType(ReporterProtocol.Record record) =>
        record.Kind == TrackedKind.PointOfInterest && record.Extras.Length > 0 ? record.Extras[0] : null;

    /// <summary>
    /// The content pack a point of interest belongs to, when the record itself says so
    /// unambiguously.
    /// </summary>
    /// <remarks>
    /// Blood and Wine is told apart by map area, because Toussaint is a separate world file:
    /// every point reported under that area index belongs to it, no exceptions.
    /// <para>
    /// Hearts of Stone adds its content to the base continent's own world file instead, so
    /// area cannot tell it apart - but its entity identifiers can: every one seen so far
    /// carries <c>ep1_</c> ("episode 1", the game's own internal name for the first
    /// expansion - the same index <see cref="GameData"/> maps through
    /// <c>WT_ContentTypeToString</c> for quests) either as a prefix or, for a handful of map
    /// pins such as <c>lw_nest_ep1_poi_31_mp</c>, as an inner token. Cross-checked against a
    /// player's own count of Hearts of Stone points of interest: this signal picks out thirty-
    /// four where thirty-three were expected, the same one-or-two margin seen reconciling
    /// Blood and Wine.
    /// </para>
    /// <para>
    /// Returns null - not "base" - for every point neither signal recognises, so the caller's
    /// own default applies uniformly.
    /// </para>
    /// </remarks>
    private static string? PoiDlc(ReporterProtocol.Record record)
    {
        if (record.Kind != TrackedKind.PointOfInterest)
        {
            return null;
        }

        if (record.Id.StartsWith("ep1_", StringComparison.OrdinalIgnoreCase) ||
            record.Id.Contains("_ep1_", StringComparison.OrdinalIgnoreCase))
        {
            return "hos";
        }

        return record.Extras.Length >= 2
               && int.TryParse(record.Extras[1], out int areaType)
               && areaType == GameData.ToussaintAreaType
            ? "baw"
            : null;
    }

    /// <summary>
    /// The content pack a diagram or formula belongs to, when one is on record for it.
    /// </summary>
    /// <remarks>
    /// The game says nothing about a schematic's origin, so this is the only signal there
    /// is - a curated table in <see cref="GameData.SchematicContentPacks"/>, keyed on the
    /// internal identifier because two different schematics can share a display name.
    /// Returns null for everything not listed, so the caller's own default applies.
    /// </remarks>
    private static string? SchematicDlc(ReporterProtocol.Record record) =>
        record.Kind is TrackedKind.Diagram or TrackedKind.Formula
        && GameData.SchematicContentPacks.TryGetValue(record.Id, out string? pack)
            ? pack
            : null;

    /// <summary>
    /// Folds a Gwent card into its card type, which is what the collection quest counts.
    /// </summary>
    private static void AddGwentType(Dictionary<string, CatalogEntry> types, ReporterProtocol.Record record)
    {
        if (!int.TryParse(record.Id, out int index))
        {
            return;
        }

        bool isBaseGame = GameData.IsBaseGameGwentCard(index, record.Faction, out string? type);

        // Skellige and expansion cards are still catalogued, so a 300% run can count them;
        // they simply belong to a different content pack.
        type ??= GameData.GwentCardTypes.TryGetValue(index, out string? known) ? known : null;

        if (type is null)
        {
            // An index with no card behind it: a leftover definition, not something the
            // player can ever collect.
            return;
        }

        if (types.ContainsKey(type))
        {
            return;
        }

        types[type] = new CatalogEntry(
            Id: $"gwent:{type}",
            Kind: TrackedKind.GwentCard,
            DisplayName: record.DisplayName ?? type.Replace('_', ' '),
            Dlc: "base",
            Region: record.Faction,
            // The game's only card-collection objective is the base-game "Collect 'Em All".
            // The Skellige deck and the expansion cards are still tracked, because seeing
            // them is useful, but neither expansion asks you to collect anything, so
            // counting them would invent an objective that does not exist.
            CountsToward: isBaseGame);
    }

    /// <summary>
    /// Produces a readable name from an internal identifier, as a starting point.
    /// </summary>
    /// <remarks>
    /// The game's unique script tag often carries a trailing GUID, for example
    /// <c>lw_cp33_sunken_treasure B8486EAF-4E34ECCB-69896A96-1E5CB685</c>. Stripping it
    /// gives something legible, but these are placeholders: proper display names come
    /// from the localisation strings and are a separate job. The identifier stays the key
    /// either way, so renaming is always safe.
    /// </remarks>
    private static string Humanise(string id, TrackedKind kind)
    {
        if (kind == TrackedKind.GwentCard)
        {
            return $"Card {id}";
        }

        return StripGuid(id).Replace('_', ' ').Trim();
    }

    /// <summary>
    /// Removes the trailing GUID the game appends to many journal script tags, leaving the
    /// readable part untouched.
    /// </summary>
    private static string StripGuid(string id)
    {
        int space = id.LastIndexOf(' ');

        return space > 0 && LooksLikeGuid(id.AsSpan(space + 1)) ? id[..space].TrimEnd() : id.TrimEnd();
    }

    private static bool LooksLikeGuid(ReadOnlySpan<char> text)
    {
        if (text.Length != 35)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            bool shouldBeDash = i is 8 or 17 or 26;

            if (shouldBeDash != (text[i] == '-'))
            {
                return false;
            }

            if (!shouldBeDash && !Uri.IsHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
