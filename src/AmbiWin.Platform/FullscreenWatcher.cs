using System.Runtime.InteropServices;
using AmbiWin.Core.Diagnostics;

namespace AmbiWin.Platform;

/// <summary>
/// Detects a fullscreen app (game, fullscreen video, slideshow) in the foreground and reports which
/// monitor it covers, so the glow can step aside. Polls twice a second: one foreground-window check
/// is far cheaper than the event hooks needed to catch every way an app goes fullscreen (F11, games
/// switching modes, video players).
/// </summary>
public sealed unsafe partial class FullscreenWatcher : IDisposable
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);
    private readonly Timer _timer;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private string? _current;

    public FullscreenWatcher() => _timer = new Timer(_ => Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>Raised with the device name (e.g. \\.\DISPLAY1) of the monitor a fullscreen app covers, or null when none.</summary>
    public event Action<string?>? Changed;

    public void Start() => _timer.Change(TimeSpan.Zero, Period);

    private void Check()
    {
        string? device = null;
        try
        {
            device = FindFullscreenMonitor();
        }
        catch (Exception ex)
        {
            Log.Warn($"Fullscreen check failed: {ex.Message}");
        }

        if (device == _current) return;
        _current = device;
        Log.Info(device is null ? "Fullscreen app closed." : $"Fullscreen app on {device}.");
        Changed?.Invoke(device);
    }

    private string? FindFullscreenMonitor()
    {
        nint fg = GetForegroundWindow();
        if (fg == 0 || !IsWindowVisible(fg)) return null;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _ownProcessId) return null;

        // The desktop and taskbar can "cover" a monitor without being an app.
        char* buffer = stackalloc char[64];
        int len = GetClassName(fg, buffer, 64);
        var cls = new string(buffer, 0, Math.Max(0, len));
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;

        nint monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (!GetMonitorInfo(monitor, &info)) return null;
        string device = new string(info.szDevice);

        // Windows' own verdict: exclusive D3D fullscreen, "busy" fullscreen app, or presentation mode.
        if (SHQueryUserNotificationState(out int state) == 0 &&
            state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return device;

        // Borderless fullscreen: the window covers the whole monitor and has no caption.
        if (!GetWindowRect(fg, out RECT r)) return null;
        var m = info.rcMonitor;
        bool coversMonitor = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        long style = GetWindowLongPtr(fg, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        return coversMonitor && !hasCaption ? device : null;
    }

    public void Dispose() => _timer.Dispose();

    // --- Win32 ---------------------------------------------------------------------------------

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassName(nint hwnd, char* name, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, int flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, MONITORINFOEXW* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
