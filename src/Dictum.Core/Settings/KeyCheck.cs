using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dictum.Llm;
using Dictum.Transcribe;

namespace Dictum.Settings;

/// <summary>How a key goes with the check: as the service's own requests send it.</summary>
public enum KeyCheckAuth
{
    /// <summary><c>Authorization: Bearer {key}</c> (OpenAI, Google Gemini's OpenAI layer, Groq, a custom server).</summary>
    Bearer,

    /// <summary>The key alone in an <c>api-key</c> header (Azure OpenAI, <see cref="CloudPreset.KeyHeader"/>).</summary>
    ApiKeyHeader,

    /// <summary>Anthropic: <c>Authorization: Bearer {key}</c> and an <c>anthropic-version</c> header.</summary>
    Anthropic,
}

/// <summary>Where "Check this key" may send one key: the consented host, the URL there, and how the key goes.</summary>
/// <param name="Variable">The key variable (one of <see cref="SecretStore.KeyNames"/>) this target checks.</param>
/// <param name="Host">The host the consent step on record names; <paramref name="Url"/> is always on it.</param>
public sealed record KeyCheckTarget(string Variable, string Host, Uri Url, KeyCheckAuth Auth);

/// <summary>
/// "Check this key" (API keys, decision D3, accepted 30/10/2026; K-UI lane): one request per click that proves a key is accepted,
/// sent only where the key is already allowed to go. It is offered only for a service whose consent step for that exact host is on
/// record (the settings in use carry consent.json's entries, <see cref="CloudConsentFile.Apply"/>), and it goes to that host alone,
/// to the cheapest authenticated request each service has that carries no user text: listing its models. A GET with no body, so no
/// dictation, dictionary word, instruction or recording can go with it. Never automatic and never on by default: the page asks it
/// once per click. It runs through the provider client (<see cref="OfflineGuardHandler"/>, no redirects), so offline only refuses it,
/// and a plain http:// address off this PC is refused here before anything is sent. What it says names the status code and the
/// service's own message with every key-like token hidden (<see cref="Redact"/>); nothing of the key is ever in it.
/// </summary>
/// <remarks>
/// The request per service (each read on the provider's own page on 04/11/2026):
/// <list type="bullet">
/// <item>Anthropic: <c>GET https://api.anthropic.com/v1/models</c> (https://platform.claude.com/docs/en/api/models/list), with
/// <c>Authorization: Bearer</c> and <c>anthropic-version: 2026-10-21</c> (https://platform.claude.com/docs/en/api/overview, the
/// Authentication table, and https://platform.claude.com/docs/en/api/versioning). An llm.base_url set for Anthropic is used in place
/// of https://api.anthropic.com, as the clean-up client does.</item>
/// <item>OpenAI: <c>GET https://api.openai.com/v1/models</c>, <c>Authorization: Bearer</c>
/// (https://developers.openai.com/api/reference/resources/models/methods/list).</item>
/// <item>Google Gemini: <c>GET https://generativelanguage.googleapis.com/v1beta/openai/models</c>, <c>Authorization: Bearer</c>
/// (the "List models" section of https://aistudio.google.com/docs/openai, to which ai.google.dev/gemini-api/docs/openai now redirects).</item>
/// <item>Azure OpenAI: <c>GET {endpoint}/openai/v1/models</c> with the key in <c>api-key</c>
/// (https://learn.microsoft.com/en-us/rest/api/microsoft-foundry/azureopenai/models, "List models" and "Authentication"); the
/// speech preset's address is already <c>{endpoint}/openai/v1</c>, so it is the address plus /models.</item>
/// <item>Groq: <c>GET https://api.groq.com/openai/v1/models</c>, <c>Authorization: Bearer</c> (https://console.groq.com/docs/api-reference).</item>
/// <item>A custom server (LLM_API_KEY, SPEECH_API_KEY): the address plus /models with <c>Authorization: Bearer</c>, the OpenAI shape the
/// clean-up and speech clients already speak there. Inferred, not read anywhere: a server that does not list models answers 404, and
/// the page says what it answered.</item>
/// </list>
/// </remarks>
public static class KeyCheck
{
    /// <summary>How long one check may take before it is given up (the provider client has no timeout of its own).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The anthropic-version header the Anthropic check sends (https://platform.claude.com/docs/en/api/versioning, read 04/11/2026).</summary>
    public const string AnthropicVersion = "2026-10-21";

