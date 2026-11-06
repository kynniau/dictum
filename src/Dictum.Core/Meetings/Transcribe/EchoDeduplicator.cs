using System.Text.RegularExpressions;

namespace Dictum.Meetings.Transcribe;

/// <summary>Deterministic, text-only suppression of microphone copies of system speech.</summary>
public static class EchoDeduplicator
{
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
        { "um", "uh", "erm", "er", "hmm" };

    public static bool IsEcho(Segment you, Segment others)
    {
        if (you.Channel != Channel.You || others.Channel != Channel.Others ||
            you.Start > others.End && you.Start - others.End > Slack ||
            others.Start > you.End && others.Start - you.End > Slack) return false;
        var left = Words(you.Text);
        var right = Words(others.Text);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left.SequenceEqual(right)) return true;

        // Guard containment and fuzzy matching: short interjections and substantial extra speech
        // are not echoes. In particular, "yes" inside a remote sentence must survive.
        if (Math.Min(left.Length, right.Length) < 3 ||
            (double)Math.Min(left.Length, right.Length) / Math.Max(left.Length, right.Length) < .8) return false;
        var leftPhrase = " " + string.Join(' ', left) + " ";
        var rightPhrase = " " + string.Join(' ', right) + " ";
        if (leftPhrase.Contains(rightPhrase, StringComparison.Ordinal) ||
            rightPhrase.Contains(leftPhrase, StringComparison.Ordinal)) return true;

        // Similarity = 1 - token Levenshtein distance / max(token counts), threshold >= 0.8.
        // Unit-cost insert/delete/substitute preserves word order unlike a bag-of-words measure.
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length] * 5 <= Math.Max(left.Length, right.Length);
    }

    // simplification: compare whole segments only, never split overlapping speech, and prefer
    // Others even for an independently spoken near-identical You sentence. Small extra phrases
    // within the 0.8 threshold cannot be distinguished from transcription errors. Retire these
    // text-only limits when a real echo canceller is available in the capture path.
    public static IReadOnlyList<Segment> Filter(IReadOnlyList<Segment> transcript)
    {
        var others = transcript.Where(s => s.Channel == Channel.Others).ToArray();
        return transcript.Where(s => s.Channel != Channel.You || !others.Any(o => IsEcho(s, o))).ToArray();
    }

    private static string[] Words(string text) => Regex.Matches(
        text.ToLowerInvariant().Replace("'", "").Replace("’", ""), @"[\p{L}\p{N}]+")
        .Select(m => m.Value).Where(word => !Fillers.Contains(word)).ToArray();
}
