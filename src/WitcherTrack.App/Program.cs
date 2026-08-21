using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitcherTrack.App;
using WitcherTrack.Core;
using WitcherTrack.Core.Ingest;
using WitcherTrack.Core.Model;
using WitcherTrack.SaveFormat;

// WitcherTrack — a completion tracker for The Witcher 3: Wild Hunt.
//
// Ships as a single self-contained executable: download it, run it, and the browser
// opens on the dashboard. There is nothing to install and no configuration to edit
// before the first run.
//
//   WitcherTrack                serve the dashboard and overlay (default)
//   WitcherTrack parse <file>   inspect one savegame and print what was found
//   WitcherTrack selftest       verify the completion rules
//   WitcherTrack credits        licence, and the terms the bundled artwork travels under
//   WitcherTrack --help         usage

const int DefaultPort = 7355;

string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";

switch (verb)
{
    case "selftest":
        return SelfTest.Run();

    case "parse":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: WitcherTrack parse <savegame.sav>");
            return 2;
        }

        return ParseSavegame(args[1]);

    case "catalog":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: WitcherTrack catalog <scriptslog.txt> [output.json]");
            return 2;
        }

        return BuildCatalog(args[1..]);

    case "replay":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: WitcherTrack replay <scriptslog.txt>");
            return 2;
        }

        return Replay(args[1]);

    case "diff":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: WitcherTrack diff <reference.csv> [Quest|Diagram|Formula|PointOfInterest|GwentCard]");
            return 2;
        }

        return Diff(args[1], args.Length > 2 ? args[2] : null);

    case "export":
        return Export(args.Length > 1 ? args[1] : "catalog.csv");

    case "credits":
    case "license":
    case "licence":
        return PrintCredits();

    case "-h":
    case "--help":
    case "help":
        PrintUsage();
        return 0;

    case "serve":
        await ServeAsync(DefaultPort);
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command '{verb}'.");
        PrintUsage();
        return 2;
}

static void PrintUsage()
{
    Console.WriteLine("WitcherTrack - completion tracker for The Witcher 3: Wild Hunt");
    Console.WriteLine();
    Console.WriteLine("  WitcherTrack                serve the dashboard and overlay on http://127.0.0.1:7355");
    Console.WriteLine("  WitcherTrack parse <file>   inspect one savegame and print what was found");
    Console.WriteLine("  WitcherTrack catalog <log>  build the catalogue from a reference dump");
    Console.WriteLine("  WitcherTrack replay <log>   replay a log and print where the run stands");
    Console.WriteLine("  WitcherTrack export [file]  write the catalogue out as a table");
    Console.WriteLine("  WitcherTrack diff <list.csv> name what is missing versus a list you keep");
    Console.WriteLine("  WitcherTrack selftest       verify the completion rules");
    Console.WriteLine("  WitcherTrack credits        licence and third-party terms");
}

/// <summary>
/// Prints the licence, which travels inside the executable.
/// </summary>
/// <remarks>
/// The binary carries the region artwork, which is CDPR's and redistributed under
/// CC BY-NC-SA 4.0. Once a picture is inside an executable, the file next to it that used
/// to state the terms may not be there at all, so the executable has to be able to state
/// them on its own. The map view keeps its own credit line for the same reason, at the
/// place the artwork is actually looked at.
/// </remarks>
static int PrintCredits()
{
    string? licence = EmbeddedAssets.ReadText("LICENSE");

    if (licence is null)
    {
        Console.Error.WriteLine("This build carries no licence text. See LICENSE in the repository.");
        return 1;
    }

    Console.WriteLine(licence.TrimEnd());
    return 0;
}

/// <summary>
/// Compares the catalogue against a list kept by hand and names what is missing on either
/// side.
/// </summary>
static int Diff(string referencePath, string? kindText)
{
    if (!File.Exists(referencePath))
    {
        Console.Error.WriteLine($"No such file: {referencePath}");
        return 2;
    }

    TrackedKind? kind = null;
    if (kindText is not null)
    {
        if (!Enum.TryParse(kindText, ignoreCase: true, out TrackedKind parsed))
        {
            Console.Error.WriteLine($"Unknown kind '{kindText}'. Use Quest, Diagram, Formula, PointOfInterest or GwentCard.");
            return 2;
        }

        kind = parsed;
    }

    var state = new TrackerState();
    LoadCatalog(state);

    if (state.Catalog.Count == 0)
    {
        Console.Error.WriteLine("No catalogue to compare against.");
        return 1;
    }

    IReadOnlyList<string> reference = ReferenceList.ReadNames(File.ReadLines(referencePath));
    ReferenceList.Comparison result = ReferenceList.Compare(reference, state.Catalog, kind);

    Console.WriteLine($"Reference {reference.Count:N0} names, catalogue {state.Catalog.Count(e => kind is null || e.Kind == kind):N0} entries");
    Console.WriteLine($"Matched   {result.Matched.Count:N0}");
    Console.WriteLine();

    if (result.Matched.Count == 0)
    {
        Console.WriteLine("Nothing matched at all. The catalogue is probably still keyed on the game's");
        Console.WriteLine("internal identifiers, which share nothing with display names. Rebuild it from a");
        Console.WriteLine("dump taken with a reporter that sends localised names.");
        return 0;
    }

    Report("In the reference list but never reported by the game", result.MissingFromCatalog);
    Report("Reported by the game but absent from the reference list", result.MissingFromReference);

    return 0;

    static void Report(string title, IReadOnlyList<string> names)
    {
        Console.WriteLine($"{title}: {names.Count}");

        foreach (string name in names)
        {
            Console.WriteLine($"    {name}");
        }

        Console.WriteLine();
    }
}

