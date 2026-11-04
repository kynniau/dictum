using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dictum.Audio;

namespace Dictum.Meetings.Transcribe;

/// <summary>Starts both devices on enumeration. Stop completes normally, including both capture tails.</summary>
public sealed class LiveTwoTrackSource(IAudioCapture microphone, IAudioCapture system,
    double silenceThreshold = 0.01, string? microphoneDevice = null, string? systemDevice = null) : IMeetingAudioSource
{
    private readonly Stopwatch _clock = new();
    private readonly object _sync = new();
    private readonly object _lifecycle = new();
    private readonly System.Threading.Channels.Channel<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> _parts =
        System.Threading.Channels.Channel.CreateUnbounded<(Channel, CapturedAudio, TimeSpan)>();
    private readonly TimeSpan[] _latest = [TimeSpan.MinValue, TimeSpan.MinValue];
    private readonly Queue<TimeSpan>[] _pending = [new(), new()];
    private static readonly TimeSpan LongestPart = TimeSpan.FromMilliseconds(SpeechSegmenter.LimitBlocks * SpeechSegmenter.BlockMs);
    private bool _micStarted, _systemStarted;
    private int _read, _stopped;
    private TimeSpan _watermark = TimeSpan.MinValue;

    public TimeSpan Watermark { get { lock (_sync) return _watermark; } }

    public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _read, 1) != 0) throw new InvalidOperationException("A live source can only be read once.");
        cancellationToken.ThrowIfCancellationRequested();
        microphone.Failed += Failed;
        system.Failed += Failed;
        try
        {
            lock (_lifecycle)
            {
                if (_stopped != 0) throw new InvalidOperationException("The live source has already stopped.");
                _clock.Start();
                var stamp = Stopwatch.GetTimestamp();
                microphone.Start(microphoneDevice, stamp);
                _micStarted = true;
                microphone.SegmentAtPauses(silenceThreshold, (part, _) => Publish(0, Channel.You, part));
                system.Start(systemDevice, stamp);
                _systemStarted = true;
                system.SegmentAtPauses(silenceThreshold, (part, _) => Publish(1, Channel.Others, part));
            }
            await foreach (var part in _parts.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Advance only as events are consumed: callbacks can be several events ahead of this reader.
                lock (_sync)
                {
                    _latest[part.Channel == Channel.You ? 0 : 1] = part.Offset;
                    _pending[part.Channel == Channel.You ? 0 : 1].Dequeue();
                    // simplification: an idle track holds the watermark LongestPart (the segmenter's 30 s bound) behind the clock, so while one side is
                    // silent the other side's parts surface up to 30 s late; retire when capture (lane L2) reports whether each track is in speech.
                    var idle = _clock.Elapsed - LongestPart;
                    if (idle < TimeSpan.Zero) idle = TimeSpan.Zero;
                    // Unconsumed callbacks must still enter the transcription queue before we pass them.
                    _watermark = Enumerable.Range(0, 2).Select(track =>
                    {
                        var progress = _latest[track] > idle ? _latest[track] : idle;
                        return _pending[track].TryPeek(out var next) && next < progress ? next : progress;
                    }).Min();
                }
                yield return part;
            }
        }
        finally
        {
            try { Stop(); }
            finally
            {
                microphone.Failed -= Failed;
                system.Failed -= Failed;
            }
        }
    }

    private readonly TimeSpan[] _published = [TimeSpan.Zero, TimeSpan.Zero];

    private void Publish(int track, Channel channel, CapturedAudio part)
    {
        if (part.Samples.Length == 0) return;
        lock (_sync)
        {
            // simplification: offset = shared clock at callback minus part duration; it ignores segmenter look-back and pause trailing time, so live tracks can be skewed by a few hundred ms; retire when capture (lane L2) reports sample boundaries on the shared clock
            var offset = _clock.Elapsed - part.Duration;
            if (offset < _published[track]) offset = _published[track];
            _published[track] = offset;
            if (_parts.Writer.TryWrite((channel, part, offset))) _pending[track].Enqueue(offset);
        }
    }

    public void Stop() => Stop(null);

    private void Stop(Exception? error)
    {
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { if (_micStarted) Publish(0, Channel.You, microphone.Stop()); }
            catch (Exception ex) { error ??= ex; }
            try { if (_systemStarted) Publish(1, Channel.Others, system.Stop()); }
            catch (Exception ex) { error ??= ex; }
            _parts.Writer.TryComplete(error);
            _clock.Stop();
        }
    }

    // A failure callback runs on the capture thread; stop on a worker so capture shutdown can join it.
    private void Failed(Exception error) => ThreadPool.QueueUserWorkItem(_ => Stop(error));
}
