namespace UnitAlert;

/// <summary>
/// A single callsign pattern and the sound it plays. An empty <see cref="SoundFile"/> falls
/// back to the default sound.
/// </summary>
public sealed class CallsignEntry
{
    public string Pattern { get; set; } = string.Empty;
    public string SoundFile { get; set; } = string.Empty;
}

/// <summary>
/// The on-disk settings shape, unchanged from the standalone app.
///
/// It has to stay unchanged: the plugin reads the same
/// <c>%LOCALAPPDATA%\UnitAlert\settings.json</c> the exe wrote, so anyone who already had Unit
/// Alert set up keeps their callsigns and sounds when they switch to the dashboard. Adding a
/// property is fine — unknown properties on the way in are ignored and missing ones take their
/// defaults — but renaming or retyping one silently drops that setting on first load.
///
/// The <c>SettingsStore</c> that used to sit beside this is gone; the host supplies
/// <see cref="RpDashboard.Contracts.ISettingsScope"/>, which does the same job for every tool.
/// </summary>
public sealed class AppSettings
{
    public string DefaultSoundFile { get; set; } = string.Empty;
    public bool AfkEnabled { get; set; }
    public string AfkSoundFile { get; set; } = string.Empty;
    public bool WholeWordMatching { get; set; }
    public bool RegexMode { get; set; }
    public List<CallsignEntry> Callsigns { get; set; } = new();
}
