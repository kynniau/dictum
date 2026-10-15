namespace Dictum.Core;

public enum DictationState { Idle, RecordingHold, PendingTap, RecordingHandsFree, Processing }

/// <summary>What the pill should show. Cancelled, Busy and MicError are short flashes.</summary>
public enum PillView { Hidden, RecordingHold, RecordingHandsFree, Transcribing, Polishing, Done, Cancelled, Busy, MicError, PolishingSkipped }

public enum ProcessingOutcome { Inserted, Discarded, Failed }

public abstract record Effect;

/// <summary>Open the microphone and start buffering.</summary>
public sealed record StartCaptureEffect(long SessionId) : Effect;

/// <summary>Stop the microphone and throw the audio away.</summary>
public sealed record DiscardCaptureEffect(long SessionId) : Effect;

/// <summary>Stop the microphone and run transcribe → clean up → insert under <paramref name="Token"/>.</summary>
public sealed record BeginProcessingEffect(long SessionId, CancellationToken Token, bool AutoStopped) : Effect;

/// <summary>The processing token for this session has been cancelled; nothing may be inserted.</summary>
public sealed record CancelProcessingEffect(long SessionId) : Effect;

public sealed record PillEffect(PillView View) : Effect;

public sealed record StateMachineOptions(int TapMs, int DoubleTapMs, int HandsFreeMaxSeconds)
{
    public static readonly StateMachineOptions Default = new(300, 400, 300);
}

/// <summary>
/// The hold / double-tap / hands-free state machine from SPEC §3. Pure: no threads, no timers, no I/O.
/// Time is injected as a monotonic millisecond clock value on every call; the owner calls
/// <see cref="Tick"/> periodically so deadlines (double-tap window, auto-stop) fire.
/// Not thread-safe: the owner confines it to one thread.
/// </summary>
public sealed class DictationStateMachine
{
    private static readonly IReadOnlyList<Effect> None = [];

    private StateMachineOptions _options;
    private bool _keyDown;            // physical state of the hotkey as seen through events
    private bool _ignoreNextUp;       // the consumed second tap's key-up
    private long _keyDownAt;
    private long _captureStartedAt;
    private long _pendingSince;
    private CancellationTokenSource? _processingCts;

    public DictationStateMachine(StateMachineOptions options, bool enabled = true)
    {
        _options = options;
        Enabled = enabled;
    }

    public DictationState State { get; private set; } = DictationState.Idle;
    public bool Enabled { get; private set; }
    public long SessionId { get; private set; }

    public void UpdateOptions(StateMachineOptions options) => _options = options;

    /// <summary>Milliseconds since capture started for the current recording (0 when not recording).</summary>
    public long RecordingElapsedMs(long nowMs) => IsRecording ? Math.Max(0, nowMs - _captureStartedAt) : 0;

    private bool IsRecording => State is DictationState.RecordingHold or DictationState.PendingTap or DictationState.RecordingHandsFree;

    public IReadOnlyList<Effect> HotkeyDown(long nowMs)
    {
        if (!Enabled) return None;

        // A PendingTap whose window already lapsed (no Tick in between) expires first.
        var expired = ExpirePendingTap(nowMs);

        if (_keyDown) return expired; // key auto-repeat: the key never went up
        _keyDown = true;
        _keyDownAt = nowMs;

        switch (State)
        {
            case DictationState.Idle:
                SessionId++;
                _captureStartedAt = nowMs;
                _ignoreNextUp = false;
                State = DictationState.RecordingHold;
                return Concat(expired, new StartCaptureEffect(SessionId), new PillEffect(PillView.RecordingHold));

            case DictationState.PendingTap:
                // Second tap inside the window: hands-free. Capture keeps running, nothing is lost.
                _ignoreNextUp = true;
                State = DictationState.RecordingHandsFree;
                return [new PillEffect(PillView.RecordingHandsFree)];

            case DictationState.RecordingHandsFree:
                return BeginProcessing(autoStopped: false);

            case DictationState.Processing:
                return [new PillEffect(PillView.Busy)];

            default:
                return None;
        }
    }

    public IReadOnlyList<Effect> HotkeyUp(long nowMs)
    {
        if (!Enabled) return None;
        var wasDown = _keyDown;
        _keyDown = false;
        if (!wasDown) return None;

        switch (State)
        {
            case DictationState.RecordingHold:
                if (nowMs - _keyDownAt >= _options.TapMs) return BeginProcessing(autoStopped: false);
                State = DictationState.PendingTap;
                _pendingSince = nowMs;
                return None;

            case DictationState.RecordingHandsFree when _ignoreNextUp:
                _ignoreNextUp = false;
                return None;

            default:
                return None;
        }
    }

