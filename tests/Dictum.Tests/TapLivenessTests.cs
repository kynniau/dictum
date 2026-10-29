using System.Runtime.CompilerServices;
using Dictum.Platform;

namespace Dictum.Tests;

/// <summary>
/// The event tap's liveness, recovery and revocation rules (docs/DECISIONS.md "Request access", items 15 to 22, 29/10/2026): a
/// tap the keys no longer get past is disabled at once, removed and asked for again; a refusal is the grant gone. Every pin here
/// drives the tap's own methods with fake counts and no native tap; the few that build a real CGEvent are macOS-only.
/// Split from InputAccessTests.cs (merge lens on fe03270: that file passed 1,000 lines).
/// </summary>
public sealed class TapLivenessTests
{
    // ---- The tap macOS keeps disabling (Finding B) -----------------------------------------------------------------

    // Liveness (Finding B, second design, approved by the orchestrator 29/10/2026 21:37 AEDT): keys the HID system saw and the tap did not.
    private static readonly long GapMs = (long)Dictum.Mac.Input.TapLiveness.Gap.TotalMilliseconds;
    private static readonly long TickMs = (long)Dictum.Mac.Input.TapLiveness.Tick.TotalMilliseconds;

    [Fact]
    public void Liveness_calls_a_tap_starved_only_when_the_system_sees_keys_the_tap_does_not()
    {
        var fed = new Dictum.Mac.Input.TapLiveness();
        long hid = 100, tap = 7;
        for (long t = 0; t <= 10 * TickMs; t += TickMs, hid += 6, tap += 6) Assert.False(fed.Check(t, hid, tap, hid, 0, pause: false)); // typing reaches it

        var idle = new Dictum.Mac.Input.TapLiveness();
        for (long t = 0; t <= 10 * TickMs; t += TickMs) Assert.False(idle.Check(t, 100, 7, 100, 0, pause: false)); // nobody types: nothing to miss

        var lagging = new Dictum.Mac.Input.TapLiveness();
        Assert.False(lagging.Check(0, 100, 7, 100, 0, pause: false));
        Assert.False(lagging.Check(TickMs, 100, 7, 100, 0, pause: false));      // settled
        Assert.False(lagging.Check(2 * TickMs, 104, 7, 104, 0, pause: false));  // four behind, but within the gap
        Assert.False(lagging.Check(3 * TickMs, 104, 11, 104, 0, pause: false)); // and it caught up

        var starved = new Dictum.Mac.Input.TapLiveness();
        Assert.False(starved.Check(0, 100, 7, 100, 0, pause: false));
        Assert.False(starved.Check(TickMs, 100, 7, 100, 0, pause: false)); // settled: the baseline is taken here
        Assert.False(starved.Check(2 * TickMs, 102, 7, 102, 0, pause: false)); // one keystroke missed: not yet
        Assert.True(starved.Check(TickMs + GapMs, 100 + Dictum.Mac.Input.TapLiveness.Behind, 7, 104, 0, pause: false));
        Assert.Equal(Dictum.Mac.Input.TapLiveness.TapStarved, starved.Tripped.Why);
        Assert.Equal(Dictum.Mac.Input.TapLiveness.Behind, starved.Tripped.Missed);
    }

    /// <summary>
    /// The lock of 22:31-22:32 AEDT (29/10/2026): the tap fed, the session counter flat, typing stalled the whole minute. HID moving
    /// with the session standing still trips it, whatever the tap's count does.
    /// </summary>
    [Fact]
    public void Liveness_calls_keys_held_when_the_session_count_stands_still_while_the_system_sees_keys()
    {
        var held = new Dictum.Mac.Input.TapLiveness();
        long hid = 5140, tap = 111;
        Assert.False(held.Check(0, hid, tap, 4528, 0, pause: false));
        hid += 9; tap += 9;
        Assert.False(held.Check(TickMs, hid, tap, 4528, 0, pause: false));
        hid += 9; tap += 9;
        Assert.True(held.Check(GapMs, hid, tap, 4528, 0, pause: false)); // the tap is fed; the keys went nowhere
        Assert.Equal(Dictum.Mac.Input.TapLiveness.KeysHeld, held.Tripped.Why);
        Assert.Equal(18, held.Tripped.Missed);
    }

