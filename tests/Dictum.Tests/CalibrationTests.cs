using Dictum.Calibration;
using Dictum.Core;
using Dictum.Personal;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>SPEC §16.5: word error rate, ranking under the latency ceiling, consistent misses, the engine.</summary>
public class WordErrorRateTests
{
    [Theory]
    [InlineData("the cat sat on the mat", "the cat sat on the mat", 0, 6)]
    [InlineData("the cat sat on the mat", "The cat sat on the mat.", 0, 6)] // case and punctuation do not count
    [InlineData("the cat sat on the mat", "the cat sat on a mat", 1, 6)] // one substitution
    [InlineData("the cat sat on the mat", "the cat sat on mat", 1, 6)] // one deletion
    [InlineData("a b c", "a x b c", 1, 3)] // one insertion
    [InlineData("a b c", "", 3, 3)] // nothing heard
    [InlineData("a b c", "c b a", 2, 3)]
    public void Known_cases(string reference, string heard, int edits, int words)
    {
        var wer = WordErrorRate.Compute(reference, heard);
        Assert.Equal(edits, wer.Edits);
        Assert.Equal(words, wer.ReferenceWords);
        Assert.Equal(edits / (double)words, wer.Rate, 6);
    }

    [Theory]
    [InlineData("We sold twenty three units last month, which is twelve percent more.", "We sold 23 units last month, which is 12% more.")]
    [InlineData("The meeting has moved to the fourteenth of March at ten thirty.", "The meeting has moved to the 14th of March at 10:30.")]
    [InlineData("at ten thirty", "at 10.30")] // whisper writes times with a full stop…
    [InlineData("at ten thirty", "at 1030")] // …or as four digits (both observed with small.en / tiny.en, 15/10/2026)
    [InlineData("Our budget is four thousand two hundred and fifty dollars.", "Our budget is $4,250.")]
    [InlineData("Our budget is four thousand two hundred and fifty dollars.", "Our budget is four thousand, two hundred and fifty dollars.")]
    [InlineData("You can email billing at northwind dot com dot au.", "You can email billing@northwind.com.au.")]
    [InlineData("I'll circulate the minutes.", "Ill circulate the minutes")]
    public void Spoken_and_written_numbers_and_addresses_compare_equal(string reference, string heard)
    {
        Assert.Equal(0, WordErrorRate.Compute(reference, heard).Edits);
    }

    [Fact]
    public void Number_words_become_digits_in_runs()
    {
        Assert.Equal(["105", "and", "6"], SpokenNumbers.ToDigits(["one", "hundred", "and", "five", "and", "six"]));
        Assert.Equal(["10", "30"], SpokenNumbers.ToDigits(["ten", "thirty"]));
        Assert.Equal(["21st", "3"], SpokenNumbers.ToDigits(["twenty", "first", "three"])); // an ordinal keeps its suffix (fix batch #9: "fourteen" is not "14th")
        Assert.Equal(["2000000", "dollars"], SpokenNumbers.ToDigits(["two", "million", "dollars"]));
        Assert.Equal(["and", "so", "on"], SpokenNumbers.ToDigits(["and", "so", "on"]));
    }

    [Fact]
    public void A_miss_carries_what_was_heard_in_its_place()
    {
        var wer = WordErrorRate.Compute("Remind Siobhan that the lease renews", "Remind shiv on that the lease renews");
        Assert.Equal(2, wer.Edits);
        var miss = Assert.Single(wer.Alignment, a => a.Op != AlignOp.Match);
        Assert.Equal(("siobhan", AlignOp.Substitute, "shiv on"), (miss.Reference, miss.Op, miss.Heard));
        Assert.Equal(AlignOp.Delete, WordErrorRate.Compute("send it to Siobhan now", "send it to now").Alignment[3].Op);
    }
}

public class CalibrationRankingTests
{
    /// <summary>A combination whose clips are each 10 s long, at the given WER and seconds per clip.</summary>
    internal static ComboResult Combo(string model, int beam, double wer, double secondsPerClip, int clips = 4, double audioSeconds = 10)
    {
        var outcomes = Enumerable.Range(1, clips).Select(i =>
        {
            var edits = (int)Math.Round(wer * 100);
            return new ClipOutcome(i, "", secondsPerClip, audioSeconds, new WerResult(edits, 100, []));
        }).ToList();
        return new ComboResult(model, beam, outcomes);
    }

    [Fact]
    public void Recommends_the_lowest_WER_at_or_under_the_latency_ceiling()
    {
        var accurateButSlow = Combo("medium.en", 5, wer: 0.03, secondsPerClip: 6.4);
        var best = Combo("small.en", 0, wer: 0.06, secondsPerClip: 2.0);
        var fast = Combo("base.en", 0, wer: 0.11, secondsPerClip: 0.9);
        var atTheCeiling = Combo("small.en", 5, wer: 0.06, secondsPerClip: 2.5);
        Assert.False(CalibrationRanking.WithinCeiling(accurateButSlow));
        Assert.True(CalibrationRanking.WithinCeiling(atTheCeiling));
        Assert.Same(best, CalibrationRanking.Recommend([fast, accurateButSlow, atTheCeiling, best])); // equal WER: the faster one
        Assert.Null(CalibrationRanking.Recommend([accurateButSlow]));
    }

    [Fact]
    public void Short_clips_are_measured_against_a_ten_second_dictation()
    {
        // whisper.cpp works on 30 s windows, so a 4 s clip costs about what a 10 s one does: 2.0 s for it is within the ceiling.
        Assert.True(CalibrationRanking.WithinCeiling(Combo("base.en", 0, 0.1, secondsPerClip: 2.0, audioSeconds: 4)));
        // A 20 s clip is allowed 5 s.
        Assert.True(CalibrationRanking.WithinCeiling(Combo("base.en", 0, 0.1, secondsPerClip: 5.0, audioSeconds: 20)));
        Assert.False(CalibrationRanking.WithinCeiling(Combo("base.en", 0, 0.1, secondsPerClip: 6.0, audioSeconds: 20)));
    }
}

