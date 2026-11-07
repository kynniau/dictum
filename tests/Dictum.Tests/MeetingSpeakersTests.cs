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
/// separates turns. The turns are the meeting's transcript segments, and <see cref="LevelEmbedder"/> returns a fixed vector per level,
/// so windowing, clustering, mapping, naming, storage and export are deterministic. No model, no real recording.
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

    /// <summary>Replaces the meeting's speakers file with one a later Dictum might write (version 2), sealed with the meeting's own key.</summary>
    public static void WriteNewerSpeakersFile(MeetingFixture files, IMeetingStore store, MeetingId id, string? root = null)
    {
        var key = MeetingCrypto.Unwrap(store.Read(id).Wraps.Single(), files.Keyring.OwnerKey());
        using var json = new MemoryStream("""{"version":2,"model":"later@00000000","threshold":0.5,"turns":[{"start":0,"end":2,"speaker":1}],"names":{"1":"Michelle Example"},"voices":[[1,0]]}"""u8.ToArray());
        MeetingCrypto.Encrypt(json, Path.Combine(root ?? files.Root, id.Value, "speakers.json.enc"), key, $"{id.Value}/speakers.json.enc");
    }
}

/// <summary>A voice per window: the level heard most often in it (silence aside), as a fixed vector. Every window it hears is kept.</summary>
internal sealed class LevelEmbedder(Dictionary<int, float[]> voices) : IVoiceEmbedder
{
    public List<float[]> Heard { get; } = [];
    public float[]? Embed(float[] samples16kMono)
    {
        Heard.Add(samples16kMono);
        if (samples16kMono.Length < 1600) return null;
        var level = samples16kMono.Where(s => s != 0).GroupBy(s => (int)Math.Round(s * 10)).MaxBy(g => g.Count())!.Key;
        return voices[level];
    }
}

/// <summary>Cancels the token once it has embedded its last expected window, so only the clustering step after it can notice.</summary>
internal sealed class CancelAfterEmbedding(IVoiceEmbedder inner, CancellationTokenSource source, int windows = 2) : IVoiceEmbedder
{
    private int _calls;
    public float[]? Embed(float[] samples16kMono)
    {
        var result = inner.Embed(samples16kMono);
        if (++_calls == windows) source.Cancel();
        return result;
    }
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

    private static SpeakerSeparator Separator(IMeetingStore store, Dictionary<int, float[]> voices, LevelEmbedder? embedder = null) =>
        new(store, embedder ?? new LevelEmbedder(voices), "synthetic@00000000");

    [Fact]
    public async Task Three_voices_become_Other_1_to_3_in_order_of_first_appearance()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((2, 2), (3, 1.5), (2, 1.5), (1, 2)); // each at least the 1.5 s window floor
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
        var embedder = new LevelEmbedder(SyntheticVoices.Distinct());

