using System.IO.Compression;
using System.Text;
using Dictum.Llm;
using Dictum.Meetings;
using Dictum.Meetings.Import;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Transcribe;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Views.Meetings;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.Tests;

/// <summary>
/// Synthetic "voices" for speaker separation (design 1.6 B): a voice is a run of constant samples at level voice / 10, silence (0)
/// separates turns. <see cref="LevelTurnFinder"/> finds the runs and <see cref="LevelEmbedder"/> returns a fixed vector per level, so
/// clustering, mapping, naming, storage and export are deterministic. No model, no real recording.
/// </summary>
internal static class SyntheticVoices
{
    public const double Gap = 0.5;

    /// <summary>The samples, and each turn's (voice, start, end) in seconds.</summary>
    public static (float[] Samples, (int Voice, double Start, double End)[] Turns) Track(params (int Voice, double Seconds)[] turns)
    {
        var samples = new List<float>();
        var placed = new List<(int, double, double)>();
        foreach (var (voice, seconds) in turns)
        {
            var start = samples.Count / 16000.0;
            samples.AddRange(Enumerable.Repeat(voice / 10f, (int)(seconds * 16000)));
            placed.Add((voice, start, samples.Count / 16000.0));
            samples.AddRange(new float[(int)(Gap * 16000)]);
        }
        return ([.. samples], [.. placed]);
    }

    public static byte[] Wav(float[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write((short)Math.Round(sample * 32767));
        return stream.ToArray();
    }

    /// <summary>Orthogonal voices: every pair is at cosine distance 1.</summary>
    public static Dictionary<int, float[]> Distinct() => new()
    {
        [1] = [1, 0, 0, 0], [2] = [0, 1, 0, 0], [3] = [0, 0, 1, 0], [4] = [0, 0, 0, 1],
    };

    /// <summary>Voices 1 and 2 at cosine distance 0.55 (kept apart at 0.5, merged at 0.6); voice 3 far from both.</summary>
    public static Dictionary<int, float[]> TwoSimilar() => new()
    {
        [1] = [1, 0, 0], [2] = [0.45f, (float)Math.Sqrt(1 - 0.45 * 0.45), 0], [3] = [0, 0, 1],
    };
}

internal sealed class LevelTurnFinder : ISpeakerTurnFinder
{
    public float[]? Heard { get; private set; }
    public double? Threshold { get; private set; }
    public IReadOnlyList<(double Start, double End)> FindTurns(float[] samples16kMono, double threshold, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        (Heard, Threshold) = ((float[])samples16kMono.Clone(), threshold);
        var turns = new List<(double, double)>();
        for (var i = 0; i < samples16kMono.Length;)
        {
            if (samples16kMono[i] == 0) { i++; continue; }
            var start = i;
            while (i < samples16kMono.Length && samples16kMono[i] != 0) i++;
            turns.Add((start / 16000.0, i / 16000.0));
        }
        progress?.Report(1);
        return turns;
    }
}

internal sealed class LevelEmbedder(Dictionary<int, float[]> voices) : IVoiceEmbedder
{
    public float[]? Embed(float[] samples16kMono) =>
        samples16kMono.Length < 1600 ? null : voices[(int)Math.Round(samples16kMono.Average() * 10)];
}

public sealed class MeetingSpeakersTests
{
    private static MeetingId Meeting(MeetingFixture files, FileMeetingStore store, float[]? others = null, float[]? you = null, float[]? mixed = null,
        params Segment[] segments)
    {
        var id = store.Create(files.Meta());
        foreach (var (channel, samples) in new[] { (Channel.Others, others), (Channel.You, you), (Channel.Mixed, mixed) })
            if (samples is not null) { using var audio = new MemoryStream(SyntheticVoices.Wav(samples)); store.WriteAudio(id, channel, audio); }
        if (segments.Length != 0) store.AppendSegments(id, segments);
        return id;
    }

    private static Segment At((int Voice, double Start, double End) turn, Channel channel, string text) =>
        new(TimeSpan.FromSeconds(turn.Start), TimeSpan.FromSeconds(turn.End), channel, text);

    private static SpeakerSeparator Separator(IMeetingStore store, Dictionary<int, float[]> voices, LevelTurnFinder? finder = null) =>
        new(store, finder ?? new LevelTurnFinder(), new LevelEmbedder(voices), "synthetic@00000000");

