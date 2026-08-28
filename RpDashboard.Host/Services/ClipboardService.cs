using System.Windows;
using System.Windows.Threading;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Services;

/// <summary>
/// Clipboard writes with retry, promoted from Street Medic's <c>Clip</c> helper.
///
/// The clipboard is a single Win32 resource that any process can hold open, and with a game
/// running something usually is, so <c>Clipboard.SetText</c> throwing is routine rather than
/// exceptional. Retrying a few times over a few hundred milliseconds clears it in practice.
///
/// This is the only path a tool has that reaches outside the app, and it is one-way by design:
/// the tool puts text on the clipboard and the human pastes it into the game. Nothing here
/// writes to FiveM, which is the invariant all three tools shipped with.
/// </summary>
internal sealed class ClipboardService : IClipboardService
{
    private const int Attempts = 5;
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(60);

    private readonly Dispatcher _dispatcher;

    public ClipboardService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool TrySetText(string text)
    {
        if (text is null) return false;

        return _dispatcher.CheckAccess()
            ? SetCore(text)
            : _dispatcher.Invoke(() => SetCore(text));
    }

    private static bool SetCore(string text)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                // The copy=true overload puts the data on the clipboard in a form that survives
                // this process exiting. Without it, closing the dashboard right after copying
                // an emote leaves the clipboard empty.
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch
            {
                if (attempt == Attempts - 1) return false;

                // A blocking sleep on the UI thread, deliberately: the alternative is an async
                // API the tools would have to await from click handlers, and the total worst
                // case here is under a third of a second.
                Thread.Sleep(Backoff);
            }
        }

        return false;
    }
}
