using Dictum.Llm;
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
            new HttpClient(new CountingHandler()), log, () => engine, dictionaryTerms: TwoTerms);
        var result = await cleaner.CleanAsync(raw, CancellationToken.None);
        Assert.False(result.Cleaned);
        Assert.Equal(Added, result.FallbackReason);
        Assert.Equal(DeterministicCleaner.Clean(raw), result.Text);
        Assert.DoesNotContain("kynni", log.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(raw, log.All, StringComparison.OrdinalIgnoreCase);
    }
}
