using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Insert;
using Dictum.Llm;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.Hotkey;
using Dictum.UI.Overlays;

namespace Dictum.UI.Shell;

/// <summary>What the entry point passes in from the command line and the startup migration.</summary>
public sealed record ShellOptions(string? ModelOverride = null, string? CleanupOverride = null, int? ExitAfterSeconds = null, MigrationReport? Migration = null);

/// <summary>
/// The tray app's composition root (what AppHost was in the WPF build): it builds the pieces, owns load / reload / enable /
/// quit, routes the hotkey, and opens the windows. Platform services come only through <see cref="IPlatform"/>; dialogs
/// through <see cref="IShellDialogs"/>. Runs on the UI thread except where a member says otherwise.
/// </summary>
public sealed partial class AppShell : IDisposable
{
    private readonly ILog _log;
    private readonly FileLog? _fileLog;
    private readonly SecretRedactor _redactor;
    private readonly ShellOptions _options;
    private readonly HttpClient _http; // cleanup providers; callers bound each request
    private readonly HttpClient _downloadHttp; // model downloads only
    private readonly HttpClient _updateHttp; // Check for updates only: follows no redirect
    private readonly ModelManager _models;
    private readonly LocalLlmManager _llm;
    private readonly IAudioCapture _capture;
    private readonly TextInserter _inserter;
    private readonly DispatcherTimer _reloadDebounce;
    private readonly Func<bool> _gpuMachine;
    private volatile AppSettings _settings = new();
    private volatile TranscriptCleaner _cleaner = TranscriptCleaner.RulesOnly("not configured yet");
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private ITrayHost? _tray;
    private OverlayPresenter? _overlays;
    private IHotkeyListener? _hook;
    private bool _hookUsesMouse; // the dictation listener's hotkey names a mouse button (ApplyHookEnabled)
    private IHotkeyListener? _fixHook;
    private string? _fixHotkey;
    private bool _fixSuppress;
    private DictationController? _controller;
    private FileSystemWatcher? _watcher;
    private IInstanceLock? _instance;
    private IDisposable? _otherLaunches;
    private Action<HookEvent>? _hotkeyRedirect;
    private int _hotkeyPauses;
    private bool _disposed;

