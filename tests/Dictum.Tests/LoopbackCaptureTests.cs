using System.Diagnostics;
using Dictum.Audio;
using Dictum.Platform;
using Dictum.Windows;
using Dictum.Windows.Audio;
using NAudio.Wave;

namespace Dictum.Tests;

public class LoopbackCaptureTests
{
    [Fact]
    public void Restored_package_contains_the_loopback_type()
    {
        Assert.Equal("NAudio.Wave.WasapiLoopbackCapture", typeof(WasapiLoopbackCapture).FullName);
        Assert.Equal("NAudio.Wasapi", typeof(WasapiLoopbackCapture).Assembly.GetName().Name);
        Assert.NotNull(typeof(WasapiLoopbackCapture).GetConstructor(Type.EmptyTypes));
    }

    [Fact]
    public void Platform_seams_create_loopback_and_existing_decoders_without_opening_devices()
    {
        TestEnv.WindowsOnly("Constructs WindowsPlatform and its Windows services.");
        using var windows = new WindowsPlatform();
        using var loopback = Assert.IsType<LoopbackCapture>(windows.CreateLoopbackCapture(NullLog.Instance));
        Assert.IsType<MediaFoundationDecoder>(windows.AudioDecoder);
        Assert.IsAssignableFrom<ITimestampedCapture>(loopback);
    }

    [Fact]
    public void Mac_platform_seam_is_constructible_without_native_audio()
    {
        using var mac = new Dictum.Mac.MacPlatform();
        Assert.Null(mac.CreateLoopbackCapture(NullLog.Instance));
        Assert.IsType<Dictum.Meetings.Import.WavOnlyDecoder>(mac.AudioDecoder);
    }

