namespace AmbiWin.Core.Audio;

/// <summary>
/// In-place iterative radix-2 complex FFT for a fixed power-of-two size.
/// Twiddle factors and the bit-reversal table are computed once; <see cref="Forward"/> does not allocate.
/// </summary>
public sealed class Fft
{
    private readonly int[] _bitReverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "FFT size must be a power of two and at least 2.");

        Size = size;
        int bits = System.Numerics.BitOperations.Log2((uint)size);

        _bitReverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _bitReverse[i] = r;
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double angle = 2.0 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(angle);
            _sin[k] = (float)Math.Sin(angle);
        }
    }

    public int Size { get; }

    /// <summary>Number of non-redundant magnitude bins for real input: Size / 2 + 1.</summary>
    public int BinCount => Size / 2 + 1;

    /// <summary>Forward transform (e^{-i 2π kn/N}) of <paramref name="re"/> + i·<paramref name="im"/>, in place.</summary>
    public void Forward(Span<float> re, Span<float> im)
    {
        if (re.Length != Size || im.Length != Size)
            throw new ArgumentException($"Both spans must have length {Size}.");

        for (int i = 0; i < Size; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= Size; len <<= 1)
        {
            int half = len >> 1;
            int step = Size / len;
            for (int start = 0; start < Size; start += len)
            {
                for (int j = 0; j < half; j++)
                {
                    float wr = _cos[j * step];
                    float wi = -_sin[j * step];
                    int a = start + j;
                    int b = a + half;
                    float tr = wr * re[b] - wi * im[b];
                    float ti = wr * im[b] + wi * re[b];
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    /// <summary>Writes |X[k]| for k in [0, Size/2] into <paramref name="magnitudes"/>.</summary>
    public void Magnitudes(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> magnitudes)
    {
        if (magnitudes.Length < BinCount)
            throw new ArgumentException($"Destination needs at least {BinCount} elements.", nameof(magnitudes));

        for (int k = 0; k < BinCount; k++)
            magnitudes[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
    }

    /// <summary>Fills <paramref name="window"/> with a periodic Hann window.</summary>
    public static void Hann(Span<float> window)
    {
        int n = window.Length;
        for (int i = 0; i < n; i++)
            window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / n);
    }
}
