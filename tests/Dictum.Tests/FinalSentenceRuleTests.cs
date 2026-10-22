using Dictum.Llm;

namespace Dictum.Tests;

/// <summary>
/// The final-sentence rule (owner decision 22/10/2026 20:55 AEDT; widened after the lens on 5e9c227): a closing sentence the
/// speaker did not say is rejected, and an honest tidy of the speaker's own last sentence is not. Both corpora are the lens's
/// (its harness, final-fix.fsx), run against the prompt the app builds by default.
/// </summary>
public class FinalSentenceRuleTests
{
    private const string Reason = "reply ends with a sentence the speaker did not say";

    private static ValidationContext Context(string terms, string carried) =>
        new(CleanupPrompt.Build(CleanupPrompt.Default, false, null), null, terms.Length == 0 ? null : terms.Split('|'), carried.Length == 0 ? null : carried);

    /// <summary>
    /// Honest tidies the rule must not reject. Mutation: the rule without the digit, apostrophe and joined-words allowances
    /// (as at 5e9c227) makes "11", "12", "13", "19", "28", "33" and "37" fail.
    /// </summary>
    [Theory]
    [InlineData("01 fillers", "um so i'll send the report tomorrow uh thanks", "I'll send the report tomorrow. Thanks.", "", "")]
    [InlineData("02 scratch that", "the meeting moved scratch that the meeting is cancelled see you next week", "The meeting is cancelled. See you next week.", "", "")]
    [InlineData("03 new line", "dear team new line the meeting is at three new line thanks", "Dear team,\nThe meeting is at 3.\nThanks", "", "")]
    [InlineData("04 short words", "okay sounds good", "Okay. Sounds good.", "", "")]
    [InlineData("05 dictionary term", "we met kinney today thanks kinney", "We met Kynni today. Thanks, Kynni.", "Kynni", "")]
    [InlineData("06 question", "is the report done what time is the meeting", "Is the report done? What time is the meeting?", "", "")]
    [InlineData("07 hands-free carried", "tuesday at ten and bring the slides", "Tuesday at 10. Bring the slides.", "", "we should meet on")]
    [InlineData("08 abbreviation split", "please call mister smith", "Please call Mr. Smith.", "", "")]
    [InlineData("09 email address", "email me at john at example dot com", "Email me at john@example.com.", "", "")]
    [InlineData("11 ordinal digits alone", "when is it due the twenty first", "When is it due? The 21st.", "", "")]
    [InlineData("12 ordinal 1st alone", "which day the first", "Which day? The 1st.", "", "")]
    [InlineData("13 contraction alone", "where are the parcels they are here", "Where are the parcels? They're here.", "", "")]
    [InlineData("14 contraction we're", "is everyone ready we are", "Is everyone ready? We are.", "", "")]
    [InlineData("15 spelling theatre", "where are we meeting at the theater", "Where are we meeting? At the theatre.", "", "")]
    [InlineData("18 email compound", "did you get it check your e mail", "Did you get it? Check your email.", "", "")]
    [InlineData("19 initialism", "who called the a t o", "Who called? The ATO.", "", "")]
    [InlineData("20 noted will do", "noted will do", "Noted. Will do.", "", "")]
    [InlineData("21 time", "i'll call you at eight thirty", "I'll call you. At 8:30.", "", "")]
    [InlineData("22 full stop", "see you then full stop thanks full stop", "See you then. Thanks.", "", "")]
    [InlineData("23 stutter", "the the report is is ready i i will send it", "The report is ready. I will send it.", "", "")]
    [InlineData("24 sign off said", "kind regards new line john smith", "Kind regards,\nJohn Smith", "", "")]
    [InlineData("25 proper noun last", "who is coming priya", "Who is coming? Priya.", "", "")]
    [InlineData("26 number words last", "how many do we need twenty or maybe thirty", "How many do we need? 20. Or maybe 30.", "", "")]
    [InlineData("27 okay alone", "got it ok", "Got it. OK.", "", "")]
    [InlineData("28 tomorrow split", "talk to you to morrow", "Talk to you. Tomorrow.", "", "")]
    [InlineData("29 contraction it's", "is it done yes it is", "Is it done? Yes, it is.", "", "")]
    [InlineData("30 cancelled spelling", "the order is canceled", "The order. Cancelled.", "", "")]
    [InlineData("31 colour", "what colour is it the color is red", "What colour is it? The colour is red.", "", "")]
    [InlineData("32 thank you to thanks", "send it over thank you", "Send it over. Thank you.", "", "")]
    [InlineData("33 alright", "see you soon all right", "See you soon. Alright.", "", "")]
    [InlineData("35 percent word", "what share twenty percent", "What share? 20%.", "", "")]
    [InlineData("36 dollars", "what is the cost fifty dollars", "What is the cost? $50.", "", "")]
    [InlineData("37 ordinal 2nd", "the meeting moved to the second", "The meeting moved. To the 2nd.", "", "")]
    [InlineData("38 dates", "the meeting is on monday the third of march", "The meeting is on Monday. The 3rd of March.", "", "")]
    [InlineData("39 okay to ok", "is that fine okay", "Is that fine? OK.", "", "")]
    [InlineData("40 gonna", "i'm gonna call later i'm gonna", "I'm going to call later. I'm going to.", "", "")]
    public void An_honest_tidy_of_the_last_sentence_is_accepted(string name, string raw, string reply, string terms, string carried)
    {
        var (text, why) = CleanupValidator.Validate(raw, reply, Context(terms, carried));
        Assert.True(text is not null, $"{name}: {why}");
    }

