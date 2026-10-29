namespace AmbiWin.Core.Audio;

/// <summary>
/// Turns a mono sample stream into <see cref="AudioFeatures"/>: auto-gained level, bass energy,
/// and onsets from bass-weighted spectral flux with an adaptive threshold.
/// <para>
/// Not thread-safe: feed it from one thread (the audio thread). It allocates nothing after construction.
/// It has no look-ahead, so it adds no delay beyond the hop it is waiting for.
/// </para>
/// </summary>
public sealed class BeatDetector
{
    private readonly BeatDetectorOptions _options;
    private readonly int _sampleRate;
    private readonly Fft _fft;

    private readonly float[] _ring;       // last FftSize samples, circular
    private readonly float[] _window;     // Hann
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _magnitudes;
    private readonly float[] _previousLogMagnitudes;
    private readonly int _bassFirstBin;
    private readonly int _bassLastBin;

    private readonly float[] _fluxHistory;
    private int _fluxCount;
    private int _fluxNext;
    private float _previousFlux;

    private readonly float _gainDecayPerHop;
    private readonly float _pulseDecayPerHop;
    private readonly long _refractorySamples;
    private readonly long _silenceHoldSamples;

    private int _ringWrite;
    private long _samplesSeen;
    private int _samplesSinceHop;
    private bool _hasPreviousSpectrum;

    private float _levelPeak;
    private float _bassPeak;
    private float _pulse;
    private long _lastOnsetSample = long.MinValue / 2;
    private long _silentSamples;

    public BeatDetector(int sampleRate, BeatDetectorOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _options = options ?? new BeatDetectorOptions();
        if (_options.HopSize <= 0 || _options.HopSize > _options.FftSize)
            throw new ArgumentException("HopSize must be between 1 and FftSize.", nameof(options));

        _sampleRate = sampleRate;
        _fft = new Fft(_options.FftSize);

        int n = _options.FftSize;
        _ring = new float[n];
        _window = new float[n];
        Fft.Hann(_window);
        _re = new float[n];
        _im = new float[n];
        _magnitudes = new float[_fft.BinCount];
        _previousLogMagnitudes = new float[_fft.BinCount];

        float binHz = (float)sampleRate / n;
        _bassFirstBin = Math.Max(1, (int)MathF.Ceiling(_options.BassMinHz / binHz));
        _bassLastBin = Math.Max(_bassFirstBin, (int)MathF.Floor(_options.BassMaxHz / binHz));

        float hopSeconds = (float)_options.HopSize / sampleRate;
        _fluxHistory = new float[Math.Max(4, (int)MathF.Round(_options.FluxHistorySeconds / hopSeconds))];
        _gainDecayPerHop = MathF.Pow(0.5f, hopSeconds / _options.AutoGainHalfLifeSeconds);
        _pulseDecayPerHop = MathF.Exp(-hopSeconds * 1000f / _options.PulseDecayMs);
        _refractorySamples = (long)(_options.RefractoryMs / 1000f * sampleRate);
        _silenceHoldSamples = (long)(_options.SilenceHoldSeconds * sampleRate);

        Latest = AudioFeatures.SilentAt(TimeSpan.Zero);
    }

    /// <summary>Raised on the calling thread for each detected onset, with its stream time.</summary>
    public event Action<TimeSpan>? Onset;

    /// <summary>Diagnostics for tests and tuning: (stream time, flux, history mean, threshold) per hop.</summary>
    internal Action<TimeSpan, float, float, float>? FluxTrace { get; set; }

    /// <summary>Features from the most recent hop.</summary>
    public AudioFeatures Latest { get; private set; }

    public int SampleRate => _sampleRate;

    /// <summary>Feeds mono samples in [-1, 1]. Returns the number of hops analyzed.</summary>
    public int Process(ReadOnlySpan<float> samples)
    {
        int hops = 0;
        foreach (float sample in samples)
        {
            _ring[_ringWrite] = sample;
            _ringWrite = (_ringWrite + 1) % _ring.Length;
            _samplesSeen++;
            _samplesSinceHop++;

            if (_samplesSinceHop >= _options.HopSize && _samplesSeen >= _options.FftSize)
            {
                _samplesSinceHop = 0;
                AnalyzeHop();
                hops++;
            }
        }
        return hops;
    }

    /// <summary>Feeds silence, e.g. when loopback capture delivers no packets because nothing is playing.</summary>
    public int ProcessSilence(int sampleCount)
    {
        Span<float> zeros = stackalloc float[256];
        int hops = 0;
        while (sampleCount > 0)
        {
            int chunk = Math.Min(sampleCount, zeros.Length);
            hops += Process(zeros[..chunk]);
            sampleCount -= chunk;
        }
        return hops;
    }

