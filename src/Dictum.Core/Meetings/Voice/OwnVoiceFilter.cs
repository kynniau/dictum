using Dictum.Core;
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

/// <summary>What the filter scores with: the print (already checked for this model) and the CAM++ embedder, or why there is none.</summary>
public sealed record OwnVoiceSource(float[]? Print, IVoiceEmbedder? Embedder, string? Unavailable)
{
    public static OwnVoiceSource Ready(float[] print, IVoiceEmbedder embedder) => new(print, embedder, null);

    /// <param name="reason">Why no part can be scored, for the log (never shown, never words from a dictation).</param>
    public static OwnVoiceSource Missing(string reason) => new(null, null, reason);

    public const string NoPrint = "there is no voice print";
    public const string OtherModel = "the voice print was made with a different model";
    public const string NoModel = "the speaker model is not downloaded";
}

/// <summary>
/// "Your voice only" (design 1.6 D): asked by DictationController about each part (a hands-free cut, or a whole held recording)
/// before the part goes to the pipeline, so a skipped part never reaches a speech engine (local or cloud), is never typed, and never
/// becomes the clean-up context of the next part. The print and the embedder come from the shell, which reads voiceprint.enc; this
/// class never touches a file. Parts are scored with <see cref="VoiceScore.ScoreSpeech"/>: their speech only, as enrolment and
/// --speaker-check --print score it.
/// </summary>
/// <param name="voice">The print and the embedder, or why either is missing (called only while the setting is on).</param>
/// <param name="log">Gets one warning per session when the setting is on but no part can be scored.</param>
public sealed class OwnVoiceFilter(Func<AppSettings> settings, Func<OwnVoiceSource> voice, ILog? log = null)
{
    private int _warned;

    public OwnVoiceVerdict Check(float[] samples16kMono)
    {
        var current = settings();
        if (!current.Audio.YourVoiceOnly) return OwnVoiceVerdict.Off;
        var source = voice();
        if (source.Print is not { } print || source.Embedder is not { } embedder)
        {
            // The switch is left as the user set it; the log says once why nothing is filtered, so a quiet failure is findable.
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                log?.Warn($"Your voice only is on, but no dictation can be checked: {source.Unavailable ?? OwnVoiceSource.NoPrint}. Everything is transcribed as usual.");
            return OwnVoiceVerdict.Off;
        }
        // simplification: a part under MinWindowSeconds passes through unscored, because the extractor is unreliable under 1.5 s
        // (SpeakerSeparator.MinWindowSeconds) and a user's quick "yes" must not be lost. Ceiling: another voice's short interjection
        // is typed. Trigger to retire: a short-window score that proves reliable on the --speaker-check harness.
        if (samples16kMono.Length < SpeakerSeparator.MinWindowSeconds * 16000) return OwnVoiceVerdict.Unscored;
        var (scored, own) = VoiceScore.ScoreSpeech(samples16kMono, print, embedder, current.Voice.Threshold);
        return VoiceScore.MostlyOther(scored, own) ? OwnVoiceVerdict.Skipped : OwnVoiceVerdict.Own;
    }

    /// <summary>The log line for a skipped part (row V16): its length in seconds, never its words.</summary>
    public static string SkippedLogLine(float[] samples16kMono) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, VoiceText.APPROVED_LogSkipped, samples16kMono.Length / 16000.0);
}
