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
    private bool _cloudWithdrawn; // Switch cloud clean-up off, until cloud clean-up is switched on again (DisableCloud, EnableCloud)
    private bool _offlineOnlyThisSession; // offline only switched on but not saved to settings.json (SetOfflineOnly)

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
        if (_fileLog is not null) _fileLog.MinLevel = LogExtensions.ParseLevel(_settings.LogLevel);
        var personal = Personal.Load(); // dictionary, corrections, profanity list (SPEC §16)
        var outgoing = _cleaner;
        _cleaner = TranscriptCleaner.Create(_settings.Llm, _settings.OfflineOnly, config.Secrets, _http, _log, () => _llm.Ready,
            personal.ForCleanup(_settings.Profanity), WhisperLanguages.OutputIsEnglish(_settings.Whisper.Language, _settings.Whisper.Translate));
        // A request it still sends is cancelled: that dictation gets the rule-based tidy and nothing more goes out on the old
        // settings. The local model sends nothing, so its clean-up under way finishes (a dictionary word, a finished download).
        if (outgoing.SendsText) outgoing.Retire();
        _llm.SetPrompt(_cleaner.SystemPrompt); // a loaded model re-evaluates a changed prompt now, not during the next dictation
        _models.SetDecoding(new DecodeOptions(personal.Dictionary.WhisperPrompt, _settings.Whisper.BeamSize, _settings.Whisper.Language, _settings.Whisper.Translate));
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
        _log.Info($"{(isReload ? "Settings reloaded" : "Settings loaded")}: hotkey {_settings.Hotkey}, model {_settings.Whisper.Model}, insert {_settings.Insert.Method}, cleanup {_cleaner.Status}.");
    }

    /// <summary>
    /// What this session's choices keep in force over the files, applied after every load: once cloud clean-up is switched off
    /// (<see cref="DisableCloud"/>) no server is used, on this PC or not, and no consent applies, whatever settings.json and
    /// consent.json still say; offline only switched on stays on when settings.json could not save it (<see cref="SetOfflineOnly"/>).
    /// </summary>
    private AppSettings SessionChoices(AppSettings settings, SecretStore secrets)
    {
        if (_offlineOnlyThisSession) settings = settings with { OfflineOnly = true };
        if (!_cloudWithdrawn || settings.Llm.Provider is not (LlmSettings.Anthropic or LlmSettings.OpenAiCompatible)) return settings;
        var local = settings with { Llm = settings.Llm with { Provider = LlmSettings.Local, CloudConsent = null } };
        return EffectiveSettings.Cleanup(local, secrets.Get(SecretStore.AnthropicKey) is not null, () => ProbeGpu(local), _log.Warn);
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
    /// Settings > Privacy > Offline only, saved as "offline_only". Switched on, it takes effect even when settings.json cannot be
    /// saved: it then stays on until Dictum quits (<see cref="SessionChoices"/>) and <see cref="OfflineOnlyNotSaved"/> says why.
    /// Switched off, nothing changes unless the file is saved (<see cref="ModelCoordinator.WriteFailed"/>). Null: saved.
    /// </summary>
    public string? SetOfflineOnly(bool on)
    {
        if (SettingsFile.SetValue(Paths.SettingsFile, "offline_only", JsonValue.Create(on), out var failure))
        {
            _offlineOnlyThisSession = false; // the file holds the choice now
            _log.Info($"Set from the app: offline_only = {(on ? "true" : "false")}.");
            Reload(userRequested: false);
            return null;
        }
        _log.Warn($"Could not write \"offline_only\" to settings.json ({failure}).");
        if (!on)
        {
            StatusChanged(); // the switch shows the settings in use again: offline only stays on
            return ModelCoordinator.WriteFailed;
        }
        _offlineOnlyThisSession = true;
        Reload(userRequested: false);
        return OfflineOnlyNotSaved(failure!);
    }

    /// <summary>
    /// PROPOSED copy (16/10/2026), not yet approved: the product owner reviews it before release. Pinned word for word in
    /// CloudOffTakesEffectTests; do not reword it until that review.
    /// </summary>
    internal static string OfflineOnlyNotSaved(string reason) =>
        $"Offline only is on for this session, but settings.json could not be saved ({reason}). Fix the file so it stays on after a restart.";

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
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
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
