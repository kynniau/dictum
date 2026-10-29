using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictum.Hotkey;
using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Input;

/// <summary>
/// A Core Graphics event tap on a dedicated thread running its own CFRunLoop: the Mac counterpart of the Windows
/// LowLevelHook. The callback "runs from the CFRunLoop to which the tap CFMachPort is added as a source" (CGEvent.h), so a
/// thread that does nothing but run that loop keeps the tap answering while the UI thread is busy; macOS disables a tap
/// that is slow to answer and tells it with kCGEventTapDisabledByTimeout, and the tap re-enables itself then, but only through
/// <see cref="TapDisableGuard"/> and a <see cref="TapLiveness"/> check: a tap macOS keeps disabling, or one the keys no longer
/// get past (not reaching it, or held after it), is not left in place, because a tap left behind after the grant was taken away held every key on the Mac (PR #82
/// round 2, 29/10/2026). A timer off the tap thread runs the liveness check every <see cref="TapLiveness.Tick"/>, since macOS
/// held the keys for about 50 s before its first disable notice; it never waits on the tap thread.
/// <para>
/// When the check trips, the tap is disabled at once, so the keys are free, and the tap thread removes it and asks macOS for a
/// fresh one (<see cref="TapRecovery"/>): refused, the grant is gone and the shell is told (IInputAccess.Revoked); created, the
/// keys went to another tap or to macOS, and the fresh tap carries on. Nothing is ever re-enabled or re-created from the timer.
/// </para>
/// <para>
/// Both taps are active filters (kCGEventTapOptionDefault) at the session tap point (kCGHIDEventTap is for root only,
/// CGEvent.h), so the app needs Accessibility and not Input Monitoring as well (orchestrator decision, 22/10/2026): a tap
/// "may only receive key up and down events if ... the caller is enabled for assistive device access" (CGEvent.h). The key
/// monitor passes every event on unchanged; the hotkey swallows an event (returns NULL) only when the matcher says so.
/// </para>
/// <para>
/// The callback only decodes, matches and posts; it never blocks and never touches the UI. Events Dictum posted itself carry
/// <see cref="InjectedMarker"/> in kCGEventSourceUserData and are passed on unseen, as Windows skips its marker.
/// </para>
/// </summary>
internal abstract class EventTap : IDisposable
{
    /// <summary>The user-data value on every event Dictum posts: the same number the Windows build puts in dwExtraInfo ("DICT").</summary>
    public const long InjectedMarker = 0x44494354;

    /// <summary>
    /// DICTUM_TAP_DIAG=1 in the environment: one Info line per liveness tick with every signal that might flip at a revocation,
    /// for the instrumented reset run only (orchestrator, 29/10/2026 22:16 AEDT). Off by default; a release build never sets it.
    /// </summary>
    internal static readonly bool Diagnostics = Environment.GetEnvironmentVariable("DICTUM_TAP_DIAG") == "1";

    private readonly ILog _log;
    private readonly string _name;
    private readonly Func<bool> _trusted;
    private readonly Func<bool> _stillTrusted;
    private readonly Action? _lost;
    private readonly TapDisableGuard _disables = new();
    private readonly TapLiveness _liveness = new();
    private readonly TapRecovery _recovery = new();
    private readonly Func<long>? _hidCount;
    private readonly Func<long>? _sessionCount;
    private readonly Func<long> _posted;
    private readonly SwallowedEvents _swallowed;
    private readonly Func<DateTime> _wallClock;
    private DateTime _lastTickWall;
    private readonly Func<bool> _secureInput;
    private readonly Func<bool> _createProbe;
    private readonly bool _diagnostics;
    private Timer? _livenessTimer;
    private long _seen;
    private int _triggered;
    private int _recreate;
    private int _ticking;
    private string _why = "";
    private string? _deniedLine;
    private Thread? _thread;
    private nint _runLoop;
    private nint _port;
    private nint _source;
    private volatile bool _stop;
    private TapEvents? _events;
    private int _faults;

