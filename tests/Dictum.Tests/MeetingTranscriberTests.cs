using System.Diagnostics;
using System.Runtime.CompilerServices;
using Dictum.Audio;
using Dictum.Meetings;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;
using Dictum.Personal;
using Dictum.Transcribe;
using Channel = Dictum.Meetings.Channel;

namespace Dictum.Tests;

public class MeetingTranscriberTests
{
    private const string First = "A. Example said the budget moves to Friday";
    private const string Second = "B. Example agreed to review on Monday";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_echo_is_never_stored_or_displayed_in_either_arrival_order(bool othersFirst)
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var seen = new List<Segment>();
        var first = othersFirst ? Channel.Others : Channel.You;
        var second = othersFirst ? Channel.You : Channel.Others;
        var engine = new ToneEngine { Output = First };
        await new MeetingTranscriber(engine, new Gate(), store).RunAsync(id,
            new AdvancingParts([(first, Tone(440), TimeSpan.Zero),
                (second, Tone(440), TimeSpan.FromSeconds(1.5))]), segment =>
            {
                seen.Add(segment);
                Assert.Equal(Channel.Others, segment.Channel);
                Assert.All(store.ReadTranscript(id), s => Assert.Equal(Channel.Others, s.Channel));
            }, default);
        Assert.Equal(2, engine.Frequencies.Count);
        Assert.Equal(Channel.Others, Assert.Single(seen).Channel);
        Assert.Equal(seen, store.ReadTranscript(id));
    }

    [Fact]
    public async Task Microphone_only_live_segments_are_untouched()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var seen = new List<Segment>();
        await new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id,
            new AdvancingParts([(Channel.You, Tone(440), TimeSpan.Zero),
                (Channel.You, Tone(440), TimeSpan.FromSeconds(1))]), seen.Add, default);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, segment => Assert.Equal(Channel.You, segment.Channel));
        Assert.Equal(seen, store.ReadTranscript(id));
    }

    [Fact]
    public async Task Cancellation_preserves_completed_inference_waiting_for_the_echo_window()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        using var cancel = new CancellationTokenSource();
        var seen = new List<Segment>();
        var engine = new ToneEngine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MeetingTranscriber(engine, new Gate(), store).RunAsync(id,
                new AdvancingParts([(Channel.You, Tone(440), TimeSpan.Zero)], cancel.Cancel), seen.Add, cancel.Token));
        Assert.Single(engine.Frequencies);
        Assert.Equal(First, Assert.Single(seen).Text);
        Assert.Equal(seen, store.ReadTranscript(id));
    }

    private sealed class AdvancingParts((Channel Channel, CapturedAudio Part, TimeSpan Offset)[] parts,
        Action? afterPart = null) : IMeetingAudioSource
    {
        public TimeSpan Watermark { get; private set; }
        public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Watermark = part.Offset;
                yield return part;
                afterPart?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task Two_tracks_interleave_by_time_not_by_arrival()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var engine = new ToneEngine();
        var seen = new List<Segment>();
        var source = new Parts([(Channel.Others, Tone(880), TimeSpan.FromSeconds(3)),
            (Channel.You, Tone(440), TimeSpan.FromSeconds(1))]);
        await new MeetingTranscriber(engine, new Gate(), store).RunAsync(id, source, seen.Add, default);
        Assert.Equal(new[] { First, Second }, seen.Select(s => s.Text));
        Assert.Equal(seen, store.ReadTranscript(id));
        Assert.Equal(new[] { 440, 880 }, engine.Frequencies);
    }

    [Fact]
    public async Task Silent_parts_never_reach_the_engine()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var engine = new ToneEngine();
        var gate = new Gate();
        await new MeetingTranscriber(engine, gate, store).RunAsync(id,
            new Parts([(Channel.You, new(new float[1600], 0), TimeSpan.Zero),
                (Channel.Others, Tone(880), TimeSpan.FromSeconds(1))]), _ => { }, default);
        Assert.Equal(2, gate.Calls);
        Assert.Equal(new[] { 880 }, engine.Frequencies);
        Assert.Equal(Second, Assert.Single(store.ReadTranscript(id)).Text);
    }

    [Fact]
    public async Task Only_one_engine_call_runs_at_a_time()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var engine = new ToneEngine();
        var transcriber = new MeetingTranscriber(engine, new Gate(), store);
        var ids = new[] { store.Create(f.Meta()), store.Create(f.Meta()) };
        await Task.WhenAll(ids.Select(id => transcriber.RunAsync(id,
            new Parts(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? Channel.You : Channel.Others,
                Tone(i % 2 == 0 ? 440 : 880), TimeSpan.FromSeconds(i))).ToArray()), _ => { }, default)));
        Assert.Equal(1, engine.MaxActive);
        Assert.Equal(16, engine.Frequencies.Count);
        Assert.All(ids, id => Assert.Equal(8, store.ReadTranscript(id).Count));
    }

    [Fact]
    public async Task Segments_carry_the_channel_of_their_track()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var seen = new List<Segment>();
        await new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id,
            new FileSource(Tone(440), Tone(880)), seen.Add, default);
        Assert.Collection(seen,
            s => { Assert.Equal(Channel.You, s.Channel); Assert.Equal(First, s.Text); Assert.Equal(TimeSpan.FromSeconds(.1), s.End); },
            s => { Assert.Equal(Channel.Others, s.Channel); Assert.Equal(Second, s.Text); });
        Assert.Equal(seen, store.ReadTranscript(id));
    }

    [Fact]
    public async Task A_single_file_import_is_channel_Mixed()
    {
        using var f = new MeetingFixture();
        Directory.CreateDirectory(f.Root);
        var path = Path.Combine(f.Root, "invented.wav");
        // A pause forces the real offline SpeechSegmenter to cut, rather than merely returning the tail.
        var samples = Tone(440, 2.2).Samples.Concat(new float[16000]).Concat(Tone(880, 2.2).Samples).ToArray();
        File.WriteAllBytes(path, Wav.Encode(samples));
        var store = f.Store();
        var id = store.Create(f.Meta());
        var seen = new List<Segment>();
        await new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id, new FileSource(path), seen.Add, default);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, s => Assert.Equal(Channel.Mixed, s.Channel));
        Assert.Equal(new[] { First, Second }, seen.Select(s => s.Text));
        Assert.Equal(TimeSpan.Zero, seen[0].Start);
        Assert.Equal(seen[0].End, seen[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(5.4), seen[1].End);
    }

    [Fact]
    public async Task Cancellation_stops_without_losing_appended_segments()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        using var cancel = new CancellationTokenSource();
        var engine = new ToneEngine();
        var seen = new List<Segment>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MeetingTranscriber(engine, new Gate(), store).RunAsync(id,
                new FileSource(Tone(440), Tone(880)), s => { seen.Add(s); cancel.Cancel(); }, cancel.Token));
        // Both inferences finish while the first You line waits for the echo window.
        Assert.Equal(new[] { First, Second }, seen.Select(segment => segment.Text));
        Assert.Equal(seen, store.ReadTranscript(id));
        Assert.Equal(2, engine.Frequencies.Count);
    }

    [Fact]
    public async Task Timestamps_come_from_the_shared_clock()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var mic = new Capture();
        var other = new Capture();
        var source = new LiveTwoTrackSource(mic, other);
        var seen = new List<Segment>();
        var run = new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id, source, seen.Add, default);
        await Task.WhenAll(mic.Ready.Task, other.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();
        await Task.Delay(250);
        mic.Emit(Tone(440, .02));
        other.Emit(Tone(880, .02));
        source.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(mic.Stamp, other.Stamp);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, s => Assert.InRange(s.Start.TotalMilliseconds, 200, elapsed.Elapsed.TotalMilliseconds + 100));
        Assert.True(seen[0].Start <= seen[1].Start);
        Assert.True(mic.Stopped && other.Stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dictionary_uses_per_call_prompt_and_leaves_decoder_unchanged(bool cancelRun)
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var engine = new ToneEngine();
        engine.Reconfigure(new("Dictation prompt", 5, "fr", true));
        var original = engine.Decode;
        var dictionary = new PersonalDictionary(["A. Example", "Budget Friday"]);
        using var cancel = new CancellationTokenSource();
        var run = new MeetingTranscriber(engine, new Gate(), store, dictionary).RunAsync(store.Create(f.Meta()),
            new FileSource(Tone(440), Tone(880)), _ => { if (cancelRun) cancel.Cancel(); }, cancel.Token);
        if (cancelRun) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else await run;
        Assert.Equal(original, engine.Decode);
        Assert.NotEmpty(engine.Prompts);
        Assert.All(engine.Prompts, prompt => Assert.Equal(dictionary.WhisperPrompt, prompt));
        Assert.All(engine.Decoders, decode => Assert.Equal(original, decode));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task Blank_engine_output_is_not_stored_or_emitted(string output)
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var engine = new ToneEngine { Output = output };
        var seen = new List<Segment>();
        await new MeetingTranscriber(engine, new Gate(), store).RunAsync(id,
            new FileSource(Tone(440)), seen.Add, default);
        Assert.Single(engine.Frequencies);
        Assert.Empty(seen);
        Assert.Empty(store.ReadTranscript(id));
    }

    [Fact]
    public async Task A_silent_live_track_allows_parts_before_stop()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var mic = new Capture();
        var other = new Capture();
        var clock = new ManualTime();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        var emitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id, source,
            _ => emitted.TrySetResult(), default);
        await Task.WhenAll(mic.Ready.Task, other.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // A maximum-length cut starts at zero, the idle track's floored watermark.
            mic.Emit(Tone(440, SpeechSegmenter.LimitBlocks * SpeechSegmenter.BlockMs / 1000.0));
            clock.Advance(TimeSpan.FromSeconds(70));
            await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(mic.Stopped);
            Assert.False(other.Stopped);
            Assert.Equal(First, Assert.Single(store.ReadTranscript(id)).Text);
        }
        finally { source.Stop(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_fault_drains_buffered_parts_and_other_tail_before_rethrow(bool captureFailure)
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var error = new InvalidOperationException("Capture failed");
        var mic = new Capture { StopError = error };
        var other = new Capture { Tail = Tone(880, .02) };
        var source = new LiveTwoTrackSource(mic, other);
        var seen = new List<Segment>();
        var engine = new ToneEngine();
        var original = engine.Decode;
        var run = new MeetingTranscriber(engine, new Gate(), store, new PersonalDictionary(["Example"]))
            .RunAsync(id, source, seen.Add, default);
        await Task.WhenAll(mic.Ready.Task, other.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        // Positive offsets remain buffered behind the silent track until the source faults.
        await Task.Delay(150);
        mic.Emit(Tone(440, .02));
        if (captureFailure) mic.Fail(error);
        else source.Stop();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(error, thrown);
        Assert.True(mic.Stopped && other.Stopped);
        Assert.Equal(new[] { First, Second }, seen.Select(s => s.Text));
        Assert.Equal(seen.OrderBy(s => s.Start), seen);
        Assert.Equal(seen, store.ReadTranscript(id));
        Assert.Equal(original, engine.Decode);
    }

    [Fact]
    public async Task One_live_part_then_silence_is_emitted_on_a_progress_tick_before_stop()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var mic = new Capture();
        var other = new Capture();
        var clock = new ManualTime();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        var observed = new ObservedSource(source);
        var emitted = new TaskCompletionSource<Segment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new Gate();
        var run = new MeetingTranscriber(new ToneEngine(), gate, store).RunAsync(id, observed,
            s => emitted.TrySetResult(s), default);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            mic.Emit(Tone(440));
            await observed.PartConsumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(emitted.Task.IsCompleted);
            // Neither capture sends another part. Only clock progress can release the buffered audio.
            clock.Advance(TimeSpan.FromSeconds(38));
            var segment = await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(4.9), segment.Start);
            Assert.Equal(First, segment.Text);
            Assert.False(mic.Stopped || other.Stopped);
            Assert.Equal(segment, Assert.Single(store.ReadTranscript(id)));
        }
        finally { source.Stop(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(clock.TimerDisposed);
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task Capture_fault_while_user_stop_owns_lifecycle_surfaces_after_both_tails()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var error = new InvalidOperationException("Capture failed during user Stop");
        var mic = new Capture { Tail = Tone(440) };
        var other = new Capture { Tail = Tone(880) };
        var clock = new ManualTime();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        // The normal Stop already owns the lifecycle lock when the capture reports its failure.
        // Its queued fault shutdown cannot acquire the lock until normal Stop completes.
        mic.OnStop = () => mic.Fail(error);
        other.OnStop = () => other.Fail(new InvalidOperationException("Later failure"));
        var seen = new List<Segment>();
        var run = new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id, source, seen.Add, default);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Stop();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(error, thrown);
        Assert.True(mic.Stopped && other.Stopped);
        Assert.True(clock.TimerDisposed);
        Assert.Equal(new[] { First, Second }, seen.Select(s => s.Text));
        Assert.Equal(seen, store.ReadTranscript(id));
    }

    [Fact]
    public async Task Live_stop_drains_both_tails_and_emits_before_stop()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var id = store.Create(f.Meta());
        var mic = new Capture { Tail = Tone(440) };
        var other = new Capture { Tail = Tone(880) };
        var clock = new ManualTime();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        var emitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<Segment>();
        var run = new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(id, source,
            s => { seen.Add(s); emitted.TrySetResult(); }, TestContext.Current.CancellationToken);
        await Task.WhenAll(mic.Ready.Task, other.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            mic.Emit(Tone(440));
            other.Emit(Tone(880));
            clock.Advance(TimeSpan.FromSeconds(35));
            await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(mic.Stopped);
        }
        finally { source.Stop(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, seen.Count);
        Assert.Equal(2, seen.Count(s => s.Channel == Channel.You));
        Assert.Equal(2, seen.Count(s => s.Channel == Channel.Others));
        Assert.Equal(seen.OrderBy(s => s.Start), seen);
        Assert.Equal(seen, store.ReadTranscript(id));
    }

    [Fact]
    public async Task Live_cancellation_releases_both_captures()
    {
        using var f = new MeetingFixture();
        var store = f.Store();
        var mic = new Capture();
        var other = new Capture();
        using var cancel = new CancellationTokenSource();
        var run = new MeetingTranscriber(new ToneEngine(), new Gate(), store).RunAsync(store.Create(f.Meta()),
            new LiveTwoTrackSource(mic, other), _ => { }, cancel.Token);
        await Task.WhenAll(mic.Ready.Task, other.Ready.Task).WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(mic.Stopped && other.Stopped);
    }

    private static CapturedAudio Tone(int frequency, double seconds = .1) => new(
        Enumerable.Range(0, (int)(16000 * seconds)).Select(i => (float)(.4 * Math.Sin(2 * Math.PI * frequency * i / 16000))).ToArray(), .3f);

    private sealed class Parts((Channel Channel, CapturedAudio Part, TimeSpan Offset)[] parts) : IMeetingAudioSource
    {
        public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var part in parts) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); yield return part; }
        }
    }

    private sealed class Gate : IVoiceGate
    {
        public int Calls;
        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new VoiceVerdict(samples16kMono.Any(s => Math.Abs(s) > .01), "Synthetic fixture"));
        }
    }

    private sealed class ToneEngine : ISpeechEngine
    {
        public string ModelPath => "synthetic";
        public DecodeOptions Decode { get; private set; } = DecodeOptions.Default;
        public void Reconfigure(DecodeOptions decode) => Decode = decode;
        public List<int> Frequencies { get; } = [];
        public List<string?> Prompts { get; } = [];
        public List<DecodeOptions> Decoders { get; } = [];
        public string? Output;
        public Task<string> TranscribeAsync(float[] samples16kMono, string? carried, CancellationToken cancellationToken)
        {
            Prompts.Add(carried);
            Decoders.Add(Decode);
            return TranscribeAsync(samples16kMono, cancellationToken);
        }
        private int _active;
        public int MaxActive;
        public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                await Task.Delay(5, cancellationToken);
                // Match the first voiced 20 ms, ignoring a leading pause left by SpeechSegmenter.
                var start = Array.FindIndex(samples16kMono, s => Math.Abs(s) > .05);
                if (start < 0) throw new InvalidOperationException("Silence reached engine");
                var count = Math.Min(300, samples16kMono.Length - start);
                double Power(int hz)
                {
                    double sin = 0, cos = 0;
                    for (var i = 0; i < count; i++)
                    {
                        sin += samples16kMono[start + i] * Math.Sin(2 * Math.PI * hz * i / 16000);
                        cos += samples16kMono[start + i] * Math.Cos(2 * Math.PI * hz * i / 16000);
                    }
                    return sin * sin + cos * cos;
                }
                var frequency = Power(440) > Power(880) ? 440 : 880;
                Frequencies.Add(frequency);
                return Output ?? (frequency == 440 ? First : Second);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() { }
    }

    private sealed class ObservedSource(IMeetingAudioSource source) : IMeetingAudioSource
    {
        public TimeSpan Watermark => source.Watermark;
        public TaskCompletionSource PartConsumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var part in source.ReadAsync(cancellationToken))
            {
                yield return part;
                // RunAsync has buffered this part and requested the next event.
                if (part.Part.Samples.Length != 0) PartConsumed.TrySetResult();
            }
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        private ManualTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TimerDisposed => _timer?.Disposed == true;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(this, callback, state);
            _timer.Change(dueTime, period);
            TimerCreated.TrySetResult();
            return _timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
            _timer?.Fire();
        }

        private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            private long _next;
            private TimeSpan _period;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                _next = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                _period = period;
                return true;
            }
            public void Fire()
            {
                if (Disposed || clock.GetTimestamp() < _next) return;
                _next = _period == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + _period.Ticks;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Capture : IAudioCapture
    {
        public float CurrentLevel => 0;
        public long Stamp;
        public bool Stopped;
        public CapturedAudio Tail = CapturedAudio.Empty;
        public Exception? StopError;
        public Action? OnStop;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<CapturedAudio, CutReason>? _part;
        public event Action<Exception>? Failed;
        public void Fail(Exception error) => Failed?.Invoke(error);
        public void Start(string? deviceSetting, long keyDownStamp) => Stamp = keyDownStamp;
        public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part)
        { _part = part; Ready.TrySetResult(); }
        public void Emit(CapturedAudio audio) => _part!(audio, CutReason.Pause);
        public CapturedAudio Stop() { Stopped = true; OnStop?.Invoke(); if (StopError is not null) throw StopError; return Tail; }
    }
}
