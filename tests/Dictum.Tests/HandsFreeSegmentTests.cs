using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// Hands-free "segments as you speak" (product owner decision 5, 16/10/2026): where the segmenter cuts, on synthetic block
/// levels. silence_threshold is 0.01; speech blocks are 0.2 RMS and quiet ones 0.001 unless a test says otherwise.
/// </summary>
public class SpeechSegmenterTests
{
    private const float Loud = 0.2f;
    private const float Quiet = 0.001f;

    /// <summary>Feeds runs of equal blocks; returns each cut with the block count fed when it came.</summary>
    private static List<(SegmentCut Cut, long At)> Feed(SpeechSegmenter segmenter, params (float Rms, int Blocks)[] runs)
    {
        var cuts = new List<(SegmentCut, long)>();
        foreach (var (rms, blocks) in runs)
        {
            for (var i = 0; i < blocks; i++)
            {
                if (segmenter.Add(rms) is { } cut) cuts.Add((cut, segmenter.Blocks));
            }
        }
        return cuts;
    }

    private static List<SegmentCut> FeedLevels(SpeechSegmenter segmenter, IEnumerable<float> levels)
    {
        var cuts = new List<SegmentCut>();
        foreach (var rms in levels)
        {
            if (segmenter.Add(rms) is { } cut) cuts.Add(cut);
        }
        return cuts;
    }

    [Fact]
    public void The_constants_are_the_designed_times()
    {
        Assert.Equal(20, SpeechSegmenter.BlockMs);
        Assert.Equal(700, SpeechSegmenter.PauseBlocks * SpeechSegmenter.BlockMs);
        Assert.Equal(2000, SpeechSegmenter.MinSpeechBlocks * SpeechSegmenter.BlockMs);
        Assert.Equal(30_000, SpeechSegmenter.LimitBlocks * SpeechSegmenter.BlockMs);
        Assert.Equal(3_000, SpeechSegmenter.LookBackBlocks * SpeechSegmenter.BlockMs);
        Assert.Equal(320, CaptureBuffer.BlockSamples);
    }

    [Fact]
    public void A_pause_after_two_seconds_of_speech_cuts_in_the_middle_of_the_pause()
    {
        var segmenter = new SpeechSegmenter(0.01);
        var cuts = Feed(segmenter, (Loud, 120), (Quiet, 35));

        var (cut, at) = Assert.Single(cuts);
        Assert.Equal(155, at);                // the 35th quiet block (0.7 s) ends the part
        Assert.Equal(138, cut.Block);         // 18 quiet blocks end this part, 17 start the next
        Assert.Equal(CutReason.Pause, cut.Reason);
        Assert.Equal(Loud, cut.Peak);
    }

    [Fact]
    public void A_pause_before_two_seconds_of_speech_does_not_cut()
    {
        var segmenter = new SpeechSegmenter(0.01);
        Assert.Empty(Feed(segmenter, (Loud, 75), (Quiet, 50))); // 1.5 s of speech, then a full second of quiet: no cut
        var cuts = Feed(segmenter, (Loud, 50), (Quiet, 35));     // speech in the part adds up across the pause: 2.5 s
        Assert.Equal(193, Assert.Single(cuts).Cut.Block);
    }

    [Fact]
    public void Continuous_speech_is_not_cut_before_thirty_seconds()
    {
        // 35 blocks of speech and 15 quiet (0.3 s gaps between words, shorter than a pause), thirty times over.
        var segmenter = new SpeechSegmenter(0.01);
        var levels = Enumerable.Range(0, 1500).Select(b => b % 50 < 35 ? Loud : Quiet).ToList();
        var cuts = new List<(SegmentCut Cut, long At)>();
        foreach (var rms in levels)
        {
            if (segmenter.Add(rms) is { } cut) cuts.Add((cut, segmenter.Blocks));
        }

        var (limit, at) = Assert.Single(cuts);
        Assert.Equal(1500, at); // nothing until the 30 s limit
        Assert.Equal(CutReason.Limit, limit.Reason);
        Assert.Equal(1499, limit.Block); // the latest of the quietest blocks in the look-back: a gap between words
    }

