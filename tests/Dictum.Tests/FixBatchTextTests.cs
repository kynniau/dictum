using Dictum.Personal;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>Fix batch, items 19, 20 and 22: corrections, the profanity filter, the dictionary and fix-last suggestions.</summary>
public class FixBatchTextTests
{
    private static readonly ProfanityFilter Shipped = ProfanityFilter.Load(
        Path.Combine(Path.GetTempPath(), $"no-override-{Guid.NewGuid():N}.txt"), Path.Combine(AppContext.BaseDirectory, "profanity.txt"));

    private static Corrections Rules(params string[] lines) => new(Corrections.Parse(lines));

    [Theory]
    [InlineData("Shit! I forgot the keys.", "I forgot the keys.")]
    [InlineData("Damn. The build failed again.", "The build failed again.")]
    [InlineData("The shit's hitting the fan.", "The hitting the fan.")]
    public void Remove_takes_a_sentence_of_its_own_and_an_apostrophe_suffix_with_the_word(string input, string expected) =>
        Assert.Equal(expected, Shipped.Apply(input, ProfanitySettings.Remove));

    [Fact]
    public void The_shipped_list_covers_common_compounds_and_leaves_the_dice_game_alone()
    {
        Assert.Equal("He's a f****** and a d******.", Shipped.Apply("He's a fuckwit and a dumbass.", ProfanitySettings.Mask));
        Assert.Equal("That s**** is s********.", Shipped.Apply("That shite is shithouse.", ProfanitySettings.Mask));
        Assert.Equal("We shot craps in Vegas.", Shipped.Apply("We shot craps in Vegas.", ProfanitySettings.Mask));
    }

    [Fact]
    public void A_correction_can_lower_case_a_word_mid_sentence_but_keeps_a_sentence_start_capital()
    {
        var bill = Rules("Bill => bill");
        Assert.Equal("Please pay the bill today.", bill.Apply("Please pay the Bill today."));
        Assert.Equal("Bill is due. Bill again.", bill.Apply("Bill is due. Bill again."));
    }

    [Fact]
    public void A_correction_never_matches_the_stem_of_a_contraction()
    {
        Assert.Equal("I don't know, ask Don.", Rules("don => Don").Apply("I don't know, ask don."));
        Assert.Equal("Ask Kynni's team.", Rules("kinney => Kynni").Apply("Ask Kinney's team.")); // a possessive still matches
        Assert.Equal("See you at ten o'clock.", Rules("clock => watch").Apply("See you at ten o'clock."));
        Assert.Equal("I won't go.", Rules("won => one").Apply("I won't go."));
    }

    [Fact]
    public void Overlapping_rules_with_a_shared_first_word_apply_longest_first()
    {
        var rules = Rules("new => knew", "new york => NYC");
        Assert.Equal("I flew to NYC.", rules.Apply("I flew to new york."));
        Assert.Equal("I knew it.", rules.Apply("I new it."));
    }

    [Fact]
    public void Corrections_run_before_the_profanity_filter_so_a_misheard_listed_word_can_be_fixed()
    {
        var personal = new PersonalText(PersonalDictionary.Empty, Rules("ass => ask", "darn => damn"), Shipped);
        var mask = new ProfanitySettings { Mode = ProfanitySettings.Mask };
        Assert.Equal("can you ask him for the figures", personal.Finish("can you ass him for the figures", mask));
        Assert.Equal("d*** it", personal.Finish("darn it", mask)); // the filter still has the last word on what is pasted
    }

    [Fact]
    public void One_term_too_long_for_the_prompt_is_skipped_not_the_whole_dictionary()
    {
        var huge = string.Join(", ", Enumerable.Range(1, 80).Select(i => $"Client{i:00}"));
        var dictionary = new PersonalDictionary(["Kynni", "Siobhan", "Parramatta", huge]);
        Assert.Equal("Kynni, Siobhan, Parramatta", dictionary.WhisperPrompt);
        Assert.StartsWith(Dictum.Llm.CleanupPrompt.DictionaryLinePrefix + "Kynni, Siobhan, Parramatta", dictionary.CleanupLine);
        Assert.Equal(1, dictionary.SkippedTerms);
    }

    [Theory]
    [InlineData("its a good plan", "it's a good plan")]
    [InlineData("meet at 10.30", "meet at 10:30")]
    [InlineData("send an e-mail", "send an email")]
    public void Punctuation_inside_a_word_is_not_a_correction(string original, string fixedText) =>
        Assert.Empty(CorrectionSuggestions.Suggest(original, fixedText));
}
