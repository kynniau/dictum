using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Dictum.Core;
using Dictum.Llm;

namespace Dictum.Tests;

/// <summary>Scripted provider: no network.</summary>
internal sealed class FakeLlmClient(Func<CancellationToken, Task<LlmReply>> respond) : ILlmClient
{
    public int Calls { get; private set; }
    public string? LastSystem { get; private set; }
    public string? LastUser { get; private set; }

    public string Name => "fake";

    public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
    {
        Calls++;
        LastSystem = systemPrompt;
        LastUser = rawTranscript;
        return respond(cancellationToken);
    }

    public static FakeLlmClient Replying(string? text, string stop = "end_turn") => new(_ => Task.FromResult(new LlmReply(text, stop)));
}

public class TranscriptCleanerTests
{
    private const string Raw = "um so the quick brown fox uh jumps over the lazy dog you know";
    private static readonly string Tidied = DeterministicCleaner.Clean(Raw); // the fallback tier

    private static TranscriptCleaner Cleaner(ILlmClient client, int timeoutMs = 2000) =>
        new(client, CleanupPrompt.Default, TimeSpan.FromMilliseconds(timeoutMs), NullLog.Instance, "test", deadline: _ => TimeSpan.FromMilliseconds(timeoutMs));

    [Fact]
    public async Task Good_reply_is_used_and_prompt_is_the_spec_prompt_with_raw_user_turn()
    {
        var client = FakeLlmClient.Replying("So the quick brown fox jumps over the lazy dog.");
        var result = await Cleaner(client).CleanAsync(Raw, CancellationToken.None);
        Assert.True(result.Cleaned);
        Assert.Equal("So the quick brown fox jumps over the lazy dog.", result.Text);
        Assert.Equal(Raw, client.LastUser);
        Assert.StartsWith("You clean up raw speech-to-text dictation.", client.LastSystem);
        Assert.Contains("Use Australian English spelling.", client.LastSystem);
    }

    [Fact]
    public async Task Refusal_falls_back_to_the_rule_based_tidy()
    {
        var result = await Cleaner(FakeLlmClient.Replying(null, "refusal")).CleanAsync(Raw, CancellationToken.None);
        Assert.False(result.Cleaned);
        Assert.Equal(Tidied, result.Text);
        Assert.Contains("refused", result.FallbackReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public async Task Empty_reply_falls_back_to_the_rule_based_tidy(string? reply)
    {
        var result = await Cleaner(FakeLlmClient.Replying(reply)).CleanAsync(Raw, CancellationToken.None);
        Assert.Equal(Tidied, result.Text);
        Assert.Contains("empty", result.FallbackReason);
    }

    [Fact]
    public async Task Timeout_falls_back_to_the_rule_based_tidy_promptly()
    {
        var slow = new FakeLlmClient(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new LlmReply("late", "end_turn");
        });
        var started = Environment.TickCount64;
        var result = await Cleaner(slow, timeoutMs: 100).CleanAsync(Raw, CancellationToken.None);
        Assert.Equal(Tidied, result.Text);
        Assert.Contains("timed out", result.FallbackReason);
        Assert.True(Environment.TickCount64 - started < 5000);
    }

    [Fact]
    public async Task Network_error_falls_back_to_the_rule_based_tidy()
    {
        var failing = new FakeLlmClient(_ => throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable));
        var result = await Cleaner(failing).CleanAsync(Raw, CancellationToken.None);
        Assert.Equal(Tidied, result.Text);
        Assert.Contains("503", result.FallbackReason);
    }

    [Fact]
    public async Task Truncated_reply_falls_back_to_the_rule_based_tidy()
    {
        var result = await Cleaner(FakeLlmClient.Replying("So the quick", "max_tokens")).CleanAsync(Raw, CancellationToken.None);
        Assert.Equal(Tidied, result.Text);
    }

