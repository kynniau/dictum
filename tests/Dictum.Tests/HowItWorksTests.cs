using System.Text.RegularExpressions;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// Settings > See how it works, the pipeline half (SPEC §29): <see cref="DictationPipeline.DemonstrateAsync"/> takes the same
/// journey as a dictation and reports each step, in order, with the pipeline's own texts and the very timings it logs; it
/// pastes nothing, does not tell "Fix last dictation", and logs a transcript only with log_transcripts on. And
/// <see cref="HowItWorksRun"/> reads those reports as the five stages, the validator's reason included.
/// </summary>
public class HowItWorksTests
{
    private static CapturedAudio Speech(double seconds = 2) => new(new float[(int)(16000 * seconds)], 0.2f);

    private sealed record Rig(DictationPipeline Pipeline, List<string> Inserted, List<string> Remembered, List<PipelineProgress> Steps, List<PillView> Pills);

    private static Rig Make(ITranscriber transcriber, TranscriptCleaner cleaner, AppSettings? settings = null, ILog? log = null, Func<string, string>? finish = null)
    {
        var inserted = new List<string>();
        var remembered = new List<string>();
        var pipeline = new DictationPipeline(_ => Task.FromResult(transcriber), () => cleaner, (text, _) =>
            {
                inserted.Add(text);
                return Task.CompletedTask;
            }, () => settings ?? new AppSettings(), log ?? NullLog.Instance, finish, remembered.Add);
        return new Rig(pipeline, inserted, remembered, [], []);
    }

    private static async Task<Rig> Demonstrate(Rig rig, CapturedAudio? audio = null)
    {
        await rig.Pipeline.DemonstrateAsync(audio ?? Speech(), CancellationToken.None, rig.Pills.Add, rig.Steps.Add);
        return rig;
    }

    /// <summary>A provider that answers after a pause, so the clean-up's time is not zero.</summary>
    private static TranscriptCleaner Model(string reply, ILog log, int delayMs = 0) =>
        new(new FakeLlmClient(async _ =>
        {
            await Task.Delay(delayMs, TestContext.Current.CancellationToken);
            return new LlmReply(reply, "end_turn");
        }), CleanupPrompt.Default, TimeSpan.FromSeconds(10), log, "fake/model (test)");

