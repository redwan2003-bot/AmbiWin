using System.Security.Cryptography;
using AmbiWin.Core.Color;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AmbiWin.Media;

/// <summary>Album art as straight-alpha BGRA pixels (Size × Size), ready for any UI toolkit.</summary>
public sealed record Artwork(byte[] Bgra, int Size);

/// <summary>What is playing, for the mini player. Position is extrapolated from <see cref="PositionAt"/> while playing.</summary>
public sealed record NowPlaying(
    string Title,
    string Artist,
    string AppId,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionAt,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    Artwork? Artwork)
{
    public TimeSpan CurrentPosition(DateTimeOffset now)
    {
        if (Duration <= TimeSpan.Zero) return TimeSpan.Zero;
        var p = IsPlaying ? Position + (now - PositionAt) : Position;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p > Duration ? Duration : p;
    }
}

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
    private const int DisplayArtworkSize = 256;
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
    private GlobalSystemMediaTransportControlsSession? _session;
    private Artwork? _displayArt;
    private string? _displayArtKey;
    private NowPlaying? _nowPlaying;
    private bool _disposed;

    public NowPlayingWatcher(Latest<Palette> palette)
    {
        _palette = palette;
        _debounce = new Timer(_ => _ = RefreshAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Title and artist of the track the palette came from, for the tray tooltip and logs.</summary>
    public string? CurrentTrack { get; private set; }

    /// <summary>Raised (on a thread-pool thread) when <see cref="CurrentTrack"/> changes.</summary>
    public event Action<string?>? TrackChanged;

    /// <summary>Full now-playing state for the mini player; null when nothing is playing or paused.</summary>
    public NowPlaying? Current => _nowPlaying;

    /// <summary>Raised (on a thread-pool thread) when anything in <see cref="Current"/> changes.</summary>
    public event Action<NowPlaying?>? NowPlayingChanged;

    public Task TogglePlayPauseAsync() => Control(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => Control(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => Control(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Control(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        var session = _session ?? PickSession();
        if (session is null) return;
        try
        {
            await command(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Media command failed: {ex.GetType().Name}: {ex.Message}");
        }
        Schedule(TimeSpan.FromMilliseconds(150));
    }

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
                s.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _subscribed.Clear();
            foreach (var s in _manager.GetSessions())
            {
                s.MediaPropertiesChanged += OnSessionChanged;
                s.PlaybackInfoChanged += OnSessionChanged;
                s.TimelinePropertiesChanged += OnSessionChanged;
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
            _session = session;
            if (session is null)
            {
                SetNowPlaying(null);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            string trackKey = $"{session.SourceAppUserModelId}|{props.Artist}|{props.Title}";
            bool newTrack = trackKey != _lastTrackKey;
            if (newTrack)
            {
                _lastTrackKey = trackKey;
                _retriedArtwork = false;
                CurrentTrack = string.IsNullOrEmpty(props.Artist) ? props.Title : $"{props.Artist} — {props.Title}";
                TrackChanged?.Invoke(CurrentTrack);
            }

            if (newTrack) _displayArt = null;
            PublishNowPlaying(session, props);

            if (props.Thumbnail is null)
            {
                if (!_retriedArtwork)
                {
                    _retriedArtwork = true;
                    Schedule(ArtworkRetry); // players often attach artwork a moment after the title
                }
                return;
            }

            if (_displayArtKey != trackKey || _displayArt is null)
            {
                var big = await DecodeAsync(props.Thumbnail, DisplayArtworkSize);
                if (big is not null)
                {
                    _displayArt = new Artwork(big, DisplayArtworkSize);
                    _displayArtKey = trackKey;
                    PublishNowPlaying(session, props);
                }
            }

            byte[]? pixels = await DecodeAsync(props.Thumbnail, ArtworkSize);
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

    private void PublishNowPlaying(GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        var info = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var controls = info?.Controls;
        var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
        SetNowPlaying(new NowPlaying(
            Title: props.Title ?? "",
            Artist: props.Artist ?? "",
            AppId: session.SourceAppUserModelId ?? "",
            IsPlaying: info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Position: timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime,
            Duration: duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            PositionAt: timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
            CanPrevious: controls?.IsPreviousEnabled ?? false,
            CanNext: controls?.IsNextEnabled ?? false,
            CanPlayPause: controls is null || controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
            Artwork: _displayArt));
    }

    private void SetNowPlaying(NowPlaying? value)
    {
        if (Equals(value, _nowPlaying)) return;
        _nowPlaying = value;
        NowPlayingChanged?.Invoke(value);
    }

    private static async Task<byte[]?> DecodeAsync(IRandomAccessStreamReference thumbnail, int size)
    {
        using var stream = await thumbnail.OpenReadAsync();
        if (stream.Size == 0) return null;
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)size,
            ScaledHeight = (uint)size,
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
                s.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _subscribed.Clear();
        }
    }
}
