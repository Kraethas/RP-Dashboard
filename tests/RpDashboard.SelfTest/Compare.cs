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
        day ??= DateTime.Today.ToString("d-MMMM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
        string name = $"Chatlog [GTAWorld Roleplay English] [{day}].txt";

        string ours = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RpDashboard", "ChatLogs", name);

        string? theirs = FindAfterline(name);

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

        // The first comparison run was misleading because the two files covered different
        // windows: the dashboard was opened mid-session and closed while the game was still up.
        // Printing both windows makes that obvious instead of leaving it to be discovered in the
        // line counts.
        Console.WriteLine("  Compare the windows before reading anything into the numbers.");
        Console.WriteLine("  Lines outside the dashboard's window were never ours to catch.");
        Console.WriteLine();

        // Only Afterline lines inside our window can fairly be called missed.
        string first = mineEntries.FirstOrDefault()?.Time ?? "00:00:00";
        string last = mineEntries.LastOrDefault()?.Time ?? "23:59:59";
        otherEntries = otherEntries.Where(e => Within(e.Time, first, last)).ToList();

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

        Console.WriteLine("Some lines were only seen by Afterline. Before switching, work out why:");
        Console.WriteLine("  - was the dashboard closed, or the chat log switched off, for part of the day?");
        Console.WriteLine("  - did the status bar report chat arriving faster than it could be read?");
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

    /// <summary>
    /// True when a time of day falls in [from, to], allowing for a window that crosses midnight.
    /// A session running past the game's midnight is the ordinary case, not the exception.
    /// </summary>
    private static bool Within(string time, string from, string to)
        => string.CompareOrdinal(from, to) <= 0
            ? string.CompareOrdinal(time, from) >= 0 && string.CompareOrdinal(time, to) <= 0
            : string.CompareOrdinal(time, from) >= 0 || string.CompareOrdinal(time, to) <= 0;

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
