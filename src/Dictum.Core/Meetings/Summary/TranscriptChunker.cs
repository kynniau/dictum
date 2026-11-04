namespace Dictum.Meetings.Summaries;

public static class TranscriptChunker
{
    public static IReadOnlyList<string> Chunk(IReadOnlyList<Segment> transcript, int maxTokens, int overlapTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        if (overlapTokens < 0 || overlapTokens >= maxTokens)
            throw new ArgumentOutOfRangeException(nameof(overlapTokens));
        var chunks = new List<string>();
        var start = 0;
        while (start < transcript.Count)
        {
            var end = start;
            var characters = 0;
            while (end < transcript.Count)
            {
                var next = characters + (end == start ? 0 : 1) + transcript[end].Text.Length;
                if (end > start && next * 0.35 > maxTokens) break;
                // A segment larger than the budget stays intact: splitting would violate the contract.
                characters = next;
                end++;
            }
            chunks.Add(string.Join("\n", transcript.Skip(start).Take(end - start).Select(s => s.Text)));
            if (end == transcript.Count) break;
            var nextStart = end;
            var overlapCharacters = 0;
            while (nextStart > start + 1)
            {
                var candidate = overlapCharacters + (nextStart == end ? 0 : 1) + transcript[nextStart - 1].Text.Length;
                if (candidate * 0.35 > overlapTokens) break;
                overlapCharacters = candidate;
                nextStart--;
            }
            // Shrink overlap until at least one new segment fits; every iteration makes progress.
            while (nextStart < end &&
                (string.Join("\n", transcript.Skip(nextStart).Take(end - nextStart).Select(s => s.Text)).Length
                    + 1 + transcript[end].Text.Length) * 0.35 > maxTokens)
                nextStart++;
            start = nextStart;
        }
        return chunks;
    }
}
