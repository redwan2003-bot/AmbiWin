using System.Text.Json;
using System.Text.Json.Serialization;
using AmbiWin.Core.Color;

namespace AmbiWin.Core.Settings;

// JSON names are pinned per member so renaming a C# member never breaks existing settings files.

public enum ColorSource
{
    [JsonStringEnumMemberName("albumArt")] AlbumArt,
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("screen")] Screen,
}

public enum AnimationMode
{
    [JsonStringEnumMemberName("musicSync")] MusicSync,
    [JsonStringEnumMemberName("idle")] Idle,
}

public enum FullscreenHide
{
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("thatMonitor")] ThatMonitor,
    [JsonStringEnumMemberName("allMonitors")] AllMonitors,
}

public enum BatteryMode
{
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("reduced")] Reduced,
    [JsonStringEnumMemberName("idle")] Idle,
}

public sealed record MonitorSettings(bool Enabled = true);

/// <summary>
/// Every user setting, matching the schema in the technical design. Immutable: a change creates a new
/// instance, which is published as a snapshot to the render thread.
/// </summary>
public sealed record GlowSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool Enabled { get; init; } = true;
    public ColorSource ColorSource { get; init; } = ColorSource.AlbumArt;
    public string ManualPrimary { get; init; } = "#7C5CFF";
    public string ManualSecondary { get; init; } = "#00D1FF";
    public float Mix { get; init; } = 0.6f;
    public AnimationMode Animation { get; init; } = AnimationMode.MusicSync;
    public int ThicknessPx { get; init; } = 8;
    public int SpreadPx { get; init; } = 48;
    public float Intensity { get; init; } = 0.8f;
    public int CornerRadiusPx { get; init; } = 10;

    /// <summary>Run the glow along the top of the taskbar (curving around it) instead of over it.</summary>
    public bool WrapTaskbar { get; init; } = true;
    public float Sensitivity { get; init; } = 0.5f;

    /// <summary>Null = not set by the user; the app picks 0, or 150 ms on Bluetooth output.</summary>
    public int? AudioDelayMs { get; init; }

    /// <summary>Glow on every connected screen (otherwise the primary screen only).</summary>
    public bool AllScreens { get; init; }

    /// <summary>Per-monitor choices keyed by device name (future use). Empty = primary monitor only.</summary>
    public Dictionary<string, MonitorSettings> Monitors { get; init; } = new();

    public FullscreenHide HideInFullscreen { get; init; } = FullscreenHide.ThatMonitor;
    public BatteryMode BatteryMode { get; init; } = BatteryMode.Reduced;
    public bool StartWithWindows { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// Keeps the glow out of screenshots, recordings and screen shares. Turn off to record a demo.
    /// </summary>
    public bool HideFromCapture { get; init; } = true;
    public string ToggleHotkey { get; init; } = "Ctrl+Alt+G";

    /// <summary>Show the floating mini player (Ctrl+Alt+M toggles it).</summary>
    public bool MiniPlayer { get; init; }

    /// <summary>Mini player position in device-independent pixels; null = default (bottom right, above the taskbar).</summary>
    public double? MiniPlayerLeft { get; init; }
    public double? MiniPlayerTop { get; init; }

    /// <summary>
    /// Keys this version does not know, kept so a downgrade or newer file never loses settings.
    /// Settable (not init) because System.Text.Json cannot bind extension data through init-only setters.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public static GlowSettings Default { get; } = new();

    /// <summary>Returns a copy with every value clamped into its documented range and invalid colors reset.</summary>
    public GlowSettings Normalized() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        ManualPrimary = Srgb.TryParseHex(ManualPrimary, out _) ? ManualPrimary : Default.ManualPrimary,
        ManualSecondary = Srgb.TryParseHex(ManualSecondary, out _) ? ManualSecondary : Default.ManualSecondary,
        Mix = Math.Clamp(Mix, 0f, 1f),
        ThicknessPx = Math.Clamp(ThicknessPx, 1, 64),
        SpreadPx = Math.Clamp(SpreadPx, 0, 200),
        Intensity = Math.Clamp(Intensity, 0f, 1f),
        CornerRadiusPx = Math.Clamp(CornerRadiusPx, 0, 48),
        Sensitivity = Math.Clamp(Sensitivity, 0f, 1f),
        AudioDelayMs = AudioDelayMs is { } d ? Math.Clamp(d, 0, 400) : null,
        Monitors = Monitors ?? new(),
        ToggleHotkey = string.IsNullOrWhiteSpace(ToggleHotkey) ? Default.ToggleHotkey : ToggleHotkey,
    };

    /// <summary>The manual palette parsed into OKLab.</summary>
    public Palette ManualPalette()
    {
        Srgb.TryParseHex(ManualPrimary, out var primary);
        Srgb.TryParseHex(ManualSecondary, out var secondary);
        return new Palette(primary, secondary);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GlowSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
