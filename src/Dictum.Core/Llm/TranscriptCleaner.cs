using System.Net;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Llm;

/// <param name="Text">What to insert: the model's cleaned text, or the deterministic tidy of the raw transcript.</param>
/// <param name="Cleaned">True only when a model reply was accepted.</param>
/// <param name="FallbackReason">Why the model's reply was not used, or null.</param>
/// <param name="Skipped">True when the model was not asked because the dictation was predicted to exceed the deadline.</param>
public sealed record CleanupResult(string Text, bool Cleaned, string? FallbackReason, bool Skipped = false);

/// <summary>What the personal files give every clean-up (SPEC §16), from PersonalText.ForCleanup.</summary>
/// <param name="DictionaryLine">The personal dictionary's "Known names and terms" line (SPEC §16.2), or null.</param>
/// <param name="MustKeepWords">Dictated words a reply may not drop (the profanity list unless profanity.mode = remove).</param>
/// <param name="DictionaryTerms">The terms in that line: a reply may not add one the speaker did not say.</param>
public sealed record PersonalCleanup(string? DictionaryLine, IReadOnlyCollection<string>? MustKeepWords, IReadOnlyCollection<string>? DictionaryTerms)
{
    public static readonly PersonalCleanup None = new(null, null, null);

    /// <summary>
    /// The system prompt (llm.prompt_file or the built-in one, the grammar-assist sentence when on, the dictionary line) and
    /// the checks every reply to it gets; the same for every provider and for --bench-cleanup.
    /// </summary>
    public (string Prompt, ValidationContext Validation) Checks(LlmSettings settings, Action<string> warn)
    {
        var prompt = CleanupPrompt.Build(CleanupPrompt.Load(settings.PromptFile, warn), settings.GrammarAssist, DictionaryLine);
        return (prompt, new ValidationContext(prompt, MustKeepWords, DictionaryTerms));
    }
}

/// <summary>
/// The cleanup step (SPEC §5). With a model (local by default, or an opted-in cloud provider) the reply is
/// validated; every failure (timeout, error, refusal, truncation, rejected reply, model not loaded yet) falls back
/// to the deterministic tidy and logs the reason, never the text. Without a model the deterministic tidy is the
/// whole step; with clean-up off (llm.provider = off) only whitespace is tidied. Only a user cancellation (Esc) propagates. The paste is never blocked on a model failing.
/// </summary>
public sealed class TranscriptCleaner
{
    private readonly ILlmClient? _client;
    private readonly string _prompt;
    private readonly Func<int, TimeSpan> _deadline;
    private readonly ILog _log;
    private readonly ValidationContext _validation;
    private readonly bool _plain;
    private readonly CancellationTokenSource _retired = new();

    /// <summary>The fallback reason for a dictation whose clean-up a settings reload stopped (<see cref="Retire"/>); for the log.</summary>
    internal const string Reloaded = "settings reloaded during the dictation";

