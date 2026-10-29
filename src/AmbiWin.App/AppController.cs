using System.Windows;
using System.Windows.Threading;
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
/// Composition root: creates the services, connects them through snapshots, and handles the
/// tray and settings-window actions. Lives on the UI thread.
/// </summary>
public sealed class AppController : IDisposable
{
    private static readonly TimeSpan PauseLength = TimeSpan.FromHours(1);

    private readonly Application _app;
    private readonly WakeSignal _wake = new();
    private readonly Latest<Palette> _palette;
    private readonly Latest<AudioFeatures> _audio;
    private readonly GlowRenderer _renderer;
    private readonly LoopbackSource _loopback;
    private readonly NowPlayingWatcher _nowPlaying;
    private readonly DispatcherTimer _pauseTimer;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private string? _track;
    private bool _quitting;

    public AppController(Application app)
    {
        _app = app;
        Settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath), _wake);
        var s = Settings.Current;

        _palette = new Latest<Palette>(s.ManualPalette(), _wake);
        _audio = new Latest<AudioFeatures>(AudioFeatures.SilentAt(TimeSpan.Zero), _wake);
        _renderer = new GlowRenderer(Settings.Snapshot, _palette, _audio, _wake);
        _loopback = new LoopbackSource(_audio, new BeatDetectorOptions().WithSensitivity(s.Sensitivity));
        _nowPlaying = new NowPlayingWatcher(_palette);
        _pauseTimer = new DispatcherTimer { Interval = PauseLength };
        _pauseTimer.Tick += (_, _) => SetPaused(false);

        Settings.Changed += OnSettingsChanged;
        _nowPlaying.TrackChanged += track => _app.Dispatcher.BeginInvoke(() =>
        {
            _track = track;
            _tray?.SetTrack(track);
            _settingsWindow?.SetTrack(track);
        });
    }

    public SettingsService Settings { get; }

    public bool IsPaused => _renderer.Suspended.HasFlag(SuspendReason.UserPause);

    public void Start(bool background)
    {
        Log.Info($"Settings: {SettingsStore.DefaultPath}{(Settings.IsFirstRun ? " (first run)" : "")}");
        _renderer.Start();
        _loopback.Start();
        _ = _nowPlaying.StartAsync().ContinueWith(t => Log.Error("Now-playing watcher failed", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
        StartupRegistration.Apply(Settings.Current.StartWithWindows);

        _tray = new TrayIcon(this);
        if (Settings.IsFirstRun)
        {
            Settings.Update(s => s); // writes the settings file so the next start is not a first run
            Settings.Flush();
            _tray.ShowTip("AmbiWin is running",
                "Your screen edges now glow with your music. Click the AmbiWin icon here for settings.");
        }
        else if (!background)
        {
            ShowSettings(); // opened by hand from the Start menu or Explorer
        }
    }

    private void OnSettingsChanged(GlowSettings before, GlowSettings after)
    {
        if (before.Sensitivity != after.Sensitivity)
            _loopback.UpdateOptions(new BeatDetectorOptions().WithSensitivity(after.Sensitivity));
        if (before.StartWithWindows != after.StartWithWindows)
            StartupRegistration.Apply(after.StartWithWindows);
        if (!after.Enabled && IsPaused)
            SetPaused(false);
    }

    public void ShowSettings()
    {
        if (!_app.Dispatcher.CheckAccess())
        {
            _app.Dispatcher.BeginInvoke(ShowSettings);
            return;
        }
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.SetTrack(_track);
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void ToggleEnabled() => Settings.Update(s => s with { Enabled = !s.Enabled });

    public void SetAnimation(AnimationMode mode) => Settings.Update(s => s with { Animation = mode });

    public void TogglePause() => SetPaused(!IsPaused);

    private void SetPaused(bool paused)
    {
        _renderer.SetSuspended(SuspendReason.UserPause, paused);
        if (paused) _pauseTimer.Start(); else _pauseTimer.Stop();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Log.Info("Quitting.");
        _settingsWindow?.Close();
        _app.Shutdown();
    }

    public void Dispose()
    {
        Settings.Flush();
        _pauseTimer.Stop();
        _tray?.Dispose();
        _nowPlaying.Dispose();
        _loopback.Dispose();
        _renderer.Dispose();
        Settings.Dispose();
        _wake.Dispose();
        Log.Info($"Stopped after {_renderer.FramesRendered} frames.");
    }
}
