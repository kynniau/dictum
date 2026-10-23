using System.Text.Json.Nodes;
using Dictum.App;
using Dictum.Help;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// The first-run questions the "Welcome to Dictum" window asks (Dictum 1.2), apart from the window so the shell and the tests
/// drive the same writes: the model folder, the speech model (through ModelCoordinator's first-run answer, as the pickers did),
/// the hotkey and the microphone. Each step writes its own settings when it is completed, only what changed, straight to
/// settings.json and without a reload (nothing is loaded yet). The first step's Continue accepts the Terms of Use
/// (<see cref="AcceptTerms"/>); closed before that, the window writes nothing and the shell quits. After it,
/// <see cref="SkipRest"/> answers whatever is still open with today's defaults, so a closed window never leaves the speech choice
/// unanswered. <see cref="TermsOnly"/>: the first step alone, for a copy that has its first-run answers but no acceptance of the
/// Terms (an upgrade, a seeded settings.json, a model override). Runs on the UI thread.
/// </summary>
public sealed class OnboardingFlow
{
    private readonly AppShell _shell;
    private readonly SettingsLoadResult _file;
    private readonly bool _askCleanup;
    private FirstRunPlan? _plan;

    internal OnboardingFlow(AppShell shell, SettingsLoadResult file, bool askCleanup, bool termsOnly = false)
    {
        _shell = shell;
        _file = file;
        _askCleanup = askCleanup;
        TermsOnly = termsOnly;
        TermsAccepted = TermsRecorded = !AppRules.AsksTerms(file, AppShell.Account); // already accepted by this account: closing does not quit
        AsksFolder = AppRules.AsksFolder(file);
        SuggestedFolder = shell.Paths.ModelsDir(file.Settings);
        Hotkey = file.Settings.Hotkey;
        Device = file.Settings.Audio.Device;
    }

    internal AppShell Shell => _shell;

    /// <summary>The window shows the first step alone (the Terms of Use), and its Continue ends it: no first-run question is open.</summary>
    public bool TermsOnly { get; }

    /// <summary>Continue was pressed on the first step (or settings.json already recorded this account's acceptance).</summary>
    public bool TermsAccepted { get; private set; }

    /// <summary>settings.json records the acceptance (terms.accepted and terms.accepted_by).</summary>
    public bool TermsRecorded { get; private set; }

    private bool _recordTried;

    /// <summary>
    /// Continue on the first step: the Terms of Use version in force is accepted. It is recorded in settings.json at once when
    /// the file exists; a new install's has none yet, so its record is written with the speech answer, which creates the file in
    /// its documented layout (<see cref="CompleteSpeech"/>), or when the window closes after Continue (<see cref="SkipRest"/>).
    /// </summary>
    public void AcceptTerms()
    {
        TermsAccepted = true;
        if (File.Exists(_shell.Paths.SettingsFile)) RecordTerms();
    }

    /// <summary>
    /// Once the Terms are accepted: writes terms.accepted (<see cref="TermsText.Version"/>) and terms.accepted_by (this
    /// account) in one write, keeping every other key; then nothing more. The shell calls it after the first run's own answer
    /// too, for a launch with a model override and no settings.json yet. A file that cannot be written keeps no record: Dictum
    /// goes on this time and asks again at the next start; the write is tried once, so the warning is logged once.
    /// </summary>
    internal void RecordTerms()
    {
        if (!TermsAccepted || TermsRecorded || _recordTried) return;
        _recordTried = true; // one try: again later only when settings.json did not exist at Continue (lens on PR #76: a refused write warned twice)
        var record = new JsonObject { ["accepted"] = TermsText.Version, ["accepted_by"] = AppShell.Account };
        TermsRecorded = SettingsFile.SetValue(_shell.Paths.SettingsFile, "terms", record, out var failure);
        if (TermsRecorded) _shell.Log.Info($"Terms of Use version {TermsText.Version} accepted; recorded in settings.json.");
        else _shell.Log.Warn($"Terms of Use version {TermsText.Version} accepted, but settings.json could not record it ({failure}); Dictum asks again at the next start.");
    }

    /// <summary>Milliseconds on a monotonic clock, for the microphone test and Try it; tests move their own.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>The folder step is shown: settings.json names no model folder (<see cref="AppRules.AsksFolder"/>).</summary>
    public bool AsksFolder { get; }

    /// <summary>The folder Dictum would use without a choice: the per-user default.</summary>
    public string SuggestedFolder { get; }

    /// <summary>The folder chosen on the folder step, or null for the suggested one.</summary>
    public string? ChosenFolder { get; private set; }

    public string Folder => ChosenFolder ?? SuggestedFolder;

    /// <summary>whisper.language and whisper.translate in settings.json: the speech step offers the Simple view's tiers for them.</summary>
    public string Language => _file.Settings.Whisper.Language;

    public bool Translate => _file.Settings.Whisper.Translate;

    /// <summary>silence_threshold in settings.json: the microphone test hears you only at or above it, as dictation does.</summary>
    public double SilenceThreshold => _file.Settings.SilenceThreshold;

    /// <summary>The hotkey in settings.json (the default when it names none); the hotkey step's answer once completed.</summary>
    public string Hotkey { get; private set; }

    /// <summary>audio.device in settings.json (null: the system default); the microphone step's answer once completed.</summary>
    public string? Device { get; private set; }

    public bool SpeechAnswered { get; private set; }

    /// <summary>Steps 1-5 are over (finished, or the window closed): the shell goes on to load the settings.</summary>
    public bool FirstStepsDone { get; private set; }

    /// <summary>The window was closed before the end: the rest were skipped, and Try it is not shown.</summary>
    public bool Abandoned { get; private set; }

