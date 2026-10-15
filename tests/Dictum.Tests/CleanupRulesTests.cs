using Dictum.Llm;

namespace Dictum.Tests;

/// <summary>The `none` provider and fallback tier (SPEC §5): conservative by design.</summary>
public class DeterministicCleanerTests
{
    [Theory]
    // The fox keeps "you know": a multi-word filler is removed only between a sentence start or comma and a comma.
    [InlineData("um so the quick brown fox uh jumps over the lazy dog you know", "So the quick brown fox jumps over the lazy dog you know.")]
    // Lens pass 3 (15/10/2026): each of these was damaged by the earlier rules; the tidy must leave meaning alone.
    [InlineData("I mean it.", "I mean it.")]
    [InlineData("You know the answer.", "You know the answer.")]
    [InlineData("Kind of Blue is my favourite album.", "Kind of Blue is my favourite album.")]
    [InlineData("Take him to the ER now.", "Take him to the ER now.")]
    [InlineData("Use a 10 mm drill bit.", "Use a 10 mm drill bit.")]
    [InlineData("We need a new line of credit.", "We need a new line of credit.")]
    [InlineData("i.e. the first one", "i.e. the first one.")]
    [InlineData("We need a new line.", "We need a new line.")]
    [InlineData("meet at 9 a.m. tomorrow", "Meet at 9 a.m. tomorrow.")]
    [InlineData("a 10 ah battery", "A 10 ah battery.")]
    [InlineData("er, the plan", "Er, the plan.")]
    // What it does remove or apply.
    [InlineData("uh hello there", "Hello there.")]
    [InlineData("i think the the report is ready", "I think the report is ready.")]
    [InlineData("you know, we should ship it", "We should ship it.")]
    [InlineData("it was, sort of, a long day", "It was, a long day.")]
    [InlineData("Done. You know, it works", "Done. It works.")]
    [InlineData("so, um, the plan", "So, the plan.")]
    [InlineData("I mean we could try", "I mean we could try.")]
    [InlineData("First point. New line. Second point.", "First point.\nSecond point.")]
    [InlineData("Intro. New paragraph. Body text.", "Intro.\n\nBody text.")]
    [InlineData("Thanks, new line", "Thanks.\n")]
    [InlineData("first point new line second point", "First point new line second point.")]
    [InlineData("hello , world . how are you ?", "Hello, world. How are you?")]
    [InlineData("do you know the way", "Do you know the way.")]
    [InlineData("I know that that is true", "I know that that is true.")]
    [InlineData("it is kind of blue", "It is kind of blue.")]
    [InlineData("hmm, okay. erm, done", "Okay. Done.")]
    [InlineData("i'm sure i've seen it", "I'm sure I've seen it.")]
    [InlineData("Already clean.", "Already clean.")]
    [InlineData("um uh hmm", "")]
    [InlineData("", "")]
    public void Table(string raw, string expected) => Assert.Equal(expected, DeterministicCleaner.Clean(raw));
}

/// <summary>One test per SPEC §5 validation rule, plus the answer guards.</summary>
public class CleanupValidatorRuleTests
{
    private const string Raw = "um so we should move the quarterly review to thursday afternoon please";

    private static string? Reject(string raw, string reply) => CleanupValidator.Validate(raw, reply).Rejection;

    [Fact]
    public void Empty_is_rejected() => Assert.Contains("empty", Reject(Raw, "  "));

    [Fact]
    public void More_than_1_6x_the_words_is_rejected() =>
        Assert.Contains("1.6x", Reject(Raw, "So we should move the quarterly review to Thursday afternoon, please, and also the monthly review and the weekly sync and the daily stand-up too."));

    [Fact]
    public void Fewer_than_0_4x_the_words_is_rejected() => Assert.Contains("0.4x", Reject(Raw, "Move it."));

