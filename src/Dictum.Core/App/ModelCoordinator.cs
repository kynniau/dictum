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
/// <param name="freeBytes">Free bytes on the drive holding a folder: tests decide; production reads the drive (<see cref="Storage.DiskSpace.FreeBytes"/>).</param>
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
    Func<bool>? gpuMachine = null,
    Func<string, long?>? freeBytes = null)
{
    /// <summary>Added to the download question when the download ends in a model switch.</summary>
    public const string SwitchNote = "Dictum switches to it when the download finishes";

    /// <summary>What the balloon, and a page whose choice it was, say when settings.json cannot be written (<see cref="Write"/>).</summary>
    public const string WriteFailed = "settings.json could not be updated (it is not valid JSON, holds a key twice, or is read-only). Fix it, then choose again.";

    /// <summary>
    /// APPROVED 22/10/2026 copy (proposed 17/10/2026): Settings > Models > Model folder > Browse… when the folder
    /// picked is the one holding the models that come with Dictum (or inside it), which is never written (Decision 16).
    /// </summary>
    public const string BundledFolderRefused = "That folder holds the models that come with Dictum, which Dictum never changes. Choose another folder.";

    private readonly HashSet<string> _warnedMissing = [];
    private readonly Dictionary<string, (long Length, DateTime Written, bool Passes)> _bundledChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, long?> _freeBytes = freeBytes ?? Storage.DiskSpace.FreeBytes;
    private ModelDownloads? _downloadQueue;

    /// <summary>The download queue; a queued download is checked for space again when its turn comes (Decision 15).</summary>
    private ModelDownloads _downloads => _downloadQueue ??= new(http, log, settings, integrity, refreshTooltip, balloon,
        refuseBeforeStart: (spec, destination) => RefuseLowSpace(destination, spec.FileName, spec.Bytes));
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

    /// <summary>How far the running download has got, as the status line says it ("42%", or "12 MB" when its size is unknown), or null.</summary>
    public string? DownloadProgress => _downloads.Progress;

    /// <summary>Whether a download of this model file is running, waiting its turn, or neither (Settings > Models, the tray pickers).</summary>
    public DownloadState DownloadStateOf(string fileName) => _downloads.StateOfFile(fileName);

    /// <summary>
    /// Tests only: swaps the spec of every download for one their in-memory server can satisfy (a catalogue file's real size
    /// and SHA-256 cannot be served by a test). Null in the app.
    /// </summary>
    internal Func<DownloadSpec, DownloadSpec>? TestSpecs { get; init; }

    // Last choice wins (decision of 19/10/2026): the latest model choice of each kind, as a token. A choice that downloads
    // holds its destination's full path; a written choice (TryWrite of whisper.model, or of llm.provider / llm.model) holds a
    // fresh marker. A download's switch runs only while its token is still the latest, so a download that arrives after the
    // user chose another model does not switch them away from it. A hand edit of settings.json is not seen here.
    private string? _speechChoice;
    private string? _cleanupChoice;
    private int _choices;

    /// <summary>The download question's note, with the low-space warning when the download would leave under 2 GB (SPEC §17).</summary>
    /// <param name="exceptRunning">Count the downloads waiting but not the running one: the question offers only to stop it.</param>
    private string WithSpaceWarning(string note, long? bytes, bool exceptRunning = false) =>
        SpaceWarning(bytes, exceptRunning) is { } warning ? $"{note}. {warning}" : note;

    /// <summary>The low-space warning alone (SPEC §17), or null: the Simple view's plain question adds it to its own words.</summary>
    private string? SpaceWarning(long? bytes, bool exceptRunning = false)
    {
        var folder = paths.ModelsDir(settings());
        return bytes is long b ? Storage.DiskSpace.DownloadWarning(folder, b, _freeBytes, _downloads.PendingBytes(folder, exceptRunning)) : null;
    }

    /// <summary>
    /// The Simple view's name for the download running to <paramref name="destination"/> ("More accurate", 1.2), read from the
    /// tiers in force when the question is asked, or its short name when it is none of them (a clean-up model, or a file the
    /// language or the machine class no longer maps to a tier). Read each time, not remembered from when it started: a name
    /// kept from then went stale when the language changed the tiers (lens on 787b300, 21/10/2026).
    /// </summary>
    private string PlainNameOf(string destination)
    {
        var s = settings();
        var gpu = gpuMachine is not null ? gpuMachine() : WhisperEngine.GpuMachine;
        return SimpleView.TierOf(ShortName(destination), gpu, s.Whisper.Language, s.Whisper.Translate)?.Label ?? ShortName(destination);
    }

    /// <summary>The speech model file the settings mean now, looked up in the model folders (<see cref="Lookups"/> counts each time).</summary>
    public string ModelPath
    {
        get
        {
            Interlocked.Increment(ref _lookups); // a name is looked up in up to three folders; a path or the environment names the file at no cost, counted all the same
            return SettingsFile.EffectiveModelPath(settings(), paths, environment, _launchOverride, BundledCheck);
        }
    }

    /// <summary>
    /// The speech model file in use: the one loaded or loading, else <see cref="ModelPath"/>. What a page that redraws on every
    /// progress tick asks (re-lens, 17/10/2026): with a model loaded it looks in no folder.
    /// </summary>
    public string CurrentPath => models.ModelPath ?? ModelPath;

    public string CurrentName => ModelCatalog.DisplayName(CurrentPath);

    /// <summary>
    /// The clean-up's name: the local model's name, "rules" for none, "no clean-up" for off (APPROVED 22/10/2026; Settings > Models'
    /// "In use:" line), or the cloud provider. The tray header and tooltip say off as SimpleView.OffHeaderLabel (owner decision 6).
    /// </summary>
    public string CurrentCleanupName => settings().Llm.Provider switch
    {
        LlmSettings.Local => CleanupCatalog.DisplayName(llm.ModelPath ?? settings().Llm.Model),
        LlmSettings.NoProvider => "rules",
        LlmSettings.Off => "no clean-up", // not "off": the tray line "base.en · Vulkan · off · F9" read as Dictum being off (lens on d6b4ea8)
        var cloud => cloud,
    };

    /// <summary>The model is on this PC: downloaded, copied in, or come with Dictum, in any of the model folders (<see cref="ModelFolders"/>).</summary>
    public bool IsDownloaded(ModelInfo info) => Source(info) is not null;

    public bool IsCleanupDownloaded(CleanupModelInfo info) => Locate(info.FileName, info.Bytes) is not null;

    /// <summary>The folder the model is used from (the first in the lookup order that has it), or null when it is not on this PC.</summary>
    public ModelFolderKind? Source(ModelInfo info) => Locate(info.FileName, info.Bytes)?.Kind;

    /// <summary>The file the model is used from (the first in the lookup order that passes), or null when it is not on this PC.</summary>
    public string? Find(ModelInfo info) => Locate(info.FileName, info.Bytes)?.Path;

    /// <summary>
    /// The copy that would be used if <paramref name="gone"/> were deleted: the next file named <paramref name="fileName"/> in the
    /// lookup order that passes (the bundled one held to its SHA-256, as for every lookup), with the folder it is in; null when none.
    /// </summary>
    public (string Path, ModelFolderKind Kind)? FindBesides(string fileName, long? bytes, string gone) => Locate(fileName, bytes, except: gone);

    private int _lookups;

    /// <summary>How many actions wait on the download to <paramref name="destination"/> besides its own (tests count them).</summary>
    internal int ArrivalActions(string destination) => _downloads.AlsoCount(destination);

    /// <summary>How many folder lookups this coordinator has made (each reads up to three folders, a name resolved by <see cref="ModelPath"/> included): tests count them.</summary>
    internal int Lookups => Volatile.Read(ref _lookups);

    /// <summary>How many downloads have ended (arrived, failed or cancelled): Settings > Models looks the catalogue up again after one.</summary>
    public int DownloadsEnded => _downloads.Ended;

    private (string Path, ModelFolderKind Kind)? Locate(string fileName, long? bytes, AppSettings? lookIn = null, string? except = null)
    {
        Interlocked.Increment(ref _lookups);
        return ModelFolders.Locate(fileName, bytes, paths, lookIn ?? settings(), BundledCheck, except);
    }

    /// <summary>The full catalogue check for a file in the bundled folder, when there is an integrity record to check with.</summary>
    private Func<string, bool>? BundledCheck => integrity is null ? null : BundledFilePasses;

    /// <summary>
    /// Decision 16: a model file in the bundled folder is used only when it passes the catalogue check of every load (size,
    /// then SHA-256; <see cref="ModelIntegrity.Verify"/>, which remembers a pass in verified-models.json, so base.en is hashed
    /// once per version of the file). One that fails is passed over as if it were absent, said once in the log, and never
    /// deleted or replaced: the folder is read-only. The result is kept per file version for the session, so a failing file is
    /// not hashed again on every lookup.
    /// </summary>
    private bool BundledFilePasses(string path)
    {
        var file = new FileInfo(path);
        lock (_bundledChecks)
        {
            if (_bundledChecks.TryGetValue(path, out var seen) && seen.Length == file.Length && seen.Written == file.LastWriteTimeUtc) return seen.Passes;
            // simplification: the first lookup of a bundled file after an install or an update hashes it on the calling
            // thread, which during a start is the UI thread (base.en: 0.5 s, measured 17/10/2026 on the development machine
            // with the built app's --selftest). Ceiling: a bigger bundled model holds that start up for longer. Trigger to
            // retire: shipping a bigger model in the package; then check it in the background.
            var passes = true;
            try
            {
                integrity!.Verify(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // ModelIntegrityException is an IOException
            {
                passes = false;
                log.Warn($"{path} came with Dictum but fails the catalogue check, so it is not used (and, being read-only, never deleted or replaced): {ex.Message}");
            }
            _bundledChecks[path] = (file.Length, file.LastWriteTimeUtc, passes);
            return passes;
        }
    }

    private bool EnvOverride => SettingsFile.ModelPathOverride(environment) is not null;

    /// <summary>
    /// First run: the speech picker, then the cleanup picker, each with the recommendation pre-selected; only the questions
    /// settings.json leaves open are asked (<see cref="AppRules.FirstRunQuestions"/>), and only those answers are written.
    /// With a model override (scripted/test launch) the pickers are skipped and the recommendations written. When base.en is on
    /// this PC (Decision 16: it comes with Dictum, so in every package that ships it) nothing is asked: the open choices are
    /// answered with base.en and the rule-based tidy, as "No AI cleanup (rule-based tidy)" answers the cleanup picker, and
    /// Settings > Models offers the AI clean-up download; a seeded settings.json that names llm.provider (local, with no
    /// llm.model) keeps it and gets the recommended clean-up model, which is not downloaded: nothing downloads, and
    /// <see cref="EnsureCleanup"/> says the rule-based tidy is used until it is. Returns false if the user chose Quit.
    /// The onboarding window (1.2) answers the same questions through <see cref="PlanFirstRun"/> and <see cref="AnswerFirstRun"/>.
    /// </summary>
    /// <param name="seeded">The settings.json being completed (a seeded models.folder is looked in too); null: the settings in use.</param>
    /// <param name="keepProvider">settings.json names llm.provider (<see cref="SettingsLoadResult.NamesCleanupProvider"/>).</param>
    public bool FirstRun(bool askSpeech = true, bool askCleanup = true, AppSettings? seeded = null, bool keepProvider = false)
    {
        var plan = PlanFirstRun(seeded);
        ModelPick? speechPick = null;
        CleanupPick? cleanupPick = null;
        if (plan.AsksPickers)
        {
            if (askSpeech)
            {
                speechPick = prompts.FirstRunPicker(plan.Speech, plan.SpeechWhy, IsDownloaded, paths.DefaultModelsDir);
                if (speechPick.Quit) return false;
            }
            if (askCleanup)
            {
                cleanupPick = prompts.CleanupPicker(plan.Cleanup, plan.CleanupWhy, IsCleanupDownloaded, paths.DefaultModelsDir);
                if (cleanupPick.Quit) return false;
            }
        }
        AnswerFirstRun(plan, askSpeech, askCleanup, keepProvider, speechPick, cleanupPick);
        return true;
    }

    /// <summary>
    /// The first run's recommendations (the GPU probe, then <see cref="ModelCatalog.Recommend"/> and
    /// <see cref="CleanupCatalog.Recommend"/>), and whether a model question is open at all: not with a model override, and not
    /// when base.en is on this PC (<see cref="FirstRunPlan.BundledPath"/>).
    /// </summary>
    /// <param name="seeded">The settings.json being completed (a seeded or just-chosen models.folder is looked in too); null: the settings in use.</param>
    public FirstRunPlan PlanFirstRun(AppSettings? seeded = null)
    {
        var gpu = gpuMachine is not null ? gpuMachine() : ProbeGpuMachine(FirstRunProbeGpu(seeded ?? settings()));
        var (speech, speechWhy) = ModelCatalog.Recommend(gpu);
        var (cleanup, cleanupWhy) = CleanupCatalog.Recommend(gpu);
        log.Info($"First run: runtime {WhisperEngine.LoadedRuntime}, Vulkan devices {WhisperEngine.DeviceSummary}: recommending {speech} ({speechWhy}) and {cleanup} ({cleanupWhy}).");
        var overridden = _launchOverride is not null || EnvOverride;
        var bundled = overridden ? null : Locate(ModelCatalog.FileName(ModelCatalog.Bundled), ModelCatalog.KnownSize(ModelCatalog.Bundled), seeded)?.Path;
        return new FirstRunPlan(gpu, speech, speechWhy, cleanup, cleanupWhy, overridden, bundled);
    }

    /// <summary>
    /// Writes the first run's answers to the open questions (<paramref name="askSpeech"/>, <paramref name="askCleanup"/>): a new
    /// settings.json with them, or, when settings.json already exists (seeded, or begun by the onboarding's folder step), only
    /// those keys added. With a model override the recommendations are written; with base.en on this PC, base.en and the
    /// rule-based tidy (or a seeded llm.provider kept, <paramref name="keepProvider"/>). Otherwise a pick answers its question: a
    /// catalogue pick that is not on this PC downloads at startup (<see cref="Ensure"/>), because the picker named the host and
    /// the size. A null speech pick (the onboarding skipped) writes the recommendation and downloads nothing: the startup
    /// missing-model question asks first (SPEC §10). A null clean-up pick (the onboarding asks no clean-up question) is the
    /// rule-based tidy, as with base.en, unless <paramref name="keepProvider"/>.
    /// A speech pick wins over base.en on this PC: the onboarding asks for one when settings.json names a language that is not
    /// English (lens on 6bacce9); FirstRun passes none when base.en is there, so it is unchanged.
    /// </summary>
    public void AnswerFirstRun(FirstRunPlan plan, bool askSpeech, bool askCleanup, bool keepProvider, ModelPick? speechPick = null, CleanupPick? cleanupPick = null)
    {
        string chosenSpeech = plan.Speech, provider = LlmSettings.Local, chosenCleanup = plan.Cleanup;
        if (plan.Overridden)
        {
            log.Info("First run with a model override: pickers skipped.");
        }
        else if (plan.BundledPath is { } bundled && speechPick is null) // a pick (the onboarding's, for a language that is not English) wins over base.en
        {
            chosenSpeech = ModelCatalog.Bundled;
            if (!keepProvider) provider = LlmSettings.NoProvider; // the lens, 17/10/2026: a seeded "local" was turned to none without a question
            log.Info(provider == LlmSettings.NoProvider
                ? $"First run: {bundled} is on this PC, so no model question is asked: {ModelCatalog.Bundled} and the rule-based tidy (Settings > Models adds AI clean-up)."
                : $"First run: {bundled} is on this PC, so no model question is asked: {ModelCatalog.Bundled}, and settings.json's llm.provider (local) is kept with the recommended {chosenCleanup}, which is not downloaded (Settings > Models).");
        }
        else
        {
            if (askSpeech && speechPick is { Quit: false })
            {
                chosenSpeech = speechPick.Model ?? speechPick.FilePath!;
                if (speechPick.Model is not null && ModelCatalog.Find(speechPick.Model) is { } info && !IsDownloaded(info)) _firstRunSpeechDownload = speechPick.Model;
            }
            if (askCleanup && cleanupPick is { Quit: false })
            {
                if (cleanupPick.RulesOnly) provider = LlmSettings.NoProvider;
                else chosenCleanup = cleanupPick.Model ?? cleanupPick.FilePath!;
                if (cleanupPick.Model is not null && CleanupCatalog.Find(cleanupPick.Model) is { } c && !IsCleanupDownloaded(c)) _firstRunCleanupDownload = c.Name;
            }
            else if (askCleanup && cleanupPick is null && !keepProvider)
            {
                provider = LlmSettings.NoProvider;
            }
        }
        if (SettingsFile.CreateIfMissing(paths.SettingsFile, chosenSpeech, provider, chosenCleanup))
        {
            log.Info($"First run: whisper.model = {chosenSpeech}, llm.provider = {provider}, llm.model = {chosenCleanup}; wrote {paths.SettingsFile}.");
            return;
        }
        // settings.json was seeded (install.ps1, an admin, the onboarding's folder step) without these choices: add them and keep everything else.
        var values = new List<(string Key, string Value)>();
        if (askSpeech) values.Add(("whisper.model", chosenSpeech));
        if (askCleanup) values.Add(("llm.provider", provider));
        if (askCleanup && provider == LlmSettings.Local) values.Add(("llm.model", chosenCleanup));
        var written = values.All(v => SettingsFile.SetValue(paths.SettingsFile, v.Key, JsonValue.Create(v.Value)));
        log.Info($"First run on an existing settings.json: {string.Join(", ", values.Select(v => $"{v.Key} = {v.Value}"))}{(written ? "" : " could not be written")}.");
    }

    /// <summary>
    /// The whisper.gpu the first-run probe loads the runtime with: the one settings.json names (a seeded or hand-written
    /// whisper.gpu = cpu is the documented escape from a GPU driver that fails, so it holds on the first run too, 21/10/2026),
    /// else the default (auto).
    /// </summary>
    internal static string FirstRunProbeGpu(AppSettings? seeded) => (seeded?.Whisper ?? new WhisperSettings()).Gpu;

    /// <summary>Tests only: stands in for <see cref="WhisperEngine.ProbeRuntime"/> (which loads the native runtime). Null in the app.</summary>
    internal Func<string, ILog, string>? TestRuntimeProbe { get; set; } // set, not init: a test shell's coordinator is built inside AppShell

    private bool ProbeGpuMachine(string gpu)
    {
        (TestRuntimeProbe ?? WhisperEngine.ProbeRuntime)(gpu, log);
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
        var path = SettingsFile.CleanupModelPath(s, paths, BundledCheck);
        if (File.Exists(path))
        {
            llm.BeginLoad(path, s.Llm.Gpu);
            return;
        }
        if (_firstRunCleanupDownload is { } name && CleanupCatalog.Find(name) is { } info)
        {
            _firstRunCleanupDownload = null;
            StartDownload(info.Download, path, () =>
            {
                // Last choice wins: a clean-up choice made while it downloaded stands (the reload after it loaded that one).
                var now = settings();
                if (now.Llm.Provider == LlmSettings.Local && SamePath(SettingsFile.CleanupModelPath(now, paths, BundledCheck), path)) llm.BeginLoad(path, now.Llm.Gpu);
                else log.Info($"{info.Name} arrived, but another clean-up choice was made meanwhile, so it is not loaded.");
            });
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

    /// <summary>
    /// A speech catalogue entry was chosen (tray panel or Settings > Models). A model on this PC is switched to at once; one
    /// that is not goes through <see cref="ChooseDownload"/>. Neither cancels a download already running (Store 10.1.2.10,
    /// 19/10/2026): only the Stop answer to the question asked first does.
    /// </summary>
    /// <param name="plainName">
    /// The Simple view's name for the choice ("More accurate", 1.2): the question, the busy question and the "already
    /// downloading" balloon then use it and plain words instead of the file name and the catalogue's trade-off line.
    /// </param>
    public void Select(string name, string? plainName = null)
    {
        if (ModelCatalog.Find(name) is not { } info) return;
        _launchOverride = null; // a tray choice replaces a --model launch override
        if (EnvOverride) balloon("The DICTUM_MODEL_PATH environment variable is set and still overrides the model choice.", true);
        (string, string)[] values = [("whisper.model", info.Name)];
        if (IsDownloaded(info)) SetValues(values); // a download running meanwhile carries on; its own switch then defers to this choice
        else ChooseDownload(info.Name, info.Download, ModelCatalog.ResolvePath(info.Name, paths.ModelsDir(settings())), info.SizeText,
            $"{info.TradeOff}. {SwitchNote}", values, speech: true, plainName);
    }

    /// <summary>A cleanup catalogue entry was chosen (tray panel or Settings): as <see cref="Select"/>, after the login check.</summary>
    public void SelectCleanup(string name)
    {
        if (CleanupCatalog.Find(name) is not { } info) return;
        (string, string)[] values = [("llm.provider", LlmSettings.Local), ("llm.model", info.Name)];
        if (IsCleanupDownloaded(info)) SetValues(values);
        else if (info.RequiresLogin) balloon($"{info.Name} {ModelDownloader.LoginRequired}.", true);
        else ChooseDownload(info.Name, info.Download, CleanupCatalog.ResolvePath(info.Name, paths.ModelsDir(settings())), info.SizeText,
            info.TradeOff + (info.CommercialUse ? "" : ". Licence: " + info.Licence) + ". " + SwitchNote, values, speech: false);
    }

    /// <summary>
    /// A model that is not on this PC was chosen (decision of 19/10/2026; nothing here cancels before the user answers):
    /// <list type="number">
    /// <item>already downloading or waiting its turn: a balloon says so and nothing else changes, except that it becomes the
    /// latest choice again, so it is switched to when it arrives;</item>
    /// <item>offline_only, or too little space: refused with a balloon, as before;</item>
    /// <item>no other download: the usual question, then the download;</item>
    /// <item>another download running: one question first, with Download after it (queued behind it, which keeps its bytes),
    /// Stop … and download … (the only answer that cancels), and Cancel (nothing changes).</item>
    /// </list>
    /// When the file arrives its values are written, unless a later choice of the same kind was made meanwhile (last choice wins).
    /// </summary>
    private void ChooseDownload(string name, DownloadSpec spec, string destination, string sizeText, string note, (string, string)[] values, bool speech,
        string? plainName = null)
    {
        var token = Path.GetFullPath(destination);
        void Apply() => ApplyIfLatest(speech, token, name, values);
        var state = _downloads.StateOf(destination);
        if (state != DownloadState.None)
        {
            SetChoice(speech, token);
            _downloads.AlsoOnArrival(destination, $"choice {token}", Apply); // a calibration or first-run download of it switches too; a repeat pick adds nothing
            balloon(OnItsWay(plainName ?? name, state), false);
            return;
        }
        // Refused before asking only when it would not fit even with the running download stopped; when it fits only that
        // way, the question offers Stop and Cancel, not Download after it (second lens, 19/10/2026).
        if (RefuseOffline(spec.FileName) || RefuseLowSpace(destination, spec.FileName, spec.Bytes, exceptRunning: true)) return;
        var canWait = LowSpaceRefusal([(destination, spec.Bytes)], spec.FileName) is null;
        var question = WithSpaceWarning(note, spec.Bytes, exceptRunning: !canWait);
        void Start()
        {
            SetChoice(speech, token);
            StartDownload(spec, destination, Apply); // behind a running download, it waits its turn (ModelDownloads.Start)
        }
        if (_downloads.CurrentDestination is not { } runningTo)
        {
            var yes = plainName is null
                ? prompts.ConfirmDownload(spec.FileName, sizeText, question)
                : prompts.ConfirmPlainDownload(spec.FileName, plainName, sizeText, SpaceWarning(spec.Bytes, exceptRunning: !canWait));
            if (yes) Start();
            return;
        }
        var progress = _downloads.Progress ?? "0%";
        var answer = plainName is null
            ? prompts.ConfirmDownloadWhileBusy(spec.FileName, sizeText, question, name, ShortName(runningTo), progress, canWait)
            : prompts.ConfirmPlainDownloadWhileBusy(spec.FileName, plainName, sizeText, SpaceWarning(spec.Bytes, exceptRunning: !canWait),
                PlainNameOf(runningTo), progress, canWait);
        switch (answer)
        {
            case BusyDownloadChoice.DownloadAfter:
                Start();
                break;
            case BusyDownloadChoice.StopAndDownload:
                // Stop cancels the download the question named; one that started while it was open is left alone (lens, 19/10/2026).
                if (_downloads.CurrentDestination is { } now && SamePath(now, runningTo)) _ = CancelThenAsync(plainName ?? name, Start);
                else
                {
                    log.Info($"{ShortName(runningTo)} is no longer downloading, so nothing is stopped; {name} waits its turn.");
                    Start();
                }
                break;
            default:
                log.Info($"{name} not downloaded: the user kept the download in progress ({_downloads.CurrentFile}).");
                break;
        }
    }

    /// <summary>The balloon for a model on its way: downloading now (how far), or waiting its turn. APPROVED 22/10/2026 copy (proposed 19/10/2026).</summary>
    private string OnItsWay(string name, DownloadState state) => state == DownloadState.Running
        ? $"{name} is already downloading ({_downloads.Progress})."
        : $"{name} is already waiting to download.";

    /// <summary>A download's switch: writes <paramref name="values"/> only while <paramref name="token"/> is still the latest choice of its kind.</summary>
    private void ApplyIfLatest(bool speech, string token, string name, (string, string)[] values)
    {
        if ((speech ? _speechChoice : _cleanupChoice) != token)
        {
            log.Info($"{name} arrived, but a later model choice stands, so Dictum does not switch to it.");
            return;
        }
        SetValues(values);
    }

    private void SetChoice(bool speech, string token)
    {
        if (speech) _speechChoice = token;
        else _cleanupChoice = token;
    }

    /// <summary>The short name of the model a download writes (base.en, qwen2.5-1.5b), for the question and its buttons.</summary>
    private static string ShortName(string destination) => destination.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        ? CleanupCatalog.DisplayName(Path.GetFullPath(destination))
        : ModelCatalog.DisplayName(Path.GetFullPath(destination));

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Tray: "No AI cleanup (rule-based tidy)".</summary>
    public void SelectRulesOnly() => SetValues(("llm.provider", LlmSettings.NoProvider));

    /// <summary>Settings > Models or the tray: no clean-up, the text pasted as whisper wrote it (llm.provider = off). Nothing is downloaded or loaded.</summary>
    public void SelectOff() => SetValues(("llm.provider", LlmSettings.Off));

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

    /// <summary>
    /// Stops the running download and any waiting their turn: the Cancel download link (a balloon says it), quit and Remove
    /// Dictum completely (the log only).
    /// </summary>
    public void CancelDownload(DownloadCancelReason reason) => _downloads.Cancel(reason);

    /// <summary>A choice's Stop answer: cancels the download in progress (its balloon names <paramref name="chosen"/>), waits for its .part to go, then runs <paramref name="next"/>.</summary>
    private async Task CancelThenAsync(string chosen, Action next)
    {
        // simplification: Stop also drops any download waiting behind the running one (ModelDownloads.Cancel drops the queue;
        // each is logged, none is ballooned). Ceiling: a model queued with "Download after it" is lost when a third choice
        // answers Stop. Trigger to retire: a report of a queued download vanishing; then cancel only the running job.
        // The refusal (PendingBytes(exceptRunning: true)) and the 2 GB warning (exceptRunning: !canWait, so with three buttons it
        // also counts the running download) likewise still count the downloads that a Stop answer would drop, so a choice that would fit only with both the running and the queued downloads stopped is
        // refused before asking (errs on the safe side). Trigger to retire: a user report of a refusal that Stop would have avoided.
        _downloads.Cancel(DownloadCancelReason.Choice, chosen);
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

    /// <summary>
    /// Decision 15: a download that would not fit on its drive with 200 MB to spare is refused, with a balloon saying so
    /// (<see cref="Storage.DiskSpace.DownloadRefusal"/>), before it is asked and again before it starts. A kept partial download
    /// needs only the rest. Unknown sizes and unknown free space (a network share) go ahead.
    /// </summary>
    private bool RefuseLowSpace(string destination, string fileNames, long? bytes, bool exceptRunning = false) =>
        RefuseLowSpace([(destination, bytes)], fileNames, exceptRunning);

    private bool RefuseLowSpace(IReadOnlyList<(string Destination, long? Bytes)> files, string fileNames, bool exceptRunning = false)
    {
        if (LowSpaceRefusal(files, fileNames, exceptRunning) is not { } refusal) return false;
        balloon(refusal, true);
        return true;
    }

    /// <summary>
    /// Why <paramref name="files"/> would be refused for space, or null. The downloads already running or waiting on that drive
    /// need their space too (lens, 19/10/2026); <paramref name="exceptRunning"/> leaves the running one out.
    /// </summary>
    private string? LowSpaceRefusal(IReadOnlyList<(string Destination, long? Bytes)> files, string fileNames, bool exceptRunning = false)
    {
        long needed = 0;
        foreach (var (destination, bytes) in files)
        {
            if (bytes is not long total) continue;
            var part = new FileInfo(destination + ".part");
            needed += part.Exists && part.Length < total ? total - part.Length : total; // the downloader resumes a kept .part from its end
        }
        if (needed == 0) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(files[0].Destination))!;
        return Storage.DiskSpace.DownloadRefusal(folder, fileNames, needed, _freeBytes, _downloads.PendingBytes(folder, exceptRunning));
    }

    /// <summary>
    /// Settings > Models > Model folder (Decision 15): writes models.folder (null: the per-user default) and reloads, so the next
    /// lookup and download use it. No file is moved. A folder that holds the bundled models, or is inside it, is refused
    /// (<see cref="BundledFolderRefused"/>, returned for the page to show). The earlier name whisper.models_dir is cleared when
    /// set, so the folder chosen here is the one in force. A file that cannot be written is ballooned (<see cref="Write"/>).
    /// </summary>
    /// <param name="reloadAfter">
    /// False for the onboarding (1.2), which runs before the settings are first loaded: writing alone, so nothing reloads or
    /// loads a model mid-way; a file that cannot be written is then returned (<see cref="WriteFailed"/>) instead of ballooned.
    /// </param>
    public string? SetModelsFolder(string? folder, bool reloadAfter = true)
    {
        if (folder is not null && FolderRefusal(folder) is { } refused) return refused;
        var values = new List<(string, JsonNode?)> { ("models.folder", folder is null ? null : JsonValue.Create(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))) };
        if (settings().Whisper.ModelsDir is not null) values.Add(("whisper.models_dir", null));
        if (reloadAfter) Write([.. values]);
        else if (!TryWrite(out _, reloadAfter: false, [.. values])) return WriteFailed;
        return null;
    }

    /// <summary>
    /// APPROVED 22/10/2026 copy (proposed 21/10/2026): a folder named by a device path other than \\?\X:\ or
    /// \\?\UNC\ (a \\?\Volume{GUID}\ path, say), which the settings load refuses for models.folder (SettingsLoader), so it would
    /// be written and then ignored.
    /// </summary>
    public const string DevicePathRefused = "Dictum cannot use a folder named that way. Choose a folder on a drive letter or a network share.";

    /// <summary>Why <paramref name="folder"/> cannot be the model folder (the bundled folder, or a device path the settings load refuses), or null.</summary>
    public string? FolderRefusal(string folder) =>
        paths.IsBundled(folder) ? BundledFolderRefused : Storage.PathRegistry.IsUnsupportedDevicePath(folder) ? DevicePathRefused : null;

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
        log.Info($"Set from the app: {string.Join(", ", values.Select(v => $"{v.Key} = {SettingsFile.LogValue(v.Key, v.Value)}"))}.");
        // A written model choice is the latest of its kind: a download still on its way no longer switches to its own model.
        if (values.Any(v => v.Key == "whisper.model")) _speechChoice = $"written {++_choices}";
        if (values.Any(v => v.Key is "llm.provider" or "llm.model")) _cleanupChoice = $"written {++_choices}";
        if (reloadAfter) reload(); // re-reads settings and hot-swaps the model (hotkey ignored while a speech model loads)
        return true;
    }

    /// <summary>
    /// Tray → Mode and the calibration window (SPEC §16.1, §16.5): when every listed model is on disk, writes the values at
    /// once, cancelling nothing (a download in progress carries on, and its own switch defers to this choice: last choice
    /// wins; decision of 19/10/2026); otherwise asks once (names, total size, host, <paramref name="note"/>), downloads the
    /// missing files one after another through the same downloader (refused under offline_only, as every download is) behind
    /// any download already running, and writes the values only when all have arrived, unless a later choice of a model kind
    /// the values set was made meanwhile (then nothing is written and <paramref name="finished"/> is told false).
    /// </summary>
    /// <param name="note">The question's explanation: what the download is for and whether Dictum then switches to it.</param>
    /// <param name="finished">Told true when the values were applied, false when nothing will be (declined, refused, failed).</param>
    public void ApplyWithDownloads(string note, IReadOnlyList<(DownloadSpec Spec, string Destination)> missing, (string Key, JsonNode? Value)[] values, Action? then = null,
        Action<bool>? finished = null)
    {
        string? speechToken = null, cleanupToken = null; // set once the download is agreed: this preset is then the latest choice of each kind it sets
        bool Stale() => speechToken is not null && _speechChoice != speechToken || cleanupToken is not null && _cleanupChoice != cleanupToken;
        void Finish()
        {
            if (Stale())
            {
                log.Info($"{string.Join(" and ", missing.Select(m => m.Spec.FileName))} arrived, but a later model choice stands, so these settings are not applied.");
                finished?.Invoke(false);
                return;
            }
            Write(values);
            then?.Invoke();
            finished?.Invoke(true);
        }
        if (missing.Count == 0)
        {
            Finish(); // never cancels a download in progress (Store 10.1.2.10, 19/10/2026)
            return;
        }
        // A file already downloading or waiting its turn is not downloaded twice, asked about again or counted twice for space
        // (lens, 19/10/2026): the preset waits for that download instead.
        var toFetch = missing.Where(m => !_downloads.IsFetching(m.Destination)).ToList();
        if (toFetch.Count > 0)
        {
            var names = string.Join(" and ", toFetch.Select(m => m.Spec.FileName));
            if (RefuseOffline(names))
            {
                finished?.Invoke(false);
                return;
            }
            if (toFetch.FirstOrDefault(m => m.Spec.RequiresLogin).Spec is { } gated)
            {
                balloon($"{gated.FileName} {ModelDownloader.LoginRequired}.", true);
                finished?.Invoke(false);
                return;
            }
            if (RefuseLowSpace([.. toFetch.Select(m => (m.Destination, m.Spec.Bytes))], names))
            {
                finished?.Invoke(false);
                return;
            }
            var total = toFetch.Sum(m => m.Spec.Bytes ?? 0);
            if (!prompts.ConfirmDownload(names, ModelCatalog.FormatSize(total), WithSpaceWarning(note, total)))
            {
                finished?.Invoke(false);
                return;
            }
        }
        else
        {
            foreach (var (_, destination) in missing) balloon(OnItsWay(ShortName(destination), _downloads.StateOf(destination)), false); // nothing new to ask about
        }
        if (values.Any(v => v.Key == "whisper.model")) _speechChoice = speechToken = $"chosen {++_choices}";
        if (values.Any(v => v.Key is "llm.provider" or "llm.model")) _cleanupChoice = cleanupToken = $"chosen {++_choices}";
        DownloadInTurn(missing, 0, $"preset {++_choices}", Stale, Finish, () => finished?.Invoke(false));
    }

    /// <summary>
    /// Downloads <paramref name="files"/> one after another, then runs <paramref name="done"/>. A file already on its way is
    /// waited for (its download's arrival or failure continues the chain), and one that arrived meanwhile is skipped. Once the
    /// choice is overtaken (<paramref name="stale"/>), the rest is not downloaded and <paramref name="failed"/> runs (lens, 19/10/2026).
    /// </summary>
    private void DownloadInTurn(IReadOnlyList<(DownloadSpec Spec, string Destination)> files, int index, string key, Func<bool> stale, Action done, Action failed)
    {
        if (index >= files.Count)
        {
            done();
            return;
        }
        if (index > 0 && stale())
        {
            log.Info($"{string.Join(", ", files.Skip(index).Select(f => f.Spec.FileName))} not downloaded: a later model choice stands.");
            failed();
            return;
        }
        void Next() => DownloadInTurn(files, index + 1, key, stale, done, failed);
        var (spec, destination) = files[index];
        if (_downloads.IsFetching(destination)) _downloads.AlsoOnArrival(destination, $"{key} {index}", Next, failed);
        else if (index > 0 && File.Exists(destination)) Next(); // it was on its way when asked, and has arrived since
        else StartDownload(spec, destination, Next, failed);
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
        if (RefuseLowSpace(destination, spec.FileName, spec.Bytes))
        {
            onFailure?.Invoke();
            return;
        }
        _downloads.Start(TestSpecs?.Invoke(spec) ?? spec, destination, onSuccess, onFailure);
    }
}
