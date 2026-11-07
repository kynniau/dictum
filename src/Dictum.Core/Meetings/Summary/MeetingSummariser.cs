using Dictum.Llm;
using MeetingSummary = Dictum.Meetings.Summary;

namespace Dictum.Meetings.Summaries;

public sealed class MeetingSummariser
{
    public const string NotWritten = "Not written: the model's summary could not be checked against the transcript.";
    private readonly int _maxTokens;
    private readonly int _overlapTokens;

    public MeetingSummariser(int maxTokens = 2048, int overlapTokens = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        if (overlapTokens < 0 || overlapTokens >= maxTokens) throw new ArgumentOutOfRangeException(nameof(overlapTokens));
        (_maxTokens, _overlapTokens) = (maxTokens, overlapTokens);
    }

    /// <param name="speakers">A separated meeting's speakers, so each voice is labelled.</param>
    /// <param name="inProcessEngine">True only for the in-process local model. Any other engine (own key, Ollama on any host) is
    /// treated as leaving this computer: it sees "Other n" labels only, never a typed name, and the names are put back into the
    /// reply here (<see cref="SpeakerNameSwap"/>). Defaults to false, so a caller that forgets fails closed.</param>
    public async Task<MeetingSummary> SummariseAsync(MeetingId id, IReadOnlyList<Segment> transcript,
        SummaryTemplate template, ILlmClient client, string engineLabel, CancellationToken cancellationToken, Speakers.MeetingSpeakers? speakers = null,
        bool inProcessEngine = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sent = inProcessEngine ? speakers : SpeakerNameSwap.WithoutNames(speakers);
        var chunks = TranscriptChunker.Chunk(transcript, _maxTokens, _overlapTokens, includeTimestamps: true, sent);
        if (chunks.Count == 0) return Unwritten(template, engineLabel);
        var system = SummaryPrompt.System(template);
        var drafts = new List<string>();
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await client.CompleteAsync(system, SummaryPrompt.ChunkUser(chunks[i], i, chunks.Count), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Partial/refused drafts cannot support a combined summary.
            if (reply.IsRefusal || reply.IsTruncated || string.IsNullOrWhiteSpace(reply.Text))
                return Unwritten(template, engineLabel);
            drafts.Add(reply.Text);
        }
        // simplification: one combine call has no recursive context budgeting; retire when long-meeting context limits are measured per provider.
        var combine = SummaryPrompt.CombineUser(drafts, template);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await client.CompleteAsync(system, combine, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = reply.IsRefusal || reply.IsTruncated
                ? (Ok: (MeetingSummary?)null, Rejection: (string?)"The response was refused or incomplete.")
                // Names go back before the check, so a cloud summary is validated exactly as a local one with names would be;
                // the rejection text sent back on a retry is fixed wording and never carries a name.
                : SummaryValidator.Validate(inProcessEngine ? reply.Text ?? "" : SpeakerNameSwap.Restore(reply.Text ?? "", speakers),
                    transcript, template, engineLabel);
            if (result.Ok is not null) return result.Ok;
            combine = SummaryPrompt.CombineUser(drafts, template) + "\nValidation rejection: " + result.Rejection +
                "\nTry again using only transcript-supported facts, figures and owners.";
        }
        return Unwritten(template, engineLabel);
    }

    private static MeetingSummary Unwritten(SummaryTemplate template, string engine) =>
        new(template.Markdown.TrimEnd() + "\n\n" + NotWritten, engine, template.Id, DateTimeOffset.UtcNow, []);
}
