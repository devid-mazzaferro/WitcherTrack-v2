using WitcherTrack.Core.Model;

namespace WitcherTrack.Core;

/// <summary>
/// Turns catalogue plus resolved state into the numbers shown on the overlay.
/// </summary>
/// <remarks>
/// <para>
/// A completion mode is a view, not a separate run. Every observation is recorded once,
/// and each mode filters that same data. Completing base-game content during a
/// "Blood and Wine only" run is therefore never lost: it simply does not count in that
/// view, and does count in the 300% view.
/// </para>
/// <para>
/// Adding a future expansion means adding a mode whose scope includes the new content
/// pack, and tagging the new catalogue entries. No stored data changes and no code
/// changes.
/// </para>
/// </remarks>
public static class ProgressCalculator
{
    /// <summary>
    /// Computes progress for one mode.
    /// </summary>
    /// <param name="catalog">Every trackable entry.</param>
    /// <param name="groups">Mutual-exclusion groups, keyed by group id.</param>
    /// <param name="ruleset">The mode to compute.</param>
    /// <param name="exceptions">Per-mode inclusion overrides.</param>
    /// <param name="states">Resolved state per catalogue id, from <see cref="StateResolver"/>.</param>
    public static RulesetProgress Compute(
        IReadOnlyCollection<CatalogEntry> catalog,
        IReadOnlyDictionary<string, ExclusionGroup> groups,
        Ruleset ruleset,
        IReadOnlyCollection<RulesetException> exceptions,
        IReadOnlyDictionary<string, CompletionState> states)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentNullException.ThrowIfNull(states);

        Dictionary<string, bool> forced = exceptions
            .Where(e => string.Equals(e.RulesetId, ruleset.Id, StringComparison.Ordinal))
            .ToDictionary(e => e.CatalogId, e => e.Include, StringComparer.Ordinal);

        List<CatalogEntry> inScope = [.. catalog.Where(entry => IsInScope(entry, ruleset, forced))];

        // Entries outside any exclusion group contribute one each.
        // Grouped entries contribute their group's cap, counted once per group.
        var perKind = new Dictionary<TrackedKind, (int Completed, int Total)>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (CatalogEntry entry in inScope)
        {
            if (entry.GroupId is null)
            {
                Accumulate(perKind, entry.Kind, IsDone(states, entry.Id) ? 1 : 0, 1);
            }
        }

        foreach (CatalogEntry entry in inScope)
        {
            if (entry.GroupId is not { } groupId || !seenGroups.Add(groupId))
            {
                continue;
            }

            // A group's cap can exceed neither its declared maximum nor the number of
            // its members that are actually in scope for this mode.
            List<CatalogEntry> members = [.. inScope.Where(m => m.GroupId == groupId)];
            int declaredCap = groups.TryGetValue(groupId, out ExclusionGroup? group) ? group.MaxCount : members.Count;
            int cap = Math.Clamp(declaredCap, 0, members.Count);

            int completed = Math.Min(cap, members.Count(m => IsDone(states, m.Id)));

            // A group can in principle span kinds; attribute it to the kind of its members.
            foreach (IGrouping<TrackedKind, CatalogEntry> byKind in members.GroupBy(m => m.Kind))
            {
                // Distribute proportionally when a group is mixed, which in practice it is not.
                int kindCap = byKind.Count() == members.Count ? cap : Math.Min(cap, byKind.Count());
                int kindCompleted = Math.Min(kindCap, byKind.Count(m => IsDone(states, m.Id)));
                Accumulate(perKind, byKind.Key, kindCompleted, kindCap);

                cap -= kindCap;
                completed -= kindCompleted;

                if (cap <= 0)
                {
                    break;
                }
            }
        }

        List<KindProgress> byKindResult =
        [
            .. perKind
                .OrderBy(static pair => pair.Key)
                .Select(static pair => new KindProgress(pair.Key, pair.Value.Completed, pair.Value.Total))
        ];

        return new RulesetProgress(
            ruleset.Id,
            ruleset.Label,
            byKindResult.Sum(static k => k.Completed),
            byKindResult.Sum(static k => k.Total),
            byKindResult);
    }

    /// <summary>Computes progress for every active mode, in display order.</summary>
    public static IReadOnlyList<RulesetProgress> ComputeAll(
        IReadOnlyCollection<CatalogEntry> catalog,
        IReadOnlyDictionary<string, ExclusionGroup> groups,
        IEnumerable<Ruleset> rulesets,
        IReadOnlyCollection<RulesetException> exceptions,
        IReadOnlyDictionary<string, CompletionState> states)
    {
        ArgumentNullException.ThrowIfNull(rulesets);

        return
        [
            .. rulesets
                .Where(static r => r.Active)
                .OrderBy(static r => r.Sort)
                .Select(r => Compute(catalog, groups, r, exceptions, states))
        ];
    }

    /// <summary>
    /// Whether one entry counts toward one mode.
    /// </summary>
    /// <remarks>
    /// The same rule the denominator is built from, exposed because two other things have
    /// to agree with it exactly: the checklist, which lists what a mode still asks for, and
    /// the overlay's feed, which should not announce a point of interest to a run that is
    /// only collecting Gwent cards. Answering them from anywhere else would be a second
    /// copy of this rule, free to drift.
    /// </remarks>
    public static bool Counts(
        CatalogEntry entry,
        Ruleset ruleset,
        IReadOnlyCollection<RulesetException> exceptions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(exceptions);

        RulesetException? forced = exceptions.FirstOrDefault(e =>
            string.Equals(e.RulesetId, ruleset.Id, StringComparison.Ordinal)
            && string.Equals(e.CatalogId, entry.Id, StringComparison.Ordinal));

        return forced is not null
            ? forced.Include
            : entry.CountsToward
              && ruleset.Scope.Contains(entry.Dlc)
              && (ruleset.Kinds is null || ruleset.Kinds.Contains(entry.Kind));
    }

    /// <summary>
    /// Decides whether an entry counts toward a mode: an explicit exception wins,
    /// otherwise the entry's content pack must be in the mode's scope, its kind must be
    /// one the mode counts, and entries flagged as non-counting never take part.
    /// </summary>
    private static bool IsInScope(CatalogEntry entry, Ruleset ruleset, Dictionary<string, bool> forced)
    {
        if (forced.TryGetValue(entry.Id, out bool include))
        {
            return include;
        }

        return entry.CountsToward
            && ruleset.Scope.Contains(entry.Dlc)
            && (ruleset.Kinds is null || ruleset.Kinds.Contains(entry.Kind));
    }

    private static bool IsDone(IReadOnlyDictionary<string, CompletionState> states, string id) =>
        states.TryGetValue(id, out CompletionState state) && state == CompletionState.Done;

    private static void Accumulate(
        Dictionary<TrackedKind, (int Completed, int Total)> perKind,
        TrackedKind kind,
        int completed,
        int total)
    {
        perKind.TryGetValue(kind, out (int Completed, int Total) current);
        perKind[kind] = (current.Completed + completed, current.Total + total);
    }
}
