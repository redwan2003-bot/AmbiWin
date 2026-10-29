using AmbiWin.Core.Color;

namespace AmbiWin.Core.Tests;

public class ScreenColorFilterTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4] = b;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = r;
            bgra[i * 4 + 3] = 255;
        }
        return bgra;
    }

    private static byte[] Output(ScreenColorFilter f)
    {
        var rgba = new byte[f.Width * f.Height * 4];
        f.WriteRgba8(rgba, f.Width * 4);
        return rgba;
    }

    [Fact]
    public void FirstFrameIsShownAtOnceInRgbOrder()
    {
        var f = new ScreenColorFilter { ChromaBoost = 1f };
        f.SetFrame(Solid(4, 2, 200, 100, 50), 4, 2, 16);
        var o = Output(f);
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, o[..4]);
        Assert.False(f.Step(0.016f));
    }

    [Fact]
    public void EasesTowardNewFramesInsteadOfJumping()
    {
        var f = new ScreenColorFilter { ChromaBoost = 1f, SmoothingSeconds = 0.25f };
        f.SetFrame(Solid(2, 2, 0, 0, 0), 2, 2, 8);
        f.SetFrame(Solid(2, 2, 255, 255, 255), 2, 2, 8);

        Assert.True(f.Step(0.05f));
        byte early = Output(f)[0];
        Assert.InRange(early, 1, 254);

        for (int i = 0; i < 200; i++) f.Step(0.016f);
        Assert.Equal(255, Output(f)[0]);
        Assert.False(f.Step(0.016f));
    }

    [Fact]
    public void BoostKeepsGrayGrayAndSaturatesColor()
    {
        var f = new ScreenColorFilter { ChromaBoost = 1.3f };
        f.SetFrame(Solid(1, 2, 128, 128, 128), 1, 2, 4);
        var gray = Output(f);
        Assert.InRange(Math.Abs(gray[0] - gray[1]) + Math.Abs(gray[1] - gray[2]), 0, 2);

        var plain = new ScreenColorFilter { ChromaBoost = 1f };
        var boosted = new ScreenColorFilter { ChromaBoost = 1.3f };
        var muted = Solid(1, 1, 150, 110, 100);
        plain.SetFrame(muted, 1, 1, 4);
        boosted.SetFrame(muted, 1, 1, 4);
        var p = Output(plain);
        var b = Output(boosted);
        Assert.True(b[0] - b[2] > p[0] - p[2], "boost should widen the red–blue gap");
    }

    [Fact]
    public void HandlesRowPaddingAndResize()
    {
        var f = new ScreenColorFilter { ChromaBoost = 1f };
        var padded = new byte[2 * 32];
        padded[2] = 255;          // row 0, pixel 0, red
        padded[32 + 4 + 0] = 255; // row 1, pixel 1, blue
        f.SetFrame(padded, 2, 2, 32);
        var o = Output(f);
        Assert.Equal(255, o[0]);
        Assert.Equal(255, o[(1 * 2 + 1) * 4 + 2]);

        f.SetFrame(Solid(3, 1, 10, 20, 30), 3, 1, 12);
        Assert.Equal(3, f.Width);
        Assert.Equal(1, f.Height);
        Assert.Equal(10, Output(f)[0]);
    }
}
