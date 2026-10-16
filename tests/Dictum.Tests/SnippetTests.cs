using Dictum.App;
using Dictum.Audio;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Snippets (SPEC §16.6): snippets.txt, pasted exactly as written, after corrections and the profanity filter.</summary>
public class SnippetTests
{
    private static Corrections Snippets(params string[] lines) => new(Corrections.Parse(lines, null, "snippets.txt", lineBreaks: true), verbatim: true);

    /// <summary>The service behind the Add-a-snippet window, over a scratch profile, with the profanity mode given.</summary>
    private static PersonalService Service(Scratch scratch, string profanityMode = ProfanitySettings.Mask)
    {
        var settings = new AppSettings { Profanity = new ProfanitySettings { Mode = profanityMode } };
        var log = NullLog.Instance;
        var coordinator = new ModelCoordinator(scratch.Paths, new ModelManager(log), new LocalLlmManager(log), new HttpClient(), log, () => settings, null,
            new ScriptedPrompts(), _ => null, () => { }, () => { }, () => { }, (_, _) => { }, gpuMachine: () => false);
        var personal = new PersonalService(scratch.Paths, log, () => settings, coordinator, reload: () => { }, gpuMachine: () => false);
        personal.Load();
        return personal;
    }

    [Fact]
    public void A_snippet_is_pasted_exactly_even_at_a_sentence_start()
    {
        var snippets = Snippets("my email => kyle@example.com", "my calendly => https://calendly.com/example/30min");
        Assert.True(snippets.Verbatim);
        Assert.Equal("https://calendly.com/example/30min works. Email kyle@example.com.", snippets.Apply("My calendly works. Email MY EMAIL."));
        // Whole words: "my emails" is another word, while "my email address" contains the phrase and is expanded.
        Assert.Equal("Email, my emails and kyle@example.com address.", snippets.Apply("Email, my emails and my email address."));

        // The same line as a correction would capitalise the URL at a sentence start: that is why snippets are verbatim.
        var correction = new Corrections(Corrections.Parse(["my calendly => https://calendly.com/example/30min"]));
        Assert.Equal("Https://calendly.com/example/30min works.", correction.Apply("My calendly works."));
    }

    [Fact]
    public void Line_breaks_are_written_as_backslash_n_and_read_back()
    {
        var line = Corrections.SnippetLine(" my signature ", "Kyle Manson\r\nCFC Finance\n0400 000 000");
        Assert.Equal(@"my signature => Kyle Manson\nCFC Finance\n0400 000 000", line);
        Assert.Equal("Regards,\nKyle Manson\nCFC Finance\n0400 000 000", Snippets(line).Apply("Regards,\nmy signature"));
        Assert.Equal(@"a\nb", new Corrections(Corrections.Parse([@"x => a\nb"])).Apply("x")); // corrections.txt keeps the characters
    }

    [Fact]
    public void Snippets_run_last_so_nothing_rewrites_the_pasted_text_and_a_correction_can_repair_the_trigger()
    {
        var corrections = new Corrections(Corrections.Parse(["kinney => Kynni", "calendar lee => calendly"]));
        var profanity = new ProfanityFilter(["damn"], "test");
        var snippets = Snippets("my calendly => https://calendly.com/kinney/30min", "my motto => damn the torpedoes");
        var personal = new PersonalText(PersonalDictionary.Empty, corrections, profanity, snippets);
        var mask = new ProfanitySettings { Mode = ProfanitySettings.Mask };

        Assert.Equal("Book Kynni at https://calendly.com/kinney/30min.", personal.Finish("Book kinney at my calendar lee.", mask));
        Assert.Equal("D*** it. damn the torpedoes", personal.Finish("Damn it. my motto", mask)); // dictated word masked, typed text kept
        Assert.Contains("snippets 2", personal.Summary(mask));
    }

    [Fact]
    public void A_bad_line_is_reported_under_the_file_name_without_its_text()
    {
        var warnings = new List<string>();
        Assert.Empty(Corrections.Parse(["no arrow in this line"], warnings.Add, "snippets.txt", lineBreaks: true).ToList());
        var warning = Assert.Single(warnings);
        Assert.StartsWith("snippets.txt:", warning);
        Assert.DoesNotContain("no arrow", warning);
    }

    [Fact]
    public void The_file_loads_verbatim_a_missing_file_is_empty_and_it_is_registered_with_the_personal_files()
    {
        using var scratch = new Scratch();
        Assert.Empty(Corrections.LoadSnippets(scratch.Paths.SnippetsFile).Rules);
        Assert.Equal(PersonalText.Empty.Snippets.Rules.Count, PersonalText.Load(scratch.Paths, _ => { }).Snippets.Rules.Count);

        TextList.Append(scratch.Paths.SnippetsFile, [Corrections.SnippetLine("my email", "kyle@example.com")], Corrections.SnippetsHeader);
        var loaded = Corrections.LoadSnippets(scratch.Paths.SnippetsFile);
        Assert.True(loaded.Verbatim);
        Assert.Equal("kyle@example.com", Assert.Single(loaded.Rules).Written);
        Assert.StartsWith("# Dictum snippets", File.ReadAllText(scratch.Paths.SnippetsFile));

        Assert.Contains(PathRegistry.Locations(scratch.Paths, new AppSettings()),
            l => l.Path == scratch.Paths.SnippetsFile && l.Category == DataCategory.PersonalFiles);
    }

