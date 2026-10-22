using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// Cloud speech, Core lane (spec "Cloud speech" §2, §7, 22/10/2026): nothing is sent without the speech consent, offline_only
/// refuses it, each host gets only its own key, the request has the documented shape, and every failure falls back to the
/// speech model on this PC with a logged reason and a status. No test reaches a network: every HttpClient here wraps a fake handler.
/// </summary>
public sealed class CloudSpeechTests
{
    private const string LocalText = "words the local model heard";

    private static readonly float[] Samples = new float[16000]; // one second; the engine does not gate silence (the pipeline does)

    /// <summary>Every speech key set, each to its own value, so a key sent to the wrong host shows in the Authorization header.</summary>
    private static SecretStore AllKeys() => SecretStore.FromContents(
        "OPENAI_API_KEY=k-openai\nAZURE_OPENAI_API_KEY=k-azure\nGROQ_API_KEY=k-groq\nSPEECH_API_KEY=k-speech\nLLM_API_KEY=k-llm\nGEMINI_API_KEY=k-gemini\nANTHROPIC_API_KEY=k-anthropic\n",
        null, _ => null);

    private static SpeechSettings Cloud(string baseUrl = "https://api.openai.com/v1", bool agreed = true, int timeoutMs = 5000) => new()
    {
        Provider = SpeechSettings.OpenAiCompatible,
        BaseUrl = baseUrl,
        Model = "speech-model",
        TimeoutMs = timeoutMs,
        Consent = agreed ? new Uri(baseUrl).Host : null,
    };

    private static DecodeOptions Decode(string? prompt = "Dictum, Kynni", string language = "en", bool translate = false) => new(prompt, 0, language, translate);

    private static (CloudSpeech Speech, FakeTranscriber Local, RecordingLog Log) Build(SpeechSettings settings, HttpMessageHandler handler, DecodeOptions? decode = null,
        bool offlineOnly = false, SecretStore? secrets = null)
    {
        var local = new FakeTranscriber(LocalText);
        var log = new RecordingLog();
        var speech = CloudSpeech.Create(settings, decode ?? Decode(), offlineOnly, secrets ?? AllKeys(), new HttpClient(handler), log, _ => Task.FromResult<ITranscriber>(local));
        return (speech, local, log);
    }

    private static async Task<string> Transcribe(CloudSpeech speech, string? carried = null)
    {
        var engine = await speech.GetAsync(TestContext.Current.CancellationToken);
        return carried is null
            ? await engine.TranscribeAsync(Samples, TestContext.Current.CancellationToken)
            : await engine.TranscribeAsync(Samples, carried, TestContext.Current.CancellationToken);
    }

    /// <summary>Pin 1 (mutation-checked): without the speech consent nothing is sent, the local model hears it, and the status names the consent step.</summary>
    [Fact]
    public async Task No_request_is_made_without_the_speech_consent()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        var (speech, local, log) = Build(Cloud(agreed: false), handler);
        Assert.False(speech.SendsAudio);
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(1, local.Calls);
        Assert.Equal(SpeechStatus.NotAgreed, speech.Status);
        Assert.Contains("speech consent step", log.All);

