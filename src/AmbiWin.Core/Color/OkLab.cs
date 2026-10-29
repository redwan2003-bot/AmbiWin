namespace AmbiWin.Core.Color;

/// <summary>A color in the OKLab space (Björn Ottosson, 2020). L in [0, 1]; a and b roughly in [-0.4, 0.4].</summary>
public readonly record struct OkLab(float L, float A, float B)
{
    /// <summary>Chroma: distance from the neutral axis.</summary>
    public float Chroma => MathF.Sqrt(A * A + B * B);

    /// <summary>Hue angle in degrees, [0, 360).</summary>
    public float HueDegrees
    {
        get
        {
            float h = MathF.Atan2(B, A) * (180f / MathF.PI);
            return h < 0 ? h + 360f : h;
        }
    }

    /// <summary>Euclidean distance in OKLab (a perceptual color difference).</summary>
    public float DistanceTo(OkLab other)
    {
        float dl = L - other.L, da = A - other.A, db = B - other.B;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    public static OkLab Lerp(OkLab from, OkLab to, float t) =>
        new(from.L + (to.L - from.L) * t, from.A + (to.A - from.A) * t, from.B + (to.B - from.B) * t);

    public static OkLab FromLch(float l, float chroma, float hueDegrees)
    {
        float h = hueDegrees * (MathF.PI / 180f);
        return new OkLab(l, chroma * MathF.Cos(h), chroma * MathF.Sin(h));
    }

    public static OkLab FromLinearSrgb(LinearRgb c)
    {
        float l = 0.4122214708f * c.R + 0.5363325363f * c.G + 0.0514459929f * c.B;
        float m = 0.2119034982f * c.R + 0.6806995451f * c.G + 0.1073969566f * c.B;
        float s = 0.0883024619f * c.R + 0.2817188376f * c.G + 0.6299787005f * c.B;

        float l_ = MathF.Cbrt(l), m_ = MathF.Cbrt(m), s_ = MathF.Cbrt(s);

        return new OkLab(
            0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
            1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
            0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_);
    }

    public LinearRgb ToLinearSrgb()
    {
        float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
        float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
        float s_ = L - 0.0894841775f * A - 1.2914855480f * B;

        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;

        return new LinearRgb(
            +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    public static OkLab FromSrgb8(byte r, byte g, byte b) =>
        FromLinearSrgb(new LinearRgb(Srgb.ToLinear(r), Srgb.ToLinear(g), Srgb.ToLinear(b)));

    /// <summary>
    /// Returns this color moved into the sRGB gamut by lowering chroma at constant lightness and hue
    /// (never by clipping channels, which shifts hue).
    /// </summary>
    public OkLab GamutMapped()
    {
        if (ToLinearSrgb().IsInGamut())
            return this;

        float lightness = Math.Clamp(L, 0f, 1f);
        float hue = HueDegrees;
        float lo = 0f, hi = Chroma;
        for (int i = 0; i < 24; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (FromLch(lightness, mid, hue).ToLinearSrgb().IsInGamut()) lo = mid;
            else hi = mid;
        }
        return FromLch(lightness, lo, hue);
    }
}

/// <summary>Linear-light sRGB. Values outside [0, 1] are out of gamut.</summary>
public readonly record struct LinearRgb(float R, float G, float B)
{
    private const float Epsilon = 1e-4f;

    public bool IsInGamut() =>
        R >= -Epsilon && R <= 1 + Epsilon &&
        G >= -Epsilon && G <= 1 + Epsilon &&
        B >= -Epsilon && B <= 1 + Epsilon;
}

/// <summary>sRGB transfer functions and 8-bit helpers.</summary>
public static class Srgb
{
    private static readonly float[] ToLinearTable = BuildTable();

    private static float[] BuildTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
            table[i] = DecodeChannel(i / 255f);
        return table;
    }

    /// <summary>8-bit sRGB channel to linear light (table lookup).</summary>
    public static float ToLinear(byte value) => ToLinearTable[value];

    public static float DecodeChannel(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float EncodeChannel(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Parses "#RRGGBB" (the leading # is optional).</summary>
    public static bool TryParseHex(string? hex, out OkLab color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return false;
        color = OkLab.FromSrgb8((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Formats an OKLab color as "#RRGGBB" after gamut mapping.</summary>
    public static string ToHex(OkLab color)
    {
        LinearRgb c = color.GamutMapped().ToLinearSrgb();
        static int To8(float linear) => (int)MathF.Round(EncodeChannel(linear) * 255f);
        return $"#{To8(c.R):X2}{To8(c.G):X2}{To8(c.B):X2}";
    }
}
