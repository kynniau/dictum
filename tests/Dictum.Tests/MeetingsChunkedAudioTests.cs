using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Audio;
using Dictum.Meetings;
using Dictum.Meetings.Store;

namespace Dictum.Tests;

public sealed class MeetingsChunkedAudioTests
{
    private static readonly float[] Samples = [0, .25f, -.5f, 1, -1];
    private static byte[] Read(IMeetingStore store, MeetingId id, Channel channel = Channel.You)
    {
        using var audio = store.OpenAudio(id, channel); using var copy = new MemoryStream();
        audio.CopyTo(copy); return copy.ToArray();
    }
    private static byte[] Pcm(params float[][] parts)
    {
        var samples = parts.SelectMany(p => p).ToArray(); var result = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), (short)(samples[i] * 32767));
        return result;
    }
    [Fact]
    public void Chunks_survive_dispose_without_complete_and_reopen_exactly()
    {
        using var files = new MeetingFixture(); var store = files.Store(); var id = store.Create(files.Meta());
        store.AppendSegments(id, MeetingFixture.Segments);
        using (var writer = store.BeginAudio(id, Channel.You)) { writer.Append(Samples); writer.Append(Samples); }
        var reopened = files.Store();
        Assert.Equal(Pcm(Samples, Samples), Read(reopened, id)[44..]);
        Assert.True(Assert.Single(reopened.List()).AudioIncomplete);
        Assert.Equal(MeetingFixture.Segments, reopened.ReadTranscript(id));
    }
    [Fact]
    public void Maximum_length_parts_on_both_tracks_are_saved_without_failing()
    {
        // The segmenter cuts unbroken speech at LimitBlocks (30 s); at Stop both tracks hand over a tail at once.
        using var files = new MeetingFixture(); var store = files.Store(); var id = store.Create(files.Meta());
        var longest = SpeechSegmenter.LimitBlocks * CaptureBuffer.BlockSamples;
        Exception? failure = null;
        using (var saving = new LiveAudioWriter(store, id, Channel.You, Channel.Others))
        {
            saving.Failed += error => failure = error;
            saving.Enqueue(Channel.You, new float[longest], TimeSpan.Zero);
            saving.Enqueue(Channel.Others, new float[longest], TimeSpan.Zero);
            saving.Complete();
        }
        Assert.Null(failure);
        Assert.Equal(44 + longest * 2, Read(store, id, Channel.You).Length);
        Assert.Equal(44 + longest * 2, Read(store, id, Channel.Others).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(31)]
    public void Torn_final_record_is_ignored(int missing)
    {
        using var files = new MeetingFixture(); var store = files.Store(); var id = store.Create(files.Meta());
        using (var writer = store.BeginAudio(id, Channel.You)) { writer.Append(Samples); writer.Append(Samples); }
        var path = files.At(id, "audio-you.chunks.enc");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - missing);
        Assert.Equal(Pcm(Samples), Read(files.Store(), id)[44..]);
    }
    [Theory]
    [InlineData("corrupt")]
    [InlineData("reorder")]
    [InlineData("drop")]
    [InlineData("length")]
    [InlineData("terminal")]
    [InlineData("meeting")]
    [InlineData("channel")]
    public void Bad_chunks_fail_before_any_wav_byte_is_released(string damage)
    {
        using var files = new MeetingFixture(); var store = files.Store(); var id = store.Create(files.Meta());
        using (var writer = store.BeginAudio(id, Channel.You))
        { writer.Append(Samples); writer.Append(Samples); writer.Append(Samples); writer.Complete(); }
        var path = files.At(id, "audio-you.chunks.enc"); var bytes = File.ReadAllBytes(path);
        var size = 60 + Samples.Length * 2;
        switch (damage)
        {
            case "length": bytes[24 + size] ^= 32; break;
            case "terminal": bytes = bytes[..^60]; break;
            case "corrupt": bytes[24 + size + 16] ^= 1; break;
            case "reorder":
                var first = bytes.AsSpan(24, size).ToArray();
                bytes.AsSpan(24 + size, size).CopyTo(bytes.AsSpan(24)); first.CopyTo(bytes, 24 + size); break;
            case "drop": bytes = bytes[..(24 + size)].Concat(bytes[(24 + size * 2)..]).ToArray(); break;
            case "meeting":
                var other = store.Create(files.Meta());
                File.WriteAllText(files.At(other, "meta.json"), JsonSerializer.Serialize(store.Read(other) with { Wraps = store.Read(id).Wraps }));
                id = other; path = files.At(id, "audio-you.chunks.enc"); break;
            case "channel": path = files.At(id, "audio-others.chunks.enc"); break;
        }
        File.WriteAllBytes(path, bytes);
        using var input = store.OpenAudio(id, damage == "channel" ? Channel.Others : Channel.You);
        Assert.Throws<CryptographicException>(() => input.ReadByte());
    }
    [Fact]
    public async Task Live_recording_saves_each_cut_part_before_stop_without_restarting_the_device()
    {
        using var files = new MeetingFixture(); var store = files.Store(); var id = store.Create(files.Meta());
        using var saving = new LiveAudioWriter(store, id, Channel.You);
        var capture = new Capture(); using var source = new MicrophoneMeetingSource(capture, null, .01, saving);
        source.Start();
        capture.Cut(); // the capture cuts a part at a pause while recording continues
        var until = DateTime.UtcNow.AddSeconds(8);
        while (new FileInfo(files.At(id, "audio-you.chunks.enc")).Length == 24 && DateTime.UtcNow < until)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(capture.Active); Assert.True(Read(store, id).Length > 44);
        // Restarting the device to drain it would cut words; the microphone must stay open between cuts.
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        Assert.Equal(1, capture.Starts); Assert.Equal(0, capture.Stops);
        source.Pause();
        // Drain queued work before asserting the paused file stays unchanged.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var size = new FileInfo(files.At(id, "audio-you.chunks.enc")).Length;
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(size, new FileInfo(files.At(id, "audio-you.chunks.enc")).Length);
        source.Stop(); source.CompleteAudio(); Assert.False(store.Read(id).AudioIncomplete);
    }
    private sealed class Capture : IAudioCapture
    {
        public bool Active;
        public int Starts, Stops;
        private Action<CapturedAudio, CutReason>? _part;
        public float CurrentLevel => 0;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start(string? deviceSetting, long keyDownStamp) { Active = true; Starts++; }
        public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part) => _part = part;
        public void Cut() => _part?.Invoke(new(Samples, .5f), CutReason.Pause);
        public CapturedAudio Stop() { Active = false; Stops++; return new(Samples, .5f); }
    }
}
