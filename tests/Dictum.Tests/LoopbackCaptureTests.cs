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
        using var windows = new WindowsPlatform();
        using var loopback = Assert.IsType<LoopbackCapture>(windows.CreateLoopbackCapture(NullLog.Instance));
        Assert.IsType<MediaFoundationDecoder>(windows.AudioDecoder);
        using var mac = new Dictum.Mac.MacPlatform();
        Assert.Null(mac.CreateLoopbackCapture(NullLog.Instance));
        Assert.IsType<Dictum.Meetings.Import.WavOnlyDecoder>(mac.AudioDecoder);
        Assert.IsAssignableFrom<ITimestampedCapture>(loopback);
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
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_DEVICE_TESTS") == "1" &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")), "Set DICTUM_DEVICE_TESTS=1 outside CI to capture the default Windows output; audio stays in memory.");
        using var capture = new LoopbackCapture(NullLog.Instance);
        var clock = Stopwatch.StartNew();
        capture.Start(null, clock);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        var audio = capture.StopTimestamped();
        Assert.InRange(audio.Part.Duration.TotalSeconds, .7, 1.5);
    }
}
