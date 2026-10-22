using NAudio.Utils;
using NAudio.Wave;

namespace Dictum.Audio;

/// <summary>The one WAV encoder: calibration's saved takes and cloud speech's in-memory upload are the same bytes.</summary>
public static class Wav
{
    /// <summary>
    /// 16 kHz mono 16-bit PCM (NAudio's WaveFileWriter), in memory. Each sample is clamped first: a resampler overshoot must not
    /// wrap round in 16-bit.
    /// </summary>
    public static byte[] Encode(float[] samples16kMono)
    {
        var samples = samples16kMono.Select(s => Math.Clamp(s, -1f, 1f)).ToArray();
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(memory), new WaveFormat(MonoResampler.WhisperRate, 16, 1)))
        {
            writer.WriteSamples(samples, 0, samples.Length);
        }
        return memory.ToArray();
    }
}
