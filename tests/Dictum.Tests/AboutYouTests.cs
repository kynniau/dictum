using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.App;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// About you (SPEC §16.7, 21/10/2026): context.txt, a paragraph about the speaker that the clean-up prompt carries after the
/// dictionary line, for spelling and term choice only; edited in Settings > Personal, watched, listed in Storage and removed
/// with everything else, never logged; and the validator keeps it to spelling.
/// </summary>
public class AboutYouTests
{
    private const string British = "I am a financial planner in the United Kingdom.";
    private const string Line = "Context about the speaker, for choosing spellings and terms only; never add, answer or change what was said: ";

    private static (string Prompt, ValidationContext Validation) Checks(PersonalText personal) =>
        personal.ForCleanup(new ProfanitySettings()).Checks(new LlmSettings(), _ => { });

    private static PersonalText Loaded(Scratch scratch) => PersonalText.Load(scratch.Paths, _ => { });

    // ---- The prompt line ----

    [Fact]
    public void The_prompt_ends_with_the_about_you_line_after_the_dictionary_line()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.DictionaryFile, "Kynni\n");
        SpeakerContext.Save(scratch.Paths.ContextFile, British);
        var (prompt, validation) = Checks(Loaded(scratch));
        Assert.Equal(Line, CleanupPrompt.ContextLinePrefix); // PROPOSED wording, pinned word for word
        Assert.EndsWith("\nKnown names and terms, keep their spelling: Kynni.\n" + Line + British, prompt);
        Assert.StartsWith(CleanupPrompt.Default, prompt);
        Assert.Equal(British, validation.AboutYou);
    }

    [Theory]
    [InlineData(null)] // no file
    [InlineData("")] // an empty file
    [InlineData("# only the header\n\n   \n")]
    public void Without_text_the_prompt_has_no_about_you_line(string? content)
    {
        using var scratch = new Scratch();
        if (content is not null) File.WriteAllText(scratch.Paths.ContextFile, content);
        var (prompt, validation) = Checks(Loaded(scratch));
        Assert.Equal(CleanupPrompt.Default, prompt);
        Assert.DoesNotContain("Context about the speaker", prompt);
        Assert.Null(validation.AboutYou);
    }

    [Fact]
    public void A_custom_prompt_file_and_grammar_assist_keep_the_line_last()
    {
        using var scratch = new Scratch();
        var promptFile = Path.Combine(scratch.Root.FullName, "prompt.txt");
        File.WriteAllText(promptFile, "My own prompt.");
        var personal = new PersonalText(new PersonalDictionary(["Kynni"]), Corrections.Empty, ProfanityFilter.Empty, context: SpeakerContext.Of(British));
        var (prompt, _) = personal.ForCleanup(new ProfanitySettings()).Checks(new LlmSettings { PromptFile = promptFile, GrammarAssist = true }, _ => { });
        Assert.Equal("My own prompt.\n" + CleanupPrompt.GrammarAssist + "\nKnown names and terms, keep their spelling: Kynni.\n" + Line + British, prompt);
    }

    // ---- The cap and the paragraph ----

    [Fact]
    public void Text_is_one_trimmed_paragraph()
    {
        Assert.Equal("I am a planner. In London.", SpeakerContext.Normalise("  I am a planner.\r\n\r\n\tIn   London.  "));
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.ContextFile, "# a comment\nI am a financial planner\nin the United Kingdom.\n");
        Assert.Equal(British, Loaded(scratch).Context.Text);
    }

    [Fact]
    public void Five_hundred_characters_are_kept_and_five_hundred_and_one_refused()
    {
        Assert.Equal(500, SpeakerContext.MaxChars);
        var fits = new string('a', 499) + ".";
        var over = new string('a', 500) + ".";
        Assert.Null(SpeakerContext.Check(fits));
        Assert.Equal("This is 501 characters; the limit is 500. Shorten it and save again.", SpeakerContext.Check(over));
        Assert.Equal(fits, SpeakerContext.Of(fits).Text);
        Assert.Null(SpeakerContext.Of(over).Text); // never cut: half a sentence is not given to the model

        using var scratch = new Scratch();
        var service = new PersonalService(scratch.Paths, NullLog.Instance, () => new AppSettings(), null!, () => { }, () => false);
        Assert.Equal("This is 501 characters; the limit is 500. Shorten it and save again.", service.SaveContext(over));
        Assert.False(File.Exists(scratch.Paths.ContextFile));
        Assert.Null(service.SaveContext("  " + fits + "\n"));
        Assert.Equal(fits, Loaded(scratch).Context.Text);
        Assert.Equal("About you cannot start with #: in context.txt that marks a comment line.", service.SaveContext("#1 broker in town"));
    }

    [Fact]
    public void A_file_over_the_cap_is_left_out_whole_and_the_warning_gives_only_its_length()
    {
        using var scratch = new Scratch();
        var secret = "Zorblax " + new string('b', 600);
        File.WriteAllText(scratch.Paths.ContextFile, secret);
        var warnings = new List<string>();
        var personal = PersonalText.Load(scratch.Paths, warnings.Add);
        Assert.Null(personal.Context.Text);
        Assert.Equal(CleanupPrompt.Default, Checks(personal).Prompt);
        Assert.Equal(["context.txt is 608 characters, over the 500-character limit; it is left out of the clean-up until it is shortened."], warnings.Where(w => w.Contains("context.txt")));
        Assert.DoesNotContain(warnings, w => w.Contains("Zorblax"));
        Assert.Equal(secret, SpeakerContext.ReadForEditing(scratch.Paths.ContextFile)); // Settings still shows it, to be shortened
    }

    [Fact]
    public void Saving_nothing_deletes_the_file()
    {
        using var scratch = new Scratch();
        var reloads = 0;
        var service = new PersonalService(scratch.Paths, NullLog.Instance, () => new AppSettings(), null!, () => reloads++, () => false);
        Assert.Null(service.SaveContext(British));
        Assert.StartsWith("# Dictum About you", File.ReadAllText(scratch.Paths.ContextFile));
        Assert.Null(service.SaveContext("  \n "));
        Assert.False(File.Exists(scratch.Paths.ContextFile));
        Assert.Equal(2, reloads);
    }

    // ---- Watched, listed, removed ----

    [Theory]
    [InlineData(WatcherChangeTypes.Changed, "context.txt", null)]
    [InlineData(WatcherChangeTypes.Created, "context.txt", null)]
    [InlineData(WatcherChangeTypes.Deleted, "context.txt", null)] // deleting it switches About you off at once
    [InlineData(WatcherChangeTypes.Renamed, "context.txt.bak", "context.txt")]
    public void The_watcher_reloads_on_context_txt(WatcherChangeTypes change, string name, string? oldName) =>
        Assert.True(AppRules.ReloadsOn(change, name, oldName));

    [Fact]
    public void Storage_lists_it_and_the_removal_plans_that_take_personal_files_delete_it()
    {
        using var scratch = new Scratch();
        SpeakerContext.Save(scratch.Paths.ContextFile, British);
        var settings = new AppSettings();
        Assert.Contains(PathRegistry.Locations(scratch.Paths, settings),
            l => l.Path == scratch.Paths.ContextFile && l.Category == DataCategory.PersonalFiles && l.Description == "About you text");
        Assert.Contains(RemovalPlan.Build(scratch.Paths, settings, RemovalScope.Everything).Items, i => PathRegistry.SamePath(i.Path, scratch.Paths.ContextFile));
        Assert.Contains(RemovalPlan.Build(scratch.Paths, settings, RemovalScope.AllDataIncludingSettings).Items, i => PathRegistry.SamePath(i.Path, scratch.Paths.ContextFile));
        Assert.DoesNotContain(RemovalPlan.Build(scratch.Paths, settings, RemovalScope.AllData).Items, i => PathRegistry.SamePath(i.Path, scratch.Paths.ContextFile));
        RemovalPlan.Build(scratch.Paths, settings, RemovalScope.Everything).Execute();
        Assert.False(File.Exists(scratch.Paths.ContextFile));
    }

    // ---- The validator ----

    private const string Added = "reply adds a dictionary term the speaker did not say";
    private const string Swapped = "reply swaps a dictated word for another while About you is on";
    private const string FromAboutYou = "reply adds a word from About you that the speaker did not say";

    // DictionaryInsertionValidatorTests' u3 case: the model wrote the company name over "a name" and after "by". With About you
    // in the prompt the dictionary-term rule still runs and still rejects it.
    private const string U3 = "It learns my words too. Add a name to the dictionary once. It's spelled right every time. Calibration tells me " +
        "which model he has my accent best, and the whole thing is free. And open source. Dictum, by Kynni.";

    [Fact]
    public void The_dictionary_term_rule_still_rejects_an_inserted_term_with_about_you_present()
    {
        var personal = new PersonalText(new PersonalDictionary(["Dictum", "Kynni", "Kynni Pty Ltd"]), Corrections.Empty, ProfanityFilter.Empty,
            context: SpeakerContext.Of("I build Dictum at Kynni Pty Ltd, an Australian software company."));
        var validation = Checks(personal).Validation;
        Assert.NotNull(validation.AboutYou);
        Assert.Equal(["Dictum", "Kynni", "Kynni Pty Ltd"], validation.DictionaryTerms!);
        Assert.Equal(Added, CleanupValidator.Validate(U3, "It learns my words too. Add Kynni Pty Ltd to the dictionary once. It's spelled right every time. " +
            "Calibration tells me which model has my accent best, and the whole thing is free. Dictum, by Kynni Pty Ltd.", validation).Rejection);
    }

    private static string? Reject(string raw, string reply, string? aboutYou) =>
        CleanupValidator.Validate(raw, reply, new ValidationContext(null, AboutYou: aboutYou)).Rejection;

    // About you bench, 21/10/2026, verbatim replies.
    [Fact]
    public void A_swap_about_you_caused_is_rejected_and_the_same_reply_without_it_is_not_checked()
    {
        Assert.Equal(Swapped, Reject("check the lvr with the lender", "Check the loan with the lender.", British)); // qwen2.5-1.5b
        Assert.Equal(Swapped, Reject("hargreaves lansdown holds his stocks and shares isa",
            "Hargreaves Lansdown holds his stocks and shares at the Australian Securities Exchange.", British)); // qwen2.5-0.5b
        Assert.Null(Reject("check the lvr with the lender", "Check the loan with the lender.", null)); // the rule runs only with About you
    }

    [Fact]
    public void A_word_of_about_you_the_speaker_did_not_say_is_rejected()
    {
        const string australian = "I work for an Australian finance company dealing with Australian lenders, bank policy and terminology.";
        Assert.Equal(FromAboutYou, Reject("hargreaves lansdown holds his stocks and shares isa",
            "Hargreaves Lansdown holds his stocks and shares at the Australian Stock Exchange.", australian)); // qwen2.5-0.5b
        Assert.Equal(FromAboutYou, Reject("send it to the client today", "Send it to the finance client today.", australian));
        Assert.Null(Reject("send it to the finance team today", "Send it to the finance team today.", australian)); // said: not an addition
    }

    [Theory]
    [InlineData("i spoke to westpac about the offset account", "I spoke to Westpac about the offset account.")]
    [InlineData("can you check whether comm bank has the better rate", "Can you check whether CommBank has the better rate?")]
    [InlineData("we compared legal and general against vitality", "We compared Legal & General against Vitality.")]
    [InlineData("the client has a pre approval with nab", "The client has a pre-approval with NAB.")]
    [InlineData("anz will need two payslips", "ANZ will need 2 payslips.")]
    [InlineData("i'm gonna call aviva", "I'm going to call Aviva.")]
    [InlineData("send it on tuesday i mean wednesday", "Send it on Wednesday.")] // a self-correction drops a word and adds none
    [InlineData("um so royal london and zurich both quoted", "Royal London and Zurich both quoted.")]
    [InlineData("her isa is with hargreaves lansdown", "Her ISA is with Hargreaves Lansdown.")]
    public void A_respelling_is_accepted_with_about_you(string raw, string reply) => Assert.Null(Reject(raw, reply, British));

    // The ceiling (simplification comment at CleanupValidator.AboutYouRejection): a rephrasing that swaps a word is rejected
    // while About you is on and the tidy is typed; measured twice on the bench (qwen2.5-1.5b, both texts).
    [Fact]
    public void A_rephrasing_that_swaps_a_word_is_rejected_while_about_you_is_on() =>
        Assert.Equal(Swapped, Reject("can you check whether commbank or bankwest has the better variable rate for this borrower",
            "Can you check which has the better variable rate, CommBank or BankWest, for this borrower?", British));

    // ---- Cloud: the text travels with the dictation ----

    [Theory]
    [InlineData(LlmSettings.OpenAiCompatible)]
    [InlineData(LlmSettings.Anthropic)]
    public async Task A_cloud_clean_up_sends_about_you_in_its_prompt(string provider)
    {
        var personal = new PersonalText(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty, context: SpeakerContext.Of(British));
        var settings = new AppSettings
        {
            Llm = provider == LlmSettings.Anthropic
                ? new LlmSettings { Provider = provider, CloudConsent = "api.anthropic.com" }
                : new LlmSettings { Provider = provider, BaseUrl = "http://localhost:11434/v1", Model = "m" },
        };
        var body = provider == LlmSettings.Anthropic
            ? """{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"Hello there."}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""
            : """{"choices":[{"message":{"role":"assistant","content":"Hello there."},"finish_reason":"stop"}]}""";
        var handler = new Capturing(body);
        using var http = new HttpClient(handler);
        var secrets = SecretStore.FromContents("ANTHROPIC_API_KEY=k-test-123456\n", null, _ => null);
        var cleaner = Dictum.Cli.CliCommands.Cleaner(settings, secrets, http, NullLog.Instance, () => null, personal);
        await cleaner.CleanAsync("hello there", TestContext.Current.CancellationToken);
        Assert.Contains("for choosing spellings and terms only", handler.Sent);
        Assert.Contains("United Kingdom", handler.Sent);
    }

    private sealed class Capturing(string body) : HttpMessageHandler
    {
        public string Sent { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent += await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // ---- Settings > Personal, and the log ----

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static List<string> VisibleLines(Window window) => All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    [Fact]
    public void The_proposed_strings_are_pinned_word_for_word()
    {
        Assert.Equal("About you", PersonalPage.CardTitle);
        Assert.Equal("Tell the clean-up model who you are and what you talk about, so it picks the right spellings and terms. Example: I work for " +
                     "an Australian finance company: Australian lenders, bank names and mortgage terms. This never adds words you did not say.", PersonalPage.Help);
        Assert.Equal("I work for an Australian finance company: Australian lenders, bank names and mortgage terms.", PersonalPage.Example);
        Assert.Equal("Your About you text is sent along with your dictation when cloud clean-up is on.", PersonalPage.CloudNote);
    }

    [Fact]
    public Task The_card_round_trips_through_context_txt_and_the_log_never_holds_the_text() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var log = new RecordingLog();
        var (shell, _, _) = scratch.Shell(log: log);
        try
        {
            shell.Reload(userRequested: false);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Personal");
            var box = All<TextBox>(window).Single(b => AutomationProperties.GetName(b) == "About you");
            Assert.Equal(PersonalPage.Example, box.Watermark);
            Assert.Contains(PersonalPage.Help, VisibleLines(window));
            Assert.Contains(PersonalPage.CloudNote, VisibleLines(window));

            // Save: the file holds the paragraph, the clean-up in use carries it, the page says so.
            box.Text = "I am a financial planner\nin the United Kingdom.  ";
            Click(window, "Save");
            Assert.Equal(British, Loaded(scratch).Context.Text);
            Assert.EndsWith(Line + British, shell.Cleaner.SystemPrompt);
            Assert.Equal(British, box.Text);
            Assert.Contains(PersonalPage.Saved, VisibleLines(window));

            // An edit in the file reaches the box (the watcher's reload).
            SpeakerContext.Save(scratch.Paths.ContextFile, "I am a mortgage broker in Sydney.");
            shell.Reload(userRequested: false);
            Assert.Equal("I am a mortgage broker in Sydney.", box.Text);
            Assert.EndsWith(Line + "I am a mortgage broker in Sydney.", shell.Cleaner.SystemPrompt);

            // Over the cap: refused with the reason, the file unchanged, the typing kept.
            var tooLong = new string('x', 501);
            box.Text = tooLong;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains("501 characters: 1 over the limit of 500.", VisibleLines(window));
            Click(window, "Save");
            Assert.Contains("This is 501 characters; the limit is 500. Shorten it and save again.", VisibleLines(window));
            Assert.Equal("I am a mortgage broker in Sydney.", Loaded(scratch).Context.Text);
            Assert.Equal(tooLong, box.Text);

            // Clear: the file is deleted and the prompt loses the line.
            Click(window, "Clear");
            Assert.False(File.Exists(scratch.Paths.ContextFile));
            Assert.DoesNotContain("Context about the speaker", shell.Cleaner.SystemPrompt);
            Assert.Contains(PersonalPage.Cleared, VisibleLines(window));
            window.Close();

            // The log has the lengths, never the text.
            Assert.Contains("Saved About you (47 characters).", log.All);
            Assert.Contains("about you 33 character(s)", log.All);
            foreach (var said in new[] { "financial planner", "United Kingdom", "mortgage broker", "Sydney", "xxxxxxxx" })
                Assert.DoesNotContain(said, log.All);
        }
        finally
        {
            shell.Dispose();
        }
    });
}
