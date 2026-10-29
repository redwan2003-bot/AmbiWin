using System.Runtime.InteropServices;
using AmbiWin.Core.Diagnostics;

namespace AmbiWin.Render;

/// <summary>
/// A borderless, click-through, always-on-top window covering one monitor. It has no GDI surface
/// (WS_EX_NOREDIRECTIONBITMAP); DirectComposition draws into it. Never takes focus, never in Alt+Tab.
/// Must be created and destroyed on the thread that pumps its messages.
/// </summary>
internal sealed unsafe class OverlayWindow : IDisposable
{
    private const string ClassName = "AmbiWin.Overlay";
    private static bool _classRegistered;
    private static char* _classNamePtr;

    /// <summary>Set when Windows reports a display change; read and cleared by the render loop.</summary>
    public static volatile bool DisplayChanged;

    public OverlayWindow(MonitorInfo monitor, bool hideFromCapture)
    {
        Monitor = monitor;
        EnsureClass();

        uint exStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST
                     | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP;

        Handle = Native.CreateWindowEx(exStyle, _classNamePtr, _classNamePtr, Native.WS_POPUP,
            monitor.X, monitor.Y, monitor.Width, monitor.Height, 0, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx failed (error {Marshal.GetLastPInvokeError()}).");

        // A layered window stays invisible until its attributes are set.
        if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA))
            Log.Warn($"SetLayeredWindowAttributes failed (error {Marshal.GetLastPInvokeError()}).");

        SetHiddenFromCapture(hideFromCapture);
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, monitor.X, monitor.Y, monitor.Width, monitor.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public nint Handle { get; private set; }

    public MonitorInfo Monitor { get; }

    public bool HiddenFromCapture { get; private set; }

    public void SetHiddenFromCapture(bool hide)
    {
        if (Native.SetWindowDisplayAffinity(Handle, hide ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE))
            HiddenFromCapture = hide;
        else
            Log.Warn($"SetWindowDisplayAffinity({(hide ? "exclude" : "none")}) failed (error {Marshal.GetLastPInvokeError()}).");
    }

    public void Show(bool visible) => Native.ShowWindow(Handle, visible ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);

    /// <summary>Dispatches pending messages for every window on this thread. Returns false on WM_QUIT.</summary>
    public static bool PumpMessages()
    {
        while (Native.PeekMessage(out var msg, 0, 0, 0, Native.PM_REMOVE))
        {
            if (msg.message == Native.WM_QUIT) return false;
            Native.TranslateMessage(msg);
            Native.DispatchMessage(msg);
        }
        return true;
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Native.DestroyWindow(Handle);
            Handle = 0;
        }
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _classNamePtr = (char*)Marshal.StringToHGlobalUni(ClassName); // lives for the process
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)sizeof(Native.WNDCLASSEXW),
            lpfnWndProc = &WndProc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = _classNamePtr,
        };
        if (Native.RegisterClassEx(&wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed (error {Marshal.GetLastPInvokeError()}).");
        _classRegistered = true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
                return Native.HTTRANSPARENT; // belt and braces: never take mouse input
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                DisplayChanged = true;
                break;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
