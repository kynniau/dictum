using System.Threading.Channels;
using Dictum.Audio;
using Dictum.Hotkey;
using Dictum.Settings;

namespace Dictum.Core;

public enum TrayState { Idle, Disabled, Recording, Processing, Error }

/// <summary>What the pill (and the orb and edge indicators) should show.</summary>
/// <param name="CaptureStartTickMs">Environment.TickCount64 when capture began (for the hands-free timer).</param>
/// <param name="Words">Words pasted, for "Pasted · 38 words" (0 when not known).</param>
public readonly record struct PillState(PillView View, long CaptureStartTickMs, int Words = 0);

/// <summary>What the controller needs from the UI. Implementations must not block the caller.</summary>
public interface IDictationUi
{
    void ShowPill(PillState state);

    void SetTrayState(TrayState state);

    /// <summary>The microphone gave no sound (<paramref name="cause"/>): a balloon says why (NoSoundText). At most once per session. Default: nothing.</summary>
    void WarnNoSound(NoSoundCause cause)
    {
    }

    /// <summary>
    /// True: the last dictation heard nothing, and the tray panel says so (NoSoundText.HeardNothingLine); false: a dictation has
    /// typed text since. Default: nothing.
    /// </summary>
    void SetHeardNothing(bool heardNothing)
    {
    }
}

