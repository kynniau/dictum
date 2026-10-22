using Dictum.Llm;

namespace Dictum.Tests;

/// <summary>
/// The final-sentence rule (owner decision 22/10/2026 20:55 AEDT; reworked after the lens on 5e9c227 and the re-lens on c796dc8):
/// a closing sentence the speaker did not say is rejected, and an honest tidy of the speaker's own last sentence is not. The
/// tables are the re-lens's corpus (its harness, lens60b/trial.fsx; its "café", "résumé" and "Zoë" were saved double-encoded
/// there and are written correctly here), each case with the result the trial validator gave, run against the default prompt.
/// Only one result differs from the trial's: "N22" ("a s i c" → "ASIC"), which the four-letter join now accepts.
/// </summary>
public class FinalSentenceRuleTests
{
    private const string Reason = "reply ends with a sentence the speaker did not say";
    private const string Accepted = "ACCEPTED";

    private static ValidationContext Context(string terms = "", string carried = "") =>
        new(CleanupPrompt.Build(CleanupPrompt.Default, false, null), null, terms.Length == 0 ? null : terms.Split('|'), carried.Length == 0 ? null : carried);

    private static string Outcome(string raw, string reply, ValidationContext context) =>
        CleanupValidator.Validate(raw, reply, context).Rejection ?? Accepted;

