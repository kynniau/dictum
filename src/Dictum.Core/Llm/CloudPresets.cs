using Dictum.Settings;

namespace Dictum.Llm;

/// <summary>One Settings > Clean-up preset: it only pre-fills the page's existing fields for the openai-compatible provider.</summary>
/// <param name="Name">What the Service list shows.</param>
/// <param name="BaseUrl">llm.base_url as the provider's own documentation gives it; null for a custom server (the user types it).</param>
/// <param name="DefaultModel">llm.model when the Model field is left empty at this address; null when the user must type one.</param>
/// <param name="KeyVariable">The .env variable whose key goes to this preset's server and to no other; null: no key is ever sent there.</param>
/// <param name="KeyRequired">True: without that key nothing is sent (the rule-based tidy is used); false: the key goes only when it is set.</param>
/// <param name="ModelHint">The Model field's hint when there is no default model.</param>
/// <param name="HostSuffix">
/// For a service whose address has the customer's own name in it (Azure OpenAI's {resource}.openai.azure.com): any https://
/// address whose host ends with this is the preset's server. Null: <paramref name="BaseUrl"/>'s host and port decide.
/// </param>
/// <param name="KeyHeader">
/// The header the key goes in: null for <c>Authorization: Bearer {key}</c>, or a header name whose value is the key alone
/// (Azure OpenAI's <c>api-key</c>).
/// </param>
public sealed record CloudPreset(string Name, string? BaseUrl, string? DefaultModel, string? KeyVariable, bool KeyRequired, string? ModelHint = null,
    string? HostSuffix = null, string? KeyHeader = null)
{
    /// <summary>Every preset runs on the existing <see cref="OpenAiCompatibleLlmClient"/>.</summary>
    public string Provider => LlmSettings.OpenAiCompatible;

    /// <summary>
    /// This service's key from <paramref name="secrets"/> (null when it has none to send: Ollama, or a custom server with no
    /// LLM_API_KEY). False only when the service needs its key and none is set (environment or .env): nothing is sent to it
    /// then. TranscriptCleaner.Create asks it with the keys read at each load and records a key it stops on as
    /// <see cref="TranscriptCleaner.MissingKey"/>; Settings > Clean-up and Privacy read that from the cleaner in use and never
    /// look at the .env files themselves, so they cannot disagree with what is sent.
    /// </summary>
    public bool TryGetKey(SecretStore secrets, out string? key)
    {
        key = KeyVariable is { } variable ? secrets.Get(variable) : null;
        return key is not null || !KeyRequired;
    }
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
/// Keys (lens finding, 16/10/2026: one LLM_API_KEY went to every host, and OpenAI got the dictation with no key at all):
/// each hosted preset has its own variable, sent only to its own server and required there; a custom address gets
/// LLM_API_KEY when it is set; Ollama on this PC gets none. <see cref="ForAddress"/> decides, from llm.base_url alone.
/// </summary>
public static class CloudPresets
{
    /// <summary>A server at an address the user types. Any address that is not a preset's own server is treated as this one.</summary>
    public static CloudPreset Custom { get; } = new("Custom server (OpenAI-compatible)", null, null, SecretStore.LlmKey, KeyRequired: false, "the model name the server uses");

    public static IReadOnlyList<CloudPreset> All { get; } =
    [
        // Endpoint POST https://api.openai.com/v1/chat/completions: https://developers.openai.com/api/reference/resources/chat (read 16/10/2026).
        // gpt-5.4-nano defaults to reasoning effort "none" (https://developers.openai.com/api/docs/models/gpt-5.4-nano), the only effort at
        // which GPT-5.4 accepts temperature (https://developers.openai.com/api/docs/guides/latest-model/gpt-5.4), both read 16/10/2026.
        // simplification: gpt-5.4-nano shuts down on 01/04/2027 (https://developers.openai.com/api/docs/deprecations, read 16/10/2026), and
        // OpenAI's current GPT-6 models list temperature as unsupported at their default effort, while this client always sends it.
        // Before that date, teach OpenAiCompatibleLlmClient reasoning_effort "none" (or no temperature) for such models, then move this on.
        // Moving the default on does not change settings.json files that already say gpt-5.4-nano, so the same change must also migrate
        // them: EffectiveSettings mapping the old model id to its successor with a log line, or a notice telling the user to pick
        // another model. Without one of those, clean-up for those users falls back to the rule-based tidy on every dictation from that date.
        new("OpenAI", "https://api.openai.com/v1", "gpt-5.4-nano", SecretStore.OpenAiKey, KeyRequired: true),

        // base_url and model exactly as https://ai.google.dev/gemini-api/docs/openai gives them (page updated 14/10/2026, read 16/10/2026);
        // gemini-3.8-flash is stable, no shutdown date announced (https://ai.google.dev/gemini-api/docs/deprecations, read 16/10/2026).
        // Google recommends temperature 1.0 for Gemini 3 models (https://ai.google.dev/gemini-api/docs/gemini-3); this client sends 0.
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.8-flash", SecretStore.GeminiKey, KeyRequired: true),

        // The local server's base_url from https://docs.ollama.com/api/openai-compatibility (read 16/10/2026), which also says the local
        // server ignores any API key, so none is sent. No default model: it is whichever model the user pulled.
        new("Ollama on this PC", "http://localhost:11434/v1/", null, null, KeyRequired: false, "the name of a model you pulled in Ollama"),

        Custom,
    ];

    /// <summary>
    /// The preset whose server llm.base_url names (the same host and port; localhost, 127.0.0.1 and ::1 are one host), else
    /// <see cref="Custom"/>. Which key goes with each request is read from the result, so a preset's key only ever goes to
    /// that preset's server, and Ollama on this PC gets none however its address is written.
    /// </summary>
    public static CloudPreset ForAddress(string? baseUrl) => Match(All, Custom, baseUrl);

    /// <summary>
    /// The one address rule for every preset list (clean-up here, cloud speech in SpeechPresets): the preset whose server
    /// <paramref name="baseUrl"/> names, else <paramref name="custom"/>. A preset with a <see cref="CloudPreset.HostSuffix"/>
    /// matches any https:// host ending with it; the others match their own host and port, with localhost, 127.0.0.1 and ::1 one host.
    /// Hosts are compared without a final "." (api.openai.com. is api.openai.com). A preset's own host name on another port
    /// (https://api.openai.com:8443/v1) is neither the preset nor a custom server: it gets no key at all (lens F6, 22/10/2026).
    /// </summary>
    public static CloudPreset Match(IEnumerable<CloudPreset> presets, CloudPreset custom, string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var address)) return custom;
        var host = HostOf(address);
        foreach (var p in presets)
        {
            if (p.HostSuffix is { } suffix)
            {
                if (address.Scheme == Uri.UriSchemeHttps && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return p;
                continue;
            }
            if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var own)) continue;
            var sameHost = string.Equals(HostOf(own), host, StringComparison.OrdinalIgnoreCase);
            if (own.Port == address.Port && (sameHost || (own.IsLoopback && address.IsLoopback))) return p;
            if (sameHost && !own.IsLoopback) return custom with { KeyVariable = null, KeyRequired = false };
        }
        return custom;
    }

    /// <summary>The host as presets and consent compare it: without a final "." (a fully qualified name is the same host).</summary>
    public static string HostOf(Uri address) => address.Host.TrimEnd('.');

    /// <summary>True while <paramref name="address"/> is still the preset's own (ignoring spaces and a final slash), not one typed over it.</summary>
    public static bool IsPresetAddress(CloudPreset preset, string? address) =>
        string.Equals(Normalise(address), Normalise(preset.BaseUrl), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// llm.model for a Model field left empty: the preset's default model only at the preset's own address. An address typed
    /// over it is another server, which may not have that model, so null: the user types the model name.
    /// </summary>
    public static string? DefaultModelFor(CloudPreset preset, string? address) => IsPresetAddress(preset, address) ? preset.DefaultModel : null;

    private static string Normalise(string? url) => (url ?? "").Trim().TrimEnd('/');
}
