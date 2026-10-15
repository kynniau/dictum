using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dictum.Llm;

/// <summary>
/// OpenAI-compatible chat completions (Ollama, LM Studio, vLLM, hosted gateways):
/// POST {base_url}/chat/completions with model, system+user messages, temperature 0.
/// The bearer key (LLM_API_KEY) is optional for local servers.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;

    public OpenAiCompatibleLlmClient(HttpClient http, string baseUrl, string model, string? apiKey)
    {
        _http = http;
        _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
        _model = model;
        _apiKey = apiKey;
    }

    public string Name => $"openai-compatible/{_model} at {_endpoint.Host}";

    public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = rawTranscript },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: response bodies can echo request headers on some gateways.
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {_endpoint.Host}", null, response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    internal static LlmReply Parse(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return new LlmReply(null, "no_choices");
        }
        var choice = choices[0];
        var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()?.ToLowerInvariant() : null;
        string? text = null;
        if (choice.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString()))
            {
                return new LlmReply(null, "refusal");
            }
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) text = content.GetString();
        }
        return new LlmReply(text, finish);
    }
}
