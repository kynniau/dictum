using System.Text.Json.Nodes;
using Dictum.App;
using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.UI.Shell;

/// <summary>
/// Cloud speech's user-facing half (spec "Cloud speech" §2-§4, 22/10/2026; the engine is <see cref="CloudSpeech"/>): the speech
/// consent step, a second dialog apart from clean-up's, that alone records the speech entry of consent.json; switching cloud
/// speech off, which withdraws that entry only; and the lines every surface shows, all read from the speech step in use
/// (<see cref="Speech"/>), as the clean-up surfaces read <see cref="Llm.TranscriptCleaner.Status"/>. It mirrors clean-up's
/// <see cref="EnableCloud"/>, <see cref="DisableCloud"/> and <see cref="SaveCloudOff"/>: the switch-off holds in memory first,
/// before either file is changed, and saving it is tried again at every reload and once at quit.
/// </summary>
public sealed partial class AppShell
{
    private bool _speechWithdrawn; // Switch cloud speech off (or "Send my voice" undone), until consent.json and settings.json both keep it, or the consent step records a new agreement
    private (string? Consent, string? Settings) _speechOffNotSaved; // why the last try could not change consent.json or save settings.json (SaveSpeechOff)

    // APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (owner, as a block): the strings around the consent step. The consent text and its two buttons are
    // APPROVED and live in SpeechPolicy; these are the strings around them.

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: the speech consent dialog's title.</summary>
    internal const string SpeechConsentTitle = "Send your voice to a cloud service?";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: the consent step was declined; nothing was recorded.</summary>
    internal const string SpeechNotSwitchedOn = "Cloud speech was not switched on; nothing is sent.";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: switching on was refused because offline only is on (clean-up's sentence, for speech).</summary>
    internal const string SpeechOfflineRefused = "offline_only is on, so cloud speech is refused. Switch offline only off in Settings > Privacy first.";

    /// <summary>The approved sentence that cloud speech makes untrue, in the Clean-up card's introduction and Privacy's cloud clean-up sentence.</summary>
    internal const string AudioNeverLeavesSentence = "Audio never leaves this PC.";

    /// <summary>
    /// APPROVED 22/10/2026 (owner, 16:27 AEDT), verbatim: do not reword. Takes the place of <see cref="AudioNeverLeavesSentence"/>
    /// in those two places only while cloud speech sends each recording; the approved originals stay byte for byte otherwise.
    /// </summary>
    internal const string AudioByDefaultSentence = "Audio never leaves this PC by default, though advanced users can add their own cloud speech and clean-up services in settings.";

    /// <summary>An approved line with <see cref="AudioNeverLeavesSentence"/>, as shown now: unchanged, or with the owner's replacement while cloud speech sends audio.</summary>
    internal string WithAudioSentence(string approved) =>
        _speech.SendsAudio ? approved.Replace(AudioNeverLeavesSentence, AudioByDefaultSentence, StringComparison.Ordinal) : approved;

    /// <summary>APPROVED 22/10/2026 (owner, 16:22-16:31 AEDT, the lens's wording): the caption above Privacy's clean-up summary while cloud speech sends audio.</summary>
    internal const string CloudCleanupCaption = "Cloud clean-up:";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4): the tray panel's speech line while cloud speech sends each recording (the footer, since the owner's 16:22-16:31 decisions).</summary>
    internal static string SpeechTrayLine(string host) => $"Cloud speech · audio leaves this PC ({host})";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4): Settings > Privacy's summary while cloud speech sends each recording.</summary>
    internal static string SpeechPrivacyLine(string host) =>
        $"Your voice goes to {host} for speech recognition. Advanced option: your audio leaves this PC. Dictum does not validate or certify how that service handles it.";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4): Settings > About's speech line while cloud speech sends each recording.</summary>
    internal static string SpeechAboutLine(string host, string model, string localModel) => $"Speech: cloud, {host} ({model}); {localModel} on this PC is the fallback.";

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4): the pill while a recording is with the cloud speech service, in place of "Transcribing…".</summary>
    internal const string HearingCloudPill = "Hearing… (cloud)";

    /// <summary>
    /// APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: what Switch cloud speech off says when a file could not be changed, mirroring clean-up's approved
    /// <see cref="OffNotSaved"/> sentences with "cloud speech" and "your speech consent".
    /// </summary>
    internal static string SpeechOffNotSaved(string? consent, string? settings) => (consent, settings) switch
    {
        ({ } c, null) => $"Cloud speech is off, but consent.json could not be changed ({c}). Nothing is sent. Dictum tries again at the next reload and when it quits; until it is changed, your speech consent stays on file.",
        (null, { } s) => $"Cloud speech is off for this session, but settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits.",
        ({ } c, { } s) => $"Cloud speech is off for this session, but consent.json could not be changed ({c}) and settings.json could not be saved ({s}). Nothing is sent. Dictum tries again at the next reload and when it quits; if both are still unchanged then, the next start turns cloud speech back on, unless settings.json cannot be read, in which case Dictum starts offline.",
        _ => throw new ArgumentException("Both files keep the switch: there is nothing to say."),
    };

    /// <summary>
    /// APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (lens on PR #54): the token after the speech runtime in the tray panel's header, the icon's tooltip and
    /// the support emails' models line while cloud speech sends each recording ("base.en · Vulkan · cloud speech · …"). No host:
    /// a company's own address could be private, as the feedback email's clean-up fact leaves it out.
    /// </summary>
    internal const string CloudSpeechToken = "cloud speech";

    /// <summary>" · cloud speech" while cloud speech sends each recording, else empty.</summary>
    private string CloudSpeechPart => _speech.SendsAudio ? $" · {CloudSpeechToken}" : "";

    /// <summary>True while each recording goes to a speech service (the speech step built at the last load).</summary>
    public bool SpeechSendsAudio => _speech.SendsAudio;

    /// <summary>The speech step's own line (<see cref="CloudSpeech.Status"/>): on, failed and fallen back, waiting, refused, or local.</summary>
    public string SpeechStatusLine => _speech.Status;

    /// <summary>
    /// Cloud speech is switched on in the settings in use: openai-compatible, with the speech consent on record for the host it
    /// would send to. It may still send nothing (offline only, no key, translate on); <see cref="SpeechStatusLine"/> says why.
    /// </summary>
    public bool CloudSpeechOn => _settings.Speech.Provider == SpeechSettings.OpenAiCompatible && SpeechPolicy.HasConsent(_settings.Speech);

    /// <summary>
    /// APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4, this composition): the tray panel's speech line, in its footer (owner, 16:22-16:31 AEDT: once,
    /// where "Nothing leaves this PC" sits when off), while cloud speech sends each recording, with the failed line under it after a
    /// fallback, else null.
    /// </summary>
    internal string? SpeechTrayValue => _speech is { SendsAudio: true, Host: { } host }
        ? _speech.LastFailure is null ? SpeechTrayLine(host) : $"{SpeechTrayLine(host)}\n{_speech.Status}"
        : null;

    /// <summary>
    /// APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword (spec §4, this composition). Settings > About's speech line: the cloud line while it sends each recording, the speech step's line after a fallback or
    /// while cloud speech is set but sends nothing, and null for the default (the speech model on this PC, which the models line names).
    /// </summary>
    internal string? SpeechAboutValue => _speech switch
    {
        { SendsAudio: true, Host: { } host, Model: { } model, LastFailure: null } => SpeechAboutLine(host, model, Models.CurrentName),
        _ when _speech.SendsAudio || _settings.Speech.Provider != SpeechSettings.Local => $"Speech: {_speech.Status.TrimEnd('.')}.",
        _ => null,
    };

    /// <summary>
    /// The pill's label for <paramref name="view"/> when cloud speech changes it, else null (the pill's own word): "Hearing… (cloud)"
    /// while a recording is with the service, and, once a dictation is pasted after a failed request, the fallback line
    /// (<see cref="SpeechStatus.FallbackPill"/>) in place of "Pasted".
    /// </summary>
    internal string? PillLabel(PillView view) => view switch
    {
        PillView.Transcribing when _speech.SendsAudio => HearingCloudPill,
        PillView.Done when _speech is { SendsAudio: true, LastFailure: not null } => SpeechStatus.FallbackPill,
        _ => null,
    };

    /// <summary>
    /// Settings > Models > Cloud speech > "Use cloud speech…" (spec §3): the speech consent step names the host and what is sent;
    /// only its agree button records the speech entry of consent.json (provider, host, time, app version), and only then is
    /// speech.* written to settings.json. Declining writes nothing. A plain http:// address is refused, and offline only refuses
    /// it outright. When settings.json cannot be written, the speech consent just recorded is withdrawn again; when that fails
    /// too, the switch-off holds instead (<see cref="DisableCloudSpeech"/>): the consent on file is not used this session. A
    /// clean-up consent on file is never touched. Null: switched on and saved; else what to say.
    /// </summary>
    public string? EnableCloudSpeech(SpeechSettings proposed)
    {
        if (SpeechPolicy.InsecureEndpoint(proposed) is { } insecure) return insecure + ".";
        if (_settings.OfflineOnly) return SpeechOfflineRefused;
        if (proposed is not { Provider: SpeechSettings.OpenAiCompatible, BaseUrl: { } baseUrl, Model: { } model } || SpeechPolicy.Host(proposed) is not { } host)
            return SpeechAddressMissing;
        var service = SpeechPresets.ForAddress(baseUrl);
        // Every address needs this step, a server on this PC included: the recording leaves Dictum either way (SpeechPolicy.HasConsent).
        if (Dialogs.Ask(SpeechConsentTitle, SpeechPolicy.ConsentText(host, service.KeyVariable ?? SecretStore.SpeechKey),
                new DialogChoice(SpeechPolicy.AgreeButton(host)), new DialogChoice(SpeechPolicy.DeclineButton, IsDefault: true)) != 0)
        {
            _log.Info($"Cloud speech consent step declined for {host}; nothing was recorded.");
            return SpeechNotSwitchedOn;
        }
        CloudConsentFile.WriteSpeech(Paths.ConsentFile, new CloudConsentRecord(SpeechSettings.OpenAiCompatible, host, DateTimeOffset.UtcNow, AppVersion));
        _log.Info($"Cloud speech agreed to in the speech consent step: {host}.");
        var withdrawn = _speechWithdrawn;
        _speechWithdrawn = false; // before the write: its reload must load the service just agreed to
        if (Write(("speech.provider", JsonValue.Create(SpeechSettings.OpenAiCompatible)), ("speech.base_url", JsonValue.Create(baseUrl)), ("speech.model", JsonValue.Create(model))))
            return null;
        _speechWithdrawn = withdrawn;
        try
        {
            CloudConsentFile.WithdrawSpeech(Paths.ConsentFile); // a clean-up consent on file stays
            _log.Info($"Speech consent to {host} withdrawn again: settings.json could not be written, so cloud speech was not switched on.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _speechWithdrawn = true; // not used this session; SaveSpeechOff withdraws it at the next reload and at quit
            _log.Warn($"Speech consent to {host} could not be withdrawn again ({SettingsFile.ShortReason(ex, Paths.ConsentFile)}); it is not used this session, and withdrawing it is tried again at the next reload and at quit.");
            Reload(userRequested: false);
        }
        return ModelCoordinator.WriteFailed;
    }

    /// <summary>APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword: switching on was asked without an address or a model (the page asks for both first).</summary>
    internal const string SpeechAddressMissing = "Type the service's https:// address and the model (for Azure OpenAI, the deployment name).";

    /// <summary>
    /// Settings > Models > Cloud speech > "Switch cloud speech off": in memory first (<see cref="SpeechChoices"/>), so the reload
    /// replaces the speech step with the local model, a request under way is cancelled and nothing more is sent; then the speech
    /// entry of consent.json is withdrawn (a clean-up consent stays) and speech.provider = local is written
    /// (<see cref="SaveSpeechOff"/>). Null when both files keep it, else <see cref="SpeechOffNotSaved"/>: the switch then holds,
    /// and saving it is tried again at every reload and once at quit.
    /// </summary>
    public string? DisableCloudSpeech()
    {
        _speechWithdrawn = true;
        _log.Info("Cloud speech switched off; speech consent withdrawn.");
        Reload(userRequested: false);
        return _speechOffNotSaved is (null, null) ? null : SpeechOffNotSaved(_speechOffNotSaved.Consent, _speechOffNotSaved.Settings);
    }

    /// <summary>
    /// What the cloud speech switch-off keeps in force over the files (<see cref="SessionChoices"/>): no speech service and no
    /// speech consent, whatever settings.json and consent.json still say, until both keep it.
    /// </summary>
    private AppSettings SpeechChoices(AppSettings settings)
    {
        if (!_speechWithdrawn || settings.Speech is { Provider: SpeechSettings.Local, Consent: null }) return settings;
        _log.Info($"Cloud speech is off for this session: the settings loaded name {settings.Speech.Provider}{(SpeechPolicy.Host(settings.Speech) is { } host ? $" at {host}" : "")}, which is not used, because the switch-off is not saved yet.");
        return settings with { Speech = settings.Speech with { Provider = SpeechSettings.Local, Consent = null } };
    }

    /// <summary>
    /// Cloud speech off (<see cref="DisableCloudSpeech"/>): withdraws the speech entry of consent.json (a clean-up consent stays;
    /// with nothing left the file goes) and writes speech.provider = local while settings.json names openai-compatible or cannot
    /// be read. One log line per try.
    /// </summary>
    private void SaveSpeechOff()
    {
        string? consent = null, settings = null;
        try
        {
            CloudConsentFile.WithdrawSpeech(Paths.ConsentFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            consent = SettingsFile.ShortReason(ex, Paths.ConsentFile);
        }
        var file = SettingsLoader.LoadFile(Paths.SettingsFile);
        if (!file.Usable || file.Settings.Speech.Provider != SpeechSettings.Local)
            Models.TryWrite(out settings, reloadAfter: false, ("speech.provider", JsonValue.Create(SpeechSettings.Local)));
        _speechOffNotSaved = (consent, settings);
        _speechWithdrawn = consent is not null || settings is not null;
        if (!_speechWithdrawn)
        {
            _log.Info("Cloud speech off is saved: consent.json holds no speech consent, and settings.json names the speech model on this PC.");
            return;
        }
        var why = new[] { consent is null ? null : $"consent.json could not be changed: {consent}", settings is null ? null : $"settings.json could not be saved: {settings}" };
        _log.Warn($"Cloud speech off is not saved yet ({string.Join("; ", why.OfType<string>())}); it stays off for this session, and saving it is tried again at the next reload and at quit.");
    }
}
