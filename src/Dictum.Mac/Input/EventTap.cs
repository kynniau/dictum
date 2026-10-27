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
/// that is slow to answer and tells it with kCGEventTapDisabledByTimeout, and the tap re-enables itself then.
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

    private readonly ILog _log;
    private readonly string _name;
    private readonly Func<bool> _trusted;
    private Thread? _thread;
    private nint _runLoop;
    private nint _port;
    private volatile bool _stop;
    private TapEvents? _events;
    private int _faults;

    /// <param name="trusted">Whether the process may run an active tap; the default asks macOS and prompts once (<see cref="Accessibility"/>). Tests pass their own.</param>
    protected EventTap(string name, ILog log, Func<bool>? trusted = null)
    {
        _name = name;
        _log = log;
        _trusted = trusted ?? Accessibility.EnsureTrusted;
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
                // PROPOSED (log line, 22/10/2026; reworded 27/10/2026: no relaunch is needed once the grant arrives, the shell reinstalls the tap).
                _log.Error($"Event tap ({_name}) not installed: Dictum is not allowed to control this Mac yet. Press Request access in Settings > General (or the menu-bar panel) and turn Dictum on in the System Settings page that opens; the hotkey starts working as soon as it is on.");
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
            ready.Set();

            // A timed run, not CFRunLoopRun: a stop that arrives before the loop starts running would otherwise be lost and
            // the thread never end. Dispose sets _stop and stops the loop, so the wait is normally far shorter than a second.
            while (!_stop) CF.RunLoopRunInMode(CF.RunLoopDefaultMode, 1.0, false);

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

    private bool Handle(uint type, nint ev)
    {
        if (type is CG.EventTapDisabledByTimeout or CG.EventTapDisabledByUserInput)
        {
            if (_port != 0) CG.EventTapEnable(_port, true);
            _log.Warn($"Event tap ({_name}) was disabled by macOS ({(type == CG.EventTapDisabledByTimeout ? "timeout" : "user input")}) and re-enabled.");
            return true;
        }
        if (_events is null || CG.EventGetIntegerValueField(ev, CG.FieldSourceUserData) == InjectedMarker) return true;

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
internal sealed class EventTapHotkey(HotkeyMatcher matcher, Action<HookEvent> post, ILog log, Func<bool>? trusted = null)
    : EventTap("hotkey listener", log, trusted), IHotkeyListener
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
internal sealed class EventTapKeyMonitor(Action<RawKeyEvent> onKey, ILog log, Func<bool>? trusted = null)
    : EventTap("key monitor", log, trusted), IKeyMonitor
{
    protected override bool WantsMouse => true;

    protected override bool OnKey(KeyChange change, bool injected)
    {
        onKey(new RawKeyEvent(change.Vk, change.Down, injected));
        return true;
    }
}

/// <summary>The Accessibility trust check the taps run before creating a tap: macOS's own prompt is asked for once per process, later checks are silent.</summary>
internal static class Accessibility
{
    private static int _prompted;

    public static bool EnsureTrusted() =>
        Interlocked.Exchange(ref _prompted, 1) == 0 ? AX.IsProcessTrustedPrompting() : AX.IsProcessTrusted();
}
