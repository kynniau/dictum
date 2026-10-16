using System.Net;
using System.Net.Sockets;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// Lens on 3e86363 (16/10/2026; Store Policies 10.5.2): with settings.json not valid JSON, "Switch cloud clean-up off" deleted
/// consent.json, the settings write was refused, nothing reloaded, and the page said "Cloud clean-up is off" while the clean-up in
/// use stayed openai-compatible/m at llm.example.com and both pages still said the text is sent. The switch now takes effect
/// either way and the page shows the write failure; "Allow" with a failed write records no consent; and a reload stops a clean-up
/// request still under way, so nothing goes out on settings the pages no longer show.
/// </summary>
public class CloudOffTakesEffectTests
{
    // The write path's own failure text (ModelCoordinator.WriteFailed), pinned word for word, independently of the app's copy.
    private const string WriteFailed = "settings.json could not be updated (it is not valid JSON, holds a key twice, or is read-only). Fix it, then choose again.";

    private const string CloudSettings = """{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" } }""";

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static T Named<T>(ILogical root, string name) where T : Control => All<T>(root).Single(c => AutomationProperties.GetName(c) == name);

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static List<string> VisibleLines(SettingsWindow window) => All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    /// <summary>Every visible line of a page, drawn afresh from the settings and the clean-up in use as they are now.</summary>
    private static List<string> PageLines(SettingsWindow window, string page)
    {
        window.ShowPage(page == "Privacy" ? "Clean-up" : "Privacy");
        window.ShowPage(page);
        return VisibleLines(window);
    }

    private static bool Shown(SettingsWindow window, string button) => All<Button>(window).Any(b => b.IsEffectivelyVisible && b.Content as string == button);