    /// <summary>
    /// Honest tidies, with the result each gets. Five are rejected: four by older rules (main rejects them too) and "16", the
    /// recorded ceiling (a grammar fix that rewrites a short last sentence into other words).
    /// </summary>
    [Theory]
    [InlineData("01 fillers", "um so i'll send the report tomorrow uh thanks", "I'll send the report tomorrow. Thanks.", "", "", "ACCEPTED")]
    [InlineData("02 scratch that", "the meeting moved scratch that the meeting is cancelled see you next week", "The meeting is cancelled. See you next week.", "", "", "ACCEPTED")]
    [InlineData("03 new line", "dear team new line the meeting is at three new line thanks", "Dear team,\nThe meeting is at 3.\nThanks", "", "", "ACCEPTED")]
    [InlineData("04 short words", "okay sounds good", "Okay. Sounds good.", "", "", "ACCEPTED")]
    [InlineData("05 dictionary term", "we met kinney today thanks kinney", "We met Kynni today. Thanks, Kynni.", "Kynni", "", "ACCEPTED")]
    [InlineData("06 question", "is the report done what time is the meeting", "Is the report done? What time is the meeting?", "", "", "ACCEPTED")]
    [InlineData("07 hands-free carried", "tuesday at ten and bring the slides", "Tuesday at 10. Bring the slides.", "we should meet on", "", "ACCEPTED")]
    [InlineData("08 abbreviation split", "please call mister smith", "Please call Mr. Smith.", "", "", "ACCEPTED")]
    [InlineData("09 email address", "email me at john at example dot com", "Email me at john@example.com.", "", "", "ACCEPTED")]
    [InlineData("10 decimal", "the rate is four point five percent", "The rate is 4.5%.", "", "", "reply drops a dictated sentence (1/5 of its content words kept, minimum a third)")]
    [InlineData("11 ordinal digits alone", "when is it due the twenty first", "When is it due? The 21st.", "", "", "ACCEPTED")]
    [InlineData("12 ordinal 1st alone", "which day the first", "Which day? The 1st.", "", "", "ACCEPTED")]
    [InlineData("13 contraction alone", "where are the parcels they are here", "Where are the parcels? They're here.", "", "", "ACCEPTED")]
    [InlineData("14 contraction we're", "is everyone ready we are", "Is everyone ready? We are.", "", "", "ACCEPTED")]
    [InlineData("15 spelling theatre", "where are we meeting at the theater", "Where are we meeting? At the theatre.", "", "", "ACCEPTED")]
    [InlineData("16 grammar past tense alone", "i asked him he go", "I asked him. He went.", "", "", "reply ends with a sentence the speaker did not say")]
    [InlineData("17 grammar didn't", "yesterday i call him he no answer", "Yesterday I called him. He didn't answer.", "", "", "reply drops a negation")]
    [InlineData("18 email compound", "did you get it check your e mail", "Did you get it? Check your email.", "", "", "ACCEPTED")]
    [InlineData("19 initialism", "who called the a t o", "Who called? The ATO.", "", "", "ACCEPTED")]
    [InlineData("20 noted will do", "noted will do", "Noted. Will do.", "", "", "ACCEPTED")]
    [InlineData("21 time", "i'll call you at eight thirty", "I'll call you. At 8:30.", "", "", "ACCEPTED")]
    [InlineData("22 full stop", "see you then full stop thanks full stop", "See you then. Thanks.", "", "", "ACCEPTED")]
    [InlineData("23 stutter", "the the report is is ready i i will send it", "The report is ready. I will send it.", "", "", "ACCEPTED")]
    [InlineData("24 sign off said", "kind regards new line john smith", "Kind regards,\nJohn Smith", "", "", "ACCEPTED")]
    [InlineData("25 proper noun last", "who is coming priya", "Who is coming? Priya.", "", "", "ACCEPTED")]
    [InlineData("26 number words last", "how many do we need twenty or maybe thirty", "How many do we need? 20. Or maybe 30.", "", "", "ACCEPTED")]
    [InlineData("27 okay alone", "got it ok", "Got it. OK.", "", "", "ACCEPTED")]
    [InlineData("28 tomorrow split", "talk to you to morrow", "Talk to you. Tomorrow.", "", "", "ACCEPTED")]
    [InlineData("29 contraction it's", "is it done yes it is", "Is it done? Yes, it is.", "", "", "ACCEPTED")]
    [InlineData("30 cancelled spelling", "the order is canceled", "The order. Cancelled.", "", "", "ACCEPTED")]
    [InlineData("31 colour", "what colour is it the color is red", "What colour is it? The colour is red.", "", "", "ACCEPTED")]
    [InlineData("32 thank you to thanks", "send it over thank you", "Send it over. Thank you.", "", "", "ACCEPTED")]
    [InlineData("33 alright", "see you soon all right", "See you soon. Alright.", "", "", "ACCEPTED")]
    [InlineData("34 cannot", "i tried i can not", "I tried. I cannot.", "", "", "commentary-shaped reply (contains \"i cannot\")")]
    [InlineData("35 percent word", "what share twenty percent", "What share? 20%.", "", "", "ACCEPTED")]
    [InlineData("36 dollars", "what is the cost fifty dollars", "What is the cost? $50.", "", "", "ACCEPTED")]
    [InlineData("37 ordinal 2nd", "the meeting moved to the second", "The meeting moved. To the 2nd.", "", "", "ACCEPTED")]
    [InlineData("38 dates", "the meeting is on monday the third of march", "The meeting is on Monday. The 3rd of March.", "", "", "ACCEPTED")]
    [InlineData("39 okay to ok", "is that fine okay", "Is that fine? OK.", "", "", "ACCEPTED")]
    [InlineData("40 gonna", "i'm gonna call later i'm gonna", "I'm going to call later. I'm going to.", "", "", "ACCEPTED")]
    [InlineData("N01 hands-free carried 2", "the invoice is paid please check the account", "The invoice is paid. Please check the account.", "we spoke to the bank about", "", "ACCEPTED")]
    [InlineData("N02 hands-free carried 3", "and then we can sign it on friday", "And then we can sign it. On Friday.", "they agreed to the terms", "", "ACCEPTED")]
    [InlineData("N03 dictionary fix last", "the new release is out send it to dictim users", "The new release is out. Send it to Dictum users.", "Dictum", "", "ACCEPTED")]
    [InlineData("N04 dictionary mispronounced end", "the invoice went through on zero", "The invoice went through. On Xero.", "Xero", "", "ACCEPTED")]
    [InlineData("N05 dictionary kinney end", "the letter is from kinney", "The letter is ready. From Kynni.", "Kynni", "", "ACCEPTED")]
    [InlineData("N06 spoken full stop", "the report is ready full stop i will send it tonight full stop", "The report is ready. I will send it tonight.", "", "", "ACCEPTED")]
    [InlineData("N07 spoken comma", "the report is ready full stop however comma it needs review", "The report is ready. However, it needs review.", "", "", "ACCEPTED")]
    [InlineData("N08 time half past", "the call is at half past two let's say two thirty", "The call is at half past two. Let's say 2:30.", "", "", "ACCEPTED")]
    [InlineData("N09 date with year", "it is due on the fifth of november twenty twenty six", "It is due. On 5 November 2026.", "", "", "ACCEPTED")]
    [InlineData("N10 email address dotted", "send it to kyle dot manson at example dot com", "Send it. To kyle.manson@example.com.", "", "", "ACCEPTED")]
    [InlineData("N11 proper nouns", "the meeting is with sarah from parramatta", "The meeting is with Sarah. From Parramatta.", "", "", "ACCEPTED")]
    [InlineData("N12 one word yes", "is it approved yes", "Is it approved? Yes.", "", "", "ACCEPTED")]
    [InlineData("N13 one word name", "who signed it priyanka", "Who signed it? Priyanka.", "", "", "ACCEPTED")]
    [InlineData("N14 question last", "the file is attached did you get it", "The file is attached. Did you get it?", "", "", "ACCEPTED")]
    [InlineData("N15 number words digits", "we have twelve staff and three managers", "We have 12 staff. And 3 managers.", "", "", "ACCEPTED")]
    [InlineData("N16 number hundred", "how many units one hundred and twenty", "How many units? 120.", "", "", "ACCEPTED")]
    [InlineData("N17 contraction don't", "i will call you do not worry", "I'll call you. Don't worry.", "", "", "ACCEPTED")]
    [InlineData("N18 contraction won't", "is he coming no he will not", "Is he coming? No, he won't.", "", "", "ACCEPTED")]
    [InlineData("N19 contraction let's", "let us meet tomorrow let us do it", "Let's meet tomorrow. Let's do it.", "", "", "ACCEPTED")]
    [InlineData("N20 o'clock", "see you at three o clock", "See you. At 3 o'clock.", "", "", "ACCEPTED")]
    [InlineData("N21 initialism ceo", "please send it to the c e o", "Please send it. To the CEO.", "", "", "ACCEPTED")]
    [InlineData("N22 initialism four letters", "we lodged it with a s i c", "We lodged it. With ASIC.", "", "", "ACCEPTED")]
    [InlineData("N23 hyphenated split", "call me we can follow up next week", "Call me. We can follow-up next week.", "", "", "ACCEPTED")]
    [InlineData("N24 hyphenated co-operate", "we need them to co operate", "We need them. To co-operate.", "", "", "ACCEPTED")]
    [InlineData("N25 cafe accent", "meet me at the cafe near the station", "Meet me. At the café near the station.", "", "", "ACCEPTED")]
    [InlineData("N26 resume accent", "i read it the resume is great", "I read it. The résumé is great.", "", "", "ACCEPTED")]
    [InlineData("N27 zoe accent", "invite them all especially zoe", "Invite them all. Especially Zoë.", "", "", "ACCEPTED")]
    [InlineData("N28 katherine respelt", "say hi to katherine", "Say hi. To Catherine.", "", "", "ACCEPTED")]
    [InlineData("N29 missus", "please call missus jones", "Please call. Mrs Jones.", "", "", "ACCEPTED")]
    [InlineData("N30 per cent", "the fee is up five per cent", "The fee is up. 5%.", "", "", "ACCEPTED")]
    [InlineData("N31 spoken question mark", "is it ready question mark when is it due question mark", "Is it ready? When is it due?", "", "", "ACCEPTED")]
    [InlineData("N32 can't", "i tried but i can not", "I tried. But I can't.", "", "", "commentary-shaped reply (contains \"i can't\")")]
    [InlineData("N33 i'd", "i would like that i would", "I'd like that. I would.", "", "", "ACCEPTED")]
    [InlineData("N34 you're last", "thanks you are the best", "Thanks. You're the best.", "", "", "ACCEPTED")]
    [InlineData("N35 shouldn't", "should we go no we should not", "Should we go? No, we shouldn't.", "", "", "ACCEPTED")]
    [InlineData("N36 isn't", "is it ready it is not", "Is it ready? It isn't.", "", "", "ACCEPTED")]
    [InlineData("N37 haven't", "have you sent it i have not", "Have you sent it? I haven't.", "", "", "ACCEPTED")]
    [InlineData("N38 doesn't", "does it work it does not", "Does it work? It doesn't.", "", "", "ACCEPTED")]
    [InlineData("N39 wouldn't", "would they agree they would not", "Would they agree? They wouldn't.", "", "", "ACCEPTED")]
    [InlineData("N40 initialism nsw", "the office is in n s w", "The office is open. In NSW.", "", "", "ACCEPTED")]
    [InlineData("N41 email spoken at", "write to accounts at kynni dot com dot au", "Write to us. accounts@kynni.com.au", "", "", "ACCEPTED")]
    [InlineData("N42 okay sure", "can you do it okay will do", "Can you do it? Okay, will do.", "", "", "ACCEPTED")]
    [InlineData("N43 the 3pm", "meet at three p m", "Meet. At 3 pm.", "", "", "ACCEPTED")]
    [InlineData("N44 mid sentence period abbreviation", "it costs about five dollars e t c", "It costs about $5. Etc.", "", "", "ACCEPTED")]
    public void Each_honest_tidy_gets_its_recorded_result(string name, string raw, string reply, string terms, string carried, string expected) =>
        Assert.True(Outcome(raw, reply, Context(terms, carried)) == expected, $"{name}: {Outcome(raw, reply, Context(terms, carried))}");

