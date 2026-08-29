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

        List<string> mine = Bodies(ours);
        List<string> other = Bodies(theirs);

        Report("dashboard", mine);
        Report("afterline", other);
        Console.WriteLine();

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
    /// The message bodies, with the timestamp stripped.
    ///
    /// Timestamps are dropped on purpose: Afterline records the time the game printed a line,
    /// while the dashboard records the time it read one, so they differ by up to a poll. The
    /// text is what has to match.
    /// </summary>
    private static List<string> Bodies(string path)
    {
        var bodies = new List<string>();

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[SERVER:", StringComparison.Ordinal)) continue;
            if (line.StartsWith("====", StringComparison.Ordinal)) continue;

            // "[HH:MM:SS] body"
            if (line.Length > 11 && line[0] == '[' && line[9] == ']')
                line = line[10..].Trim();

            if (line.Length > 0) bodies.Add(line);
        }

        return bodies;
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in lines) counts[line] = counts.GetValueOrDefault(line) + 1;
        return counts;
    }

    private static void Report(string label, List<string> lines)
    {
        int distinct = lines.Distinct(StringComparer.Ordinal).Count();
        int duplicates = lines.Count - distinct;
        double share = lines.Count == 0 ? 0 : 100.0 * duplicates / lines.Count;

        Console.WriteLine($"  {label,-10} {lines.Count,7} lines  {distinct,7} distinct  " +
                          $"{duplicates,7} duplicates ({share:F1}%)");
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
