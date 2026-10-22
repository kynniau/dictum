using Dictum.Calibration;

namespace Dictum.Core;

/// <summary>
/// Every word on Settings &gt; See how it works (SPEC §29), the one source for its page title and its five stage labels. All
/// APPROVED 22/10/2026 (proposed 22/10/2026): do not reword. The texts it frames (the speech
/// model's words, the validator's reasons, the clean-up's status line, <see cref="NoSoundText"/>) are the app's own.
/// </summary>
public static class HowItWorksText
{
    /// <summary>The page's name in the Settings list and its title.</summary>
    public const string PageTitle = "See how it works";

    public const string Intro =
        "Record one short dictation and watch what Dictum does with it, step by step. It uses your current settings: the same speech model and clean-up as the hotkey.";

    /// <summary>The in-memory line with the speech model on this PC: the recording is never written anywhere and nothing is pasted.</summary>
    public const string MemoryNote = "This page pastes nothing. The recording is kept in memory only, never saved to disk, and the sound never leaves this PC.";

    /// <summary>The in-memory line while cloud speech is on (<see cref="Transcribe.CloudSpeech.SendsAudio"/>): the sound goes to its host, as a dictation's does.</summary>
    public static string CloudSpeechNote(string host) =>
        $"This page pastes nothing. The recording is kept in memory only and never saved to disk. With cloud speech on, the sound is sent to {host} to be turned into text.";

    /// <summary>The in-memory line for the speech step in use: <paramref name="speechHost"/> is cloud speech's host, or null for the model on this PC.</summary>
    public static string Memory(string? speechHost) => speechHost is null ? MemoryNote : CloudSpeechNote(speechHost);

    /// <summary>Step 2's description while cloud speech is on, in place of the one for the model on this PC.</summary>
    public static string HeardByCloud(string host) =>
        $"With cloud speech on, {host} turns the sound into words (the speech model on this PC, if that fails). This is the raw text.";

    /// <summary>Shown while the clean-up in use sends text to another program (<see cref="Llm.TranscriptCleaner.SendsText"/>).</summary>
    public static string SendsText(string status) =>
        $"Your clean-up sends text to another program ({status}), so this recording's words go there too, as a dictation's would.";

    public const string RecordLabel = "Record";
    public const string StopLabel = "Stop";

    /// <summary>The Record button's name for a screen reader.</summary>
    public const string RecordName = "Record one dictation";

    /// <summary>The button's name while recording.</summary>
    public const string StopName = "Stop recording and show the steps";

    /// <summary>Beside Record: the dictation hotkey records here too while this page is in front (calibration's wording).</summary>
    public static string HoldHint(string hotkey) => $"or hold {hotkey}";

    public const string Recording = "Recording: say a sentence or two, then press Stop.";

    /// <summary>The recording reached the take limit calibration uses, and was stopped and shown.</summary>
    public static readonly string LimitReached = $"Stopped at {CalibrationSession.TakeLimit.TotalSeconds:0} s, the longest this page records.";

    public const string Busy = "A dictation is in progress: try again when it has finished.";

    /// <summary>Esc while recording here.</summary>
    public const string Cancelled = "Cancelled: nothing was kept.";

    /// <summary>The steps have all been shown.</summary>
    public const string Done = "Done. Record again to try another sentence.";

    public static string Failed(string message) => $"That did not work: {message}";

    /// <summary>No speech model is ready and none is loading (one loading says the welcome window's "Getting the speech model ready…").</summary>
    public const string NoSpeechModel = "No speech model is ready yet, so Record is off. Settings > Models shows the model's state.";

    /// <summary>The five stages, in order: title then one line on what the stage is.</summary>
    public static readonly IReadOnlyList<(string Title, string Description)> Stages =
    [
        ("1. What Dictum hears", "Sound, not words: the bars show how loud your microphone is."),
        ("2. What the speech model heard", "The speech model on this PC turns the sound into words. This is its raw text."),
        ("3. Rule-based tidy", "Fixed rules remove hesitations such as \"um\", add capitals and a full stop, and follow spoken commands such as \"new line\"."),
        ("4. AI clean-up", "A language model polishes the words, then Dictum checks its reply and uses it only if it passes every check."),
        ("5. What would be pasted", "After your corrections, the profanity filter and snippets. A dictation pastes this; this page does not."),
    ];

    // Each stage's state, said beside its title.
    public const string Waiting = "Waiting";
    public const string Working = "Working…";
    public const string StageDone = "Done";
    public const string NotUsed = "Not used";
    public const string NotReached = "Not reached";

    public static string Took(long ms) => $"Took {ms} ms";

    public static string SoundLength(TimeSpan duration) => $"{duration.TotalSeconds:0.0} s of sound";

    /// <summary>The journey stopped where a dictation's would (too short, or no words heard); a silent one says <see cref="NoSoundText.SilentBalloon"/>.</summary>
    public static string StoppedHere(string reason) => $"Dictum stops here ({reason}), as it would for a dictation.";

    public const string TidyPlain = "The rule-based tidy did not run: only spaces were tidied.";
    public const string TidyIsAll = "No AI clean-up is in use, so this is the clean-up.";
    public const string TidyUsed = "Used, because the AI reply was not (step 4).";
    public const string TidyNotNeeded = "Not needed this time: the AI reply passed. Dictum uses this text when a reply does not.";

    public static string NotInUse(string status) => $"Not in use. Clean-up in use: {status}.";

    public const string Accepted = "Accepted: the reply passed every check, so Dictum used it.";

    /// <summary>The model's reply was not used: the validator's rule, a timeout or the provider's error, in the app's own words.</summary>
    public static string Rejected(string reason) => $"Not used: {reason}. Dictum used the rule-based tidy (step 3) instead.";

    /// <summary>The model was not asked: the dictation would have run past its deadline at the measured speed.</summary>
    public static string NotAsked(string reason) => $"Not asked: {reason}. Dictum used the rule-based tidy (step 3) instead.";

    public const string WouldPaste = "A dictation would paste this. This page pasted nothing.";
    public const string Changed = "Your corrections, the profanity filter or snippets changed the text.";
    public const string NothingLeft = "Nothing would be pasted: no text is left.";

    /// <summary>Every fixed sentence, for the pins (the ones built from a value are checked where they are shown).</summary>
    public static IEnumerable<string> FixedText() =>
    [
        PageTitle, Intro, MemoryNote, RecordLabel, StopLabel, Recording, LimitReached, Busy, Cancelled, Done, NoSpeechModel,
        .. Stages.SelectMany(s => new[] { s.Title, s.Description }),
        Waiting, Working, StageDone, NotUsed, NotReached, TidyPlain, TidyIsAll, TidyUsed, TidyNotNeeded, Accepted, WouldPaste, Changed, NothingLeft,
    ];
}
