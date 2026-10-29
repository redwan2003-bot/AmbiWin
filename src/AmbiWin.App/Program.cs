using System.Reflection;
using System.Windows;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Render;

namespace AmbiWin.App;

/// <summary>
/// Entry point of the tray app.
///
///   AmbiWin [--background] [--log path]
///
/// --background: started by Windows at sign-in; don't open the settings window.
/// --log: write the log somewhere other than %LOCALAPPDATA%\AmbiWin\logs (used by dev\run.cmd).
/// </summary>
internal static class Program
{
    private const long MaxLogBytes = 2 * 1024 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        // First, before any window exists: size everything in physical pixels on every monitor.
        Monitors.EnablePerMonitorDpiAwareness();

        string logPath = ArgValue(args, "--log") ?? DefaultLogPath();
        RotateIfLarge(logPath);
        Log.OpenFile(logPath);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Log.Info("AmbiWin is already running; asked it to open settings.");
            return 0;
        }

        string version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        Log.Info($"AmbiWin {version} starting on {Environment.OSVersion.VersionString}");

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            ThemeMode = ThemeMode.System, // Windows 11 Fluent look, light or dark to match the system
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true; // keep the glow running; the log has the details
        };

        using var controller = new AppController(app);
        instance.Listen(controller.ShowSettings);
        controller.Start(background: args.Contains("--background"));

        app.Run();
        Log.Close();
        return 0;
    }

    private static string DefaultLogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmbiWin", "logs", "ambiwin.log");

    /// <summary>Keeps one previous log (ambiwin.log.old) so the log never grows without bound.</summary>
    private static void RotateIfLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogBytes)
                File.Move(path, path + ".old", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
