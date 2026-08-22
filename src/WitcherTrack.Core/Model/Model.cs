using System.Text.Json.Serialization;

namespace WitcherTrack.Core.Model;

/// <summary>The kinds of thing a completion run tracks.</summary>
/// <remarks>
/// Serialised as its name, not the underlying number: the web interface matches on
/// <c>"Quest"</c>, <c>"PointOfInterest"</c> and so on to pick an icon and a label, and a
/// name survives the enum being reordered or extended in a way a raw integer would not.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TrackedKind>))]
public enum TrackedKind
{
    Quest,
    Diagram,
    Formula,
    PointOfInterest,
    GwentCard,
}

/// <summary>Whether a tracked entry has been completed.</summary>
public enum CompletionState
{
    /// <summary>Not completed, or never seen.</summary>
    NotDone,

    /// <summary>Completed and counted.</summary>
    Done,

    /// <summary>
    /// Permanently unobtainable, for example a quest failed or made inaccessible by a
    /// story choice. Excluded from the numerator but still shown, because knowing what
    /// is lost matters during a run.
    /// </summary>
    Failed,
}

/// <summary>Where a piece of information came from, in increasing order of authority.</summary>
public enum EventSource
{
    /// <summary>Optical character recognition of the on-screen notification. Legacy, imprecise.</summary>
    Ocr,

    /// <summary>A live event pushed by the in-game reporter.</summary>
    GameEvent,

    /// <summary>A full state snapshot, either from the in-game reporter or from a savegame.</summary>
    Snapshot,

    /// <summary>A correction entered by the player. Always wins.</summary>
    Manual,
}

/// <summary>
/// One trackable thing, identified by the name the game itself uses.
/// </summary>
/// <param name="Id">
/// The game's internal identifier: a journal script tag, a schematic name, a map pin
/// tag or a Gwent card index. Never a localised display string, so the catalogue is
/// independent of the game's language.
/// </param>
/// <param name="Kind">What sort of entry this is.</param>
/// <param name="DisplayName">The human-readable name shown in the interface.</param>
/// <param name="Dlc">
/// Which content pack the entry belongs to: <c>base</c>, <c>hos</c>, <c>baw</c>, or the
/// identifier of a future expansion. This is what the completion modes filter on.
/// </param>
/// <param name="Region">Optional in-game region, used for grouping in the interface.</param>
/// <param name="CountsToward">
/// False for entries that are tracked for information but must not affect the totals.
/// </param>
/// <param name="GroupId">
/// Optional mutual-exclusion group. Entries in the same group represent branches of a
/// choice, only some of which can ever be completed in a single playthrough.
/// </param>
/// <param name="X">
/// World X, for a point of interest whose dump included it. Null for every other kind,
/// and for a point of interest built from a dump taken before the reporter sent
/// coordinates - existing catalogues stay valid, just without a position to plot.
/// </param>
/// <param name="Y">World Y, alongside <paramref name="X"/>.</param>
/// <param name="World">
/// The streamed world <paramref name="X"/>/<paramref name="Y"/> were read from, for
/// example <c>levels\novigrad\novigrad.w2w</c>. Null for every other kind, and for a
/// point of interest built from a dump taken before the reporter started sending it.
/// Needed because the coordinates alone are not unique: White Orchard, Velen+Novigrad,
/// Skellige and Kaer Morhen each reset their own X/Y near their own origin, the same way
/// Toussaint's separate world file does (see <c>KNOWN-ISSUES.md</c>) - so this is what
/// says which of them a position belongs to.
/// </param>
public sealed record CatalogEntry(
    string Id,
    TrackedKind Kind,
    string DisplayName,
    string Dlc,
    string? Region = null,
    bool CountsToward = true,
    string? GroupId = null,
    double? X = null,
    double? Y = null,
    string? World = null);

/// <summary>
/// A set of catalogue entries that exclude one another.
/// </summary>
/// <param name="Id">The group identifier referenced by <see cref="CatalogEntry.GroupId"/>.</param>
/// <param name="MaxCount">
/// How many entries of the group a single playthrough can obtain. The group contributes
/// this much to the denominator instead of its full size, and the numerator is capped
/// at the same value.
/// </param>
/// <param name="Note">Why the entries exclude one another, shown in the interface.</param>
public sealed record ExclusionGroup(string Id, int MaxCount, string? Note = null);

