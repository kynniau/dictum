using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>SPEC §16.2: dictionary.txt → whisper's prompt and the cleanup prompt.</summary>
public class PersonalDictionaryTests
{
    [Fact]
    public void Prompt_from_a_file_with_comments_blanks_and_duplicates()
    {
        var dictionary = new PersonalDictionary(TextList.Parse("# my words\r\n\r\nKynni\r\n  Parramatta  \r\n# another comment\r\nkynni\r\nSiobhan\r\n"));
        Assert.Equal(["Parramatta", "kynni", "Siobhan"], dictionary.Terms); // the later "kynni" is the most recent entry
        Assert.Equal("Parramatta, kynni, Siobhan", dictionary.WhisperPrompt);
        Assert.Equal(3, dictionary.WhisperPromptTerms);
        Assert.Equal("Known names and terms, keep their spelling: Parramatta, kynni, Siobhan.", dictionary.CleanupLine);
        Assert.True(dictionary.Contains("KYNNI"));
    }

    [Fact]
    public void Over_budget_keeps_the_most_recently_added_terms_newest_last()
    {
        var terms = Enumerable.Range(1, 200).Select(i => $"Term{i:000}").ToList();
        var dictionary = new PersonalDictionary(terms);
        var kept = dictionary.WhisperPrompt!.Split(", ");
        Assert.Equal(PersonalDictionary.WhisperPromptTokens / PersonalDictionary.Estimate("Term001"), kept.Length); // 224 / 4 = 56
        Assert.Equal("Term200", kept[^1]);
        Assert.Equal(terms[^kept.Length..], kept);
        Assert.DoesNotContain("Term001", dictionary.WhisperPrompt);
        Assert.True(kept.Sum(PersonalDictionary.Estimate) <= PersonalDictionary.WhisperPromptTokens);
        Assert.True(dictionary.CleanupLine!.Length <= PersonalDictionary.CleanupLineChars);
        Assert.EndsWith("Term200.", dictionary.CleanupLine);
    }

    [Fact]
    public void Empty_or_missing_file_changes_no_prompt()
    {
        var empty = PersonalDictionary.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"));
        Assert.Null(empty.WhisperPrompt);
        Assert.Null(empty.CleanupLine);
        Assert.Equal(CleanupPrompt.Default, CleanupPrompt.Build(CleanupPrompt.Default, grammarAssist: false, empty.CleanupLine));
        Assert.Null(new PersonalDictionary(TextList.Parse("# only a comment\n\n   \n")).WhisperPrompt);
    }

    [Fact]
    public void Cleanup_prompt_gains_the_dictionary_line()
    {
        var line = new PersonalDictionary(["Kynni"]).CleanupLine;
        var prompt = CleanupPrompt.Build(CleanupPrompt.Default, grammarAssist: false, line);
        Assert.StartsWith(CleanupPrompt.Default, prompt);
        Assert.EndsWith("\nKnown names and terms, keep their spelling: Kynni.", prompt);
    }

    [Fact]
    public void Append_creates_the_file_with_a_header_and_reads_back()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dictate-dict-{Guid.NewGuid():N}", "dictionary.txt");
        try
        {
            TextList.Append(file, ["Kynni"], PersonalDictionary.FileHeader);
            File.AppendAllText(file, "Siobhan"); // a hand edit without a final line break
            TextList.Append(file, ["Parramatta"], PersonalDictionary.FileHeader);
            Assert.StartsWith("# Dictum personal dictionary", File.ReadAllText(file));
            Assert.Equal(["Kynni", "Siobhan", "Parramatta"], PersonalDictionary.Load(file).Terms);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}

/// <summary>SPEC §16.3: corrections.txt.</summary>
public class CorrectionsTests
{
    private static Corrections Rules(params string[] lines) => new(Corrections.Parse(lines));

    [Fact]
    public void Whole_word_only_ER_never_matches_her()
    {
        var corrections = Rules("ER => emergency room");
        Assert.Equal("Take her to the emergency room now.", corrections.Apply("Take her to the ER now."));
        Assert.Equal("Hermione and Herbert were here.", corrections.Apply("Hermione and Herbert were here."));
        Assert.Equal("emergency room, emergency room.", corrections.Apply("ER, er."));
    }

