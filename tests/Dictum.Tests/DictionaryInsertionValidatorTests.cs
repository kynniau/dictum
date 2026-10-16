using System.Net;
using System.Text;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// The clean-up may not add a dictionary term the speaker did not say (16/10/2026). With the dictionary {Dictum, Kynni,
/// Kynni Pty Ltd}, qwen2.5-1.5b wrote the company name over "a name" and after "by", every other validator rule accepted the
/// reply, and it was typed. Respelling a misheard term, the fix the dictionary exists for, stays allowed.
/// </summary>
public class DictionaryInsertionValidatorTests
{
    private const string Added = "reply adds a dictionary term the speaker did not say";

    // u3.txt of the investigation: whisper's text of the dictation (the live utterance was 205 characters, this is 210).
    private const string Dictated = "It learns my words too. Add a name to the dictionary once. It's spelled right every time. Calibration tells me " +
        "which model he has my accent best, and the whole thing is free. And open source. Dictum, by Kynni.";

    private static readonly string[] ThreeTerms = ["Dictum", "Kynni", "Kynni Pty Ltd"];
    private static readonly string[] TwoTerms = ["Dictum", "Kynni"];

    private static string? Reject(string raw, string reply, IReadOnlyCollection<string>? terms) =>
        CleanupValidator.Validate(raw, reply, new ValidationContext(null, null, terms)).Rejection;

    [Fact]
    public void The_typed_reply_that_added_the_company_name_is_rejected() => // qwen2.5-1.5b, verbatim
        Assert.Equal(Added, Reject(Dictated, "It learns my words too. Add Kynni Pty Ltd to the dictionary once. It's spelled right every time. " +
            "Calibration tells me which model has my accent best, and the whole thing is free. Dictum, by Kynni Pty Ltd.", ThreeTerms));

    [Fact]
    public void A_term_written_over_a_common_word_is_rejected_though_the_speaker_said_it_elsewhere() => // qwen2.5-1.5b, verbatim, two runs
        Assert.Equal(Added, Reject(Dictated, "It learns my words too. Add Kynni to the dictionary once. It's spelled right every time. " +
            "Calibration tells me which model has my accent best, and the whole thing is free. Dictum, by Kynni.", TwoTerms));

    [Fact]
    public void A_term_in_place_of_a_name_is_rejected() =>
        Assert.Equal(Added, Reject("add a name to the dictionary once", "Add Kynni to the dictionary once.", TwoTerms));

    [Fact]
    public void A_content_word_of_a_multi_word_term_written_alone_is_rejected() =>
        Assert.Equal(Added, Reject("add a name to the dictionary once", "Add Kynni to the dictionary once.", ["Kynni Pty Ltd"]));

    [Fact]
    public void A_term_added_in_the_possessive_is_rejected() =>
        Assert.Equal(Added, Reject("add a name to the dictionary once", "Add Kynni's name to the dictionary once.", TwoTerms));

    [Theory]
    [InlineData("dictum by kinney", "Dictum by Kynni.")] // the fix the dictionary exists for
    [InlineData("dictum by kinny", "Dictum by Kynni.")]
    [InlineData("dictim by kimmy", "Dictum by Kynni.")]
    [InlineData("dictum by kynni", "Dictum by Kynni.")]
    public void Respelling_what_was_said_is_accepted(string raw, string reply) => Assert.Null(Reject(raw, reply, TwoTerms));

    [Fact]
    public void A_multi_word_term_said_in_full_is_accepted() =>
        Assert.Null(Reject("send it to kynni pty ltd today", "Send it to Kynni Pty Ltd today.", ["Kynni Pty Ltd"]));

    // Whether "scratch that" retracts the whole clause before it or only "at 3" is ambiguous, so the name the speaker said
    // there is not an addition (counting against the shortened dictation would reject this correct reply).
    [Fact]
    public void A_term_said_before_scratch_that_may_stay() =>
        Assert.Null(Reject("tell kynni the meeting is at 3, scratch that, at 4", "Tell Kynni the meeting is at 4.", ["Kynni"]));

