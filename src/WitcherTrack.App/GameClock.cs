using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WitcherTrack.App;

/// <summary>
/// Reads the game's own "not loading" flag directly out of its process memory - the same
/// signal the community's LiveSplit "Load Remover" autosplitter uses - to accumulate
/// in-game time: real time with loading screens subtracted out.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately different mechanism from everything else in the app. Nothing
/// else here reads the game's memory: the mod's script log is enough for what the
/// dashboard counts. Elapsed <em>real</em> run time is what the "Progress over time" chart
/// uses by default and always will, because it needs no cooperation from the player. This
/// is additive and opt-in, because in-game time can only be accumulated going forward from
/// the moment tracking starts - there is no way to recover how much of the time already
/// recorded was spent loading, so it cannot be retrofitted onto a run already in progress.
/// </para>
/// <para>
/// The offsets below are copied from the community autosplitter's ASL script, keyed by the
/// same five build identifiers it uses, resolved the same way it resolves them: the
/// executable's own file version, plus - for the one file version shared by both Complete
/// Edition builds - the name of the folder it runs from, to tell DirectX 11 and DirectX 12
/// apart. A build this does not recognise is reported as such rather than guessed at: a
/// wrong offset would read whatever else happens to sit at that address and say nothing is
/// wrong.
/// </para>
/// <para>
/// Reading another process's memory is a Windows-only operation with no equivalent
/// elsewhere, so this type - and only this type - is marked accordingly. Every other
/// source works on any platform .NET runs on.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class GameClock : IDisposable
{
    private const string ProcessName = "witcher3";
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    /// <summary>
    /// The "not loading" flag's byte offset from the main module's base address, per build.
    /// Straight from the autosplitter's <c>state(...)</c> blocks.
    /// </summary>
    private static readonly Dictionary<string, nint> OffsetsByBuild = new(StringComparer.Ordinal)
    {
        ["standard"] = 0x02CCB638,
        ["gog_goty"] = 0x02BF3608,
        ["old_patch"] = 0x02A0BA98,
        ["complete_edition_dx11"] = 0x056F17C0,
        ["complete_edition_dx12"] = 0x054A5F14,
    };

    private readonly Lock _gate = new();
    private readonly TimeSpan _pollInterval;
    private readonly IgtAccumulator _accumulator = new();

    private CancellationTokenSource? _loop;
    private IntPtr _processHandle;
    private nint _flagAddress;
    private string? _build;
    private string? _failure;

    public GameClock(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>Whether the clock is currently attached to a running game and accumulating.</summary>
    public bool IsRunning
    {
        get { lock (_gate) { return _loop is not null; } }
    }

    /// <summary>
    /// Which build was detected, or why attachment failed - shown in the interface so
    /// anyone can tell whether the option actually did anything.
    /// </summary>
    /// <remarks>
    /// The build name survives for as long as the clock stays attached. It used to be
    /// cleared by the first successful read, which left the interface with nothing to show
    /// exactly when it was working - and the build name is the one thing worth checking
    /// when the accumulated time looks wrong, because a wrong build means a wrong offset
    /// and a flag that never changes.
    /// </remarks>
    public string? Detail
    {
        get { lock (_gate) { return _failure ?? _build; } }
    }

    /// <summary>Total in-game time accumulated, across every start and pause.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) { return _accumulator.Elapsed; } }
    }

    /// <summary>
    /// Whether the game is on a loading screen right now, or null when the clock is not
    /// attached or has not read the flag yet.
    /// </summary>
    /// <remarks>
    /// Published so the interface can stop advancing its own display while a load is on.
    /// Without it a dashboard has no way to tell a paused clock from a slow update, and
    /// the only thing it can do between readings is guess - which is what made the
    /// displayed time run straight through loading screens that this had already excluded.
    /// </remarks>
    public bool? IsLoading
    {
        get { lock (_gate) { return _loop is null ? null : _accumulator.Loading; } }
    }

    /// <summary>
    /// Attaches to the running game and accumulates onto whatever total is already there.
    /// </summary>
    /// <remarks>
    /// Starting does <em>not</em> zero the total. Stopping is a pause - the run continues
    /// tomorrow, the game gets closed for the night, the game crashes and is reopened - and
    /// a start that silently threw the accumulated hours away would make pausing a trap
    /// rather than a feature. Zeroing is <see cref="Reset"/>, which is reached by resetting
    /// the run itself, the one action that already means "this is a new run".
    /// </remarks>
    /// <returns>
    /// True if a supported build was found and attached to. False leaves the clock stopped
    /// and <see cref="Detail"/> explains why - the game is not running, or its build is not
    /// one of the five the offsets above cover.
    /// </returns>
    public bool Start()
    {
        Stop();

        if (!TryAttach(out string detail))
        {
            lock (_gate)
            {
                _build = null;
                _failure = detail;
            }

            return false;
        }

        var cancellation = new CancellationTokenSource();

        lock (_gate)
        {
            _build = detail;
            _failure = null;

            // The last loading flag read belongs to a previous attachment and says nothing
            // about the game running now.
            _accumulator.Forget();
            _loop = cancellation;
        }

        _ = PollAsync(cancellation.Token);
        return true;
    }

    /// <summary>
    /// Seeds the total, for a run resumed from disk. Call before <see cref="Start"/>.
    /// </summary>
    public void Seed(TimeSpan total)
    {
        lock (_gate)
        {
            _accumulator.Seed(total);
        }
    }

    /// <summary>Throws the accumulated total away. Belongs to starting a new run.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _accumulator.Seed(TimeSpan.Zero);
        }
    }

    /// <summary>Detaches and pauses accumulating. <see cref="Elapsed"/> keeps its value.</summary>
    public void Stop()
    {
        CancellationTokenSource? loop;
        IntPtr handle;

        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            handle = _processHandle;
            _processHandle = IntPtr.Zero;
        }

        loop?.Cancel();
        loop?.Dispose();

        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }

        lock (_gate)
        {
            // Nothing is loading once nothing is attached, and leaving the last reading in
            // place would freeze a dashboard's display on it.
            _accumulator.Forget();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Finds the game process, works out which build it is, and opens a handle to read its
    /// memory. Kept separate from the polling loop so a lost process can be retried without
    /// tearing down and rebuilding the whole loop.
    /// </summary>
    private bool TryAttach(out string detail)
    {
        Process[] candidates = Process.GetProcessesByName(ProcessName);
        if (candidates.Length == 0)
        {
            detail = "witcher3.exe is not running";
            return false;
        }

        Process process = candidates[0];
        ProcessModule? mainModule;

        try
        {
            mainModule = process.MainModule;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            detail = $"could not read the game's process ({ex.Message})";
            return false;
        }

        if (mainModule is null)
        {
            detail = "could not read the game's main module";
            return false;
        }

        string? build = DetectBuild(mainModule.FileVersionInfo, mainModule.FileName);
        if (build is null)
        {
            detail = "unrecognised game build (file version " +
                      $"{mainModule.FileVersionInfo.FileMajorPart}." +
                      $"{mainModule.FileVersionInfo.FileMinorPart}." +
                      $"{mainModule.FileVersionInfo.FileBuildPart}." +
                      $"{mainModule.FileVersionInfo.FilePrivatePart}) - " +
                      "not one of the five the offsets are known for";
            return false;
        }

        IntPtr handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            detail = "could not open the game process to read its memory";
            return false;
        }

        lock (_gate)
        {
            _processHandle = handle;
            _flagAddress = mainModule.BaseAddress + OffsetsByBuild[build];
        }

        detail = build;
        return true;
    }

    /// <summary>
    /// Identifies the build the same way the autosplitter's <c>init</c> block does: by the
    /// executable's own file version, and - for the one version both Complete Edition
    /// builds share - by the folder it runs from.
    /// </summary>
    /// <remarks>
    /// A thin wrapper around <see cref="GameBuildDetector.Detect"/>, which holds the actual
    /// matching logic. <see cref="FileVersionInfo"/> has no public constructor, so the
    /// version-string comparison lives in a plain, unattributed type the self-test can call
    /// on any platform, checked directly against the autosplitter's own version table
    /// without a running game.
    /// </remarks>
    internal static string? DetectBuild(FileVersionInfo fileVersionInfo, string executablePath)
    {
        string fileVersion = string.Join('.',
            fileVersionInfo.FileMajorPart, fileVersionInfo.FileMinorPart,
            fileVersionInfo.FileBuildPart, fileVersionInfo.FilePrivatePart);

        return GameBuildDetector.Detect(fileVersion, executablePath);
    }

    /// <summary>
    /// Polls the "not loading" flag and hands each interval to <see cref="IgtAccumulator"/>,
    /// which decides whether it counts - the same accrual an autosplitter's own timer does,
    /// at the same granularity: a periodic sample, not a frame-accurate one.
    /// </summary>
    /// <remarks>
    /// Timed on <see cref="Stopwatch"/> rather than on the wall clock. The two agree until
    /// the system clock is stepped - an NTP correction, a timezone change, a laptop waking
    /// up - and then a wall-clock difference is not a duration at all: it can jump forwards
    /// by minutes or run backwards, and a run tracked over several evenings has plenty of
    /// opportunity to meet one.
    /// </remarks>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        long lastTick = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TimeSpan delta = Stopwatch.GetElapsedTime(lastTick);
            lastTick = Stopwatch.GetTimestamp();

            bool? notLoading = ReadNotLoading();

            lock (_gate)
            {
                // The process went away or the read failed. Stay attached rather than
                // tearing down - the game regularly owns the foreground exclusively during
                // a scene change, and a transient read failure should not lose the run's
                // accumulated time.
                _failure = notLoading is null
                    ? "lost contact with the game (will keep trying)"
                    : null;

                _accumulator.Sample(delta, notLoading);
            }
        }
    }

    private bool? ReadNotLoading()
    {
        IntPtr handle;
        nint address;

        lock (_gate)
        {
            handle = _processHandle;
            address = _flagAddress;
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[1];
        return ReadProcessMemory(handle, (IntPtr)address, buffer, buffer.Length, out nint bytesRead) && bytesRead == 1
            ? buffer[0] != 0
            : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, Span<byte> buffer, nint size, out nint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Decides how much of each polled interval counts as in-game time.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="GameClock"/> for the same reason
/// <see cref="GameBuildDetector"/> is: this is the rule that decides what the number
/// means, and it can be checked against a scripted sequence of loading screens on any
/// platform, with no game running and no process memory involved.
/// </para>
/// <para>
/// An interval is charged when the sample that <em>closes</em> it says the game was not
/// loading. The previous sample used to decide instead, which charged the first interval
/// of every loading screen and dropped the first interval of every return to play; both
/// are the same 200ms, so the totals were close, but the rule read backwards from the
/// autosplitter's own <c>isLoading { return !current.notLoading; }</c> and was that much
/// harder to reason about.
/// </para>
/// <para>
/// A failed read charges nothing. Time that cannot be classified is not silently counted
/// as play: a run left open while the game is closed would otherwise accumulate hours.
/// </para>
/// </remarks>
internal sealed class IgtAccumulator
{
    /// <summary>Everything charged so far.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>
    /// The last reading: true on a loading screen, false while playing, null when nothing
    /// has been read yet or the last read failed.
    /// </summary>
    public bool? Loading { get; private set; }

    /// <summary>Offers one interval, closed by one reading of the game's flag.</summary>
    /// <param name="delta">How long the interval lasted.</param>
    /// <param name="notLoading">The flag, or null if it could not be read.</param>
    public void Sample(TimeSpan delta, bool? notLoading)
    {
        if (notLoading is null)
        {
            return;
        }

        Loading = !notLoading.Value;

        if (notLoading.Value)
        {
            Elapsed += delta;
        }
    }

    /// <summary>Sets the total, for a run resumed from disk or reset back to zero.</summary>
    public void Seed(TimeSpan total)
    {
        Elapsed = total;
        Loading = null;
    }

    /// <summary>Drops the last reading without touching the total.</summary>
    public void Forget() => Loading = null;
}

/// <summary>
/// The build-matching logic behind <see cref="GameClock.DetectBuild(FileVersionInfo, string)"/>,
/// pulled out as a plain function with no platform attribute and no dependency on a running
/// process, so <c>WitcherTrack selftest</c> can check it against the autosplitter's own
/// version table on any platform, not only Windows.
/// </summary>
internal static class GameBuildDetector
{
    /// <summary>Matches a file version string plus the folder the executable runs from.</summary>
    internal static string? Detect(string fileVersion, string executablePath)
    {
        switch (fileVersion)
        {
            case "3.0.19.14337": return "standard";
            case "3.0.19.14336": return "gog_goty";
            case "3.0.4.58000": return "old_patch";

            case "4.0.1.37654":
                return string.Equals(ContainingFolderName(executablePath), "x64_dx12", StringComparison.OrdinalIgnoreCase)
                    ? "complete_edition_dx12"
                    : "complete_edition_dx11";

            default:
                return null;
        }
    }

    /// <summary>
    /// The name of the folder directly containing <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Split by hand on both separators rather than through <see cref="Path"/>, whose
    /// separator handling follows the platform it runs on: the game only ever runs on
    /// Windows, so a path here is always backslash-delimited regardless of where this is
    /// checked, and <see cref="Path"/>'s methods would silently misparse it everywhere
    /// except Windows itself - exactly where the self-test that exercises this also needs
    /// to pass.
    /// </remarks>
    private static string? ContainingFolderName(string path)
    {
        string[] segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : null;
    }
}
