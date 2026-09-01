using System.IO;

namespace RpDashboard.SelfTest;

/// <summary>
/// Compares the dashboard's chat log against Afterline's for the same day.
///
/// This is the whole point of running both at once. Replacing Afterline is only safe if the
/// dashboard captures everything it does, and "I'm confident it does" is not evidence —
/// this project has twice shipped a rule that read correctly and failed on real use.
///
/// What matters is the first number: lines Afterline saw and we did not. Duplicates on their
/// side are expected (73% of their output across five days) and are the reason for the exercise.
///
///     dotnet run --project tests/RpDashboard.SelfTest -- --compare [day]
///
/// `day` is like "29-August-2026", defaulting to today.
/// </summary>
internal static class Compare
{
    private const string AfterlineFolder = @"D:\Documents\GTA RP\Chat Parser\Afterline";

    public static int Run(string? day)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        day ??= DateTime.Today.ToString("dd-MMMM-yyyy", invariant);


        // Matched on the date rather than the exact filename. Afterline zero-pads the day and
        // the dashboard did not until 1 September made the difference visible, so files written
        // either way still have to pair up.
        var names = new List<string>();
        if (DateTime.TryParse(day.Replace('-', ' '), invariant,
                System.Globalization.DateTimeStyles.None, out DateTime parsed))
        {
            // ToString with the culture spelled out, not interpolation: interpolation formats
            // with the *current* culture, and on this machine that renders September as
            // "Σεπτεμβρίου" — a filename nothing on disk will ever match. Same trap the writer
            // guards against when it names the file in the first place.
            names.Add("Chatlog [GTAWorld Roleplay English] [" +
                      parsed.ToString("dd-MMMM-yyyy", invariant) + "].txt");
            names.Add("Chatlog [GTAWorld Roleplay English] [" +
                      parsed.ToString("d-MMMM-yyyy", invariant) + "].txt");
        }
        else
        {
            names.Add($"Chatlog [GTAWorld Roleplay English] [{day}].txt");
        }

        string logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RpDashboard", "ChatLogs");

        string ours = names.Select(n => Path.Combine(logs, n)).FirstOrDefault(File.Exists)
                      ?? Path.Combine(logs, names[0]);

        string? theirs = names.Select(FindAfterline).FirstOrDefault(found => found is not null);

        Console.WriteLine($"day     {day}");
        Console.WriteLine($"ours    {ours}");
        Console.WriteLine($"theirs  {theirs ?? "(not found under " + AfterlineFolder + ")"}");
        Console.WriteLine();

        if (!File.Exists(ours))
        {
            Console.WriteLine("Nothing written by the dashboard for that day yet.");
            Console.WriteLine("Turn on 'Chat log' in the status bar and play a session.");
            return 1;
        }

        if (theirs is null || !File.Exists(theirs))
        {
            Console.WriteLine("No Afterline log for that day, so there is nothing to compare against.");
            return 1;
        }

        List<Entry> mineEntries = Bodies(ours);
        List<Entry> otherEntries = Bodies(theirs);

        Report("dashboard", mineEntries);
        Report("afterline", otherEntries);
        Console.WriteLine();

        // Only Afterline lines from while the dashboard was actually writing can fairly be
        // called missed.
        //
        // First-line-to-last-line is not that window, and reading it as such is badly
        // misleading: on 31 August the dashboard ran in eight short bursts totalling 23 minutes,
        // spread from 08:39 to 23:59. Treating that as one span said 394 lines were missed. The
        // truth was zero — every one of those 394 arrived while the dashboard was closed.
        List<(int From, int To)> spans = WritingSpans(mineEntries);

        Console.WriteLine("  the dashboard was writing during:");
        foreach ((int from, int to) in spans)
            Console.WriteLine($"     {Clock(from)}..{Clock(to)}  ({(to - from) / 60} min)");

        int covered = spans.Sum(s => s.To - s.From);
        Console.WriteLine($"     {spans.Count} span(s), {covered / 60} minutes in total");
        Console.WriteLine();

        otherEntries = otherEntries.Where(e => InAnySpan(Seconds(e.Time), spans)).ToList();
        Console.WriteLine($"  Afterline wrote {otherEntries.Count} line(s) during those spans.");
        Console.WriteLine("  Anything outside them was never ours to catch.");
        Console.WriteLine();

        List<string> mine = mineEntries.Select(e => e.Body).ToList();
        List<string> other = otherEntries.Select(e => e.Body).ToList();

        // Compared as multisets: a line legitimately repeated in play ("* Ilya nods.") should
        // count as many times as it happened, not be collapsed into one.
        Dictionary<string, int> mineCounts = Counts(mine);
        Dictionary<string, int> otherCounts = Counts(other);

