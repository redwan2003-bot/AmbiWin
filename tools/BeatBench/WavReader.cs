using System.Buffers.Binary;

namespace BeatBench;

/// <summary>Minimal RIFF/WAVE reader: PCM 16/24/32-bit integer or 32-bit float, any channel count, mixed to mono.</summary>
public static class WavReader
{
    public static (float[] Mono, int SampleRate) ReadMono(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 12 || ReadTag(data, 0) != "RIFF" || ReadTag(data, 8) != "WAVE")
            throw new InvalidDataException($"{path}: not a RIFF/WAVE file.");

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        int dataOffset = -1, dataLength = 0;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            string id = ReadTag(data, pos);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
                if (format == 0xFFFE && size >= 26) // WAVE_FORMAT_EXTENSIBLE: real format is in the sub-format GUID
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 24));
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, data.Length - body);
                break;
            }
            pos = body + size + (size & 1);
        }

        if (dataOffset < 0 || channels == 0)
            throw new InvalidDataException($"{path}: missing fmt or data chunk.");

        int bytesPerSample = bits / 8;
        int frameBytes = bytesPerSample * channels;
        int frames = dataLength / frameBytes;
        var mono = new float[frames];

        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += ReadSample(data.AsSpan(dataOffset + f * frameBytes + c * bytesPerSample), format, bits);
            mono[f] = sum / channels;
        }
        return (mono, sampleRate);
    }

    private static float ReadSample(ReadOnlySpan<byte> s, int format, int bits) => (format, bits) switch
    {
        (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        (1, 24) => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
        (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
        _ => throw new NotSupportedException($"WAV format {format} with {bits} bits is not supported."),
    };

    private static string ReadTag(byte[] data, int offset) =>
        System.Text.Encoding.ASCII.GetString(data, offset, 4);

    /// <summary>Writes 16-bit mono PCM. Used to create synthetic test clips.</summary>
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (float x in samples)
            w.Write((short)Math.Clamp(x * 32767f, -32768f, 32767f));
    }
}