    /// <summary>A paste moves the HID count and the session count together (22:29:35: HID +5, session +4): never a held key.</summary>
    [Fact]
    public void Liveness_does_not_call_keys_held_for_a_paste_that_reaches_the_session()
    {
        var live = new Dictum.Mac.Input.TapLiveness();
        long hid = 5034, session = 4428, tap = 4;
        Assert.False(live.Check(0, hid, tap, session, 0, pause: false));
        Assert.False(live.Check(TickMs, hid, tap, session, 0, pause: false));
        hid += 5; session += 4; tap += 4; // the paste
        Assert.False(live.Check(2 * TickMs, hid, tap, session, 0, pause: false));
        for (var i = 3; i <= 8; i++) Assert.False(live.Check(i * TickMs, hid, tap, session, 0, pause: false));
    }

    /// <summary>Secure input pauses the session comparison as it pauses the tap's: keys a password field takes never count as held.</summary>
    [Fact]
    public void Liveness_pauses_the_session_comparison_for_secure_input()
    {
        var live = new Dictum.Mac.Input.TapLiveness();
        long hid = 100, tap = 7;
        Assert.False(live.Check(0, hid, tap, 100, 0, pause: false));
        for (var i = 1; i <= 20; i++) { hid += 6; tap += 6; Assert.False(live.Check(i * TickMs, hid, tap, 100, 0, pause: true)); }
        hid += 6; tap += 6;
        Assert.False(live.Check(21 * TickMs, hid, tap, 100, 0, pause: false)); // just turned off: still paused
        Assert.False(live.Check(22 * TickMs, hid, tap, 100, 0, pause: false)); // the gap is over; the baseline is fresh
        hid += 6; tap += 6;
        Assert.False(live.Check(23 * TickMs, hid, tap, 100, 0, pause: false));
        Assert.True(live.Check(22 * TickMs + GapMs, hid, tap, 100, 0, pause: false)); // keys held again count again
    }

    /// <summary>
    /// The HID counter records an event up to half a second after the tap saw it (the paste of 22:29:35): with the baseline taken as the
    /// tap moved, the late record read as a missed key and a healthy tap was asked for again. The baseline is taken one tick later.
    /// </summary>
    [Fact]
    public void Liveness_waits_one_tick_after_the_tap_moves_before_counting()
    {
        var live = new Dictum.Mac.Input.TapLiveness();
        Assert.False(live.Check(0, 5034, 4, 4428, 0, pause: false));
        Assert.False(live.Check(TickMs, 5034, 4, 4428, 0, pause: false));
        Assert.False(live.Check(2 * TickMs, 5035, 8, 4428, 0, pause: false)); // the tap saw four; the HID counter has one so far
        Assert.False(live.Check(3 * TickMs, 5039, 8, 4432, 0, pause: false)); // the other four recorded late: the baseline, not a miss
        Assert.False(live.Check(4 * TickMs, 5039, 8, 4432, 0, pause: false));
        Assert.False(live.Check(3 * TickMs + GapMs, 5039, 8, 4432, 0, pause: false));
        Assert.False(live.Check(3 * TickMs + GapMs + TickMs, 5039, 8, 4432, 0, pause: false));
    }

    /// <summary>Condition 1: a password field or Secure Keyboard Entry keeps keys from taps on purpose; that is never starvation, nor for one gap after it ends.</summary>
    [Fact]
    public void Liveness_pauses_for_secure_input_and_for_one_gap_after_it()
    {
        var live = new Dictum.Mac.Input.TapLiveness();
        long t = 0, hid = 100;
        Assert.False(live.Check(t, hid, 7, hid, 0, pause: false));
        for (var i = 0; i < 20; i++) { hid += 6; Assert.False(live.Check(t += TickMs, hid, 7, hid, 0, pause: true)); } // ten seconds of password typing
        hid += 6;
        Assert.False(live.Check(t += TickMs, hid, 7, hid, 0, pause: false)); // just turned off: keys still missed, still paused
        Assert.False(live.Probe(t, hid, 7, hid, 0, pause: true));
        Assert.False(live.Check(t += TickMs, hid, 7, hid, 0, pause: false));    // the window is over, nothing missed since
        Assert.False(live.Check(t += TickMs, hid, 7, hid, 0, pause: false));    // settled
        Assert.True(live.Check(t + GapMs, hid + Dictum.Mac.Input.TapLiveness.Behind, 7, hid + Dictum.Mac.Input.TapLiveness.Behind, 0, pause: false)); // keys missed now count again
    }