    [Fact]
    public void The_thirty_second_cut_lands_on_the_quietest_block_of_the_last_three_seconds()
    {
        var levels = Enumerable.Repeat(Loud, 1500).ToArray();
        levels[1200] = Quiet; // quieter, but 6 s back: outside the look-back
        levels[1400] = 0.03f; // the quietest block of the last 3 s (above the floor: no pause there)
        levels[1450] = 0.05f;
        var cut = Assert.Single(FeedLevels(new SpeechSegmenter(0.01), levels));
        Assert.Equal(CutReason.Limit, cut.Reason);
        Assert.Equal(1400, cut.Block);
        Assert.Equal(Loud, cut.Peak);
    }

    [Fact]
    public void A_cut_lands_on_a_quiet_block_when_the_look_back_has_one()
    {
        // Block 1380 (0.015) arrived below the floor (a tenth of the 0.2 speech level). Block 1460 (0.012) is lower, but by then
        // the speech level was 0.1, so it was above the floor: inside a sound. The cut takes the quiet block, never the sound.
        var levels = Enumerable.Range(0, 1500).Select(b => b < 1400 ? Loud : 0.1f).ToArray();
        levels[1380] = 0.015f;
        levels[1460] = 0.012f;
        var cut = Assert.Single(FeedLevels(new SpeechSegmenter(0.01), levels));
        Assert.Equal(1380, cut.Block);
    }

    [Fact]
    public void A_pause_cut_has_quiet_blocks_on_both_sides()
    {
        var levels = Enumerable.Repeat(Loud, 140).Concat(Enumerable.Repeat(Quiet, 35)).ToArray();
        var cut = Assert.Single(FeedLevels(new SpeechSegmenter(0.01), levels));
        Assert.True(levels[cut.Block - 1] < 0.01f && levels[cut.Block] < 0.01f, $"cut at block {cut.Block} touches a sound");
    }

    [Fact]
    public void A_pause_in_a_noisy_room_is_found_by_the_relative_floor()
    {
        // Room noise at 0.015 is above silence_threshold (0.01), so silence_threshold alone sees no pause; a tenth of the 0.3
        // speech level (0.03) does.
        var cuts = Feed(new SpeechSegmenter(0.01), (0.3f, 120), (0.015f, 35));
        Assert.Equal(138, Assert.Single(cuts).Cut.Block);
    }

    [Theory]
    [InlineData(0.0615f, true)]  // −24.2 dB under the speech: the 35th quiet block is still under the decayed floor
    [InlineData(0.0625f, false)] // −24.1 dB: by the 35th block the floor (0.1 × 0.986^34 ≈ 0.0619 of the speech) is under it
    public void A_pause_is_found_only_in_noise_more_than_about_24_dB_under_the_speech(float fraction, bool found)
    {
        // The ceiling the relative floor's simplification note states (merge lens finding 10: the note said 20 dB).
        var cuts = Feed(new SpeechSegmenter(0.01), (Loud, 120), (Loud * fraction, 200));
        Assert.Equal(found, cuts.Any(c => c.Cut.Reason == CutReason.Pause));
    }

    [Fact]
    public void Silence_threshold_stays_the_floor_for_a_quiet_speaker()
    {
        // A quiet speaker (0.05): a tenth of that is 0.005, so silence_threshold (0.01) decides: 0.008 is quiet.
        var cuts = Feed(new SpeechSegmenter(0.01), (0.05f, 120), (0.008f, 35));
        Assert.Equal(CutReason.Pause, Assert.Single(cuts).Cut.Reason);
    }

    [Fact]
    public void Each_part_reports_its_own_peak_even_when_the_loudest_block_left_the_look_back()
    {
        var segmenter = new SpeechSegmenter(0.01);
        var levels = Enumerable.Repeat(Loud, 300).ToArray();
        levels[10] = 0.5f; // 5.8 s before the cut: long gone from the 3 s rings
        var cuts = FeedLevels(segmenter, levels.Concat(Enumerable.Repeat(Quiet, 35)));
        Assert.Equal(0.5f, Assert.Single(cuts).Peak);

        FeedLevels(segmenter, Enumerable.Repeat(0.25f, 120));
        Assert.Equal(0.25f, segmenter.TailPeak); // the rest at the stop: only what followed the cut
    }