/// <summary>
/// A completion mode, such as "100% base game" or "300%".
/// </summary>
/// <param name="Id">Stable identifier, for example <c>base100</c> or <c>all300</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Label">Short label for the overlay, for example <c>300%</c>.</param>
/// <param name="Scope">The content packs that count toward this mode.</param>
/// <param name="Sort">Display order.</param>
/// <param name="Active">
/// False hides the mode without deleting it. This is how a 300% mode is retired once a
/// new expansion turns it into a 400% mode, while keeping old runs readable.
/// </param>
/// <param name="Kinds">
/// Which kinds of entry the mode counts, or null for every kind. A completion mode is
/// normally about a content pack and takes all of it; a single-objective run - collecting
/// the Gwent deck, say - is about one kind of thing wherever it comes from, and the two
/// need saying separately because scope alone cannot express the second.
/// </param>
public sealed record Ruleset(
    string Id,
    string Name,
    string Label,
    IReadOnlySet<string> Scope,
    int Sort = 0,
    bool Active = true,
    IReadOnlySet<TrackedKind>? Kinds = null);

/// <summary>
/// Forces a single catalogue entry into or out of one mode, overriding the DLC scope.
/// </summary>
/// <remarks>
/// Content attribution is a curation decision, not something the game reports, so it
/// will occasionally be wrong. This exists so a mistake can be corrected without
/// reshaping the catalogue.
/// </remarks>
/// <param name="RulesetId">The mode being adjusted.</param>
/// <param name="CatalogId">The entry being forced.</param>
/// <param name="Include">True forces the entry in, false forces it out.</param>
/// <param name="Reason">Why, shown in the interface.</param>
public sealed record RulesetException(string RulesetId, string CatalogId, bool Include, string? Reason = null);

/// <summary>
/// One observation about one catalogue entry.
/// </summary>
/// <remarks>
/// The event log is append-only. Nothing is ever rewritten, which is what makes it
/// possible to reload an earlier savegame without corrupting the run: a later snapshot
/// simply supersedes the events that came before it.
/// </remarks>
/// <param name="Sequence">Monotonic sequence number.</param>
/// <param name="Timestamp">When the observation was recorded.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="CatalogId">The entry it concerns.</param>
/// <param name="State">The observed state.</param>
/// <param name="SnapshotId">
/// Set for events belonging to a full snapshot, so that one snapshot can be treated as
/// a single atomic replacement of everything known before it.
/// </param>
/// <param name="Raw">The original payload, kept for debugging.</param>
public sealed record ProgressEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    EventSource Source,
    string CatalogId,
    CompletionState State,
    string? SnapshotId = null,
    string? Raw = null);

/// <summary>A player correction, which takes precedence over every automatic source.</summary>
/// <param name="CatalogId">The entry being corrected.</param>
/// <param name="State">The state the player asserts.</param>
/// <param name="Reason">Why, so the correction can be reviewed later.</param>
/// <param name="Timestamp">When the correction was made.</param>
public sealed record ManualOverride(string CatalogId, CompletionState State, string? Reason, DateTimeOffset Timestamp);

/// <summary>Progress for one kind of entry within one mode.</summary>
/// <param name="Kind">The kind being counted.</param>
/// <param name="Completed">How many are done.</param>
/// <param name="Total">How many count toward the mode.</param>
public sealed record KindProgress(TrackedKind Kind, int Completed, int Total)
{
    /// <summary>Completion as a percentage, or zero when nothing counts.</summary>
    public double Percent => Total == 0 ? 0d : Math.Round(Completed * 100d / Total, 2);
}

/// <summary>Overall progress for one mode.</summary>
/// <param name="RulesetId">The mode this describes.</param>
/// <param name="Label">The mode's short label.</param>
/// <param name="Completed">Total completed within the mode.</param>
/// <param name="Total">Total that counts toward the mode.</param>
/// <param name="ByKind">The same figures broken down by kind.</param>
public sealed record RulesetProgress(
    string RulesetId,
    string Label,
    int Completed,
    int Total,
    IReadOnlyList<KindProgress> ByKind)
{
    /// <summary>Completion as a percentage, or zero when nothing counts.</summary>
    public double Percent => Total == 0 ? 0d : Math.Round(Completed * 100d / Total, 2);
}
