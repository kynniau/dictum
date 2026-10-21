using Dictum.Settings;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// What the "Welcome to Dictum" window says (Dictum 1.2, product owner 21/10/2026). Every string here is PROPOSED copy
/// (21/10/2026), listed word for word in DECISIONS for the product owner to approve before release; none may be reworded without
/// that review. Strings the window reuses from elsewhere (the hotkey check, the Simple view's tier names, the microphone list's
/// labels, "Calibrate my voice") are referenced, never copied.
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

    public const string FolderBoxName = "Folder for the model files";
    public const string ChooseOther = "Choose another drive or folder…";
    public const string UseSuggested = "Use the suggested folder";

    /// <summary>"C: has 41.0 GB free" (the size as every other size in Dictum, ModelCatalog.FormatSize).</summary>
    public static string FreeSpace(string drive, long bytes) => $"{drive} has {ModelCatalog.FormatSize(bytes)} free";

    // ---- 3. Speech model ----
    public const string SpeechName = "Speech model";
    public const string SpeechHeading = "Speech model";
    public const string SpeechIncluded = "The Standard model is included and ready.";

    /// <summary>The speech choice when base.en is not on this PC (a build without it): the tiers, then every model on request.</summary>
    public static string SpeechChoice(string host) =>
        $"Choose how carefully Dictum listens. Bigger models are more accurate but slower. A model that is not on this PC yet is downloaded from {host} " +
        "when you finish. You can change this later in Settings > Models.";

    public const string ShowEveryModel = "Show every model";

    // ---- 4. Your hotkey: HotkeyCheckStep reuses the first-run hotkey check's own strings ----
    public const string HotkeyName = "Your hotkey";

    // ---- 5. Microphone test ----
    public const string MicName = "Microphone";
    public const string MicHeading = "Test your microphone";
    public const string SayWords = "Say a few words.";
    public const string HeardYou = "Heard you.";

    /// <summary>The tail repeats the "No sound from mic" balloon's advice (PR #22, NoSoundText.SilentBalloon); see DECISIONS for the merge.</summary>
    public const string NothingHeard =
        "Nothing heard. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.";

    /// <summary>
    /// merge: PR #22's NoSoundText.NoMicrophoneBalloon, word for word. When main has PR #22, this constant is replaced by a
    /// reference to it (the orchestrator's "git merge origin/main" step), so the words live in one place.
    /// </summary>
    public const string NoMicrophone =
        "No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.";

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

    public const string AccentExplainer =
        "Everyone's accent, voice and microphone are different. The Standard model works well for most people. If words come out wrong, " +
        "choose More accurate in Settings > Models, or press Calibrate my voice to find which model hears you best.";

    /// <summary>Settings > Help is lane C's page (PR #24, merged before this one); nothing re-runs this window, so the line says Help explains how.</summary>
    public const string NothingCame = "Nothing came through. You can try again any time: Settings > Help explains how.";
}