    /// <summary>
    /// settings.json made unwritable: not valid JSON (a closing brace lost; the reload the file watcher makes keeps the settings in
    /// use), or read-only (still readable).
    /// </summary>
    private static void MakeUnwritable(Scratch scratch, AppShell shell, bool readOnly)
    {
        if (readOnly)
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            return;
        }
        File.WriteAllText(scratch.Paths.SettingsFile, File.ReadAllText(scratch.Paths.SettingsFile).TrimEnd()[..^1]);
        shell.Reload(userRequested: false);
    }

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

    [Theory]
    [InlineData(false)] // not valid JSON
    [InlineData(true)] // read-only
    public Task Switching_cloud_clean_up_off_takes_effect_when_settings_json_cannot_be_written(bool readOnly) => UiSession.Run(() =>
    {
        using var scratch = new Scratch(CloudSettings);
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly);
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus); // on: each dictation goes to the host
            Assert.False(shell.NothingLeaves);
            var before = File.ReadAllText(scratch.Paths.SettingsFile);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            Click(window, "Switch cloud clean-up off");

            // The page says the write failed, never that cloud clean-up is off...
            var said = VisibleLines(window);
            Assert.Contains(WriteFailed, said);
            Assert.DoesNotContain("Cloud clean-up is off; the local model tidies your dictations again.", said);
            Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile)); // the write really was refused
            // ...and the switch took effect anyway: the consent is withdrawn and the clean-up in use runs on this PC.
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider);
            Assert.StartsWith("local/", shell.CleanupStatus);
            Assert.EndsWith(" on this PC (nothing leaves it)", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);

            // Both pages say so: the Clean-up card is in its off state and shows the local clean-up; Privacy says nothing leaves.
            var cleanUp = PageLines(window, "Clean-up");
            Assert.Contains(shell.CleanupStatus, cleanUp);
            Assert.DoesNotContain(cleanUp, l => l.StartsWith("On: ", StringComparison.Ordinal) || l.StartsWith("On, ", StringComparison.Ordinal));
            Assert.True(Shown(window, "Use cloud clean-up…"));
            Assert.False(Shown(window, "Switch cloud clean-up off"));
            var privacy = PageLines(window, "Privacy");
            Assert.Contains("Nothing leaves this PC: speech recognition and clean-up both run here.", privacy);
            Assert.DoesNotContain(privacy, l => l.Contains("llm.example.com", StringComparison.Ordinal));

            // settings.json fixed by hand, still naming the host: the consent stays withdrawn, so nothing is sent there.
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            shell.Reload(userRequested: false);
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider);
            Assert.True(shell.NothingLeaves);
            window.Close();
        }
        finally
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal); // so the scratch folder can be deleted
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // not valid JSON
    [InlineData(true)] // read-only
    public Task Allowing_cloud_clean_up_when_settings_json_cannot_be_written_records_no_consent_and_sends_nothing(bool readOnly) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            Named<ComboBox>(window, "Service").SelectedIndex = CloudPresets.All.ToList().FindIndex(p => p.BaseUrl is null) + 1; // the custom server
            Named<TextBox>(window, "Server address").Text = "https://llm.example.com/v1";
            Named<TextBox>(window, "Model").Text = "m";
            Dispatcher.UIThread.RunJobs(); // TextBox raises TextChanged through the dispatcher
            dialogs.Answers.Enqueue(0); // Allow
            Click(window, "Use cloud clean-up…");
            Assert.Equal("Send dictations to a cloud service?", Assert.Single(dialogs.Asked).Title); // asked, and agreed to

            var said = VisibleLines(window);
            Assert.Contains(WriteFailed, said);
            Assert.DoesNotContain("Cloud clean-up is on.", said);
            Assert.False(File.Exists(scratch.Paths.ConsentFile)); // no consent stands for a host the settings could not be switched to
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider);
            Assert.StartsWith("local/", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);

            // settings.json fixed by hand to name that host: no consent is on record, so nothing is sent there.
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            shell.Reload(userRequested: false);
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider); // repaired at load: llm.example.com was not agreed to
            Assert.True(shell.NothingLeaves);
            window.Close();
        }
        finally
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            shell.Dispose();
        }
    });

    [Fact]
    public async Task A_retired_cleaner_cancels_the_request_under_way_and_starts_no_other()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new HeldClient();
        try
        {
            var cleaner = new TranscriptCleaner(client, CleanupPrompt.Default, TimeSpan.FromSeconds(60), NullLog.Instance, "test");
            const string raw = "um the invoice went out today";
            var cleaning = cleaner.CleanAsync(raw, ct);
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            cleaner.Retire(); // what a settings reload does to the cleaner it replaces
            var result = await cleaning.WaitAsync(TimeSpan.FromSeconds(5), ct); // at once, not at the 60 s deadline, though the provider has not stopped
            Assert.Equal(new CleanupResult(DeterministicCleaner.Clean(raw), false, TranscriptCleaner.Reloaded), result);
            Assert.True(client.Token.IsCancellationRequested); // the provider's own token: it is told to stop, so an SDK retry is not made

            var later = await cleaner.CleanAsync(raw, ct); // a dictation that picked this cleaner up just before the reload
            Assert.Equal(1, client.Calls); // starts no request
            Assert.Equal(new CleanupResult(DeterministicCleaner.Clean(raw), false, TranscriptCleaner.Reloaded), later);
        }
        finally
        {
            client.Release();
        }
    }

    [Fact]
    public Task A_reload_during_a_clean_up_request_cancels_it_and_the_dictation_gets_the_rule_based_tidy() => UiSession.Run(() =>
    {
        // The lens (16/10/2026): the pipeline reads the cleaner once, so a request already sent ran on (with Anthropic's one retry)
        // after the pages changed. A server on this PC that never answers stands in for the cloud host: the request takes the same
        // path (OpenAiCompatibleLlmClient over the shell's HttpClient) with no consent step and no network.
        using var server = new SilentServer();
        using var scratch = new Scratch($$"""
            { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false },
              "llm": { "provider": "openai-compatible", "base_url": "http://127.0.0.1:{{server.Port}}/v1", "model": "m" } }
            """);
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin"); // the fake engine transcribes every recording as this name
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model));
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            Assert.False(shell.NothingLeaves); // the server tidies each dictation
            var key = platform.LastListener!;

            // A one-second hold. Event times a minute ahead of the controller's own clock, so its tick can expire nothing meanwhile.
            var t0 = Environment.TickCount64 + 60_000;
            key.Post(new HookEvent(HotkeySignal.Down, t0));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            key.Post(new HookEvent(HotkeySignal.Up, t0 + 1_000));
            Assert.True(Eventually(() => server.Received == 1), "the clean-up request did not reach the server");

            shell.DisableCloud(); // settings.json is written and the settings reload, with the request under way
            Assert.True(Eventually(() => server.Abandoned == 1), "the request under way ran on after the reload (it would have waited for the 15 s deadline)");
            var tidy = DeterministicCleaner.Clean("fake-model.bin");
            Assert.True(Eventually(() => platform.Desktop.Ops.Contains($"set-text:{tidy} ")), "the dictation was not pasted with the rule-based tidy");
            Assert.Equal(1, server.Received); // nothing was sent again
            Assert.True(shell.NothingLeaves);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// A provider whose request stays under way until the test releases it, even once its token is cancelled (as llama.cpp
    /// mid-prompt, or an SDK that does not stop at once); keeps the token of the first request.
    /// </summary>
    private sealed class HeldClient : ILlmClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public int Calls => Volatile.Read(ref _calls);
        public string Name => "held";

        public void Release() => _release.TrySetResult();

        public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1) Token = cancellationToken;
            Started.TrySetResult();
            await _release.Task;
            return new LlmReply("never", "end_turn");
        }
    }

    /// <summary>A server on this PC that reads each request and never answers it; counts the requests the client gave up on.</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _received;
        private int _abandoned;

        public SilentServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Received => Volatile.Read(ref _received);
        public int Abandoned => Volatile.Read(ref _abandoned);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HoldAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task HoldAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[64 * 1024];
                try
                {
                    if (await stream.ReadAsync(buffer, _stop.Token) == 0) return;
                    Interlocked.Increment(ref _received);
                    while (await stream.ReadAsync(buffer, _stop.Token) > 0)
                    {
                        // the rest of the request; no answer is ever written
                    }
                    Interlocked.Increment(ref _abandoned); // the client closed the connection
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref _abandoned); // the client reset it
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
