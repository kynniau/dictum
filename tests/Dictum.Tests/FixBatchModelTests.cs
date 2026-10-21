using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.App;

namespace Dictum.Tests;

/// <summary>Fix batch, items 8, 9, 10, 16 and 22 (#18, #19, #20, #30, #54, #72): model loads, downloads and first run.</summary>
public sealed class FixBatchModelTests : IDisposable
{
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-fixc-");
    private readonly ScriptedPrompts _prompts = new();
    private readonly ConcurrentQueue<string> _balloons = new();
    private readonly GateHandler _server = new(Payload);
    private readonly HttpClient _client;
    private readonly AppPaths _paths;
    private AppSettings _settings = new();

    public FixBatchModelTests()
    {
        _client = new HttpClient(_server);
        _paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), _dir.FullName);
        Directory.CreateDirectory(_paths.ConfigDir);
        Directory.CreateDirectory(_paths.DefaultModelsDir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a test waits before it reports that a signal never came; order decides these tests, so it is generous.</summary>
    private const int FailAfterMs = 30_000;

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    private static DownloadSpec Spec(string file) =>
        new(new Uri($"https://dictate-test.invalid/{file}"), file, Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)));

    private string Model(string file) => Path.Combine(_paths.DefaultModelsDir, file);

    private ModelCoordinator Make(ModelManager? models = null, LocalLlmManager? llm = null, Action? reload = null) =>
        new(_paths, models ?? new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false),
            llm ?? new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p)), _client, NullLog.Instance, () => _settings, null, _prompts, _ => null,
            reload: reload ?? (() => _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings), shutdown: () => { }, refreshTooltip: () => { },
            balloon: (text, _) => _balloons.Enqueue(text), gpuMachine: () => false);

    [Fact]
    public async Task Overlapping_speech_loads_install_the_last_choice_and_release_the_other()
    {
        var loads = new GatedLoads();
        var models = new ModelManager(NullLog.Instance, loader: loads.LoadSpeech, gpuInUse: () => false);
        var (small, @base) = (Model("ggml-small.en.bin"), Model("ggml-base.en.bin"));
        models.BeginLoad(small, new WhisperSettings());
        Assert.True(await Eventually(() => loads.Started.Contains(small)));
        models.BeginLoad(@base, new WhisperSettings()); // picked again inside the first load's window
        loads.Release(@base); // the later choice is ready first...
        await Task.Delay(100, Ct);
        loads.Release(small); // ...then the earlier one finishes

        Assert.True(await Eventually(() => loads.Speech.Count == 2 && models.IsReady));
        var used = Assert.IsType<FakeSpeechEngine>(await models.GetAsync(Ct));
        Assert.Equal(@base, used.ModelPath);
        Assert.True(await Eventually(() => loads.Speech[small].Disposed), "the engine nobody chose any more stays in memory");
        Assert.False(used.Disposed);
        models.SetDecoding(new DecodeOptions("Kowalski, Parramatta", 0)); // a dictionary edit reaches the engine dictation uses
        Assert.True(await Eventually(() => used.Decode.Prompt is not null), "the dictionary edit went to another engine");
        models.Dispose();
        Assert.True(used.Disposed);
    }

    [Fact]
    public async Task Overlapping_cleanup_loads_leave_Ready_on_the_last_choice()
    {
        var loads = new GatedLoads();
        var llm = new LocalLlmManager(NullLog.Instance, loader: loads.LoadCleanup);
        var (q15, q05) = (Model("qwen2.5-1.5b-instruct-q4_k_m.gguf"), Model("qwen2.5-0.5b-instruct-q4_k_m.gguf"));
        llm.BeginLoad(q15, "auto");
        Assert.True(await Eventually(() => loads.Started.Contains(q15)));
        llm.BeginLoad(q05, "auto");
        loads.Release(q05);
        await Task.Delay(100, Ct);
        loads.Release(q15);

        Assert.True(await Eventually(() => loads.Cleanup.Count == 2 && !llm.IsLoading && llm.Ready is not null));
        Assert.Equal(q05, llm.Ready!.ModelPath);
        Assert.True(await Eventually(() => loads.Cleanup[q15].Disposed), "qwen2.5-1.5b stays in memory after the switch to 0.5b");
    }

    [Fact]
    public async Task Unload_during_a_cleanup_load_leaves_no_model_in_memory()
    {
        var loads = new GatedLoads();
        var llm = new LocalLlmManager(NullLog.Instance, loader: loads.LoadCleanup);
        var q15 = Model("qwen2.5-1.5b-instruct-q4_k_m.gguf");
        llm.BeginLoad(q15, "auto");
        Assert.True(await Eventually(() => loads.Started.Contains(q15)));
        llm.Unload(); // provider switched to none or a cloud provider meanwhile
        loads.Release(q15);

        Assert.True(await Eventually(() => loads.Cleanup.ContainsKey(q15) && loads.Cleanup[q15].Disposed), "the model loaded after Unload stays resident");
        Assert.Null(llm.Ready);
        Assert.Null(llm.ModelPath);
    }

    [Fact]
    public async Task Quit_stops_a_speech_warm_up_and_releases_its_engine()
    {
        FakeSpeechEngine? engine = null;
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => engine = new FakeSpeechEngine(p, d, waitForCancel: true), gpuInUse: () => true);
        models.BeginLoad(Model("ggml-small.en.bin"), new WhisperSettings());
        Assert.True(await Eventually(() => engine is not null, FailAfterMs), "the speech GPU load never began");
        await engine!.TranscribeStarted.WaitAsync(TimeSpan.FromMilliseconds(FailAfterMs), Ct); // the warm-up is running
        Assert.False(engine.TranscribeCancelled.IsCompleted); // nothing but quit can end it
        models.Dispose();
        await engine.TranscribeCancelled.WaitAsync(TimeSpan.FromMilliseconds(FailAfterMs), Ct); // times out if quit never cancels it
        Assert.True(await Eventually(() => engine.Disposed, FailAfterMs), "the warm-up ran on after quit and its engine was never released");
    }

    [Fact]
    public async Task Quit_stops_a_cleanup_warm_up_and_releases_its_engine()
    {
        FakeLocalEngine? engine = null;
        var llm = new LocalLlmManager(NullLog.Instance, loader: (p, _) => engine = new FakeLocalEngine(p, waitForCancel: true));
        llm.BeginLoad(Model("qwen2.5-1.5b-instruct-q4_k_m.gguf"), "auto");
        Assert.True(await Eventually(() => engine is not null, FailAfterMs), "the clean-up load never began");
        await engine!.WarmUpStarted.WaitAsync(TimeSpan.FromMilliseconds(FailAfterMs), Ct); // the warm-up is running
        Assert.False(engine.WarmUpCancelled.IsCompleted); // nothing but quit can end it
        llm.Dispose();
        await engine.WarmUpCancelled.WaitAsync(TimeSpan.FromMilliseconds(FailAfterMs), Ct); // times out if quit never cancels it
        Assert.True(await Eventually(() => engine.Disposed, FailAfterMs), "the warm-up ran on after quit and its engine was never released");
    }

    [Fact]
    public async Task A_failed_speech_load_is_retried_when_the_same_model_is_chosen_again()
    {
        var calls = 0;
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => Interlocked.Increment(ref calls) == 1
            ? throw new ModelIntegrityException("ggml-tiny.en.bin is 1,000 bytes but the catalogue says 77,704,715")
            : new FakeSpeechEngine(p, d), gpuInUse: () => false);
        File.WriteAllBytes(Model("ggml-tiny.en.bin"), Payload);
        _settings = new AppSettings { Whisper = new WhisperSettings { Model = "tiny.en" } };
        var coordinator = Make(models);
        coordinator.Ensure(previousPath: null, startup: true);
        Assert.True(await Eventually(() => models.LoadFailed));

        coordinator.Ensure(coordinator.ModelPath, startup: false); // re-downloaded, or tray > Reload settings: same path
        Assert.True(await Eventually(() => models.IsReady), "a failed load at the same path is never retried");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task First_run_downloads_one_model_at_a_time_and_Cancel_stops_both()
    {
        _prompts.Speech = new ModelPick("tiny.en", null, false);
        _prompts.Cleanup = new CleanupPick("qwen2.5-0.5b", null, false, false);
        var coordinator = Make();
        Assert.True(coordinator.FirstRun());
        _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
        coordinator.Ensure(previousPath: null, startup: true); // as AppHost.Start does
        coordinator.EnsureCleanup();
        Assert.True(await Eventually(() => !_server.Requests.IsEmpty));
        await Task.Delay(300, Ct);
        Assert.Equal(["ggml-tiny.en.bin"], _server.Requests.ToArray());
        coordinator.EnsureCleanup(); // a settings reload while the cleanup model waits its turn
        Assert.DoesNotContain(_balloons, b => b.Contains("not downloaded", StringComparison.Ordinal));
        Assert.True(coordinator.IsDownloading);

        coordinator.CancelDownload(DownloadCancelReason.CancelLink);
        Assert.True(await Eventually(() => !coordinator.IsDownloading && _server.InFlight == 0), "a download kept running after Cancel");
        await Task.Delay(300, Ct);
        Assert.Single(_server.Requests); // the queued cleanup download went with it
        Assert.False(coordinator.IsDownloading);
    }

    [Fact]
    public async Task A_second_calibration_download_waits_for_the_first_instead_of_cancelling_it()
    {
        _prompts.Confirm = true;
        var coordinator = Make();
        var (first, second) = (0, 0);
        _server.Release("b.bin");
        coordinator.ApplyWithDownloads(PersonalService.CalibrationDownload, [(Spec("a.bin"), Model("a.bin"))], [], () => first++);
        Assert.True(await Eventually(() => _server.InFlight == 1));
        coordinator.ApplyWithDownloads(PersonalService.CalibrationDownload, [(Spec("b.bin"), Model("b.bin"))], [], () => second++);
        await Task.Delay(200, Ct);
        Assert.Equal(0, _server.Cancelled); // a.bin keeps its progress
        Assert.Equal(["a.bin"], _server.Requests.ToArray());

        _server.Release("a.bin");
        Assert.True(await Eventually(() => first == 1 && second == 1), "a queued download never ran, or ran before the first finished");
        Assert.Equal(["a.bin", "b.bin"], _server.Requests.ToArray());
        Assert.True(File.Exists(Model("a.bin")) && File.Exists(Model("b.bin")));
    }

    [Fact]
    public async Task A_download_whose_switch_fails_says_so_in_a_balloon()
    {
        _prompts.Confirm = true;
        _server.Release("a.bin");
        var coordinator = Make(reload: () => throw new InvalidOperationException("reload broke"));
        coordinator.ApplyWithDownloads("Full mode", [(Spec("a.bin"), Model("a.bin"))], [("whisper.beam_size", JsonValue.Create(5))]);
        Assert.True(await Eventually(() => _balloons.Any(b => b.Contains("reload broke", StringComparison.Ordinal))),
            "the switch after the download failed silently: " + string.Join(" | ", _balloons));
        Assert.Contains(_balloons, b => b.Contains("a.bin", StringComparison.Ordinal) && b.Contains("reload broke", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tray speech model")]
    [InlineData("tray cleanup model")]
    [InlineData("mode preset")]
    public async Task Declining_a_download_makes_no_request(string site)
    {
        var coordinator = Make(); // _prompts.Confirm is false: the user clicks Cancel
        switch (site)
        {
            case "tray speech model": coordinator.Select("small.en"); break;
            case "tray cleanup model": coordinator.SelectCleanup("qwen2.5-0.5b"); break;
            default: coordinator.ApplyWithDownloads("Full mode", [(Spec("a.bin"), Model("a.bin"))], [("whisper.beam_size", JsonValue.Create(5))]); break;
        }
        await Task.Delay(200, Ct);
        Assert.Equal(1, _prompts.ConfirmCalls);
        Assert.False(coordinator.IsDownloading);
        Assert.Empty(_server.Requests);
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    [Fact]
    public void A_declined_download_tells_its_caller_it_finished()
    {
        var finished = new List<bool>();
        Make().ApplyWithDownloads(PersonalService.CalibrationDownload, [(Spec("a.bin"), Model("a.bin"))], [], finished: finished.Add);
        Assert.Equal([false], finished); // the calibration window can enable its button again
    }

    [Fact]
    public void First_run_on_a_seeded_file_asks_only_what_the_file_leaves_open()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "whisper": { "model": "base.en" }, "offline_only": true }""");
        var load = SettingsLoader.LoadFile(_paths.SettingsFile);
        Assert.Equal((false, true), AppRules.FirstRunQuestions(load));
        _prompts.Cleanup = new CleanupPick("qwen2.5-0.5b", null, false, false);

        Assert.True(Make().FirstRun(askSpeech: false, askCleanup: true));
        Assert.Equal((0, 1), (_prompts.SpeechPickerCalls, _prompts.CleanupPickerCalls));
        var after = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
        Assert.Equal(("base.en", "local", "qwen2.5-0.5b", true), (after.Whisper.Model, after.Llm.Provider, after.Llm.Model, after.OfflineOnly));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("""{ "offline_only": true }""", true, true)]
    [InlineData("""{ "whisper": { "model": "base.en" } }""", false, true)]
    [InlineData("""{ "llm": { "provider": "none" } }""", true, false)]
    [InlineData("""{ "whisper": { "model": "base.en" }, "llm": { "model": "qwen2.5-1.5b" } }""", false, false)]
    [InlineData("""{ "whisper": { "model": "base.en" """, false, false)] // broken: never overwritten by pickers
    public void First_run_pickers_follow_what_settings_json_names(string? json, bool speech, bool cleanup)
    {
        if (json is not null) File.WriteAllText(_paths.SettingsFile, json);
        Assert.Equal((speech, cleanup), AppRules.FirstRunQuestions(SettingsLoader.LoadFile(_paths.SettingsFile)));
    }

    [Fact]
    public void A_loaded_catalogue_cleanup_model_is_named_and_ticked_by_its_catalogue_name()
    {
        var llm = new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p));
        var path = Model("qwen2.5-1.5b-instruct-q4_k_m.gguf");
        llm.BeginLoad(path, "auto");
        Assert.Equal("qwen2.5-1.5b", Make(llm: llm).CurrentCleanupName); // the tray ticks the entry with this name
        Assert.Equal("qwen2.5-1.5b", CleanupCatalog.DisplayName(path));
        Assert.Equal("my-own-model", CleanupCatalog.DisplayName(@"D:\Models\my-own-model.gguf"));
    }

    [Fact]
    public void Download_prompts_promise_a_switch_only_when_there_is_one()
    {
        var coordinator = Make();
        coordinator.ApplyWithDownloads(PersonalService.CalibrationDownload, [(ModelCatalog.Find("medium.en")!.Download, Model("ggml-medium.en.bin"))], []);
        var calibration = ModelPromptText.ConfirmText("ggml-medium.en.bin", "1.5 GB", _prompts.LastNote!);
        Assert.DoesNotContain("switches to it", calibration, StringComparison.Ordinal);
        Assert.Contains("your current model stays in use", calibration, StringComparison.Ordinal);

        coordinator.Select("small.en");
        Assert.Contains("switches to it when the download finishes", ModelPromptText.ConfirmText("ggml-small.en.bin", "487 MB", _prompts.LastNote!), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _client.Dispose();
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
