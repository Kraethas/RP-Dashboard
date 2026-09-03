using System.Runtime.InteropServices;
using System.Threading;

namespace RpDashboard.Host.Services;

/// <summary>
/// One dashboard per login session. A second launch hands off to the first and exits.
///
/// This is a safety rule, not a nicety. The whole point of the host is a single connection to
/// the game, and the chat log writer is keeping the only copy of the chat that will ever exist:
/// two instances mean two sockets into the HUD and two writers appending to the same file —
/// which is precisely the duplication Afterline had and this app exists to remove. The easiest
/// way to get there by accident is the one this guards against: the copy Windows started at
/// login is sitting in the tray, invisible, and the user double-clicks the shortcut.
///
/// The name is deliberately not tied to the executable's path. A Debug build and the shipped
/// package are still two dashboards over one game.
///
/// Two kernel objects: a mutex to decide who is first, and an event the loser pulses so the
/// winner can bring its window forward. The event is what makes this work from the tray — a
/// hidden window has nothing for FindWindow/SetForegroundWindow to act on, so the running
/// instance shows itself rather than being shown.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "RpDashboard";

    private readonly string _name;
    private Mutex? _mutex;
    private EventWaitHandle? _showRequested;
    private RegisteredWaitHandle? _watch;

    /// <param name="name">
    /// Overridable so the self-test can exercise the real kernel objects without poking the
    /// dashboard that is probably running while it does.
    /// </param>
    public SingleInstance(string name = DefaultName)
    {
        _name = name;
    }

    /// <summary>Raised, on a threadpool thread, when another launch asked this one to show itself.</summary>
    public event Action? ShowRequested;

    /// <summary>
    /// True when this is the first instance, which now holds the claim until disposed. False
    /// when another instance already has it — in which case it has been asked to come forward,
    /// and the caller should exit.
    /// </summary>
    public bool TryClaim()
    {
        // "Local\" scopes it to this login session. The dashboard reads one user's game and
        // writes one user's log; another user's session on the same machine is a different
        // dashboard, and a Global name would need rights this app has no reason to ask for.
        _showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + _name + ".Show");
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + _name + ".Running", out bool createdNew);

        if (!createdNew)
        {
            // We are the process the user just launched, so we hold the right to change the
            // foreground window and the running instance does not; without this its Activate()
            // is refused and Windows just flashes the taskbar button. Hand the right over
            // before signalling, and the handoff looks like the window simply appeared.
            AllowSetForegroundWindow(AsfwAny);
            _showRequested.Set();

            // Not ours to hold. Dispose closes the handle without releasing an ownership this
            // thread never acquired (createdNew false means the object existed; ownership was
            // not granted because someone else holds it).
            _mutex.Dispose();
            _mutex = null;
            _showRequested.Dispose();
            _showRequested = null;
            return false;
        }

        // A single registered wait rather than a thread of our own: it costs nothing while
        // idle, which is the whole life of this object.
        _watch = ThreadPool.RegisterWaitForSingleObject(
            _showRequested,
            (_, _) => ShowRequested?.Invoke(),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);

        return true;
    }

    public void Dispose()
    {
        _watch?.Unregister(null);
        _watch = null;

        if (_mutex is not null)
        {
            // Only the owning thread may release, and Dispose alone would do it at process
            // exit anyway; this just makes an explicit release the normal path.
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not the owner */ }
            _mutex.Dispose();
            _mutex = null;
        }

        _showRequested?.Dispose();
        _showRequested = null;
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
