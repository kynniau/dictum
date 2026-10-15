using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>The cleanup deadline scales with the dictation, holds even if the provider ignores it, and a dictation the
/// local model cannot finish in time skips the model up front.</summary>
public class CleanupDeadlineTests
{
    /// <summary>Never answers and ignores cancellation (like llama.cpp evaluating a long prompt).</summary>
    private sealed class StuckClient(TimeSpan? predicted) : ILlmClient
    {
        public int Calls { get; private set; }
        public string Name => "stuck";
        public TimeSpan? Predict(string rawTranscript) => predicted;

        public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            Calls++;
            return new TaskCompletionSource<LlmReply>().Task;
        }
    }

    [Theory]
    [InlineData(0, 15, 15)]
    [InlineData(50, 15, 15)]
    [InlineData(100, 15, 20)]
    [InlineData(400, 15, 50)]
    [InlineData(1000, 15, 60)]
    [InlineData(0, 30, 30)]
    [InlineData(5000, 90, 90)]
    public void The_deadline_is_10_s_plus_a_tenth_per_word_between_the_setting_and_60_s(int words, int minimumSeconds, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TranscriptCleaner.Deadline(words, TimeSpan.FromSeconds(minimumSeconds)));

    [Fact]
    public async Task The_deadline_holds_even_when_the_provider_ignores_cancellation()
    {
        var client = new StuckClient(predicted: null);
        var cleaner = new TranscriptCleaner(client, "p", TimeSpan.FromSeconds(15), NullLog.Instance, "s", deadline: _ => TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        var clean = cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Same(clean, await Task.WhenAny(clean, Task.Delay(5000, TestContext.Current.CancellationToken)));
        var result = await clean;
        Assert.Equal(("Hello there.", "timed out after 200 ms"), (result.Text, result.FallbackReason));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"returned after {sw.Elapsed}");
    }

    [Fact]
    public async Task A_dictation_predicted_to_run_past_the_deadline_skips_the_model()
    {
        var client = new StuckClient(predicted: TimeSpan.FromSeconds(100));
        var log = new RecordingLog();
        var cleaner = new TranscriptCleaner(client, "p", TimeSpan.FromSeconds(15), log, "s");
        Assert.NotNull(cleaner.SkipReason("um hello there"));
        var result = await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Equal(("Hello there.", false, true), (result.Text, result.Cleaned, result.Skipped));
        Assert.Equal(0, client.Calls);
        Assert.Contains(log.Lines, l => l.Message.Contains("skipped: estimated 100.0 s for 3 words at the measured speed, over the 15 s limit"));
    }

    [Fact]
    public void A_prediction_inside_the_deadline_does_not_skip() =>
        Assert.Null(new TranscriptCleaner(new StuckClient(TimeSpan.FromSeconds(5)), "p", TimeSpan.FromSeconds(15), NullLog.Instance, "s").SkipReason("hello there"));

    [Fact]
    public async Task The_pill_says_the_model_was_skipped()
    {
        var cleaner = new TranscriptCleaner(new StuckClient(TimeSpan.FromSeconds(100)), "p", TimeSpan.FromSeconds(15), NullLog.Instance, "s");
        var pills = new List<PillView>();
        var inserted = new List<string>();
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber("um hello there")), () => cleaner,
            (text, _) => { inserted.Add(text); return Task.CompletedTask; }, () => new AppSettings(), NullLog.Instance);
        await pipeline.RunAsync(new CapturedAudio(new float[32000], 0.2f), TestContext.Current.CancellationToken, pills.Add);
        Assert.Equal([PillView.PolishingSkipped], pills);
        Assert.Equal(["Hello there."], inserted);
        Assert.Equal("Polishing… skipped (long dictation)", PillText.For(PillView.PolishingSkipped, TimeSpan.Zero));
    }
}
