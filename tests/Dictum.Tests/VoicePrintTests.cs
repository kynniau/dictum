using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dictum.Audio;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Voice;
using Dictum.Settings;
using Dictum.Storage;

namespace Dictum.Tests;

/// <summary>
/// The user voice print (design 1.6 C) with the synthetic voices of speaker separation (<see cref="SyntheticVoices"/>, a constant level
/// per voice) and <see cref="LevelEmbedder"/>'s fixed vector per voice: enrolment, its gates, the sealed file, Delete, You on a
/// one-track meeting, and where the print must never go. No model, no real recording.
/// </summary>
public sealed class VoicePrintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dictum-voiceprint-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCredentialStore _credentials = new();
    private readonly AppPaths _paths;

    public VoicePrintTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "local"), Path.Combine(_root, "exe"));
        Directory.CreateDirectory(_paths.LocalDir);
        Directory.CreateDirectory(_paths.ConfigDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private MeetingKeyring Keyring => new(_credentials);
    private string PrintPath => VoicePrintStore.PathFor(_paths);
    private static readonly string[] Script = [.. Enumerable.Range(1, 10).Select(i => $"Invented sentence number {i}.")];

    /// <summary>The owner is voice 1; 2 and 3 are others. Orthogonal, so every cosine is 1 or 0.</summary>
    private static LevelEmbedder Embedder() => new(SyntheticVoices.Distinct());

    /// <summary>One clip of <paramref name="seconds"/> of voice <paramref name="voice"/>, with half a second of silence after it.</summary>
    private static float[] Clip(int voice, double seconds) => SyntheticVoices.Track((voice, seconds)).Samples;

    /// <summary>A "Record my voice now" folder with these clips as the calibration screen writes them (clip-01.wav …).</summary>
    private string Recorded(params float[][] clips)
    {
        var folder = VoiceEnrolment.NewTemporaryFolder(_paths);
        for (var i = 0; i < clips.Length; i++) File.WriteAllBytes(Path.Combine(folder, $"clip-{i + 1:00}.wav"), Wav.Encode(clips[i]));
        return folder;
    }

    private EnrolmentResult Enrol(IVoiceEmbedder embedder, params float[][] clips) =>
        VoiceEnrolment.FromTemporaryFolder(_paths, Recorded(clips), Script, embedder, Keyring, new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(11)));

    private string[] TemporaryFolders() => Directory.GetDirectories(_paths.LocalDir, "voice-enrolment-*");

    [Fact]
    public void Enrolment_stores_a_sealed_print_and_no_audio()
    {
        var result = Enrol(Embedder(), Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6));

        Assert.Equal(EnrolmentOutcome.Made, result.Outcome);
        Assert.Empty(TemporaryFolders()); // the recording is gone the moment the print is built
        Assert.Empty(Directory.GetFiles(_paths.LocalDir, "*.wav", SearchOption.AllDirectories)); // no audio kept anywhere
        Assert.Equal([PrintPath], Directory.GetFiles(_paths.LocalDir, "*", SearchOption.AllDirectories));
        var bytes = File.ReadAllBytes(PrintPath);
        Assert.Equal("DCTMENC\u0001"u8.ToArray(), bytes[..8]); // MeetingCrypto's sealed format
        Assert.DoesNotContain("embedding", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        // Sealed under the meetings owner key with the identity voiceprint/v1; the plaintext shape is pinned.
        var key = Keyring.OwnerKey();
        using var plain = MeetingCrypto.OpenDecrypted(PrintPath, key, "voiceprint/v1");
        using var json = JsonDocument.Parse(plain);
        var root = json.RootElement;
        Assert.Equal(["version", "model", "dimension", "embedding", "windows", "seconds", "enrolledAt", "selfSimilarity"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx@357a834f", root.GetProperty("model").GetString());
        Assert.Equal(4, root.GetProperty("dimension").GetInt32());
        Assert.Equal([1f, 0f, 0f, 0f], root.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()));
        // The pause segmenter (as --speaker-check and imports cut) keeps the half second of quiet that ends each clip: 6.5 s parts,
        // each 5 windows of 2 s at a 1 s hop plus its tail window.
        Assert.Equal(24, root.GetProperty("windows").GetInt32());
        Assert.Equal(26.0, root.GetProperty("seconds").GetDouble());
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?[+-]\d{2}:\d{2}$"), root.GetProperty("enrolledAt").GetString());
        Assert.Equal(["mean", "p20"], root.GetProperty("selfSimilarity").EnumerateObject().Select(p => p.Name));
        Assert.Equal(1.0, root.GetProperty("selfSimilarity").GetProperty("mean").GetDouble());

        var (print, state) = VoicePrintStore.Read(PrintPath, Keyring, VoicePrintStore.CurrentModel);
        Assert.Equal(VoicePrintState.Ready, state);
        Assert.Equal(26.0, print!.Seconds);
    }

    [Fact]
    public void The_temporary_recording_is_deleted_whatever_the_outcome()
    {
        Assert.Equal(EnrolmentOutcome.TooLittleSpeech, Enrol(Embedder(), Clip(1, 5)).Outcome);
        Assert.Empty(TemporaryFolders());
        Assert.Equal(EnrolmentOutcome.ModelMissing, Enrol(null!, Clip(1, 6)).Outcome);
        Assert.Empty(TemporaryFolders());
        Assert.Throws<InvalidOperationException>(() => Enrol(new ThrowingEmbedder(), Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6)));
        Assert.Empty(TemporaryFolders());
        Assert.False(File.Exists(PrintPath));

        // A folder an earlier attempt left behind (Dictum closed mid-attempt) goes when the next attempt starts.
        Directory.CreateDirectory(Path.Combine(_paths.LocalDir, "voice-enrolment-left"));
        var next = VoiceEnrolment.NewTemporaryFolder(_paths);
        Assert.Equal([next], TemporaryFolders());
    }

    [Fact]
    public void Too_little_speech_refuses_enrolment()
    {
        var result = Enrol(Embedder(), Clip(1, 6), Clip(1, 6), Clip(1, 6)); // 18 s of voice; the parts the segmenter cuts are 6.5 s each
        Assert.Equal(EnrolmentOutcome.TooLittleSpeech, result.Outcome);
        Assert.Equal(19.5, result.Seconds);
        Assert.Equal(VoiceText.APPROVED_TooShort, result.Refusal);
        Assert.False(File.Exists(PrintPath));
    }

    [Fact]
    public void Noisy_recording_refuses_enrolment()
    {
        // Every window a different direction: none is within cosine distance 0.5 of the mean.
        var result = Enrol(new ScatterEmbedder(8), Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6));
        Assert.Equal(EnrolmentOutcome.Inconsistent, result.Outcome);
        Assert.True(result.ConsistentShare < VoicePrintBuilder.ConsistentShare);
        Assert.Equal(VoiceText.APPROVED_Inconsistent, result.Refusal);
        Assert.False(File.Exists(PrintPath));
    }

    [Fact]
    public void Delete_removes_the_print_and_turns_both_uses_off()
    {
        Assert.Equal(EnrolmentOutcome.Made, Enrol(Embedder(), Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6)).Outcome);
        File.WriteAllText(_paths.SettingsFile, """{ "audio": { "your_voice_only": true }, "meetings": { "use_voice_print": true } }""");

        Assert.True(VoiceEnrolment.Delete(_paths, keys => keys.All(k => SettingsFile.SetValue(_paths.SettingsFile, k.Key, k.Value))));

        Assert.False(File.Exists(PrintPath));
        var settings = SettingsLoader.LoadFile(_paths.SettingsFile);
        Assert.False(settings.Settings.Audio.YourVoiceOnly);
        Assert.False(settings.Meetings.UseVoicePrint);
        Assert.Equal((null, VoicePrintState.Absent), VoicePrintStore.Read(PrintPath, Keyring, VoicePrintStore.CurrentModel));
    }

    [Fact]
    public void A_delete_whose_settings_cannot_be_written_still_deletes_the_print()
    {
        Assert.Equal(EnrolmentOutcome.Made, Enrol(Embedder(), Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6)).Outcome);
        Assert.False(VoiceEnrolment.Delete(_paths, _ => false)); // the failed save is reported
        Assert.False(File.Exists(PrintPath)); // but privacy favours deletion: the print is gone
    }

    [Fact]
    public void A_leftover_folder_that_is_a_junction_is_removed_as_a_link_and_its_target_kept()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "junctions are a Windows feature");
        var target = Path.Combine(_paths.LocalDir, "elsewhere"); Directory.CreateDirectory(target);
        var kept = Path.Combine(target, "keep.txt"); File.WriteAllText(kept, "mine");
        var link = Path.Combine(_paths.LocalDir, "voice-enrolment-link");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));

        VoiceEnrolment.DeleteLeftovers(_paths);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Mixed_segments_scored_as_the_owner_become_You_and_are_not_clustered()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((2, 2), (1, 3), (3, 2), (1, 2), (2, 2));
        var id = MixedMeeting(files, store, samples, turns);
        var embedder = Embedder();
        using var separator = new SpeakerSeparator(store, embedder, "synthetic@00000000") { OwnerPrint = [1, 0, 0, 0], OwnerThreshold = 0.55 };

        var result = await separator.SeparateAsync(id, MeetingSpeakers.DefaultThreshold, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal([new OwnerTurn(turns[1].Start, turns[1].End), new OwnerTurn(turns[3].Start, turns[3].End)], result.You!);
        Assert.Equal([1, 2, 1], result.Turns.Select(t => t.Speaker)); // voices 2 and 3 are Other 1 and Other 2: You is not numbered
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result.Turns, t => result.You!.Any(y => t.Start < y.End && y.Start < t.End));
        var transcript = store.ReadTranscript(id);
        Assert.Equal(["Other 1", "You", "Other 2", "You", "Other 1"], transcript.Select(s => store.ReadSpeakers(id)!.LabelOf(s)));
        // Display, export and the summary chunks write You exactly as for a You channel; citations keep the channel.
        Assert.Contains("] You: Synthetic words 2", store.ExportMarkdown(id), StringComparison.Ordinal);
        var chunk = Assert.Single(TranscriptChunker.Chunk(transcript, 4000, 0, includeTimestamps: true, store.ReadSpeakers(id)));
        Assert.Contains("[00:02 Mixed] You: Synthetic words 2", chunk, StringComparison.Ordinal);
        Assert.Equal("00:00:02 · You: Synthetic words 2", Dictum.UI.Views.Meetings.MeetingsWindow.FormatSegment(transcript[1], store.ReadSpeakers(id)));
    }

    [Theory]
    [InlineData(0.55, true)]
    [InlineData(0.7, false)]
    public async Task The_voice_threshold_decides_which_segments_are_You(double threshold, bool you)
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 3), (2, 3));
        var id = MixedMeeting(files, store, samples, turns);
        // The owner's windows score 0.6 against the print: You at 0.55, not at 0.7.
        var voices = new Dictionary<int, float[]> { [1] = [0.6f, 0.8f, 0], [2] = [0, 0, 1] };
        using var separator = new SpeakerSeparator(store, new LevelEmbedder(voices), "synthetic@00000000") { OwnerPrint = [1, 0, 0], OwnerThreshold = threshold };

        var result = await separator.SeparateAsync(id, MeetingSpeakers.DefaultThreshold, null, TestContext.Current.CancellationToken);

        Assert.Equal(you, result!.HasYou);
        Assert.Equal(you ? 1 : 2, result.Count);
    }

    [Fact]
    public async Task Separating_again_without_the_print_drops_You()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (samples, turns) = SyntheticVoices.Track((1, 3), (2, 3));
        var id = MixedMeeting(files, store, samples, turns);
        using (var with = new SpeakerSeparator(store, Embedder(), "synthetic@00000000") { OwnerPrint = [1, 0, 0, 0] })
            Assert.True((await with.SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken))!.HasYou);
        using var without = new SpeakerSeparator(store, Embedder(), "synthetic@00000000"); // the switch is off: no print handed over
        var again = await without.SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);
        Assert.Null(again!.You);
        Assert.Equal(2, again.Count);
    }

    [Fact]
    public async Task Two_track_meetings_ignore_the_print()
    {
        using var files = new MeetingFixture(); var store = files.Store();
        var (others, turns) = SyntheticVoices.Track((1, 3), (2, 3));
        var id = store.Create(files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(others))) store.WriteAudio(id, Channel.Others, audio);
        using (var audio = new MemoryStream(SyntheticVoices.Wav(SyntheticVoices.Track((1, 3)).Samples))) store.WriteAudio(id, Channel.You, audio);
        store.AppendSegments(id, [.. turns.Select((t, i) => new Segment(TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), Channel.Others, $"Synthetic words {i + 1}"))]);
        using var separator = new SpeakerSeparator(store, Embedder(), "synthetic@00000000") { OwnerPrint = [1, 0, 0, 0] };

        var result = await separator.SeparateAsync(id, 0.5, null, TestContext.Current.CancellationToken);

        Assert.Null(result!.You); // the microphone track is already You; the Others track is clustered as before
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Model_mismatch_treats_the_print_as_absent()
    {
        var other = new VoicePrint(1, "other-model.onnx@00000000", 4, [1, 0, 0, 0], 20, 24, DateTimeOffset.Now, new(1, 1));
        VoicePrintStore.Write(PrintPath, other, Keyring);
        Assert.Equal((null, VoicePrintState.OtherModel), VoicePrintStore.Read(PrintPath, Keyring, VoicePrintStore.CurrentModel));

        // A later version, or content that is not a print, reads as absent once it has authenticated; a damaged file stays loud.
        Seal("""{"version":2,"model":"x@00000000","dimension":1,"embedding":[1],"windows":1,"seconds":1,"enrolledAt":"2026-11-08T09:30:00+11:00","selfSimilarity":{"mean":1,"p20":1}}""");
        Assert.Equal((null, VoicePrintState.Absent), VoicePrintStore.Read(PrintPath, Keyring, VoicePrintStore.CurrentModel));
        var bytes = File.ReadAllBytes(PrintPath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(PrintPath, bytes);
        Assert.ThrowsAny<CryptographicException>(() => VoicePrintStore.Read(PrintPath, Keyring, VoicePrintStore.CurrentModel));
    }

    [Fact]
    public void Print_is_never_a_bundle_entry()
    {
        Assert.False(BundleFormat.Allowed(Path.GetFileName(PrintPath)));
        Assert.True(BundleFormat.Allowed("speakers.json.enc")); // the check really reads the allowed names
    }

    [Fact]
    public void Remove_Dictum_deletes_the_print()
    {
        Assert.Equal(EnrolmentOutcome.Made, Enrol(Embedder(), Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6)).Outcome);

        var plan = RemovalPlan.Build(_paths, new AppSettings(), RemovalScope.Everything);
        Assert.Contains(plan.Items, i => i.Path == PrintPath && i.Category == DataCategory.VoicePrint);
        Assert.Empty(plan.Execute().Failures);
        Assert.False(File.Exists(PrintPath));
    }

    /// <summary>
    /// Fix round F1: a "Record my voice now" folder that a quit or a crash left behind (a recorded clip in it, and a folder inside
    /// that) is listed under the print's category and gone after Remove Dictum completely, which before removed the data folder only
    /// when empty and never listed the folder.
    /// </summary>
    [Fact]
    public void Remove_Dictum_deletes_a_recording_folder_left_mid_attempt()
    {
        var left = Path.Combine(_paths.LocalDir, "voice-enrolment-0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(Path.Combine(left, "inner"));
        File.WriteAllBytes(Path.Combine(left, "clip-01.wav"), Wav.Encode(Clip(1, 2)));
        File.WriteAllText(Path.Combine(left, "inner", "note.txt"), "x");
        var unrelated = Path.Combine(_paths.LocalDir, "not-dictum");
        Directory.CreateDirectory(unrelated);

        var plan = RemovalPlan.Build(_paths, new AppSettings(), RemovalScope.Everything);
        Assert.Contains(plan.Items, i => i.Path == left && i.IsFolder && i.Category == DataCategory.VoicePrint);
        Assert.Contains(plan.Items, i => i.Path == Path.Combine(left, "clip-01.wav") && !i.IsFolder && i.Category == DataCategory.VoicePrint);
        Assert.Empty(plan.Execute().Failures);

        Assert.False(Directory.Exists(left));
        Assert.Empty(TemporaryFolders());
        Assert.True(Directory.Exists(unrelated)); // a folder Dictum does not own stays, and so does the data folder holding it
    }

    [Fact]
    public void You_is_a_valid_owner_only_when_the_meeting_has_You_turns()
    {
        // No "you" spoken anywhere, so the spoken-owner rule (unchanged) cannot admit it on its own.
        Segment[] transcript = [new(TimeSpan.Zero, TimeSpan.FromSeconds(3), Channel.Mixed, "Synthetic opening words"),
            new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), Channel.Mixed, "Synthetic reply words")];
        var separated = new MeetingSpeakers(1, "synthetic@00000000", 0.5, [new SpeakerTurn(0, 3, 1)], new Dictionary<string, string>());
        const string reply = "# Summary\n- The plan was agreed.\n## Action items\n- You: send the agenda.";
        var template = SummaryTemplates.Get("general");

        Assert.Equal("An action-item owner was not present in the transcript.", SummaryValidator.Validate(reply, transcript, template, "Local", separated).Rejection);
        Assert.Equal("An action-item owner was not present in the transcript.", SummaryValidator.Validate(reply, transcript, template, "Local").Rejection);
        var withYou = separated with { You = [new OwnerTurn(4, 7)] };
        var (summary, rejection) = SummaryValidator.Validate(reply, transcript, template, "Local", withYou);
        Assert.Null(rejection);
        Assert.Equal("You", Assert.Single(summary!.Items).Owner);
    }

    [Fact]
    public void Speakers_files_without_You_turns_are_unchanged_and_with_them_round_trip()
    {
        var plain = new MeetingSpeakers(1, "synthetic@00000000", 0.5, [new SpeakerTurn(0, 3, 1)], new Dictionary<string, string>());
        Assert.DoesNotContain("\"you\"", JsonSerializer.Serialize(plain), StringComparison.Ordinal); // absent = none; version stays 1
        var withYou = plain with { You = [new OwnerTurn(4, 7)] };
        var back = JsonSerializer.Deserialize<MeetingSpeakers>(JsonSerializer.Serialize(withYou))!.Validated();
        Assert.Equal(1, back.Version);
        Assert.Equal([new OwnerTurn(4, 7)], back.You!);
        Assert.Throws<InvalidDataException>(() => (plain with { You = [new OwnerTurn(5, 4)] }).Validated());
    }

    [Theory]
    [InlineData("""{ "voice": { "threshold": 0.95 } }""", 0.9, "voice.threshold 0.95 is outside 0.3–0.9: using 0.9.")]
    [InlineData("""{ "voice": { "threshold": 0.1 } }""", 0.3, "voice.threshold 0.1 is outside 0.3–0.9: using 0.3.")]
    [InlineData("""{ "voice": { "threshold": "high" } }""", 0.55, "Setting \"voice.threshold\" has invalid value \"high\": expected a number from 0.3 to 0.9. Using default 0.55.")]
    [InlineData("""{ "voice": { "threshold": 0.7 } }""", 0.7, null)]
    public void The_voice_threshold_is_read_clamped_and_warned(string json, double expected, string? warning)
    {
        var result = SettingsLoader.Parse(json);
        Assert.Equal(expected, result.Settings.Voice.Threshold);
        if (warning is null) Assert.Empty(result.Warnings);
        else Assert.Equal([warning], result.Warnings);
    }

    [Fact]
    public void The_uses_default_to_meetings_on_and_dictation_off()
    {
        var defaults = SettingsLoader.Parse("{}");
        Assert.True(defaults.Meetings.UseVoicePrint);
        Assert.False(defaults.Settings.Audio.YourVoiceOnly);
        Assert.Equal(0.55, defaults.Settings.Voice.Threshold);
        var set = SettingsLoader.Parse("""{ "audio": { "your_voice_only": true }, "meetings": { "use_voice_print": false } }""");
        Assert.True(set.Settings.Audio.YourVoiceOnly);
        Assert.False(set.Meetings.UseVoicePrint);
        Assert.Empty(set.Warnings);
    }

    [Fact]
    public void Speaker_check_print_mode_reports_the_gates_the_EER_and_the_threshold_at_it()
    {
        var parsed = Dictum.Cli.CliOptions.Parse(["--speaker-check", "--print", "enrol", "own", "other"]);
        Assert.Null(parsed.Error);
        Assert.Equal(Dictum.Cli.CliMode.SpeakerCheck, parsed.Options!.Mode);
        Assert.Equal(["enrol", "own", "other"], parsed.Options.PrintFolders!);
        Assert.NotNull(Dictum.Cli.CliOptions.Parse(["--speaker-check", "--print", "enrol", "own"]).Error);
        Assert.Null(Dictum.Cli.CliOptions.Parse(["--speaker-check", "folder"]).Options!.PrintFolders); // the B harness as before

        string Folder(string name, params float[][] clips)
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            for (var i = 0; i < clips.Length; i++) File.WriteAllBytes(Path.Combine(folder, $"{name}-{i}.wav"), SyntheticVoices.Wav(clips[i]));
            return folder;
        }
        // Voice 1 is the owner; voice 2 sits at cosine 0.45 to it, voice 3 at 0 (SyntheticVoices.TwoSimilar).
        var output = new StringWriter();
        var exit = SpeakerCheck.RunPrint(Folder("enrol", Clip(1, 6), Clip(1, 6), Clip(1, 6), Clip(1, 6)), Folder("own", Clip(1, 3), Clip(1, 4)),
            Folder("other", Clip(2, 3), Clip(3, 4)), new LevelEmbedder(SyntheticVoices.TwoSimilar()), "synthetic@00000000", output);

        Assert.Equal(0, exit);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Enrolment: Made; 26.0 s of speech in 24 windows (gate 20 s); 100.0 % of windows within cosine distance 0.50 of the mean (gate 80 %).", lines[0]);
        Assert.StartsWith("EER over 14 windows (7 own, 7 other): 0.00 % at threshold 1.000", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Scoring time: ", lines[2], StringComparison.Ordinal);
    }

    private void Seal(string json)
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        MeetingCrypto.Encrypt(input, PrintPath, Keyring.OwnerKey(), "voiceprint/v1");
    }

    private static MeetingId MixedMeeting(MeetingFixture files, FileMeetingStore store, float[] samples, (int Voice, double Start, double End)[] turns)
    {
        var id = store.Create(files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(samples))) store.WriteAudio(id, Channel.Mixed, audio);
        store.AppendSegments(id, [.. turns.Select((t, i) => new Segment(TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), Channel.Mixed, $"Synthetic words {i + 1}"))]);
        return id;
    }

    /// <summary>Fails on the first window, as a model that cannot run would.</summary>
    private sealed class ThrowingEmbedder : IVoiceEmbedder
    {
        public float[]? Embed(float[] samples16kMono) => throw new InvalidOperationException("synthetic failure");
    }

    /// <summary>Each window a different unit direction in turn, so the windows do not agree with each other.</summary>
    private sealed class ScatterEmbedder(int dimension) : IVoiceEmbedder
    {
        private int _next;
        public float[]? Embed(float[] samples16kMono)
        {
            var embedding = new float[dimension];
            embedding[_next++ % dimension] = 1;
            return embedding;
        }
    }
}