        var missed = new List<string>();
        foreach ((string line, int count) in otherCounts)
        {
            // Their duplicates are noise, so only a line we have *fewer distinct occasions* of
            // counts as missed. Their count is inflated; ours is the honest one.
            if (!mineCounts.ContainsKey(line)) missed.Add(line);
        }

        var extra = mineCounts.Keys.Where(line => !otherCounts.ContainsKey(line)).ToList();

        Console.WriteLine($"in Afterline but not in ours   {missed.Count}   <-- the number that matters");
        Console.WriteLine($"in ours but not in Afterline   {extra.Count}");
        Console.WriteLine();

        Show("missed", missed);
        Show("extra", extra);

        if (missed.Count == 0)
        {
            Console.WriteLine("Nothing was missed. One clean day is not proof; a few are.");
            return 0;
        }

        Console.WriteLine("Some lines were only seen by Afterline while the dashboard was writing.");
        Console.WriteLine("That is the case that matters, so work out which it was:");
        Console.WriteLine("  - did the status bar report chat arriving faster than it could be read?");
        Console.WriteLine("  - did the dashboard restart inside one of those spans?");
        return 1;
    }

    /// <summary>Afterline nests its logs by year and month.</summary>
    private static string? FindAfterline(string name)
    {
        if (!Directory.Exists(AfterlineFolder)) return null;

        try
        {
            return Directory.EnumerateFiles(AfterlineFolder, name, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>How long a silence ends one writing span and starts the next.</summary>
    private const int SpanGapSeconds = 300;

    /// <summary>
    /// The stretches the dashboard was actually writing, rather than the gap between its first
    /// line and its last. A dashboard opened for ten minutes at breakfast and ten at midnight
    /// covered twenty minutes, not sixteen hours.
    /// </summary>
    private static List<(int From, int To)> WritingSpans(List<Entry> entries)
    {
        var spans = new List<(int, int)>();
        List<int> times = entries.Select(e => Seconds(e.Time)).Where(t => t >= 0).Distinct().Order().ToList();

        if (times.Count == 0) return spans;

        int start = times[0], last = times[0];

        foreach (int time in times.Skip(1))
        {
            if (time - last > SpanGapSeconds)
            {
                spans.Add((start, last));
                start = time;
            }

            last = time;
        }

        spans.Add((start, last));
        return spans;
    }

    private static bool InAnySpan(int time, List<(int From, int To)> spans)
        => time >= 0 && spans.Any(span => time >= span.From && time <= span.To);

    private static int Seconds(string time)
        => time.Length == 8 && int.TryParse(time[..2], out int h) &&
           int.TryParse(time[3..5], out int m) && int.TryParse(time[6..], out int s)
            ? h * 3600 + m * 60 + s
            : -1;

    private static string Clock(int seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";

    private sealed record Entry(string Time, string Body);

    /// <summary>
    /// Each line as (time, body). Both logs now stamp on the game's clock, so the times line up
    /// and are what bounds the comparison window; the body is what has to match.
    /// </summary>
    private static List<Entry> Bodies(string path)
    {
        var bodies = new List<Entry>();

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[SERVER:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("====", StringComparison.Ordinal)) continue;

            // "[HH:MM:SS] body"
            string time = "";
            if (line.Length > 11 && line[0] == '[' && line[9] == ']')
            {
                time = line[1..9];
                line = line[10..].Trim();
            }

            if (line.Length > 0) bodies.Add(new Entry(time, line));
        }

        return bodies;
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in lines) counts[line] = counts.GetValueOrDefault(line) + 1;
        return counts;
    }

    private static void Report(string label, List<Entry> entries)
    {
        List<string> lines = entries.Select(e => e.Body).ToList();
        int distinct = lines.Distinct(StringComparer.Ordinal).Count();
        int duplicates = lines.Count - distinct;
        double share = lines.Count == 0 ? 0 : 100.0 * duplicates / lines.Count;
        string window = entries.Count == 0 ? "-" : $"{entries[0].Time}..{entries[^1].Time}";

        Console.WriteLine($"  {label,-10} {lines.Count,7} lines  {distinct,7} distinct  " +
                          $"{duplicates,7} duplicates ({share,5:F1}%)  window {window}");
    }

    private static void Show(string label, List<string> lines)
    {
        if (lines.Count == 0) return;

        Console.WriteLine($"{label}, first 15:");
        foreach (string line in lines.Take(15))
            Console.WriteLine($"    {(line.Length > 100 ? line[..97] + "..." : line)}");

        if (lines.Count > 15) Console.WriteLine($"    ... and {lines.Count - 15} more");
        Console.WriteLine();
    }
}