    /// <summary>
    /// Closing sentences the speaker did not say, on the same line. Mutation: sound-matching against every dictated word again
    /// (function words included, as at 5e9c227) makes "A9", "A17" and "A21" ("… day.") fail.
    /// </summary>
    [Theory]
    [InlineData("A1 thanks for reading", "um so i think we should uh move the meeting to thursday and tell the whole team before lunch", "So I think we should move the meeting to Thursday and tell the whole team before lunch. Thanks for reading.")]
    [InlineData("A2 kind regards", "um so i think we should uh move the meeting to thursday and tell the whole team before lunch", "So I think we should move the meeting to Thursday and tell the whole team before lunch. Kind regards.")]
    [InlineData("A3 please call", "um so i think we should uh move the meeting to thursday and tell the whole team before lunch", "So I think we should move the meeting to Thursday and tell the whole team before lunch. Please call me if you have any questions.")]
    [InlineData("A4 best wishes", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Best wishes.")]
    [InlineData("A5 cheers", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Cheers!")]
    [InlineData("A6 sincerely", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Yours sincerely.")]
    [InlineData("A7 hope helps", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Hope this works.")]
    [InlineData("A9 have a great day", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Have a great day.")]
    [InlineData("A10 talk soon", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Talk soon.")]
    [InlineData("A11 thank you", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Thank you.")]
    [InlineData("A12 take care", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Take care.")]
    [InlineData("A13 many thanks", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Many thanks.")]
    [InlineData("A14 thanks in advance", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Thanks in advance.")]
    [InlineData("A15 all the best", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. All the best.")]
    [InlineData("A16 see you soon", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. See you soon.")]
    [InlineData("A17 good day", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Good day.")]
    [InlineData("A18 regards", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Regards.")]
    [InlineData("A19 hope that helps", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Hope that helps.")]
    [InlineData("A20 looking forward", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Looking forward to it.")]
    [InlineData("A21 have a nice day", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Have a nice day.")]
    [InlineData("A22 thanks", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Thanks.")]
    [InlineData("A23 bye", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Bye.")]
    [InlineData("A24 ttys", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Talk to you soon.")]
    [InlineData("A25 done", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Done.")]
    [InlineData("A27 kind regards kyle", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Kind regards, Kyle.")]
    public void A_closing_sentence_the_speaker_did_not_say_is_rejected(string name, string raw, string reply)
    {
        var (_, why) = CleanupValidator.Validate(raw, reply, Context("", ""));
        Assert.True(why == Reason, $"{name}: {why ?? "accepted"}");
    }

    /// <summary>The recorded ceiling: a grammar fix that rewrites a short last sentence into other words is rejected (polish lost only).</summary>
    [Fact]
    public void The_grammar_assist_ceiling_stays_the_one_recorded_exception() =>
        Assert.Equal(Reason, CleanupValidator.Validate("i asked him he go", "I asked him. He went.", Context("", "")).Rejection);
}
