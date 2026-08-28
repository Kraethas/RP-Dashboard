namespace RpDashboard.Contracts;

/// <summary>
/// A tool's own slice of disk.
///
/// Each tool keeps writing to the same file it always did —
/// <c>%LOCALAPPDATA%\&lt;Tool&gt;\settings.json</c> — so a tool converted to a plugin picks
/// up the settings its standalone version left behind, with no migration step and no
/// window where the user's callsigns quietly reset to empty.
/// </summary>
public interface ISettingsScope
{
    /// <summary>
    /// The tool's settings folder, created on first access. Use it directly for anything
    /// that isn't the main settings blob — sound files, caches, exported reports.
    /// </summary>
    string DirectoryPath { get; }

    /// <summary>
    /// Reads and deserialises <c>settings.json</c>. Returns <paramref name="fallback"/> when
    /// the file is missing, empty or unparseable — a corrupt settings file loses settings, it
    /// does not stop the tool loading.
    /// </summary>
    T Load<T>(T fallback) where T : class;

    /// <summary>
    /// Serialises to <c>settings.json</c> via a temp file and a replace, so a crash mid-write
    /// can't leave a half-written file behind.
    /// </summary>
    void Save<T>(T value) where T : class;
}