    [Fact]
    public async Task Three_voices_become_Other_1_to_3_in_order_of_first_appearance()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((2, 2), (3, 1.5), (2, 1), (1, 2));
        var id = Meeting(files, store, others: samples, segments: [.. turns.Select((t, i) => At(t, Channel.Others, $"Synthetic line {i}"))]);

        var result = await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, MeetingSpeakers.DefaultThreshold, null, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 1, 3], result.Turns.Select(t => t.Speaker));
        Assert.Equal(3, result.Count);
        var stored = Assert.IsType<MeetingSpeakers>(store.ReadSpeakers(id));
        Assert.Equal(["Other 1", "Other 2", "Other 1", "Other 3"], store.ReadTranscript(id).Select(s => stored.LabelOf(s)));
        Assert.Equal(1, stored.Version); Assert.Equal(0.5, stored.Threshold); Assert.Equal("synthetic@00000000", stored.Model);
        Assert.Empty(stored.Names);
    }

    [Fact]
    public async Task You_track_is_never_clustered()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (others, otherTurns) = SyntheticVoices.Track((1, 2), (1, 1));
        var (you, youTurns) = SyntheticVoices.Track((2, 1), (3, 1), (2, 1));
        var id = Meeting(files, store, others: others, you: you,
            segments: [At(youTurns[0], Channel.You, "Budget review moves to Friday"), At(otherTurns[0], Channel.Others, "Supplier contract renewal is pending")]);
        var finder = new LevelTurnFinder();

        var result = await Separator(store, SyntheticVoices.Distinct(), finder).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);

        Assert.Equal(others.Length, finder.Heard!.Length); // the Others track (voice 1 only), never the You track (voices 2 and 3)
        Assert.All(finder.Heard.Where(s => s != 0), s => Assert.Equal(1, Math.Round(s * 10)));
        Assert.Equal(1, result.Count);
        var transcript = store.ReadTranscript(id);
        Assert.Null(result.LabelOf(transcript.Single(s => s.Channel == Channel.You)));
        Assert.Equal("You", MeetingsWindow.FormatSegment(transcript.Single(s => s.Channel == Channel.You), result).Split(" · ")[1].Split(':')[0]);
        Assert.Equal("Other 1", result.LabelOf(transcript.Single(s => s.Channel == Channel.Others)));

        // A meeting with only the You track has nothing to separate, and nothing is written.
        var mineOnly = Meeting(files, store, you: you);
        Assert.Null(SpeakerSeparator.TrackFor(store, mineOnly));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Separator(store, SyntheticVoices.Distinct()).SeparateAsync(mineOnly, 0.5, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(files.At(mineOnly, "speakers.json.enc")));
    }

    [Fact]
    public async Task Names_are_stored_only_inside_the_meeting_and_deleted_with_it()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var id = Meeting(files, store, others: samples, segments: [At(turns[0], Channel.Others, "Synthetic first"), At(turns[1], Channel.Others, "Synthetic second")]);
        var before = Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var separated = await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);
        store.WriteSpeakers(id, separated.WithNames(new Dictionary<int, string> { [1] = "Michelle Example", [2] = "  " }));

        var added = Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).Where(f => !before.Contains(f) && !f.EndsWith("index.json", StringComparison.Ordinal)).ToArray();
        Assert.Equal(files.At(id, "speakers.json.enc"), Assert.Single(added), StringComparer.OrdinalIgnoreCase);
        var name = Encoding.UTF8.GetBytes("Michelle");
        foreach (var file in Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories))
            Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(name) < 0, $"{file} holds the name in plain text");
        var stored = store.ReadSpeakers(id)!;
        Assert.Equal("Michelle Example", stored.Label(1)); Assert.Equal("Other 2", stored.Label(2)); // a blank name is no name
        Assert.Single(stored.Names);

        // Sealed to this meeting and role: the same bytes under another meeting do not open.
        var other = Meeting(files, store, others: samples);
        File.Copy(files.At(id, "speakers.json.enc"), files.At(other, "speakers.json.enc"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => store.ReadSpeakers(other));

        store.Delete(id);
        Assert.False(Directory.Exists(Path.Combine(files.Root, id.Value)));
        Assert.DoesNotContain(Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories), f => f.Contains(id.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([files.At(other, "speakers.json.enc")], Directory.GetFiles(files.Root, "speakers*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_uses_names_when_set_and_Other_n_otherwise()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var id = Meeting(files, store, others: samples, segments: [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), Channel.You, "Synthetic own words"),
            At(turns[0], Channel.Others, "Synthetic first guest"), At(turns[1], Channel.Others, "Synthetic second guest"),
            new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5), Channel.Mixed, "Synthetic typed note")]);
        var plain = store.ExportMarkdown(id);
        Assert.Contains("Others: Synthetic first guest", plain); Assert.DoesNotContain("Other 1", plain);

        var separated = await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);
        Assert.Contains("Other 1: Synthetic first guest", store.ExportMarkdown(id));
        Assert.Contains("Other 2: Synthetic second guest", store.ExportMarkdown(id));

        store.WriteSpeakers(id, separated.WithNames(new Dictionary<int, string> { [2] = "Priya Example" }));
        var named = store.ExportMarkdown(id);
        Assert.Contains("You: Synthetic own words", named);
        Assert.Contains("Other 1: Synthetic first guest", named);
        Assert.Contains("Priya Example: Synthetic second guest", named);
        Assert.Contains("Mixed: Synthetic typed note", named); // a typed note is nobody's voice
    }

    [Fact]
    public async Task Separate_again_with_fewer_speakers_merges()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2), (3, 2));
        var id = Meeting(files, store, others: samples, segments: [.. turns.Select(t => At(t, Channel.Others, "Synthetic words"))]);
        var separator = Separator(store, SyntheticVoices.TwoSimilar());
        var first = await separator.SeparateAsync(id, MeetingSpeakers.DefaultThreshold, null, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], first.Turns.Select(t => t.Speaker));
        store.WriteSpeakers(id, first.WithNames(new Dictionary<int, string> { [3] = "Alex Example" }));

        var fewer = SpeakerSeparator.Step(first.Threshold, fewer: true);
        Assert.Equal(0.6, fewer);
        var merged = await separator.SeparateAsync(id, fewer, null, TestContext.Current.CancellationToken);
        Assert.Equal([1, 1, 2], merged.Turns.Select(t => t.Speaker));
        Assert.Equal(0.6, store.ReadSpeakers(id)!.Threshold);
        Assert.Equal("Alex Example", store.ReadSpeakers(id)!.Label(2)); // the name follows its voice

        var more = await separator.SeparateAsync(id, SpeakerSeparator.Step(fewer, fewer: false), null, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], more.Turns.Select(t => t.Speaker));
        Assert.Equal(0.1, SpeakerSeparator.Step(0.1, fewer: false)); Assert.Equal(0.9, SpeakerSeparator.Step(0.9, fewer: true));
    }

    [Fact]
    public async Task Bundle_carries_speakers_file()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture(); var store = source.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var id = Meeting(source, store, others: samples, segments: [At(turns[0], Channel.Others, "Synthetic first"), At(turns[1], Channel.Others, "Synthetic second")]);
        var separated = await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);
        store.WriteSpeakers(id, separated.WithNames(new Dictionary<int, string> { [1] = "Michelle Example" }));
        Assert.True(BundleFormat.Allowed("speakers.json.enc"));

        var path = Path.Combine(source.Root, "invented.dictum"); const string code = "abandon ability able about above absent";
        store.ExportBundle(id, path, code);
        using (var zip = ZipFile.OpenRead(path))
        {
            using var entry = zip.GetEntry("speakers.json.enc")!.Open(); using var copy = new MemoryStream(); entry.CopyTo(copy);
            Assert.Equal(File.ReadAllBytes(source.At(id, "speakers.json.enc")), copy.ToArray());
        }
        var route = new MeetingProcessingRoute(null, SummaryEngine.Local, null, KeepAudio.Forever);
        var importer = new BundleImporter(target.Store(), new NoTranscriber(), (_, identity, _) => Task.FromResult<Agreement?>(
            new Agreement(DateTimeOffset.UtcNow, "written", ApprovedScriptVersion, AgreementKind.Import)
            {
                Confirmation = new("written", DateTimeOffset.UtcNow, AgreementKind.Import, ApprovedScriptVersion, MeetingsWindow.BundleImportScript(route), identity, route),
            }), ApprovedScriptVersion, MeetingsWindow.BundleImportScript);
        var imported = await importer.ImportAsync(path, code, "test", ct: TestContext.Current.CancellationToken);
        var arrived = target.Store().ReadSpeakers(imported)!;
        Assert.Equal(separated.Turns, arrived.Turns);
        Assert.Equal("Michelle Example", arrived.Label(1));
        Assert.Contains("Michelle Example: Synthetic first", target.Store().ExportMarkdown(imported));
    }

    [Fact]
    public async Task Summary_chunks_use_names()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var id = Meeting(files, store, others: samples, segments: [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), Channel.You, "Synthetic own words"),
            At(turns[0], Channel.Others, "Synthetic first guest"), At(turns[1], Channel.Others, "Synthetic second guest")]);
        var transcript = store.ReadTranscript(id);
        Assert.DoesNotContain("Other 1", Assert.Single(TranscriptChunker.Chunk(transcript, 2048, 128, includeTimestamps: true)));

        var separated = await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);
        var named = separated.WithNames(new Dictionary<int, string> { [1] = "Michelle Example" });
        var chunk = Assert.Single(TranscriptChunker.Chunk(transcript, 2048, 128, includeTimestamps: true, named));
        // The timestamp and channel stay first, as summary quotes cite them; the voice follows.
        Assert.Contains("[00:00 You] Synthetic own words", chunk);
        Assert.Contains("[00:00 Others] Michelle Example: Synthetic first guest", chunk);
        Assert.Contains("[00:02 Others] Other 2: Synthetic second guest", chunk);
        Assert.Contains("Michelle Example: Synthetic first guest", Assert.Single(TranscriptChunker.Chunk(transcript, 2048, 128, speakers: named)));

        var client = new CapturingClient();
        await new MeetingSummariser().SummariseAsync(id, transcript, SummaryTemplates.Get("general", files.Root), client, "Local", TestContext.Current.CancellationToken, named);
        Assert.Contains(client.Requests, r => r.Contains("Michelle Example: Synthetic first guest", StringComparison.Ordinal));
    }

    [Fact]
    public void Allowed_speaker_models_are_pinned()
    {
        var expected = new (string File, long Bytes, string Sha256, string Url)[]
        {
            ("pyannote-segmentation-3.0.onnx", 5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
                "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx"),
            ("3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx", 29_596_978, "357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b",
                "https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/8be2a75c9ed7a590538b268e46fbb65e1aa9d208/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx"),
        };
        Assert.Equal(expected, SpeakerModels.All.Select(s => (s.FileName, s.Bytes!.Value, s.Sha256!, s.Uri.AbsoluteUri)));
        foreach (var spec in SpeakerModels.All)
        {
            Assert.Equal("https", spec.Uri.Scheme);
            Assert.Equal(ModelCatalog.DownloadHost, spec.Uri.Host); // huggingface.co: no new host
            Assert.Matches("/resolve/[0-9a-f]{40}/", spec.Uri.AbsolutePath); // a pinned commit, never a branch
            Assert.False(spec.RequiresLogin);
            Assert.Same(spec, ModelIntegrity.Expected(Path.Combine("C:\\any", spec.FileName))); // hash-checked before first load
            foreach (var excluded in new[] { "reverb", "diarizen", "simam", "voxblink", "redimnet", "wespeaker", "resnet" })
            {
                Assert.DoesNotContain(excluded, spec.FileName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(excluded, spec.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.Equal(["reverb", "diarizen", "simam", "voxblink", "redimnet"], SpeakerModels.Excluded);
        Assert.Equal("3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx@357a834f", SpeakerModels.Tag(SpeakerModels.Embedding));
    }

    [Fact]
    public void Clustering_follows_sherpa_cut_rules()
    {
        float[][] points = [[1, 0], [0.9f, 0.1f], [0, 1], [0.1f, 0.9f]];
        Assert.Equal([0, 0, 1, 1], SpeakerClustering.Cluster(points, 0.5));
        Assert.Equal([0, 1, 2, 3], SpeakerClustering.Cluster(points, 0.001)); // merges only below the threshold
        Assert.Equal([0, 0, 0, 0], SpeakerClustering.Cluster(points, 1.01));
        Assert.Equal([0, 0, 1, 1], SpeakerClustering.Cluster(points, 0.001, count: 2)); // a fixed count ignores the threshold
        Assert.Empty(SpeakerClustering.Cluster([], 0.5));
    }

    [Fact]
    public void A_speakers_file_with_a_bad_name_or_turn_is_refused()
    {
        var good = new MeetingSpeakers(1, "m@00000000", 0.5, [new(0, 1, 1)], new Dictionary<string, string>());
        Assert.Same(good, good.Validated());
        Assert.Throws<InvalidDataException>(() => (good with { Names = new Dictionary<string, string> { ["2"] = "No such voice" } }).Validated());
        Assert.Throws<InvalidDataException>(() => (good with { Names = new Dictionary<string, string> { ["1"] = "Line\nbreak" } }).Validated());
        Assert.Throws<InvalidDataException>(() => (good with { Names = new Dictionary<string, string> { ["1"] = new string('x', 81) } }).Validated());
        Assert.Throws<InvalidDataException>(() => (good with { Version = 2 }).Validated());
        Assert.Throws<InvalidDataException>(() => (good with { Turns = [new(2, 1, 1)] }).Validated());
    }

    private sealed class NoTranscriber : IMeetingTranscriber
    {
        public Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingClient : ILlmClient
    {
        public List<string> Requests { get; } = [];
        public string Name => "synthetic";
        public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            Requests.Add(rawTranscript);
            return Task.FromResult(new LlmReply("# Summary\nSynthetic.", "stop"));
        }
    }
}

public sealed class SpeakerCheckTests
{
    private static readonly (double, double, string)[] Reference = [(0, 10, "A"), (10, 20, "B"), (20, 30, "A")];

    [Fact]
    public void A_perfect_hypothesis_scores_zero_whatever_its_speaker_names()
    {
        Assert.Equal(0, SpeakerCheck.Score(Reference, Reference).Rate);
        Assert.Equal(0, SpeakerCheck.Score(Reference, [(0, 10, "2"), (10, 20, "1"), (20, 30, "2")]).Rate);
    }

    [Fact]
    public void Missed_false_alarm_and_confusion_are_counted_outside_the_collar()
    {
        var all = SpeakerCheck.Score(Reference, []);
        Assert.Equal(1, all.Rate, 6);
        // Scored time: 30 s less the 0.25 s collars either side of the four boundaries (0, 10, 20, 30): 30 - 0.25 - 0.5 - 0.5 - 0.25.
        Assert.Equal(28.5, all.Scored, 6);
        var oneVoice = SpeakerCheck.Score(Reference, [(0, 30, "X")]); // B's 10 s heard as A's voice: confusion 9.5 s
        Assert.Equal(9.5, oneVoice.Confusion, 6); Assert.Equal(0, oneVoice.Missed); Assert.Equal(0, oneVoice.FalseAlarm);
        var extra = SpeakerCheck.Score(Reference, [.. Reference, (40, 42, "C")]);
        Assert.Equal(2, extra.FalseAlarm, 6);
        var overlap = SpeakerCheck.Score([(0, 10, "A"), (5, 10, "B")], [(0, 10, "1")]); // overlap is scored: B missed for 4.5 s
        Assert.Equal(4.5, overlap.Missed, 2);
    }

    [Fact]
    public void Rttm_lines_are_read_and_equal_error_rate_is_where_the_errors_cross()
    {
        var turns = SpeakerCheck.ReadRttm(["SPEAKER f 1 1.50 2.25 <NA> <NA> spk1 <NA> <NA>", ";; comment", "SPEAKER f 1 4 1 <NA> <NA> spk2 <NA> <NA>"]);
        Assert.Equal([(1.5, 3.75, "spk1"), (4.0, 5.0, "spk2")], turns);
        Assert.Equal(0, SpeakerCheck.EqualErrorRate([(true, 0.9), (true, 0.8), (false, 0.2), (false, 0.1)]));
        Assert.Equal(0.5, SpeakerCheck.EqualErrorRate([(true, 0.9), (true, 0.1), (false, 0.8), (false, 0.2)]));
        Assert.Equal(1, SpeakerCheck.Cosine([1, 0], [2, 0]), 6);
    }
}
