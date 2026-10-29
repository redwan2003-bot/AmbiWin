using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Settings;
using AmbiWin.Core.Threading;

namespace AmbiWin.App;

/// <summary>
/// The single owner of the current settings. Every change is published to the render thread at once
/// (live preview) and saved to disk 300 ms after the last change, so dragging a slider writes once.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _store;
    private readonly Timer _saveTimer;
    private readonly Lock _gate = new();

    public SettingsService(SettingsStore store, WakeSignal wake)
    {
        _store = store;
        IsFirstRun = !File.Exists(store.FilePath);
        Snapshot = new Latest<GlowSettings>(store.Load(), wake);
        _saveTimer = new Timer(_ => Save(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True when no settings file existed at startup.</summary>
    public bool IsFirstRun { get; }

    public Latest<GlowSettings> Snapshot { get; }

    public GlowSettings Current => Snapshot.Value;

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event Action<GlowSettings, GlowSettings>? Changed;

    /// <summary>Applies <paramref name="change"/> to the current settings, publishes and schedules a save.</summary>
    public void Update(Func<GlowSettings, GlowSettings> change)
    {
        GlowSettings before, after;
        lock (_gate)
        {
            before = Snapshot.Value;
            after = change(before).Normalized();
            if (after == before) return;
            Snapshot.Publish(after);
        }
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(before, after);
    }

    public void Reset() => Update(current => GlowSettings.Default with { Monitors = current.Monitors });

    /// <summary>Writes pending changes now (called on exit).</summary>
    public void Flush()
    {
        _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Save();
    }

    private void Save()
    {
        try
        {
            _store.Save(Snapshot.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Saving settings failed: {ex.Message}");
        }
    }

    public void Dispose() => _saveTimer.Dispose();
}
