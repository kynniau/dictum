using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Runtime resilience (17/10/2026, after the Store certification investigation): a model that cannot load is named in plain
/// words (F2), a load or warm-up that fails on the GPU is tried once more on the CPU and never leaks its engine (F3), the
/// clean-up model stays off integrated graphics (F4), and the speed estimate neither keeps the first warm-up's figures nor
/// stays wrong when it skips every dictation (F5). The lens's fixes (the GPU-load marker, Try on the CPU's gating and the rest)
/// are in RuntimeResilienceTests.Marker.cs.
/// </summary>
public sealed partial class RuntimeResilienceTests
{
    /// <summary>Whisper.net 1.9.1's message, as the Windows Sandbox self-test printed it (investigation, 16/10/2026).</summary>
    private const string WhisperNativeFailure =
        "Failed to load native whisper library. Error: Cannot load the library on this platform using NativeLibrary. PInvokeError: The operation completed successfully.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// How long a test waits before it reports that a signal never came. Only for tests whose outcome is decided by order, not by
    /// time (the event either happens or never does), so it is generous: a loaded CI runner (21/10/2026, run 39629537889) took
    /// over 2 s to schedule a load that runs in milliseconds locally.
    /// </summary>
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

    /// <summary>The same on the UI thread, which must run the jobs the shell posts.</summary>
    private static bool EventuallyOnUi(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static string VisibleTexts(ILogical root) =>
        string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    private static Button? VisibleButton(ILogical root, string label) =>
        All<Button>(root).FirstOrDefault(b => b.Content as string == label && b.IsEffectivelyVisible);

    // ---- F2: failures named in plain words ----

    [Fact]
    public void The_proposed_words_are_pinned_as_proposed()
    {
        // APPROVED 22/10/2026 copy (proposed 17/10/2026): pinned word for word.
        Assert.Equal("The Microsoft Visual C++ runtime is missing; install it from Microsoft, then restart Dictum", ModelFailure.RuntimeMissing);
        Assert.Equal("The graphics driver could not run the model; Dictum will use the CPU", ModelFailure.GpuFailed);
        Assert.Equal("The graphics driver could not run this model, so Dictum uses the CPU for it.", ModelFailure.GpuFailedLastTime); // lens fix, 17/10/2026
        Assert.Equal("Try on the CPU", AppShell.TryOnCpuLabel);
        Assert.Equal("Restart Dictum to use the CPU.", AppShell.RestartForCpuHint);
        Assert.Equal("Clean-up: qwen2.5-1.5b could not be loaded, so the rule-based tidy is used.", AboutPage.CleanupFailedLine("qwen2.5-1.5b"));
    }

    [Fact]
    public void An_engine_library_that_cannot_load_without_the_Visual_Cpp_runtime_is_named_in_plain_words()
    {
        Exception[] failures =
        [
            new Exception(WhisperNativeFailure),
            new TypeInitializationException("LLama.Native.NativeApi", new Exception("The native library cannot be correctly loaded.")),
            new AggregateException(new TypeInitializationException("LLama.Native.NativeApi", null)), // as a faulted load task hands it on
            new DllNotFoundException("Unable to load DLL 'llama' or one of its dependencies"),
        ];
        foreach (var failure in failures)
        {
            Assert.True(ModelFailure.IsNativeLibraryFailure(failure), failure.Message);
            Assert.Equal(ModelFailure.RuntimeMissing, ModelFailure.Describe(failure, runtimePresent: () => false));
            Assert.Equal(failure.GetBaseException().Message, ModelFailure.Describe(failure, runtimePresent: () => true)); // another cause: its own words
            Assert.False(ModelFailure.WorthCpuRetry(failure)); // the CPU build needs the same runtime
        }
    }

    [Fact]
    public void Other_failures_keep_their_own_words_and_ask_nothing_about_the_runtime()
    {
        var asked = false;
        var integrity = new ModelIntegrityException("ggml-tiny.en.bin is 1,000 bytes but the catalogue says 77,704,715; it was not loaded.");
        Assert.Equal(integrity.Message, ModelFailure.Describe(new AggregateException(integrity), () => asked = true));
        Assert.False(asked);
        Assert.False(ModelFailure.WorthCpuRetry(integrity));
        Assert.False(ModelFailure.WorthCpuRetry(new FileNotFoundException("Whisper model file not found.")));
        Assert.False(ModelFailure.WorthCpuRetry(new OperationCanceledException()));
        Assert.True(ModelFailure.WorthCpuRetry(new InvalidOperationException("llama.cpp decode failed: Error")));
    }

    /// <summary>No version resource: the version is not known, so the file counts, as before the version floor.</summary>
    private static Version? NoVersion(string path) => null;

    [Fact]
    public void The_runtime_counts_as_present_when_each_DLL_loads_or_sits_beside_the_engines()
    {
        var folder = Directory.CreateTempSubdirectory("dictum-vcrt-");
        try
        {
            string[] none = [];
            Assert.True(ModelFailure.VisualCppRuntimePresent(dll => dll, none, NoVersion));
            Assert.False(ModelFailure.VisualCppRuntimePresent(_ => null, none, NoVersion));
            Assert.False(ModelFailure.VisualCppRuntimePresent(dll => dll == "vcomp140.dll" ? null : dll, none, NoVersion)); // one of the four missing
            foreach (var dll in ModelFailure.RuntimeDlls) File.WriteAllBytes(Path.Combine(folder.FullName, dll), [0]);
            Assert.True(ModelFailure.VisualCppRuntimePresent(_ => null, [folder.FullName], NoVersion)); // copies beside the engines (publish -VcRuntimeDir)
            File.Delete(Path.Combine(folder.FullName, "msvcp140.dll"));
            Assert.False(ModelFailure.VisualCppRuntimePresent(_ => null, [folder.FullName], NoVersion));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void One_problem_line_names_both_models_when_they_share_the_reason()
    {
        Assert.Null(AppShell.ProblemLine(null, null));
        Assert.Equal($"Speech and clean-up models: {ModelFailure.RuntimeMissing}.", AppShell.ProblemLine(ModelFailure.RuntimeMissing, ModelFailure.RuntimeMissing));
        Assert.Equal($"Speech model: {ModelFailure.GpuFailed}.", AppShell.ProblemLine(ModelFailure.GpuFailed, null));
        Assert.Equal("Clean-up model: Failed to load the whisper model.", AppShell.ProblemLine(null, "Failed to load the whisper model."));
        Assert.Equal($"Speech model: a.\nClean-up model: {ModelFailure.GpuFailed}.", AppShell.ProblemLine("a", ModelFailure.GpuFailed));
    }

    /// <summary>A speech model at an absolute path, so the shell loads it without the catalogue's checks.</summary>
    private static (Scratch Scratch, string Model) SpeechScratch(string extraJson = "")
    {
        var scratch = new Scratch();
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        File.WriteAllText(scratch.Paths.SettingsFile,
            $$"""{ "hotkey": "F24", "whisper": { "model": {{JsonSerializer.Serialize(model)}} }, "llm": { "provider": "none" }{{extraJson}} }""");
        return (scratch, model);
    }

    [Fact]
    public Task The_tray_panel_names_a_missing_Visual_Cpp_runtime_and_offers_the_log_folder_not_the_CPU() => UiSession.Run(() =>
    {
        var (scratch, _) = SpeechScratch();
        using var _scratch = scratch;
        var models = new ModelManager(NullLog.Instance, loader: (_, _, _) => throw new Exception(WhisperNativeFailure), gpuInUse: () => false,
            visualCppRuntime: () => false);
        var (shell, platform, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.SpeechError is not null), "the load never failed");

        var status = shell.Status;
        Assert.Equal(("Model error", "error"), (status.State, status.Tone));
        Assert.Equal($"Speech model: {ModelFailure.RuntimeMissing}.", status.Problem);
        Assert.False(status.TryCpu);

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains($"Speech model: {ModelFailure.RuntimeMissing}.", VisibleTexts(flyout));
        Assert.Null(VisibleButton(flyout, AppShell.TryOnCpuLabel)); // the CPU build needs the same runtime
        var logs = VisibleButton(flyout, "Open log folder");
        Assert.NotNull(logs);
        logs.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(scratch.Paths.LogsDir, platform.Opened);
        flyout.Close();
        shell.Dispose();
    });

    [Fact]
    public Task When_the_GPU_and_the_retry_on_the_CPU_both_fail_the_panel_shows_the_CPU_error_with_the_log_folder_and_no_Try_on_the_CPU() => UiSession.Run(() =>
    {
        // The lens's real run R1 (17/10/2026) with stand-ins: a truncated model failed on the GPU and on the CPU, and the button
        // was offered; pressing it repeated the failed attempt and wrote whisper.gpu = cpu for good.
        var (scratch, _) = SpeechScratch();
        using var _scratch = scratch;
        var asked = new ConcurrentQueue<string>();
        var models = new ModelManager(NullLog.Instance, loader: (_, s, _) =>
        {
            asked.Enqueue(s.Gpu);
            throw new InvalidOperationException(s.Gpu == "cpu" ? "out of memory" : "ggml_vulkan: device lost");
        }, gpuInUse: () => true); // inside a GPU runtime: the GPU load fails, and so does the retry on the CPU inside it
        var (shell, _, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.SpeechError is not null), "the load never failed");
        Assert.Equal(new[] { "auto", "cpu" }, asked.ToArray());
        Assert.Equal("Speech model: out of memory.", shell.Status.Problem); // the CPU attempt's own error
        Assert.False(shell.Status.TryCpu); // the CPU was tried already

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains("Speech model: out of memory.", VisibleTexts(flyout));
        Assert.Null(VisibleButton(flyout, AppShell.TryOnCpuLabel));
        Assert.NotNull(VisibleButton(flyout, "Open log folder"));
        Assert.Equal("auto", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Whisper.Gpu); // nothing written
        flyout.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Try_on_the_CPU_is_not_offered_when_the_model_already_ran_on_the_CPU() => UiSession.Run(() =>
    {
        var (scratch, _) = SpeechScratch();
        using var _scratch = scratch;
        var models = new ModelManager(NullLog.Instance, loader: (_, _, _) => throw new InvalidOperationException("ggml_vulkan: device lost"),
            gpuInUse: () => false); // no GPU runtime in use: the model already ran on the CPU, so the CPU cannot help
        var (shell, _, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.SpeechError is not null), "the load never failed");
        Assert.Equal("Speech model: ggml_vulkan: device lost.", shell.Status.Problem);
        Assert.False(shell.Status.TryCpu);
        shell.Dispose();
    });

    [Fact]
    public Task A_model_moved_to_the_CPU_after_the_GPU_failed_says_so_in_the_tray_panel() => UiSession.Run(() =>
    {
        var (scratch, _) = SpeechScratch(""", "ui": { "view": "advanced" }"""); // the runtime is Advanced detail; the Simple header names none (1.2 polish)
        using var _scratch = scratch;
        var models = new ModelManager(NullLog.Instance, loader: (p, s, d) =>
            s.Gpu == "cpu" ? new FakeSpeechEngine(p, d) : throw new InvalidOperationException("Failed to load the whisper model."), gpuInUse: () => true);
        var (shell, _, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.SpeechReady), "the model was not loaded on the CPU");

        var status = shell.Status;
        Assert.Equal(("Ready", "ready"), (status.State, status.Tone)); // it works: the line says why it is on the CPU
        Assert.Contains(" · CPU · ", status.Models, StringComparison.Ordinal);
        Assert.Equal($"Speech model: {ModelFailure.GpuFailed}.", status.Problem);
        Assert.False(status.TryCpu); // nothing failed: there is nothing to try
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains($"Speech model: {ModelFailure.GpuFailed}.", VisibleTexts(flyout));
        flyout.Close();
        shell.Dispose();
    });

    [Fact]
    public Task About_says_a_failed_clean_up_model_leaves_the_rule_based_tidy_and_why() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "local", "model": "qwen2.5-0.5b" } }""");
        Directory.CreateDirectory(scratch.Paths.DefaultModelsDir);
        var file = CleanupCatalog.Find("qwen2.5-0.5b")!.FileName;
        File.WriteAllBytes(Path.Combine(scratch.Paths.DefaultModelsDir, file), [1, 2, 3, 4]); // fails its size check: nothing native loads
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.CleanupError is not null), "the clean-up load never failed");

        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        var texts = VisibleTexts(window);
        Assert.Contains(AboutPage.CleanupFailedLine("qwen2.5-0.5b"), texts);
        Assert.DoesNotContain("on this PC (nothing leaves it)", texts); // the line the page showed while the engine had failed
        Assert.Contains($"Clean-up model: {shell.CleanupError}", texts);
        Assert.Null(VisibleButton(window, AppShell.TryOnCpuLabel)); // a file that fails its check is not the GPU's fault
        window.Close();
        shell.Dispose();
    });

    // ---- F3: one more try on the CPU after a GPU failure, and no engine left behind ----

    /// <summary>A speech engine whose warm-up (or any transcription) fails, as a GPU that cannot run it would.</summary>
    private sealed class FailingSpeechEngine(string path, DecodeOptions decode) : ISpeechEngine
    {
        private int _disposed;

        public string ModelPath { get; } = path;
        public DecodeOptions Decode { get; private set; } = decode;
        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public void Reconfigure(DecodeOptions options) => Decode = options;

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Native whisper stopped processing with error code -6");

        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }

    [Fact]
    public async Task A_speech_load_that_fails_on_the_GPU_is_loaded_again_on_the_CPU()
    {
        var asked = new ConcurrentQueue<string>();
        var log = new RecordingLog();
        using var models = new ModelManager(log, loader: (p, s, d) =>
        {
            asked.Enqueue(s.Gpu);
            return s.Gpu == "cpu" ? new FakeSpeechEngine(p, d) : throw new InvalidOperationException("Failed to load the whisper model.");
        }, gpuInUse: () => true);
        models.BeginLoad("ggml-small.en.bin", new WhisperSettings());

        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.True(models.IsReady, $"the model was not loaded again on the CPU: {models.LastError}");
        Assert.Equal(new[] { "auto", "cpu" }, asked.ToArray());
        Assert.Null(models.LastError);
        Assert.True(models.RunsOnCpu);
        Assert.Equal(ModelFailure.GpuFailed, models.Notice);
        Assert.Single(log.Lines, l => l.Message.Contains("failed while the GPU was in use", StringComparison.Ordinal)); // one line says so
    }

    [Fact]
    public async Task A_speech_warm_up_that_fails_on_the_GPU_releases_that_engine_and_runs_the_model_on_the_CPU()
    {
        var made = new ConcurrentQueue<(string Gpu, ISpeechEngine Engine)>();
        using var models = new ModelManager(NullLog.Instance, loader: (p, s, d) =>
        {
            ISpeechEngine engine = s.Gpu == "cpu" ? new FakeSpeechEngine(p, d) : new FailingSpeechEngine(p, d);
            made.Enqueue((s.Gpu, engine));
            return engine;
        }, gpuInUse: () => true);
        models.BeginLoad("ggml-small.en.bin", new WhisperSettings());

        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.True(models.IsReady);
        Assert.Equal(new[] { "auto", "cpu" }, made.Select(m => m.Gpu).ToArray());
        Assert.True(((FailingSpeechEngine)made.First().Engine).Disposed, "the engine whose warm-up failed was left loaded");
        Assert.Same(made.Last().Engine, await models.GetAsync(Ct));
        Assert.Equal(ModelFailure.GpuFailed, models.Notice);
    }

    [Fact]
    public async Task When_the_CPU_fails_too_its_own_error_is_shown_and_the_GPU_engine_is_released()
    {
        FailingSpeechEngine? onGpu = null;
        using var models = new ModelManager(NullLog.Instance, loader: (p, s, d) =>
            s.Gpu == "cpu" ? throw new InvalidOperationException("out of memory") : onGpu = new FailingSpeechEngine(p, d), gpuInUse: () => true);
        models.BeginLoad("ggml-small.en.bin", new WhisperSettings());
        Assert.True(await Eventually(() => models.LoadFailed && models.LastError is not null)); // the failure is recorded just after the load faults (ModelManager.Finished)
        Assert.True(onGpu!.Disposed, "the engine whose warm-up failed was left loaded");
        Assert.Equal("out of memory", models.LastError); // the CPU's own failure
        Assert.False(models.CpuMightHelp); // the CPU was tried for this failure: Try on the CPU would only repeat it
        Assert.False(models.RestartForCpu);
        Assert.False(models.RunsOnCpu);
        Assert.Null(models.Notice);
    }

    [Fact]
    public async Task Without_a_GPU_runtime_in_use_a_failed_load_is_not_tried_again_and_the_CPU_is_not_offered()
    {
        var calls = 0;
        using var models = new ModelManager(NullLog.Instance, loader: (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("ggml_vulkan: device lost");
        }, gpuInUse: () => false);
        models.BeginLoad("ggml-small.en.bin", new WhisperSettings());
        Assert.True(await Eventually(() => models.LoadFailed && models.LastError is not null)); // the failure is recorded just after the load faults (ModelManager.Finished)
        Assert.Equal(1, calls);
        Assert.Equal("ggml_vulkan: device lost", models.LastError);
        Assert.False(models.CpuMightHelp); // the model already ran on the CPU
        Assert.False(models.RestartForCpu);

        using var onCpu = new ModelManager(NullLog.Instance, loader: (_, _, _) => throw new InvalidOperationException("bad file"), gpuInUse: () => true);
        onCpu.BeginLoad("ggml-small.en.bin", new WhisperSettings { Gpu = "cpu" }); // on the CPU inside a GPU runtime
        Assert.True(await Eventually(() => onCpu.LoadFailed && onCpu.LastError is not null)); // the failure is recorded just after the load faults (ModelManager.Finished)
        Assert.False(onCpu.CpuMightHelp); // whisper.gpu is cpu already
        Assert.True(onCpu.RestartForCpu); // only a restart loads the CPU runtime
    }

    [Fact]
    public async Task A_missing_engine_library_is_not_tried_again_on_the_CPU()
    {
        var calls = 0;
        using var models = new ModelManager(NullLog.Instance, loader: (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new Exception(WhisperNativeFailure);
        }, gpuInUse: () => true, visualCppRuntime: () => false);
        models.BeginLoad("ggml-small.en.bin", new WhisperSettings());
        Assert.True(await Eventually(() => models.LoadFailed && models.LastError is not null)); // the failure is recorded just after the load faults (ModelManager.Finished)
        Assert.Equal(1, calls);
        Assert.Equal(ModelFailure.RuntimeMissing, models.LastError);
        Assert.False(models.CpuMightHelp);
    }

    /// <summary>A clean-up engine that measures nothing and whose warm-up can fail.</summary>
    private sealed class ScriptedLocalEngine(string path, bool warmUpFails = false) : ILocalLlmEngine
    {
        private int _disposed;

        public string ModelPath { get; } = path;
        public bool Disposed => Volatile.Read(ref _disposed) == 1;
        public TimeSpan? Estimate { get; set; }
        public int Calls;
        public (double Prompt, double Generation) Rates => (0, 0);

        public Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default) =>
            warmUpFails ? throw new InvalidOperationException("llama.cpp decode failed: Error") : Task.FromResult(TimeSpan.Zero);

        public int CountTokens(string text) => text.Length / 4;

        public TimeSpan? Predict(int dictationTokens) => Estimate;

        public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new GenerationResult(raw, true, 0, TimeSpan.Zero, 0, TimeSpan.Zero));
        }

        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }

    [Fact]
    public async Task A_clean_up_load_that_fails_on_the_GPU_is_loaded_again_on_the_CPU()
    {
        var asked = new ConcurrentQueue<string>();
        using var llm = new LocalLlmManager(NullLog.Instance, loader: (p, gpu) =>
        {
            asked.Enqueue(gpu);
            return gpu == "cpu" ? new ScriptedLocalEngine(p) : throw new InvalidOperationException("Failed to load model");
        }, gpuInUse: () => true);
        llm.BeginLoad("qwen2.5-1.5b-instruct-q4_k_m.gguf", "auto");

        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.NotNull(llm.Ready);
        Assert.Equal(new[] { "auto", "cpu" }, asked.ToArray());
        Assert.Null(llm.LastError);
        Assert.True(llm.RunsOnCpu);
        Assert.Equal(ModelFailure.GpuFailed, llm.Notice);
    }

    [Fact]
    public async Task A_clean_up_warm_up_that_fails_on_the_GPU_releases_that_engine_and_runs_the_model_on_the_CPU()
    {
        var made = new ConcurrentQueue<ScriptedLocalEngine>();
        using var llm = new LocalLlmManager(NullLog.Instance, loader: (p, gpu) =>
        {
            var engine = new ScriptedLocalEngine(p, warmUpFails: gpu != "cpu");
            made.Enqueue(engine);
            return engine;
        }, gpuInUse: () => true);
        llm.BeginLoad("qwen2.5-1.5b-instruct-q4_k_m.gguf", "auto");

        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.Equal(2, made.Count);
        Assert.True(made.First().Disposed, "the engine whose warm-up failed was left loaded");
        Assert.Same(made.Last(), llm.Ready);
        Assert.Equal(ModelFailure.GpuFailed, llm.Notice);

        using var cpu = new LocalLlmManager(NullLog.Instance, loader: (p, _) => new ScriptedLocalEngine(p, warmUpFails: true), gpuInUse: () => true);
        cpu.BeginLoad("qwen2.5-1.5b-instruct-q4_k_m.gguf", "cpu"); // llm.gpu = cpu inside the Vulkan build: nothing else to try now
        Assert.True(await Eventually(() => !cpu.IsLoading && cpu.LastError is not null)); // the failure is recorded just after the load faults (LocalLlmManager.Finished)
        Assert.Equal("llama.cpp decode failed: Error", cpu.LastError);
        Assert.False(cpu.CpuMightHelp);
        Assert.True(cpu.RestartForCpu); // only a restart loads the CPU build
    }

    // ---- F4: the clean-up model stays off integrated graphics ----

    private const string Quadro = "ggml_vulkan: 0 = Quadro RTX 4000 (NVIDIA) | uma: 0 | fp16: 1 | bf16: 1 | warp size: 32 | shared memory: 49152 | int dot: 1 | matrix cores: NV_coopmat2";
    private const string P630 = "ggml_vulkan: 0 = Intel(R) UHD Graphics P630 (Intel Corporation) | uma: 1 | fp16: 1 | bf16: 0 | warp size: 32 | shared memory: 32768 | int dot: 0 | matrix cores: none";
    private const string P630Second = "ggml_vulkan: 1 = Intel(R) UHD Graphics P630 (Intel Corporation) | uma: 1 | fp16: 1 | bf16: 0 | warp size: 32 | shared memory: 32768 | int dot: 0 | matrix cores: none";

    /// <summary>Device counts as whisper's runtime reports them, from ggml_vulkan's own lines (this PC's logs, 15 and 16/10/2026).</summary>
    private static (int Found, int Discrete, int Integrated) Devices(params string[] lines)
    {
        var parsed = lines.Select(l => WhisperEngine.ParseDevice(l)!.Value).ToList();
        return (parsed.Count, parsed.Count(d => !d.Integrated), parsed.Count(d => d.Integrated));
    }

    [Fact]
    public void The_clean_up_model_uses_the_CPU_when_the_only_GPU_is_integrated_graphics()
    {
        Assert.False(LocalLlmEngine.UseVulkan("auto", Devices(P630))); // the P630 alone (GGML_VK_VISIBLE_DEVICES=1)
        Assert.True(LocalLlmEngine.UseVulkan("auto", Devices(Quadro, P630Second))); // this PC: the Quadro keeps Vulkan
        Assert.True(LocalLlmEngine.UseVulkan("auto", Devices(Quadro)));
        Assert.True(LocalLlmEngine.UseVulkan("auto", null)); // devices never described (whisper on the CPU, or not loaded yet): as before
        Assert.True(LocalLlmEngine.UseVulkan("auto", (0, 0, 0))); // a Vulkan loader with no device: as before
        Assert.True(LocalLlmEngine.UseVulkan("vulkan", Devices(P630))); // llm.gpu still forces
        Assert.False(LocalLlmEngine.UseVulkan("cpu", Devices(Quadro)));
    }

    // ---- F5: an honest speed estimate ----

    private static GenerationResult Measured(int promptTokens, double promptSeconds, int generated, double generationSeconds) =>
        new("", true, promptTokens, TimeSpan.FromSeconds(promptSeconds), generated, TimeSpan.FromSeconds(generationSeconds));

    [Fact]
    public async Task The_first_warm_up_runs_twice_and_the_estimate_keeps_the_second_pass()
    {
        var speed = new SpeedEstimate();
        var passes = 0;
        // The P630's first pass (16/10/2026: 2 tokens/s reading, 3.3 writing), then what a warm model does.
        await speed.WarmUpAsync(() =>
        {
            speed.Record(++passes == 1 ? Measured(21, 10.5, 16, 4.85) : Measured(21, 0.5, 16, 2.0));
            return Task.CompletedTask;
        });
        Assert.Equal(2, passes);
        Assert.Equal((42.0, 8.0), speed.Rates);
        Assert.True(speed.Predict(34) < TimeSpan.FromSeconds(15), $"a 23-word dictation is still estimated at {speed.Predict(34)}");

        await speed.WarmUpAsync(() => // a changed prompt later: one pass
        {
            passes++;
            return Task.CompletedTask;
        });
        Assert.Equal(3, passes);
    }

    [Fact]
    public async Task The_first_pass_figures_never_stand_in_for_a_figure_the_second_pass_could_not_measure()
    {
        var speed = new SpeedEstimate();
        var passes = 0;
        await speed.WarmUpAsync(() =>
        {
            speed.Record(++passes == 1 ? Measured(21, 10.5, 16, 4.85) : Measured(8, 0.1, 16, 2.0)); // too few prompt tokens to measure
            return Task.CompletedTask;
        });
        Assert.Equal(0, speed.Rates.Prompt);
        Assert.Null(speed.Predict(34)); // no estimate, so the first dictation runs the model and measures it
    }

    /// <summary>A fresh string per call, as each transcription is (the estimate tells dictations apart by instance).</summary>
    private static string Dictation(int i) => new($"the quarterly report for region {i} is ready for review please send comments by friday".AsSpan());

    [Fact]
    public async Task An_estimate_that_skips_every_dictation_is_measured_again_after_three()
    {
        var engine = new ScriptedLocalEngine("m.gguf") { Estimate = TimeSpan.FromSeconds(100) };
        var cleaner = new TranscriptCleaner(new LocalLlmClient(() => engine, "m"), CleanupPrompt.Default, TimeSpan.FromSeconds(15), NullLog.Instance, "local/m");
        var skipped = new List<bool>();
        for (var i = 0; i < 16; i++)
        {
            if (i == 11) engine.Estimate = TimeSpan.FromSeconds(1); // the 12th: the model turned out fast after all
            if (i == 12) engine.Estimate = TimeSpan.FromSeconds(100);
            skipped.Add((await cleaner.CleanAsync(Dictation(i), Ct)).Skipped);
        }
        // Three skipped, the fourth measured; then six (the allowance doubles while the model stays slow), the eleventh measured;
        // a dictation inside the deadline puts the allowance back to three.
        bool[] expected = [true, true, true, false, true, true, true, true, true, true, false, false, true, true, true, false];
        Assert.Equal(expected, skipped);
        Assert.Equal(4, engine.Calls);
    }

    [Fact]
    public async Task The_pill_and_the_clean_up_agree_on_each_dictation()
    {
        var engine = new ScriptedLocalEngine("m.gguf") { Estimate = TimeSpan.FromSeconds(100) };
        var cleaner = new TranscriptCleaner(new LocalLlmClient(() => engine, "m"), CleanupPrompt.Default, TimeSpan.FromSeconds(15), NullLog.Instance, "local/m");
        var dictation = 0;
        var pills = new List<PillView>();
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber(Dictation(++dictation))), () => cleaner,
            (_, _) => Task.CompletedTask, () => new AppSettings(), NullLog.Instance);
        for (var i = 0; i < 4; i++) await pipeline.RunAsync(new CapturedAudio(new float[32000], 0.2f), Ct, pills.Add);
        // The pill asks first and the clean-up asks again about the same dictation: one dictation, one count.
        Assert.Equal([PillView.PolishingSkipped, PillView.PolishingSkipped, PillView.PolishingSkipped, PillView.Polishing], pills);
        Assert.Equal(1, engine.Calls);
    }
}
