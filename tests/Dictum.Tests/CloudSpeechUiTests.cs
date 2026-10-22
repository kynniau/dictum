using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Cloud speech's user-facing half (spec "Cloud speech" §3-§4, UI lane, 22/10/2026): the speech consent step is the only way
/// the speech entry of consent.json is written, declining writes nothing, Switch cloud speech off withdraws that entry only,
/// and every surface (Settings > Models, the tray panel, the log, the pill) reads the speech step in use, including after a
/// failed request fell back to the speech model on this PC. The provider client keeps its real offline_only guard; a fake
/// server stands in for the network under it. No real key is used anywhere.
/// </summary>
public sealed class CloudSpeechUiTests
{
    private const string SpeechHost = "speech.example.com";
    private const string SpeechUrl = $"https://{SpeechHost}/v1";
    private const string CustomService = "Custom server (OpenAI-compatible)";

    // Approved by the product owner on 16/10/2026 as written: the tray panel's line while nothing leaves. Pinned word for word,
    // independently of the app's copy (as CloudWarningAndPresetsTests does).
    private const string ApprovedNothingLeaves = "Nothing leaves this PC";
    private const string ApprovedFlyoutCloud = "Cloud clean-up on · text leaves this PC";

    private static string Settings(string speech = "", string extra = "") => $$"""
        { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false }, "ui": { "view": "advanced" }{{speech}}{{extra}} }
        """;

    private static string SpeechAt(string baseUrl = SpeechUrl, string model = "speech-model") =>
        $$""", "speech": { "provider": "openai-compatible", "base_url": "{{baseUrl}}", "model": "{{model}}" }""";

    private static void SpeechConsent(Scratch scratch, string host = SpeechHost) =>
        CloudConsentFile.WriteSpeech(scratch.Paths.ConsentFile, new CloudConsentRecord(SpeechSettings.OpenAiCompatible, host, DateTimeOffset.UtcNow, "test"));

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static T Named<T>(ILogical root, string name) where T : Control => All<T>(root).Single(c => AutomationProperties.GetName(c) == name);

    private static bool Shown(ILogical root, string text) => All<TextBlock>(root).Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static bool ButtonShown(ILogical root, string label) => All<Button>(root).Any(b => b.IsEffectivelyVisible && b.Content as string == label);

    // Approved by the product owner as written: Settings > Privacy's default summary and the Clean-up card's last sentence.
    // Pinned byte for byte, independently of the app's copy.
    private const string ApprovedPrivacyDefault = "Nothing leaves this PC: speech recognition and clean-up both run here.";
    private const string ApprovedAudioSentence = "Audio never leaves this PC.";

    // Approved by the owner 22/10/2026 (16:22-16:31 AEDT), verbatim: the replacement for "Audio never leaves this PC." while
    // cloud speech sends audio, and the caption above Privacy's clean-up line. Pinned byte for byte, independently of the app.
    private const string ApprovedReplacement = "Audio never leaves this PC by default, though advanced users can add their own cloud speech and clean-up services in settings.";
    private const string ApprovedCleanupCaption = "Cloud clean-up:";

    private const string ApprovedIntro = "Optional. A cloud model can tidy longer dictations well, but the text of every dictation then goes to that service. Audio never leaves this PC.";
    private const string ReplacedIntro = "Optional. A cloud model can tidy longer dictations well, but the text of every dictation then goes to that service. " + ApprovedReplacement;
    private const string ApprovedCleanupSentence = "The text of each dictation goes to llm.example.com for clean-up, because you switched cloud clean-up on. Audio never leaves this PC. " +
                                                   "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it.";
    private const string ReplacedCleanupSentence = "The text of each dictation goes to llm.example.com for clean-up, because you switched cloud clean-up on. " + ApprovedReplacement + " " +
                                                   "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it.";

