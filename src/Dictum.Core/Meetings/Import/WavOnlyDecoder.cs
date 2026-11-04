using Dictum.Audio;
using Dictum.Platform;

namespace Dictum.Meetings.Import;

public sealed class WavOnlyDecoder : IAudioDecoder
{
    public CapturedAudio Decode(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This decoder supports WAV files only. Choose a platform decoder for other formats.");
        return AudioFiles.ReadWav(path);
    }
}