    [Fact]
    public void Audio_from_before_hands_free_belongs_to_the_first_part()
    {
        // Hands-free starts after the first tap: 30 blocks are already in the recording, their loudest 0.4.
        var segmenter = new SpeechSegmenter(0.01, firstBlock: 30, peakSoFar: 0.4f);
        var cut = Assert.Single(Feed(segmenter, (Loud, 120), (Quiet, 35))).Cut;
        Assert.Equal(30 + 155 - 17, cut.Block);
        Assert.Equal(0.4f, cut.Peak);
    }
}

/// <summary>The capture buffer in hands-free: parts handed over at each cut tile the recording; hold-to-talk is one recording.</summary>
public class SegmentingCaptureBufferTests
{
    /// <summary>
    /// Every sample is distinct (a rising ramp inside each level), so a lost, repeated or reordered sample shows. Loud blocks
    /// are about 0.25-0.32 RMS and quiet ones about 0.001-0.002.
    /// </summary>
    private static float[] Recording(params (bool Loud, int Blocks)[] runs)
    {
        var samples = new List<float>();
        foreach (var (loud, blocks) in runs)
        {
            for (var n = 0; n < blocks * CaptureBuffer.BlockSamples; n++)
            {
                var i = samples.Count;
                samples.Add(loud ? (float)(0.25 + i * 1e-7) : (float)(0.001 + i * 1e-9));
            }
        }
        for (var n = 0; n < 37; n++) samples.Add((float)(0.001 + samples.Count * 1e-9)); // a part-block at the stop
        return [.. samples];
    }

    [Fact]
    public void Parts_tile_the_recording_exactly()
    {
        var input = Recording((false, 25), (true, 150), (false, 40), (true, 200), (false, 50), (true, 1600), (false, 100));
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, 16000);
        var parts = new List<(float[] Samples, CutReason Reason)>();
        var fed = 0;
        void FeedTo(int end)
        {
            while (fed < end)
            {
                var n = Math.Min(1000, end - fed); // device packets that never line up with the 320-sample blocks
                buffer.AppendResampled(input.AsSpan(fed, n));
                fed += n;
            }
        }

        FeedTo(8100); // the first tap and the window for the second: in the recording before hands-free starts
        buffer.SegmentAtPauses(0.01, (audio, reason) => parts.Add((audio.Samples, reason)));
        FeedTo(input.Length);
        var tail = buffer.Seal();

        Assert.Equal([CutReason.Pause, CutReason.Pause, CutReason.Limit, CutReason.Pause], parts.Select(p => p.Reason));
        Assert.All(parts, p => Assert.Equal(0, p.Samples.Length % CaptureBuffer.BlockSamples)); // cuts are block boundaries
        Assert.Equal(input, parts.SelectMany(p => p.Samples).Concat(tail.Samples).ToArray()); // nothing lost, repeated or moved
    }

    [Fact]
    public void Hold_to_talk_is_one_recording()
    {
        var input = Recording((true, 150), (false, 40), (true, 200), (false, 50));
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, 16000);
        buffer.AppendResampled(input);
        Assert.Equal(input, buffer.Seal().Samples); // no SegmentAtPauses: every sample comes back at the stop
    }

    [Fact]
    public void Each_part_carries_its_own_peak_for_the_silence_check()
    {
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, 16000);
        var parts = new List<CapturedAudio>();
        buffer.SegmentAtPauses(0.01, (audio, _) => parts.Add(audio));
        buffer.AppendResampled(Enumerable.Repeat(0.3f, 120 * 320).Concat(Enumerable.Repeat(0.001f, 40 * 320)).ToArray());
        buffer.AppendResampled(new float[30 * 320]); // silence after the cut
        var tail = buffer.Seal();
        Assert.Equal(0.3f, Assert.Single(parts).PeakLevel, 3);
        Assert.True(tail.PeakLevel < 0.01f, $"the silent rest reported peak {tail.PeakLevel}, so the gate would transcribe it");
    }
}

