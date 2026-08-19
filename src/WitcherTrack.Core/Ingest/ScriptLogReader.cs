using System.Text;

namespace WitcherTrack.Core.Ingest;

/// <summary>
/// Follows the game's script log and yields complete lines as they are written.
/// </summary>
/// <remarks>
/// <para>
/// The Witcher 3 writes <c>Documents\The Witcher 3\scriptslog.txt</c> when launched with
/// <c>-debugscripts</c>. Reading that file is the whole of the live ingest path: it needs
/// no network port, no injected code and no elevated privileges, and it keeps working if
/// the tracker is started after the game.
/// </para>
/// <para>
/// Three things have to be handled to follow it reliably:
/// </para>
/// <list type="bullet">
///   <item>the file may not exist yet, because the game creates it on launch;</item>
///   <item>it is truncated when the game restarts, so a shrinking file means start over;</item>
///   <item>a read can land mid-line, so a partial tail is carried over to the next read.</item>
/// </list>
/// <para>
/// The file is opened with full sharing, which is what allows reading it while the game
/// holds it open for writing.
/// </para>
/// </remarks>
public sealed class ScriptLogReader
{
    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly StringBuilder _partialLine = new();

    private long _position;

    /// <summary>Creates a reader for a specific log file.</summary>
    /// <param name="path">Full path to the script log.</param>
    /// <param name="pollInterval">How often to check for new content. Defaults to 250 ms.</param>
    public ScriptLogReader(string path, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>
    /// The default location of the script log, under the current user's Documents folder.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "The Witcher 3",
        "scriptslog.txt");

    /// <summary>Raised when the log is truncated, which means the game restarted.</summary>
    public event Action? LogRestarted;

    /// <summary>
    /// Raised each time everything currently in the log has been handed over and the
    /// reader is about to wait for more.
    /// </summary>
    /// <remarks>
    /// The first of these separates the backlog - whatever the log already held when the
    /// tracker started, delivered in one burst - from lines that arrive as they are
    /// written. A caller that measures anything against the clock needs to know which it
    /// is looking at.
    /// </remarks>
    public event Action? CaughtUp;

    /// <summary>
    /// Streams complete lines until cancelled. Waits for the file to appear if it is not
    /// there yet, and never throws for a missing or briefly locked file.
    /// </summary>
    /// <param name="fromStart">
    /// True to replay the existing contents, which is what you want when the tracker is
    /// started after the game. False to report only what is written from now on.
    /// </param>
    public async IAsyncEnumerable<string> ReadLinesAsync(
        bool fromStart = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _position = 0;
        bool positioned = fromStart;

        while (!cancellationToken.IsCancellationRequested)
        {
            string[] lines;

            try
            {
                if (!File.Exists(_path))
                {
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var info = new FileInfo(_path);

                if (!positioned)
                {
                    // Skip whatever was already there and follow from the end.
                    _position = info.Length;
                    positioned = true;
                }
                else if (info.Length < _position)
                {
                    // The game restarted and recreated the log.
                    _position = 0;
                    _partialLine.Clear();
                    LogRestarted?.Invoke();
                }

                if (info.Length == _position)
                {
                    CaughtUp?.Invoke();
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lines = ReadNewLines();
            }
            catch (IOException)
            {
                // The game momentarily holds the file exclusively; try again shortly.
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (string line in lines)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// Reads everything appended since the last call and splits it into complete lines,
    /// carrying any trailing partial line over to the next read.
    /// </summary>
    private string[] ReadNewLines()
    {
        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        stream.Position = _position;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string chunk = reader.ReadToEnd();
        _position = stream.Position;

        _partialLine.Append(chunk);
        string buffered = _partialLine.ToString();

        int lastBreak = buffered.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            return [];
        }

        string complete = buffered[..lastBreak];

        _partialLine.Clear();
        _partialLine.Append(buffered[(lastBreak + 1)..]);

        return complete.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
