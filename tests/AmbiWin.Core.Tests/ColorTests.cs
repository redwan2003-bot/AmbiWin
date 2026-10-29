using AmbiWin.Core.Color;

namespace AmbiWin.Core.Tests;

public class ColorTests
{
    [Fact]
    public void Srgb_red_matches_the_published_OKLab_value()
    {
        // Reference: Björn Ottosson, "A perceptual color space for image processing" (2020).
        var red = OkLab.FromSrgb8(255, 0, 0);
        Assert.InRange(red.L, 0.6279f - 0.001f, 0.6279f + 0.001f);
        Assert.InRange(red.A, 0.2249f - 0.001f, 0.2249f + 0.001f);
        Assert.InRange(red.B, 0.1258f - 0.001f, 0.1258f + 0.001f);
    }

    [Fact]
    public void White_is_neutral_with_full_lightness()
    {
        var white = OkLab.FromSrgb8(255, 255, 255);
        Assert.InRange(white.L, 0.999f, 1.001f);
        Assert.InRange(white.Chroma, 0f, 1e-3f);
    }

    [Theory]
    [InlineData("#7C5CFF")]
    [InlineData("#00D1FF")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#123456")]
    public void Hex_round_trips(string hex)
    {
        Assert.True(Srgb.TryParseHex(hex, out var color));
        Assert.Equal(hex, Srgb.ToHex(color));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Invalid_hex_is_rejected(string? hex)
    {
        Assert.False(Srgb.TryParseHex(hex, out _));
    }

    [Fact]
    public void Gamut_mapping_keeps_lightness_and_hue()
    {
        var outOfGamut = OkLab.FromLch(0.7f, 0.4f, 150f); // far too saturated for sRGB
        Assert.False(outOfGamut.ToLinearSrgb().IsInGamut());

        var mapped = outOfGamut.GamutMapped();
        Assert.True(mapped.ToLinearSrgb().IsInGamut());
        Assert.InRange(mapped.L, 0.699f, 0.701f);
        Assert.InRange(mapped.HueDegrees, 149.5f, 150.5f);
        Assert.True(mapped.Chroma < 0.4f);
    }
}