    /// <summary>The most of a service's refusal read: its message is short, and a page of HTML is not shown at all.</summary>
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>The most of a service's message shown.</summary>
    private const int MaxMessageLength = 300;

    // PROPOSED 04/11/2026 (approve-api-keys.md rows 30 to 35): what a check says.

    /// <summary>PROPOSED 04/11/2026 (row 30): the service accepted the key.</summary>
    public const string Works = "The key works.";

    /// <summary>PROPOSED 04/11/2026 (row 31): the service answered with an error. The message is the service's own, key-like tokens hidden.</summary>
    public static string Refused(string host, int code, string? reason, string? message) =>
        $"The key did not work: {host} answered {code}{(string.IsNullOrWhiteSpace(reason) ? "" : " " + reason)}." + (message is { Length: > 0 } m ? $" {host} says: {m}" : "");

    /// <summary>PROPOSED 04/11/2026 (row 32): offline only refused the check.</summary>
    public const string OfflineRefused = "Offline only is on, so the key was not checked.";

    /// <summary>PROPOSED 04/11/2026 (row 33): a plain http:// address off this PC; nothing was sent.</summary>
    public static string PlainHttpRefused(string host) => $"The key was not checked: {host} uses http://, which would send the key unencrypted.";

    /// <summary>PROPOSED 04/11/2026 (row 34): no answer in time.</summary>
    public static string NoAnswer(string host) => $"The key was not checked: {host} did not answer within {Timeout.TotalSeconds:0} seconds.";

    /// <summary>PROPOSED 04/11/2026 (row 35): the request could not be made (no network, a name that does not resolve).</summary>
    public static string NotReached(string host, string reason) => $"The key was not checked: {host} could not be reached ({reason}).";

    /// <summary>PROPOSED 04/11/2026 (row 36): what stands in for each hidden token in a service's message.</summary>
    public const string Hidden = "[hidden]";

    /// <summary>
    /// Where a key for <paramref name="variable"/> may be checked under <paramref name="settings"/> (the settings in use, with the
    /// consents on record applied), or null: there is no consent step on record for the host that key goes to, the address is refused
    /// as insecure, or no service in use takes that key. The clean-up service comes first, then the speech service; the same key
    /// (OPENAI_API_KEY) may serve both, and either consent is enough, since each names api.openai.com.
    /// </summary>
    public static KeyCheckTarget? Target(string variable, AppSettings settings)
    {
        foreach (var target in Targets(settings))
        {
            if (target.Variable == variable) return target;
        }
        return null;
    }

    private static IEnumerable<KeyCheckTarget> Targets(AppSettings s)
    {
        var llm = s.Llm;
        if (CloudPolicy.CloudHost(llm) is { } host && CloudPolicy.HasConsent(llm) && CloudPolicy.InsecureEndpoint(llm) is null)
        {
            if (llm.Provider == LlmSettings.Anthropic)
            {
                if (Address((llm.BaseUrl ?? AnthropicLlmClient.DefaultBaseUrl).TrimEnd('/') + "/v1/models", host) is { } url)
                    yield return new KeyCheckTarget(SecretStore.AnthropicKey, host, url, KeyCheckAuth.Anthropic);
            }
            else if (CloudPresets.ForAddress(llm.BaseUrl).KeyVariable is { } variable && Address(llm.BaseUrl!.Trim().TrimEnd('/') + "/models", host) is { } url)
            {
                yield return new KeyCheckTarget(variable, host, url, KeyCheckAuth.Bearer);
            }
        }
        var speech = s.Speech;
        if (SpeechPolicy.HasConsent(speech) && SpeechPolicy.InsecureEndpoint(speech) is null && SpeechPolicy.Host(speech) is { } speechHost)
        {
            var service = SpeechPresets.ForAddress(speech.BaseUrl);
            if (service.KeyVariable is { } variable && Address(speech.BaseUrl!.Trim().TrimEnd('/') + "/models", speechHost) is { } url)
                yield return new KeyCheckTarget(variable, speechHost, url, service.KeyHeader is null ? KeyCheckAuth.Bearer : KeyCheckAuth.ApiKeyHeader);
        }
    }

