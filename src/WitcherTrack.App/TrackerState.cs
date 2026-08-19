using WitcherTrack.Core.Ingest;
using System.Text.Json;
using System.Threading.Channels;
using WitcherTrack.Core;
using WitcherTrack.Core.Model;

namespace WitcherTrack.App;

/// <summary>
/// Holds the run in memory and notifies subscribers when it changes.
/// </summary>
/// <remarks>
/// <para>
/// The event log is the source of truth and is append-only; everything the interface
/// shows is derived from it. Ingestion sources call <see cref="Record"/> and never write
/// to the projected state directly, which keeps the reload-safety rules in
/// <see cref="StateResolver"/> in one place.
/// </para>
/// <para>
/// The default set of modes is created here. Adding an expansion later means adding a
/// mode whose scope contains the new content pack and retiring the old combined mode by
/// marking it inactive; no stored data has to change.
/// </para>
/// </remarks>
internal sealed class TrackerState
{
    /// <summary>
    /// How many completions the live state payload carries.
    /// </summary>
    /// <remarks>
    /// The overlay shows five. This is pushed to every subscriber on every change, so it
    /// stays small deliberately; the full history is fetched separately by the one view
    /// that plots it.
    /// </remarks>
    private const int RecentUnlockCount = 10;

    /// <summary>
    /// A ceiling on the stored history, far above a real run.
    /// </summary>
    /// <remarks>
    /// A 300% run completes about fifteen hundred entries, so this only ever engages if
    /// something is recording in a loop - it is a leak guard, not a display limit.
    /// </remarks>
    private const int UnlockHistoryLimit = 20_000;

    /// <summary>
    /// How long a change waits for company before a payload is built and pushed. Short
    /// enough to read as instant on an overlay, long enough that a burst of records
    /// produces one payload rather than hundreds.
    /// </summary>
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(120);

    private readonly Lock _gate = new();
    private readonly List<ProgressEvent> _events = [];
    private readonly Dictionary<string, ManualOverride> _overrides = new(StringComparer.Ordinal);
    private readonly List<Channel<string>> _subscribers = [];
    private readonly Dictionary<string, DateTimeOffset> _sourceLastSeen = new(StringComparer.Ordinal);

    // Tracks what was already known so that a snapshot - which re-asserts hundreds of
    // already-completed entries every time the player reloads - only produces timeline
    // entries for what is genuinely new, not a flood of everything the run has ever done.
    private readonly Dictionary<string, CompletionState> _lastKnownStates = new(StringComparer.Ordinal);
    private readonly List<UnlockEvent> _unlocks = [];

    // Every id the history already names. The timeline records when something was *first*
    // completed, and that has to survive being told again - which happens constantly. The
    // report on load re-asserts the whole run; the tracker re-reads the whole script log
    // whenever it starts, so a session's completions arrive a second time; and loading an
    // earlier save then replaying an hour re-completes everything in it. None of those is
    // a new completion, and dating any of them to now would rewrite the run's history with
    // the moment it was last talked about.
    private readonly HashSet<string> _everUnlocked = new(StringComparer.Ordinal);

    // Where the reporter last said the player was, and where things have been finished.
    // A point of interest never needs either: the game knows where its pin is. Everything
    // else has no place of its own, so the only one it can have is where the player stood
    // when it happened.
    private readonly Dictionary<string, PlayerPlace> _finishedAt = new(StringComparer.Ordinal);
    private PlayerPlace? _playerPlace;

    // Catalogue ids in the order they were first completed, live play only. What a
    // chest-to-quest link is derived from: the evidence is that two things happened
    // together, so the sequence is the evidence and this is the sequence.
    private readonly List<string> _completionOrder = [];

    // Chest pins proven done by the treasure hunt that took them. Rebuilt when the
    // sequence gains a chest or a hunt rather than on every resolve, because a resolve
    // happens on every single record and this walks the whole catalogue.
    // The hand-curated links are the floor: deriving none never loses them.
    private Dictionary<string, string> _provenByQuest =
        new(GameData.PoiProvenByQuest, StringComparer.Ordinal);

