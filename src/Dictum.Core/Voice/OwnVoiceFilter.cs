using Dictum.Meetings.Speakers;
using Dictum.Settings;

namespace Dictum.Meetings.Voice;

/// <summary>The filter's word on one dictated part.</summary>
public enum OwnVoiceVerdict
{
    /// <summary>audio.your_voice_only is off, or there is no usable print or speaker model: no gate at all.</summary>
    Off,

    /// <summary>Shorter than <see cref="SpeakerSeparator.MinWindowSeconds"/>: passed on without a score.</summary>
    Unscored,

    /// <summary>Scored and not mostly another voice: passed on.</summary>
    Own,

    /// <summary>Most of its windows scored below voice.threshold: dropped before any speech engine.</summary>
    Skipped,
}

/// <summary>
/// "Your voice only" (design 1.6 D): asked by DictationController about each part (a hands-free cut, or a whole held recording)
/// before the part goes to the pipeline, so a skipped part never reaches a speech engine (local or cloud), is never typed, and never
/// becomes the clean-up context of the next part. The print and the embedder come from the shell, which reads voiceprint.enc; this
/// class never touches a file.
/// </summary>
/// <param name="voice">The print (already checked for this model) and the CAM++ embedder, or null when either is missing.</param>
public sealed class OwnVoiceFilter(Func<AppSettings> settings, Func<(float[] Print, IVoiceEmbedder Embedder)?> voice)
{
    public OwnVoiceVerdict Check(float[] samples16kMono)
    {
        var current = settings();
        if (!current.Audio.YourVoiceOnly || voice() is not { } owner) return OwnVoiceVerdict.Off;
        // simplification: a part under MinWindowSeconds passes through unscored, because the extractor is unreliable under 1.5 s
        // (SpeakerSeparator.MinWindowSeconds) and a user's quick "yes" must not be lost. Ceiling: another voice's short interjection
        // is typed. Trigger to retire: a short-window score that proves reliable on the --speaker-check harness.
        if (samples16kMono.Length < SpeakerSeparator.MinWindowSeconds * 16000) return OwnVoiceVerdict.Unscored;
        var (scored, own) = VoiceScore.Score(samples16kMono, 0, samples16kMono.Length, owner.Print, owner.Embedder, current.Voice.Threshold);
        return VoiceScore.MostlyOther(scored, own) ? OwnVoiceVerdict.Skipped : OwnVoiceVerdict.Own;
    }

    /// <summary>The log line for a skipped part (row V16): its length in seconds, never its words.</summary>
    public static string SkippedLogLine(float[] samples16kMono) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, VoiceText.PROPOSED_LogSkipped, samples16kMono.Length / 16000.0);
}