/// <summary>The boundary rule and the carried words (SPEC §3 amendment, 16/10/2026).</summary>
public class SegmentJoinTests
{
    private static readonly string[] NoNames = [];

    [Fact]
    public void The_carried_words_are_the_last_ten_words_on_one_line()
    {
        Assert.Equal("three four five six seven eight nine ten eleven twelve.",
            SegmentJoin.Tail("One two\nthree four five six seven eight nine ten eleven twelve."));
        Assert.Equal("Hello there.", SegmentJoin.Tail("Hello there."));
        Assert.Null(SegmentJoin.Tail(" \n "));
        Assert.Null(new HandsFreePart(1, null).Carried);
        const string said = "Yes. And then I said that we should move the meeting to";
        Assert.Equal("then I said that we should move the meeting to", new HandsFreePart(2, new HandsFreePart(1, null) { Typed = said, Spoken = said }).Carried);
    }

    [Fact]
    public void A_parts_whisper_prompt_adds_the_carried_words_and_keeps_the_language_and_translate()
    {
        // Rebase onto main (16/10/2026): the language and translate travel in DecodeOptions now, and the processor built for a
        // part's carried words is built from WithCarried's options, so a Spanish part being translated stays one.
        const string carried = "we should move the meeting to";
        Assert.Equal(new DecodeOptions("Kynni, Parramatta. " + carried, 5, "es", Translate: true),
            new DecodeOptions("Kynni, Parramatta", 5, "es", Translate: true).WithCarried(carried));
        Assert.Equal(new DecodeOptions(carried, 0, "de"), new DecodeOptions(null, 0, "de").WithCarried(carried));
    }

    [Fact]
    public void The_boundary_rule_reads_what_was_typed_and_the_context_what_was_said()
    {
        // Merge lens P1, 16/10/2026: the carried words reach a cloud clean-up's prompt, so a snippet's text must never be among them.
        var last = new HandsFreePart(1, null) { Typed = "Send the draft to kyle@example.com.", Spoken = "Send the draft to my email." };
        var next = new HandsFreePart(2, last);
        Assert.Equal("Send the draft to kyle@example.com.", next.Before);
        Assert.Equal("Send the draft to my email.", next.Carried);
    }

    [Fact]
    public void After_terminal_punctuation_a_part_starts_with_its_capital_after_a_space()
    {
        Assert.Equal((" ", "Then we left."), SegmentJoin.Join("We met at ten.", "then we left", "Then we left.", trailingSpace: false, NoNames));
        Assert.Equal((" ", "Then we left."), SegmentJoin.Join("We met at ten.", "then we left", "then we left.", trailingSpace: false, NoNames));
        Assert.Equal((" ", "Then we left."), SegmentJoin.Join("Did we?", "then we left", "Then we left.", trailingSpace: false, NoNames));
        Assert.Equal((" ", "Then we left."), SegmentJoin.Join("He said \"stop.\"", "then we left", "Then we left.", trailingSpace: false, NoNames));
        Assert.Equal(("", "Then we left."), SegmentJoin.Join("We met at ten.", "then we left", "Then we left.", trailingSpace: true, NoNames));
    }

    /// <summary>
    /// The sentence end the boundary rule shares with the corrections and the profanity filter (SentenceStart, merge lens finding
    /// 9, 16/10/2026): . ! ? … or a line break, past spaces, quotes and brackets of either kind.
    /// </summary>
    [Theory]
    [InlineData("We met at ten!")]
    [InlineData("Was it ten…")]
    [InlineData("He said “stop.”")]
    [InlineData("He said 'stop.'")]
    [InlineData("We met (at ten.)")]
    [InlineData("See [the notes.]")]
    [InlineData("Il a dit «stop.»")]
    [InlineData("Thanks.\r\n")]
    [InlineData("Thanks,\n\"")] // a line break, then an opening quote (aligned 16/10/2026: it carried on before)
    [InlineData("वह आ गया।")]   // the danda (16/10/2026)
    [InlineData("वह आ गया॥")]
    public void After_any_sentence_end_a_part_starts_with_its_capital(string before)
    {
        Assert.Equal("Then we left.", SegmentJoin.Join(before, "then we left", "then we left.", trailingSpace: false, NoNames).Text);
    }