    /// <summary>The URL when it is absolute and on <paramref name="host"/> exactly (the consented host, compared as consent compares it).</summary>
    private static Uri? Address(string url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && string.Equals(CloudPresets.HostOf(u), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ? u : null;

    /// <summary>
    /// Sends the one check for <paramref name="target"/> with <paramref name="key"/> through <paramref name="http"/> (the provider client)
    /// and says what came back. Never throws for what the network or the service does; <paramref name="cancel"/> cancels it.
    /// </summary>
    public static async Task<string> RunAsync(HttpClient http, KeyCheckTarget target, string key, bool offlineOnly, CancellationToken cancel)
    {
        var url = target.Url;
        // The guard below this client refuses offline; this says so first, and the address rule holds whatever the settings said.
        if (!string.Equals(CloudPresets.HostOf(url), target.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The check's address is not on its host.", nameof(target));
        if (url.Scheme == Uri.UriSchemeHttp && !url.IsLoopback) return PlainHttpRefused(target.Host);
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) return PlainHttpRefused(target.Host);
        if (offlineOnly && !url.IsLoopback) return OfflineRefused;

        using var request = new HttpRequestMessage(HttpMethod.Get, url); // no body: nothing the user said or wrote goes with it
        switch (target.Auth)
        {
            case KeyCheckAuth.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation("api-key", key);
                break;
            case KeyCheckAuth.Anthropic:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return Works;
            var body = await ReadCapped(response, deadline.Token).ConfigureAwait(false);
            return Refused(target.Host, (int)response.StatusCode, Redact(response.ReasonPhrase, key), Redact(ProviderMessage(body), key));
        }
        catch (OfflineOnlyException)
        {
            return OfflineRefused;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return NoAnswer(target.Host);
        }
        catch (HttpRequestException ex)
        {
            return NotReached(target.Host, Redact(ex.HttpRequestError == HttpRequestError.Unknown ? ex.Message : ex.HttpRequestError.ToString(), key) ?? "");
        }
    }

    private static async Task<string> ReadCapped(HttpResponseMessage response, CancellationToken cancel)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var buffer = new byte[MaxBodyBytes];
        var read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false) is var n and > 0) read += n;
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>
    /// The service's own error message from a JSON error body: <c>error.message</c> (OpenAI, Anthropic, Azure, Groq, Google's
    /// OpenAI layer), a string <c>error</c>, a top-level <c>message</c>, or the same in the first item of an array. Null for anything
    /// else, an HTML page included: only a message the service wrote for this is shown.
    /// </summary>
    public static string? ProviderMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var root = JsonNode.Parse(body);
            if (root is JsonArray { Count: > 0 } array) root = array[0];
            if (root is not JsonObject o) return null;
            var text = o["error"] switch
            {
                JsonObject e when e["message"] is JsonValue m && m.TryGetValue<string>(out var s) => s,
                JsonValue e when e.TryGetValue<string>(out var s) => s,
                _ => o["message"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null,
            };
            if (text is null) return null;
            text = Regex.Replace(new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), @"\s+", " ").Trim();
            return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength].TrimEnd() + "…";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with every word that could be a key, or any part of <paramref name="key"/>, replaced by
    /// <see cref="Hidden"/> (decision D2: never any part of a key on screen). A word goes when it holds the key, shares any four
    /// characters in a row with it, holds a '*' (a masked key such as "sk-proj-****ab12"), or is 20 characters or more of letters,
    /// digits and -_. with both a letter and a digit (a token of any kind).
    /// </summary>
    public static string? Redact(string? text, string key)
    {
        // simplification: the four-character rule also hides an ordinary word that happens to share four letters with the key ("proj"
        // in "project" for a key starting sk-proj-). Hiding too much is the safe side of D2; retire it only for a parser of each
        // service's own error format, if users find the messages too gappy to act on.
        if (text is null) return null;
        var k = key.Trim();
        return Regex.Replace(text, @"\S+", word =>
        {
            var w = word.Value;
            var keyLike = w.Contains('*') || (k.Length > 0 && w.Contains(k, StringComparison.Ordinal)) || SharesRun(w, k, 4)
                          || (Regex.IsMatch(w, @"[A-Za-z0-9_\-.]{20,}") && w.Any(char.IsAsciiLetter) && w.Any(char.IsAsciiDigit));
            return keyLike ? Hidden : w;
        });
    }

    private static bool SharesRun(string word, string key, int run)
    {
        if (key.Length < run) return false;
        for (var i = 0; i + run <= word.Length; i++)
        {
            if (key.Contains(word.AsSpan(i, run), StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
