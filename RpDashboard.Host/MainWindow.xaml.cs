using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RpDashboard.Contracts;
using RpDashboard.Host.Chat;
using RpDashboard.Host.Hosting;
using RpDashboard.Host.Services;
using Brush = System.Windows.Media.Brush;

namespace RpDashboard.Host;

/// <summary>
/// The shell: loads the plugins, gives each one a tab, and owns the services they share.
///
/// Deliberately thin. Everything a user actually does lives in a tool; what's here is the tab
/// strip, the tray icon, the one status bar, and the wiring that lets three tools share one
/// connection to the game.
/// </summary>
/// <summary>The host's own settings, as opposed to any tool's.</summary>
public sealed class HostSettings
{
    /// <summary>
    /// On by default. The chat log only earns its place by being compared against Afterline's
    /// over several real sessions, and that cannot happen if it has to be remembered each time.
    /// Costs nothing extra either way: the connection is already open for whatever tools are
    /// running, and this only writes what the feed was already reporting.
    /// </summary>
    public bool ChatLogEnabled { get; set; } = true;

    /// <summary>
    /// Where the chat log is written. Empty means the default, which is the only sensible value
    /// on a machine that isn't this developer's: a hardcoded absolute path travels badly, so a
    /// shared build with no host.json lands in <c>%LOCALAPPDATA%\RpDashboard\ChatLogs</c>. This
    /// author's own install carries an override here pointing at the "Chat Parser" folder they
    /// manage by hand; nothing in the UI sets it yet — it is edited in host.json.
    /// </summary>
    public string ChatLogFolder { get; set; } = string.Empty;
}

public partial class MainWindow : Window
{
    private readonly ChatFeed _chat;
    private readonly AudioService _audio;
    private readonly ClipboardService _clipboard;
    private readonly HotkeyService _hotkeys = new();
    private readonly ChatLogWriter _chatLog;
    private readonly SettingsScope _hostSettings = new("RpDashboard", "host.json");
    private readonly HostSettings _hostConfig;
    private readonly List<ToolHost> _hosts = new();
    private readonly Dictionary<ToolHost, TabItem> _tabs = new();

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _settingStartup;
    private PluginScanResult _scan = new(Array.Empty<LoadedTool>(), Array.Empty<PluginProblem>());

