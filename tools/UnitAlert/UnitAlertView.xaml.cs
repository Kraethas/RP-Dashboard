using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RpDashboard.Contracts;
using Brush = System.Windows.Media.Brush;

namespace UnitAlert;

/// <summary>
/// The Unit Alert tab.
///
/// This is the standalone MainWindow with four things taken out of it — the tray icon, the
/// dark-title-bar call, the window's own settings file, and its private connection to the game
/// — because the host owns all four now. What's left is the same UI doing the same job.
///
/// Activate/Deactivate is now a subscription to the shared feed rather than starting a poll
/// loop. That matters more than it looks: while this tab is deactivated it costs the game
/// nothing at all, and if it is the only active tool, deactivating it closes the connection.
/// </summary>
public partial class UnitAlertView : UserControl
{
    private sealed class CallsignRow
    {
        public required UIElement Container { get; init; }
        public required TextBox PatternBox { get; init; }
        public required Button SoundButton { get; init; }
        public string SoundFile { get; set; } = string.Empty;
    }

    private const int MaxRecentHits = 40;

    private readonly IToolHost _host;
    private readonly AlertMatcher _matcher = new();
    private readonly List<CallsignRow> _callsignRows = new();

    private AppSettings _settings;
    private IChatSubscription? _subscription;
    private bool _isLoadingSettings;

    public UnitAlertView(IToolHost host)
    {
        _host = host;
        InitializeComponent();

        _settings = _host.Settings.Load(new AppSettings());
        LoadSettingsIntoUi();

        _matcher.Matched += OnMatched;
        _matcher.PatternWarning += message => SetStatus(message, StatusTone.Error);

        _host.Chat.StatusChanged += OnFeedStatusChanged;

        UpdateActivateEnabled();
    }

    private bool IsListening => _subscription is not null;

    // ---------- settings load ----------

    private void LoadSettingsIntoUi()
    {
        // Programmatically setting a CheckBox's IsChecked still raises Checked/Unchecked, which
        // would otherwise call PersistSettings() mid-load and stomp whatever hasn't been
        // assigned yet — notably the callsign rows, which are added last. Guard against it.
        // Any new checkbox wired to a persist-on-change handler has to go inside this block.
        _isLoadingSettings = true;
        try
        {
            DefaultSoundFileBox.Text = _settings.DefaultSoundFile;
            AfkToggle.IsChecked = _settings.AfkEnabled;
            AfkSoundFileBox.Text = _settings.AfkSoundFile;
            WholeWordCheckBox.IsChecked = _settings.WholeWordMatching;
            RegexModeCheckBox.IsChecked = _settings.RegexMode;
            WholeWordCheckBox.IsEnabled = !_settings.RegexMode;

            if (_settings.Callsigns.Count == 0)
                AddCallsignRow(string.Empty, string.Empty);
            else
                foreach (CallsignEntry entry in _settings.Callsigns)
                    AddCallsignRow(entry.Pattern, entry.SoundFile);
        }
        finally
        {
            _isLoadingSettings = false;
        }

        if (File.Exists(_settings.DefaultSoundFile)) _host.Audio.Preload(_settings.DefaultSoundFile);
        if (File.Exists(_settings.AfkSoundFile)) _host.Audio.Preload(_settings.AfkSoundFile);
        foreach (CallsignEntry entry in _settings.Callsigns)
            if (File.Exists(entry.SoundFile)) _host.Audio.Preload(entry.SoundFile);
    }

    // ---------- callsign rows ----------

    private void AddCallsignRow(string pattern, string soundFile)
    {
        var patternBox = new TextBox
        {
            Text = pattern,
            ToolTip = "Text (or regex, in regex mode) to listen for.",
            VerticalAlignment = VerticalAlignment.Center
        };

        var soundButton = new Button
        {
            Content = string.IsNullOrWhiteSpace(soundFile) ? "Dispatch" : Path.GetFileName(soundFile),
            Width = 92,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Click to give this callsign its own sound. Right-click to use the default sound again."
        };

        var testButton = new Button
        {
            Content = "Test",
            Width = 50,
            Margin = new Thickness(6, 0, 0, 0)
        };

        var removeButton = new Button
        {
            Content = "✕",
            Width = 26,
            Margin = new Thickness(6, 0, 0, 0)
        };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(patternBox, 0);
        Grid.SetColumn(soundButton, 1);
        Grid.SetColumn(testButton, 2);
        Grid.SetColumn(removeButton, 3);
        grid.Children.Add(patternBox);
        grid.Children.Add(soundButton);
        grid.Children.Add(testButton);
        grid.Children.Add(removeButton);

        var row = new CallsignRow
        {
            Container = grid,
            PatternBox = patternBox,
            SoundButton = soundButton,
            SoundFile = soundFile
        };

        var resetMenuItem = new MenuItem { Header = "Use default sound" };
        resetMenuItem.Click += (_, _) =>
        {
            row.SoundFile = string.Empty;
            row.SoundButton.Content = "Dispatch";
            PersistSettings();
        };
        soundButton.ContextMenu = new ContextMenu();
        soundButton.ContextMenu.Items.Add(resetMenuItem);

        soundButton.Click += (_, _) => OnRowSoundButtonClick(row);
        testButton.Click += (_, _) =>
            PlayTestSound(string.IsNullOrWhiteSpace(row.SoundFile) ? DefaultSoundFileBox.Text : row.SoundFile);
        removeButton.Click += (_, _) => OnRowRemoveButtonClick(row);
        patternBox.TextChanged += (_, _) => UpdateActivateEnabled();

        _callsignRows.Add(row);
        CallsignsPanel.Children.Add(grid);
    }

