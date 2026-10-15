using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Real whisper.cpp run. Skipped unless DICTUM_MODEL_PATH points at a ggml model file.</summary>
[Collection("Process-wide state")] // loads native models: never beside another model-loading class
public class WhisperIntegrationTests(ITestOutputHelper output)
{
    public const string Sentence = "um so the quick brown fox uh jumps over the lazy dog you know";

    public static string SynthesiseWav(string text)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"dictate-it-{Guid.NewGuid():N}.wav");
        using var synth = new SpeechSynthesizer();
        synth.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        synth.SetOutputToNull();
        return wav;
    }

    [Fact]
    public async Task Synthesised_dictation_with_fillers_is_transcribed()
    {
        var modelPath = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelPath), "DICTUM_MODEL_PATH is not set.");

        var wav = SynthesiseWav(Sentence);
        try
        {
            var audio = AudioFiles.ReadWav(wav);
            Assert.True(audio.Duration.TotalSeconds > 1);
            using var engine = WhisperEngine.Load(modelPath!, "en", 0, "auto", NullLog.Instance);
            var text = await engine.TranscribeAsync(audio.Samples, TestContext.Current.CancellationToken);
            output.WriteLine($"runtime {WhisperEngine.LoadedRuntime}; transcript: {text}");

            var words = Regex.Replace(text.ToLowerInvariant(), "[^a-z ]", " ");
            words = Regex.Replace(words, " +", " ");
            Assert.Contains("quick brown fox", words);
            Assert.Contains("lazy dog", words);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    /// <summary>
    /// SPEC §5 "Esc must stop it": cancelling must stop whisper's work, not just the wait for it. Bound relative to an
    /// uncancelled run of the same input on the same engine, so it holds on CPU and Vulkan alike (fix batch #77).
    /// </summary>
    [Fact]
    public async Task Cancelling_stops_the_transcription_and_dispose_waits_for_it()
    {
        var modelPath = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelPath), "DICTUM_MODEL_PATH is not set.");
        var ct = TestContext.Current.CancellationToken;
        var wav = SynthesiseWav("um so the quick brown fox uh jumps over the lazy dog you know. The quarterly report is ready for review, please send comments by Friday.");
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
        var engine = WhisperEngine.Load(modelPath!, "en", 0, "auto", NullLog.Instance);
        await engine.TranscribeAsync(clip, ct); // warm-up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.TranscribeAsync(minute, ct);
        var fullMs = sw.ElapsedMilliseconds;

        using var cts = new CancellationTokenSource();
        sw.Restart();
        var running = engine.TranscribeAsync(minute, cts.Token);
        await Task.Delay(200, ct);
        cts.Cancel();
        var cancelledAt = sw.ElapsedMilliseconds;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var returnedAfter = sw.ElapsedMilliseconds - cancelledAt;
        output.WriteLine($"runtime {WhisperEngine.LoadedRuntime}: uncancelled {fullMs} ms; cancelled at +200 ms, returned {returnedAfter} ms after Cancel()");
        Assert.True(returnedAfter < fullMs / 2, $"whisper ran on after Esc: returned {returnedAfter} ms after Cancel(); an uncancelled run takes {fullMs} ms");

        // Disposing right after a cancel (model swap, quit) must wait for the native run, not throw.
        using var cts2 = new CancellationTokenSource();
        var second = engine.TranscribeAsync(minute, cts2.Token);
        await Task.Delay(200, ct);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        engine.Dispose();
    }
}

/// <summary>
/// SPEC §7: showing the pill (and the orb and the edge glow) must not change GetForegroundWindow(), and a click on them
/// must reach the window below. Avalonia hosts one platform per process and this one runs the headless platform, so the
/// real Win32 windows are shown by Dictum.UiProbe in a child process, which reports each check. Needs an interactive
/// desktop: gated by DICTUM_UI_TESTS=1, and skipped (with the probe's reason) when no window can take the foreground.
/// </summary>
public class PillFocusTests(ITestOutputHelper output)
{
    /// <summary>The probe's build output beside this test project's (same configuration).</summary>
    internal static string ProbePath()
    {
        var bin = AppContext.BaseDirectory; // …\tests\Dictum.Tests\bin\<Configuration>\<tfm>\
        var tfm = new DirectoryInfo(bin).Name;
        var configuration = new DirectoryInfo(bin).Parent!.Name;
        var tests = new DirectoryInfo(bin).Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(tests, "Dictum.UiProbe", "bin", configuration, tfm, "Dictum.UiProbe.exe");
    }

    [Fact]
    public void Showing_the_overlays_does_not_change_the_foreground_window()
    {
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 on an interactive desktop (shows windows for a few seconds).");
        var probe = ProbePath();
        Assert.True(File.Exists(probe), $"{probe} is missing: build tests\\Dictum.UiProbe");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(probe) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line) lines.Add(line);
        Assert.True(process.WaitForExit(60_000), "the probe did not finish");
        foreach (var line in lines) output.WriteLine(line);
        Assert.SkipWhen(process.ExitCode == 2, lines.LastOrDefault() ?? "the probe skipped");
        Assert.DoesNotContain(lines, l => l.StartsWith("FAIL", StringComparison.Ordinal));
        Assert.Equal(0, process.ExitCode);
        Assert.Contains(lines, l => l.StartsWith("PASS showing the pill left the foreground unchanged", StringComparison.Ordinal));
    }
}
