using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RpDashboard.Contracts;
using Brush = System.Windows.Media.Brush;

namespace LogPusherPanel;

/// <summary>Where the LogPusher exe is. The only thing this tab has to remember.</summary>
public sealed class PanelSettings
{
    public string ExePath { get; set; } = string.Empty;
}

/// <summary>
/// The LogPusher tab: start it, stop it, and show what it has been deciding.
///
/// This tool is different in kind from the other three and the difference is deliberate.
/// LogPusher is a Python program that reads Afterline's log *files* off disk — it never touches
/// FiveM, so it never subscribes to <see cref="IChatFeed"/> and costs the game nothing whether
/// this tab is open or not. It also outlives the dashboard: it is meant to run headless all day,
/// and closing the dashboard does not stop it.
///
/// So this panel doesn't own LogPusher, it watches it. Everything shown here is read from the
/// <c>state.json</c> and <c>logs/decisions.jsonl</c> that LogPusher already writes for itself.
/// </summary>
public partial class LogPusherView : UserControl
{
    /// <summary>
    /// How often to re-read the decision log. LogPusher polls its own log folder every 30-60
    /// seconds, so anything faster than this is just reading an unchanged file — and the file is
    /// only touched when something actually happened.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private const int DecisionsShown = 60;

    private readonly IToolHost _host;
    private readonly DispatcherTimer _refresh = new() { Interval = RefreshInterval };

    private PanelSettings _settings;
    private LogPusherInstall? _install;
    private DateTime? _lastSeenWrite;
    private bool _sawRunning;

    public LogPusherView(IToolHost host)
    {
        _host = host;
        InitializeComponent();

        _settings = _host.Settings.Load(new PanelSettings());

        if (string.IsNullOrWhiteSpace(_settings.ExePath))
        {
            string? found = LogPusherInstall.AutoDetect();
            if (found is not null)
            {
                _settings.ExePath = found;
                _host.Settings.Save(_settings);
            }
        }

        UseInstall(_settings.ExePath);

        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        Refresh();
    }

    private void UseInstall(string exePath)
    {
        ExePathBox.Text = exePath;
        _install = string.IsNullOrWhiteSpace(exePath) ? null : new LogPusherInstall(exePath);

        // Force the next refresh to redraw the list even if the file's timestamp hasn't moved.
        _lastSeenWrite = null;
    }

    // ---------- commands ----------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Find LogPusher.exe",
            Filter = "LogPusher|LogPusher.exe|Programs|*.exe|All files|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(ExePathBox.Text))
        {
            string? folder = Path.GetDirectoryName(ExePathBox.Text);
            if (folder is not null && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _settings = new PanelSettings { ExePath = dialog.FileName };
        _host.Settings.Save(_settings);
        UseInstall(dialog.FileName);
        Refresh();
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        string? found = LogPusherInstall.AutoDetect();

        if (found is null)
        {
            SummaryText.Text = "Couldn't find LogPusher nearby. Point at its exe with Browse.";
            return;
        }

        _settings = new PanelSettings { ExePath = found };
        _host.Settings.Save(_settings);
        UseInstall(found);
        Refresh();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_install is null || !_install.Exists)
        {
            SummaryText.Text = "Point at LogPusher.exe first.";
            return;
        }

        try
        {
            _install.Start();
            SummaryText.Text = "Starting LogPusher...";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Couldn't start it: {ex.Message}";
        }

        Refresh();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_install is null) return;

        using Process? running = _install.FindRunning();
        if (running is null)
        {
            SummaryText.Text = "It isn't running.";
            return;
        }

        // Confirmed every time, not just for a process the dashboard didn't start. There is no
        // way from out here to tell a LogPusher sitting idle between polls from one halfway
        // through summarising a session and pushing it to Notion, and killing the second kind
        // loses that work — the session is archived to disk first, but the push isn't repeated
        // until the next run notices.
        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            "Stop LogPusher?\n\n" +
            "If it is midway through pushing a session, that push will be interrupted. " +
            "The session stays archived on disk and gets picked up again on the next run.",
            "Stop LogPusher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            // CloseMainWindow first: LogPusher's own GUI shuts the watcher thread down through
            // its should_stop seam, which is a clean stop. Killing is the fallback.
            if (!running.CloseMainWindow() || !running.WaitForExit(4000))
            {
                running.Kill(entireProcessTree: true);
                running.WaitForExit(2000);
            }