    // The event log resolved down to one state per id, maintained as records arrive
    // instead of being recomputed from the whole log each time. See ResolveLive.
    private readonly Dictionary<string, CompletionState> _baseResolved = new(StringComparer.Ordinal);

    // The catalogue keyed by id. Built once: the catalogue is loaded at startup and never
    // grows afterwards, and rebuilding it per record cost two thousand dictionary inserts
    // for every line the reporter wrote.
    private Dictionary<string, CatalogEntry>? _byId;

    private bool _publishPending;
    private long _nextSequence = 1;
    private string? _activeModeId;
    private DateTimeOffset? _runStartedAt;

    // Time the game was demonstrably being played, accumulated across every session the
    // run has lived through, and when the tracker last saw the game write anything.
    private double _playSeconds;
    private DateTimeOffset? _lastActivityAt;

    /// <summary>
    /// A momentary reading of the optional in-game-time clock: whether it is attached, how
    /// much it has accumulated, and a build/status string for the interface.
    /// </summary>
    /// <remarks>
    /// A plain struct rather than a reference to <c>GameClock</c> itself, because that type
    /// is Windows-only and this one is not - keeping the dependency out of this class is
    /// what lets the rest of the tracker stay platform-agnostic.
    /// </remarks>
    public readonly record struct IgtSample(bool Active, TimeSpan? Elapsed, string? Detail);

    /// <summary>
    /// Supplies the current <see cref="IgtSample"/>, if in-game-time tracking is wired up at
    /// all. Null on every platform this clock cannot run on, and on Windows until the player
    /// turns the option on.
    /// </summary>
    public Func<IgtSample>? IgtSource { get; set; }

    /// <summary>Every trackable entry. Populated from the catalogue files at startup.</summary>
    public List<CatalogEntry> Catalog { get; } = [];

    /// <summary>
    /// Fitted world-to-map transforms, keyed by the world file they apply to. Empty when
    /// none are on file, which only costs the map view its orientation.
    /// </summary>
    public Dictionary<string, MapCalibration> Calibration { get; } = new(StringComparer.Ordinal);

    /// <summary>Region background pictures, keyed by world file. Empty until they are built.</summary>
    public Dictionary<string, MapBackground> Backgrounds { get; } = new(StringComparer.Ordinal);

    /// <summary>Where those pictures live on disk, so the server can hand them out.</summary>
    public string? MapImageFolder { get; set; }

    /// <summary>
    /// Mutual-exclusion groups, keyed by group id.
    /// </summary>
    /// <remarks>
    /// A group contributes its cap to the total rather than its full size, because only
    /// that many of its members are reachable in one playthrough.
    /// </remarks>
    public Dictionary<string, ExclusionGroup> Groups { get; } = new(StringComparer.Ordinal)
    {
        ["baw_paths_of_destiny"] = new(
            "baw_paths_of_destiny",
            MaxCount: 1,
            "The Paths of Destiny has two halves; finishing one leaves the other permanently inactive."),
    };

    /// <summary>Per-mode inclusion overrides.</summary>
    public List<RulesetException> Exceptions { get; } = [];

    /// <summary>
    /// Which mode the dashboard is currently showing, or null before the player has chosen
    /// one.
    /// </summary>
    /// <remarks>
    /// Only one mode is played at a time, so the interface asks once at the start rather
    /// than showing all four - the event log underneath is the same regardless of which is
    /// selected, so switching later costs nothing and loses no data.
    /// </remarks>
    public string? ActiveModeId
    {
        get { lock (_gate) { return _activeModeId; } }
    }

    /// <summary>
    /// The completion modes.
    /// </summary>
    /// <remarks>
    /// When the next expansion ships, add <c>newdlc100</c> and a combined <c>all400</c>,
    /// then set <c>all300</c> inactive. Existing runs keep working because a mode is a
    /// view over the same events, not a separate database.
    /// </remarks>
    public List<Ruleset> Rulesets { get; } =
    [
        new("base100", "100% Base Game", "100%", Scope("base"), 10),
        new("hos100", "100% Hearts of Stone", "100%", Scope("hos"), 20),
        new("baw100", "100% Blood and Wine", "100%", Scope("baw"), 30),
        new("all300", "300%", "300%", Scope("base", "hos", "baw"), 40),
    ];

