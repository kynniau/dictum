using Dictum.Audio;
using Dictum.Calibration;
using Dictum.Core;
using Dictum.Personal;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Fix batch, items 4, 6, 15 and 20 (#6-#14, #58): calibration scoring, results, recordings and the window's rules.</summary>
public sealed class FixBatchCalibrationTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-fixd-");

    private const string Meeting = "The meeting with Kowalski Freight has moved to the fourteenth of March at ten thirty.";

    [Theory]
    [InlineData(Meeting, "The meeting with Kowalski Freight has moved to 14 March at 10.30.")] // small.en greedy's real output
    [InlineData(Meeting, "The meeting with Kowalski Freight has moved to March 14 at 10:30.")]
    [InlineData(Meeting, "The meeting with Kowalski Freight has moved to the 14th of March at 10:30am.")]
    [InlineData("We sold twenty three units, which is twelve percent more.", "We sold 23 units, which is 12 per cent more.")]
    [InlineData("We sold twenty three units.", "We sold twenty-three units.")]
    [InlineData("You can email the accounts team.", "You can e-mail the accounts team.")]
    [InlineData("Thanks everyone, I'll circulate the minutes.", "Thanks everyone, I will circulate the minutes.")]
    [InlineData("billing at northwind dot com dot au", "billing at northwind dot com dot A U")]
    [InlineData("billing at northwind dot com dot au", "billing@northwind.com.A.U.")]
    [InlineData("at ten thirty", "at 10:30 a.m.")]
    [InlineData("Our budget is four thousand two hundred and fifty dollars.", "Our budget is $4,250.00.")]
    public void Formatting_is_not_scored_as_mishearing(string script, string heard) =>
        Assert.Equal(0, WordErrorRate.Compute(script, heard).Edits);

    [Theory]
    [InlineData("Our budget is four thousand two hundred and fifty dollars.", "Our budget is $42.50.")] // a hundred-fold amount error
    [InlineData("Our budget is four hundred and twenty five dollars.", "Our budget is $4.25.")]
    [InlineData("We meet at ten thirty.", "We meet at 1,030.")]
    [InlineData("He came fourteenth.", "He came fourteen.")]
    public void A_different_amount_time_or_rank_is_a_mishearing(string script, string heard) =>
        Assert.True(WordErrorRate.Compute(script, heard).Edits > 0);

    private sealed class Sync<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task Cancelling_the_test_keeps_the_combinations_that_finished()
    {
        var clips = new[] { new CalibrationClip(1, "the cat sat", new float[32000]) };
        var engine = new CalibrationEngine((path, decode) => new FakeSpeechEngine(path, decode, waitForCancel: path == "slow"), NullLog.Instance);
        var finished = new List<ComboResult>();
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(clips, [new SpeechCandidate("tiny.en", "fast"), new SpeechCandidate("medium.en", "slow")], null,
            new Sync<string>(line => { if (line.StartsWith("medium.en", StringComparison.Ordinal)) cts.Cancel(); }), cts.Token, new Sync<ComboResult>(finished.Add));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["tiny.en greedy", "tiny.en beam search (5)"], finished.Select(c => $"{c.Model} {c.Decoding}"));
    }

    private CalibrationRecordings Recordings(params string[] script) => new(Path.Combine(_dir.FullName, "calibration"), script);

    private static CapturedAudio Seconds(double seconds) => new(Enumerable.Repeat(0.2f, (int)(seconds * MonoResampler.WhisperRate)).ToArray(), 0.2f);

    [Fact]
    public void A_failed_save_leaves_no_partial_recording_behind()
    {
        var recordings = Recordings("one", "two", "three");
        Directory.CreateDirectory(recordings.PathFor(3)); // the final name is taken, so the move fails
        Assert.ThrowsAny<Exception>(() => recordings.Save(3, Seconds(2)));
        Assert.False(File.Exists(recordings.PathFor(3) + ".tmp"), "a voice recording was left behind as clip-03.wav.tmp");
    }

    [Fact]
    public void Delete_recordings_removes_leftovers_and_the_folder_and_counts_recordings_only()
    {
        var recordings = Recordings("one", "two");
        recordings.Save(1, Seconds(2));
        File.WriteAllBytes(recordings.PathFor(2) + ".tmp", [1, 2, 3]); // a take whose save failed before this batch
        Assert.Equal(1, recordings.DeleteAll());
        Assert.False(Directory.Exists(recordings.Directory));
    }

    [Fact]
    public void A_take_recorded_for_an_older_sentence_is_not_scored_against_the_new_one()
    {
        var before = Recordings("Please send the invoice.", "The lease renews in August.");
        before.Save(1, Seconds(2));
        before.Save(2, Seconds(2));
        var after = Recordings("Please send the invoice.", "The lease renews in September."); // an upgrade rewords sentence 2
        Assert.Equal([2], after.StaleTakes());
        Assert.Equal([1], after.LoadClips().Select(c => c.Index));
    }

    [Fact]
    public void The_window_never_lets_a_run_start_mid_take_and_keeps_that_take_stoppable()
    {
        var session = new CalibrationSession();
        session.StartTake(10);
        Assert.False(session.CanRun);
        Assert.False(session.CanDelete);
        Assert.True(session.CanRecord(10)); // its Stop
        Assert.False(session.CanRecord(3));
        session.EndTake();
        session.SetRunning(true);
        Assert.False(session.CanDelete); // deleting mid-run would bring the deleted results back when it ends
        Assert.False(session.CanRecord(3));
    }

    [Fact]
    public void A_take_longer_than_thirty_seconds_is_discarded()
    {
        Assert.Null(CalibrationSession.RejectTake(Seconds(6), 0.01));
        Assert.Contains("30 s", CalibrationSession.RejectTake(Seconds(31), 0.01), StringComparison.Ordinal);
    }

    [Fact]
    public void Results_always_come_from_the_recordings_on_disk()
    {
        var session = new CalibrationSession();
        session.Add(CalibrationRankingTests.Combo("tiny.en", 0, 0.1, 1), clipSet: "A");
        session.Add(CalibrationRankingTests.Combo("base.en", 0, 0.1, 1), clipSet: "A");
        IReadOnlyList<SpeechCandidate> all = [new("tiny.en", "t"), new("base.en", "b"), new("medium.en", "m")];
        Assert.Equal(["medium.en"], session.ModelsToRun(all, "medium.en", "A").Select(m => m.Name)); // same recordings: just the new model
        Assert.Equal(3, session.ModelsToRun(all, "medium.en", "B").Count); // re-recorded since: every model again

        session.Add(CalibrationRankingTests.Combo("medium.en", 0, 0.1, 1), clipSet: "B");
        Assert.Equal(["medium.en"], session.Results.Select(r => r.Model)); // never mixed with results from other audio
        session.RecordingsChanged(); // a take saved or the recordings deleted
        Assert.Empty(session.Results);
    }

    [Fact]
    public void A_correction_is_offered_only_if_it_changes_what_whisper_wrote()
    {
        string[] script = ["Could you check the Parramatta office?", "Remind Siobhan that the Parramatta lease renews."];
        string[] heard = ["Could you check the Para-Matta office?", "Remind Siobhan that the Para-Matta lease renews."];
        var combo = new ComboResult("small.en", 0, [.. heard.Select((t, i) => new ClipOutcome(i + 1, t, 1, 5, WordErrorRate.Compute(script[i], t)))]);
        var s = Assert.Single(MissedWords.Find(combo, script, PersonalDictionary.Empty, Corrections.Empty));
        Assert.Equal("Parramatta", s.Word); // the dictionary entry is still offered
        Assert.Null(s.Correction); // a rule that leaves "Para-Matta" as it is would do nothing
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