    /// <summary>The shell has loaded the settings and installed the hotkey: Try it can use the real dictation path.</summary>
    public bool TryItLive { get; private set; }

    /// <summary>Raised when <see cref="TryItLive"/> becomes true.</summary>
    public event Action? TryItReady;

    /// <summary>
    /// The folder step's choice, checked by the same rule as Settings > Models > Model folder (ModelCoordinator.FolderRefusal:
    /// never the folder of the models that come with Dictum, never a device path the settings load refuses). Returns the refusal
    /// to show, or null when the folder is taken (nothing is written until the step is completed).
    /// </summary>
    public string? ChooseFolder(string? folder)
    {
        if (folder is null || Storage.PathRegistry.SamePath(folder, SuggestedFolder))
        {
            ChosenFolder = null;
            return null;
        }
        if (_shell.Models.FolderRefusal(folder) is { } refused) return refused;
        ChosenFolder = folder;
        return null;
    }

    /// <summary>
    /// Completes the folder step: writes models.folder only when the choice differs from what settings.json holds (nothing, until
    /// another folder is chosen; Back and "Use the suggested folder" can put it back to null). Returns why it could not be
    /// written, or null.
    /// </summary>
    public string? CompleteFolder()
    {
        if (string.Equals(ChosenFolder, _writtenFolder, StringComparison.OrdinalIgnoreCase)) return null;
        var failure = _shell.Models.SetModelsFolder(ChosenFolder, reloadAfter: false);
        if (failure is not null) return failure;
        _writtenFolder = ChosenFolder;
        _plan = null; // base.en may be in the folder chosen: looked up again
        _shell.Log.Info($"Onboarding: models.folder set to {ChosenFolder ?? "the default"}.");
        return null;
    }

    private string? _writtenFolder;

    /// <summary>The first-run recommendation and whether base.en is on this PC, looked up with the folder chosen (once).</summary>
    public FirstRunPlan Plan => _plan ??= _shell.Models.PlanFirstRun(SettingsLoader.LoadFile(_shell.Paths.SettingsFile) is { Usable: true } now ? now.Settings : _file.Settings);

    /// <summary>
    /// The speech step shows a choice: base.en is not on this PC, or settings.json names a language that is not English (or
    /// translate), for which base.en, English only, is not one of the Simple view's choices (lens on 6bacce9). Esc there keeps
    /// base.en, as before 1.2: dictation works at once and the existing language warning follows.
    /// </summary>
    public bool AsksSpeech => Plan.AsksPickers || !Plan.Overridden && SimpleView.Multilingual(Language, Translate);

    /// <summary>
    /// Completes the speech step: writes whisper.model (and the clean-up answer, the rule-based tidy as with base.en, unless
    /// settings.json names llm.provider) through the first run's own writer. Null: no choice made (base.en, or skipped), which
    /// never replaces an answer already written; a choice made again after Back replaces it.
    /// </summary>
    public void CompleteSpeech(ModelPick? pick)
    {
        if (SpeechAnswered && pick is null) return;
        SpeechAnswered = true;
        _shell.Models.AnswerFirstRun(Plan, askSpeech: true, _askCleanup, keepProvider: _file.NamesCleanupProvider, speechPick: pick);
        RecordTerms(); // a new install: settings.json exists from now on
    }

    /// <summary>Completes the hotkey step: writes "hotkey" only when another key was chosen (the first-run hotkey check's rule).</summary>
    public void CompleteHotkey(string chosen)
    {
        if (string.Equals(chosen, Hotkey, StringComparison.Ordinal)) return;
        if (SettingsFile.SetValue(_shell.Paths.SettingsFile, "hotkey", JsonValue.Create(chosen)))
        {
            Hotkey = chosen;
            _shell.Log.Info($"First run: hotkey set to {chosen}.");
        }
        else
        {
            _shell.Log.Warn($"First run: could not write hotkey {chosen} to settings.json.");
        }
    }

    /// <summary>Completes the microphone step: writes audio.device only when another microphone was chosen.</summary>
    public void CompleteMicrophone(string? device)
    {
        if (string.Equals(device, Device, StringComparison.Ordinal)) return;
        if (SettingsFile.SetValue(_shell.Paths.SettingsFile, "audio.device", device is null ? null : JsonValue.Create(device)))
        {
            Device = device;
            _shell.Log.Info($"Onboarding: audio.device set to {device ?? "the system default"}.");
        }
        else
        {
            _shell.Log.Warn("Onboarding: could not write audio.device to settings.json.");
        }
    }

    /// <summary>Steps 1-5 are finished: the window moves to Try it while the shell loads (or, <see cref="TermsOnly"/>, closes).</summary>
    public void FinishFirstSteps()
    {
        if (!TermsOnly) CompleteSpeech(null); // the speech step always completes before this; a no-op then
        FirstStepsDone = true;
    }

    /// <summary>
    /// Esc, or closing the window, before the end: the rest keep today's defaults. Only the speech answer must be written (the
    /// settings load needs it); the folder, hotkey and microphone stay as settings.json has them. Before Continue on the first
    /// step nothing is written at all: the Terms of Use were not accepted, and the shell quits.
    /// </summary>
    public void SkipRest()
    {
        if (!FirstStepsDone) Abandoned = true;
        FirstStepsDone = true;
        if (TermsAccepted && !TermsOnly) CompleteSpeech(null);
    }

    /// <summary>Called by the shell once the settings are loaded and the hotkey listens.</summary>
    internal void BeginTryIt()
    {
        if (Abandoned || TryItLive || TermsOnly) return;
        TryItLive = true;
        TryItReady?.Invoke();
    }
}
