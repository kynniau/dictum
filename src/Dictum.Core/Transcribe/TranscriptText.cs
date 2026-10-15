using System.Text.RegularExpressions;

namespace Dictum.Transcribe;

public static partial class TranscriptText
{
    // Whisper's non-speech annotations, e.g. "[BLANK_AUDIO]", "[MUSIC]", "(silence)", "[ Silence ]".
    [GeneratedRegex(@"[\[(]\s*(blank_audio|silence|music|noise|inaudible|no speech|sound|applause|laughter)\s*[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex Annotation();

    // A segment that is nothing but bracketed spans: whisper's description of a sound ("[farting]", "(water running)").
    [GeneratedRegex(@"^(\s*[\[(][^\])]{1,60}[\])])+\s*$")]
    private static partial Regex OnlyAnnotations();

    // Square brackets are never speech in whisper's output, so any [...] inside speech is an annotation too.
    [GeneratedRegex(@"\[[^\]]{0,60}\]")]
    private static partial Regex SquareBracketSpan();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>
    /// Concatenates segment text without the non-speech annotations: a segment that is only bracketed descriptions is
    /// dropped (whisper's tags are open-ended: "[farting]", "[BEEP]", "(water running)" were all seen on noise, lens probe
    /// 15/10/2026), every [...] span is removed, the known ones in round brackets too; then trims and collapses whitespace.
    /// </summary>
    public static string Normalise(IEnumerable<string?> segments)
    {
        var speech = segments
            .Where(s => !string.IsNullOrWhiteSpace(s) && !OnlyAnnotations().IsMatch(s!))
            .Select(s => SquareBracketSpan().Replace(s!, " "));
        var joined = Annotation().Replace(string.Join(" ", speech), " ");
        joined = Whitespace().Replace(joined, " ").Trim();
        return SpaceBeforePunctuation().Replace(joined, "$1");
    }
}
