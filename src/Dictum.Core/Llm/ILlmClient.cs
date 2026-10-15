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
}
