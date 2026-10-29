using System.Text.Json;

namespace AmbiWin.Core.Settings;

/// <summary>
/// Loads and saves <see cref="GlowSettings"/> as JSON. Saves are atomic (temp file, then replace),
/// so a crash mid-save never leaves a half-written file. A corrupt file is moved aside, never deleted.
/// </summary>
public sealed class SettingsStore
{
    public SettingsStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>%LOCALAPPDATA%\AmbiWin\settings.json (the Store build's copy of that folder is per-app automatically).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmbiWin", "settings.json");

    public GlowSettings Load()
    {
        if (!File.Exists(FilePath))
            return GlowSettings.Default;

        try
        {
            using var stream = File.OpenRead(FilePath);
            var loaded = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.GlowSettings);
            return Migrate(loaded ?? GlowSettings.Default).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryMoveAside();
            return GlowSettings.Default;
        }
    }

    public void Save(GlowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings.Normalized(), SettingsJsonContext.Default.GlowSettings);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    public static string Serialize(GlowSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.GlowSettings);

    public static GlowSettings Deserialize(string json) =>
        Migrate(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.GlowSettings) ?? GlowSettings.Default).Normalized();

    /// <summary>Upgrades older files one schema version at a time. Version 1 is the first, so nothing to do yet.</summary>
    private static GlowSettings Migrate(GlowSettings settings)
    {
        // Add a case per version bump, e.g.:
        // if (settings.SchemaVersion < 2) settings = settings with { NewKey = ..., SchemaVersion = 2 };
        return settings;
    }

    private void TryMoveAside()
    {
        try
        {
            string backup = $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(FilePath, backup, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
