using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Help;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// 1.2.1: the owner's decisions of 22/10/2026 (docs/DECISIONS.md, "Owner approvals (22/10/2026)"), each pinned word for word,
/// independently of the app's constants. Headless: nothing reaches the desktop.
/// </summary>
public class OwnerApprovalsTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string> Visible(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    /// <summary>Pumps the UI thread until <paramref name="condition"/> holds; the bound is a ceiling for a hung load, not a timing.</summary>
    private static bool Eventually(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    // ---- Decision 3: the hold pill ----

    /// <summary>Pin (decision 3): the hold pill says "Recording · let go to finish"; hands-free's label is unchanged.</summary>
    [Fact]
    public void The_hold_pill_says_let_go_to_finish()
    {
        Assert.Equal("Recording · let go to finish", PillText.For(PillView.RecordingHold, TimeSpan.Zero));
        Assert.Equal("Recording · let go to finish", PillText.RecordingHold);
        Assert.Equal("Hands-free", PillText.For(PillView.RecordingHandsFree, TimeSpan.Zero));
        Assert.Equal("typing as you pause · tap to stop · Esc stops and keeps what's typed", PillText.HandsFreeHint);
    }

    // ---- Decision 4: no microphone ----

    /// <summary>Pin (decision 4): "Ready" becomes "No microphone" while none is listed; every other state word is kept.</summary>
    [Fact]
    public void The_state_word_is_no_microphone_only_in_place_of_ready()
    {
        Assert.Equal("No microphone", NoSoundText.NoMicrophoneState);
        Assert.Equal(("No microphone", "error"), FlyoutWindow.StateWithNoMicrophone("Ready", "ready", none: true));
        Assert.Equal(("Ready", "ready"), FlyoutWindow.StateWithNoMicrophone("Ready", "ready", none: false));
        foreach (var (state, tone) in new[] { ("Off", "off"), ("Recording", "recording"), ("Working", "working"), ("Downloading", "working"),
                     ("Loading", "working"), ("No model", "error"), ("Model error", "error"), ("Error", "error") })
            Assert.Equal((state, tone), FlyoutWindow.StateWithNoMicrophone(state, tone, none: true));
    }

    /// <summary>
    /// Pin (decision 4): with a speech model ready and no microphone listed the panel's state word is "No microphone", the
    /// no-microphone sentence stays under the header, and once a microphone is listed the next refresh says "Ready" again.
    /// </summary>
    [Fact]
    public Task The_panel_says_no_microphone_until_one_appears() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        File.WriteAllText(scratch.Paths.SettingsFile,
            $$"""{ "hotkey": "F24", "whisper": { "model": {{JsonSerializer.Serialize(model)}} }, "llm": { "provider": "none" } }""");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, platform, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(Eventually(() => shell.SpeechReady), "the model never loaded");
        Assert.Equal("Ready", shell.Status.State);

        platform.Devices.Clear();
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var texts = Visible(flyout);
        Assert.Contains("No microphone", texts);
        Assert.DoesNotContain("Ready", texts);
        Assert.Contains(texts, t => t.Split('\n').Contains(NoSoundText.NoMicrophoneBalloon));

        platform.Devices.Add(new AudioDeviceInfo("id-1", "Test microphone", true));
        shell.StatusChanged(); // the panel reads the list again on every refresh
        Dispatcher.UIThread.RunJobs();
        texts = Visible(flyout);
        Assert.Contains("Ready", texts);
        Assert.DoesNotContain("No microphone", texts);
        Assert.DoesNotContain(texts, t => t.Contains(NoSoundText.NoMicrophoneBalloon, StringComparison.Ordinal));
        flyout.Close();
        shell.Dispose();
    });

    // ---- Decision 5: Light | Full in the Advanced view only ----

    /// <summary>Pin (decision 5): the Simple view's panel hides Mode, Light | Full and its line; the Advanced view shows all three.</summary>
    [Theory]
    [InlineData("simple", false)]
    [InlineData("advanced", true)]
    public Task Light_and_Full_are_in_the_advanced_panel_only(string view, bool shown) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var segments = All<Avalonia.Controls.Primitives.ToggleButton>(flyout).Where(b => AutomationProperties.GetName(b) is "Mode: Light" or "Mode: Full").ToList();
        Assert.Equal(2, segments.Count);
        Assert.All(segments, b => Assert.Equal(shown, b.IsEffectivelyVisible));
        var texts = Visible(flyout);
        Assert.Equal(shown, texts.Contains("Mode"));
        Assert.Equal(shown, texts.Any(t => t.Contains("Light: ", StringComparison.Ordinal)));
        Assert.Contains("Speech model", texts); // the rest of the panel is still there
        flyout.Close();
        shell.Dispose();
    });

    // ---- Decision 6: "No clean-up" in the header ----

    /// <summary>
    /// Pin (decision 6): with clean-up off the panel header's clean-up part is "No clean-up" in both views (and the icon's
    /// tooltip with it); the choice keeps its name "Off", and Settings > Models' "In use:" line keeps "no clean-up".
    /// </summary>
    [Theory]
    [InlineData("simple")]
    [InlineData("advanced")]
    public Task The_header_says_No_clean_up_when_clean_up_is_off(string view) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "off" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Contains(" · No clean-up · ", shell.Status.Models, StringComparison.Ordinal);
        Assert.Contains(" · No clean-up · ", shell.Status.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(" · Off · ", shell.Status.Models, StringComparison.Ordinal);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(Visible(flyout), t => t.Contains(" · No clean-up · ", StringComparison.Ordinal));
        flyout.Close();

        Assert.Equal("No clean-up", SimpleView.OffHeaderLabel);
        Assert.Equal("Off", SimpleView.OffLabel);
        Assert.Equal("Off (paste what was heard)", SimpleView.OffAdvancedLabel);
        Assert.Equal("no clean-up", shell.Models.CurrentCleanupName);
        shell.Dispose();
    });

    // ---- The 1.2.1 block (22/10/2026, 14:25 AEDT): sections 1 to 3 of the owner's list, markers only ----
    // Pinned word for word, independently of the app's constants. The cloud speech engine lines (SpeechPolicy, CloudSpeech*) are
    // not in this block (the owner approved them later, 22/10/2026 17:22 AEDT, on PR #54), so nothing here claims them.

    /// <summary>Pin (1.2.1 block, section 1): the Help question for turning on cloud clean-up is in the list, under its approved words.</summary>
    [Fact]
    public void The_help_question_for_cloud_clean_up_is_pinned()
    {
        Assert.Equal("How do I turn on cloud clean-up?", HelpText.CloudQuestion);
        Assert.Contains(HelpText.Questions, q => q.Question == "How do I turn on cloud clean-up?" && ReferenceEquals(q.Answer, HelpText.CloudAnswer));
        Assert.Equal("Cloud clean-up is an advanced option, and it is off unless you turn it on. If you turn on cloud clean-up, the text of each dictation and the words in your dictionary leave this PC and go to the service you choose, to be tidied; your voice still stays here unless you also turn on cloud speech. Most people do not need it: Tidy my words runs on this PC.",
            HelpText.CloudAnswer[0]);
        Assert.Contains("To turn it off, open Settings > Clean-up and click Switch cloud clean-up off. Your dictations are tidied on this PC again.", HelpText.CloudAnswer);
    }

    /// <summary>Pin (1.2.1 block, section 2): Settings > Storage's Change… button, its accessible name, the drive-speed sentence, and consent.json's label.</summary>
    [Fact]
    public void The_storage_page_strings_are_pinned()
    {
        Assert.Equal("Change…", StoragePage.ChangeLabel);
        Assert.Equal("Change the model folder", StoragePage.ChangeName);
        Assert.Equal("A slower drive makes the speech model slower to load and to switch.", StoragePage.SlowerDriveNote);
        // APPROVED 22/10/2026 22:42 AEDT: the model folder note, on Models and Storage (one constant), second sentence reworded for move-on-change.
        Assert.Equal("Dictum looks here first and downloads go here. Changing it asks before moving any files.", ModelsPage.FolderNote);
        using var scratch = new Scratch();
        var consent = PathRegistry.Locations(scratch.Paths, new AppSettings()).Single(l => l.Path == scratch.Paths.ConsentFile && !l.Path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal("consent to cloud clean-up and cloud speech", consent.Description);
    }

    /// <summary>Pin (1.2.1 block, section 3): the words of Settings > See how it works, including the two cloud speech variants and the no-model line.</summary>
    [Fact]
    public void The_see_how_it_works_page_words_are_pinned()
    {
        Assert.Equal("See how it works", HowItWorksText.PageTitle);
        Assert.Equal("Record one short dictation and watch what Dictum does with it, step by step. It uses your current settings: the same speech model and clean-up as the hotkey.", HowItWorksText.Intro);
        Assert.Equal("This page pastes nothing. The recording is kept in memory only, never saved to disk, and the sound never leaves this PC.", HowItWorksText.MemoryNote);
        Assert.Equal("This page pastes nothing. The recording is kept in memory only and never saved to disk. With cloud speech on, the sound is sent to HOST to be turned into text.", HowItWorksText.CloudSpeechNote("HOST"));
        Assert.Equal("With cloud speech on, HOST turns the sound into words (the speech model on this PC, if that fails). This is the raw text.", HowItWorksText.HeardByCloud("HOST"));
        Assert.Equal("Your clean-up sends text to another program (STATUS), so this recording's words go there too, as a dictation's would.", HowItWorksText.SendsText("STATUS"));
        Assert.Equal(("Record", "Stop", "Record one dictation", "Stop recording and show the steps", "or hold F24"),
            (HowItWorksText.RecordLabel, HowItWorksText.StopLabel, HowItWorksText.RecordName, HowItWorksText.StopName, HowItWorksText.HoldHint("F24")));
        Assert.Equal("Recording: say a sentence or two, then press Stop.", HowItWorksText.Recording);
        Assert.Equal("Stopped at 30 s, the longest this page records.", HowItWorksText.LimitReached);
        Assert.Equal("A dictation is in progress: try again when it has finished.", HowItWorksText.Busy);
        Assert.Equal("Cancelled: nothing was kept.", HowItWorksText.Cancelled);
        Assert.Equal("Done. Record again to try another sentence.", HowItWorksText.Done);
        Assert.Equal("That did not work: MESSAGE", HowItWorksText.Failed("MESSAGE"));
        Assert.Equal("No speech model is ready yet, so Record is off. Settings > Models shows the model's state.", HowItWorksText.NoSpeechModel);
        Assert.Equal(
        [
            ("1. What Dictum hears", "Sound, not words: the bars show how loud your microphone is."),
            ("2. What the speech model heard", "The speech model on this PC turns the sound into words. This is its raw text."),
            ("3. Rule-based tidy", "Fixed rules remove hesitations such as \"um\", add capitals and a full stop, and follow spoken commands such as \"new line\"."),
            ("4. AI clean-up", "A language model polishes the words, then Dictum checks its reply and uses it only if it passes every check."),
            ("5. What would be pasted", "After your corrections, the profanity filter and snippets. A dictation pastes this; this page does not."),
        ], HowItWorksText.Stages);
        Assert.Equal(["Waiting", "Working…", "Done", "Not used", "Not reached"],
            [HowItWorksText.Waiting, HowItWorksText.Working, HowItWorksText.StageDone, HowItWorksText.NotUsed, HowItWorksText.NotReached]);
        Assert.Equal("Took 12 ms", HowItWorksText.Took(12));
        Assert.Equal("Dictum stops here (REASON), as it would for a dictation.", HowItWorksText.StoppedHere("REASON"));
        Assert.Equal("The rule-based tidy did not run: only spaces were tidied.", HowItWorksText.TidyPlain);
        Assert.Equal("No AI clean-up is in use, so this is the clean-up.", HowItWorksText.TidyIsAll);
        Assert.Equal("Used, because the AI reply was not (step 4).", HowItWorksText.TidyUsed);
        Assert.Equal("Not needed this time: the AI reply passed. Dictum uses this text when a reply does not.", HowItWorksText.TidyNotNeeded);
        Assert.Equal("Not in use. Clean-up in use: STATUS.", HowItWorksText.NotInUse("STATUS"));
        Assert.Equal("Accepted: the reply passed every check, so Dictum used it.", HowItWorksText.Accepted);
        Assert.Equal("Not used: REASON. Dictum used the rule-based tidy (step 3) instead.", HowItWorksText.Rejected("REASON"));
        Assert.Equal("Not asked: REASON. Dictum used the rule-based tidy (step 3) instead.", HowItWorksText.NotAsked("REASON"));
        Assert.Equal("A dictation would paste this. This page pasted nothing.", HowItWorksText.WouldPaste);
        Assert.Equal("Your corrections, the profanity filter or snippets changed the text.", HowItWorksText.Changed);
    }

    /// <summary>
    /// Pin (plain words, section 2b, APPROVED 22/10/2026 17:36 AEDT): the short version on Settings > About and in the Welcome window,
    /// its card title, its button and the page it opens, word for word, independently of the app's constants.
    /// </summary>
    [Fact]
    public void The_plain_words_short_version_is_pinned()
    {
        Assert.Equal("Dictum is free. Free: no subscription, no account, no ads, no analytics, no telemetry. No spyware: Dictum sends nothing about you to Kynni.", PlainWordsText.Free);
        Assert.Equal("Dictum is open source under the MIT licence. It is provided as is, without warranty. We built it with care, but we cannot promise it will always hear you right.", PlainWordsText.OpenSource);
        Assert.Equal("Nothing you say leaves this PC unless you switch on a cloud option and agree to it. If you do, you take responsibility for what is sent to that service, and its own terms apply.", PlainWordsText.Cloud);
        Assert.Equal("Read the whole statement at dictum.kynni.au/plain-words.", PlainWordsText.ReadAll);
        Assert.Equal(("In plain words", "Open in your browser", "https://dictum.kynni.au/plain-words"), (PlainWordsText.CardTitle, PlainWordsText.OpenLabel, PlainWordsText.Url));
    }

    // ---- Cloud speech UI block (22/10/2026 17:22 AEDT): the owner's list for PR #54, sections 1 to 3 ----
    // Pinned word for word, independently of the app's constants, one test per surface. The three approved at 18:46 AEDT
    // (SpeechPolicy.LoopbackHttpRefusal, About's fallback form "Speech: {status}.", the both-files switch-off sentence whole)
    // are pinned here too. The consent text and buttons, the replacement sentence and the "Cloud clean-up:" caption have their
    // own byte pins (CloudSpeechTests, CloudSpeechUiTests); the SpeechStatus lines are pinned in CloudSpeechTests.

    /// <summary>Pin (cloud speech UI block): the consent step's title and what switching on can say.</summary>
    [Fact]
    public void Cloud_speech_dialog_and_switch_on_lines_are_pinned()
    {
        Assert.Equal("Send your voice to a cloud service?", AppShell.SpeechConsentTitle);
        Assert.Equal("Cloud speech was not switched on; nothing is sent.", AppShell.SpeechNotSwitchedOn);
        Assert.Equal("offline_only is on, so cloud speech is refused. Switch offline only off in Settings > Privacy first.", AppShell.SpeechOfflineRefused);
        Assert.Equal("Type the service's https:// address and the model (for Azure OpenAI, the deployment name).", AppShell.SpeechAddressMissing);
    }

    /// <summary>Pin (cloud speech UI block): Settings > Models' Cloud speech card.</summary>
    [Fact]
    public void Cloud_speech_card_words_are_pinned()
    {
        Assert.Equal("Cloud speech", ModelsPage.CloudSpeechTitle);
        Assert.Equal("Use cloud speech…", ModelsPage.UseCloudSpeechLabel);
        Assert.Equal("Switch cloud speech off", ModelsPage.CloudSpeechOffLabel);
        Assert.Equal("Optional, for a slow PC or one without a GPU. A cloud service turns each recording into text instead of the speech model on this PC, but the recording of every dictation, your voice, then goes to that service. The speech model on this PC stays as the fallback.",
            ModelsPage.CloudSpeechIntro);
        Assert.Equal("Dictum adds /audio/transcriptions. Cloud speech needs an https:// address, also for a server on this PC.", ModelsPage.CloudSpeechAddressHint);
        Assert.Equal("Offline only is on (Privacy), so cloud speech cannot be switched on.", ModelsPage.CloudSpeechOfflineNote);
        Assert.Equal("Cloud speech is on.", ModelsPage.CloudSpeechSwitchedOn);
        Assert.Equal("Cloud speech is off; the speech model on this PC hears your dictations again.", ModelsPage.CloudSpeechSwitchedOff);
        var azure = SpeechPresets.All.Single(p => p.Name == "Azure OpenAI");
        Assert.Equal("https://your-resource.openai.azure.com/openai/v1", ModelsPage.CloudSpeechAddressWatermark(azure));
        Assert.Equal("https://your-server.example.com/v1", ModelsPage.CloudSpeechAddressWatermark(SpeechPresets.Custom));
        Assert.Equal("The key goes in .env in the settings folder: GROQ_API_KEY=your Groq API key. Without it nothing is sent.",
            ModelsPage.CloudSpeechKeyLine(SpeechPresets.All.Single(p => p.Name == "Groq")));
        Assert.Equal("If the server needs a key, it goes in .env in the settings folder: SPEECH_API_KEY=your key.", ModelsPage.CloudSpeechKeyLine(SpeechPresets.Custom));
    }

    /// <summary>Pin (cloud speech UI block): Switch cloud speech off when a file could not be changed (the approved parts).</summary>
    [Fact]
    public void Cloud_speech_off_not_saved_sentences_are_pinned()
    {
        Assert.Equal("Cloud speech is off, but consent.json could not be changed (C). Nothing is sent. Dictum tries again at the next reload and when it quits; until it is changed, your speech consent stays on file.",
            AppShell.SpeechOffNotSaved("C", null));
        Assert.Equal("Cloud speech is off for this session, but settings.json could not be saved (S). Nothing is sent. Dictum tries again at the next reload and when it quits.",
            AppShell.SpeechOffNotSaved(null, "S"));
        // Both files, whole (its last clause APPROVED 22/10/2026 18:46 AEDT).
        Assert.Equal("Cloud speech is off for this session, but consent.json could not be changed (C) and settings.json could not be saved (S). Nothing is sent. Dictum tries again at the next reload and when it quits; if both are still unchanged then, the next start turns cloud speech back on, unless settings.json cannot be read, in which case Dictum starts offline.",
            AppShell.SpeechOffNotSaved("C", "S"));
    }

    /// <summary>Pin (cloud speech UI block): the tray panel's footer line and the header, tooltip and support-email token.</summary>
    [Fact]
    public void Cloud_speech_tray_line_and_token_are_pinned()
    {
        Assert.Equal("Cloud speech · audio leaves this PC (speech.example.com)", AppShell.SpeechTrayLine("speech.example.com"));
        Assert.Equal("cloud speech", AppShell.CloudSpeechToken);
    }

    /// <summary>Pin (cloud speech UI block): the feedback email's cloud speech line (the provider only).</summary>
    [Fact]
    public void Cloud_speech_support_email_line_is_pinned()
    {
        var body = Uri.UnescapeDataString(SupportMail.FeedbackUri(new FeedbackSetup("1.2.1", "Windows build 10.0.26100", "base.en", "CPU", "rules", "cloud speech (openai-compatible)")));
        Assert.Contains("\r\nSpeech model: base.en on CPU\r\nSpeech: cloud speech (openai-compatible)\r\nClean-up: rules", body);
    }

    /// <summary>Pin (cloud speech UI block): Settings > Privacy's and Settings > About's cloud speech lines.</summary>
    [Fact]
    public void Cloud_speech_privacy_and_about_lines_are_pinned()
    {
        Assert.Equal("Your voice goes to speech.example.com for speech recognition. Advanced option: your audio leaves this PC. Dictum does not validate or certify how that service handles it.",
            AppShell.SpeechPrivacyLine("speech.example.com"));
        Assert.Equal("Speech: cloud, speech.example.com (m); base.en on this PC is the fallback.", AppShell.SpeechAboutLine("speech.example.com", "m", "base.en"));
    }

    /// <summary>Pin (cloud speech UI block): the pill's two cloud speech labels.</summary>
    [Fact]
    public void Cloud_speech_pill_lines_are_pinned()
    {
        Assert.Equal("Hearing… (cloud)", AppShell.HearingCloudPill);
        Assert.Equal("Heard on this PC (cloud speech failed)", SpeechStatus.FallbackPill);
    }

    /// <summary>Pin (cloud speech UI block): the load log line and the settings loader's https message.</summary>
    [Fact]
    public void Cloud_speech_log_and_loader_lines_are_pinned()
    {
        var noon = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Unspecified);
        var at = new DateTimeOffset(noon, TimeZoneInfo.Local.GetUtcOffset(noon)); // noon on this PC's clock: the date cannot shift
        Assert.Equal("Cloud speech on: audio goes to speech.example.com (m); consent recorded 2026-10-22; base.en is the fallback.",
            CloudSpeech.LoadLine("speech.example.com", "m", at, "base.en"));
        Assert.Equal("expected an absolute https:// URL such as \"https://api.openai.com/v1\"; cloud speech needs https://, also for a server on this PC, because its consent step says the connection is encrypted",
            SettingsLoader.SpeechBaseUrlProblem("http://localhost:8080/v1"));
        // APPROVED 22/10/2026 18:46 AEDT: the refusal of a plain http:// address on this PC (the speech step's status and log).
        Assert.Equal("speech.base_url localhost uses http://, but cloud speech needs an https:// address, also on this PC, because its consent step says the connection is encrypted",
            SpeechPolicy.LoopbackHttpRefusal("localhost"));
    }

    /// <summary>
    /// Pin (cloud speech UI block, 18:46 AEDT): About's fallback form "Speech: {status}.", here while cloud speech is agreed to and
    /// offline only refuses it (the status is clean-up's approved offline line, word for word).
    /// </summary>
    [Fact]
    public Task Cloud_speech_about_fallback_form_is_pinned() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "offline_only": true, "speech": { "provider": "openai-compatible", "base_url": "https://speech.example.com/v1", "model": "m" } }""");
        CloudConsentFile.WriteSpeech(scratch.Paths.ConsentFile, new CloudConsentRecord(SpeechSettings.OpenAiCompatible, "speech.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            Assert.Equal("Speech: On, but offline only is on: nothing is sent until you switch offline only off.", shell.SpeechAboutValue);
        }
        finally
        {
            shell.Dispose();
        }
    });

    // ---- Cloud clean-up consent (owner, 22/10/2026 16:35 and 18:41 AEDT; counsel review pending) ----
    // Moved here from CloudWarningAndPresetsTests (final lens on PR #54): the literal pins belong with the owner's approvals.

    // Approved by the product owner on 16/10/2026 as written (CloudPolicy.ConsentWarning): a literal copy, independent of the app's.
    private static string ApprovedCleanupWarning(string host) =>
        $"WARNING: this is an advanced option and it is less private than local clean-up. Your text leaves this PC to be processed on {host}'s servers. Dictum encrypts the connection (HTTPS) but cannot see, validate or certify how that service stores, protects or uses your text, or who else can read it. Do not dictate confidential, personal or client information with cloud clean-up on unless you have checked that service's terms yourself.";

    /// <summary>
    /// Pin (mutation-checked): the whole clean-up consent text for the Anthropic and a custom OpenAI-compatible host, byte for byte,
    /// with the owner's 18:41 parenthesis and 16:35 paragraph (22/10/2026; counsel review pending), independently of the app's copy.
    /// </summary>
    [Fact]
    public void The_clean_up_consent_text_is_the_approved_text_byte_for_byte()
    {
        var anthropic = new LlmSettings { Provider = LlmSettings.Anthropic, Model = LlmSettings.AnthropicDefaultModel };
        var host = Dictum.Llm.CloudPolicy.CloudHost(anthropic)!;
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(
            $"With cloud clean-up on, every dictation's text (not the audio: cloud speech, if you turn it on, sends the audio to the service you choose for it) is sent to {host} (Anthropic) together with Dictum's clean-up instructions, which include the words in your personal dictionary. " +
            "It uses your own Anthropic API key (ANTHROPIC_API_KEY), and Anthropic's terms and privacy policy apply to what it receives.\n\n" +
            ApprovedCleanupWarning(host) + "\n\n" +
            "By switching cloud clean-up on you accept responsibility for the security and privacy of the text sent there.\n\n" +
            "Nothing is sent until you agree. You can switch it off at any time in Settings > Clean-up, and offline only mode refuses it."),
            System.Text.Encoding.UTF8.GetBytes(Dictum.Llm.CloudPolicy.ConsentText(anthropic, host)));
        var custom = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" };
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(
            "With cloud clean-up on, every dictation's text (not the audio: cloud speech, if you turn it on, sends the audio to the service you choose for it) is sent to llm.example.com together with Dictum's clean-up instructions, which include the words in your personal dictionary. " +
            "It uses your own API key (LLM_API_KEY) if you have set one. Whoever runs that server receives the text; its own terms apply.\n\n" +
            ApprovedCleanupWarning("llm.example.com") + "\n\n" +
            "By switching cloud clean-up on you accept responsibility for the security and privacy of the text sent there.\n\n" +
            "Nothing is sent until you agree. You can switch it off at any time in Settings > Clean-up, and offline only mode refuses it."),
            System.Text.Encoding.UTF8.GetBytes(Dictum.Llm.CloudPolicy.ConsentText(custom, "llm.example.com")));
    }

    // ---- The two clean-up Help questions: APPROVED 22/10/2026 19:20 AEDT (proposed 22/10/2026), as a block ----
    // Pinned word for word, independently of the app's constants. Reworded only on the owner's written replacement, and then these
    // literals change with the strings in the same commit.

    /// <summary>APPROVED 22/10/2026 19:20 AEDT pin: the two new Help questions, their answers word for word, and their place in the list.</summary>
    [Fact]
    public void The_proposed_clean_up_help_questions_are_pinned()
    {
        Assert.Equal("What is clean-up?", HelpText.CleanupQuestion);
        Assert.Equal(
        [
            "Clean-up is the tidying Dictum does between hearing you and typing. When we speak, we say ums and ahs and repeat ourselves. Clean-up takes those out and tidies the punctuation and capitals before your words are typed.",
            "You choose it in Settings > Models. Tidy my words uses fixed rules: it fixes punctuation and capitals, takes out ums, ahs, doubled words such as \"the the\" and fillers such as \"you know\" set off by commas, and follows \"scratch that\" and \"new line\" when they are said as a phrase of their own. It needs no download.",
            "AI clean-up (Add AI clean-up) is an optional download that runs on this PC. It also takes out \"you know\" and false starts anywhere in a sentence, and follows \"scratch that\" even mid-sentence. Dictum checks its answer against what you said; if the check fails, or the model is too slow, not ready or runs into an error, it uses the rule-based tidy instead.",
            "Off pastes what was heard; only your own corrections, snippets and the profanity filter (if on) still apply.",
            "Unless the spoken language is set to English (or Translate to English is on), only the spacing is tidied.",
            "Clean-up happens on this PC. The text of each dictation and the words in your dictionary go to another service only if you turn on cloud clean-up in Settings > Clean-up, and it is off unless you turn it on. Your voice still stays here unless you also turn on cloud speech. Cloud speech also sends the words in your dictionary, as a hint.",
            "To watch clean-up work on your own words, open Settings > See how it works and record a short dictation.",
        ], HelpText.CleanupAnswer);
        Assert.Equal("How can I see what Dictum does with my words?", HelpText.SeeHowQuestion);
        Assert.Equal(
        [
            "Open Settings > See how it works, press Record, say a sentence or two, then press Stop. The page shows five steps: what Dictum hears, what the speech model heard, the rule-based tidy, the AI clean-up, and what would be pasted.",
            "It uses your current settings. It pastes nothing, and the recording is never saved to disk.",
        ], HelpText.SeeHowAnswer);

        var questions = HelpText.Questions.Select(q => q.Question).ToList();
        Assert.Equal(["Why did it type the wrong words?", "What is clean-up?", "How can I see what Dictum does with my words?"],
            questions.Skip(questions.IndexOf("Why did it type the wrong words?")).Take(3)); // together, after the question about wrong words
        Assert.Contains(HelpText.Questions, q => q.Question == "What is clean-up?" && ReferenceEquals(q.Answer, HelpText.CleanupAnswer));
        Assert.Contains(HelpText.Questions, q => q.Question == "How can I see what Dictum does with my words?" && ReferenceEquals(q.Answer, HelpText.SeeHowAnswer));
    }

    /// <summary>
    /// APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword: pin of the Help strings for the own-instructions box and the Create the
    /// key file… button, word for word. The sentences are the own-instructions lane's (PR #60), except the cloud answer's second paragraph and the
    /// own-instructions answer's second paragraph and last clause, which are the lens's wording (22/10/2026). Approved by the owner as part of the 1.3 block (22/10/2026 22:52 AEDT).
    /// </summary>
    [Fact]
    public void The_approved_own_instructions_and_key_file_help_strings_are_pinned()
    {
        Assert.Equal("To add a key for cloud clean-up, choose the service in Settings > Clean-up and press Create the key file…. Dictum makes the file and opens it: put your key in place of your-key-here, save, and Dictum reads it. If you already have a key file, Dictum opens it as it is.",
            HelpText.CloudAnswer[4]);
        Assert.Equal("If the button does not work for you, make the key file yourself with steps 2 to 15 below; steps 1 and 16 are the same either way.", HelpText.CloudAnswer[5]);
        Assert.StartsWith("1. Open Settings > Clean-up.", HelpText.CloudAnswer[6]); // the sixteen steps follow, unchanged
        Assert.Equal("Can I give the clean-up my own instructions?", HelpText.OwnInstructionsQuestion);
        Assert.Equal(
        [
            "In the Advanced view, Settings > Clean-up has a box for your own instructions, such as \"use short sentences\" or \"write product names in capitals\". Dictum adds them to its own clean-up instructions for the AI clean-up on this PC and for cloud clean-up; the rule-based tidy and Off do not use them. With cloud clean-up on, your instructions go to that service with each dictation, as part of Dictum's clean-up instructions.",
            "Dictum still checks every reply against what you said. A reply that drops or changes your words, translates or summarises them, answers you instead of tidying, or adds a sign-off you did not say is usually rejected, and the rule-based tidy is pasted instead. The check cannot catch everything, so read what Dictum typed.",
        ], HelpText.OwnInstructionsAnswer);
        Assert.Contains(HelpText.Questions, q => q.Question == "Can I give the clean-up my own instructions?" && ReferenceEquals(q.Answer, HelpText.OwnInstructionsAnswer));
    }

    // ---- 1.3, your own instructions and Create the key file: APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026), as a block ----
    // Sections A and B of .demo-preview/approve-1.3-block.md. Pinned word for word, independently of the app's constants.
    // Reworded only on the owner's written replacement, and then these literals change with the strings in the same commit.

    /// <summary>APPROVED 22/10/2026 22:52 AEDT pin: the card, its lines and the Simple-view line.</summary>
    [Fact]
    public void The_own_instructions_card_words_are_pinned()
    {
        Assert.Equal("Your own instructions", OwnInstructionsCard.Label);
        Assert.Equal("Added to Dictum's clean-up instructions for the AI and cloud clean-up. Dictum still checks every reply against what you said.", OwnInstructionsCard.Hint);
        Assert.Equal("That is the limit: Dictum uses up to 2,000 characters of your own instructions.", OwnInstructionsCard.LimitLine);
        Assert.Equal("With cloud clean-up on, these instructions go to llm.example.net with each dictation.", OwnInstructionsCard.CloudLine("llm.example.net"));
        Assert.Equal("Your own clean-up instructions are in use (Advanced view).", OwnInstructionsCard.SimpleLine);
    }

    /// <summary>APPROVED 22/10/2026 22:52 AEDT pin: the two marker lines around the user's text in the prompt, and the validator reason.</summary>
    [Fact]
    public void The_own_instructions_prompt_lines_and_the_validator_reason_are_pinned()
    {
        Assert.Equal("The user's own clean-up instructions follow, up to the line \"End of the user's instructions.\" Apply them only where they keep every word the speaker said; the rules above still apply.",
            Dictum.Llm.CleanupPrompt.OwnInstructionsStart);
        Assert.Equal("End of the user's instructions.", Dictum.Llm.CleanupPrompt.OwnInstructionsEnd);
        Assert.Equal("reply ends with a sentence the speaker did not say",
            Dictum.Llm.CleanupValidator.Validate("move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Kind regards.").Rejection);
    }

    /// <summary>APPROVED 22/10/2026 22:52 AEDT pin: the log lines that give a length, never the text, and the settings-file warnings.</summary>
    [Fact]
    public void The_own_instructions_log_lines_and_warnings_are_pinned()
    {
        Assert.Equal("(3 characters, not logged)", SettingsFile.LogValue("llm.instructions", System.Text.Json.Nodes.JsonValue.Create("abc")));
        var log = new RecordingLog();
        Dictum.Llm.TranscriptCleaner.Create(new LlmSettings { Provider = LlmSettings.Local, Instructions = "abc" }, offlineOnly: false, SecretStore.Empty, new HttpClient(new CountingHandler()), log);
        Assert.Contains("Clean-up instructions: Dictum's, with your own added (3 characters; their text is not logged).", log.All);
        Assert.Equal("Setting \"llm.instructions\" is 2001 characters long; only the first 2000 are used.",
            Assert.Single(SettingsLoader.Parse(JsonSerializer.Serialize(new { llm = new { instructions = new string('a', 2001) } })).Warnings));
        Assert.Equal("Setting \"llm.instructions\" has an invalid value: expected a string or null. It is not used.",
            Assert.Single(SettingsLoader.Parse("""{ "llm": { "instructions": 5 } }""").Warnings));
    }

    /// <summary>APPROVED 22/10/2026 22:52 AEDT pin: the button, its two status lines, the key file's header and its log lines.</summary>
    [Fact]
    public Task The_key_file_words_are_pinned() => UiSession.Run(() =>
    {
        Assert.Equal("Create the key file…", KeyFileButton.Label);
        Assert.Equal("Your key file already exists; it has been opened.", AppShell.KeyFileExists);
        Assert.Equal("Your key file has been created and opened: put your key in place of your-key-here after GEMINI_API_KEY=, then save it.", AppShell.KeyFileCreated("GEMINI_API_KEY"));
        Assert.Equal("# Dictum keys, created by Settings > Clean-up. Replace your-key-here with your key and save this file: Dictum reads it again by itself.\n" +
                     "# A real environment variable of the same name wins over this file. Keys are never written to settings.json or the log.\n", KeyFile.Header);
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" } }""");
        var log = new RecordingLog();
        var (shell, _, _) = scratch.Shell(log: log);
        shell.CreateKeyFile("GEMINI_API_KEY");
        shell.CreateKeyFile("GEMINI_API_KEY");
        Assert.Contains("Key file created with a placeholder line for GEMINI_API_KEY (no key written).", log.All);
        Assert.Contains("Key file already exists: opened as it is, not changed.", log.All);
        shell.Dispose();
    });

    // ---- Moving the model files (1.3 block, section C): APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026), as a block ----

    /// <summary>
    /// APPROVED 22/10/2026 22:52 AEDT pin: the move question, its buttons (Leave the default), the refusals, the progress line,
    /// every result-line form built by a function ("Moved {k} of {n}." when Quit stopped the move included), and the four reasons,
    /// word for word.
    /// </summary>
    [Fact]
    public void The_model_move_strings_are_pinned()
    {
        Assert.Equal("Move your model files?", ModelFolderActions.MoveTitle);
        Assert.Equal(@"Your model files are in C:\a. Move them to D:\b so Dictum finds them there? 2 files, 1.0 GB. Dictum copies each file, checks it, then removes the original.",
            ModelFolderActions.MoveQuestion(@"C:\a", @"D:\b", 2, 1_000_000_000));
        Assert.Equal("Move the files", ModelFolderActions.MoveLabel);
        Assert.Equal("Leave them where they are", ModelFolderActions.LeaveLabel);
        Assert.Equal("A download is running; wait for it to finish, then change the folder again to move the files.", ModelFolderActions.DownloadRunning);
        Assert.Equal("One of these folders is inside the other, so Dictum does not move the files. Choose a folder outside the current one.", ModelFolderActions.NestedRefused);
        Assert.Equal("Model files are being moved; try again when it finishes.", Dictum.App.ModelCoordinator.MovingRefusal);
        Assert.Equal("Moving small.en (1 of 3)…", ModelFolderActions.Moving("small.en", 1, 3));
        Assert.Equal("unfinished small.en download", ModelMover.DisplayName("ggml-small.en.bin.part"));
        Assert.Equal(@"Moved 3 files to D:\b.", ModelFolderActions.Moved(3, @"D:\b"));
        Assert.Equal(@"Moved 1 of 3; small.en could not be moved: why. The rest stay in C:\a.", ModelFolderActions.MovedSome(1, 3, "small.en", "why", @"C:\a"));
        Assert.Equal(" 2 other files could not be moved either; the log says why.", ModelFolderActions.MoreProblems(2));
        Assert.Equal("Moved 1 of 3.", ModelFolderActions.MovedStopped(1, 3));
        Assert.Equal(@" The model folder is still C:\a, because the model in use did not move; the files that did are in D:\b; choose that folder again to use them.",
            ModelFolderActions.FolderKept(@"C:\a", @"D:\b", someMoved: true));
        Assert.Equal("a different file of that name is already in the new folder", ModelMover.AlreadyThere);
        Assert.Equal("the copy is not the same size as the original, so the copy was deleted", ModelMover.CopySizeDiffers);
        Assert.Equal("the copy did not match the catalogue, so the copy was deleted", ModelMover.CopyDoesNotMatch);
        Assert.Equal("it was copied, but the original could not be removed", ModelMover.OriginalKept);
    }
}
