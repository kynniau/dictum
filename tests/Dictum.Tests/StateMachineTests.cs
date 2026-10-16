using Dictum.Core;

namespace Dictum.Tests;

public class StateMachineTests
{
    private static readonly StateMachineOptions Options = new(TapMs: 300, DoubleTapMs: 400, HandsFreeMaxSeconds: 300);

    private static DictationStateMachine NewMachine() => new(Options);

    private static T Single<T>(IReadOnlyList<Effect> effects) where T : Effect => Assert.Single(effects.OfType<T>());

    private static DictationStateMachine HandsFree(out List<Effect> log)
    {
        var m = NewMachine();
        log = [];
        log.AddRange(m.HotkeyDown(0));
        log.AddRange(m.HotkeyUp(100));     // tap
        log.AddRange(m.HotkeyDown(300));   // second tap inside the 400 ms window
        Assert.Equal(DictationState.RecordingHandsFree, m.State);
        return m;
    }

    [Fact]
    public void Hold_then_release_transcribes()
    {
        var m = NewMachine();
        var down = m.HotkeyDown(0);
        Assert.Equal(DictationState.RecordingHold, m.State);
        Single<StartCaptureEffect>(down);
        Assert.Contains(new PillEffect(PillView.RecordingHold), down);

        var up = m.HotkeyUp(450);
        Assert.Equal(DictationState.Processing, m.State);
        var begin = Single<BeginProcessingEffect>(up);
        Assert.False(begin.AutoStopped);
        Assert.Contains(new PillEffect(PillView.Transcribing), up);
        Assert.DoesNotContain(up, e => e is DiscardCaptureEffect);
    }

    [Fact]
    public void Hold_exactly_tap_ms_counts_as_hold()
    {
        var m = NewMachine();
        m.HotkeyDown(1000);
        m.HotkeyUp(1300);
        Assert.Equal(DictationState.Processing, m.State);
    }

    [Fact]
    public void Short_tap_alone_is_discarded_after_the_window()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        Assert.Empty(m.HotkeyUp(120));
        Assert.Equal(DictationState.PendingTap, m.State);

        Assert.Empty(m.Tick(520)); // exactly 400 ms after the up: still inside the window
        Assert.Equal(DictationState.PendingTap, m.State);

