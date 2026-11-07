namespace Dictum.Meetings.Summaries;

public static class TranscriptChunker
{
    /// <param name="speakers">A separated meeting's speakers: each segment then says its voice (the user's name for it, else "Other n") after
    /// the timestamp and channel, which stay as they are because summary quotes cite them (SummaryValidator).</param>
    public static IReadOnlyList<string> Chunk(IReadOnlyList<Segment> transcript, int maxTokens, int overlapTokens, bool includeTimestamps = false,
        Speakers.MeetingSpeakers? speakers = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        if (overlapTokens < 0 || overlapTokens >= maxTokens)
            throw new ArgumentOutOfRangeException(nameof(overlapTokens));
        transcript = Transcribe.EchoDeduplicator.Filter(transcript);
        var rendered = transcript.Select(s => (includeTimestamps ? $"[{(long)s.Start.TotalMinutes:00}:{s.Start.Seconds:00} {s.Channel}] " : "")
            + (speakers?.LabelOf(s) is { } label ? label + ": " : "") + s.Text).ToArray();
        var chunks = new List<string>();
        var start = 0;
        while (start < transcript.Count)
        {
            var end = start;
            var characters = 0;
            while (end < transcript.Count)
            {
                var next = characters + (end == start ? 0 : 1) + rendered[end].Length;
                if (end > start && next * 0.35 > maxTokens) break;
                // A segment larger than the budget stays intact: splitting would violate the contract.
                characters = next;
                end++;
            }
            chunks.Add(string.Join("\n", rendered.Skip(start).Take(end - start)));
            if (end == transcript.Count) break;
            var nextStart = end;
            var overlapCharacters = 0;
            while (nextStart > start + 1)
            {
                var candidate = overlapCharacters + (nextStart == end ? 0 : 1) + rendered[nextStart - 1].Length;
                if (candidate * 0.35 > overlapTokens) break;
                overlapCharacters = candidate;
                nextStart--;
            }
            // Shrink overlap until at least one new segment fits; every iteration makes progress.
            while (nextStart < end &&
                (string.Join("\n", rendered.Skip(nextStart).Take(end - nextStart)).Length
                    + 1 + rendered[end].Length) * 0.35 > maxTokens)
                nextStart++;
            start = nextStart;
        }
        return chunks;
    }
}
