using System.Drawing;
using System.Windows.Forms;

namespace AmbiWin.App;

/// <summary>
/// The notification-area icon and its menu. Every action goes back to <see cref="AppController"/>;
/// check marks are refreshed each time the menu opens, so they always match the current settings.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabled;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _music;
    private readonly ToolStripMenuItem _steady;

    public TrayIcon(AppController app)
    {
        _app = app;

        var header = new ToolStripMenuItem("AmbiWin") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        _enabled = new ToolStripMenuItem("Glow on", null, (_, _) => _app.ToggleEnabled());
        _pause = new ToolStripMenuItem("Pause for 1 hour", null, (_, _) => _app.TogglePause());
        _music = new ToolStripMenuItem("Pulse with music", null, (_, _) => _app.SetAnimation(Core.Settings.AnimationMode.MusicSync));
        _steady = new ToolStripMenuItem("Steady glow", null, (_, _) => _app.SetAnimation(Core.Settings.AnimationMode.Idle));
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => _app.ShowSettings()) { Font = new Font(header.Font, FontStyle.Regular) };
        var quit = new ToolStripMenuItem("Quit AmbiWin", null, (_, _) => _app.Quit());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([header, new ToolStripSeparator(), _enabled, _pause, new ToolStripSeparator(),
            _music, _steady, new ToolStripSeparator(), settings, quit]);
        menu.Opening += (_, _) => RefreshChecks();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AmbiWin",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _app.ShowSettings(); };
    }

    private void RefreshChecks()
    {
        var s = _app.Settings.Current;
        _enabled.Checked = s.Enabled;
        _pause.Text = _app.IsPaused ? "Resume" : "Pause for 1 hour";
        _pause.Enabled = s.Enabled;
        _music.Checked = s.Animation == Core.Settings.AnimationMode.MusicSync;
        _steady.Checked = s.Animation == Core.Settings.AnimationMode.Idle;
    }

    /// <summary>Shows the playing track in the tooltip (Windows limits tooltips to 127 characters).</summary>
    public void SetTrack(string? track)
    {
        string text = string.IsNullOrEmpty(track) ? "AmbiWin" : $"AmbiWin — {track}";
        _icon.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    public void ShowTip(string title, string text) => _icon.ShowBalloonTip(8000, title, text, ToolTipIcon.None);

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AmbiWin.ico"));
        if (resource is null) return SystemIcons.Application;
        using var stream = resource.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
