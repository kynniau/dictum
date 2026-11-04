using Dictum.Audio;
using Dictum.Platform;
using Dictum.Meetings.Import;
using NAudio.Wave;

namespace Dictum.Windows.Audio;

/// <summary>Uses the codecs installed in Windows Media Foundation, including the audio track of MP4.</summary>
public sealed class MediaFoundationDecoder(TimeSpan? maximumDuration = null) : IAudioDecoder
{
    private readonly WavOnlyDecoder _boundedDecoder = new(maximumDuration);

    public CapturedAudio Decode(string path) => Decode(path, CancellationToken.None);

    public CapturedAudio Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // WAV parsing is shared with Core, including PCM/float, downmix and MonoResampler.
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            return _boundedDecoder.Decode(path, cancellationToken);
        using var reader = new MediaFoundationReader(path);
        return _boundedDecoder.Decode(reader, cancellationToken);
    }
}
