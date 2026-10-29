using AmbiWin.Core.Color;

namespace AmbiWin.Core.Tests;

public class PaletteExtractorTests
{
    private const int Size = 64;

    /// <summary>Builds a 64×64 BGRA image from a function of (x, y) → (r, g, b, a).</summary>
    private static byte[] Image(Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var bgra = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int o = (y * Size + x) * 4;
                bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = a;
            }
        return bgra;
    }

    private static float HueDistance(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    [Fact]
    public void Two_color_cover_gives_those_two_hues()
    {
        var red = OkLab.FromSrgb8(220, 30, 40);
        var blue = OkLab.FromSrgb8(30, 60, 220);
        var image = Image((x, _) => x < 40 ? ((byte)220, (byte)30, (byte)40, (byte)255) : ((byte)30, (byte)60, (byte)220, (byte)255));

        var palette = new PaletteExtractor().Extract(image, Size, Size)
                      ?? throw new InvalidOperationException("Expected a palette.");

        Assert.True(HueDistance(palette.Primary.HueDegrees, red.HueDegrees) < 10f, "primary should be the larger red area");
        Assert.True(HueDistance(palette.Secondary.HueDegrees, blue.HueDegrees) < 10f, "secondary should be blue");
    }

    [Fact]
    public void Vivid_accent_beats_a_large_grey_background()
    {
        // 80% grey, 20% orange: the glow should be orange, not grey.
        var image = Image((x, y) => x < 13 ? ((byte)240, (byte)120, (byte)20, (byte)255) : ((byte)110, (byte)110, (byte)110, (byte)255));
        var palette = new PaletteExtractor().Extract(image, Size, Size)!.Value;

        var orange = OkLab.FromSrgb8(240, 120, 20);
        Assert.True(HueDistance(palette.Primary.HueDegrees, orange.HueDegrees) < 10f);
    }

    [Fact]
    public void Output_is_glow_ready_lightness_and_in_gamut()
    {
        // Very dark navy cover.
        var image = Image((_, _) => ((byte)10, (byte)15, (byte)45, (byte)255));
        var palette = new PaletteExtractor().Extract(image, Size, Size)!.Value;

        foreach (var c in new[] { palette.Primary, palette.Secondary })
        {
            Assert.InRange(c.L, 0.65f - 1e-3f, 0.82f + 1e-3f);
            Assert.True(c.ToLinearSrgb().IsInGamut());
        }
    }

    [Fact]
    public void Black_and_white_cover_gets_a_neutral_glow_not_an_invented_hue()
    {
        var image = Image((x, y) => (x + y) % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var palette = new PaletteExtractor().Extract(image, Size, Size)!.Value;

        Assert.True(palette.Primary.Chroma < 0.02f);
        Assert.True(palette.Secondary.Chroma < 0.02f);
    }

    [Fact]
    public void Same_image_always_gives_the_same_palette()
    {
        var image = Image((x, y) => ((byte)(x * 4), (byte)(y * 4), (byte)((x * y) % 256), (byte)255));
        var first = new PaletteExtractor().Extract(image, Size, Size);
        var second = new PaletteExtractor().Extract(image, Size, Size);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Fully_transparent_image_returns_null()
    {
        var image = Image((_, _) => ((byte)255, (byte)0, (byte)0, (byte)0));
        Assert.False(new PaletteExtractor().Extract(image, Size, Size).HasValue);
    }

    [Fact]
    public void Single_color_cover_still_gets_two_distinct_colors()
    {
        var image = Image((_, _) => ((byte)40, (byte)200, (byte)90, (byte)255));
        var palette = new PaletteExtractor().Extract(image, Size, Size)!.Value;
        Assert.True(palette.Primary.DistanceTo(palette.Secondary) > 0.05f);
    }
}
