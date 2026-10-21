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

    /// <summary>The balloon when the microphone delivers only silence.</summary>
    public const string SilentBalloon =
        "Dictum is not hearing anything. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.";

    /// <summary>The balloon when the system lists no microphone at all.</summary>
    public const string NoMicrophoneBalloon =
        "No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.";

    /// <summary>The tray panel (and Settings &gt; About) after a dictation that heard nothing, until one types text.</summary>
    public const string HeardNothingLine = "Last dictation heard nothing: check the microphone";

    public static string Balloon(NoSoundCause cause) => cause == NoSoundCause.NoMicrophone ? NoMicrophoneBalloon : SilentBalloon;
}
