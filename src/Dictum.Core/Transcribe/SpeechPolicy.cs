using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// Cloud speech's gate (spec "Cloud speech" §2-§3, 22/10/2026): a recording leaves Dictum only after the user agreed to that
/// exact host in the speech consent step, which consent.json records apart from the clean-up consent (agreeing to send text
/// never agrees to send audio). offline_only refuses any host off this PC and a plain http:// address off this PC is refused,
/// by clean-up's own rules (<see cref="CloudPolicy.OfflineRefusal"/>, <see cref="CloudPolicy.PlainHttpRefusal"/>).
/// </summary>
public static class SpeechPolicy
{
    /// <summary>The host recordings would go to with speech.provider = openai-compatible, on this PC or not; null for local.</summary>
    public static string? Host(SpeechSettings s) =>
        s.Provider == SpeechSettings.OpenAiCompatible && Uri.TryCreate(s.BaseUrl?.Trim(), UriKind.Absolute, out var u) ? CloudPresets.HostOf(u) : null;

    /// <summary>
    /// True only when the speech consent on record names the host recordings would go to. Unlike clean-up there is no exemption
    /// for a server on this PC: every address needs the speech consent step, because the audio leaves Dictum either way.
    /// </summary>
    public static bool HasConsent(SpeechSettings s) => Host(s) is { } host && string.Equals(s.Consent?.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why offline_only forbids cloud speech, or null: any host off this PC is refused; a server on this PC is allowed.</summary>
    public static string? OfflineRefusal(SpeechSettings s, bool offlineOnly) =>
        s.Provider == SpeechSettings.OpenAiCompatible ? CloudPolicy.OfflineRefusal("cloud speech", s.BaseUrl, offlineOnly) : null;

    /// <summary>
    /// Why a plain http:// speech.base_url is refused, or null. Off this PC it would carry the recording unencrypted over the
    /// network (clean-up's rule and wording). On this PC too (orchestrator decision, 22/10/2026, superseding the spec's §2 on
    /// this point): the approved consent says the recording goes "over the internet" and that Dictum "encrypts the connection
    /// (HTTPS)", as Anthropic clean-up's refusal of any http:// address does for its consent (<see cref="CloudPolicy.InsecureEndpoint"/>).
    /// </summary>
    public static string? InsecureEndpoint(SpeechSettings s)
    {
        if (s.Provider != SpeechSettings.OpenAiCompatible || !Uri.TryCreate(s.BaseUrl?.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp) return null;
        return CloudPolicy.PlainHttpRefusal("speech.base_url", s.BaseUrl) ?? LoopbackHttpRefusal(CloudPresets.HostOf(u));
    }

    /// <summary>
    /// PROPOSED (22/10/2026), pending the owner: not in the 17:22 AEDT approved list (re-lens on PR #54). The refusal of a plain
    /// http:// speech.base_url on this PC, in the speech step's "nothing sent" status and its log line (<see cref="InsecureEndpoint"/>).
    /// The settings loader's own warning is <c>SettingsLoader.SpeechBaseUrlProblem</c>, not this.
    /// </summary>
    public static string LoopbackHttpRefusal(string host) =>
        $"speech.base_url {host} uses http://, but cloud speech needs an https:// address, also on this PC, because its consent step says the connection is encrypted";

    /// <summary>
    /// The speech consent step's text (spec "Cloud speech" §3), with {host} and {VAR} (<paramref name="keyVariable"/>) filled in.
    /// APPROVED 22/10/2026 (clause revised 16:31 AEDT): do not reword. The product owner approved the appendix text verbatim
    /// (22/10/2026, 10:50 AEDT), then approved one clause of the last paragraph (16:31 AEDT): "offline only mode refuses it"
    /// became "offline only mode refuses any service that is not on this PC", because offline only still allows a speech server
    /// on this PC (loopback https), as it does for clean-up. Only the owner's written replacement may change it. Pinned byte for
    /// byte in CloudSpeechTests and CloudSpeechUiTests.
    /// </summary>
    public static string ConsentText(string host, string keyVariable) =>
        $"With cloud speech on, the recording of every dictation, your voice, is sent over the internet to {host} to be turned into text, together with the words in your personal dictionary as a hint. It uses your own API key ({keyVariable})." +
        "\n\n" +
        $"Dictum encrypts the connection (HTTPS) but cannot see, check or certify what {host} keeps, for how long, how it protects it, or who else can listen to it. By switching cloud speech on you accept responsibility for the security and privacy of the speech sent there. Check that service's terms yourself, and do not dictate confidential, personal or client information with cloud speech on unless you have." +
        "\n\n" +
        "Nothing is sent until you agree. You can switch it off at any time in Settings > Models, and offline only mode refuses any service that is not on this PC; your speech model on this PC is used again.";

    /// <summary>The consent step's agree button (spec §3). APPROVED 22/10/2026: do not reword.</summary>
    public static string AgreeButton(string host) => $"Send my voice to {host}";

    /// <summary>The consent step's decline button (spec §3). APPROVED 22/10/2026: do not reword.</summary>
    public const string DeclineButton = "Keep speech on this PC";
}

/// <summary>
/// The speech step's state for Settings, About and the self-test (<see cref="CloudSpeech.Status"/>), as
/// <see cref="TranscriptCleaner.OffStatus"/> is for clean-up. Every line is APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword;
/// the waiting-for-key and offline-only lines reuse clean-up's approved lines, which name no text or audio.
/// </summary>
public static class SpeechStatus
{
    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: speech.provider = local, the default.</summary>
    public const string Local = "on this PC: your speech model hears every dictation (nothing leaves it)";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4): the Settings > Models line while on.</summary>
    public static string On(string host, string model) =>
        $"On: your voice goes to {host} (model {model}) to be turned into text. Advanced option: your audio leaves this PC. Dictum does not validate or certify how that service handles it.";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §2): the last request failed and the speech model on this PC heard that dictation; shown until the next success.</summary>
    public static string Failed(string reason) => $"On, but the last request failed ({reason}); the local model heard this dictation";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: openai-compatible is set but the speech consent step has not recorded this host.</summary>
    public const string NotAgreed = "On, but not agreed to yet: nothing is sent until you agree to it in Settings > Models; your speech model on this PC hears every dictation.";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §2): v1 refuses cloud speech while whisper.translate is on.</summary>
    public const string TranslateRefused = "On, but translate is on and cloud speech does not translate: nothing is sent; your speech model on this PC hears every dictation.";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: nothing is sent for a reason no other line names (an address refused as insecure, speech.base_url or speech.model not set).</summary>
    public static string NothingSent(string reason) => $"On, but nothing is sent ({reason}): your speech model on this PC hears every dictation.";

    /// <summary>The key the service needs is not set: clean-up's approved line, word for word.</summary>
    public static string WaitingForKey(string variable) => CloudPolicy.WaitingForKeyLine(variable);

    /// <summary>offline_only refuses it: clean-up's approved line, word for word.</summary>
    public const string OfflineOnly = CloudPolicy.OfflineOnlyLine;

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §2), for the UI lane's pill after a failed request: the speech model on this PC heard the dictation.</summary>
    public const string FallbackPill = "Heard on this PC (cloud speech failed)";
}
