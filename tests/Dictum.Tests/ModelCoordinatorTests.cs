using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.App;

namespace Dictum.Tests;

/// <summary>Tray-mode model flows with the dialogs faked out (no UI, no downloads).</summary>
public sealed class ModelCoordinatorTests : IDisposable
{
    private sealed class FakePrompts : IModelPrompts
    {
        public int MissingCalls { get; private set; }
        public int PickerCalls { get; private set; }

        public ModelPick FirstRunPicker(string recommended, string reason, Func<ModelInfo, bool> isDownloaded, string modelsDir)
        {
            PickerCalls++;
            return new ModelPick(null, null, true);
        }

        public CleanupPick CleanupPicker(string recommended, string reason, Func<CleanupModelInfo, bool> isDownloaded, string modelsDir) =>
            new(null, null, false, true);

        public MissingModelChoice MissingModel(string shortName, bool canDownload)
        {
            MissingCalls++;
            return MissingModelChoice.Quit; // also what Esc/close maps to
        }

        public int ConfirmCalls { get; private set; }

        public bool ConfirmDownload(string fileName, string sizeText, string note)
        {
            ConfirmCalls++;
            return false;
        }

        public string? ChooseFile(string initialDir, bool cleanupModel) => null;
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-models-");
    private readonly FakePrompts _prompts = new();
    private readonly List<string> _balloons = [];
    private readonly ModelManager _models = new(NullLog.Instance);
    private readonly Dictum.Llm.LocalLlmManager _llm = new(NullLog.Instance);
    private readonly CountingHandler _server = new();
    private HttpClient _http => new(_server); // answers 403 and counts: a declined or refused download makes no request
    private int _shutdowns;

    private string SettingsFile => Path.Combine(_dir.FullName, "cfg", "settings.json");

    private ModelCoordinator Make(string model, bool offlineOnly = false)
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), _dir.FullName);
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = model }, OfflineOnly = offlineOnly };
        return new ModelCoordinator(paths, _models, _llm, _http, NullLog.Instance, () => settings, null, _prompts, _ => null,
            reload: () => { }, shutdown: () => _shutdowns++, refreshTooltip: () => { }, balloon: (text, _) => _balloons.Add(text));
    }

    /// <summary>SPEC §23: every speech load passes the language guard; a model that cannot do what is asked still loads, with a balloon saying why.</summary>
    [Fact]
    public void A_model_that_cannot_hear_the_language_still_loads_but_a_balloon_says_why()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), _dir.FullName);
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = "base.en", Language = "hi" } };
        var file = Path.Combine(paths.ModelsDir(settings), "ggml-base.en.bin");
        Directory.CreateDirectory(paths.ModelsDir(settings));
        File.WriteAllBytes(file, new byte[16]); // present; the fake loader never reads it
        using var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);
        var coordinator = new ModelCoordinator(paths, models, _llm, _http, NullLog.Instance, () => settings, null, _prompts, _ => null,
            reload: () => { }, shutdown: () => _shutdowns++, refreshTooltip: () => { }, balloon: (text, _) => _balloons.Add(text));

        coordinator.Ensure(previousPath: null, startup: true);
        var balloon = Assert.Single(_balloons);
        Assert.Contains("base.en is an English-only model", balloon);
        Assert.Contains("\"hi\"", balloon);
        Assert.Equal(file, models.ModelPath); // the load still began: the user keeps a working model, and the reason to change it

        _balloons.Clear();
        settings = settings with { Whisper = settings.Whisper with { Language = "en" } };
        coordinator.Ensure(previousPath: null, startup: true);
        Assert.Empty(_balloons);
    }

    private static (string, System.Text.Json.Nodes.JsonNode?)[] FullOnCpu =>
        [("whisper.model", "small.en"), ("llm.provider", "local"), ("llm.model", "qwen2.5-1.5b"), ("whisper.beam_size", 5)];

    [Fact]
    public void A_preset_whose_models_are_on_disk_is_written_at_once()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var then = 0;
        Make("base.en").ApplyWithDownloads("Full mode", [], FullOnCpu, () => then++);
        var written = Dictum.Settings.SettingsLoader.LoadFile(SettingsFile).Settings;
        Assert.Equal(("small.en", "local", "qwen2.5-1.5b", 5), (written.Whisper.Model, written.Llm.Provider, written.Llm.Model, written.Whisper.BeamSize));
        Assert.Equal(1, then);
        Assert.Equal(0, _prompts.ConfirmCalls);
    }

    [Fact]
    public void Missing_models_are_offered_once_and_nothing_is_written_when_declined_or_offline()
    {
        var missing = new List<(DownloadSpec, string)> { (ModelCatalog.Find("small.en")!.Download, Path.Combine(_dir.FullName, "ggml-small.en.bin")) };
        Make("base.en").ApplyWithDownloads("Full mode", missing, FullOnCpu);
        Assert.Equal(1, _prompts.ConfirmCalls); // declined by the fake
        Assert.False(File.Exists(SettingsFile));

        Make("base.en", offlineOnly: true).ApplyWithDownloads("Full mode", missing, FullOnCpu);
        Assert.Equal(1, _prompts.ConfirmCalls); // refused before asking
        Assert.Contains("offline_only is on", Assert.Single(_balloons));
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void A_gated_cleanup_model_says_it_needs_a_login_instead_of_offering_a_download()
    {
        Make("base.en").SelectCleanup("gemma-3-1b");
        Assert.Equal(0, _prompts.ConfirmCalls);
        Assert.Equal("gemma-3-1b requires a Hugging Face login to download; use install.ps1 -ModelsFrom or download-model.ps1 with a token.", Assert.Single(_balloons));
    }

    [Fact]
    public void Reload_to_a_missing_model_shows_a_balloon_not_a_dialog_and_never_quits()
    {
        var coordinator = Make("base.en"); // not on disk in the temp models folder
        coordinator.Ensure(previousPath: @"X:\elsewhere\ggml-small.en.bin", startup: false);

        Assert.Equal(0, _prompts.MissingCalls);
        Assert.Equal(0, _shutdowns);
        Assert.Contains("base.en is not downloaded", Assert.Single(_balloons));

        coordinator.Ensure(previousPath: coordinator.ModelPath, startup: false); // another reload, same missing model
        Assert.Single(_balloons);
        Assert.Equal(0, _prompts.MissingCalls);
    }

    [Fact]
    public void Reload_to_a_missing_model_keeps_the_loaded_one()
    {
        var loaded = Path.Combine(_dir.FullName, "ggml-small.en.bin");
        _models.BeginLoad(loaded, new WhisperSettings()); // stands in for the model already in use
        var coordinator = Make("base.en");
        coordinator.Ensure(previousPath: loaded, startup: false);

        Assert.Equal(loaded, _models.ModelPath);
        Assert.Contains("keeping small.en", Assert.Single(_balloons));
        Assert.Equal("small.en", coordinator.CurrentName);
        Assert.Equal(0, _shutdowns);
    }

    [Fact]
    public void Startup_with_a_missing_model_asks_and_quit_exits()
    {
        Make("base.en").Ensure(previousPath: null, startup: true);
        Assert.Equal(1, _prompts.MissingCalls);
        Assert.Equal(1, _shutdowns);
        Assert.Empty(_balloons);
    }

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
