using Dictum.Audio;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>The voice activity gate (SPEC §3, 04/11/2026) in front of the speech step, with a stand-in detector.</summary>
public sealed class VoiceGateTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-vad-");

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>A detector that answers as told and counts its calls; <paramref name="fails"/> makes it throw.</summary>
    private sealed class FakeGate(bool speech, bool fails = false) : IVoiceGate
    {
        public int Calls { get; private set; }

        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            Calls++;
            if (fails) throw new InvalidOperationException("detector broke");
            return Task.FromResult(new VoiceVerdict(speech, speech ? "the voice activity detector found speech: 1 segment, 1.0 s of 3.0 s" : "the voice activity detector found no speech in 3.0 s"));
        }
    }

    /// <summary>A transcriber that writes text whatever it hears, as whisper does on silence and noise, and keeps what it heard.</summary>
    private sealed class HallucinatingTranscriber(string text) : ITranscriber
    {
        public List<float[]> Heard { get; } = [];

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
        {
            Heard.Add(samples);
            return Task.FromResult(text);
        }
    }

    private static DictationPipeline Make(ITranscriber transcriber, IVoiceGate? gate, List<string> inserted, ILog? log = null) =>
        new(_ => Task.FromResult(transcriber), () => TranscriptCleaner.RulesOnly("off"),
            (text, _) =>
            {
                inserted.Add(text);
                return Task.CompletedTask;
            },
            () => new AppSettings(), log ?? NullLog.Instance, voiceGate: () => gate);

    /// <summary>Loud enough for the level rule (peak 0.2 against silence_threshold 0.01): a cough, a keyboard, a fan.</summary>
    private static CapturedAudio Loud() => new(new float[48000], 0.2f);

    /// <summary>Below silence_threshold: a quiet speaker the level rule throws away.</summary>
    private static CapturedAudio Quiet() => new(new float[48000], 0.005f);

    [Fact]
    public async Task A_take_the_detector_finds_no_speech_in_types_nothing_and_never_reaches_whisper()
    {
        var whisper = new HallucinatingTranscriber("Thank you for watching.");
        var gate = new FakeGate(speech: false);
        var inserted = new List<string>();
        var log = new RecordingLog();
        var outcome = await Make(whisper, gate, inserted, log).RunAsync(Loud(), CancellationToken.None, _ => { });
        Assert.Equal(ProcessingOutcome.Discarded, outcome);
        Assert.Equal(1, gate.Calls);
        Assert.Empty(whisper.Heard);
        Assert.Empty(inserted);
        // The log names the decision and why, in words, and holds no audio and no transcript.
        Assert.Contains("Recording discarded: no speech (the voice activity detector found no speech in 3.0 s).", log.All);
        Assert.DoesNotContain("Thank you", log.All);
    }

    [Fact]
    public async Task Speech_goes_to_whisper_whole_and_its_text_is_typed_unchanged()
    {
        var whisper = new HallucinatingTranscriber("Send the report to Kynni by Friday.");
        var inserted = new List<string>();
        var log = new RecordingLog();
        var audio = Quiet(); // the level rule would discard it: the detector hears speech, so it is transcribed
        var outcome = await Make(whisper, new FakeGate(speech: true), inserted, log).RunAsync(audio, CancellationToken.None, _ => { });
        Assert.Equal(ProcessingOutcome.Inserted, outcome);
        Assert.Same(audio.Samples, Assert.Single(whisper.Heard)); // the same samples, not a trimmed copy
        Assert.Equal(["Send the report to Kynni by Friday."], inserted);
        Assert.Contains("Recording: the voice activity detector found speech: 1 segment, 1.0 s of 3.0 s; transcribing.", log.All);
    }

    [Fact]
    public async Task Without_a_detector_the_level_rule_decides_as_before()
    {
        var whisper = new HallucinatingTranscriber("Thank you.");
        var inserted = new List<string>();
        var log = new RecordingLog();
        var pipeline = Make(whisper, gate: null, inserted, log);
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(Quiet(), CancellationToken.None, _ => { }));
        Assert.Contains("Recording discarded: silent (peak level 0.0050 < 0.01).", log.All);
        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(Loud(), CancellationToken.None, _ => { }));
        Assert.Single(whisper.Heard);
        Assert.Equal(["Thank you."], inserted);
    }

    [Fact]
    public async Task The_length_rule_still_applies_with_a_detector()
    {
        var gate = new FakeGate(speech: true);
        var inserted = new List<string>();
        var outcome = await Make(new HallucinatingTranscriber("x"), gate, inserted).RunAsync(new CapturedAudio(new float[3200], 0.2f), CancellationToken.None, _ => { });
        Assert.Equal(ProcessingOutcome.Discarded, outcome);
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public async Task A_detector_that_fails_leaves_the_decision_to_the_level()
    {
        var whisper = new HallucinatingTranscriber("Hello.");
        var inserted = new List<string>();
        var log = new RecordingLog();
        var pipeline = Make(whisper, new FakeGate(speech: false, fails: true), inserted, log);
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(Quiet(), CancellationToken.None, _ => { }));
        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(Loud(), CancellationToken.None, _ => { }));
        Assert.Equal(["Hello."], inserted);
        Assert.Contains("the voice activity detector failed (InvalidOperationException: detector broke); the level decides instead", log.All);
    }

    /// <summary>A detector that answers each call in turn from <paramref name="answers"/> (true: speech).</summary>
    private sealed class ScriptedGate(params bool[] answers) : IVoiceGate
    {
        public int Calls { get; private set; }

        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            var speech = answers[Calls++];
            return Task.FromResult(new VoiceVerdict(speech, speech ? "the voice activity detector found speech: 1 segment, 1.0 s of 3.0 s" : "the voice activity detector found no speech in 3.0 s"));
        }
    }

    [Fact]
    public async Task Each_hands_free_part_is_gated_on_its_own()
    {
        var whisper = new HallucinatingTranscriber("Send it today.");
        var inserted = new List<string>();
        var log = new RecordingLog();
        var gate = new ScriptedGate(false, true, false); // silent, speech, silent
        var first = new HandsFreePart(1, null);
        var second = new HandsFreePart(2, first);
        var third = new HandsFreePart(3, second);
        var (one, two, three) = (Loud(), Loud(), Loud());
        var pipeline = Make(whisper, gate, inserted, log);
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(one, CancellationToken.None, _ => { }, part: first));
        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(two, CancellationToken.None, _ => { }, part: second));
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(three, CancellationToken.None, _ => { }, part: third));
        Assert.Equal(3, gate.Calls);
        Assert.Same(two.Samples, Assert.Single(whisper.Heard)); // whisper heard part 2 only
        Assert.Equal(["Send it today."], inserted);
        Assert.Contains("Segment 1 discarded: no speech (the voice activity detector found no speech in 3.0 s).", log.All);
        Assert.Contains("Segment 2: the voice activity detector found speech: 1 segment, 1.0 s of 3.0 s; transcribing.", log.All);
        Assert.Contains("Segment 3 discarded: no speech (the voice activity detector found no speech in 3.0 s).", log.All);
    }

    /// <summary>A detector that honours the token, as the real one does (DetectSpeechAsync takes it).</summary>
    private sealed class CancellingGate : IVoiceGate
    {
        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VoiceVerdict(true, "speech"));
        }
    }

    [Fact]
    public async Task Esc_during_the_detector_check_cancels_the_take_and_whisper_never_hears_it()
    {
        var whisper = new HallucinatingTranscriber("Thank you.");
        var inserted = new List<string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Make(whisper, new CancellingGate(), inserted).RunAsync(Loud(), cts.Token, _ => { }));
        Assert.Empty(whisper.Heard); // a cancellation is not a detector failure: the level never takes over
        Assert.Empty(inserted);
    }

    [Fact]
    public async Task A_check_queued_behind_the_free_never_runs_on_the_freed_detector()
    {
        var use = new SerialUse("detector");
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = use.RunAsync(async () =>
        {
            inside.SetResult();
            await release.Task;
            return 1;
        }, CancellationToken.None);
        await inside.Task;
        var secondRan = false;
        var second = use.RunAsync(() =>
        {
            secondRan = true;
            return Task.FromResult(2);
        }, CancellationToken.None); // queued behind the first
        var freed = false;
        var free = Task.Run(() => use.Free(() => freed = true)); // waits for the first, as Dispose does
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(freed); // never under the call in progress
        release.SetResult();
        Assert.Equal(1, await first);
        await free;
        Assert.True(freed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second);
        Assert.False(secondRan);
    }

    [Fact]
    public async Task Settings_reload_never_waits_behind_a_load_and_a_load_for_an_old_file_is_dropped()
    {
        var file = Path.Combine(_dir.FullName, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[885_098]);
        var inCheck = new ManualResetEventSlim();
        var go = new ManualResetEventSlim();
        var log = new RecordingLog();
        // The readiness check stands in for the load's wait on NativeLoadGate: it holds Get here until the reload has happened.
        using var gates = new VoiceGates(log, integrity: null, runtimeReady: () =>
        {
            inCheck.Set();
            go.Wait(TimeSpan.FromSeconds(10));
            return false;
        });
        gates.Configure(file);
        var get = Task.Run(gates.Get);
        Assert.True(inCheck.Wait(TimeSpan.FromSeconds(10)));
        var reload = Task.Run(() => gates.Configure(null, "vad is off in settings.json")); // the UI thread's settings reload
        Assert.True(reload.Wait(TimeSpan.FromSeconds(5)), "Configure waited behind Get");
        go.Set();
        Assert.Null(await get);
        Assert.DoesNotContain("waiting for the speech model", log.All); // what it found was for a file no longer in use
    }

    [Fact]
    public async Task See_how_it_works_shows_a_refused_take_as_no_speech_recognised()
    {
        var reports = new List<PipelineProgress>();
        await Make(new HallucinatingTranscriber("x"), new FakeGate(speech: false), []).DemonstrateAsync(Loud(), CancellationToken.None, _ => { }, reports.Add);
        Assert.Equal(DictationPipeline.NoSpeech, Assert.Single(reports).Reason); // no new words on the page: the reason it already shows
    }

    [Fact]
    public void Verdict_is_speech_only_with_a_segment()
    {
        Assert.False(SileroVoiceGate.Decide([], TimeSpan.FromSeconds(5)).Speech);
        var verdict = SileroVoiceGate.Decide([(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5)), (TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3.5))], TimeSpan.FromSeconds(5));
        Assert.True(verdict.Speech);
        Assert.Equal("the voice activity detector found speech: 2 segments, 2.0 s of 5.0 s", verdict.Why);
    }

    [Fact]
    public void The_model_is_pinned_by_commit_size_and_hash_and_checked_like_every_model()
    {
        var spec = SileroVoiceGate.Download;
        Assert.Equal("ggml-silero-v6.2.0.bin", spec.FileName);
        Assert.Equal("huggingface.co", spec.Uri.Host);
        Assert.Contains("/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/", spec.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(885_098, spec.Bytes);
        Assert.Equal("2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987", spec.Sha256);
        Assert.Same(spec, ModelIntegrity.Expected(Path.Combine(_dir.FullName, "ggml-silero-v6.2.0.bin")));
    }

    [Fact]
    public void The_gate_is_off_without_the_model_file_or_when_switched_off()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        var settings = new AppSettings();
        Assert.Null(VoiceGates.Locate(true, paths, settings));
        Directory.CreateDirectory(paths.DefaultModelsDir);
        var file = Path.Combine(paths.DefaultModelsDir, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[100]); // the wrong size is passed over, as for every model
        Assert.Null(VoiceGates.Locate(true, paths, settings));
        File.WriteAllBytes(file, new byte[885_098]);
        Assert.Equal(file, VoiceGates.Locate(true, paths, settings));
        Assert.Null(VoiceGates.Locate(false, paths, settings));

        var log = new RecordingLog();
        using var gates = new VoiceGates(log);
        gates.Configure(null, "no ggml-silero-v6.2.0.bin in the model folders");
        Assert.Null(gates.Get());
        Assert.Contains("Voice activity gate: off (no ggml-silero-v6.2.0.bin in the model folders); the level decides silence (silence_threshold).", log.All);
    }

    [Fact]
    public void Vad_is_auto_by_default_off_when_set_and_auto_with_a_warning_for_anything_else()
    {
        Assert.Equal(AppSettings.VadAuto, SettingsLoader.Parse("{}").Settings.Vad);
        Assert.Equal(AppSettings.VadOff, SettingsLoader.Parse("""{ "vad": "OFF" }""").Settings.Vad);
        var odd = SettingsLoader.Parse("""{ "vad": "always" }""");
        Assert.Equal(AppSettings.VadAuto, odd.Settings.Vad);
        Assert.Contains(odd.Warnings, w => w.Contains("vad", StringComparison.Ordinal));

        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        Directory.CreateDirectory(paths.DefaultModelsDir);
        var file = Path.Combine(paths.DefaultModelsDir, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[885_098]);
        var off = new AppSettings { Vad = AppSettings.VadOff };
        Assert.Null(VoiceGates.Locate(VoiceGates.Enabled(off), paths, off)); // the file is there, and off keeps the gate out
        Assert.Equal("vad is off in settings.json", VoiceGates.OffReason(off));
        Assert.Equal(file, VoiceGates.Locate(VoiceGates.Enabled(new AppSettings()), paths, new AppSettings()));
    }

    [Fact]
    public void Deleting_all_app_data_removes_the_voice_activity_model_and_its_partial_download()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        Directory.CreateDirectory(paths.DefaultModelsDir);
        var file = Path.Combine(paths.DefaultModelsDir, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[10]);
        File.WriteAllBytes(file + ".part", new byte[5]);
        File.WriteAllBytes(Path.Combine(paths.DefaultModelsDir, "notes.txt"), new byte[3]); // not Dictum's: never in the plan
        var plan = Dictum.Storage.RemovalPlan.Build(paths, new AppSettings(), Dictum.Storage.RemovalScope.AllData);
        Assert.Contains(plan.Items, i => i.Path == file);
        Assert.Contains(plan.Items, i => i.Path == file + ".part");
        Assert.DoesNotContain(plan.Items, i => i.Path.EndsWith("notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void The_self_test_says_the_gate_is_off_and_why_without_failing()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        var lines = new List<(string Label, string Value, bool? Pass)>();
        var integrity = new ModelIntegrity(paths.VerifiedModelsFile, NullLog.Instance);
        Assert.True(Dictum.Cli.SelfTestCommand.VoiceGateLine(new AppSettings(), paths, integrity, (l, v, p) => lines.Add((l, v, p))));
        Assert.Equal(("voice activity", "off: no ggml-silero-v6.2.0.bin in the model folders; Dictum.exe --download-model silero-v6.2.0 fetches it; silence_threshold decides silence", (bool?)null),
            Assert.Single(lines));
        lines.Clear();
        Assert.True(Dictum.Cli.SelfTestCommand.VoiceGateLine(new AppSettings { Vad = AppSettings.VadOff }, paths, integrity, (l, v, p) => lines.Add((l, v, p))));
        Assert.Equal("off: vad is off in settings.json; silence_threshold decides silence", Assert.Single(lines).Value);
    }

    [Fact]
    public void A_model_that_fails_its_check_is_never_loaded_and_the_level_decides()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        Directory.CreateDirectory(paths.DefaultModelsDir);
        var file = Path.Combine(paths.DefaultModelsDir, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[885_098]); // the right size, the wrong bytes
        var log = new RecordingLog();
        using var gates = new VoiceGates(log, new ModelIntegrity(paths.VerifiedModelsFile, log));
        gates.Configure(file);
        Assert.Null(gates.Get());
        Assert.Null(gates.Get()); // said once, not tried on every take
        Assert.Single(log.Lines, l => l.Message.Contains("could not be loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void The_gate_never_loads_before_a_speech_load_chooses_the_runtime()
    {
        // Whisper.net keeps the first native library it loads: a detector loaded first would choose it, and could put Vulkan back on a
        // PC whose last start the GPU ended (GpuLoadMarker's CPU-only order). Until then the level decides, and the gate is not given up.
        var file = Path.Combine(_dir.FullName, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[885_098]);
        var log = new RecordingLog();
        using var gates = new VoiceGates(log, integrity: null, runtimeReady: () => false);
        gates.Configure(file);
        Assert.Null(gates.Get());
        Assert.Null(gates.Get());
        Assert.Single(log.Lines, l => l.Message.Contains("waiting for the speech model to load the whisper runtime", StringComparison.Ordinal));
        Assert.DoesNotContain("could not be loaded", log.All);
    }
}

/// <summary>The gate's runtime rule against the real default readiness check, with the runtime state it reads replaced.</summary>
[Collection("Process-wide state")] // swaps VoiceGates.Runtime, a static: never beside a class that reads it
public sealed class VoiceGateRuntimeTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-vad-rt-");
    private readonly Func<(bool OrderSet, bool LibraryLoaded)> _real = VoiceGates.Runtime;

    public void Dispose()
    {
        VoiceGates.Runtime = _real;
        _dir.Delete(recursive: true);
    }

    [Fact]
    public void The_order_set_with_no_library_loaded_gives_no_gate()
    {
        // ModelManager.BeginLoad sets the order at once and loads the library later, behind its SHA-256 check: in between the detector
        // would be the first native load, outside GpuLoadMarker's record (lens on e6b7055, 04/11/2026).
        VoiceGates.Runtime = () => (OrderSet: true, LibraryLoaded: false);
        var file = Path.Combine(_dir.FullName, SileroVoiceGate.FileName);
        File.WriteAllBytes(file, new byte[885_098]);
        var log = new RecordingLog();
        using var gates = new VoiceGates(log); // the default readiness check
        gates.Configure(file);
        Assert.Null(gates.Get());
        Assert.Contains("waiting for the speech model to load the whisper runtime", log.All);
        Assert.False(VoiceGates.Ready((OrderSet: true, LibraryLoaded: false)));
        Assert.True(VoiceGates.Ready((OrderSet: true, LibraryLoaded: true)));
    }
}

/// <summary>The real Silero detector through Whisper.net. Skipped unless DICTUM_VAD_MODEL_PATH points at ggml-silero-v6.2.0.bin.</summary>
[Collection("Process-wide state")] // loads native models: never beside another model-loading class
public class VoiceGateIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Silence_and_noise_hold_no_speech_and_a_spoken_sentence_does()
    {
        var modelPath = TestEnv.Get("VAD_MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelPath), "DICTUM_VAD_MODEL_PATH is not set.");
        TestEnv.WindowsOnly("The spoken clip is synthesised with System.Speech.");
        var ct = TestContext.Current.CancellationToken;
        WhisperEngine.ConfigureRuntime("auto", NullLog.Instance); // as a speech load does first
        using var gate = SileroVoiceGate.Load(modelPath!);

        var silence = await gate.CheckAsync(new float[5 * 16000], ct);
        output.WriteLine($"5 s of digital silence: {silence.Why}");
        Assert.False(silence.Speech);

        var random = new Random(4);
        var hiss = Enumerable.Range(0, 5 * 16000).Select(_ => (float)(random.NextDouble() - 0.5) * 0.1f).ToArray(); // RMS about 0.03, above silence_threshold
        var noise = await gate.CheckAsync(hiss, ct);
        output.WriteLine($"5 s of white noise: {noise.Why}");
        Assert.False(noise.Speech);

        var wav = WhisperIntegrationTests.SynthesiseWav(WhisperIntegrationTests.Sentence);
        try
        {
            var spoken = await gate.CheckAsync(AudioFiles.ReadWav(wav).Samples, ct);
            output.WriteLine($"spoken sentence: {spoken.Why}");
            Assert.True(spoken.Speech);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>A take as the capture hands it over: the samples and the loudest 20 ms block's RMS (what silence_threshold is compared with).</summary>
    private static CapturedAudio Take(float[] samples) =>
        new(samples, samples.Chunk(320).Max(block => (float)Math.Sqrt(block.Average(s => (double)s * s))));

    /// <summary>
    /// The before and after of the gate (04/11/2026 lane report): the characters typed for each clip by the pipeline without the gate
    /// (the level decides) and with it, beside what whisper writes with no gate at all. Lengths only. Skipped unless both
    /// DICTUM_VAD_MODEL_PATH and DICTUM_MODEL_PATH are set.
    /// </summary>
    [Fact]
    public async Task With_whisper_the_gate_types_nothing_for_silence_or_noise_and_the_same_text_for_speech()
    {
        var vadPath = TestEnv.Get("VAD_MODEL_PATH");
        var modelPath = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(vadPath) || string.IsNullOrWhiteSpace(modelPath), "DICTUM_VAD_MODEL_PATH and DICTUM_MODEL_PATH are not both set.");
        TestEnv.WindowsOnly("The spoken clip is synthesised with System.Speech.");
        var ct = TestContext.Current.CancellationToken;
        using var engine = WhisperEngine.Load(modelPath!, 0, "auto", NullLog.Instance); // chooses the runtime, as the app's speech load does
        using var gate = SileroVoiceGate.Load(vadPath!);
        output.WriteLine($"{Path.GetFileName(modelPath)} on {WhisperEngine.LoadedRuntime}");

        var random = new Random(4);
        var wav = WhisperIntegrationTests.SynthesiseWav(WhisperIntegrationTests.Sentence);
        float[] spoken;
        try
        {
            spoken = AudioFiles.ReadWav(wav).Samples;
        }
        finally
        {
            File.Delete(wav);
        }
        var clips = new (string Name, float[] Samples)[]
        {
            ("5 s of digital silence", new float[5 * 16000]),
            ("5 s of white noise", [.. Enumerable.Range(0, 5 * 16000).Select(_ => (float)(random.NextDouble() - 0.5) * 0.1f)]),
            ("the spoken sentence", spoken),
        };
        var typed = new Dictionary<string, (string Before, string After)>();
        foreach (var (name, samples) in clips)
        {
            var take = Take(samples);
            var whisperAlone = await engine.TranscribeAsync(samples, ct);
            var before = await TypedAsync(engine, null, take, ct);
            var after = await TypedAsync(engine, gate, take, ct);
            typed[name] = (before, after);
            output.WriteLine($"{name} (peak {take.PeakLevel:0.000}): whisper alone {whisperAlone.Length} chars; typed before {before.Length} chars, after {after.Length} chars");
        }
        Assert.Equal("", typed["5 s of digital silence"].After);
        Assert.Equal("", typed["5 s of white noise"].After);
        Assert.NotEqual("", typed["the spoken sentence"].After);
        Assert.Equal(typed["the spoken sentence"].Before, typed["the spoken sentence"].After);
    }

    private static async Task<string> TypedAsync(ITranscriber engine, IVoiceGate? gate, CapturedAudio take, CancellationToken ct)
    {
        var inserted = new List<string>();
        var pipeline = new DictationPipeline(_ => Task.FromResult(engine), () => TranscriptCleaner.RulesOnly("off"),
            (text, _) =>
            {
                inserted.Add(text);
                return Task.CompletedTask;
            },
            () => new AppSettings(), NullLog.Instance, voiceGate: () => gate);
        await pipeline.RunAsync(take, ct, _ => { });
        return string.Concat(inserted);
    }
}
