namespace AmbiWin.Core.Audio;

/// <summary>
/// One analysis result, published about every 10.7 ms (512 samples at 48 kHz).
/// Immutable; this is the only audio type that crosses threads.
/// </summary>
/// <param name="Level">Normalized loudness envelope, 0..1 (auto-gain applied).</param>
/// <param name="Bass">Normalized 20–250 Hz energy, 0..1.</param>
/// <param name="Pulse">Onset impulse: 1 on a detected beat, decaying toward 0.</param>
/// <param name="Silent">True once the input has stayed under the silence floor for the hold time.</param>
/// <param name="StreamTime">Position in the analyzed stream (end of the analyzed frame).</param>
public readonly record struct AudioFeatures(float Level, float Bass, float Pulse, bool Silent, TimeSpan StreamTime)
{
    public static AudioFeatures SilentAt(TimeSpan streamTime) => new(0f, 0f, 0f, true, streamTime);
}