    private static long Logged(RecordingLog log, string pattern) => long.Parse(Regex.Match(log.All, pattern).Groups[1].Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_demonstration_reports_each_step_in_order_with_the_pipelines_texts_and_logged_timings_and_pastes_nothing(bool logTranscripts)
    {
        const string raw = "um so the fox does not jump";
        const string reply = "So the fox does not jump.";
        var log = new RecordingLog();
        var rig = await Demonstrate(Make(new FakeTranscriber(raw, gate: Task.Delay(60, TestContext.Current.CancellationToken)), Model(reply, log, delayMs: 40), new AppSettings { LogTranscripts = logTranscripts }, log,
            finish: text => text.Replace("fox", "Fox")));

        Assert.Equal([PipelineStep.Transcribed, PipelineStep.CleanedUp, PipelineStep.Finished], rig.Steps.Select(s => s.Step));
        Assert.Equal(raw, rig.Steps[0].Text);
        Assert.Equal(reply, rig.Steps[1].Text);
        Assert.Equal(raw, rig.Steps[1].Raw);
        Assert.True(rig.Steps[1].Cleanup!.Cleaned);
        Assert.Equal("So the Fox does not jump.", rig.Steps[2].Text); // corrections, the profanity filter and snippets ran
        // The timings are the ones the log carries for this dictation: one clock, the pipeline's.
        Assert.Equal(Logged(log, @"Transcribed 2\.0 s of audio in (\d+) ms"), rig.Steps[0].Ms);
        Assert.Equal(Logged(log, @"Cleanup by the model in (\d+) ms"), rig.Steps[1].Ms);
        Assert.True(rig.Steps[0].Ms >= 50 && rig.Steps[1].Ms >= 30, $"{rig.Steps[0].Ms} / {rig.Steps[1].Ms}");
        Assert.Null(rig.Steps[2].Ms); // the finishing step is not timed by the pipeline, so none is shown
        Assert.Equal([PillView.Polishing], rig.Pills); // the pill event a dictation gets

        Assert.Empty(rig.Inserted); // nothing pasted
        Assert.Empty(rig.Remembered); // and Fix last dictation keeps the last real dictation
        Assert.Contains("See how it works: nothing pasted", log.All);
        Assert.DoesNotContain("Inserted", log.All);
        Assert.Equal(logTranscripts, log.All.Contains(raw));
        Assert.Equal(logTranscripts, log.All.Contains(reply));
        Assert.Equal(logTranscripts, log.All.Contains("Fox")); // the finished text, in the "nothing pasted" line or anywhere, only with log_transcripts on
    }

    [Fact]
    public async Task A_dictation_still_pastes_and_reports_nothing()
    {
        var rig = Make(new FakeTranscriber("hello there"), TranscriptCleaner.RulesOnly("rules"));
        Assert.Equal(ProcessingOutcome.Inserted, await rig.Pipeline.RunAsync(Speech(), CancellationToken.None, rig.Pills.Add));
        Assert.Equal([DeterministicCleaner.Clean("hello there")], rig.Inserted);
        Assert.Equal(rig.Inserted, rig.Remembered);
    }

    [Fact]
    public async Task A_rejected_reply_shows_the_validators_rule_and_the_tidy_it_fell_back_to()
    {
        const string raw = "the fox does not jump";
        var rig = await Demonstrate(Make(new FakeTranscriber(raw), Model("The fox does jump.", NullLog.Instance)));
        var cleaned = rig.Steps[1];
        Assert.False(cleaned.Cleanup!.Cleaned);
        Assert.Equal("reply drops a negation", cleaned.Cleanup.FallbackReason);

        var run = Run(rig);
        var stages = run.Stages();
        Assert.Equal(StageState.Done, stages[3].State);
        Assert.Equal(HowItWorksText.Rejected("reply drops a negation"), stages[3].Note);
        Assert.Equal("Not used: reply drops a negation. Dictum used the rule-based tidy (step 3) instead.", stages[3].Note);
        Assert.Null(stages[3].Text); // the rejected reply is not shown as if it were used
        Assert.Equal(HowItWorksText.Took(cleaned.Ms!.Value), stages[3].Timing);
        Assert.Equal(DeterministicCleaner.Clean(raw), stages[2].Text);
        Assert.Equal(HowItWorksText.TidyUsed, stages[2].Note);
        Assert.Null(stages[2].Timing); // its time is inside step 4's: there is no second clock
        Assert.Equal(DeterministicCleaner.Clean(raw), stages[4].Text);
    }

    [Fact]
    public async Task An_accepted_reply_shows_the_tidy_as_not_used_untimed()
    {
        const string raw = "um the invoice went out today";
        var rig = await Demonstrate(Make(new FakeTranscriber(raw), Model("The invoice went out today.", NullLog.Instance)));
        var stages = Run(rig).Stages();
        Assert.Equal((StageState.NotUsed, DeterministicCleaner.Clean(raw), (string?)null, HowItWorksText.TidyNotNeeded),
            (stages[2].State, stages[2].Text, stages[2].Timing, stages[2].Note));
        Assert.Equal((StageState.Done, "The invoice went out today.", HowItWorksText.Accepted), (stages[3].State, stages[3].Text, stages[3].Note));
        Assert.Equal((HowItWorksText.WouldPaste, "The invoice went out today."), (stages[4].Note, stages[4].Text));
    }

    [Fact]
    public async Task The_rule_based_tidy_alone_is_the_clean_up_and_step_4_says_it_is_not_in_use()
    {
        var rig = await Demonstrate(Make(new FakeTranscriber("so it went out"), TranscriptCleaner.RulesOnly("rule-based tidy only (llm.provider = none)")));
        var stages = Run(rig).Stages();
        Assert.Equal((StageState.Done, DeterministicCleaner.Clean("so it went out"), HowItWorksText.Took(rig.Steps[1].Ms!.Value), HowItWorksText.TidyIsAll),
            (stages[2].State, stages[2].Text, stages[2].Timing, stages[2].Note));
        Assert.Equal((StageState.NotUsed, "Not in use. Clean-up in use: rule-based tidy only (llm.provider = none)."), (stages[3].State, stages[3].Note));
        Assert.Empty(rig.Pills); // no model: no "Polishing…"
    }

    [Fact]
    public async Task Clean_up_off_tidies_spaces_only()
    {
        var rig = await Demonstrate(Make(new FakeTranscriber("so  it went out"), TranscriptCleaner.Off()));
        var stages = Run(rig).Stages();
        Assert.Equal((DeterministicCleaner.WhitespaceOnly("so  it went out"), HowItWorksText.TidyPlain), (stages[2].Text, stages[2].Note));
        Assert.Equal(HowItWorksText.NotInUse(TranscriptCleaner.OffStatus), stages[3].Note);
    }

    [Fact]
    public async Task A_silent_recording_stops_at_the_gate_with_the_no_sound_sentence()
    {
        var transcriber = new FakeTranscriber("ghost words");
        var rig = await Demonstrate(Make(transcriber, TranscriptCleaner.RulesOnly("rules")), new CapturedAudio(new float[32000], 0.0001f));
        Assert.Equal(0, transcriber.Calls);
        var step = Assert.Single(rig.Steps);
        Assert.Equal(PipelineStep.Discarded, step.Step);
        var run = new HowItWorksRun();
        run.Recorded(TimeSpan.FromSeconds(2));
        run.Apply(step);
        var stages = run.Stages();
        Assert.Equal(NoSoundText.SilentBalloon, stages[0].Note);
        Assert.All(stages.Skip(1), s => Assert.Equal(StageState.NotReached, s.State));
    }

    [Fact]
    public async Task A_too_short_recording_and_no_words_heard_stop_where_a_dictation_would()
    {
        var rig = await Demonstrate(Make(new FakeTranscriber("x"), TranscriptCleaner.RulesOnly("rules")), Speech(0.1));
        var run = new HowItWorksRun();
        run.Recorded(TimeSpan.FromSeconds(0.1));
        run.Apply(Assert.Single(rig.Steps));
        Assert.StartsWith("Dictum stops here (too short (", run.Stages()[0].Note);

        rig = await Demonstrate(Make(new FakeTranscriber(""), TranscriptCleaner.RulesOnly("rules")));
        Assert.Equal([PipelineStep.Transcribed, PipelineStep.Discarded], rig.Steps.Select(s => s.Step));
        var stages = Run(rig).Stages();
        Assert.Equal((StageState.Done, (string?)null, HowItWorksText.StoppedHere(DictationPipeline.NoSpeech)), (stages[1].State, stages[1].Text, stages[1].Note));
        Assert.All(stages.Skip(2), s => Assert.Equal(StageState.NotReached, s.State));
    }

    [Fact]
    public void The_stages_fill_in_order_as_the_reports_arrive()
    {
        var run = new HowItWorksRun();
        Assert.All(run.Stages(), s => Assert.Equal(StageState.Waiting, s.State));
        run.StartRecording();
        Assert.Equal((StageState.Working, HowItWorksText.Recording), (run.Stages()[0].State, run.Stages()[0].Note));
        run.Recorded(TimeSpan.FromSeconds(3.2));
        Assert.Equal(["Done", "Working…", "Waiting", "Waiting", "Waiting"], run.Stages().Select(s => s.StateLabel));
        Assert.Equal(HowItWorksText.SoundLength(TimeSpan.FromSeconds(3.2)), run.Stages()[0].Timing);
        Assert.Equal("Transcribing…", run.Stages()[1].Note); // the pill's word
        run.Apply(new PipelineProgress(PipelineStep.Transcribed, "hello", 120));
        run.Pill(PillView.Polishing);
        Assert.Equal(["Done", "Done", "Working…", "Working…", "Waiting"], run.Stages().Select(s => s.StateLabel));
        Assert.Equal("Polishing…", run.Stages()[3].Note);
        Assert.Equal(HowItWorksText.Took(120), run.Stages()[1].Timing);
        Assert.Equal(HowItWorksText.Stages.Select(s => s.Title), run.Stages().Select(s => s.Title)); // one source for the labels
    }

    private static HowItWorksRun Run(Rig rig)
    {
        var run = new HowItWorksRun();
        run.Recorded(TimeSpan.FromSeconds(2));
        foreach (var step in rig.Steps) run.Apply(step);
        return run;
    }
}
