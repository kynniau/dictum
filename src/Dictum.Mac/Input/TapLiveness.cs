namespace Dictum.Mac.Input;

/// <summary>
/// Whether an installed tap is still being fed (PR #82 round 2, 29/10/2026). After the grant was taken away under a running tap,
/// macOS went on routing every key through it without calling it, and no app received keys; its first "disabled by timeout"
/// notice came about 50 s later, and the Accessibility check still answered trusted, so neither could catch it (owner's Mac,
/// 21:34-21:35 AEDT). This compares two counts instead: the events of the tap's own types the HID system has seen
/// (CGEventSourceCounterForEventType), and the events the tap's callback has seen. When the HID count moves on by
/// <see cref="Behind"/> while the tap's stays put for at least <see cref="Gap"/>, the tap is starved and is removed.
/// <para>
/// Paused while Secure Event Input is on (a password field, Terminal's Secure Keyboard Entry), when macOS keeps keys from taps
/// on purpose, and for one <see cref="Gap"/> after it turns off; paused the same way after the timer itself stalled (sleep).
/// Thread-safe: <see cref="Check"/> runs on the timer, <see cref="Probe"/> on the tap thread.
/// </para>
/// </summary>
internal sealed class TapLiveness
{
    /// <summary>
    /// simplification: a fixed 500 ms poll. Ceiling: a starved tap holds keys for up to one tick past <see cref="Behind"/> events.
    /// Trigger to retire: a stall over 1.5 s seen in the reset-while-running test, or a notification from macOS worth the native code.
    /// </summary>
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// simplification: the tap may lag the HID system this long before it counts as starved. Ceiling: a tap that is slow for
    /// longer than a second while keys arrive is removed too (the shell shows Request access, which installs a fresh one).
    /// Trigger to retire: a removal logged in normal use with the grant still in place.
    /// </summary>
    internal static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// simplification: four events (two keystrokes) the tap never saw. Ceiling: another app's tap ahead of Dictum that swallows
    /// four events in a row with none reaching Dictum reads as starvation. Trigger to retire: such a removal seen in a log.
    /// </summary>
    internal const long Behind = 4;

    private readonly object _gate = new();
    private long _tapSeen = -1;
    private long _hidBase;
    private long _baseMs;
    private long _lastTickMs = long.MinValue;
    private long _pausedUntilMs = long.MinValue;

    /// <summary>One poll, from the timer. True when the tap is starved and must be removed.</summary>
    /// <param name="nowMs">Environment.TickCount64 now.</param>
    /// <param name="hidCount">Events of the tap's types the HID system has seen so far.</param>
    /// <param name="tapCount">Events of those types the tap's callback has seen so far.</param>
    /// <param name="secureInput">Whether Secure Event Input is on now.</param>
    public bool Check(long nowMs, long hidCount, long tapCount, bool secureInput)
    {
        lock (_gate)
        {
            var stalled = _lastTickMs != long.MinValue && nowMs - _lastTickMs > 3 * (long)Tick.TotalMilliseconds;
            _lastTickMs = nowMs;
            if (secureInput || stalled) _pausedUntilMs = nowMs + (long)Gap.TotalMilliseconds;
            if (nowMs < _pausedUntilMs || tapCount != _tapSeen)
            {
                (_tapSeen, _hidBase, _baseMs) = (tapCount, hidCount, nowMs);
                return false;
            }
            return Starved(nowMs, hidCount);
        }
    }

    /// <summary>The same test now, without moving the baseline: macOS disabled the tap, and it is turned back on only if this says no.</summary>
    public bool Probe(long nowMs, long hidCount, long tapCount, bool secureInput)
    {
        lock (_gate)
        {
            if (secureInput || nowMs < _pausedUntilMs || tapCount != _tapSeen) return false;
            return Starved(nowMs, hidCount);
        }
    }

    private bool Starved(long nowMs, long hidCount) =>
        hidCount - _hidBase >= Behind && nowMs - _baseMs >= (long)Gap.TotalMilliseconds;
}
