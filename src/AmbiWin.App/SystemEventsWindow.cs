using System.Runtime.InteropServices;
using System.Windows.Interop;
using AmbiWin.Core.Diagnostics;

namespace AmbiWin.App;

/// <summary>
/// A hidden window that receives the Windows notifications AmbiWin reacts to: session lock/unlock,
/// display on/off, sleep/resume and AC/battery changes. All of them are pushed by Windows; nothing polls.
/// Events are raised on the UI thread.
/// </summary>
public sealed partial class SystemEventsWindow : IDisposable
{
    private static Guid _displayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");   // GUID_CONSOLE_DISPLAY_STATE
    private static Guid _powerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");    // GUID_ACDC_POWER_SOURCE

    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;

    private readonly HwndSource _source;
    private readonly List<nint> _powerRegistrations = [];
    private readonly bool _sessionRegistered;

    public SystemEventsWindow()
    {
        // A 0×0 window that is never shown.
        _source = new HwndSource(new HwndSourceParameters("AmbiWin.SystemEvents") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);

        nint hwnd = _source.Handle;
        _powerRegistrations.Add(RegisterPowerSettingNotification(hwnd, ref _displayState, 0));
        _powerRegistrations.Add(RegisterPowerSettingNotification(hwnd, ref _powerSource, 0));
        _sessionRegistered = WTSRegisterSessionNotification(hwnd, 0);
        if (!_sessionRegistered) Log.Warn("Lock/unlock notifications unavailable.");
    }

    /// <summary>true when the screen turns off (or dims to off), false when it comes back.</summary>
    public event Action<bool>? DisplayOffChanged;

    public event Action<bool>? SessionLockedChanged;

    public event Action<bool>? OnBatteryChanged;

    public event Action? Suspending;

    public event Action? Resumed;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WTSSESSION_CHANGE:
                if (wParam == WTS_SESSION_LOCK) SessionLockedChanged?.Invoke(true);
                else if (wParam == WTS_SESSION_UNLOCK) SessionLockedChanged?.Invoke(false);
                break;

            case WM_POWERBROADCAST:
                switch ((int)wParam)
                {
                    case PBT_APMSUSPEND:
                        Suspending?.Invoke();
                        break;
                    case PBT_APMRESUMEAUTOMATIC:
                        Resumed?.Invoke();
                        break;
                    case PBT_POWERSETTINGCHANGE:
                        OnPowerSetting(lParam);
                        break;
                }
                break;
        }
        return 0;
    }

    private void OnPowerSetting(nint lParam)
    {
        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
        if (setting.PowerSetting == _displayState)
        {
            // 0 = off, 1 = on, 2 = dimmed (still visible)
            DisplayOffChanged?.Invoke(setting.Data == 0);
        }
        else if (setting.PowerSetting == _powerSource)
        {
            // 0 = AC, 1 = battery, 2 = short-term source (UPS)
            OnBatteryChanged?.Invoke(setting.Data != 0);
        }
    }

    public void Dispose()
    {
        foreach (var h in _powerRegistrations.Where(h => h != 0)) UnregisterPowerSettingNotification(h);
        if (_sessionRegistered) WTSUnRegisterSessionNotification(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // first DWORD of the data
    }

    [LibraryImport("user32.dll")]
    private static partial nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSettingGuid, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hwnd, int flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint hwnd);
}