    /// <summary>
    /// Appends observations to the log and notifies subscribers.
    /// </summary>
    /// <param name="source">Which ingestion source produced these.</param>
    /// <param name="observations">The observed states, keyed by catalogue id.</param>
    /// <param name="isSnapshot">
    /// True when the observations describe the complete world state rather than
    /// individual changes. A snapshot supersedes everything recorded before it, which is
    /// how the tracker recovers after the player reloads an earlier savegame.
    /// </param>
    public void Record(
        EventSource source,
        IEnumerable<KeyValuePair<string, CompletionState>> observations,
        bool isSnapshot)
    {
        ArgumentNullException.ThrowIfNull(observations);

        string? snapshotId = isSnapshot ? Guid.NewGuid().ToString("n") : null;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            // The run clock starts at the first thing the tracker ever hears, so elapsed
            // time on the timeline means time since the run actually began rather than
            // since the process happened to be launched.
            _runStartedAt ??= now;

            // A snapshot describes the world as a whole, so it supersedes everything
            // recorded before it - which for the resolved view means starting again from
            // what the snapshot itself says. Same rule StateResolver applies by finding
            // the last snapshot boundary and ignoring what precedes it.
            if (isSnapshot)
            {
                _baseResolved.Clear();
            }

            foreach ((string id, CompletionState state) in observations)
            {
                _events.Add(new ProgressEvent(_nextSequence++, now, source, id, state, snapshotId));
                _baseResolved[id] = state;
            }

            // A second pass when a link was just derived: the chest it proves was resolved
            // as not-done a few lines above, before the link existed. Waiting for the next
            // record would usually work - the reporter sweeps right after a quest update -
            // but "usually" is not a thing to leave in the counting path.
            if (NoteNewUnlocks(now, placeable: !isSnapshot))
            {
                NoteNewUnlocks(now, placeable: false);
            }
        }

