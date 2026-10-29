namespace Dictum.Mac.Input;

/// <summary>
/// Whether keys are getting past an installed tap (PR #82 round 2, 29/10/2026). After the grant was taken away under a running
/// tap, no app received keys until the process was killed, and nothing the process can ask flipped: AXIsProcessTrusted,
/// CGPreflightPostEventAccess and CGEventTapIsEnabled all said yes throughout (owner's Mac, 22:31-22:32 AEDT, per-tick log).
/// What does move is three counters, read every <see cref="Tick"/>: the HID system's count of the tap's own event types
/// (CGEventSourceCounterForEventType, HID state), the count the tap's callback has seen, and the combined session state's count
/// of the same types, which rises only for keys that got past the taps into the session. Two ways to be stuck, both seen:
/// <list type="bullet">
/// <item>the tap starved: HID moves on by <see cref="Behind"/> over <see cref="Gap"/> while the tap's count stands still
/// (22:11:55 the same day, macOS held the keys without calling the tap);</item>
/// <item>the keys held after the tap: HID moves on while the session count stands still although the tap is fed (22:31:38 to
/// 22:32:12: HID +457, tap +456, session +0, typing stalled the whole minute).</item>
/// </list>
/// Events Dictum's own taps swallowed (a suppressed hotkey) are counted by the HID system and reach no app, so they are taken off
/// the HID count before the session comparison (<see cref="SwallowedEvents"/>); otherwise two quick hands-free dictations with no
/// typing between read as keys held (merge lens on fe03270).
/// Either trips the tap's trigger (<see cref="EventTap"/>): disabled at once, removed, and asked for again (<see cref="TapRecovery"/>),
/// because the counts alone cannot tell the grant gone from keys another tap or macOS took. In healthy use all three rise together
/// (22:29:32 to 22:30:02: HID +100, session +96, tap +100). The HID counter records an event up to about half a second after the
/// tap saw it (the paste of 22:29:35), so the tap's baseline is taken one tick after the tap last moved, not at the moment it moved.
/// <para>
/// Paused while Secure Event Input is on (a password field, Terminal's Secure Keyboard Entry), when macOS keeps keys from taps on
/// purpose, and for one <see cref="Gap"/> after it turns off; paused the same way after the timer itself stalled (sleep).
/// Thread-safe: <see cref="Check"/> runs on the timer, <see cref="Probe"/> on the tap thread.
/// </para>
/// </summary>
internal sealed class TapLiveness
{
    /// <summary>
    /// simplification: a fixed 500 ms poll. Ceiling: a stuck tap holds keys for up to one tick past <see cref="Behind"/> events.
    /// Trigger to retire: a stall over 1.5 s seen in the reset-while-running test, or a notification from macOS worth the native code.
    /// </summary>
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// simplification: the tap or the session may lag the HID system this long before it counts as stuck. Ceiling: a tap that is slow
    /// for longer than a second while keys arrive is asked for again too (a fresh tap allowed keeps the hotkey). Trigger to retire:
    /// a removal logged in normal use with the grant still in place.
    /// </summary>
    internal static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// simplification: four events (two keystrokes) that did not get through. Ceiling: another app's tap ahead of Dictum that
    /// swallows four events in a row reads as stuck; the fresh tap is then allowed and kept. Trigger to retire: such a line seen in a
    /// log more than once a day.
    /// </summary>
    internal const long Behind = 4;

    /// <summary>
    /// Which comparison tripped, in the words the log line uses after the count: what was measured, and no time claim the rule
    /// does not measure (the second is counted from the baseline, not from the first missed key; orchestrator, 29/10/2026 22:50 AEDT).
    /// </summary>
    internal const string TapStarved = "reached macOS but not Dictum";
    internal const string KeysHeld = "reached macOS but no app";

    private readonly object _gate = new();
    private readonly Lag _tap = new(settle: true);
    private readonly Lag _session = new(settle: false);
    private long _lastTickMs = long.MinValue;
    private long _pausedUntilMs = long.MinValue;
    private string _tripped = TapStarved;
    private long _trippedMissed;
    private long _trippedSinceMs;