/// <summary>
/// Writes the catalogue out as a spreadsheet-friendly table.
/// </summary>
/// <remarks>
/// Meant for checking the catalogue against notes kept by hand: sort it, filter it, and
/// diff the names. The identifier is included alongside the display name so an entry can
/// always be traced back to what the game actually reported.
/// </remarks>
static int Export(string outputPath)
{
    var state = new TrackerState();
    LoadCatalog(state);

    if (state.Catalog.Count == 0)
    {
        Console.Error.WriteLine("No catalogue to export.");
        return 1;
    }

    var lines = new List<string> { "kind;dlc;category;counts;name;id" };

    lines.AddRange(state.Catalog
        .OrderBy(e => e.Kind)
        .ThenBy(e => e.Dlc, StringComparer.Ordinal)
        .ThenBy(e => e.Region ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(e => string.Join(';',
            e.Kind,
            e.Dlc,
            e.Region ?? string.Empty,
            e.CountsToward ? "yes" : "no",
            Escape(e.DisplayName),
            Escape(e.Id))));

    File.WriteAllLines(outputPath, lines);

    Console.WriteLine($"{state.Catalog.Count:N0} entries written to {Path.GetFullPath(outputPath)}");
    return 0;

    // Semicolon-separated so names containing commas survive a spreadsheet import.
    static string Escape(string value) => value.Replace(';', ',');
}

/// <summary>
/// Replays a script log against the catalogue and prints where the run stands.
/// </summary>
/// <remarks>
/// The same ingestion path the server uses, without the server: useful to check a log
/// after a session, to confirm a catalogue rebuild produced sane totals, or to see what
/// the tracker would have shown without replaying the session in game.
/// </remarks>
static int Replay(string logPath)
{
    if (!File.Exists(logPath))
    {
        Console.Error.WriteLine($"No such file: {logPath}");
        return 2;
    }

    var state = new TrackerState();
    LoadCatalog(state);

    var ingest = new ReporterIngest();
    ingest.SnapshotReceived += observations => state.Record(EventSource.Snapshot, observations, isSnapshot: true);
    ingest.EventReceived += (id, observed) =>
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>(id, observed)], isSnapshot: false);

    // Where the player is, remembered until something completes and claims it.
    ingest.PlaceReceived += state.NotePlayerPlace;

    foreach (string line in File.ReadLines(logPath))
    {
        ingest.Accept(line);
    }

    Console.WriteLine();
    Console.WriteLine($"Snapshots {ingest.SnapshotCount}, loose events {ingest.EventCount}");

    StateResponse snapshot = state.Snapshot();
    Console.WriteLine($"Catalogue {snapshot.CatalogSize:N0} entries, {snapshot.KnownStates:N0} of them reported");

    foreach (RulesetProgress mode in snapshot.Modes)
    {
        Console.WriteLine();
        Console.WriteLine($"  {mode.RulesetId,-10} {mode.Completed,5} / {mode.Total,-5} {mode.Percent,7:0.00}%");

        foreach (KindProgress kind in mode.ByKind)
        {
            Console.WriteLine($"      {kind.Kind,-18} {kind.Completed,5} / {kind.Total,-5} {kind.Percent,7:0.00}%");
        }
    }

    // Which chest pins this run proved by the hunt that took them, and which were
    // already known. Printed because a link silently forces a pin done, and anything
    // that moves the percentage without being asked should say so somewhere.
    Console.WriteLine();
    Console.WriteLine("Chest pins proven by their treasure hunt:");
    var names = state.Catalog.ToDictionary(e => e.Id, e => e.DisplayName, StringComparer.Ordinal);
    foreach ((string poiId, string questId) in state.ProvenByQuest.OrderBy(l => l.Key, StringComparer.Ordinal))
    {
        string origin = GameData.PoiProvenByQuest.ContainsKey(poiId) ? "curated" : "derived";
        Console.WriteLine($"      {origin,-8} {names.GetValueOrDefault(poiId, poiId),-28} <- "
                          + names.GetValueOrDefault(questId, questId));
    }

    TimelineResponse timeline = state.Timeline();
    Console.WriteLine();
    Console.WriteLine($"Timeline: {timeline.Unlocks.Count:N0} unlock events recorded");

    foreach (IGrouping<TrackedKind, UnlockEvent> group in timeline.Unlocks.GroupBy(u => u.Kind))
    {
        int distinctIds = group.Select(u => u.CatalogId).Distinct(StringComparer.Ordinal).Count();
        Console.WriteLine($"      {group.Key,-18} {group.Count(),5} events, {distinctIds,5} distinct ids");
    }

    return 0;
}