        Publish();
        RunChanged?.Invoke();
    }

    /// <summary>
    /// The full unlock history with the moment the run's clock started, for the progress
    /// chart.
    /// </summary>
    /// <remarks>
    /// Kept off the pushed state payload because it grows to roughly fifteen hundred
    /// entries over a 300% run, and re-sending all of it on every single completion would
    /// mean the overlay pays for a view it does not draw.
    /// </remarks>
    public TimelineResponse Timeline()
    {
        lock (_gate)
        {
            return new TimelineResponse(_runStartedAt, [.. _unlocks]);
        }
    }

    /// <summary>
    /// Selects which mode the dashboard shows. The event log is unaffected: every mode is
    /// a view over the same recorded observations, so switching is free and reversible.
    /// </summary>
    public void SetActiveMode(string modeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);

        lock (_gate)
        {
            if (!Rulesets.Any(r => r.Id == modeId))
            {
                throw new ArgumentException($"Unknown mode '{modeId}'.", nameof(modeId));
            }

            _activeModeId = modeId;
        }

        Publish();
        RunChanged?.Invoke();
    }

    /// <summary>
    /// Clears the run back to zero: every event, override, and timeline entry is
    /// discarded, and the chosen mode is forgotten.
    /// </summary>
    /// <remarks>
    /// Nothing about the catalogue or the rules changes - only the recorded observations
    /// do. The next thing the tracker hears, typically the full snapshot the reporter sends
    /// when a savegame loads, becomes the new starting point. This is the only way progress
    /// moves backward: the append-only log otherwise never forgets anything, by design.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            _events.Clear();
            _baseResolved.Clear();
            _overrides.Clear();
            _lastKnownStates.Clear();
            _unlocks.Clear();
            _everUnlocked.Clear();
            _finishedAt.Clear();
            _completionOrder.Clear();
            _playerPlace = null;
            _provenByQuest = new Dictionary<string, string>(GameData.PoiProvenByQuest, StringComparer.Ordinal);
            _nextSequence = 1;
            _activeModeId = null;
            _runStartedAt = null;
            _playSeconds = 0;
            _lastActivityAt = null;
        }

        Publish();
        RunChanged?.Invoke();
    }

    /// <summary>
    /// The current state of every id the run has heard about, without walking the event
    /// log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Equivalent to <see cref="StateResolver.Resolve"/> over the whole log, and reached
    /// the same way - the last snapshot, then events after it, then quest-proven chests,
    /// then manual corrections - but starting from a running total rather than replaying
    /// everything each time.
    /// </para>
    /// <para>
    /// This is called once per record, and a record arrives for every line the reporter
    /// writes: a single map-pin sweep is dozens of them. Resolving the whole log each time
    /// made the work quadratic in the length of the log - invisible during play, where
    /// lines arrive a few per second, and crippling when the tracker is started against a
    /// log a session has already filled. StateResolver stays the definition of the rules
    /// and is what every read path uses; this is the same answer, kept warm.
    /// </para>
    /// </remarks>
    private Dictionary<string, CompletionState> ResolveLive()
    {
        var resolved = new Dictionary<string, CompletionState>(_baseResolved, StringComparer.Ordinal);

        // Ahead of the corrections, so a player correction - including one that
        // deliberately sets a chest back to not-done - still wins.
        foreach ((string poiId, string questId) in _provenByQuest)
        {
            if (resolved.TryGetValue(questId, out CompletionState questState)
                && questState == CompletionState.Done)
            {
                resolved[poiId] = CompletionState.Done;
            }
        }

        foreach (ManualOverride correction in _overrides.Values)
        {
            resolved[correction.CatalogId] = correction.State;
        }

        return resolved;
    }

    private Dictionary<string, CatalogEntry> CatalogById()
    {
        if (_byId is null || _byId.Count != Catalog.Count)
        {
            _byId = Catalog.ToDictionary(e => e.Id, StringComparer.Ordinal);
        }

        return _byId;
    }

    /// <summary>
    /// Compares the resolved state against what was last known and records a timeline entry
    /// for every entry that has newly become <see cref="CompletionState.Done"/>.
    /// </summary>
    /// <remarks>
    /// Must be called with <see cref="_gate"/> already held. A snapshot re-asserts every
    /// entry the run has ever completed, so diffing against the previous resolution - not
    /// against "was this event a completion" - is what keeps a reload from replaying the
    /// entire run's history into the timeline at once.
    /// </remarks>
    private bool NoteNewUnlocks(DateTimeOffset observedAt, bool placeable)
    {
        bool placeWasUsed = false;
        bool linksChanged = false;
        bool sequenceGrew = false;

        Dictionary<string, CompletionState> resolved = ResolveLive();
        Dictionary<string, CatalogEntry> byId = CatalogById();

        // Sampled once per call, not once per unlock: every entry newly completed in the
        // same batch shares the same in-game-time reading, the same way they already share
        // the same real-time timestamp.
        double? igtElapsedSeconds = IgtSource?.Invoke() is { Active: true, Elapsed: { } elapsed }
            ? elapsed.TotalSeconds
            : null;

        foreach ((string id, CompletionState state) in resolved)
        {
            _lastKnownStates.TryGetValue(id, out CompletionState previous);

            if (state == CompletionState.Done && previous != CompletionState.Done
                && byId.TryGetValue(id, out CatalogEntry? entry) && entry.CountsToward
                && _everUnlocked.Add(entry.Id))
            {
                // A completion with no place of its own takes the player's. Points of
                // interest are excluded on purpose: theirs is the pin's real location,
                // which is both more accurate and available whether or not anyone was
                // standing there when it cleared.
                //
                // Only a live batch is placeable. A snapshot re-asserts the whole run at
                // once - after a reload it can turn dozens of entries done together - and
                // a hand-ticked correction happens at the dashboard, not in the world;
                // neither says anything about where the player was.
                if (placeable
                    && entry.Kind != TrackedKind.PointOfInterest
                    && _playerPlace is { } place
                    && !_finishedAt.ContainsKey(entry.Id))
                {
                    _finishedAt[entry.Id] = place;
                    placeWasUsed = true;
                }

                // Live completions only. The report on load re-asserts a whole run in
                // one batch, in no meaningful order, and pairing inside that batch would
                // be pairing dictionary iteration order.
                if (placeable)
                {
                    _completionOrder.Add(entry.Id);
                    sequenceGrew = true;
                }

                _unlocks.Add(new UnlockEvent(
                    entry.Id, entry.Kind, entry.DisplayName, entry.Dlc, entry.Region,
                    entry.Kind == TrackedKind.PointOfInterest
                        ? GameData.PinTypeName(entry.Region)
                        : null,
                    observedAt,
                    igtElapsedSeconds,
                    _playSeconds));
            }
        }

        _lastKnownStates.Clear();
        foreach ((string id, CompletionState state) in resolved)
        {
            _lastKnownStates[id] = state;
        }

        // A place is spent once something has taken it, so it can never be attached to a
        // second, later completion that happened somewhere else. It is deliberately *not*
        // cleared when nothing took it: the card sweep re-lists the whole collection, so
        // the place the reporter sent just before it has to survive every already-owned
        // card in that list to reach the one card that is actually new.
        if (placeWasUsed)
        {
            _playerPlace = null;
        }

        if (sequenceGrew)
        {
            // A chest and the hunt that opened it complete together, so every addition to
            // the sequence can complete a pair. The hand-curated links are merged over the
            // derived ones, not under them: they were each proven individually and are not
            // up for revision by a heuristic.
            Dictionary<string, string> derived = QuestPinLink.Derive(Catalog, _completionOrder, _finishedAt);
            foreach ((string poiId, string questId) in GameData.PoiProvenByQuest)
            {
                derived[poiId] = questId;
            }

            linksChanged = derived.Count != _provenByQuest.Count
                || derived.Any(link => !_provenByQuest.TryGetValue(link.Key, out string? was)
                                       || !string.Equals(was, link.Value, StringComparison.Ordinal));
            _provenByQuest = derived;
        }

        if (_unlocks.Count > UnlockHistoryLimit)
        {
            _unlocks.RemoveRange(0, _unlocks.Count - UnlockHistoryLimit);
        }

        return linksChanged;
    }

    /// <summary>
    /// Notes where the player is, for the next thing that completes to claim.
    /// </summary>
    /// <remarks>
    /// Deliberately not timestamped or expired. The reporter only emits one of these
    /// immediately before or after something it has just reported finishing, so the
    /// newest is always the right one; making it stale after some interval would only
    /// invent a way to lose a position that is already correct.
    /// </remarks>
    public void NotePlayerPlace(PlayerPlace place)
    {
        lock (_gate)
        {
            _playerPlace = place;
        }
    }

    /// <summary>
    /// Records that an ingestion source is alive, so the health panel can show when a
    /// source goes quiet. Liveness is tracked separately from observations because a
    /// source that is connected but has nothing new to report is still healthy.
    /// </summary>
    public void MarkSourceSeen(string key)
    {
        lock (_gate)
        {
            _sourceLastSeen[key] = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// The chest pins currently proven done by a treasure hunt, keyed by pin id, with the
    /// hand-curated links merged in. For reporting what a run has derived.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProvenByQuest
    {
        get { lock (_gate) { return new Dictionary<string, string>(_provenByQuest, StringComparer.Ordinal); } }
    }

    /// <summary>
    /// How long a silence ends a play session.
    /// </summary>
    /// <remarks>
    /// The game writes to its script log constantly while it is running - warnings, buff
    /// churn, streaming messages - so a gap this long means the game is closed, paused at
    /// the desktop, or the tracker was not running. Long enough not to be tripped by a
    /// loading screen; short enough that a night's sleep is never counted as play.
    /// </remarks>
    private static readonly TimeSpan SessionGap = TimeSpan.FromSeconds(60);

    /// <summary>Raised when something worth saving has changed.</summary>
    public event Action? RunChanged;

    /// <summary>
    /// Play time so far, across every session this run has lived through.
    /// </summary>
    public TimeSpan PlayElapsed
    {
        get { lock (_gate) { return TimeSpan.FromSeconds(_playSeconds); } }
    }

    /// <summary>
    /// Notes that the game is alive, which is what play time is measured from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called for every line the game writes, not only the ones the reporter produces:
    /// standing still in a field completes nothing but is still play, and the game's own
    /// log noise is the only evidence of it there is.
    /// </para>
    /// <para>
    /// Time is accumulated between consecutive signs of life, and a gap longer than
    /// <see cref="SessionGap"/> starts a new session instead of being counted. So closing
    /// the game, sleeping, and playing again tomorrow adds tomorrow's play and none of the
    /// night - which is what makes an elapsed time on the chart mean time spent, rather
    /// than time since the run began.
    /// </para>
    /// </remarks>
    public void NoteActivity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_lastActivityAt is { } last)
            {
                TimeSpan sinceLast = now - last;

                if (sinceLast > TimeSpan.Zero && sinceLast <= SessionGap)
                {
                    _playSeconds += sinceLast.TotalSeconds;
                }
            }

            _lastActivityAt = now;
        }
    }

    /// <summary>Everything about this run that is worth keeping between sessions.</summary>
    public PersistedRun Capture()
    {
        lock (_gate)
        {
            return new PersistedRun(
                RunStore.CurrentVersion,
                _runStartedAt,
                _playSeconds,
                _activeModeId,
                [.. _unlocks],
                new Dictionary<string, CompletionState>(_baseResolved, StringComparer.Ordinal),
                new Dictionary<string, PlayerPlace>(_finishedAt, StringComparer.Ordinal),
                [.. _completionOrder],
                [.. _overrides.Values]);
        }
    }

    /// <summary>
    /// Resumes a stored run. Call before any record arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters. What the run had already completed is seeded first, so that the
    /// report the game sends on its next load - which re-asserts every one of them - adds
    /// nothing to the history and leaves yesterday's timestamps alone. Only what is
    /// genuinely new to this session is recorded as new.
    /// </para>
    /// <para>
    /// The stored states are also replayed as a snapshot, which is what they are: a
    /// description of the whole world at a moment. That makes the dashboard correct the
    /// instant it opens, instead of reading zero until the game is next started.
    /// </para>
    /// </remarks>
    public void Restore(PersistedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        lock (_gate)
        {
            _runStartedAt = run.RunStartedAt;
            _playSeconds = run.PlaySeconds;
            _activeModeId = run.ActiveModeId;

            _unlocks.Clear();
            _unlocks.AddRange(run.Unlocks);

            _everUnlocked.Clear();
            foreach (UnlockEvent unlock in run.Unlocks)
            {
                _everUnlocked.Add(unlock.CatalogId);
            }

            _finishedAt.Clear();
            foreach ((string id, PlayerPlace place) in run.FinishedAt)
            {
                _finishedAt[id] = place;
            }

            _completionOrder.Clear();
            _completionOrder.AddRange(run.CompletionOrder);

            _overrides.Clear();
            foreach (ManualOverride correction in run.Overrides)
            {
                _overrides[correction.CatalogId] = correction;
            }

            // Seeded before anything is recorded, so the next report on load re-asserting
            // the whole run produces no new history.
            _lastKnownStates.Clear();
            foreach ((string id, CompletionState state) in run.States)
            {
                _lastKnownStates[id] = state;
            }

            _provenByQuest = QuestPinLink.Derive(Catalog, _completionOrder, _finishedAt);
            foreach ((string poiId, string questId) in GameData.PoiProvenByQuest)
            {
                _provenByQuest[poiId] = questId;
            }
        }

        // Outside the lock: Record takes it. A snapshot rather than a pile of events,
        // because that is what a description of the whole world is - and because it makes
        // the read paths, which resolve the event log, agree with the seeded view.
        if (run.States.Count > 0)
        {
            Record(EventSource.Snapshot, run.States, isSnapshot: true);
        }
    }

    /// <summary>Records or replaces a player correction.</summary>
    public void SetOverride(string catalogId, CompletionState state, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);

        lock (_gate)
        {
            _overrides[catalogId] = new ManualOverride(catalogId, state, reason, DateTimeOffset.UtcNow);
            _ = NoteNewUnlocks(DateTimeOffset.UtcNow, placeable: false);
        }

        Publish();
        RunChanged?.Invoke();
    }

    /// <summary>Removes a player correction, letting the automatic sources decide again.</summary>
    public void ClearOverride(string catalogId)
    {
        lock (_gate)
        {
            _overrides.Remove(catalogId);
            _ = NoteNewUnlocks(DateTimeOffset.UtcNow, placeable: false);
        }

        Publish();
        RunChanged?.Invoke();
    }

    /// <summary>Computes the current progress for every mode, plus the selection and timeline.</summary>
    public StateResponse Snapshot()
    {
        lock (_gate)
        {
            Dictionary<string, CompletionState> states = StateResolver.Resolve(_events, _overrides.Values, _provenByQuest);

            IReadOnlyList<RulesetProgress> modes =
                ProgressCalculator.ComputeAll(Catalog, Groups, Rulesets, Exceptions, states);

            // Newest first, so the overlay can just take the first few.
            UnlockEvent[] recent = [.. _unlocks.AsEnumerable().Reverse().Take(RecentUnlockCount)];

            IgtSample? igt = IgtSource?.Invoke();

            return new StateResponse(
                DateTimeOffset.UtcNow, modes, Catalog.Count, states.Count,
                _activeModeId, recent, _runStartedAt, _unlocks.Count,
                igt?.Active ?? false, igt?.Detail, igt?.Elapsed?.TotalSeconds);
        }
    }

    /// <summary>
    /// Every catalogue entry that can be drawn on a map, with the state it is in now.
    /// </summary>
    /// <remarks>
    /// Only points of interest carry coordinates - a quest is a journal entry, not a
    /// place, and nothing the reporter collects gives one a position (see
    /// <c>INTERACTIVE-MAP-NOTES.md</c>). Points are grouped by the streamed world they
    /// were recorded in, because their coordinates are local to it: the same X/Y means
    /// somewhere quite different in Skellige than in Velen, so a map has to be drawn one
    /// world at a time.
    /// </remarks>
    public MapResponse MapPoints()
    {
        lock (_gate)
        {
            Dictionary<string, CompletionState> states = StateResolver.Resolve(_events, _overrides.Values, _provenByQuest);

            // A point of interest is placed by its pin; everything else by where the
            // player was standing when it was finished, if the reporter said. The two are
            // folded into one list here so the map does not have to care which is which.
            IEnumerable<(CatalogEntry Entry, double X, double Y, string World)> placed = Catalog
                .Select(entry => entry.X is { } x && entry.Y is { } y && entry.World is { } world
                    ? (Entry: entry, X: x, Y: y, World: world)
                    : _finishedAt.TryGetValue(entry.Id, out PlayerPlace at)
                        ? (Entry: entry, X: at.X, Y: at.Y, World: at.World)
                        : default)
                .Where(candidate => candidate.Entry is not null);

            MapRegion[] regions =
            [
                .. placed
                    .GroupBy(candidate => candidate.World, StringComparer.Ordinal)
                    // Only worlds that are places a run is played through. The game reports
                    // a handful of others - a single point in Vizima's castle, two in the
                    // Spiral - which are staging areas for one scene apiece, not regions.
                    // Having a fitted transform is what tells them apart: a world worth
                    // drawing is one someone has drawn a map of.
                    .Where(group => FitFor(group.Key) is not null)
                    .OrderByDescending(group => group.Count())
                    .Select(group => new MapRegion(
                        group.Key,
                        [
                            .. group.Select(candidate => new MapPoint(
                                candidate.Entry.Id,
                                candidate.Entry.DisplayName,
                                candidate.Entry.Dlc,
                                candidate.Entry.Region,
                                candidate.Entry.Kind == TrackedKind.PointOfInterest && candidate.Entry.Region is not null
                                    ? GameData.PinTypeName(candidate.Entry.Region)
                                    : null,
                                candidate.Entry.CountsToward,
                                candidate.X,
                                candidate.Y,
                                states.TryGetValue(candidate.Entry.Id, out CompletionState state)
                                    && state == CompletionState.Done,
                                candidate.Entry.Kind.ToString()))
                        ],
                        FitFor(group.Key)?.Projection,
                        FitFor(group.Key)?.Matrix,
                        Backgrounds.FirstOrDefault(pair =>
                            group.Key.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase)).Value))
            ];

            return new MapResponse(regions);
        }

        // The catalogue records a world as the full path the game reports
        // (levels\novigrad\novigrad.w2w); the fits are filed under the file's own name,
        // because that is what identifies a region regardless of where it sits in the
        // game's tree. Matching on the suffix is what joins the two without either side
        // having to know the other's shape.
        MapCalibration? FitFor(string world) =>
            Calibration.FirstOrDefault(pair => world.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Describes each ingestion source for the health panel.</summary>
    public IReadOnlyList<SourceStatus> DescribeSources()
    {
        lock (_gate)
        {
            return
            [
                Describe("Savegame watcher", "savegame"),
                Describe("In-game reporter", "game"),
            ];
        }

        SourceStatus Describe(string name, string key)
        {
            DateTimeOffset? lastSeen = _sourceLastSeen.TryGetValue(key, out DateTimeOffset seen) ? seen : null;
            return new SourceStatus(name, lastSeen is not null, lastSeen, null);
        }
    }

    /// <summary>
    /// Streams a message every time the run changes, for the server-sent events endpoint.
    /// </summary>
    public async IAsyncEnumerable<string> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<string> channel = Channel.CreateUnbounded<string>();

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            // Send the current state immediately so a newly connected overlay is correct
            // before anything else happens.
            yield return Serialize(Snapshot());

            await foreach (string message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    /// <summary>
    /// Publishes the current state to every subscriber, at most once per
    /// <see cref="PublishInterval"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Building a payload means resolving the run, computing four modes over the whole
    /// catalogue and serialising the result. Doing that once per record is affordable
    /// while the game is being played, where records arrive a few per second - and ruinous
    /// when the tracker is started against a log that already exists, where a session's
    /// worth of them arrives at once. A 200,000-line log meant twenty thousand full
    /// payloads, none of which any subscriber could have seen: only the last one is the
    /// truth, and the ones in front of it are discarded microseconds later.
    /// </para>
    /// <para>
    /// So a change marks the state dirty and a payload follows shortly. Nothing is lost:
    /// the trailing publish always reflects everything recorded up to that moment, and a
    /// subscriber is sent the current state the instant it connects regardless.
    /// </para>
    /// <para>
    /// With no subscribers there is nothing to build at all, which is what makes
    /// <c>replay</c> and the self-tests pay none of this.
    /// </para>
    /// </remarks>
    private void Publish()
    {
        lock (_gate)
        {
            if (_subscribers.Count == 0 || _publishPending)
            {
                return;
            }

            _publishPending = true;
        }

        _ = PublishAfterDelayAsync();
    }

    private async Task PublishAfterDelayAsync()
    {
        await Task.Delay(PublishInterval).ConfigureAwait(false);

        string payload = Serialize(Snapshot());

        lock (_gate)
        {
            _publishPending = false;

            foreach (Channel<string> subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(payload);
            }
        }
    }

    private static string Serialize(StateResponse response) =>
        JsonSerializer.Serialize(response, ApiJsonContext.Default.StateResponse);

    private static HashSet<string> Scope(params string[] packs) => new(packs, StringComparer.Ordinal);
}
