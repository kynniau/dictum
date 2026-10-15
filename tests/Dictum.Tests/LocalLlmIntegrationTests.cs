using System.Diagnostics;
using Dictum.Core;
using Dictum.Llm;

namespace Dictum.Tests;

/// <summary>Real llama.cpp runs. Gated by DICTUM_CLEANUP_MODEL_PATH (or DICTUM_LLM_PATH), a .gguf file; DICTUM_LLM_GPU=cpu forces the CPU backend.</summary>
[Collection("Process-wide state")] // loads native models: never beside another model-loading class
public class LocalLlmIntegrationTests(ITestOutputHelper output)
{
    /// <summary>DICTUM_CLEANUP_MODEL_PATH (the documented gate), or DICTUM_LLM_PATH (its earlier name).</summary>
    internal static string? ModelPath =>
        new[] { "CLEANUP_MODEL_PATH", "LLM_PATH" }.Select(TestEnv.Get).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    internal const string Gate = "DICTUM_CLEANUP_MODEL_PATH (or DICTUM_LLM_PATH) is not set.";
    private static string Gpu => TestEnv.Get("LLM_GPU") == "cpu" ? "cpu" : "auto";

    [Fact]
    public async Task Cleans_the_test_sentence()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ModelPath), Gate);
        using var engine = LocalLlmEngine.Load(ModelPath!, Gpu, new DebugLog(output));
        const string raw = "um so the quick brown fox uh jumps over the lazy dog you know";
        var sw = Stopwatch.StartNew();
        var result = await engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, raw,
            CleanupExamples.LocalMaxTokens(engine.CountTokens(raw)), TestContext.Current.CancellationToken);
        var (text, finished) = (result.Text, result.Finished);
        output.WriteLine($"backend {LocalLlmEngine.Backend}; {sw.ElapsedMilliseconds} ms; finished={finished}; reply: \"{text}\"");
        var (accepted, rejection) = CleanupValidator.Validate(raw, text);
        output.WriteLine($"validator: {(accepted is null ? "rejected: " + rejection : "accepted")}");
        Assert.Contains("quick brown fox", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(rejection is null, $"the app would reject this reply and paste the tidy instead: {rejection}");
    }

    /// <summary>Fix batch #56: the warm-up alone must give the long-dictation skip both speeds (it used to stop short of 4 tokens).</summary>
    [Fact]
    public async Task The_warm_up_measures_both_speeds()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ModelPath), Gate);
        using var engine = LocalLlmEngine.Load(ModelPath!, Gpu, NullLog.Instance);
        await engine.WarmUpAsync(null, TestContext.Current.CancellationToken);
        var (prompt, generation) = engine.Rates;
        output.WriteLine($"backend {LocalLlmEngine.Backend}: warm-up measured prompt {prompt:0} tokens/s, generation {generation:0.0} tokens/s");
        Assert.True(prompt > 0 && generation > 0, "the long-dictation skip has no speed estimate until a dictation finishes");
        Assert.NotNull(engine.Predict(200));
    }

    private const string LongDictation =
        "um okay so this is the update for the team. uh the quarterly report is nearly finished and I think we can send it to the client by friday afternoon. " +
        "the main change is the forecast section, which now uses the new numbers from finance. um we also need to check the appendix, because a couple of the " +
        "tables still show last year's figures. sarah said she can look at that on thursday. uh the other thing is the budget meeting, which has moved to next " +
        "tuesday at ten. please let me know if that time doesn't work for you. and you know, if anyone has comments on the draft, send them through by wednesday " +
        "so we have time to fix them. i'll book a room and send the invite this afternoon. oh and one more thing, the new starter begins on monday, so please say " +
        "hello and help them find their way around. thanks everyone, talk soon.";

    /// <summary>Measurement: does cancelling (Esc) stop generation, and how long does the backend stay busy?</summary>
    [Fact]
    public async Task Cancelling_generation_returns_promptly()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ModelPath), Gate);
        var ct = TestContext.Current.CancellationToken;
        using var engine = LocalLlmEngine.Load(ModelPath!, Gpu, NullLog.Instance);
        var max = CleanupExamples.LocalMaxTokens(engine.CountTokens(LongDictation));
        await engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, "ok", 4, ct); // warm-up

        var sw = Stopwatch.StartNew();
        await engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, "uh see you soon", 32, ct);
        var shortMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var full = (await engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, LongDictation, max, ct)).Text;
        var fullMs = sw.ElapsedMilliseconds;

        using var cts = new CancellationTokenSource();
        sw.Restart();
        var running = engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, LongDictation, max, cts.Token);
        await Task.Delay(200, ct);
        cts.Cancel();
        var cancelledAt = sw.ElapsedMilliseconds;
        var outcome = "completed";
        try
        {
            await running;
        }
        catch (OperationCanceledException ex)
        {
            outcome = ex.GetType().Name;
        }
        var returnedAfter = sw.ElapsedMilliseconds - cancelledAt;

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        await Task.Delay(500, ct);
        process.Refresh();
        var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;

        sw.Restart();
        await engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, "uh see you soon", 32, ct);
        var nextMs = sw.ElapsedMilliseconds;

        output.WriteLine($"backend {LocalLlmEngine.Backend}, model {Path.GetFileName(ModelPath)}, long input {engine.CountTokens(LongDictation)} tokens, max_tokens {max}");
        output.WriteLine($"baseline: short call {shortMs} ms; full long cleanup {fullMs} ms ({full.Length} chars)");
        output.WriteLine($"cancel at +200 ms: returned {returnedAfter} ms after Cancel() ({outcome})");
        output.WriteLine($"process CPU in the 500 ms after it returned: {cpuMs:0} ms; next short call {nextMs} ms (baseline {shortMs} ms)");
        Assert.NotEqual("completed", outcome);
        Assert.True(returnedAfter < fullMs / 2, $"generation ran on after Esc: returned {returnedAfter} ms after Cancel(); an uncancelled run takes {fullMs} ms");
    }

    /// <summary>
    /// The cached prompt prefix must leave no trace of one dictation in the next: B after A (and after a cancelled A)
    /// must match B on a freshly loaded model, token for token (greedy decoding is deterministic on one backend).
    /// </summary>
    [Fact]
    public async Task The_prompt_cache_carries_nothing_from_one_dictation_to_the_next()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ModelPath), Gate);
        var ct = TestContext.Current.CancellationToken;
        const string b = "um so the quick brown fox uh jumps over the lazy dog you know";
        async Task<string> Run(LocalLlmEngine engine, string raw) =>
            (await engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, raw, CleanupExamples.LocalMaxTokens(engine.CountTokens(raw)), ct)).Text;

        string fresh, afterA, afterCancelledA;
        using (var engine = LocalLlmEngine.Load(ModelPath!, Gpu, NullLog.Instance)) fresh = await Run(engine, b);
        using (var engine = LocalLlmEngine.Load(ModelPath!, Gpu, NullLog.Instance))
        {
            await Run(engine, LongDictation);
            afterA = await Run(engine, b);
        }
        using (var engine = LocalLlmEngine.Load(ModelPath!, Gpu, NullLog.Instance))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var cancelled = engine.CompleteAsync(CleanupPrompt.Default, CleanupExamples.Pairs, LongDictation, 400, cts.Token);
            var outcome = "completed";
            try
            {
                await cancelled;
            }
            catch (OperationCanceledException ex)
            {
                outcome = ex.GetType().Name;
            }
            afterCancelledA = await Run(engine, b);
            output.WriteLine($"cancelled A: {outcome}");
        }
        output.WriteLine($"backend {LocalLlmEngine.Backend}, {Path.GetFileName(ModelPath)}");
        output.WriteLine($"fresh:            \"{fresh}\"");
        output.WriteLine($"after A:          \"{afterA}\"");
        output.WriteLine($"after cancelled A: \"{afterCancelledA}\"");
        Assert.Equal(fresh, afterA);
        Assert.Equal(fresh, afterCancelledA);
    }

    private sealed class DebugLog(ITestOutputHelper output) : ILog
    {
        public void Write(LogLevel level, string message)
        {
            if (level >= LogLevel.Info) output.WriteLine($"{level}: {message}");
        }
    }
}
