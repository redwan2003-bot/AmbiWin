namespace AmbiWin.Core.Motion;

/// <summary>
/// One-pole smoother with separate rise and fall times: y += (x − y)(1 − e^(−dt/τ)).
/// Frame-rate independent, so the glow feels the same at 30, 60 or 144 Hz.
/// </summary>
public struct OnePole
{
    public OnePole(float initial = 0f) => Value = initial;

    public float Value { get; private set; }

    public float Step(float target, float dtSeconds, float attackSeconds, float releaseSeconds)
    {
        if (dtSeconds <= 0f) return Value;
        float tau = target > Value ? attackSeconds : releaseSeconds;
        if (tau <= 0f)
        {
            Value = target;
            return Value;
        }
        Value += (target - Value) * (1f - MathF.Exp(-dtSeconds / tau));
        return Value;
    }

    public void Reset(float value) => Value = value;
}

public static class Ease
{
    /// <summary>Smooth ease-in-out over t in [0, 1] (cubic).</summary>
    public static float InOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
    }
}