    [Theory]
    [InlineData("We met at ten,")]
    [InlineData("We met at (")]
    [InlineData("We met at “ten”")]
    public void Without_a_sentence_end_a_part_carries_on(string before)
    {
        Assert.Equal("then we left.", SegmentJoin.Join(before, "then we left", "Then we left.", trailingSpace: false, NoNames).Text);
    }

    [Fact]
    public void Without_terminal_punctuation_a_part_goes_on_in_lower_case_after_a_space()
    {
        Assert.Equal((" ", "and then we will see."), SegmentJoin.Join("We should move the meeting to Thursday", "and then we will see", "And then we will see.", false, NoNames));
        Assert.Equal(("", "and then we will see."), SegmentJoin.Join("We should move the meeting to Thursday,", "and then we will see", "And then we will see.", true, NoNames));
    }

    [Theory]
    [InlineData("I think so.")]
    [InlineData("I'm late.")]
    [InlineData("I’ll call.")]
    [InlineData("NASA called.")]
    [InlineData("PowerPoint is open.")]
    [InlineData("McDonald's is shut.")]
    [InlineData("Kynni will call.")]
    [InlineData("Kynni's team called.")]
    public void A_word_that_keeps_its_capital_mid_sentence_keeps_it(string cleaned)
    {
        Assert.Equal((" ", cleaned), SegmentJoin.Join("we spoke to", "x", cleaned, false, ["Kynni Pty Ltd", "parramatta"]));
    }

    [Fact]
    public void A_lower_case_dictionary_term_and_a_name_the_dictionary_lacks_are_lower_cased()
    {
        // The documented ceiling: only the dictionary knows which words are names.
        Assert.Equal((" ", "sarah tomorrow."), SegmentJoin.Join("We are meeting", "sarah tomorrow", "Sarah tomorrow.", false, ["Kynni"]));
        Assert.Equal((" ", "parramatta next."), SegmentJoin.Join("We drive to", "parramatta next", "Parramatta next.", false, ["parramatta"]));
    }

    [Fact]
    public void Line_breaks_on_either_side_of_a_cut_still_work()
    {
        // A part that ended with a spoken "new line": the next one starts on that line with its capital, no space before it.
        Assert.Equal(("", "Regards, Kyle."), SegmentJoin.Join("Thanks,\n", "regards kyle", "Regards, Kyle.", true, NoNames));
        Assert.Equal(("", "Regards, Kyle."), SegmentJoin.Join("Thanks,\n", "regards kyle", "Regards, Kyle.", false, NoNames));
        // A part that opened with one: the clean-up drops a break at the start of a text, so it goes between the parts.
        Assert.Equal(("\n\n", "Next topic is the budget."),
            SegmentJoin.Join("That is all for now", "New paragraph. Next topic is the budget.", "Next topic is the budget.", true, NoNames));
        Assert.Equal(("\n", "Kind regards."), SegmentJoin.Join("Thanks.", "um new line, kind regards", "Kind regards.", false, NoNames));
        // Words the clean-up kept are words, not a command.
        Assert.Equal((" ", "new line of credit approved."),
            SegmentJoin.Join("We got a", "new line of credit approved", "New line of credit approved.", false, NoNames));
    }

    [Fact]
    public void A_part_that_does_not_start_with_a_letter_is_left_as_it_is()
    {
        Assert.Equal((" ", "3 more things."), SegmentJoin.Join("There are", "3 more things", "3 more things.", false, NoNames));
        Assert.Equal((" ", "\"Hello,\" she said."), SegmentJoin.Join("And then", "hello she said", "\"Hello,\" she said.", false, NoNames));
        Assert.Equal((" ", "iPhone is fine."), SegmentJoin.Join("All good.", "iphone is fine", "iPhone is fine.", false, NoNames));
    }

