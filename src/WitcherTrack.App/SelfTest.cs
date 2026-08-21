using System.Text.Json;
using WitcherTrack.Core;
using WitcherTrack.Core.Ingest;
using WitcherTrack.Core.Model;

namespace WitcherTrack.App;

/// <summary>
/// Executable checks for the rules that decide what a percentage means.
/// </summary>
/// <remarks>
/// These live in the shipped binary rather than in a test project on purpose: the
/// project has no third-party dependencies, so <c>WitcherTrack selftest</c> can be run
/// by anyone who downloaded the release to confirm the counting rules behave as
/// documented, without installing a test runner or rebuilding from source.
/// </remarks>
public static class SelfTest
{
    /// <summary>Runs every check and prints a report.</summary>
    /// <returns>Zero when everything passed, otherwise the number of failures.</returns>
    public static int Run()
    {
        var failures = new List<string>();

        Check(failures, "DLC scope filters the denominator", ScopeFiltersDenominator);
        Check(failures, "A 300% mode sums every content pack", CombinedModeSumsEverything);
        Check(failures, "Exclusion groups cap both numerator and denominator", ExclusionGroupsAreCapped);
        Check(failures, "Per-mode exceptions force entries in and out", ExceptionsOverrideScope);
        Check(failures, "Entries flagged as non-counting are ignored", NonCountingEntriesAreIgnored);
        Check(failures, "A manual override beats every automatic source", ManualOverrideWins);
        Check(failures, "A quest-proven point of interest reads done despite its own pin state", QuestProvenPoiIsForcedDone);
        Check(failures, "A manual override still beats a quest-proven correction", ManualOverrideBeatsQuestProof);
        Check(failures, "A snapshot supersedes everything recorded before it", SnapshotSupersedesEarlierEvents);
        Check(failures, "Events after a snapshot still apply", EventsAfterSnapshotApply);
        Check(failures, "Reporter lines are parsed out of noisy game log output", ReporterLinesAreParsed);
        Check(failures, "A framed dump becomes one snapshot", DumpBecomesSnapshot);
        Check(failures, "Records outside a dump are individual events", LooseRecordsAreEvents);
        Check(failures, "A restart discards a half-received dump", RestartDiscardsPartialDump);
        Check(failures, "Quest records carry their content pack and category", QuestExtrasAreParsed);
        Check(failures, "A point of interest's world path is parsed, and only when it looks like one", WorldPathIsParsed);
        Check(failures, "A player-position record is parsed and carries no identifier", PlayerPlaceIsParsed);
        Check(failures, "A reported place lands on the completion that follows it, once", PlaceLandsOnTheNextCompletion);
        Check(failures, "A treasure hunt proves the chest pin that cleared alongside it", ChestLinksAreDerived);
        Check(failures, "The live resolution matches resolving the whole log", LiveResolutionMatchesTheResolver);
        Check(failures, "A resumed run keeps its history and its timings", ResumingKeepsHistory);
        Check(failures, "A catalogue carries a point of interest's world path through", CatalogCarriesWorldPath);
        Check(failures, "A catalogue merges every dump it is given", CatalogMergesDumps);
        Check(failures, "Schematics are filed under the content pack that sells them", SchematicsCarryTheirContentPack);
        Check(failures, "The shipped catalogue agrees with the schematic content packs", ShippedCatalogAgreesOnSchematics);
        Check(failures, "The executable carries its own catalogue, map and licence", ExecutableCarriesItsOwnData);
        Check(failures, "The in-game-time clock recognises every known game build", GameBuildsAreDetected);
        Check(failures, "In-game time excludes loading screens and unreadable intervals", InGameTimeExcludesLoading);
        Check(failures, "A paused in-game clock keeps its total", InGameTimeSurvivesAPause);
        Check(failures, "Pausing and resuming the in-game clock is recorded with the real time", IgtControlsAreRecorded);

        Console.WriteLine();

        if (failures.Count == 0)
        {
            Console.WriteLine("All checks passed.");
            return 0;
        }

        Console.WriteLine($"{failures.Count} check(s) failed:");
        foreach (string failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return failures.Count;
    }

    // ------------------------------------------------------------------ checks

    private static void ScopeFiltersDenominator()
    {
        // Three ungrouped base quests, plus the two-branch exclusion group which
        // contributes its cap of one rather than both of its members.
        RulesetProgress progress = Compute("base100", NoState());
        AssertEqual(4, progress.Total, "base game total");

        // Hearts of Stone is standalone: it counts its own content only, and needs
        // nothing from the base game.
        RulesetProgress hearts = Compute("hos100", NoState());
        AssertEqual(1, hearts.Total, "Hearts of Stone total");

        RulesetProgress wine = Compute("baw100", NoState());
        AssertEqual(1, wine.Total, "Blood and Wine total");
    }

    private static void CombinedModeSumsEverything()
    {
        RulesetProgress progress = Compute("all300", NoState());

        // Three base entries, one Hearts of Stone, one Blood and Wine, plus the
        // two-branch exclusion group which contributes one.
        AssertEqual(6, progress.Total, "combined total");
    }

    private static void ExclusionGroupsAreCapped()
    {
        // Both branches of the group are marked done, which cannot happen in a real
        // playthrough but must not inflate the numerator if it ever does.
        var states = new Dictionary<string, CompletionState>(StringComparer.Ordinal)
        {
            ["quest.branch.a"] = CompletionState.Done,
            ["quest.branch.b"] = CompletionState.Done,
        };

        RulesetProgress progress = Compute("all300", states);
        AssertEqual(1, progress.Completed, "group contribution to numerator");
    }

    private static void ExceptionsOverrideScope()
    {
        // Force the Blood and Wine diagram into the base-game mode, and force one base
        // quest out of it.
        RulesetException[] exceptions =
        [
            new("base100", "diagram.baw", Include: true, "test"),
            new("base100", "quest.base.1", Include: false, "test"),
        ];

        RulesetProgress progress = ProgressCalculator.Compute(
            Catalog, Groups, Rulesets["base100"], exceptions, NoState());

        // Base game normally totals four; one entry is forced in and one forced out.
        AssertEqual(4, progress.Total, "total after exceptions");
    }

    private static void NonCountingEntriesAreIgnored()
    {
        CatalogEntry[] catalog =
        [
            new("a", TrackedKind.Quest, "A", "base"),
            new("b", TrackedKind.Quest, "B", "base", CountsToward: false),
        ];

        RulesetProgress progress = ProgressCalculator.Compute(
            catalog, Groups, Rulesets["base100"], [], NoState());

        AssertEqual(1, progress.Total, "total excluding non-counting entries");
    }

    private static void ManualOverrideWins()
    {
        ProgressEvent[] events =
        [
            new(1, Now, EventSource.Snapshot, "poi.bugged", CompletionState.NotDone, SnapshotId: "s1"),
        ];

        ManualOverride[] overrides =
        [
            new("poi.bugged", CompletionState.Done, "pin never clears, cleared in game", Now),
        ];

        Dictionary<string, CompletionState> states = StateResolver.Resolve(events, overrides);
        AssertEqual(CompletionState.Done, states["poi.bugged"], "overridden state");
    }

    private static void QuestProvenPoiIsForcedDone()
    {
        // The chest's own pin reads not_done, exactly the known bug this rule exists
        // for, but the quest that requires looting it reads done in the same snapshot.
        ProgressEvent[] events =
        [
            new(1, Now, EventSource.Snapshot, "quest.treasure", CompletionState.Done, SnapshotId: "s1"),
            new(2, Now, EventSource.Snapshot, "poi.chest", CompletionState.NotDone, SnapshotId: "s1"),
        ];

        var provenByQuest = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["poi.chest"] = "quest.treasure",
        };

        Dictionary<string, CompletionState> states = StateResolver.Resolve(events, provenByQuest: provenByQuest);
        AssertEqual(CompletionState.Done, states["poi.chest"], "quest-proven pin state");

        // No proof without the quest: an unmapped chest keeps whatever the pin itself says.
        ProgressEvent[] unmapped =
        [
            new(1, Now, EventSource.Snapshot, "poi.other_chest", CompletionState.NotDone, SnapshotId: "s1"),
        ];

        Dictionary<string, CompletionState> unmappedStates = StateResolver.Resolve(unmapped, provenByQuest: provenByQuest);
        AssertEqual(CompletionState.NotDone, unmappedStates["poi.other_chest"], "unmapped pin is untouched");
    }