    /// <summary>A timer that stalled (the Mac slept) pauses the check for one gap, as secure input does.</summary>
    [Fact]
    public void Liveness_pauses_after_the_timer_stalled()
    {
        var live = new Dictum.Mac.Input.TapLiveness();
        Assert.False(live.Check(0, 100, 7, 100, 0, pause: false));
        Assert.False(live.Check(60_000, 200, 7, 200, 0, pause: false)); // first tick after a sleep, the counts far apart
        Assert.False(live.Check(60_000 + TickMs, 200, 7, 200, 0, pause: false));
    }

    /// <summary>Condition 2: the counts compared are the tap's own types; a mouse move the tap never sees is never one of them.</summary>
    [Fact]
    public void Liveness_counts_only_the_event_types_in_the_tap_mask()
    {
        Assert.Equal([10u, 11u, 12u], Dictum.Mac.Input.TapEvents.Types(mouse: false));
        Assert.Equal([10u, 11u, 12u, 3u, 4u, 25u, 26u], Dictum.Mac.Input.TapEvents.Types(mouse: true));
        ulong bits = 0;
        foreach (var type in Dictum.Mac.Input.TapEvents.Types(mouse: true)) bits |= 1UL << (int)type;
        Assert.Equal(bits, Dictum.Mac.Input.TapEvents.Mask(mouse: true));
    }

    // Re-create confirmation (orchestrator decision 29/10/2026 22:16 AEDT): keys stopped reaching the tap, so it is disabled at once
    // and asked for again; refused = the grant gone, created = a false positive kept. The native create itself is the tap thread's;
    // AfterRecreate(created) is the decision, driven here without a tap.

