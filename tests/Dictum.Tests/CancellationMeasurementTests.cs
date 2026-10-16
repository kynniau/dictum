using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// Measurement, not a unit test: does cancelling the token stop native whisper mid-run? Gated by
/// DICTUM_CANCEL_TESTS=1 plus DICTUM_MODEL_PATH, and must run alone in its process (the CPU runtime is forced,
/// and Whisper.net keeps the first runtime a process loads): Dictum.Tests.exe -class Dictum.Tests.CancellationMeasurementTests
/// </summary>
[Collection("Process-wide state")] // loads native models: never beside another model-loading class
public class CancellationMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Cancelling_mid_transcription()
    {
        var modelPath = TestEnv.Get("MODEL_PATH");
        Assert.SkipUnless(TestEnv.Get("CANCEL_TESTS") == "1" && !string.IsNullOrWhiteSpace(modelPath),
            "Set DICTUM_CANCEL_TESTS=1 and DICTUM_MODEL_PATH, and run this class on its own.");

        var wav = WhisperIntegrationTests.SynthesiseWav(
            "um so the quick brown fox uh jumps over the lazy dog you know. The quarterly report is ready for review, please send comments by Friday.");
        float[] clip;
        try
        {
            clip = AudioFiles.ReadWav(wav).Samples;
        }
        finally
        {
            File.Delete(wav);
        }
        var minute = Enumerable.Repeat(clip, 6).SelectMany(s => s).ToArray();

        // DICTUM_CANCEL_RUNTIME=cpu (default) forces the CPU runtime; "auto" lets Vulkan load (the GPU figure).
        var runtime = TestEnv.Get("CANCEL_RUNTIME") == "auto" ? "auto" : "cpu";
        using var engine = WhisperEngine.Load(modelPath!, 0, runtime, NullLog.Instance);
        Assert.SkipUnless(runtime == "auto" || WhisperEngine.LoadedRuntime == "CPU", $"CPU runtime not loaded in this process ({WhisperEngine.LoadedRuntime}); run the class alone.");
        var ct = TestContext.Current.CancellationToken;
        var process = Process.GetCurrentProcess();

        var sw = Stopwatch.StartNew();
        await engine.TranscribeAsync(clip, ct);
        var clipMs = sw.ElapsedMilliseconds;

        sw.Restart();
        await engine.TranscribeAsync(minute, ct);
        var minuteMs = sw.ElapsedMilliseconds;

        using var cts = new CancellationTokenSource();
        sw.Restart();
        var running = engine.TranscribeAsync(minute, cts.Token);
        await Task.Delay(200, ct);
        cts.Cancel();
        var cancelledAt = sw.ElapsedMilliseconds;
        string outcome;
        try
        {
            await running;
            outcome = "completed normally";
        }
        catch (OperationCanceledException ex)
        {
            outcome = ex.GetType().Name;
        }
        var returnedAfterCancelMs = sw.ElapsedMilliseconds - cancelledAt;

        // Sample process CPU in 250 ms windows until it goes quiet: that is how long native whisper kept working.
        var cpuLog = new List<string>();
        var busyUntilMs = 0L;
        var quietWindows = 0;
        var watch = Stopwatch.StartNew();
        while (quietWindows < 2 && watch.ElapsedMilliseconds < 30_000)
        {
            process.Refresh();
            var before = process.TotalProcessorTime;
            await Task.Delay(250, ct);
            process.Refresh();
            var used = (process.TotalProcessorTime - before).TotalMilliseconds;
            if (cpuLog.Count < 8 || used < 100) cpuLog.Add($"{watch.ElapsedMilliseconds}ms:{used:0}");
            if (used >= 100) { busyUntilMs = watch.ElapsedMilliseconds; quietWindows = 0; }
            else quietWindows++;
        }
        var cpuDuringIdleSecond = busyUntilMs;

        sw.Restart();
        var again = await engine.TranscribeAsync(clip, ct);
        var againMs = sw.ElapsedMilliseconds;

        output.WriteLine($"runtime {WhisperEngine.LoadedRuntime}, model {Path.GetFileName(modelPath)}, clip {clip.Length / 16000.0:0.00} s, long input {minute.Length / 16000.0:0.0} s");
        output.WriteLine($"baseline: clip {clipMs} ms, long input {minuteMs} ms uncancelled");
        output.WriteLine($"cancel at +200 ms: returned {returnedAfterCancelMs} ms after Cancel() ({outcome})");
        output.WriteLine($"native work continued for about {cpuDuringIdleSecond} ms after the call returned (CPU ms per 250 ms window: {string.Join(" ", cpuLog)})");
        output.WriteLine($"next transcription of the clip: {againMs} ms (baseline {clipMs} ms): \"{again}\"");
        Assert.Contains("quick brown fox", again, StringComparison.OrdinalIgnoreCase);
        // The pass/fail checks (cancel stops the work; dispose after a cancel waits) are in the DICTUM_MODEL_PATH-gated
        // WhisperIntegrationTests; this class keeps only the CPU-forced measurement.
    }
}
