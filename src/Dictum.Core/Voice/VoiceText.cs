namespace Dictum.Meetings.Voice;

/// <summary>
/// New user-facing words for the voice print (design 1.6 C and D). PROPOSED: each is the v2 text of its row in
/// .demo-preview/two-model-voiceprint.md (Fable draft, GPT challenge, 08/11/2026), copied verbatim; the owner approves or rewords them.
/// Row V14 is not here: the card reuses <see cref="Speakers.SpeakerText.PROPOSED_ModelsMissing"/> as it stands.
/// </summary>
public static class VoiceText
{
    /// <summary>V1: the card title.</summary>
    public const string PROPOSED_CardTitle = "Your voice";

    /// <summary>V2: the card sentence while there is no print.</summary>
    public const string PROPOSED_CardNoPrint = "Dictum can learn how your voice sounds. It keeps your voice print, a list of numbers and not a recording, encrypted on this computer. In meetings recorded on one track, it labels parts that sound like you as You. You can also turn on a filter that skips dictation that does not sound like you. It can make mistakes.";

    /// <summary>V3: the button shown when calibration recordings exist.</summary>
    public const string PROPOSED_UseCalibration = "Use my calibration recordings";

    /// <summary>V4.</summary>
    public const string PROPOSED_RecordNow = "Record my voice now";

    /// <summary>V5: the card sentence once a print exists; {0} the date it was made, {1} the seconds of recording.</summary>
    public const string PROPOSED_CardPrint = "Your voice print was made on {0} from {1} seconds of recording.";

    /// <summary>V6: the toggle for use 1 (meetings.use_voice_print), on once a print exists.</summary>
    public const string PROPOSED_UseInMeetings = "Label speech that sounds like me as You in one-track meeting recordings";

    /// <summary>V7: the toggle for use 2 (audio.your_voice_only), off by default.</summary>
    public const string PROPOSED_YourVoiceOnly = "Skip speech that does not sound like me";

    /// <summary>V8: the sentence under V7.</summary>
    public const string PROPOSED_YourVoiceOnlyNote = "While you dictate, parts that do not sound like you are skipped before any speech service hears them. A cold, a different microphone, a quiet voice or someone with a similar voice can make Dictum skip you or type someone else.";

    /// <summary>V9.</summary>
    public const string PROPOSED_Delete = "Delete my voice print";

    /// <summary>V10: the delete confirmation.</summary>
    public const string PROPOSED_DeleteConfirm = "Delete your voice print? Both uses turn off. You can make a new one any time.";

    /// <summary>V11: enrolment refused, too little speech.</summary>
    public const string PROPOSED_TooShort = "The recording had less than 20 seconds of speech. Record a little more.";

    /// <summary>V12: enrolment refused, the windows disagree.</summary>
    public const string PROPOSED_Inconsistent = "Dictum could not make a consistent voice print from this recording. Try recording again, in a quieter place if you can.";

    /// <summary>V13: a print made with another model is treated as absent.</summary>
    public const string PROPOSED_OtherModel = "Your voice print was made with a different model. Record it again.";

    /// <summary>V15: the pill while dictating with V7 on, when a part is skipped.</summary>
    public const string PROPOSED_PillSkipped = "Audio skipped by voice filter";

    /// <summary>V16: the log line per skipped part (seconds only, never words).</summary>
    public const string PROPOSED_LogSkipped = "voice filter: skipped {0:F1} s";

    /// <summary>V17: the settings-file warning for voice.threshold out of range; {0} the value read, {1} the value used.</summary>
    public const string PROPOSED_ThresholdClamped = "voice.threshold {0} is outside 0.3–0.9: using {1}.";

    /// <summary>V18: the help guide paragraph (site and in-app help place it only once approved; this lane does not touch help).</summary>
    public const string PROPOSED_HelpParagraph = "Learn my voice (Settings > General) makes a voice print from your calibration recordings or a new recording. Dictum labels parts that sound like you as You in one-track meeting recordings, including phone recordings. While dictating, you can also skip parts that do not sound like you. Both can make mistakes. The print is a list of numbers kept encrypted on this computer; it is never uploaded or included in a .dictum file. Delete it any time.";
}
