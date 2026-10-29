using System.Reflection;
using AmbiWin.Core.Settings;

namespace AmbiWin.App;

/// <summary>
/// Week-0 placeholder entry point. Week 1 replaces this with the overlay spike;
/// weeks 4-5 turn it into the tray app with the settings window.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        string version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

        var store = new SettingsStore(SettingsStore.DefaultPath);
        var settings = store.Load();

        Console.WriteLine($"AmbiWin {version}");
        Console.WriteLine($"Settings: {store.FilePath}");
        Console.WriteLine($"Color source: {settings.ColorSource}, animation: {settings.Animation}, " +
                          $"thickness {settings.ThicknessPx} px, spread {settings.SpreadPx} px");
        Console.WriteLine("The overlay arrives in week 1.");
        return 0;
    }
}