    [Fact]
    public async Task User_cancellation_propagates_instead_of_falling_back()
    {
        using var cts = new CancellationTokenSource();
        var waiting = new FakeLlmClient(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new LlmReply("never", "end_turn");
        });
        var task = Cleaner(waiting, timeoutMs: 10_000).CleanAsync(Raw, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Rules_only_cleaner_returns_the_tidy_without_calling_a_model()
    {
        var result = await TranscriptCleaner.RulesOnly("off").CleanAsync(Raw, CancellationToken.None);
        Assert.Equal(new CleanupResult(Tidied, false, null), result);
    }
}

public class CleanupValidatorTests
{
    [Theory]
    [InlineData("um so the quick brown fox jumps", "Here's the cleaned text: So the quick brown fox jumps.")]
    [InlineData("um so the quick brown fox jumps", "So the quick brown fox jumps.\n\nLet me know if you need anything else!")]
    [InlineData("um so the quick brown fox jumps", "I'm sorry, but I can't help with that.")]
    [InlineData("what's the capital of france?", "The capital of France is Paris.")]
    [InlineData("send it to tom", "Send it to Tom. Also, here are three tips for writing better emails: keep it short, be specific, and proofread.")]
    [InlineData("can you write me a poem about the sea", "Waves roll in beneath a silver moon, the tide sings softly to the dunes at night.")]
    public void Commentary_or_answers_are_rejected(string raw, string reply)
    {
        var (text, rejection) = CleanupValidator.Validate(raw, reply);
        Assert.Null(text);
        Assert.NotNull(rejection);
    }

    [Theory]
    [InlineData("um sure i'll be there at five", "Sure, I'll be there at five.")]
    [InlineData("here is the report you asked for", "Here is the report you asked for.")]
    [InlineData("what's the capital of france question mark", "What's the capital of France?")]
    [InlineData("i realised the colour was wrong", "I realised the colour was wrong.")]
    [InlineData("uh lol ok see you soon", "lol ok see you soon")]
    public void Genuine_cleanups_are_accepted(string raw, string reply)
    {
        var (text, rejection) = CleanupValidator.Validate(raw, reply);
        Assert.Null(rejection);
        Assert.Equal(reply, text);
    }

