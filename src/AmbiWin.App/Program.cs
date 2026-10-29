using System.Reflection;
using AmbiWin.Audio;
using AmbiWin.Core.Audio;
using AmbiWin.Core.Color;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Settings;
using AmbiWin.Core.Threading;
using AmbiWin.Media;
using AmbiWin.Render;

namespace AmbiWin.App;

/// <summary>
/// Day-1 entry point: glow on the primary monitor, pulsing to whatever plays.
/// Console-hosted for now; day 2 replaces this with the tray app and settings window.
///
///   AmbiWin [--log path] [--show-in-capture] [--idle]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? logPath = ArgValue(args, "--log");
        if (logPath is not null) Log.OpenFile(logPath);

        string version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        Log.Info($"AmbiWin {version} starting on {Environment.OSVersion.VersionString}");

        Monitors.EnablePerMonitorDpiAwareness();

        var store = new SettingsStore(SettingsStore.DefaultPath);
        var settings = store.Load();
        if (args.Contains("--show-in-capture")) settings = settings with { HideFromCapture = false };
        if (args.Contains("--idle")) settings = settings with { Animation = AnimationMode.Idle };
        Log.Info($"Settings: {store.FilePath} (color {settings.ColorSource}, {settings.Animation}, " +
                 $"thickness {settings.ThicknessPx}, spread {settings.SpreadPx}, hidden from capture {settings.HideFromCapture})");

        using var wake = new WakeSignal();
        var settingsSnapshot = new Latest<GlowSettings>(settings, wake);
        var palette = new Latest<Palette>(settings.ManualPalette(), wake);
        var audio = new Latest<AudioFeatures>(AudioFeatures.SilentAt(TimeSpan.Zero), wake);

        using var renderer = new GlowRenderer(settingsSnapshot, palette, audio, wake);
        using var loopback = new LoopbackSource(audio, new BeatDetectorOptions().WithSensitivity(settings.Sensitivity));

        using var nowPlaying = new NowPlayingWatcher(palette);

        renderer.Start();
        loopback.Start();
        nowPlaying.StartAsync().GetAwaiter().GetResult();

        var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        Log.Info("Running. Press Ctrl+C to quit.");
        quit.Wait();

        Log.Info($"Stopping after {renderer.FramesRendered} frames.");
        Log.Close();
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
