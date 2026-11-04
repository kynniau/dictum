using Dictum.Audio;
using Dictum.Meetings.Store;
using Dictum.Personal;
using Dictum.Transcribe;
using System.Runtime.ExceptionServices;

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
            // The per-call prompt leaves the shared dictation decoder unchanged.
            var queue = new PriorityQueue<(Channel Channel, CapturedAudio Part, TimeSpan Offset), (TimeSpan, long)>();
            long sequence = 0;
            ExceptionDispatchInfo? sourceError = null;
            await using var reader = source.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool more;
                try { more = await reader.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sourceError = ExceptionDispatchInfo.Capture(ex);
                    break;
                }
                if (!more) break;
                var part = reader.Current;
                cancellationToken.ThrowIfCancellationRequested();
                queue.Enqueue(part, (part.Offset, sequence++));
                await Drain(source.Watermark).ConfigureAwait(false);
            }
            await Drain(TimeSpan.MaxValue).ConfigureAwait(false);
            sourceError?.Throw();

            async Task Drain(TimeSpan through)
            {
                while (queue.TryPeek(out var part, out _) && part.Offset <= through)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    queue.Dequeue();
                    if (!(await gate.CheckAsync(part.Part.Samples, cancellationToken).ConfigureAwait(false)).Speech) continue;
                    var text = await engine.TranscribeAsync(part.Part.Samples, dictionary?.WhisperPrompt, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text)) continue;
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
