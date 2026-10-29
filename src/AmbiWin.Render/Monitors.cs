using System.Runtime.InteropServices;

namespace AmbiWin.Render;

/// <summary>A connected display, in physical pixels.</summary>
/// <param name="Handle">HMONITOR (valid until the display configuration changes).</param>
/// <param name="DeviceName">e.g. \\.\DISPLAY1 — stable key for per-monitor settings.</param>
public sealed record MonitorInfo(nint Handle, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary, float Scale);

public static unsafe class Monitors
{
    [ThreadStatic] private static List<MonitorInfo>? _collecting;

    /// <summary>Opts the process into per-monitor DPI awareness v2. Call before creating any window.</summary>
    public static void EnablePerMonitorDpiAwareness() =>
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        _collecting = [];
        try
        {
            Native.EnumDisplayMonitors(0, null, &OnMonitor, 0);
            return _collecting.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.X).ToList();
        }
        finally
        {
            _collecting = null;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, Native.RECT* rect, nint data)
    {
        var info = new Native.MONITORINFOEXW { cbSize = (uint)sizeof(Native.MONITORINFOEXW) };
        if (!Native.GetMonitorInfo(monitor, &info)) return 1;

        float scale = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96f : 1f;
        string device = new string(info.szDevice);
        var r = info.rcMonitor;
        _collecting?.Add(new MonitorInfo(monitor, device, r.Left, r.Top, r.Width, r.Height,
            (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, scale));
        return 1; // continue enumeration
    }
}