    [Fact]
    public void Text_that_is_not_English_keeps_its_case_and_still_gets_its_separator()
    {
        // SPEC §23 (rebase onto main, 16/10/2026): with the plain clean-up, text is left exactly as whisper wrote it, so the
        // boundary rule changes no case on either side of a sentence end; the separator follows the usual rules.
        Assert.Equal((" ", "Madrid ayer."), SegmentJoin.Join("Fuimos a", "Madrid ayer.", "Madrid ayer.", false, NoNames, keepCase: true));
        Assert.Equal((" ", "gracias"), SegmentJoin.Join("Fuimos a Madrid ayer.", "gracias", "gracias", false, NoNames, keepCase: true));
        Assert.Equal((" ", "वह आ गया"), SegmentJoin.Join("हम कल", "वह आ गया", "वह आ गया", false, NoNames, keepCase: true));
        Assert.Equal(("", "Madrid ayer."), SegmentJoin.Join("Fuimos a", "Madrid ayer.", "Madrid ayer.", true, NoNames, keepCase: true));
        Assert.Equal(("", "gracias"), SegmentJoin.Join("Fuimos.\n", "gracias", "gracias", false, NoNames, keepCase: true));
    }
}

/// <summary>
/// The clean-up with a hands-free part's carried words (SPEC §3 amendment, 16/10/2026): they are context, never dictation,
/// so every check measures the reply against the part's own words, and a reply may not type them again.
/// </summary>
public class CarriedContextCleanupTests
{
    private static TranscriptCleaner Cleaner(ILlmClient client, IReadOnlyCollection<string>? terms = null) =>
        new(client, CleanupPrompt.Default, TimeSpan.FromSeconds(2), NullLog.Instance, "test", deadline: _ => TimeSpan.FromSeconds(2),
            validation: new ValidationContext(CleanupPrompt.Default, null, terms));

    [Fact]
    public async Task A_parts_dictionary_term_check_counts_only_the_parts_own_words()
    {
        // The carried words said "Kynni"; this part did not. Counted as dictation they would pay for the inserted Kynni.
        var client = FakeLlmClient.Replying("And then Kynni walked home.");
        var result = await Cleaner(client, ["Kynni"]).CleanAsync("and then we walked home", CancellationToken.None, carried: "I had lunch with Kynni");
        Assert.False(result.Cleaned);
        Assert.Equal("reply adds a dictionary term the speaker did not say", result.FallbackReason);
        Assert.Equal("And then we walked home.", result.Text); // the tidy of the part's own words
    }

    [Fact]
    public async Task The_carried_words_reach_a_provider_beside_the_part_never_in_it()
    {
        var client = FakeLlmClient.Replying("Thursday and then we will see.");
        var result = await Cleaner(client).CleanAsync("thursday and then we will see", CancellationToken.None, carried: "so we should move the meeting to");
        Assert.True(result.Cleaned);
        Assert.Equal("thursday and then we will see", client.LastUser); // the dictation is the part alone
        Assert.Equal(CleanupPrompt.WithCarried(CleanupPrompt.Default, "so we should move the meeting to"), client.LastSystem);
        Assert.EndsWith("do not repeat it: \"so we should move the meeting to\"", client.LastSystem);
    }

    [Fact]
    public async Task Without_carried_words_the_request_is_unchanged()
    {
        var client = FakeLlmClient.Replying("Hello there.");
        await Cleaner(client).CleanAsync("hello there", CancellationToken.None);
        Assert.Equal(CleanupPrompt.Default, client.LastSystem);
    }

