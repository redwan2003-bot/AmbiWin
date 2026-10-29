using AmbiWin.Core.Audio;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AmbiWin.Audio;

/// <summary>
/// Captures what the default speakers play (WASAPI loopback, never the microphone), runs the beat
/// detector on it and publishes an <see cref="AudioFeatures"/> snapshot for every analysis hop.
/// Restarts on the new device when the default output changes, and reports silence when capture
/// delivers no packets (loopback goes quiet entirely while nothing plays).
/// </summary>
public sealed class LoopbackSource : IDisposable
{
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(250);

    private readonly Latest<AudioFeatures> _output;
    private readonly Lock _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly Timer _watchdog;

    private BeatDetectorOptions _options;
    private WasapiRecorder? _capture;
    private BeatDetector? _detector;
    private WaveFormat? _format;
    private float[] _mono = new float[4096];
    private long _lastPacketTicks;
    private int _restartPending;
    private bool _disposed;

    public LoopbackSource(Latest<AudioFeatures> output, BeatDetectorOptions options)
    {
        _output = output;
        _options = options;
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _watchdog = new Timer(OnWatchdog, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_gate) StartCapture();
        _watchdog.Change(WatchdogPeriod, WatchdogPeriod);
    }

    /// <summary>Reopens capture on the current default device (after sleep/resume the old stream can go silent).</summary>
    public void Restart() => Interlocked.Exchange(ref _restartPending, 1);

    /// <summary>Applies new detector options (e.g. the sensitivity setting) without restarting capture.</summary>
    public void UpdateOptions(BeatDetectorOptions options)
    {
        lock (_gate)
        {
            if (options == _options) return;
            _options = options;
            if (_format is not null) _detector = new BeatDetector(_format.SampleRate, _options);
        }
    }

    private void StartCapture()
    {
        StopCapture();
        try
        {
            var capture = new WasapiRecorderBuilder()
                .WithLoopbackCapture()            // default render device: what the speakers play
                .WithSharedMode()
                .WithMmcssThreadPriority("Audio")
                .Build();
            _format = capture.WaveFormat;
            _detector = new BeatDetector(_format.SampleRate, _options);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            _lastPacketTicks = Environment.TickCount64;
            Log.Info($"Loopback capture started: {_format.SampleRate} Hz, {_format.Channels} ch, {_format.BitsPerSample}-bit {_format.Encoding}.");
        }
        catch (Exception ex)
        {
            // No output device, or it is exclusively in use. Report silence and retry from the watchdog.
            Log.Warn($"Loopback capture unavailable ({ex.GetType().Name}: {ex.Message}); will retry.");
            _capture = null;
            _output.Publish(AudioFeatures.SilentAt(TimeSpan.Zero));
            Interlocked.Exchange(ref _restartPending, 1);
        }
    }

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try { capture.StopRecording(); } catch (InvalidOperationException) { }
        capture.Dispose();
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_gate)
        {
            if (_detector is null || _format is null || buffer.IsEmpty) return;
            _lastPacketTicks = Environment.TickCount64;

            int bytesPerFrame = Math.Max(1, _format.BlockAlign);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                _detector.ProcessSilence(buffer.Length / bytesPerFrame); // contents are undefined; treat as silence
            }
            else
            {
                int frames = DownmixToMono(buffer, _format);
                _detector.Process(_mono.AsSpan(0, frames));
            }
            _output.Publish(_detector.Latest);
        }
    }

    /// <summary>Converts interleaved float or 16-bit PCM to mono floats in <see cref="_mono"/>.</summary>
    private int DownmixToMono(ReadOnlySpan<byte> data, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = data.Length / (bytesPerSample * channels);
        if (_mono.Length < frames) _mono = new float[frames * 2];

        if (format.BitsPerSample == 32)
        {
            // Shared-mode mix formats are 32-bit float (plain or WAVE_FORMAT_EXTENSIBLE).
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += samples[f * channels + c];
                _mono[f] = sum / channels;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += samples[f * channels + c] / 32768f;
                _mono[f] = sum / channels;
            }
        }
        else
        {
            return 0;
        }
        return frames;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Log.Warn($"Loopback capture stopped: {e.Exception.Message}");
        Interlocked.Exchange(ref _restartPending, 1);
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Render) return;
        Log.Info("Default output device changed; restarting capture.");
        Interlocked.Exchange(ref _restartPending, 1);
    }

    private void OnWatchdog(object? state)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (Interlocked.Exchange(ref _restartPending, 0) == 1)
            {
                StartCapture();
                return;
            }

            // Loopback sends nothing while nothing plays: feed silence so the detector reports it.
            long sinceLastPacket = Environment.TickCount64 - _lastPacketTicks;
            if (_detector is not null && _format is not null && sinceLastPacket >= WatchdogPeriod.TotalMilliseconds)
            {
                _detector.ProcessSilence((int)(_format.SampleRate * WatchdogPeriod.TotalSeconds));
                _output.Publish(_detector.Latest);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watchdog.Dispose();
            _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _notifications.Dispose();
            StopCapture();
            _enumerator.Dispose();
        }
    }
}
