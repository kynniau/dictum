using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.App;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Decisions 15 and 16 (17/10/2026): models.folder, the lookup order over the chosen, per-user and bundled folders, the
/// bundled folder that is never written, the free-space refusal, the network-folder warning, the first run with the base.en
/// that ships beside Dictum.exe, and Settings > Models and Storage. Model files of the catalogue's exact size are made with
/// SetLength (no bytes are written), so the lookup's size check passes without a real model.
/// </summary>
public sealed class ModelFolderTests : IDisposable
{
    private static readonly long BaseEnBytes = ModelCatalog.Find("base.en")!.Bytes;
    private static readonly long TinyEnBytes = ModelCatalog.Find("tiny.en")!.Bytes;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-model-folder-");
    private readonly AppPaths _paths;
    private readonly string _chosen;
    private readonly ScriptedPrompts _prompts = new();
    private readonly List<string> _balloons = [];
    private AppSettings _settings = new();

    public ModelFolderTests()
    {
        _paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        _chosen = Path.Combine(_dir.FullName, "D-drive models");
        Directory.CreateDirectory(_paths.ConfigDir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A file of <paramref name="bytes"/> bytes (zeros) in <paramref name="folder"/>.</summary>
    private static string Create(string folder, string fileName, long bytes)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(bytes);
        return path;
    }

    private ModelCoordinator Make(ModelIntegrity? integrity = null, Func<string, long?>? free = null, HttpMessageHandler? server = null, ILog? log = null,
        ModelManager? models = null, ScriptedPrompts? prompts = null, AppPaths? paths = null, Func<AppSettings>? settings = null) =>
        new(paths ?? _paths, models ?? new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false),
            new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p)), new HttpClient(server ?? new CountingHandler()), log ?? NullLog.Instance,
            settings ?? (() => _settings), null, prompts ?? _prompts, _ => null, reload: () => _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings,
            shutdown: () => { }, refreshTooltip: () => { }, balloon: (text, _) => _balloons.Add(text), integrity: integrity, gpuMachine: () => false,
            freeBytes: free ?? (_ => 100_000_000_000));

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

    // ---- The setting ----

    [Theory]
    [InlineData("""{ "models": { "folder": "D:\\Dictum models" } }""", @"D:\Dictum models", 0)]
    [InlineData("""{ "models": { "folder": "\\\\fileserver\\dictum\\models" } }""", @"\\fileserver\dictum\models", 0)] // a share is allowed (with the warning)
    [InlineData("""{ "models": { "folder": "" } }""", null, 0)] // empty: the default
    [InlineData("""{ "models": { "folder": null } }""", null, 0)]
    [InlineData("""{ "models": { "folder": "models" } }""", null, 1)] // relative: the default, with a warning
    [InlineData("""{ "models": { "folder": "C:\\models|old" } }""", null, 1)] // not a valid path
    [InlineData("""{ "models": { "folder": 5 } }""", null, 1)]
    [InlineData("""{ "models": { "folder": "D:\\m", "foldr": "x" } }""", @"D:\m", 1)] // an unknown key in the section
    public void Models_folder_is_an_absolute_folder_or_empty_and_anything_else_falls_back_with_a_warning(string json, string? folder, int warnings)
    {
        var load = SettingsLoader.Parse(json);
        Assert.Equal(folder, load.Settings.Models.Folder);
        Assert.Equal(warnings, load.Warnings.Count);
        if (warnings == 1 && folder is null) Assert.Contains("\"models.folder\"", Assert.Single(load.Warnings));
    }

    [Fact]
    public void The_default_file_documents_models_folder_and_reads_back_without_warnings()
    {
        var json = SettingsFile.DefaultJson("base.en");
        var root = JsonNode.Parse(json)!;
        Assert.True(root["models"]!.AsObject().ContainsKey("folder"));
        Assert.Null(root["models"]!["folder"]);
        Assert.False(root["whisper"]!.AsObject().ContainsKey("models_dir")); // one key for the folder in a new file
        Assert.Contains("never changed", root["_doc"]!["models"]!.GetValue<string>());
        Assert.Contains("Changing it moves no files", root["_doc"]!["models"]!.GetValue<string>());
        Assert.Empty(SettingsLoader.Parse(json).Warnings);
    }

    [Fact]
    public void Whisper_models_dir_is_still_read_as_the_earlier_name_and_models_folder_wins_over_it()
    {
        var earlier = SettingsLoader.Parse("""{ "whisper": { "models_dir": "E:\\m" } }""");
        Assert.Empty(earlier.Warnings);
        Assert.Equal(@"E:\m", _paths.ModelsDir(earlier.Settings));

        var both = SettingsLoader.Parse("""{ "whisper": { "models_dir": "E:\\m" }, "models": { "folder": "F:\\n" } }""");
        Assert.Equal(@"F:\n", _paths.ModelsDir(both.Settings));
        Assert.Equal("Setting \"whisper.models_dir\" is ignored: \"models.folder\" chooses the models folder.", Assert.Single(both.Warnings));
        Assert.Empty(SettingsLoader.Parse("""{ "whisper": { "models_dir": "F:\\n" }, "models": { "folder": "f:\\N" } }""").Warnings); // the same folder
    }

    // ---- The lookup ----

    /// <summary>Pin: the chosen folder wins over the per-user folder, which wins over the bundled folder; both kinds use the one lookup.</summary>
    [Fact]
    public void A_model_in_the_chosen_folder_wins_over_the_per_user_folder_which_wins_over_the_bundled_folder()
    {
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = "tiny.en" }, Models = new ModelsSettings { Folder = _chosen } };
        string Speech() => SettingsFile.EffectiveModelPath(settings, _paths, _ => null);
        var chosen = Create(_chosen, "ggml-tiny.en.bin", TinyEnBytes);
        var own = Create(_paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        var bundled = Create(_paths.BundledModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Assert.Equal(
            [new ModelFolder(_chosen, ModelFolderKind.Chosen), new ModelFolder(_paths.DefaultModelsDir, ModelFolderKind.Default), new ModelFolder(_paths.BundledModelsDir, ModelFolderKind.Bundled)],
            ModelFolders.LookupOrder(_paths, settings));

        Assert.Equal(chosen, Speech());
        File.Delete(chosen);
        Assert.Equal(own, Speech());
        File.Delete(own);
        Assert.Equal(bundled, Speech());
        File.Delete(bundled);
        Assert.Equal(chosen, Speech()); // on no folder: where a download puts it, the chosen folder

        Create(_chosen, "ggml-tiny.en.bin", 1000); // a copy that fails the size check is passed over
        own = Create(_paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Assert.Equal(own, Speech());
        Assert.Equal(own, SettingsFile.EffectiveModelPath(settings, _paths, _ => null, launchOverride: "tiny.en")); // --model looks the same way

        // The clean-up kind goes through the same lookup: the bundled folder's copy, with a wrong-size one in the user's folder passed over.
        var cleanup = settings with { Llm = new LlmSettings { Model = "qwen2.5-0.5b" } };
        var bundledCleanup = Create(_paths.BundledModelsDir, "qwen2.5-0.5b-instruct-q4_k_m.gguf", CleanupCatalog.Find("qwen2.5-0.5b")!.Bytes);
        Create(_paths.DefaultModelsDir, "qwen2.5-0.5b-instruct-q4_k_m.gguf", 500);
        Assert.Equal(bundledCleanup, SettingsFile.CleanupModelPath(cleanup, _paths));
    }

    /// <summary>Pin: the bundled folder is never a download destination, even when it (or a folder inside it) is chosen.</summary>
    [Fact]
    public async Task A_download_never_lands_in_the_bundled_folder_even_when_that_folder_is_chosen()
    {
        _settings = new AppSettings { Models = new ModelsSettings { Folder = _paths.BundledModelsDir } };
        Assert.Null(_paths.ChosenModelsDir(_settings));
        Assert.Equal(_paths.DefaultModelsDir, _paths.ModelsDir(_settings));
        Assert.Equal(_paths.DefaultModelsDir, _paths.ModelsDir(new AppSettings { Models = new ModelsSettings { Folder = Path.Combine(_paths.BundledModelsDir, "more") } }));
        Assert.Equal(_paths.DefaultModelsDir, _paths.ModelsDir(new AppSettings { Whisper = new WhisperSettings { ModelsDir = _paths.BundledModelsDir } }));

        var log = new RecordingLog();
        var server = new CountingHandler(); // answers 403: the download fails at once, after its destination is set
        _prompts.Confirm = true;
        var coordinator = Make(server: server, log: log);
        coordinator.Select("tiny.en");
        Assert.True(await Eventually(() => server.Requests == 1 && !coordinator.IsDownloading));
        Assert.Contains(log.Lines, l => l.Message.EndsWith($" to {Path.Combine(_paths.DefaultModelsDir, "ggml-tiny.en.bin")}.", StringComparison.Ordinal));
        Assert.False(Directory.Exists(_paths.BundledModelsDir)); // nothing was ever made there, not even a .part

        Assert.Equal(ModelCoordinator.BundledFolderRefused, coordinator.SetModelsFolder(_paths.BundledModelsDir));
        Assert.Equal(ModelCoordinator.BundledFolderRefused, coordinator.SetModelsFolder(Path.Combine(_paths.BundledModelsDir, "more")));
        Assert.False(File.Exists(_paths.SettingsFile)); // a refused folder writes nothing
    }

    /// <summary>Pin: a bundled file that fails the catalogue check (size, then SHA-256) is passed over, logged once, never deleted.</summary>
    [Theory]
    [InlineData(1000L)] // the wrong size
    [InlineData(147_964_211L)] // base.en's size with the wrong bytes (zeros): only the SHA-256 tells
    public void A_bundled_file_that_fails_the_catalogue_check_is_passed_over_logged_once_and_never_deleted(long bytes)
    {
        var log = new RecordingLog();
        var bundled = Create(_paths.BundledModelsDir, "ggml-base.en.bin", bytes);
        var coordinator = Make(integrity: new ModelIntegrity(_paths.VerifiedModelsFile, log, () => ModelFolders.LookupOrder(_paths, _settings)), log: log);
        var baseEn = ModelCatalog.Find("base.en")!;

        Assert.False(coordinator.IsDownloaded(baseEn));
        Assert.Null(coordinator.Source(baseEn));
        Assert.False(coordinator.IsDownloaded(baseEn)); // looked up again: not checked or logged again
        Assert.Single(log.Lines, l => l.Message.Contains("came with Dictum but fails the catalogue check", StringComparison.Ordinal));
        Assert.Equal(bytes, new FileInfo(bundled).Length); // still there, untouched

        _prompts.Speech = new ModelPick("base.en", null, false);
        _prompts.Cleanup = new CleanupPick(null, null, true, false);
        Assert.True(coordinator.FirstRun());
        Assert.Equal((1, 1), (_prompts.SpeechPickerCalls, _prompts.CleanupPickerCalls)); // so the first run asks as before
        Assert.True(File.Exists(bundled));
    }

    // ---- The first run ----

    /// <summary>
    /// Pins: with base.en beside Dictum.exe the first run asks no model question and writes base.en and the rule-based tidy,
    /// exactly what the pickers' base.en and "No AI cleanup (rule-based tidy)" write (a new settings.json and a seeded one);
    /// without it the same first run asks both questions, as before.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("""{ "offline_only": true }""")]
    public void First_run_with_the_bundled_base_en_asks_nothing_and_writes_what_the_rule_based_tidy_answer_writes(string? seed)
    {
        if (seed is not null) File.WriteAllText(_paths.SettingsFile, seed);
        var bundled = Create(_paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        var integrity = new ModelIntegrity(_paths.VerifiedModelsFile, NullLog.Instance, () => ModelFolders.LookupOrder(_paths, _settings));
        integrity.Remember(bundled); // as if hashed once already: zeros would fail the SHA-256
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);
        var coordinator = Make(integrity: integrity, models: models);

        Assert.Equal((true, true), AppRules.FirstRunQuestions(SettingsLoader.LoadFile(_paths.SettingsFile)));
        Assert.True(coordinator.FirstRun());
        Assert.Equal((0, 0), (_prompts.SpeechPickerCalls, _prompts.CleanupPickerCalls));
        _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
        Assert.Equal(("base.en", LlmSettings.NoProvider), (_settings.Whisper.Model, _settings.Llm.Provider));
        Assert.Equal(seed is not null, _settings.OfflineOnly); // a seed keeps what it set
        coordinator.Ensure(previousPath: null, startup: true);
        Assert.Equal(bundled, models.ModelPath); // starts with the bundled file: no download, no question
        coordinator.EnsureCleanup();
        Assert.Empty(_balloons); // and no "cleanup model not downloaded" notice: none is configured

        // The same first run without the bundled file asks both questions, as before; answered base.en and the rule-based tidy,
        // it writes the very same file.
        using var other = new Scratch(seed);
        var prompts = new ScriptedPrompts { Speech = new ModelPick("base.en", null, false), Cleanup = new CleanupPick(null, null, true, false) };
        Assert.True(Make(prompts: prompts, paths: other.Paths, settings: () => new AppSettings()).FirstRun());
        Assert.Equal((1, 1), (prompts.SpeechPickerCalls, prompts.CleanupPickerCalls));
        Assert.Equal(File.ReadAllText(other.Paths.SettingsFile), File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void First_run_on_a_seeded_file_with_the_bundled_model_answers_only_what_the_file_leaves_open()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "whisper": { "model": "small.en" } }""");
        Create(_paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        Assert.Equal((false, true), AppRules.FirstRunQuestions(SettingsLoader.LoadFile(_paths.SettingsFile)));
        Assert.True(Make().FirstRun(askSpeech: false, askCleanup: true));
        Assert.Equal((0, 0), (_prompts.SpeechPickerCalls, _prompts.CleanupPickerCalls)); // the clean-up question is no longer asked
        var after = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
        Assert.Equal(("small.en", LlmSettings.NoProvider), (after.Whisper.Model, after.Llm.Provider));
    }

    [Fact]
    public void A_seeded_models_folder_holding_base_en_counts_at_first_run()
    {
        var inChosen = Create(_chosen, "ggml-base.en.bin", BaseEnBytes);
        var seeded = new AppSettings { Models = new ModelsSettings { Folder = _chosen } };
        Assert.True(Make().FirstRun(seeded: seeded)); // the settings in use (defaults) name no folder; the seed does
        Assert.Equal((0, 0), (_prompts.SpeechPickerCalls, _prompts.CleanupPickerCalls));
        Assert.Equal(inChosen, SettingsFile.EffectiveModelPath(SettingsLoader.LoadFile(_paths.SettingsFile).Settings with { Models = seeded.Models }, _paths, _ => null));
    }

    // ---- Free space ----

    [Fact]
    public void The_free_space_rule_is_the_model_plus_200_MB()
    {
        Assert.Equal(200_000_000, DiskSpace.SpareBytes);
        Assert.Equal(
            "Not enough space for ggml-tiny.en.bin (77 MB): 277 MB is free on that drive, and Dictum keeps 200 MB spare. Free some space, or choose another model folder in Settings > Models.",
            DiskSpace.DownloadRefusal(@"C:\m", "ggml-tiny.en.bin", TinyEnBytes, _ => TinyEnBytes + 199_999_999));
        Assert.Null(DiskSpace.DownloadRefusal(@"C:\m", "ggml-tiny.en.bin", TinyEnBytes, _ => TinyEnBytes + 200_000_000));
        Assert.Null(DiskSpace.DownloadRefusal(@"\\fileserver\dictum", "ggml-tiny.en.bin", TinyEnBytes, _ => null)); // unknown free space: no refusal
    }

    /// <summary>Pin: a download that would leave under 200 MB is refused with a plain message before it is asked, at every site.</summary>
    [Fact]
    public async Task A_download_is_refused_before_it_is_asked_when_the_drive_would_keep_under_200_MB()
    {
        var free = TinyEnBytes + DiskSpace.SpareBytes - 1;
        var server = new CountingHandler();
        _prompts.Confirm = true;
        var coordinator = Make(free: _ => free, server: server);
        var refusal = DiskSpace.DownloadRefusal(_paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes, _ => free);

        coordinator.Select("tiny.en"); // tray panel or Settings > Models
        Assert.Equal(0, _prompts.ConfirmCalls);
        Assert.Equal(refusal, Assert.Single(_balloons));

        var finished = new List<bool>();
        coordinator.ApplyWithDownloads("Needed for Light mode", [(ModelCatalog.Find("tiny.en")!.Download, Path.Combine(_paths.DefaultModelsDir, "ggml-tiny.en.bin"))],
            [], finished: finished.Add); // Mode presets and calibration
        Assert.Equal([false], finished);
        Assert.Equal(0, _prompts.ConfirmCalls);
        Assert.Equal(2, _balloons.Count);

        // The first run's download (picked, then started without a second question) is refused where it starts.
        _prompts.Speech = new ModelPick("tiny.en", null, false);
        _prompts.Cleanup = new CleanupPick(null, null, true, false);
        Assert.True(coordinator.FirstRun());
        _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
        coordinator.Ensure(previousPath: null, startup: true);
        Assert.Equal(refusal, _balloons[^1]);
        await Task.Delay(200, Ct);
        Assert.Equal(0, server.Requests); // nothing was fetched
        Assert.False(coordinator.IsDownloading);

        free = TinyEnBytes + DiskSpace.SpareBytes; // exactly enough
        _balloons.Clear();
        _prompts.Confirm = false;
        coordinator.Select("tiny.en");
        Assert.Equal(1, _prompts.ConfirmCalls); // now it is asked
        Assert.Empty(_balloons);
    }

    [Fact]
    public void A_kept_partial_download_needs_only_the_rest()
    {
        Create(_paths.DefaultModelsDir, "ggml-tiny.en.bin.part", TinyEnBytes - 10_000_000); // kept by a download that ran out of retries
        var coordinator = Make(free: _ => 10_000_000 + DiskSpace.SpareBytes);
        coordinator.Select("tiny.en");
        Assert.Equal(1, _prompts.ConfirmCalls);
        Assert.Empty(_balloons);
    }

    // ---- The network warning ----

    /// <summary>Pin: a folder on a share gets the slow-loading line; the UNC strings are read as text, no share is touched.</summary>
    [Theory]
    [InlineData(@"\\fileserver\dictum\models", true)]
    [InlineData(@"//fileserver/dictum/models", true)]
    [InlineData(@"\\?\UNC\fileserver\dictum\models", true)]
    [InlineData(@"\\?\C:\Dictum models", false)]
    [InlineData(@"C:\Dictum models", false)]
    public void A_folder_on_a_share_gets_the_slow_loading_warning(string folder, bool network)
    {
        Assert.Equal(network, DiskSpace.IsNetwork(folder));
        Assert.Equal(network ? "Models load slowly from a network folder; a folder on this PC is faster." : null, ModelsPage.FolderWarning(folder));
    }

    // ---- Settings > Models and Storage ----

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static Button ButtonNamed(ILogical root, string label) => All<Button>(root).First(b => b.Content as string == label);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    /// <summary>Pin: Browse… opens the system's folder chooser, writes models.folder and reloads; Use default clears it; the bundled folder is refused.</summary>
    [Fact]
    public Task Browse_writes_models_folder_and_reloads_and_Use_default_clears_it() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en", "models_dir": null } }""");
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        var inChosen = Create(chosen, "ggml-tiny.en.bin", TinyEnBytes);
        var (shell, _, dialogs) = scratch.Shell(new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false));
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var field = All<TextBox>(window).Single(t => AutomationProperties.GetName(t) == "Model folder");
        var browse = ButtonNamed(window, "Browse…");
        var useDefault = ButtonNamed(window, "Use default");
        Assert.Equal(scratch.Paths.DefaultModelsDir, field.Text);
        Assert.Equal("Browse for a model folder", AutomationProperties.GetName(browse)); // labelled for a screen reader
        Assert.True(browse.Focusable && browse.IsTabStop && field.IsTabStop); // and reached with Tab
        Assert.False(useDefault.IsEnabled); // nothing is chosen yet
        Assert.Contains("Dictum looks here first and downloads go here. Changing it does not move any files.", Texts(window));

        dialogs.Folder = chosen;
        Click(browse);
        Assert.Equal([scratch.Paths.DefaultModelsDir], dialogs.FolderChooserOpenedIn); // the chooser opens in the folder in use
        Assert.Equal(chosen, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder); // written
        Assert.Equal(chosen, shell.Settings.Models.Folder); // and reloaded:
        Assert.Equal(inChosen, shell.Models.ModelPath); // the copy in the chosen folder is the one used now
        Assert.Equal(chosen, field.Text);
        Assert.True(useDefault.IsEnabled);
        Assert.True(File.Exists(inChosen)); // nothing moved

        Click(useDefault);
        Assert.Null(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        Assert.Null(shell.Settings.Models.Folder);
        Assert.Equal(scratch.Paths.DefaultModelsDir, field.Text);

        dialogs.Folder = scratch.Paths.BundledModelsDir;
        Click(browse);
        Assert.Contains(ModelCoordinator.BundledFolderRefused, Texts(window));
        Assert.Null(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);

        dialogs.Folder = null; // the chooser cancelled: nothing changes
        Click(browse);
        Assert.Null(shell.Settings.Models.Folder);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Browse_clears_the_earlier_whisper_models_dir_so_the_folder_chosen_is_the_one_in_force() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var earlier = Path.Combine(scratch.Root.FullName, "seeded by install");
        var chosen = Path.Combine(scratch.Root.FullName, "picked");
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", whisper = new { model = "tiny.en", models_dir = earlier } }));
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Equal(earlier, shell.ModelsDir);
        Assert.Null(shell.Models.SetModelsFolder(chosen));
        var file = SettingsLoader.LoadFile(scratch.Paths.SettingsFile);
        Assert.Equal((chosen, (string?)null), (file.Settings.Models.Folder, file.Settings.Whisper.ModelsDir));
        Assert.Empty(file.Warnings);
        Assert.Equal(chosen, shell.ModelsDir);
        shell.Dispose();
    });

    [Fact]
    public Task The_models_page_offers_AI_clean_up_as_an_explicit_download_while_the_rule_based_tidy_is_in_use() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var server = new CountingHandler();
        var (shell, _, dialogs) = scratch.Shell(downloadHandler: server);
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var add = ButtonNamed(window, "Add AI clean-up (1.1 GB download)");
        Assert.True(add.IsEffectivelyVisible);
        Click(add);
        Assert.Equal(["qwen2.5-1.5b-instruct-q4_k_m.gguf"], dialogs.DownloadQuestions); // the catalogue's download path: asked first
        Assert.Equal(LlmSettings.NoProvider, shell.Settings.Llm.Provider); // declined: nothing changes, nothing is fetched
        Assert.Equal(0, server.Requests);

        shell.Write(("llm.provider", JsonValue.Create(LlmSettings.Local)));
        window.ShowPage("Storage");
        window.ShowPage("Models");
        Assert.False(add.IsVisible); // AI clean-up is on: no offer
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Settings_models_marks_the_bundled_model_as_included() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" } }""");
        Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        var (shell, _, _) = scratch.Shell();
        new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(Path.Combine(scratch.Paths.BundledModelsDir, "ggml-base.en.bin"));
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        Assert.Contains($"Included · {ModelCatalog.Find("base.en")!.TradeOff}", Texts(window));
        Assert.Equal(ModelFolderKind.Bundled, shell.Models.Source(ModelCatalog.Find("base.en")!));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Storage lists the three folders in lookup order and which one each model came from; a bundled model has no Delete.</summary>
    [Fact]
    public Task Storage_lists_the_three_folders_in_lookup_order_and_the_folder_each_model_is_in() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", models = new { folder = chosen } }));
        var small = Create(chosen, "ggml-small.en.bin", 10);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", 10);
        var included = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", 10);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Storage");
        var texts = Texts(window);
        Assert.Contains($"Downloads go to: {chosen}", texts);
        Assert.Contains("Dictum looks for a model in these folders, in this order:", texts);
        Assert.Contains($"1. Chosen folder: {chosen}", texts);
        Assert.Contains($"2. Default folder: {scratch.Paths.DefaultModelsDir}", texts);
        Assert.Contains($"3. Included with Dictum (read-only): {scratch.Paths.BundledModelsDir}", texts);
        Assert.Contains($"Chosen folder · {small}", texts);
        Assert.Contains($"Default folder · {tiny}", texts);
        Assert.Contains($"Included with Dictum (read-only) · {included}", texts);
        var deletes = All<Button>(window).Select(AutomationProperties.GetName).ToList();
        Assert.Contains("Delete small.en", deletes);
        Assert.Contains("Delete tiny.en", deletes);
        Assert.DoesNotContain("Delete base.en", deletes); // came with Dictum: never deleted
        Assert.False(ModelFiles.Delete(included, scratch.Paths, shell.Settings, [], shell.Integrity).Deleted); // nor by the deletion itself
        Assert.True(File.Exists(included));
        window.Close();
        shell.Dispose();
    });

    /// <summary>The Light preset and the calibration test find the bundled base.en like every other lookup: nothing to download.</summary>
    [Fact]
    public Task The_Light_preset_and_calibration_use_the_bundled_base_en_without_a_download() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" } }""");
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled);
        var server = new CountingHandler();
        var (shell, _, dialogs) = scratch.Shell(new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false),
            downloadHandler: server);
        shell.Reload(userRequested: false);

        var calibration = shell.CreateCalibrationHost();
        Assert.Contains(calibration.InstalledModels(), c => c.Name == "base.en" && c.Path == bundled);
        Assert.DoesNotContain(calibration.MissingModels(), m => m.Name == "base.en"); // not offered as "Download and include"

        shell.Personal.SelectPreset(ModePreset.Light); // base.en and the rule-based tidy
        Assert.Empty(dialogs.DownloadQuestions);
        Assert.Equal(("base.en", LlmSettings.NoProvider), (shell.Settings.Whisper.Model, shell.Settings.Llm.Provider));
        Assert.Equal(bundled, shell.Models.ModelPath);
        Assert.Equal(0, server.Requests);
        shell.Dispose();
    });

    /// <summary>
    /// Pin, end to end: a first start with base.en beside Dictum.exe shows no model picker, keeps the hotkey check, writes base.en
    /// and the rule-based tidy, and loads the bundled file with nothing downloaded.
    /// </summary>
    [Fact]
    public Task A_first_start_with_the_bundled_base_en_asks_nothing_about_models_keeps_the_hotkey_check_and_starts_ready() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(); // no settings.json: a first start
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled); // as if hashed once already
        var server = new CountingHandler();
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);
        var (shell, _, dialogs) = scratch.Shell(models, downloadHandler: server);
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 1), dialogs.FirstRunSteps); // no speech picker, no clean-up picker; the hotkey check stays
            var written = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings;
            Assert.Equal(("base.en", LlmSettings.NoProvider), (written.Whisper.Model, written.Llm.Provider));
            Assert.Equal(bundled, models.ModelPath);
            Assert.Equal("rules", shell.Models.CurrentCleanupName);
            Assert.Equal(0, server.Requests);
        }
        finally
        {
            shell.Dispose();
        }
    });

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