    [Fact]
    public void Without_a_dictionary_the_rule_does_not_run() // what every other validator test relies on
    {
        Assert.Null(Reject("add a name to the dictionary once", "Add Kynni to the dictionary once.", null));
        Assert.Null(Reject("add a name to the dictionary once", "Add Kynni to the dictionary once.", []));
    }

    [Theory]
    [InlineData("kinney", "kynni")] // whisper's spellings, from the corrections the user wrote by hand
    [InlineData("kinny", "kynni")]
    [InlineData("kimmy", "kynni")]
    [InlineData("kenny", "kynni")]
    [InlineData("kimi", "kynni")]
    [InlineData("dictim", "dictum")]
    [InlineData("dictom", "dictum")]
    [InlineData("dictam", "dictum")]
    [InlineData("nguyen", "nguyễn")] // accents removed
    public void A_misheard_spelling_sounds_like_the_term(string heard, string term) => Assert.True(ValidatorText.SoundsLike(heard, term));

    [Theory]
    [InlineData("name", "kynni")]
    [InlineData("kim", "kynni")]
    [InlineData("dictation", "dictum")]
    [InlineData("win", "win11")] // with a digit only the same word matches
    public void Another_word_does_not_sound_like_the_term(string heard, string term) => Assert.False(ValidatorText.SoundsLike(heard, term));

