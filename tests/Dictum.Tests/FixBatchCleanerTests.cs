using System.Collections.Concurrent;
using System.Net;
using Dictum.Core;
using Dictum.Llm;

namespace Dictum.Tests;

/// <summary>Fix batch, item 7 (the deadline token) and the item 5 pins on the fallback paths.</summary>
public class FixBatchCleanerTests
{
    [Fact]
    public async Task A_cleanup_that_times_out_always_cancels_the_provider_call()
    {
        // Lens probe (15/10/2026): the linked source was disposed before its CancelAfter timer fired, so the provider kept
        // running (21 of 400 on an idle machine, 396 of 400 with every core busy) and held the local model's lock.
        var tokens = new ConcurrentQueue<CancellationToken>();
        var never = new FakeLlmClient(ct =>
        {
            tokens.Enqueue(ct);
            return new TaskCompletionSource<LlmReply>().Task;
        });
        var cleaner = new TranscriptCleaner(never, CleanupPrompt.Default, TimeSpan.FromMilliseconds(20), NullLog.Instance, "test",
            deadline: _ => TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < 200; i++)
        {
            var result = await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
            Assert.Contains("timed out", result.FallbackReason);
        }
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(200, tokens.Count);
        Assert.Equal(0, tokens.Count(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task A_truncated_reply_is_never_used_even_when_the_words_would_pass()
    {
        // "dog" lost to max_tokens; every other rule accepts this reply, so only the truncation check stands in the way.
        const string raw = "um so the quick brown fox uh jumps over the lazy dog you know";
        Assert.Null(CleanupValidator.Validate(raw, "So the quick brown fox jumps over the lazy").Rejection);
        var cleaner = new TranscriptCleaner(FakeLlmClient.Replying("So the quick brown fox jumps over the lazy", "max_tokens"), CleanupPrompt.Default,
            TimeSpan.FromSeconds(2), NullLog.Instance, "test", deadline: _ => TimeSpan.FromSeconds(2));
        var result = await cleaner.CleanAsync(raw, TestContext.Current.CancellationToken);
        Assert.False(result.Cleaned);
        Assert.Contains("truncated", result.FallbackReason);
    }

    private const string Marker = "zanzibar";

    /// <summary>One row per rejection rule: the marker is in the dictation and in the reply, and must reach neither the log nor the reason.</summary>
    [Theory]
    [InlineData("zanzibar island trip notes", "<|im_end|> Zanzibar island trip notes", "end_turn")]
    [InlineData("zanzibar is a lovely island to visit in the summer months",
        "Zanzibar is a lovely island to visit in the summer months, Zanzibar Zanzibar Zanzibar is lovely in summer, truly lovely.", "end_turn")]
    [InlineData("zanzibar is a lovely island to visit in the summer months", "Zanzibar.", "end_turn")]
    [InlineData("zanzibar is a lovely island to visit in the summer months", "Here is Zanzibar, a lovely island to visit in the summer months.", "end_turn")]
    [InlineData("zanzibar is a lovely island to visit in the summer months", "Zanzibar is a lovely island to visit in the summer months. Let me know!", "end_turn")]
    [InlineData("zanzibar is a lovely island to visit in the summer months", "Zanzibar: remove filler words um uh er like you know.", "end_turn")]
    [InlineData("is zanzibar a lovely island to visit?", "Zanzibar is a lovely island to visit.", "end_turn")]
    [InlineData("send zanzibar notes", "Zanzibar has warm beaches and great food all year round.", "end_turn")]
    [InlineData("zanzibar ok", "Zanzibar 好的", "end_turn")]
    [InlineData("zanzibar is not cold in the summer months at all", "Zanzibar is cold in the summer months at all.", "end_turn")]
    [InlineData("zanzibar has 3301 visitors a day", "Zanzibar has 33 visitors a day.", "end_turn")]
    [InlineData("zanzibar trip is booked", "Zanzibar trip is booked.\nThanks.", "end_turn")]
    [InlineData("zanzibar trip is booked", "Output: Zanzibar trip is booked.", "end_turn")]
    [InlineData("the trip is to zanzibar scratch that to mombasa", "The trip is to Zanzibar.", "end_turn")]
    [InlineData("the damn zanzibar trip is booked", "The Zanzibar trip is booked.", "end_turn")]
    [InlineData("zanzibar trip is booked", "Zanzibar", "refusal")]
    [InlineData("zanzibar trip is booked", "Zanzibar trip is", "max_tokens")]
    public async Task No_fallback_puts_dictation_text_in_the_log_or_the_reason(string raw, string reply, string stop)
    {
        var log = new RecordingLog();
        var cleaner = new TranscriptCleaner(FakeLlmClient.Replying(reply, stop), CleanupPrompt.Default, TimeSpan.FromSeconds(2), log, "test",
            deadline: _ => TimeSpan.FromSeconds(2), validation: new ValidationContext(CleanupPrompt.Default, ["damn"]));
        var result = await cleaner.CleanAsync(raw, TestContext.Current.CancellationToken);
        Assert.False(result.Cleaned);
        Assert.DoesNotContain(Marker, result.FallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Marker, log.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_http_error_and_an_unloaded_model_keep_dictation_text_out_of_the_log()
    {
        const string raw = "zanzibar trip is booked for friday";
        var routes = new ILlmClient[]
        {
            new FakeLlmClient(async ct => { await Task.Delay(Timeout.Infinite, ct); return new LlmReply("Zanzibar", "end_turn"); }),
            new FakeLlmClient(_ => throw new HttpRequestException("Response status code does not indicate success: 500.", null, HttpStatusCode.InternalServerError)),
            new LocalLlmClient(() => null, "qwen2.5-1.5b"),
        };
        foreach (var client in routes)
        {
            var log = new RecordingLog();
            var cleaner = new TranscriptCleaner(client, CleanupPrompt.Default, TimeSpan.FromMilliseconds(100), log, "test", deadline: _ => TimeSpan.FromMilliseconds(100));
            var result = await cleaner.CleanAsync(raw, TestContext.Current.CancellationToken);
            Assert.False(result.Cleaned);
            Assert.DoesNotContain(Marker, result.FallbackReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Marker, log.All, StringComparison.OrdinalIgnoreCase);
        }
    }
}
