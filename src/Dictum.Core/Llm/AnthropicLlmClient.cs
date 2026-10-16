using Anthropic;
using Anthropic.Models.Messages;

namespace Dictum.Llm;

/// <summary>
/// The opt-in cloud provider (llm.provider = anthropic; the default is local): Anthropic Messages API via the official
/// C# SDK (NuGet "Anthropic"). Non-streaming, thinking disabled, effort low, no temperature/top_p (rejected on
/// claude-haiku-5-5). The API key comes from ANTHROPIC_API_KEY (environment or .env) and is never placed in the prompt.
/// The endpoint is pinned: the SDK would otherwise honour an ANTHROPIC_BASE_URL environment variable and send
/// transcripts and the key to whatever host it names. Only llm.base_url in settings.json can change it. Its
/// OpenTelemetry spans are switched off for the same reason: with ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE=content
/// (set for another tool) they would carry the transcript and the prompt to any in-process exporter.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient, IDisposable
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly bool _ownsHttp;

    /// <param name="baseUrl">llm.base_url from settings.json, or null for https://api.anthropic.com.</param>
    /// <param name="http">Production passes the app's offline-guarded HttpClient; tests pass one over a recording handler. It is
    /// shared: <see cref="Dispose"/> leaves it open.</param>
    public AnthropicLlmClient(string apiKey, string model, TimeSpan timeout, string? baseUrl = null, HttpClient? http = null)
    {
        _model = model;
        _ownsHttp = http is null;
        BaseUrl = baseUrl ?? DefaultBaseUrl;
        // OpenTelemetry = false: the SDK then reads none of its ANTHROPIC_OPEN_TELEMETRY* variables (12.57.0 XML docs).
        _client = http is null
            ? new AnthropicClient { ApiKey = apiKey, Timeout = timeout, MaxRetries = 1, BaseUrl = BaseUrl, OpenTelemetry = false }
            : new AnthropicClient { ApiKey = apiKey, Timeout = timeout, MaxRetries = 1, BaseUrl = BaseUrl, HttpClient = http, OpenTelemetry = false };
    }

    public string BaseUrl { get; }

    public string Name => $"anthropic/{_model} at {new Uri(BaseUrl).Host}";

    public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = TokenBudget.MaxTokens(rawTranscript),
            System = systemPrompt,
            Thinking = new ThinkingConfigDisabled(),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new() { Role = Role.User, Content = rawTranscript }],
        };

        var message = await _client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        return ToReply(message);
    }

    /// <summary>Checks stop_reason before reading content (SPEC §5); a refusal never yields text.</summary>
    internal static LlmReply ToReply(Message message)
    {
        var stopReason = message.StopReason?.Raw()?.ToLowerInvariant();
        if (stopReason is "refusal") return new LlmReply(null, stopReason);
        var text = string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new LlmReply(text, stopReason);
    }

    /// <summary>Disposes the SDK client only when it made its own HttpClient: disposing it disposes the HttpClient it was given too.</summary>
    public void Dispose()
    {
        if (_ownsHttp) (_client as IDisposable)?.Dispose();
    }
}
