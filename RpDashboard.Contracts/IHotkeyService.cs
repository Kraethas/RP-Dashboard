using System.Windows.Input;

namespace RpDashboard.Contracts;

/// <summary>
/// System-wide hotkeys, registered against the host window.
///
/// This has to be host-owned now. Win32 hotkey registration is per-HWND and there is one
/// window, so two tools that both wanted Ctrl+Alt+1 as separate processes are now in direct
/// conflict. The host resolves it the simple way: first claim wins, the loser is told, and
/// nothing is silently swallowed.
/// </summary>
public interface IHotkeyService
{
    /// <summary>
    /// Claims a chord for as long as the returned handle lives. Fires on the UI thread,
    /// whether or not the claiming tool's tab is on screen or the window is even visible —
    /// Street Medic's copy-emote chords have to work with the game focused, which is the
    /// entire point of them.
    /// </summary>
    /// <returns>
    /// The handle, or null if another tool already holds the chord or Windows refused it
    /// (something outside the app has it). Callers should degrade rather than throw — a
    /// hotkey that couldn't be claimed is a missing convenience, not a broken tool.
    /// </returns>
    IHotkeyRegistration? TryRegister(ModifierKeys modifiers, Key key, Action callback);

    /// <summary>Who currently holds a chord, for the host's hotkey conflict list. Null if free.</summary>
    string? HolderOf(ModifierKeys modifiers, Key key);
}

public interface IHotkeyRegistration : IDisposable
{
    ModifierKeys Modifiers { get; }
    Key Key { get; }
}
