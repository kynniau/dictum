using System.Runtime.CompilerServices;
using Dictum.Audio;

namespace Dictum.Meetings.Transcribe;

public sealed class FileSource : IMeetingAudioSource
{
    private readonly CapturedAudio _first;
    private readonly CapturedAudio? _second;
    private readonly double _threshold;

    public FileSource(string firstWav, string? secondWav = null, double silenceThreshold = 0.01)
        : this(AudioFiles.ReadWav(firstWav), secondWav is null ? null : AudioFiles.ReadWav(secondWav), silenceThreshold) { }

    /// <summary>Already decoded 16 kHz mono audio, for the importer and in-memory fixtures.</summary>
    public FileSource(CapturedAudio first, CapturedAudio? second = null, double silenceThreshold = 0.01)
        => (_first, _second, _threshold) = (first, second, silenceThreshold);

    public TimeSpan Watermark { get; private set; } = TimeSpan.MinValue;

    public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var first = Cut(_first, _second is null ? Channel.Mixed : Channel.You, cancellationToken);
        var second = _second is null ? [] : Cut(_second, Channel.Others, cancellationToken);
        foreach (var part in first.Concat(second).OrderBy(p => p.Offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Watermark = part.Offset;
            yield return part;
        }
        await Task.CompletedTask;
    }

    private List<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> Cut(CapturedAudio audio, Channel channel, CancellationToken token)
    {
        var parts = new List<(Channel, CapturedAudio, TimeSpan)>();
        var segmenter = new SpeechSegmenter(_threshold);
        var start = 0;
        for (var i = 0; i + CaptureBuffer.BlockSamples <= audio.Samples.Length; i += CaptureBuffer.BlockSamples)
        {
            token.ThrowIfCancellationRequested();
            double sum = 0;
            for (var j = i; j < i + CaptureBuffer.BlockSamples; j++) sum += audio.Samples[j] * audio.Samples[j];
            if (segmenter.Add((float)Math.Sqrt(sum / CaptureBuffer.BlockSamples)) is { } cut)
            {
                Add(checked((int)cut.Block * CaptureBuffer.BlockSamples), cut.Peak);
            }
        }
        if (start < audio.Samples.Length) Add(audio.Samples.Length, Math.Max(segmenter.TailPeak, audio.PeakLevel));
        return parts;

        void Add(int end, float peak)
        {
            parts.Add((channel, new CapturedAudio(audio.Samples[start..end], peak),
                TimeSpan.FromSeconds(start / (double)MonoResampler.WhisperRate)));
            start = end;
        }
    }
}