        var expired = m.Tick(521);
        Assert.Equal(DictationState.Idle, m.State);
        Single<DiscardCaptureEffect>(expired);
        Assert.Contains(new PillEffect(PillView.Hidden), expired);
    }

    [Fact]
    public void Double_tap_enters_hands_free_without_restarting_capture()
    {
        var m = HandsFree(out var log);
        Assert.Single(log.OfType<StartCaptureEffect>());           // capture started once, on the first press
        Assert.DoesNotContain(log, e => e is DiscardCaptureEffect); // no audio thrown away
        Assert.Contains(new PillEffect(PillView.RecordingHandsFree), log);
    }

    [Fact]
    public void Consumed_second_press_key_up_is_ignored()
    {
        var m = HandsFree(out _);
        // The second tap is held well past tap_ms: its release must not stop or discard anything.
        Assert.Empty(m.HotkeyUp(2000));
        Assert.Equal(DictationState.RecordingHandsFree, m.State);
    }

    [Fact]
    public void Hands_free_stops_on_the_next_press()
    {
        var m = HandsFree(out _);
        m.HotkeyUp(350);
        var stop = m.HotkeyDown(8000);
        Assert.Equal(DictationState.Processing, m.State);
        Assert.False(Single<BeginProcessingEffect>(stop).AutoStopped);
        Assert.Empty(m.HotkeyUp(8100)); // release of the stopping press is ignored
        Assert.Equal(DictationState.Processing, m.State);
    }

    [Fact]
    public void Hands_free_auto_stops_at_300_seconds_and_transcribes()
    {
        var m = HandsFree(out _);
        m.HotkeyUp(350);
        Assert.Empty(m.Tick(299_999));
        Assert.Equal(DictationState.RecordingHandsFree, m.State);

        var effects = m.Tick(300_000);
        Assert.Equal(DictationState.Processing, m.State);
        Assert.True(Single<BeginProcessingEffect>(effects).AutoStopped);
        Assert.DoesNotContain(effects, e => e is DiscardCaptureEffect); // transcribe, never discard
    }

    [Fact]
    public void Stuck_hold_key_auto_stops_at_300_seconds_and_transcribes()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        Assert.Empty(m.Tick(150_000));
        var effects = m.Tick(300_001);
        Assert.Equal(DictationState.Processing, m.State);
        Assert.True(Single<BeginProcessingEffect>(effects).AutoStopped);
        Assert.DoesNotContain(effects, e => e is DiscardCaptureEffect);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("pending")] // hands-free: Esc stops and keeps what is typed (HandsFreeStateMachineTests)
    public void Esc_during_recording_discards(string where)
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        if (where != "hold") m.HotkeyUp(100);

        var effects = m.Escape(350);
        Assert.Equal(DictationState.Idle, m.State);
        Single<DiscardCaptureEffect>(effects);
        Assert.Contains(new PillEffect(PillView.Cancelled), effects);
        Assert.DoesNotContain(effects, e => e is BeginProcessingEffect);
    }

    [Fact]
    public void Esc_during_processing_cancels_the_token()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        var token = Single<BeginProcessingEffect>(m.HotkeyUp(500)).Token;
        Assert.False(token.IsCancellationRequested);

        var effects = m.Escape(600);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(DictationState.Idle, m.State);
        Single<CancelProcessingEffect>(effects);
    }

    [Fact]
    public void Esc_when_idle_does_nothing()
    {
        var m = NewMachine();
        Assert.Empty(m.Escape(0));
        Assert.Equal(DictationState.Idle, m.State);
    }

    [Fact]
    public void Hotkey_while_processing_is_ignored_with_busy_flash()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        var token = Single<BeginProcessingEffect>(m.HotkeyUp(500)).Token;
        var session = m.SessionId;

        var effects = m.HotkeyDown(700);
        Assert.Equal([new PillEffect(PillView.Busy)], effects);
        Assert.Empty(m.HotkeyUp(800));
        Assert.Equal(DictationState.Processing, m.State);
        Assert.False(token.IsCancellationRequested);

        var done = m.ProcessingFinished(session, ProcessingOutcome.Inserted);
        Assert.Equal(DictationState.Idle, m.State);
        Assert.Contains(new PillEffect(PillView.Done), done);
    }

    [Fact]
    public void Key_auto_repeat_is_ignored()
    {
        var m = NewMachine();
        var first = m.HotkeyDown(0);
        Assert.Single(first.OfType<StartCaptureEffect>());
        for (var t = 30; t < 600; t += 30) Assert.Empty(m.HotkeyDown(t)); // WM_KEYDOWN repeats while held
        Assert.Equal(DictationState.RecordingHold, m.State);
        Assert.Single(m.HotkeyUp(650).OfType<BeginProcessingEffect>());
    }

    [Fact]
    public void Auto_repeat_of_the_second_tap_does_not_stop_hands_free()
    {
        var m = HandsFree(out _);
        for (var t = 330; t < 1000; t += 30) Assert.Empty(m.HotkeyDown(t));
        Assert.Equal(DictationState.RecordingHandsFree, m.State);
    }

    [Fact]
    public void Disabled_ignores_everything()
    {
        var m = new DictationStateMachine(Options, enabled: false);
        Assert.Empty(m.HotkeyDown(0));
        Assert.Empty(m.HotkeyUp(500));
        Assert.Empty(m.Escape(600));
        Assert.Empty(m.Tick(400_000));
        Assert.Equal(DictationState.Idle, m.State);
    }

    [Fact]
    public void Disabling_while_recording_discards_and_while_processing_cancels()
    {
        var recording = NewMachine();
        recording.HotkeyDown(0);
        Single<DiscardCaptureEffect>(recording.SetEnabled(false));
        Assert.Equal(DictationState.Idle, recording.State);
        Assert.Empty(recording.HotkeyDown(100));

        var processing = NewMachine();
        processing.HotkeyDown(0);
        var token = Single<BeginProcessingEffect>(processing.HotkeyUp(500)).Token;
        Single<CancelProcessingEffect>(processing.SetEnabled(false));
        Assert.True(token.IsCancellationRequested);

        Assert.Empty(processing.SetEnabled(true));
        Assert.Single(processing.HotkeyDown(1000).OfType<StartCaptureEffect>());
    }

    [Fact]
    public void Stale_processing_completion_is_ignored()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        m.HotkeyUp(500);
        var oldSession = m.SessionId;
        m.Escape(600);                      // cancel session 1
        m.HotkeyDown(1000);
        m.HotkeyUp(1500);                   // session 2 now processing
        Assert.Empty(m.ProcessingFinished(oldSession, ProcessingOutcome.Inserted));
        Assert.Equal(DictationState.Processing, m.State);
    }

    [Fact]
    public void Late_down_after_window_starts_a_fresh_recording()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        m.HotkeyUp(100);
        var effects = m.HotkeyDown(900); // window lapsed with no Tick in between
        Single<DiscardCaptureEffect>(effects);
        Single<StartCaptureEffect>(effects);
        Assert.Equal(DictationState.RecordingHold, m.State);
    }

    [Fact]
    public void Capture_failure_returns_to_idle_with_mic_error()
    {
        var m = NewMachine();
        m.HotkeyDown(0);
        var effects = m.CaptureFailed(m.SessionId);
        Assert.Equal(DictationState.Idle, m.State);
        Assert.Contains(new PillEffect(PillView.MicError), effects);
    }
}
