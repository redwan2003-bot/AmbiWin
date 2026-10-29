using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmbiWin.Core.Color;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Settings;

namespace AmbiWin.App;

/// <summary>
/// Every control writes straight into <see cref="SettingsService"/>, so the glow changes live while you
/// drag. Values are shown in the units people think in (%, px, ms); settings store the raw values.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private bool _loading = true; // ignore control events while filling the controls

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        string version = typeof(SettingsWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        VersionText.Text = $"Version {(plus > 0 ? version[..Math.Min(version.Length, plus + 8)] : version)}";

        Load(settings.Current);
        _settings.Changed += OnSettingsChangedElsewhere;
        Closed += (_, _) => _settings.Changed -= OnSettingsChangedElsewhere;
    }

    public void SetTrack(string? track) =>
        NowPlayingText.Text = string.IsNullOrEmpty(track) ? "Nothing playing" : $"Now playing: {track}";

    /// <summary>Keeps the window in sync when the tray menu changes a setting.</summary>
    private void OnSettingsChangedElsewhere(GlowSettings before, GlowSettings after)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChangedElsewhere(before, after));
            return;
        }
        if (!_loading && (before.Enabled != after.Enabled || before.Animation != after.Animation))
            Load(after);
    }

    private void Load(GlowSettings s)
    {
        _loading = true;
        EnabledBox.IsChecked = s.Enabled;
        MusicRadio.IsChecked = s.Animation == AnimationMode.MusicSync;
        IdleRadio.IsChecked = s.Animation == AnimationMode.Idle;
        AlbumRadio.IsChecked = s.ColorSource == ColorSource.AlbumArt;
        ScreenRadio.IsChecked = s.ColorSource == ColorSource.Screen;
        ManualRadio.IsChecked = s.ColorSource == ColorSource.Manual;
        PrimaryHex.Text = s.ManualPrimary.ToUpperInvariant();
        SecondaryHex.Text = s.ManualSecondary.ToUpperInvariant();
        MixSlider.Value = Math.Round(s.Mix * 100);
        IntensitySlider.Value = Math.Round(s.Intensity * 100);
        ThicknessSlider.Value = s.ThicknessPx;
        SpreadSlider.Value = s.SpreadPx;
        CornerSlider.Value = s.CornerRadiusPx;
        SensitivitySlider.Value = Math.Round(s.Sensitivity * 100);
        DelaySlider.Value = s.AudioDelayMs ?? 0;
        StartupBox.IsChecked = s.StartWithWindows;
        CaptureBox.IsChecked = s.HideFromCapture;
        AllScreensBox.IsChecked = s.AllScreens;
        FullscreenCombo.SelectedIndex = (int)s.HideInFullscreen;
        BatteryCombo.SelectedIndex = (int)s.BatteryMode;
        UpdateLabels();
        UpdateSwatches();
        _loading = false;
    }

    private void UpdateLabels()
    {
        MixValue.Text = $"{MixSlider.Value:0}% main color";
        IntensityValue.Text = $"{IntensitySlider.Value:0}%";
        ThicknessValue.Text = $"{ThicknessSlider.Value:0} px";
        SpreadValue.Text = $"{SpreadSlider.Value:0} px";
        CornerValue.Text = $"{CornerSlider.Value:0} px";
        SensitivityValue.Text = $"{SensitivitySlider.Value:0}%";
        DelayValue.Text = DelaySlider.Value == 0 ? "Off" : $"{DelaySlider.Value:0} ms";
    }

    private void UpdateSwatches()
    {
        PrimarySwatch.Background = BrushFor(PrimaryHex.Text);
        SecondarySwatch.Background = BrushFor(SecondaryHex.Text);
    }

    private static SolidColorBrush BrushFor(string hex)
    {
        if (!Srgb.TryParseHex(hex, out _)) return Brushes.Transparent;
        int v = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
    }

    /// <summary>Reads every control into a new settings value and publishes it.</summary>
    private void Commit()
    {
        if (_loading) return;
        UpdateLabels();
        _settings.Update(s => s with
        {
            Enabled = EnabledBox.IsChecked == true,
            Animation = IdleRadio.IsChecked == true ? AnimationMode.Idle : AnimationMode.MusicSync,
            ColorSource = ManualRadio.IsChecked == true ? ColorSource.Manual
                : ScreenRadio.IsChecked == true ? ColorSource.Screen : ColorSource.AlbumArt,
            Mix = (float)(MixSlider.Value / 100),
            Intensity = (float)(IntensitySlider.Value / 100),
            ThicknessPx = (int)ThicknessSlider.Value,
            SpreadPx = (int)SpreadSlider.Value,
            CornerRadiusPx = (int)CornerSlider.Value,
            Sensitivity = (float)(SensitivitySlider.Value / 100),
            AudioDelayMs = (int)DelaySlider.Value,
            StartWithWindows = StartupBox.IsChecked == true,
            HideFromCapture = CaptureBox.IsChecked == true,
            AllScreens = AllScreensBox.IsChecked == true,
            HideInFullscreen = (FullscreenHide)Math.Max(0, FullscreenCombo.SelectedIndex),
            BatteryMode = (BatteryMode)Math.Max(0, BatteryCombo.SelectedIndex),
        });
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Commit();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Commit();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Commit();

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitHex((TextBox)sender);
    }

    private void OnHexCommitted(object sender, RoutedEventArgs e) => CommitHex((TextBox)sender);

    private void CommitHex(TextBox box)
    {
        if (_loading) return;
        string text = box.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (!Srgb.TryParseHex(text, out _))
        {
            // Not a color: put back the saved value.
            box.Text = (box == PrimaryHex ? _settings.Current.ManualPrimary : _settings.Current.ManualSecondary).ToUpperInvariant();
            UpdateSwatches();
            return;
        }
        box.Text = text.ToUpperInvariant();
        UpdateSwatches();
        string hex = box.Text;
        _settings.Update(s => box == PrimaryHex ? s with { ManualPrimary = hex } : s with { ManualSecondary = hex });
    }

    private void OnPickPrimary(object sender, RoutedEventArgs e) => PickColor(PrimaryHex);

    private void OnPickSecondary(object sender, RoutedEventArgs e) => PickColor(SecondaryHex);

    private void PickColor(TextBox box)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (Srgb.TryParseHex(box.Text, out _))
        {
            int v = int.Parse(box.Text.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            dialog.Color = System.Drawing.Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        box.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ManualRadio.IsChecked = true; // picking a color means "use my colors"
        CommitHex(box);
        Commit();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _settings.Reset();
        Load(_settings.Current);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        string? folder = Log.FilePath is null ? null : Path.GetDirectoryName(Log.FilePath);
        if (folder is not null && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
