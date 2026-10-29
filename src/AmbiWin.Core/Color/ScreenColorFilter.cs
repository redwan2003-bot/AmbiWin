namespace AmbiWin.Core.Color;

/// <summary>
/// Turns small, downscaled screen frames into the glow's "screen colors" (Ambilight mode): converts them
/// to linear light, eases toward each new frame so cuts and flicker fade instead of strobing, and gives the
/// result a little extra saturation, as light spilling from a screen looks richer than the pixels themselves.
/// </summary>
public sealed class ScreenColorFilter
{
    private float[] _current = [];
    private float[] _target = [];
    private bool _hasFrame;

    /// <summary>Time for the shown colors to cover ~63% of the way to a new frame.</summary>
    public float SmoothingSeconds { get; init; } = 0.25f;

    /// <summary>Chroma multiplier in OKLab (1 = unchanged).</summary>
    public float ChromaBoost { get; init; } = 1.3f;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool HasFrame => _hasFrame;

    /// <summary>Sets the frame to ease toward. <paramref name="bgra"/> holds 8-bit sRGB pixels in B, G, R, A order.</summary>
    public void SetFrame(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4)
            throw new ArgumentException("Frame buffer is smaller than width × height.", nameof(bgra));

        bool resized = width != Width || height != Height;
        if (resized)
        {
            Width = width;
            Height = height;
            _target = new float[width * height * 3];
            _current = new float[width * height * 3];
        }

        int i = 0;
        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++, i += 3)
            {
                _target[i] = Srgb.ToLinear(row[x * 4 + 2]);
                _target[i + 1] = Srgb.ToLinear(row[x * 4 + 1]);
                _target[i + 2] = Srgb.ToLinear(row[x * 4]);
            }
        }

        if (!_hasFrame || resized) _target.AsSpan().CopyTo(_current); // first frame: show it at once
        _hasFrame = true;
    }

    /// <summary>Eases the shown colors toward the latest frame. Returns true while they are still visibly changing.</summary>
    public bool Step(float dt)
    {
        if (!_hasFrame || dt <= 0) return false;
        float k = 1f - MathF.Exp(-dt / Math.Max(SmoothingSeconds, 1e-3f));
        float maxDelta = 0;
        for (int i = 0; i < _current.Length; i++)
        {
            float delta = _target[i] - _current[i];
            _current[i] += delta * k;
            maxDelta = Math.Max(maxDelta, Math.Abs(delta));
        }
        return maxDelta > 0.002f;
    }

    /// <summary>Writes the shown colors (boosted, sRGB-encoded) as R, G, B, A bytes.</summary>
    public void WriteRgba8(Span<byte> rgba, int stride)
    {
        if (!_hasFrame) return;
        if (stride < Width * 4 || rgba.Length < stride * (Height - 1) + Width * 4)
            throw new ArgumentException("Destination is smaller than width × height.", nameof(rgba));

        int i = 0;
        for (int y = 0; y < Height; y++)
        {
            var row = rgba.Slice(y * stride, Width * 4);
            for (int x = 0; x < Width; x++, i += 3)
            {
                var c = new LinearRgb(_current[i], _current[i + 1], _current[i + 2]);
                if (ChromaBoost != 1f)
                {
                    var lab = OkLab.FromLinearSrgb(c);
                    c = new OkLab(lab.L, lab.A * ChromaBoost, lab.B * ChromaBoost).ToLinearSrgb();
                }
                row[x * 4] = Encode(c.R);
                row[x * 4 + 1] = Encode(c.G);
                row[x * 4 + 2] = Encode(c.B);
                row[x * 4 + 3] = 255;
            }
        }
    }

    private static byte Encode(float linear) =>
        (byte)Math.Clamp(MathF.Round(Srgb.EncodeChannel(Math.Clamp(linear, 0f, 1f)) * 255f), 0f, 255f);
}
