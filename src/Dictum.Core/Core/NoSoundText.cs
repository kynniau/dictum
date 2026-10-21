namespace Dictum.Core;

/// <summary>
/// What Dictum says when the microphone gives no sound (21/10/2026): the pill label, the balloon for each cause, and the tray
/// panel's line after a dictation that heard nothing. All PROPOSED 21/10/2026, awaiting the product owner's approval: do not
/// reword without it.
/// </summary>
public static class NoSoundText
{
    /// <summary>The pill, while recording goes on with no sound and when the recording ends that way.</summary>
    public const string Pill = "No sound from mic";

    /// <summary>What to check when the microphone delivers only silence: the balloon's advice, and the onboarding microphone test's.</summary>
    public const string SilentAdvice = "Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.";

    /// <summary>The balloon when the microphone delivers only silence.</summary>
    public const string SilentBalloon = "Dictum is not hearing anything. " + SilentAdvice;

    /// <summary>The balloon when the system lists no microphone at all.</summary>
    public const string NoMicrophoneBalloon =
        "No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.";

    /// <summary>The tray panel (and Settings &gt; About) after a dictation that heard nothing, until one types text.</summary>
    public const string HeardNothingLine = "Last dictation heard nothing: check the microphone";

    public static string Balloon(NoSoundCause cause) => cause == NoSoundCause.NoMicrophone ? NoMicrophoneBalloon : SilentBalloon;
}

/// <summary>
/// The platform has no microphone to open (Windows: no default capture device). Its message is the one no-microphone sentence,
/// <see cref="NoSoundText.NoMicrophoneBalloon"/>, so the welcome window's microphone test, calibration and every other window that
/// shows a capture failure say the same words (1.2 polish, 22/10/2026: calibration showed the WASAPI layer's own sentence).
/// </summary>
public sealed class NoMicrophoneException() : InvalidOperationException(NoSoundText.NoMicrophoneBalloon);
