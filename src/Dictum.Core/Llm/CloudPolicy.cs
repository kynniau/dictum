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

    /// <summary>
    /// The host of an OpenAI-compatible server on this PC (a loopback llm.base_url), or null. Dictum's own request stays on
    /// this PC, but the server may pass the text on (Ollama can serve its cloud models once you sign in to it), which Dictum cannot see.
    /// </summary>
    public static string? LocalServerHost(LlmSettings s) =>
        s.Provider == LlmSettings.OpenAiCompatible && s.BaseUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback ? u.Host : null;

    /// <summary>
    /// True when no dictation's text goes to another program: clean-up runs inside Dictum (the local model or the rule-based
    /// tidy), or the configured service is not in use (<paramref name="usesModel"/> false: offline_only, a missing key, a
    /// refused address). A server on this PC never counts as nothing leaving, because Dictum cannot see whether it forwards the text.
    /// </summary>
    public static bool NothingLeaves(LlmSettings s, bool usesModel) => !usesModel || (CloudHost(s) is null && LocalServerHost(s) is null);

    /// <summary>
    /// Settings > Privacy and the Settings > Clean-up state line while an OpenAI-compatible server on this PC does the
    /// clean-up. Approved by the product owner on 16/10/2026 as written: never reword it.
    /// </summary>
    public static string LocalServerLine(string host, string model) =>
        $"On: a server on this PC ({host}, model {model}) tidies each dictation. Dictum cannot see whether that server forwards your text elsewhere.";

    /// <summary>True when nothing would leave the PC, or the user agreed to this exact host.</summary>
    public static bool HasConsent(LlmSettings s) => CloudHost(s) is not { } host || string.Equals(s.CloudConsent, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why the endpoint is refused, or null: a plain http:// address off this PC (the dictation would cross the network
    /// unencrypted), or any http:// address for Anthropic, because its consent dialog says the connection is encrypted (HTTPS).
    /// </summary>
    public static string? InsecureEndpoint(LlmSettings s)
    {
        if (s.Provider is not (LlmSettings.Anthropic or LlmSettings.OpenAiCompatible) || s.BaseUrl is not { } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp) return null;
        if (!u.IsLoopback) return $"llm.base_url {u.Host} uses http://, which would send dictations unencrypted; only a server on this PC may use http://";
        return s.Provider == LlmSettings.Anthropic
            ? $"llm.base_url {u.Host} uses http://, but Anthropic clean-up needs an https:// address, because its consent step says the connection is encrypted"
            : null;
    }

    /// <summary>What the consent dialog says is sent, and to whom (SPEC §22; Store Policies 10.5.2), then the warning paragraph.</summary>
    public static string ConsentText(LlmSettings s, string host) =>
        $"With cloud clean-up on, every dictation's text (never the audio) is sent to {host}" +
        (s.Provider == LlmSettings.Anthropic ? " (Anthropic)" : "") +
        " together with Dictum's clean-up instructions, which include the words in your personal dictionary. " +
        (s.Provider == LlmSettings.Anthropic
            ? "It uses your own Anthropic API key (ANTHROPIC_API_KEY), and Anthropic's terms and privacy policy apply to what it receives."
            : KeySentence(CloudPresets.ForAddress(s.BaseUrl)) + " Whoever runs that server receives the text; its own terms apply.") +
        "\n\n" + ConsentWarning(host) + "\n\n" +
        "Nothing is sent until you agree. You can switch it off at any time in Settings > Clean-up, and offline only mode refuses it.";

    /// <summary>Which key goes with the text to an OpenAI-compatible service: the preset's own variable at its server, LLM_API_KEY elsewhere.</summary>
    private static string KeySentence(CloudPreset service) => service switch
    {
        { KeyVariable: null } => "It sends no API key.",
        { KeyRequired: true } => $"It uses your own {service.Name} API key ({service.KeyVariable}).",
        _ => $"It uses your own API key ({service.KeyVariable}) if you have set one.",
    };

    /// <summary>
    /// The consent dialog's warning paragraph for every provider that sends text off this PC (cloud clean-up is an advanced
    /// option). Copy approved by the product owner on 16/10/2026 as written: never reword, shorten or soften it.
    /// </summary>
    public static string ConsentWarning(string host) =>
        $"WARNING: this is an advanced option and it is less private than local clean-up. Your text leaves this PC to be processed on {host}'s servers. Dictum encrypts the connection (HTTPS) but cannot see, validate or certify how that service stores, protects or uses your text, or who else can read it. Do not dictate confidential, personal or client information with cloud clean-up on unless you have checked that service's terms yourself.";

    /// <summary>
    /// Appended to the Settings > Clean-up state line and the Settings > Privacy summary while cloud clean-up sends text to a
    /// host off this PC. Approved on 16/10/2026 as written: never reword it.
    /// </summary>
    public const string StateLineWarning = "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it.";

    /// <summary>
    /// The tray panel's privacy line (FlyoutWindow) while cloud clean-up sends text to a host off this PC; Help quotes it.
    /// Approved by the product owner on 16/10/2026 as written: never reword it.
    /// </summary>
    public const string TrayLine = "Cloud clean-up on · text leaves this PC";

    /// <summary>
    /// The Settings > Clean-up state line and the Settings > Privacy summary while cloud clean-up is on for a service that
    /// needs its own key and the clean-up in use found none (<see cref="TranscriptCleaner.MissingKey"/>), so nothing is sent.
    /// Approved by the product owner on 16/10/2026 as written: never reword it.
    /// </summary>
    public static string WaitingForKeyLine(string variable) => $"On, but waiting for your {variable}: nothing is sent until you add it to .env.";

    /// <summary>
    /// The Settings > Clean-up state line and the Settings > Privacy summary while cloud clean-up is on but offline only
    /// refuses it (<see cref="TranscriptCleaner.OfflineRefusal"/>), so nothing is sent. Approved by the product owner on 16/10/2026
    /// as written: never reword it; only the product owner's replacement may change it.
    /// </summary>
    public const string OfflineOnlyLine = "On, but offline only is on: nothing is sent until you switch offline only off.";

    /// <summary>
    /// The Settings > Clean-up state line while cloud clean-up was agreed to but the clean-up in use sends nothing for a reason no other
    /// line names, such as an address refused as insecure (<see cref="InsecureEndpoint"/>): the rule-based tidy runs on this PC
    /// (<see cref="NothingLeaves"/>). Approved by the product owner on 16/10/2026 as written: never reword it; only the product owner's replacement may change it.
    /// </summary>
    public const string NothingSentLine = "On, but nothing is sent: the clean-up in use runs on this PC.";
}
