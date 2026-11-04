using Dictum.Audio;
using Dictum.Platform;
using NAudio.Wave;

namespace Dictum.Windows.Audio;

/// <summary>Uses the codecs installed in Windows Media Foundation, including the audio track of MP4.</summary>
public sealed class MediaFoundationDecoder : IAudioDecoder
{
    public CapturedAudio Decode(string path)
    {
        // WAV parsing is shared with Core, including PCM/float, downmix and MonoResampler.
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            return AudioFiles.ReadWav(path);
        using var reader = new MediaFoundationReader(path);
        var format = reader.WaveFormat;
        var buffer = new CaptureBuffer(SampleDecoder.EncodingOf(format), format.Channels, format.SampleRate);
        var chunk = new byte[format.BlockAlign * 4096];
        int count;
        while ((count = reader.Read(chunk, 0, chunk.Length)) > 0)
            buffer.Append(chunk.AsSpan(0, count - count % format.BlockAlign));
        return buffer.Seal();
    }
}
