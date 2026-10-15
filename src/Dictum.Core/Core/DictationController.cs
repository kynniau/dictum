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
}

/// <summary>
/// Owns the state machine and runs it on a single consumer task fed by a channel: hook events, a 50 ms
/// tick, pipeline completions and tray commands all arrive as messages, so the machine is never touched
/// concurrently and the hook thread never waits for anything.
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
    private readonly Timer _ticker;
    private readonly Task _loop;
    private long _captureStartTick;
    private long _lastKeyDownStamp; // Stopwatch timestamp of the key-down that started the recording
    private int _lastWords; // words of the dictation that just finished, for the "Pasted" pill

    private sealed record HookInput(HookEvent Event);
    private sealed record TickInput(long NowMs);
    private sealed record DoneInput(long SessionId, ProcessingOutcome Outcome, int Words);
    private sealed record CaptureLostInput(long SessionId);
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
        Func<bool>? hotkeyBlocked = null)
    {
        _machine = machine;
        _capture = capture;
        _pipeline = pipeline;
        _ui = ui;
        _settings = settings;
        _log = log;
        _hotkeyBlocked = hotkeyBlocked ?? (() => false);
        _capture.Failed += _ => _inbox.Writer.TryWrite(new CaptureLostInput(_machine.SessionId));
        _loop = Task.Run(RunAsync);
        _ticker = new Timer(_ => _inbox.Writer.TryWrite(new TickInput(Environment.TickCount64)), null, 50, 50);
    }

    /// <summary>Called from the hook thread. Never blocks.</summary>
    public void Post(HookEvent e) => _inbox.Writer.TryWrite(new HookInput(e));

    /// <summary>
    /// No dictation is recording or processing. Read from other threads as a gate (a window that takes focus must not
    /// open mid-dictation, or the paste would land in it); a stale read only delays or allows one opening.
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
        return _machine.ProcessingFinished(done.SessionId, done.Outcome);
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
                    StartCapture(start.SessionId);
                    break;
                case DiscardCaptureEffect:
                    _capture.Stop();
                    _ui.SetTrayState(_machine.Enabled ? TrayState.Idle : TrayState.Disabled);
                    _log.Info("Recording discarded.");
                    break;
                case BeginProcessingEffect begin:
                    BeginProcessing(begin);
                    break;
                case CancelProcessingEffect cancel:
                    _log.Info($"Session {cancel.SessionId} cancelled during processing; nothing will be inserted.");
                    _ui.SetTrayState(_machine.Enabled ? TrayState.Idle : TrayState.Disabled);
                    break;
                case PillEffect pill:
                    if (_settings().ShowsDictationState || pill.View == PillView.Hidden)
                        _ui.ShowPill(new PillState(pill.View, _captureStartTick, pill.View == PillView.Done ? _lastWords : 0));
                    break;
            }
        }
    }

    private void StartCapture(long sessionId)
    {
        try
        {
            _captureStartTick = Environment.TickCount64;
            _capture.Start(_settings().Audio.Device, _lastKeyDownStamp);
            _ui.SetTrayState(TrayState.Recording);
        }
        catch (Exception ex)
        {
            _log.Error($"Microphone could not be opened: {ex.GetType().Name}: {ex.Message}");
            _ui.SetTrayState(TrayState.Error);
            Execute(_machine.CaptureFailed(sessionId));
        }
    }

    private void BeginProcessing(BeginProcessingEffect begin)
    {
        var audio = _capture.Stop();
        _ui.SetTrayState(TrayState.Processing);
        if (begin.AutoStopped)
        {
            _log.Info($"Recording auto-stopped at hands_free_max_seconds ({_settings().HandsFreeMaxSeconds} s); transcribing what was captured.");
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
            _ui.SetTrayState(outcome == ProcessingOutcome.Failed ? TrayState.Error : TrayState.Idle);
            _inbox.Writer.TryWrite(new DoneInput(begin.SessionId, outcome, words));
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
