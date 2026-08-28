namespace RpDashboard.Contracts;

/// <summary>
/// Clipboard writes, with retry.
///
/// The clipboard is a shared Win32 resource and another process holding it open makes
/// <c>Clipboard.SetText</c> throw, which is common enough with a game running that Street
/// Medic already wrapped this. Promoted to a host service so every tool gets the retry.
///
/// This is the only path a tool has that reaches outside the app, and it is deliberately
/// one-way and user-driven: the tool puts text on the clipboard, the human pastes it into
/// the game. Nothing here writes to FiveM.
/// </summary>
public interface IClipboardService
{
    /// <summary>True if the text landed. False after the retries are exhausted — say so in the UI.</summary>
    bool TrySetText(string text);
}
