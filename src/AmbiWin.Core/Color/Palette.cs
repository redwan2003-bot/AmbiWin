namespace AmbiWin.Core.Color;

/// <summary>The two glow colors. Immutable; crosses threads as a snapshot.</summary>
public readonly record struct Palette(OkLab Primary, OkLab Secondary)
{
    /// <summary>Cross-fade in OKLab, where interpolation stays perceptually even.</summary>
    public static Palette Lerp(Palette from, Palette to, float t) =>
        new(OkLab.Lerp(from.Primary, to.Primary, t), OkLab.Lerp(from.Secondary, to.Secondary, t));

    public override string ToString() => $"{Srgb.ToHex(Primary)} / {Srgb.ToHex(Secondary)}";
}

/// <summary>Tuning values for <see cref="PaletteExtractor"/>. Starting points from the technical design.</summary>
public sealed record PaletteOptions
{
    public int Clusters { get; init; } = 6;
    public int Iterations { get; init; } = 8;
    public ulong Seed { get; init; } = 0x9E3779B97F4A7C15UL;

    /// <summary>score = share × (ChromaBase + ChromaWeight × chroma).</summary>
    public float ChromaBase { get; init; } = 0.3f;
    public float ChromaWeight { get; init; } = 1.0f;

    public float DarkL { get; init; } = 0.2f;
    public float LightL { get; init; } = 0.92f;
    public float GreyChroma { get; init; } = 0.03f;
    public float Penalty { get; init; } = 0.2f;

    /// <summary>Minimum OKLab distance between primary and secondary.</summary>
    public float MinSeparation { get; init; } = 0.12f;
    public float FallbackHueRotation { get; init; } = 40f;

    public float GlowMinL { get; init; } = 0.65f;
    public float GlowMaxL { get; init; } = 0.82f;
    public float GlowMinChroma { get; init; } = 0.10f;

    /// <summary>Below this chroma a color is treated as neutral and is not given an invented hue.</summary>
    public float AchromaticChroma { get; init; } = 0.02f;
}