/// <summary>
/// Builds the catalogue from a reference dump and writes it to disk.
/// </summary>
/// <remarks>
/// Run this once against a log captured on a savegame where everything is unlocked. The
/// catalogue is the denominator, so it cannot be derived from an ordinary run: the game
/// only reports quests the player has already encountered.
/// </remarks>
static int BuildCatalog(string[] arguments)
{
    // A trailing .json argument is where to write the result; everything else is a log
    // to merge. Deciding by extension rather than by whether the file exists means
    // rebuilding over an existing catalogue does not read it back in as input.
    bool hasOutput = arguments.Length > 1 && arguments[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    string outputPath = hasOutput ? arguments[^1] : "catalog.json";
    string[] logs = [.. (hasOutput ? arguments[..^1] : arguments).Where(File.Exists)];

    if (logs.Length == 0)
    {
        Console.Error.WriteLine("No readable log files given.");
        return 2;
    }

    CatalogBuilder.Result result = CatalogBuilder.FromScriptLogs(logs.Select(File.ReadLines));

    if (result.Entries.Count == 0)
    {
        Console.Error.WriteLine("No complete dump found in that log. Load a savegame with the reporter installed, then try again.");
        return 1;
    }

    Console.WriteLine($"Logs read       {logs.Length}, dumps merged {result.DumpsFound}");
    Console.WriteLine($"Catalogue size  {result.Entries.Count:N0}");
    Console.WriteLine();

    foreach (IGrouping<TrackedKind, CatalogEntry> byKind in result.Entries.GroupBy(static e => e.Kind).OrderBy(static g => g.Key))
    {
        string packs = string.Join(", ", byKind
            .GroupBy(static e => e.Dlc)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => $"{g.Key} {g.Count()}"));

        Console.WriteLine($"  {byKind.Key,-16} {byKind.Count(),5}   ({packs})");
    }


    File.WriteAllText(outputPath, JsonSerializer.Serialize(result.Entries.ToArray(), typeof(CatalogEntry[]), ApiJsonContext.Default));

    Console.WriteLine();
    Console.WriteLine($"Written to {Path.GetFullPath(outputPath)}");
    return 0;
}

/// <summary>
/// Opens a savegame and reports what the format layer was able to read. This is the
/// command to run when a game update changes the save format: it fails loudly and
/// says where.
/// </summary>
static int ParseSavegame(string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No such file: {path}");
        return 2;
    }

    try
    {
        var stopwatch = Stopwatch.StartNew();

        SaveArchive.SaveImage image = SaveArchive.Open(path);
        SavegameIndex index = SavegameIndex.Read(image);

        stopwatch.Stop();

        Console.WriteLine($"File            {Path.GetFileName(path)}");
        Console.WriteLine($"Compressed      {new FileInfo(path).Length:N0} bytes");
        Console.WriteLine($"Expanded        {image.Payload.Length:N0} bytes in {image.Chunks.Length} chunk(s)");
        Console.WriteLine($"Variable names  {index.VariableNames.Length:N0}");
        Console.WriteLine($"Variables       {index.Variables.Length:N0}");
        Console.WriteLine($"Read in         {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine();

        // A quick confidence check that the name table really was decoded: these are
        // the sections the tracker cares about.
        string[] interesting =
        [
            "CJournalManager", "JActiveEntries", "JS_Success", "JS_Active", "JS_Failed",
            "CGwintManager", "SBSCollectionCard", "CCommonMapManager", "knownMapPins",
        ];

        Console.WriteLine("Sections of interest present in the name table:");
        var names = new HashSet<string>(index.VariableNames, StringComparer.Ordinal);

        foreach (string name in interesting)
        {
            Console.WriteLine($"  {(names.Contains(name) ? "yes" : " no")}  {name}");
        }

        return 0;
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine($"Could not read the savegame: {exception.Message}");
        return 1;
    }
}