    [Fact]
    public async Task The_cleaner_types_the_tidy_instead_and_logs_neither_the_term_nor_the_dictation()
    {
        const string raw = "add a name to the dictionary once";
        var log = new RecordingLog();
        var engine = new FakeLocalEngine("qwen2.5-1.5b-instruct-q4_k_m.gguf", reply: "Add Kynni to the dictionary once.");
        var cleaner = TranscriptCleaner.Create(new LlmSettings { Provider = LlmSettings.Local }, offlineOnly: false, SecretStore.Empty,
            new HttpClient(new CountingHandler()), log, () => engine, new PersonalCleanup(null, null, TwoTerms));
        var result = await cleaner.CleanAsync(raw, CancellationToken.None);
        Assert.False(result.Cleaned);
        Assert.Equal(Added, result.FallbackReason);
        Assert.Equal(DeterministicCleaner.Clean(raw), result.Text);
        Assert.DoesNotContain("kynni", log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(raw, log.All, StringComparison.OrdinalIgnoreCase);
    }

    // Lens on ad38c63 (16/10/2026, bench and probes): counting the reply in any case made every everyday word that spells a
    // term, or a content word of a multi-word term, count as the term. The reply is counted as the term is spelt.
    [Theory]
    [InlineData("Loan Market|Parramatta", "She need a new loans for the house in Parramatta.", "She needs a new loan for the house in Parramatta.")] // bench: all three models
    [InlineData("Will", "Tomorrow i send the report to the client.", "Tomorrow I will send the report to the client.")] // bench: qwen2.5-1.5b
    [InlineData("IT", "is not working since monday", "It is not working since Monday.")]
    [InlineData("US", "please let know by friday", "Please let us know by Friday.")]
    [InlineData("May", "it be ready by friday or monday", "It may be ready by Friday or Monday.")]
    [InlineData("The Hub", "check hub for update", "Check the hub for updates.")]
    [InlineData("Commonwealth Bank|Bank of Melbourne", "he go to the banks tomorrow for sign the form", "He will go to the bank tomorrow to sign the form.")]
    [InlineData("CFC Finance", "he work in finances", "He works in finance.")]
    public void An_everyday_word_that_spells_a_term_is_not_the_term(string terms, string raw, string reply) =>
        Assert.Null(Reject(raw, reply, terms.Split('|')));

    // Lens on fc177ba (16/10/2026): with the dictation counted in any case, each lower-case use of the everyday word in the
    // dictation let one capitalised term through. A term with a capital is counted as written on both sides.
    [Theory]
    [InlineData("Will", "ask him to call me", "Ask Will to call me.")]
    [InlineData("Will", "ask him if he will call me", "Ask Will if he will call me.")]
    [InlineData("Will", "Ask him if he will call me.", "Ask Will if he will call me.")] // with whisper's capitals
    [InlineData("Bill", "pay the bill and send it to accounts", "Bill, pay the bill and send it to accounts.")]
    [InlineData("IT", "it is broken so call support about it", "It is broken so call IT support about it.")]
    [InlineData("May", "ask her if it may rain", "Ask May if it may rain.")]
    public void A_term_that_is_also_an_everyday_word_is_caught_when_written_as_the_term(string term, string raw, string reply) =>
        Assert.Equal(Added, Reject(raw, reply, [term]));

    // The documented cost of counting the dictation as written: whisper's lower-case "will", capitalised at the start of a
    // sentence, no longer covers that "Will" once a grammar fix adds the word again. With whisper's own sentence capitals the
    // same reply is accepted. A false rejection costs polish only: the tidy is typed instead.
    [Theory]
    [InlineData("Will", "will you come tomorrow i see you then", "Will you come tomorrow? I will see you then.", Added)]
    [InlineData("Will", "Will you come tomorrow? I see you then.", "Will you come tomorrow? I will see you then.", null)]
    [InlineData("May", "may be late i come tomorrow", "May be late. I may come tomorrow.", Added)]
    [InlineData("May", "May be late. I come tomorrow.", "May be late. I may come tomorrow.", null)]
    public void An_everyday_word_a_grammar_fix_repeats_needs_whisper_capitals_at_a_sentence_start(string term, string raw, string reply, string? rejection) =>
        Assert.Equal(rejection, Reject(raw, reply, [term]));

    // Lens on fc177ba (16/10/2026): the dictation's allowance counted plurals and joined runs while the reply was counted by
    // sound alone, so a plural or split word the reply kept paid for an added term. Both sides are counted alike.
    [Theory]
    [InlineData("Dictum|Kynni", "add a name to the dictionary when it comes to new words", "Add Kynni to the dictionary when it comes to new words.")]
    [InlineData("Dictum|Kynni", "It learns my words too. Add a name to the dictionary once. It's spelled right every time it comes up. Dictum, by Kynni.",
        "It learns my words too. Add Kynni to the dictionary once. It's spelled right every time it comes up. Dictum, by Kynni.")]
    [InlineData("Bill", "pay the bills today please", "Bill, pay the bills today please.")]
    [InlineData("Will", "check the wills today", "Will, check the wills today.")]
    [InlineData("Xero", "send the series of invoices today", "Send the Xero series of invoices today.")]
    [InlineData("Priya", "list the pros and cons for me", "Priya, list the pros and cons for me.")]
    [InlineData("Mark", "check the marks before friday", "Mark, check the marks before Friday.")]
    [InlineData("Zoom", "the camera zooms in too far", "The Zoom camera zooms in too far.")]
    [InlineData("Dictum|Kynni", "tell them the games start at noon", "Tell Kynni the games start at noon.")]
    [InlineData("GitHub", "push it to git hub and tell the team", "Push it to git hub and tell the GitHub team.")]
    public void A_plural_or_split_word_the_reply_keeps_does_not_pay_for_an_added_term(string terms, string raw, string reply) =>
        Assert.Equal(Added, Reject(raw, reply, terms.Split('|')));

    // Lens on fc177ba (16/10/2026), in the rule since ad38c63: a spoken command that shares a term's sound key ("comma" and
    // Kynni, "mark" and Mark, "quote" and Kate, "line" and Lena), or a hesitation ("um" and Owen), paid for an added term.
    [Theory]
    [InlineData("Dictum|Kynni", "add a name to the dictionary comma it is spelled right every time", "Add Kynni to the dictionary, it is spelled right every time.")]
    [InlineData("Mark", "can you send the report today question mark", "Mark, can you send the report today?")]
    [InlineData("Kate", "she said open quote send it today close quote", "Kate said \"send it today\".")]
    [InlineData("Lena", "thanks for the update new line regards", "Thanks for the update, Lena.\nRegards")]
    [InlineData("Owen", "um send the report today", "Send Owen the report today.")]
    public void A_spoken_command_does_not_pay_for_an_added_term(string terms, string raw, string reply) =>
        Assert.Equal(Added, Reject(raw, reply, terms.Split('|')));

    // Commands and hesitations are left out on both sides: one the reply keeps as a word is not counted against it either.
    [Theory]
    [InlineData("Dictum|Kynni", "dictum comma by kynni", "Dictum, by Kynni.")] // the spoken comma, no insertion
    [InlineData("Mark", "ask mark if it is done question mark", "Ask Mark if it is done?")]
    [InlineData("Dictum|Kynni", "put a comma after kynni", "Put a comma after Kynni.")]
    [InlineData("Mark", "put a question mark after mark", "Put a question mark after Mark.")]
    [InlineData("Lena", "add a new line after lena", "Add a new line after Lena.")]
    [InlineData("Owen", "um tell owen the news", "Um, tell Owen the news.")]
    public void A_spoken_command_is_counted_on_neither_side(string terms, string raw, string reply) =>
        Assert.Null(Reject(raw, reply, terms.Split('|')));

    // Lens on 8e5a28c (16/10/2026): a term spelt like a spoken command or hesitation was left out on both sides, so adding
    // it was never caught (the fix's probes found the same for that word of a multi-word term). A term with one in it is
    // counted with them kept.
    [Theory]
    [InlineData("ER", "the patient was seen this morning and sent home", "The patient was seen in the ER this morning and sent home.")]
    [InlineData("ERM", "we need to update the risk register before the audit", "We need to update the ERM risk register before the audit.")]
    [InlineData("UM", "the students from the university arrive on monday", "The UM students from the university arrive on Monday.")]
    [InlineData("Comma", "send the invoice to the agency today", "Send the invoice to the Comma agency today.")]
    [InlineData("ER", "she went to the er last night", "She went to the ER last night and the ER sent them home.")] // said once, added once
    [InlineData("ER Department", "go to the department now", "Go to the ER Department now.")]
    [InlineData("New Line Cinema", "send it to cinema today", "Send it to New Line Cinema today.")]
    public void A_term_spelt_like_a_spoken_command_is_counted(string term, string raw, string reply) =>
        Assert.Equal(Added, Reject(raw, reply, [term]));

    // Lens on 8e5a28c (16/10/2026): whisper writes some commands in capitals ("Question Mark"), and the capitalised word
    // paid for an added term spelt like it. Commands are left out in any case.
    [Theory]
    [InlineData("Mark", "can you send the report today Question Mark", "Mark, can you send the report today?")]
    [InlineData("Stop", "please hold the payment Full Stop", "Please hold the payment. Stop.")]
    [InlineData("Line", "thanks for the update New Line regards", "Thanks for the update, Line.\nRegards")]
    [InlineData("Paragraph", "thanks for that New Paragraph see you soon", "Thanks for that.\n\nParagraph, see you soon.")]
    [InlineData("Quote", "she said Open Quote send it today Close Quote", "Quote said \"send it today\".")]
    public void A_spoken_command_in_capitals_does_not_pay_for_an_added_term(string term, string raw, string reply) =>
        Assert.Equal(Added, Reject(raw, reply, [term]));

    // The documented cost of reading commands in any case: a word whisper capitalised right after "question" or "full", with
    // no punctuation between, is read as the command, so a reply that punctuates them adds that word (as it already did when
    // whisper wrote it in lower case). With whisper's own punctuation the same reply is accepted.
    [Theory]
    [InlineData("Mark", "I have a question Mark can you help", "I have a question, Mark, can you help?", Added)]
    [InlineData("Mark", "I have a question, Mark, can you help?", "I have a question, Mark, can you help?", null)]
    [InlineData("Stop", "The tank is full Stop the pump", "The tank is full. Stop the pump.", Added)]
    [InlineData("Stop", "The tank is full. Stop the pump.", "The tank is full. Stop the pump.", null)]
    public void A_capitalised_word_after_a_command_word_needs_whisper_punctuation(string term, string raw, string reply, string? rejection) =>
        Assert.Equal(rejection, Reject(raw, reply, [term]));

    // Saying such a term still pays for it, and so does saying a term beside whisper's capitalised command.
    [Theory]
    [InlineData("Comma", "the comma agency called", "The Comma agency called.")]
    [InlineData("ER", "she went to the er last night", "She went to the ER last night.")]
    [InlineData("New Line Cinema", "send it to new line cinema today", "Send it to New Line Cinema today.")]
    [InlineData("Mark", "Mark is it done Question Mark", "Mark, is it done?")]
    public void A_term_spelt_like_a_spoken_command_is_accepted_when_said(string term, string raw, string reply) =>
        Assert.Null(Reject(raw, reply, [term]));

    // The documented cost of keeping commands and hesitations for such a term: one the speaker dictated pays for it.
    [Theory]
    [InlineData("ER", "er the patient was seen this morning", "The patient was seen in the ER this morning.")]
    [InlineData("Comma", "yes comma i agree", "Yes, Comma, I agree.")]
    public void A_dictated_command_or_hesitation_pays_for_a_term_spelt_like_it(string term, string raw, string reply) =>
        Assert.Null(Reject(raw, reply, [term]));

    // Respellings that join or split words (lens, 16/10/2026: rejected on ad38c63, accepted before the rule existed).
    [Theory]
    [InlineData("PayPal", "Pay the invoice with pay pal before Friday.", "Pay the invoice with PayPal before Friday.")] // bench: all three models
    [InlineData("McDonald's", "Meet me at mcdonalds at noon.", "Meet me at McDonald's at noon.")] // bench: all three models
    [InlineData("Dictum|Kynni|dictum.kynni.com.au", "Download it from dictum dot kynni dot com dot au.", "Download it from dictum.kynni.com.au.")] // bench: all three
    [InlineData("GitHub", "Push the fix to git hub today and tell the team.", "Push the fix to GitHub today and tell the team.")] // bench: qwen2.5-1.5b
    [InlineData("Dictum|Kynni", "Send it to kinneys office today.", "Send it to Kynni's office today.")] // bench: qwen2.5-1.5b
    [InlineData("e-mail", "send me an email", "Send me an e-mail.")] // "mail" is covered by the whole term said as "email"
    [InlineData("Wi-Fi", "the wifi is down again", "The Wi-Fi is down again.")]
    [InlineData("kyle@kynni.com.au", "email kyle at kynni dot com dot au", "Email kyle@kynni.com.au.")]
    [InlineData("Salestrekker", "update sales trekker today", "Update Salestrekker today.")]
    [InlineData("O'Brien", "call o brien today", "Call O'Brien today.")]
    [InlineData("e-mail", "send me an e dash mail", "Send me an e-mail.")] // a spoken separator left out
    [InlineData("e-mail", "send me an e hyphen mail", "Send me an e-mail.")]
    [InlineData("DoorDash", "order lunch on door dash today", "Order lunch on DoorDash today.")] // a separator word that is part of the term
    public void A_respelling_that_joins_or_splits_words_is_accepted(string terms, string raw, string reply) =>
        Assert.Null(Reject(raw, reply, terms.Split('|')));

    [Theory]
    [InlineData("Dictum|Kynni", "dictum by kin knee", "Dictum by Kynni.")] // a split mishearing is the corrections' to fix: no letters match
    [InlineData("Kynni", "can i go now", "Kynni go now.")] // exact letters, not sounds
    [InlineData("GitHub", "push it to git hub", "Push it to GitHub. GitHub.")] // one run covers one
    [InlineData("Kynni Pty Ltd", "send it to kynni pty ltd today", "Send it to Kynni Pty Ltd and Kynni today.")] // the larger allowance, not both
    [InlineData("Ed", "its done now", "Ed, its done now.")] // a three-letter word ending in s is not a plural
    [InlineData("DoorDash", "order lunch on door dash today", "Order lunch on door dash today from DoorDash.")] // the reply's run counts too
    public void An_allowance_does_not_admit_an_addition(string terms, string raw, string reply) =>
        Assert.Equal(Added, Reject(raw, reply, terms.Split('|')));

    // The documented cost of counting the reply as the term is spelt: an insertion in other capitals is not caught. Both
    // insertions observed on 16/10/2026 were spelt as in the dictionary.
    [Fact]
    public void A_term_inserted_in_lower_case_is_not_caught() =>
        Assert.Null(Reject("add a name to the dictionary once", "Add kynni to the dictionary once.", TwoTerms));

    [Theory]
    [InlineData("Lakshmi", "call laxmi today", "Call Lakshmi today.")] // an x after the first letter sounds "ks"
    [InlineData("Dixon", "ask dickson to sign it", "Ask Dixon to sign it.")]
    [InlineData("Aleks", "tell alex the news", "Tell Aleks the news.")]
    [InlineData("Xero", "the invoice is in zero", "The invoice is in Xero.")] // a first x sounds "z"
    public void A_respelling_through_an_x_is_accepted(string term, string raw, string reply) => Assert.Null(Reject(raw, reply, [term]));

    // A term typed with a combining accent (decomposed Unicode, as some editors save it) is the same word as the single
    // accented letter a model writes, and the other way round.
    [Theory]
    [InlineData("both composed", "Zo\u00EB", "Add Zo\u00EB to the dictionary once.")]
    [InlineData("term decomposed", "Zoe\u0308", "Add Zo\u00EB to the dictionary once.")]
    [InlineData("reply decomposed", "Zo\u00EB", "Add Zoe\u0308 to the dictionary once.")]
    public void A_term_is_the_same_word_composed_or_decomposed(string form, string term, string reply) =>
        Assert.Equal((form, Added), (form, Reject("add a name to the dictionary once", reply, [term])));

    // Text that cannot be normalised (an unpaired surrogate makes string.Normalize throw) is read as it is, not thrown.
    [Fact]
    public void Text_that_cannot_be_normalised_is_read_as_it_is() =>
        Assert.Equal(Added, Reject("add a name \uD800 to the dictionary once", "Add Kynni \uD800 to the dictionary once.", TwoTerms));

    private static PersonalText PersonalOf(IEnumerable<string> terms, params string[] keep) =>
        new(new PersonalDictionary(terms), Corrections.Empty, keep.Length == 0 ? ProfanityFilter.Empty : new ProfanityFilter(keep, "test"));

    private static string? Reject(PersonalText personal, string raw, string reply) =>
        CleanupValidator.Validate(raw, reply, personal.ForCleanup(new ProfanitySettings()).Checks(new LlmSettings(), _ => { }).Validation).Rejection;

    // The validator checks what the model was shown: the terms in the prompt's 1,000-character line, never the oldest
    // terms left out of it or a pasted paragraph too long for it.
    [Fact]
    public void Only_the_terms_in_the_prompt_line_are_checked()
    {
        var terms = new[] { "Will" }.Concat(Enumerable.Range(0, 120).Select(i => $"Zorblax{(char)('a' + i % 26)}{(char)('a' + i / 26)}")).ToList();
        var dictionary = new PersonalDictionary(terms);
        Assert.Equal(121, dictionary.Terms.Count);
        Assert.DoesNotContain("Will", dictionary.CleanupTerms);
        Assert.Equal(PersonalDictionary.CleanupLinePrefix + string.Join(", ", dictionary.CleanupTerms) + ".", dictionary.CleanupLine);
        Assert.Null(Reject(PersonalOf(terms), "ask him to send the report", "Ask Will to send the report.")); // an addition, but "Will" is not in the line

        var paragraph = string.Concat(Enumerable.Repeat("Please note that every invoice has been checked twice before it goes out to the client. ", 12)).Trim();
        var withParagraph = PersonalOf([paragraph, "Kynni"]);
        Assert.Contains(paragraph, withParagraph.Dictionary.Terms);
        Assert.Equal(["Kynni"], withParagraph.Dictionary.CleanupTerms);
        Assert.Null(Reject(withParagraph, "it has approved yesterday", "It has been approved yesterday."));
        Assert.Equal(Added, Reject(withParagraph, "add a name to the dictionary once", "Add Kynni to the dictionary once."));
    }

    private const string AddedReply = "Add Kynni to the dictionary once.";

    // Wherever a clean-up is built, the terms come from PersonalText.ForCleanup with the dictionary line and the kept words:
    // --transcribe (the tray app makes the same call, pinned below), every provider of TranscriptCleaner.Create, and the
    // prompt and checks --bench-cleanup judges with. Removing the terms at any of them fails this test.
    [Theory]
    [InlineData(LlmSettings.Local)]
    [InlineData(LlmSettings.OpenAiCompatible)]
    [InlineData(LlmSettings.Anthropic)]
    public async Task Every_clean_up_checks_the_terms_in_its_prompt(string provider)
    {
        var personal = PersonalOf(TwoTerms, "damn");
        var settings = new AppSettings
        {
            Llm = provider switch
            {
                LlmSettings.OpenAiCompatible => new LlmSettings { Provider = provider, BaseUrl = "http://localhost:11434/v1", Model = "m" },
                LlmSettings.Anthropic => new LlmSettings { Provider = provider, CloudConsent = "api.anthropic.com" },
                _ => new LlmSettings { Provider = provider },
            },
        };
        var body = provider == LlmSettings.Anthropic
            ? $$$"""{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"{{{AddedReply}}}"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""
            : $$$"""{"choices":[{"message":{"role":"assistant","content":"{{{AddedReply}}}"},"finish_reason":"stop"}]}""";
        using var http = new HttpClient(new Replying(body));
        var secrets = SecretStore.FromContents("ANTHROPIC_API_KEY=k-test-123456\n", null, _ => null);
        var engine = new FakeLocalEngine("qwen2.5-1.5b-instruct-q4_k_m.gguf", reply: AddedReply);

        var cleaner = Dictum.Cli.CliCommands.Cleaner(settings, secrets, http, NullLog.Instance, () => engine, personal);
        Assert.True(cleaner.UsesModel);
        Assert.EndsWith("\n" + personal.Dictionary.CleanupLine, cleaner.SystemPrompt);
        Assert.Equal(personal.Dictionary.CleanupTerms, cleaner.Validation.DictionaryTerms!);
        Assert.Equal(["damn"], cleaner.Validation.MustKeepWords!);
        Assert.Equal(Dictum.Cli.BenchCleanupCommand.Checks(settings, personal, _ => { }), (cleaner.SystemPrompt, cleaner.Validation));
        var result = await cleaner.CleanAsync("add a name to the dictionary once", TestContext.Current.CancellationToken);
        Assert.Equal(Added, result.FallbackReason);
    }

    // The constructor takes the prompt and the checks separately; the checks always use the prompt it sends, so a context
    // built with another prompt (or none) cannot turn off the check for a reply that repeats it (lens on fc177ba).
    [Fact]
    public void A_cleaner_checks_replies_against_the_prompt_it_sends()
    {
        static ValidationContext Checks(ValidationContext? given) =>
            new TranscriptCleaner(null, "the prompt sent", TimeSpan.FromSeconds(1), NullLog.Instance, "test", validation: given).Validation;
        var other = new ValidationContext("another prompt", ["damn"], TwoTerms);
        Assert.Equal(other with { SystemPrompt = "the prompt sent" }, Checks(other));
        Assert.Equal(new ValidationContext("the prompt sent"), Checks(new ValidationContext(null)));
        Assert.Equal(new ValidationContext("the prompt sent"), Checks(null));
    }

    [Fact]
    public Task The_tray_app_checks_the_terms_in_its_prompt() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        File.WriteAllText(scratch.Paths.DictionaryFile, "Dictum\nKynni\n");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.EndsWith("\nKnown names and terms, keep their spelling: Dictum, Kynni.", shell.Cleaner.SystemPrompt);
        Assert.Equal(TwoTerms, shell.Cleaner.Validation.DictionaryTerms!);
        shell.Dispose();
    });

    private sealed class Replying(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