    /// <param name="instance">The single-instance lock the entry point took; this shell listens on it for later launches.</param>
    /// <param name="gpuMachine">Tests decide; production asks the whisper runtime (<see cref="WhisperEngine.GpuMachine"/>).</param>
    /// <param name="speechModels">Tests pass one that loads a fake engine; production builds the whisper one.</param>
    /// <param name="downloadHandler">
    /// Tests pass a fake server to stand in for the network under the model-download client's offline_only guard;
    /// production uses the socket handler.
    /// </param>
    /// <param name="updateHandler">The same for the Check for updates client; production uses its socket handler, which follows no redirect.</param>
    /// <param name="cleanupModels">Tests pass one that loads a fake engine; production builds the llama.cpp one.</param>
    public AppShell(IPlatform platform, AppPaths paths, ILog log, SecretRedactor redactor, ShellOptions options, IShellDialogs? dialogs = null,
        IInstanceLock? instance = null, Func<bool>? gpuMachine = null, ModelManager? speechModels = null, HttpMessageHandler? downloadHandler = null,
        HttpMessageHandler? updateHandler = null, LocalLlmManager? cleanupModels = null)
    {
        Platform = platform;
        Paths = paths;
        _log = log;
        _fileLog = log as FileLog;
        _redactor = redactor;
        _options = options;
        _instance = instance;
        _gpuMachine = gpuMachine ?? (() => WhisperEngine.GpuMachine);
        Dialogs = dialogs ?? new ShellDialogs();
        (_http, _downloadHttp, _updateHttp) = AppRules.CreateClients(() => _settings.OfflineOnly, downloadHandler, updateHandler);
        Integrity = new ModelIntegrity(paths.VerifiedModelsFile, log, () => ModelFolders.LookupOrder(paths, _settings));
        _llm = cleanupModels ?? new LocalLlmManager(log, Integrity);
        _models = speechModels ?? new ModelManager(log, Integrity);
        Models = new ModelCoordinator(paths, _models, _llm, _downloadHttp, log, () => _settings, options.ModelOverride, Dialogs, Environment.GetEnvironmentVariable,
            reload: () => Reload(userRequested: false), shutdown: Quit, refreshTooltip: StatusChanged,
            balloon: (text, warning) => Notify("Dictum", text, warning), integrity: Integrity, gpuMachine: gpuMachine);
        Personal = new PersonalService(paths, log, () => _settings, Models, reload: () => Reload(userRequested: false), _gpuMachine);
        _capture = platform.CreateCapture(log);
        _inserter = new TextInserter(platform.Clipboard, platform.CreateInputSender(log), log);
        _reloadDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => DebouncedReload()) { IsEnabled = false };
        // From construction, not from Start: nothing loads before Start, and the hotkey listener must follow every change.
        _models.Changed += () => Dispatcher.UIThread.Post(() => ModelStateChanged(_models.LastError, ref _speechErrorShown, "Speech model"));
        _llm.Changed += () => Dispatcher.UIThread.Post(() => ModelStateChanged(_llm.LastError, ref _cleanupErrorShown, "Clean-up model"));
    }

    public IPlatform Platform { get; }

    public AppPaths Paths { get; }

    public ILog Log => _log;

    public IShellDialogs Dialogs { get; }

    public ModelCoordinator Models { get; }

    public PersonalService Personal { get; }

    public ModelIntegrity Integrity { get; }

    /// <summary>The settings in use (repaired, overrides applied).</summary>
    public AppSettings Settings => _settings;

    public bool GpuMachine => _gpuMachine();

    /// <summary>The microphone's level now (0-1), for the meters; 0 when not recording.</summary>
    public float CurrentLevel => _capture.CurrentLevel;

    /// <summary>The clean-up step's own description ("local/qwen2.5-1.5b on this PC (nothing leaves it)").</summary>
    public string CleanupStatus => _cleaner.Status;

    /// <summary>
    /// The key the clean-up in use is waiting for (OPENAI_API_KEY, GEMINI_API_KEY, ANTHROPIC_API_KEY), or null: the cleaner built at the last
    /// load found none, so nothing is sent until a reload finds it (<see cref="TranscriptCleaner.MissingKey"/>).
    /// </summary>
    public string? CleanupWaitingForKey => _cleaner.MissingKey;

    /// <summary>The clean-up step in use, with its prompt and checks.</summary>
    internal TranscriptCleaner Cleaner => _cleaner;

    /// <summary>A speech model is loaded and no load is in progress (the hotkey works).</summary>
    public bool SpeechReady => _models.IsReady;

    public bool SpeechLoading => _models.IsLoading;

    public string? SpeechError => _models.LastError;

    public bool CleanupLoading => _llm.IsLoading;

    public bool CleanupReady => _llm.Ready is not null;

    public string? CleanupError => _llm.LastError;

    /// <summary>The dictation state machine is idle (no recording or processing), so a window may take focus.</summary>
    public bool DictationIdle => _controller?.IsIdle ?? true;

    /// <summary>Raised on the UI thread whenever anything the tray panel or Settings show may have changed.</summary>
    public event Action? Changed;

    /// <summary>Builds the tray app. Returns false when the user chose Quit in the first-run picker.</summary>
    public bool Start(IClassicDesktopStyleApplicationLifetime? lifetime)
    {
        _lifetime = lifetime;
        var file = SettingsLoader.LoadFile(Paths.SettingsFile);
        var (askSpeech, askCleanup) = AppRules.FirstRunQuestions(file);
        if (askSpeech || askCleanup)
        {
            // With base.en on this PC (it comes with Dictum) no model question is asked; the hotkey check below stays.
            if (!Models.FirstRun(askSpeech, askCleanup, file.Settings, keepProvider: file.NamesCleanupProvider))
            {
                _log.Info("First-run picker: user chose Quit.");
                Quit();
                return false;
            }
            if (askSpeech && _options.ModelOverride is null && SettingsFile.ModelPathOverride(Environment.GetEnvironmentVariable) is null) FirstRunHotkeyCheck();
        }
        LoadConfiguration(isReload: false);

        _overlays = new OverlayPresenter(this);
        _tray = Platform.CreateTray(point => Dispatcher.UIThread.Post(() => ToggleFlyout(point)), _log);

        var machine = new DictationStateMachine(AppRules.Options(_settings), _settings.Enabled);
        var pipeline = new DictationPipeline(_models.GetAsync, () => _cleaner, InsertAsync, () => _settings, _log, Personal.Finish, RememberPasted,
            names: () => Personal.Text.Dictionary.Terms);
        _controller = new DictationController(machine, _capture, pipeline, new UiBridge(this), () => _settings, _log, hotkeyBlocked: () => !_models.IsReady);
        StartHook();
        ApplyFixHotkey(_settings, reinstall: false);
        if (!_settings.Enabled) _tray.SetState(TrayState.Disabled);

        Models.Ensure(previousPath: null, startup: true);
        Models.EnsureCleanup();
        WatchConfigFolder();
        _otherLaunches = _instance?.ListenForOtherLaunches(() => Dispatcher.UIThread.Post(() =>
            Notify("Dictum is already running", "Click its icon in the notification area (system tray).", warning: false)));
        StartupNotices();
        ScheduleDiagnosticExit();
        StatusChanged();
        return true;
    }

    private string? _speechErrorShown;
    private string? _cleanupErrorShown;

    /// <summary>
    /// Brings a mouse-button hotkey's listener in line with the speech model (<see cref="ApplyHookEnabled"/>), refreshes the
    /// status and shows a load failure (for example a SHA-256 mismatch) once per distinct error.
    /// </summary>
    private void ModelStateChanged(string? error, ref string? shown, string what)
    {
        ApplyHookEnabled();
        StatusChanged();
        if (error is null || error == shown) return;
        shown = error;
        if (LoadBalloon(error, what, bothShown: _speechErrorShown == _cleanupErrorShown) is { } text) Notify("Dictum", text, warning: true);
    }

    /// <summary>
    /// What the start-up notices say about the migration, and whether to offer removing the earlier build. A packaged
    /// copy copied: Dictate's folders stay whole (the user's only copy outside the package, whose data Windows deletes
    /// at uninstall), so there is nothing to offer and Remove Dictum completely never touches them.
    /// </summary>
    internal static (IReadOnlyList<(string Text, bool Warning)> Notices, bool OfferRemoval) MigrationNotices(MigrationReport migration)
    {
        var notices = new List<(string, bool)>();
        var verb = migration.Copied ? "copy" : "move";
        if (migration.Left.Count > 0)
            notices.Add(($"Some of Dictate's files are in use (is Dictate still running?). Quit Dictate, then restart Dictum to {verb} the rest.", true));
        else if (migration.Moved.Count > 0 && migration.Copied)
            notices.Add(("Your Dictate settings, models and recordings were copied into this Store copy of Dictum. Dictate's own folders are left as they were: Windows deletes this copy's data when Dictum is uninstalled.", false));
        else if (migration.Moved.Count > 0)
            notices.Add(("Your Dictate settings, models and recordings were moved to Dictum.", false));
        if (migration.Skipped.Count > 0)
        {
            notices.Add((migration.Copied
                ? $"{migration.Skipped.Count} of Dictate's files were not copied because Dictum already has files of those names (the log lists them)."
                : $"{migration.Skipped.Count} of Dictate's files were not moved because Dictum already has files of those names (the log lists them). They stay in Dictate's folders; Remove Dictum completely deletes them too.", true));
        }
        return (notices, migration.Moved.Count > 0 && migration.Left.Count == 0 && !migration.Copied);
    }

    /// <summary>The one-time notices (the entry point logs each migrated item): what the migration moved or could not move, and a second install (SPEC §18.2, §21).</summary>
    private void StartupNotices()
    {
        if (_options.Migration is { } migration)
        {
            var (notices, offerRemoval) = MigrationNotices(migration);
            foreach (var (text, warning) in notices) Notify("Dictum", text, warning);
            if (offerRemoval) OfferEarlierBuildRemoval();
        }
        if (Platform.Package.OtherInstall() is { } other)
        {
            _log.Warn(other);
            Notify("Dictum", other, warning: true);
        }
    }

    /// <summary>A balloon / banner from the tray icon (logged too, so a missed one can be found).</summary>
    public void Notify(string title, string text, bool warning)
    {
        if (warning) _log.Warn($"Notice: {text}");
        if (Dispatcher.UIThread.CheckAccess()) _tray?.Notify(title, text, warning);
        else Dispatcher.UIThread.Post(() => _tray?.Notify(title, text, warning));
    }

    /// <summary>Tray panel → Quit (and --exit-after): the same shutdown either way.</summary>
    public void Quit()
    {
        if (_lifetime is not null) _lifetime.Shutdown();
        else Dispose();
    }

    /// <summary>--exit-after: runs the same shutdown as Quit after N seconds (diagnostics and scripted checks).</summary>
    private void ScheduleDiagnosticExit()
    {
        if (_options.ExitAfterSeconds is not int seconds) return;
        DispatcherTimer.RunOnce(() =>
        {
            _log.Info($"--exit-after {seconds}: shutting down the same way as Quit.");
            Quit();
        }, TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Models.CancelDownload(DownloadCancelReason.Quit); // a download still running at quit stops and deletes its .part
        _watcher?.Dispose();
        SaveSessionChoices(); // once at quit, the last try to keep this session's choices; with the watcher gone, nothing reloads
        _otherLaunches?.Dispose();
        _hook?.Dispose();
        _fixHook?.Dispose();
        CloseWindows();
        _controller?.Dispose();
        (_capture as IDisposable)?.Dispose();
        _tray?.Dispose(); // the icon goes at once; releasing the models below can wait for a warm-up still running
        _overlays?.Dispose();
        _models.Dispose();
        _llm.Dispose();
        _http.Dispose();
        _downloadHttp.Dispose();
        _updateHttp.Dispose();
    }
}
