using Dictum.Settings;

namespace Dictum.Llm;

/// <summary>One Settings > Clean-up preset: it only pre-fills the page's existing fields for the openai-compatible provider.</summary>
/// <param name="Name">What the Service list shows.</param>
/// <param name="BaseUrl">llm.base_url as the provider's own documentation gives it; null for a custom server (the user types it).</param>
/// <param name="DefaultModel">llm.model when the Model field is left empty; null when the user must type one.</param>
/// <param name="KeyVariable">The .env name the key goes under (the one the client already reads); null when no key is needed.</param>
/// <param name="ModelHint">The Model field's hint when there is no default model.</param>
public sealed record CloudPreset(string Name, string? BaseUrl, string? DefaultModel, string? KeyVariable, string? ModelHint = null)
{
    /// <summary>Every preset runs on the existing <see cref="OpenAiCompatibleLlmClient"/>.</summary>
    public string Provider => LlmSettings.OpenAiCompatible;
}

/// <summary>
/// The presets on Settings > Clean-up (decided 16/10/2026: cloud clean-up stays an advanced option, but the main services
/// need no hand-typed address). A preset adds no settings key and never records consent: switching cloud clean-up on still
/// goes through AppShell.EnableCloud, so a host off this PC still needs the consent dialog, a plain http:// address off
/// this PC is still refused, and offline_only still refuses it. Each address and default model below was read on the
/// provider's own documentation on 16/10/2026, at the page named beside it.
/// Perplexity is left out: on 16/10/2026 its documentation offered Chat Completions (the only format this client speaks)
/// only through the Orchestrator API, in private preview (https://docs.perplexity.ai/docs/router/quickstart); its
/// generally available Agent API uses the Responses format (https://docs.perplexity.ai/docs/agent-api/openai-compatibility).
/// </summary>
public static class CloudPresets
{
    public static IReadOnlyList<CloudPreset> All { get; } =
    [
        // Endpoint POST https://api.openai.com/v1/chat/completions: https://developers.openai.com/api/reference/resources/chat (read 16/10/2026).
        // gpt-5.4-nano defaults to reasoning effort "none" (https://developers.openai.com/api/docs/models/gpt-5.4-nano), the only effort at
        // which GPT-5.4 accepts temperature (https://developers.openai.com/api/docs/guides/latest-model/gpt-5.4), both read 16/10/2026.
        // simplification: gpt-5.4-nano shuts down on 01/04/2027 (https://developers.openai.com/api/docs/deprecations, read 16/10/2026), and
        // OpenAI's current GPT-6 models list temperature as unsupported at their default effort, while this client always sends it.
        // Before that date, teach OpenAiCompatibleLlmClient reasoning_effort "none" (or no temperature) for such models, then move this on.
        new("OpenAI", "https://api.openai.com/v1", "gpt-5.4-nano", SecretStore.LlmKey),

        // base_url and model exactly as https://ai.google.dev/gemini-api/docs/openai gives them (page updated 14/10/2026, read 16/10/2026);
        // gemini-3.8-flash is stable, no shutdown date announced (https://ai.google.dev/gemini-api/docs/deprecations, read 16/10/2026).
        // Google recommends temperature 1.0 for Gemini 3 models (https://ai.google.dev/gemini-api/docs/gemini-3); this client sends 0.
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.8-flash", SecretStore.LlmKey),

        // The local server's base_url from https://docs.ollama.com/api/openai-compatibility (read 16/10/2026), which also says the local
        // server ignores any API key. No default model: it is whichever model the user pulled.
        new("Ollama on this PC", "http://localhost:11434/v1/", null, null, "the name of a model you pulled in Ollama"),

        new("Custom server (OpenAI-compatible)", null, null, SecretStore.LlmKey, "the model name the server uses"),
    ];
}