    private void OnRowSoundButtonClick(CallsignRow row)
    {
        string? path = PickSoundFile(row.SoundFile);
        if (path is null) return;

        row.SoundFile = path;
        row.SoundButton.Content = Path.GetFileName(path);
        _host.Audio.Play(path);
        PersistSettings();
    }

    private void OnRowRemoveButtonClick(CallsignRow row)
    {
        CallsignsPanel.Children.Remove(row.Container);
        _callsignRows.Remove(row);
        if (_callsignRows.Count == 0) AddCallsignRow(string.Empty, string.Empty);

        PersistSettings();
        UpdateActivateEnabled();
    }

    private void AddCallsignButton_Click(object sender, RoutedEventArgs e)
        => AddCallsignRow(string.Empty, string.Empty);

    // ---------- other commands ----------

    private void DefaultSoundBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = PickSoundFile(DefaultSoundFileBox.Text);
        if (path is null) return;

        DefaultSoundFileBox.Text = path;
        _host.Audio.Play(path);
        PersistSettings();
        UpdateActivateEnabled();
    }

    private void AfkBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = PickSoundFile(AfkSoundFileBox.Text);
        if (path is null) return;

        AfkSoundFileBox.Text = path;
        _host.Audio.Play(path);
        PersistSettings();
        UpdateActivateEnabled();
    }

    private void AfkToggle_Changed(object sender, RoutedEventArgs e)
    {
        PersistSettings();
        UpdateActivateEnabled();
    }

    private void DefaultSoundTestButton_Click(object sender, RoutedEventArgs e)
        => PlayTestSound(DefaultSoundFileBox.Text);

    private void AfkSoundTestButton_Click(object sender, RoutedEventArgs e)
        => PlayTestSound(AfkSoundFileBox.Text);

    private void PlayTestSound(string path)
    {
        if (!File.Exists(path))
        {
            SetStatus("That sound file isn't there anymore. Pick another.", StatusTone.Error);
            return;
        }

        _host.Audio.Play(path);
    }

    private void MatchOption_Changed(object sender, RoutedEventArgs e) => PersistSettings();

    private void RegexModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool regexMode = RegexModeCheckBox.IsChecked == true;
        WholeWordCheckBox.IsEnabled = !regexMode;
        PersistSettings();
    }

    private string? PickSoundFile(string currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a sound",
            Filter = "Audio files|*.wav;*.mp3;*.wma;*.m4a;*.aac;*.ogg;*.flac|All files|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string? folder = Path.GetDirectoryName(currentPath);
            if (folder is not null && Directory.Exists(folder))
                dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    // ---------- activation ----------

    private void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsListening)
        {
            Deactivate();
            return;
        }

        CallsignRule[] rules = _callsignRows
            .Where(row => !string.IsNullOrWhiteSpace(row.PatternBox.Text))
            .Select(row => new CallsignRule(row.PatternBox.Text.Trim(), row.SoundFile))
            .ToArray();

        string defaultSound = DefaultSoundFileBox.Text;
        bool afkEnabled = AfkToggle.IsChecked == true && File.Exists(AfkSoundFileBox.Text);
        string afkSound = AfkSoundFileBox.Text;

        bool hasUsableCallsign = rules.Any(rule =>
            File.Exists(string.IsNullOrWhiteSpace(rule.SoundFile) ? defaultSound : rule.SoundFile));

        if (!hasUsableCallsign && !afkEnabled)
        {
            SetStatus("Add a callsign with a sound, or enable AFK alerts with a sound.", StatusTone.Error);
            return;
        }

        if (File.Exists(defaultSound)) _host.Audio.Preload(defaultSound);
        if (afkEnabled) _host.Audio.Preload(afkSound);
        foreach (CallsignRule rule in rules)
            if (!string.IsNullOrWhiteSpace(rule.SoundFile) && File.Exists(rule.SoundFile))
                _host.Audio.Preload(rule.SoundFile);

        PersistSettings();

        _matcher.Configure(new MatcherOptions
        {
            Callsigns = rules,
            DefaultSoundFile = defaultSound,
            WholeWord = WholeWordCheckBox.IsChecked == true,
            RegexMode = RegexModeCheckBox.IsChecked == true,
            AfkEnabled = afkEnabled,
            AfkSoundFile = afkSound
        });

        _subscription = _host.Chat.Subscribe(new ChatSubscriptionOptions
        {
            Name = "Unit Alert",

            // Half a second, as the standalone app polled. A hail that arrives half a second
            // late is still a hail; asking for less would make the game work harder for nothing.
            PreferredInterval = TimeSpan.FromMilliseconds(500)
        });

        _subscription.LineReceived += OnLineReceived;

        ActivateButton.Content = "Deactivate";
        SettingsPanel.IsEnabled = false;
        OnFeedStatusChanged(this, _host.Chat.Status);
    }

    private void Deactivate()
    {
        if (_subscription is not null)
        {
            _subscription.LineReceived -= OnLineReceived;
            _subscription.Dispose();
            _subscription = null;
        }

        ActivateButton.Content = "Activate";
        SettingsPanel.IsEnabled = true;
        SetStatus("Inactive", StatusTone.Muted);
        _host.SetStatus(string.Empty);
    }

    // ---------- feed callbacks (already on the UI thread) ----------

    private void OnLineReceived(object? sender, ChatLine line) => _matcher.Evaluate(line.Text);

    private void OnFeedStatusChanged(object? sender, ChatFeedStatus status)
    {
        if (!IsListening) return;

        switch (status.State)
        {
            case ChatFeedState.Reading:
                SetStatus("Listening.", StatusTone.Active);
                break;
            case ChatFeedState.Waiting:
                SetStatus(status.Detail, StatusTone.Muted);
                break;
            default:
                SetStatus("Inactive", StatusTone.Muted);
                break;
        }
    }

    private void OnMatched(AlertHit hit)
    {
        _host.Audio.Play(hit.SoundFile);
        AddHit(hit);

        string preview = hit.Line.Length > 60 ? hit.Line[..57] + "..." : hit.Line;
        SetStatus($"{hit.TimestampUtc.ToLocalTime():HH:mm:ss}  {preview}", StatusTone.Alert);

        // The sound always plays; this is the visual half, and it only does anything when the
        // user is looking at another tab or the window is in the tray.
        _host.RequestAttention(hit.Kind == HitKind.Afk ? "AFK check" : hit.MatchedText);
    }

    private void AddHit(AlertHit hit)
    {
        string tag = hit.Kind == HitKind.Afk ? "AFK CHECK" : hit.MatchedText;
        string preview = hit.Line.Length > 80 ? hit.Line[..77] + "..." : hit.Line;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = hit.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
            Foreground = (Brush)FindResource("MutedText"),
            FontSize = 11,
            Width = 54,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = tag,
            Foreground = (Brush)FindResource(hit.Kind == HitKind.Afk ? "AfkAccent" : "ActiveAccent"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Width = 90,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = preview,
            Foreground = (Brush)FindResource("PrimaryText"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        RecentHitsList.Items.Insert(0, panel);
        while (RecentHitsList.Items.Count > MaxRecentHits)
            RecentHitsList.Items.RemoveAt(RecentHitsList.Items.Count - 1);
    }

    // ---------- helpers ----------

    private bool CanActivate()
    {
        bool hasDefaultSound = File.Exists(DefaultSoundFileBox.Text);
        bool hasValidCallsign = _callsignRows.Any(row =>
            !string.IsNullOrWhiteSpace(row.PatternBox.Text) &&
            (File.Exists(row.SoundFile) || hasDefaultSound));
        bool afkValid = AfkToggle.IsChecked == true && File.Exists(AfkSoundFileBox.Text);
        return hasValidCallsign || afkValid;
    }

    private void UpdateActivateEnabled()
    {
        if (IsListening) return;
        ActivateButton.IsEnabled = CanActivate();
    }

    private enum StatusTone { Muted, Active, Alert, Error }

    private void SetStatus(string message, StatusTone tone)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(tone switch
        {
            StatusTone.Active => "ActiveAccent",
            StatusTone.Alert => "AlertAccent",
            StatusTone.Error => "ErrorAccent",
            _ => "MutedText"
        });

        // Also under the tab header, so the state is readable while another tab is on screen.
        _host.SetStatus(message);
    }

    private void PersistSettings()
    {
        if (_isLoadingSettings) return;

        _settings = new AppSettings
        {
            DefaultSoundFile = DefaultSoundFileBox.Text,
            AfkEnabled = AfkToggle.IsChecked == true,
            AfkSoundFile = AfkSoundFileBox.Text,
            WholeWordMatching = WholeWordCheckBox.IsChecked == true,
            RegexMode = RegexModeCheckBox.IsChecked == true,
            Callsigns = _callsignRows
                .Select(row => new CallsignEntry { Pattern = row.PatternBox.Text.Trim(), SoundFile = row.SoundFile })
                .ToList()
        };

        _host.Settings.Save(_settings);
    }

    /// <summary>Called by the tool on the way out.</summary>
    internal void Shutdown()
    {
        PersistSettings();
        _host.Chat.StatusChanged -= OnFeedStatusChanged;

        if (_subscription is null) return;

        _subscription.LineReceived -= OnLineReceived;
        _subscription.Dispose();
        _subscription = null;
    }
}