public class MissedWordsTests
{
    private static readonly string[] Script =
    [
        "Please send the invoice to Siobhan before Friday.",
        "Siobhan thinks the Kowalski shipment arrives on Tuesday.",
        "Remind Siobhan that Kowalski Freight renews in August.",
    ];

    private static ComboResult Heard(params string[] transcripts) => new("small.en", 0,
        transcripts.Select((t, i) => new ClipOutcome(i + 1, t, 1, 5, WordErrorRate.Compute(Script[i], t))).ToList());

    [Fact]
    public void A_word_missed_the_same_way_every_time_gives_a_dictionary_entry_and_a_correction()
    {
        var combo = Heard(
            "Please send the invoice to Shivon before Friday.",
            "Shivon thinks the Kowalski shipment arrives on Tuesday.",
            "Remind Shivon that Kowalski Freight renews in August.");
        var s = Assert.Single(MissedWords.Find(combo, Script, PersonalDictionary.Empty, Corrections.Empty));
        Assert.Equal("Siobhan", s.Word);
        Assert.Equal("Add to dictionary: Siobhan", s.DictionaryText);
        Assert.Equal("Add correction: shivon => Siobhan", s.CorrectionText);
    }

    [Fact]
    public void An_unstable_wrong_form_gives_only_the_dictionary_entry()
    {
        var combo = Heard(
            "Please send the invoice to Shivon before Friday.",
            "Chevonne thinks the Kowalski shipment arrives on Tuesday.",
            "Remind Shivon that Kowalski Freight renews in August.");
        var s = Assert.Single(MissedWords.Find(combo, Script, PersonalDictionary.Empty, Corrections.Empty));
        Assert.Equal("Siobhan", s.Word);
        Assert.Null(s.Correction);
    }

    [Fact]
    public void Occasional_misses_single_occurrences_and_known_words_are_not_suggested()
    {
        var once = Heard(
            "Please send the invoice to Shivon before Friday.",
            "Siobhan thinks the Kowalski shipment arrives on Tuesday.",
            "Remind Siobhan that Kowalski Freight renews in Orgust."); // Siobhan missed 1 of 3; August appears once
        Assert.Empty(MissedWords.Find(once, Script, PersonalDictionary.Empty, Corrections.Empty));

        var always = Heard(
            "Please send the invoice to Shivon before Friday.",
            "Shivon thinks the Kowalsky shipment arrives on Tuesday.",
            "Remind Shivon that Kowalsky Freight renews in August.");
        var known = MissedWords.Find(always, Script, new PersonalDictionary(["siobhan"]), new Corrections(Corrections.Parse(["kowalsky => Kowalski"])));
        var s = Assert.Single(known); // Siobhan is already in the dictionary
        Assert.Equal("Kowalski", s.Word);
        Assert.Null(s.Correction); // that correction already exists
    }
}

public class CalibrationEngineTests
{
    private sealed class FakeEngine(string model, Func<string, DecodeOptions, int, string> hear, List<string> events) : ISpeechEngine
    {
        private int _clip;

        public string ModelPath => model;

        public DecodeOptions Decode { get; private set; } = DecodeOptions.Default;

        public void Reconfigure(DecodeOptions decode)
        {
            events.Add($"{model} reconfigure beam {decode.BeamSize}");
            Decode = decode;
            _clip = 0;
        }

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) =>
            Task.FromResult(samples.Length == 16000 ? "" : hear(model, Decode, _clip++)); // 1 s of silence is the warm-up

        public void Dispose() => events.Add($"{model} disposed");

        public FakeEngine Loaded(DecodeOptions decode)
        {
            Decode = decode;
            events.Add($"{model} loaded prompt \"{decode.Prompt}\" beam {decode.BeamSize}");
            return this;
        }
    }

    [Fact]
    public async Task Every_model_runs_greedy_then_beam_search_over_every_clip()
    {
        var events = new List<string>();
        CalibrationClip[] clips = [new(1, "send it to Siobhan", new float[48000]), new(2, "call Siobhan today", new float[48000])];
        string Hear(string model, DecodeOptions d, int clip) => model == "tiny.en" ? "send it to shivon" : clips[clip].Script;
        var engine = new CalibrationEngine((path, decode) => path == "broken"
                ? throw new InvalidDataException("bad file")
                : new FakeEngine(Path.GetFileNameWithoutExtension(path), Hear, events).Loaded(decode),
            NullLog.Instance, warmUp: () => true);

        var results = await engine.RunAsync(clips, [new("tiny.en", "tiny.en.bin"), new("bad", "broken"), new("small.en", "small.en.bin")], "Siobhan",
            null, TestContext.Current.CancellationToken);

        Assert.Equal(["tiny.en greedy", "tiny.en beam search (5)", "small.en greedy", "small.en beam search (5)"], results.Select(r => $"{r.Model} {r.Decoding}"));
        Assert.All(results, r => Assert.Equal(2, r.Clips.Count));
        Assert.Equal(0, results[2].Wer);
        Assert.True(results[0].Wer > 0);
        Assert.Equal(
            ["tiny.en loaded prompt \"Siobhan\" beam 0", "tiny.en reconfigure beam 5", "tiny.en disposed",
             "small.en loaded prompt \"Siobhan\" beam 0", "small.en reconfigure beam 5", "small.en disposed"], events);
        Assert.Equal("small.en", CalibrationRanking.Recommend(results)!.Model);
    }
}
