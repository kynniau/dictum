using Dictum.Llm;

namespace Dictum.Core;

/// <summary>Where a stage of Settings &gt; See how it works is.</summary>
public enum StageState { Waiting, Working, Done, NotUsed, NotReached }

/// <summary>One stage as the page shows it: its label, where it is, its text, its time (the pipeline's) and what it means.</summary>
public sealed record StageView(string Title, string Description, StageState State, string? Text = null, string? Timing = null, string? Note = null)
{
    public string StateLabel => State switch
    {
        StageState.Working => HowItWorksText.Working,
        StageState.Done => HowItWorksText.StageDone,
        StageState.NotUsed => HowItWorksText.NotUsed,
        StageState.NotReached => HowItWorksText.NotReached,
        _ => HowItWorksText.Waiting,
    };
}

/// <summary>
/// One demonstration on Settings &gt; See how it works (SPEC §29): what the recording and the pipeline have reported so far,
/// read as the five stages. It holds only what the pipeline reported (<see cref="PipelineProgress"/>) and the recording's
/// length; nothing is timed or worked out here except the rule-based tidy shown beside an accepted AI reply, which is the
/// tidy's own function (<see cref="DeterministicCleaner.Clean"/>), labelled as not used. Not thread-safe: the page feeds it
/// on the UI thread.
/// </summary>
public sealed class HowItWorksRun
{
    private bool _recording;
    private TimeSpan? _recorded;
    private bool _polishing;
    private PipelineProgress? _heard;
    private PipelineProgress? _cleaned;
    private PipelineProgress? _finished;
    private string? _stopped;
    private bool _ended;

    /// <summary>Cloud speech's host while it is the speech step in use, else null: step 2 then says who turns the sound into words.</summary>
    public string? SpeechHost { get; set; }

    /// <summary>The microphone is open for this demonstration.</summary>
    public void StartRecording() => _recording = true;

    /// <summary>The microphone closed with this much sound; the pipeline starts.</summary>
    public void Recorded(TimeSpan duration)
    {
        _recording = false;
        _recorded = duration;
    }

    /// <summary>The pill event a dictation's pill gets: "Polishing…" while a model cleans up.</summary>
    public void Pill(PillView view) => _polishing = view is PillView.Polishing or PillView.PolishingSkipped;

    public void Apply(PipelineProgress progress)
    {
        switch (progress.Step)
        {
            case PipelineStep.Transcribed:
                _heard = progress;
                break;
            case PipelineStep.CleanedUp:
                _cleaned = progress;
                _polishing = false;
                break;
            case PipelineStep.Finished:
                _finished = progress;
                _ended = true;
                break;
            default:
                _stopped = progress.Reason ?? "";
                _ended = true;
                break;
        }
    }

    /// <summary>The run ended early (cancelled or failed): stages not reached say so.</summary>
    public void End()
    {
        _recording = false;
        _polishing = false;
        _ended = true;
    }

    /// <summary>The pipeline has reported its last step, or the run was ended.</summary>
    public bool Ended => _ended;

    public IReadOnlyList<StageView> Stages() => [Hears(), Heard(), Tidy(), Polish(), Paste()];

    private StageView View(int stage, StageState state, string? text = null, string? timing = null, string? note = null) =>
        new(HowItWorksText.Stages[stage].Title, stage == 1 && SpeechHost is { } host ? HowItWorksText.HeardByCloud(host) : HowItWorksText.Stages[stage].Description,
            state, text, timing, note);

    /// <summary>The state of a stage the pipeline has not reported: working while the run goes on, else not reached (or waiting).</summary>
    private StageState Pending(bool started) => _ended ? StageState.NotReached : started ? StageState.Working : StageState.Waiting;

    private StageView Hears()
    {
        if (_recording) return View(0, StageState.Working, note: HowItWorksText.Recording);
        if (_recorded is not { } length) return View(0, StageState.Waiting);
        var note = _heard is null && _stopped is { } reason ? Stopped(reason) : null; // stopped by the recording gate
        return View(0, StageState.Done, timing: HowItWorksText.SoundLength(length), note: note);
    }

    private static string Stopped(string reason) =>
        reason.StartsWith("silent", StringComparison.Ordinal) ? NoSoundText.SilentBalloon : HowItWorksText.StoppedHere(reason); // RecordingGate's "silent (…)"

    private StageView Heard()
    {
        if (_heard is not { } heard)
        {
            var state = Pending(_recorded is not null);
            return View(1, state, note: state == StageState.Working ? PillText.For(PillView.Transcribing, TimeSpan.Zero) : null); // the pill's word
        }
        var none = heard.Text.Length == 0;
        return View(1, StageState.Done, none ? null : heard.Text, Timing(heard.Ms), none ? Stopped(_stopped ?? DictationPipeline.NoSpeech) : null);
    }

    private static string? Timing(long? ms) => ms is { } value ? HowItWorksText.Took(value) : null;

    private bool Cleaning => _heard is { Text.Length: > 0 };

    private StageView Tidy()
    {
        if (_cleaned is not { Cleaner: { } cleaner, Cleanup: { } result } cleaned) return View(2, Pending(Cleaning));
        if (cleaner.IsPlain) return View(2, StageState.Done, result.Text, Timing(cleaned.Ms), HowItWorksText.TidyPlain);
        if (!cleaner.UsesModel) return View(2, StageState.Done, result.Text, Timing(cleaned.Ms), HowItWorksText.TidyIsAll);
        if (!result.Cleaned) return View(2, StageState.Done, result.Text, note: HowItWorksText.TidyUsed); // its time is inside step 4's
        // The AI reply was accepted, so the pipeline did not run the tidy: shown, untimed, as what a rejected reply would have got.
        return View(2, StageState.NotUsed, DeterministicCleaner.Clean(cleaned.Raw ?? ""), note: HowItWorksText.TidyNotNeeded);
    }

    private StageView Polish()
    {
        if (_cleaned is not { Cleaner: { } cleaner, Cleanup: { } result } cleaned)
        {
            var state = Pending(Cleaning);
            return View(3, state, note: state == StageState.Working && _polishing ? PillText.For(PillView.Polishing, TimeSpan.Zero) : null);
        }
        if (!cleaner.UsesModel) return View(3, StageState.NotUsed, note: HowItWorksText.NotInUse(cleaner.Status));
        if (result.Cleaned) return View(3, StageState.Done, result.Text, Timing(cleaned.Ms), HowItWorksText.Accepted);
        var reason = result.FallbackReason ?? "";
        var note = result.Skipped ? HowItWorksText.NotAsked(reason.StartsWith("skipped: ", StringComparison.Ordinal) ? reason["skipped: ".Length..] : reason)
            : HowItWorksText.Rejected(reason);
        return View(3, StageState.Done, timing: Timing(cleaned.Ms), note: note);
    }

    private StageView Paste()
    {
        if (_finished is not { } finished) return View(4, Pending(_cleaned is not null));
        if (string.IsNullOrWhiteSpace(finished.Text)) return View(4, StageState.Done, note: HowItWorksText.NothingLeft);
        var changed = finished.Text != _cleaned?.Text;
        return View(4, StageState.Done, finished.Text, note: changed ? $"{HowItWorksText.Changed} {HowItWorksText.WouldPaste}" : HowItWorksText.WouldPaste);
    }
}
