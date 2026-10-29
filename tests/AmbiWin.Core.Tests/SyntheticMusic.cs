namespace AmbiWin.Core.Tests;

/// <summary>
/// Generates deterministic synthetic music for detector tests: kick drums on every beat,
/// hi-hats on the off-beats, a sustained chord pad and a little noise. Kick times are the ground truth.
/// </summary>
internal static class SyntheticMusic
{
    public static (float[] Samples, List<double> BeatTimes) KickTrack(
        int sampleRate, double bpm, double seconds, float gain = 1f, bool withPad = true, int seed = 1)
    {
        int n = (int)(sampleRate * seconds);
        var samples = new float[n];
        var beats = new List<double>();
        var rng = new Random(seed);
        double beatInterval = 60.0 / bpm;

        // Start after 1 s so the detector's history is warm, as in real playback.
        for (double t = 1.0; t < seconds - 0.3; t += beatInterval)
        {
            beats.Add(t);
            AddKick(samples, sampleRate, t);
            AddHat(samples, sampleRate, t + beatInterval / 2, rng);
        }

        for (int i = 0; i < n; i++)
        {
            double time = (double)i / sampleRate;
            float pad = withPad
                ? 0.06f * (MathF.Sin((float)(2 * Math.PI * 220.0 * time)) +
                           MathF.Sin((float)(2 * Math.PI * 277.18 * time)) +
                           MathF.Sin((float)(2 * Math.PI * 329.63 * time)))
                : 0f;
            float noise = (float)(rng.NextDouble() * 2 - 1) * 0.005f;
            samples[i] = Math.Clamp((samples[i] + pad + noise) * gain, -1f, 1f);
        }
        return (samples, beats);
    }

    private static void AddKick(float[] samples, int sampleRate, double start)
    {
        int s0 = (int)(start * sampleRate);
        int length = (int)(0.25 * sampleRate);
        double phase = 0;
        for (int i = 0; i < length && s0 + i < samples.Length; i++)
        {
            double t = (double)i / sampleRate;
            double freq = 50 + 90 * Math.Exp(-t * 30);   // pitch drop 140 Hz -> 50 Hz
            phase += 2 * Math.PI * freq / sampleRate;
            samples[s0 + i] += (float)(0.8 * Math.Exp(-t * 12) * Math.Sin(phase));
        }
    }

    private static void AddHat(float[] samples, int sampleRate, double start, Random rng)
    {
        int s0 = (int)(start * sampleRate);
        int length = (int)(0.05 * sampleRate);
        float previous = 0;
        for (int i = 0; i < length && s0 + i < samples.Length; i++)
        {
            double t = (double)i / sampleRate;
            float white = (float)(rng.NextDouble() * 2 - 1);
            float high = white - previous; // crude high-pass
            previous = white;
            samples[s0 + i] += (float)(0.15 * Math.Exp(-t * 80)) * high;
        }
    }

    /// <summary>Greedy one-to-one matching of detections to labels within a tolerance, as BeatBench scores.</summary>
    public static double F1(IReadOnlyList<double> detected, IReadOnlyList<double> labels, double tolerance)
    {
        int hits = 0, j = 0;
        foreach (double d in detected)
        {
            while (j < labels.Count && labels[j] < d - tolerance) j++;
            if (j < labels.Count && Math.Abs(labels[j] - d) <= tolerance) { hits++; j++; }
        }
        if (detected.Count == 0 || labels.Count == 0) return 0;
        double p = (double)hits / detected.Count, r = (double)hits / labels.Count;
        return p + r == 0 ? 0 : 2 * p * r / (p + r);
    }
}