    [Fact]
    public void Synthetic_stereo_48k_uses_the_microphone_decode_and_resample_path()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var raw = new byte[48000 * 2 * sizeof(float)];
        for (var i = 0; i < 48000; i++)
        {
            var sample = (float)(.25 * Math.Sin(2 * Math.PI * 440 * i / 48000));
            BitConverter.TryWriteBytes(raw.AsSpan(i * 8), sample);
            BitConverter.TryWriteBytes(raw.AsSpan(i * 8 + 4), sample);
        }
        var expected = new CaptureBuffer(SampleDecoder.EncodingOf(format), 2, 48000);
        expected.Append(raw);
        var pipeline = new LoopbackAudioBuffer(format, TimeSpan.FromSeconds(7));
        pipeline.Append(raw);
        var actual = pipeline.Seal();
        Assert.Equal(expected.Seal().Samples, actual.Part.Samples);
        Assert.InRange(actual.Part.Samples.Length, 15950, 16000);
        Assert.Equal(TimeSpan.FromSeconds(7), actual.Offset);
    }

    [Fact]
    public void Quiet_blocks_remain_continuous_and_advance_idle_progress()
    {
        var pipeline = new LoopbackAudioBuffer(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1), TimeSpan.FromSeconds(2));
        var seen = new List<(CapturedAudio Audio, TimeSpan Offset)>();
        pipeline.SegmentAtPauses(.01, (audio, _, offset) => seen.Add((audio, offset)));
        pipeline.Append(new byte[16000 * sizeof(float)]);
        Assert.Equal(16000, seen.Sum(p => p.Audio.Samples.Length));
        Assert.Equal(50, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(20 * i), seen[i].Offset);
            Assert.All(seen[i].Audio.Samples, sample => Assert.Equal(0, sample));
        }
        Assert.Equal(CaptureActivity.Silent, pipeline.Progress.Activity);
        Assert.Equal(TimeSpan.FromSeconds(3), pipeline.Progress.PendingStart);
    }

    [Fact]
    public void Pause_cuts_and_stop_tail_tile_speech_without_losing_samples()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        var samples = Enumerable.Repeat(.25f, 16000 * 3).Concat(new float[16000]).ToArray();
        var raw = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, raw, 0, raw.Length);
        var pipeline = new LoopbackAudioBuffer(format, TimeSpan.Zero);
        var seen = new List<(CapturedAudio Part, TimeSpan Offset)>();
        pipeline.SegmentAtPauses(.01, (audio, _, offset) => seen.Add((audio, offset)));
        pipeline.Append(raw);
        seen.Add(pipeline.Seal());
        Assert.Equal(samples, seen.SelectMany(p => p.Part.Samples));
        var elapsed = TimeSpan.Zero;
        foreach (var part in seen) { Assert.Equal(elapsed, part.Offset); elapsed += part.Part.Duration; }
        Assert.Contains(seen, p => p.Part.PeakLevel > .1);
    }

    [Fact]
    public void Missing_packets_are_filled_from_clock_progress_without_duplicate_silence()
    {
        var clock = new GapClock();
        var start = clock.GetTimestamp();
        var pipeline = new LoopbackAudioBuffer(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1), TimeSpan.Zero);
        var seen = new List<float>();
        pipeline.SegmentAtPauses(.01, (audio, _, _) => seen.AddRange(audio.Samples));
        clock.Advance(TimeSpan.FromSeconds(2));
        pipeline.FillSilence(clock.GetElapsedTime(start));
        pipeline.FillSilence(clock.GetElapsedTime(start));
        Assert.Equal(32000, seen.Count);
        var raw = new byte[1600 * sizeof(float)];
        for (var i = 0; i < 1600; i++) BitConverter.TryWriteBytes(raw.AsSpan(i * 4), .25f);
        pipeline.Append(raw, TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        pipeline.FillSilence(clock.GetElapsedTime(start));
        seen.AddRange(pipeline.Seal().Part.Samples);
        Assert.Equal(33600, seen.Count);
        Assert.All(seen.Take(32000), sample => Assert.Equal(0, sample));
        Assert.All(seen.Skip(32000), sample => Assert.Equal(.25f, sample));
    }

    private sealed class GapClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    [Fact]
    public void Packet_clock_anchors_prevent_sample_count_drift()
    {
        var pipeline = new LoopbackAudioBuffer(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1), TimeSpan.Zero);
        var offsets = new List<TimeSpan>();
        pipeline.SegmentAtPauses(.01, (_, _, offset) => offsets.Add(offset));
        pipeline.Append(new byte[320 * sizeof(float)], TimeSpan.FromSeconds(1));
        pipeline.Append(new byte[320 * sizeof(float)], TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, offsets);
    }

    [Fact]
    public async Task Real_default_output_produces_continuous_audio()
    {
        TestEnv.WindowsOnly("Plays a generated tone through the default Windows output.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_DEVICE_TESTS") == "1" &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")), "Set DICTUM_DEVICE_TESTS=1 outside CI to capture the default Windows output; audio stays in memory.");
        using var capture = new LoopbackCapture(NullLog.Instance);
        var clock = Stopwatch.StartNew();
        capture.Start(null, clock);
        var pcm = new byte[48000 * 2];
        for (var i = 0; i < 48000; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(8000 * Math.Sin(2 * Math.PI * 1000 * i / 48000)));
        using var stream = new MemoryStream(pcm);
        using var tone = new RawSourceWaveStream(stream, new WaveFormat(48000, 16, 1));
        using var output = new WasapiOut();
        output.Init(tone);
        output.Play();
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        output.Stop();
        var audio = capture.StopTimestamped();
        Assert.InRange(audio.Part.Duration.TotalSeconds, .7, 1.5);
        var energies = Enumerable.Range(0, audio.Part.Samples.Length / 1600)
            .Select(i => (Tone: Energy(audio.Part.Samples, i * 1600, 1000), Other: Energy(audio.Part.Samples, i * 1600, 700))).ToArray();
        Assert.Contains(energies, energy => energy.Tone > .0001 && energy.Tone > energy.Other * 10);
    }

    private static double Energy(float[] samples, int start, int frequency)
    {
        double real = 0, imaginary = 0;
        for (var i = 0; i < 1600; i++)
        {
            var phase = 2 * Math.PI * frequency * i / 16000;
            real += samples[start + i] * Math.Cos(phase);
            imaginary += samples[start + i] * Math.Sin(phase);
        }
        return (real * real + imaginary * imaginary) / (1600 * 1600);
    }

    [Fact]
    public void Batched_packets_tile_and_a_real_one_second_gap_adds_only_missing_silence()
    {
        var now = TimeSpan.Zero;
        var device = new FakeSource();
        using var capture = new LoopbackCapture(NullLog.Instance, _ => device, () => now);
        capture.Start(null, new Stopwatch());
        var seen = new List<(CapturedAudio Part, TimeSpan Offset)>();
        capture.SegmentAtPauses(.01, (audio, _, offset) => seen.Add((audio, offset)));
        now = TimeSpan.FromMilliseconds(50);
        for (var i = 0; i < 5; i++) device.Emit(160, .25f);
        capture.Pulse();
        // Another wake-up batches five 10 ms packets, with 10 ms of scheduling jitter.
        // The short discrepancy must not insert silence between the speech batches.
        now = TimeSpan.FromMilliseconds(110);
        for (var i = 0; i < 5; i++) device.Emit(160, .5f);
        capture.Pulse();
        now = TimeSpan.FromMilliseconds(1110);
        device.Emit(160, .25f);
        capture.Pulse();
        seen.Add(capture.StopTimestamped());
        var samples = seen.SelectMany(part => part.Part.Samples).ToArray();
        Assert.Equal(17760, samples.Length);
        Assert.All(samples.Take(800), sample => Assert.Equal(.25f, sample));
        Assert.All(samples.Skip(800).Take(800), sample => Assert.Equal(.5f, sample));
        Assert.All(samples.Skip(1600).Take(16000), sample => Assert.Equal(0, sample));
        Assert.All(samples.Skip(17600), sample => Assert.Equal(.25f, sample));
        var end = TimeSpan.Zero;
        foreach (var part in seen.Where(part => part.Part.Samples.Length > 0))
        { Assert.Equal(end, part.Offset); end += part.Part.Duration; }
    }

    [Fact]
    public void Idle_pulses_cut_speech_and_advance_progress_before_stop()
    {
        var now = TimeSpan.Zero;
        var device = new FakeSource();
        using var capture = new LoopbackCapture(NullLog.Instance, _ => device, () => now);
        capture.Start(null, new Stopwatch());
        var seen = new List<(CapturedAudio Part, CutReason Reason, TimeSpan Offset)>();
        capture.SegmentAtPauses(.01, (audio, reason, offset) => seen.Add((audio, reason, offset)));
        for (var i = 0; i < 30; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            device.Emit(1600, .25f);
            capture.Pulse();
        }
        Assert.Empty(seen);
        var pending = capture.Progress.PendingStart;
        for (var i = 0; i < 20; i++)
        {
            now += TimeSpan.FromMilliseconds(100);
            capture.Pulse();
            var progress = capture.Progress.PendingStart;
            Assert.True(progress >= pending);
            pending = progress;
            if (i == 4) Assert.Empty(seen); // Exactly 500 ms is still within the idle threshold.
        }
        var speech = Assert.Single(seen.Where(part => part.Part.PeakLevel > .1));
        Assert.Equal(CutReason.Pause, speech.Reason);
        Assert.Equal(TimeSpan.Zero, speech.Offset);
        Assert.Equal(48000, speech.Part.Samples.Count(sample => sample == .25f));
        Assert.Equal(CaptureActivity.Silent, capture.Progress.Activity);
        Assert.True(pending > TimeSpan.FromSeconds(4));

        // A late packet follows the silence already committed by sample count.
        device.Emit(320, .5f);
        capture.Pulse();
        var tail = capture.StopTimestamped();
        seen.Add((tail.Part, CutReason.Stop, tail.Offset));
        var end = TimeSpan.Zero;
        foreach (var part in seen)
        {
            Assert.Equal(end, part.Offset);
            end += part.Part.Duration;
        }
        Assert.Equal(320, seen.Sum(part => part.Part.Samples.Count(sample => sample == .5f)));
        Assert.Equal(now, end);
    }

    [Fact]
    public async Task Stop_preserves_packets_delivered_before_recording_stopped()
    {
        var now = TimeSpan.Zero;
        var device = new FakeSource { AutoStop = false };
        using var capture = new LoopbackCapture(NullLog.Instance, _ => device, () => now);
        capture.Start(null, new Stopwatch());
        now = TimeSpan.FromMilliseconds(50);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.OnStop = () => stopping.TrySetResult();
        var stop = Task.Run(capture.StopTimestamped);
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // StopRecording has returned, but RecordingStopped has deliberately not fired.
            await Task.WhenAny(stop, Task.Delay(100));
            for (var i = 0; i < 5; i++) device.Emit(160, .25f);
        }
        finally { device.CompleteStop(); }
        var audio = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(800, audio.Part.Samples.Length);
        Assert.All(audio.Part.Samples, sample => Assert.Equal(.25f, sample));
        Assert.True(device.Disposed);
    }

    [Fact]
    public void Old_generation_data_and_failure_callbacks_do_not_reach_the_restarted_capture()
    {
        var first = new FakeSource();
        var second = new FakeSource();
        var devices = new Queue<FakeSource>([first, second]);
        var work = new Queue<Action>();
        var now = TimeSpan.Zero;
        using var capture = new LoopbackCapture(NullLog.Instance, _ => devices.Dequeue(), () => now, work.Enqueue);
        var failures = new List<Exception>();
        capture.Failed += failures.Add;
        capture.Start(null, new Stopwatch());
        // Retain delegates as if the first device had callbacks already in flight when detached.
        var oldData = first.DataCallback!;
        var oldStopped = first.StoppedCallback!;
        capture.Stop();
        capture.Start(null, new Stopwatch());
        now = TimeSpan.FromMilliseconds(10);
        var stalePacket = new byte[160 * sizeof(float)];
        for (var i = 0; i < 160; i++) BitConverter.TryWriteBytes(stalePacket.AsSpan(i * 4), .75f);
        oldData(first, new WaveInEventArgs(stalePacket, stalePacket.Length));
        oldStopped(first, new StoppedEventArgs(new InvalidOperationException("Old device failure")));
        oldStopped(first, new StoppedEventArgs());
        while (work.TryDequeue(out var action)) action();
        Assert.Empty(work);
        Assert.Empty(failures);
        Assert.False(second.Disposed);
        second.Emit(160, .25f);
        var audio = capture.Stop();
        Assert.Equal(160, audio.Samples.Length);
        Assert.All(audio.Samples, sample => Assert.Equal(.25f, sample));
    }

    [Fact]
    public async Task Stop_times_out_without_recording_stopped_and_fills_only_missing_tail()
    {
        var now = TimeSpan.Zero;
        var device = new FakeSource { AutoStop = false };
        var log = new DrainLog();
        using var capture = new LoopbackCapture(log, _ => device, () => now);
        capture.Start(null, new Stopwatch());
        now = TimeSpan.FromMilliseconds(50);
        device.Emit(800, .25f);
        capture.Pulse();
        now = TimeSpan.FromMilliseconds(100);
        var elapsed = Stopwatch.StartNew();
        var stop = Task.Run(capture.StopTimestamped);
        (CapturedAudio Part, TimeSpan Offset) audio;
        try { audio = await stop.WaitAsync(TimeSpan.FromSeconds(4)); }
        finally
        {
            // Release the worker even if an unbounded-wait regression fails the test.
            device.CompleteStop();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        Assert.Contains(log.Errors, message => message.Contains("timed out") && message.Contains("RecordingStopped"));
        Assert.True(device.Disposed);
        Assert.Null(device.DataCallback);
        Assert.Null(device.StoppedCallback);
        Assert.Equal(TimeSpan.Zero, audio.Offset);
        Assert.Equal(1600, audio.Part.Samples.Length);
        Assert.All(audio.Part.Samples.Take(800), sample => Assert.Equal(.25f, sample));
        Assert.All(audio.Part.Samples.Skip(800), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Stale_failure_cleanup_and_callbacks_cannot_dispose_a_new_session()
    {
        var first = new FakeSource(); var second = new FakeSource();
        var devices = new Queue<FakeSource>([first, second]);
        var work = new Queue<Action>();
        using var capture = new LoopbackCapture(NullLog.Instance, _ => devices.Dequeue(), () => TimeSpan.Zero, work.Enqueue);
        var failures = new List<Exception>();
        capture.Failed += failures.Add;
        capture.Start(null, new Stopwatch());
        var stale = first.StoppedCallback;
        first.Fail();
        capture.Stop();
        capture.Start(null, new Stopwatch());
        stale!(first, new StoppedEventArgs(new InvalidOperationException("Stale failure")));
        while (work.TryDequeue(out var action)) action();
        Assert.False(second.Disposed);
        Assert.Empty(failures);
        second.Emit(160, .25f);
        Assert.Equal(160, capture.Stop().Samples.Length);
    }

    private sealed class DrainLog : ILog
    {
        public List<string> Errors { get; } = new();
        public void Write(LogLevel level, string message)
        {
            if (level == LogLevel.Error) Errors.Add(message);
        }
    }

    private sealed class FakeSource : ILoopbackCaptureSource
    {
        public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public EventHandler<WaveInEventArgs>? DataCallback => DataAvailable;
        public EventHandler<StoppedEventArgs>? StoppedCallback => RecordingStopped;
        public bool AutoStop = true;
        public Action? OnStop;
        public bool Disposed;
        public void Emit(int frames, float value)
        {
            var raw = new byte[frames * sizeof(float)];
            for (var i = 0; i < frames; i++) BitConverter.TryWriteBytes(raw.AsSpan(i * 4), value);
            DataAvailable?.Invoke(this, new WaveInEventArgs(raw, raw.Length));
        }
        public void Fail() => RecordingStopped?.Invoke(this, new StoppedEventArgs(new InvalidOperationException("Synthetic failure")));
        public void StartRecording() { }
        public void CompleteStop() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void StopRecording() { OnStop?.Invoke(); if (AutoStop) CompleteStop(); }
        public void CheckDevice() { }
        public void Dispose() => Disposed = true;
    }
}