    public MainWindow()
    {
        InitializeComponent();

        _chat = new ChatFeed(Dispatcher);
        _audio = new AudioService(Dispatcher);
        _clipboard = new ClipboardService(Dispatcher);

        // Loaded before the writer is built, because it carries where the writer should write.
        _hostConfig = _hostSettings.Load(new HostSettings());

        _chatLog = new ChatLogWriter(_chat, Dispatcher, ChatLogDirectory(_hostConfig.ChatLogFolder));
        _chatLog.Progress += (_, _) => ShowChatLogStatus();
        _chatLog.Failed += (_, message) => ToolStatusText.Text = "Chat log: " + message;

        // Rare, and the log writer is the subscriber it matters to: the tools only ever wanted
        // new lines, but this one is keeping the only copy of the chat that will ever exist.
        _chat.GapSuspected += (_, _) =>
            ToolStatusText.Text = "Chat came in faster than it could be read — some may be missing.";

        _chat.StatusChanged += (_, status) => ShowConnectionStatus(status);
        _audio.PlaybackFailed += (_, failure) =>
            ToolStatusText.Text = $"Sound failed: {Path.GetFileName(failure.FilePath)} — {failure.Message}";

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Hotkeys need the HWND, which doesn't exist until now.
        _hotkeys.Attach(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetUpTrayIcon();
        LoadTools();
        ShowConnectionStatus(_chat.Status);

        SetUpStartupToggle();

        ChatLogBox.IsChecked = _hostConfig.ChatLogEnabled;
        if (_hostConfig.ChatLogEnabled) _chatLog.Start();
        ShowChatLogStatus();
    }

    private void LoadTools()
    {
        string pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
        _scan = PluginLoader.Load(pluginsDirectory);

        foreach (LoadedTool loaded in _scan.Tools.OrderBy(t => t.Tool.Title, StringComparer.OrdinalIgnoreCase))
            AddTool(loaded);

        PluginCountText.Text = _scan.Problems.Count == 0
            ? $"{_hosts.Count} tool{(_hosts.Count == 1 ? "" : "s")}"
            : $"{_hosts.Count} loaded, {_scan.Problems.Count} problem{(_scan.Problems.Count == 1 ? "" : "s")}";

        WriteStartupLog(pluginsDirectory);

        if (_hosts.Count == 0) ShowEmptyState(pluginsDirectory);
    }

    /// <summary>
    /// Records what loaded, to %LOCALAPPDATA%\RpDashboard\last-start.log.
    ///
    /// A missing tab has no other trace. The tools are separate DLLs built from separate
    /// repositories and dropped in by hand, so "it isn't there any more" is a thing that will
    /// happen — a stale build against an older contract, a content folder left behind, a DLL
    /// copied while the app was open. The status bar says how many loaded and clicking it lists
    /// them, but only while the app is running and only if it got that far. This is the file to
    /// read when it didn't.
    ///
    /// Overwritten each start: the interesting question is always about the run you just had.
    /// </summary>
    private void WriteStartupLog(string pluginsDirectory)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RpDashboard");

            Directory.CreateDirectory(directory);

            var text = new System.Text.StringBuilder();
            text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  RP Dashboard, contract API v{HostApi.Version}");
            text.AppendLine($"app       {AppContext.BaseDirectory}");
            text.AppendLine($"plugins   {pluginsDirectory}");
            text.AppendLine();

            if (_scan.Tools.Count == 0) text.AppendLine("No tools loaded.");

            foreach (LoadedTool loaded in _scan.Tools)
                text.AppendLine($"loaded    {loaded.Tool.Title}  v{loaded.AssemblyVersion}  ({loaded.FileName})");

            foreach (PluginProblem problem in _scan.Problems)
                text.AppendLine($"PROBLEM   {problem.FileName}: {problem.Message}");

            File.WriteAllText(Path.Combine(directory, "last-start.log"), text.ToString());
        }
        catch
        {
            // A diagnostic that can't be written is not a reason to fail to start.
        }
    }

    private void AddTool(LoadedTool loaded)
    {
        IRpTool tool = loaded.Tool;

        var host = new ToolHost(
            tool,
            _chat,
            _audio,
            new SettingsScope(tool.SettingsFolderName),
            new HotkeyService.ForTool(_hotkeys, tool.Title),
            _clipboard,
            Dispatcher,
            OnToolStatus,
            OnToolAttention);

        FrameworkElement view;

        try
        {
            tool.Initialise(host);
            view = tool.CreateView();
        }
        catch (Exception ex)
        {
            // One tool failing to start is one broken tab, not a dashboard that won't open.
            // This is the whole reason Initialise is allowed to throw.
            _scan = _scan with
            {
                Problems = _scan.Problems.Append(new PluginProblem(loaded.FileName, ex.Message)).ToArray()
            };
            return;
        }

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = tool.Title,
            Foreground = (Brush)FindResource("Bone"),
            FontFamily = new FontFamily("Segoe UI Semibold"),
            FontSize = 13
        });

        var status = new TextBlock
        {
            Style = (Style)FindResource("Label"),
            FontSize = 10.5,
            Margin = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed
        };
        header.Children.Add(status);

        var tab = new TabItem { Header = header, Content = view, Tag = host };

        _hosts.Add(host);
        _tabs[host] = tab;
        Tabs.Items.Add(tab);

        if (Tabs.Items.Count == 1)
        {
            Tabs.SelectedItem = tab;
            host.IsActiveTab = true;
        }
    }

    private void ShowEmptyState(string pluginsDirectory)
    {
        var message = new TextBlock
        {
            Text = "No tools found.\n\n" +
                   "Drop a tool's DLL into:\n" + pluginsDirectory + "\n\n" +
                   "Each tool is built from its own repository and copied in — the dashboard " +
                   "doesn't need rebuilding when one of them changes.",
            Foreground = (Brush)FindResource("Muted"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 21
        };

        Tabs.Items.Add(new TabItem { Header = "Tools", Content = message });
    }

    private void OnToolStatus(ToolHost host, string status)
    {
        if (!_tabs.TryGetValue(host, out TabItem? tab)) return;
        if (tab.Header is not StackPanel header || header.Children.Count < 2) return;
        if (header.Children[1] is not TextBlock line) return;

        line.Text = status;
        line.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;

        if (host.IsActiveTab) ToolStatusText.Text = status;
    }

    private void OnToolAttention(ToolHost host, string reason)
    {
        if (!_tabs.TryGetValue(host, out TabItem? tab)) return;

        // Nothing to flag if the user is already looking at it.
        if (host.IsActiveTab && IsVisible && WindowState != WindowState.Minimized) return;

        if (tab.Header is StackPanel header && header.Children.Count > 0 &&
            header.Children[0] is TextBlock title)
        {
            title.Foreground = (Brush)FindResource("Tungsten");
        }

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _trayIcon?.ShowBalloonTip(
                4000,
                host.Tool.Title,
                string.IsNullOrWhiteSpace(reason) ? "Something needs a look." : reason,
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;

        foreach (ToolHost host in _hosts)
        {
            bool active = _tabs.TryGetValue(host, out TabItem? tab) &&
                          ReferenceEquals(Tabs.SelectedItem, tab);

            host.IsActiveTab = active;

            if (!active) continue;

            ToolStatusText.Text = host.Status;

            // Clear the attention highlight now that it has been seen.
            if (tab!.Header is StackPanel header && header.Children.Count > 0 &&
                header.Children[0] is TextBlock title)
            {
                title.Foreground = (Brush)FindResource("Bone");
            }
        }
    }

    private void ShowConnectionStatus(ChatFeedStatus status)
    {
        ConnectionText.Text = status.Detail;

        ConnectionDot.Fill = status.State switch
        {
            ChatFeedState.Reading => (Brush)FindResource("Tungsten"),
            ChatFeedState.Waiting => (Brush)FindResource("Blood"),
            _ => (Brush)FindResource("Muted")
        };
    }

    /// <summary>
    /// Where the chat log is written. The default is <c>%LOCALAPPDATA%\RpDashboard\ChatLogs</c> —
    /// portable, so a shared build with no settings lands somewhere sensible on any machine.
    /// <paramref name="configured"/> (from <see cref="HostSettings.ChatLogFolder"/>) overrides it;
    /// this author's install points it at the "Chat Parser" folder they manage by hand.
    ///
    /// A configured path that can't be created — a drive that isn't on this machine — falls back
    /// to the default rather than failing to log at all. That is what makes an install carrying a
    /// personal override still start on someone else's computer.
    /// </summary>
    private static string ChatLogDirectory(string? configured)
    {
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RpDashboard", "ChatLogs");

        if (string.IsNullOrWhiteSpace(configured)) return fallback;

        try
        {
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Reflects the Run key rather than a setting of ours, because the registry is the truth:
    /// the entry can be removed from Task Manager's Startup tab without this app being involved,
    /// and a checkbox that disagreed with that would be worse than none.
    /// </summary>
    private void SetUpStartupToggle()
    {
        _settingStartup = true;
        StartupBox.IsChecked = StartupRegistration.IsEnabled;
        _settingStartup = false;

        // The app was moved or replaced since it was registered, so the entry points at
        // something that may not be there. Quietly repoint it at whatever is running now.
        if (StartupRegistration.IsEnabled && StartupRegistration.IsStale)
        {
            StartupRegistration.Enable();
            ToolStatusText.Text = "Start with Windows now points at this copy of the dashboard.";
        }
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        // Setting IsChecked in code raises this too, and re-writing the registry from inside the
        // handler that is only reflecting it would be a small loop of pointless writes.
        if (_settingStartup) return;

        bool wanted = StartupBox.IsChecked == true;
        bool ok = wanted ? StartupRegistration.Enable() : StartupRegistration.Disable();

        if (!ok)
        {
            ToolStatusText.Text = "Windows wouldn't let that be changed.";
            _settingStartup = true;
            StartupBox.IsChecked = StartupRegistration.IsEnabled;
            _settingStartup = false;
            return;
        }

        ToolStatusText.Text = wanted
            ? "The dashboard will start with Windows, in the tray."
            : "The dashboard will no longer start with Windows.";
    }

    private void OnChatLogChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = ChatLogBox.IsChecked == true;

        if (enabled) _chatLog.Start();
        else _chatLog.Stop();

        // Save through the loaded config so a custom ChatLogFolder isn't wiped by toggling the log.
        _hostConfig.ChatLogEnabled = enabled;
        _hostSettings.Save(_hostConfig);
        ShowChatLogStatus();
    }

    private void ShowChatLogStatus()
        => ChatLogText.Text = _chatLog.IsRunning
            ? $"log {_chatLog.LinesWritten:N0}"
            : string.Empty;

    private void OnChatLogClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        string directory = ChatLogDirectory(_hostConfig.ChatLogFolder);

        if (!Directory.Exists(directory))
        {
            ToolStatusText.Text = "Nothing written yet — the folder appears with the first line.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = "Couldn't open it: " + ex.Message;
        }
    }

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        bool muted = MuteBox.IsChecked == true;
        _audio.Volume = muted ? 0.0 : 1.0;
        if (muted) _audio.StopAll();
    }

    private void OnPluginCountClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"Contract API v{HostApi.Version}");
        text.AppendLine();

        foreach (LoadedTool loaded in _scan.Tools)
            text.AppendLine($"{loaded.Tool.Title}  {loaded.AssemblyVersion}  ({loaded.FileName})");

        if (_scan.Problems.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Not loaded:");
            foreach (PluginProblem problem in _scan.Problems)
                text.AppendLine($"  {problem.FileName}: {problem.Message}");
        }

        MessageBox.Show(this, text.ToString(), "Tools", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetUpTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            // Reuses the icon already embedded in the exe's Win32 resources rather than
            // shipping a second copy of the file — the arrangement Unit Alert settled on.
            Icon = LoadAppIcon(),
            Text = "RP Dashboard",
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // Falls through to the stock icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>
    /// Also what a second launch of the app turns into: the app-level single-instance guard
    /// hands off here rather than opening another window.
    /// </summary>
    internal void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Activate() is only honoured when this process holds the foreground right, and a
        // second launch started from anything other than the shell cannot pass it on — Windows
        // then flashes the taskbar button and leaves the window behind whatever is in front.
        // A pulse through Topmost is honoured regardless. Skipped when a tool has genuinely
        // pinned the window on top, so as not to unpin it.
        if (!Topmost)
        {
            Topmost = true;
            Topmost = false;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Minimising hides to tray; closing exits. Same rule Unit Alert shipped, and the tools
        // depend on it — they keep polling while hidden, which is the point of the tray icon.
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (ToolHost host in _hosts)
        {
            // One tool throwing on the way out must not strand the others, or leave the tray
            // icon behind after the window is gone.
            try { host.Tool.Shutdown(); } catch { /* going away regardless */ }
        }

        // Before the feed goes: this drains whatever is queued, so the last couple of seconds
        // of a session are not lost every time the dashboard closes.
        _chatLog.Dispose();

        _hotkeys.Dispose();
        _audio.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        // Fire and forget: the feed's shutdown closes a socket to a process that may already be
        // gone, and blocking the UI thread on it would hang the close.
        _ = _chat.DisposeAsync();

        Application.Current.Shutdown();
    }
}
