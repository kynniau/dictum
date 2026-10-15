using Dictum.Llm;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Fix batch, items 12 and 19: the reply validator, held to what the dictation actually says.</summary>
public class FixBatchValidatorTests
{
    private static string? Reject(string raw, string reply, ValidationContext? context = null) => CleanupValidator.Validate(raw, reply, context).Rejection;

    // "Scratch that" (lens probes, 15/10/2026). Rows marked "real" are qwen2.5-1.5b replies the old rules accepted or rejected
    // wrongly; the rest are constructed to the same pattern.
    [Theory]
    [InlineData("the colour is blue scratch that the colour is green", "The colour is blue.")] // real
    [InlineData("The color is blue scratch that the color is green.", "The color is blue.")] // real, whisper base.en raw
    [InlineData("book the large meeting room downstairs for friday afternoon with the whole finance team scratch that cancel the booking",
        "Book the large meeting room downstairs for Friday afternoon with the whole finance team.")] // real
    [InlineData("book the large meeting room downstairs for friday. scratch that. we will use the small kitchen upstairs instead",
        "Book the large meeting room downstairs for Friday. We will use the small kitchen upstairs instead.")] // real: both kept
    public void A_reply_that_keeps_the_retracted_clause_or_drops_the_correction_is_rejected(string raw, string reply) =>
        Assert.NotNull(Reject(raw, reply));

    [Theory]
    [InlineData("book the large meeting room downstairs for friday afternoon with the whole finance team scratch that cancel the booking",
        "Cancel the booking.")] // real: rejected before as "0.16x"
    [InlineData("is the meeting on friday? scratch that, the meeting is on monday", "The meeting is on Monday.")] // rejected before: "question missing"
    [InlineData("send it to tom scratch that send it to jane", "Send it to Jane.")]
    [InlineData("the meeting is on tuesday scratch that wednesday", "The meeting is on Wednesday.")]
    [InlineData("we need three chairs scratch that four chairs", "We need four chairs.")]
    public void A_correct_retraction_is_accepted(string raw, string reply) => Assert.Null(Reject(raw, reply));

    [Fact]
    public void A_dropped_negation_or_a_changed_number_is_rejected()
    {
        Assert.NotNull(Reject("we should not sign the contract with acme today", "We should sign the contract with Acme today."));
        Assert.NotNull(Reject("please pay invoice 3301 for 250 dollars by the 15th", "Please pay invoice 3301 for 25 dollars by the 15th."));
        Assert.Null(Reject("i don't think we can make it on friday", "I do not think we can make it on Friday."));
        Assert.Null(Reject("the meeting is at 10.30 on the 15th", "The meeting is at 10:30 on the 15th."));
    }

    [Theory]
    [InlineData("there is a problem with the invoice", "Here is your text: There is a problem with the invoice.")]
    [InlineData("please ensure the invoices are paid by friday", "Sure! Please ensure the invoices are paid by Friday.")]
    [InlineData("make sure the report is ready by friday", "Sure. Make sure the report is ready by Friday.")]
    [InlineData("so we should move the quarterly review to thursday afternoon please",
        "So we should move the quarterly review to Thursday afternoon, please. (I removed the filler words.)")]
    [InlineData("so we should move the quarterly review to thursday afternoon please", "Output: So we should move the quarterly review to Thursday afternoon, please.")]
    [InlineData("send the figures to the auditors by friday", "Send the figures to the auditors by Friday.\nThanks.")] // real: the few-shot line
    [InlineData("so we should move the quarterly review to thursday afternoon please",
        "Here’s your text: So we should move the quarterly review to Thursday afternoon, please.")] // curly apostrophe
    [InlineData("ok see you tomorrow", "好的，明天见。")] // a script the dictation did not use
    public void Framing_labels_and_foreign_scripts_are_rejected(string raw, string reply) => Assert.NotNull(Reject(raw, reply));

    [Theory]
    [InlineData("um sure i'll be there at five", "Sure, I'll be there at five.")]
    [InlineData("dear john: the report is attached", "Dear John: the report is attached.")]
    [InlineData("i'll send it, we're done, it's fine, don't worry, that's all, and i can't stay",
        "I’ll send it, we’re done, it’s fine, don’t worry, that’s all, and I can’t stay.")] // curly apostrophes, rejected before
    public void What_the_speaker_said_is_not_framing(string raw, string reply) => Assert.Null(Reject(raw, reply));

    [Fact]
    public void Spoken_commands_quotes_and_a_final_line_break_survive()
    {
        Assert.Null(Reject("yes comma thanks full stop new line priya", "Yes, thanks.\nPriya")); // rejected before at 0.38x
        Assert.Equal("\"We will ship on Friday.\"", CleanupValidator.Validate("open quote we will ship on friday close quote", "\"We will ship on Friday.\"").Text);
        Assert.Equal("Thanks,\n", CleanupValidator.Validate("thanks comma new line", "Thanks,\n").Text);
    }

