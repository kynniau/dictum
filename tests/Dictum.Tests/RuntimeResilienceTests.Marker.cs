using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// The lens's findings on PR #12 (17/10/2026): the GPU-load marker for a GPU that ends Dictum (whisper.cpp's GGML_ASSERT), Try on
/// the CPU offered only where the CPU has not been tried, About's line for the plain clean-up, the pins the lens found missing,
/// keyboard focus after Try on the CPU, the Visual C++ runtime's version floor and the load balloon.
/// </summary>
public sealed partial class RuntimeResilienceTests
{
    /// <summary>A per-user local data folder for a manager with no shell: its integrity record's folder holds the marker.</summary>
    private sealed class LocalFolder : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("dictum-marker-");
        public ModelIntegrity Integrity => new(Path.Combine(Dir.FullName, "verified-models.json"), NullLog.Instance);
        public string SpeechMarker => Path.Combine(Dir.FullName, GpuLoadMarker.SpeechFile);
        public string CleanupMarker => Path.Combine(Dir.FullName, GpuLoadMarker.CleanupFile);

        /// <summary>A model path outside the catalogue, so the integrity check reads nothing.</summary>
        public string Model(string name) => Path.Combine(Dir.FullName, name);

        public void Dispose() => Dir.Delete(recursive: true);
    }

    private static string? Read(string file) => File.Exists(file) ? File.ReadAllText(file) : null;

    // ---- Finding 1: the GPU-load marker ----

    [Fact]
    public void The_managers_marker_files_are_the_ones_the_path_registry_lists()
    {
        using var scratch = new Scratch();
        var folder = new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Folder;
        Assert.Equal(scratch.Paths.SpeechGpuMarkerFile, Path.Combine(folder, GpuLoadMarker.SpeechFile));
        Assert.Equal(scratch.Paths.CleanupGpuMarkerFile, Path.Combine(folder, GpuLoadMarker.CleanupFile));
    }

    [Fact]
    public async Task A_start_whose_marker_names_the_speech_model_asks_the_loader_for_the_CPU_only_and_keeps_the_marker()
    {
        using var local = new LocalFolder();
        var model = local.Model("speech-a.bin");
        File.WriteAllText(local.SpeechMarker, model); // the GPU ended Dictum while it loaded this model
        var asked = new ConcurrentQueue<string>();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            asked.Enqueue(s.Gpu);
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(model, new WhisperSettings()); // whisper.gpu = auto
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.True(models.IsReady);
        Assert.Equal(new[] { "cpu" }, asked.ToArray());
        Assert.True(models.RunsOnCpu);
        Assert.Equal(ModelFailure.GpuFailedLastTime, models.Notice);
        Assert.Equal(model, Read(local.SpeechMarker)); // kept: the next start goes to the CPU too, so starts do not alternate

        models.BeginLoad(model, new WhisperSettings()); // the same model again in this run (a settings reload)
        Assert.True(await Eventually(() => asked.Count == 2 && models.IsReady));
        Assert.Equal(new[] { "cpu", "cpu" }, asked.ToArray());
    }

    [Fact]
    public async Task The_speech_marker_names_the_model_during_a_GPU_attempt_and_is_gone_after_it()
    {
        using var local = new LocalFolder();
        var model = local.Model("speech-a.bin");
        var seen = new ConcurrentQueue<(string Gpu, string? Marker)>();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            seen.Enqueue((s.Gpu, Read(local.SpeechMarker)));
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(model, new WhisperSettings());
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.Equal(new[] { ("auto", (string?)Path.GetFullPath(model)) }, seen.ToArray());
        Assert.False(File.Exists(local.SpeechMarker)); // a load that succeeded leaves no marker

        // A GPU attempt that fails: its marker goes before the retry on the CPU, and none is left after.
        var retried = new ConcurrentQueue<(string Gpu, string? Marker)>();
        using var failing = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            retried.Enqueue((s.Gpu, Read(local.SpeechMarker)));
            return s.Gpu == "cpu" ? new FakeSpeechEngine(p, d) : throw new InvalidOperationException("Failed to load the whisper model.");
        }, gpuInUse: () => true);
        failing.BeginLoad(model, new WhisperSettings());
        Assert.True(await Eventually(() => failing.IsReady || failing.LoadFailed));
        Assert.Equal(new[] { ("auto", (string?)Path.GetFullPath(model)), ("cpu", null) }, retried.ToArray());
        Assert.False(File.Exists(local.SpeechMarker));
    }

    [Fact]
    public async Task A_speech_marker_naming_another_model_leaves_this_one_to_try_the_GPU()
    {
        using var local = new LocalFolder();
        File.WriteAllText(local.SpeechMarker, local.Model("speech-other.bin"));
        var asked = new ConcurrentQueue<string>();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            asked.Enqueue(s.Gpu);
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(local.Model("speech-a.bin"), new WhisperSettings());
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.Equal(new[] { "auto" }, asked.ToArray());
        Assert.False(models.RunsOnCpu);
        Assert.Null(models.Notice);
        Assert.False(File.Exists(local.SpeechMarker)); // replaced by this model's GPU load, which succeeded
    }

    [Fact]
    public async Task A_start_whose_marker_names_the_clean_up_model_asks_the_loader_for_the_CPU_only_and_keeps_the_marker()
    {
        using var local = new LocalFolder();
        var model = local.Model("cleanup-a.gguf");
        File.WriteAllText(local.CleanupMarker, model);
        var asked = new ConcurrentQueue<string>();
        using var llm = new LocalLlmManager(NullLog.Instance, local.Integrity, (p, gpu) =>
        {
            asked.Enqueue(gpu);
            return new FakeLocalEngine(p);
        }, gpuInUse: () => true);
        llm.BeginLoad(model, "auto");
        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.NotNull(llm.Ready);
        Assert.Equal(new[] { "cpu" }, asked.ToArray());
        Assert.True(llm.RunsOnCpu);
        Assert.Equal(ModelFailure.GpuFailedLastTime, llm.Notice);
        Assert.Equal(model, Read(local.CleanupMarker));
    }

    [Fact]
    public async Task The_clean_up_marker_names_the_model_during_a_GPU_attempt_and_is_gone_after_it()
    {
        using var local = new LocalFolder();
        var model = local.Model("cleanup-a.gguf");
        var seen = new ConcurrentQueue<(string Gpu, string? Marker)>();
        using var llm = new LocalLlmManager(NullLog.Instance, local.Integrity, (p, gpu) =>
        {
            seen.Enqueue((gpu, Read(local.CleanupMarker)));
            return new FakeLocalEngine(p);
        }, gpuInUse: () => true);
        llm.BeginLoad(model, "auto");
        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.NotNull(llm.Ready);
        Assert.Equal(new[] { ("auto", (string?)Path.GetFullPath(model)) }, seen.ToArray());
        Assert.False(File.Exists(local.CleanupMarker));
    }

    [Fact]
    public async Task A_clean_up_marker_naming_another_model_leaves_this_one_to_try_the_GPU()
    {
        using var local = new LocalFolder();
        File.WriteAllText(local.CleanupMarker, local.Model("cleanup-other.gguf"));
        var asked = new ConcurrentQueue<string>();
        using var llm = new LocalLlmManager(NullLog.Instance, local.Integrity, (p, gpu) =>
        {
            asked.Enqueue(gpu);
            return new FakeLocalEngine(p);
        }, gpuInUse: () => true);
        llm.BeginLoad(local.Model("cleanup-a.gguf"), "auto");
        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.Equal(new[] { "auto" }, asked.ToArray());
        Assert.False(llm.RunsOnCpu);
        Assert.Null(llm.Notice);
    }

    [Fact]
    public async Task Quitting_during_a_GPU_attempt_removes_its_marker_but_never_one_read_at_the_start()
    {
        using var local = new LocalFolder();
        var model = local.Model("speech-a.bin");
        using var release = new ManualResetEventSlim();
        var models = new ModelManager(NullLog.Instance, local.Integrity, (p, _, d) =>
        {
            release.Wait(TimeSpan.FromSeconds(10)); // a GPU load still under way at quit
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(model, new WhisperSettings());
        Assert.True(await Eventually(() => File.Exists(local.SpeechMarker)), "the GPU attempt wrote no marker");
        models.Dispose();
        Assert.False(File.Exists(local.SpeechMarker)); // quitting is not the GPU ending Dictum
        release.Set();

        File.WriteAllText(local.SpeechMarker, model); // the GPU ended Dictum last time
        var onCpu = new ModelManager(NullLog.Instance, local.Integrity, (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => true);
        onCpu.BeginLoad(model, new WhisperSettings());
        Assert.True(await Eventually(() => onCpu.IsReady));
        onCpu.Dispose();
        Assert.Equal(model, Read(local.SpeechMarker));
    }

    // ---- Lens fixes (17/10/2026): one GPU load at a time, and a hand-set vulkan ----

    [Fact]
    public async Task While_only_the_speech_GPU_load_is_under_way_the_clean_up_marker_is_never_written()
    {
        // Start-up begins both loads at once (AppShell). The clean-up's marker used to be written before its load queued behind
        // whisper's, so a GPU crash during whisper's load left both files, and the next start put the clean-up model on the CPU too.
        using var local = new LocalFolder();
        using var release = new ManualResetEventSlim();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, _, d) =>
        {
            release.Wait(TimeSpan.FromSeconds(10)); // the speech GPU load, still under way
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        var bothAtCleanupLoad = new ConcurrentQueue<bool>();
        using var llm = new LocalLlmManager(NullLog.Instance, local.Integrity, (p, _) =>
        {
            bothAtCleanupLoad.Enqueue(File.Exists(local.SpeechMarker) && File.Exists(local.CleanupMarker));
            Thread.Sleep(200); // long enough for the poll below to see a clean-up marker written during the speech load
            return new FakeLocalEngine(p);
        }, gpuInUse: () => true);
        models.BeginLoad(local.Model("speech-a.bin"), new WhisperSettings());
        Assert.True(await Eventually(() => File.Exists(local.SpeechMarker)), "the speech GPU load wrote no marker");
        llm.BeginLoad(local.Model("cleanup-a.gguf"), "auto");
        var bothSeen = await Eventually(() => File.Exists(local.SpeechMarker) && File.Exists(local.CleanupMarker), 500); // only the speech load is under way
        release.Set();
        Assert.True(await Eventually(() => models.IsReady && !llm.IsLoading), "the loads never finished");
        Assert.False(bothSeen, "both markers existed while only the speech GPU load was under way");
        Assert.Equal(new[] { false }, bothAtCleanupLoad.ToArray()); // the clean-up's GPU load ran after the speech load's ended
        Assert.False(File.Exists(local.SpeechMarker));
        Assert.False(File.Exists(local.CleanupMarker));
    }

    [Fact]
    public async Task A_hand_set_vulkan_tries_the_GPU_even_when_the_marker_names_the_model()
    {
        using var local = new LocalFolder();
        var speech = local.Model("speech-a.bin");
        var cleanup = local.Model("cleanup-a.gguf");
        File.WriteAllText(local.SpeechMarker, speech);
        File.WriteAllText(local.CleanupMarker, cleanup);
        var asked = new ConcurrentQueue<string>();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            asked.Enqueue(s.Gpu);
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(speech, new WhisperSettings { Gpu = "vulkan" });
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.Equal(new[] { "vulkan" }, asked.ToArray());
        Assert.False(models.RunsOnCpu);
        Assert.Null(models.Notice);

        var askedLlm = new ConcurrentQueue<string>();
        using var llm = new LocalLlmManager(NullLog.Instance, local.Integrity, (p, gpu) =>
        {
            askedLlm.Enqueue(gpu);
            return new FakeLocalEngine(p);
        }, gpuInUse: () => true);
        llm.BeginLoad(cleanup, "vulkan");
        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.Equal(new[] { "vulkan" }, askedLlm.ToArray());
        Assert.False(llm.RunsOnCpu);
        Assert.Null(llm.Notice);
    }

    // ---- Third lens on 3f86820 (17/10/2026): each half of the auto-only rule pinned on its own, and a stale warm-up ----

    [Fact]
    public async Task A_marker_read_under_auto_stops_applying_once_the_setting_is_vulkan()
    {
        // Pins CpuForThisRun's return (gpuSetting == "auto" && _cpuModel …): without it the model read under auto stays on the CPU.
        using var local = new LocalFolder();
        var speech = local.Model("speech-a.bin");
        File.WriteAllText(local.SpeechMarker, speech);
        var asked = new ConcurrentQueue<string>();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            asked.Enqueue(s.Gpu);
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(speech, new WhisperSettings()); // auto: the marker names it, so the CPU
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.Equal(ModelFailure.GpuFailedLastTime, models.Notice);
        models.BeginLoad(speech, new WhisperSettings { Gpu = "vulkan" });
        Assert.True(await Eventually(() => asked.Count == 2 && (models.IsReady || models.LoadFailed)));
        Assert.Equal(new[] { "cpu", "vulkan" }, asked.ToArray());
        Assert.False(models.RunsOnCpu);
        Assert.Null(models.Notice);
    }

    [Fact]
    public async Task A_marker_seen_first_under_vulkan_never_puts_a_later_auto_load_on_the_CPU()
    {
        // Pins the first read's condition (gpuSetting == "auto" && Read() …): without it a vulkan start records the model for the CPU.
        using var local = new LocalFolder();
        var speech = local.Model("speech-a.bin");
        File.WriteAllText(local.SpeechMarker, speech);
        var asked = new ConcurrentQueue<string>();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, s, d) =>
        {
            asked.Enqueue(s.Gpu);
            return new FakeSpeechEngine(p, d);
        }, gpuInUse: () => true);
        models.BeginLoad(speech, new WhisperSettings { Gpu = "vulkan" });
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        models.BeginLoad(speech, new WhisperSettings()); // auto
        Assert.True(await Eventually(() => asked.Count == 2 && (models.IsReady || models.LoadFailed)));
        Assert.Equal(new[] { "vulkan", "auto" }, asked.ToArray());
        Assert.False(models.RunsOnCpu);
        Assert.Null(models.Notice);
    }

    [Fact]
    public async Task Unload_during_a_clean_up_GPU_warm_up_gives_up_the_gate_at_once()
    {
        // The fake warm-up waits 5 s for its token. Before the fix only quit cancelled it, so a warm-up Unload had made stale kept
        // the shared GPU gate, and the speech model's GPU load waited behind it.
        using var local = new LocalFolder();
        FakeLocalEngine? warming = null;
        using var llm = new LocalLlmManager(NullLog.Instance, local.Integrity, (p, _) => warming = new FakeLocalEngine(p, waitForCancel: true),
            gpuInUse: () => true);
        llm.BeginLoad(local.Model("cleanup-a.gguf"), "auto");
        Assert.True(await Eventually(() => warming is not null && File.Exists(local.CleanupMarker)), "the clean-up GPU load never began");
        llm.Unload();
        using var models = new ModelManager(NullLog.Instance, local.Integrity, (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => true);
        models.BeginLoad(local.Model("speech-a.bin"), new WhisperSettings());
        Assert.True(await Eventually(() => models.IsReady, 2000), "the speech GPU load still waited behind the stale clean-up warm-up");
        Assert.False(File.Exists(local.CleanupMarker));
        Assert.True(warming!.Disposed);
        Assert.Null(llm.Ready);
    }

    [Fact]
    public void Off_Windows_the_runtime_check_is_true_even_when_the_probes_find_nothing()
    {
        string[] none = [];
        var asked = false;
        Assert.True(ModelFailure.VisualCppRuntimePresent(_ => null, () => { asked = true; return none; }, _ => null, isWindows: false));
        Assert.False(asked); // nothing is probed off Windows
        Assert.False(ModelFailure.VisualCppRuntimePresent(_ => null, () => none, _ => null, isWindows: true));
    }

    // ---- Finding 2: Try on the CPU only where the CPU has not been tried ----

    [Fact]
    public async Task When_the_clean_up_GPU_and_CPU_attempts_both_fail_the_CPU_error_stands_and_the_CPU_is_not_offered()
    {
        var log = new RecordingLog();
        using var llm = new LocalLlmManager(log, loader: (_, gpu) =>
            throw new InvalidOperationException(gpu == "cpu" ? "out of memory" : "Failed to load model"), gpuInUse: () => true);
        llm.BeginLoad("qwen2.5-1.5b-instruct-q4_k_m.gguf", "auto");
        Assert.True(await Eventually(() => !llm.IsLoading));
        Assert.Equal("out of memory", llm.LastError);
        Assert.False(llm.CpuMightHelp);
        Assert.False(llm.RestartForCpu);
        // It failed while the GPU was in use; whether the GPU caused it is not known.
        Assert.Single(log.Lines, l => l.Message.Contains("failed while the GPU was in use", StringComparison.Ordinal));
    }

    /// <summary>
    /// A start that finds the marker naming the speech model, inside a GPU runtime set before it (the first-run probe, say), where
    /// the load on the CPU fails: the CPU-only runtime has not been tried, so Try on the CPU is offered.
    /// </summary>
    private static (AppShell Shell, ConcurrentQueue<string> Asked) MarkerStartThatFails(Scratch scratch, string model)
    {
        Directory.CreateDirectory(scratch.Paths.LocalDir);
        File.WriteAllText(scratch.Paths.SpeechGpuMarkerFile, model);
        var asked = new ConcurrentQueue<string>();
        var models = new ModelManager(NullLog.Instance, new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance), (_, s, _) =>
        {
            asked.Enqueue(s.Gpu);
            throw new InvalidOperationException("out of memory");
        }, gpuInUse: () => true);
        var (shell, _, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.SpeechError is not null), "the load never failed");
        return (shell, asked);
    }

    [Fact]
    public Task A_start_after_the_GPU_ended_Dictum_offers_Try_on_the_CPU_once() => UiSession.Run(() =>
    {
        var (scratch, model) = SpeechScratch();
        using var _scratch = scratch;
        var (shell, asked) = MarkerStartThatFails(scratch, model);
        Assert.Equal(new[] { "cpu" }, asked.ToArray()); // the marker: no GPU attempt
        Assert.Equal("Speech model: out of memory.", shell.Status.Problem);
        Assert.True(shell.Status.TryCpu);

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        VisibleButton(flyout, AppShell.TryOnCpuLabel)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(EventuallyOnUi(() => VisibleTexts(flyout).Contains(AppShell.RestartForCpuHint, StringComparison.Ordinal)), "the panel never asked for a restart");
        Assert.Equal(new[] { "cpu", "cpu" }, asked.ToArray()); // the reload asked for the CPU at once
        Assert.Equal("cpu", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Whisper.Gpu);
        Assert.Equal($"Speech model: out of memory. {AppShell.RestartForCpuHint}", shell.Status.Problem);
        Assert.False(shell.Status.TryCpu); // offered once
        Assert.Null(VisibleButton(flyout, AppShell.TryOnCpuLabel));
        flyout.Close();
        shell.Dispose();
    });

    // ---- Finding 3: About's line for text that is not English ----

    [Fact]
    public Task About_keeps_the_plain_clean_ups_own_line_when_the_local_model_failed_for_text_that_is_not_English() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "language": "hi" }, "llm": { "provider": "local", "model": "qwen2.5-0.5b" } }""");
        Directory.CreateDirectory(scratch.Paths.DefaultModelsDir);
        var file = CleanupCatalog.Find("qwen2.5-0.5b")!.FileName;
        File.WriteAllBytes(Path.Combine(scratch.Paths.DefaultModelsDir, file), [1, 2, 3, 4]); // fails its size check
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.CleanupError is not null), "the clean-up load never failed");
        Assert.True(shell.Cleaner.IsPlain, "the cleaner in use is not the plain one");
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        var texts = VisibleTexts(window);
        window.Close();
        shell.Dispose();
        Assert.Contains("whitespace tidied only", texts); // SPEC §23's plain cleaner, the one in use
        Assert.DoesNotContain(AboutPage.CleanupFailedLine("qwen2.5-0.5b"), texts);
    });

    // ---- Finding 4: behaviours the lens found unpinned ----

    [Fact]
    public async Task A_later_model_that_loads_on_the_GPU_clears_the_GPU_failed_notice()
    {
        using var models = new ModelManager(NullLog.Instance, loader: (p, s, d) =>
            s.Gpu == "cpu" || p.Contains("good", StringComparison.Ordinal) ? new FakeSpeechEngine(p, d) : throw new InvalidOperationException("Failed to load the whisper model."),
            gpuInUse: () => true);
        models.BeginLoad("bad.bin", new WhisperSettings());
        Assert.True(await Eventually(() => models.IsReady || models.LoadFailed));
        Assert.Equal(ModelFailure.GpuFailed, models.Notice);
        models.BeginLoad("good.bin", new WhisperSettings());
        Assert.True(await Eventually(() => !models.IsLoading && models.IsReady));
        Assert.Null(models.Notice);
        Assert.False(models.RunsOnCpu);
    }

    [Fact]
    public Task About_offers_Try_on_the_CPU_when_the_tray_panel_does() => UiSession.Run(() =>
    {
        var (scratch, model) = SpeechScratch();
        using var _scratch = scratch;
        var (shell, _) = MarkerStartThatFails(scratch, model);
        Assert.True(shell.Status.TryCpu);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Assert.NotNull(VisibleButton(window, AppShell.TryOnCpuLabel));
        Assert.Contains("Speech model: out of memory.", VisibleTexts(window));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_clean_up_label_says_CPU_after_the_retry_on_the_CPU() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var speech = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        var cleanup = Path.Combine(scratch.Root.FullName, "fake-cleanup.gguf");
        File.WriteAllText(speech, "");
        File.WriteAllText(cleanup, "");
        File.WriteAllText(scratch.Paths.SettingsFile,
            $$"""{ "hotkey": "F24", "whisper": { "model": {{JsonSerializer.Serialize(speech)}} }, "llm": { "provider": "local", "model": {{JsonSerializer.Serialize(cleanup)}} } }""");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var llm = new LocalLlmManager(NullLog.Instance, loader: (p, gpu) =>
            gpu == "cpu" ? new FakeLocalEngine(p) : throw new InvalidOperationException("Failed to load model"), gpuInUse: () => true);
        var (shell, _, _) = scratch.Shell(models, cleanupModels: llm);
        shell.Reload(userRequested: false);
        Assert.True(EventuallyOnUi(() => shell.CleanupReady && shell.SpeechReady), "the clean-up model was not loaded on the CPU");
        Assert.Contains($"{CleanupCatalog.DisplayName(cleanup)} · CPU", shell.Status.Models, StringComparison.Ordinal);
        Assert.Equal($"Clean-up model: {ModelFailure.GpuFailed}.", shell.Status.Problem);
        shell.Dispose();
    });

    [Fact]
    public void Op_offload_is_turned_off_only_on_the_CPU()
    {
        Assert.Equal((0, (bool?)false), LocalLlmEngine.Parameters("cpu"));
        Assert.Equal((999, (bool?)null), LocalLlmEngine.Parameters("auto")); // llama.cpp's own op offload
        Assert.Equal((999, (bool?)null), LocalLlmEngine.Parameters("vulkan"));
    }

    [Fact]
    public void The_clean_up_backend_probe_never_loads_whispers_runtime_against_whisper_gpu_cpu()
    {
        Assert.False(WhisperEngine.ProbeAllowed(null)); // no speech load asked for yet
        Assert.False(WhisperEngine.ProbeAllowed("cpu"));
        Assert.True(WhisperEngine.ProbeAllowed("auto"));
        Assert.True(WhisperEngine.ProbeAllowed("vulkan"));
    }

    // ---- Finding 6: keyboard focus after Try on the CPU ----

    private static string Describe(IInputElement? focused) =>
        focused is null ? "nothing" : $"{focused.GetType().Name} '{(focused as ContentControl)?.Content ?? (focused as TextBlock)?.Text}' visible={(focused as Visual)?.IsEffectivelyVisible}";

    [Fact]
    public Task Keyboard_focus_moves_to_the_tray_panels_first_control_when_Try_on_the_CPU_hides() => UiSession.Run(() =>
    {
        var (scratch, model) = SpeechScratch();
        using var _scratch = scratch;
        var (shell, _) = MarkerStartThatFails(scratch, model);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var tryCpu = VisibleButton(flyout, AppShell.TryOnCpuLabel)!;
        Assert.True(tryCpu.Focus(NavigationMethod.Tab), "the button took no focus");
        var focus = TopLevel.GetTopLevel(flyout)!.FocusManager!;
        tryCpu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(EventuallyOnUi(() => VisibleTexts(flyout).Contains(AppShell.RestartForCpuHint, StringComparison.Ordinal)), "no restart hint");
        var now = focus.GetFocusedElement();
        var where = Describe(now);
        var name = now is Control control ? AutomationProperties.GetName(control) : null;
        var ring = now is Control shown && shown.Classes.Contains(":focus-visible");
        flyout.Close();
        shell.Dispose();
        Assert.True(now is Visual { IsEffectivelyVisible: true }, $"focus after Try on the CPU: {where}");
        Assert.Equal("Dictation on", name);
        Assert.True(ring, "the focus ring is not shown");
    });

    [Fact]
    public Task Keyboard_focus_moves_to_the_next_control_in_About_when_Try_on_the_CPU_hides() => UiSession.Run(() =>
    {
        var (scratch, model) = SpeechScratch();
        using var _scratch = scratch;
        var (shell, _) = MarkerStartThatFails(scratch, model);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        window.Activate();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // lays the page out, so its controls can take focus
        var tryCpu = VisibleButton(window, AppShell.TryOnCpuLabel)!;
        Assert.True(tryCpu.Focus(NavigationMethod.Tab), "the button took no focus");
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        tryCpu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(EventuallyOnUi(() => VisibleTexts(window).Contains(AppShell.RestartForCpuHint, StringComparison.Ordinal)), "no restart hint");
        var now = focus.GetFocusedElement();
        var where = Describe(now);
        var ring = now is Control control && control.Classes.Contains(":focus-visible");
        window.Close();
        shell.Dispose();
        // Where Tab goes from it: Check for updates in the download build (the Store build: Report a problem).
        Assert.True(now is Button { IsEffectivelyVisible: true, Content: UpdateCheck.ButtonLabel }, $"focus after Try on the CPU: {where}");
        Assert.True(ring, "the focus ring is not shown");
    });

    // ---- Finding 8: the Visual C++ runtime's version floor ----

    [Fact]
    public void An_msvcp140_older_than_the_build_scripts_floor_counts_as_missing()
    {
        string[] none = [];
        static string Loaded(string dll) => $@"C:\Windows\System32\{dll}";
        Assert.False(ModelFailure.VisualCppRuntimePresent(Loaded, none, _ => new Version(14, 44)));
        Assert.True(ModelFailure.VisualCppRuntimePresent(Loaded, none, _ => new Version(14, 51)));
        Assert.True(ModelFailure.VisualCppRuntimePresent(Loaded, none, _ => new Version(14, 52)));
        Assert.True(ModelFailure.VisualCppRuntimePresent(Loaded, none, path => path.EndsWith("msvcp140.dll", StringComparison.Ordinal) ? new Version(14, 51) : new Version(14, 0)));

        var folder = Directory.CreateTempSubdirectory("dictum-vcrt-");
        try
        {
            foreach (var dll in ModelFailure.RuntimeDlls) File.WriteAllBytes(Path.Combine(folder.FullName, dll), [0]);
            Assert.False(ModelFailure.VisualCppRuntimePresent(_ => null, [folder.FullName], _ => new Version(14, 44))); // an old copy beside the engines
            Assert.True(ModelFailure.VisualCppRuntimePresent(_ => null, [folder.FullName], _ => new Version(14, 51)));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_runtime_check_uses_the_build_scripts_floor_and_DLLs()
    {
        // scripts/build-common.ps1's $MinVcRuntime and $VcRuntimeDlls, read as the VC++ runtime tests read them.
        Assert.Equal(VcRuntimeLayout.MinVcRuntime, ModelFailure.MinRuntime);
        Assert.True(VcRuntimeLayout.Shipped.SetEquals(ModelFailure.RuntimeDlls), $"build-common.ps1 ships {string.Join(", ", VcRuntimeLayout.Shipped)}");
    }

    // ---- Finding 9: the load balloon ----

    [Fact]
    public void The_load_balloon_names_the_model_page_except_for_a_missing_runtime()
    {
        Assert.Equal("bad file (Speech model: Settings > Models)", AppShell.LoadBalloon("bad file", "Speech model", bothShown: false));
        Assert.Equal("bad file (Clean-up model: Settings > Models)", AppShell.LoadBalloon("bad file", "Clean-up model", bothShown: true));
        Assert.Equal(ModelFailure.RuntimeMissing, AppShell.LoadBalloon(ModelFailure.RuntimeMissing, "Speech model", bothShown: false));
        Assert.Null(AppShell.LoadBalloon(ModelFailure.RuntimeMissing, "Clean-up model", bothShown: true));
    }

    [Fact]
    public Task A_missing_runtime_is_ballooned_once_for_both_models() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var speech = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        var cleanup = Path.Combine(scratch.Root.FullName, "fake-cleanup.gguf");
        File.WriteAllText(speech, "");
        File.WriteAllText(cleanup, "");
        File.WriteAllText(scratch.Paths.SettingsFile,
            $$"""{ "hotkey": "F24", "whisper": { "model": {{JsonSerializer.Serialize(speech)}} }, "llm": { "provider": "local", "model": {{JsonSerializer.Serialize(cleanup)}} } }""");
        var models = new ModelManager(NullLog.Instance, loader: (_, _, _) => throw new Exception(WhisperNativeFailure), gpuInUse: () => false, visualCppRuntime: () => false);
        var llm = new LocalLlmManager(NullLog.Instance, loader: (_, _) => throw new TypeInitializationException("LLama.Native.NativeApi", null),
            gpuInUse: () => false, visualCppRuntime: () => false);
        var log = new RecordingLog();
        var (shell, _, _) = scratch.Shell(models, log: log, cleanupModels: llm);
        shell.Reload(userRequested: false);
        List<string> Notices() => [.. log.Lines.Select(l => l.Message).Where(m => m.StartsWith("Notice: ", StringComparison.Ordinal))];
        Assert.True(EventuallyOnUi(() => shell.SpeechError is not null && shell.CleanupError is not null && Notices().Count > 0), "no balloon");
        EventuallyOnUi(() => false, 300); // let a second balloon, if any, arrive
        Assert.Equal(ModelFailure.RuntimeMissing, shell.SpeechError);
        Assert.Equal(ModelFailure.RuntimeMissing, shell.CleanupError);
        Assert.Equal([$"Notice: {ModelFailure.RuntimeMissing}"], Notices());
        shell.Dispose();
    });
}