        var result = await Separator(store, SyntheticVoices.Distinct(), embedder).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);

        // The Others track (voice 1 only), never the You track (voices 2 and 3), and only the Others segment's stretch of it.
        Assert.NotEmpty(embedder.Heard);
        Assert.All(embedder.Heard.SelectMany(w => w).Where(s => s != 0), s => Assert.Equal(1, Math.Round(s * 10)));
        Assert.Equal([new SpeakerTurn(Math.Round(otherTurns[0].Start, 3), Math.Round(otherTurns[0].End, 3), 1)], result.Turns);
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
        await new MeetingSummariser().SummariseAsync(id, transcript, SummaryTemplates.Get("general", files.Root), client, "Local", TestContext.Current.CancellationToken, named,
            inProcessEngine: true);
        Assert.Contains(client.Requests, r => r.Contains("Michelle Example: Synthetic first guest", StringComparison.Ordinal));
        // Without inProcessEngine the summariser fails closed: no name is sent (MeetingSpeakerNamesTests covers the routes).
        var unspecified = new CapturingClient();
        await new MeetingSummariser().SummariseAsync(id, transcript, SummaryTemplates.Get("general", files.Root), unspecified, "Ollama", TestContext.Current.CancellationToken, named);
        Assert.DoesNotContain(unspecified.Requests, r => r.Contains("Michelle", StringComparison.Ordinal));
        Assert.Contains(unspecified.Requests, r => r.Contains("Other 1: Synthetic first guest", StringComparison.Ordinal));
    }

    [Fact]
    public void Allowed_speaker_models_are_pinned()
    {
        // One model only (owner decision, 07/11/2026): CAM++. pyannote's segmentation model is not in the catalogue, and never returns.
        var expected = new (string File, long Bytes, string Sha256, string Url)[]
        {
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
            foreach (var excluded in new[] { "reverb", "diarizen", "simam", "voxblink", "redimnet", "wespeaker", "resnet", "pyannote", "segmentation" })
            {
                Assert.DoesNotContain(excluded, spec.FileName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(excluded, spec.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.Equal(["reverb", "diarizen", "simam", "voxblink", "redimnet", "pyannote"], SpeakerModels.Excluded);
        Assert.Null(SpeakerModels.FindByFile("pyannote-segmentation-3.0.onnx")); // nothing downloads, verifies or keeps it
        Assert.Null(ModelIntegrity.Expected(Path.Combine("C:\\any", "pyannote-segmentation-3.0.onnx")));
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
    public void A_merge_exactly_at_the_threshold_is_not_made()
    {
        // Orthogonal voices are exactly 1 apart (1 - cosine 0), and identical ones exactly 0: cutree_cdist merges only below the threshold.
        float[][] apart = [[1, 0], [0, 1]];
        Assert.Equal([0, 1], SpeakerClustering.Cluster(apart, 1.0));
        Assert.Equal([0, 0], SpeakerClustering.Cluster(apart, Math.BitIncrement(1.0)));
        Assert.Equal([0, 1], SpeakerClustering.Cluster([[1, 0], [1, 0]], 0.0));
    }

    [Fact]
    public void Clustering_matches_the_plain_complete_linkage_loop_on_random_voices()
    {
        var random = new Random(20261107);
        for (var round = 0; round < 60; round++)
        {
            var points = Enumerable.Range(0, random.Next(1, 40)).Select(_ => Enumerable.Range(0, 6).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray()).ToArray();
            var threshold = random.NextDouble() * 1.2;
            Assert.Equal(PlainCompleteLinkage(points, threshold), SpeakerClustering.Cluster(points, threshold));
            var count = random.Next(1, points.Length + 1);
            Assert.Equal(PlainCompleteLinkage(points, threshold, count), SpeakerClustering.Cluster(points, threshold, count));
        }
    }

    // The loop SpeakerClustering replaced (07/11/2026, O(n^3) and slow for a meeting's windows): kept here as the reference it must match.
    private static int[] PlainCompleteLinkage(float[][] embeddings, double threshold, int count = 0)
    {
        var n = embeddings.Length;
        var unit = embeddings.Select(v => { var l = Math.Sqrt(v.Sum(x => (double)x * x)); return v.Select(x => (float)(x / l)).ToArray(); }).ToArray();
        var distance = new float[n, n];
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                distance[i, j] = distance[j, i] = (float)Math.Max(0, 1 - unit[i].Zip(unit[j], (a, b) => (double)a * b).Sum());
        var clusters = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        while (clusters.Count > 1 && (count <= 0 || clusters.Count > count))
        {
            var (a, b, height) = (-1, -1, double.PositiveInfinity);
            for (var i = 0; i < clusters.Count; i++)
                for (var j = i + 1; j < clusters.Count; j++)
                {
                    var link = clusters[i].Max(x => clusters[j].Max(y => distance[x, y]));
                    if (link < height) (a, b, height) = (i, j, link);
                }
            if (count <= 0 && height >= threshold) break;
            clusters[a].AddRange(clusters[b]);
            clusters.RemoveAt(b);
        }
        var labels = new int[n];
        var order = clusters.OrderBy(c => c.Min()).ToList();
        for (var label = 0; label < order.Count; label++)
            foreach (var member in order[label]) labels[member] = label;
        return labels;
    }

    [Fact]
    public void Each_segment_is_cut_into_overlapping_windows_and_a_short_one_into_none()
    {
        const int track = 16000 * 60;
        Assert.Empty(SpeakerSeparator.Windows(0, 1.49, track)); // under the 1.5 s floor
        Assert.Equal([(0, 24000)], SpeakerSeparator.Windows(0, 1.5, track)); // a short segment is one window
        Assert.Equal([(16000, 48000)], SpeakerSeparator.Windows(1, 3, track));
        Assert.Equal([(0, 32000), (16000, 48000), (32000, 64000)], SpeakerSeparator.Windows(0, 4, track)); // 2 s, every 1 s
        Assert.Equal([(0, 32000), (8000, 40000)], SpeakerSeparator.Windows(0, 2.5, track)); // the tail window ends at the segment's end
        Assert.Equal([(0, 30000)], SpeakerSeparator.Windows(0, 5, 30000)); // never past the track
        Assert.Empty(SpeakerSeparator.Windows(0, 5, 20000));
        Assert.Equal(
            [(0.0, 2.0), (3.0, 5.0)],
            SpeakerSeparator.Turns([new(TimeSpan.Zero, TimeSpan.FromSeconds(2), Channel.Others, "a"), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), Channel.You, "b"),
                new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), Channel.Mixed, "c"), new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), Channel.Mixed, "typed note")]));
    }

    [Fact]
    public void A_segment_takes_the_majority_speaker_of_its_windows_and_a_tie_goes_to_the_earliest_window()
    {
        // 0-2 s voice 1 alone. 3-7 s: voice 1 for 1.2 s, then voice 2: windows 3-5 (mostly 1), 4-6 and 5-7 (voice 2): voice 2.
        // 8-10.5 s: voice 2 for 1.25 s, then voice 1: windows 8-10 (mostly 2) and 8.5-10.5 (mostly 1), a tie the earlier window decides.
        var samples = new float[16000 * 21 / 2];
        void Fill(double from, double to, int voice) { for (var i = (int)(from * 16000); i < (int)(to * 16000); i++) samples[i] = voice / 10f; }
        Fill(0, 2, 1); Fill(3, 4.2, 1); Fill(4.2, 7, 2); Fill(8, 9.25, 2); Fill(9.25, 10.5, 1);
        var turns = SpeakerSeparator.Diarize(samples, [(0, 2), (3, 7), (8, 10.5)], new LevelEmbedder(SyntheticVoices.Distinct()), 0.5, null,
            TestContext.Current.CancellationToken);
        Assert.Equal([new SpeakerTurn(0, 2, 1), new SpeakerTurn(3, 7, 2), new SpeakerTurn(8, 10.5, 2)], turns);
    }


    [Fact]
    public void A_window_with_a_non_finite_embedding_is_ignored_and_the_others_cluster_as_before()
    {
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2), (1, 2));
        var voices = SyntheticVoices.Distinct();
        voices[2] = [float.NaN, 1, 0, 0]; // the middle segment's only window
        var result = SpeakerSeparator.Diarize(samples, [.. turns.Select(t => (t.Start, t.End))], new LevelEmbedder(voices), 0.5, null,
            TestContext.Current.CancellationToken);
        Assert.Equal([new SpeakerTurn(Math.Round(turns[0].Start, 3), Math.Round(turns[0].End, 3), 1), new SpeakerTurn(Math.Round(turns[2].Start, 3), Math.Round(turns[2].End, 3), 1)], result);
    }

    [Fact]
    public void Clustering_embeddings_that_are_all_non_finite_does_not_throw()
    {
        float[][] nan = [[float.NaN, 1], [float.NaN, 2], [float.NaN, 3]];
        Assert.Equal([0, 1, 2], SpeakerClustering.Cluster(nan, 0.5));
    }

    [Fact]
    public void Clustering_stops_at_once_when_the_token_is_cancelled()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SpeakerClustering.Cluster([[1f, 0], [0, 1f], [1f, 1f]], 0.5, 0, cancel.Token));
    }

    [Fact]
    public async Task Cancelling_before_clustering_writes_no_speakers_file()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var id = Meeting(files, store, others: samples, segments: [.. turns.Select((t, i) => At(t, Channel.Others, $"Synthetic line {i}"))]);
        using var cancel = new CancellationTokenSource();
        var embedder = new CancelAfterEmbedding(new LevelEmbedder(SyntheticVoices.Distinct()), cancel); // cancels once the last window is embedded
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SpeakerSeparator(store, embedder, "synthetic@00000000").SeparateAsync(id, 0.5, null, cancel.Token));
        Assert.Null(store.ReadSpeakers(id));
        Assert.False(store.HasSpeakers(id));
    }

    [Fact]
    public async Task No_surviving_window_writes_no_speakers_file_and_returns_null()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 1.0), (2, 1.2)); // each under the 1.5 s minimum
        var id = Meeting(files, store, others: samples, segments: [.. turns.Select((t, i) => At(t, Channel.Others, $"Synthetic line {i}"))]);
        Assert.Null(await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken));
        Assert.False(store.HasSpeakers(id));
        var none = Meeting(files, store, others: samples); // no transcript at all
        Assert.Null(await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(none, 0.5, null, TestContext.Current.CancellationToken));
        Assert.False(store.HasSpeakers(none));
    }

    [Fact]
    public void More_than_the_cap_of_windows_are_sub_sampled_evenly_and_every_segment_still_gets_a_label()
    {
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var segments = Enumerable.Range(0, SpeakerSeparator.MaxWindows + 101).Select(i => (turns[i % 2].Start, turns[i % 2].End)).ToArray(); // one window each, over the same audio
        var embedder = new LevelEmbedder(SyntheticVoices.Distinct());
        var result = SpeakerSeparator.Diarize(samples, segments, embedder, 0.5, null, TestContext.Current.CancellationToken);
        Assert.Equal(SpeakerSeparator.MaxWindows, embedder.Heard.Count); // the clustering input is capped
        Assert.Equal(segments.Length, result.Length);
        for (var i = 0; i < result.Length; i++) Assert.Equal(i % 2 + 1, result[i].Speaker); // a segment with no kept window takes the nearest kept window's speaker
    }
    [Fact]
    public async Task A_speakers_file_from_a_later_version_reads_as_none_and_separating_again_replaces_it()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2));
        var id = Meeting(files, store, others: samples, segments: [At(turns[0], Channel.Others, "Synthetic first"), At(turns[1], Channel.Others, "Synthetic second")]);
        SyntheticVoices.WriteNewerSpeakersFile(files, store, id);

        Assert.Null(store.ReadSpeakers(id)); // plain channel labels, not an error
        Assert.True(store.HasSpeakers(id)); // still there for Delete to mention
        Assert.Contains("Others: Synthetic first", store.ExportMarkdown(id)); // export works
        var separated = await Separator(store, SyntheticVoices.Distinct()).SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);
        Assert.Equal(separated.Turns, store.ReadSpeakers(id)!.Turns); // identify again works and writes this version's file
        Assert.Contains("Other 2: Synthetic second", store.ExportMarkdown(id));

        // A file that fails authentication is still an error: tampering is loud.
        var path = files.At(id, "speakers.json.enc");
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => store.ReadSpeakers(id));
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
        Assert.Throws<InvalidDataException>(() => (good with { Names = new Dictionary<string, string> { ["1"] = "Ana (HR)" } }).Validated());
    }

    // One name rule (MeetingSpeakers.IsValidName) for save, read and the name box: what the summary validator accepts as an owner.
    [Theory]
    [InlineData("Ana (HR)")]
    [InlineData("Smith, Ana")]
    [InlineData("Ana \"Al\" Lee")]
    [InlineData("Re: Ana")]
    [InlineData("Ana/Lee")]
    [InlineData("Line\nbreak")]
    [InlineData("Other 2")] // shaped like a label: it would read as another speaker
    [InlineData("Other 12")]
    [InlineData(" Other 1 ")]
    public void A_name_the_owner_rule_would_refuse_is_refused_at_save(string name)
    {
        Assert.False(MeetingSpeakers.IsValidName(name));
        var speakers = new MeetingSpeakers(1, "m@00000000", 0.5, [new(0, 1, 1), new(1, 2, 2)], new Dictionary<string, string>());
        Assert.Throws<ArgumentException>(() => speakers.WithNames(new Dictionary<int, string> { [1] = name }));
        Assert.Throws<InvalidDataException>(() => (speakers with { Names = new Dictionary<string, string> { ["1"] = name } }).Validated());
    }

    [Theory]
    [InlineData("Michelle Example")]
    [InlineData("Ana O’Neil-Ng")]
    [InlineData("Dr. A. Sample")]
    [InlineData("R2")]
    [InlineData("  Priya Example  ")]
    [InlineData("Others")] // not "Other n"
    [InlineData("other 2")] // the label is matched exactly (ordinal), as the owner rule now is
    public void A_name_the_owner_rule_accepts_is_saved_trimmed(string name)
    {
        Assert.True(MeetingSpeakers.IsValidName(name));
        var speakers = new MeetingSpeakers(1, "m@00000000", 0.5, [new(0, 1, 1)], new Dictionary<string, string>())
            .WithNames(new Dictionary<int, string> { [1] = name, [2] = "  " }).Validated();
        Assert.Equal(name.Trim(), speakers.Label(1));
        Assert.False(MeetingSpeakers.IsValidName(new string('x', MeetingSpeakers.MaxNameLength + 1)));
        Assert.False(MeetingSpeakers.IsValidName("   "));
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
