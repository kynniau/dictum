using Dictum.Settings;

namespace Dictum.Llm;

/// <summary>
/// SPEC §22 and docs/STORE.md (Store Policies 10.5.2, 10.5.4): a clean-up provider that sends dictations off this PC runs
/// only after the user agreed to it in Dictum's own consent dialog, which names what is sent and to whom; the agreement is
/// recorded in consent.json (provider, host, time, app version), never in settings.json. A plain http://
/// endpoint is refused unless it is on this PC, so a dictation is never sent unencrypted over a network.
/// </summary>
public static class CloudPolicy
{
    /// <summary>The host the provider would send dictations to when that host is off this PC; null for local, none and loopback servers.</summary>
    public static string? CloudHost(LlmSettings s) => s.Provider switch
    {
        LlmSettings.Anthropic => Uri.TryCreate(s.BaseUrl ?? AnthropicLlmClient.DefaultBaseUrl, UriKind.Absolute, out var a) ? a.Host : null,
        LlmSettings.OpenAiCompatible when s.BaseUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var o) && !o.IsLoopback => o.Host,
        _ => null,
    };

    /// <summary>True when nothing would leave the PC, or the user agreed to this exact host.</summary>
    public static bool HasConsent(LlmSettings s) => CloudHost(s) is not { } host || string.Equals(s.CloudConsent, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the endpoint is refused (non-loopback http://), or null.</summary>
    public static string? InsecureEndpoint(LlmSettings s) =>
        s.Provider is LlmSettings.Anthropic or LlmSettings.OpenAiCompatible && s.BaseUrl is { } url
        && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback
            ? $"llm.base_url {u.Host} uses http://, which would send dictations unencrypted; only a server on this PC may use http://"
            : null;

    /// <summary>What the consent dialog says is sent, and to whom (SPEC §22; Store Policies 10.5.2).</summary>
    public static string ConsentText(LlmSettings s, string host) =>
        $"With cloud clean-up on, every dictation's text (never the audio) is sent to {host}" +
        (s.Provider == LlmSettings.Anthropic ? " (Anthropic)" : "") +
        " together with Dictum's clean-up instructions, which include the words in your personal dictionary. " +
        (s.Provider == LlmSettings.Anthropic
            ? "It uses your own Anthropic API key (ANTHROPIC_API_KEY), and Anthropic's terms and privacy policy apply to what it receives. "
            : "Whoever runs that server receives the text; its own terms apply. ") +
        "Nothing is sent until you agree. You can switch it off at any time in Settings > Clean-up, and offline only mode refuses it.";
}
