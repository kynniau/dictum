using System.Text.Json.Nodes;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.App;

/// <summary>
/// The app's model lifecycle for both models (SPEC §5, §8, §10): the first-run pickers, the startup missing-model
/// prompt, the model choices in the tray panel and Settings > Models (hot-swap, ask-then-download, choose file) and download
/// progress. Runs on the UI thread. Nothing is downloaded without an explicit user action, never with
/// offline_only on, and no dialog opens on a settings reload (it could take focus mid-dictation).
/// </summary>
/// <param name="gpuMachine">Tests decide; production probes the whisper runtime (<see cref="WhisperEngine.GpuMachine"/>).</param>
public sealed class ModelCoordinator(
    AppPaths paths,
    ModelManager models,
    LocalLlmManager llm,
    HttpClient http,
    ILog log,
    Func<AppSettings> settings,
    string? launchOverride,
    IModelPrompts prompts,
    Func<string, string?> environment,
    Action reload,
    Action shutdown,
    Action refreshTooltip,
    Action<string, bool> balloon,
    ModelIntegrity? integrity = null,
    Func<bool>? gpuMachine = null)
{
    /// <summary>Added to the download question when the download ends in a model switch.</summary>
    public const string SwitchNote = "Dictum switches to it when the download finishes";

    /// <summary>What the balloon, and a page whose choice it was, say when settings.json cannot be written (<see cref="Write"/>).</summary>
    public const string WriteFailed = "settings.json could not be updated (it is not valid JSON, holds a key twice, or is read-only). Fix it, then choose again.";

    private readonly HashSet<string> _warnedMissing = [];
    private readonly ModelDownloads _downloads = new(http, log, settings, integrity, refreshTooltip, balloon);
    private string? _launchOverride = launchOverride;
    private string? _firstRunSpeechDownload;
    private string? _firstRunCleanupDownload;
    private string? _languageProblemShown; // the last WhisperLanguages.Problem ballooned, so a reload does not repeat it

    /// <summary>True while a model download runs or waits its turn (the tray panel then shows "Cancel download").</summary>
    public bool IsDownloading => _downloads.IsDownloading;

    /// <summary>"downloading base.en 42%" while a download runs, else null.</summary>
    public string? DownloadStatus => _downloads.Status;

    /// <summary>How far the running download has got (0-1), or null.</summary>
    public double? DownloadFraction => _downloads.Fraction;

    /// <summary>The download question's note, with the low-space warning when the download would leave under 2 GB (SPEC §17).</summary>
    private string WithSpaceWarning(string note, long? bytes) =>
        bytes is long b && Storage.DiskSpace.DownloadWarning(paths.ModelsDir(settings()), b) is { } warning ? $"{note}. {warning}" : note;

    public string ModelPath => SettingsFile.EffectiveModelPath(settings(), paths, environment, _launchOverride);

    public string CurrentName => ModelCatalog.DisplayName(models.ModelPath ?? ModelPath);

    /// <summary>Cleanup shown in the tray: the local model's name, "rules" for none, or the cloud provider.</summary>
    public string CurrentCleanupName => settings().Llm.Provider switch
    {
        LlmSettings.Local => CleanupCatalog.DisplayName(llm.ModelPath ?? settings().Llm.Model),
        LlmSettings.NoProvider => "rules",
        var cloud => cloud,
    };

    public bool IsDownloaded(ModelInfo info) => File.Exists(ModelCatalog.ResolvePath(info.Name, paths.ModelsDir(settings())));

    public bool IsCleanupDownloaded(CleanupModelInfo info) => File.Exists(CleanupCatalog.ResolvePath(info.Name, paths.ModelsDir(settings())));

    private bool EnvOverride => SettingsFile.ModelPathOverride(environment) is not null;

    /// <summary>
    /// First run: the speech picker, then the cleanup picker, each with the recommendation pre-selected; only the questions
    /// settings.json leaves open are asked (<see cref="AppRules.FirstRunQuestions"/>), and only those answers are written.
    /// With a model override (scripted/test launch) the pickers are skipped and the recommendations written. Returns false
    /// if the user chose Quit.
    /// </summary>
    public bool FirstRun(bool askSpeech = true, bool askCleanup = true)
    {
        var gpu = gpuMachine is not null ? gpuMachine() : ProbeGpuMachine();
        var (speech, speechWhy) = ModelCatalog.Recommend(gpu);
        var (cleanup, cleanupWhy) = CleanupCatalog.Recommend(gpu);
        log.Info($"First run: runtime {WhisperEngine.LoadedRuntime}, Vulkan devices {WhisperEngine.DeviceSummary}: recommending {speech} ({speechWhy}) and {cleanup} ({cleanupWhy}).");

        string chosenSpeech = speech, provider = LlmSettings.Local, chosenCleanup = cleanup;
        if (_launchOverride is not null || EnvOverride)
        {
            log.Info("First run with a model override: pickers skipped.");
        }
        else
        {
            if (askSpeech)
            {
                var pick = prompts.FirstRunPicker(speech, speechWhy, IsDownloaded, paths.DefaultModelsDir);
                if (pick.Quit) return false;
                chosenSpeech = pick.Model ?? pick.FilePath!;
                if (pick.Model is not null && ModelCatalog.Find(pick.Model) is { } info && !IsDownloaded(info)) _firstRunSpeechDownload = pick.Model;
            }
            if (askCleanup)
            {
                var cleanupPick = prompts.CleanupPicker(cleanup, cleanupWhy, IsCleanupDownloaded, paths.DefaultModelsDir);
                if (cleanupPick.Quit) return false;
                if (cleanupPick.RulesOnly) provider = LlmSettings.NoProvider;
                else chosenCleanup = cleanupPick.Model ?? cleanupPick.FilePath!;
                if (cleanupPick.Model is not null && CleanupCatalog.Find(cleanupPick.Model) is { } c && !IsCleanupDownloaded(c)) _firstRunCleanupDownload = c.Name;
            }
        }
        if (SettingsFile.CreateIfMissing(paths.SettingsFile, chosenSpeech, provider, chosenCleanup))
        {
            log.Info($"First run: whisper.model = {chosenSpeech}, llm.provider = {provider}, llm.model = {chosenCleanup}; wrote {paths.SettingsFile}.");
            return true;
        }
        // settings.json was seeded (install.ps1, an admin) without these choices: add them and keep everything else.
        var values = new List<(string Key, string Value)>();
        if (askSpeech) values.Add(("whisper.model", chosenSpeech));
        if (askCleanup) values.Add(("llm.provider", provider));
        if (askCleanup && provider == LlmSettings.Local) values.Add(("llm.model", chosenCleanup));
        var written = values.All(v => SettingsFile.SetValue(paths.SettingsFile, v.Key, JsonValue.Create(v.Value)));
        log.Info($"First run on an existing settings.json: {string.Join(", ", values.Select(v => $"{v.Key} = {v.Value}"))}{(written ? "" : " could not be written")}.");
        return true;
    }

    private bool ProbeGpuMachine()
    {
        WhisperEngine.ProbeRuntime(new WhisperSettings().Gpu, log);
        return WhisperEngine.GpuMachine;
    }

    /// <summary>
    /// Every speech-model load goes through here (a settings change, the first-run picker, a finished download): the model
    /// still loads, and a balloon says when it cannot do what whisper.language or whisper.translate ask (SPEC §23), whichever
    /// way it was chosen (tray picker, Mode preset, Choose file, calibration's Use this, a hand edit).
    /// </summary>
    private void BeginSpeechLoad(string path)
    {
        var whisper = settings().Whisper;
        if (WhisperLanguages.Problem(path, whisper.Language, whisper.Translate) is { } problem) ShowLanguageProblem(problem);
        models.BeginLoad(path, whisper);
    }

    private void ShowLanguageProblem(string problem)
    {
        _languageProblemShown = problem;
        balloon(problem, true);
    }

    /// <summary>
    /// Loads the configured speech model if it changed, or again if its last load failed (a re-download or tray > Reload
    /// settings is the retry). At startup a missing model gets the Download / Choose file… / Quit dialog (SPEC §10); on a
    /// reload it only gets a balloon and the loaded model stays in use. With the same model, a change of whisper.language or
    /// whisper.translate only rebuilds its decoder (AppShell.Reload), so no load is there to say the model cannot do it
    /// (SPEC §23): the reload says it, once per distinct problem.
    /// </summary>
    public void Ensure(string? previousPath, bool startup)
    {
        var path = ModelPath;
        var changed = previousPath is null || path != previousPath;
        if (!changed && models.ModelPath == path && !models.LoadFailed) // same model, already loading or loaded
        {
            var whisper = settings().Whisper;
            if (WhisperLanguages.Problem(path, whisper.Language, whisper.Translate) is { } problem && problem != _languageProblemShown) ShowLanguageProblem(problem);
            return;
        }
        if (File.Exists(path))
        {
            BeginSpeechLoad(path);
            return;
        }
        log.Warn($"Model file missing: {path}");
        var name = ModelCatalog.DisplayName(path);
        if (!startup)
        {
            if (!changed) return;
            var keeping = models.ModelPath is { } loaded ? ModelCatalog.DisplayName(loaded) : null;
            balloon(keeping is null
                ? $"Model {name} is not downloaded. Choose or download one in Settings > Models."
                : $"Model {name} is not downloaded; keeping {keeping}. Download it in Settings > Models.", true);
            return;
        }
        var configured = settings().Whisper.Model;
        if (_firstRunSpeechDownload is not null && _firstRunSpeechDownload == configured && _launchOverride is null && !EnvOverride)
        {
            _firstRunSpeechDownload = null;
            StartDownload(ModelCatalog.Find(configured)!.Download, ModelCatalog.ResolvePath(configured, paths.ModelsDir(settings())),
                () => BeginSpeechLoad(ModelPath)); // picked in the first-run picker
            return;
        }
        PromptMissing(configured);
    }

    /// <summary>Loads the local cleanup model when that provider is on; otherwise releases it. Never prompts.</summary>
    public void EnsureCleanup()
    {
        var s = settings();
        if (s.Llm.Provider != LlmSettings.Local)
        {
            if (llm.ModelPath is not null) llm.Unload();
            return;
        }
        var path = SettingsFile.CleanupModelPath(s, paths);
        if (File.Exists(path))
        {
            llm.BeginLoad(path, s.Llm.Gpu);
            return;
        }
        if (_firstRunCleanupDownload is { } name && CleanupCatalog.Find(name) is { } info)
        {
            _firstRunCleanupDownload = null;
            StartDownload(info.Download, path, () => llm.BeginLoad(path, settings().Llm.Gpu));
            return;
        }
        if (_downloads.IsFetching(path)) return; // on its way: it loads when it arrives
        if (_warnedMissing.Add(path))
        {
            log.Warn($"Cleanup model missing: {path}; the rule-based tidy is used.");
            balloon($"Cleanup model {CleanupCatalog.DisplayName(s.Llm.Model)} is not downloaded; using the rule-based tidy. Download it in Settings > Models.", true);
        }
    }

    private void PromptMissing(string configured)
    {
        var canDownload = _launchOverride is null && !EnvOverride && ModelCatalog.Find(configured) is not null && !settings().OfflineOnly;
        switch (prompts.MissingModel(ModelCatalog.DisplayName(configured), canDownload))
        {
            case MissingModelChoice.Download:
                StartDownload(ModelCatalog.Find(configured)!.Download, ModelCatalog.ResolvePath(configured, paths.ModelsDir(settings())),
                    () => BeginSpeechLoad(ModelPath));
                break;
            case MissingModelChoice.ChooseFile:
                ChooseFile();
                break;
            default:
                shutdown();
                break;
        }
    }

    /// <summary>A speech catalogue entry was chosen (tray panel or Settings > Models).</summary>
    public void Select(string name)
    {
        if (ModelCatalog.Find(name) is not { } info) return;
        if (IsDownloading)
        {
            _ = CancelThenAsync(() => Select(name)); // choosing another model cancels the running download (README)
            return;
        }
        _launchOverride = null; // a tray choice replaces a --model launch override
        if (EnvOverride) balloon("The DICTUM_MODEL_PATH environment variable is set and still overrides the model choice.", true);
        if (IsDownloaded(info)) SetValues(("whisper.model", info.Name));
        else if (!RefuseOffline(info.FileName) && prompts.ConfirmDownload(info.FileName, info.SizeText, WithSpaceWarning($"{info.TradeOff}. {SwitchNote}", info.Bytes)))
            StartDownload(info.Download, ModelCatalog.ResolvePath(info.Name, paths.ModelsDir(settings())), () => SetValues(("whisper.model", info.Name)));
    }

    /// <summary>A cleanup catalogue entry was chosen (tray panel or Settings).</summary>
    public void SelectCleanup(string name)
    {
        if (CleanupCatalog.Find(name) is not { } info) return;
        if (IsDownloading)
        {
            _ = CancelThenAsync(() => SelectCleanup(name));
            return;
        }
        (string, string)[] values = [("llm.provider", LlmSettings.Local), ("llm.model", info.Name)];
        if (IsCleanupDownloaded(info)) SetValues(values);
        else if (info.RequiresLogin) balloon($"{info.Name} {ModelDownloader.LoginRequired}.", true);
        else if (!RefuseOffline(info.FileName) && prompts.ConfirmDownload(info.FileName, info.SizeText,
                     WithSpaceWarning(info.TradeOff + (info.CommercialUse ? "" : ". Licence: " + info.Licence) + ". " + SwitchNote, info.Bytes)))
            StartDownload(info.Download, CleanupCatalog.ResolvePath(info.Name, paths.ModelsDir(settings())), () => SetValues(values));
    }

    /// <summary>Tray: "No AI cleanup (rule-based tidy)".</summary>
    public void SelectRulesOnly() => SetValues(("llm.provider", LlmSettings.NoProvider));

    /// <summary>Tray or startup prompt: pick any whisper ggml file.</summary>
    public void ChooseFile()
    {
        var file = prompts.ChooseFile(paths.ModelsDir(settings()), cleanupModel: false);
        if (file is null) return;
        _launchOverride = null;
        SetValues(("whisper.model", file));
    }

    /// <summary>Tray: pick any .gguf cleanup model.</summary>
    public void ChooseCleanupFile()
    {
        var file = prompts.ChooseFile(paths.ModelsDir(settings()), cleanupModel: true);
        if (file is not null) SetValues(("llm.provider", LlmSettings.Local), ("llm.model", file));
    }

    /// <summary>Tray: "Cancel download" (and quit): the running download and any waiting their turn.</summary>
    public void CancelDownload() => _downloads.Cancel();

    private async Task CancelThenAsync(Action next)
    {
        _downloads.Cancel();
        try
        {
            if (_downloads.Running is { } running) await running; // lets the cancelled download delete its .part first
            next();
        }
        catch (Exception ex)
        {
            log.Error($"Model choice after cancelling a download failed: {ex.GetType().Name}: {ex.Message}");
            balloon(ex.Message, true);
        }
    }

    private bool RefuseOffline(string fileName)
    {
        if (!settings().OfflineOnly) return false;
        balloon($"offline_only is on: {fileName} cannot be downloaded. Copy it into {paths.ModelsDir(settings())} instead.", true);
        return true;
    }

    private void SetValues(params (string Key, string Value)[] values) => Write([.. values.Select(v => (v.Key, (JsonNode?)JsonValue.Create(v.Value)))]);

    /// <summary>
    /// Writes settings.json keys chosen in the tray or a window, then reloads (and hot-swaps a changed model). Returns false when
    /// the file could not be written (the balloon says <see cref="WriteFailed"/>); nothing reloads then.
    /// </summary>
    public bool Write(params (string Key, JsonNode? Value)[] values)
    {
        if (TryWrite(out _, reloadAfter: true, values)) return true;
        balloon(WriteFailed, true);
        return false;
    }

    /// <summary>
    /// <see cref="Write"/> without the balloon, for a caller that says itself why the file could not be written: writes the
    /// keys in order and, when <paramref name="reloadAfter"/>, reloads. False when a key could not be written (those before it
    /// stay written, as in <see cref="Write"/>); <paramref name="failure"/> then says why (<see cref="SettingsFile.ShortReason"/>)
    /// and nothing reloads.
    /// </summary>
    public bool TryWrite(out string? failure, bool reloadAfter, params (string Key, JsonNode? Value)[] values)
    {
        failure = null;
        if (values.Length == 0) return true;
        foreach (var (key, value) in values)
        {
            if (!SettingsFile.SetValue(paths.SettingsFile, key, value?.DeepClone(), out failure)) return false;
        }
        log.Info($"Set from the app: {string.Join(", ", values.Select(v => $"{v.Key} = {v.Value?.ToJsonString() ?? "null"}"))}.");
        if (reloadAfter) reload(); // re-reads settings and hot-swaps the model (hotkey ignored while a speech model loads)
        return true;
    }

    /// <summary>
    /// Tray → Mode and the calibration window (SPEC §16.1, §16.5): when every listed model is on disk, writes the values at
    /// once (cancelling a download in progress, as any other model choice does); otherwise asks once (names, total size,
    /// host, <paramref name="note"/>), downloads the missing files one after another through the same downloader (refused
    /// under offline_only, as every download is) behind any download already running, and writes the values only when all
    /// have arrived.
    /// </summary>
    /// <param name="note">The question's explanation: what the download is for and whether Dictum then switches to it.</param>
    /// <param name="finished">Told true when the values were applied, false when nothing will be (declined, refused, failed).</param>
    public void ApplyWithDownloads(string note, IReadOnlyList<(DownloadSpec Spec, string Destination)> missing, (string Key, JsonNode? Value)[] values, Action? then = null,
        Action<bool>? finished = null)
    {
        void Finish()
        {
            Write(values);
            then?.Invoke();
            finished?.Invoke(true);
        }
        if (missing.Count == 0)
        {
            if (IsDownloading) _ = CancelThenAsync(Finish);
            else Finish();
            return;
        }
        var names = string.Join(" and ", missing.Select(m => m.Spec.FileName));
        if (RefuseOffline(names))
        {
            finished?.Invoke(false);
            return;
        }
        if (missing.FirstOrDefault(m => m.Spec.RequiresLogin).Spec is { } gated)
        {
            balloon($"{gated.FileName} {ModelDownloader.LoginRequired}.", true);
            finished?.Invoke(false);
            return;
        }
        var total = missing.Sum(m => m.Spec.Bytes ?? 0);
        if (!prompts.ConfirmDownload(names, ModelCatalog.FormatSize(total), WithSpaceWarning(note, total)))
        {
            finished?.Invoke(false);
            return;
        }
        DownloadInTurn(missing, 0, Finish, () => finished?.Invoke(false));
    }

    private void DownloadInTurn(IReadOnlyList<(DownloadSpec Spec, string Destination)> files, int index, Action done, Action failed)
    {
        if (index >= files.Count) done();
        else StartDownload(files[index].Spec, files[index].Destination, () => DownloadInTurn(files, index + 1, done, failed), failed);
    }

    private void StartDownload(DownloadSpec spec, string destination, Action onSuccess, Action? onFailure = null)
    {
        if (RefuseOffline(spec.FileName))
        {
            onFailure?.Invoke();
            return;
        }
        if (spec.RequiresLogin)
        {
            balloon($"{spec.FileName} {ModelDownloader.LoginRequired}.", true);
            onFailure?.Invoke();
            return;
        }
        _downloads.Start(spec, destination, onSuccess, onFailure);
    }
}
