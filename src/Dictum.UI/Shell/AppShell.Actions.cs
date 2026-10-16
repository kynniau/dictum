using System.Text;
using System.Text.Json.Nodes;
using Dictum.App;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    public const string SupportEmail = "hello@kynni.com.au";
    public const string IssuesUrl = "https://github.com/kynniau/dictum/issues";
    public const string PrivacyUrl = "https://dictum.kynni.au/privacy";

    /// <summary>The model files in use now (speech, and clean-up when local), which may not be deleted.</summary>
    private IEnumerable<string?> ModelsInUse() =>
        [_models.ModelPath ?? Models.ModelPath, _settings.Llm.Provider == LlmSettings.Local ? SettingsFile.CleanupModelPath(_settings, Paths) : null];

    /// <summary>
    /// Settings → Storage → Delete for one model (SPEC §17): names the file and its size and asks once. The model in use
    /// cannot be deleted; the dialog offers to switch to the recommended downloaded model first, then deletes it once the
    /// switch has loaded. Returns what happened, for the page.
    /// </summary>
    public async Task<string> DeleteModelAsync(StoredModel model)
    {
        var inUse = ModelsInUse().Any(p => p is not null && PathRegistry.SamePath(p, model.Partial ? model.Path[..^5] : model.Path));
        if (inUse)
        {
            if (Replacement(model) is not { } other) return $"{model.Name} is in use and no other model is downloaded. Download or choose another model first.";
            if (Dialogs.Ask("Delete a model in use", $"{model.Name} is in use. Switch to {other.Name} first, then delete {model.Name} ({ModelCatalog.FormatSize(model.Bytes)})?",
                    new DialogChoice($"Switch and delete", IsDefault: true, IsDanger: true), new DialogChoice("Cancel")) != 0) return "Nothing was deleted.";
            if (other.IsCleanup) Write(("llm.provider", JsonValue.Create(LlmSettings.Local)), ("llm.model", JsonValue.Create(other.Name)));
            else Write(("whisper.model", JsonValue.Create(other.Name)));
            if (!await WaitForSwitchAsync(model)) return $"Switched to {other.Name}, but {model.Name} was still in use; delete it again in a moment.";
        }
        else if (Dialogs.Ask("Delete a model", $"Delete {Path.GetFileName(model.Path)} ({ModelCatalog.FormatSize(model.Bytes)})? You can download it again later.",
                     new DialogChoice("Delete", IsDefault: true, IsDanger: true), new DialogChoice("Cancel")) != 0)
        {
            return "Nothing was deleted.";
        }
        var result = ModelFiles.Delete(model.Path, Paths, _settings, ModelsInUse(), Integrity);
        _log.Info(result.Message);
        StatusChanged();
        return result.Message;
    }

    /// <summary>The recommended downloaded model of the same kind, else any other downloaded one.</summary>
    private StoredModel? Replacement(StoredModel model)
    {
        var others = ModelFiles.List(Paths, _settings).Where(m => !m.Partial && m.IsCleanup == model.IsCleanup && m.Path != model.Path).ToList();
        var recommended = model.IsCleanup ? CleanupCatalog.Recommend(GpuMachine).Model : ModelCatalog.Recommend(GpuMachine).Model;
        return others.FirstOrDefault(m => m.Name == recommended) ?? others.FirstOrDefault();
    }

    private async Task<bool> WaitForSwitchAsync(StoredModel model)
    {
        for (var i = 0; i < 600; i++) // up to a minute for the new model to load and the old one to be released
        {
            await Task.Delay(100);
            if (!_models.IsLoading && !_llm.IsLoading && !ModelsInUse().Any(p => p is not null && PathRegistry.SamePath(p, model.Path))) return true;
        }
        return false;
    }

    /// <summary>
    /// Settings → Storage: delete recordings, logs, or all app data (SPEC §17). Shows the size it frees, asks once, then
    /// deletes. "All app data" releases the loaded models first and never touches .env or the program folder.
    /// </summary>
    public string DeleteData(RemovalScope scope)
    {
        var plan = RemovalPlan.Build(Paths, _settings, scope);
        var files = plan.Items.Count(i => !i.IsFolder);
        if (files == 0) return "There is nothing to delete.";
        var what = scope switch
        {
            RemovalScope.CalibrationRecordings => "your calibration recordings",
            RemovalScope.Logs => "the logs",
            RemovalScope.AllDataIncludingSettings => "all of Dictum's data, including settings, your dictionary, corrections and snippets (never your .env keys or the program)",
            _ => "all of Dictum's data: models, recordings, logs (settings, your dictionary, corrections, snippets and .env keys are kept)",
        };
        if (Dialogs.Ask("Delete data", $"Delete {what}? {files} file(s), {ModelCatalog.FormatSize(plan.TotalBytes)}.",
                new DialogChoice("Delete", IsDefault: true, IsDanger: true), new DialogChoice("Cancel")) != 0)
        {
            return "Nothing was deleted.";
        }
        if (scope is RemovalScope.AllData or RemovalScope.AllDataIncludingSettings)
        {
            _models.Unload();
            _llm.Unload();
        }
        if (scope is RemovalScope.Logs or RemovalScope.AllData or RemovalScope.AllDataIncludingSettings) _fileLog?.Release();
        var result = plan.Execute();
        var message = $"Deleted {ModelCatalog.FormatSize(result.FreedBytes)}." + (result.Failures.Count > 0 ? $" {result.Failures.Count} item(s) could not be deleted (in use); see the log." : "");
        foreach (var failure in result.Failures) _log.Warn($"Not deleted: {failure}");
        _log.Info($"Storage: {scope} {message}");
        if (scope is RemovalScope.AllData or RemovalScope.AllDataIncludingSettings) Reload(userRequested: false);
        StatusChanged();
        return message;
    }

    /// <summary>
    /// "Remove Dictum completely" (SPEC §17, §18.2): the first warning lists every path and file with its size and the total;
    /// the second needs DELETE typed and says there is no undo. Then the data goes (Dictate's leftover folders too), the shortcut
    /// and the launch-at-login entry, and the program's own files (from its manifest.txt) in a detached step after Dictum quits
    /// (packaged: Windows' Apps settings do the uninstall).
    /// </summary>
    public bool RemoveEverything()
    {
        // Packaged, the migration only copied: Dictate's folders are the user's, not this package's, and are never listed or deleted.
        var plan = RemovalPlan.Build(Paths, _settings, RemovalScope.Everything, Platform.Removal.PlatformLocations(),
            Platform.Package.IsPackaged ? null : Platform.LegacyFolders());
        var list = new StringBuilder();
        foreach (var item in plan.Items)
        {
            list.Append("• ").Append(item.Path);
            if (item.Bytes > 0) list.Append("  (").Append(ModelCatalog.FormatSize(item.Bytes)).Append(')');
            list.Append('\n');
        }
        var packaged = Platform.Package.IsPackaged;
        var first = $"This deletes everything Dictum has put on this PC:\n\n{list}\nTotal: {ModelCatalog.FormatSize(plan.TotalBytes)}.\n\n" +
                    (packaged ? "Windows then opens Settings > Apps, where you uninstall Dictum itself."
                        : Platform.Removal.ProgramFolderProblem() is { } problem ? $"Dictum then quits. The program itself is not removed: {problem}"
                        : "Dictum then quits. The program's files listed above are removed after it has closed, and its folder too if nothing else is left in it.");
        if (Dialogs.Ask("Remove Dictum completely", first, new DialogChoice("Continue", IsDanger: true), new DialogChoice("Cancel", IsDefault: true)) != 0) return false;
        if (!Dialogs.TypeToConfirm("Remove Dictum completely", "There is no undo: your models, recordings, settings, dictionary, corrections, snippets and keys are deleted for good. Type DELETE to confirm.", "DELETE"))
            return false;

        _log.Info("Remove Dictum completely: confirmed twice; deleting.");
        Models.CancelDownload();
        _models.Unload();
        _llm.Unload();
        _watcher?.Dispose();
        _watcher = null;
        _fileLog?.Stop(); // the logs folder is deleted next; nothing may write it again
        var result = plan.Execute();
        _offlineOnlyThisSession = false; // nothing is kept: the quit must not write settings.json again (SaveSessionChoices)
        var problems = result.Failures.Concat(Platform.Removal.RemoveShortcutAndStartup()).ToList();
        if (packaged) Platform.Removal.OpenUninstallSettings();
        else if (!Platform.Removal.ScheduleProgramFolderRemoval(Paths.ExeDir, out var reason)) problems.Add(reason ?? "the program folder was not removed");
        if (problems.Count > 0) Dialogs.Tell("Remove Dictum completely", "Done, except:\n\n" + string.Join("\n", problems.Select(p => "• " + p)));
        Quit();
        return true;
    }

    /// <summary>
    /// After a migration (SPEC §21, amended): the earlier build is still installed; offer to remove its launch-at-login entry,
    /// Start menu shortcut and program folder. Nothing is removed without "Remove Dictate".
    /// </summary>
    private void OfferEarlierBuildRemoval()
    {
        var items = Platform.Removal.EarlierBuildLeftovers();
        if (items.Count == 0) return;
        var text = "Dictate's settings, models and recordings are now Dictum's. The earlier build itself is still installed:\n\n" +
                   string.Join("\n", items.Select(i => "• " + i)) + "\n\nRemove these? Everything that moved to Dictum stays.";
        if (Dialogs.Ask("Remove the earlier build?", text, new DialogChoice("Remove Dictate", IsDanger: true), new DialogChoice("Keep it", IsDefault: true)) != 0) return;
        var problems = Platform.Removal.RemoveEarlierBuild();
        if (problems.Count > 0) Dialogs.Tell("Remove the earlier build", "Done, except:\n\n" + string.Join("\n", problems.Select(p => "• " + p)));
    }

    /// <summary>
    /// SPEC §22: before any cloud clean-up is switched on, the consent dialog says what is sent and to whom; only "Allow"
    /// records the consent in consent.json (provider, host, time, app version), then writes the provider to settings.json.
    /// A plain http:// server off this PC is refused outright. When settings.json cannot be written, the consent just recorded
    /// is withdrawn again, since the settings do not name that host, and the write failure is returned. A successful write
    /// ends the off switch's hold (<see cref="DisableCloud"/>); a failed one leaves it in force.
    /// </summary>
    public string? EnableCloud(LlmSettings proposed)
    {
        if (CloudPolicy.InsecureEndpoint(proposed) is { } insecure) return insecure + ".";
        if (_settings.OfflineOnly) return "offline_only is on, so cloud clean-up is refused. Switch offline only off in Settings > Privacy first.";
        var values = new List<(string, JsonNode?)>
        {
            ("llm.provider", JsonValue.Create(proposed.Provider)),
            ("llm.base_url", proposed.BaseUrl is null ? null : JsonValue.Create(proposed.BaseUrl)),
            ("llm.model", JsonValue.Create(proposed.Model)),
        };
        var host = CloudPolicy.CloudHost(proposed);
        if (host is not null)
        {
            if (Dialogs.Ask("Send dictations to a cloud service?", CloudPolicy.ConsentText(proposed, host),
                    new DialogChoice("Allow cloud clean-up"), new DialogChoice("Cancel", IsDefault: true)) != 0)
            {
                return "Cloud clean-up was not switched on; nothing is sent.";
            }
            CloudConsentFile.Write(Paths.ConsentFile, new CloudConsentRecord(proposed.Provider, host, DateTimeOffset.UtcNow, AppVersion));
            _log.Info($"Cloud clean-up agreed to in the consent dialog: {proposed.Provider} at {host}.");
        }
        var withdrawn = _cloudWithdrawn;
        _cloudWithdrawn = false; // before the write: its reload must load the service just agreed to
        if (Write([.. values])) return null;
        _cloudWithdrawn = withdrawn;
        if (host is not null)
        {
            CloudConsentFile.Delete(Paths.ConsentFile);
            _log.Info($"Consent to {host} withdrawn again: settings.json could not be written, so cloud clean-up was not switched on.");
        }
        return ModelCoordinator.WriteFailed;
    }

    /// <summary>
    /// Switches cloud clean-up off, in memory first, before either file is changed: from this call until both files keep it,
    /// or cloud clean-up is switched on again (<see cref="EnableCloud"/>), no server tidies a dictation, on this PC or not,
    /// and no consent applies, whatever settings.json and consent.json say (<see cref="SessionChoices"/>). The reload replaces
    /// the clean-up in use with the local model, so a request it has under way is cancelled and nothing more is sent; then it
    /// deletes consent.json and writes the local model to settings.json (<see cref="SaveCloudOff"/>). Returns null when both
    /// files keep the switch, else <see cref="OffNotSaved"/>: the switch then holds, and saving it is tried again at every
    /// reload and once at quit.
    /// </summary>
    public string? DisableCloud()
    {
        _cloudWithdrawn = true;
        _log.Info("Cloud clean-up switched off; consent withdrawn.");
        Reload(userRequested: false);
        return _cloudOffNotSaved is (null, null) ? null : OffNotSaved(_cloudOffNotSaved.Consent, _cloudOffNotSaved.Settings);
    }

    /// <summary>
    /// PROPOSED copy (16/10/2026, second draft), not yet approved: the product owner reviews it before release. Pinned word for
    /// word in CloudOffTakesEffectTests; do not reword it until that review. What Switch cloud clean-up off says when a file
    /// could not be changed, each line true of the files as they are when it is shown: consent.json still there
    /// (<paramref name="consent"/>, why it could not be deleted), settings.json not saved (<paramref name="settings"/>), or both.
    /// Only the first says plain "off": settings.json was saved, or names no cloud service.
    /// </summary>
    internal static string OffNotSaved(string? consent, string? settings) => (consent, settings) switch
    {
        ({ } c, null) => $"Cloud clean-up is off, but consent.json could not be deleted ({c}). Nothing is sent. Dictum tries again at the next reload and when it quits.",
        (null, { } s) => $"Cloud clean-up is off for this session, but settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits.",
        ({ } c, { } s) => $"Cloud clean-up is off for this session, but consent.json could not be deleted ({c}) and settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits.",
        _ => throw new ArgumentException("Both files keep the switch: there is nothing to say."),
    };

    /// <summary>"1.0.0": what the consent record, Report a problem and Check for updates name.</summary>
    internal static string AppVersion => typeof(AppShell).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>
    /// Settings → About → Check for updates (decision 11, 16/10/2026): runs only when the user presses the button. One
    /// request for version.json through the check's own client (AppRules.CreateClients): the offline_only guard, TLS 1.2
    /// and the proxy rules of every download apply, and it follows no redirect; offline_only refuses before anything is
    /// sent, and the Store build never checks.
    /// </summary>
    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        UpdateCheck.CheckAsync(_updateHttp, AppVersion, Platform.Package.IsPackaged, () => _settings.OfflineOnly, _log, cancellationToken);

    /// <summary>
    /// SPEC §22 "Report a problem": a prefilled email to the support address (the user's own mail app sends it, or not).
    /// Nothing is sent by Dictum; no dictation text is included.
    /// </summary>
    public void ReportProblem() => Platform.Open(ReportUri(AppVersion, Platform.Name, Status.Models));

    internal static string ReportUri(string version, string platform, string models)
    {
        var body = $"What happened (and what you expected):\n\n\nDictum {version} on {platform}\nModels: {models}\n\n" +
                   "To report text Dictum produced that should not have been there, describe it here; nothing is attached automatically.";
        return $"mailto:{SupportEmail}?subject={Uri.EscapeDataString($"Dictum {version}: problem report")}&body={Uri.EscapeDataString(body)}";
    }
}
