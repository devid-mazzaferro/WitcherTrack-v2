using WitcherTrack.Core.Model;

namespace WitcherTrack.Core;

/// <summary>
/// Collapses the append-only event log into the current state of every catalogue entry.
/// </summary>
/// <remarks>
/// <para>
/// Precedence, highest first:
/// </para>
/// <list type="number">
///   <item>a manual override, which always wins;</item>
///   <item>a proven-by-quest correction (see <see cref="GameData.PoiProvenByQuest"/>):
///         if the linked quest resolved <see cref="CompletionState.Done"/>, the entry is
///         forced <see cref="CompletionState.Done"/> regardless of its own raw state;</item>
///   <item>the most recent snapshot, which describes the world as a whole and therefore
///         supersedes every event recorded before it;</item>
///   <item>events recorded after that snapshot, most recent first;</item>
///   <item>otherwise the entry is not done.</item>
/// </list>
/// <para>
/// Rule 3 is what makes reloading a savegame safe. An OCR-based tracker can only ever
/// add completions, so dying and reloading permanently desynchronises it. Here the next
/// snapshot re-asserts the truth and everything recorded in the abandoned timeline stops
/// counting, without deleting any history.
/// </para>
/// <para>
/// Rule 2 exists because the game itself sometimes cannot be trusted on a specific,
/// individually proven entry: a handful of points of interest read <c>not_done</c> in
/// every savefile checked despite the quest that requires completing them reading
/// <c>done</c> in the same savefile - see <see cref="GameData.PoiProvenByQuest"/> for the
/// evidence behind each one. It sits below manual overrides and above the raw event
/// resolution deliberately: a player correction can still overrule it, but nothing in the
/// ordinary event stream can, since the ordinary event stream is exactly what is wrong
/// for these entries.
/// </para>
/// </remarks>
public static class StateResolver
{
    /// <summary>
    /// Resolves the current state of every entry mentioned by the log or the overrides.
    /// </summary>
    /// <param name="events">The event log. Order is irrelevant; sequence numbers are used.</param>
    /// <param name="overrides">Player corrections, keyed by catalogue id.</param>
    /// <param name="provenByQuest">
    /// Entries individually proven done by a linked quest's completion, keyed by the
    /// entry's catalogue id with the quest's catalogue id as the value. Defaults to
    /// <see cref="GameData.PoiProvenByQuest"/>; pass an empty dictionary to resolve the
    /// raw event log without this correction, as the self-tests do.
    /// </param>
    public static Dictionary<string, CompletionState> Resolve(
        IEnumerable<ProgressEvent> events,
        IEnumerable<ManualOverride>? overrides = null,
        IReadOnlyDictionary<string, string>? provenByQuest = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Materialise once: the log is walked twice below.
        List<ProgressEvent> ordered = [.. events.OrderBy(static e => e.Sequence)];

        // Everything at or after the last snapshot's first event is authoritative.
        // Events before it belong to a superseded view of the world.
        long snapshotBoundary = FindLastSnapshotStart(ordered);

        var resolved = new Dictionary<string, CompletionState>(StringComparer.Ordinal);

        foreach (ProgressEvent progressEvent in ordered)
        {
            if (progressEvent.Sequence < snapshotBoundary)
            {
                continue;
            }

            resolved[progressEvent.CatalogId] = progressEvent.State;
        }

        // A quest-proven entry is forced done ahead of overrides, so a player correction
        // - including one that deliberately sets it back to not-done - still wins.
        foreach ((string poiId, string questId) in provenByQuest ?? GameData.PoiProvenByQuest)
        {
            if (resolved.TryGetValue(questId, out CompletionState questState) && questState == CompletionState.Done)
            {
                resolved[poiId] = CompletionState.Done;
            }
        }

        if (overrides is not null)
        {
            foreach (ManualOverride manualOverride in overrides)
            {
                resolved[manualOverride.CatalogId] = manualOverride.State;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Finds the sequence number at which the most recent complete snapshot begins.
    /// Returns <see cref="long.MinValue"/> when no snapshot has been recorded, in which
    /// case every event counts.
    /// </summary>
    private static long FindLastSnapshotStart(List<ProgressEvent> ordered)
    {
        string? lastSnapshotId = null;

        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            if (ordered[i].Source == EventSource.Snapshot && ordered[i].SnapshotId is { } id)
            {
                lastSnapshotId = id;
                break;
            }
        }

        if (lastSnapshotId is null)
        {
            return long.MinValue;
        }

        foreach (ProgressEvent progressEvent in ordered)
        {
            if (progressEvent.SnapshotId == lastSnapshotId)
            {
                return progressEvent.Sequence;
            }
        }

        return long.MinValue;
    }
}
