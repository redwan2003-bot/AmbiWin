using System.Numerics;
using System.Runtime.InteropServices;

namespace AmbiWin.Render;

/// <summary>Constant buffer for the glow shader. Layout matches the HLSL cbuffer (16-byte registers).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GlowConstants
{
    public Vector2 MonitorSize;   // physical px
    public Vector2 StripOffset;   // strip origin within the monitor, px
    public float Thickness;       // px (DPI-scaled)
    public float Spread;          // px (DPI-scaled)
    public float CornerRadius;    // px (DPI-scaled)
    public float Intensity;       // 0..1
    public Vector3 ColorA;        // linear sRGB
    public float Mix;             // 0..1 share of ColorA
    public Vector3 ColorB;        // linear sRGB
    public float Phase;           // 0..1, slow drift of the gradient around the screen
    public float Level;           // 0..1, eased loudness
    public float Pulse;           // 0..1, eased onset impulse
    public float StripDepth;      // px: light fades to zero before the strip edge
    public float ScreenMix;       // 0..1 share of screen colors (Ambilight mode)
    public Vector4 Inset;         // px in from left, top, right, bottom: the glow follows this inner rectangle (taskbar wrap)

    public static readonly uint Size = (uint)Marshal.SizeOf<GlowConstants>();
}

internal static class GlowShader
{
    /// <summary>
    /// Full-screen triangle from SV_VertexID (no vertex buffer), and the glow pixel shader.
    /// Every pixel knows its position in monitor coordinates, so the four strips join seamlessly.
    /// </summary>
    public const string Source = """
        cbuffer Glow : register(b0)
        {
            float2 MonitorSize;  float2 StripOffset;
            float  Thickness;    float  Spread;
            float  CornerRadius; float  Intensity;
            float3 ColorA;       float  Mix;
            float3 ColorB;       float  Phase;
            float  Level;        float  Pulse;
            float  StripDepth;   float  ScreenMix;
            float4 Inset;        // left, top, right, bottom
        };

        Texture2D    ScreenColors : register(t0);   // tiny, smoothed copy of the screen (linear light)
        SamplerState Linear       : register(s0);

        float4 VSMain(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        // Signed distance to a rounded rectangle centered at the origin (negative inside).
        float sdRoundRect(float2 p, float2 halfSize, float r)
        {
            float2 q = abs(p) - halfSize + r;
            return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
        }

        float hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

        float4 PSMain(float4 pos : SV_Position) : SV_Target
        {
            float2 p = pos.xy + StripOffset;
            float2 lo = Inset.xy, hi = MonitorSize - Inset.zw;          // the edge the light follows
            float2 center = (lo + hi) * 0.5;
            float d = -sdRoundRect(p - center, (hi - lo) * 0.5, CornerRadius); // px inward from that edge
            if (d < 0) return float4(0, 0, 0, 0);                        // outside rounded corners

            float spread = max(Spread * (1.0 + 0.35 * Pulse), 1.0);    // a beat pushes light further in
            float core = 1.0 - smoothstep(Thickness - 1.0, Thickness + 1.0, d);
            float halo = exp(-max(d - Thickness, 0.0) / (spread * 0.35));
            float fade = 1.0 - smoothstep(StripDepth * 0.75, StripDepth, d); // never a hard edge at the strip boundary
            float a = saturate(core + halo) * fade * Intensity * (0.55 + 0.45 * Level);

            // Two-color gradient around the screen; Mix is ColorA's share.
            float2 v = p - center;
            float u = atan2(v.y, v.x) / 6.2831853 + 0.5 + Phase;
            float t = 0.5 + 0.5 * cos(6.2831853 * u);
            float w = smoothstep(1.0 - Mix - 0.3, 1.0 - Mix + 0.3, t);
            float3 c = lerp(ColorB, ColorA, w);

            // Ambilight: the color of the content just inside this point of the edge.
            if (ScreenMix > 0)
            {
                float2 inset = MonitorSize * float2(0.04, 0.06);
                float2 q = clamp(p, lo + inset, hi - inset) / MonitorSize;
                c = lerp(c, ScreenColors.SampleLevel(Linear, q, 0).rgb, ScreenMix);
            }

            c += (hash(pos.xy) - 0.5) / 255.0;                           // dither against banding
            return float4(c * a, a);                                     // premultiplied alpha
        }
        """;
}
