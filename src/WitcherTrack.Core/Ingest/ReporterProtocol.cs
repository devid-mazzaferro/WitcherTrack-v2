using System.Globalization;
using WitcherTrack.Core.Model;

namespace WitcherTrack.Core.Ingest;

/// <summary>
/// Where the player was standing when something was completed.
/// </summary>
/// <remarks>
/// A map pin knows where it is; a quest entry and a Gwent card do not, and the only
/// moment either can be given a place is the moment it happens. The reporter therefore
/// says just "the player is here, now" and leaves it to the tracker to attach that to
/// whatever it sees finish in the same breath - which also means a card acquires a
/// position for free, even though the sweep that reports cards re-lists the whole
/// collection and cannot say which one is new.
/// </remarks>
public readonly record struct PlayerPlace(double X, double Y, string World);

/// <summary>
/// Parses the lines emitted by the in-game reporter (<c>modWitcherTrack</c>).
/// </summary>
/// <remarks>
/// <para>
/// The reporter writes one record per line through the game's own logging function, so
/// every line arrives mixed into the game's script log alongside unrelated output. The
/// <c>WT|</c> prefix and a version field make the tracker's own lines unambiguous and
/// let the format change without breaking older builds.
/// </para>
/// <code>
/// WT|v1|meta|begin|light
/// WT|v1|quest|q104_wandering_in_the_dark|done
/// WT|v1|diagram|Diagram: Svarog runestone|done
/// WT|v1|poi|q001_bandit_camp_velen_03|done|BanditCamp|3|142.93|-184.08|levels\novigrad\novigrad.w2w
/// WT|v1|meta|end|light
/// </code>
/// <para>
/// Identifiers are the game's internal names, never localised display strings, so the
/// same line means the same thing whatever language the game is running in.
/// </para>
/// </remarks>
public static class ReporterProtocol
{
    /// <summary>The prefix every reporter line starts with.</summary>
    public const string Prefix = "WT|v1|";

