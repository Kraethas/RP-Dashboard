using System.Windows.Threading;
using RpDashboard.Contracts;
using RpDashboard.Host.Services;

namespace RpDashboard.Host.Hosting;

/// <summary>
/// One tool's view of the host: shared services passed straight through, per-tool services
/// (settings, hotkey attribution, status, attention) scoped to it.
///
/// One of these exists per loaded tool, for the life of the process.
/// </summary>
internal sealed class ToolHost : IToolHost
{
    private readonly Action<ToolHost, string> _onStatus;
    private readonly Action<ToolHost, string> _onAttention;
    private bool _isActiveTab;

    public ToolHost(
        IRpTool tool,
        IChatFeed chat,
        IAudioService audio,
        ISettingsScope settings,
        IHotkeyService hotkeys,
        IClipboardService clipboard,
        Dispatcher dispatcher,
        Action<ToolHost, string> onStatus,
        Action<ToolHost, string> onAttention)
    {
        Tool = tool;
        Chat = chat;
        Audio = audio;
        Settings = settings;
        Hotkeys = hotkeys;
        Clipboard = clipboard;
        Dispatcher = dispatcher;
        _onStatus = onStatus;
        _onAttention = onAttention;
    }

    public IRpTool Tool { get; }
    public IChatFeed Chat { get; }
    public IAudioService Audio { get; }
    public ISettingsScope Settings { get; }
    public IHotkeyService Hotkeys { get; }
    public IClipboardService Clipboard { get; }
    public Dispatcher Dispatcher { get; }

    /// <summary>Last status this tool reported, for the tab header.</summary>
    public string Status { get; private set; } = string.Empty;

    public bool IsActiveTab
    {
        get => _isActiveTab;
        internal set
        {
            if (_isActiveTab == value) return;
            _isActiveTab = value;
            ActiveTabChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? ActiveTabChanged;

    public void SetStatus(string status)
    {
        string value = status ?? string.Empty;

        // Called from poll loops as often as once per line, so it marshals rather than making
        // every tool remember to.
        if (Dispatcher.CheckAccess()) Apply(value);
        else Dispatcher.BeginInvoke(() => Apply(value));

        void Apply(string s)
        {
            if (Status == s) return;
            Status = s;
            _onStatus(this, s);
        }
    }

    public void RequestAttention(string reason)
    {
        if (Dispatcher.CheckAccess()) _onAttention(this, reason ?? string.Empty);
        else Dispatcher.BeginInvoke(() => _onAttention(this, reason ?? string.Empty));
    }
}
