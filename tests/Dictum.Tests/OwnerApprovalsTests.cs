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
    // not in this block and stay PROPOSED, so nothing here claims them.

    /// <summary>Pin (1.2.1 block, section 1): the Help question for turning on cloud clean-up is in the list, under its approved words.</summary>
    [Fact]
    public void The_help_question_for_cloud_clean_up_is_pinned()
    {
        Assert.Equal("How do I turn on cloud clean-up?", HelpText.CloudQuestion);
        Assert.Contains(HelpText.Questions, q => q.Question == "How do I turn on cloud clean-up?" && ReferenceEquals(q.Answer, HelpText.CloudAnswer));
        Assert.Equal("Cloud clean-up is an advanced option, and it is off unless you turn it on. If you turn on cloud clean-up, the text of each dictation and the words in your dictionary leave this PC and go to the service you choose, to be tidied; your voice still stays here. Most people do not need it: Tidy my words runs on this PC.",
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

    // ---- PROPOSED (22/10/2026, clean-up explainers lane): NOT approved ----
    // Pinned word for word so the copy cannot drift before the owner decides, independently of the app's constants. This block is
    // the only claim these strings make: nothing here records an approval, and the markers in the code stay PROPOSED. Reworded on
    // approval means these literals change with them, in the same commit as the owner's decision.

    /// <summary>PROPOSED pin: the three row explainers for Tidy my words, AI clean-up and Off, each under 120 characters.</summary>
    [Fact]
    public void The_proposed_clean_up_row_explainers_are_pinned()
    {
        Assert.Equal("Fixes punctuation and capitals and takes out ums and ahs, without rewriting. A good choice for most people.", SimpleView.TidyExplainer);
        Assert.Equal("Also takes out \"you know\" and false starts, and follows \"scratch that\". Pick it if plain tidying leaves too much in.", SimpleView.AiExplainer);
        Assert.Equal("Pastes what was heard, with no tidying. Pick it if you would rather fix the text yourself.", SimpleView.OffExplainer);
        Assert.All(new[] { SimpleView.TidyExplainer, SimpleView.AiExplainer, SimpleView.OffExplainer }, line => Assert.True(line.Length < 120, $"{line.Length} characters: {line}"));
    }

    /// <summary>PROPOSED pin: the two new Help questions, their answers word for word, and their place in the list.</summary>
    [Fact]
    public void The_proposed_clean_up_help_questions_are_pinned()
    {
        Assert.Equal("What is clean-up?", HelpText.CleanupQuestion);
        Assert.Equal(
        [
            "Clean-up is the tidying Dictum does between hearing you and typing. When we speak, we say ums and ahs and repeat ourselves. Clean-up takes those out and sets the punctuation and capitals right before your words are typed.",
            "You choose it in Settings > Models. Tidy my words uses fixed rules: it fixes punctuation and capitals, and takes out ums, ahs and doubled words such as \"the the\". It does not rewrite your sentences, and it needs no download.",
            "AI clean-up (Add AI clean-up) is an optional download that runs on this PC. It also takes out \"you know\" and false starts, and follows \"scratch that\". Dictum checks its answer against what you said; if the check fails, it uses the rule-based tidy instead.",
            "Off pastes what was heard. Only your own corrections, snippets and the profanity filter (if on) still apply.",
            "Tidy my words and AI clean-up work on English dictations today. For other languages, only the spacing is tidied.",
            "Clean-up happens on this PC. Text goes to another service only if you switch on a cloud option in Settings > Clean-up, and that is off unless you switch it on.",
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
}
