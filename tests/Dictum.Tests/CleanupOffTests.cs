using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Clean-up Off (Dictum 1.2, 21/10/2026): <c>llm.provider = "off"</c> pastes the transcript as whisper wrote it, whitespace
/// tidied only: no model, no rules, no spoken commands, no reply checks, no network. <c>none</c> stays the rule-based tidy.
/// </summary>
public sealed class CleanupOffTests
{
    private static readonly LlmSettings OffSettings = new() { Provider = LlmSettings.Off };

    /// <summary>Fails the test on any request: clean-up off must never reach the network.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Assert.Fail($"clean-up off sent a request to {request.RequestUri?.Host}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    /// <summary>Every key a provider could use, so a fall-through to any provider would have what it needs to send.</summary>
    private static SecretStore AllKeys() => SecretStore.FromContents(
        "ANTHROPIC_API_KEY=sk-ant-test\nOPENAI_API_KEY=sk-test\nGEMINI_API_KEY=g-test\nLLM_API_KEY=l-test\n", null, _ => null);

    /// <summary>Pin: "off" loads without a warning in any case, is written and read back by the settings writer, and an unknown value still warns.</summary>
    [Fact]
    public void The_off_provider_round_trips()
    {
        foreach (var json in new[] { """{ "llm": { "provider": "off" } }""", """{ "llm": { "provider": "OFF" } }""" })
        {
            var load = SettingsLoader.Parse(json);
            Assert.Equal(LlmSettings.Off, load.Settings.Llm.Provider);
            Assert.DoesNotContain(load.Warnings, w => w.Contains("llm.provider", StringComparison.Ordinal));
        }
        Assert.Equal(LlmSettings.Off, SettingsLoader.Parse(SettingsFile.DefaultJson("base.en", LlmSettings.Off)).Settings.Llm.Provider);
        Assert.Contains("off (no clean-up", SettingsFile.DefaultJson("base.en"), StringComparison.Ordinal); // documented in the default file

        var dir = Directory.CreateTempSubdirectory("dictum-off-");
        try
        {
            var file = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(file, """{ "llm": { "provider": "none" } }""");
            Assert.True(SettingsFile.SetValue(file, "llm.provider", System.Text.Json.Nodes.JsonValue.Create(LlmSettings.Off), out _));
            var read = SettingsLoader.LoadFile(file);
            Assert.Equal(LlmSettings.Off, read.Settings.Llm.Provider);
            Assert.Empty(read.Warnings);
        }
        finally
        {
            dir.Delete(recursive: true);
        }

        var unknown = SettingsLoader.Parse("""{ "llm": { "provider": "disabled" } }""");
        Assert.Equal(LlmSettings.Local, unknown.Settings.Llm.Provider);
        Assert.Contains(unknown.Warnings, w => w.Contains("llm.provider", StringComparison.Ordinal) && w.Contains("\"off\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// What off pastes, against what the rule-based tidy (none) makes of the same words: fillers, repeats, spoken commands,
    /// "scratch that", lower case, a missing full stop, odd punctuation and extra spaces. Off changes the spaces only.
    /// </summary>
    public static TheoryData<string, string, string> Dictations => new()
    {
        // raw transcript, off, none (the rule-based tidy, unchanged)
        { "um so the quick brown fox uh jumps", "um so the quick brown fox uh jumps", "So the quick brown fox jumps." },
        { "  hello   there\tworld  ", "hello there world", "Hello there world." },
        { "first point, new paragraph, second point", "first point, new paragraph, second point", "First point.\n\nSecond point." },
        { "thanks, new line", "thanks, new line", "Thanks.\n" },
        { "the colour is blue, scratch that, green", "the colour is blue, scratch that, green", "Green." },
        { "you know, i think the the plan works", "you know, i think the the plan works", "I think the plan works." },
        { "Kynni Pty Ltd ships Dictum 1.2 ,okay", "Kynni Pty Ltd ships Dictum 1.2 ,okay", "Kynni Pty Ltd ships Dictum 1.2,okay." },
        { "what time is it ?", "what time is it ?", "What time is it?" },
        { "line one\r\nline two\n\nline three", "line one line two line three", "Line one line two line three." },
        { "full stop comma question mark", "full stop comma question mark", "Full stop comma question mark." },
    };

    /// <summary>Pin: the off cleaner returns the transcript with only whitespace tidied; none still gives the rule-based tidy.</summary>
    [Theory]
    [MemberData(nameof(Dictations))]
    public async Task Off_pastes_exactly_what_was_heard(string raw, string off, string none)
    {
        using var http = new HttpClient(new FailingHandler());
        var cleaner = TranscriptCleaner.Create(OffSettings, false, SecretStore.Empty, http, NullLog.Instance);
        Assert.Equal(new CleanupResult(off, false, null), await cleaner.CleanAsync(raw, CancellationToken.None));

        var rules = TranscriptCleaner.Create(new LlmSettings { Provider = LlmSettings.NoProvider }, false, SecretStore.Empty, http, NullLog.Instance);
        Assert.Equal(none, (await rules.CleanAsync(raw, CancellationToken.None)).Text);
        Assert.False(rules.IsPlain); // none is not whitespace-only
    }

    /// <summary>Pin: an empty or blank transcript pastes nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task Off_with_nothing_said_pastes_nothing(string raw) =>
        Assert.Equal("", (await TranscriptCleaner.Off().CleanAsync(raw, CancellationToken.None)).Text);

    /// <summary>
    /// Pin: off builds no client and no reply checks, loads no model and sends nothing, even with every key present, a base_url,
    /// a prompt file that cannot be read, grammar assist and personal words that a model's checks would use.
    /// </summary>
    [Fact]
    public async Task Off_builds_no_validator_loads_no_model_and_sends_nothing()
    {
        var handler = new FailingHandler();
        using var http = new HttpClient(handler);
        var log = new RecordingLog();
        var localAsked = 0;
        var settings = OffSettings with
        {
            BaseUrl = "https://llm.example.com/v1",
            PromptFile = Path.Combine(Path.GetTempPath(), $"dictum-no-such-prompt-{Guid.NewGuid():N}.txt"),
            GrammarAssist = true,
            CloudConsent = "llm.example.com",
        };
        var personal = new PersonalCleanup("Known names and terms, keep their spelling: Kynni.", ["damn"], ["Kynni"]);
        var cleaner = TranscriptCleaner.Create(settings, offlineOnly: false, AllKeys(), http, log, () =>
        {
            localAsked++;
            return null;
        }, personal);

        Assert.False(cleaner.UsesModel);
        Assert.False(cleaner.SendsText);
        Assert.True(cleaner.IsPlain);
        Assert.Null(cleaner.MissingKey);
        Assert.Equal(TranscriptCleaner.OffStatus, cleaner.Status);
        Assert.Null(cleaner.SkipReason("a long dictation about the name of the thing"));
        var result = await cleaner.CleanAsync("um tell kynni the name, new line", CancellationToken.None);
        Assert.Equal(new CleanupResult("um tell kynni the name, new line", false, null), result);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, localAsked);
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning); // the unreadable prompt file was never read
        Assert.Contains("Clean-up is off (llm.provider = off)", log.All, StringComparison.Ordinal);
        Assert.Null(TranscriptCleaner.OfflineRefusal(settings, offlineOnly: true)); // offline only has nothing to refuse
    }

    /// <summary>Pin: off is chosen over the not-English skip, so the status says what the user chose.</summary>
    [Fact]
    public void Off_wins_over_the_language_skip()
    {
        using var http = new HttpClient(new FailingHandler());
        var cleaner = TranscriptCleaner.Create(OffSettings, false, SecretStore.Empty, http, NullLog.Instance, englishText: false);
        Assert.True(cleaner.IsPlain);
        Assert.Equal(TranscriptCleaner.OffStatus, cleaner.Status);
        Assert.StartsWith("no clean-up: ", cleaner.Status, StringComparison.Ordinal); // not "off:", which read as Dictum being off (lens on 787b300)
    }

    /// <summary>
    /// Pin: through the pipeline, a hands-free part with clean-up off keeps whisper's case at the boundary and types a spoken
    /// "new paragraph" as words; the rule-based tidy turns the same part into a paragraph break.
    /// </summary>
    [Fact]
    public async Task A_hands_free_part_with_off_is_typed_as_heard()
    {
        async Task<string> Typed(TranscriptCleaner cleaner, string raw, string before = "Thanks for that.")
        {
            var inserted = new List<string>();
            var pipeline = new DictationPipeline(_ => Task.FromResult<Dictum.Transcribe.ITranscriber>(new FakeTranscriber(raw)), () => cleaner,
                (text, _) =>
                {
                    inserted.Add(text);
                    return Task.CompletedTask;
                }, () => new AppSettings(), NullLog.Instance);
            // insert.trailing_space (on by default) already put a space after the last part, so no separator is typed.
            var last = new HandsFreePart(1, null) { Typed = before, Spoken = before };
            await pipeline.RunAsync(new CapturedAudio(new float[32000], 0.2f), CancellationToken.None, _ => { }, part: new HandsFreePart(2, last));
            return Assert.Single(inserted);
        }

        Assert.Equal("new paragraph, see you then", await Typed(TranscriptCleaner.Off(), "new paragraph, see you then"));
        Assert.Equal("The meeting moved", await Typed(TranscriptCleaner.Off(), "The meeting moved", before: "and then")); // not lower-cased mid-sentence
        Assert.Equal("\n\nSee you then.", await Typed(TranscriptCleaner.RulesOnly("test"), "new paragraph, see you then"));
        Assert.Equal("the meeting moved.", await Typed(TranscriptCleaner.RulesOnly("test"), "The meeting moved", before: "and then"));
    }

    // ---- Settings > Models, the tray panel and the privacy lines (headless, as UiWindowTests) ----

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string> Lines(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button Named(ILogical root, string name) => All<Button>(root).Single(b => AutomationProperties.GetName(b) == name);

    private static ComboBox Picker(FlyoutWindow flyout, string name) => All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == name);

    private static List<string> Labels(ComboBox picker) => [.. Assert.IsAssignableFrom<IEnumerable<string>>(picker.ItemsSource)];

    private static string FileProvider(Scratch scratch) => SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider;

    private static SettingsWindow Page(Dictum.UI.Shell.AppShell shell, string page)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage(page == "Storage" ? "Models" : "Storage");
        window.ShowPage(page);
        return window;
    }