    [Fact]
    public void Case_insensitive_match_and_case_handling()
    {
        var corrections = Rules("kinney => Kynni", "gonna => going to");
        Assert.Equal("Ask Kynni first.", corrections.Apply("Ask kinney first."));
        Assert.Equal("Kynni said yes.", corrections.Apply("KINNEY said yes."));
        Assert.Equal("Going to be late, we're going to be late.", corrections.Apply("Gonna be late, we're gonna be late."));
    }

    [Fact]
    public void Overlapping_rules_apply_longest_first_and_never_chain()
    {
        var corrections = Rules("york => Yorkshire", "new york => NYC", "colour => color", "color => hue");
        Assert.Equal("I flew to NYC from Yorkshire.", corrections.Apply("I flew to New York from York."));
        Assert.Equal("NYC", corrections.Apply("new   york"));
        Assert.Equal("color hue", corrections.Apply("colour color")); // one pass: colour's output is not re-matched
    }

    [Fact]
    public void Lines_without_both_sides_are_ignored_and_a_later_line_wins()
    {
        var warnings = new List<string>();
        var corrections = new Corrections(Corrections.Parse(["no arrow here", " => orphan", "lonely =>", "teh => the", "teh => THE"], warnings.Add));
        Assert.Single(corrections.Rules);
        Assert.Equal("THE end", corrections.Apply("teh end"));
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w => Assert.DoesNotContain("orphan", w)); // the line's text never reaches the log
    }

    [Fact]
    public void Diff_of_a_known_pair_suggests_each_substitution()
    {
        var suggestions = CorrectionSuggestions.Suggest(
            "I met shiv on at the cafe, then emailed Kinney. Thanks",
            "I met Siobhan at the café, then emailed Kynni. Thanks!");
        Assert.Equal(["shiv on => Siobhan", "cafe => café", "Kinney => Kynni"], suggestions.Select(s => s.ToString()));
    }

    [Theory]
    [InlineData("Please send it", "Please send it now")] // pure insertion
    [InlineData("Please send it now", "Please send it")] // pure deletion
    [InlineData("thanks, see you", "Thanks! See you.")] // punctuation and capitals only
    [InlineData("one two three four five six", "a b c d e f")] // a rewrite of more than four words
    public void No_suggestion_for_changes_that_are_not_corrections(string original, string fixedText)
    {
        Assert.Empty(CorrectionSuggestions.Suggest(original, fixedText));
    }
}

/// <summary>SPEC §16.4: profanity.mode.</summary>
public class ProfanityFilterTests
{
    private static readonly ProfanityFilter Shipped = ProfanityFilter.Load(
        Path.Combine(Path.GetTempPath(), $"no-override-{Guid.NewGuid():N}.txt"), Path.Combine(AppContext.BaseDirectory, "profanity.txt"));

    [Fact]
    public void The_shipped_list_is_beside_the_exe_with_variants_listed()
    {
        Assert.Equal("shipped list", Shipped.Source);
        foreach (var word in new[] { "fuck", "fucks", "fucked", "fucking", "shit", "shitting", "ass", "cunt" }) Assert.Contains(word, Shipped.Words);
    }

    [Fact]
    public void Mask_keeps_the_first_letter_and_stars_the_rest()
    {
        Assert.Equal("What the f*** is this s***?", Shipped.Apply("What the fuck is this shit?", ProfanitySettings.Mask));
        Assert.Equal("F****** printer, it's b*******.", Shipped.Apply("Fucking printer, it's bullshit.", ProfanitySettings.Mask));
    }

    [Theory]
    [InlineData("Well, shit, that's bad.", "Well, that's bad.")]
    [InlineData("Fuck, that hurt.", "That hurt.")]
    [InlineData("That was fucking great.", "That was great.")]
    [InlineData("It's shit.", "It's.")]
    [InlineData("Done. Shit, it broke again.", "Done. It broke again.")]
    [InlineData("shit", "")]
    public void Remove_drops_the_word_and_tidies(string input, string expected)
    {
        Assert.Equal(expected, Shipped.Apply(input, ProfanitySettings.Remove));
    }

