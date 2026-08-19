using WitcherTrack.Core.Ingest;
using WitcherTrack.Core.Model;

namespace WitcherTrack.Core;

/// <summary>
/// Derives chest-to-quest links from what was completed together, so a point of interest
/// whose pin never clears can still be proven done by the hunt that took it.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one job only: forcing a point of interest done when its own pin says
/// otherwise. The pin remains the first-level source and is trusted wherever it reports
/// anything at all; a link is a repair for the case where it does not.
/// </para>
/// <para>
/// <b>The evidence is that the two things happened together, not that they are near each
/// other.</b> Looting a chest and closing the hunt that wanted it are one action, so they
/// land side by side in the completion sequence. Where they are on the map is not
/// evidence: a chest can sit metres from the inn a completely unrelated quest is handed in
/// at, and a rule built on distance would link those two and quietly add a point to the
/// percentage. Distance is used here for exactly one thing - refusing a pair in two
/// different streamed worlds, which is a physical impossibility rather than an inference.
/// </para>
/// <para>
/// A link is asserted only when all of these hold:
/// </para>
/// <list type="number">
///   <item>the quest is a treasure hunt - the only category whose completion is the act of
///         opening a chest rather than talking to whoever pays out;</item>
///   <item>the pin is a chest: <c>TreasureHuntMappin</c> or <c>BossAndTreasure</c>;</item>
///   <item>the two are <b>adjacent</b> in the completion sequence once everything that is
///         neither a chest nor a hunt is set aside - no other chest and no other hunt
///         completed between them. This is what does the work: it is what separates the
///         chest that was opened from the one opened five minutes earlier;</item>
///   <item>no more than <see cref="MaxUnlocksApart"/> completions of any kind sit between
///         them, so two things that merely happen to be the only ones of their sort in a
///         long stretch of play are not paired;</item>
///   <item>if the hunt recorded where it finished, that is the chest's own world.</item>
/// </list>
/// <para>
/// Measured against a full White Orchard prologue: eight chest pins and four treasure
/// hunts completed, in an order that interleaves them. Condition 3 alone reduces that to
/// two pairs - <c>camp1_creatures</c> with <c>Dirty Funds</c>, two completions apart, and
/// <c>cemetary_wraith</c> with <c>Scavenger Hunt: Viper School Gear</c>, one apart - and
/// both are correct. Every other combination has another chest or another hunt sitting
/// between the two, including the pair a distance rule would have found first.
/// </para>
/// <para>
/// Only live completions count. The report the reporter sends on load re-asserts an
/// entire run in one batch, in no meaningful order, and pairing anything inside it would
/// be pairing dictionary iteration order.
/// </para>
/// <para>
/// What this cannot do is place a hunt that has no chest pin at all. The same run finished
/// <c>Temerian Valuables</c> and <c>Deserter Gold</c> with no pin clearing anywhere near
/// them in the sequence, because White Orchard carries fewer chest pins than it has hunts.
/// Those assert nothing here and stay on the map as quests in their own right.
/// </para>
/// </remarks>
public static class QuestPinLink
{
    /// <summary>
    /// How many completions of any kind may sit between a chest and the hunt it is paired
    /// with.
    /// </summary>
    /// <remarks>
    /// The two measured links are one and two completions apart. The allowance is wider
    /// than that because one chest can hand over several diagrams and formulae, and every
    /// one of them lands between the pin clearing and the quest closing.
    /// </remarks>
    public const int MaxUnlocksApart = 8;

    /// <summary>Pin types that mark a chest, and so can be what a treasure hunt opened.</summary>
    private static readonly HashSet<string> ChestPinTypes =
        new(StringComparer.Ordinal) { "TreasureHuntMappin", "BossAndTreasure" };

    /// <summary>
    /// Builds the point-of-interest to quest map to hand to
    /// <see cref="StateResolver.Resolve"/>.
    /// </summary>
    /// <param name="catalog">Every tracked entry. Only quests and points of interest are read.</param>
    /// <param name="completionOrder">
    /// Catalogue ids in the order they were first completed, counting live play only.
    /// Ids that are neither a chest pin nor a treasure hunt still have to be present:
    /// they are what the distance in condition 4 is measured in.
    /// </param>
    /// <param name="finishedAt">
    /// Where each entry was finished, keyed by catalogue id, for the same-world check.
    /// May be empty - a reporter older than v1.5 records none, and the rule still works.
    /// </param>
    /// <returns>Point-of-interest id to the quest id that proves it.</returns>
    public static Dictionary<string, string> Derive(
        IEnumerable<CatalogEntry> catalog,
        IReadOnlyList<string> completionOrder,
        IReadOnlyDictionary<string, PlayerPlace>? finishedAt = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(completionOrder);

        Dictionary<string, CatalogEntry> byId = [];
        foreach (CatalogEntry entry in catalog)
        {
            byId[entry.Id] = entry;
        }

        // The sequence with everything that is neither a chest nor a hunt removed, each
        // remembering how far along the full sequence it sat. Condition 3 is adjacency in
        // this list; condition 4 is measured in the original one.
        List<(int At, string Id, bool IsChest)> marks = [];
        for (int i = 0; i < completionOrder.Count; i++)
        {
            if (!byId.TryGetValue(completionOrder[i], out CatalogEntry? entry))
            {
                continue;
            }

            if (entry.Kind == TrackedKind.PointOfInterest
                && entry.Region is not null && ChestPinTypes.Contains(entry.Region))
            {
                marks.Add((i, entry.Id, true));
            }
            else if (entry.Kind == TrackedKind.Quest
                     && string.Equals(entry.Region, "treasure", StringComparison.Ordinal))
            {
                marks.Add((i, entry.Id, false));
            }
        }

        var links = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 1; i < marks.Count; i++)
        {
            (int At, string Id, bool IsChest) a = marks[i - 1], b = marks[i];

            // Two chests in a row, or two hunts in a row, pair with nothing: whatever the
            // second one belongs to, the first one is in the way of saying so.
            if (a.IsChest == b.IsChest || b.At - a.At > MaxUnlocksApart)
            {
                continue;
            }

            string chest = a.IsChest ? a.Id : b.Id;
            string hunt = a.IsChest ? b.Id : a.Id;

            if (finishedAt is not null
                && finishedAt.TryGetValue(hunt, out PlayerPlace place)
                && byId.TryGetValue(chest, out CatalogEntry? pin)
                && pin.World is not null
                && !string.Equals(pin.World, place.World, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A chest already claimed is left alone rather than reassigned: the sequence
            // is walked once, forwards, so the first claim is the earlier evidence.
            links.TryAdd(chest, hunt);
        }

        return links;
    }
}
