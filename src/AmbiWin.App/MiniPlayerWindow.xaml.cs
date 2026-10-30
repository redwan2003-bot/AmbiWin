using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmbiWin.Core.Color;
using AmbiWin.Media;

namespace AmbiWin.App;

/// <summary>
/// The floating "liquid glass" mini player: album art, title, progress and transport controls over a
/// blurred, glow-tinted backdrop. Driven by <see cref="NowPlayingWatcher"/>; owned by <see cref="AppController"/>.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private static readonly TimeSpan TintFade = TimeSpan.FromMilliseconds(800);

    private readonly NowPlayingWatcher _media;
    private readonly SettingsService _settings;
    private readonly Func<Palette> _palette;
    private readonly DispatcherTimer _tick;
    private NowPlaying? _np;
    private Artwork? _shownArt;
    private Palette? _shownPalette;

    public MiniPlayerWindow(NowPlayingWatcher media, SettingsService settings, Func<Palette> palette)
    {
        _media = media;
        _settings = settings;
        _palette = palette;
        InitializeComponent();
        PlacePlayer();

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        IsVisibleChanged += (_, _) => { if (IsVisible) { _tick.Start(); Refresh(); } else _tick.Stop(); };
        SourceInitialized += (_, _) => ApplyGlass();
        ProgressTrack.SizeChanged += (_, _) => UpdateProgress();
        SetNowPlaying(media.Current);
    }

    /// <summary>Shows a new track or playback state (UI thread).</summary>
    public void SetNowPlaying(NowPlaying? np)
    {
        _np = np;
        bool has = np is not null && (np.Title.Length > 0 || np.Artist.Length > 0);
        TitleText.Text = has ? (np!.Title.Length > 0 ? np.Title : "Unknown title") : "Nothing playing";
        ArtistText.Text = has ? (np!.Artist.Length > 0 ? np.Artist : AppName(np.AppId)) : "Play something to see it here";

        PlayPauseButton.Content = np?.IsPlaying == true ? "" : "";
        PlayPauseButton.IsEnabled = np?.CanPlayPause ?? false;
        PreviousButton.IsEnabled = np?.CanPrevious ?? false;
        NextButton.IsEnabled = np?.CanNext ?? false;

        if (!ReferenceEquals(np?.Artwork, _shownArt))
        {
            _shownArt = np?.Artwork;
            ArtBrush.ImageSource = _shownArt is { } art ? ToBitmap(art) : null;
            ArtPlaceholder.Visibility = _shownArt is null ? Visibility.Visible : Visibility.Hidden;
        }
        Refresh();
    }

    private void Refresh()
    {
        UpdateProgress();
        UpdateTint();
    }

    private void UpdateProgress()
    {
        var np = _np;
        bool timed = np is not null && np.Duration > TimeSpan.Zero;
        ProgressRow.Visibility = timed ? Visibility.Visible : Visibility.Hidden;
        if (!timed)
        {
            ElapsedText.Text = DurationText.Text = "";
            return;
        }
        var position = np!.CurrentPosition(DateTimeOffset.Now);
        ProgressFill.Width = ProgressTrack.ActualWidth * Math.Clamp(position / np.Duration, 0, 1);
        ElapsedText.Text = Format(position);
        DurationText.Text = Format(np.Duration);
    }

    private void UpdateTint()
    {
        var palette = _palette();
        if (palette == _shownPalette) return;
        bool first = _shownPalette is null;
        _shownPalette = palette;
        Fade(TintA, ToColor(palette.Primary), first);
        Fade(TintB, ToColor(palette.Secondary), first);
    }

    private static void Fade(GradientStop stop, Color to, bool immediately)
    {
        if (immediately)
        {
            stop.Color = to;
            return;
        }
        stop.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(to, TintFade) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    private static Color ToColor(OkLab c) => (Color)ColorConverter.ConvertFromString(Srgb.ToHex(c));

    private static BitmapSource ToBitmap(Artwork art)
    {
        var bitmap = BitmapSource.Create(art.Size, art.Size, 96, 96, PixelFormats.Bgra32, null, art.Bgra, art.Size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "ZuneMusic".</summary>
    private static string AppName(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        string name = appId.Split('!')[^1];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    // --- Placement --------------------------------------------------------------------------------

    private void PlacePlayer()
    {
        var s = _settings.Current;
        var work = SystemParameters.WorkArea;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (s.MiniPlayerLeft is { } left && s.MiniPlayerTop is { } top &&
            virtualScreen.Contains(new Point(left + 40, top + 20)))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = work.Right - Width - 20; // bottom right, above the taskbar
            Top = work.Bottom - Height - 20;
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        double left = Left, top = Top;
        _settings.Update(s => s with { MiniPlayerLeft = left, MiniPlayerTop = top });
    }

    private void OnHover(object sender, MouseEventArgs e) =>
        CloseButton.Visibility = Root.IsMouseOver ? Visibility.Visible : Visibility.Collapsed;

    // --- Buttons ----------------------------------------------------------------------------------

    private void OnPlayPause(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNext(object sender, RoutedEventArgs e) => _ = _media.NextAsync();

    private void OnPrevious(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    private void OnClose(object sender, RoutedEventArgs e) => _settings.Update(s => s with { MiniPlayer = false });

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        base.OnClosed(e);
    }

    // --- Glass ------------------------------------------------------------------------------------

    /// <summary>
    /// Blurred acrylic behind the window that stays on when the window is inactive (a floating player is
    /// almost never the active window), rounded corners and a dark frame. Falls back to a solid dark panel.
    /// </summary>
    private void ApplyGlass()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;

        int dark = 1, round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = 0x55181418, // AABBGGRR: a light, slightly warm dark tint
        };
        bool ok;
        unsafe
        {
            var data = new WindowCompositionAttribData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = (nint)(&accent),
                SizeOfData = sizeof(AccentPolicy),
            };
            ok = SetWindowCompositionAttribute(hwnd, ref data);
        }
        if (!ok) Root.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x22));
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttribData data);
}
