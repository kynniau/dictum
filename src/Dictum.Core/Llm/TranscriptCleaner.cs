using System.Net;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Llm;

/// <param name="Text">What to insert: the model's cleaned text, or the deterministic tidy of the raw transcript.</param>
/// <param name="Cleaned">True only when a model reply was accepted.</param>
/// <param name="FallbackReason">Why the model's reply was not used, or null.</param>
/// <param name="Skipped">True when the model was not asked because the dictation was predicted to exceed the deadline.</param>
public sealed record CleanupResult(string Text, bool Cleaned, string? FallbackReason, bool Skipped = false);

/// <summary>
/// The cleanup step (SPEC §5). With a model (local by default, or an opted-in cloud provider) the reply is
/// validated; every failure (timeout, error, refusal, truncation, rejected reply, model not loaded yet) falls back
/// to the deterministic tidy and logs the reason, never the text. Without a model the deterministic tidy is the
/// whole step. Only a user cancellation (Esc) propagates. The paste is never blocked on a model failing.
/// </summary>
public sealed class TranscriptCleaner
{
    private readonly ILlmClient? _client;
    private readonly string _prompt;
    private readonly Func<int, TimeSpan> _deadline;
    private readonly ILog _log;
    private readonly ValidationContext _validation;

    /// <param name="timeout">llm.timeout_ms: the shortest deadline; longer dictations get longer (<see cref="Deadline"/>).</param>
    /// <param name="deadline">Tests replace the deadline rule; production uses <see cref="Deadline"/>.</param>
    /// <param name="mustKeepWords">Dictated words a reply may not drop: the profanity list unless profanity.mode = remove.</param>
    public TranscriptCleaner(ILlmClient? client, string systemPrompt, TimeSpan timeout, ILog log, string status, Func<int, TimeSpan>? deadline = null,
        IReadOnlyCollection<string>? mustKeepWords = null)
    {
        _client = client;
        _prompt = systemPrompt;
        _deadline = deadline ?? (words => Deadline(words, timeout));
        _log = log;
        Status = status;
        _validation = new ValidationContext(systemPrompt, mustKeepWords);
    }

    /// <summary>
    /// The hard deadline for one cleanup: 10 s plus 0.1 s per dictated word, at least llm.timeout_ms (15 s by default)
    /// and at most 60 s (or llm.timeout_ms if that is longer).
    /// </summary>
    public static TimeSpan Deadline(int words, TimeSpan minimum)
    {
        var scaled = TimeSpan.FromSeconds(10 + 0.1 * words);
        var max = minimum > TimeSpan.FromSeconds(60) ? minimum : TimeSpan.FromSeconds(60);
        return scaled < minimum ? minimum : scaled > max ? max : scaled;
    }

    /// <summary>The deterministic tidy only (provider none, or a provider that cannot run).</summary>
    public static TranscriptCleaner RulesOnly(string status) => new(null, CleanupPrompt.Default, TimeSpan.Zero, NullLog.Instance, status);

    /// <summary>True when a model takes part (the pill shows "Polishing…").</summary>
    public bool UsesModel => _client is not null;

    /// <summary>Human-readable provider state for the self-test and About box (never contains a key).</summary>
    public string Status { get; }

    /// <summary>The system prompt sent with every cleanup (the local model keeps it evaluated in its cache).</summary>
    public string SystemPrompt => _prompt;

    private static int WordCount(string raw) => raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Why the model would be skipped for this dictation, or null: the provider's own estimate (the local model's
    /// measured speed) is longer than the deadline, so waiting would only end in a timeout.
    /// </summary>
    public string? SkipReason(string raw)
    {
        if (_client?.Predict(raw) is not { } predicted) return null;
        var words = WordCount(raw);
        var deadline = _deadline(words);
        return predicted > deadline
            ? $"skipped: estimated {predicted.TotalSeconds:0.0} s for {words} words at the measured speed, over the {deadline.TotalSeconds:0} s limit"
            : null;
    }