    /// <summary>Every visible line of a Settings page, drawn afresh from the settings and the steps in use.</summary>
    private static IReadOnlyList<string> PageLines(AppShell shell, string page)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage(page);
        var lines = All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        window.Close();
        return lines;
    }

    /// <summary>The About box's runtime lines, one per line.</summary>
    private static IReadOnlyList<string> AboutLines(AppShell shell) => [.. PageLines(shell, "About").SelectMany(t => t.Split('\n'))];

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.IsEffectivelyVisible && b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Settings > Models drawn afresh from the settings and the speech step in use.</summary>
    private static SettingsWindow Models(AppShell shell)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        return window;
    }

    /// <summary>Fills the Cloud speech card for a custom server, as a user would.</summary>
    private static void FillCustom(SettingsWindow window, string address = SpeechUrl, string model = "speech-model")
    {
        var service = Named<ComboBox>(window, "Service");
        service.SelectedIndex = SpeechPresets.All.ToList().FindIndex(p => p.Name == CustomService);
        Named<TextBox>(window, "Server address").Text = address;
        Named<TextBox>(window, "Model").Text = model;
    }

    /// <summary>A shell over the scratch profile whose dialogs also record each question's buttons.</summary>
    private static (AppShell Shell, FakePlatform Platform, ChoiceDialogs Dialogs) Shell(Scratch scratch, HttpMessageHandler? server = null, ILog? log = null,
        ModelManager? models = null, ShellOptions? options = null)
    {
        var platform = new FakePlatform(scratch.Paths);
        var dialogs = new ChoiceDialogs();
        var shell = new AppShell(platform, scratch.Paths, log ?? NullLog.Instance, new SecretRedactor(), options ?? new ShellOptions(), dialogs,
            gpuMachine: () => false, speechModels: models, providerHandler: server ?? new SpeechServer(HttpStatusCode.OK));
        return (shell, platform, dialogs);
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

    /// <summary>
    /// Pin 1 (mutation-checked): with no speech consent, "Use cloud speech…" brings up the speech consent step with the approved
    /// text and buttons (decline is the default), and declining records nothing, writes nothing to settings.json and sends nothing.
    /// </summary>
    [Fact]
    public Task Declining_the_speech_consent_step_records_nothing_and_sends_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        var server = new SpeechServer(HttpStatusCode.OK);
        var (shell, _, dialogs) = Shell(scratch, server);
        try
        {
            shell.Reload(userRequested: false);
            var before = File.ReadAllText(scratch.Paths.SettingsFile);
            var window = Models(shell);
            Assert.True(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            FillCustom(window);
            dialogs.Answers.Enqueue(1); // Keep speech on this PC
            Click(window, ModelsPage.UseCloudSpeechLabel);

            var (title, text, choices) = Assert.Single(dialogs.Questions);
            Assert.Equal(AppShell.SpeechConsentTitle, title);
            // The approved consent text (owner 22/10/2026, 10:50 AEDT; last paragraph's clause revised 16:31 AEDT), byte for byte.
            Assert.Equal(Encoding.UTF8.GetBytes(
                "With cloud speech on, the recording of every dictation, your voice, is sent over the internet to speech.example.com to be turned into text, together with the words in your personal dictionary as a hint. It uses your own API key (SPEECH_API_KEY).\n\n" +
                "Dictum encrypts the connection (HTTPS) but cannot see, check or certify what speech.example.com keeps, for how long, how it protects it, or who else can listen to it. By switching cloud speech on you accept responsibility for the security and privacy of the speech sent there. Check that service's terms yourself, and do not dictate confidential, personal or client information with cloud speech on unless you have.\n\n" +
                "Nothing is sent until you agree. You can switch it off at any time in Settings > Models, and offline only mode refuses any service that is not on this PC; your speech model on this PC is used again."),
                Encoding.UTF8.GetBytes(text));
            Assert.Equal([SpeechPolicy.AgreeButton(SpeechHost), SpeechPolicy.DeclineButton], choices.Select(c => c.Label));
            Assert.True(choices[1].IsDefault); // Enter keeps speech on this PC
            Assert.DoesNotContain(dialogs.Inner.Asked, a => a.Title == "Send dictations to a cloud service?"); // not the clean-up dialog

            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile));
            Assert.Equal(SpeechSettings.Local, shell.Settings.Speech.Provider);
            Assert.False(shell.SpeechSendsAudio);
            Assert.Equal(SpeechStatus.Local, shell.SpeechStatusLine);
            Assert.Equal(0, server.Count);
            Assert.True(Shown(window, AppShell.SpeechNotSwitchedOn));
            Assert.True(ButtonShown(window, ModelsPage.UseCloudSpeechLabel)); // still off
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin 1, second half: speech.* written into settings.json by hand never switches it on; the card says nothing is agreed to.</summary>
    [Fact]
    public Task Settings_json_alone_never_switches_cloud_speech_on() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        var server = new SpeechServer(HttpStatusCode.OK);
        var (shell, _, dialogs) = Shell(scratch, server);
        try
        {
            shell.Reload(userRequested: false);
            Assert.False(shell.CloudSpeechOn);
            Assert.False(shell.SpeechSendsAudio);
            var window = Models(shell);
            Assert.True(Shown(window, SpeechStatus.NotAgreed));
            Assert.True(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            Assert.False(ButtonShown(window, ModelsPage.CloudSpeechOffLabel));
            Assert.Empty(dialogs.Questions);
            Assert.Equal(0, server.Count);
            window.Close();
            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.True(Shown(flyout, ApprovedNothingLeaves));
            flyout.Close();
            Assert.Contains(ApprovedPrivacyDefault, PageLines(shell, "Privacy"));
            Assert.DoesNotContain(AboutLines(shell), l => l.StartsWith("Speech: cloud", StringComparison.Ordinal));
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin 2: agreeing writes the speech entry of consent.json (a clean-up entry is not written) and speech.* to settings.json;
    /// Settings > Models, the tray panel, the log and the pill then show the on state, and "Nothing leaves this PC" is gone.
    /// </summary>
    [Fact]
    public Task Agreeing_records_the_speech_consent_and_every_surface_shows_cloud_speech_on() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        var log = new RecordingLog();
        var (shell, _, dialogs) = Shell(scratch, log: log);
        try
        {
            shell.Reload(userRequested: false);
            var window = Models(shell);
            FillCustom(window);
            dialogs.Answers.Enqueue(0); // Send my voice to speech.example.com
            Click(window, ModelsPage.UseCloudSpeechLabel);

            var consents = CloudConsentFile.ReadAll(scratch.Paths.ConsentFile);
            Assert.Null(consents.Cleanup);
            Assert.Equal((SpeechSettings.OpenAiCompatible, SpeechHost), (consents.Speech?.Provider, consents.Speech?.Host));
            var saved = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Speech;
            Assert.Equal((SpeechSettings.OpenAiCompatible, SpeechUrl, "speech-model"), (saved.Provider, saved.BaseUrl, saved.Model));
            Assert.True(shell.SpeechSendsAudio);
            Assert.True(Shown(window, ModelsPage.CloudSpeechSwitchedOn));

            window.ShowPage("Privacy");
            window.ShowPage("Models");
            Assert.True(Shown(window, SpeechStatus.On(SpeechHost, "speech-model")));
            Assert.True(ButtonShown(window, ModelsPage.CloudSpeechOffLabel));
            Assert.False(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            window.Close();

            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.Equal(1, All<TextBlock>(flyout).Count(t => t.IsEffectivelyVisible && t.Text == AppShell.SpeechTrayLine(SpeechHost))); // once, in the footer
            Assert.StartsWith(shell.Models.CurrentName, Named<ComboBox>(flyout, "Speech model").SelectedItem as string); // the picker keeps the model's name
            Assert.False(Shown(flyout, ApprovedNothingLeaves));
            flyout.Close();

            var privacy = PageLines(shell, "Privacy");
            Assert.Contains(AppShell.SpeechPrivacyLine(SpeechHost), privacy);
            Assert.DoesNotContain(ApprovedPrivacyDefault, privacy); // untrue now: replaced, not reworded
            Assert.Contains(AppShell.SpeechAboutLine(SpeechHost, "speech-model", shell.Models.CurrentName), AboutLines(shell));
            Assert.DoesNotContain(AppShell.CloudCleanupCaption, privacy); // clean-up sends nothing: no clean-up line to caption
            Assert.Contains(ReplacedIntro, PageLines(shell, "Clean-up"));
            Assert.DoesNotContain(ApprovedIntro, PageLines(shell, "Clean-up"));

            var agreed = CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech!.AgreedAt;
            Assert.Contains(CloudSpeech.LoadLine(SpeechHost, "speech-model", agreed, shell.Models.CurrentName), log.All);
            Assert.Contains($"; consent recorded {agreed.ToLocalTime():yyyy-MM-dd}; ", log.All);
            Assert.Equal(AppShell.HearingCloudPill, shell.PillLabel(PillView.Transcribing));
            Assert.Null(shell.PillLabel(PillView.Done)); // no request has failed
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin 3 (mutation-checked): Switch cloud speech off withdraws the speech consent only, so a clean-up consent on file stays;
    /// settings.json goes back to the speech model on this PC, and the card and the tray panel return to their default.
    /// </summary>
    [Fact]
    public Task Switching_cloud_speech_off_withdraws_only_the_speech_consent() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt(), """, "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" }"""));
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.True(shell.SpeechSendsAudio);
            // Both on: the speech line, the "Cloud clean-up:" caption, then the clean-up sentence with the owner's replacement.
            var both = PageLines(shell, "Privacy");
            Assert.Contains(AppShell.SpeechPrivacyLine(SpeechHost), both);
            Assert.Contains(ApprovedCleanupCaption, both);
            Assert.Contains(ReplacedCleanupSentence, both);
            Assert.DoesNotContain(ApprovedCleanupSentence, both);
            var window = Models(shell);
            Click(window, ModelsPage.CloudSpeechOffLabel);

            var consents = CloudConsentFile.ReadAll(scratch.Paths.ConsentFile);
            Assert.Null(consents.Speech);
            Assert.Equal("llm.example.com", consents.Cleanup?.Host); // the clean-up consent is untouched
            Assert.Equal(SpeechSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Speech.Provider);
            Assert.False(shell.SpeechSendsAudio);
            Assert.Equal(SpeechStatus.Local, shell.SpeechStatusLine);
            Assert.False(shell.NothingLeaves); // cloud clean-up is still on
            Assert.True(Shown(window, ModelsPage.CloudSpeechSwitchedOff));
            window.ShowPage("Privacy");
            window.ShowPage("Models");
            Assert.True(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            Assert.False(ButtonShown(window, ModelsPage.CloudSpeechOffLabel));
            window.Close();

            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.False(Shown(flyout, AppShell.SpeechTrayLine(SpeechHost)));
            Assert.True(Shown(flyout, ApprovedFlyoutCloud));
            flyout.Close();
            Assert.Null(shell.PillLabel(PillView.Transcribing));
            // Back to the clean-up-only default: the approved Privacy sentence, byte for byte, and no speech line on About.
            var privacy = PageLines(shell, "Privacy");
            Assert.DoesNotContain(AppShell.SpeechPrivacyLine(SpeechHost), privacy);
            Assert.Contains(ApprovedCleanupSentence, privacy);
            Assert.DoesNotContain(ReplacedCleanupSentence, privacy);
            Assert.DoesNotContain(ApprovedCleanupCaption, privacy);
            Assert.DoesNotContain(AboutLines(shell), l => l.StartsWith("Speech: ", StringComparison.Ordinal));
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin 3, the default: with cloud speech alone switched off, the tray panel says "Nothing leaves this PC" again, beside its lock.</summary>
    [Fact]
    public Task Switching_cloud_speech_off_brings_back_nothing_leaves_this_PC() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            var on = new FlyoutWindow(shell);
            on.ShowNear(null);
            Assert.False(Shown(on, ApprovedNothingLeaves));
            on.Close();
            Assert.Null(shell.DisableCloudSpeech());
            Assert.False(File.Exists(scratch.Paths.ConsentFile)); // nothing else was on file
            var off = new FlyoutWindow(shell);
            off.ShowNear(null);
            Assert.True(Shown(off, ApprovedNothingLeaves));
            Assert.False(Shown(off, AppShell.SpeechTrayLine(SpeechHost)));
            off.Close();
            var privacy = PageLines(shell, "Privacy");
            Assert.Contains(ApprovedPrivacyDefault, privacy);
            Assert.DoesNotContain(AppShell.SpeechPrivacyLine(SpeechHost), privacy);
            Assert.Contains(ApprovedIntro, PageLines(shell, "Clean-up"));
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin 3, saving: while consent.json cannot be changed the switch-off holds (nothing is sent) and the page says so.</summary>
    [Fact]
    public Task A_switch_off_that_cannot_be_saved_holds_for_the_session() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.True(shell.SpeechSendsAudio);
            using (new FileStream(scratch.Paths.ConsentFile, FileMode.Open, FileAccess.Read, FileShare.Read)) // held open: it cannot be deleted
            {
                var said = shell.DisableCloudSpeech();
                Assert.NotNull(said);
                Assert.StartsWith("Cloud speech is off, but consent.json could not be changed (", said);
                Assert.False(shell.SpeechSendsAudio);
                shell.Reload(userRequested: false);
                Assert.False(shell.SpeechSendsAudio); // still held: the consent on file is not used
            }
            shell.Reload(userRequested: false); // the next reload saves it
            Assert.Null(CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech);
            Assert.False(shell.SpeechSendsAudio);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin 4: agreed to, but the key is not set: the card says the approved waiting line and nothing leaves this PC.</summary>
    [Fact]
    public Task Without_the_key_the_card_shows_the_waiting_line_and_nothing_is_sent()
    {
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SecretStore.GroqKey)), "GROQ_API_KEY is set in this machine's environment, which wins over .env.");
        return UiSession.Run(() =>
        {
            const string groq = "https://api.groq.com/openai/v1";
            using var scratch = new Scratch(Settings(SpeechAt(groq)));
            SpeechConsent(scratch, "api.groq.com");
            var server = new SpeechServer(HttpStatusCode.OK);
            var (shell, _, _) = Shell(scratch, server);
            try
            {
                shell.Reload(userRequested: false);
                Assert.True(shell.CloudSpeechOn);
                Assert.False(shell.SpeechSendsAudio);
                var window = Models(shell);
                // Clean-up's approved line, word for word (CloudWarningAndPresetsTests pins it independently too).
                Assert.True(Shown(window, "On, but waiting for your GROQ_API_KEY: nothing is sent until you add it to .env."));
                Assert.True(ButtonShown(window, ModelsPage.CloudSpeechOffLabel));
                window.Close();
                var flyout = new FlyoutWindow(shell);
                flyout.ShowNear(null);
                Assert.True(Shown(flyout, ApprovedNothingLeaves));
                Assert.False(Shown(flyout, AppShell.SpeechTrayLine("api.groq.com")));
                flyout.Close();
                Assert.Null(shell.PillLabel(PillView.Transcribing));
                const string waiting = "On, but waiting for your GROQ_API_KEY: nothing is sent until you add it to .env.";
                var privacy = PageLines(shell, "Privacy");
                Assert.Contains(waiting, privacy);
                Assert.Contains(ApprovedPrivacyDefault, privacy); // true: nothing is sent
                Assert.Contains($"Speech: {waiting}", AboutLines(shell));
                Assert.Equal(0, server.Count);
            }
            finally
            {
                shell.Dispose();
            }
        });
    }

    /// <summary>Pin 4: agreed to, but offline only is on: the offline line; and switching on from the card is refused before any dialog.</summary>
    [Fact]
    public Task With_offline_only_on_the_card_shows_the_offline_line_and_switching_on_is_refused() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt(), """, "offline_only": true"""));
        SpeechConsent(scratch);
        var (shell, _, dialogs) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.False(shell.SpeechSendsAudio);
            var window = Models(shell);
            const string offline = "On, but offline only is on: nothing is sent until you switch offline only off.";
            Assert.True(Shown(window, offline));
            window.Close();
            Assert.Contains(offline, PageLines(shell, "Privacy"));
            Assert.Contains($"Speech: {offline}", AboutLines(shell));
            Assert.Equal(AppShell.SpeechOfflineRefused,
                shell.EnableCloudSpeech(new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = "https://other.example.com/v1", Model = "m" }));
            Assert.Empty(dialogs.Questions);
            Assert.Equal(SpeechHost, CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech?.Host); // unchanged
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin 4: with offline only on and cloud speech off, the card's button is disabled and says why.</summary>
    [Fact]
    public Task With_offline_only_on_the_card_cannot_switch_cloud_speech_on() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(extra: """, "offline_only": true"""));
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            var window = Models(shell);
            Assert.False(All<Button>(window).Single(b => b.Content as string == ModelsPage.UseCloudSpeechLabel).IsEnabled);
            Assert.True(Shown(window, ModelsPage.CloudSpeechOfflineNote));
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin 5: a dictation whose request fails is heard by the speech model on this PC; the log names the reason, and the card,
    /// the tray panel and the pill show the failed and fallback lines until the next success.
    /// </summary>
    [Fact]
    public Task After_a_failed_request_every_surface_shows_the_fallback() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        SpeechConsent(scratch);
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin"); // the fake local engine transcribes every recording as this name
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var log = new RecordingLog();
        var server = new SpeechServer(HttpStatusCode.InternalServerError);
        var (shell, platform, _) = Shell(scratch, server, log, models, new ShellOptions(ModelOverride: model));
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(shell.SpeechSendsAudio);
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            var key = platform.LastListener!;
            var t0 = Environment.TickCount64 + 60_000; // event times ahead of the controller's clock, as the shell tests do
            key.Post(new HookEvent(HotkeySignal.Down, t0));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            key.Post(new HookEvent(HotkeySignal.Up, t0 + 1_000));
            Assert.True(Eventually(() => platform.Desktop.Ops.Any(op => op.StartsWith("set-text:", StringComparison.Ordinal) && op.Contains("fake-model", StringComparison.OrdinalIgnoreCase))),
                "the local model's text was not pasted");
            Assert.Equal(1, server.Count);

            const string reason = $"HTTP 500 from {SpeechHost}";
            Assert.Contains($"Cloud speech via {SpeechHost} not used, the local model heard this dictation instead: {reason}.", log.All);
            Assert.Equal(SpeechStatus.Failed(reason), shell.SpeechStatusLine);
            Assert.Equal(SpeechStatus.FallbackPill, shell.PillLabel(PillView.Done));
            var window = Models(shell);
            Assert.True(Shown(window, SpeechStatus.Failed(reason)));
            window.Close();
            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.True(Shown(flyout, $"{AppShell.SpeechTrayLine(SpeechHost)}\n{SpeechStatus.Failed(reason)}"));
            flyout.Close();
            Assert.Contains($"{AppShell.SpeechPrivacyLine(SpeechHost)}\n{SpeechStatus.Failed(reason)}", PageLines(shell, "Privacy"));
            Assert.Contains($"Speech: {SpeechStatus.Failed(reason)}.", AboutLines(shell));
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin 6 (owner, 22/10/2026): with cloud speech off, the Simple view shows nothing of the Cloud speech card; Advanced shows its setup.</summary>
    [Fact]
    public Task The_simple_view_shows_nothing_of_the_card_while_cloud_speech_is_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "ui": { "view": "simple" } }""");
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            var window = Models(shell);
            Assert.False(Shown(window, ModelsPage.CloudSpeechTitle.ToUpperInvariant()));
            Assert.False(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            Assert.False(ButtonShown(window, ModelsPage.CloudSpeechOffLabel));
            Assert.False(Shown(window, ModelsPage.CloudSpeechIntro));
            Assert.DoesNotContain(All<ComboBox>(window), c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "Service");
            Assert.True(shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced))));
            window.ShowPage("Privacy");
            window.ShowPage("Models");
            Assert.True(Shown(window, ModelsPage.CloudSpeechTitle.ToUpperInvariant()));
            Assert.True(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin 6 (owner, 22/10/2026): with cloud speech on, the Simple view shows the card's state line and "Switch cloud speech off";
    /// the setup fields (service, address, model, key line) stay Advanced-only. The off switch works from Simple.
    /// </summary>
    [Fact]
    public Task The_simple_view_shows_the_state_line_and_off_switch_while_cloud_speech_is_on() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "simple" }{{SpeechAt()}} }""");
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.True(shell.SpeechSendsAudio);
            var window = Models(shell);
            Assert.True(Shown(window, ModelsPage.CloudSpeechTitle.ToUpperInvariant()));
            Assert.True(Shown(window, SpeechStatus.On(SpeechHost, "speech-model")));
            Assert.True(ButtonShown(window, ModelsPage.CloudSpeechOffLabel));
            Assert.False(ButtonShown(window, ModelsPage.UseCloudSpeechLabel));
            Assert.False(Shown(window, ModelsPage.CloudSpeechIntro));
            Assert.DoesNotContain(All<ComboBox>(window), c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "Service");
            Assert.DoesNotContain(All<TextBox>(window), t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) is "Server address" or "Model");
            Assert.DoesNotContain(All<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text?.Contains(".env in the settings folder", StringComparison.Ordinal) == true);
            Click(window, ModelsPage.CloudSpeechOffLabel);
            Assert.False(shell.SpeechSendsAudio);
            Assert.Null(CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech);
            window.ShowPage("Privacy");
            window.ShowPage("Models");
            Assert.False(Shown(window, ModelsPage.CloudSpeechTitle.ToUpperInvariant())); // off again: nothing of the card in Simple
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin 7 (mutation-checked): the approved default sentences stay byte for byte, compared with literal copies: the tray panel's
    /// "Nothing leaves this PC", and Settings > Privacy's default summary, which still shows while cloud speech is off.
    /// </summary>
    [Fact]
    public Task The_approved_default_sentences_are_unchanged() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.True(Shown(flyout, "Nothing leaves this PC"));
            flyout.Close();
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Privacy");
            Assert.True(Shown(window, "Nothing leaves this PC: speech recognition and clean-up both run here."));
            window.ShowPage("Clean-up");
            Assert.True(Shown(window, "Optional. A cloud model can tidy longer dictations well, but the text of every dictation then goes to that service. Audio never leaves this PC."));
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin 7, the owner's replacement (22/10/2026, 16:27 AEDT; mutation-checked): in both places that say "Audio never leaves this
    /// PC." (the Clean-up card's introduction and Privacy's cloud clean-up sentence), the approved replacement shows, byte for
    /// byte, only while cloud speech sends audio, with the "Cloud clean-up:" caption above Privacy's clean-up line; switched off,
    /// both approved originals are back byte for byte and the caption is gone.
    /// </summary>
    [Fact]
    public Task The_owner_replacement_sentence_shows_only_while_cloud_speech_sends_audio() => UiSession.Run(() =>
    {
        // The introduction is on the card's off state, so it is read with cloud clean-up off; Privacy's sentence with it on.
        using var speechOnly = new Scratch(Settings(SpeechAt()));
        SpeechConsent(speechOnly);
        var (first, _, _) = Shell(speechOnly);
        try
        {
            first.Reload(userRequested: false);
            Assert.Equal(Encoding.UTF8.GetBytes(ReplacedIntro), Encoding.UTF8.GetBytes(PageLines(first, "Clean-up").Single(l => l.StartsWith("Optional. A cloud model", StringComparison.Ordinal))));
            Assert.Null(first.DisableCloudSpeech());
            Assert.Equal(Encoding.UTF8.GetBytes(ApprovedIntro), Encoding.UTF8.GetBytes(PageLines(first, "Clean-up").Single(l => l.StartsWith("Optional. A cloud model", StringComparison.Ordinal))));
        }
        finally
        {
            first.Dispose();
        }

        using var both = new Scratch(Settings(SpeechAt(), """, "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" }"""));
        CloudConsentFile.Write(both.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        SpeechConsent(both);
        var (second, _, _) = Shell(both);
        try
        {
            second.Reload(userRequested: false);
            var on = PageLines(second, "Privacy");
            var at = on.ToList().IndexOf(ApprovedCleanupCaption);
            Assert.True(at >= 0, "the caption is not shown");
            Assert.Equal(Encoding.UTF8.GetBytes(ReplacedCleanupSentence), Encoding.UTF8.GetBytes(on[at + 1])); // straight under its caption
            Assert.Null(second.DisableCloudSpeech());
            var off = PageLines(second, "Privacy");
            Assert.Equal(Encoding.UTF8.GetBytes(ApprovedCleanupSentence), Encoding.UTF8.GetBytes(off.Single(l => l.StartsWith("The text of each dictation goes to", StringComparison.Ordinal))));
            Assert.DoesNotContain(ApprovedCleanupCaption, off);
        }
        finally
        {
            second.Dispose();
        }
    });

    /// <summary>Pin 7, cloud clean-up alone on: Settings > Privacy's approved clean-up sentence, byte for byte, with cloud speech off.</summary>
    [Fact]
    public Task The_approved_clean_up_privacy_sentence_is_unchanged_while_cloud_speech_is_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(extra: """, "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" }"""));
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.Contains("The text of each dictation goes to llm.example.com for clean-up, because you switched cloud clean-up on. Audio never leaves this PC. " +
                            "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it.", PageLines(shell, "Privacy"));
        }
        finally
        {
            shell.Dispose();
        }
    });

    // Lens on PR #54 (22/10/2026): the files that may not be written. Each pin below runs the shell's own paths against a
    // settings.json marked read-only and/or a consent.json another program holds open (FileShare.Read: it can be read, not
    // deleted or replaced), as CloudOffTakesEffectTests does for clean-up.

    private static SpeechSettings Custom => new() { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = SpeechUrl, Model = "speech-model" };

    private static FileStream HoldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static void Writable(string path)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
    }

    /// <summary>L1: settings.json read-only when agreeing: the write-failed sentence, the speech consent withdrawn again, settings.json unchanged, nothing sent.</summary>
    [Fact]
    public Task Agreeing_while_settings_json_cannot_be_written_leaves_no_consent_and_sends_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        var server = new SpeechServer(HttpStatusCode.OK);
        var (shell, _, dialogs) = Shell(scratch, server);
        try
        {
            shell.Reload(userRequested: false);
            var before = File.ReadAllText(scratch.Paths.SettingsFile);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            dialogs.Answers.Enqueue(0); // Send my voice
            Assert.Equal(ModelCoordinator.WriteFailed, shell.EnableCloudSpeech(Custom));
            Assert.False(File.Exists(scratch.Paths.ConsentFile)); // recorded, then withdrawn again: nothing else was on file
            Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile));
            shell.Reload(userRequested: false);
            Assert.False(shell.SpeechSendsAudio);
            Assert.Equal(0, server.Count);
        }
        finally
        {
            Writable(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    /// <summary>
    /// L2 (mutation-checked): the write fails and consent.json is held open from the moment the agreement is recorded, so it cannot
    /// be withdrawn again: it stays on file but unused, also after settings.json is edited by hand to that host; once it is let
    /// go, the next reload withdraws it.
    /// </summary>
    [Fact]
    public Task A_consent_that_cannot_be_withdrawn_again_stays_unused_until_it_is() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        FileStream? held = null;
        var log = new HookLog(message =>
        {
            if (message.StartsWith("Cloud speech agreed to", StringComparison.Ordinal)) held = HoldOpen(scratch.Paths.ConsentFile);
        });
        var server = new SpeechServer(HttpStatusCode.OK);
        var (shell, _, dialogs) = Shell(scratch, server, log);
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            dialogs.Answers.Enqueue(0); // Send my voice
            Assert.Equal(ModelCoordinator.WriteFailed, shell.EnableCloudSpeech(Custom));
            Assert.NotNull(held);
            Assert.Equal(SpeechHost, CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech?.Host); // still on file
            Assert.False(shell.SpeechSendsAudio);

            Writable(scratch.Paths.SettingsFile);
            File.WriteAllText(scratch.Paths.SettingsFile, Settings(SpeechAt())); // edited by hand to the host on file
            shell.Reload(userRequested: false);
            Assert.Equal(SpeechHost, CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech?.Host);
            Assert.False(shell.SpeechSendsAudio); // the consent on file is not used
            Assert.Equal(0, server.Count);

            held!.Dispose();
            held = null;
            shell.Reload(userRequested: false);
            Assert.Null(CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech); // withdrawn at the next reload
            Assert.False(shell.SpeechSendsAudio);
        }
        finally
        {
            held?.Dispose();
            Writable(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    /// <summary>L3 (mutation-checked): a clean-up consent on file survives an agreement whose settings.json write fails.</summary>
    [Fact]
    public Task A_failed_switch_on_keeps_the_clean_up_consent() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings());
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, dialogs) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            dialogs.Answers.Enqueue(0); // Send my voice
            Assert.Equal(ModelCoordinator.WriteFailed, shell.EnableCloudSpeech(Custom));
            var consents = CloudConsentFile.ReadAll(scratch.Paths.ConsentFile);
            Assert.Equal("llm.example.com", consents.Cleanup?.Host);
            Assert.Null(consents.Speech);
        }
        finally
        {
            Writable(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    /// <summary>L4 (mutation-checked): switching off with settings.json read-only says so; the next start finds no consent and sends nothing.</summary>
    [Fact]
    public Task A_switch_off_whose_settings_cannot_be_saved_leaves_nothing_agreed_at_the_next_start() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.True(shell.SpeechSendsAudio);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            var said = shell.DisableCloudSpeech();
            Assert.StartsWith("Cloud speech is off for this session, but settings.json could not be saved (", said);
            Assert.EndsWith("). Nothing is sent. Dictum tries again at the next reload and when it quits.", said);
            Assert.False(shell.SpeechSendsAudio);
        }
        finally
        {
            shell.Dispose(); // the last try at quit fails too: settings.json still names the service
        }
        var server = new SpeechServer(HttpStatusCode.OK);
        var (next, _, _) = Shell(scratch, server);
        try
        {
            next.Reload(userRequested: false);
            Assert.Equal(SpeechSettings.OpenAiCompatible, next.Settings.Speech.Provider);
            Assert.Equal(SpeechStatus.NotAgreed, next.SpeechStatusLine);
            Assert.False(next.SpeechSendsAudio);
            var window = Models(next);
            Assert.True(Shown(window, SpeechStatus.NotAgreed));
            window.Close();
            Assert.Equal(0, server.Count);
        }
        finally
        {
            Writable(scratch.Paths.SettingsFile);
            next.Dispose();
        }
    });

    /// <summary>L8 (mutation-checked): switching off with both files blocked holds; both let go before quitting, quitting saves it.</summary>
    [Fact]
    public Task A_switch_off_with_both_files_blocked_is_saved_at_quit() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        var held = HoldOpen(scratch.Paths.ConsentFile);
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            var said = shell.DisableCloudSpeech();
            Assert.StartsWith("Cloud speech is off for this session, but consent.json could not be changed (", said);
            Assert.Contains(") and settings.json could not be saved (", said);
            Assert.False(shell.SpeechSendsAudio);
            held.Dispose();
            Writable(scratch.Paths.SettingsFile);
        }
        finally
        {
            held.Dispose();
            Writable(scratch.Paths.SettingsFile);
            shell.Dispose(); // quitting: the last try, with nothing reloading
        }
        Assert.Null(CloudConsentFile.ReadAll(scratch.Paths.ConsentFile).Speech);
        Assert.Equal(SpeechSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Speech.Provider);
    });

    /// <summary>
    /// Lens item 2: while cloud speech sends each recording, the header (both views), the tooltip and the support emails' models line
    /// carry "cloud speech", and the feedback email names the provider only, never the host.
    /// </summary>
    [Fact]
    public Task The_header_tooltip_and_feedback_email_say_cloud_speech_without_the_host() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Settings(SpeechAt()));
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        try
        {
            shell.Reload(userRequested: false);
            Assert.True(shell.SpeechSendsAudio);
            Assert.Contains(" · cloud speech · ", shell.ModelsLine);
            Assert.Contains(" · cloud speech · ", shell.Status.Models);
            Assert.Contains(" · cloud speech · ", shell.Status.Tooltip);
            Assert.Contains(" · cloud speech · ", shell.SimpleModelsLine);
            var facts = shell.FeedbackFacts();
            Assert.Equal("cloud speech (openai-compatible)", facts.CloudSpeech);
            var body = Uri.UnescapeDataString(Dictum.Help.SupportMail.FeedbackUri(facts));
            Assert.Contains("\r\nSpeech: cloud speech (openai-compatible)\r\nClean-up: ", body);
            Assert.DoesNotContain(SpeechHost, body);
            Assert.DoesNotContain(SpeechHost, shell.ModelsLine);

            Assert.Null(shell.DisableCloudSpeech());
            Assert.DoesNotContain("cloud speech", shell.ModelsLine);
            Assert.DoesNotContain("cloud speech", shell.Status.Tooltip);
            Assert.Null(shell.FeedbackFacts().CloudSpeech);
            Assert.DoesNotContain("\r\nSpeech: ", Uri.UnescapeDataString(Dictum.Help.SupportMail.FeedbackUri(shell.FeedbackFacts())));
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>A log that runs <c>onMessage</c> for each line as it is written (to act at an exact point in a shell path).</summary>
    private sealed class HookLog(Action<string> onMessage) : ILog
    {
        public void Write(LogLevel level, string message) => onMessage(message);
    }

    /// <summary>Every request gets the same status; a 200 carries one transcription. Counts them.</summary>
    private sealed class SpeechServer(HttpStatusCode status) : HttpMessageHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? """{"text":"Hello from the cloud."}""" : "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>The scripted dialogs, also recording each question's buttons (the speech consent step's labels and default).</summary>
    private sealed class ChoiceDialogs : IShellDialogs
    {
        public ScriptedDialogs Inner { get; } = new();

        public Queue<int> Answers => Inner.Answers;

        public List<(string Title, string Text, DialogChoice[] Choices)> Questions { get; } = [];

        public int Ask(string title, string text, params DialogChoice[] choices)
        {
            Questions.Add((title, text, choices));
            return Inner.Ask(title, text, choices);
        }

        public void Tell(string title, string text) => Inner.Tell(title, text);

        public bool TypeToConfirm(string title, string text, string word) => Inner.TypeToConfirm(title, text, word);

        public void Onboard(OnboardingFlow flow) => Inner.Onboard(flow);

        public string? ChooseFolder(string initialDir) => Inner.ChooseFolder(initialDir);

        public ModelPick FirstRunPicker(string recommended, string reason, Func<ModelInfo, bool> isDownloaded, string modelsDir) =>
            Inner.FirstRunPicker(recommended, reason, isDownloaded, modelsDir);

        public CleanupPick CleanupPicker(string recommended, string reason, Func<CleanupModelInfo, bool> isDownloaded, string modelsDir) =>
            Inner.CleanupPicker(recommended, reason, isDownloaded, modelsDir);

        public MissingModelChoice MissingModel(string shortName, bool canDownload) => Inner.MissingModel(shortName, canDownload);

        public bool ConfirmDownload(string fileName, string sizeText, string note) => Inner.ConfirmDownload(fileName, sizeText, note);

        public BusyDownloadChoice ConfirmDownloadWhileBusy(string fileName, string sizeText, string note, string chosen, string running, string runningProgress, bool canWait = true) =>
            Inner.ConfirmDownloadWhileBusy(fileName, sizeText, note, chosen, running, runningProgress, canWait);

        public string? ChooseFile(string initialDir, bool cleanupModel) => Inner.ChooseFile(initialDir, cleanupModel);
    }
}
