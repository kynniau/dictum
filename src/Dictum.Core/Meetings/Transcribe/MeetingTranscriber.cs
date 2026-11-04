using Dictum.Audio;
using Dictum.Meetings.Store;
using Dictum.Personal;
using Dictum.Transcribe;

namespace Dictum.Meetings.Transcribe;

public sealed class MeetingTranscriber(ISpeechEngine engine, IVoiceGate gate, IMeetingStore store,
    PersonalDictionary? dictionary = null) : IMeetingTranscriber
{
    private readonly SemaphoreSlim _run = new(1, 1);

    public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment,
        CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The same initial-prompt path as dictation; keep language, translation and beam settings.
            if (dictionary is not null)
                engine.Reconfigure(engine.Decode with { Prompt = dictionary.WhisperPrompt });
            var queue = new PriorityQueue<(Channel Channel, CapturedAudio Part, TimeSpan Offset), (TimeSpan, long)>();
            long sequence = 0;
            await foreach (var part in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                queue.Enqueue(part, (part.Offset, sequence++));
                await Drain(source.Watermark).ConfigureAwait(false);
            }
            await Drain(TimeSpan.MaxValue).ConfigureAwait(false);

            async Task Drain(TimeSpan through)
            {
                while (queue.TryPeek(out var part, out _) && part.Offset <= through)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    queue.Dequeue();
                    if (!(await gate.CheckAsync(part.Part.Samples, cancellationToken).ConfigureAwait(false)).Speech) continue;
                    var text = await engine.TranscribeAsync(part.Part.Samples, cancellationToken).ConfigureAwait(false);
                    // Persist a completed inference even if cancellation arrived as it returned.
                    var segment = new Segment(part.Offset, part.Offset + part.Part.Duration, part.Channel, text);
                    store.AppendSegments(id, [segment]);
                    onSegment(segment);
                }
            }
        }
        finally { _run.Release(); }
    }
}
