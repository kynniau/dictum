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
                    // simplification: an idle track holds the ordering watermark until its next cut or Stop.
                    // Retire when IAudioCapture supplies timestamped progress during silence, allowing earlier draining.
                    _watermark = _latest.Min();
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
            // simplification: IAudioCapture exposes neither packet timestamps nor the segmenter's retained look-back.
            // Anchor each part's end at callback wall time; pause/limit look-back and callback latency bound the error.
            // Retire when capture exposes the actual cut timestamp. Sample totals never accumulate device drift here.
            var offset = _clock.Elapsed - part.Duration;
            if (offset < _published[track]) offset = _published[track];
            _published[track] = offset;
            _parts.Writer.TryWrite((channel, part, offset));
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            Exception? error = null;
            try { if (_micStarted) Publish(0, Channel.You, microphone.Stop()); }
            catch (Exception ex) { error = ex; }
            try { if (_systemStarted) Publish(1, Channel.Others, system.Stop()); }
            catch (Exception ex) { error ??= ex; }
            _parts.Writer.TryComplete(error);
            _clock.Stop();
        }
    }

    private void Failed(Exception error) => _parts.Writer.TryComplete(error);
}