    [Fact]
    public void A_reply_that_types_the_carried_words_again_is_rejected()
    {
        var context = new ValidationContext(CleanupPrompt.WithCarried(CleanupPrompt.Default, "so we should move the meeting to"), Carried: "so we should move the meeting to");
        var (text, rejection) = CleanupValidator.Validate("thursday", "Move the meeting to Thursday.", context);
        Assert.Null(text);
        Assert.Equal("reply repeats words already typed before this part", rejection);
        // Without the carried words the same reply passes every other rule: this rule is what stops it.
        Assert.NotNull(CleanupValidator.Validate("thursday", "Move the meeting to Thursday.", new ValidationContext(CleanupPrompt.Default)).Text);
    }

    [Theory]
    [InlineData("move the meeting to friday instead", "Move the meeting to Friday instead.")]
    [InlineData("move uh the meeting to friday instead", "Move the meeting to Friday instead.")] // a hesitation between said words
    [InlineData("and then we will see", "And then we will see.")]
    [InlineData("move the meeting to friday and move the meeting to monday", "Move the meeting to Friday and move the meeting to Monday.")] // said twice, kept twice
    public void A_part_that_says_the_carried_words_again_is_accepted(string raw, string reply)
    {
        var context = new ValidationContext(CleanupPrompt.WithCarried(CleanupPrompt.Default, "so we should move the meeting to"), Carried: "so we should move the meeting to");
        var (text, rejection) = CleanupValidator.Validate(raw, reply, context);
        Assert.True(text is not null, rejection);
    }

    /// <summary>
    /// Merge lens P2, 16/10/2026: faithful replies that share a pair with the carried words, which the pair rule rejected. (a) A
    /// filler dropped between said words; (b) a homophone fixed; (c) a dictionary term respelt (PR #4's term rule accepts it);
    /// (d) a filler's word used as a word, kept.
    /// </summary>
    [Theory]
    [InlineData("and then we need to finalise the budget", "so the like budget report is due on friday", "The budget report is due on Friday.", null)]
    [InlineData("we still have to buy the tickets", "we need to by the tickets tonight", "We need to buy the tickets tonight.", null)]
    [InlineData("I had lunch with Kynni", "and then i met with kinney again", "And then I met with Kynni again.", "Kynni")]
    [InlineData("they really like budgets", "i like budgets too", "I like budgets too.", null)]
    public void A_faithful_reply_that_shares_a_pair_with_the_carried_words_is_accepted(string carried, string raw, string reply, string? term)
    {
        var context = new ValidationContext(CleanupPrompt.WithCarried(CleanupPrompt.Default, carried), DictionaryTerms: term is null ? null : [term], Carried: carried);
        var (text, rejection) = CleanupValidator.Validate(raw, reply, context);
        Assert.True(text is not null, rejection);
    }

    [Fact]
    public void A_fix_that_changes_a_words_sound_into_a_carried_pair_is_still_rejected()
    {
        // The rule's documented ceiling (a false rejection: the tidy is typed): "buys" does not sound like the carried "buy".
        var context = new ValidationContext(CleanupPrompt.WithCarried(CleanupPrompt.Default, "we still have to buy the tickets"), Carried: "we still have to buy the tickets");
        Assert.Equal("reply repeats words already typed before this part",
            CleanupValidator.Validate("we need to buys the tickets tonight", "We need to buy the tickets tonight.", context).Rejection);
    }

    /// <summary>
    /// Lens on 93e7e72, 16/10/2026: a pair the part said that only sounds like a carried pair ("to nothing" for "the meeting",
    /// "to town" for "the team") used to pay for the carried pair even when the reply also kept the said one, so a reply that
    /// inserted the carried words beside the part's own was accepted. Pairs are counted now: the reply may have as many pairs
    /// that sound like a carried pair as the dictation has.
    /// </summary>
    [Theory]
    [InlineData("so we had the meeting", "it came to nothing in the end", "The meeting came to nothing in the end.")]
    [InlineData("so we had the meeting", "it came to nothing in the end", "The meeting. It came to nothing in the end.")]
    [InlineData("I spoke to the team", "we went to town afterwards", "The team went to town afterwards.")]
    public void A_reply_that_adds_a_carried_pair_beside_a_said_pair_that_only_sounds_like_it_is_rejected(string carried, string raw, string reply)
    {
        var context = new ValidationContext(CleanupPrompt.WithCarried(CleanupPrompt.Default, carried), Carried: carried);
        Assert.Equal("reply repeats words already typed before this part", CleanupValidator.Validate(raw, reply, context).Rejection);
        // Without the carried words the same reply passes every other rule: this rule is what stops it.
        Assert.NotNull(CleanupValidator.Validate(raw, reply, new ValidationContext(CleanupPrompt.Default)).Text);
    }