    public static readonly (string Name, string Raw, string Clean)[] Bases =
    [
        ("B1", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team."),
        ("B2", "i need to go to the bank before it closes and it will be a long day", "I need to go to the bank before it closes and it will be a long day."),
        ("B3", "hi sarah just checking in about the invoice we sent last week can you tell me when it will be paid", "Hi Sarah, just checking in about the invoice we sent last week. Can you tell me when it will be paid?"),
        ("B4", "the report is done and i have sent it to the client to have a look at it today", "The report is done and I have sent it to the client to have a look at it today."),
        ("B5", "we should be able to get it done by the end of the week if the team can take on the extra work", "We should be able to get it done by the end of the week if the team can take on the extra work."),
    ];

    public static readonly string[] SignOffs =
    [
        "Best wishes.", "Cheers, Sam.", "Let me know.", "Regards, the team.", "Sent from my phone.", "See attached.", "Thank you.",
        "Yours sincerely.", "Over to you.", "Happy to discuss.", "Take care.", "Bye.", "Done.", "Thanks.", "Cheers.", "Talk soon.",
        "Have a good one.", "Speak soon.", "Many thanks.", "Kind regards.", "Thanks again.", "Warm regards.", "Have a great weekend.",
        "Thanks heaps.", "Good luck.", "Hope this helps.", "Let me know if you have questions.", "Any questions, call me.",
        "Chat soon.", "Catch you later.", "Ta.", "Thanks, Kyle.", "Best.", "Regards.", "Love.", "Noted.", "Sure thing.",
        "Today.", "All good.", "No worries.", "Talk tomorrow.", "Thank you for your time.", "Appreciate it.", "Much appreciated.",
        "With thanks.", "Sincerely.", "Respectfully.", "Hope you are well.", "Hope all is well.", "Looking forward to hearing from you.",
    ];

    /// <summary>
    /// The sign-offs that pass, each the recorded ceiling: every counted word was said in that dictation ("today", "done",
    /// "take", "team", "sent"), or it has no counted word at all ("Ta.").
    /// </summary>
    private static readonly HashSet<(string, string)> PassingSignOffs =
    [
        ("B1", "Regards, the team."), ("B1", "Ta."), ("B2", "Ta."), ("B3", "Sent from my phone."), ("B3", "Ta."),
        ("B4", "Sent from my phone."), ("B4", "Done."), ("B4", "Ta."), ("B4", "Today."),
        ("B5", "Regards, the team."), ("B5", "Take care."), ("B5", "Done."), ("B5", "Ta."),
    ];

    /// <summary>Sign-offs an older rule rejects first, with its own reason (main rejects them the same way).</summary>
    private static string? OlderRule(string baseName, string signOff) => (baseName, signOff) switch
    {
        ("B1", "Let me know if you have questions.") => "reply has 1.78x the dictation's words (limit 1.6x)",
        ("B1", "Looking forward to hearing from you.") => "reply has 1.67x the dictation's words (limit 1.6x)",
        (_, "Let me know." or "Let me know if you have questions.") => "commentary-shaped reply (contains \"let me know\")",
        _ => null,
    };

    public static TheoryData<string, string> SignOffCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, _, _) in Bases) foreach (var signOff in SignOffs) data.Add(name, signOff);
        return data;
    }

    /// <summary>Fifty sign-offs on five dictations: rejected by this rule, but for the thirteen recorded passes and the eleven an older rule rejects first.</summary>
    [Theory]
    [MemberData(nameof(SignOffCases))]
    public void Each_sign_off_gets_its_recorded_result(string baseName, string signOff)
    {
        var (_, raw, clean) = Bases.Single(b => b.Name == baseName);
        Assert.Equal(PassingSignOffs.Contains((baseName, signOff)) ? Accepted : OlderRule(baseName, signOff) ?? Reason, Outcome(raw, clean + " " + signOff, Context()));
    }

    /// <summary>
    /// The fourteen the re-lens found passing through sound-key collisions ("bye" with "be", "good" with "go"…). Mutation:
    /// sound-matching the joined and counted words again (SoundsLike in place of the one-edit rule) makes these fail.
    /// </summary>
    [Theory]
    [InlineData("B2", "Bye.")]
    [InlineData("B2", "Take care.")]
    [InlineData("B2", "Today.")]
    [InlineData("B3", "Sure thing.")]
    [InlineData("B3", "Thanks again.")]
    [InlineData("B4", "Good luck.")]
    [InlineData("B4", "Happy to discuss.")]
    [InlineData("B4", "Hope all is well.")]
    [InlineData("B4", "Hope this helps.")]
    [InlineData("B4", "Hope you are well.")]
    [InlineData("B5", "All good.")]
    [InlineData("B5", "Chat soon.")]
    [InlineData("B5", "Good luck.")]
    [InlineData("B5", "Have a good one.")]
    public void A_sound_key_collision_does_not_let_a_sign_off_through(string baseName, string signOff)
    {
        var (_, raw, clean) = Bases.Single(b => b.Name == baseName);
        Assert.Equal(Reason, Outcome(raw, clean + " " + signOff, Context()));
    }

    /// <summary>The first lens's closing sentences: all rejected but "A8" (every word said, the ceiling) and "A26" ("team" said).</summary>
    [Theory]
    [InlineData("A1 thanks for reading", "um so i think we should uh move the meeting to thursday and tell the whole team before lunch", "So I think we should move the meeting to Thursday and tell the whole team before lunch. Thanks for reading.")]
    [InlineData("A2 kind regards", "um so i think we should uh move the meeting to thursday and tell the whole team before lunch", "So I think we should move the meeting to Thursday and tell the whole team before lunch. Kind regards.")]
    [InlineData("A3 please call", "um so i think we should uh move the meeting to thursday and tell the whole team before lunch", "So I think we should move the meeting to Thursday and tell the whole team before lunch. Please call me if you have any questions.")]
    [InlineData("A4 best wishes", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Best wishes.")]
    [InlineData("A5 cheers", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Cheers!")]
    [InlineData("A6 sincerely", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Yours sincerely.")]
    [InlineData("A7 hope helps", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Hope this works.")]
    [InlineData("A8 reordered said", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Team meeting Thursday.")]
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
    [InlineData("A26 thanks team", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Thanks, team.")]
    [InlineData("A27 kind regards kyle", "move the meeting to thursday and tell the team", "Move the meeting to Thursday and tell the team. Kind regards, Kyle.")]
    public void Each_closing_sentence_of_the_first_lens_gets_its_recorded_result(string name, string raw, string reply) =>
        Assert.True(Outcome(raw, reply, Context()) == (name.StartsWith("A8 ", StringComparison.Ordinal) || name.StartsWith("A26 ", StringComparison.Ordinal) ? Accepted : Reason),
            $"{name}: {Outcome(raw, reply, Context())}");

    /// <summary>
    /// The apostrophe stem on its own: "I'd" is said when "i" was, and no other route covers it (too short for the one-edit
    /// rule, and "i would" joined is another length). Mutation: Stem keeping the word whole after its "-n't" cases makes this fail.
    /// </summary>
    [Fact]
    public void An_apostrophe_word_is_said_when_the_part_before_it_was() =>
        Assert.Equal(Accepted, Outcome("i will call you i would like that", "I will call you. I'd like that.", Context()));

    /// <summary>"-n't" contractions are said by their stem ("won't" for "will"), not split at the apostrophe ("won").</summary>
    [Theory]
    [InlineData("is he coming no he will not", "Is he coming? No, he won't.")]
    [InlineData("should we go no we should not", "Should we go? No, we shouldn't.")]
    [InlineData("is it ready it is not", "Is it ready? It isn't.")]
    [InlineData("have you sent it i have not", "Have you sent it? I haven't.")]
    [InlineData("does it work it does not", "Does it work? It doesn't.")]
    [InlineData("would they agree they would not", "Would they agree? They wouldn't.")]
    public void A_contraction_with_nt_is_said_by_its_stem(string raw, string reply) => Assert.Equal(Accepted, Outcome(raw, reply, Context()));

    /// <summary>Four single letters spelt out join into one word ("a s i c" → "ASIC").</summary>
    [Fact]
    public void Four_spelt_out_letters_are_said_as_their_initialism() =>
        Assert.Equal(Accepted, Outcome("we lodged it with a s i c", "We lodged it. With ASIC.", Context()));

    /// <summary>The honest respellings the one-edit, accent and dictionary routes keep.</summary>
    [Theory]
    [InlineData("who called the a t o", "Who called? The ATO.", "")]
    [InlineData("did you get it check your e mail", "Did you get it? Check your email.", "")]
    [InlineData("talk to you to morrow", "Talk to you. Tomorrow.", "")]
    [InlineData("see you soon all right", "See you soon. Alright.", "")]
    [InlineData("the order is canceled", "The order. Cancelled.", "")]
    [InlineData("say hi to katherine", "Say hi. To Catherine.", "")]
    [InlineData("meet me at the cafe near the station", "Meet me. At the café near the station.", "")]
    [InlineData("i read it the resume is great", "I read it. The résumé is great.", "")]
    [InlineData("invite them all especially zoe", "Invite them all. Especially Zoë.", "")]
    [InlineData("the letter is from kinney", "The letter is ready. From Kynni.", "Kynni")]
    [InlineData("the invoice went through on zero", "The invoice went through. On Xero.", "Xero")]
    [InlineData("the new release is out send it to dictim users", "The new release is out. Send it to Dictum users.", "Dictum")]
    public void An_honest_respelling_is_still_said(string raw, string reply, string terms) => Assert.Equal(Accepted, Outcome(raw, reply, Context(terms)));
}
