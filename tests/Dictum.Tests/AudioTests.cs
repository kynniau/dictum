using System.Buffers.Binary;
using Dictum.Audio;
using NAudio.Wave;

namespace Dictum.Tests;

public class ResamplerTests
{
    private static byte[] StereoFloatSine(double frequency, int sampleRate, double seconds, float amplitude = 0.5f)
    {
        var frames = (int)(sampleRate * seconds);
        var bytes = new byte[frames * 2 * 4];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), v);      // left
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 4), v);  // right
        }
        return bytes;
    }

    /// <summary>Frequency estimated from rising zero crossings, ignoring the resampler's start-up transient.</summary>
    private static double EstimateFrequency(float[] samples, int sampleRate)
    {
        var start = sampleRate / 20; // skip 50 ms
        int first = -1, last = -1, crossings = 0;
        for (var i = start + 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                if (first < 0) first = i;
                else crossings++;
                last = i;
            }
        }
        return crossings * (double)sampleRate / (last - first);
    }

    [Fact]
    public void Sine_440Hz_48kHz_stereo_becomes_16kHz_mono_with_same_frequency_and_length()
    {
        var buffer = new CaptureBuffer(SampleEncoding.Float32, channels: 2, sampleRate: 48000);
        var bytes = StereoFloatSine(440, 48000, 2.0);
        const int chunk = 480 * 8; // 10 ms device packets
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            buffer.Append(bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)));
        }
        var audio = buffer.Seal();

        Assert.InRange(audio.Samples.Length, 32000 * 0.99, 32000 * 1.01);        // 2.0 s at 16 kHz, within 1%
        Assert.InRange(EstimateFrequency(audio.Samples, 16000), 440 * 0.995, 440 * 1.005);
        Assert.InRange(audio.PeakLevel, 0.30f, 0.40f);                            // RMS of a 0.5 sine is 0.354
        Assert.Equal(TimeSpan.FromSeconds(2.0).TotalSeconds, audio.Duration.TotalSeconds, 0.02);
    }

    [Fact]
    public void Whole_buffer_resample_matches_rate_ratio()
    {
        var mono = Enumerable.Range(0, 44100).Select(i => (float)Math.Sin(2 * Math.PI * 1000 * i / 44100)).ToArray();
        var output = MonoResampler.ResampleAll(mono, 44100);
        Assert.InRange(output.Length, 16000 * 0.99, 16000 * 1.01);
        Assert.InRange(EstimateFrequency(output, 16000), 995, 1005);
    }

    [Fact]
    public void Sixteen_kHz_input_passes_through_unchanged()
    {
        var mono = new[] { 0.1f, -0.2f, 0.3f };
        Assert.Equal(mono, MonoResampler.ResampleAll(mono, 16000));
    }

    [Fact]
    public void Decoder_handles_pcm16_pcm24_and_extensible_float()
    {
        Span<float> dst = stackalloc float[2];
        byte[] pcm16 = [0x00, 0x40, 0x00, 0xC0];               // +0.5, -0.5
        Assert.Equal(2, SampleDecoder.Decode(pcm16, SampleEncoding.Int16, dst));
        Assert.Equal([0.5f, -0.5f], dst.ToArray());

        byte[] pcm24 = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0];   // +0.5, -0.5
        SampleDecoder.Decode(pcm24, SampleEncoding.Int24, dst);
        Assert.Equal([0.5f, -0.5f], dst.ToArray());

        var extensibleFloat = new WaveFormatExtensible(48000, 32, 2, new Guid("00000003-0000-0010-8000-00aa00389b71"), 0, 3);
        Assert.Equal(SampleEncoding.Float32, SampleDecoder.EncodingOf(extensibleFloat));
        Assert.Equal(SampleEncoding.Int16, SampleDecoder.EncodingOf(new WaveFormat(16000, 16, 1)));
        Assert.Equal(SampleEncoding.Float32, SampleDecoder.EncodingOf(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)));
    }

    [Fact]
    public void Downmix_averages_channels()
    {
        float[] interleaved = [1f, 0f, 0.5f, 0.5f, -1f, 1f];
        var mono = new float[3];
        Assert.Equal(3, SampleDecoder.Downmix(interleaved, 2, mono));
        Assert.Equal([0.5f, 0.5f, 0f], mono);
    }

    [Fact]
    public void Sealed_buffer_ignores_late_packets()
    {
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, 16000);
        buffer.AppendResampled(new float[1600]);
        var audio = buffer.Seal();
        buffer.AppendResampled(new float[1600]);
        Assert.Equal(1600, audio.Samples.Length);
        Assert.Equal(1600, buffer.SampleCount);
    }
}

public class RecordingGateTests
{
    private static CapturedAudio Audio(double seconds, float peak) => new(new float[(int)(16000 * seconds)], peak);

    [Fact]
    public void Short_recordings_are_discarded()
    {
        Assert.Contains("too short", RecordingGate.RejectReason(Audio(0.25, 0.2f), 300, 0.01));
        Assert.Null(RecordingGate.RejectReason(Audio(0.31, 0.2f), 300, 0.01));
    }

    [Fact]
    public void Silent_recordings_are_discarded()
    {
        Assert.Contains("silent", RecordingGate.RejectReason(Audio(3, 0.004f), 300, 0.01));
        Assert.Null(RecordingGate.RejectReason(Audio(3, 0.05f), 300, 0.01));
    }
}