    private static void ManualOverrideBeatsQuestProof()
    {
        // Even a proven correction has to yield to a player who has looked at the game
        // and knows better - for example, a future patch that fixes the underlying bug.
        ProgressEvent[] events =
        [
            new(1, Now, EventSource.Snapshot, "quest.treasure", CompletionState.Done, SnapshotId: "s1"),
            new(2, Now, EventSource.Snapshot, "poi.chest", CompletionState.NotDone, SnapshotId: "s1"),
        ];

        var provenByQuest = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["poi.chest"] = "quest.treasure",
        };

        ManualOverride[] overrides =
        [
            new("poi.chest", CompletionState.NotDone, "the fix shipped, this pin now reads correctly", Now),
        ];

        Dictionary<string, CompletionState> states = StateResolver.Resolve(events, overrides, provenByQuest);
        AssertEqual(CompletionState.NotDone, states["poi.chest"], "manual override over quest proof");
    }

    private static void SnapshotSupersedesEarlierEvents()
    {
        // A quest is reported complete, then the player dies and reloads an earlier
        // save. The next snapshot must roll the state back.
        ProgressEvent[] events =
        [
            new(1, Now, EventSource.GameEvent, "quest.base.1", CompletionState.Done),
            new(2, Now, EventSource.Snapshot, "quest.base.2", CompletionState.Done, SnapshotId: "s1"),
            new(3, Now, EventSource.Snapshot, "quest.base.3", CompletionState.NotDone, SnapshotId: "s1"),
        ];

        Dictionary<string, CompletionState> states = StateResolver.Resolve(events);

        AssertEqual(false, states.ContainsKey("quest.base.1"), "pre-snapshot event was discarded");
        AssertEqual(CompletionState.Done, states["quest.base.2"], "snapshot state");
    }

    private static void EventsAfterSnapshotApply()
    {
        ProgressEvent[] events =
        [
            new(1, Now, EventSource.Snapshot, "quest.base.1", CompletionState.NotDone, SnapshotId: "s1"),
            new(2, Now, EventSource.GameEvent, "quest.base.1", CompletionState.Done),
        ];

        Dictionary<string, CompletionState> states = StateResolver.Resolve(events);
        AssertEqual(CompletionState.Done, states["quest.base.1"], "post-snapshot event");
    }

    private static void ReporterLinesAreParsed()
    {
        // Real game log lines carry the engine's own decoration in front.
        const string line = "[2026.08.16 18:04:11][Script][WT]: WT|v1|quest|q104_wandering_in_the_dark|done";

        AssertEqual(true, ReporterProtocol.TryParse(line, out ReporterProtocol.Record record), "parsed a decorated line");
        AssertEqual(TrackedKind.Quest, record.Kind!.Value, "kind");
        AssertEqual("q104_wandering_in_the_dark", record.Id, "identifier");
        AssertEqual(CompletionState.Done, record.State, "state");

        // Unrelated engine output must be ignored rather than misread.
        AssertEqual(false, ReporterProtocol.TryParse("[Script] Error: something unrelated", out _), "ignored noise");

        // A point of interest that is only discovered has been seen, not cleared.
        AssertEqual(true, ReporterProtocol.TryParse("WT|v1|poi|q001_camp|discovered|BanditCamp|3", out ReporterProtocol.Record pin), "parsed a pin");
        AssertEqual(CompletionState.NotDone, pin.State, "discovered is not done");
    }

    private static void DumpBecomesSnapshot()
    {
        var ingest = new ReporterIngest();
        IReadOnlyDictionary<string, CompletionState>? snapshot = null;
        ingest.SnapshotReceived += s => snapshot = s;

        foreach (string line in new[]
        {
            "WT|v1|meta|begin|light",
            "WT|v1|quest|q1|done",
            "WT|v1|diagram|Diagram: Svarog runestone|done",
            "WT|v1|meta|count_quests|1",
            "WT|v1|meta|end|light",
        })
        {
            ingest.Accept(line);
        }

        AssertEqual(1, ingest.SnapshotCount, "snapshot count");
        AssertEqual(2, snapshot!.Count, "entries in the snapshot");
        AssertEqual(CompletionState.Done, snapshot["Diagram: Svarog runestone"], "diagram state");
    }

    private static void LooseRecordsAreEvents()
    {
        var ingest = new ReporterIngest();
        var seen = new List<string>();
        ingest.EventReceived += (id, _) => seen.Add(id);

        ingest.Accept("WT|v1|formula|Recipe for Swallow 1|done");

        AssertEqual(1, seen.Count, "event count");
        AssertEqual(0, ingest.SnapshotCount, "no snapshot was emitted");
    }

    private static void RestartDiscardsPartialDump()
    {
        var ingest = new ReporterIngest();
        bool sawSnapshot = false;
        ingest.SnapshotReceived += _ => sawSnapshot = true;

        ingest.Accept("WT|v1|meta|begin|full");
        ingest.Accept("WT|v1|quest|q1|done");

        // The game restarted before the dump finished.
        ingest.Reset();
        ingest.Accept("WT|v1|meta|end|full");

        AssertEqual(false, sawSnapshot, "an incomplete dump was not reported as a snapshot");
    }

    private static void QuestExtrasAreParsed()
    {
        // The reporter appends the content pack and the quest category, both read from
        // the engine rather than inferred from the identifier.
        const string line = "WT|v1|quest|q701_ofieri_mage|done|hos|story";

        AssertEqual(true, ReporterProtocol.TryParse(line, out ReporterProtocol.Record record), "parsed");
        AssertEqual("hos", record.Dlc!, "content pack");
        AssertEqual("story", record.Category!, "category");

        // Kinds that carry no extras must not invent any.
        AssertEqual(true, ReporterProtocol.TryParse("WT|v1|gwent|1002|done", out ReporterProtocol.Record card), "parsed a card");
        AssertEqual(null, card.Dlc, "a card has no content pack field");
    }

    private static void WorldPathIsParsed()
    {
        // New format: pin type, area, X, Y, world path, then a display name.
        const string full = @"WT|v1|poi|q001_camp|done|BanditCamp|3|142.93|-184.08|levels\novigrad\novigrad.w2w|Bandit Camp";
        AssertEqual(true, ReporterProtocol.TryParse(full, out ReporterProtocol.Record record), "parsed");
        AssertEqual(@"levels\novigrad\novigrad.w2w", record.WorldPath!, "world path");
        AssertEqual("Bandit Camp", record.DisplayName!, "display name after the world path");
        AssertEqual(142.93, record.Position!.Value.X, "position unaffected by the new field");

        // New format with no display name at all.
        const string noName = @"WT|v1|poi|q001_camp|done|BanditCamp|3|142.93|-184.08|levels\novigrad\novigrad.w2w";
        AssertEqual(true, ReporterProtocol.TryParse(noName, out ReporterProtocol.Record recordNoName), "parsed with no name");
        AssertEqual(@"levels\novigrad\novigrad.w2w", recordNoName.WorldPath!, "world path with no trailing name");
        AssertEqual(null, recordNoName.DisplayName, "no display name field present");

        // Older reporter build: coordinates, but no world path at all - still has to parse.
        const string noWorld = "WT|v1|poi|q001_camp|done|BanditCamp|3|142.93|-184.08";
        AssertEqual(true, ReporterProtocol.TryParse(noWorld, out ReporterProtocol.Record recordNoWorld), "parsed an older-format pin");
        AssertEqual(null, recordNoWorld.WorldPath, "no world path field to read");
        AssertEqual(142.93, recordNoWorld.Position!.Value.X, "position still reads");

        // A display name must never be mistaken for a world path just because it comes
        // right after the coordinates - only a path-shaped field counts as one.
        const string plainName = "WT|v1|poi|q001_camp|done|BanditCamp|3|142.93|-184.08|Grotto";
        AssertEqual(true, ReporterProtocol.TryParse(plainName, out ReporterProtocol.Record recordPlain), "parsed an older-format pin with a name");
        AssertEqual(null, recordPlain.WorldPath, "a plain word is not a world path");
        AssertEqual("Grotto", recordPlain.DisplayName!, "the name is read normally instead");

        // Oldest reporter build: no coordinates at all, so no world path either.
        const string noPosition = "WT|v1|poi|q001_camp|discovered|BanditCamp|3";
        AssertEqual(true, ReporterProtocol.TryParse(noPosition, out ReporterProtocol.Record recordNoPos), "parsed the oldest pin format");
        AssertEqual(null, recordNoPos.WorldPath, "no coordinates means no world path either");
    }

    private static void PlayerPlaceIsParsed()
    {
        // `at` has no identifier: the second field is already the X coordinate. What the
        // place belongs to is decided by whatever the app sees completing alongside it.
        const string here = @"WT|v1|at|142.93|-184.08|levels\novigrad\novigrad.w2w";
        AssertEqual(true, ReporterProtocol.TryParse(here, out ReporterProtocol.Record record), "parsed a position");
        AssertEqual(true, record.Place is not null, "the record carries a place");
        AssertEqual(142.93, record.Place!.Value.X, "X");
        AssertEqual(-184.08, record.Place!.Value.Y, "Y");
        AssertEqual(@"levels\novigrad\novigrad.w2w", record.Place!.Value.World, "world path");
        AssertEqual(null, record.Kind, "a position is not a tracked entry");
        AssertEqual("", record.Id, "a position has no identifier");

        // A position with a missing world, or with something unreadable where a number
        // belongs, is not half-usable - it is discarded rather than defaulted to zero.
        AssertEqual(false, ReporterProtocol.TryParse("WT|v1|at|142.93|-184.08", out _), "rejected a position with no world");
        AssertEqual(false, ReporterProtocol.TryParse(@"WT|v1|at|nowhere|-184.08|levels\novigrad\novigrad.w2w", out _), "rejected an unreadable X");

        // Every other record type still leaves Place unset.
        AssertEqual(true, ReporterProtocol.TryParse("WT|v1|gwent|1002|done", out ReporterProtocol.Record card), "parsed a card");
        AssertEqual(null, card.Place, "a card carries no place of its own");
    }

    private static void PlaceLandsOnTheNextCompletion()
    {
        var state = new TrackerState();
        state.Calibration["novigrad.w2w"] = new MapCalibration(
            @"levels\novigrad\novigrad.w2w", "linear", [[1, 0], [0, 1], [0, 0]]);
        state.Catalog.AddRange(
        [
            new CatalogEntry("q1", TrackedKind.Quest, "First quest", "base"),
            new CatalogEntry("q2", TrackedKind.Quest, "Second quest", "base"),
            new CatalogEntry("card1", TrackedKind.GwentCard, "A card already owned", "base"),
            new CatalogEntry("card2", TrackedKind.GwentCard, "The card that is new", "base"),
        ]);

        var here = new PlayerPlace(10, 20, @"levels\novigrad\novigrad.w2w");
        state.NotePlayerPlace(here);
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q1", CompletionState.Done)], isSnapshot: false);

        // The place a second quest finished was never reported, so it takes none: a stale
        // one would put it wherever the last thing happened, which is worse than nowhere.
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q2", CompletionState.Done)], isSnapshot: false);

        // One card is already in the collection before any of this.
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("card1", CompletionState.Done)], isSnapshot: false);

        // The card sweep re-lists the whole collection, one record at a time, so the place
        // reported just before it has to survive every already-owned card in that list to
        // reach the one card that is genuinely new.
        state.NotePlayerPlace(new PlayerPlace(-30, 40, @"levels\novigrad\novigrad.w2w"));
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("card1", CompletionState.Done)], isSnapshot: false);
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("card2", CompletionState.Done)], isSnapshot: false);

        MapRegion region = state.MapPoints().Regions.Single();
        Dictionary<string, MapPoint> placed = region.Points.ToDictionary(p => p.Id, StringComparer.Ordinal);

        AssertEqual(true, placed.ContainsKey("q1"), "the quest that finished where the player was is placed");
        AssertEqual(10.0, placed["q1"].X, "it is placed where the player was");
        AssertEqual(false, placed.ContainsKey("q2"), "a later completion does not reuse a spent place");
        AssertEqual(-30.0, placed["card2"].X, "the place reaches the card that is new");
        AssertEqual(false, placed.ContainsKey("card1"), "the already-owned card it passed through takes nothing");
    }

    private static void ChestLinksAreDerived()
    {
        const string world = @"levels\prolog_village\prolog_village.w2w";

        CatalogEntry[] catalog =
        [
            new("chestA", TrackedKind.PointOfInterest, "telescope spy", "base", "TreasureHuntMappin", World: world),
            new("chestB", TrackedKind.PointOfInterest, "camp1 creatures", "base", "TreasureHuntMappin", World: world),
            new("chestC", TrackedKind.PointOfInterest, "cemetary wraith", "base", "BossAndTreasure", World: world),
            new("huntA", TrackedKind.Quest, "Dirty Funds", "base", "treasure"),
            new("huntB", TrackedKind.Quest, "Temerian Valuables", "base", "treasure"),
            new("huntC", TrackedKind.Quest, "Scavenger Hunt: Viper School Gear", "base", "treasure"),
            new("diagram", TrackedKind.Diagram, "Diagram: Exploding bolt", "base"),
            new("formula", TrackedKind.Formula, "Torn-out page: Arachas decoction", "base"),
            new("whetstone", TrackedKind.PointOfInterest, "whale whetstone", "base", "Whetstone", World: world),
            new("sidequest", TrackedKind.Quest, "Twisted Firestarter", "base", "side"),
        ];

        // The order White Orchard actually produced, in miniature: a chest opened earlier,
        // then the chest-and-hunt pair, then a hunt with no pin of its own, then a hunt
        // whose pin only cleared on the next sweep.
        string[] order =
        [
            "chestA",                              // a hidden treasure with no tracked quest
            "diagram", "chestB", "formula", "huntA",  // one chest opened: pin, loot, quest
            "huntB",                               // a hunt with no chest pin at all
            "sidequest", "whetstone",              // adjacent, and neither is of the right sort
            "huntC", "chestC",                     // quest first, pin on the following sweep
        ];

        Dictionary<string, string> links = QuestPinLink.Derive(catalog, order);

        AssertEqual(2, links.Count, "exactly two links are asserted");
        AssertEqual("huntA", links["chestB"], "the hunt that closed as the chest cleared proves it");
        AssertEqual("huntC", links["chestC"], "and it works with the pin reported after the quest");
        AssertEqual(false, links.ContainsKey("chestA"), "an earlier chest is not claimed by a later hunt");
        AssertEqual(false, links.ContainsKey("whetstone"), "a non-chest pin is never linked");

        // Adjacency is what does the work, so removing what sits between two things must
        // change the answer: with chestB gone, chestA is now adjacent to huntA - but the
        // eight-completion allowance still refuses it, because it is far too early.
        // Padded with repeats of one diagram, which stands in for whatever ten unrelated
        // completions would be: only the kind and the position matter here.
        string[] farApart =
        [
            "chestA",
            "diagram", "diagram", "diagram", "diagram", "diagram",
            "diagram", "diagram", "diagram", "diagram", "diagram",
            "huntA",
        ];
        AssertEqual(0, QuestPinLink.Derive(catalog, farApart).Count,
                    "adjacency alone is not enough when the two are a run apart");

        // Same world only. A chest cannot have been opened from another map.
        var elsewhere = new Dictionary<string, PlayerPlace>(StringComparer.Ordinal)
        {
            ["huntA"] = new PlayerPlace(0, 0, @"levels\skellige\skellige.w2w"),
        };
        Dictionary<string, string> guarded = QuestPinLink.Derive(catalog, order, elsewhere);
        AssertEqual(false, guarded.ContainsKey("chestB"), "a hunt finished in another world proves nothing");
        AssertEqual("huntC", guarded["chestC"], "the other link is unaffected");

        // And the link does what it is for: forcing the pin done when the pin says no.
        ProgressEvent[] events =
        [
            new(1, Now, EventSource.Snapshot, "huntA", CompletionState.Done, SnapshotId: "s1"),
            new(2, Now, EventSource.Snapshot, "chestB", CompletionState.NotDone, SnapshotId: "s1"),
        ];

        Dictionary<string, CompletionState> states = StateResolver.Resolve(events, provenByQuest: links);
        AssertEqual(CompletionState.Done, states["chestB"], "the pin is forced done by its hunt");
    }

    private static void LiveResolutionMatchesTheResolver()
    {
        // TrackerState keeps a running resolution instead of replaying the log on every
        // record, because a record arrives for every line the reporter writes. StateResolver
        // stays the definition of the rules, so the two must not be able to disagree - and
        // the cases where they could are all here: a snapshot discarding what came before,
        // events landing after it, a chest proven by its hunt, and a correction on top.
        var state = new TrackerState();
        state.Catalog.AddRange(
        [
            new CatalogEntry("q1", TrackedKind.Quest, "First", "base"),
            new CatalogEntry("q2", TrackedKind.Quest, "Second", "base"),
            new CatalogEntry("q3", TrackedKind.Quest, "Third", "base"),
            new CatalogEntry("poi1", TrackedKind.PointOfInterest, "A pin", "base", "BanditCamp"),
        ]);

        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q1", CompletionState.Done)], isSnapshot: false);
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q2", CompletionState.Done)], isSnapshot: false);

        // An earlier save: the snapshot says q1 was never done, and q2 is not mentioned at
        // all, which means not done. Both have to disappear from the count.
        state.Record(EventSource.Snapshot,
            [new KeyValuePair<string, CompletionState>("q1", CompletionState.NotDone), new KeyValuePair<string, CompletionState>("poi1", CompletionState.Done)], isSnapshot: true);

        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q3", CompletionState.Done)], isSnapshot: false);
        state.SetOverride("q2", CompletionState.Done, "cleared in game, pin never moved");

        // Snapshot() goes through StateResolver over the whole log. The percentage it
        // reports is the number the running resolution has to agree with.
        StateResponse snapshot = state.Snapshot();
        RulesetProgress progress = snapshot.Modes.Single(m => m.RulesetId == "base100");

        AssertEqual(3, progress.Completed, "poi1 from the snapshot, q3 after it, q2 by correction");
        AssertEqual(4, progress.Total, "the whole catalogue counts");

        // And the timeline, which is what the running resolution drives, must name the
        // same three - no more, and not q1, which the snapshot took back.
        string[] unlocked = [.. state.Timeline().Unlocks.Select(u => u.CatalogId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        AssertEqual("poi1,q1,q2,q3", string.Join(',', unlocked), "everything that was ever done appears in the history");

        // The history keeps q1 because it genuinely was completed once. What must not
        // happen is the count still believing it, which is what the 3 above rules out.
    }

    private static void ResumingKeepsHistory()
    {
        CatalogEntry[] catalog =
        [
            new("q1", TrackedKind.Quest, "Yesterday", "base"),
            new("q2", TrackedKind.Quest, "Also yesterday", "base"),
            new("q3", TrackedKind.Quest, "Today", "base"),
        ];

        // Yesterday's run: two completions, four hours of play apart, saved and closed.
        var yesterday = new TrackerState();
        yesterday.Catalog.AddRange(catalog);
        yesterday.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q1", CompletionState.Done)], isSnapshot: false);
        yesterday.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q2", CompletionState.Done)], isSnapshot: false);

        PersistedRun stored = yesterday.Capture();
        AssertEqual(2, stored.Unlocks.Count, "both completions were captured");

        // Today: the tracker starts again and the game sends its report on load, which
        // re-asserts everything the run has ever done.
        var today = new TrackerState();
        today.Catalog.AddRange(catalog);
        today.Restore(stored);

        AssertEqual(2, today.Timeline().Unlocks.Count, "resuming adds no history of its own");

        today.Record(EventSource.Snapshot,
            [
                new KeyValuePair<string, CompletionState>("q1", CompletionState.Done),
                new KeyValuePair<string, CompletionState>("q2", CompletionState.Done),
            ], isSnapshot: true);

        // This is the whole point: the report says these are done, and the run already
        // knew, so nothing is re-dated to now.
        AssertEqual(2, today.Timeline().Unlocks.Count, "the report on load re-dates nothing");
        AssertEqual(stored.Unlocks[0].Timestamp, today.Timeline().Unlocks[0].Timestamp, "yesterday's timestamp survived");

        // And the dashboard is already correct, before the game has said anything new.
        RulesetProgress resumed = today.Snapshot().Modes.Single(m => m.RulesetId == "base100");
        AssertEqual(2, resumed.Completed, "a resumed run counts what it had done");

        // Something genuinely new is still recorded.
        today.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q3", CompletionState.Done)], isSnapshot: false);
        AssertEqual(3, today.Timeline().Unlocks.Count, "a new completion is still new");

        // The case that actually bit: the tracker re-reads the whole script log when it
        // starts, so a session's own history arrives a second time - beginning with the
        // report the game wrote at the *start* of that session, when almost nothing was
        // done. Everything then completes again as the log replays. None of it is new.
        today.Record(EventSource.Snapshot, [], isSnapshot: true);
        today.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q1", CompletionState.Done)], isSnapshot: false);
        today.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q2", CompletionState.Done)], isSnapshot: false);
        today.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>("q3", CompletionState.Done)], isSnapshot: false);

        AssertEqual(3, today.Timeline().Unlocks.Count, "replaying a whole session adds nothing");
        AssertEqual(stored.Unlocks[0].Timestamp, today.Timeline().Unlocks[0].Timestamp, "and still has not moved a timestamp");

        // A reset leaves nothing behind for the next start to pick up.
        today.Reset();
        AssertEqual(0, today.Capture().Unlocks.Count, "a reset run captures nothing");
        AssertEqual(0.0, today.Capture().PlaySeconds, "and no play time");
    }

    private static void CatalogCarriesWorldPath()
    {
        string[] log =
        [
            "WT|v1|meta|begin|light",
            @"WT|v1|poi|q001_camp|done|BanditCamp|3|142.93|-184.08|levels\novigrad\novigrad.w2w|Bandit Camp",
            "WT|v1|meta|end|light",
        ];

        CatalogBuilder.Result result = CatalogBuilder.FromScriptLog(log);
        CatalogEntry entry = result.Entries.Single(e => e.Id == "q001_camp");

        AssertEqual(@"levels\novigrad\novigrad.w2w", entry.World!, "world path carried into the catalogue");
        AssertEqual(142.93, entry.X!.Value, "position still carried too");
    }

    private static void CatalogMergesDumps()
    {
        // A log holds one dump per savegame load. The reference save is the fullest one,
        // so the largest dump wins regardless of the order they appear in.
        string[] log =
        [
            "WT|v1|meta|begin|light",
            "WT|v1|quest|q1|done|base|story",
            "WT|v1|meta|end|light",
            "WT|v1|meta|begin|light",
            "WT|v1|quest|q1|done|base|story",
            "WT|v1|quest|q2|not_done|hos|side",
            "WT|v1|quest|lw_cp33_sunken_treasure B8486EAF-4E34ECCB-69896A96-1E5CB685|done|baw|treasure",
            "WT|v1|meta|end|light",
        ];

        CatalogBuilder.Result result = CatalogBuilder.FromScriptLog(log);

        AssertEqual(2, result.DumpsFound, "dumps found");
        AssertEqual(3, result.Entries.Count, "catalogue merges every dump");
        AssertEqual(3, result.Entries.Count, "catalogue size");

        CatalogEntry hearts = result.Entries.First(e => e.Id == "q2");
        AssertEqual("hos", hearts.Dlc, "content pack of an unfinished quest");

        // The trailing GUID is stripped from the display name; the identifier keeps it.
        CatalogEntry treasure = result.Entries.First(e => e.Dlc == "baw");
        AssertEqual("lw cp33 sunken treasure", treasure.DisplayName, "display name");
    }

    private static void SchematicsCarryTheirContentPack()
    {
        // The game sends a content pack for quests and nothing for schematics: the fifth
        // field of a diagram record is empty. Attribution therefore comes entirely from
        // GameData.SchematicContentPacks, and anything absent from it stays base-game.
        string[] log =
        [
            "WT|v1|meta|begin|light",
            "WT|v1|diagram|Knight Geralt Armor 3 schematic|done||Diagram: Toussaint knight's armor",
            "WT|v1|diagram|Knight Geralt A Armor 3 schematic|done||Diagram: Toussaint knight's armor",
            "WT|v1|diagram|Light Armor 1 schematic|done||Diagram: Leather jacket",
            "WT|v1|meta|end|light",
        ];

        CatalogBuilder.Result result = CatalogBuilder.FromScriptLog(log);

        AssertEqual(3, result.Entries.Count, "three schematics catalogued");
        AssertEqual("base", result.Entries.Single(e => e.Id == "Light Armor 1 schematic").Dlc, "an unlisted schematic stays base-game");

        // Both halves of the name collision have to be attributed independently. The game
        // reports one display name for two schematics - the plain Toussaint knight's set
        // and the tourney set - so a table keyed on names would file only one of them.
        CatalogEntry plain = result.Entries.Single(e => e.Id == "Knight Geralt Armor 3 schematic");
        CatalogEntry tourney = result.Entries.Single(e => e.Id == "Knight Geralt A Armor 3 schematic");

        AssertEqual("baw", plain.Dlc, "the knight's armor diagram");
        AssertEqual("baw", tourney.Dlc, "the tourney armor diagram, which shares its name");
        AssertEqual(plain.DisplayName, tourney.DisplayName, "and the two names really are identical");

        // The two hand-collected lists, in the sizes they were delivered in.
        AssertEqual(65, GameData.SchematicContentPacks.Values.Count(pack => pack == "baw"), "Blood and Wine schematics on file");
        AssertEqual(29, GameData.SchematicContentPacks.Values.Count(pack => pack == "hos"), "Hearts of Stone schematics on file");
    }

    private static void ShippedCatalogAgreesOnSchematics()
    {
        // The table above only decides what a *rebuilt* catalogue says. What every install
        // actually counts is the shipped catalog.json, so the two have to agree: adding an
        // identifier to GameData without rebuilding the catalogue changes nothing at all,
        // and does so silently.
        string? path = new[]
            {
                "catalog.json",
                Path.Combine("data", "catalog.json"),
                Path.Combine(AppContext.BaseDirectory, "catalog.json"),
                Path.Combine(AppContext.BaseDirectory, "data", "catalog.json"),
            }
            .FirstOrDefault(File.Exists);

        if (path is null)
        {
            Console.WriteLine("        (no catalog.json to check against)");
            return;
        }

        CatalogEntry[] entries = JsonSerializer.Deserialize(
            File.ReadAllText(path), ApiJsonContext.Default.CatalogEntryArray) ?? [];

        Dictionary<string, CatalogEntry> byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);

        foreach ((string id, string pack) in GameData.SchematicContentPacks)
        {
            AssertTrue(byId.ContainsKey(id), $"the shipped catalogue to hold {id}");
            AssertEqual(pack, byId[id].Dlc, $"the content pack of {id}");
        }
    }

    private static void ExecutableCarriesItsOwnData()
    {
        // A release is one file, and that promise is invisible from inside a checkout:
        // every path also resolves against the data folder, so dropping an EmbeddedResource
        // line from the project file breaks nothing here and everything for whoever
        // downloads the bare executable. This is the only thing that notices.
        string? catalog = EmbeddedAssets.ReadText("catalog.json");
        AssertTrue(catalog is not null, "a catalogue inside the executable");

        CatalogEntry[] entries = JsonSerializer.Deserialize(
            catalog!, ApiJsonContext.Default.CatalogEntryArray) ?? [];
        AssertTrue(entries.Length > 0, "the embedded catalogue to hold entries");

        AssertTrue(EmbeddedAssets.ReadText("calibration.json") is not null, "the map transforms");

        string? index = EmbeddedAssets.ReadText("backgrounds.json");
        AssertTrue(index is not null, "the background index");

        // Every picture the index names has to be in there too. Asked this way rather
        // than against a list of region names, adding a region covers itself. Without the
        // artwork the map view draws points on nothing, which reads as a broken fit
        // rather than as a missing file.
        Dictionary<string, MapBackground> backgrounds = JsonSerializer.Deserialize(
            index!, ApiJsonContext.Default.DictionaryStringMapBackground) ?? [];

        AssertTrue(backgrounds.Count > 0, "the index to name at least one region");

        foreach ((string region, MapBackground image) in backgrounds)
        {
            using Stream? picture = EmbeddedAssets.Open(image.Image);
            AssertTrue(picture is not null, $"artwork for {region} ({image.Image})");
        }

        // The pictures are CDPR's, redistributed under CC BY-NC-SA 4.0. Carrying them
        // inside the binary is only defensible if the binary can state the terms without
        // any file beside it, which is what "WitcherTrack credits" prints.
        string? licence = EmbeddedAssets.ReadText("LICENSE");
        AssertTrue(licence is not null, "the licence inside the executable");
        AssertTrue(licence!.Contains("NonCommercial", StringComparison.Ordinal), "the licence to name the NonCommercial term");
    }

    private static void GameBuildsAreDetected()
    {
        // Straight from the autosplitter's own version table. A wrong offset would read
        // whatever else happens to sit at that address and say nothing is wrong, so every
        // one of the five builds it recognises has to match exactly here too.
        AssertEqual("standard", GameBuildDetector.Detect("3.0.19.14337", @"C:\Witcher 3\bin\x64\witcher3.exe"), "standard");
        AssertEqual("gog_goty", GameBuildDetector.Detect("3.0.19.14336", @"C:\Witcher 3\bin\x64\witcher3.exe"), "gog_goty");
        AssertEqual("old_patch", GameBuildDetector.Detect("3.0.4.58000", @"C:\Witcher 3\bin\x64\witcher3.exe"), "old_patch");

        // The one file version both Complete Edition builds share: DirectX 11 and 12 are
        // told apart only by the folder the executable runs from.
        AssertEqual(
            "complete_edition_dx11",
            GameBuildDetector.Detect("4.0.1.37654", @"C:\Witcher 3\bin\x64\witcher3.exe"),
            "complete edition, dx11 folder");
        AssertEqual(
            "complete_edition_dx12",
            GameBuildDetector.Detect("4.0.1.37654", @"C:\Witcher 3\bin\x64_dx12\witcher3.exe"),
            "complete edition, dx12 folder");
        AssertEqual(
            "complete_edition_dx12",
            GameBuildDetector.Detect("4.0.1.37654", @"C:\Witcher 3\bin\X64_DX12\witcher3.exe"),
            "complete edition, dx12 folder is matched case-insensitively");

        // A version that is none of the five known builds is reported as unrecognised
        // rather than guessed at.
        AssertEqual(null, GameBuildDetector.Detect("1.0.0.0", @"C:\Witcher 3\bin\x64\witcher3.exe"), "unrecognised build");
    }

    private static void InGameTimeExcludesLoading()
    {
        var accumulator = new IgtAccumulator();
        var second = TimeSpan.FromSeconds(1);

        // Three seconds of play, two of loading, one more of play. Six seconds passed and
        // four of them count, which is the whole point of reading the flag at all.
        foreach (bool notLoading in new[] { true, true, true, false, false, true })
        {
            accumulator.Sample(second, notLoading);
        }

        AssertEqual(TimeSpan.FromSeconds(4), accumulator.Elapsed, "in-game time");
        AssertEqual(false, accumulator.Loading, "the last reading");

        // A failed read charges nothing and does not pretend to know what the game was
        // doing. A tracker left running with the game closed would otherwise quietly
        // accumulate hours of in-game time nobody played.
        accumulator.Sample(TimeSpan.FromHours(3), null);
        AssertEqual(TimeSpan.FromSeconds(4), accumulator.Elapsed, "in-game time after a failed read");
        AssertEqual(false, accumulator.Loading, "the last reading is left alone by a failed read");
    }

    private static void InGameTimeSurvivesAPause()
    {
        var accumulator = new IgtAccumulator();
        accumulator.Sample(TimeSpan.FromSeconds(30), true);

        // Stopping the clock forgets what the game was doing but not how long it was
        // played: pausing a run at the end of an evening and resuming it the next day has
        // to continue the total, or the option is a trap rather than a feature.
        accumulator.Forget();
        AssertEqual(TimeSpan.FromSeconds(30), accumulator.Elapsed, "the total across a pause");
        AssertEqual(null, accumulator.Loading, "the loading flag after a pause");

        accumulator.Sample(TimeSpan.FromSeconds(10), true);
        AssertEqual(TimeSpan.FromSeconds(40), accumulator.Elapsed, "the total after resuming");

        // Only a new run zeroes it, which is what seeding is for - and it is also how a
        // run resumed from disk gets yesterday's total back.
        accumulator.Seed(TimeSpan.Zero);
        AssertEqual(TimeSpan.Zero, accumulator.Elapsed, "the total after a reset");
    }

    private static void IgtControlsAreRecorded()
    {
        var state = new TrackerState();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // The console line is half of what this is for, so it is checked rather than
        // spilled into the report: a validator reading a session's output has to be able
        // to see the pause without the run file in front of them.
        TextWriter console = Console.Out;
        var written = new StringWriter();

        try
        {
            Console.SetOut(written);
            state.NoteIgtControl(IgtControl.Started, TimeSpan.Zero);
            state.NoteIgtControl(IgtControl.Paused, TimeSpan.FromMinutes(62));
            state.NoteIgtControl(IgtControl.Started, TimeSpan.FromMinutes(62));
        }
        finally
        {
            Console.SetOut(console);
        }

        string log = written.ToString();
        AssertTrue(log.Contains("in-game timer paused at 1:02:00"), "the console says what was paused and where");
        AssertTrue(log.Contains("in-game timer started at 1:02:00"), "the console says where it resumed from");

        IReadOnlyList<IgtControlEvent> controls = state.Timeline().IgtControls;
        AssertEqual(3, controls.Count, "the acts recorded");
        AssertEqual(IgtControl.Paused, controls[1].Action, "the second act");
        AssertEqual(3720d, controls[1].ElapsedSeconds, "the total at the pause");

        // Real time, moving forwards. The gap between a pause and the start after it is
        // the whole point: it is how long the run was not being played.
        AssertTrue(controls[0].At >= before, "the first act is stamped with a real instant");
        AssertTrue(controls[2].At >= controls[1].At, "the acts are in order");

        // A pause taken last night has to still be there this morning.
        var resumed = new TrackerState();
        resumed.Restore(state.Capture());
        AssertEqual(3, resumed.Timeline().IgtControls.Count, "the acts survive being stored and read back");

        // A new run gets a clean record, because the pauses of the old one were not this
        // run's pauses.
        resumed.Reset();
        AssertEqual(0, resumed.Timeline().IgtControls.Count, "resetting the run clears the record");
    }

    // ------------------------------------------------------------------ fixture

    private static DateTimeOffset Now => DateTimeOffset.UnixEpoch;

    private static readonly CatalogEntry[] Catalog =
    [
        new("quest.base.1", TrackedKind.Quest, "Base quest 1", "base"),
        new("quest.base.2", TrackedKind.Quest, "Base quest 2", "base"),
        new("quest.base.3", TrackedKind.Quest, "Base quest 3", "base"),
        new("quest.hos.1", TrackedKind.Quest, "Hearts of Stone quest", "hos"),
        new("diagram.baw", TrackedKind.Diagram, "Blood and Wine diagram", "baw"),
        new("quest.branch.a", TrackedKind.Quest, "Branch A", "base", GroupId: "choice"),
        new("quest.branch.b", TrackedKind.Quest, "Branch B", "base", GroupId: "choice"),
    ];

    private static readonly Dictionary<string, ExclusionGroup> Groups = new(StringComparer.Ordinal)
    {
        ["choice"] = new("choice", MaxCount: 1, "Only one branch of this choice is reachable per playthrough."),
    };

    private static readonly Dictionary<string, Ruleset> Rulesets = new(StringComparer.Ordinal)
    {
        ["base100"] = new("base100", "100% Base Game", "100%", new HashSet<string>(StringComparer.Ordinal) { "base" }, 10),
        ["hos100"] = new("hos100", "100% Hearts of Stone", "100%", new HashSet<string>(StringComparer.Ordinal) { "hos" }, 20),
        ["baw100"] = new("baw100", "100% Blood and Wine", "100%", new HashSet<string>(StringComparer.Ordinal) { "baw" }, 30),
        ["all300"] = new("all300", "300%", "300%", new HashSet<string>(StringComparer.Ordinal) { "base", "hos", "baw" }, 40),
    };

    private static Dictionary<string, CompletionState> NoState() => new(StringComparer.Ordinal);

    private static RulesetProgress Compute(string rulesetId, Dictionary<string, CompletionState> states) =>
        ProgressCalculator.Compute(Catalog, Groups, Rulesets[rulesetId], [], states);

    // ------------------------------------------------------------------ harness

    private static void Check(List<string> failures, string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  FAIL  {name}: {exception.Message}");
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void AssertTrue(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"expected {what}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected {what} to be {expected} but it was {actual}");
        }
    }
}