    /// <summary>One poll, from the timer. True when the keys are stuck and the tap must be asked for again.</summary>
    /// <param name="nowMs">Environment.TickCount64 now.</param>
    /// <param name="hidCount">Events of the tap's types the HID system has seen so far (Dictum's own posted ones subtracted).</param>
    /// <param name="tapCount">Events of those types the tap's callback has seen so far.</param>
    /// <param name="sessionCount">Events of those types that reached the session (the combined session state's count).</param>
    /// <param name="swallowedCount">Events Dictum's own taps swallowed so far (<see cref="SwallowedEvents"/>): counted by HID, received by no app.</param>
    /// <param name="pause">Whether to pause now: Secure Event Input is on, or the caller's wall clock says the timer stalled (a sleep; Environment.TickCount64 does not count sleep on macOS, measured 29/10/2026).</param>
    public bool Check(long nowMs, long hidCount, long tapCount, long sessionCount, long swallowedCount, bool pause)
    {
        lock (_gate)
        {
            var stalled = _lastTickMs != long.MinValue && nowMs - _lastTickMs > 3 * (long)Tick.TotalMilliseconds;
            _lastTickMs = nowMs;
            if (pause || stalled) _pausedUntilMs = nowMs + (long)Gap.TotalMilliseconds;
            var paused = nowMs < _pausedUntilMs;
            var tapStuck = _tap.Follow(nowMs, hidCount, tapCount, paused);
            var sessionStuck = _session.Follow(nowMs, hidCount - swallowedCount, sessionCount, paused);
            return Trip(tapStuck, sessionStuck, hidCount, swallowedCount);
        }
    }

    /// <summary>The same test now, without moving the baselines: macOS disabled the tap, and it is turned back on only if this says no.</summary>
    public bool Probe(long nowMs, long hidCount, long tapCount, long sessionCount, long swallowedCount, bool pause)
    {
        lock (_gate)
        {
            if (pause || nowMs < _pausedUntilMs) return false;
            return Trip(_tap.Stuck(nowMs, hidCount, tapCount), _session.Stuck(nowMs, hidCount - swallowedCount, sessionCount), hidCount, swallowedCount);
        }
    }

    private bool Trip(bool tapStuck, bool sessionStuck, long hidCount, long swallowedCount)
    {
        if (!tapStuck && !sessionStuck) return false;
        (_tripped, _trippedMissed, _trippedSinceMs) = tapStuck
            ? (TapStarved, _tap.Missed(hidCount), _tap.SinceMs)
            : (KeysHeld, _session.Missed(hidCount - swallowedCount), _session.SinceMs);
        return true;
    }

    /// <summary>A fresh tap was allowed: the keys went somewhere else. Both counts start again from here.</summary>
    public void Fed(long nowMs, long hidCount, long tapCount, long sessionCount, long swallowedCount)
    {
        lock (_gate)
        {
            _tap.Reset(nowMs, hidCount, tapCount);
            _session.Reset(nowMs, hidCount - swallowedCount, sessionCount);
        }
    }

    /// <summary>What tripped last: the words for the log line, how many events did not get through, and since when.</summary>
    public (string Why, long Missed, long SinceMs) Tripped
    {
        get
        {
            lock (_gate) return (_tripped, _trippedMissed, _trippedSinceMs);
        }
    }

    /// <summary>One follower (the tap's count, or the session's) against the HID leader.</summary>
    private sealed class Lag(bool settle)
    {
        private long _followerSeen = -1;
        private long _leaderBase;
        private long _baseMs;
        private bool _settling;
        private bool _wasPaused;

        /// <summary>Feeds one tick; true when the follower has stood still while the leader moved on by <see cref="Behind"/> for <see cref="Gap"/>.</summary>
        public bool Follow(long nowMs, long leader, long follower, bool paused)
        {
            if (paused || _wasPaused || follower != _followerSeen)
            {
                // The follower moved, the check is paused, or the pause just ended: the leader's baseline is taken now, or one tick
                // later when the leader is known to record late (the HID counter after the tap saw a key), so neither a late record
                // nor a key that arrived while paused is read as a missed key.
                _followerSeen = follower;
                _settling = settle && !paused;
                _wasPaused = paused;
                (_leaderBase, _baseMs) = (leader, nowMs);
                return false;
            }
            if (_settling)
            {
                _settling = false;
                (_leaderBase, _baseMs) = (leader, nowMs);
                return false;
            }
            return Stuck(nowMs, leader, follower);
        }

        public bool Stuck(long nowMs, long leader, long follower) =>
            follower == _followerSeen && !_settling && leader - _leaderBase >= Behind && nowMs - _baseMs >= (long)Gap.TotalMilliseconds;

        public void Reset(long nowMs, long leader, long follower)
        {
            _followerSeen = follower;
            _settling = false;
            (_leaderBase, _baseMs) = (leader, nowMs);
        }

        public long Missed(long leader) => leader - _leaderBase;

        public long SinceMs => _baseMs;
    }
}
