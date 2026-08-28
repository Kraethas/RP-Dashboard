using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Services;

/// <summary>
/// System-wide hotkeys for every tool, registered against the host window.
///
/// This has to be host-owned now, and it is the one service the merge genuinely changes the
/// behaviour of. <c>RegisterHotKey</c> binds a chord to a window handle, and where there were
/// three windows there is now one — so two tools that each happily claimed Ctrl+Alt+1 in
/// their own process are now in direct conflict. The resolution is deliberately blunt and
/// visible: first claim wins, the second is told no and degrades, and nothing is swallowed.
///
/// Hotkeys fire regardless of which tab is on screen or whether the window is visible at all.
/// That is the point of them — Street Medic's copy-emote chords exist to be used with the game
/// focused, which is exactly when the dashboard isn't.
/// </summary>
internal sealed class HotkeyService : IHotkeyService, IDisposable
{
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [Flags]
    private enum HotkeyModifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,

        /// <summary>Stops the callback firing repeatedly while the key is held down.</summary>
        NoRepeat = 0x4000
    }

    private readonly Dictionary<int, Registration> _byId = new();
    private readonly Dictionary<(ModifierKeys, Key), Registration> _byChord = new();

    private HwndSource? _source;
    private IntPtr _handle = IntPtr.Zero;
    private int _nextId = 1;

    /// <summary>
    /// Must be called once the host window has a handle — after <c>SourceInitialized</c>, not in
    /// the constructor. A window has no HWND before then and every registration would fail.
    /// </summary>
    public void Attach(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
    }

    public IHotkeyRegistration? TryRegister(ModifierKeys modifiers, Key key, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (_handle == IntPtr.Zero) return null;
        if (_byChord.ContainsKey((modifiers, key))) return null;

        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (!RegisterHotKey(_handle, id, (uint)(Translate(modifiers) | HotkeyModifiers.NoRepeat), vk))
        {
            // Something outside the app holds it — another program, or Windows itself. The
            // caller degrades; there is nothing useful to throw about.
            return null;
        }

        var registration = new Registration(this, id, modifiers, key, callback);
        _byId[id] = registration;
        _byChord[(modifiers, key)] = registration;
        return registration;
    }

    public string? HolderOf(ModifierKeys modifiers, Key key)
        => _byChord.TryGetValue((modifiers, key), out Registration? existing) ? existing.Owner : null;

    private static HotkeyModifiers Translate(ModifierKeys modifiers)
    {
        HotkeyModifiers result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        if (!_byId.TryGetValue(wParam.ToInt32(), out Registration? registration)) return IntPtr.Zero;

        handled = true;

        // Already on the UI thread — this is the window's message pump. One tool throwing from
        // a hotkey handler must not take the pump down with it.
        try { registration.Callback(); } catch { /* the tool's problem */ }

        return IntPtr.Zero;
    }

    private void Release(Registration registration)
    {
        if (_handle != IntPtr.Zero) UnregisterHotKey(_handle, registration.Id);
        _byId.Remove(registration.Id);
        _byChord.Remove((registration.Modifiers, registration.Key));
    }

    public void Dispose()
    {
        foreach (Registration registration in _byId.Values.ToArray())
        {
            if (_handle != IntPtr.Zero) UnregisterHotKey(_handle, registration.Id);
        }

        _byId.Clear();
        _byChord.Clear();

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private sealed class Registration : IHotkeyRegistration
    {
        private readonly HotkeyService _service;
        private bool _disposed;

        public Registration(HotkeyService service, int id, ModifierKeys modifiers, Key key, Action callback)
        {
            _service = service;
            Id = id;
            Modifiers = modifiers;
            Key = key;
            Callback = callback;
        }

        public int Id { get; }
        public ModifierKeys Modifiers { get; }
        public Key Key { get; }
        public Action Callback { get; }

        /// <summary>Set by the host once it knows which tool asked, for the conflict list.</summary>
        public string Owner { get; set; } = "a tool";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _service.Release(this);
        }
    }

    /// <summary>
    /// Wraps this service for one tool, so the host can record who claimed what without every
    /// tool having to pass its own name in on each call.
    /// </summary>
    internal sealed class ForTool : IHotkeyService
    {
        private readonly HotkeyService _inner;
        private readonly string _toolTitle;

        public ForTool(HotkeyService inner, string toolTitle)
        {
            _inner = inner;
            _toolTitle = toolTitle;
        }

        public IHotkeyRegistration? TryRegister(ModifierKeys modifiers, Key key, Action callback)
        {
            IHotkeyRegistration? registration = _inner.TryRegister(modifiers, key, callback);
            if (registration is Registration owned) owned.Owner = _toolTitle;
            return registration;
        }

        public string? HolderOf(ModifierKeys modifiers, Key key) => _inner.HolderOf(modifiers, key);
    }
}
