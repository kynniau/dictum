using Dictum.Audio;
using Dictum.Platform;
using NAudio.Wave;

namespace Dictum.Meetings.Import;

public sealed class WavOnlyDecoder
    : IAudioDecoder
{
    // simplification: cap decoded audio at 4 hours of 16 kHz mono; retired when the import streams to the store.
    public const int MaximumDurationSeconds = 4 * 60 * 60;
    private readonly TimeSpan _maximumDuration;

    public WavOnlyDecoder(TimeSpan? maximumDuration = null)
    {
        _maximumDuration = maximumDuration ?? TimeSpan.FromSeconds(MaximumDurationSeconds);
        if (_maximumDuration <= TimeSpan.Zero || _maximumDuration.TotalSeconds > MaximumDurationSeconds)
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
    }

    public CapturedAudio Decode(string path) => Decode(path, CancellationToken.None);

    public CapturedAudio Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This decoder supports WAV files only. Choose a platform decoder for other formats.");
        using var reader = new WaveFileReader(path);
        return Decode(reader, cancellationToken);
    }

    /// <summary>Decode an open audio reader without taking ownership of it.</summary>
    public CapturedAudio Decode(WaveStream reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var format = reader.WaveFormat;
        var buffer = new CaptureBuffer(SampleDecoder.EncodingOf(format), format.Channels, format.SampleRate);
        var chunk = new byte[checked(format.BlockAlign * 4096)];
        var maximumFrames = (long)Math.Floor(_maximumDuration.TotalSeconds * format.SampleRate);
        long frames = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = reader.Read(chunk, 0, chunk.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            frames += count / format.BlockAlign;
            if (frames > maximumFrames)
                throw new NotSupportedException($"Decoded recording exceeds the maximum duration of {_maximumDuration} (16 kHz mono).");
            buffer.Append(chunk.AsSpan(0, count - count % format.BlockAlign));
        }
        return buffer.Seal();
    }
}
