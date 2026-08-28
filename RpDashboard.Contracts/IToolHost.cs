using System.Windows.Threading;

namespace RpDashboard.Contracts;

/// <summary>
/// Everything the host lends a tool. A tool gets one of these when its view is created and
/// should hold onto it; the services behind it live for the life of the process.
/// </summary>
public interface IToolHost
{
    /// <summary>The one connection to the game's chat, shared by every tool.</summary>
    IChatFeed Chat { get; }

    /// <summary>Sound playback. Shared so two tools' alerts overlap instead of cutting each other off.</summary>
    IAudioService Audio { get; }

    /// <summary>This tool's own settings, already namespaced — no other tool can see them.</summary>
    ISettingsScope Settings { get; }

    /// <summary>
    /// Global hotkey registration. There is one window now, so registration is host-wide and
    /// the first tool to claim a chord gets it.
    /// </summary>
    IHotkeyService Hotkeys { get; }

    /// <summary>Clipboard writes, with the retry Street Medic needed. The only outward path a tool has.</summary>
    IClipboardService Clipboard { get; }

    /// <summary>The UI thread. Feed and audio callbacks already arrive on it; background work doesn't.</summary>
    Dispatcher Dispatcher { get; }

    /// <summary>True when this tool's tab is the one on screen.</summary>
    bool IsActiveTab { get; }

    /// <summary>Raised when this tool's tab is shown or hidden.</summary>
    event EventHandler<bool>? ActiveTabChanged;

    /// <summary>
    /// Short line shown under this tool's tab header — "reading chat", "4:15 idle", "waiting for FiveM".
    /// Safe to call from any thread.
    /// </summary>
    void SetStatus(string status);

    /// <summary>
    /// Something happened that the user should look at while a different tab is on screen:
    /// highlights this tool's tab, and shows a tray balloon if the window is hidden. Alerts
    /// still play their sound regardless — this is the visual half.
    /// </summary>
    void RequestAttention(string reason);
}
