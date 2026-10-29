using AmbiWin.Core.Diagnostics;
using Microsoft.Win32;

namespace AmbiWin.App;

/// <summary>Start with Windows via the per-user Run key (no admin rights, visible in Task Manager → Startup apps).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AmbiWin";

    public static void Apply(bool enabled)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;

        // Builds run from bin\ by `dotnet run` are developer builds: never register those.
        if (exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
        {
            if (enabled) Log.Info("Developer build: start with Windows is not registered.");
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            string command = $"\"{exe}\" --background";
            if (enabled)
            {
                if (key.GetValue(ValueName) as string != command)
                {
                    key.SetValue(ValueName, command);
                    Log.Info("Registered to start with Windows.");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Removed from start with Windows.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Updating start with Windows failed: {ex.Message}");
        }
    }
}
