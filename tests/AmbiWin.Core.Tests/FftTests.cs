using AmbiWin.Core.Audio;

namespace AmbiWin.Core.Tests;

public class FftTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(511)]
    public void Sine_at_bin_k_peaks_at_bin_k(int bin)
    {
        const int n = 1024;
        var fft = new Fft(n);
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
            re[i] = MathF.Sin(2f * MathF.PI * bin * i / n);

        fft.Forward(re, im);
        var mags = new float[fft.BinCount];
        fft.Magnitudes(re, im, mags);

        int peak = Array.IndexOf(mags, mags.Max());
        Assert.Equal(bin, peak);
        Assert.InRange(mags[bin], n / 2f - 0.5f, n / 2f + 0.5f); // amplitude-1 sine -> N/2
    }

    [Fact]
    public void Constant_signal_puts_everything_in_bin_zero()
    {
        var fft = new Fft(64);
        var re = Enumerable.Repeat(1f, 64).ToArray();
        var im = new float[64];
        fft.Forward(re, im);
        var mags = new float[fft.BinCount];
        fft.Magnitudes(re, im, mags);

        Assert.InRange(mags[0], 63.99f, 64.01f);
        for (int k = 1; k < mags.Length; k++)
            Assert.InRange(mags[k], 0f, 1e-3f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    public void Rejects_sizes_that_are_not_powers_of_two(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(size));
    }
}
