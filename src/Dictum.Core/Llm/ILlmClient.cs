namespace Dictum.Llm;

/// <summary>Outcome of one provider call, before validation.</summary>
/// <param name="Text">The model's text, or null when the provider gave none.</param>
/// <param name="StopReason">Provider stop/finish reason, normalised to lower case ("end_turn", "refusal", "max_tokens"...).</param>
public sealed record LlmReply(string? Text, string? StopReason)
{
    public bool IsRefusal => StopReason is "refusal" or "content_filter";
    public bool IsTruncated => StopReason is "max_tokens" or "length";
}

/// <summary>One cleanup request to a provider. Implementations throw on transport/HTTP errors.</summary>
public interface ILlmClient
{
    string Name { get; }

    Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken);

    /// <summary>
    /// A hands-free part (SPEC §3 amendment, 16/10/2026): <paramref name="carried"/>, the closing words of the part typed before
    /// it, go with the request as context, never as the dictation. This default sends them as a line after the system prompt
    /// (<see cref="CleanupPrompt.WithCarried"/>); the local model puts them after its cached prompt prefix instead.
    /// </summary>
    Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, string carried, CancellationToken cancellationToken) =>
        CompleteAsync(CleanupPrompt.WithCarried(systemPrompt, carried), rawTranscript, cancellationToken);

    /// <summary>Estimated time for this dictation, when the provider can tell (the local model); null otherwise.</summary>
    TimeSpan? Predict(string rawTranscript) => null;
}

/// <summary>A loaded local cleanup model (<see cref="LocalLlmEngine"/>; tests use fakes).</summary>
public interface ILocalLlmEngine : IDisposable
{
    string ModelPath { get; }

    /// <summary>Tokens per second for prompt evaluation and for generation (0 = not measured yet).</summary>
    (double Prompt, double Generation) Rates { get; }

    /// <summary>Evaluates the prompt prefix into the cache and measures both rates; the token stops it at quit.</summary>
    Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default);

    int CountTokens(string text);

    TimeSpan? Predict(int dictationTokens);

    Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
        CancellationToken cancellationToken);

    /// <summary>
    /// A hands-free part with <paramref name="carried"/> as context. <see cref="LocalLlmEngine"/> puts it after the cached
    /// prompt prefix, so the prefix stays evaluated; this default (for stand-ins) puts it after the system prompt.
    /// </summary>
    Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, string carried, int maxTokens,
        CancellationToken cancellationToken) =>
        CompleteAsync(CleanupPrompt.WithCarried(systemPrompt, carried), examples, raw, maxTokens, cancellationToken);
}