    [Fact]
    public void Wrapping_quotes_are_removed()
    {
        Assert.Equal("See you at five.", CleanupValidator.Validate("see you at five", "\"See you at five.\"").Text);
    }
}

/// <summary>In the non-parallel collection: one test sets ANTHROPIC_BASE_URL for the whole process.</summary>
[Collection("Process-wide state")]
public class ProviderParsingTests
{
    private static Message AnthropicMessage(string stopReason, string text)
    {
        var json = $$"""
            {
              "id": "msg_test", "type": "message", "role": "assistant", "model": "claude-haiku-5-5",
              "content": [ { "type": "text", "text": {{JsonSerializer.Serialize(text)}} } ],
              "stop_reason": "{{stopReason}}", "stop_sequence": null,
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var raw = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return Message.FromRawUnchecked(raw);
    }

    [Fact]
    public void Anthropic_refusal_is_detected_before_reading_content()
    {
        var reply = AnthropicLlmClient.ToReply(AnthropicMessage("refusal", "I can't help with that."));
        Assert.True(reply.IsRefusal);
        Assert.Null(reply.Text);
    }

    [Fact]
    public void Anthropic_end_turn_text_is_read()
    {
        var reply = AnthropicLlmClient.ToReply(AnthropicMessage("end_turn", "Cleaned."));
        Assert.Equal(new LlmReply("Cleaned.", "end_turn"), reply);
    }

    [Fact]
    public async Task Anthropic_request_wire_format_matches_the_spec()
    {
        const string response = """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5",
             "content":[{"type":"text","text":"So the fox jumps."}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":6}}
            """;
        var handler = new RecordingHandler(response);
        using var client = new AnthropicLlmClient("test-key-not-real", "claude-haiku-5-5", TimeSpan.FromSeconds(5), http: new HttpClient(handler));
        var reply = await client.CompleteAsync("SYSTEM PROMPT", "um so the fox uh jumps", TestContext.Current.CancellationToken);

        Assert.Equal(new LlmReply("So the fox jumps.", "end_turn"), reply);
        Assert.EndsWith("/v1/messages", handler.Uri!.AbsolutePath);
        Assert.Equal("test-key-not-real", handler.ApiKeyHeader);
        Assert.DoesNotContain("test-key-not-real", handler.Body);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("SYSTEM PROMPT", root.GetProperty("system").GetString());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.False(root.TryGetProperty("stream", out var stream) && stream.GetBoolean());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString() : content[0].GetProperty("text").GetString();
        Assert.Equal("um so the fox uh jumps", text);
    }

    private const string EndTurnMessage = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5",
         "content":[{"type":"text","text":"ok"}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
        """;

    [Fact]
    public async Task Anthropic_requests_go_to_api_anthropic_com_even_when_ANTHROPIC_BASE_URL_is_set()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://attacker.example");
        try
        {
            var handler = new RecordingHandler(EndTurnMessage);
            using var client = new AnthropicLlmClient("test-key-not-real", "claude-haiku-5-5", TimeSpan.FromSeconds(5), http: new HttpClient(handler));
            await client.CompleteAsync("s", "u", TestContext.Current.CancellationToken);
            Assert.Equal("api.anthropic.com", handler.Uri!.Host);
            Assert.Equal("https", handler.Uri.Scheme);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", previous);
        }
    }

    [Fact]
    public async Task Anthropic_works_on_a_shared_http_client_that_has_already_sent_requests()
    {
        // Production now hands the Anthropic SDK the app's shared, offline-guarded HttpClient.
        var handler = new RecordingHandler(EndTurnMessage);
        using var http = new HttpClient(handler);
        using var first = new AnthropicLlmClient("k", "claude-haiku-5-5", TimeSpan.FromSeconds(5), http: http);
        await first.CompleteAsync("s", "u", TestContext.Current.CancellationToken);
        using var second = new AnthropicLlmClient("k", "claude-haiku-5-5", TimeSpan.FromSeconds(7), http: http);
        var reply = await second.CompleteAsync("s", "u", TestContext.Current.CancellationToken);
        Assert.Equal("end_turn", reply.StopReason);
    }

    [Fact]
    public async Task Disposing_an_Anthropic_client_leaves_the_shared_http_client_open()
    {
        // Lens P13 (16/10/2026): Dispose disposed the SDK client, and with it the HttpClient it was given, which in production is the
        // app's one shared, offline-guarded client that every later clean-up request goes through.
        var handler = new RecordingHandler(EndTurnMessage);
        using var http = new HttpClient(handler);
        new AnthropicLlmClient("k", "claude-haiku-5-5", TimeSpan.FromSeconds(5), http: http).Dispose();
        using var next = new AnthropicLlmClient("k", "claude-haiku-5-5", TimeSpan.FromSeconds(5), http: http);
        var reply = await next.CompleteAsync("s", "u", TestContext.Current.CancellationToken);
        Assert.Equal("end_turn", reply.StopReason);
    }

    [Fact]
    public async Task Anthropic_uses_llm_base_url_from_settings_when_set()
    {
        var handler = new RecordingHandler(EndTurnMessage);
        using var client = new AnthropicLlmClient("k", "claude-haiku-5-5", TimeSpan.FromSeconds(5), "https://gateway.example.com", new HttpClient(handler));
        await client.CompleteAsync("s", "u", TestContext.Current.CancellationToken);
        Assert.Equal("gateway.example.com", handler.Uri!.Host);
        Assert.Contains("gateway.example.com", client.Name);
    }

    [Fact]
    public void Anthropic_with_a_leftover_base_url_logs_where_requests_will_go()
    {
        var secrets = Dictum.Settings.SecretStore.FromContents("ANTHROPIC_API_KEY=k-test-123456\n", null, _ => null);
        using var http = new HttpClient(new CountingHandler()); // never a real network stack in a unit test

        var log = new RecordingLog();
        var redirected = new Dictum.Settings.LlmSettings { Provider = "anthropic", BaseUrl = "https://gw.example.com/v1", CloudConsent = "gw.example.com" }; // agreed in the app (SPEC §22)
        TranscriptCleaner.Create(redirected, false, secrets, http, log);
        var warning = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning).Message;
        Assert.Contains("Anthropic requests will go to gw.example.com", warning);

        var quiet = new RecordingLog();
        TranscriptCleaner.Create(new Dictum.Settings.LlmSettings(), false, secrets, http, quiet);
        TranscriptCleaner.Create(redirected with { Provider = "openai-compatible" }, false, secrets, http, quiet);
        Assert.DoesNotContain(quiet.Lines, l => l.Level == LogLevel.Warning);
        Assert.Null(TranscriptCleaner.AnthropicRedirectWarning(new Dictum.Settings.LlmSettings()));
    }