    /// <summary>Pin: the Simple rows write the right provider: Off writes off, and Tidy my words from Off writes the rule-based tidy.</summary>
    [Fact]
    public Task The_simple_rows_switch_between_tidy_and_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = Page(shell, "Models");
        var lines = Lines(window);
        Assert.Contains(SimpleView.OffLabel, lines);
        Assert.Contains(SimpleView.OffSubLine, lines); // not in use: the line alone
        Assert.Contains($"In use · {SimpleView.TidySubLine}", lines);

        Click(Named(window, SimpleView.ActionName("Use", SimpleView.OffLabel)));
        Assert.Equal(LlmSettings.Off, FileProvider(scratch)); // written
        Assert.Equal(LlmSettings.Off, shell.Settings.Llm.Provider); // and reloaded
        Assert.True(shell.Cleaner.IsPlain);
        Assert.False(shell.Cleaner.UsesModel);
        lines = Lines(window);
        Assert.Contains($"In use · {SimpleView.OffSubLine}", lines);
        Assert.Contains(SimpleView.TidySubLine, lines); // Tidy is no longer in use
        Assert.DoesNotContain(lines, l => l.StartsWith("Custom: ", StringComparison.Ordinal)); // Off is a row of its own, not Custom
        Assert.DoesNotContain(All<Button>(window), b => b.IsEffectivelyVisible && (b.Content as string ?? "").StartsWith(ModelsPage.AddCleanupLabel, StringComparison.Ordinal));