    [Fact]
    public void A_reply_that_swaps_a_said_pair_for_the_carried_one_it_sounds_like_is_still_accepted()
    {
        // The documented ceiling, the other way round from the pin above (a false acceptance): the part's own "to town" pays for
        // the carried "the team" once, so the swap passes. Pinned so the note in RepeatsCarried cannot go stale unseen.
        const string carried = "I spoke to the team";
        var context = new ValidationContext(CleanupPrompt.WithCarried(CleanupPrompt.Default, carried), Carried: carried);
        var (text, rejection) = CleanupValidator.Validate("we went to town afterwards", "We went to the team afterwards.", context);
        Assert.True(text is not null, rejection);
    }

    /// <summary>A local model stand-in that records which call it got.</summary>
    private sealed class RecordingLocalEngine : ILocalLlmEngine
    {
        public List<(string System, string Raw, string? Carried)> Calls { get; } = [];
        public string ModelPath => "fake.gguf";
        public (double Prompt, double Generation) Rates => (0, 0);
        public Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default) => Task.FromResult(TimeSpan.Zero);
        public int CountTokens(string text) => text.Length / 4;
        public TimeSpan? Predict(int dictationTokens) => null;

        public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
            CancellationToken cancellationToken) => Reply(systemPrompt, raw, null);

        public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, string carried, int maxTokens,
            CancellationToken cancellationToken) => Reply(systemPrompt, raw, carried);

        private Task<GenerationResult> Reply(string system, string raw, string? carried)
        {
            Calls.Add((system, raw, carried));
            return Task.FromResult(new GenerationResult(raw, true, 0, TimeSpan.Zero, 0, TimeSpan.Zero));
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void The_local_model_hears_the_carried_words_in_a_system_turn_of_their_own_after_the_cached_prefix()
    {
        // Merge lens, finding 4, 16/10/2026: folding the carried words into the user turn failed no test.
        (string, string)[] examples = [("um raw one", "Raw one."), ("raw two", "Raw two.")];
        var (turns, prefix) = CleanupSession.Turns("the prompt", examples, "thursday", "so we should move the meeting to");
        Assert.Equal(
        [
            ("system", "the prompt"), ("user", "um raw one"), ("assistant", "Raw one."), ("user", "raw two"), ("assistant", "Raw two."),
            ("system", CleanupPrompt.CarriedLine("so we should move the meeting to")), ("user", "thursday"),
        ], turns);
        Assert.Equal(5, prefix); // the system prompt and the examples: the turns the cache keeps

        var (whole, wholePrefix) = CleanupSession.Turns("the prompt", examples, "hello there", null);
        Assert.Equal([("system", "the prompt"), ("user", "um raw one"), ("assistant", "Raw one."), ("user", "raw two"), ("assistant", "Raw two."), ("user", "hello there")], whole);
        Assert.Equal(5, wholePrefix);
    }

    [Fact]
    public async Task The_local_model_gets_the_carried_words_after_its_cached_prompt_not_in_it()
    {
        var engine = new RecordingLocalEngine();
        var client = new LocalLlmClient(() => engine, "fake");
        await client.CompleteAsync(CleanupPrompt.Default, "thursday", "so we should move the meeting to", CancellationToken.None);
        await client.CompleteAsync(CleanupPrompt.Default, "hello there", CancellationToken.None);
        Assert.Equal([(CleanupPrompt.Default, "thursday", "so we should move the meeting to"), (CleanupPrompt.Default, "hello there", null)], engine.Calls);
    }
}