    [Theory]
    [InlineData("", "x", "Type the words")]
    [InlineData("# note", "x", "comment")]
    [InlineData("a => b", "x", "=>")]
    [InlineData("...", "x", "letter or a digit")]
    [InlineData("my email", "", "Type what")]
    [InlineData("my\nemail", "x", "one line")] // the phrase is one line of snippets.txt: a break in it would split the line
    [InlineData("my\r\nemail", "x", "one line")]
    [InlineData("my email\r", "x", "one line")]
    public void A_snippet_needs_a_one_line_phrase_with_a_word_in_it_and_some_text(string phrase, string text, string fragment)
    {
        using var scratch = new Scratch();
        Assert.Contains(fragment, Service(scratch).CheckNewSnippet(phrase, text));
    }

    [Fact]
    public void A_good_snippet_passes_the_check()
    {
        using var scratch = new Scratch();
        Assert.Null(Service(scratch).CheckNewSnippet("my email", "kyle@example.com"));
    }

    [Fact]
    public void A_phrase_the_profanity_filter_would_change_is_refused()
    {
        // The filter runs before the snippets, so a masked or removed phrase could never match.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.ProfanityOverrideFile, "damn\n");
        Assert.Equal("The profanity filter would change these words before the snippet runs; choose another phrase.",
            Service(scratch, ProfanitySettings.Mask).CheckNewSnippet("damn it", "x"));
        Assert.NotNull(Service(scratch, ProfanitySettings.Remove).CheckNewSnippet("Damn it", "x"));
        Assert.Null(Service(scratch, ProfanitySettings.Mask).CheckNewSnippet("darn it", "x"));
        Assert.Null(Service(scratch, ProfanitySettings.Off).CheckNewSnippet("damn it", "x")); // the filter is off: it changes nothing
    }

    [Fact]
    public void A_phrase_a_correction_would_change_is_refused_but_a_change_of_case_passes()
    {
        // Corrections also run before the snippets: "my e mail" is "my email" by then, so its snippet could never fire.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.CorrectionsFile, "e mail => email\nkynni => Kynni\n");
        var personal = Service(scratch);
        Assert.Equal("A correction in corrections.txt changes these words before the snippet runs; use the corrected form as the phrase.",
            personal.CheckNewSnippet("my e mail", "kyle@example.com"));
        Assert.Equal("Send it to my email.", personal.Finish("Send it to my e mail.")); // why: the snippet would see this
        Assert.Null(personal.CheckNewSnippet("my email", "kyle@example.com")); // the corrected form

        Assert.Null(personal.CheckNewSnippet("my kynni link", "https://kynni.au")); // a correction that only changes case
        personal.AddSnippet("my kynni link", "https://kynni.au");
        personal.Load(); // this service's reload callback does nothing
        Assert.Equal("Book at https://kynni.au.", personal.Finish("Book at my kynni link.")); // snippets match any case: it fires
    }

    [Fact]
    public void A_backslash_is_written_as_two_and_read_back_as_typed()
    {
        var line = Corrections.SnippetLine("my share", @"\\server\new-clients");
        Assert.Equal(@"my share => \\\\server\\new-clients", line); // without the escape, "\new" would read back as a line break
        Assert.Equal(@"Save it to \\server\new-clients.", Snippets(line).Apply("Save it to my share."));
        Assert.Equal("a\\nb\nc", Snippets(Corrections.SnippetLine("x", "a\\nb\nc")).Apply("x")); // a typed \n stays two characters
    }

    [Fact]
    public void A_snippet_keeps_a_line_break_at_either_end_or_one_that_is_all_of_it()
    {
        var newLine = Snippets(@"new line => \n");
        Assert.Equal("\n", Assert.Single(newLine.Rules).Written);
        Assert.Equal("Thanks \n Kyle", newLine.Apply("Thanks new line Kyle")); // the spaces around the phrase stay where they were
        Assert.Equal("\nKyle Manson\n", Snippets(@"my sign off => \nKyle Manson\n").Apply("my sign off"));
        Assert.Equal("Kynni", new Corrections(Corrections.Parse(["kinney =>   Kynni"])).Rules.Single().Written); // corrections still trim
    }

    [Fact]
    public async Task The_clean_up_sees_the_phrase_as_spoken_and_the_paste_has_the_snippet_text()
    {
        const string raw = "send it to my email please";
        var client = FakeLlmClient.Replying("Send it to my email, please.");
        var cleaner = new TranscriptCleaner(client, CleanupPrompt.Default, TimeSpan.FromSeconds(1), NullLog.Instance, "fake");
        var personal = new PersonalText(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty, Snippets("my email => kyle@example.com"));
        var inserted = new List<string>();
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber(raw)), () => cleaner,
            (text, _) =>
            {
                inserted.Add(text);
                return Task.CompletedTask;
            },
            () => new AppSettings(), NullLog.Instance, finish: text => personal.Finish(text, new ProfanitySettings()));

        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(new CapturedAudio(new float[32000], 0.2f), CancellationToken.None, _ => { }));
        Assert.Equal(1, client.Calls);
        Assert.Equal(raw, client.LastUser); // the clean-up provider received the spoken phrase
        Assert.DoesNotContain("kyle@example.com", client.LastUser + client.LastSystem, StringComparison.Ordinal); // never the snippet
        Assert.Equal(["Send it to kyle@example.com, please."], inserted);
    }
}