    /// <param name="timeout">llm.timeout_ms: the shortest deadline; longer dictations get longer (<see cref="Deadline"/>).</param>
    /// <param name="deadline">Tests replace the deadline rule; production uses <see cref="Deadline"/>.</param>
    /// <param name="validation">
    /// The checks for each reply (<see cref="PersonalCleanup.Checks"/>); without them, the prompt only. Their prompt is always
    /// <paramref name="systemPrompt"/>, so the echo check runs against the prompt actually sent.
    /// </param>
    /// <param name="plain">The text is not English (SPEC §23): no model and no English rules, whitespace tidied only.</param>
    public TranscriptCleaner(ILlmClient? client, string systemPrompt, TimeSpan timeout, ILog log, string status, Func<int, TimeSpan>? deadline = null,
        ValidationContext? validation = null, bool plain = false)
    {
        _client = client;
        _prompt = systemPrompt;
        _deadline = deadline ?? (words => Deadline(words, timeout));
        _log = log;
        Status = status;
        _validation = (validation ?? new ValidationContext(systemPrompt)) with { SystemPrompt = systemPrompt };
        _plain = plain;
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
    /// <param name="missingKey">The key variable whose absence stopped the provider (<see cref="MissingKey"/>), or null.</param>
    public static TranscriptCleaner RulesOnly(string status, string? missingKey = null) =>
        new(null, CleanupPrompt.Default, TimeSpan.Zero, NullLog.Instance, status) { MissingKey = missingKey };

    /// <summary>
    /// Whitespace only (SPEC §23): whisper is writing a language other than English, and the model prompt and the
    /// rule-based tidy are written for English (the tidy would, for example, capitalise Polish "i").
    /// </summary>
    public static TranscriptCleaner Plain(string status) => new(null, CleanupPrompt.Default, TimeSpan.Zero, NullLog.Instance, status, plain: true);

    /// <summary>
    /// No clean-up (llm.provider = off, 1.2): the transcript is pasted as whisper wrote it, with whitespace tidied only, the same
    /// step as <see cref="Plain"/>. No model is built or loaded, nothing is validated and nothing is sent; the rule-based tidy's
    /// fillers, capitals, full stop and spoken commands do not run, so a spoken "new paragraph" is typed as those words.
    /// </summary>
    public static TranscriptCleaner Off() => new(null, CleanupPrompt.Default, TimeSpan.Zero, NullLog.Instance, OffStatus, plain: true);

    /// <summary>
    /// The off cleaner's <see cref="Status"/>, shown on Settings > Clean-up and About. PROPOSED copy (21/10/2026). It starts "no clean-up",
    /// not "off", as the tray name does (lens on 787b300): "Clean-up: off" read as Dictum being off.
    /// </summary>
    public const string OffStatus = "no clean-up: whitespace tidied only; your corrections, snippets and the profanity filter still apply (llm.provider = off)";

    /// <summary>
    /// True when only whitespace is tidied: the text is not English (<see cref="Plain"/>), or clean-up is off (<see cref="Off"/>).
    /// Either way no letter changes case, here or at a hands-free part's boundary (SegmentJoin's keepCase).
    /// </summary>
    public bool IsPlain => _plain;

    /// <summary>True when a model takes part (the pill shows "Polishing…").</summary>
    public bool UsesModel => _client is not null;

    /// <summary>
    /// True when the clean-up sends each dictation's text to another program (a cloud service, or a server on this PC); false
    /// for the local model and the rule-based tidy. A settings reload retires only such a cleaner (<see cref="Retire"/>).
    /// </summary>
    public bool SendsText => _client is not (null or LocalLlmClient);

    /// <summary>
    /// The key variable (OPENAI_API_KEY, GEMINI_API_KEY, ANTHROPIC_API_KEY) whose absence stopped the configured service when this cleaner was
    /// built, or null. Settings > Clean-up and Privacy read it from the cleaner in use, never from the .env files, so they
    /// say "waiting" exactly while that is why nothing is sent, until a reload builds the next cleaner.
    /// </summary>
    public string? MissingKey { get; private init; }

    /// <summary>Human-readable provider state for the self-test and About box (never contains a key).</summary>
    public string Status { get; }

    /// <summary>The system prompt sent with every cleanup (the local model keeps it evaluated in its cache).</summary>
    public string SystemPrompt => _prompt;

    /// <summary>The checks every reply gets: the prompt, the words it may not drop and the dictionary terms it may not add.</summary>
    internal ValidationContext Validation => _validation;

    /// <summary>
    /// A settings reload replaced this cleaner: a request it still has under way is cancelled and it starts no other, so that
    /// dictation gets the rule-based tidy and nothing more goes out on settings the pages no longer show (switched off,
    /// consent withdrawn, offline only on). The provider's token carries it, so an Anthropic retry is not made either.
    /// </summary>
    public void Retire() => _retired.Cancel();

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

    /// <param name="carried">
    /// A hands-free part's context (SPEC §3 amendment, 16/10/2026): the closing words of what the part before it said, before
    /// corrections, the profanity filter and snippets. The provider gets them beside the part, never as its dictation, and the
    /// reply is checked against the part's own words with them as prompt text (a reply that repeats them is rejected). Null for
    /// a whole recording.
    /// </param>
    public async Task<CleanupResult> CleanAsync(string raw, CancellationToken cancellationToken, string? carried = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CleanupResult("", false, null);
        if (_plain) return new CleanupResult(DeterministicCleaner.WhitespaceOnly(raw), false, null);
        if (_client is null) return new CleanupResult(DeterministicCleaner.Clean(raw), false, null);
        if (_retired.IsCancellationRequested) return Fallback(raw, Reloaded);
        if (SkipReason(raw) is { } skip)
        {
            _log.Info($"Cleanup via {_client.Name} {skip}; the rule-based tidy is used.");
            return new CleanupResult(DeterministicCleaner.Clean(raw), false, skip, Skipped: true);
        }

        var deadline = _deadline(WordCount(raw));
        // The provider's token: Esc, a settings reload (Retire), or the deadline below.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _retired.Token);
        Task<LlmReply>? call = null;
        try
        {
            call = carried is null ? _client.CompleteAsync(_prompt, raw, timeout.Token) : _client.CompleteAsync(_prompt, raw, carried, timeout.Token);
            // This stops waiting at the deadline, or at a reload, even if the provider does not stop (llama.cpp cannot interrupt
            // a prompt half-way); the timeout path then cancels the provider's token itself.
            var reply = await call.WaitAsync(deadline, timeout.Token).ConfigureAwait(false);
            if (reply.IsRefusal) return Fallback(raw, $"provider refused (stop_reason {reply.StopReason})");
            if (reply.IsTruncated) return Fallback(raw, $"reply truncated (stop_reason {reply.StopReason})");
            var validation = carried is null ? _validation : _validation with { SystemPrompt = CleanupPrompt.WithCarried(_prompt, carried), Carried = carried };
            var (text, rejection) = CleanupValidator.Validate(raw, reply.Text, validation);
            return text is null ? Fallback(raw, rejection!) : new CleanupResult(text, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (call is not null) Observe(call);
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The deadline passed (or a reload retired this cleaner): tell the provider to stop, before the using disposes the source. This is the only place the
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
            return Fallback(raw, _retired.IsCancellationRequested ? Reloaded : $"timed out after {deadline.TotalMilliseconds:0} ms");
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
    /// Why offline_only forbids this provider, or null if it may run. Local, none and off never touch the network; an
    /// OpenAI-compatible server on this machine (loopback) is allowed; anything else is a network call.
    /// </summary>
    public static string? OfflineRefusal(LlmSettings settings, bool offlineOnly)
    {
        if (!offlineOnly) return null;
        return settings.Provider switch
        {
            LlmSettings.Anthropic => "offline_only is on: the anthropic provider is refused (it would send transcripts to a cloud API)",
            LlmSettings.OpenAiCompatible => CloudPolicy.OfflineRefusal("the openai-compatible provider", settings.BaseUrl, offlineOnly),
            _ => null,
        };
    }

    /// <summary>Builds the configured provider. A provider that cannot run becomes rules-only, with the reason.</summary>
    /// <param name="local">The loaded local model, if any (null until it has loaded).</param>
    /// <param name="personal">The dictionary line, kept words and dictionary terms (PersonalText.ForCleanup), or none.</param>
    /// <param name="englishText">False when whisper writes a language other than English (<see cref="Transcribe.WhisperLanguages.OutputIsEnglish"/>): whitespace tidied only.</param>
    public static TranscriptCleaner Create(LlmSettings settings, bool offlineOnly, SecretStore secrets, HttpClient http, ILog log, Func<ILocalLlmEngine?>? local = null,
        PersonalCleanup? personal = null, bool englishText = true)
    {
        // Off first: no prompt file is read, no reply checks are built, and no client is made, so nothing can be sent.
        if (settings.Provider == LlmSettings.Off)
        {
            log.Info("Clean-up is off (llm.provider = off): the text is pasted as whisper wrote it, with whitespace tidied only.");
            return Off();
        }
        var (prompt, validation) = (personal ?? PersonalCleanup.None).Checks(settings, log.Warn);
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        if (!englishText)
        {
            log.Info("Clean-up skipped: whisper writes the language spoken, not English, and the clean-up is written for English; whitespace is tidied only.");
            return Plain("skipped: the text is not English (whisper.language is not en and translate is off); whitespace tidied only");
        }
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
                return new TranscriptCleaner(client, prompt, timeout, log, $"local/{name} on this PC (nothing leaves it)", validation: validation);

            case LlmSettings.OpenAiCompatible:
                if (settings.BaseUrl is null) return RulesOnly("rule-based tidy only: llm.base_url is required for openai-compatible");
                // The server base_url names decides the key: a preset's own only to its own server, LLM_API_KEY only to a custom
                // address, none to Ollama on this PC. A service that needs its key gets nothing at all without it.
                var service = CloudPresets.ForAddress(settings.BaseUrl);
                if (!service.TryGetKey(secrets, out var key))
                {
                    log.Warn($"{service.KeyVariable} is not set (environment or .env), so nothing is sent to {new Uri(settings.BaseUrl).Host}; the rule-based tidy is used instead.");
                    return RulesOnly($"rule-based tidy only: {service.KeyVariable} is not set (environment or .env)", missingKey: service.KeyVariable);
                }
                var oai = new OpenAiCompatibleLlmClient(http, settings.BaseUrl, settings.Model, key);
                var keyState = service.KeyVariable is null ? "no key sent" : $"{service.KeyVariable} {(key is null ? "absent" : "present")}";
                return new TranscriptCleaner(oai, prompt, timeout, log, $"{oai.Name} ({keyState})", validation: validation);

            default:
                if (AnthropicRedirectWarning(settings) is { } redirect) log.Warn(redirect);
                var apiKey = secrets.Get(SecretStore.AnthropicKey);
                if (apiKey is null) return RulesOnly("rule-based tidy only: ANTHROPIC_API_KEY is not set (environment or .env)", missingKey: SecretStore.AnthropicKey);
                // llm.model defaults to a local cleanup model; that is not an Anthropic model id.
                var model = CleanupCatalog.IsValidSetting(settings.Model) ? LlmSettings.AnthropicDefaultModel : settings.Model;
                var anthropic = new AnthropicLlmClient(apiKey, model, timeout, settings.BaseUrl, http); // the shared, offline-guarded client
                return new TranscriptCleaner(anthropic, prompt, timeout, log, $"{anthropic.Name} (ANTHROPIC_API_KEY present)", validation: validation);
        }
    }
}