        Click(Named(window, SimpleView.ActionName("Use", SimpleView.TidyLabel)));
        Assert.Equal(LlmSettings.NoProvider, FileProvider(scratch)); // the recommended model is not on this PC: the rule-based tidy
        Assert.False(shell.Cleaner.IsPlain);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: Off has no language dependency: with text that is not English (or Detect automatically) the row and its button are still offered.</summary>
    [Theory]
    [InlineData("""{ "hotkey": "F24", "whisper": { "model": "small", "language": "fr" }, "llm": { "provider": "none" } }""")]
    [InlineData("""{ "hotkey": "F24", "whisper": { "model": "small", "language": "auto" }, "llm": { "provider": "none" } }""")]
    public Task Off_is_offered_for_every_language(string settings) => UiSession.Run(() =>
    {
        using var scratch = new Scratch(settings);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = Page(shell, "Models");
        Assert.Contains(SimpleView.OffSubLine, Lines(window));
        var use = Named(window, SimpleView.ActionName("Use", SimpleView.OffLabel));
        Assert.True(use.IsEffectivelyVisible && use.IsEnabled);
        Click(use);
        Assert.Equal(LlmSettings.Off, FileProvider(scratch));
        window.Close();

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(SimpleView.OffLabel, Labels(Picker(flyout, "Clean-up")));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the Advanced clean-up buttons offer Off and say so in the In use line.</summary>
    [Fact]
    public Task The_advanced_list_offers_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" }, "ui": { "view": "advanced" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = Page(shell, "Models");
        Assert.Contains("In use: rules (rule-based tidy)", Lines(window)); // none unchanged
        Click(All<Button>(window).Single(b => b.Content as string == SimpleView.OffAdvancedLabel));
        Assert.Equal(LlmSettings.Off, FileProvider(scratch));
        Assert.Contains($"In use: no clean-up{SimpleView.OffInUseNote}", Lines(window));
        Click(All<Button>(window).Single(b => b.Content as string == "Rule-based tidy only"));
        Assert.Equal(LlmSettings.NoProvider, FileProvider(scratch));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the tray panel's clean-up picker offers Off in both views, shows it selected while in use, and writes off when picked.</summary>
    [Fact]
    public Task The_tray_pickers_offer_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var cleanup = Picker(flyout, "Clean-up");
        Assert.Equal([SimpleView.TidyLabel, $"{ModelsPage.AddCleanupLabel} (download)", SimpleView.OffLabel], Labels(cleanup));
        cleanup.SelectedIndex = 2; // what a pick in the list does
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LlmSettings.Off, FileProvider(scratch));
        flyout.Close();

        flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        cleanup = Picker(flyout, "Clean-up");
        Assert.Equal([SimpleView.TidyLabel, SimpleView.OffLabel], Labels(cleanup)); // no Custom entry, no Add AI clean-up
        Assert.Equal(1, cleanup.SelectedIndex);
        flyout.Close();

        shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced)));
        Dispatcher.UIThread.RunJobs();
        flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        cleanup = Picker(flyout, "Clean-up");
        var labels = Labels(cleanup);
        Assert.Equal(SimpleView.OffAdvancedLabel, labels[cleanup.SelectedIndex]); // selected, not a "· cloud" entry
        Assert.DoesNotContain(labels, l => l.Contains("cloud", StringComparison.Ordinal));
        cleanup.SelectedIndex = labels.IndexOf("Rule-based tidy");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LlmSettings.NoProvider, FileProvider(scratch));
        flyout.Close();

        flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        cleanup = Picker(flyout, "Clean-up");
        cleanup.SelectedIndex = Labels(cleanup).IndexOf(SimpleView.OffAdvancedLabel);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LlmSettings.Off, FileProvider(scratch));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (lens on d6b4ea8): the tray panel's models line and the icon's tooltip say "no clean-up", never "off", which read as
    /// Dictum itself being off; and the closed Advanced clean-up picker shows the Off entry through the picker's ellipsis template.
    /// </summary>
    [Fact]
    public Task The_tray_line_says_no_clean_up_and_the_off_entry_is_trimmed() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "off" }, "ui": { "view": "advanced" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Equal("no clean-up", shell.Models.CurrentCleanupName);
        Assert.Contains(" · no clean-up · ", shell.Status.Models, StringComparison.Ordinal);
        Assert.Contains(" · no clean-up · ", shell.Status.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(" · off · ", shell.Status.Models, StringComparison.Ordinal);

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        flyout.UpdateLayout();
        var cleanup = Picker(flyout, "Clean-up");
        var shown = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(cleanup).OfType<TextBlock>().Single(t => t.Text == SimpleView.OffAdvancedLabel);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, shown.TextTrimming);
        Assert.True(shown.Bounds.Width <= cleanup.Bounds.Width, $"{shown.Bounds.Width:0} of {cleanup.Bounds.Width:0}");
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin: with clean-up off, the privacy lines are the approved "nothing leaves" ones (Settings > Privacy and the tray panel,
    /// with its lock), and Settings > Clean-up shows the off status and no cloud "On" line.
    /// </summary>
    [Fact]
    public Task The_privacy_lines_say_nothing_leaves() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "off" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.True(shell.NothingLeaves);
        Assert.Null(shell.CleanupWaitingForKey);

        var window = Page(shell, "Privacy");
        Assert.Contains("Nothing leaves this PC: speech recognition and clean-up both run here.", Lines(window));
        window.ShowPage("Clean-up");
        var cleanUp = Lines(window);
        Assert.Contains(TranscriptCleaner.OffStatus, cleanUp);
        Assert.DoesNotContain(cleanUp, l => l.StartsWith("On: ", StringComparison.Ordinal) || l.StartsWith("On, ", StringComparison.Ordinal));
        window.Close();

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var privacy = All<TextBlock>(flyout).Single(t => t.Text == "Nothing leaves this PC");
        Assert.True(privacy.IsEffectivelyVisible);
        flyout.Close();
        shell.Dispose();
    });
}