    [Fact]
    public void OpenAi_shapes_are_parsed()
    {
        static LlmReply P(string json) { using var d = JsonDocument.Parse(json); return OpenAiCompatibleLlmClient.Parse(d.RootElement); }
        Assert.Equal(new LlmReply("Hi.", "stop"), P("""{"choices":[{"message":{"role":"assistant","content":"Hi."},"finish_reason":"stop"}]}"""));
        Assert.True(P("""{"choices":[{"message":{"content":null,"refusal":"no"},"finish_reason":"stop"}]}""").IsRefusal);
        Assert.True(P("""{"choices":[{"message":{"content":""},"finish_reason":"content_filter"}]}""").IsRefusal);
        Assert.True(P("""{"choices":[{"message":{"content":"Hi"},"finish_reason":"length"}]}""").IsTruncated);
        Assert.Null(P("""{"error":{"message":"bad"}}""").Text);
    }

    [Fact]
    public async Task OpenAi_request_shape_and_optional_bearer_key()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"Done."},"finish_reason":"stop"}]}""");
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), "http://localhost:11434/v1/", "llama3.2", apiKey: null);
        var reply = await client.CompleteAsync("SYS", "raw words", CancellationToken.None);

        Assert.Equal("Done.", reply.Text);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Uri!.ToString());
        Assert.Null(handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("llama3.2", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(("system", "SYS"), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString()));
        Assert.Equal(("user", "raw words"), (messages[1].GetProperty("role").GetString(), messages[1].GetProperty("content").GetString()));

        var keyed = new RecordingHandler("""{"choices":[{"message":{"content":"x"}}]}""");
        await new OpenAiCompatibleLlmClient(new HttpClient(keyed), "https://gw.example/v1", "m", "k-123").CompleteAsync("s", "u", CancellationToken.None);
        Assert.Equal("Bearer k-123", keyed.Authorization);
    }

    [Fact]
    public async Task OpenAi_http_error_throws_without_echoing_the_body()
    {
        var handler = new RecordingHandler("secret body", HttpStatusCode.Unauthorized);
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), "http://localhost:1/v1", "m", "k-123");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("k-123", ex.Message);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(300, 1024)]
    [InlineData(10_000, 6924)]
    [InlineData(100_000, 8192)]
    public void Max_tokens_is_clamped(int chars, int expected) => Assert.Equal(expected, TokenBudget.MaxTokens(new string('a', chars)));

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKeyHeader { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ApiKeyHeader = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

public class SecretRedactorTests
{
    [Fact]
    public void Registered_secrets_never_reach_the_log_file()
    {
        var dir = Directory.CreateTempSubdirectory("dictate-log-");
        try
        {
            var redactor = new SecretRedactor();
            redactor.Register("sk-ant-api03-SECRET");
            using (var log = new FileLog(dir.FullName, redactor, LogLevel.Debug))
            {
                log.Error("request failed with header x-api-key: sk-ant-api03-SECRET");
            }
            var text = File.ReadAllText(Directory.GetFiles(dir.FullName).Single());
            Assert.DoesNotContain("sk-ant-api03-SECRET", text);
            Assert.Contains("[redacted]", text);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
