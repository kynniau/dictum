using System.Text.Json.Nodes;
using Dictum.App;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// The first-run questions the "Welcome to Dictum" window asks (Dictum 1.2), apart from the window so the shell and the tests
/// drive the same writes: the model folder, the speech model (through ModelCoordinator's first-run answer, as the pickers did),
/// the hotkey and the microphone. Each step writes its own settings when it is completed, only what changed, straight to
/// settings.json and without a reload (nothing is loaded yet). <see cref="SkipRest"/> answers whatever is still open with
/// today's defaults, so a closed window never leaves the speech choice unanswered. Runs on the UI thread.
/// </summary>
public sealed class OnboardingFlow
{
    private readonly AppShell _shell;
    private readonly SettingsLoadResult _file;
    private readonly bool _askCleanup;
    private FirstRunPlan? _plan;

    internal OnboardingFlow(AppShell shell, SettingsLoadResult file, bool askCleanup)
    {
        _shell = shell;
        _file = file;
        _askCleanup = askCleanup;
        AsksFolder = AppRules.AsksFolder(file);
        SuggestedFolder = shell.Paths.ModelsDir(file.Settings);
        Hotkey = file.Settings.Hotkey;
        Device = file.Settings.Audio.Device;
    }

    internal AppShell Shell => _shell;

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

    /// <summary>The speech step shows a choice: base.en is not on this PC.</summary>
    public bool AsksSpeech => Plan.AsksPickers;

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

    /// <summary>Steps 1-5 are finished: the window moves to Try it while the shell loads.</summary>
    public void FinishFirstSteps()
    {
        CompleteSpeech(null); // the speech step always completes before this; a no-op then
        FirstStepsDone = true;
    }

    /// <summary>
    /// Esc, or closing the window, before the end: the rest keep today's defaults. Only the speech answer must be written (the
    /// settings load needs it); the folder, hotkey and microphone stay as settings.json has them.
    /// </summary>
    public void SkipRest()
    {
        if (!FirstStepsDone) Abandoned = true;
        CompleteSpeech(null);
        FirstStepsDone = true;
    }

    /// <summary>Called by the shell once the settings are loaded and the hotkey listens.</summary>
    internal void BeginTryIt()
    {
        if (Abandoned || TryItLive) return;
        TryItLive = true;
        TryItReady?.Invoke();
    }
}