    /// <summary>One parsed record.</summary>
    /// <param name="Kind">The record kind, or <see langword="null"/> for metadata records.</param>
    /// <param name="Id">The catalogue identifier, or the metadata key.</param>
    /// <param name="State">The observed state, for non-metadata records.</param>
    /// <param name="MetaValue">The metadata value, for metadata records.</param>
    /// <param name="IsMeta">True for <c>meta</c> records, which frame a dump rather than describe an entry.</param>
    /// <param name="Extras">
    /// Trailing fields, whose meaning depends on the kind. For a quest they are the
    /// content pack and the quest category; for a point of interest the pin type and the
    /// area. Empty for kinds that carry no extras.
    /// </param>
    public readonly record struct Record(
        TrackedKind? Kind,
        string Id,
        CompletionState State,
        string? MetaValue,
        bool IsMeta,
        string[] Extras,
        PlayerPlace? Place = null)
    {
        /// <summary>
        /// The content pack a quest belongs to: <c>base</c>, <c>hos</c> or <c>baw</c>.
        /// </summary>
        /// <remarks>
        /// Reported by the game itself through <c>CJournalQuest.GetContentType()</c>, which
        /// vanilla calls the expansion index, so no curation is needed to decide which
        /// completion mode a quest counts toward.
        /// </remarks>
        public string? Dlc => Kind == TrackedKind.Quest && Extras.Length > 0 ? Extras[0] : null;

        /// <summary>
        /// The quest category: <c>story</c>, <c>chapter</c>, <c>side</c>, <c>contract</c>
        /// or <c>treasure</c>, from <c>CJournalQuest.GetType()</c>.
        /// </summary>
        public string? Category => Kind == TrackedKind.Quest && Extras.Length > 1 ? Extras[1] : null;

        /// <summary>
        /// The Gwent faction. Skellige is the deck Blood and Wine added, which is what
        /// separates the base-game collection from the expansion cards.
        /// </summary>
        public string? Faction => Kind == TrackedKind.GwentCard && Extras.Length > 0 ? Extras[0] : null;

        /// <summary>
        /// World X/Y for a point of interest, in the game's own coordinate space, or null
        /// for a reporter build that predates sending them (or for any other kind).
        /// </summary>
        /// <remarks>
        /// Trails the pin type and area, which is why <see cref="DisplayName"/> knows to
        /// skip four fields rather than two for this kind. Parsed with
        /// <see cref="CultureInfo.InvariantCulture"/>: the reporter always writes a dot for
        /// the decimal separator regardless of the game's own display language.
        /// </remarks>
        public (double X, double Y)? Position
        {
            get
            {
                if (Kind != TrackedKind.PointOfInterest || Extras.Length < 4)
                {
                    return null;
                }

                return double.TryParse(Extras[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                       double.TryParse(Extras[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    ? (x, y)
                    : null;
            }
        }

        /// <summary>
        /// The game's own path for the streamed world a point of interest's position was
        /// read from, for example <c>levels\novigrad\novigrad.w2w</c>, or null for a
        /// reporter build that predates sending it (or for any other kind).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what tells apart points of interest that only look the same: White
        /// Orchard, Velen+Novigrad, Skellige and Kaer Morhen each reset their own X/Y near
        /// their own origin, the same way Toussaint's separate world file does (see
        /// <c>KNOWN-ISSUES.md</c>) - so a position alone cannot say which of them a point
        /// belongs to, only this can.
        /// </para>
        /// <para>
        /// Told apart from the start of a display name, rather than counted on a fixed
        /// field position, because an older reporter build's dump has no such field at all
        /// and still has to parse: a world path always contains a path separator or ends in
        /// <c>.w2w</c>, and neither is plausible in the game's own localised place names.
        /// </para>
        /// </remarks>
        public string? WorldPath
        {
            get
            {
                if (Kind != TrackedKind.PointOfInterest || Position is null || Extras.Length < 5)
                {
                    return null;
                }

                string candidate = Extras[4];
                return candidate.Contains('\\', StringComparison.Ordinal) ||
                       candidate.EndsWith(".w2w", StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : null;
            }
        }

        /// <summary>
        /// The name the game shows for this entry, localised, or null when the reporter is
        /// an older build that did not send one.
        /// </summary>
        /// <remarks>
        /// The name is always the last field and may itself contain the separator, so
        /// everything past the fields this kind is known to carry is rejoined rather than
        /// taking a single element. The identifier stays the key; this is for reading.
        /// <para>
        /// A point of interest counts five fields before the name once a world path is
        /// present, four with coordinates but no world path, two with neither - and all
        /// three shapes have to keep working, because a log captured before the reporter
        /// started sending positions (or before it started sending world paths) is still a
        /// valid replay input, not just something to migrate away from.
        /// </para>
        /// </remarks>
        public string? DisplayName
        {
            get
            {
                int fieldsBeforeName = Kind switch
                {
                    TrackedKind.Quest => 2,                          // content pack, category
                    TrackedKind.PointOfInterest => WorldPath is not null
                        ? 5                                          // pin type, area, X, Y, world path
                        : Position is null
                            ? 2                                      // pin type, area
                            : 4,                                     // pin type, area, X, Y
                    TrackedKind.GwentCard => 1,                      // faction
                    _ => 1,                                          // an empty placeholder
                };

                if (Extras.Length <= fieldsBeforeName)
                {
                    return null;
                }

                string name = string.Join('|', Extras[fieldsBeforeName..]).Trim();
                return name.Length == 0 ? null : name;
            }
        }
    }

    /// <summary>
    /// Attempts to parse one line of game log output.
    /// </summary>
    /// <remarks>
    /// Returns false for anything that is not a reporter line, which is the common case:
    /// the game writes a great deal of unrelated output to the same log.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<char> line, out Record record)
    {
        record = default;

        // The game prefixes log lines with its own channel decoration, so look for the
        // marker anywhere rather than requiring it at position zero.
        int start = line.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        ReadOnlySpan<char> body = line[(start + Prefix.Length)..].TrimEnd();

        if (!TryTake(ref body, out ReadOnlySpan<char> kindText) ||
            !TryTake(ref body, out ReadOnlySpan<char> idText))
        {
            return false;
        }

        if (kindText.Equals("meta", StringComparison.Ordinal))
        {
            record = new Record(null, idText.ToString(), CompletionState.NotDone, body.ToString(), IsMeta: true, []);
            return true;
        }

        // `at` says only where the player is, with no identifier: what the place belongs
        // to is decided by whatever completes alongside it. Its second field is already
        // the X coordinate, which is why it is read here rather than through the usual
        // id-and-state path below.
        if (kindText.Equals("at", StringComparison.Ordinal))
        {
            if (!TryTake(ref body, out ReadOnlySpan<char> yText) ||
                !double.TryParse(idText, NumberStyles.Float, CultureInfo.InvariantCulture, out double atX) ||
                !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out double atY) ||
                body.IsEmpty)
            {
                return false;
            }

            record = new Record(null, string.Empty, CompletionState.NotDone, null, IsMeta: false, [],
                                new PlayerPlace(atX, atY, body.ToString()));
            return true;
        }

        if (!TryTake(ref body, out ReadOnlySpan<char> stateText) ||
            !TryParseKind(kindText, out TrackedKind kind))
        {
            return false;
        }

        // Empty fields are kept: positions carry meaning, and the display name that
        // follows them is identified by position.
        string[] extras = body.IsEmpty ? [] : body.ToString().Split('|');

        record = new Record(kind, idText.ToString(), ParseState(stateText), null, IsMeta: false, extras);
        return true;
    }

    /// <summary>Splits off the next field, up to the next separator or the end.</summary>
    private static bool TryTake(ref ReadOnlySpan<char> body, out ReadOnlySpan<char> field)
    {
        if (body.IsEmpty)
        {
            field = default;
            return false;
        }

        int separator = body.IndexOf('|');

        if (separator < 0)
        {
            field = body;
            body = default;
        }
        else
        {
            field = body[..separator];
            body = body[(separator + 1)..];
        }

        return !field.IsEmpty;
    }

    private static bool TryParseKind(ReadOnlySpan<char> text, out TrackedKind kind)
    {
        if (text.Equals("quest", StringComparison.Ordinal)) { kind = TrackedKind.Quest; return true; }
        if (text.Equals("diagram", StringComparison.Ordinal)) { kind = TrackedKind.Diagram; return true; }
        if (text.Equals("formula", StringComparison.Ordinal)) { kind = TrackedKind.Formula; return true; }
        if (text.Equals("poi", StringComparison.Ordinal)) { kind = TrackedKind.PointOfInterest; return true; }
        if (text.Equals("gwent", StringComparison.Ordinal)) { kind = TrackedKind.GwentCard; return true; }

        kind = default;
        return false;
    }

    /// <summary>
    /// Maps a reported state onto the tracker's own.
    /// </summary>
    /// <remarks>
    /// A point of interest that is merely <c>discovered</c> or <c>known</c> has been seen
    /// but not cleared, so it does not count. Only a disabled pin, which the game itself
    /// treats as exhausted, is reported as <c>done</c>.
    /// </remarks>
    private static CompletionState ParseState(ReadOnlySpan<char> text)
    {
        if (text.Equals("done", StringComparison.Ordinal)) return CompletionState.Done;
        if (text.Equals("failed", StringComparison.Ordinal)) return CompletionState.Failed;
        return CompletionState.NotDone;
    }
}
