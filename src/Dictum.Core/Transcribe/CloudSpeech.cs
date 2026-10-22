using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// The speech step's provider (cloud speech, Core lane, 22/10/2026): the speech model on this PC (speech.provider = local,
/// the default), or a cloud service that hears each recording first (<see cref="CloudSpeechEngine"/>). Built at every settings
/// load, like <see cref="Llm.TranscriptCleaner"/>; the pipeline keeps its one seam (<see cref="GetAsync"/>). The local model is
/// loaded as before whatever this says, so it is always there as the fallback.
/// </summary>
public sealed class CloudSpeech
{
    private readonly Func<CancellationToken, Task<ITranscriber>> _local;
    private readonly CloudSpeechEngine? _engine;
    private readonly string _status;

    private CloudSpeech(Func<CancellationToken, Task<ITranscriber>> local, CloudSpeechEngine? engine, string status, string? missingKey)
    {
        _local = local;
        _engine = engine;
        _status = status;
        MissingKey = missingKey;
    }

    /// <summary>The speech model on this PC only: nothing is sent.</summary>
    public static CloudSpeech Local(Func<CancellationToken, Task<ITranscriber>> local) => new(local, null, SpeechStatus.Local, null);

    /// <summary>
    /// The transcriber for the next recording: the cloud engine when it may send, else the local model (waiting for a load in
    /// progress, as <see cref="ModelManager.GetAsync"/> does). The cloud engine does not wait for the local model: it asks
    /// for it only when a request fails.
    /// </summary>
    public Task<ITranscriber> GetAsync(CancellationToken cancellationToken) =>
        _engine is { } engine ? Task.FromResult<ITranscriber>(engine) : _local(cancellationToken);

    /// <summary>True when each recording is sent to a speech service (this PC's own server included); false: the local model only.</summary>
    public bool SendsAudio => _engine is not null;

    /// <summary>The host recordings go to while <see cref="SendsAudio"/>, else null.</summary>
    public string? Host => _engine?.Host;

    /// <summary>
    /// The key variable whose absence stopped the service when this was built, or null; the UI reads it from here, never from
    /// the .env files, as clean-up's <see cref="Llm.TranscriptCleaner.MissingKey"/>.
    /// </summary>
    public string? MissingKey { get; }

    /// <summary>The speech step's state (<see cref="SpeechStatus"/>); while on, it says when the last request failed, until the next success.</summary>
    public string Status => _engine?.Status ?? _status;

    /// <summary>
    /// A settings reload replaced this provider: a request under way is cancelled and none is started, so that recording is heard
    /// on this PC and nothing more goes out on settings the pages no longer show (switched off, consent withdrawn, offline only on).
    /// </summary>
    public void Retire() => _engine?.Retire();

    /// <summary>True once a cloud engine was retired (a settings reload or quit replaced it); false for the local model, which sends nothing.</summary>
    public bool IsRetired => _engine?.IsRetired ?? false;

    /// <summary>
    /// Builds the configured provider. Anything that stops cloud speech (not configured, offline_only, an http:// address off
    /// this PC, no speech consent, translate on, no key) gives the local model, with the reason logged and in <see cref="Status"/>.
    /// </summary>
    /// <param name="decode">The decoding the local model uses (dictionary prompt, language, translate): cloud speech sends the same hint and language.</param>
    /// <param name="http">The offline-guarded provider client (<see cref="OfflineGuardHandler.CreateClient"/>) that clean-up uses; tests pass a fake handler.</param>
    /// <param name="local">The local model (<see cref="ModelManager.GetAsync"/>): every recording when cloud speech cannot run, and the fallback when a request fails.</param>
    public static CloudSpeech Create(SpeechSettings settings, DecodeOptions decode, bool offlineOnly, SecretStore secrets, HttpClient http, ILog log,
        Func<CancellationToken, Task<ITranscriber>> local)
    {
        if (settings.Provider != SpeechSettings.OpenAiCompatible) return Local(local);

        CloudSpeech Refused(string reason, string status, string? missingKey = null)
        {
            log.Warn($"Cloud speech is not used: {reason}; the speech model on this PC hears every dictation.");
            return new CloudSpeech(local, null, status, missingKey);
        }

        if (settings.BaseUrl is null) return Refused("speech.base_url is not set", SpeechStatus.NothingSent("speech.base_url is not set"));
        if (settings.Model is null) return Refused("speech.model is not set", SpeechStatus.NothingSent("speech.model is not set"));
        if (SpeechPolicy.OfflineRefusal(settings, offlineOnly) is { } offline) return Refused(offline, SpeechStatus.OfflineOnly);
        if (SpeechPolicy.InsecureEndpoint(settings) is { } insecure) return Refused(insecure, SpeechStatus.NothingSent(insecure));
        var host = SpeechPolicy.Host(settings)!;
        if (!SpeechPolicy.HasConsent(settings))
            return Refused($"sending recordings to {host} has not been agreed to in the speech consent step (consent.json)", SpeechStatus.NotAgreed);
        if (decode.Translate) return Refused("whisper.translate is on, and cloud speech does not translate", SpeechStatus.TranslateRefused);
        // The server speech.base_url names decides the key: a preset's own only to its own server, SPEECH_API_KEY only to any
        // other address. A service that needs its key gets nothing at all without it.
        var service = SpeechPresets.ForAddress(settings.BaseUrl);
        if (!service.TryGetKey(secrets, out var key))
            return Refused($"{service.KeyVariable} is not set (environment or .env), so nothing is sent to {host}", SpeechStatus.WaitingForKey(service.KeyVariable!), service.KeyVariable);

        log.Info($"Cloud speech on: audio goes to {host} ({settings.Model}); the speech model on this PC is the fallback.");
        var engine = new CloudSpeechEngine(http, settings.BaseUrl, settings.Model, key, service.KeyHeader, decode, TimeSpan.FromMilliseconds(settings.TimeoutMs), local, log);
        return new CloudSpeech(local, engine, "", null);
    }
}
