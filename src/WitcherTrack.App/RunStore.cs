using System.Text.Json;
using WitcherTrack.Core.Ingest;
using WitcherTrack.Core.Model;

namespace WitcherTrack.App;

/// <summary>
/// A run written to disk, so it survives the tracker being closed and the game being
/// restarted.
/// </summary>
/// <remarks>
/// <para>
/// Without this the run only ever exists for as long as the process does, and it is
/// rebuilt each time from the game's script log - which the game itself recreates on every
/// launch. Yesterday's completions are therefore not in today's log at all: what puts them
/// back is the report the reporter sends on load, which says <em>that</em> they are done
/// and nothing about <em>when</em>. Everything then carries today's timestamp, and the
/// progress chart collapses a week of play into the first few seconds.
/// </para>
/// <para>
/// So the tracker keeps its own record. The script log stays the source of truth for what
/// is done; this is the source of truth for when it happened.
/// </para>
/// </remarks>
/// <param name="Version">
/// Bumped when the shape changes. A file from a newer version is ignored rather than
/// half-read: starting a run over is a smaller loss than resuming a misread one.
/// </param>
/// <param name="PlaySeconds">
/// Time the game was demonstrably being played, accumulated across every session. See
/// <see cref="TrackerState.NoteActivity"/> for what counts.
/// </param>
/// <param name="States">
/// What the run had completed when it was last saved, so the dashboard is correct the
/// moment it opens rather than blank until the game next writes a report.
/// </param>
/// <param name="IgtSeconds">
/// In-game time accumulated so far, if the optional clock was ever started. Stored for
/// the same reason <paramref name="PlaySeconds"/> is: the clock can only accumulate going
/// forward, so a total lost on shutdown can never be recovered - and a run that spans
/// several evenings would restart it from zero every night. Absent from a file written
/// before this existed, where it reads as zero, which is exactly right for a run that
/// never used the clock.
/// </param>
internal sealed record PersistedRun(
    int Version,
    DateTimeOffset? RunStartedAt,
    double PlaySeconds,
    string? ActiveModeId,
    IReadOnlyList<UnlockEvent> Unlocks,
    IReadOnlyDictionary<string, CompletionState> States,
    IReadOnlyDictionary<string, PlayerPlace> FinishedAt,
    IReadOnlyList<string> CompletionOrder,
    IReadOnlyList<ManualOverride> Overrides,
    double IgtSeconds = 0);

/// <summary>Reads and writes the run file.</summary>
internal static class RunStore
{
    /// <summary>
    /// The newest shape this build writes. Older files still load: every field added since
    /// has a default that means "this run never had one", so a run in progress survives an
    /// upgrade instead of being thrown away by it.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Where the run lives: beside the executable, like the catalogue and the map data.
    /// </summary>
    /// <remarks>
    /// The tracker is a portable folder someone extracts wherever they like, so its state
    /// belongs in that folder rather than in a profile directory the player will never
    /// find when they want to back it up or move it to another machine.
    /// </remarks>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "run.json");

    /// <summary>
    /// Loads the stored run, or null when there is none, it cannot be read, or it was
    /// written by a newer build.
    /// </summary>
    /// <remarks>
    /// Never throws. A corrupt or unreadable run file must not stop the tracker from
    /// starting - the run is a convenience, and the alternative to resuming it is starting
    /// fresh, which is exactly what the player would do anyway.
    /// </remarks>
    public static PersistedRun? Load(string? path = null)
    {
        path ??= DefaultPath;

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            PersistedRun? run = JsonSerializer.Deserialize(
                File.ReadAllText(path), ApiJsonContext.Default.PersistedRun);

            if (run is null || run.Version > CurrentVersion)
            {
                Console.WriteLine(run is null
                    ? $"Ignoring {path}: it is empty."
                    : $"Ignoring {path}: it was written by a newer version ({run.Version}).");
                return null;
            }

            return run;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not read {path}: {exception.Message}");
            Console.WriteLine("Starting a fresh run. The old file is left alone.");
            return null;
        }
    }

    /// <summary>Writes the run, replacing whatever was there.</summary>
    /// <remarks>
    /// Written to a temporary file and moved into place, so a crash or a power cut during
    /// the write cannot leave a half-written run where a complete one used to be.
    /// </remarks>
    public static void Save(PersistedRun run, string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            string temporary = path + ".writing";
            File.WriteAllText(temporary, JsonSerializer.Serialize(run, ApiJsonContext.Default.PersistedRun));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not save the run to {path}: {exception.Message}");
        }
    }

    /// <summary>Removes the stored run, for when a new one is started.</summary>
    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Could not remove {path}: {exception.Message}");
        }
    }
}
