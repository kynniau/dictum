using System.Threading.Channels;
using Dictum.Audio;

namespace Dictum.Meetings.Store;

internal sealed class AudioSaveException(Exception cause) : IOException("Meeting audio could not be saved.", cause);

/// <summary>Capture only enqueues; encryption and durable writes run on one worker.</summary>
internal sealed class LiveAudioWriter : IDisposable
{
    // simplification: queue ceiling is 64 parts and 6 seconds of PCM across tracks; retire when
    // disk latency measurements justify a different bound. Overflow stops with a visible save failure.
    private readonly System.Threading.Channels.Channel<(Channel Track, float[] Samples, TimeSpan Offset)> _queue =
        System.Threading.Channels.Channel.CreateBounded<(Channel, float[], TimeSpan)>(64);
    private readonly Dictionary<Channel, IMeetingAudioWriter> _writers = [];
    private readonly Dictionary<Channel, long> _written = [];
    private readonly IMeetingStore _store;
    private readonly MeetingId _id;
    private readonly Task _worker;
    private long _queued;
    private Exception? _failure;
    private int _disposed;
    public event Action<Exception>? Failed;

    internal LiveAudioWriter(IMeetingStore store, MeetingId id, params Channel[] tracks)
    {
        _store = store; _id = id;
        try { foreach (var track in tracks) { _writers.Add(track, store.BeginAudio(id, track)); _written.Add(track, 0); } }
        catch { foreach (var writer in _writers.Values) writer.Dispose(); throw; }
        _worker = Task.Run(Write);
    }
    // A capture part can be up to SpeechSegmenter.LimitBlocks long (30 s of unbroken speech), and two tracks can each
    // hand over one at once (a cut or the tails at Stop). The budget holds two such parts per track.
    // simplification: a fixed budget of 120 s of 16 kHz samples (about 7.7 MB). Ceiling: a disk that stays more than
    // two maximum parts behind fails the recording. Trigger to retire: a slower-than-real-time store or longer parts.
    internal const int QueueBudget = 2 * 2 * SpeechSegmenter.LimitBlocks * CaptureBuffer.BlockSamples;
    internal void Enqueue(Channel track, float[] samples, TimeSpan offset)
    {
        if (_failure is not null || samples.Length == 0) return;
        if (Interlocked.Add(ref _queued, samples.Length) > QueueBudget || !_queue.Writer.TryWrite((track, samples, offset)))
        {
            Interlocked.Add(ref _queued, -samples.Length);
            Fail(new IOException("Audio persistence queue exceeded its capacity."));
        }
    }
    private void Fail(Exception error)
    {
        var failure = new AudioSaveException(error);
        if (Interlocked.CompareExchange(ref _failure, failure, null) is null) Failed?.Invoke(failure);
    }
    private async Task Write()
    {
        try
        {
            await foreach (var part in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (Volatile.Read(ref _disposed) != 0 || _failure is not null) continue;
                    var writer = _writers[part.Track];
                    var target = Math.Max(0, checked((long)(part.Offset.TotalSeconds * 16000)));
                    while (_written[part.Track] < target)
                    {
                        var count = (int)Math.Min(16000, target - _written[part.Track]);
                        writer.Append(new float[count]); _written[part.Track] += count;
                    }
                    var skip = (int)Math.Min(part.Samples.Length, Math.Max(0, _written[part.Track] - target));
                    if (skip < part.Samples.Length)
                    {
                        writer.Append(skip == 0 ? part.Samples : part.Samples[skip..]);
                        _written[part.Track] += part.Samples.Length - skip;
                    }
                }
                finally { Interlocked.Add(ref _queued, -part.Samples.Length); }
            }
        }
        catch (Exception error) { Fail(error); }
    }
    internal void Complete()
    {
        _queue.Writer.TryComplete(); _worker.GetAwaiter().GetResult();
        if (_failure is not null) throw _failure;
        foreach (var writer in _writers.Values) writer.Complete();
    }
    internal Stream Audio(Channel track) => _store.OpenAudio(_id, track);
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _queue.Writer.TryComplete(); _worker.GetAwaiter().GetResult();
        while (_queue.Reader.TryRead(out _)) { }
        foreach (var writer in _writers.Values) writer.Dispose();
    }
}
