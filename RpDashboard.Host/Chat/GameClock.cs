using System.Text.RegularExpressions;

namespace RpDashboard.Host.Chat;

/// <summary>
/// The game's clock, recovered from the timestamps it prints.
///
/// GTA World stamps each line with the server's time, which is not this machine's: the observed
/// offset here is three hours, and daylight saving or a move will change it. The chat log has to
/// be kept on the *game's* clock, because that is the one every other player's log is on. A log
/// that can't be lined up against someone else's is worth much less if it is ever needed.
///
/// The game only ever gives a time of day, never a date, so the date is inferred: of yesterday,
/// today and tomorrow, take whichever puts the stamp nearest to the moment the line was read.
/// With a three-hour offset that resolves correctly, and it keeps resolving correctly across
/// midnight in either clock without anyone configuring a timezone.
///
/// Lines the HUD did not stamp — and there are some — are dated by carrying the last known
/// offset forward, which is better than filing them under the local date and much better than
/// dropping them.
/// </summary>
internal sealed class GameClock
{
    private static readonly Regex Stamp = new(@"^\[(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})\]",
        RegexOptions.Compiled);

    /// <summary>Game time minus local time, from the most recent stamped line.</summary>
    private TimeSpan _offset = TimeSpan.Zero;
    private bool _known;

    /// <summary>True once a stamped line has been seen and the offset is real rather than assumed.</summary>
    public bool OffsetKnown => _known;

    /// <summary>Game time minus local time. Zero until a stamped line has been seen.</summary>
    public TimeSpan Offset => _offset;

    /// <summary>
    /// When the game says this line happened. <paramref name="seenAt"/> is when it was read,
    /// and is only used to work out which day the game's time-of-day belongs to.
    /// </summary>
    public DateTime TimeOf(string text, DateTime seenAt)
    {
        Match match = Stamp.Match(text ?? string.Empty);

        if (!match.Success)
        {
            // Unstamped. The last known offset is the best available answer; before any stamped
            // line has arrived that is zero, which is simply local time.
            return seenAt + _offset;
        }

        var timeOfDay = new TimeSpan(
            int.Parse(match.Groups["h"].Value),
            int.Parse(match.Groups["m"].Value),
            int.Parse(match.Groups["s"].Value));

        // Of yesterday, today and tomorrow, whichever puts this time closest to when it was read.
        DateTime best = seenAt.Date + timeOfDay;
        TimeSpan bestGap = Gap(best, seenAt);

        foreach (int shift in new[] { -1, 1 })
        {
            DateTime candidate = seenAt.Date.AddDays(shift) + timeOfDay;
            TimeSpan gap = Gap(candidate, seenAt);

            if (gap < bestGap)
            {
                best = candidate;
                bestGap = gap;
            }
        }

        _offset = best - seenAt;
        _known = true;
        return best;
    }

    private static TimeSpan Gap(DateTime a, DateTime b) => (a - b).Duration();
}
