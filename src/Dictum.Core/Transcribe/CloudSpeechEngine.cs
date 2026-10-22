using System.Net.Http.Headers;
using System.Text.Json;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;

namespace Dictum.Transcribe;

/// <summary>
/// Cloud speech for one settings load (spec "Cloud speech" §2, 22/10/2026): each recording, or each hands-free part, is one
/// multipart POST to {base_url}/audio/transcriptions (the OpenAI shape, which Azure OpenAI and Groq share) with the WAV built
/// in memory, the model, the language hint (none for auto) and the prompt the local decoder gets (the dictionary terms, then
/// a part's carried words). Never silent: an HTTP error, the deadline, a reply that is not JSON or has no text, offline_only
/// at the guard, or a settings reload logs the reason (status and host, never audio or text), sets <see cref="Status"/>, and
/// the speech model on this PC hears the same samples. Only a user cancellation (Esc) propagates.
/// </summary>
public sealed class CloudSpeechEngine : ITranscriber
{
    /// <summary>The fallback reason when a settings reload stopped this engine (<see cref="Retire"/>).</summary>
    internal const string Reloaded = "settings reloaded during the dictation";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _key;
    private readonly string? _keyHeader;
    private readonly DecodeOptions _decode;
    private readonly TimeSpan _timeout;
    private readonly Func<CancellationToken, Task<ITranscriber>> _local;
    private readonly ILog _log;
    private readonly CancellationTokenSource _retired = new();
    private volatile string? _lastFailure;

    /// <param name="key">The key for this service (SpeechPresets decides which), or null to send none.</param>
    /// <param name="keyHeader">The header the key goes in: null for <c>Authorization: Bearer</c>, else that header with the key alone (Azure's <c>api-key</c>).</param>
    /// <param name="timeout">speech.timeout_ms: the deadline for one request.</param>
    /// <param name="local">The speech model on this PC, asked only when a request fails.</param>
    public CloudSpeechEngine(HttpClient http, string baseUrl, string model, string? key, string? keyHeader, DecodeOptions decode, TimeSpan timeout,
        Func<CancellationToken, Task<ITranscriber>> local, ILog log)
    {
        _http = http;
        _endpoint = Endpoint(baseUrl);
        _model = model;
        _key = key;
        _keyHeader = keyHeader;
        _decode = decode;
        _timeout = timeout;
        _local = local;
        _log = log;
    }

    /// <summary>
    /// {base_url}/audio/transcriptions, with the path joined as a path (lens F7, 22/10/2026): a query in speech.base_url (such as
    /// an api-version) stays a query after the new path instead of swallowing it, and a fragment is dropped.
    /// </summary>
    internal static Uri Endpoint(string baseUrl)
    {
        var builder = new UriBuilder(baseUrl.Trim()) { Fragment = "" };
        builder.Path = builder.Path.TrimEnd('/') + "/audio/transcriptions";
        return builder.Uri;
    }

    public string Host => CloudPresets.HostOf(_endpoint);

    /// <summary>The model (or Azure deployment) each request names.</summary>
    public string Model => _model;

    /// <summary>Why the last request was not used, until the next one succeeds; null when the last one succeeded or none was made.</summary>
    public string? LastFailure => _lastFailure;

    public string Status => _lastFailure is { } reason ? SpeechStatus.Failed(reason) : SpeechStatus.On(Host, _model);

    /// <summary>A settings reload replaced this engine: a request under way is cancelled and none is started (the local model hears it).</summary>
    public void Retire() => _retired.Cancel();

    /// <summary>True once <see cref="Retire"/> was called: no request is started any more.</summary>
    public bool IsRetired => _retired.IsCancellationRequested;

    public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken) => TranscribeAsync(samples16kMono, null, cancellationToken);

    public async Task<string> TranscribeAsync(float[] samples16kMono, string? carried, CancellationToken cancellationToken)
    {
        var reason = Reloaded;
        if (!_retired.IsCancellationRequested)
        {
            // The request's token: Esc, a settings reload (Retire), or the deadline below.
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _retired.Token);
            Task<string>? call = null;
            try
            {
                var prompt = carried is null ? _decode.Prompt : _decode.WithCarried(carried).Prompt;
                call = PostAsync(samples16kMono, prompt, request.Token);
                var text = await call.WaitAsync(_timeout, request.Token).ConfigureAwait(false);
                _lastFailure = null;
                return text;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (call is not null) Observe(call);
                throw;
            }
            catch (Exception ex)
            {
                // The deadline, a reload or a failed request: stop the request before the using disposes its source.
                try
                {
                    request.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                if (call is not null) Observe(call);
                reason = _retired.IsCancellationRequested ? Reloaded : ex is TimeoutException ? $"timed out after {_timeout.TotalMilliseconds:0} ms" : Describe(ex);
            }
        }
        _lastFailure = reason;
        // The per-failure log line (spec "Cloud speech" §4): APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword.
        _log.Warn($"Cloud speech via {Host} not used, the local model heard this dictation instead: {reason}.");
        var local = await _local(cancellationToken).ConfigureAwait(false);
        return carried is null
            ? await local.TranscribeAsync(samples16kMono, cancellationToken).ConfigureAwait(false)
            : await local.TranscribeAsync(samples16kMono, carried, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One request: multipart form data with file (16 kHz mono 16-bit WAV), model, language (left out for auto), prompt (left
    /// out when empty) and response_format=json; returns the reply's text, tidied as whisper's own output is
    /// (<see cref="TranscriptText.Normalise"/>).
    /// </summary>
    private async Task<string> PostAsync(float[] samples, string? prompt, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Wav.Encode(samples));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "dictation.wav");
        form.Add(new StringContent(_model), "model");
        if (_decode.Language != WhisperLanguages.Auto) form.Add(new StringContent(_decode.Language), "language");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("json"), "response_format");

        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
        if (!string.IsNullOrEmpty(_key))
        {
            if (_keyHeader is { } header) message.Headers.Add(header, _key);
            else message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        }
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: response bodies can echo request headers on some gateways.
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {Host}", null, response.StatusCode);
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var text = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var normalised = TranscriptText.Normalise([text]);
        return normalised.Length > 0 ? normalised : throw new InvalidDataException("the reply had no text");
    }

    /// <summary>The reason for the log and <see cref="Status"/>: a status code and host, or the kind of failure; never a body, the audio or the text.</summary>
    private string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code} from {Host}",
        JsonException => "the reply was not valid JSON",
        InvalidDataException => ex.Message,
        OfflineOnlyException => "offline_only is on",
        HttpRequestException => $"could not reach {Host} ({ex.GetType().Name})",
        _ => ex.GetType().Name,
    };

    /// <summary>A request left running past its deadline must not surface as an unobserved task exception later.</summary>
    private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
