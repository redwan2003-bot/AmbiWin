using AmbiWin.Core.Audio;

namespace AmbiWin.Core.Tests;

public class BeatDetectorTests
{
    private const int SampleRate = 48000;
    private const double Tolerance = 0.050; // ±50 ms, as BeatBench scores

    private static (List<double> Onsets, BeatDetector Detector) Run(float[] samples, float sensitivity = 0.5f)
    {
        var detector = new BeatDetector(SampleRate, new BeatDetectorOptions().WithSensitivity(sensitivity));
        var onsets = new List<double>();
        // The synthetic pad starts abruptly at t = 0, a real onset the labels don't include; the first
        // labeled kick is at 1.0 s, so warm-up detections before 0.9 s are not scored.
        detector.Onset += t => { if (t.TotalSeconds >= 0.9) onsets.Add(t.TotalSeconds); };

        // Feed in uneven chunks, like real capture callbacks.
        int pos = 0, chunk = 441;
        while (pos < samples.Length)
        {
            int len = Math.Min(chunk, samples.Length - pos);
            detector.Process(samples.AsSpan(pos, len));
            pos += len;
            chunk = chunk == 441 ? 480 : 441;
        }
        return (onsets, detector);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(174)]
    public void Finds_kicks_in_synthetic_music(double bpm)
    {
        var (samples, beats) = SyntheticMusic.KickTrack(SampleRate, bpm, seconds: 20);
        var (onsets, _) = Run(samples);

        double f1 = SyntheticMusic.F1(onsets, beats, Tolerance);
        Assert.True(f1 >= 0.9, $"F1 {f1:0.000} at {bpm} BPM ({onsets.Count} onsets for {beats.Count} beats)");
    }

    [Theory]
    [InlineData(70)]
    [InlineData(95)]
    public void Slow_tempos_do_not_fire_early_when_the_last_kick_leaves_the_history(double bpm)
    {
        // Regression: with a 0.5 s flux history the threshold collapsed between slow beats and
        // noise fired onsets ~90 ms early, which the refractory period then turned into missed kicks.
        var (samples, beats) = SyntheticMusic.KickTrack(44100, bpm, seconds: 20, seed: 2);
        var detector = new BeatDetector(44100);
        var onsets = new List<double>();
        detector.Onset += t => { if (t.TotalSeconds >= 0.9) onsets.Add(t.TotalSeconds); };
        detector.Process(samples);

        double f1 = SyntheticMusic.F1(onsets, beats, Tolerance);
        Assert.True(f1 >= 0.85, $"F1 {f1:0.000} at {bpm} BPM, 44.1 kHz");
    }

    [Fact]
    public void Auto_gain_keeps_quiet_tracks_working()
    {
        // About -26 dB quieter, still above the -60 dBFS silence floor.
        var (samples, beats) = SyntheticMusic.KickTrack(SampleRate, 120, seconds: 20, gain: 0.05f);
        var (onsets, detector) = Run(samples);

        double f1 = SyntheticMusic.F1(onsets, beats, Tolerance);
        Assert.True(f1 >= 0.9, $"F1 {f1:0.000} on a quiet track");
        Assert.False(detector.Latest.Silent);
    }

    [Fact]
    public void Onsets_are_never_closer_than_the_refractory_period()
    {
        var (samples, _) = SyntheticMusic.KickTrack(SampleRate, 174, seconds: 15);
        var (onsets, _) = Run(samples, sensitivity: 1f);

        for (int i = 1; i < onsets.Count; i++)
            Assert.True(onsets[i] - onsets[i - 1] >= 0.120 - 1e-6, $"Onsets at {onsets[i - 1]:0.000} and {onsets[i]:0.000}");
    }

    [Fact]
    public void Silence_is_reported_after_the_hold_time_and_fires_no_onsets()
    {
        var detector = new BeatDetector(SampleRate);
        int onsets = 0;
        detector.Onset += _ => onsets++;

        detector.ProcessSilence(SampleRate);           // 1 s: not yet silent
        Assert.False(detector.Latest.Silent);

        detector.ProcessSilence(SampleRate + 2048);    // > 2 s total
        Assert.True(detector.Latest.Silent);
        Assert.Equal(0, onsets);
        Assert.Equal(0f, detector.Latest.Level);
    }

    [Fact]
    public void Music_after_silence_clears_the_silent_flag()
    {
        var detector = new BeatDetector(SampleRate);
        detector.ProcessSilence(SampleRate * 3);
        Assert.True(detector.Latest.Silent);

        var (samples, _) = SyntheticMusic.KickTrack(SampleRate, 120, seconds: 2);
        detector.Process(samples);
        Assert.False(detector.Latest.Silent);
    }

    [Fact]
    public void Features_stay_in_range()
    {
        var (samples, _) = SyntheticMusic.KickTrack(SampleRate, 128, seconds: 10);
        var detector = new BeatDetector(SampleRate);
        int pos = 0;
        while (pos < samples.Length)
        {
            int len = Math.Min(512, samples.Length - pos);
            detector.Process(samples.AsSpan(pos, len));
            pos += len;
            var f = detector.Latest;
            Assert.InRange(f.Level, 0f, 1f);
            Assert.InRange(f.Bass, 0f, 1f);
            Assert.InRange(f.Pulse, 0f, 1f);
        }
    }

    [Theory]
    [InlineData(0f, 2.2f)]
    [InlineData(0.5f, 1.6f)]
    [InlineData(1f, 1.0f)]
    [InlineData(-3f, 2.2f)]
    [InlineData(9f, 1.0f)]
    public void Sensitivity_maps_to_threshold(float sensitivity, float expectedK)
    {
        Assert.InRange(BeatDetectorOptions.ThresholdForSensitivity(sensitivity), expectedK - 1e-5f, expectedK + 1e-5f);
    }
}