/// <summary>
/// Starts the local web server that backs the dashboard and the OBS overlay.
/// </summary>
static async Task ServeAsync(int port)
{
    // CreateSlimBuilder keeps the ahead-of-time compiled binary small: no MVC, no
    // configuration providers that rely on reflection.
    var builder = WebApplication.CreateSlimBuilder();

    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));

    WebApplication app = builder.Build();

    var state = new TrackerState();
    LoadCatalog(state);
    LoadCalibration(state);

    // Resumed before anything is ingested, so the report the game sends on its next load
    // lands on top of a run that already knows what it had done and when.
    PersistedRun? storedRun = RunStore.Load();
    if (storedRun is not null)
    {
        state.Restore(storedRun);
        Console.WriteLine(
            $"Resumed run: {storedRun.Unlocks.Count:N0} completions, "
            + $"{TimeSpan.FromSeconds(storedRun.PlaySeconds):h\\h\\ mm\\m} played, from {RunStore.DefaultPath}");
    }
    else
    {
        // Said out loud, because the silent version of this is what makes an update cost a
        // run's history. The file sits beside the executable, so extracting a new build
        // into a different folder leaves it behind, and nothing afterwards looks wrong: the
        // next report re-asserts every completion, and only the timings are gone.
        state.NoteFreshStart();
        Console.WriteLine($"No stored run at {RunStore.DefaultPath} - starting a new one.");
        Console.WriteLine(
            "  If you have just updated, close this, copy run.json from the folder you were "
            + "running before into this one, and start again. A report says that something "
            + "is done, never when, so a run recovered later carries today's times.");
    }

    SaveRunInBackground(state);

    var ingestCancellation = new CancellationTokenSource();

    // On its own thread, not inline. The reader hands over whatever the log already
    // contains in one synchronous read, so starting the follower here ran the entire
    // backlog - a session's worth of records - before the next line of this method, and
    // the server did not come up until it finished. Nothing about it needs to happen
    // before the port is listening.
    _ = Task.Run(() => FollowReporterAsync(state, ingestCancellation.Token), ingestCancellation.Token);

    // Optional in-game-time tracking reads the game's own process memory, which only
    // Windows can do - everywhere else the option simply is not offered, rather than
    // failing every time someone opens the dashboard.
    GameClock? gameClock = null;
    if (OperatingSystem.IsWindows())
    {
        gameClock = new GameClock();

        // A resumed run resumes its clock too. Seeded before anything can start it, so the
        // first press of the button continues the total rather than restarting it.
        if (storedRun is not null)
        {
            gameClock.Seed(TimeSpan.FromSeconds(storedRun.IgtSeconds));
        }

        // The analyzer cannot see that this closure only ever runs on the platform that
        // set it, since the guard above is on the *assignment*, not on every future call
        // of the delegate it assigns - but nothing reaches this lambda body unless that
        // assignment happened, which only happens inside the guard above.
#pragma warning disable CA1416
        // The total is reported whether or not the clock is attached: pausing keeps it,
        // and a dashboard that blanked the moment someone paused would look like the time
        // had been thrown away. Only the stamping of completions keys on Active.
        state.IgtSource = () => new TrackerState.IgtSample(
            gameClock.IsRunning, gameClock.Elapsed, gameClock.Detail, gameClock.IsLoading);
#pragma warning restore CA1416
    }

    app.MapGet("/api/health", () => new HealthResponse(
        Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        Sources: state.DescribeSources(),
        ServerTime: DateTimeOffset.UtcNow));

    app.MapGet("/api/state", () => state.Snapshot());

    // The whole run, for the progress chart. Separate from the pushed state so that the
    // overlay is not made to carry a history it never draws.
    app.MapGet("/api/timeline", () => state.Timeline());

    // Everything placeable, grouped by the streamed world it belongs to. Separate from
    // the pushed state for the same reason the timeline is: the overlay never draws a
    // map, and this is a thousand points.
    app.MapGet("/api/map", () => state.MapPoints());

    app.MapGet("/api/modes", () => state.Rulesets
        .Where(r => r.Active)
        .OrderBy(r => r.Sort)
        .Select(r => new ModeInfo(r.Id, r.Name, r.Label, [.. r.Scope.Order()]))
        .ToArray());

    // Chooses which of the four modes the dashboard and overlay show. The event log is
    // the same underneath regardless, so this is just a display selection.
    app.MapPost("/api/mode", (ModeSelection selection) =>
    {
        try
        {
            state.SetActiveMode(selection.Id);
            return Results.Ok(state.Snapshot());
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    });

    // Clears the run back to zero. Meant to be pressed once, before loading the savegame
    // a run starts from - the next full snapshot the reporter sends becomes the new
    // baseline, the same way it does when the tracker starts up for the first time.
#pragma warning disable CA1416 // gameClock is only ever non-null on Windows; see its assignment above.
    app.MapPost("/api/reset", () =>
    {
        TimeSpan discarded = gameClock?.Elapsed ?? TimeSpan.Zero;
        state.Reset();

        // The in-game clock is part of the run, so it goes back to zero with it. This is
        // the only thing that zeroes it: stopping the clock is a pause, and a new run is
        // the one moment where throwing the accumulated time away is what was meant.
        gameClock?.Reset();

        // Noted after the reset cleared the previous run's record, so this is the first
        // line of the new one and says what the old one had reached.
        if (discarded > TimeSpan.Zero)
        {
            state.NoteIgtControl(IgtControl.Reset, discarded);
        }

        return Results.Ok(state.Snapshot());
    });
#pragma warning restore CA1416

    // Works around a base-game quirk, not a WitcherTrack bug: HasCardInCollection()
    // sometimes reports the Geralt and Ciri hero cards as missing even when the player
    // owns them, confirmed against other trackers hitting the same CD Projekt Red issue.
    // One press marks both Done through the same manual-override mechanism a player
    // correction uses, so it can be undone the normal way if a save later reads correctly.
    app.MapPost("/api/fix-gwent", () =>
    {
        const string reason = "Known engine quirk: HasCardInCollection() misreports Geralt/Ciri";
        state.SetOverride("gwent:geralt", CompletionState.Done, reason);
        state.SetOverride("gwent:ciri", CompletionState.Done, reason);
        return Results.Ok(state.Snapshot());
    });

    // Attaches the optional in-game-time clock and carries on from whatever total it
    // already holds, so stopping it is a pause rather than a discard. A 501 on a
    // non-Windows host is a real answer, not a missing route: the option cannot
    // work there, so the interface should say so rather than retry.
#pragma warning disable CA1416 // gameClock is only ever non-null on Windows; see its assignment above.
    app.MapPost("/api/igt/start", () =>
    {
        if (gameClock is null)
        {
            return Results.Json(
                new IgtStartResponse(false, "In-game-time tracking needs Windows to read the game's memory."),
                ApiJsonContext.Default.IgtStartResponse,
                statusCode: StatusCodes.Status501NotImplemented);
        }

        bool started = gameClock.Start();

        // Only an attachment that actually happened is an act on the clock. A press that
        // could not find the game changed nothing and does not belong in the record.
        if (started)
        {
            state.NoteIgtControl(IgtControl.Started, gameClock.Elapsed);
        }

        // Nothing was recorded in the event log, so nothing would otherwise be pushed, and
        // every open dashboard would keep showing the clock as it was before this call.
        state.PublishNow();
        return Results.Json(new IgtStartResponse(started, gameClock.Detail), ApiJsonContext.Default.IgtStartResponse);
    });

    // The clock's own reading, cheap enough to ask for once a second. The dashboard shows
    // a moving counter, and the state stream cannot feed one: it is published when a
    // record arrives, and during a loading screen - the one moment the counter must not
    // move - no records arrive at all. So the display asks rather than guesses.
    app.MapGet("/api/igt", () => Results.Json(
        gameClock is null
            ? new IgtStatusResponse(false, null, 0, "In-game-time tracking needs Windows to read the game's memory.")
            : new IgtStatusResponse(
                gameClock.IsRunning, gameClock.IsLoading, gameClock.Elapsed.TotalSeconds, gameClock.Detail),
        ApiJsonContext.Default.IgtStatusResponse));

    // Detaches the clock. Whatever it accumulated stays on every unlock already recorded
    // with it - only the running total for entries still to come stops advancing.
    app.MapPost("/api/igt/stop", () =>
    {
        bool wasRunning = gameClock?.IsRunning ?? false;
        gameClock?.Stop();

        if (wasRunning && gameClock is not null)
        {
            state.NoteIgtControl(IgtControl.Paused, gameClock.Elapsed);
        }

        state.PublishNow();
        return Results.Ok(state.Snapshot());
    });
#pragma warning restore CA1416

    // Server-sent events: the overlay subscribes once and is pushed every change,
    // instead of polling a text file the way the previous version did.
    app.MapGet("/api/events", async (HttpContext context, CancellationToken cancellationToken) =>
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        await foreach (string payload in state.SubscribeAsync(cancellationToken))
        {
            await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    });

    // The web assets are embedded in the executable, so the release stays a single file.
    app.MapGet("/", static () => ServeEmbedded("index.html"));
    app.MapGet("/overlay", static () => ServeEmbedded("overlay.html"));
    app.MapGet("/map", static () => ServeEmbedded("map.html"));

    // The region backgrounds: from the data folder when one is there, otherwise from
    // inside the executable. A folder wins so that replacing the artwork stays a matter of
    // dropping a file in, and the embedded copy is what makes a bare WitcherTrack.exe draw
    // a real map instead of points on nothing.
    app.MapGet("/map/bg/{file}", (string file) =>
    {
        // Only ever a bare .webp name from our own index, never a path. The extension is
        // part of the check rather than decoration: EmbeddedAssets.Open matches on a suffix, so an
        // unfiltered name could reach any other resource in the binary.
        if (!file.EndsWith(".webp", StringComparison.Ordinal)
            || file.Contains('/') || file.Contains('\\') || file.Contains(".."))
        {
            return Results.NotFound();
        }

        if (state.MapImageFolder is not null)
        {
            string full = Path.Combine(state.MapImageFolder, file);

            if (File.Exists(full))
            {
                return Results.File(full, "image/webp");
            }
        }

        return ServeEmbedded(file);
    });

    // Shared by both pages, so the map artwork is defined and paid for once.
    app.MapGet("/icons.js", static () => ServeEmbedded("icons.js"));

    Console.WriteLine($"WitcherTrack is running on http://127.0.0.1:{port}");
    Console.WriteLine("  dashboard  http://127.0.0.1:{0}/", port);
    Console.WriteLine("  overlay    http://127.0.0.1:{0}/overlay   (add as a Browser source in OBS)", port);
    Console.WriteLine();
    Console.WriteLine("Press Ctrl+C to stop.");

    OpenBrowser($"http://127.0.0.1:{port}/");

    await app.RunAsync();
    await ingestCancellation.CancelAsync();
}