/// <summary>
/// Owns the state machine and runs it on a single consumer task fed by a channel: hook events, a 50 ms
/// tick, pipeline completions and tray commands all arrive as messages, so the machine is never touched
/// concurrently and the hook thread never waits for anything. In hands-free (SPEC §3 amendment, 16/10/2026) the capture cuts
/// the recording at pauses and each part goes through the pipeline while recording continues: one at a time, in order,
/// the next one queued (<see cref="HandsFreeSession"/>).
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly DictationStateMachine _machine;
    private readonly IAudioCapture _capture;
    private readonly DictationPipeline _pipeline;
    private readonly IDictationUi _ui;
    private readonly Func<AppSettings> _settings;
    private readonly ILog _log;
    private readonly Func<bool> _hotkeyBlocked;
    private readonly Func<bool> _microphoneListed; // the system lists a microphone (asked only after one could not be opened)
    private readonly Timer _ticker;
    private readonly Task _loop;
    private long _captureStartTick;
    private long _lastKeyDownStamp; // Stopwatch timestamp of the key-down that started the recording
    private int _lastWords; // words of the dictation that just finished, for the "Pasted" pill
    private HandsFreeSession? _handsFree; // the session whose parts are being typed while it records (loop thread only)

    /// <summary>
    /// One hands-free session's parts, queued in the order the capture cut them (the capture thread writes them under its
    /// buffer's lock, so every part cut before the stop is queued before the rest the stop returns). One worker types them.
    /// </summary>
    private sealed class HandsFreeSession(long sessionId, CancellationToken token)
    {
        public long SessionId { get; } = sessionId;
        public CancellationToken Token { get; } = token;
        public Channel<(CapturedAudio Audio, CutReason Reason)> Parts { get; } =
            Channel.CreateUnbounded<(CapturedAudio, CutReason)>(new UnboundedChannelOptions { SingleReader = true });
    }

    private sealed record HookInput(HookEvent Event);
    private sealed record TickInput(long NowMs);
    private sealed record DoneInput(long SessionId, ProcessingOutcome Outcome, int Words);
    private sealed record CaptureLostInput(long SessionId);
    private sealed record SoundInput(long SessionId);
    private sealed record EnableInput(bool Enabled);
    private sealed record OptionsInput(StateMachineOptions Options);
    private sealed record PolishingInput(long SessionId, PillView View);

    public DictationController(
        DictationStateMachine machine,
        IAudioCapture capture,
        DictationPipeline pipeline,
        IDictationUi ui,
        Func<AppSettings> settings,
        ILog log,
        Func<bool>? hotkeyBlocked = null,
        Func<bool>? microphoneListed = null)
    {
        _machine = machine;
        _capture = capture;
        _pipeline = pipeline;
        _ui = ui;
        _settings = settings;
        _log = log;
        _hotkeyBlocked = hotkeyBlocked ?? (() => false);
        _microphoneListed = microphoneListed ?? (() => true);
        _capture.Failed += _ => _inbox.Writer.TryWrite(new CaptureLostInput(_machine.SessionId));
        _capture.SoundHeard += () => _inbox.Writer.TryWrite(new SoundInput(_machine.SessionId));
        _loop = Task.Run(RunAsync);
        _ticker = new Timer(_ => _inbox.Writer.TryWrite(new TickInput(Environment.TickCount64)), null, 50, 50);
    }

    /// <summary>Called from the hook thread. Never blocks.</summary>
    public void Post(HookEvent e) => _inbox.Writer.TryWrite(new HookInput(e));

    /// <summary>
    /// No dictation is recording or processing. Read from other threads as a gate (a window that takes focus must not
    /// open mid-dictation, or the paste would land in it); a stale read only delays or allows one opening. The mouse-hotkey
    /// listener reads it too (AppShell.ApplyHookEnabled), again on each tray state, and the end of processing is reported
    /// only after the machine is idle (Finished).
    /// </summary>
    public bool IsIdle => _machine.State == DictationState.Idle;

    public void SetEnabled(bool enabled) => _inbox.Writer.TryWrite(new EnableInput(enabled));

    public void UpdateOptions(StateMachineOptions options) => _inbox.Writer.TryWrite(new OptionsInput(options));

    /// <summary>
    /// The pipeline reached LLM cleanup for <paramref name="sessionId"/>. Routed through the inbox so the on-screen gate
    /// (pill.enabled or a big indicator) and the state check apply, exactly like every other pill change.
    /// </summary>
    internal void ReportPolishing(long sessionId, PillView view = PillView.Polishing) => _inbox.Writer.TryWrite(new PolishingInput(sessionId, view));

    private async Task RunAsync()
    {
        await foreach (var input in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Execute(Dispatch(input));
            }
            catch (Exception ex)
            {
                _log.Error($"Controller error handling {input.GetType().Name}: {ex}");
            }
        }
    }

    private IReadOnlyList<Effect> Dispatch(object input) => input switch
    {
        HookInput { Event.Signal: HotkeySignal.Down } h => KeyDown(h.Event.TimestampMs, h.Event.Stamp),
        HookInput { Event.Signal: HotkeySignal.Up } h => _machine.HotkeyUp(h.Event.TimestampMs),
        HookInput { Event.Signal: HotkeySignal.Escape } h => _machine.Escape(h.Event.TimestampMs),
        TickInput t => _machine.Tick(t.NowMs),
        DoneInput d => Finished(d),
        CaptureLostInput c => CaptureLost(c.SessionId),
        SoundInput s => _machine.SoundHeard(s.SessionId),
        EnableInput e => SetEnabledCore(e.Enabled),
        OptionsInput o => UpdateOptionsCore(o.Options),
        PolishingInput p => _machine.State == DictationState.Processing && p.SessionId == _machine.SessionId
            ? [new PillEffect(p.View)]
            : [], // a late report for a cancelled or finished session must not replace "Cancelled"/"Done"
        _ => [],
    };

    private IReadOnlyList<Effect> KeyDown(long tickMs, long stamp)
    {
        if (_machine.State == DictationState.Idle && _machine.Enabled && _hotkeyBlocked())
        {
            // No model ready (loading or missing): the press is ignored, its key-up then finds the machine idle.
            _log.Info("Hotkey ignored: the speech model is not loaded yet.");
            return [new PillEffect(PillView.Busy)];
        }
        _lastKeyDownStamp = stamp != 0 ? stamp : System.Diagnostics.Stopwatch.GetTimestamp();
        return _machine.HotkeyDown(tickMs);
    }

    private IReadOnlyList<Effect> Finished(DoneInput done)
    {
        _lastWords = done.Words;
        var effects = _machine.ProcessingFinished(done.SessionId, done.Outcome);
        // The UI hears the end only after the machine's own transition (for every finished pipeline, as before). Told from
        // the pipeline task, before this message was even queued, a UI that reads IsIdle on that news (the mouse-hotkey gate,
        // AppShell.ApplyHookEnabled) could still see the dictation under way and keep a mouse button swallowed after it.
        _ui.SetTrayState(done.Outcome == ProcessingOutcome.Failed ? TrayState.Error : TrayState.Idle);
        if (done.Outcome == ProcessingOutcome.Inserted) _ui.SetHeardNothing(false); // a dictation typed text: the microphone works
        return effects;
    }

    private IReadOnlyList<Effect> CaptureLost(long sessionId)
    {
        var effects = _machine.CaptureLost(sessionId);
        if (effects.OfType<BeginProcessingEffect>().Any()) _log.Warn("Microphone lost mid-recording; transcribing what was captured.");
        return effects;
    }

    private IReadOnlyList<Effect> SetEnabledCore(bool enabled)
    {
        var effects = _machine.SetEnabled(enabled);
        _ui.SetTrayState(enabled ? TrayState.Idle : TrayState.Disabled);
        return effects;
    }

    private IReadOnlyList<Effect> UpdateOptionsCore(StateMachineOptions options)
    {
        _machine.UpdateOptions(options);
        return [];
    }

    private void Execute(IReadOnlyList<Effect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case StartCaptureEffect start:
                    // A microphone that could not be opened has already ended the session ("Mic error" or the no-microphone
                    // warning): the rest of this batch (the "Recording · hold" pill) belonged to the recording that never started,
                    // and shown after the flash it stayed on screen with nothing recording (found 21/10/2026).
                    if (!StartCapture(start.SessionId)) return;
                    break;
                case BeginSegmentsEffect segments:
                    BeginSegments(segments);
                    break;
                case DiscardCaptureEffect discard:
                    _capture.Stop();
                    var handsFree = EndParts(discard.SessionId); // nothing after the last cut is typed
                    _ui.SetTrayState(_machine.Enabled ? TrayState.Idle : TrayState.Disabled);
                    _log.Info(handsFree ? "Hands-free recording stopped; the audio after the last pause is discarded." : "Recording discarded.");
                    break;
                case BeginProcessingEffect begin:
                    BeginProcessing(begin);
                    break;
                case CancelProcessingEffect cancel:
                    EndParts(cancel.SessionId);
                    _log.Info(cancel.KeepsTyped
                        ? $"Session {cancel.SessionId} stopped: the text already typed stays; parts not yet typed are dropped."
                        : $"Session {cancel.SessionId} cancelled during processing; nothing will be inserted.");
                    _ui.SetTrayState(_machine.Enabled ? TrayState.Idle : TrayState.Disabled);
                    break;
                case NoSoundEffect noSound:
                    _log.Warn(noSound.Cause == NoSoundCause.NoMicrophone
                        ? "No microphone is listed; the recording did not start."
                        : $"No sound from the microphone {DictationStateMachine.NoSoundGraceMs} ms into the recording (every 20 ms block below {CaptureBuffer.SoundFloor}); recording goes on.");
                    _ui.WarnNoSound(noSound.Cause);
                    break;
                case HeardNothingEffect:
                    _log.Info("The recording ended with no sound from the microphone; nothing was transcribed.");
                    _ui.SetHeardNothing(true);
                    break;
                case PillEffect pill:
                    if (_settings().ShowsDictationState || pill.View == PillView.Hidden)
                        _ui.ShowPill(new PillState(pill.View, _captureStartTick, pill.View == PillView.Done ? _lastWords : 0));
                    break;
            }
        }
    }

    /// <summary>Opens the microphone; false when it could not be opened (the session has ended, its effects executed).</summary>
    private bool StartCapture(long sessionId)
    {
        try
        {
            _captureStartTick = Environment.TickCount64;
            _capture.Start(_settings().Audio.Device, _lastKeyDownStamp);
            if (_capture.ReportsSound) _machine.WatchForSound(sessionId); // its first sound arrives through the inbox, after this
            _ui.SetTrayState(TrayState.Recording);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Microphone could not be opened: {ex.GetType().Name}: {ex.Message}");
            _ui.SetTrayState(TrayState.Error);
            // No microphone listed at all: the no-microphone warning instead of "Mic error". Asked only now, so a press that opens
            // the microphone pays nothing for the check.
            Execute(MicrophoneListed() ? _machine.CaptureFailed(sessionId) : _machine.NoMicrophone(sessionId));
            return false;
        }
    }

    private bool MicrophoneListed()
    {
        try
        {
            return _microphoneListed();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not list the microphones: {ex.GetType().Name}: {ex.Message}");
            return true; // unknown: the existing "Mic error"
        }
    }

    /// <summary>
    /// Hands-free began: the capture cuts at pauses from now on and one worker types each part, in order, while recording
    /// goes on. A capture that cannot cut (IAudioCapture's default) hands everything over at the stop, as one part.
    /// </summary>
    private void BeginSegments(BeginSegmentsEffect begin)
    {
        if (_handsFree is { } stale) stale.Parts.Writer.TryComplete();
        var session = new HandsFreeSession(begin.SessionId, begin.Token);
        _handsFree = session;
        _ = Task.Run(() => TypePartsAsync(session));
        var parts = session.Parts.Writer;
        _capture.SegmentAtPauses(_settings().SilenceThreshold, (audio, reason) => parts.TryWrite((audio, reason)));
        _log.Info("Hands-free: each part is typed when you pause.");
    }

    /// <summary>No more parts for this session (stop, Esc, disabled, the microphone gone): its worker types what is queued, or drops it once the token is cancelled.</summary>
    private bool EndParts(long sessionId)
    {
        if (_handsFree is not { } session || session.SessionId != sessionId) return false;
        session.Parts.Writer.TryComplete();
        _handsFree = null;
        return true;
    }

    /// <summary>
    /// Types one hands-free session's parts one at a time, in the order they were cut; a slow part delays the next and never
    /// reorders it. Each part gets the last part that typed something: its typed text for the boundary rule, the closing words
    /// of what it said for the context. After Esc (the token) the parts still queued are dropped and nothing more is reported:
    /// the machine is already idle. Logs counts and timings only.
    /// </summary>
    // simplification: each part is typed into whatever window is in front when it is ready (TextInserter, SPEC §6), as a whole
    // recording always was; but a session runs for minutes while the speaker may switch windows. Ceiling: after a window change
    // mid-session the next parts are typed into the other app, and with insert.method = type a spoken "new line" there is the
    // Enter key, which sends the message in some chat apps. The README tells the user to stay in the window until the session
    // stops. Trigger to retire: the product owner's decision on holding parts while another window is in front, due 17/10/2026.
    private async Task TypePartsAsync(HandsFreeSession session)
    {
        var (number, typed, dropped, failed, words) = (0, 0, 0, 0, 0);
        HandsFreePart? last = null;
        await foreach (var (audio, reason) in session.Parts.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            number++;
            if (session.Token.IsCancellationRequested)
            {
                dropped++;
                continue;
            }
            _log.Info($"Segment {number}: {audio.Duration.TotalSeconds:0.0} s, {Describe(reason)}.");
            var part = new HandsFreePart(number, last);
            try
            {
                var outcome = await _pipeline.RunAsync(audio, session.Token, view =>
                {
                    if (view is PillView.Polishing or PillView.PolishingSkipped) ReportPolishing(session.SessionId, view);
                }, inserted => words += inserted, part).ConfigureAwait(false);
                if (outcome == ProcessingOutcome.Inserted) typed++;
                if (part.Typed is not null) last = part;
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
            {
                dropped++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.Error($"Segment {number} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        _log.Info($"Hands-free session {session.SessionId}: {number} part(s), {typed} typed, {dropped} dropped, {failed} failed.");
        if (session.Token.IsCancellationRequested) return; // Esc, disabled or quit: the machine is idle and has said so
        var result = failed > 0 ? ProcessingOutcome.Failed : typed > 0 ? ProcessingOutcome.Inserted : ProcessingOutcome.Discarded;
        _inbox.Writer.TryWrite(new DoneInput(session.SessionId, result, words)); // Finished sets the tray state
    }

    private static string Describe(CutReason reason) => reason switch
    {
        CutReason.Pause => "cut on quiet",
        CutReason.Limit => $"cut at {SpeechSegmenter.LimitBlocks * SpeechSegmenter.BlockMs / 1000} s on the quietest block",
        _ => "the rest at the stop",
    };

    private void BeginProcessing(BeginProcessingEffect begin)
    {
        var audio = _capture.Stop();
        _ui.SetTrayState(TrayState.Processing);
        var session = _handsFree is { } current && current.SessionId == begin.SessionId ? current : null;
        if (begin.AutoStopped)
        {
            _log.Info(session is not null
                ? $"Hands-free recording auto-stopped at hands_free_max_seconds ({_machine.Options.HandsFreeMaxSeconds} s); typing what followed the last cut."
                : $"Held key auto-stopped at the hold limit ({_machine.Options.HoldMaxSeconds} s, not a setting); transcribing what was captured.");
        }
        if (session is not null)
        {
            // Every part cut before the stop is already queued (the capture hands parts over before Stop returns): the rest
            // goes last, and the worker reports the session done once all are typed.
            session.Parts.Writer.TryWrite((audio, CutReason.Stop));
            EndParts(begin.SessionId);
            return;
        }
        _ = Task.Run(async () =>
        {
            var outcome = ProcessingOutcome.Failed;
            var words = 0;
            try
            {
                outcome = await _pipeline.RunAsync(audio, begin.Token, view =>
                {
                    if (view is PillView.Polishing or PillView.PolishingSkipped) ReportPolishing(begin.SessionId, view);
                }, inserted => words = inserted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (begin.Token.IsCancellationRequested)
            {
                _log.Info($"Session {begin.SessionId} processing stopped after cancellation.");
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"Processing failed: {ex.GetType().Name}: {ex.Message}");
            }
            _inbox.Writer.TryWrite(new DoneInput(begin.SessionId, outcome, words)); // Finished sets the tray state
        });
    }

    /// <summary>
    /// Quit: a recording is discarded and a dictation being processed is cancelled (the same as unticking Enabled), so the
    /// engines AppHost disposes next are not held by a transcription or cleanup nobody will paste.
    /// </summary>
    public void Dispose()
    {
        _ticker.Dispose();
        _inbox.Writer.TryWrite(new EnableInput(false));
        _inbox.Writer.TryComplete();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // loop faults are already logged
        }
        _capture.Stop();
    }
}