    /// <param name="trusted">Whether the process may run an active tap; the default asks macOS and prompts once (<see cref="Accessibility"/>). Tests pass their own.</param>
    /// <param name="lost">Told, on the tap thread after the tap is gone, when it removed itself for want of the grant (IInputAccess.Revoked).</param>
    /// <param name="stillTrusted">The silent check read when macOS disables the tap; the default is AXIsProcessTrusted. Tests pass their own.</param>
    /// <param name="hidCount">Events of the tap's types the HID system has seen; the default sums CGEventSourceCounterForEventType over the mask. Tests pass their own.</param>
    /// <param name="secureInput">Whether Secure Event Input is on; the default is IsSecureEventInputEnabled. Tests pass their own.</param>
    /// <param name="posted">Key events Dictum has posted itself (<see cref="PostedEvents"/>), subtracted from the HID count; none by default.</param>
    /// <param name="sessionCount">Events of the tap's types that reached the session; the default reads the combined session state's counters. Tests pass their own.</param>
    /// <param name="createProbe">The diagnostic's listen-only create probe (allowed or refused); the default makes a real one. Tests pass their own.</param>
    /// <param name="diagnostics">Whether the per-tick diagnostic runs; the default is <see cref="Diagnostics"/>. Only tests set it.</param>
    /// <param name="swallowed">The platform's count of events its taps swallowed (<see cref="SwallowedEvents"/>); a private one when null (tests).</param>
    /// <param name="wallClock">The clock the stall pause reads; DateTime.UtcNow by default, and only tests pass one.</param>
    protected EventTap(string name, ILog log, Func<bool>? trusted = null, Action? lost = null, Func<bool>? stillTrusted = null,
        Func<long>? hidCount = null, Func<bool>? secureInput = null, Func<long>? posted = null, Func<long>? sessionCount = null,
        Func<bool>? createProbe = null, bool? diagnostics = null, SwallowedEvents? swallowed = null, Func<DateTime>? wallClock = null)
    {
        _sessionCount = sessionCount;
        _swallowed = swallowed ?? new SwallowedEvents();
        _wallClock = wallClock ?? (() => DateTime.UtcNow);
        _createProbe = createProbe ?? CreateProbe;
        _diagnostics = diagnostics ?? Diagnostics;
        _name = name;
        _log = log;
        _trusted = trusted ?? Accessibility.EnsureTrusted;
        _lost = lost;
        _stillTrusted = stillTrusted ?? AX.IsProcessTrusted;
        _hidCount = hidCount;
        _secureInput = secureInput ?? HIToolbox.IsSecureEventInputEnabled;
        _posted = posted ?? (() => 0);
    }

    /// <summary>Starts the tap thread and waits until the tap is installed. False when it could not be (not trusted for Accessibility, or refused), with the reason logged.</summary>
    public bool Start()
    {
        if (_thread is not null) return Installed;
        _stop = false;
        // Not disposed here: after a 5 s timeout the tap thread may still set it (it never allocates a kernel handle).
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = $"Dictum {_name}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return Installed;
    }

    private bool Installed => Volatile.Read(ref _port) != 0;

    /// <summary>For the log line: what this tap listens for.</summary>
    protected virtual string Describe() => "";

    /// <summary>Whether this tap watches the right and other mouse buttons too; read once, on the tap thread, when it starts.</summary>
    protected abstract bool WantsMouse { get; }

    /// <summary>Decides one key or button change on the tap thread. Returns whether to pass the event on.</summary>
    protected abstract bool OnKey(KeyChange change, bool injected);

    /// <summary>The HID system's count of the event types in this tap's mask (CGEventSourceCounterForEventType, HID system state).</summary>
    private long HidCount()
    {
        if (_hidCount is not null) return _hidCount();
        return Count(CG.SourceStateHidSystem);
    }

    private long Count(int stateId)
    {
        long total = 0;
        foreach (var type in TapEvents.Types(_events is { Mouse: true })) total += CG.EventSourceCounterForEventType(stateId, type);
        return total;
    }

    /// <summary>The HID count less the events Dictum posted itself, which it counts with the hardware keys (<see cref="PostedEvents"/>).</summary>
    private long AdjustedHid() => HidCount() - _posted();

    /// <summary>The combined session state's count of the tap's types: keys that got past the taps into the session.</summary>
    private long SessionCount() => _sessionCount is not null ? _sessionCount() : Count(CG.SourceStateCombinedSession);

