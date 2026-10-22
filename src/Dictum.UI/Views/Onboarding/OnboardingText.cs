using Dictum.Core;
using Dictum.Settings;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// What the "Welcome to Dictum" window says (Dictum 1.2, product owner 21/10/2026). Every string here is APPROVED 22/10/2026 copy
/// (proposed 21/10/2026, listed word for word in DECISIONS); none may be reworded. Strings the window reuses from elsewhere (the hotkey check, the Simple view's tier names, the microphone list's
/// labels through MicrophoneChoices, the "No sound from mic" advice through NoSoundText.SilentAdvice, "Calibrate my voice")
/// are referenced, never copied.
/// </summary>
internal static class OnboardingText
{
    public const string Title = "Welcome to Dictum";

    public const string Back = "Back";
    public const string Next = "Next";
    public const string Finish = "Finish";
    public const string Skip = "Skip";

    /// <summary>The step indicator: "Step 2 of 6: Model files".</summary>
    public static string StepLine(int number, int total, string name) => $"Step {number} of {total}: {name}";

    // ---- 1. Welcome / How it works ----
    public const string WelcomeName = "Welcome";
    public const string WelcomeHeading = "How Dictum works";

    /// <summary>
    /// The orchestrator's decision (21/10/2026): "Everything happens on this PC." only. "Nothing you say is sent anywhere" was
    /// dropped: cloud clean-up, off by default and behind its own consent step, sends text once switched on.
    /// </summary>
    public const string WelcomeText =
        "Dictum types what you say. Hold down your hotkey, speak, then let go: your words appear wherever you were typing, in any program. " +
        "Everything happens on this PC.";

    public const string WelcomeNext = "The next few steps take a minute or two. Press Next to begin.";

    // ---- 2. Where to keep the model files ----
    public const string FolderName = "Model files";
    public const string FolderHeading = "Where to keep the model files";

    /// <summary>No exact total in the UI (orchestrator, 21/10/2026); DECISIONS records the catalogue totals behind "several gigabytes".</summary>
    public const string FolderText =
        "Dictum keeps its speech models on this PC. The included one is about 150 MB; adding more models can take several gigabytes more.";

    /// <summary>The same without base.en on this PC (a build without it): "included" would be untrue (lens on 6bacce9).</summary>
    public const string FolderTextNotIncluded =
        "Dictum keeps its speech models on this PC. The Standard model is about 150 MB to download; adding more models can take several gigabytes more.";

    public const string FolderBoxName = "Folder for the model files";
    public const string ChooseOther = "Choose another drive or folder…";
    public const string UseSuggested = "Use the suggested folder";

    /// <summary>
    /// PROPOSED 23/10/2026 (not yet approved): the free-space line's name for the startup disk, whose root ("/") has no name to
    /// show; without it the line read " has 41.0 GB free".
    /// </summary>
    public const string StartupDisk = "The startup disk";

    /// <summary>"C: has 41.0 GB free" (the size as every other size in Dictum, ModelCatalog.FormatSize).</summary>
    public static string FreeSpace(string drive, long bytes) => $"{drive} has {ModelCatalog.FormatSize(bytes)} free";

    // ---- 3. Speech model ----
    public const string SpeechName = "Speech model";
    public const string SpeechHeading = "Speech model";
    public const string SpeechIncluded = "The Standard model is included and ready.";

    /// <summary>The speech choice when base.en is not on this PC (a build without it): the tiers, then every model on request.</summary>
    public static string SpeechChoice(string host) =>
        $"Choose how carefully Dictum listens. Bigger models are more accurate but slower. A model that is not on this PC yet is downloaded from {host} " +
        "when you reach the end of these steps. You can change this later in Settings > Models.";

    public const string ShowEveryModel = "Show every model";

    // ---- 4. Your hotkey: HotkeyCheckStep reuses the first-run hotkey check's own strings ----
    public const string HotkeyName = "Your hotkey";

    // ---- 5. Microphone test ----
    public const string MicName = "Microphone";
    public const string MicHeading = "Test your microphone";
    public const string SayWords = "Say a few words.";
    public const string HeardYou = "Heard you.";

    /// <summary>The tail is the "No sound from mic" balloon's advice (PR #22), the same constant, so the two cannot drift apart.</summary>
    public const string NothingHeard = "Nothing heard. " + NoSoundText.SilentAdvice;

    /// <summary>The words Settings > Microphone uses when the device cannot be opened (MicrophonePage).</summary>
    public static string CouldNotOpen(string reason) => $"The microphone could not be opened: {reason}";

    public const string MicSkipNote = "You can skip this test. If Dictum hears nothing when you dictate, it will tell you.";
    public const string MeterName = "Microphone level";

    // ---- 6. Try it ----
    public const string TryName = "Try it";
    public const string TryHeading = "Try it";

    public static string TryInstruction(string hotkey) => $"Click in the box, hold {hotkey}, say a sentence, then let go.";

    public const string TryLoading = "Getting the speech model ready…";
    public const string TryBoxName = "Practice box";

    /// <summary>
    /// After a first dictation on the Standard model (the Simple view's first choice for the language in use). "More accurate"
    /// is a Simple view name, so the line says where it is: a seeded settings.json may open Settings > Models in Advanced (final lens, 21/10/2026).
    /// </summary>
    public const string AccentExplainer =
        "Everyone's accent, voice and microphone are different. The Standard model works well for most people. If words come out wrong, " +
        "choose More accurate in the Simple view of Settings > Models, or press Calibrate my voice to find which model hears you best.";

    /// <summary>After a first dictation on any other model: the one chosen in the speech step, or a seeded one (lens on 6bacce9).</summary>
    public const string AccentExplainerChosen =
        "Everyone's accent, voice and microphone are different. The model you chose works well for most people. If words come out wrong, " +
        "choose a more accurate model in Settings > Models, or press Calibrate my voice to find which model hears you best.";

    /// <summary>The microphone test closed the microphone (about 5 s after "Heard you.", or after a minute): this opens it again.</summary>
    public const string TestAgain = "Test again";

    /// <summary>Settings > Help is lane C's page (PR #24, merged before this one); nothing re-runs this window, so the line says Help explains how.</summary>
    public const string NothingCame = "Nothing came through. You can try again any time: Settings > Help explains how.";
}
