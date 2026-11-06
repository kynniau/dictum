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
            var recentOthers = new List<Segment>();
            var pending = new List<Segment>();
            ExceptionDispatchInfo? sourceError = null;
            await using var reader = source.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
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
                    if (part.Part.Samples.Length != 0) queue.Enqueue(part, (part.Offset, sequence++));
                    await Drain(source.Watermark).ConfigureAwait(false);
                }
                await Drain(TimeSpan.MaxValue).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                sourceError?.Throw();
            }
            catch (OperationCanceledException)
            {
                // Keep completed inference on cancellation; no untranscribed audio is processed.
                Publish(TimeSpan.MaxValue);
                throw;
            }

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
                    var earliest = pending.Where(s => s.Channel == Channel.You).Select(s => s.Start)
                        .Append(segment.Start).Min();
                    recentOthers.RemoveAll(s => earliest > s.End && earliest - s.End > EchoDeduplicator.Slack);
                    if (segment.Channel == Channel.Others)
                    {
                        recentOthers.Add(segment);
                        pending.RemoveAll(s => EchoDeduplicator.IsEcho(s, recentOthers));
                    }
                    if (segment.Channel != Channel.You || !EchoDeduplicator.IsEcho(segment, recentOthers))
                        pending.Add(segment);
                    // The next queued part also constrains what can safely be published.
                    var safeThrough = queue.TryPeek(out var next, out _) && next.Offset < through ? next.Offset : through;
                    Publish(safeThrough);
                }
                Publish(through);
            }

            void Publish(TimeSpan through)
            {
                while (pending.Count != 0)
                {
                    var segment = pending[0];
                    // The append-only live callback cannot retract a line. Hold You (and following
                    // lines for ordering) until no future Others can overlap its slack window.
                    if (segment.Channel == Channel.You && through != TimeSpan.MaxValue &&
                        (through <= segment.End || through - segment.End <= EchoDeduplicator.Slack)) break;
                    pending.RemoveAt(0);
                    store.AppendSegments(id, [segment]);
                    onSegment(segment);
                }
            }
        }
        finally { _run.Release(); }
    }
}