            SummaryText.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Couldn't stop it: {ex.Message}";
        }

        Refresh();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_install?.LogsDirectory);

    private void OpenArchive_Click(object sender, RoutedEventArgs e) => OpenFolder(_install?.ArchiveDirectory);

    /// <summary>
    /// The raw chat logs LogPusher reads, as opposed to its own diagnostics under "Diagnostics".
    /// The path lives in LogPusher's <c>state.json</c>, so it is only known once LogPusher has
    /// completed a poll — before that there is nothing to point at.
    /// </summary>
    private void ChatLogs_Click(object sender, RoutedEventArgs e)
    {
        string? folder = _install?.SourceLogFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            SummaryText.Text = "The raw-log folder shows up here once LogPusher has run one poll. " +
                               "Start it, then try again.";
            return;
        }

        OpenFolder(folder);
    }

    /// <summary>
    /// Summarize and push the backlog now. This is the on-demand half of auto-summarize being
    /// off: the watcher archives all day for free, and this pays for the summaries in one go when
    /// the player is done. Runs LogPusher once with --push-all and reports what it did.
    /// </summary>
    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (_install is null || !_install.Exists)
        {
            SummaryText.Text = "Point at LogPusher.exe first.";
            return;
        }

        PushButton.IsEnabled = false;
        string previous = PushButton.Content as string ?? "Push logs";
        PushButton.Content = "Pushing...";
        SummaryText.Text = "Summarizing and pushing finished sessions. This can take a moment.";

        // Note the result file's age before the run, so a stale line from a previous push is not
        // mistaken for this one's if the exe dies before writing.
        DateTime before = SafeWriteTime(_install.LastPushPath);

        string resultText;
        try
        {
            (int exitCode, string _) = await _install.RunOnceAsync("--push-all");

            // Read the outcome from the file LogPusher wrote rather than its stdout: the exe is a
            // windowed build and its console streams are not reliable.
            string fromFile = ResultFileIfFresh(_install.LastPushPath, before);
            resultText = fromFile.Length > 0
                ? fromFile
                : (exitCode == 0
                    ? "Push finished."
                    : $"Push failed (exit {exitCode}). See Diagnostics.");
        }
        catch (Exception ex)
        {
            resultText = $"Couldn't run the push: {ex.Message}";
        }

        PushButton.Content = previous;
        PushButton.IsEnabled = true;

        // Refresh first — it rewrites the state line and the decision list — then have the last
        // word with what the push actually did, so the result isn't clobbered by the refresh.
        _lastSeenWrite = null;
        Refresh();
        SummaryText.Text = resultText;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (IOException) { return DateTime.MinValue; }
    }

    /// <summary>The result line LogPusher wrote, but only if it was written by this run.</summary>
    private static string ResultFileIfFresh(string path, DateTime notNewerThan)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            if (File.GetLastWriteTimeUtc(path) <= notNewerThan) return string.Empty;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            SummaryText.Text = "That folder doesn't exist yet — LogPusher creates it on first run.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Couldn't open it: {ex.Message}";
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_install is null) return;

        var text = new StringBuilder();
        foreach (Decision decision in _install.ReadDecisions(DecisionsShown))
        {
            text.Append(decision.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "?");
            text.Append("  ").Append(decision.Action);
            if (decision.Character is { Length: > 0 }) text.Append("  [").Append(decision.Character).Append(']');
            if (decision.Reason.Length > 0) text.Append("  ").Append(decision.Reason);
            text.AppendLine();
        }

        SummaryText.Text = _host.Clipboard.TrySetText(text.ToString())
            ? "Copied the visible decisions."
            : "Another program is holding the clipboard. Try again.";
    }

    // ---------- refresh ----------

    private void Refresh()
    {
        if (_install is null)
        {
            SetState("Not configured", "Muted");
            SummaryText.Text = "Point at LogPusher.exe to begin.";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            _host.SetStatus(string.Empty);
            return;
        }

        if (!_install.Exists)
        {
            SetState("Not found", "Blood");
            SummaryText.Text = $"Nothing at {_install.ExePath}.";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            _host.SetStatus("exe missing");
            return;
        }

        using Process? running = _install.FindRunning();
        bool isRunning = running is not null;

        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;

        SetState(isRunning ? "Running" : "Stopped", isRunning ? "Tungsten" : "Muted");
        SummaryText.Text = DescribeState(_install.ReadState());
        _host.SetStatus(isRunning ? "running" : "stopped");

        // Worth saying out loud: a tool that quietly stopped archiving sessions is exactly the
        // failure you don't notice until you go looking for a session that was never pushed.
        if (_sawRunning && !isRunning)
            _host.RequestAttention("LogPusher stopped");

        _sawRunning = isRunning;

        RefreshDecisions();
    }

    private static string DescribeState(WatcherState? state)
    {
        if (state is null) return "No state file yet — it hasn't completed a poll.";

        var bits = new List<string>
        {
            $"{state.Sessions} session{(state.Sessions == 1 ? "" : "s")} tracked",
            $"{state.Files} file{(state.Files == 1 ? "" : "s")} watched"
        };

        if (state.ProblemFiles > 0)
            bits.Add($"{state.ProblemFiles} problem file{(state.ProblemFiles == 1 ? "" : "s")}");

        if (state.LastPoll is { } poll)
            bits.Add($"last poll {Ago(poll.LocalDateTime)}");

        return string.Join("  ·  ", bits);
    }

    private void RefreshDecisions()
    {
        if (_install is null) return;

        DateTime? written = _install.LastActivity();

        // The decision log is append-only and only touched when something happened, so its
        // timestamp is a reliable "is there anything new" check. Rebuilding the list on every
        // tick regardless would throw away the user's scroll position for nothing.
        if (written is not null && written == _lastSeenWrite)
        {
            RefreshText.Text = $"last activity {Ago(written.Value)}";
            return;
        }

        _lastSeenWrite = written;

        IReadOnlyList<Decision> decisions = _install.ReadDecisions(DecisionsShown);
        DecisionList.Items.Clear();

        if (decisions.Count == 0)
        {
            DecisionList.Items.Add(new TextBlock
            {
                Text = "Nothing recorded yet.",
                Foreground = (Brush)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap
            });

            RefreshText.Text = string.Empty;
            return;
        }

        // Newest first: the question this panel answers is almost always "what did it just do".
        foreach (Decision decision in decisions.Reverse())
            DecisionList.Items.Add(DecisionRow(decision));

        DecisionScroll.ScrollToTop();
        RefreshText.Text = written is null ? string.Empty : $"last activity {Ago(written.Value)}";
    }

    private UIElement DecisionRow(Decision decision)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var time = new TextBlock
        {
            Text = decision.Timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "",
            Foreground = (Brush)FindResource("Muted"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top
        };

        var action = new TextBlock
        {
            Text = decision.Action,
            Foreground = (Brush)FindResource(AccentFor(decision.Action)),
            FontFamily = new FontFamily("Segoe UI Semibold"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        };

        var detail = new StringBuilder();
        if (decision.Character is { Length: > 0 }) detail.Append('[').Append(decision.Character).Append("]  ");
        detail.Append(decision.Reason);
        if (decision.SourceFile is { Length: > 0 }) detail.Append("  —  ").Append(decision.SourceFile);

        var reason = new TextBlock
        {
            Text = detail.ToString(),
            Foreground = (Brush)FindResource("Bone"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };

        Grid.SetColumn(time, 0);
        Grid.SetColumn(action, 1);
        Grid.SetColumn(reason, 2);
        grid.Children.Add(time);
        grid.Children.Add(action);
        grid.Children.Add(reason);

        return grid;
    }

    /// <summary>A pushed session is the good outcome, a failure is the one to notice.</summary>
    private static string AccentFor(string action) => action switch
    {
        LogPusherInstall.ActionPushed => "Tungsten",
        LogPusherInstall.ActionFailed => "Blood",
        LogPusherInstall.ActionSkipped => "Muted",
        _ => "Clinical"
    };

    private void SetState(string text, string brushKey)
    {
        StateText.Text = text;
        StateDot.Fill = (Brush)FindResource(brushKey);
    }

    private static string Ago(DateTime when)
    {
        TimeSpan elapsed = DateTime.Now - when;

        if (elapsed < TimeSpan.Zero) return when.ToString("HH:mm");
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";

        return when.ToString("d MMM HH:mm");
    }

    /// <summary>Called by the tool on the way out.</summary>
    internal void Shutdown()
    {
        _refresh.Stop();

        // LogPusher is deliberately left running. It is a background service that outlives the
        // dashboard, and closing a window it happens to be displayed in is not a reason to stop
        // archiving someone's session.
    }
}