    public IReadOnlyList<Effect> Escape(long nowMs)
    {
        if (!Enabled) return None;
        switch (State)
        {
            case DictationState.RecordingHold:
            case DictationState.PendingTap:
            case DictationState.RecordingHandsFree:
                State = DictationState.Idle;
                _ignoreNextUp = false;
                return [new DiscardCaptureEffect(SessionId), new PillEffect(PillView.Cancelled)];

            case DictationState.Processing:
                CancelProcessingToken();
                State = DictationState.Idle;
                return [new CancelProcessingEffect(SessionId), new PillEffect(PillView.Cancelled)];

            default:
                return None; // Esc does nothing when Idle
        }
    }

    public IReadOnlyList<Effect> Tick(long nowMs)
    {
        if (!Enabled) return None;
        var expired = ExpirePendingTap(nowMs);
        if (expired.Count > 0) return expired;

        if ((State is DictationState.RecordingHold or DictationState.RecordingHandsFree)
            && nowMs - _captureStartedAt >= _options.HandsFreeMaxSeconds * 1000L)
        {
            // Auto-stop: a stuck key or a forgotten hands-free session is transcribed, never discarded. The key is
            // forgotten too: if its key-up was lost (secure desktop), the next press must start a recording.
            _keyDown = false;
            return BeginProcessing(autoStopped: true);
        }
        return None;
    }

    /// <summary>The pipeline for <paramref name="sessionId"/> finished. Stale sessions are ignored.</summary>
    public IReadOnlyList<Effect> ProcessingFinished(long sessionId, ProcessingOutcome outcome)
    {
        if (State != DictationState.Processing || sessionId != SessionId) return None;
        ReleaseProcessingToken();
        State = DictationState.Idle;
        return [new PillEffect(outcome == ProcessingOutcome.Inserted ? PillView.Done : PillView.Hidden)];
    }

    /// <summary>
    /// The device failed mid-recording (unplugged, a headset battery died). What was captured is transcribed, as the
    /// auto-stop does (never discarded), with the "Mic error" flash; a lone tap still waiting for its second tap is
    /// discarded as before, since it is not a dictation yet.
    /// </summary>
    public IReadOnlyList<Effect> CaptureLost(long sessionId)
    {
        if (sessionId != SessionId || State is not (DictationState.RecordingHold or DictationState.RecordingHandsFree)) return CaptureFailed(sessionId);
        return [.. BeginProcessing(autoStopped: false).Where(e => e is not PillEffect), new PillEffect(PillView.MicError)];
    }

    /// <summary>The microphone could not be opened or failed mid-recording.</summary>
    public IReadOnlyList<Effect> CaptureFailed(long sessionId)
    {
        if (!IsRecording || sessionId != SessionId) return None;
        State = DictationState.Idle;
        return [new DiscardCaptureEffect(SessionId), new PillEffect(PillView.MicError)];
    }

    public IReadOnlyList<Effect> SetEnabled(bool enabled)
    {
        if (enabled == Enabled) return None;
        IReadOnlyList<Effect> effects = None;
        if (!enabled)
        {
            if (IsRecording) effects = [new DiscardCaptureEffect(SessionId), new PillEffect(PillView.Hidden)];
            else if (State == DictationState.Processing)
            {
                CancelProcessingToken();
                effects = [new CancelProcessingEffect(SessionId), new PillEffect(PillView.Hidden)];
            }
            State = DictationState.Idle;
        }
        Enabled = enabled;
        _keyDown = false;
        _ignoreNextUp = false;
        return effects;
    }

    private IReadOnlyList<Effect> ExpirePendingTap(long nowMs)
    {
        if (State != DictationState.PendingTap || nowMs - _pendingSince <= _options.DoubleTapMs) return None;
        State = DictationState.Idle;
        return [new DiscardCaptureEffect(SessionId), new PillEffect(PillView.Hidden)];
    }

    private IReadOnlyList<Effect> BeginProcessing(bool autoStopped)
    {
        ReleaseProcessingToken();
        _processingCts = new CancellationTokenSource();
        State = DictationState.Processing;
        return [new BeginProcessingEffect(SessionId, _processingCts.Token, autoStopped), new PillEffect(PillView.Transcribing)];
    }

    private void CancelProcessingToken()
    {
        _processingCts?.Cancel();
        _processingCts = null;
    }

    // A CancellationTokenSource without a timer holds no unmanaged resources, and the pipeline may still
    // be observing the token, so sources are released, not disposed.
    private void ReleaseProcessingToken() => _processingCts = null;

    private static IReadOnlyList<Effect> Concat(IReadOnlyList<Effect> first, params Effect[] rest)
        => first.Count == 0 ? rest : [.. first, .. rest];
}