    [Fact]
    public void Short_inputs_are_exempt_from_the_word_ratio() => Assert.Null(Reject("uh yes", "Yes."));

    [Theory]
    [InlineData("Here is the text: So we should move the quarterly review to Thursday afternoon, please.")]
    [InlineData("Sure. So we should move the quarterly review to Thursday afternoon, please.")]
    [InlineData("Cleaned text: So we should move the quarterly review to Thursday afternoon, please.")]
    public void Commentary_at_the_start_is_rejected(string reply) => Assert.NotNull(Reject(Raw, reply));

    [Fact]
    public void System_prompt_wording_is_rejected() =>
        Assert.Contains("system prompt", Reject(Raw, "So we should move the review. Remove filler words um uh er like."));

    [Theory]
    [InlineData("So we should move the quarterly review to Thursday afternoon, please.<|im_end|>")]
    [InlineData("<|im_start|>assistant So we should move the quarterly review to Thursday afternoon, please.")]
    [InlineData("So we should move the quarterly review to Thursday afternoon, please.</s>")]
    [InlineData("So we should move the quarterly review to Thursday afternoon, please.<end_of_turn>")]
    public void Chat_template_tokens_are_rejected(string reply) => Assert.Contains("chat-template token", Reject(Raw, reply));

    [Fact]
    public void A_good_cleanup_passes() => Assert.Null(Reject(Raw, "So we should move the quarterly review to Thursday afternoon, please."));

    // Verbatim from this machine (15/10/2026): base.en raw, and qwen2.5-0.5b's reply on Vulkan that lost a sentence.
    private const string TwoSentences = "Umsow the quick brown foxer jumps over the lazy dog you know. The quarterly report is ready for review, please send comments by Friday.";

    [Fact]
    public void A_reply_that_drops_a_sentence_is_rejected() =>
        Assert.Contains("drops dictated content", Reject(TwoSentences, "The quarterly report is ready for review, please send comments by Friday."));

    [Fact]
    public void Fixing_misheard_words_is_not_dropping_content() =>
        Assert.Null(Reject(TwoSentences, "So the quick brown fox jumps over the lazy dog. The quarterly report is ready for review. Please send comments by Friday."));

    // Verbatim from this machine (15/10/2026): base.en raw of the 60.9 s clip with CPU forced, and the two cleanup replies
    // the overall 60% check accepted although they dropped whole sentences.
    private const string Sixty =
        "\"I'm okay so this is the update for the team.\" Other quarterly report is nearly finished and I think we can send it to the client by Friday afternoon. " +
        "The main change is the forecast section, which now uses the new numbers from finance. \"I'm we also need to check the appendix, because a couple of the " +
        "tables still show last year's figures.\" Sarah said she can look at that on Thursday. \"Ah the other thing is the budget meeting, which has moved to next " +
        "Tuesday at 10. Please let me know if that time doesn't work for you.\" And you know, if anyone has comments on the draft, send them through by Wednesday " +
        "so we have time to fix them. I'll book a room and send the invite this afternoon. Oh and one more thing, the new starter begins on Monday, so please say " +
        "hello and help them find their way around. Thanks everyone, talk soon.";

    [Fact]
    public void A_long_reply_that_drops_one_sentence_is_rejected() => // qwen2.5-1.5b, CPU
        Assert.Contains("drops a dictated sentence", Reject(Sixty,
            "I'm okay. This is the update for the team. The main change is the forecast section, which now uses the new numbers from finance. We also need to " +
            "check the appendix, because a couple of the tables still show last year's figures. Sarah said she can look at that on Thursday. The budget meeting " +
            "has moved to next Tuesday at 10. Please let me know if that time doesn't work for you. And if anyone has comments on the draft, send them through by " +
            "Wednesday so we have time to fix them. I'll book a room and send the invite this afternoon. The new starter begins on Monday, so please say hello " +
            "and help them find their way around. Thanks everyone, talk soon."));

