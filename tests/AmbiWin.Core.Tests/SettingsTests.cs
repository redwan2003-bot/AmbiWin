using AmbiWin.Core.Settings;

namespace AmbiWin.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ambiwin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        Assert.Equal(GlowSettings.Default, store.Load());
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = GlowSettings.Default with
        {
            ColorSource = ColorSource.Manual,
            ManualPrimary = "#FF0055",
            ThicknessPx = 20,
            AudioDelayMs = 150,
            HideFromCapture = false,
            Monitors = new() { [@"\\?\DISPLAY#ABC"] = new MonitorSettings(Enabled: false) },
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(ColorSource.Manual, loaded.ColorSource);
        Assert.Equal("#FF0055", loaded.ManualPrimary);
        Assert.Equal(20, loaded.ThicknessPx);
        Assert.Equal(150, loaded.AudioDelayMs);
        Assert.False(loaded.HideFromCapture);
        Assert.False(loaded.Monitors[@"\\?\DISPLAY#ABC"].Enabled);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Enums_are_written_as_readable_strings()
    {
        string json = SettingsStore.Serialize(GlowSettings.Default);
        Assert.Contains("\"colorSource\": \"albumArt\"", json, StringComparison.Ordinal);
        Assert.Contains("\"batteryMode\": \"reduced\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_bad_colors_reset()
    {
        var loaded = SettingsStore.Deserialize("""
            { "thicknessPx": 999, "spreadPx": -5, "mix": 3.0, "audioDelayMs": 5000, "manualPrimary": "not a color" }
            """);

        Assert.Equal(64, loaded.ThicknessPx);
        Assert.Equal(0, loaded.SpreadPx);
        Assert.Equal(1f, loaded.Mix);
        Assert.Equal(400, loaded.AudioDelayMs);
        Assert.Equal(GlowSettings.Default.ManualPrimary, loaded.ManualPrimary);
    }

    [Fact]
    public void Unknown_keys_from_newer_versions_are_kept()
    {
        var loaded = SettingsStore.Deserialize("""{ "thicknessPx": 12, "futureFeature": { "on": true } }""");
        string json = SettingsStore.Serialize(loaded);
        Assert.Contains("futureFeature", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept_aside()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var loaded = new SettingsStore(path).Load();

        Assert.Equal(GlowSettings.Default, loaded);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }
}
