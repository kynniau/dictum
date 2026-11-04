using Dictum.Audio;

namespace Dictum.Platform;

public interface IAudioDecoder
{
    /// <summary>Decode to 16 kHz mono samples, as required by CapturedAudio.</summary>
    CapturedAudio Decode(string path);

    CapturedAudio Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var audio = Decode(path);
        cancellationToken.ThrowIfCancellationRequested();
        return audio;
    }
}