    /// <summary>Events of the mask's types the callback has counted (for the pins).</summary>
    internal long Seen => Interlocked.Read(ref _seen);

    /// <summary>
    /// Whether to pause the liveness check now: Secure Event Input is on, or the wall clock says this timer stalled (a sleep).
    /// Environment.TickCount64 does not count time the Mac spent asleep (measured 29/10/2026: 4,306 s behind the wall clock
    /// after a day with three sleeps), so TapLiveness's own stall test cannot see a sleep; the wall clock can.
    /// </summary>
    private bool Pause()
    {
        var now = _wallClock();
        var stalled = _lastTickWall != default && now - _lastTickWall > 3 * TapLiveness.Tick;
        _lastTickWall = now;
        return stalled || _secureInput();
    }

    private unsafe void Run(ManualResetEventSlim ready)
    {
        GCHandle self = default;
        try
        {
            if (!_trusted())
            {
                // APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 7): do not reword. (Log line; no relaunch is needed once the grant arrives, the shell reinstalls the tap.)
                _log.Error($"Event tap ({_name}) not installed: Dictum is not allowed to control this Mac yet. Press Request access in Settings > General (or the menu-bar panel), or turn Dictum on in System Settings > Privacy & Security > Accessibility; the hotkey starts working as soon as it is on.");
                return;
            }
            // Retained so Dispose can still stop it, or find it already finished, after this thread has ended. This thread keeps
            // its own copy: Dispose may zero the field after its 2 s join while the removal below still needs the loop.
            var runLoop = CF.Retain(CF.RunLoopGetCurrent());
            Volatile.Write(ref _runLoop, runLoop);
            _events = new TapEvents(WantsMouse);
            self = GCHandle.Alloc(this);
            delegate* unmanaged<nint, uint, nint, nint, nint> callback = &Callback;
            if (!Install(runLoop, (nint)callback, GCHandle.ToIntPtr(self)))
            {
                _log.Error($"Event tap ({_name}) installation failed: macOS refused it (CGEventTapCreate returned null) although Accessibility reports this app as allowed.");
                return;
            }
            _log.Info($"Event tap installed on thread {Environment.CurrentManagedThreadId} ({_name}{Describe()}{(_events.Mouse ? ", mouse buttons watched" : "")}).");
            _livenessTimer = new Timer(_ => LivenessTick(), null, TapLiveness.Tick, TapLiveness.Tick);
            ready.Set();

            // A timed run, not CFRunLoopRun: a stop that arrives before the loop starts running would otherwise be lost and
            // the thread never end. Dispose sets _stop and stops the loop, so the wait is normally far shorter than a second.
            while (!_stop)
            {
                CF.RunLoopRunInMode(CF.RunLoopDefaultMode, 1.0, false);
                if (Interlocked.Exchange(ref _recreate, 0) == 0) continue;
                // The liveness check tripped and disabled the tap: removed here, then asked for again, on the thread that owns it.
                Remove(runLoop);
                if (!AfterRecreate(Install(runLoop, (nint)callback, GCHandle.ToIntPtr(self)))) break;
            }

            // The timer may be in a tick that reads the port: wait for it here, on the tap thread, before the port is released.
            // The timer never waits on this thread, so this cannot deadlock.
            using (var stopped = new ManualResetEvent(false))
                if (_livenessTimer.Dispose(stopped)) stopped.WaitOne(TimeSpan.FromSeconds(2));
            Remove(runLoop);
            _log.Info($"Event tap removed ({_name}).");
            if (_deniedLine is { } denied)
            {
                _log.Error(denied);
                try
                {
                    _lost?.Invoke();
                }
                catch (Exception ex)
                {
                    _log.Error($"Event tap ({_name}): telling the shell it was removed failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.Error($"Event tap ({_name}) installation failed: {ex.Message}");
        }
        finally
        {
            if (self.IsAllocated) self.Free();
            ready.Set(); // no-op when already set; releases Start on every failure path
        }
    }

    /// <summary>Creates the tap and adds it to this thread's run loop; false when macOS refused it. Tap thread only.</summary>
    private bool Install(nint runLoop, nint callback, nint userInfo)
    {
        var port = CG.EventTapCreate(CG.SessionEventTap, CG.HeadInsertEventTap, CG.TapOptionDefault, TapEvents.Mask(_events!.Mouse), callback, userInfo);
        if (port == 0) return false;
        _source = CF.MachPortCreateRunLoopSource(0, port, 0);
        CF.RunLoopAddSource(runLoop, _source, CF.RunLoopCommonModes);
        CG.EventTapEnable(port, true);
        Volatile.Write(ref _port, port);
        return true;
    }

    /// <summary>Disables, detaches, invalidates and releases the tap, if there is one. Tap thread only.</summary>
    private void Remove(nint runLoop)
    {
        var port = Volatile.Read(ref _port);
        if (port == 0) return;
        CG.EventTapEnable(port, false);
        CF.RunLoopRemoveSource(runLoop, _source, CF.RunLoopCommonModes);
        CF.MachPortInvalidate(port);
        CF.Release(_source);
        CF.Release(port);
        _source = 0;
        Volatile.Write(ref _port, 0);
    }

    /// <summary>
    /// The CGEventTapCallBack: returns the event to pass it on, or NULL to swallow it. Never lets an exception out (it would
    /// end the process from native code): a fault passes the event on and is logged, the first few times only.
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint Callback(nint proxy, uint type, nint ev, nint userInfo)
    {
        if (GCHandle.FromIntPtr(userInfo).Target is not EventTap tap) return ev;
        try
        {
            return tap.Handle(type, ev) ? ev : 0;
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref tap._faults) <= 3) tap._log.Error($"Event tap ({tap._name}) callback failed; the event was passed on: {ex.Message}");
            return ev;
        }
    }

    /// <summary>Decides one event on the tap thread; internal so a pin drives the disabled path without a real tap (no port: nothing native is called).</summary>
    internal bool Handle(uint type, nint ev)
    {
        if (type is CG.EventTapDisabledByTimeout or CG.EventTapDisabledByUserInput)
        {
            Disabled(type == CG.EventTapDisabledByTimeout ? "timeout" : "user input");
            return true;
        }
        Interlocked.Increment(ref _seen); // every event of the mask's types, posted or not: the liveness count (TapLiveness)
        if (ev == 0) return true; // never from macOS; a pin counts a key this way without building an event
        _events ??= new TapEvents(WantsMouse); // Run makes it before the tap exists; a pin that hands in a real event has no Run
        if (CG.EventGetIntegerValueField(ev, CG.FieldSourceUserData) == InjectedMarker) return true;

        var keyboard = type is CG.EventKeyDown or CG.EventKeyUp or CG.EventFlagsChanged;
        var changes = _events.Decode(
            type,
            keyboard ? CG.EventGetIntegerValueField(ev, CG.FieldKeyboardKeycode) : -1,
            type == CG.EventFlagsChanged ? CG.EventGetFlags(ev) : 0,
            type == CG.EventKeyDown && CG.EventGetIntegerValueField(ev, CG.FieldKeyboardAutorepeat) != 0,
            keyboard ? -1 : CG.EventGetIntegerValueField(ev, CG.FieldMouseButtonNumber));
        if (changes.Length == 0) return true;

        // A hardware event's source state is the HID system state; anything else was posted by a program (recalled, not verified).
        var injected = CG.EventGetIntegerValueField(ev, CG.FieldSourceStateId) != CG.SourceStateHidSystem;
        var passOn = true;
        foreach (var change in changes) passOn &= OnKey(change, injected);
        if (!passOn) _swallowed.Add(1); // counted by HID, received by no app: the session comparison takes it off (TapLiveness)
        return passOn;
    }

    /// <summary>
    /// macOS disabled the tap (tap thread). Turned back on only while the guard allows and the counts say keys still reach it;
    /// keys that stopped reaching it go through the same trigger as the timer's; a tap macOS keeps disabling is removed for good.
    /// </summary>
    private void Disabled(string why)
    {
        if (_stop || Volatile.Read(ref _triggered) != 0) return;
        var now = Environment.TickCount64;
        if (_disables.ShouldReenable(now, _stillTrusted()))
        {
            if (!_liveness.Probe(now, AdjustedHid(), Seen, SessionCount(), _swallowed.Count, _secureInput()))
            {
                var port = Volatile.Read(ref _port);
                if (port != 0) CG.EventTapEnable(port, true);
                _log.Warn($"Event tap ({_name}) was disabled by macOS ({why}) and re-enabled.");
                return;
            }
            var (phrase, missed, _) = _liveness.Tripped;
            Trigger($"macOS disabled it ({why}) and {missed} key events {phrase}", now);
            return;
        }
        // APPROVED by the owner 29/10/2026 23:29 AEDT (Mac request-access block, row 8): do not reword.
        GiveUp($"Event tap ({_name}) removed: macOS keeps disabling it ({why}), which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel).");
    }

    /// <summary>One liveness poll, from the timer (internal so a pin drives it with fake counts). Never waits on the tap thread.</summary>
    /// <param name="nowMs">The clock reading; null is Environment.TickCount64, and only tests pass one.</param>
    internal void LivenessTick(long? nowMs = null)
    {
        if (_stop || Volatile.Read(ref _triggered) != 0 || Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            var now = nowMs ?? Environment.TickCount64;
            var hid = AdjustedHid();
            if (_diagnostics) Diagnose(hid);
            if (!_liveness.Check(now, hid, Seen, SessionCount(), _swallowed.Count, Pause())) return;
            var (phrase, missed, _) = _liveness.Tripped;
            Trigger($"{missed} key events {phrase}", now);
        }
        catch (Exception ex)
        {
            _log.Error($"Event tap ({_name}) liveness check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>The per-tick diagnostic (DICTUM_TAP_DIAG=1): every signal that might flip at a revocation, read live, and a create probe.</summary>
    private void Diagnose(long adjustedHid)
    {
        var port = Volatile.Read(ref _port);
        _log.Info($"Tap diag ({_name}): post={CG.PreflightPostEventAccess()} listen={CG.PreflightListenEventAccess()} ax={AX.IsProcessTrusted()} "
            + $"hid={HidCount()} session={SessionCount()} posted={_posted()} swallowed={_swallowed.Count} adjusted={adjustedHid} seen={Seen} "
            + $"enabled={(port != 0 && CG.EventTapIsEnabled(port))} secure={_secureInput()} disables={_disables.Count} create={(_createProbe() ? "allowed" : "refused")}");
    }

    /// <summary>The diagnostic's probe: a listen-only tap for key-ups, never added to a run loop, released at once. Allowed or refused is the answer.</summary>
    private static unsafe bool CreateProbe()
    {
        delegate* unmanaged<nint, uint, nint, nint, nint> callback = &ProbeCallback;
        var port = CG.EventTapCreate(CG.SessionEventTap, CG.HeadInsertEventTap, CG.TapOptionListenOnly, CG.MaskBit(CG.EventKeyUp), (nint)callback, 0);
        if (port == 0) return false;
        CF.MachPortInvalidate(port);
        CF.Release(port);
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint ProbeCallback(nint proxy, uint type, nint ev, nint userInfo) => ev;

    /// <summary>
    /// Keys stopped reaching the tap, by the timer's count or macOS's disable notice: the tap is disabled at once, from whichever
    /// thread found it, so the keys are free, and the tap thread removes it and asks for a fresh one; a second trigger inside
    /// <see cref="TapRecovery.Window"/> of a fresh tap stays removed and reports the grant gone instead.
    /// </summary>
    private void Trigger(string why, long nowMs)
    {
        if (Interlocked.Exchange(ref _triggered, 1) != 0) return;
        var port = Volatile.Read(ref _port);
        if (port != 0) CG.EventTapEnable(port, false);
        _why = why; // the count and what was measured, already in words
        if (_recovery.MayRecreate(nowMs))
        {
            Volatile.Write(ref _recreate, 1);
        }
        else
        {
            // APPROVED by the owner 29/10/2026 23:29 AEDT (Mac request-access block, row 14): do not reword.
            _deniedLine = $"Event tap ({_name}) removed: keys stopped reaching it again within {TapRecovery.Window.TotalSeconds:0} s of a fresh tap, which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel).";
            _stop = true;
        }
        var runLoop = Volatile.Read(ref _runLoop);
        if (runLoop != 0) CF.RunLoopStop(runLoop);
    }

    /// <summary>
    /// The outcome of asking for a fresh tap (tap thread; internal so a pin drives it without a tap). Refused: the grant is gone,
    /// the thread ends and the shell is told after the removal. Created: a false positive, the counts start again from here, and
    /// the next trigger may fire.
    /// </summary>
    /// <returns>Whether the tap goes on running.</returns>
    internal bool AfterRecreate(bool created, long? nowMs = null)
    {
        Volatile.Write(ref _recreate, 0); // the request is answered (Run has already taken it; a pin calls this directly)
        var now = nowMs ?? Environment.TickCount64;
        if (!created)
        {
            // APPROVED by the owner 29/10/2026 23:29 AEDT (Mac request-access block, row 10): do not reword.
            _deniedLine = $"Event tap ({_name}) removed: {_why}, and macOS refused a fresh tap, which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel).";
            _stop = true;
            return false;
        }
        _recovery.Recreated(now);
        _liveness.Fed(now, AdjustedHid(), Seen, SessionCount(), _swallowed.Count);
        // APPROVED by the owner 29/10/2026 23:44 AEDT, with his rewording (Mac request-access block, row 13): do not reword.
        _log.Info($"Event tap ({_name}) re-created: {_why}, but macOS allowed a fresh tap, which usually means another app or macOS took those keys; the hotkey carries on.");
        Volatile.Write(ref _triggered, 0);
        return true;
    }

    /// <summary>A fresh tap was asked for (for the pins): the timer or the disable notice tripped and the tap thread has yet to act.</summary>
    internal bool RecreateRequested => Volatile.Read(ref _recreate) != 0;

    /// <summary>The line the tap will log, after it is gone, to say the grant is gone (for the pins); null while it runs.</summary>
    internal string? DeniedLine => _deniedLine;

    /// <summary>Removes the tap for good: disabled at once, the run loop stopped so Run removes it, the shell told after. Never re-created.</summary>
    private void GiveUp(string line)
    {
        if (Interlocked.Exchange(ref _triggered, 1) != 0) return;
        var port = Volatile.Read(ref _port);
        if (port != 0) CG.EventTapEnable(port, false);
        _deniedLine = line;
        _stop = true;
        var runLoop = Volatile.Read(ref _runLoop);
        if (runLoop != 0) CF.RunLoopStop(runLoop);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        _stop = true;
        var runLoop = Volatile.Read(ref _runLoop);
        if (runLoop != 0) CF.RunLoopStop(runLoop);
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            if (runLoop != 0) CF.Release(runLoop);
            Volatile.Write(ref _runLoop, 0);
        }
        else
        {
            // The thread still runs (it may be waiting on the timer before it removes the tap): it keeps its own copy of the loop,
            // and the field stays for a later Dispose; the retained loop is leaked rather than released under a live thread.
            _log.Warn($"Event tap thread ({_name}) did not exit within 2 s.");
        }
        _thread = null;
    }
}

/// <summary>
/// The dictation hotkey (and the fix_hotkey): matches keys and mouse buttons, posts signals, swallows the hotkey when the
/// matcher says to. Mouse buttons are in the tap's mask only when the hotkey names one; a hotkey change replaces the whole
/// listener (the shell disposes this one and starts another).
/// </summary>
internal sealed class EventTapHotkey(HotkeyMatcher matcher, Action<HookEvent> post, ILog log, Func<bool>? trusted = null, Action? lost = null,
    Func<bool>? stillTrusted = null, Func<long>? hidCount = null, Func<bool>? secureInput = null, Func<long>? posted = null, Func<long>? sessionCount = null,
    Func<bool>? createProbe = null, bool? diagnostics = null, SwallowedEvents? swallowed = null, Func<DateTime>? wallClock = null)
    : EventTap("hotkey listener", log, trusted, lost, stillTrusted, hidCount, secureInput, posted, sessionCount, createProbe, diagnostics, swallowed, wallClock), IHotkeyListener
{
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    protected override string Describe() => $" for {matcher.Spec.Display}";

    protected override bool WantsMouse => matcher.Spec.UsesMouse;

    protected override bool OnKey(KeyChange change, bool injected) => Decide(matcher, post, change, _enabled);

    /// <summary>
    /// One change through the matcher; returns whether the event passes on. Auto-repeat is fed to the matcher as Windows
    /// feeds it (a second down for a held key), and the matcher already gives a repeat no signal and swallows it when the
    /// press was swallowed. A repeat carries no event time: macOS marks repeats itself, and its longest "delay until
    /// repeat" setting can exceed the matcher's 1.2 s lost-key-up window (recalled), which would read a slow first repeat
    /// as a fresh press. A real press carries the time it was seen.
    /// <para>
    /// <see cref="KeyDecision.NeedsMenuMask"/> is ignored: the menu mask stops a released Win or Alt from opening the
    /// Start menu or a menu bar on Windows; macOS opens nothing when Command or Option is released alone.
    /// </para>
    /// </summary>
    internal static bool Decide(HotkeyMatcher matcher, Action<HookEvent> post, KeyChange change, bool enabled)
    {
        var decision = matcher.OnKey(change.Vk, change.Down, enabled, change.Repeat ? -1 : Environment.TickCount64);
        if (decision.Signal != HotkeySignal.None) post(new HookEvent(decision.Signal, Environment.TickCount64, Stopwatch.GetTimestamp()));
        return decision.PassesThrough;
    }
}

/// <summary>
/// Reports every key and the right and other mouse buttons (never the left) as macOS delivers them and passes all of them
/// on (the hotkey capture box, --selftest).
/// </summary>
internal sealed class EventTapKeyMonitor(Action<RawKeyEvent> onKey, ILog log, Func<bool>? trusted = null, Action? lost = null,
    Func<bool>? stillTrusted = null, Func<long>? hidCount = null, Func<bool>? secureInput = null, Func<long>? posted = null, Func<long>? sessionCount = null,
    Func<bool>? createProbe = null, bool? diagnostics = null, SwallowedEvents? swallowed = null, Func<DateTime>? wallClock = null)
    : EventTap("key monitor", log, trusted, lost, stillTrusted, hidCount, secureInput, posted, sessionCount, createProbe, diagnostics, swallowed, wallClock), IKeyMonitor
{
    protected override bool WantsMouse => true;

    protected override bool OnKey(KeyChange change, bool injected)
    {
        onKey(new RawKeyEvent(change.Vk, change.Down, injected));
        return true;
    }
}

/// <summary>
/// When a tap macOS disabled may be turned back on: only while the silent check still says trusted, and not when macOS disabled
/// it again within <see cref="Window"/>. A healthy tap answers from its own thread, so a second disable that soon means the
/// events are not reaching it (the grant was taken away under it, 29/10/2026), and re-enabling it again would hold every key.
/// The check alone cannot decide: after a live revocation it went on answering trusted (dictum-20261029.log, 08:10:24).
/// </summary>
internal sealed class TapDisableGuard
{
    /// <summary>
    /// simplification: a fixed count in a fixed window. Ceiling: a healthy tap that a heavily loaded Mac disables twice within a
    /// minute is removed too; the shell then shows the line and the button, and its next grant check or Request access installs
    /// a fresh listener. Trigger to retire: a removal seen in a log with the grant still in place.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private long _lastMs = long.MinValue;
    private int _count;

    /// <summary>How many times macOS has disabled the tap (the diagnostic line).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <param name="nowMs">Environment.TickCount64 now.</param>
    /// <param name="trusted">What the silent Accessibility check answers now.</param>
    public bool ShouldReenable(long nowMs, bool trusted)
    {
        Interlocked.Increment(ref _count);
        var repeated = _lastMs != long.MinValue && nowMs - _lastMs <= (long)Window.TotalMilliseconds;
        _lastMs = nowMs;
        return trusted && !repeated;
    }
}

/// <summary>The Accessibility trust check the taps run before creating a tap: macOS's own prompt is asked for once per process, later checks are silent.</summary>
internal static class Accessibility
{
    private static int _prompted;

    public static bool EnsureTrusted() =>
        Interlocked.Exchange(ref _prompted, 1) == 0 ? AX.IsProcessTrustedPrompting() : AX.IsProcessTrusted();
}
