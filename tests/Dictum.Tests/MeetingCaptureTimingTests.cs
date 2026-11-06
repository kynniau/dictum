using System.Diagnostics;
using Dictum.Audio;
using Dictum.Meetings.Transcribe;
using Dictum.Meetings;
using Dictum.Transcribe;

namespace Dictum.Tests;

public class MeetingCaptureTimingTests
{
    private static CapturedAudio Audio => new(new float[1600], .2f);

    [Fact]
    public async Task Tiny_retention_limit_fails_explicitly_and_reaches_the_transcriber()
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store(); var id = store.Create(fixture.Meta());
        var mic = new TimedCapture(); var other = new TimedCapture();
        var source = new LiveTwoTrackSource(mic, other, maxQueuedAudio: TimeSpan.FromMilliseconds(50));
        var run = new MeetingTranscriber(new Engine(), new Gate(), store).RunAsync(id, source,
            _ => { }, TestContext.Current.CancellationToken);
        mic.Emit(Audio, TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("retention limit", error.Message);
        Assert.True(mic.Stopped && other.Stopped);
    }

    [Fact]
    public async Task Reported_starts_and_stop_tails_override_callback_time()
    {
        var clock = new ManualClock();
        var mic = new TimedCapture(); var other = new TimedCapture();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        await using var reader = source.ReadAsync(default).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        Assert.Same(mic.Clock, other.Clock);
        Assert.True(mic.Clock!.IsRunning);
        clock.Advance(20, false);
        mic.Emit(Audio, TimeSpan.FromSeconds(2.125));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(2.125), reader.Current.Offset);
        other.Tail = (Audio, TimeSpan.FromSeconds(3.75));
        source.Stop();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(TimeSpan.FromSeconds(3.75), reader.Current.Offset);
        Assert.False(await reader.MoveNextAsync());
    }

    [Theory]
    [InlineData(CaptureActivity.Idle)]
    [InlineData(CaptureActivity.Silent)]
    public async Task Idle_track_advances_watermark_within_one_second(CaptureActivity activity)
    {
        var clock = new ManualClock();
        var mic = new TimedCapture(); var other = new TimedCapture();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        await using var reader = source.ReadAsync(default).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        mic.Emit(Audio, TimeSpan.FromSeconds(4));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(source.Watermark < TimeSpan.FromSeconds(4));
        mic.Progress = new(CaptureActivity.Idle, TimeSpan.FromSeconds(5));
        other.Progress = new(activity, TimeSpan.FromSeconds(5));
        next = reader.MoveNextAsync().AsTask();
        clock.Advance(1);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(reader.Current.Part.Samples);
        Assert.Equal(TimeSpan.FromSeconds(5), source.Watermark);
        Assert.False(mic.Stopped || other.Stopped);
        source.Stop();
    }

    [Fact]
    public async Task Speech_pins_progress_to_its_buffered_start()
    {
        var clock = new ManualClock();
        var mic = new TimedCapture { Progress = new(CaptureActivity.Speech, TimeSpan.FromSeconds(2)) };
        var other = new TimedCapture { Progress = new(CaptureActivity.Idle, TimeSpan.FromSeconds(10)) };
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        await using var reader = source.ReadAsync(default).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        clock.Advance(10);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(2), source.Watermark);
        source.Stop();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Either_capture_without_extension_keeps_legacy_timing_and_idle_lag(bool mixed)
    {
        var clock = new ManualClock();
        var mic = new BasicCapture();
        BasicCapture other = mixed ? new TimedCapture() : new BasicCapture();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        await using var reader = source.ReadAsync(default).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        clock.Advance(5, false);
        mic.EmitLegacy(Audio);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(4.9), reader.Current.Offset);
        Assert.Equal(TimeSpan.Zero, source.Watermark);
        next = reader.MoveNextAsync().AsTask();
        clock.Advance(1);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.Zero, source.Watermark);
        next = reader.MoveNextAsync().AsTask();
        clock.Advance(30);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(6), source.Watermark);
        if (other is TimedCapture timed) Assert.Null(timed.Clock);
        source.Stop();
    }

    [Fact]
    public async Task Pending_callbacks_limit_watermark_even_when_capture_state_is_ahead()
    {
        var clock = new ManualClock(); var mic = new TimedCapture(); var other = new TimedCapture();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        await using var reader = source.ReadAsync(default).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        mic.Emit(Audio, TimeSpan.FromSeconds(1));
        mic.Emit(Audio, TimeSpan.FromSeconds(2));
        mic.Progress = other.Progress = new(CaptureActivity.Idle, TimeSpan.FromSeconds(10));
        clock.Advance(10);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(source.Watermark <= TimeSpan.FromSeconds(2));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(TimeSpan.FromSeconds(2), reader.Current.Offset);
        source.Stop();
    }

    [Fact]
    public async Task Idle_peer_releases_transcription_before_stop_on_the_next_tick()
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store(); var id = store.Create(fixture.Meta());
        var mic = new TimedCapture(); var other = new TimedCapture(); var clock = new ManualClock();
        var source = new LiveTwoTrackSource(mic, other, timeProvider: clock);
        var emitted = new TaskCompletionSource<Segment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new MeetingTranscriber(new Engine(), new Gate(), store).RunAsync(id, source,
            part => emitted.TrySetResult(part), TestContext.Current.CancellationToken);
        try
        {
            clock.Advance(5, false);
            mic.Emit(Audio, TimeSpan.FromSeconds(4));
            // Clear the microphone segment's end plus the two-second echo window.
            mic.Progress = new(CaptureActivity.Idle, TimeSpan.FromSeconds(8));
            other.Progress = new(CaptureActivity.Silent, TimeSpan.FromSeconds(8));
            clock.Advance(3);
            var segment = await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(4), segment.Start);
            Assert.Equal(segment, Assert.Single(store.ReadTranscript(id)));
            Assert.False(mic.Stopped || other.Stopped);
        }
        finally { source.Stop(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Engine : ISpeechEngine
    {
        public string ModelPath => "synthetic";
        public DecodeOptions Decode { get; private set; } = DecodeOptions.Default;
        public void Reconfigure(DecodeOptions decode) => Decode = decode;
        public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken) => Task.FromResult("A. Example agreed");
        public void Dispose() { }
    }
    private sealed class Gate : IVoiceGate
    {
        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken) => Task.FromResult(new VoiceVerdict(true, "Synthetic fixture"));
    }

    private class BasicCapture : IAudioCapture
    {
        private Action<CapturedAudio, CutReason>? _part;
        public bool Stopped;
        public float CurrentLevel => 0;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start(string? deviceSetting, long keyDownStamp) { }
        public CapturedAudio Stop() { Stopped = true; return CapturedAudio.Empty; }
        public void SegmentAtPauses(double threshold, Action<CapturedAudio, CutReason> part) => _part = part;
        public void EmitLegacy(CapturedAudio audio) => _part!(audio, CutReason.Pause);
    }

    private sealed class TimedCapture : BasicCapture, ITimestampedCapture
    {
        private Action<CapturedAudio, CutReason, TimeSpan>? _part;
        public Stopwatch? Clock;
        public CaptureProgress Progress { get; set; } = new(CaptureActivity.Silent, TimeSpan.Zero);
        public (CapturedAudio Part, TimeSpan Offset) Tail = (CapturedAudio.Empty, TimeSpan.Zero);
        public void Start(string? deviceSetting, Stopwatch sessionClock) => Clock = sessionClock;
        public void SegmentAtPauses(double threshold, Action<CapturedAudio, CutReason, TimeSpan> part) => _part = part;
        public void Emit(CapturedAudio audio, TimeSpan offset) => _part!(audio, CutReason.Pause, offset);
        public (CapturedAudio Part, TimeSpan Offset) StopTimestamped() { Stopped = true; return Tail; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private TimerCallback? _callback;
        private object? _state;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; return new ManualTimer(); }
        public void Advance(double seconds, bool tick = true) { _ticks += TimeSpan.FromSeconds(seconds).Ticks; if (tick) _callback?.Invoke(_state); }
        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