    [Fact]
    public void A_long_reply_that_drops_and_repeats_sentences_is_rejected() => // qwen2.5-0.5b, CPU
        Assert.Contains("drops a dictated sentence", Reject(Sixty,
            "\"I'm okay so this is the update for the team.\"\n\"Other quarterly report is nearly finished and I think we can send it to the client by Friday afternoon.\"\n" +
            "\"The main change is the forecast section, which now uses the new numbers from finance.\"\n\"I'm we also need to check the appendix, because a couple " +
            "of the tables still show last year's figures.\"\n\"The other thing is the budget meeting, which has moved to next Tuesday at 10.\"\n\"Please let me " +
            "know if that time doesn't work for you.\"\n\"Ah the other thing is the budget meeting, which has moved to next Tuesday at 10.\"\n\"Please say hello " +
            "and help them find their way around.\"\n\"Thanks everyone, talk soon.\"\n\"Talk soon.\""));

    [Fact]
    public void A_full_cleanup_of_the_long_dictation_passes() =>
        Assert.Null(Reject(Sixty,
            "I'm okay, so this is the update for the team. Our quarterly report is nearly finished and I think we can send it to the client by Friday afternoon. " +
            "The main change is the forecast section, which now uses the new numbers from finance. We also need to check the appendix, because a couple of the " +
            "tables still show last year's figures. Sarah said she can look at that on Thursday. The other thing is the budget meeting, which has moved to next " +
            "Tuesday at 10. Please let me know if that time doesn't work for you. If anyone has comments on the draft, send them through by Wednesday so we have " +
            "time to fix them. I'll book a room and send the invite this afternoon. Oh, and one more thing, the new starter begins on Monday, so please say hello " +
            "and help them find their way around. Thanks everyone, talk soon."));

    [Theory]
    [InlineData("we analyzed the color behavior of the center", "We analysed the colour behaviour of the centre.")]
    [InlineData("um we analyzed the color behavior of the center and the theater catalog license",
        "We analysed the colour behaviour of the centre and the theatre catalogue licence.")]
    public void Australian_spelling_of_a_US_spelled_transcript_is_not_a_different_word(string raw, string reply) => Assert.Null(Reject(raw, reply));

    [Theory]
    [InlineData("colour", "color")]
    [InlineData("behaviours", "behaviors")]
    [InlineData("analysed", "analyzed")]
    [InlineData("organisation", "organization")]
    [InlineData("centred", "centered")]
    [InlineData("theatres", "theaters")]
    [InlineData("catalogue", "catalog")]
    [InlineData("licence", "license")]
    [InlineData("hour", "hour")]
    public void Spelling_variants_normalise_to_one_form(string word, string expected) => Assert.Equal(SpellingVariants.Normalise(expected), SpellingVariants.Normalise(word));

    [Fact]
    public void Scratch_that_does_not_hide_dropped_content() =>
        Assert.Contains("drops dictated content", Reject(
            "send the budget figures and the quarterly forecast and the revised staffing plan to the finance director and the board chair today. the colour is blue scratch that the colour is green.",
            "Send the figures to the director and the chair today. The colour is green."));

    [Theory]
    [InlineData("the colour is blue scratch that the colour is green", " the colour is green")]
    [InlineData("we met. book the room for friday. scratch that. use the kitchen", "we met. use the kitchen")]
    [InlineData("i'll call tom, scratch that, email him", " email him")]
    public void The_retracted_clause_is_removed_before_coverage(string raw, string expected) =>
        Assert.Equal(expected, CleanupValidator.WithoutRetractedClauses(raw));

    [Fact]
    public void Scratch_that_may_delete_a_sentence() =>
        Assert.Null(Reject("Book the large meeting room downstairs for Friday. Scratch that. We will use the small kitchen upstairs instead, and invite everyone from the finance team.",
            "We will use the small kitchen upstairs instead, and invite everyone from the finance team."));
}