    public async Task<CleanupResult> CleanAsync(string raw, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CleanupResult("", false, null);
        if (_client is null) return new CleanupResult(DeterministicCleaner.Clean(raw), false, null);
        if (SkipReason(raw) is { } skip)
        {
            _log.Info($"Cleanup via {_client.Name} {skip}; the rule-based tidy is used.");
            return new CleanupResult(DeterministicCleaner.Clean(raw), false, skip, Skipped: true);
        }

        var deadline = _deadline(WordCount(raw));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); // the provider's token: Esc, or the deadline below
        Task<LlmReply>? call = null;
        try
        {
            call = _client.CompleteAsync(_prompt, raw, timeout.Token);
            // This stops waiting at the deadline even if the provider does not stop (llama.cpp cannot interrupt a prompt
            // half-way); the timeout path then cancels the provider's token itself.
            var reply = await call.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
            if (reply.IsRefusal) return Fallback(raw, $"provider refused (stop_reason {reply.StopReason})");
            if (reply.IsTruncated) return Fallback(raw, $"reply truncated (stop_reason {reply.StopReason})");
            var (text, rejection) = CleanupValidator.Validate(raw, reply.Text, _validation);
            return text is null ? Fallback(raw, rejection!) : new CleanupResult(text, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (call is not null) Observe(call);
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The deadline passed: tell the provider to stop, before the using disposes the source. This is the only place the
            // provider's token is cancelled for a timeout (the source has no timer of its own any more), so a call past its
            // deadline is always cancelled. Lens probe, 15/10/2026: with a CancelAfter timer racing the dispose, the provider
            // ran on in 21 of 400 timeouts, 396 with busy cores, holding the local model's lock and stalling the next dictation.
            try
            {
                timeout.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            if (call is not null) Observe(call);
            return Fallback(raw, $"timed out after {deadline.TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            return Fallback(raw, Describe(ex));
        }
    }

    /// <summary>A call left running past its deadline must not surface as an unobserved task exception later.</summary>
    private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private CleanupResult Fallback(string raw, string reason)
    {
        _log.Warn($"Cleanup via {_client?.Name} not used, applying the rule-based tidy instead: {reason}.");
        return new CleanupResult(DeterministicCleaner.Clean(raw), false, reason);
    }

    private static string Describe(Exception ex)
    {
        var status = ex is HttpRequestException { StatusCode: HttpStatusCode code } ? $" HTTP {(int)code}" : "";
        var message = ex.Message.Length > 200 ? ex.Message[..200] + "..." : ex.Message;
        return $"{ex.GetType().Name}{status}: {message}";
    }

    /// <summary>
    /// llm.base_url is shared by the providers, so a gateway URL left over from openai-compatible would receive the
    /// Anthropic key and transcripts after a switch to anthropic. Returns the warning to log and show, or null.
    /// </summary>
    public static string? AnthropicRedirectWarning(LlmSettings settings) =>
        settings.Provider == LlmSettings.Anthropic && settings.BaseUrl is { } url
            ? $"Anthropic requests will go to {new Uri(url).Host} (llm.base_url), not api.anthropic.com: the API key and transcripts are sent there. Clear llm.base_url if that is not intended."
            : null;

    /// <summary>
    /// Why offline_only forbids this provider, or null if it may run. Local and none never touch the network; an
    /// OpenAI-compatible server on this machine (loopback) is allowed; anything else is a network call.
    /// </summary>
    public static string? OfflineRefusal(LlmSettings settings, bool offlineOnly)
    {
        if (!offlineOnly) return null;
        return settings.Provider switch
        {
            LlmSettings.Anthropic => "offline_only is on: the anthropic provider is refused (it would send transcripts to a cloud API)",
            LlmSettings.OpenAiCompatible when settings.BaseUrl is { } u && !new Uri(u).IsLoopback =>
                $"offline_only is on: the openai-compatible provider at {new Uri(u).Host} is refused (only a server on this machine is allowed)",
            _ => null,
        };
    }

    /// <summary>Builds the configured provider. A provider that cannot run becomes rules-only, with the reason.</summary>
    /// <param name="local">The loaded local model, if any (null until it has loaded).</param>
    /// <param name="dictionaryLine">The personal dictionary's "Known names and terms" line (SPEC §16.2), or null.</param>
    /// <param name="mustKeepWords">Dictated words a reply may not drop (the profanity list unless profanity.mode = remove).</param>
    public static TranscriptCleaner Create(LlmSettings settings, bool offlineOnly, SecretStore secrets, HttpClient http, ILog log, Func<ILocalLlmEngine?>? local = null,
        string? dictionaryLine = null, IReadOnlyCollection<string>? mustKeepWords = null)
    {
        var prompt = CleanupPrompt.Build(CleanupPrompt.Load(settings.PromptFile, log.Warn), settings.GrammarAssist, dictionaryLine);
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        if (OfflineRefusal(settings, offlineOnly) is { } refusal)
        {
            log.Warn(refusal + "; the rule-based tidy is used instead.");
            return RulesOnly(refusal + "; rule-based tidy only");
        }
        if (CloudPolicy.InsecureEndpoint(settings) is { } insecure)
        {
            log.Warn(insecure + "; the rule-based tidy is used instead.");
            return RulesOnly(insecure + "; rule-based tidy only");
        }
        if (!CloudPolicy.HasConsent(settings))
        {
            // Normally repaired at load (EffectiveSettings); this is the backstop for settings built any other way.
            log.Warn($"Cloud clean-up via {settings.Provider} was not agreed to in Settings > Clean-up; the rule-based tidy is used instead.");
            return RulesOnly($"{settings.Provider} not agreed to in Settings > Clean-up; rule-based tidy only");
        }
        switch (settings.Provider)
        {
            case LlmSettings.NoProvider:
                return RulesOnly("rule-based tidy only (llm.provider = none)");

            case LlmSettings.Local:
                var name = CleanupCatalog.DisplayName(settings.Model);
                var client = new LocalLlmClient(local ?? (() => null), name);
                return new TranscriptCleaner(client, prompt, timeout, log, $"local/{name} on this PC (nothing leaves it)", mustKeepWords: mustKeepWords);

            case LlmSettings.OpenAiCompatible:
                if (settings.BaseUrl is null) return RulesOnly("rule-based tidy only: llm.base_url is required for openai-compatible");
                // The server base_url names decides the key: a preset's own only to its own server, LLM_API_KEY only to a custom
                // address, none to Ollama on this PC. A service that needs its key gets nothing at all without it.
                var service = CloudPresets.ForAddress(settings.BaseUrl);
                var key = service.KeyVariable is { } variable ? secrets.Get(variable) : null;
                if (key is null && service.KeyRequired)
                {
                    log.Warn($"{service.KeyVariable} is not set (environment or .env), so nothing is sent to {new Uri(settings.BaseUrl).Host}; the rule-based tidy is used instead.");
                    return RulesOnly($"rule-based tidy only: {service.KeyVariable} is not set (environment or .env)");
                }
                var oai = new OpenAiCompatibleLlmClient(http, settings.BaseUrl, settings.Model, key);
                var keyState = service.KeyVariable is null ? "no key sent" : $"{service.KeyVariable} {(key is null ? "absent" : "present")}";
                return new TranscriptCleaner(oai, prompt, timeout, log, $"{oai.Name} ({keyState})", mustKeepWords: mustKeepWords);

            default:
                if (AnthropicRedirectWarning(settings) is { } redirect) log.Warn(redirect);
                var apiKey = secrets.Get(SecretStore.AnthropicKey);
                if (apiKey is null) return RulesOnly("rule-based tidy only: ANTHROPIC_API_KEY is not set (environment or .env)");
                // llm.model defaults to a local cleanup model; that is not an Anthropic model id.
                var model = CleanupCatalog.IsValidSetting(settings.Model) ? LlmSettings.AnthropicDefaultModel : settings.Model;
                var anthropic = new AnthropicLlmClient(apiKey, model, timeout, settings.BaseUrl, http); // the shared, offline-guarded client
                return new TranscriptCleaner(anthropic, prompt, timeout, log, $"{anthropic.Name} (ANTHROPIC_API_KEY present)", mustKeepWords: mustKeepWords);
        }
    }
}