/// <summary>
/// Loads the catalogue, which supplies the totals every counter is measured against.
/// </summary>
/// <remarks>
/// Looked for next to the executable first, then in a <c>data</c> folder beside it, so a
/// release can ship the file alongside the binary and a checkout can keep it in the repo.
/// Without it the tracker still runs and still records everything; it just has nothing to
/// count against, which the interface shows rather than hides.
/// </remarks>
/// <summary>
/// Loads the fitted world-to-map transforms, if they are on file.
/// </summary>
/// <remarks>
/// Entirely optional: without them the map view still draws every point, just in raw
/// world axes rather than oriented the way the game's own map is. Nothing else in the
/// tracker reads them.
/// </remarks>
static void LoadCalibration(TrackerState state)
{
    string? path = new[]
        {
            Path.Combine("data", "map", "calibration.json"),
            Path.Combine(AppContext.BaseDirectory, "map", "calibration.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "map", "calibration.json"),
        }
        .FirstOrDefault(File.Exists);

    // The same file-first, executable-second rule the catalogue follows.
    string? folder = path is null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
    string? calibration = path is null ? EmbeddedAssets.ReadText("calibration.json") : File.ReadAllText(path);

    if (calibration is null)
    {
        return;
    }

    try
    {
        Dictionary<string, MapCalibration>? fits = JsonSerializer.Deserialize(
            calibration, ApiJsonContext.Default.DictionaryStringMapCalibration);

        if (fits is null)
        {
            return;
        }

        foreach (MapCalibration fit in fits.Values)
        {
            // Keyed by the world file, because that is what a point carries; the region
            // names the fit is filed under are the community map's, not the game's.
            state.Calibration[fit.World] = fit;
        }

        Console.WriteLine($"Map calibration: {fits.Count} regions from {folder ?? "the executable"}");

        // Backgrounds sit beside the calibration and are keyed the same way, so they are
        // matched back to a world through the fit that shares their region name.
        string? beside = folder is null ? null : Path.Combine(folder, "backgrounds.json");
        string? backgrounds = beside is not null && File.Exists(beside)
            ? File.ReadAllText(beside)
            : EmbeddedAssets.ReadText("backgrounds.json");

        if (backgrounds is not null)
        {
            Dictionary<string, MapBackground>? images = JsonSerializer.Deserialize(
                backgrounds, ApiJsonContext.Default.DictionaryStringMapBackground);

            foreach ((string region, MapBackground image) in images ?? [])
            {
                if (fits.TryGetValue(region, out MapCalibration? fit))
                {
                    state.Backgrounds[fit.World] = image;
                }
            }

            // Null when there is no data folder at all: the pictures then come from the
            // executable, which is what the background route falls back to.
            state.MapImageFolder = folder;
            Console.WriteLine($"Map backgrounds: {state.Backgrounds.Count} regions");
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Ignoring calibration.json: {exception.Message}");
    }
}

static void LoadCatalog(TrackerState state)
{
    string? path = new[]
        {
            "catalog.json",
            Path.Combine("data", "catalog.json"),
            Path.Combine(AppContext.BaseDirectory, "catalog.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "catalog.json"),
        }
        .FirstOrDefault(File.Exists);

    // A copy travels inside the executable, so a bare WitcherTrack.exe is a whole
    // tracker. The file on disk still wins: rebuilding the catalogue after a game update
    // means dropping a new catalog.json here, and that has to keep working.
    string source = path is null ? "the executable" : Path.GetFullPath(path);
    string? json = path is null ? EmbeddedAssets.ReadText("catalog.json") : File.ReadAllText(path);

    if (json is null)
    {
        Console.WriteLine("No catalog.json found - progress will be recorded but totals will be zero.");
        Console.WriteLine("Build one with:  WitcherTrack catalog <scriptslog.txt>");
        return;
    }

    try
    {
        CatalogEntry[]? entries = JsonSerializer.Deserialize(json, ApiJsonContext.Default.CatalogEntryArray);

        if (entries is { Length: > 0 })
        {
            state.Catalog.AddRange(entries);
            Console.WriteLine($"Catalogue: {entries.Length:N0} entries from {source}");
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Could not read the catalogue from {source}: {exception.Message}");
    }
}

/// <summary>
/// Follows the game's script log and feeds whatever the in-game reporter writes into the
/// run.
/// </summary>
/// <remarks>
/// This runs for the lifetime of the process and tolerates the game not being open: the
/// reader waits for the log to appear, and notices when the game restarts and recreates
/// it. Nothing here can fail in a way that stops the web server.
/// </remarks>
static async Task FollowReporterAsync(TrackerState state, CancellationToken cancellationToken)
{
    var ingest = new ReporterIngest();

    // A dump describes the whole world, so it replaces what came before. Individual
    // records are additive.
    ingest.SnapshotReceived += observations =>
        state.Record(EventSource.Snapshot, observations, isSnapshot: true);

    ingest.EventReceived += (id, observed) =>
        state.Record(EventSource.GameEvent, [new KeyValuePair<string, CompletionState>(id, observed)], isSnapshot: false);

    // Where the player is, remembered until something completes and claims it.
    ingest.PlaceReceived += state.NotePlayerPlace;

    var reader = new ScriptLogReader(ScriptLogReader.DefaultPath);
    reader.LogRestarted += ingest.Reset;

    // Play time is only counted once the reader has caught up with what the log already
    // held. That backlog is delivered in one burst - a whole session's worth of lines in
    // a second or two - and counting it would add seconds of "play" that already happened
    // and were already counted when they happened.
    bool live = false;
    reader.CaughtUp += () => live = true;

    try
    {
        await foreach (string line in reader.ReadLinesAsync(fromStart: true, cancellationToken))
        {
            state.MarkSourceSeen("game");

            // Every line, not just the reporter's: the game logs constantly while it is
            // running, and that is the only evidence there is that it is being played.
            if (live)
            {
                state.NoteActivity();
            }

            ingest.Accept(line);
        }
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }
}

/// <summary>
/// Writes the run to disk shortly after it changes, and no more often than that.
/// </summary>
/// <remarks>
/// Coalesced for the same reason the state payload is: a burst of records is one run, and
/// serialising it once per record would write the file thousands of times for a single
/// map-pin sweep. The delay is longer than the overlay's, because nothing is watching this
/// and a second of lag costs nothing.
/// </remarks>
static void SaveRunInBackground(TrackerState state)
{
    var pending = new SemaphoreSlim(1, 1);

    state.RunChanged += () =>
    {
        // A save is already scheduled: it will pick up this change too, because it
        // captures the run when it runs rather than when it was scheduled.
        if (!pending.Wait(0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                RunStore.Save(state.Capture());
            }
            finally
            {
                pending.Release();
            }
        });
    };
}

/// <summary>Returns an embedded web asset, or 404 when it is missing.</summary>
static IResult ServeEmbedded(string name)
{
    Stream? stream = EmbeddedAssets.Open(name);

    if (stream is null)
    {
        return Results.NotFound();
    }

    string contentType = Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    return Results.Stream(stream, contentType);
}

/// <summary>
/// Opens the dashboard in the default browser. Best effort: a headless or restricted
/// environment simply keeps the server running and prints the address.
/// </summary>
static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception)
    {
        // Not being able to open a browser is never a reason to fail to start.
    }
}

/// <summary>Reported by <c>/api/health</c> so the interface can show whether ingestion is alive.</summary>
internal sealed record HealthResponse(string Version, IReadOnlyList<SourceStatus> Sources, DateTimeOffset ServerTime);

/// <summary>A completion mode as exposed by <c>/api/modes</c>.</summary>
internal sealed record ModeInfo(string Id, string Name, string Label, string[] Scope);

/// <summary>Liveness of one ingestion source.</summary>
internal sealed record SourceStatus(string Name, bool Connected, DateTimeOffset? LastSeen, string? Detail);

/// <summary>The payload behind <c>/api/state</c>.</summary>
/// <param name="ActiveModeId">
/// The mode the player selected, or null before the dashboard's mode-selection screen has
/// been used. Every field in <paramref name="Modes"/> is still computed regardless, so
/// switching later needs no recomputation.
/// </param>
/// <param name="RecentUnlocks">
/// Newly completed entries, most recent first, real wall-clock time rather than the
/// game's fictional calendar - what a speedrun overlay needs is elapsed run time, and the
/// server already timestamps every observation the moment it arrives.
/// </param>
/// <param name="UnlockCount">
/// How many completions the full history holds. The chart refetches
/// <c>/api/timeline</c> when this changes, so the pushed payload stays small without the
/// chart going stale.
/// </param>
/// <param name="IgtActive">
/// Whether the optional in-game-time clock is currently attached and accumulating. False
/// does not necessarily mean it was never used this run - it also covers "not started
/// yet" and "lost the game process" - <paramref name="IgtDetail"/> is what tells those
/// apart in the interface.
/// </param>
/// <param name="IgtDetail">
/// Which game build the clock attached to, or why it is not running, meant to be shown
/// next to the option rather than left to fail silently.
/// </param>
/// <param name="IgtElapsedSeconds">
/// The clock's running total right now, independent of whether anything has been
/// completed yet and of whether it is currently attached - pausing keeps the total, so
/// the toolbar goes on showing where the run stands.
/// </param>
/// <param name="IgtLoading">
/// Whether the game is on a loading screen, or null when the clock is not attached.
/// </param>
/// <param name="StartedFresh">
/// True when the tracker started with no stored run and nobody asked for a new one, which
/// is what an update extracted into a different folder looks like.
/// </param>
internal sealed record StateResponse(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RulesetProgress> Modes,
    int CatalogSize,
    int KnownStates,
    string? ActiveModeId,
    IReadOnlyList<UnlockEvent> RecentUnlocks,
    DateTimeOffset? RunStartedAt,
    int UnlockCount,
    bool IgtActive = false,
    string? IgtDetail = null,
    double? IgtElapsedSeconds = null,
    bool? IgtLoading = null,
    bool StartedFresh = false);

/// <summary>The payload behind <c>/api/timeline</c>: the whole run, oldest first.</summary>
/// <param name="IgtControls">
/// Every manual start, pause and reset of the in-game clock, oldest first. Empty for a run
/// that never used it.
/// </param>
internal sealed record TimelineResponse(
    DateTimeOffset? RunStartedAt,
    IReadOnlyList<UnlockEvent> Unlocks,
    IReadOnlyList<IgtControlEvent> IgtControls);

/// <summary>What was done to the in-game clock.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IgtControl>))]
internal enum IgtControl
{
    /// <summary>Attached and accumulating, whether for the first time or after a pause.</summary>
    Started,

    /// <summary>Detached by hand. The total is kept.</summary>
    Paused,

    /// <summary>Put back to zero, which only resetting the run does.</summary>
    Reset,
}

/// <summary>
/// One deliberate act on the in-game clock, with the real time it happened at.
/// </summary>
/// <remarks>
/// <para>
/// Speedrun rules require a single unbroken session, so a timer that can be paused is only
/// as trustworthy as its record of having been paused. This is that record: wall-clock
/// instants, kept alongside the run and printed to the console as they happen, so the gap
/// between a pause and the start that follows it is a stated fact rather than something
/// missing from the evidence.
/// </para>
/// <para>
/// Recorded at the endpoint, so only a deliberate press lands here. The clock losing sight
/// of the game is not an entry: it accumulates nothing while it cannot read the flag, and
/// it never detaches on its own.
/// </para>
/// </remarks>
/// <param name="At">
/// When it happened, in UTC, the same clock every other timestamp in the run uses so the
/// two can be lined up.
/// </param>
/// <param name="Action">What was done.</param>
/// <param name="ElapsedSeconds">The clock's total at that moment.</param>
internal sealed record IgtControlEvent(DateTimeOffset At, IgtControl Action, double ElapsedSeconds);

/// <summary>One newly completed catalogue entry, for the timeline and the overlay feed.</summary>
/// <param name="Region">
/// For a point of interest, the game's own pin type (for example <c>BossAndTreasure</c>).
/// Kept raw because it is what the rules key on. Null for every other kind.
/// </param>
/// <param name="RegionLabel">
/// The same thing as a player would name it ("Guarded Treasure"), which is what the
/// interface shows: the game supplies no name of its own for the points a run must clear,
/// so the category is the label. Resolved here rather than in the browser so the mapping
/// lives with the rest of the curated game data, in one reviewable place.
/// </param>
/// <param name="IgtElapsedSeconds">
/// In-game time - real time with loading screens subtracted - elapsed since the clock was
/// started, in seconds. Null unless that optional tracking was running at the moment this
/// entry was recorded: it cannot be reconstructed afterward, so an entry recorded before
/// tracking started simply has none.
/// </param>
internal sealed record UnlockEvent(
    string CatalogId,
    TrackedKind Kind,
    string DisplayName,
    string Dlc,
    string? Region,
    string? RegionLabel,
    DateTimeOffset Timestamp,
    double? IgtElapsedSeconds = null,
    double? PlayElapsedSeconds = null);

/// <summary>The body of <c>POST /api/mode</c>.</summary>
internal sealed record ModeSelection(string Id);

/// <summary>The payload behind <c>/api/map</c>: everything placeable, by streamed world.</summary>
internal sealed record MapResponse(IReadOnlyList<MapRegion> Regions);

/// <summary>
/// How one region's world coordinates relate to the community map's own, as fitted by
/// <c>tools/fit_map_calibration.py</c> and stored in <c>data/map/calibration.json</c>.
/// </summary>
/// <remarks>
/// Only the two fields needed to place a point are read; the file also carries the fit's
/// diagnostics and control points, which are there to be audited rather than consumed.
/// Without this the map still draws - the points are self-consistent on their own - but
/// it draws in raw world axes, which are rotated and mirrored with respect to every map
/// of the game anyone has ever seen. Applying it is what makes a region recognisable,
/// and it is the same transform the tiles will need when they are added.
/// </remarks>
internal sealed record MapCalibration(string World, string Projection, double[][] Matrix);

/// <summary>
/// One region's background picture, as produced by <c>tools/build_map_backgrounds.py</c>.
/// </summary>
/// <param name="PixelMatrix">
/// World coordinates straight to a pixel in <paramref name="Image"/>, applied as
/// <c>[px, py] = [x, y, 1] * pixelMatrix</c>. The fit, the projection, the crop and the
/// downscale are all folded into it, so nothing downstream has to know about tile grids
/// or zoom levels.
/// </param>
internal sealed record MapBackground(string Image, int Width, int Height, double[][] PixelMatrix);

/// <summary>
/// One streamed world's points.
/// </summary>
/// <param name="World">
/// The world file the game reported these from, for example
/// <c>levels\novigrad\novigrad.w2w</c>. This is the grouping key rather than a
/// prettier region name because coordinates are only comparable within one of these -
/// the interface is what turns it into "Velen".
/// </param>
/// <param name="Projection">
/// How to read <paramref name="Matrix"/>'s output, or null when this world has no fit.
/// </param>
/// <param name="Matrix">
/// The world-to-map transform for this region, or null when none was fitted. Applied as
/// <c>[north, east] = [x, y, 1] * matrix</c>.
/// </param>
/// <param name="Background">
/// The region's map picture, when one has been built. Null is the ordinary case until
/// someone downloads the tiles and runs the tool over them, and the map view draws the
/// points on a plain surface instead.
/// </param>
internal sealed record MapRegion(
    string World,
    IReadOnlyList<MapPoint> Points,
    string? Projection = null,
    double[][]? Matrix = null,
    MapBackground? Background = null);

/// <summary>One point of interest, where it is and whether it is cleared.</summary>
/// <param name="PinType">The game's own pin type, for example <c>BossAndTreasure</c>.</param>
/// <param name="PinLabel">That type as a player would name it, for example "Guarded Treasure".</param>
/// <param name="Counts">
/// Whether this point contributes to a completion percentage. Signposts, workbenches and
/// the like do not; they are still worth drawing, because a map showing only the hundred
/// things left to do is harder to place yourself on than one that also shows the roads.
/// </param>
/// <param name="Kind">
/// Which sort of thing this is - a point of interest, a quest, a diagram and so on. Only
/// points of interest are placed by the game; the rest are placed by where the player was
/// standing when they were finished, and are on the map only once that has happened.
/// </param>
internal sealed record MapPoint(
    string Id,
    string DisplayName,
    string Dlc,
    string? PinType,
    string? PinLabel,
    bool Counts,
    double X,
    double Y,
    bool Done,
    string Kind = "PointOfInterest");

/// <summary>The payload behind <c>GET /api/igt</c>.</summary>
/// <param name="Active">Whether the clock is attached and accumulating.</param>
/// <param name="Loading">Whether the game is on a loading screen, null when not attached.</param>
/// <param name="ElapsedSeconds">The running total, kept across a pause.</param>
/// <param name="Detail">The game build it attached to, or why it is not running.</param>
internal sealed record IgtStatusResponse(bool Active, bool? Loading, double ElapsedSeconds, string? Detail);

/// <summary>The payload behind <c>POST /api/igt/start</c>.</summary>
/// <param name="Started">
/// True if a supported game build was found and the clock is now attached and
/// accumulating. False leaves in-game-time tracking off for the rest of the run - the
/// player can try again once the condition <paramref name="Detail"/> describes is fixed
/// (for example, launching the game).
/// </param>
/// <param name="Detail">Which build was detected, or why attaching failed.</param>
internal sealed record IgtStartResponse(bool Started, string? Detail);

/// <summary>
/// Source-generated JSON, which is what allows the binary to be compiled ahead of time:
/// no runtime reflection over the response types.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StateResponse))]
[JsonSerializable(typeof(ModeInfo[]))]
[JsonSerializable(typeof(RulesetProgress))]
[JsonSerializable(typeof(SourceStatus[]))]
[JsonSerializable(typeof(CatalogEntry[]))]
[JsonSerializable(typeof(UnlockEvent[]))]
[JsonSerializable(typeof(ModeSelection))]
[JsonSerializable(typeof(TimelineResponse))]
[JsonSerializable(typeof(IgtStartResponse))]
[JsonSerializable(typeof(IgtStatusResponse))]
[JsonSerializable(typeof(IgtControlEvent))]
[JsonSerializable(typeof(IReadOnlyList<IgtControlEvent>))]
[JsonSerializable(typeof(MapResponse))]
[JsonSerializable(typeof(PersistedRun))]
[JsonSerializable(typeof(Dictionary<string, MapCalibration>))]
[JsonSerializable(typeof(Dictionary<string, MapBackground>))]
internal sealed partial class ApiJsonContext : JsonSerializerContext
{
}
