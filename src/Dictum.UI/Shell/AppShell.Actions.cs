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
    public const string SupportEmail = Help.SupportMail.Address; // an alias: the one constant is in Core
    public const string IssuesUrl = "https://github.com/kynniau/dictum/issues";
    public const string PrivacyUrl = "https://dictum.kynni.au/privacy";

    /// <summary>The model files in use now (speech, and clean-up when local), which may not be deleted.</summary>
    private IEnumerable<string?> ModelsInUse() =>
        [_models.ModelPath ?? Models.ModelPath, _settings.Llm.Provider == LlmSettings.Local ? SettingsFile.CleanupModelPath(_settings, Paths) : null];

    /// <summary>
    /// Settings → Storage → Delete for one model (SPEC §17): names the file and its size and asks once. The model in use
    /// cannot be deleted; the dialog offers to switch to the recommended downloaded model first, then deletes it once the
    /// switch has loaded. When the same model is also further along the lookup (a base.en downloaded by 1.0.x, beside the one
    /// that comes with Dictum) the copy in use can be deleted: it asks (<see cref="DeleteThisCopy"/>), releases the model,
    /// deletes the file and reloads, as "Delete all app data" does, so Dictum goes on with the other copy (re-lens, 17/10/2026).
    /// Returns what happened, for the page.
    /// </summary>
    public async Task<string> DeleteModelAsync(StoredModel model)
    {
        var inUse = ModelsInUse().Any(p => p is not null && PathRegistry.SamePath(p, model.Partial ? model.Path[..^5] : model.Path));
        string? released = null; // the copy in use that is deleted after its model is released: the reload then finds the next copy
        if (inUse && NextCopy(model) is { } next)
        {
            var otherFolder = next.Kind == ModelFolderKind.Bundled ? null : RealPath(Path.GetDirectoryName(next.Path)!);
            if (Dialogs.Ask("Delete a model", DeleteThisCopy(model.Name, model.Bytes, otherFolder),
                    new DialogChoice("Delete", IsDefault: true, IsDanger: true), new DialogChoice("Cancel")) != 0) return "Nothing was deleted.";
            if (model.IsCleanup) _llm.Unload();
            else _models.Unload();
            released = model.Path;
        }
        else if (inUse)
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
        ModelDeletion result;
        try
        {
            // After the release the lookup still finds this file first, so ModelsInUse names it: it is left out of the check.
            result = ModelFiles.Delete(model.Path, Paths, _settings, ModelsInUse().Where(p => released is null || p is null || !PathRegistry.SamePath(p, released)), Integrity);
        }
        finally
        {
            if (released is not null) Reload(userRequested: false); // loads the next copy, or this one again when it could not be deleted
        }
        _modelFileChanges++;
        _log.Info(result.Message);
        StatusChanged();
        return result.Message;
    }

    /// <summary>
    /// PROPOSED copy (17/10/2026), for the product owner to approve: the question Settings > Storage > Delete asks, titled "Delete a
    /// model", on the copy of a model in use when the same model is also further along the lookup (a base.en downloaded by 1.0.x,
    /// beside the one that comes with Dictum; <paramref name="otherFolder"/> is null for that one). The second sentence says what
    /// Dictum uses afterwards; for a copy in another of Dictum's folders it names that folder.
    /// </summary>
    internal static string DeleteThisCopy(string name, long bytes, string? otherFolder) =>
        $"Delete this copy of {name} ({ModelCatalog.FormatSize(bytes)})? Dictum then uses {(otherFolder is null ? "the copy that came with it" : $"the other copy, in {otherFolder}")}.";

    /// <summary>
    /// What Dictum would go on with if this copy of the model in use were deleted: the same file further along the lookup order
    /// that passes the tray app's own check (the size, and for the one that came with Dictum its SHA-256); null when there is
    /// none, or the file is a partial download (re-lens, 17/10/2026).
    /// </summary>
    private (string Path, ModelFolderKind Kind)? NextCopy(StoredModel model)
    {
        if (model.Partial || Path.IsPathFullyQualified(model.IsCleanup ? _settings.Llm.Model : _settings.Whisper.Model)) return null; // a setting that names the file by its path has no other copy to fall back to
        var (fileName, bytes) = model.IsCleanup
            ? (CleanupCatalog.FileNameFor(model.Name), CleanupCatalog.Find(model.Name)?.Bytes)
            : (ModelCatalog.FileName(model.Name), ModelCatalog.KnownSize(model.Name));
        return Models.FindBesides(fileName, bytes, model.Path);
    }

    /// <summary>
    /// The recommended model of the same kind on this PC, else any other one: downloaded, or come with Dictum (the bundled base.en
    /// is switched to, never deleted; lens, 17/10/2026). By name, so a second copy of the model in use is never its replacement,
    /// and only a model the tray app's lookup would load (<see cref="WouldLoad"/>; re-lens, 17/10/2026).
    /// </summary>
    private StoredModel? Replacement(StoredModel model)
    {
        var others = ModelFiles.List(Paths, _settings, withBundled: true)
            .Where(m => !m.Partial && m.IsCleanup == model.IsCleanup && m.Name != model.Name && WouldLoad(m)).ToList();
        var recommended = model.IsCleanup ? CleanupCatalog.Recommend(GpuMachine).Model : ModelCatalog.Recommend(GpuMachine).Model;
        return others.FirstOrDefault(m => m.Name == recommended) ?? others.FirstOrDefault();
    }

    /// <summary>
    /// The tray app's lookup loads this very file when its name is chosen (re-lens, 17/10/2026): a bundled base.en that fails
    /// its SHA-256 is passed over there, so switching to its name would leave the setting naming a file that is not loaded.
    /// </summary>
    private bool WouldLoad(StoredModel model) => model.IsCleanup
        ? CleanupCatalog.Find(model.Name) is { } cleanup && Models.IsCleanupDownloaded(cleanup)
        : ModelCatalog.Find(model.Name) is { } speech && Models.Find(speech) is { } found && PathRegistry.SamePath(found, model.Path);

    /// <summary>
    /// PROPOSED copy (17/10/2026), for the product owner to approve: what "Delete all app data" asks about a chosen models folder
    /// outside the user's profile, which it leaves alone (lens, 17/10/2026: other users may load the models in it).
    /// </summary>
    internal static string LeftAlone(string folder) => $"Left alone: {folder} (outside your user folder, so other users may share its models; Delete beside a model removes one).";

    private int _modelFileChanges; // settings loads and deletions (ModelFilesVersion)

    /// <summary>
    /// Changes whenever the model files on this PC, or the folders they are looked for in, may have changed: a settings load,
    /// a download that ended, a deletion. Settings > Models looks the catalogue up again only then (lens, 17/10/2026: it looked
    /// every model up on each redraw, a download's progress ticks included, on the UI thread).
    /// </summary>
    internal (int Changes, int DownloadsEnded) ModelFilesVersion => (_modelFileChanges, Models.DownloadsEnded);

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
    /// deletes. "All app data" releases the loaded models first and never touches .env or the program folder, and its question
    /// names a chosen models folder outside the user's profile that it leaves alone (<see cref="DataCategory.SharedModels"/>).
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
        var leftAlone = scope is RemovalScope.AllData or RemovalScope.AllDataIncludingSettings
            ? string.Concat(PathRegistry.Locations(Paths, _settings).Where(l => l.Category == DataCategory.SharedModels).Select(l => " " + LeftAlone(RealPath(l.Path))))
            : "";
        if (Dialogs.Ask("Delete data", $"Delete {what}? {files} file(s), {ModelCatalog.FormatSize(plan.TotalBytes)}.{leftAlone}",
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
        _modelFileChanges++;
        // PROPOSED copy (17/10/2026), for the product owner to approve: "(in use or not permitted)" was "(in use)"; a file Windows refuses to delete counts too.
        var message = $"Deleted {ModelCatalog.FormatSize(result.FreedBytes)}." + (result.Failures.Count > 0 ? $" {result.Failures.Count} item(s) could not be deleted (in use or not permitted); see the log." : "");
        foreach (var failure in result.Failures) _log.Warn($"Not deleted: {failure}");
        _log.Info($"Storage: {scope} {message}");
        if (scope is RemovalScope.AllDataIncludingSettings && !File.Exists(Paths.SettingsFile)) _offlineOnlyThisSession = false; // deleted on request: no new settings.json (SaveSessionChoices)
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
        Models.CancelDownload(DownloadCancelReason.Remove);
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
    /// is withdrawn again, since the settings do not name that host, and the write failure is returned. When consent.json
    /// cannot be deleted either (another program holds it open), the consent withdrawal holds instead
    /// (<see cref="DisableCloud"/>): the consent on file is not used this session, deleting it is tried again at every reload
    /// and at quit, and the delete's exception never reaches the page. A successful write ends the off switch's hold
    /// (<see cref="DisableCloud"/>); a failed one leaves it in force. Only "Allow" ends the switch's consent withdrawal: a
    /// server on this PC switched on leaves a consent.json that could not be deleted unused.
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
            _consentWithdrawn = false; // a new consent is on record
            _log.Info($"Cloud clean-up agreed to in the consent dialog: {proposed.Provider} at {host}.");
        }
        var withdrawn = _cloudWithdrawn;
        _cloudWithdrawn = false; // before the write: its reload must load the service just agreed to
        if (Write([.. values])) return null;
        _cloudWithdrawn = withdrawn;
        if (host is not null)
        {
            try
            {
                CloudConsentFile.Delete(Paths.ConsentFile);
                _log.Info($"Consent to {host} withdrawn again: settings.json could not be written, so cloud clean-up was not switched on.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _consentWithdrawn = true; // not used this session; SaveCloudOff deletes it at the next reload and at quit
                _log.Warn($"Consent to {host} could not be withdrawn again ({SettingsFile.ShortReason(ex, Paths.ConsentFile)}); it is not used this session, and deleting it is tried again at the next reload and at quit.");
            }
        }
        return ModelCoordinator.WriteFailed;
    }

    /// <summary>
    /// Switches cloud clean-up off, in memory first, before either file is changed (<see cref="SessionChoices"/>): from this
    /// call until both files keep it, or cloud clean-up is switched on again (<see cref="EnableCloud"/>), no server tidies a
    /// dictation, on this PC or not, whatever settings.json says; and until consent.json is gone, or "Allow" records a new
    /// consent, no consent applies, whatever consent.json says, also after a server on this PC is switched on. The reload
    /// replaces the clean-up in use with the local model, so a request it has under way is cancelled and nothing more is
    /// sent; then it deletes consent.json and writes the local model to settings.json (<see cref="SaveCloudOff"/>). Returns
    /// null when both files keep the switch, else <see cref="OffNotSaved"/>: the switch then holds, and saving it is tried
    /// again at every reload and once at quit.
    /// </summary>
    public string? DisableCloud()
    {
        _cloudWithdrawn = true;
        _consentWithdrawn = true;
        _log.Info("Cloud clean-up switched off; consent withdrawn.");
        Reload(userRequested: false);
        return _cloudOffNotSaved is (null, null) ? null : OffNotSaved(_cloudOffNotSaved.Consent, _cloudOffNotSaved.Settings, SettingsNameAServerOnThisPc());
    }

    /// <summary>
    /// PROPOSED copy (16/10/2026, fifth draft), not yet approved: the product owner reviews it before release. Pinned word for
    /// word in CloudOffTakesEffectTests; do not reword it until that review. What Switch cloud clean-up off says when a file
    /// could not be changed: consent.json still there (<paramref name="consent"/>, why it could not be deleted), settings.json
    /// not saved (<paramref name="settings"/>), or both. Only the first says plain "off": settings.json was saved, or names no
    /// cloud service. The first, the one for both files and the variant for a server on this PC say what the files mean if the
    /// last try, at quit, fails too; the last two also name the start with a settings.json that still cannot be read (not valid
    /// JSON, say), which runs offline with the defaults (ConfigLoader.BrokenAtStartup). settings.json alone says nothing of
    /// the next start when the file names a service off this PC (it is refused without a consent on file) or cannot be read
    /// (the start is offline): nothing is sent either way. Changed in the fifth draft: the sentence for both files ends "turns
    /// cloud clean-up back on" (the fourth draft's "sends dictations again" is untrue when the next start has offline only on
    /// or is waiting for a key: CloudPolicy.OfflineOnlyLine, WaitingForKeyLine), and settings.json alone has a variant when the
    /// file names a server on this PC (<paramref name="serverOnThisPc"/>), which the next start uses again. Unchanged:
    /// consent.json alone (third draft) and settings.json alone otherwise (second draft).
    /// </summary>
    internal static string OffNotSaved(string? consent, string? settings, bool serverOnThisPc) => (consent, settings) switch
    {
        ({ } c, null) => $"Cloud clean-up is off, but consent.json could not be deleted ({c}). Nothing is sent. Dictum tries again at the next reload and when it quits; until it is deleted, your consent stays on file.",
        (null, { } s) when serverOnThisPc => $"Cloud clean-up is off for this session, but settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits; if it is still not saved then, the next start uses that server again, unless settings.json cannot be read, in which case Dictum starts offline.",
        (null, { } s) => $"Cloud clean-up is off for this session, but settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits.",
        ({ } c, { } s) => $"Cloud clean-up is off for this session, but consent.json could not be deleted ({c}) and settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits; if both are still unchanged then, the next start turns cloud clean-up back on, unless settings.json cannot be read, in which case Dictum starts offline.",
        _ => throw new ArgumentException("Both files keep the switch: there is nothing to say."),
    };

    /// <summary>
    /// settings.json can be read and names a server on this PC (a loopback llm.base_url), which the next start uses again unless
    /// the switch-off has been saved by then. Read afresh, after the try that could not save it, so it is what the file says now.
    /// </summary>
    private bool SettingsNameAServerOnThisPc()
    {
        var file = SettingsLoader.LoadFile(Paths.SettingsFile);
        return file.Usable && CloudPolicy.LocalServerHost(file.Settings.Llm) is not null;
    }

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
        return Help.SupportMail.Uri($"Dictum {version}: problem report", body);
    }

    /// <summary>
    /// Settings → Help → "Send feedback or suggest a feature" (lane F): a new email to the support address in the user's own mail
    /// program, through the same platform seam and the same builder as <see cref="ReportProblem"/>. Dictum sends nothing itself.
    /// </summary>
    public void SendFeedback() => Platform.Open(Help.SupportMail.FeedbackUri(FeedbackFacts()));

    /// <summary>
    /// The five facts the feedback email carries, taken from what About shows: the version, the system and its version, the
    /// speech model and the runtime it loaded on, and the clean-up in use. Never a path, a mic, a dictionary word, text
    /// that was dictated, or a machine or user name.
    /// </summary>
    internal Help.FeedbackSetup FeedbackFacts()
    {
        var speechRuntime = _models.IsLoading ? "loading" : _models.LastError is not null ? "no runtime (model error)" : SpeechRuntime();
        // The cloud provider's name alone: the status line also names the server, which on a company's own address could be private.
        var cleanup = _settings.Llm.Provider switch
        {
            LlmSettings.NoProvider => "rule-based tidy",
            LlmSettings.Local => CleanupError is not null && !CleanupLoading ? "rule-based tidy (the clean-up model could not be loaded)" : $"{Models.CurrentCleanupName} on this PC",
            var cloud => cloud + (_cleaner.MissingKey is null ? "" : " (no key, so rule-based tidy)"),
        };
        return new Help.FeedbackSetup(AppVersion, $"{Platform.Name} {Environment.OSVersion.Version.ToString(3)}", Models.CurrentName, speechRuntime, cleanup);
    }
}