        // Consent to another host is not consent to this one.
        var other = new CountingHandler(HttpStatusCode.OK);
        var (elsewhere, _, _) = Build(Cloud() with { Consent = "api.groq.com" }, other);
        await Transcribe(elsewhere);
        Assert.Equal(0, other.Requests);
    }

    /// <summary>Pin 2 (mutation-checked): offline_only refuses a host off this PC; a server on this PC is still allowed.</summary>
    [Fact]
    public async Task Offline_only_refuses_cloud_speech_off_this_PC()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        var (speech, local, log) = Build(Cloud(), handler, offlineOnly: true);
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(1, local.Calls);
        Assert.Equal(CloudPolicy.OfflineOnlyLine, speech.Status);
        Assert.Contains("offline_only is on: cloud speech at api.openai.com is refused (only a server on this machine is allowed)", log.All);

        var loopback = new SpeechRecorder();
        var (onThisPc, _, _) = Build(Cloud("http://localhost:8000/v1"), loopback, offlineOnly: true);
        Assert.Equal("Hello from the cloud.", await Transcribe(onThisPc));
        Assert.Single(loopback.Requests);
    }

    /// <summary>Spec test 3: a plain http:// address off this PC is refused (the loader refuses it too; this is the backstop); http://localhost is allowed.</summary>
    [Fact]
    public async Task Plain_http_is_refused_off_this_PC()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        var (speech, _, _) = Build(Cloud("http://speech.example.com/v1"), handler);
        await Transcribe(speech);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(SpeechStatus.NothingSent("speech.base_url speech.example.com uses http://, which would send dictations unencrypted; only a server on this PC may use http://"), speech.Status);
        Assert.Contains("speech.base_url", SettingsLoader.Parse("""{ "speech": { "base_url": "http://speech.example.com/v1" } }""").Warnings.Single());
    }

    /// <summary>Pin (mutation-checked): each host gets its own key and no other: OpenAI, any Azure OpenAI resource, Groq, and SPEECH_API_KEY for any other address.</summary>
    [Theory]
    [InlineData("https://api.openai.com/v1", "Bearer k-openai")]
    [InlineData("https://my-resource.openai.azure.com/openai/v1/", "Bearer k-azure")]
    [InlineData("https://api.groq.com/openai/v1", "Bearer k-groq")]
    [InlineData("https://speech.example.net/v1", "Bearer k-speech")]
    [InlineData("https://openai.azure.com.example.net/v1", "Bearer k-speech")] // not an Azure host: only a host ending .openai.azure.com is
    [InlineData("https://evil-openai.azure.com/v1", "Bearer k-speech")]
    public async Task Each_host_gets_only_its_own_key(string baseUrl, string authorization)
    {
        var handler = new SpeechRecorder();
        var (speech, _, _) = Build(Cloud(baseUrl), handler);
        await Transcribe(speech);
        Assert.Equal(authorization, Assert.Single(handler.Requests).Authorization);
    }

    /// <summary>A preset that needs its key sends nothing without it, whatever other keys are set; the status says which key it waits for.</summary>
    [Theory]
    [InlineData("https://api.openai.com/v1", "OPENAI_API_KEY")]
    [InlineData("https://my-resource.openai.azure.com/openai/v1/", "AZURE_OPENAI_API_KEY")]
    [InlineData("https://api.groq.com/openai/v1", "GROQ_API_KEY")]
    public async Task A_service_that_needs_its_key_gets_nothing_without_it(string baseUrl, string variable)
    {
        var others = string.Concat(SecretStore.KeyNames.Where(k => k != variable).Select(k => $"{k}=k-{k.ToLowerInvariant()}\n"));
        var handler = new CountingHandler(HttpStatusCode.OK);
        var (speech, local, _) = Build(Cloud(baseUrl), handler, secrets: SecretStore.FromContents(others, null, _ => null));
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(1, local.Calls);
        Assert.Equal(variable, speech.MissingKey);
        Assert.Equal(CloudPolicy.WaitingForKeyLine(variable), speech.Status);
    }

    /// <summary>A custom address without SPEECH_API_KEY is sent no key at all (a server on this PC needs none).</summary>
    [Fact]
    public async Task A_custom_address_without_its_key_is_sent_no_key()
    {
        var handler = new SpeechRecorder();
        var (speech, _, _) = Build(Cloud("https://speech.example.net/v1"), handler, secrets: SecretStore.FromContents("OPENAI_API_KEY=k-openai\n", null, _ => null));
        await Transcribe(speech);
        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    /// <summary>Pin (mutation-checked): one multipart POST to {base_url}/audio/transcriptions with file, model, language, prompt and response_format.</summary>
    [Fact]
    public async Task The_request_has_the_documented_shape()
    {
        var handler = new SpeechRecorder();
        var (speech, local, _) = Build(Cloud("https://api.openai.com/v1/"), handler);
        Assert.True(speech.SendsAudio);
        Assert.Equal("api.openai.com", speech.Host);
        Assert.Equal("Hello from the cloud.", await Transcribe(speech));
        Assert.Equal(0, local.Calls);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.Uri.ToString());
        Assert.Equal(["file", "model", "language", "prompt", "response_format"], request.Fields.Keys);
        Assert.Equal("dictation.wav", request.FileName);
        Assert.Equal("audio/wav", request.FileType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(request.Fields["file"], 0, 4)); // a WAV header, 16 kHz mono 16-bit (Wav.Encode)
        Assert.Equal(44 + Samples.Length * 2, request.Fields["file"].Length);
        Assert.Equal("speech-model", request.Text("model"));
        Assert.Equal("en", request.Text("language"));
        Assert.Equal("Dictum, Kynni", request.Text("prompt")); // the dictionary terms, as the local decoder gets them
        Assert.Equal("json", request.Text("response_format"));
    }

    /// <summary>Pin (mutation-checked): auto sends no language; no dictionary terms sends no prompt.</summary>
    [Fact]
    public async Task Auto_sends_no_language_and_no_terms_send_no_prompt()
    {
        var handler = new SpeechRecorder();
        var (speech, _, _) = Build(Cloud(), handler, Decode(prompt: null, language: WhisperLanguages.Auto));
        await Transcribe(speech);
        Assert.Equal(["file", "model", "response_format"], Assert.Single(handler.Requests).Fields.Keys);

        var other = new SpeechRecorder();
        var (spanish, _, _) = Build(Cloud(), other, Decode(language: "es"));
        await Transcribe(spanish);
        Assert.Equal("es", Assert.Single(other.Requests).Text("language"));
    }

    /// <summary>Spec test 5: three hands-free parts are three requests in order; the second and third prompts carry the words before them.</summary>
    [Fact]
    public async Task Hands_free_parts_are_one_request_each_with_the_carried_words()
    {
        var handler = new SpeechRecorder();
        var (speech, _, _) = Build(Cloud(), handler);
        await Transcribe(speech);
        await Transcribe(speech, carried: "the first part ended here");
        await Transcribe(speech, carried: "and the second one here");
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Dictum, Kynni", handler.Requests[0].Text("prompt"));
        Assert.Equal("Dictum, Kynni. the first part ended here", handler.Requests[1].Text("prompt")); // DecodeOptions.WithCarried, as whisper gets it
        Assert.Equal("Dictum, Kynni. and the second one here", handler.Requests[2].Text("prompt"));
    }

    /// <summary>Pin (mutation-checked): 401 and 500 fall back to the local model, which hears the same samples; the log and status name the reason, never the text or key.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401 from api.openai.com")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500 from api.openai.com")]
    public async Task An_HTTP_error_falls_back_to_the_local_model(HttpStatusCode status, string reason)
    {
        var handler = new CountingHandler(status);
        var (speech, local, log) = Build(Cloud(), handler);
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, local.Calls);
        Assert.Equal(SpeechStatus.Failed(reason), speech.Status);
        Assert.Contains($"Cloud speech via api.openai.com not used, the local model heard this dictation instead: {reason}.", log.All);
        Assert.DoesNotContain(LocalText, log.All);
        Assert.DoesNotContain("k-openai", log.All);
        Assert.DoesNotContain("Kynni", log.All); // the dictionary hint is not logged either
    }

    /// <summary>The deadline: a request that never answers is cancelled at speech.timeout_ms and the local model hears the dictation.</summary>
    [Fact]
    public async Task A_timeout_falls_back_to_the_local_model()
    {
        var handler = new Hanging();
        var (speech, local, log) = Build(Cloud(timeoutMs: 100), handler);
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(1, local.Calls);
        Assert.Equal(SpeechStatus.Failed("timed out after 100 ms"), speech.Status);
        Assert.Contains("timed out after 100 ms", log.All);
        Assert.True(await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)); // the request itself was stopped
    }

    /// <summary>An empty or unreadable reply is a failure too, never an empty paste; the next good reply clears the failed status.</summary>
    [Theory]
    [InlineData("""{"text":"   "}""", "the reply had no text")]
    [InlineData("""{"error":"x"}""", "the reply had no text")]
    [InlineData("not json", "the reply was not valid JSON")]
    public async Task An_empty_or_unreadable_reply_falls_back_and_a_good_one_clears_the_status(string body, string reason)
    {
        var handler = new SpeechRecorder(n => n == 1 ? body : """{"text":"Hello from the cloud."}""");
        var (speech, local, _) = Build(Cloud(), handler);
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(SpeechStatus.Failed(reason), speech.Status);
        Assert.Equal("Hello from the cloud.", await Transcribe(speech));
        Assert.Equal(SpeechStatus.On("api.openai.com", "speech-model"), speech.Status);
        Assert.Equal(1, local.Calls);
    }

    /// <summary>Esc during a request propagates (nothing is pasted) and does not fall back to the local model.</summary>
    [Fact]
    public async Task A_user_cancellation_propagates()
    {
        var handler = new Hanging();
        var (speech, local, _) = Build(Cloud(), handler);
        using var esc = new CancellationTokenSource();
        var engine = await speech.GetAsync(esc.Token);
        var call = engine.TranscribeAsync(Samples, esc.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await esc.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, local.Calls);
    }

    /// <summary>A settings reload retires the old provider: its request under way is cancelled and the local model hears that recording.</summary>
    [Fact]
    public async Task A_reload_cancels_the_request_under_way_and_the_local_model_hears_it()
    {
        var handler = new Hanging();
        var (speech, local, _) = Build(Cloud(), handler);
        var call = Transcribe(speech);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        speech.Retire();
        Assert.Equal(LocalText, await call);
        Assert.Equal(1, local.Calls);
        Assert.Equal(SpeechStatus.Failed("settings reloaded during the dictation"), speech.Status);
        await Transcribe(speech); // and starts no other request
        Assert.Equal(1, handler.Count);
    }

    /// <summary>Pin (mutation-checked): v1 refuses cloud speech with translate on (the transcriptions endpoint does not translate) and says why.</summary>
    [Fact]
    public async Task Translate_is_refused_and_the_local_model_translates()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        var (speech, local, log) = Build(Cloud(), handler, Decode(language: "es", translate: true));
        Assert.Equal(LocalText, await Transcribe(speech));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(1, local.Calls);
        Assert.Equal(SpeechStatus.TranslateRefused, speech.Status);
        Assert.Contains("whisper.translate is on, and cloud speech does not translate", log.All);
    }

    /// <summary>local (the default) and an incomplete openai-compatible setup build no engine and send nothing.</summary>
    [Fact]
    public async Task Local_and_an_incomplete_setup_send_nothing()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        foreach (var (settings, status) in new[]
                 {
                     (new SpeechSettings(), SpeechStatus.Local),
                     (Cloud() with { BaseUrl = null }, SpeechStatus.NothingSent("speech.base_url is not set")),
                     (Cloud() with { Model = null }, SpeechStatus.NothingSent("speech.model is not set")),
                 })
        {
            var (speech, _, _) = Build(settings, handler);
            Assert.False(speech.SendsAudio);
            Assert.Equal(status, speech.Status);
            Assert.Equal(LocalText, await Transcribe(speech));
        }
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>Pin (mutation-checked): the status lines word for word, PROPOSED copy for the UI lane to show (22/10/2026).</summary>
    [Fact]
    public void The_status_lines_are_the_proposed_copy()
    {
        Assert.Equal("on this PC: your speech model hears every dictation (nothing leaves it)", SpeechStatus.Local);
        Assert.Equal("On: your voice goes to api.openai.com (model m) to be turned into text. Advanced option: your audio leaves this PC. Dictum does not validate or certify how that service handles it.",
            SpeechStatus.On("api.openai.com", "m"));
        Assert.Equal("On, but the last request failed (HTTP 401 from api.openai.com); the local model heard this dictation", SpeechStatus.Failed("HTTP 401 from api.openai.com"));
        Assert.Equal("On, but not agreed to yet: nothing is sent until you agree to it in Settings > Models; your speech model on this PC hears every dictation.", SpeechStatus.NotAgreed);
        Assert.Equal("On, but translate is on and cloud speech does not translate: nothing is sent; your speech model on this PC hears every dictation.", SpeechStatus.TranslateRefused);
        Assert.Equal("On, but nothing is sent (x): your speech model on this PC hears every dictation.", SpeechStatus.NothingSent("x"));
        Assert.Equal("On, but waiting for your GROQ_API_KEY: nothing is sent until you add it to .env.", SpeechStatus.WaitingForKey("GROQ_API_KEY"));
        Assert.Equal("On, but offline only is on: nothing is sent until you switch offline only off.", SpeechStatus.OfflineOnly);
        Assert.Equal("Heard on this PC (cloud speech failed)", SpeechStatus.FallbackPill);
    }

    /// <summary>
    /// Pin (mutation-checked): the speech consent text and buttons, APPROVED by the product owner 22/10/2026 (10:50 AEDT) as the
    /// spec's §3 text verbatim, byte for byte, with the host and the one key that goes there filled in. Never reword it.
    /// </summary>
    [Fact]
    public void The_consent_text_is_the_approved_text_byte_for_byte()
    {
        var text = SpeechPolicy.ConsentText("api.groq.com", SpeechPresets.ForAddress("https://api.groq.com/openai/v1").KeyVariable!);
        Assert.Equal(
            "With cloud speech on, the recording of every dictation, your voice, is sent over the internet to api.groq.com to be turned into text, together with the words in your personal dictionary as a hint. It uses your own API key (GROQ_API_KEY).\n\n" +
            "Dictum encrypts the connection (HTTPS) but cannot see, check or certify what api.groq.com keeps, for how long, how it protects it, or who else can listen to it. By switching cloud speech on you accept responsibility for the security and privacy of the speech sent there. Check that service's terms yourself, and do not dictate confidential, personal or client information with cloud speech on unless you have.\n\n" +
            "Nothing is sent until you agree. You can switch it off at any time in Settings > Models, and offline only mode refuses it; your speech model on this PC is used again.",
            text);
        // The template itself, as the appendix writes it with its two placeholders, compared as UTF-8 bytes.
        Assert.Equal(Encoding.UTF8.GetBytes(
            "With cloud speech on, the recording of every dictation, your voice, is sent over the internet to {host} to be turned into text, together with the words in your personal dictionary as a hint. It uses your own API key ({VAR}).\n\n" +
            "Dictum encrypts the connection (HTTPS) but cannot see, check or certify what {host} keeps, for how long, how it protects it, or who else can listen to it. By switching cloud speech on you accept responsibility for the security and privacy of the speech sent there. Check that service's terms yourself, and do not dictate confidential, personal or client information with cloud speech on unless you have.\n\n" +
            "Nothing is sent until you agree. You can switch it off at any time in Settings > Models, and offline only mode refuses it; your speech model on this PC is used again."),
            Encoding.UTF8.GetBytes(SpeechPolicy.ConsentText("{host}", "{VAR}")));
        Assert.Equal("Send my voice to api.groq.com", SpeechPolicy.AgreeButton("api.groq.com"));
        Assert.Equal("Keep speech on this PC", SpeechPolicy.DeclineButton);
    }

    /// <summary>Every speech key is one the log redactor and the self-test know (SecretStore.KeyNames).</summary>
    [Fact]
    public void Every_speech_key_is_redacted_and_listed()
    {
        Assert.All(SpeechPresets.All.Where(p => p.KeyVariable is not null), p => Assert.Contains(p.KeyVariable!, SecretStore.KeyNames));
        Assert.Equal(SecretStore.SpeechKey, SpeechPresets.Custom.KeyVariable);
    }

    /// <summary>
    /// Network sweep, Core side (the Help page's own sweep, HelpPageTests.The_page_reaches_the_network_only_through_the_platform_seam,
    /// covers that page only): the only code that makes an HTTP request is the known list, and cloud speech's engine is the one
    /// new entry. A new file that sends anything fails this until it is added here on purpose.
    /// </summary>
    [Fact]
    public void Only_the_known_clients_make_network_requests()
    {
        var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var request = new Regex(@"new HttpClient\b|HttpRequestMessage|\.PostAsync\(|\.SendAsync\(|\.GetStreamAsync\(|\.GetStringAsync\(|SocketsHttpHandler|WebRequest|WebClient|TcpClient|UdpClient|new Socket\b");
        var senders = Directory.EnumerateFiles(Path.Combine(FixBatchDownloadTests.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(bin) && !f.Contains(obj))
            .Where(f => File.ReadLines(f).Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && request.IsMatch(l)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["CloudSpeechEngine.cs", "ModelDownloader.cs", "OfflineGuardHandler.cs", "OpenAiCompatibleLlmClient.cs", "UpdateCheck.cs"], senders);
    }

    /// <summary>Records each request: where it went, its key, and every multipart field's bytes; answers with a transcription.</summary>
    private sealed class SpeechRecorder(Func<int, string>? body = null) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = new Dictionary<string, byte[]>();
            string? fileName = null, fileType = null;
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    fields[name] = await part.ReadAsByteArrayAsync(cancellationToken);
                    if (name == "file") (fileName, fileType) = (part.Headers.ContentDisposition.FileName?.Trim('"'), part.Headers.ContentType?.MediaType);
                }
            }
            lock (Requests) Requests.Add(new Recorded(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), fields, fileName, fileType));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body?.Invoke(Requests.Count) ?? """{"text":"Hello from the cloud."}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization, Dictionary<string, byte[]> Fields, string? FileName, string? FileType)
    {
        public string Text(string field) => Encoding.UTF8.GetString(Fields[field]);
    }

    /// <summary>Never answers: waits until the request is cancelled, and says when it started and when it was stopped.</summary>
    private sealed class Hanging : HttpMessageHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult(true);
                throw;
            }
            throw new InvalidOperationException("unreachable");
        }
    }
}
