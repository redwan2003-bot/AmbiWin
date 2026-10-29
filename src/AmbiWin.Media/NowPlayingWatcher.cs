using System.Security.Cryptography;
using AmbiWin.Core.Color;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AmbiWin.Media;

/// <summary>
/// Watches Windows' media sessions (Spotify, Apple Music, browsers, …) and publishes a glow palette
/// from the playing track's album art. Property changes are debounced (players often fire twice per
/// track, the first time without artwork); a missing thumbnail is retried once.
/// Artwork stays in memory only and is never written to disk.
/// </summary>
public sealed class NowPlayingWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ArtworkRetry = TimeSpan.FromSeconds(1);
    private const int ArtworkSize = 64;
    private const int CacheCapacity = 64;

    private readonly Latest<Palette> _palette;
    private readonly PaletteExtractor _extractor = new();
    private readonly Lock _gate = new();
    private readonly Timer _debounce;
    private readonly Dictionary<string, Palette> _cache = [];
    private readonly Queue<string> _cacheOrder = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = [];
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string? _lastTrackKey;
    private bool _retriedArtwork;
    private bool _disposed;

    public NowPlayingWatcher(Latest<Palette> palette)
    {
        _palette = palette;
        _debounce = new Timer(_ => _ = RefreshAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Title and artist of the track the palette came from, for the tray tooltip and logs.</summary>
    public string? CurrentTrack { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Media sessions unavailable ({ex.GetType().Name}: {ex.Message}); using manual colors.");
            return;
        }
        _manager.SessionsChanged += (_, _) => { Resubscribe(); Schedule(); };
        _manager.CurrentSessionChanged += (_, _) => Schedule();
        Resubscribe();
        Schedule();
        Log.Info("Now-playing watcher started.");
    }

    private void Resubscribe()
    {
        lock (_gate)
        {
            if (_manager is null || _disposed) return;
            foreach (var s in _subscribed)
            {
                s.MediaPropertiesChanged -= OnSessionChanged;
                s.PlaybackInfoChanged -= OnSessionChanged;
            }
            _subscribed.Clear();
            foreach (var s in _manager.GetSessions())
            {
                s.MediaPropertiesChanged += OnSessionChanged;
                s.PlaybackInfoChanged += OnSessionChanged;
                _subscribed.Add(s);
            }
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Schedule();

    private void Schedule(TimeSpan? delay = null)
    {
        if (!_disposed) _debounce.Change(delay ?? Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The session to follow: one that is playing (most recent first), else Windows' current session.</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        var sessions = _manager.GetSessions();
        var current = _manager.GetCurrentSession();
        bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        if (current is not null && IsPlaying(current)) return current;
        return sessions.LastOrDefault(IsPlaying) ?? current;
    }

    private async Task RefreshAsync()
    {
        if (!await _refreshing.WaitAsync(0))
        {
            Schedule(); // a refresh is running; look again once it is done
            return;
        }
        try
        {
            var session = PickSession();
            if (session is null) return;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            string trackKey = $"{session.SourceAppUserModelId}|{props.Artist}|{props.Title}";
            bool newTrack = trackKey != _lastTrackKey;
            if (newTrack)
            {
                _lastTrackKey = trackKey;
                _retriedArtwork = false;
                CurrentTrack = string.IsNullOrEmpty(props.Artist) ? props.Title : $"{props.Artist} — {props.Title}";
            }

            if (props.Thumbnail is null)
            {
                if (!_retriedArtwork)
                {
                    _retriedArtwork = true;
                    Schedule(ArtworkRetry); // players often attach artwork a moment after the title
                }
                return;
            }

            byte[]? pixels = await DecodeAsync(props.Thumbnail);
            if (pixels is null) return;

            string hash = Convert.ToHexString(SHA256.HashData(pixels));
            if (!_cache.TryGetValue(hash, out var palette))
            {
                var extracted = _extractor.Extract(pixels, ArtworkSize, ArtworkSize);
                if (extracted is null) return;
                palette = extracted.Value;
                Remember(hash, palette);
            }

            if (palette != _palette.Value)
            {
                _palette.Publish(palette);
                Log.Info($"Colors from \"{CurrentTrack}\" ({session.SourceAppUserModelId}): {palette}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading now-playing info failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private static async Task<byte[]?> DecodeAsync(IRandomAccessStreamReference thumbnail)
    {
        using var stream = await thumbnail.OpenReadAsync();
        if (stream.Size == 0) return null;
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = ArtworkSize,
            ScaledHeight = ArtworkSize,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }

    private void Remember(string hash, Palette palette)
    {
        _cache[hash] = palette;
        _cacheOrder.Enqueue(hash);
        while (_cacheOrder.Count > CacheCapacity) _cache.Remove(_cacheOrder.Dequeue());
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        _refreshing.Dispose();
        lock (_gate)
        {
            foreach (var s in _subscribed)
            {
                s.MediaPropertiesChanged -= OnSessionChanged;
                s.PlaybackInfoChanged -= OnSessionChanged;
            }
            _subscribed.Clear();
        }
    }
}