    private void AnalyzeHop()
    {
        int n = _options.FftSize;

        // Unroll the ring (oldest first), measure RMS, apply the window.
        double sumSquares = 0;
        for (int i = 0; i < n; i++)
        {
            float x = _ring[(_ringWrite + i) % n];
            sumSquares += x * x;
            _re[i] = x * _window[i];
            _im[i] = 0f;
        }
        float rms = (float)Math.Sqrt(sumSquares / n);
        float db = 20f * MathF.Log10(rms + 1e-9f);

        _fft.Forward(_re, _im);
        _fft.Magnitudes(_re, _im, _magnitudes);

        // Spectral flux on log-compressed magnitudes. Bass and non-bass rises are AVERAGED per bin before
        // weighting: summed, broadband hi-hat noise across hundreds of bins would swamp the few bass bins.
        float bassRise = 0f, otherRise = 0f;
        int bassBins = 0, otherBins = 0;
        float bass = 0f;
        for (int k = 1; k < _magnitudes.Length; k++)
        {
            float logMag = MathF.Log(1f + 100f * _magnitudes[k]);
            float rise = _hasPreviousSpectrum ? Math.Max(0f, logMag - _previousLogMagnitudes[k]) : 0f;
            _previousLogMagnitudes[k] = logMag;

            if (k <= _bassLastBin)
            {
                bassRise += rise;
                bassBins++;
            }
            else
            {
                otherRise += rise;
                otherBins++;
            }

            if (k >= _bassFirstBin && k <= _bassLastBin)
                bass += _magnitudes[k];
        }
        float flux = _options.BassWeight * (bassBins > 0 ? bassRise / bassBins : 0f)
                   + _options.OtherWeight * (otherBins > 0 ? otherRise / otherBins : 0f);
        _hasPreviousSpectrum = true;

        long frameEndSample = _samplesSeen;
        TimeSpan streamTime = TimeSpan.FromSeconds((double)frameEndSample / _sampleRate);

        // Silence tracking.
        bool belowFloor = db < _options.SilenceFloorDb;
        _silentSamples = belowFloor ? _silentSamples + _options.HopSize : 0;
        bool silent = _silentSamples >= _silenceHoldSamples;

        // Auto-gain: peaks decay with the configured half-life, so quiet tracks still reach ~1.
        _levelPeak = Math.Max(rms, _levelPeak * _gainDecayPerHop);
        _bassPeak = Math.Max(bass, _bassPeak * _gainDecayPerHop);
        float level = belowFloor || _levelPeak <= 0 ? 0f : Math.Clamp(rms / _levelPeak, 0f, 1f);
        float bassLevel = belowFloor || _bassPeak <= 0 ? 0f : Math.Clamp(bass / _bassPeak, 0f, 1f);

        // Adaptive threshold over the recent flux history (current hop excluded).
        bool onset = false;
        if (!belowFloor && _fluxCount >= _fluxHistory.Length / 2)
        {
            float mean = 0f;
            for (int i = 0; i < _fluxCount; i++) mean += _fluxHistory[i];
            mean /= _fluxCount;
            float variance = 0f;
            for (int i = 0; i < _fluxCount; i++)
            {
                float d = _fluxHistory[i] - mean;
                variance += d * d;
            }
            float sigma = MathF.Sqrt(variance / _fluxCount);
            float threshold = mean + _options.ThresholdK * sigma;

            onset = flux > threshold
                    && flux > mean * _options.MinFluxOverMean
                    && flux > _previousFlux
                    && frameEndSample - _lastOnsetSample >= _refractorySamples;
            FluxTrace?.Invoke(streamTime, flux, mean, Math.Max(threshold, mean * _options.MinFluxOverMean));
        }

        _fluxHistory[_fluxNext] = flux;
        _fluxNext = (_fluxNext + 1) % _fluxHistory.Length;
        _fluxCount = Math.Min(_fluxCount + 1, _fluxHistory.Length);
        _previousFlux = flux;

        if (onset)
        {
            _lastOnsetSample = frameEndSample;
            _pulse = 1f;
        }
        else
        {
            _pulse *= _pulseDecayPerHop;
            if (_pulse < 1e-4f) _pulse = 0f;
        }

        Latest = new AudioFeatures(level, bassLevel, _pulse, silent, streamTime);
        if (onset) Onset?.Invoke(streamTime);
    }
}
