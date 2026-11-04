using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dictum.Meetings;
using Dictum.Meetings.Store;
using Dictum.Settings;

namespace Dictum.Tests;

// Synthesized fixtures only, all files below the current worktree.
internal sealed class MeetingFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Worktree(), ".meeting-tests-" + Guid.NewGuid().ToString("N"));
    public FakeCredentialStore Credentials { get; } = new();
    public MeetingKeyring Keyring => new(Credentials);
    public static readonly DateTimeOffset Started = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public const string TranscriptText = "A. Example said the budget review moves to Friday";
    public static readonly Segment[] Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), Channel.You, TranscriptText)];
    public static Summary Summary(string engine = "Local") => new("Review the budget on Friday.", engine, "general", Started.AddHours(1), [new("A. Example", "Review the budget")]);
    public MeetingMeta Meta() => new(MeetingId.New(), "Invented meeting", Started, TimeSpan.FromMinutes(20), MeetingSource.Recorded,
        new Agreement(Started, "spoken", "test-v1", AgreementKind.Live), [], [], false, null, null);
    public FileMeetingStore Store(MeetingsSettings? settings = null) => new(Root, settings ?? new(), Keyring, engine => "CAPTION:" + engine);
    public string At(MeetingId id, string name) => Path.Combine(Root, id.ToString(), name);
    public MeetingId Populate(FileMeetingStore store)
    {
        var id = store.Create(Meta());
        store.AppendSegments(id, Segments);
        store.WriteSummary(id, Summary());
        foreach (var channel in Enum.GetValues<Channel>()) { using var audio = new MemoryStream(Wav()); store.WriteAudio(id, channel, audio); }
        return id;
    }
    public static byte[] Wav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        const int samples = 1600;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
        for (var n = 0; n < samples; n++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * n / 16000)));
        return stream.ToArray();
    }
    private static string Worktree()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "MEETINGS-CONTRACTS.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Tests must run inside this worktree.");
    }
    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        var worktree = Path.GetFullPath(Worktree()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(worktree, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped worktree.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

public class MeetingStoreTests
{
    [Fact]
    public void Swapping_audio_files_within_a_meeting_is_rejected()
    {
        using var fixture = new MeetingFixture(); var store = fixture.Store(); var id = fixture.Populate(store);
        var you = fixture.At(id, "audio-you.wav.enc"); var others = fixture.At(id, "audio-others.wav.enc");
        var saved = File.ReadAllBytes(you);
        File.WriteAllBytes(you, File.ReadAllBytes(others)); File.WriteAllBytes(others, saved);
        foreach (var channel in new[] { Channel.You, Channel.Others })
        {
            using var input = store.OpenAudio(id, channel);
            Assert.Throws<CryptographicException>(() => input.ReadByte());
        }
    }

    [Fact]
    public void A_file_copied_from_another_meeting_is_rejected_even_with_its_key_wrap()
    {
        using var fixture = new MeetingFixture(); var store = fixture.Store();
        var first = fixture.Populate(store); var second = fixture.Populate(store);
        // Copy the wrap too so failure proves meeting identity binding, independent of key separation.
        var meta = store.Read(second) with { Wraps = store.Read(first).Wraps };
        File.WriteAllText(fixture.At(second, "meta.json"), JsonSerializer.Serialize(meta));
        File.Copy(fixture.At(first, "audio-you.wav.enc"), fixture.At(second, "audio-you.wav.enc"), true);
        using var input = store.OpenAudio(second, Channel.You);
        Assert.Throws<CryptographicException>(() => input.ReadByte());
    }

    [Fact]
    public void A_meeting_round_trips_meta_transcript_summary_and_audio()
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store();
        var meta = fixture.Meta();
        var id = store.Create(meta);
        var marker = new Marker(TimeSpan.FromSeconds(1), MarkerKind.Moment);
        store.AddMarker(id, marker);
        store.AppendSegments(id, MeetingFixture.Segments);
        store.WriteSummary(id, MeetingFixture.Summary());
        foreach (var channel in Enum.GetValues<Channel>()) { using var audio = new MemoryStream(MeetingFixture.Wav()); store.WriteAudio(id, channel, audio); }
        var reopened = fixture.Store();
        var read = reopened.Read(id);
        Assert.Equal(meta.Id, read.Id); Assert.Equal(meta.Title, read.Title); Assert.Equal(meta.StartedAt, read.StartedAt);
        Assert.Equal(meta.Duration, read.Duration); Assert.Equal(meta.Source, read.Source); Assert.Equal(meta.Agreement, read.Agreement);
        Assert.Equal(marker, Assert.Single(read.Markers));
        Assert.Equal("owner", Assert.Single(read.Wraps).RecipientId);
        Assert.Equal("Local", read.SummaryEngine); Assert.Equal("general", read.SummaryTemplate); Assert.False(read.HasVideo);
        Assert.Equal(MeetingFixture.Segments, reopened.ReadTranscript(id));
        var summary = Assert.IsType<Summary>(reopened.ReadSummary(id));
        Assert.Equal(MeetingFixture.Summary().Markdown, summary.Markdown);
        Assert.Equal(MeetingFixture.Summary().Items, summary.Items); Assert.Equal(MeetingFixture.Summary().At, summary.At);
        foreach (var channel in Enum.GetValues<Channel>())
        {
            using var audio = reopened.OpenAudio(id, channel); using var copy = new MemoryStream(); audio.CopyTo(copy);
            Assert.Equal(MeetingFixture.Wav(), copy.ToArray());
        }
        Assert.Equal(id, Assert.Single(reopened.List()).Id);
        Assert.Equal(Directory.GetFiles(Path.Combine(fixture.Root, id.ToString()), "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length), store.SizeOf(id));
    }

    [Fact]
    public void Files_on_disk_are_ciphertext_and_contain_no_transcript_words()
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store(); var id = fixture.Populate(store);
        store.AppendSegments(id, [new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), Channel.Others, "Invented confidential platypus")]);
        foreach (var path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("budget", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("platypus", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Example", text, StringComparison.Ordinal);
            if (path.EndsWith(".enc", StringComparison.Ordinal)) Assert.True(bytes.AsSpan(0, 8).SequenceEqual("DCTMENC\u0001"u8));
        }
        var owner = fixture.Keyring.OwnerKey();
        Assert.DoesNotContain(Convert.ToBase64String(owner), File.ReadAllText(fixture.At(id, "meta.json")), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(KeepAudio.Forever, 200, false)]
    [InlineData(KeepAudio.Days30, 29, false)]
    [InlineData(KeepAudio.Days30, 30, true)]
    [InlineData(KeepAudio.Days90, 89, false)]
    [InlineData(KeepAudio.Days90, 90, true)]
    public void Retention_deletes_audio_only_and_never_transcript_or_summary(KeepAudio keep, int days, bool removed)
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store(new() { KeepAudio = keep }); var id = fixture.Populate(store);
        var transcript = File.ReadAllBytes(fixture.At(id, "transcript.json.enc"));
        var summary = File.ReadAllBytes(fixture.At(id, "summary.md.enc"));
        var videoPath = Path.Combine(fixture.Root, "invented.mp4"); File.WriteAllBytes(videoPath, [0, 1, 2, 3]);
        store.WriteVideo(id, videoPath);
        store.ApplyRetention(MeetingFixture.Started.AddMinutes(20).AddDays(days));
        foreach (var name in new[] { "you", "others", "mixed" }) Assert.Equal(!removed, File.Exists(fixture.At(id, $"audio-{name}.wav.enc")));
        Assert.Equal(transcript, File.ReadAllBytes(fixture.At(id, "transcript.json.enc")));
        Assert.Equal(summary, File.ReadAllBytes(fixture.At(id, "summary.md.enc")));
        Assert.Equal(MeetingFixture.Segments, store.ReadTranscript(id)); Assert.NotNull(store.ReadSummary(id));
        Assert.True(File.Exists(fixture.At(id, "video.mp4.enc"))); Assert.True(store.Read(id).HasVideo);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, File.ReadAllBytes(videoPath));
    }

    [Fact]
    public void Delete_removes_the_folder_and_the_index_entry()
    {
        using var fixture = new MeetingFixture(); var store = fixture.Store(); var id = fixture.Populate(store);
        var other = store.Create(fixture.Meta()); store.Delete(id);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, id.ToString())));
        Assert.DoesNotContain(id.ToString(), File.ReadAllText(Path.Combine(fixture.Root, "index.json")), StringComparison.Ordinal);
        Assert.Equal(other, Assert.Single(store.List()).Id);
    }

    [Fact]
    public void Search_finds_a_transcript_word_and_returns_a_snippet()
    {
        using var fixture = new MeetingFixture(); var store = fixture.Store(); var id = fixture.Populate(store);
        var found = Assert.Single(store.Search("BUDGET"));
        Assert.Equal(id, found.Item1.Id); Assert.Contains("budget", found.Snippet, StringComparison.Ordinal);
        Assert.Empty(store.Search("unspokenword"));
    }

    [Fact]
    public void Index_is_rebuilt_when_missing()
    {
        using var fixture = new MeetingFixture(); var store = fixture.Store(); var id = fixture.Populate(store);
        var index = Path.Combine(fixture.Root, "index.json"); File.Delete(index);
        Assert.Equal(id, Assert.Single(fixture.Store().List()).Id); Assert.True(File.Exists(index));
        File.WriteAllText(index, "[]"); Assert.Equal(id, Assert.Single(fixture.Store().List()).Id);
        Assert.Contains(id.ToString(), File.ReadAllText(index), StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_setting_moves_the_root()
    {
        using var fixture = new MeetingFixture();
        var paths = AppPaths.ForRoots(Path.Combine(fixture.Root, "config"), Path.Combine(fixture.Root, "local"), fixture.Root, null);
        var defaultStore = new FileMeetingStore(paths, new(), fixture.Keyring, e => "CAPTION:" + e);
        var defaultId = defaultStore.Create(fixture.Meta());
        Assert.True(File.Exists(Path.Combine(paths.LocalDir, "meetings", defaultId.ToString(), "meta.json")));
        var chosen = Path.Combine(fixture.Root, "chosen");
        var moved = new FileMeetingStore(paths, new() { Folder = chosen }, fixture.Keyring, e => "CAPTION:" + e);
        var id = moved.Create(fixture.Meta());
        Assert.True(File.Exists(Path.Combine(chosen, id.ToString(), "meta.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.LocalDir, "meetings", id.ToString())));
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Ollama")]
    [InlineData("OwnKey")]
    public void Export_markdown_carries_the_caption_for_the_engine_that_wrote_the_summary(string engine)
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store(new() { SummaryEngine = SummaryEngine.OwnKey }); var id = store.Create(fixture.Meta());
        store.AppendSegments(id, MeetingFixture.Segments); store.WriteSummary(id, MeetingFixture.Summary(engine));
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        var export = store.ExportMarkdown(id);
        Assert.Contains("CAPTION:" + engine, export, StringComparison.Ordinal);
        Assert.Contains(MeetingFixture.TranscriptText, export, StringComparison.Ordinal);
        Assert.Contains(MeetingFixture.Summary().Markdown, export, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public void Rapid_appends_are_durable_without_rewriting_the_snapshot_each_time()
    {
        using var fixture = new MeetingFixture(); var store = fixture.Store(); var id = store.Create(fixture.Meta());
        store.AppendSegments(id, MeetingFixture.Segments);
        var snapshot = File.ReadAllBytes(fixture.At(id, "transcript.json.enc"));
        store.AppendSegments(id, [new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), Channel.Others, "Invented second segment")]);
        Assert.Equal(snapshot, File.ReadAllBytes(fixture.At(id, "transcript.json.enc")));
        Assert.Equal(2, fixture.Store().ReadTranscript(id).Count);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, id.ToString()), "transcript-batch-*.json.enc"));
    }

    [Fact]
    public void Invalid_ids_cannot_escape_the_meetings_root()
    {
        Assert.Throws<ArgumentException>(() => new MeetingId("../escape"));
        using var fixture = new MeetingFixture();
        Assert.Throws<ArgumentException>(() => fixture.Store().Read(default));
    }
}
