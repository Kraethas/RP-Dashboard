using System.IO;
using Microsoft.Win32;

namespace RpDashboard.Host.Services;

/// <summary>
/// "Start with Windows", via the per-user Run key.
///
/// This is the same mechanism Afterline uses, and it matters more here than it looks: FiveM
/// never writes chat to disk, so the dashboard's log only covers the time the dashboard is open.
/// Three days of comparison showed the reader misses nothing while it is running — every line
/// Afterline had that we didn't came from a stretch where the dashboard simply wasn't up. This
/// toggle is the fix for that, and the last thing standing between running both loggers and
/// running one.
///
/// HKCU, not HKLM: no administrator rights, and it only ever affects the user who set it.
/// Turning it off removes the value rather than leaving a disabled one behind.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The name the entry appears under in Task Manager's Startup tab.</summary>
    private const string ValueName = "RP Dashboard";

    /// <summary>
    /// Started by Windows rather than by hand, so it goes straight to the tray. Being logged in
    /// should not mean a window in your face; the point is only that the log is running.
    /// </summary>
    public const string TrayArgument = "--tray";

    public static bool IsEnabled => Read() is not null;

    /// <summary>Where the registered entry points, or null if there isn't one.</summary>
    public static string? RegisteredCommand => Read();

    /// <summary>
    /// True when an entry exists but points somewhere other than this executable — the usual
    /// cause being that the app was moved or replaced by a different build.
    /// </summary>
    public static bool IsStale
    {
        get
        {
            string? registered = Read();
            return registered is not null && !string.Equals(registered, Command(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool Enable()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, Command(), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Quoted, because the path has spaces in it and Windows will not guess.</summary>
    private static string Command()
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        return $"\"{exe}\" {TrayArgument}";
    }
}
