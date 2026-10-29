namespace AmbiWin.Core.Color;

/// <summary>
/// Picks two glow colors from album art: k-means in OKLab, clusters scored by size and chroma,
/// then adjusted so dark or dull covers still glow. Deterministic: the same image always gives
/// the same palette (fixed seed, own RNG).
/// </summary>
public sealed class PaletteExtractor
{
    private readonly PaletteOptions _options;

    public PaletteExtractor(PaletteOptions? options = null) => _options = options ?? new PaletteOptions();

    /// <summary>
    /// Extracts a palette from 32-bit BGRA pixels (row-major, no padding). Pixels with alpha below 128 are ignored.
    /// Returns null when no usable pixels exist.
    /// </summary>
    public Palette? Extract(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            throw new ArgumentException("Pixel buffer is smaller than width × height × 4.", nameof(bgra));

        var points = new List<OkLab>(width * height);
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            if (bgra[o + 3] < 128) continue;
            points.Add(OkLab.FromSrgb8(bgra[o + 2], bgra[o + 1], bgra[o]));
        }
        if (points.Count == 0) return null;

        var (centroids, counts) = KMeans(points);
        var scored = Score(centroids, counts, points.Count);

        OkLab primary = scored[0].Color;
        OkLab? secondary = null;
        for (int i = 1; i < scored.Count; i++)
        {
            if (scored[i].Color.DistanceTo(primary) >= _options.MinSeparation)
            {
                secondary = scored[i].Color;
                break;
            }
        }

        OkLab glowPrimary = MakeGlowReady(primary);
        OkLab glowSecondary = secondary is { } s
            ? MakeGlowReady(s)
            : RotateHue(glowPrimary, _options.FallbackHueRotation);

        return new Palette(glowPrimary, glowSecondary);
    }

    /// <summary>Clamps lightness and raises chroma so the color reads as light; maps into sRGB by lowering chroma.</summary>
    public OkLab MakeGlowReady(OkLab color)
    {
        float l = Math.Clamp(color.L, _options.GlowMinL, _options.GlowMaxL);
        float c = color.Chroma;
        if (c < _options.AchromaticChroma)
            return new OkLab(l, 0f, 0f).GamutMapped();   // neutral cover → soft white glow, no invented hue
        c = Math.Max(c, _options.GlowMinChroma);
        return OkLab.FromLch(l, c, color.HueDegrees).GamutMapped();
    }

    private OkLab RotateHue(OkLab color, float degrees)
    {
        if (color.Chroma < _options.AchromaticChroma)
            return new OkLab(Math.Clamp(color.L - 0.12f, _options.GlowMinL - 0.12f, _options.GlowMaxL), 0f, 0f);
        return OkLab.FromLch(color.L, color.Chroma, (color.HueDegrees + degrees) % 360f).GamutMapped();
    }

    private List<(OkLab Color, float Score)> Score(OkLab[] centroids, int[] counts, int total)
    {
        var scored = new List<(OkLab, float)>(centroids.Length);
        for (int i = 0; i < centroids.Length; i++)
        {
            if (counts[i] == 0) continue;
            OkLab c = centroids[i];
            float share = (float)counts[i] / total;
            float score = share * (_options.ChromaBase + _options.ChromaWeight * c.Chroma);
            if (c.L < _options.DarkL || c.L > _options.LightL || c.Chroma < _options.GreyChroma)
                score *= _options.Penalty;
            scored.Add((c, score));
        }
        // Stable order: by score, then by lightness so ties never depend on cluster order.
        scored.Sort((x, y) =>
        {
            int byScore = y.Item2.CompareTo(x.Item2);
            return byScore != 0 ? byScore : y.Item1.L.CompareTo(x.Item1.L);
        });
        return scored;
    }

    private (OkLab[] Centroids, int[] Counts) KMeans(List<OkLab> points)
    {
        int k = Math.Min(_options.Clusters, points.Count);
        var rng = new SplitMix64(_options.Seed);
        var centroids = new OkLab[k];

        // k-means++ seeding.
        centroids[0] = points[(int)(rng.NextDouble() * points.Count)];
        var distances = new double[points.Count];
        for (int c = 1; c < k; c++)
        {
            double sum = 0;
            for (int i = 0; i < points.Count; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    double d = points[i].DistanceTo(centroids[j]);
                    best = Math.Min(best, d * d);
                }
                distances[i] = best;
                sum += best;
            }

            if (sum <= 0)
            {
                // All remaining points coincide with existing centroids.
                Array.Resize(ref centroids, c);
                k = c;
                break;
            }

            double pick = rng.NextDouble() * sum;
            int chosen = points.Count - 1;
            for (int i = 0; i < points.Count; i++)
            {
                pick -= distances[i];
                if (pick <= 0) { chosen = i; break; }
            }
            centroids[c] = points[chosen];
        }

        var assignment = new int[points.Count];
        var counts = new int[k];
        for (int iteration = 0; iteration < _options.Iterations; iteration++)
        {
            Array.Clear(counts);
            var sumL = new double[k];
            var sumA = new double[k];
            var sumB = new double[k];

            for (int i = 0; i < points.Count; i++)
            {
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int j = 0; j < k; j++)
                {
                    float d = points[i].DistanceTo(centroids[j]);
                    if (d < bestDistance) { bestDistance = d; best = j; }
                }
                assignment[i] = best;
                counts[best]++;
                sumL[best] += points[i].L;
                sumA[best] += points[i].A;
                sumB[best] += points[i].B;
            }

            for (int j = 0; j < k; j++)
            {
                if (counts[j] == 0) continue; // keep the old centroid for an empty cluster
                centroids[j] = new OkLab((float)(sumL[j] / counts[j]), (float)(sumA[j] / counts[j]), (float)(sumB[j] / counts[j]));
            }
        }

        return (centroids, counts);
    }

    /// <summary>Small deterministic RNG, independent of System.Random's implementation across .NET versions.</summary>
    private struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
    }
}
