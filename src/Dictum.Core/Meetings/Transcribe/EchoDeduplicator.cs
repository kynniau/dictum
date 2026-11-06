using System.Text.RegularExpressions;

namespace Dictum.Meetings.Transcribe;

/// <summary>Deterministic, text-only suppression of microphone copies of system speech.</summary>
public static class EchoDeduplicator
{
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
        { "um", "uh", "erm", "er", "hmm" };

    public static bool IsEcho(Segment you, Segment others) => IsEcho(you, new[] { others });

    public static bool IsEcho(Segment you, IEnumerable<Segment> others)
    {
        if (you.Channel != Channel.You) return false;
        var left = Words(you.Text);
        if (left.Length == 0) return false;
        var window = others.Where(s => s.Channel == Channel.Others &&
                !(you.Start > s.End && you.Start - s.End > Slack) &&
                !(s.Start > you.End && s.Start - you.End > Slack))
            .OrderBy(s => s.Start).Select(s => (Segment: s, Words: Words(s.Text))).ToArray();
        // simplification: cap the reference at 400 words nearest in time (word times are
        // interpolated within each segment), restoring transcript order after selection.
        // A user repeating remote words at the same instant is dropped; a real echo
        // transcribed very differently without an onset match is kept. Retire these
        // text-only limits with the user voice print planned for 1.6.
        var right = window.SelectMany(s => s.Words.Select((word, i) =>
                (Word: word, Time: s.Segment.Start.TotalSeconds +
                    (s.Segment.End - s.Segment.Start).TotalSeconds * (i + .5) / s.Words.Length)))
            .Select((entry, i) => (entry.Word, Index: i,
                Distance: Math.Max(0, Math.Max(you.Start.TotalSeconds - entry.Time, entry.Time - you.End.TotalSeconds))))
            .OrderBy(entry => entry.Distance).ThenBy(entry => entry.Index).Take(400)
            .OrderBy(entry => entry.Index).Select(entry => entry.Word).ToArray();
        if (left.Length == 0 || right.Length == 0) return false;
        var onsetMatched = window.Any(s => Math.Abs((s.Segment.Start - you.Start).TotalSeconds) <= .5 &&
            (double)Math.Min(left.Length, s.Words.Length) / Math.Max(left.Length, s.Words.Length) >= .75);
        // One or two words ("yes", "okay"): an echo only when the words appear verbatim and the You segment starts with
        // an Others segment of about the same length. A user's own "yes" while the remote voice is mid-sentence is kept.
        if (left.Length < 3)
            return onsetMatched && Enumerable.Range(0, Math.Max(0, right.Length - left.Length + 1))
                .Any(start => left.SequenceEqual(right.Skip(start).Take(left.Length)));

        var threshold = onsetMatched ? .5 : .8;
        var minimum = (int)Math.Ceiling(left.Length * .8);
        var maximum = Math.Min(right.Length, (int)Math.Floor(left.Length * 1.2));
        // Each start shares one distance table across all candidate lengths: O(n*m*k),
        // n = You words, m = reference words, k = maximum candidate length.
        for (var start = 0; start + minimum <= right.Length; start++)
        {
            var length = Math.Min(maximum, right.Length - start);
            var previous = Enumerable.Range(0, length + 1).ToArray();
            var current = new int[length + 1];
            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= length; j++)
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + (left[i - 1] == right[start + j - 1] ? 0 : 1));
                (previous, current) = (current, previous);
            }
            for (var count = minimum; count <= length; count++)
                if (previous[count] <= (1 - threshold) * Math.Max(left.Length, count) + 1e-9) return true;
        }
        return false;
    }

    public static IReadOnlyList<Segment> Filter(IReadOnlyList<Segment> transcript)
    {
        var others = transcript.Where(s => s.Channel == Channel.Others).ToArray();
        return transcript.Where(s => s.Channel != Channel.You || !IsEcho(s, others)).ToArray();
    }

    private static string[] Words(string text) => Regex.Matches(
        text.ToLowerInvariant().Replace("'", "").Replace("’", ""), @"[\p{L}\p{N}]+")
        .Select(m => m.Value).Where(word => !Fillers.Contains(word)).ToArray();
}
