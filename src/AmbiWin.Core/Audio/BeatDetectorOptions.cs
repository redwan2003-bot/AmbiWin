namespace AmbiWin.Core.Audio;

/// <summary>
/// Tuning values for <see cref="BeatDetector"/>. Defaults are starting points from the technical design,
/// to be tuned with BeatBench, not measured results.
/// </summary>
public sealed record BeatDetectorOptions
{
    public int FftSize { get; init; } = 1024;
    public int HopSize { get; init; } = 512;

    /// <summary>Onset threshold = mean + K·σ of recent flux. Lower K = more sensitive.</summary>
    public float ThresholdK { get; init; } = 1.6f;

    public float RefractoryMs { get; init; } = 120f;
    public float PulseDecayMs { get; init; } = 150f;
    public float AutoGainHalfLifeSeconds { get; init; } = 8f;
    public float SilenceFloorDb { get; init; } = -60f;
    public float SilenceHoldSeconds { get; init; } = 2f;
    /// <summary>
    /// Must exceed one beat interval at slow tempos: with 0.5 s, the previous kick leaves the history before the
    /// next arrives at under ~120 BPM, the threshold collapses and noise fires early onsets.
    /// </summary>
    public float FluxHistorySeconds { get; init; } = 1.0f;

    /// <summary>Weight of the per-bin average flux at or below <see cref="BassMaxHz"/>.</summary>
    public float BassWeight { get; init; } = 1.0f;

    /// <summary>Weight of the per-bin average flux above <see cref="BassMaxHz"/>.</summary>
    public float OtherWeight { get; init; } = 0.2f;

    /// <summary>
    /// An onset must also exceed this multiple of the recent mean flux. Stops noise from firing
    /// during smooth passages, where σ is tiny and mean + K·σ sits barely above the mean.
    /// </summary>
    /// <remarks>
    /// Chosen by a sweep on synthetic tracks (44.1/48 kHz, 70–174 BPM, full and −26 dB gain, two noise seeds):
    /// with a 1.0 s history, mean F1 0.989, worst 0.842. Provisional until tuned on real clips in BeatBench.
    /// </remarks>
    public float MinFluxOverMean { get; init; } = 3.0f;

    public float BassMinHz { get; init; } = 20f;
    public float BassMaxHz { get; init; } = 250f;

    /// <summary>Maps the 0..1 sensitivity setting to <see cref="ThresholdK"/>: 0 → 2.2 (fewer beats), 1 → 1.0.</summary>
    public static float ThresholdForSensitivity(float sensitivity) => 2.2f - 1.2f * Math.Clamp(sensitivity, 0f, 1f);

    public BeatDetectorOptions WithSensitivity(float sensitivity) =>
        this with { ThresholdK = ThresholdForSensitivity(sensitivity) };
}
