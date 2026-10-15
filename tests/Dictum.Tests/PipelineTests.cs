using Dictum.Audio;
using Dictum.Cli;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

public class PipelineTests
{
    private static CapturedAudio Speech(double seconds = 2) => new(new float[(int)(16000 * seconds)], 0.2f);

    private static (DictationPipeline, List<string> inserted, List<PillView> pills) Make(ITranscriber transcriber, TranscriptCleaner? cleaner = null, AppSettings? settings = null, ILog? log = null)
    {
        var inserted = new List<string>();
        var pills = new List<PillView>();
        var pipeline = new DictationPipeline(
            _ => Task.FromResult(transcriber),
            () => cleaner ?? TranscriptCleaner.RulesOnly("off"),
            (text, token) =>
            {
                token.ThrowIfCancellationRequested();
                inserted.Add(text);
                return Task.CompletedTask;
            },
            () => settings ?? new AppSettings(),
            log ?? NullLog.Instance);
        return (pipeline, inserted, pills);
    }

    [Fact]
    public async Task Transcript_is_inserted()
    {
        var (pipeline, inserted, pills) = Make(new FakeTranscriber("Hello world."));
        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(Speech(), CancellationToken.None, pills.Add));
        Assert.Equal(["Hello world."], inserted);
    }

    [Fact]
    public async Task Cancelled_during_transcription_inserts_nothing()
    {
        using var cts = new CancellationTokenSource();
        var (pipeline, inserted, pills) = Make(new FakeTranscriber("Hello world.", cts.Cancel)); // Esc lands mid-transcription
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(Speech(), cts.Token, pills.Add));
        Assert.Empty(inserted);
    }

    [Fact]
    public async Task Short_and_silent_recordings_are_discarded_without_transcribing()
    {
        var transcriber = new FakeTranscriber("ghost words");
        var (pipeline, inserted, pills) = Make(transcriber);
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(Speech(0.2), CancellationToken.None, pills.Add));
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(new CapturedAudio(new float[48000], 0.001f), CancellationToken.None, pills.Add));
        Assert.Equal(0, transcriber.Calls);
        Assert.Empty(inserted);
    }

    [Fact]
    public async Task Cleanup_shows_polishing_and_inserts_the_cleaned_text()
    {
        var cleaner = new TranscriptCleaner(FakeLlmClient.Replying("So the fox jumps."), CleanupPrompt.Default, TimeSpan.FromSeconds(1), NullLog.Instance, "fake");
        var (pipeline, inserted, pills) = Make(new FakeTranscriber("um so the fox uh jumps"), cleaner);
        await pipeline.RunAsync(Speech(), CancellationToken.None, pills.Add);
        Assert.Equal([PillView.Polishing], pills);
        Assert.Equal(["So the fox jumps."], inserted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transcript_text_reaches_the_log_only_when_log_transcripts_is_true(bool logTranscripts)
    {
        const string raw = "um the secret merger is with acme";
        const string cleaned = "The secret merger is with Acme.";
        var log = new RecordingLog();
        var cleaner = new TranscriptCleaner(FakeLlmClient.Replying(cleaned), CleanupPrompt.Default, TimeSpan.FromSeconds(1), log, "fake");
        var (pipeline, inserted, pills) = Make(new FakeTranscriber(raw), cleaner, new AppSettings { LogTranscripts = logTranscripts }, log);

        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(Speech(), CancellationToken.None, pills.Add));
        Assert.Equal([cleaned], inserted);
        Assert.NotEmpty(log.Lines); // timings and lengths are always logged
        var all = log.All;
        if (logTranscripts)
        {
            Assert.Contains($"Raw transcript: {raw}", all);
            Assert.Contains($"Cleaned transcript: {cleaned}", all);
        }
        else
        {
            Assert.DoesNotContain("merger", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("acme", all, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Empty_transcript_is_discarded()
    {
        var (pipeline, inserted, pills) = Make(new FakeTranscriber(""));
        Assert.Equal(ProcessingOutcome.Discarded, await pipeline.RunAsync(Speech(), CancellationToken.None, pills.Add));
        Assert.Empty(inserted);
    }
}

public class TranscriptTextTests
{
    [Theory]
    [InlineData("ggml_vulkan: Found 2 Vulkan devices:", 2)]   // verbatim from this machine's log, 15/10/2026
    [InlineData("ggml_vulkan: Found 1 Vulkan device:", 1)]
    [InlineData("ggml_vulkan: Found 0 Vulkan devices", 0)]
    public void Vulkan_device_count_is_read_from_the_native_log(string line, int expected) =>
        Assert.Equal(expected, WhisperEngine.ParseVulkanDeviceCount(line));

    [Fact]
    public void Unrelated_native_log_lines_are_ignored() =>
        Assert.Null(WhisperEngine.ParseVulkanDeviceCount("whisper_model_load:      Vulkan0 total size =   487.00 MB"));

    [Fact]
    public void Segments_are_joined_trimmed_and_collapsed()
    {
        Assert.Equal("Hello there. How are you?", TranscriptText.Normalise([" Hello   there.", "\n How are you? ", null, "  "]));
    }

    [Fact]
    public void Non_speech_annotations_are_dropped()
    {
        Assert.Equal("", TranscriptText.Normalise([" [BLANK_AUDIO]"]));
        Assert.Equal("Okay.", TranscriptText.Normalise(["(silence) Okay.", "[ Music ]"]));
    }
}

public class StartupRegistrationTests
{
    [Fact]
    public void Enable_writes_quoted_exe_path_and_disable_removes_it()
    {
        var key = new FakeRunKey();
        var reg = new RunKeyStartup(key, @"C:\Program Files\Dictum\Dictum.exe");
        Assert.False(reg.IsEnabled());
        reg.SetEnabled(true);
        Assert.Equal("\"C:\\Program Files\\Dictum\\Dictum.exe\"", key.Values["Dictum"]);
        Assert.True(reg.IsEnabled());
        reg.SetEnabled(false);
        Assert.Empty(key.Values);
        Assert.False(reg.IsEnabled());
    }

    [Fact]
    public void Entry_pointing_at_another_exe_reads_as_off()
    {
        var key = new FakeRunKey();
        key.Set("Dictum", "\"D:\\old\\Dictum.exe\"");
        Assert.False(new RunKeyStartup(key, @"C:\new\Dictum.exe").IsEnabled());
    }
}

public class CliOptionsTests
{
    [Fact]
    public void Parses_the_three_modes()
    {
        Assert.Equal(CliMode.Tray, CliOptions.Parse([]).Options!.Mode);
        var t = CliOptions.Parse(["--transcribe", "a.wav", "--no-llm", "--config", @"C:\cfg"]).Options!;
        Assert.Equal((CliMode.Transcribe, "a.wav", true, @"C:\cfg"), (t.Mode, t.WavPath, t.NoLlm, t.ConfigDir));
        Assert.Equal(CliMode.SelfTest, CliOptions.Parse(["--selftest"]).Options!.Mode);
    }

    [Fact]
    public void Parses_model_override_and_download()
    {
        var a = CliOptions.Parse(["--model", "base.en"]).Options!;
        Assert.Equal((CliMode.Tray, "base.en"), (a.Mode, a.Model));
        var b = CliOptions.Parse(["--selftest", "--model", @"D:\m\ggml-x.bin"]).Options!;
        Assert.Equal((CliMode.SelfTest, @"D:\m\ggml-x.bin"), (b.Mode, b.Model));
        var c = CliOptions.Parse(["--download-model", "tiny.en", "--config", @"C:\c"]).Options!;
        Assert.Equal((CliMode.DownloadModel, "tiny.en", @"C:\c"), (c.Mode, c.DownloadName, c.ConfigDir));
    }

    [Theory]
    [InlineData("--transcribe")]
    [InlineData("--bogus")]
    [InlineData("--no-llm")]
    [InlineData("--model")]
    [InlineData("--download-model")]
    public void Rejects_bad_arguments(string arg)
    {
        var (options, error) = CliOptions.Parse([arg]);
        Assert.Null(options);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("--model", "../evil")]
    [InlineData("--download-model", "..\\x")]
    [InlineData("--download-model", @"C:\abs\path.bin")]
    public void Rejects_unsafe_model_names(string flag, string value) => Assert.Null(CliOptions.Parse([flag, value]).Options);
}
