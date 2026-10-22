using System.Text.Json.Nodes;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    private string? _offlineWarned;
    private string? _redirectWarned;
    private bool _cloudWithdrawn; // Switch cloud clean-up off, until both files keep it or cloud clean-up is switched on again (DisableCloud, SaveCloudOff, EnableCloud)
    private bool _consentWithdrawn; // its consent withdrawal (also set when EnableCloud cannot delete the consent it just recorded), until consent.json is gone or "Allow" records a new consent; a server on this PC switched on does not end it
    private (string? Consent, string? Settings) _cloudOffNotSaved; // why the last try could not delete consent.json or save settings.json (SaveCloudOff)
    private bool _offlineOnlyThisSession; // offline only switched on, until settings.json keeps it (SetOfflineOnly, SaveOfflineOnly)
    private string? _offlineOnlyNotSaved; // why the last try could not save it (SaveOfflineOnly)

    private void LoadConfiguration(bool isReload)
    {
        var config = ConfigLoader.Load(Paths, _redactor, Environment.GetEnvironmentVariable, ProbeGpu, _log.Warn, _options.CleanupOverride,
            previous: isReload ? _settings : null);
        if (config.Problem is { } problem)
        {
            _log.Warn(problem);
            Dispatcher.UIThread.Post(() => Notify("Dictum settings", problem, warning: true));
        }
        else if (config.File.Warnings.Count > 0 && _tray is not null)
        {
            Notify("Dictum settings", $"{config.File.Warnings.Count} setting(s) were invalid and use defaults. Details are in the log.", warning: true);
        }
        _settings = SessionChoices(config.Settings, config.Secrets);
        _modelFileChanges++; // another models folder, or a file chosen, may be in force now (ModelFilesVersion)
        if (_fileLog is not null) _fileLog.MinLevel = LogExtensions.ParseLevel(_settings.LogLevel);
        var personal = Personal.Load(); // dictionary, corrections, profanity list (SPEC §16)
        var outgoing = _cleaner;
        _cleaner = TranscriptCleaner.Create(_settings.Llm, _settings.OfflineOnly, config.Secrets, _http, _log, () => _llm.Ready,
            personal.ForCleanup(_settings.Profanity), WhisperLanguages.OutputIsEnglish(_settings.Whisper.Language, _settings.Whisper.Translate));
        // A request it still sends is cancelled: that dictation gets the rule-based tidy and nothing more goes out on the old
        // settings. The local model sends nothing, so its clean-up under way finishes (a dictionary word, a finished download).
        if (outgoing.SendsText) outgoing.Retire();
        _llm.SetPrompt(_cleaner.SystemPrompt); // a loaded model re-evaluates a changed prompt now, not during the next dictation
        var decode = new DecodeOptions(personal.Dictionary.WhisperPrompt, _settings.Whisper.BeamSize, _settings.Whisper.Language, _settings.Whisper.Translate);
        _models.SetDecoding(decode);
        // Cloud speech (speech.*) sends what the local decoder gets; a request the old one still has under way is cancelled
        // and heard on this PC instead, so nothing more goes out on settings the pages no longer show.
        var outgoingSpeech = _speech;
        _speech = CloudSpeech.Create(_settings.Speech, decode, _settings.OfflineOnly, config.Secrets, _http, _log, _models.GetAsync);
        outgoingSpeech.Retire();
        if (TranscriptCleaner.OfflineRefusal(_settings.Llm, _settings.OfflineOnly) is { } refusal && _offlineWarned != refusal)
        {
            _offlineWarned = refusal; // once per distinct refusal
            Dispatcher.UIThread.Post(() => Notify("Dictum: offline only", refusal + ". The rule-based tidy is used.", warning: true));
        }
        if (TranscriptCleaner.AnthropicRedirectWarning(_settings.Llm) is not null && _redirectWarned != _settings.Llm.BaseUrl)
        {
            _redirectWarned = _settings.Llm.BaseUrl; // once per URL, not on every reload
            Dispatcher.UIThread.Post(() => Notify("Dictum", $"Anthropic requests will go to {new Uri(_settings.Llm.BaseUrl!).Host} (llm.base_url).", warning: true));
        }
        _log.Info($"{(isReload ? "Settings reloaded" : "Settings loaded")}: hotkey {_settings.Hotkey}, model {_settings.Whisper.Model}, insert {_settings.Insert.Method}, cleanup {_cleaner.Status}, speech {_speech.Status.TrimEnd('.')}.");
    }

    /// <summary>
    /// What this session's choices keep in force over the files, applied after every load until the files keep them
    /// (<see cref="SaveSessionChoices"/>): once cloud clean-up is switched off (<see cref="DisableCloud"/>) no server is used,
    /// on this PC or not, whatever settings.json still says, and no consent applies, whatever consent.json still says, until it
    /// is gone or "Allow" records a new one, also after a server on this PC is switched on (<see cref="EnableCloud"/>); offline
    /// only switched on stays on (<see cref="SetOfflineOnly"/>). Each time a choice overrides the settings loaded, the log says
    /// so in one line.
    /// </summary>
    private AppSettings SessionChoices(AppSettings settings, SecretStore secrets)
    {
        if (_offlineOnlyThisSession && !settings.OfflineOnly)
        {
            _log.Info("Offline only is on for this session: the settings loaded have it off, because settings.json does not keep it yet.");
            settings = settings with { OfflineOnly = true };
        }
        var anthropicKey = secrets.Get(SecretStore.AnthropicKey) is not null;
        if (_cloudWithdrawn && settings.Llm.Provider is (LlmSettings.Anthropic or LlmSettings.OpenAiCompatible))
        {
            var host = CloudPolicy.CloudHost(settings.Llm) ?? CloudPolicy.LocalServerHost(settings.Llm);
            _log.Info($"Cloud clean-up is off for this session: the settings loaded name {settings.Llm.Provider}{(host is null ? "" : $" at {host}")}, which is not used, because the switch-off is not saved yet.");
            var local = settings with { Llm = settings.Llm with { Provider = LlmSettings.Local, CloudConsent = null } };
            return EffectiveSettings.Cleanup(local, anthropicKey, () => ProbeGpu(local), _log.Warn);
        }
        // Only a consent on file lets a host off this PC through the load (EffectiveSettings): while the withdrawal is not
        // saved, that consent is dropped and the repair runs again, so the local model is used.
        if (_consentWithdrawn && CloudPolicy.CloudHost(settings.Llm) is { } cloudHost)
        {
            _log.Info($"Cloud clean-up consent is withdrawn for this session: the consent to {cloudHost} in consent.json is not used, because consent.json is not deleted yet.");
            var unconsented = settings with { Llm = settings.Llm with { CloudConsent = null } };
            return EffectiveSettings.Cleanup(unconsented, anthropicKey, () => ProbeGpu(unconsented), _log.Warn);
        }
        return settings;
    }

    /// <summary>
    /// Saves what this session holds over the files (<see cref="SessionChoices"/>) to the files the next start reads: after
    /// every reload, once the load has put the choices in effect (so clearing read-only on settings.json or fixing it by hand
    /// is enough: the folder watcher reloads on both; a program letting go of a file it held open is not seen, so that file is
    /// tried again at the next reload of any kind), and once at quit (<see cref="Dispose"/>). A hold ends when its files keep
    /// it. A file that already keeps a choice is not written again, so the reload the watcher makes after a write finds
    /// nothing to do. No balloon: the page that made the choice said what could not be saved.
    /// </summary>
    private void SaveSessionChoices()
    {
        if (_cloudWithdrawn || _consentWithdrawn) SaveCloudOff();
        if (_offlineOnlyThisSession) SaveOfflineOnly();
    }

    /// <summary>
    /// Cloud clean-up off (<see cref="DisableCloud"/>): deletes consent.json if it is there, which ends the consent withdrawal,
    /// and, while the switch-off holds, writes the local model to settings.json (llm.provider, and the recommended llm.model)
    /// while that file names anthropic or openai-compatible, or cannot be read. A missing settings.json loads as the defaults,
    /// which name the local model, so it is never created: the next start then asks the first-run questions and uses no
    /// server. One log line per try.
    /// </summary>
    private void SaveCloudOff()
    {
        string? consent = null, settings = null;
        try
        {
            CloudConsentFile.WithdrawCleanup(Paths.ConsentFile); // a speech consent on file stays (lens F1, 22/10/2026)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            consent = SettingsFile.ShortReason(ex, Paths.ConsentFile);
        }
        _consentWithdrawn = consent is not null;
        if (!_cloudWithdrawn)
        {
            // A server on this PC was switched on since (EnableCloud): only the consent is left to withdraw.
            if (consent is null) _log.Info("Cloud clean-up consent withdrawal is saved: there is no consent.json.");
            else _log.Warn($"Cloud clean-up consent withdrawal is not saved yet (consent.json could not be deleted: {consent}); the consent on file is not used this session, and deleting it is tried again at the next reload and at quit.");
            return;
        }
        var file = SettingsLoader.LoadFile(Paths.SettingsFile);
        if (!file.Usable || file.Settings.Llm.Provider is LlmSettings.Anthropic or LlmSettings.OpenAiCompatible)
        {
            Models.TryWrite(out settings, reloadAfter: false,
                ("llm.provider", JsonValue.Create(LlmSettings.Local)), ("llm.model", JsonValue.Create(CleanupCatalog.Recommend(GpuMachine).Model)));
        }
        _cloudOffNotSaved = (consent, settings);
        _cloudWithdrawn = consent is not null || settings is not null;
        if (!_cloudWithdrawn)
        {
            _log.Info("Cloud clean-up off is saved: there is no consent.json, and settings.json names no cloud service.");
            return;
        }
        var why = new[] { consent is null ? null : $"consent.json could not be deleted: {consent}", settings is null ? null : $"settings.json could not be saved: {settings}" };
        _log.Warn($"Cloud clean-up off is not saved yet ({string.Join("; ", why.OfType<string>())}); it stays off for this session, and saving it is tried again at the next reload and at quit.");
    }

    /// <summary>
    /// Offline only switched on (<see cref="SetOfflineOnly"/>): writes offline_only = true to settings.json unless the file can
    /// be read and has it on. That is the switch's own write, so it creates a missing settings.json as the switch does. One log
    /// line per try.
    /// </summary>
    private void SaveOfflineOnly()
    {
        var file = SettingsLoader.LoadFile(Paths.SettingsFile);
        string? failure = null;
        if (!(file.Usable && file.Settings.OfflineOnly)) Models.TryWrite(out failure, reloadAfter: false, ("offline_only", JsonValue.Create(true)));
        _offlineOnlyNotSaved = failure;
        _offlineOnlyThisSession = failure is not null;
        if (failure is null) _log.Info("Offline only is saved: settings.json has it on.");
        else _log.Warn($"Offline only is not saved yet (settings.json could not be saved: {failure}); it stays on for this session, and saving it is tried again at the next reload and at quit.");
    }

    private bool ProbeGpu(AppSettings settings)
    {
        WhisperEngine.ProbeRuntime(settings.Whisper.Gpu, _log);
        return _gpuMachine();
    }

    /// <summary>Writes settings.json keys chosen in a window, then reloads (and hot-swaps a changed model). False: the file could not be written.</summary>
    public bool Write(params (string Key, JsonNode? Value)[] values) => Models.Write(values);

    /// <param name="userRequested">
    /// Settings → About → Reload settings: always re-installs the hook (the documented recovery if Windows removed it). A
    /// file-watcher reload re-installs it only when the hotkey changed, so a key held while settings.json is saved keeps
    /// its tracked state and its key-up is still seen.
    /// </param>
    public void Reload(bool userRequested)
    {
        var previousPath = Models.ModelPath;
        var previous = _settings;
        LoadConfiguration(isReload: true);
        SaveSessionChoices(); // after the load: what this session holds is in effect before either file is changed
        _controller?.UpdateOptions(AppRules.Options(_settings));
        ApplyEnabled(_settings.Enabled, persist: false);
        if (userRequested || AppRules.HotkeyChanged(previous, _settings))
        {
            _hook?.Dispose();
            _hook = null;
            StartHook();
        }
        ApplyFixHotkey(_settings, reinstall: userRequested);
        Models.Ensure(previousPath, startup: false);
        Models.EnsureCleanup();
        StatusChanged();
    }

    private void DebouncedReload()
    {
        _reloadDebounce.Stop();
        try
        {
            Reload(userRequested: false);
        }
        catch (Exception ex)
        {
            _log.Error($"Reload failed: {ex}");
        }
    }

    /// <summary>The switch at the top of the tray panel: dictation on or off, saved as "enabled".</summary>
    public void SetEnabled(bool enabled) => ApplyEnabled(enabled, persist: true);

    /// <summary>
    /// Settings > Privacy > Offline only, saved as "offline_only". Switched on, it takes effect in memory first, before
    /// settings.json is written (<see cref="SessionChoices"/>), and the reload then saves it (<see cref="SaveSessionChoices"/>).
    /// When it cannot be saved it stays on, <see cref="OfflineOnlyNotSaved"/> says why, and saving it is tried again at every
    /// reload and once at quit. Switched off, nothing changes unless the file is saved (<see cref="ModelCoordinator.WriteFailed"/>).
    /// Null: saved.
    /// </summary>
    public string? SetOfflineOnly(bool on)
    {
        if (on)
        {
            _offlineOnlyThisSession = true;
            Reload(userRequested: false);
            return _offlineOnlyNotSaved is { } why ? OfflineOnlyNotSaved(why) : null;
        }
        if (Models.TryWrite(out var failure, reloadAfter: false, ("offline_only", JsonValue.Create(false))))
        {
            _offlineOnlyThisSession = false; // the file holds the choice now
            Reload(userRequested: false);
            return null;
        }
        _log.Warn($"Could not write \"offline_only\" to settings.json ({failure}).");
        StatusChanged(); // the switch shows the settings in use again: offline only stays on
        return ModelCoordinator.WriteFailed;
    }

    /// <summary>
    /// APPROVED 22/10/2026 copy (proposed 16/10/2026, fifth draft; this sentence is unchanged since the fourth).
    /// Pinned word for word in CloudOffTakesEffectTests; do not reword it.
    /// Shown while offline only is on for this session and settings.json does not have it on. No "Nothing is sent": offline
    /// only still allows a server on this PC. The last clause names the start with a settings.json that still cannot be read
    /// (not valid JSON, say), which runs offline with the defaults (ConfigLoader.BrokenAtStartup).
    /// </summary>
    internal static string OfflineOnlyNotSaved(string reason) =>
        $"Offline only is on for this session, but settings.json could not be saved ({reason}). Dictum tries again at the next reload and when it quits; if it is still not saved then, the next start runs without offline only, unless settings.json cannot be read, in which case Dictum starts offline.";

    private void ApplyEnabled(bool enabled, bool persist)
    {
        if (persist && !SettingsFile.SetValue(Paths.SettingsFile, "enabled", JsonValue.Create(enabled)))
        {
            _log.Warn("Could not write \"enabled\" to settings.json (file unreadable or not valid JSON); the change lasts until restart.");
        }
        if (persist) _settings = _settings with { Enabled = enabled };
        ApplyHookEnabled();
        if (_fixHook is not null) _fixHook.Enabled = enabled && _hotkeyPauses == 0;
        _controller?.SetEnabled(enabled);
        _log.Info(enabled ? "Dictation enabled." : "Dictation disabled.");
        StatusChanged();
    }

    private void StartHook()
    {
        var spec = HotkeyParser.Parse(_settings.Hotkey); // already validated by the loader
        var matcher = new HotkeyMatcher(spec, _settings.SuppressHotkey, Platform.IsKeyDown);
        _hook = Platform.CreateHotkeyListener(matcher, OnHotkey, _log);
        _hookUsesMouse = spec.UsesMouse;
        ApplyHookEnabled();
        if (!_hook.Start())
        {
            _tray?.SetState(TrayState.Error);
            Notify("Dictum", "The global hotkey could not be registered. See the log for details.", warning: true);
        }
    }

    /// <summary>
    /// The one rule for the dictation listener: on while dictation is enabled and no capture box pauses it, and, for a
    /// mouse-button hotkey, only while a speech model is ready or a dictation is under way. A mouse button is always
    /// swallowed (HotkeyMatcher), so with no model to dictate with (the first-run download, a failed load, a model swap) it
    /// would vanish from every app for nothing; it keeps its normal job until the model is ready. A dictation already under
    /// way keeps the listener on: a model load that starts mid-dictation (a download finishing, a model swap) must not cut
    /// off its release, its stop tap or Esc. A keyboard hotkey stays on (the controller ignores a press while no model is
    /// ready). Called on every change it depends on: the listener, enabled, a pause, each model state change, and each
    /// dictation state the controller reports (SetTrayState), so the listener goes off again when that dictation ends.
    /// </summary>
    private void ApplyHookEnabled()
    {
        if (_hook is not null) _hook.Enabled = _settings.Enabled && _hotkeyPauses == 0 && (!_hookUsesMouse || _models.IsReady || _controller is { IsIdle: false });
    }

    /// <summary>From the hook thread: to the window that asked for the hotkey (calibration's "or hold Right Ctrl"), else to dictation.</summary>
    private void OnHotkey(HookEvent e)
    {
        if (Volatile.Read(ref _hotkeyRedirect) is { } redirect) Dispatcher.UIThread.Post(() => redirect(e));
        else _controller?.Post(e);
    }

    /// <summary>While a window holds this, the hotkey and Esc go to it instead of starting a dictation.</summary>
    public IDisposable RedirectHotkey(Action<HookEvent> to)
    {
        Volatile.Write(ref _hotkeyRedirect, to);
        return new Undo(() => Interlocked.CompareExchange(ref _hotkeyRedirect, null, to));
    }

    /// <summary>While the "Press your hotkey" box listens, both hotkeys (dictation and fix_hotkey) pass through and do nothing.</summary>
    public IDisposable PauseHotkey()
    {
        _hotkeyPauses++;
        ApplyHookEnabled();
        if (_fixHook is not null) _fixHook.Enabled = false;
        return new Undo(() =>
        {
            _hotkeyPauses = Math.Max(0, _hotkeyPauses - 1);
            ApplyHookEnabled();
            if (_fixHook is not null) _fixHook.Enabled = _settings.Enabled && _hotkeyPauses == 0;
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) undo();
        }
    }

    /// <summary>(Re)installs the fix_hotkey listener when the setting changes; it follows "enabled" like the dictation hotkey.</summary>
    private void ApplyFixHotkey(AppSettings s, bool reinstall)
    {
        if (reinstall || s.FixHotkey != _fixHotkey || s.SuppressHotkey != _fixSuppress)
        {
            _fixHook?.Dispose();
            _fixHook = null;
            (_fixHotkey, _fixSuppress) = (s.FixHotkey, s.SuppressHotkey);
            if (s.FixHotkey is { } key && HotkeyParser.TryParse(key, out var spec, out _))
            {
                var hook = Platform.CreateHotkeyListener(new HotkeyMatcher(spec, s.SuppressHotkey, Platform.IsKeyDown), e =>
                {
                    if (e.Signal == HotkeySignal.Down) Dispatcher.UIThread.Post(() => OpenFixLast(fromHotkey: true));
                }, _log);
                if (hook.Start()) _fixHook = hook;
                else
                {
                    hook.Dispose(); // its thread and a keyboard hook that did install must not live on unreferenced
                    Notify("Dictum", "The fix_hotkey could not be registered. See the log for details.", warning: true);
                }
            }
        }
        if (_fixHook is not null) _fixHook.Enabled = s.Enabled && _hotkeyPauses == 0;
    }

    /// <summary>The paste, on the UI thread (the clipboard is used there); the token is checked before the first keystroke.</summary>
    private Task InsertAsync(string text, CancellationToken token) =>
        Dispatcher.UIThread.InvokeAsync(() => _inserter.InsertAsync(text, _settings.Insert, token));

    /// <summary>The pipeline pasted <paramref name="text"/>: kept in memory with the window it went to, for Fix last dictation.</summary>
    private void RememberPasted(string text)
    {
        var target = Platform.WindowFlags.ForegroundWindow();
        Personal.Remember(text, target, Platform.WindowFlags.AppName(target));
    }

    private void WatchConfigFolder()
    {
        Directory.CreateDirectory(Paths.ConfigDir);
        _watcher = new FileSystemWatcher(Paths.ConfigDir)
        {
            // Attributes: clearing read-only is a change that reloads, so the reload can save what this session holds (SaveSessionChoices).
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.Attributes,
            IncludeSubdirectories = false,
        };
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (AppRules.ReloadsOn(e.ChangeType, e.Name, (e as RenamedEventArgs)?.OldName))
                Dispatcher.UIThread.Post(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
        }
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += (s, e) => Changed(s, e);
        _watcher.Deleted += Changed;
        _watcher.EnableRaisingEvents = true;
    }
}