    [Fact]
    public void A_dictated_swear_word_may_not_be_dropped_unless_the_filter_removes_them()
    {
        const string raw = "that's fucking brilliant work from the team";
        const string reply = "That's fantastic work from the team.";
        Assert.NotNull(Reject(raw, reply, new ValidationContext(null, ["fucking"])));
        Assert.Null(Reject(raw, reply, new ValidationContext(null, null))); // profanity.mode = remove passes no list
    }

    [Fact]
    public void A_reply_that_echoes_the_prompt_actually_sent_is_rejected()
    {
        const string custom = "Rewrite the dictation as formal business English for our clients and keep it short.";
        Assert.NotNull(Reject("so we should move the quarterly review to thursday afternoon please",
            "Rewrite the dictation as formal business English: So we should move the quarterly review to Thursday afternoon, please.",
            new ValidationContext(custom)));
    }

    // Item 5, validator pins: each rule below had no test that depended on it alone.
    [Fact]
    public void The_answer_guard_rejects_a_reply_that_shares_too_few_words() =>
        Assert.Contains("shares too few words", Reject("send the report", "Please find attached the quarterly figures today."));

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    [InlineData(4, true)]
    [InlineData(3, false)]
    public void The_word_ratio_bounds_are_1_6x_and_0_4x(int replyWords, bool accepted)
    {
        const string raw = "b c d f g h j k l m"; // single letters: no content words, so only the ratio decides
        var words = raw.Split(' ');
        var reply = string.Join(' ', Enumerable.Range(0, replyWords).Select(i => words[i % words.Length]));
        Assert.Equal(accepted, Reject(raw, reply) is null);
    }

    [Fact]
    public void Dictations_under_eight_words_are_exempt_from_the_ratio()
    {
        Assert.Null(Reject("um uh er ok", "OK."));
        Assert.Null(Reject("yes yes yes", "Yes.")); // 0.33x: rejected but for the exemption (hesitations are not counted, so the row above is 1x)
    }
}

/// <summary>Fix batch, items 13 and 14: whisper's annotations and the rule-based tidy.</summary>
public class FixBatchTidyTests
{
    [Theory]
    [InlineData("[farting]")]
    [InlineData(" (water running) ")]
    [InlineData("[BEEP]")]
    [InlineData("[Music] (applause)")]
    public void A_segment_that_is_only_annotations_is_dropped(string segment) => Assert.Equal("", TranscriptText.Normalise([segment]));

    [Fact]
    public void Square_bracket_spans_are_removed_inside_speech() =>
        Assert.Equal("Hello there, see you soon.", TranscriptText.Normalise(["Hello [inaudible] there,", "[cough] see you soon."]));

    [Theory]
    [InlineData("Our ERM framework needs review", "Our ERM framework needs review.")]
    [InlineData("We trained an HMM on the data", "We trained an HMM on the data.")]
    [InlineData("Um, ERM is enterprise risk management", "ERM is enterprise risk management.")]
    [InlineData("The UM column is wrong", "The UM column is wrong.")]
    [InlineData("I flew to Wagga Wagga on Monday", "I flew to Wagga Wagga on Monday.")]
    [InlineData("We stayed in Woy Woy and then Kurri Kurri", "We stayed in Woy Woy and then Kurri Kurri.")]
    [InlineData("What it is is a scam", "What it is is a scam.")]
    [InlineData("We build on .NET and Azure", "We build on .NET and Azure.")]
    [InlineData("Use a .5 mm lead", "Use a .5 mm lead.")]
    [InlineData("The the report and I I think so", "The report and I think so.")]
    // Item 5 pins: the anchors that stop the tidy eating real words.
    [InlineData("go ahead and send it", "Go ahead and send it.")]
    [InlineData("ask Ahmed about the umbrella", "Ask Ahmed about the umbrella.")]
    [InlineData("the dogs I like, labradors and poodles", "The dogs I like, labradors and poodles.")]
    [InlineData("do you know, Tom, where it is", "Do you know, Tom, where it is.")]
    // "scratch that" is applied only as a clause of its own; inside a clause it is left for the reader.
    [InlineData("book the room for friday. scratch that. use the kitchen", "Use the kitchen.")]
    [InlineData("i'll call tom, scratch that, email him", "Email him.")]
    [InlineData("send it to tom scratch that send it to jane", "Send it to tom scratch that send it to jane.")]
    public void The_tidy_keeps_meaning(string raw, string expected) => Assert.Equal(expected, DeterministicCleaner.Clean(raw));
}
