using WitcherTrack.Core.Model;

namespace WitcherTrack.Core.Ingest;

/// <summary>
/// Turns a stream of reporter lines into observations, grouping the records that belong
/// to one dump into a single snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The reporter frames a dump with <c>meta|begin</c> and <c>meta|end</c>. Everything in
/// between describes the whole world at one instant, so it is emitted as a snapshot,
/// which supersedes earlier observations and is what lets the tracker recover after the
/// player reloads an earlier savegame.
/// </para>
/// <para>
/// Records arriving outside a dump are individual events: the reporter saw a quest
/// complete or a diagram learned, and said so immediately. Those are additive.
/// </para>
/// </remarks>
public sealed class ReporterIngest
{
    private readonly Dictionary<string, CompletionState> _pending = new(StringComparer.Ordinal);
    private bool _insideDump;

    /// <summary>Raised for a completed dump, describing the whole world state.</summary>
    public event Action<IReadOnlyDictionary<string, CompletionState>>? SnapshotReceived;

    /// <summary>Raised for a single observation reported outside a dump.</summary>
    public event Action<string, CompletionState>? EventReceived;

    /// <summary>
    /// Raised when the reporter says where the player is. Carries no identifier: what the
    /// place belongs to is whatever completes alongside it, which only the tracker knows.
    /// </summary>
    public event Action<PlayerPlace>? PlaceReceived;

    /// <summary>Number of dumps completed since the tracker started.</summary>
    public int SnapshotCount { get; private set; }

    /// <summary>Number of individual events seen since the tracker started.</summary>
    public int EventCount { get; private set; }

    /// <summary>
    /// Feeds one line of game log output. Lines that are not reporter output are ignored.
    /// </summary>
    public void Accept(string line)
    {
        if (!ReporterProtocol.TryParse(line, out ReporterProtocol.Record record))
        {
            return;
        }

        if (record.IsMeta)
        {
            HandleMeta(record);
            return;
        }

        // Only ever written live, never inside a dump - a dump re-asserts hundreds of
        // entries at once and the player is standing in exactly one place.
        if (record.Place is { } place)
        {
            PlaceReceived?.Invoke(place);
            return;
        }

        if (Normalise(record) is not { } observation)
        {
            return;
        }

        (string id, CompletionState observed) = observation;

        if (_insideDump)
        {
            // Several Gwent cards fold into one catalogue entry, and owning any one of
            // them satisfies it, so a completion already recorded is never overwritten by
            // a later copy that the player does not have.
            if (_pending.TryGetValue(id, out CompletionState existing) && existing == CompletionState.Done)
            {
                return;
            }

            _pending[id] = observed;
        }
        else
        {
            EventCount++;
            EventReceived?.Invoke(id, observed);
        }
    }

    /// <summary>
    /// Maps a reported record onto the catalogue entry it belongs to, or null when it is
    /// something the catalogue does not track.
    /// </summary>
    /// <remarks>
    /// Only Gwent needs translating. The game reports a card index, but "Collect 'Em All"
    /// asks for one of each card type and several indices can be the same card, so the
    /// index is folded into its type. An index with no card behind it is dropped.
    /// </remarks>
    private static (string Id, CompletionState State)? Normalise(ReporterProtocol.Record record)
    {
        if (record.Kind != TrackedKind.GwentCard)
        {
            return (record.Id, record.State);
        }

        if (!int.TryParse(record.Id, out int index) ||
            !GameData.GwentCardTypes.TryGetValue(index, out string? type))
        {
            return null;
        }

        return ($"gwent:{type}", record.State);
    }

    /// <summary>
    /// Called when the game restarts and the log is recreated: any half-received dump is
    /// discarded rather than being reported as a complete picture of the world.
    /// </summary>
    public void Reset()
    {
        _pending.Clear();
        _insideDump = false;
    }

    private void HandleMeta(ReporterProtocol.Record record)
    {
        switch (record.Id)
        {
            case "begin":
                _pending.Clear();
                _insideDump = true;
                break;

            case "end":
                if (_insideDump)
                {
                    _insideDump = false;
                    SnapshotCount++;
                    SnapshotReceived?.Invoke(new Dictionary<string, CompletionState>(_pending, StringComparer.Ordinal));
                    _pending.Clear();
                }

                break;

            default:
                // Counts and diagnostics: useful in the log, not needed here.
                break;
        }
    }
}
