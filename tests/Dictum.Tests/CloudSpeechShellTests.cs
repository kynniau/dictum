using System.Net;
using System.Text;
using Avalonia.Threading;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;

namespace Dictum.Tests;

/// <summary>
/// Cloud speech in the shell (lens on b190186, 22/10/2026): the pipeline's transcriber seam really asks the cloud speech step
/// (F3), a settings reload and quitting retire it, and switching cloud clean-up off withdraws the clean-up consent only (F1).
/// The provider client keeps its real offline_only guard; a fake server stands in for the network under it.
/// </summary>
public sealed class CloudSpeechShellTests
{
    private const string SpeechHost = "speech.example.com";

    private static string Settings(string extra = "") => $$"""
        { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false },
          "speech": { "provider": "openai-compatible", "base_url": "https://{{SpeechHost}}/v1", "model": "s" }{{extra}} }
        """;

    private static void SpeechConsent(Scratch scratch) =>
        CloudConsentFile.WriteSpeech(scratch.Paths.ConsentFile, new CloudConsentRecord(SpeechSettings.OpenAiCompatible, SpeechHost, DateTimeOffset.UtcNow, "test"));

    private static void CleanupConsent(Scratch scratch) =>
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));

    /// <summary>Runs the UI thread's queued work until <paramref name="condition"/> holds.</summary>
    private static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>
    /// Pin F3 (mutation-checked): with the speech consent on file, one dictation through the started shell is one request to the
    /// speech service, and its text is what is pasted, not the local model's. Pointing the pipeline back at the local model
    /// (ModelManager.GetAsync) fails this.
    /// </summary>
    [Fact]
    public Task A_dictation_through_the_shell_goes_to_cloud_speech() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        SpeechConsent(scratch);
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin"); // the fake local engine transcribes every recording as this name
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var server = new SpeechServer();
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model), providerHandler: server);
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(shell.Speech.SendsAudio);
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            var key = platform.LastListener!;
            var t0 = Environment.TickCount64 + 60_000; // event times ahead of the controller's clock, as the clean-up shell tests do
            key.Post(new HookEvent(HotkeySignal.Down, t0));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            key.Post(new HookEvent(HotkeySignal.Up, t0 + 1_000));
            var cloud = DeterministicCleaner.Clean("Hello from the cloud.");
            Assert.True(Eventually(() => platform.Desktop.Ops.Contains($"set-text:{cloud} ")), "the cloud speech text was not pasted");
            Assert.Equal(1, server.Count);
            Assert.Equal($"https://{SpeechHost}/v1/audio/transcriptions", server.LastUri?.ToString());
            Assert.DoesNotContain(platform.Desktop.Ops, op => op.Contains("fake-model.bin", StringComparison.Ordinal)); // the local model did not hear it
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin (mutation-checked): a settings reload retires the speech step it replaces, so its request under way stops.</summary>
    [Fact]
    public Task A_reload_retires_the_speech_step_it_replaces() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        SpeechConsent(scratch);
        var (shell, _, _) = scratch.Shell(providerHandler: new SpeechServer());
        try
        {
            shell.Reload(userRequested: false);
            var before = shell.Speech;
            Assert.True(before.SendsAudio);
            Assert.False(before.IsRetired);
            shell.Reload(userRequested: false);
            Assert.NotSame(before, shell.Speech);
            Assert.True(before.IsRetired);
            Assert.False(shell.Speech.IsRetired);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin (mutation-checked): quitting retires the speech step before the models and the provider client are disposed.</summary>
    [Fact]
    public Task Quitting_retires_the_speech_step() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        SpeechConsent(scratch);
        var (shell, _, _) = scratch.Shell(providerHandler: new SpeechServer());
        shell.Reload(userRequested: false);
        var speech = shell.Speech;
        Assert.True(speech.SendsAudio);
        shell.Dispose();
        Assert.True(speech.IsRetired);
    });

    /// <summary>Pin F1 (mutation-checked): Switch cloud clean-up off withdraws the clean-up consent and keeps the speech consent.</summary>
    [Fact]
    public Task Switching_cloud_clean_up_off_keeps_the_speech_consent() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(""", "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" }"""));
        CleanupConsent(scratch);
        SpeechConsent(scratch);
        var (shell, _, _) = scratch.Shell(providerHandler: new SpeechServer());
        try
        {
            shell.Reload(userRequested: false);
            Assert.False(shell.NothingLeaves);
            Assert.Null(shell.DisableCloud());
            var consents = CloudConsentFile.ReadAll(scratch.Paths.ConsentFile);
            Assert.Null(consents.Cleanup);
            Assert.Equal(SpeechHost, consents.Speech?.Host);
            Assert.True(shell.Speech.SendsAudio); // cloud speech is still agreed to after the reload
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin F1 (mutation-checked): "Allow" undone because settings.json cannot be written withdraws the clean-up consent it just
    /// recorded and keeps the speech consent.
    /// </summary>
    [Fact]
    public Task Allow_undone_keeps_the_speech_consent() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        SpeechConsent(scratch);
        var (shell, _, dialogs) = scratch.Shell(providerHandler: new SpeechServer());
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            dialogs.Answers.Enqueue(0); // Allow cloud clean-up
            var said = shell.EnableCloud(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" });
            Assert.Equal(Dictum.App.ModelCoordinator.WriteFailed, said);
            var consents = CloudConsentFile.ReadAll(scratch.Paths.ConsentFile);
            Assert.Null(consents.Cleanup);
            Assert.Equal(SpeechHost, consents.Speech?.Host);
        }
        finally
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            shell.Dispose();
        }
    });

    /// <summary>Answers every request with one transcription; counts them and remembers where the last went.</summary>
    private sealed class SpeechServer : HttpMessageHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hello from the cloud."}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