    /// <summary>
    /// A removed word that started a sentence takes its comma with it and passes on its capital, wherever a sentence starts: the
    /// rule the filter shares with the corrections and the hands-free boundary rule (SentenceStart, merge lens finding 9).
    /// </summary>
    [Theory]
    [InlineData("Done! Shit, it broke.", "Done! It broke.")]
    [InlineData("Done? Shit, it broke.", "Done? It broke.")]
    [InlineData("Done.\nShit, it broke.", "Done.\nIt broke.")]
    [InlineData("He said \"stop.\" Shit, it broke.", "He said \"stop.\" It broke.")]
    [InlineData("Done… Shit, it broke.", "Done… It broke.")]                           // an ellipsis (aligned 16/10/2026)
    [InlineData("He said “stop.” Shit, it broke.", "He said “stop.” It broke.")]       // a curly closing quote (aligned)
    [InlineData("It failed (again.) Shit, it broke.", "It failed (again.) It broke.")] // a closing bracket (aligned)
    public void Remove_finds_the_sentence_start_past_quotes_and_brackets(string input, string expected)
    {
        Assert.Equal(expected, Shipped.Apply(input, ProfanitySettings.Remove));
    }

    /// <summary>
    /// The danda and double danda end a sentence like a full stop (orchestrator, 16/10/2026: the languages lens
    /// found "वह कमीना।" filtered to "वह ।"). A one-word list, as an override profanity.txt would give.
    /// </summary>
    [Theory]
    [InlineData("वह कमीना।", "वह।")]                         // the mark attaches to the word before, as in "It's shit." → "It's."
    [InlineData("वह कमीना॥", "वह॥")]
    [InlineData("कमीना। मैं भूल गया।", "मैं भूल गया।")]       // a sentence of its own goes with its mark
    [InlineData("पहला। कमीना, दूसरा।", "पहला। दूसरा।")]      // a sentence start after a danda takes the comma with the word
    public void Remove_ends_a_sentence_at_a_danda_too(string input, string expected)
    {
        Assert.Equal(expected, new ProfanityFilter(["कमीना"], "test").Apply(input, ProfanitySettings.Remove));
    }

    [Theory]
    [InlineData("It's shit… really.", "It's… really.")] // aligned with the danda, 16/10/2026: was "It's … really."
    [InlineData("Shit… I forgot.", "I forgot.")]          // was "… I forgot."
    public void Remove_treats_an_ellipsis_after_the_word_like_a_full_stop(string input, string expected)
    {
        Assert.Equal(expected, Shipped.Apply(input, ProfanitySettings.Remove));
    }

    [Theory]
    [InlineData(ProfanitySettings.Mask)]
    [InlineData(ProfanitySettings.Remove)]
    public void Word_boundaries_leave_class_assess_and_Scunthorpe_alone(string mode)
    {
        const string text = "The class will assess the Scunthorpe shitake order, e.g. the arsenal.";
        Assert.Equal(text, Shipped.Apply(text, mode));
    }

    [Fact]
    public void Off_changes_nothing()
    {
        Assert.Equal("What the fuck.", Shipped.Apply("What the fuck.", ProfanitySettings.Off));
    }

    [Fact]
    public void The_override_file_replaces_the_shipped_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dictate-prof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var overrideFile = Path.Combine(dir, "profanity.txt");
            File.WriteAllText(overrideFile, "# mine\ndarn\nheck\n");
            var filter = ProfanityFilter.Load(overrideFile, Path.Combine(AppContext.BaseDirectory, "profanity.txt"));
            Assert.Equal($"override {overrideFile}", filter.Source);
            Assert.Equal("d*** it, what the fuck.", filter.Apply("darn it, what the fuck.", ProfanitySettings.Mask));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Finish_runs_the_corrections_before_the_profanity_filter()
    {
        // Orchestrator decision (15/10/2026): user intent first, so a correction applies even to a listed word.
        var personal = new PersonalText(PersonalDictionary.Empty, new Corrections(Corrections.Parse(["shit => stuff", "kinney => Kynni"])), Shipped);
        var mask = new ProfanitySettings { Mode = ProfanitySettings.Mask };
        Assert.Equal("Kynni said stuff.", personal.Finish("kinney said shit.", mask));
        var nameOnly = new PersonalText(PersonalDictionary.Empty, new Corrections(Corrections.Parse(["kinney => Kynni"])), Shipped);
        Assert.Equal("Kynni said s***.", nameOnly.Finish("kinney said shit.", mask)); // without a rule for it, the word is masked
    }
}
