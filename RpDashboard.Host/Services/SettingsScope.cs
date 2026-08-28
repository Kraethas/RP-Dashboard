using System.IO;
using System.Text.Json;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Services;

/// <summary>
/// A tool's own settings folder under <c>%LOCALAPPDATA%</c>.
///
/// The folder name is supplied by the host rather than derived from the tool id, because the
/// point is to keep writing where the standalone app already wrote —
/// <c>%LOCALAPPDATA%\UnitAlert\settings.json</c>, not <c>...\unit-alert\</c>. A tool converted
/// to a plugin then finds the callsigns and sound paths its exe version left behind, with no
/// migration step and no moment where the user's configuration silently comes back empty.
/// </summary>
internal sealed class SettingsScope : ISettingsScope
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _fileName;

    public SettingsScope(string folderName, string fileName = "settings.json")
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            folderName);

        _fileName = fileName;
    }

    public string DirectoryPath { get; }

    private string FilePath => Path.Combine(DirectoryPath, _fileName);

    public T Load<T>(T fallback) where T : class
    {
        try
        {
            if (!File.Exists(FilePath)) return fallback;

            string json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
        }
        catch
        {
            // A corrupt settings file loses settings; it does not stop the tool loading, and it
            // certainly does not stop the other tabs loading.
            return fallback;
        }
    }

    public void Save<T>(T value) where T : class
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            // Write beside the target and swap, so a crash or a power cut mid-write leaves the
            // previous settings intact rather than a half-written file that Load then discards.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));

            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
        catch
        {
            // Losing a save is survivable and happens for reasons outside the app's control
            // (a sync client holding the file, a full disk). Throwing here would take out
            // whatever UI handler triggered the save.
        }
    }
}
