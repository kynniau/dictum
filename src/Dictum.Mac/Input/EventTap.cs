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
/// <see cref="TapDisableGuard"/> and a <see cref="TapLiveness"/> probe: a tap macOS keeps disabling, or one the keys no longer
/// reach, is removed instead, because a tap left in place after the grant was taken away held every key on the Mac (PR #82
/// round 2, 29/10/2026). A timer off the tap thread runs the same liveness check every <see cref="TapLiveness.Tick"/>, since macOS
/// held the keys for about 50 s before its first disable notice; it never waits on the tap thread.
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

    /// <summary>The user-data value on the liveness probe ("DICU"): the tap counts it and swallows it, so no app ever sees it.</summary>
    public const long ProbeMarker = 0x44494355;

    /// <summary>
    /// simplification: how long the timer waits for its probe, which passes through every tap ahead of Dictum (Siri's active
    /// session key tap showed a 395 ms worst latency on the owner's Mac, 29/10/2026). Ceiling: a tap ahead slower than 500 ms makes a
    /// fed tap read as starved and removed (Request access installs a fresh one). Trigger to retire: a probe latency over 250 ms in a
    /// log (the "kept" line reports them). Orchestrator, 29/10/2026 22:05 AEDT.
    /// </summary>
    internal static readonly TimeSpan ProbeWait = TimeSpan.FromMilliseconds(500);

    private readonly ILog _log;
    private readonly string _name;
    private readonly Func<bool> _trusted;
    private readonly Func<bool> _stillTrusted;
    private readonly Action? _lost;
    private readonly TapDisableGuard _disables = new();
    private readonly TapLiveness _liveness = new();
    private readonly Func<long>? _hidCount;
    private readonly Func<bool> _secureInput;
    private readonly Func<bool> _postProbe;
    private Timer? _livenessTimer;
    private long _seen;
    private long _probesSeen;
    private long _keptLoggedMs = long.MinValue;
    private int _probesSinceLine;
    private long _slowestSinceLineMs;
    private int _gaveUp;
    private int _ticking;
    private Thread? _thread;
    private nint _runLoop;
    private nint _port;
    private volatile bool _stop;
    private TapEvents? _events;
    private int _faults;

    /// <param name="trusted">Whether the process may run an active tap; the default asks macOS and prompts once (<see cref="Accessibility"/>). Tests pass their own.</param>
    /// <param name="lost">Told, on the tap thread, when the tap removed itself because macOS kept disabling it (IInputAccess.Revoked).</param>
    /// <param name="stillTrusted">The silent check read when macOS disables the tap; the default is AXIsProcessTrusted. Tests pass their own.</param>
    /// <param name="hidCount">Events of the tap's types the HID system has seen; the default sums CGEventSourceCounterForEventType over the mask. Tests pass their own.</param>
    /// <param name="secureInput">Whether Secure Event Input is on; the default is IsSecureEventInputEnabled. Tests pass their own.</param>
    /// <param name="postProbe">Posts one liveness probe and says whether it was posted; the default posts the real event (<see cref="PostProbe"/>). Tests pass their own.</param>
    protected EventTap(string name, ILog log, Func<bool>? trusted = null, Action? lost = null, Func<bool>? stillTrusted = null,
        Func<long>? hidCount = null, Func<bool>? secureInput = null, Func<bool>? postProbe = null)
    {
        _postProbe = postProbe ?? PostProbe;
        _name = name;
        _log = log;
        _trusted = trusted ?? Accessibility.EnsureTrusted;
        _lost = lost;
        _stillTrusted = stillTrusted ?? AX.IsProcessTrusted;
        _hidCount = hidCount;
        _secureInput = secureInput ?? HIToolbox.IsSecureEventInputEnabled;
    }

    /// <summary>The HID system's count of the event types in this tap's mask (CGEventSourceCounterForEventType, HID system state).</summary>
    private long HidCount()
    {
        if (_hidCount is not null) return _hidCount();
        long total = 0;
        foreach (var type in TapEvents.Types(_events is { Mouse: true })) total += CG.EventSourceCounterForEventType(CG.SourceStateHidSystem, type);
        return total;
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

    private bool Installed => _port != 0;

    /// <summary>For the log line: what this tap listens for.</summary>
    protected virtual string Describe() => "";

    /// <summary>Whether this tap watches the right and other mouse buttons too; read once, on the tap thread, when it starts.</summary>
    protected abstract bool WantsMouse { get; }

    /// <summary>Decides one key or button change on the tap thread. Returns whether to pass the event on.</summary>
    protected abstract bool OnKey(KeyChange change, bool injected);

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
            // Retained so Dispose can still stop it, or find it already finished, after this thread has ended.
            _runLoop = CF.Retain(CF.RunLoopGetCurrent());
            _events = new TapEvents(WantsMouse);
            self = GCHandle.Alloc(this);
            delegate* unmanaged<nint, uint, nint, nint, nint> callback = &Callback;
            var port = CG.EventTapCreate(CG.SessionEventTap, CG.HeadInsertEventTap, CG.TapOptionDefault, TapEvents.Mask(_events.Mouse), (nint)callback, GCHandle.ToIntPtr(self));
            if (port == 0)
            {
                _log.Error($"Event tap ({_name}) installation failed: macOS refused it (CGEventTapCreate returned null) although Accessibility reports this app as allowed.");
                return;
            }
            var source = CF.MachPortCreateRunLoopSource(0, port, 0);
            CF.RunLoopAddSource(_runLoop, source, CF.RunLoopCommonModes);
            CG.EventTapEnable(port, true);
            _port = port;
            _log.Info($"Event tap installed on thread {Environment.CurrentManagedThreadId} ({_name}{Describe()}{(_events.Mouse ? ", mouse buttons watched" : "")}).");
            _livenessTimer = new Timer(_ => LivenessTick(), null, TapLiveness.Tick, TapLiveness.Tick);
            ready.Set();

            // A timed run, not CFRunLoopRun: a stop that arrives before the loop starts running would otherwise be lost and
            // the thread never end. Dispose sets _stop and stops the loop, so the wait is normally far shorter than a second.
            while (!_stop) CF.RunLoopRunInMode(CF.RunLoopDefaultMode, 1.0, false);

            // The timer may be in a tick that calls CGEventTapEnable on the port: wait for it here, on the tap thread, before the port
            // is released. The timer never waits on this thread, so this cannot deadlock.
            using (var stopped = new ManualResetEvent(false))
                if (_livenessTimer is { } timer && timer.Dispose(stopped)) stopped.WaitOne(TimeSpan.FromSeconds(2));
            CG.EventTapEnable(port, false);
            CF.RunLoopRemoveSource(_runLoop, source, CF.RunLoopCommonModes);
            CF.MachPortInvalidate(port);
            CF.Release(source);
            CF.Release(port);
            _port = 0;
            _log.Info($"Event tap removed ({_name}).");
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
        var userData = CG.EventGetIntegerValueField(ev, CG.FieldSourceUserData);
        if (userData == ProbeMarker)
        {
            ProbeArrived();
            return false; // our own probe: swallowed, no app sees it
        }
        Interlocked.Increment(ref _seen); // every other event of the mask's types, posted or not: the liveness count (TapLiveness)
        if (_events is null || userData == InjectedMarker) return true;

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
        return passOn;
    }

    /// <summary>
    /// macOS disabled the tap. Re-enabled while the guard allows; otherwise the tap is never enabled again: the run loop is
    /// stopped (Run then removes the tap), and the shell is told so it shows the line and the Request access button and replaces
    /// the listeners once the grant is back.
    /// </summary>
    private void Disabled(string why)
    {
        if (Volatile.Read(ref _gaveUp) != 0) return;
        var now = Environment.TickCount64;
        if (_disables.ShouldReenable(now, _stillTrusted()))
        {
            if (!_liveness.Probe(now, HidCount(), Interlocked.Read(ref _seen), _secureInput()))
            {
                if (_port != 0) CG.EventTapEnable(_port, true);
                _log.Warn($"Event tap ({_name}) was disabled by macOS ({why}) and re-enabled.");
                return;
            }
            // PROPOSED (log line, PR #82 round 2, 29/10/2026): awaiting owner approval.
            GiveUp($"Event tap ({_name}) removed: macOS disabled it ({why}) and keys had stopped reaching it, which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel).");
            return;
        }
        // PROPOSED (log line, PR #82 round 2, 29/10/2026): awaiting owner approval.
        GiveUp($"Event tap ({_name}) removed: macOS keeps disabling it ({why}), which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel).");
    }

    /// <summary>One liveness poll, from the timer (internal so a pin drives it with fake counts). Never waits on the tap thread.</summary>
    /// <param name="nowMs">The clock reading; null is Environment.TickCount64, and only tests pass one.</param>
    internal void LivenessTick(long? nowMs = null)
    {
        if (Volatile.Read(ref _gaveUp) != 0 || Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            var now = nowMs ?? Environment.TickCount64;
            var hid = HidCount();
            if (!_liveness.Check(now, hid, Interlocked.Read(ref _seen), _secureInput())) return;
            // The counts say keys went by unseen. Starved, or taken by another tap or by macOS? Only a probe of our own can tell.
            var before = Interlocked.Read(ref _probesSeen);
            if (_postProbe() && ProbeAnswered(before) is { } latencyMs)
            {
                var (missed, since) = _liveness.MissedSince(hid);
                _liveness.Fed(now, HidCount(), Interlocked.Read(ref _seen));
                _probesSinceLine++;
                _slowestSinceLineMs = Math.Max(_slowestSinceLineMs, latencyMs);
                if (_keptLoggedMs == long.MinValue || now - _keptLoggedMs >= 60_000)
                {
                    _keptLoggedMs = now;
                    // PROPOSED (log line, PR #82 liveness round, 29/10/2026): awaiting owner approval. At most once a minute.
                    _log.Info($"Event tap ({_name}) kept: {missed} key events reached macOS in the last {(now - since) / 1000.0:0.0} s without reaching it, but its probe arrived in {latencyMs} ms, so another app or macOS took those keys. Probes since the last such line: {_probesSinceLine}, slowest {_slowestSinceLineMs} ms.");
                    (_probesSinceLine, _slowestSinceLineMs) = (0, 0);
                }
                return;
            }
            // PROPOSED (log line, PR #82 round 2, 29/10/2026): awaiting owner approval.
            GiveUp($"Event tap ({_name}) removed: keys reached macOS but not Dictum for over a second, which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel).");
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

    /// <summary>The tap's callback saw its own probe (internal so a pin stands in for the callback).</summary>
    internal void ProbeArrived() => Interlocked.Increment(ref _probesSeen);

    /// <summary>Waits, on the timer thread, up to <see cref="ProbeWait"/> for the probe to reach the callback: its round trip in ms, or null.</summary>
    private long? ProbeAnswered(long before)
    {
        var start = Environment.TickCount64;
        while (Interlocked.Read(ref _probesSeen) == before)
        {
            if (Environment.TickCount64 - start >= (long)ProbeWait.TotalMilliseconds) return null;
            Thread.Sleep(5);
        }
        return Environment.TickCount64 - start;
    }

    /// <summary>Events of the mask's types the callback has counted, probes excluded (for the pins).</summary>
    internal long Seen => Interlocked.Read(ref _seen);

    /// <summary>
    /// One probe: a flagsChanged event carrying the modifier flags already held (so it changes nothing) and <see cref="ProbeMarker"/>,
    /// posted where Dictum's paste is posted, so it passes through this tap like a real key. False when it could not be made.
    /// </summary>
    private static bool PostProbe()
    {
        var ev = CG.EventCreate(0);
        if (ev == 0) return false;
        try
        {
            CG.EventSetType(ev, CG.EventFlagsChanged);
            CG.EventSetFlags(ev, CG.EventSourceFlagsState(CG.SourceStateHidSystem));
            CG.EventSetIntegerValueField(ev, CG.FieldSourceUserData, ProbeMarker);
            CG.EventPost(CG.HidEventTap, ev);
            return true;
        }
        finally
        {
            CF.Release(ev);
        }
    }

    /// <summary>
    /// Removes the tap for good, from whichever thread found it dead: disabled at once (from the timer too, so the keys are let go
    /// even while the tap thread is stuck), the run loop stopped so Run invalidates and releases it, and the shell told
    /// (IInputAccess.Revoked). Never re-created here: the shell installs a fresh listener through its own paths.
    /// </summary>
    private void GiveUp(string line)
    {
        if (Interlocked.Exchange(ref _gaveUp, 1) != 0) return;
        var port = _port;
        if (port != 0) CG.EventTapEnable(port, false);
        _log.Error(line);
        _stop = true;
        if (_runLoop != 0) CF.RunLoopStop(_runLoop);
        try
        {
            _lost?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"Event tap ({_name}): telling the shell it was removed failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_thread is null) return;
        _stop = true;
        if (_runLoop != 0) CF.RunLoopStop(_runLoop);
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            if (_runLoop != 0) CF.Release(_runLoop);
        }
        else
        {
            _log.Warn($"Event tap thread ({_name}) did not exit within 2 s.");
        }
        _runLoop = 0;
        _thread = null;
    }
}

/// <summary>
/// The dictation hotkey (and the fix_hotkey): matches keys and mouse buttons, posts signals, swallows the hotkey when the
/// matcher says to. Mouse buttons are in the tap's mask only when the hotkey names one; a hotkey change replaces the whole
/// listener (the shell disposes this one and starts another).
/// </summary>
internal sealed class EventTapHotkey(HotkeyMatcher matcher, Action<HookEvent> post, ILog log, Func<bool>? trusted = null, Action? lost = null,
    Func<bool>? stillTrusted = null, Func<long>? hidCount = null, Func<bool>? secureInput = null, Func<bool>? postProbe = null)
    : EventTap("hotkey listener", log, trusted, lost, stillTrusted, hidCount, secureInput, postProbe), IHotkeyListener
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
    Func<bool>? stillTrusted = null, Func<long>? hidCount = null, Func<bool>? secureInput = null, Func<bool>? postProbe = null)
    : EventTap("key monitor", log, trusted, lost, stillTrusted, hidCount, secureInput, postProbe), IKeyMonitor
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

    /// <param name="nowMs">Environment.TickCount64 now.</param>
    /// <param name="trusted">What the silent Accessibility check answers now.</param>
    public bool ShouldReenable(long nowMs, bool trusted)
    {
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
