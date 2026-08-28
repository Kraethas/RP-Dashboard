using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LogPusherPanel;

/// <summary>What LogPusher records about its own progress, out of <c>state.json</c>.</summary>
public sealed record WatcherState(int Sessions, int Files, int ProblemFiles, DateTimeOffset? LastPoll);

/// <summary>One line out of <c>logs/decisions.jsonl</c>.</summary>
public sealed record Decision(
    DateTimeOffset? Timestamp,
    string Action,
    string Reason,
    string? Character,
    string? SourceFile);

/// <summary>
/// A LogPusher installation on disk, addressed by its exe.
///
/// Every path LogPusher uses hangs off the folder holding the executable — that is
/// <c>config._default_root()</c>'s rule for a frozen build, and it is why one setting is enough
/// here. Nothing in this class writes to any of them: the panel reads what LogPusher already
/// records and otherwise only starts and stops the process.
/// </summary>
public sealed class LogPusherInstall
{
    /// <summary>Matches the ACTION_* constants in <c>logpusher_core/decision_log.py</c>.</summary>
    public const string ActionPushed = "pushed";
    public const string ActionFailed = "failed";
    public const string ActionSkipped = "skipped";

    public LogPusherInstall(string exePath) => ExePath = exePath;

    public string ExePath { get; }

    /// <summary>LogPusher's project root: the folder its exe sits in.</summary>
    public string Root => Path.GetDirectoryName(Path.GetFullPath(ExePath)) ?? string.Empty;

    public string DecisionLogPath => Path.Combine(Root, "logs", "decisions.jsonl");
    public string StatePath => Path.Combine(Root, "state.json");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string ArchiveDirectory => Path.Combine(Root, "archive");

    public bool Exists => File.Exists(ExePath);

    /// <summary>
    /// The places LogPusher is normally unpacked, relative to the dashboard. Checked in order so
    /// the common layout — both projects side by side under the same parent — just works, and the
    /// user only has to point at the exe when it lives somewhere else.
    /// </summary>
    public static string? AutoDetect()
    {
        var candidates = new List<string>();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Walk up a few levels and look for a LogPusher beside each one. Four is enough to climb
        // out of bin\Debug\net8.0-windows and still see a sibling project folder.
        for (int i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            foreach (string folder in new[] { "NotionUpdater", "LogPusher" })
            {
                candidates.Add(Path.Combine(directory.FullName, folder, "LogPusher.exe"));
                candidates.Add(Path.Combine(directory.FullName, folder, "dist", "LogPusher.exe"));
            }

            if (directory.Parent is null) break;

            foreach (string folder in new[] { "NotionUpdater", "LogPusher" })
            {
                candidates.Add(Path.Combine(directory.Parent.FullName, folder, "LogPusher.exe"));
                candidates.Add(Path.Combine(directory.Parent.FullName, folder, "dist", "LogPusher.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The running LogPusher, if there is one — including one started outside the dashboard,
    /// which is the normal case for a tool designed to run headless all day.
    ///
    /// Matched on the executable path rather than the process name, so a LogPusher unpacked
    /// somewhere else isn't mistaken for this one.
    /// </summary>
    public Process? FindRunning()
    {
        string target;
        try { target = Path.GetFullPath(ExePath); }
        catch { return null; }

        foreach (Process process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(ExePath)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch
            {
                // Access denied reading another process's module is routine and not worth
                // reporting; it just means this isn't a process we can identify.
            }

            process.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Starts LogPusher's own window. Deliberately not a hidden child process: LogPusher has a
    /// start/stop GUI of its own, it outlives the dashboard, and burying it inside a tab would
    /// make a tool that runs all day harder to see rather than easier.
    /// </summary>
    public Process Start()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(ExePath),
            Arguments = "--gui",
            WorkingDirectory = Root,
            UseShellExecute = true
        };

        return Process.Start(info) ?? throw new IOException("Windows didn't start LogPusher.");
    }

    /// <summary>The last <paramref name="count"/> decisions, newest last.</summary>
    public IReadOnlyList<Decision> ReadDecisions(int count)
    {
        if (!File.Exists(DecisionLogPath)) return Array.Empty<Decision>();

        string[] lines;

        try
        {
            // LogPusher appends to this file while we read it, so the share mode has to allow
            // its writes. Without ReadWrite this throws whenever a push happens to land.
            using var stream = new FileStream(
                DecisionLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            lines = reader.ReadToEnd().Split('\n');
        }
        catch (IOException)
        {
            return Array.Empty<Decision>();
        }

        var decisions = new List<Decision>();

        foreach (string line in lines.Reverse())
        {
            if (decisions.Count >= count) break;

            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            Decision? decision = ParseDecision(trimmed);
            if (decision is not null) decisions.Add(decision);
        }

        decisions.Reverse();
        return decisions;
    }

    private static Decision? ParseDecision(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            return new Decision(
                Timestamp: root.TryGetProperty("ts", out JsonElement ts) &&
                           DateTimeOffset.TryParse(ts.GetString(), out DateTimeOffset parsed)
                    ? parsed
                    : null,
                Action: Text(root, "action") ?? "note",
                Reason: Text(root, "reason") ?? string.Empty,
                Character: Text(root, "character"),
                SourceFile: Text(root, "source_file"));
        }
        catch (JsonException)
        {
            // A half-written last line is normal when the file is being appended to.
            return null;
        }

        static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>When the decision log last changed, for "nothing has happened in a while".</summary>
    public DateTime? LastActivity()
    {
        try
        {
            return File.Exists(DecisionLogPath) ? File.GetLastWriteTime(DecisionLogPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>What LogPusher's own state file says it knows, or null if there isn't one yet.</summary>
    public WatcherState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;

            using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            return new WatcherState(
                Sessions: CountOf(root, "sessions"),
                Files: CountOf(root, "files"),
                ProblemFiles: CountOf(root, "problem_files"),
                LastPoll: root.TryGetProperty("last_poll", out JsonElement poll) &&
                          DateTimeOffset.TryParse(poll.GetString(), out DateTimeOffset parsed)
                    ? parsed
                    : null);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }

        static int CountOf(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().Count()
                : 0;
    }
}