    /// <summary>The timer's trigger: the tap asks for a fresh tap first and says nothing yet; a refusal then sets the line the shell will hear.</summary>
    [Fact]
    public void A_starved_tap_asks_for_a_fresh_tap_and_reports_the_grant_gone_when_refused()
    {
        var log = new RecordingLog();
        long hid = 500;
        var lost = 0;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, lost: () => lost++, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => hid);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs); // settled
        hid += 20; // keys typed, none reaching the tap
        tap.LivenessTick(TickMs + GapMs);
        Assert.True(tap.RecreateRequested);
        Assert.Null(tap.DeniedLine); // nothing decided until macOS answers
        Assert.Equal(0, lost);       // the shell hears only after the tap is really gone (the tap thread's job)
        Assert.DoesNotContain(log.Lines, l => l.Level == LogLevel.Error);

        Assert.False(tap.AfterRecreate(created: false, nowMs: GapMs + 10));
        Assert.StartsWith("Event tap (key monitor) removed: 20 key events reached macOS but not Dictum, and macOS refused a fresh tap", tap.DeniedLine, StringComparison.Ordinal);
        Assert.Contains("press Request access in Settings > General (or the menu-bar panel).", tap.DeniedLine, StringComparison.Ordinal);
    }

    /// <summary>A fresh tap allowed: a false positive, kept, logged once, and the counts start again; a later trigger may ask again.</summary>
    [Fact]
    public void A_fresh_tap_allowed_is_kept_and_the_count_starts_again()
    {
        var log = new RecordingLog();
        long hid = 500;
        var lost = 0;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, lost: () => lost++, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => hid);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs); // settled
        hid += 20;
        tap.LivenessTick(TickMs + GapMs);
        Assert.True(tap.RecreateRequested);
        Assert.True(tap.AfterRecreate(created: true, nowMs: TickMs + GapMs + 10));
        Assert.Null(tap.DeniedLine);
        Assert.Single(log.Lines, l => l.Level == LogLevel.Info && l.Message.StartsWith("Event tap (key monitor) re-created: 20 key events reached macOS but not Dictum, but macOS allowed a fresh tap", StringComparison.Ordinal));

        tap.LivenessTick(TickMs + 2 * GapMs); // nothing missed since the fresh tap: no trigger
        hid += 3;
        tap.LivenessTick(TickMs + 3 * GapMs);
        Assert.Equal(0, lost);
        Assert.DoesNotContain(log.Lines, l => l.Level == LogLevel.Error);
    }

    /// <summary>A second trigger inside the window of a fresh tap is not asked again: the tap stays removed and the grant is reported gone.</summary>
    [Fact]
    public void A_second_trigger_soon_after_a_fresh_tap_stays_removed()
    {
        var windowMs = (long)Dictum.Mac.Input.TapRecovery.Window.TotalMilliseconds;
        var log = new RecordingLog();
        long hid = 500;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => hid);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs);
        hid += 20;
        tap.LivenessTick(TickMs + GapMs);
        Assert.True(tap.AfterRecreate(created: true, nowMs: TickMs + GapMs));

        var t = TickMs + GapMs + TickMs;
        tap.LivenessTick(t);
        hid += 20;
        tap.LivenessTick(t + GapMs); // the second trigger, well inside the window
        Assert.False(tap.RecreateRequested);
        Assert.StartsWith("Event tap (key monitor) removed: keys stopped reaching it again within 30 s of a fresh tap", tap.DeniedLine, StringComparison.Ordinal);

        var later = new Dictum.Mac.Input.TapRecovery();
        Assert.True(later.MayRecreate(0));
        later.Recreated(1_000);
        Assert.False(later.MayRecreate(1_000 + windowMs));
        Assert.True(later.MayRecreate(1_000 + windowMs + 1)); // past the window: asked again
    }

    /// <summary>The keys Dictum posts itself (a paste) count with the hardware keys and need not reach its own tap: subtracted, they never trigger (22:10:12 AEDT).</summary>
    [Fact]
    public void Keys_dictum_posted_itself_never_count_as_missed()
    {
        var posted = new Dictum.Mac.Input.PostedEvents();
        long hid = 500;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, new RecordingLog(), trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, posted: () => posted.Count, sessionCount: () => hid);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs);
        hid += 4;      // Cmd down, V down, V up, Cmd up: the paste, counted by the HID system
        posted.Add(4); // and counted by Dictum as its own
        tap.LivenessTick(TickMs + GapMs);
        tap.LivenessTick(TickMs + 2 * GapMs);
        Assert.False(tap.RecreateRequested);
        Assert.Null(tap.DeniedLine);
        Assert.Equal(4, posted.Count);
    }

    /// <summary>The tap fed and the keys held after it (22:31:38): asked for again all the same, and the line says which comparison tripped.</summary>
    [Fact]
    public void A_fed_tap_whose_keys_are_held_after_it_is_asked_for_again()
    {
        var log = new RecordingLog();
        long hid = 5140, seenBase = 0;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => 4528);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs);
        hid += 20;
        for (var i = 0; i < 20; i++) tap.Handle(Dictum.Mac.Interop.CG.EventKeyUp, 0); // the tap sees every key (null event: counted, then nothing decoded)
        seenBase = tap.Seen;
        tap.LivenessTick(TickMs + GapMs);
        Assert.True(tap.RecreateRequested);
        Assert.Equal(20, seenBase);
        Assert.False(tap.AfterRecreate(created: false, nowMs: TickMs + GapMs + 10));
        Assert.StartsWith("Event tap (key monitor) removed: 20 key events reached macOS but no app, and macOS refused a fresh tap", tap.DeniedLine, StringComparison.Ordinal);
    }

    /// <summary>Condition 4 (29/10/2026 22:36 AEDT): the diagnostic's create probe never runs without DICTUM_TAP_DIAG=1 (both platforms).</summary>
    [Fact]
    public void The_diagnostic_create_probe_never_runs_outside_the_switch()
    {
        var probes = 0;
        var quiet = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, new RecordingLog(), trusted: () => true, stillTrusted: () => true, hidCount: () => 100, secureInput: () => false, sessionCount: () => 100,
            createProbe: () => { probes++; return true; }, diagnostics: false);
        for (var i = 0; i < 5; i++) quiet.LivenessTick(i * TickMs);
        Assert.Equal(0, probes);
        Assert.False(Dictum.Mac.Input.EventTap.Diagnostics); // the test host never sets the switch
    }

    /// <summary>Under the switch the probe runs on every tick and the line ends with its answer. macOS only: the line reads the Core Graphics preflight calls.</summary>
    [Fact]
    public void The_diagnostic_create_probe_runs_on_every_tick_under_the_switch()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The diagnostic line calls CGPreflightPostEventAccess and AXIsProcessTrusted.");
        DiagnosticProbeCheck();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DiagnosticProbeCheck()
    {
        var probes = 0;
        var log = new RecordingLog();
        var on = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, stillTrusted: () => true, hidCount: () => 100, secureInput: () => false, sessionCount: () => 100,
            createProbe: () => { probes++; return true; }, diagnostics: true);
        for (var i = 0; i < 5; i++) on.LivenessTick(i * TickMs);
        Assert.Equal(5, probes);
        Assert.Equal(5, log.Lines.Count(l => l.Message.StartsWith("Tap diag (key monitor):", StringComparison.Ordinal) && l.Message.EndsWith("create=allowed", StringComparison.Ordinal)));
    }


    /// <summary>
    /// BLOCKER (merge lens on fe03270, 29/10/2026 23:50 AEDT): the hotkey Dictum swallows (SuppressHotkey, the default) is counted by the
    /// HID system and reaches no app, so two quick hands-free dictations with no typing between read as keys held. The platform's
    /// swallowed count is taken off the HID count before the session comparison: four swallowed events with the session flat never trigger.
    /// </summary>
    [Fact]
    public void Keys_dictum_swallowed_itself_never_count_as_held()
    {
        var swallowed = new Dictum.Mac.Input.SwallowedEvents();
        long hid = 5000;
        var tap = new Dictum.Mac.Input.EventTapHotkey(new Dictum.Hotkey.HotkeyMatcher(Dictum.Hotkey.HotkeyParser.Parse("RightAlt"), suppress: true), _ => { }, new RecordingLog(),
            trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => 4000, swallowed: swallowed);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs);
        hid += 4;         // two hands-free taps on Right Option: four flagsChanged events the HID system counted
        for (var i = 0; i < 4; i++) tap.Handle(Dictum.Mac.Interop.CG.EventFlagsChanged, 0); // the tap saw them (null event: counted, nothing decoded)
        swallowed.Add(4); // and Dictum swallowed them, so no app ever received them and the session count stays at 4000
        tap.LivenessTick(TickMs + GapMs);
        tap.LivenessTick(TickMs + 2 * GapMs);
        Assert.False(tap.RecreateRequested);
        Assert.Null(tap.DeniedLine);

        var rule = new Dictum.Mac.Input.TapLiveness();
        Assert.False(rule.Check(0, 5000, 10, 4000, 0, pause: false));
        Assert.False(rule.Check(TickMs, 5000, 10, 4000, 0, pause: false));
        Assert.False(rule.Check(TickMs + GapMs, 5004, 14, 4000, 4, pause: false));       // swallowed: not held
        Assert.True(rule.Check(TickMs + 2 * GapMs, 5008, 18, 4000, 4, pause: false));    // four more that nobody swallowed: held
        Assert.Equal(Dictum.Mac.Input.TapLiveness.KeysHeld, rule.Tripped.Why);
        Assert.Equal(4, rule.Tripped.Missed);
    }

    /// <summary>The swallowed count is the handler's own: an event the hotkey swallows counts, one it passes on does not. macOS only (a real CGEvent).</summary>
    [Fact]
    public void A_swallowed_hotkey_event_is_counted_as_swallowed()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Builds a real CGEvent.");
        SwallowedCountCheck();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SwallowedCountCheck()
    {
        var swallowed = new Dictum.Mac.Input.SwallowedEvents();
        var tap = new Dictum.Mac.Input.EventTapHotkey(new Dictum.Hotkey.HotkeyMatcher(Dictum.Hotkey.HotkeyParser.Parse("F20"), suppress: true), _ => { }, new RecordingLog(),
            trusted: () => true, stillTrusted: () => true, hidCount: () => 0, secureInput: () => false, sessionCount: () => 0, swallowed: swallowed);
        var ev = Dictum.Mac.Interop.CG.EventCreateKeyboardEvent(0, (ushort)Dictum.Mac.Input.KeyCodeMap.ToMac(0x83 /* VK_F20 */), true);
        try
        {
            Assert.False(tap.Handle(Dictum.Mac.Interop.CG.EventKeyDown, ev)); // the hotkey: swallowed
            Assert.Equal(1, swallowed.Count);
            var other = Dictum.Mac.Interop.CG.EventCreateKeyboardEvent(0, 0 /* A */, true);
            try
            {
                Assert.True(tap.Handle(Dictum.Mac.Interop.CG.EventKeyDown, other)); // any other key: passed on
            }
            finally
            {
                Dictum.Mac.Interop.CF.Release(other);
            }
            Assert.Equal(1, swallowed.Count);
        }
        finally
        {
            Dictum.Mac.Interop.CF.Release(ev);
        }
    }

    /// <summary>
    /// A sleep: Environment.TickCount64 does not count it on macOS (measured 29/10/2026), so the tap reads a wall clock for the stall
    /// pause. The first tick after a wake, with the keys that woke the Mac counted by HID before any app saw them, never triggers.
    /// </summary>
    [Fact]
    public void A_tap_waking_from_sleep_pauses_on_the_wall_clock_not_the_tick_count()
    {
        long hid = 500;
        var wall = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, new RecordingLog(), trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => 400,
            wallClock: () => wall);
        tap.LivenessTick(0);
        wall = wall.AddMilliseconds(TickMs);
        tap.LivenessTick(TickMs);
        wall = wall.AddMinutes(5); // asleep five minutes: the tick count moved on by one tick only
        hid += 6;                  // the keys that woke it
        tap.LivenessTick(2 * TickMs);
        wall = wall.AddMilliseconds(TickMs);
        tap.LivenessTick(3 * TickMs);
        Assert.False(tap.RecreateRequested);
        Assert.Null(tap.DeniedLine);
    }

    /// <summary>Secure input: keys withheld from taps on purpose never trigger, even for seconds.</summary>
    [Fact]
    public void A_tap_under_secure_input_is_never_asked_for_again()
    {
        long hid = 500;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, new RecordingLog(), trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => true, sessionCount: () => hid);
        tap.LivenessTick(0);
        tap.LivenessTick(TickMs);
        hid += 20;
        tap.LivenessTick(TickMs + GapMs);
        tap.LivenessTick(TickMs + 2 * GapMs);
        Assert.False(tap.RecreateRequested);
        Assert.Null(tap.DeniedLine);
    }

    /// <summary>Condition 5: the first timeout-disable with the check saying trusted turns the tap back on only while keys still reach it; otherwise it is asked for again, not turned back on.</summary>
    [Fact]
    public void A_disabled_tap_the_keys_stopped_reaching_is_asked_for_again_not_turned_back_on()
    {
        var log = new RecordingLog();
        long hid = 500;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, stillTrusted: () => true, hidCount: () => hid, secureInput: () => false, sessionCount: () => hid);
        tap.LivenessTick(Environment.TickCount64 - 3 * GapMs); // the last polls, seconds ago
        tap.LivenessTick(Environment.TickCount64 - 2 * GapMs);
        hid += 20;                                              // keys since, none reaching the tap
        tap.Handle(Dictum.Mac.Interop.CG.EventTapDisabledByTimeout, 0);
        Assert.True(tap.RecreateRequested);
        Assert.DoesNotContain(log.Lines, l => l.Message.EndsWith("re-enabled.", StringComparison.Ordinal));
        Assert.False(tap.AfterRecreate(created: false));
        Assert.StartsWith("Event tap (key monitor) removed: macOS disabled it (timeout) and 20 key events reached macOS but not Dictum, and macOS refused a fresh tap", tap.DeniedLine, StringComparison.Ordinal);

        var quietLog = new RecordingLog();
        var quiet = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, quietLog, trusted: () => true, stillTrusted: () => true, hidCount: () => 500, secureInput: () => false, sessionCount: () => 500);
        quiet.LivenessTick(Environment.TickCount64 - 3 * GapMs);
        quiet.LivenessTick(Environment.TickCount64 - 2 * GapMs);
        quiet.Handle(Dictum.Mac.Interop.CG.EventTapDisabledByTimeout, 0); // nothing missed: a plain timeout, turned back on
        Assert.False(quiet.RecreateRequested);
        Assert.Single(quietLog.Lines, l => l.Message.EndsWith("re-enabled.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_disable_guard_reenables_once_and_never_for_a_repeat_or_without_the_grant()
    {
        var window = (long)Dictum.Mac.Input.TapDisableGuard.Window.TotalMilliseconds;
        var guard = new Dictum.Mac.Input.TapDisableGuard();
        Assert.True(guard.ShouldReenable(1_000, trusted: true));                // a first timeout: turned back on
        Assert.False(guard.ShouldReenable(3_000, trusted: true));               // again 2 s later: not
        Assert.True(guard.ShouldReenable(3_000 + window + 1, trusted: true));   // a lone timeout a minute on: turned back on
        Assert.False(new Dictum.Mac.Input.TapDisableGuard().ShouldReenable(1_000, trusted: false)); // the grant gone: never
    }

    /// <summary>The re-enable path itself, through the tap's own handler: the second disable removes the tap for good (no fresh tap asked for) and sets the line the shell will hear.</summary>
    [Fact]
    public void A_tap_macOS_keeps_disabling_is_removed_and_reported_instead_of_turned_back_on()
    {
        var log = new RecordingLog();
        var lost = 0;
        var tap = new Dictum.Mac.Input.EventTapKeyMonitor(_ => { }, log, trusted: () => true, lost: () => lost++, stillTrusted: () => true, hidCount: () => 0, secureInput: () => false, sessionCount: () => 0);
        Assert.True(tap.Handle(Dictum.Mac.Interop.CG.EventTapDisabledByTimeout, 0));
        Assert.Null(tap.DeniedLine);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.EndsWith("was disabled by macOS (timeout) and re-enabled.", StringComparison.Ordinal));

        Assert.True(tap.Handle(Dictum.Mac.Interop.CG.EventTapDisabledByTimeout, 0));
        Assert.True(tap.Handle(Dictum.Mac.Interop.CG.EventTapDisabledByUserInput, 0));
        Assert.False(tap.RecreateRequested);
        Assert.Single(log.Lines, l => l.Message.EndsWith("re-enabled.", StringComparison.Ordinal));
        Assert.StartsWith("Event tap (key monitor) removed: macOS keeps disabling it (timeout)", tap.DeniedLine, StringComparison.Ordinal);
        Assert.Equal(0, lost); // told after the removal, by the tap thread

        var other = new Dictum.Mac.Input.EventTapHotkey(new Dictum.Hotkey.HotkeyMatcher(Dictum.Hotkey.HotkeyParser.Parse("RightAlt"), suppress: true), _ => { }, new RecordingLog(), trusted: () => true, stillTrusted: () => false, hidCount: () => 0, secureInput: () => false, sessionCount: () => 0);
        other.Handle(Dictum.Mac.Interop.CG.EventTapDisabledByTimeout, 0); // the grant already gone: removed at the first disable
        Assert.NotNull(other.DeniedLine);
    }
}
