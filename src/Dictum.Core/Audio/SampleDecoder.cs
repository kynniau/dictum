using System.Buffers.Binary;
using NAudio.Wave;

namespace Dictum.Audio;

public enum SampleEncoding { Float32, Int16, Int24, Int32 }

/// <summary>
/// Converts raw capture/WAV bytes to interleaved float32 in [-1, 1].
/// Handles plain and WAVE_FORMAT_EXTENSIBLE formats explicitly (by SubFormat), because the shared-mode
/// mix format is usually EXTENSIBLE-wrapped float and must not be decoded as 32-bit integers.
/// </summary>
public static class SampleDecoder
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    public static SampleEncoding EncodingOf(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        if (format is WaveFormatExtensible ext)
        {
            isFloat = ext.SubFormat == FloatSubFormat;
            isPcm = ext.SubFormat == PcmSubFormat;
        }
        return (isFloat, isPcm, format.BitsPerSample) switch
        {
            (true, _, 32) => SampleEncoding.Float32,
            (_, true, 16) => SampleEncoding.Int16,
            (_, true, 24) => SampleEncoding.Int24,
            (_, true, 32) => SampleEncoding.Int32,
            _ => throw new NotSupportedException($"Unsupported audio format: {format.Encoding}, {format.BitsPerSample}-bit."),
        };
    }

    public static int BytesPerSample(SampleEncoding e) => e switch
    {
        SampleEncoding.Int16 => 2,
        SampleEncoding.Int24 => 3,
        _ => 4,
    };

    /// <summary>Decodes whole samples from <paramref name="src"/> into <paramref name="dst"/>; returns the count written.</summary>
    public static int Decode(ReadOnlySpan<byte> src, SampleEncoding encoding, Span<float> dst)
    {
        var size = BytesPerSample(encoding);
        var count = Math.Min(src.Length / size, dst.Length);
        for (var i = 0; i < count; i++)
        {
            var s = src.Slice(i * size, size);
            dst[i] = encoding switch
            {
                SampleEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
                SampleEncoding.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                SampleEncoding.Int24 => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            };
        }
        return count;
    }

    /// <summary>Averages interleaved channels into mono. Returns the number of frames written.</summary>
    public static int Downmix(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        if (channels <= 1)
        {
            var n = Math.Min(interleaved.Length, mono.Length);
            interleaved[..n].CopyTo(mono);
            return n;
        }
        var frames = Math.Min(interleaved.Length / channels, mono.Length);
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            var baseIndex = f * channels;
            for (var c = 0; c < channels; c++) sum += interleaved[baseIndex + c];
            mono[f] = sum / channels;
        }
        return frames;
    }
}
