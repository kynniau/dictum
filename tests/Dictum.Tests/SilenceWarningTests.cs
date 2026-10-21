using System.Collections.Concurrent;
using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;

namespace Dictum.Tests;

/// <summary>
/// "No sound from mic" (21/10/2026): a recording whose microphone gives no sound says so instead of showing "Recording" with
/// nothing behind it (the product owner's virtual desktop, whose microphone was not redirected). The state machine is pinned
/// pure, as in StateMachineTests; the controller with a microphone stand-in that reports its first sound when told.
/// </summary>
public class SilenceWarningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Grace = DictationStateMachine.NoSoundGraceMs;

    private static T Single<T>(IReadOnlyList<Effect> effects) where T : Effect => Assert.Single(effects.OfType<T>());

    /// <summary>A hold whose capture reports its first sound (as WasapiCapture does).</summary>
    private static DictationStateMachine Holding()
    {
        var m = new DictationStateMachine(StateMachineOptions.Default);
        m.HotkeyDown(0);
        m.WatchForSound(m.SessionId);
        return m;
    }

    /// <summary>A hands-free session (tap, tap) whose capture reports its first sound.</summary>
    private static DictationStateMachine HandsFree(out List<Effect> log)
    {
        var m = new DictationStateMachine(StateMachineOptions.Default);
        log = [.. m.HotkeyDown(0)];
        m.WatchForSound(m.SessionId);
        log.AddRange(m.HotkeyUp(100));
        log.AddRange(m.HotkeyDown(300));
        log.AddRange(m.HotkeyUp(400)); // the second tap's key-up, consumed
        Assert.Equal(DictationState.RecordingHandsFree, m.State);
        return m;
    }

    [Fact]
    public void Silence_after_the_start_shows_the_warning_and_one_balloon_while_recording_goes_on()
    {
        var m = Holding();
        Assert.Empty(m.Tick(Grace - 1));

        var warned = m.Tick(Grace);
        Assert.Equal(DictationState.RecordingHold, m.State); // recording goes on: the speaker may only be slow to start
        Assert.Contains(new PillEffect(PillView.NoSound), warned);
        Assert.Equal(new NoSoundEffect(m.SessionId, NoSoundCause.Silent), Single<NoSoundEffect>(warned));
        Assert.DoesNotContain(warned, e => e is DiscardCaptureEffect or BeginProcessingEffect);

        Assert.Empty(m.Tick(Grace + 50)); // the warning stays, and is not repeated
        Assert.Empty(m.Tick(Grace * 10));
    }

    [Fact]
    public void Sound_arriving_after_the_warning_goes_back_to_the_recording_label_and_the_dictation_is_transcribed()
    {
        var m = Holding();
        m.Tick(Grace);
        Assert.Equal([new PillEffect(PillView.RecordingHold)], m.SoundHeard(m.SessionId));
        Assert.Empty(m.SoundHeard(m.SessionId)); // only the first sound matters
        Assert.Empty(m.Tick(Grace * 10)); // heard once: later quiet is a pause, never a warning

        var up = m.HotkeyUp(Grace * 10);
        Single<BeginProcessingEffect>(up);
        Assert.Contains(new PillEffect(PillView.Transcribing), up);

        var free = HandsFree(out _);
        free.Tick(Grace);
        Assert.Equal([new PillEffect(PillView.RecordingHandsFree)], free.SoundHeard(free.SessionId));
    }

    [Fact]
    public void A_hold_ending_with_nothing_heard_ends_on_the_warning_and_nothing_is_transcribed()
    {
        var m = Holding();
        m.Tick(Grace);
        var up = m.HotkeyUp(Grace + 500);
        Assert.Equal(DictationState.Idle, m.State);
        Single<DiscardCaptureEffect>(up);
        Assert.Contains(new PillEffect(PillView.NoSoundEnded), up);
        Assert.DoesNotContain(new PillEffect(PillView.Transcribing), up);
        Assert.DoesNotContain(up, e => e is BeginProcessingEffect);
        Assert.DoesNotContain(up, e => e is NoSoundEffect); // the balloon was given at the warning: one per session
        Single<HeardNothingEffect>(up);

        // The same for a hands-free stop and for the auto-stop: nothing typed, and the session's token is cancelled.
        var free = HandsFree(out var log);
        var token = Single<BeginSegmentsEffect>(log).Token;
        free.Tick(Grace);
        var stop = free.HotkeyDown(Grace + 500);
        Assert.Contains(new PillEffect(PillView.NoSoundEnded), stop);
        Assert.DoesNotContain(stop, e => e is BeginProcessingEffect);
        Assert.True(token.IsCancellationRequested);

        var stuck = Holding();
        stuck.Tick(Grace);
        var auto = stuck.Tick(StateMachineOptions.DefaultHoldMaxSeconds * 1000L);
        Assert.Contains(new PillEffect(PillView.NoSoundEnded), auto);
        Assert.DoesNotContain(auto, e => e is BeginProcessingEffect);
    }

    [Fact]
    public void No_microphone_listed_warns_at_once_and_does_not_stay_on_recording()
    {
        var m = new DictationStateMachine(StateMachineOptions.Default);
        m.HotkeyDown(0);
        var effects = m.NoMicrophone(m.SessionId);
        Assert.Equal(DictationState.Idle, m.State);
        Single<DiscardCaptureEffect>(effects);
        Assert.Contains(new PillEffect(PillView.NoSoundEnded), effects);
        Assert.Equal(new NoSoundEffect(m.SessionId, NoSoundCause.NoMicrophone), Single<NoSoundEffect>(effects));
        Single<HeardNothingEffect>(effects);
        Assert.DoesNotContain(new PillEffect(PillView.MicError), effects);
        Assert.Empty(m.HotkeyUp(500)); // the key-up finds nothing to stop
    }

    [Fact]
    public void Mic_error_is_unchanged_when_the_microphone_cannot_be_opened_or_is_lost()
    {
        var m = Holding();
        var failed = m.CaptureFailed(m.SessionId);
        Assert.Equal([new DiscardCaptureEffect(m.SessionId), new PillEffect(PillView.MicError)], failed);

        var lost = Holding();
        lost.Tick(Grace); // even with the warning up, a lost device is "Mic error" and what was captured is transcribed
        var effects = lost.CaptureLost(lost.SessionId);
        Single<BeginProcessingEffect>(effects);
        Assert.Contains(new PillEffect(PillView.MicError), effects);
        Assert.DoesNotContain(effects, e => e is NoSoundEffect or HeardNothingEffect);
    }

    [Fact]
    public void Hands_free_is_not_warned_for_a_pause_shorter_than_the_grace_period_or_any_pause_after_a_sound()
    {
        // A normal pause before the first word (1.5 s), and quiet for just under the grace period: nothing.
        var slow = HandsFree(out _);
        Assert.Empty(slow.Tick(1500));
        Assert.Empty(slow.Tick(Grace - 1));
        Assert.Empty(slow.SoundHeard(slow.SessionId));
        Assert.Empty(slow.Tick(Grace * 30)); // then a long pause between parts: still nothing

        // Spoke at once, then paused far longer than the grace period: the segments go on as before.
        var m = HandsFree(out var log);
        m.SoundHeard(m.SessionId);
        Assert.Empty(m.Tick(Grace * 5));
        Assert.Equal(DictationState.RecordingHandsFree, m.State);
        var stop = m.HotkeyDown(Grace * 5);
        Single<BeginProcessingEffect>(stop);
        Assert.Single(log.OfType<BeginSegmentsEffect>());
    }

    [Fact]
    public void A_capture_that_cannot_report_sound_never_warns()
    {
        var m = new DictationStateMachine(StateMachineOptions.Default);
        m.HotkeyDown(0); // no WatchForSound: as with a capture whose ReportsSound is false
        Assert.Empty(m.Tick(Grace * 10));
        Single<BeginProcessingEffect>(m.HotkeyUp(Grace * 10));
        Assert.False(((IAudioCapture)new FakeCapture()).ReportsSound); // the interface default
    }

    [Fact]
    public void The_capture_buffer_reports_its_first_sound_once_and_never_for_digital_silence()
    {
        var heard = 0;
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, MonoResampler.WhisperRate, () => heard++);
        buffer.AppendResampled(new float[CaptureBuffer.BlockSamples * 100]); // two seconds of zeros
        Assert.Equal(0, heard);
        var whisper = Enumerable.Repeat(CaptureBuffer.SoundFloor * 0.5f, CaptureBuffer.BlockSamples * 10).ToArray();
        buffer.AppendResampled(whisper); // below the floor
        Assert.Equal(0, heard);

        var oneBit = Enumerable.Range(0, CaptureBuffer.BlockSamples).Select(i => (i % 2 == 0 ? 1f : -1f) / 32768f).ToArray();
        buffer.AppendResampled(oneBit); // one 16-bit step of noise: a real microphone in a silent room
        Assert.Equal(1, heard);
        buffer.AppendResampled(Enumerable.Repeat(0.2f, CaptureBuffer.BlockSamples * 5).ToArray());
        Assert.Equal(1, heard);
    }

    [Fact]
    public void The_proposed_strings_are_the_ones_listed_for_approval()
    {
        Assert.Equal("No sound from mic", PillText.For(PillView.NoSound, TimeSpan.Zero));
        Assert.Equal("No sound from mic", PillText.For(PillView.NoSoundEnded, TimeSpan.Zero));
        Assert.Equal("Dictum is not hearing anything. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.",
            NoSoundText.Balloon(NoSoundCause.Silent));
        Assert.Equal("No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.",
            NoSoundText.Balloon(NoSoundCause.NoMicrophone));
        Assert.Equal("Last dictation heard nothing: check the microphone", NoSoundText.HeardNothingLine);

        Assert.True(PillText.IsRecording(PillView.NoSound)); // still recording: the dot, meter, orb and edge glow stay
        Assert.Null(PillText.FlashMs(PillView.NoSound)); // until a sound or the end
        Assert.Equal(PillText.FlashMs(PillView.MicError), PillText.FlashMs(PillView.NoSoundEnded));

        Assert.Null(AppShell.WithHeardNothing(null, heardNothing: false));
        Assert.Equal(NoSoundText.HeardNothingLine, AppShell.WithHeardNothing(null, heardNothing: true));
        Assert.Equal($"Speech model: out of memory.\n{NoSoundText.HeardNothingLine}", AppShell.WithHeardNothing("Speech model: out of memory.", heardNothing: true));
    }

    /// <summary>A microphone that reports its first sound when told (as WasapiCapture's buffer does), or cannot be opened.</summary>
    private sealed class QuietCapture : IAudioCapture
    {
        public bool ReportsSound => true;
        public float CurrentLevel => 0f;
        public Exception? OpenFails { get; init; }

        public event Action<Exception>? Failed { add { } remove { } }

        public event Action? SoundHeard;

        public void Start(string? deviceSetting, long keyDownStamp)
        {
            if (OpenFails is { } failure) throw failure;
        }

        public CapturedAudio Stop() => new(new float[32000], 0.2f);

        public void Hear() => SoundHeard?.Invoke();
    }

    private sealed record Rig(DictationController Controller, QuietCapture Capture, FakeUi Ui, FakeTranscriber Whisper, ConcurrentQueue<string> Inserted) : IDisposable
    {
        public void Dispose() => Controller.Dispose();

        public void Down() => Controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));

        public void Up() => Controller.Post(new HookEvent(HotkeySignal.Up, Environment.TickCount64));
    }

    private static Rig Make(QuietCapture? capture = null, bool microphoneListed = true)
    {
        var settings = new AppSettings();
        var whisper = new FakeTranscriber("hello there");
        var inserted = new ConcurrentQueue<string>();
        var cleaner = TranscriptCleaner.RulesOnly("test");
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(whisper), () => cleaner,
            (text, _) => { inserted.Enqueue(text); return Task.CompletedTask; }, () => settings, NullLog.Instance);
        capture ??= new QuietCapture();
        var ui = new FakeUi();
        var controller = new DictationController(new DictationStateMachine(StateMachineOptions.Default), capture, pipeline, ui, () => settings, NullLog.Instance,
            microphoneListed: () => microphoneListed);
        return new Rig(controller, capture, ui, whisper, inserted);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 6000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
        await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task The_controller_warns_once_for_a_silent_microphone_types_nothing_and_clears_the_line_after_a_dictation_that_types()
    {
        using var rig = Make();
        var sw = Stopwatch.StartNew();
        rig.Down();
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.NoSound), "the warning");
        Assert.True(sw.ElapsedMilliseconds >= Grace - 100, $"warned after {sw.ElapsedMilliseconds} ms");
        Assert.Equal([NoSoundCause.Silent], rig.Ui.NoSoundWarnings.ToArray());
        await Task.Delay(300, Ct);
        Assert.Equal(PillView.NoSound, rig.Ui.Pills.Last()); // it stays while recording goes on

        rig.Up();
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.NoSoundEnded), "the end of the session");
        await Task.Delay(200, Ct);
        Assert.Empty(rig.Inserted);
        Assert.Equal(0, rig.Whisper.Calls);
        Assert.DoesNotContain(PillView.Transcribing, rig.Ui.Pills);
        Assert.Equal([NoSoundCause.Silent], rig.Ui.NoSoundWarnings.ToArray()); // one balloon for the session
        Assert.True(rig.Ui.HeardNothing);
        Assert.True(rig.Controller.IsIdle);

        // The next dictation hears its microphone and types: the tray panel's line goes.
        rig.Down();
        rig.Capture.Hear();
        await Task.Delay(400, Ct);
        rig.Up();
        await WaitUntil(() => !rig.Inserted.IsEmpty, "the paste");
        await WaitUntil(() => rig.Ui.HeardNothing == false, "the line cleared");
        Assert.Equal(["Hello there."], rig.Inserted.ToArray());
    }

    [Fact]
    public async Task The_controller_goes_back_to_recording_when_sound_arrives()
    {
        using var rig = Make();
        rig.Down();
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.NoSound), "the warning");
        rig.Capture.Hear();
        await WaitUntil(() => rig.Ui.Pills.Last() == PillView.RecordingHold, "the recording label again");
        rig.Up();
        await WaitUntil(() => !rig.Inserted.IsEmpty, "the paste");
        Assert.Equal(["Hello there."], rig.Inserted.ToArray());
        Assert.Single(rig.Ui.NoSoundWarnings);
        Assert.NotEqual(true, rig.Ui.HeardNothing);
    }

    [Fact]
    public async Task The_controller_warns_at_once_when_no_microphone_is_listed()
    {
        using var none = Make(new QuietCapture { OpenFails = new InvalidOperationException("No microphone found.") }, microphoneListed: false);
        none.Down();
        await WaitUntil(() => !none.Ui.NoSoundWarnings.IsEmpty, "the no-microphone balloon", timeoutMs: 1500);
        await Task.Delay(200, Ct);
        Assert.Equal([NoSoundCause.NoMicrophone], none.Ui.NoSoundWarnings.ToArray());
        Assert.Equal(PillView.NoSoundEnded, none.Ui.Pills.Last()); // not left on "Recording · hold"
        Assert.DoesNotContain(PillView.MicError, none.Ui.Pills);
        Assert.True(none.Ui.HeardNothing);
        Assert.True(none.Controller.IsIdle);
    }

    [Fact]
    public async Task The_controller_keeps_mic_error_when_a_listed_microphone_cannot_be_opened_and_the_flash_is_not_replaced_by_recording()
    {
        using var busy = Make(new QuietCapture { OpenFails = new InvalidOperationException("The device is in use.") }, microphoneListed: true);
        busy.Down();
        await WaitUntil(() => busy.Ui.Pills.Contains(PillView.MicError), "Mic error", timeoutMs: 1500);
        await Task.Delay(200, Ct);
        Assert.Equal(PillView.MicError, busy.Ui.Pills.Last()); // the flash is not replaced by "Recording · hold"
        Assert.Empty(busy.Ui.NoSoundWarnings);
        Assert.Null(busy.Ui.HeardNothing);
        Assert.True(busy.Controller.IsIdle);
    }
}
