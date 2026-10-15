using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Platform;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Input;

/// <summary>
/// WH_KEYBOARD_LL hook on a dedicated thread with its own message loop (SPEC §2).
/// Why a dedicated thread: if a low-level hook does not return within LowLevelHooksTimeout
/// (HKCU\Control Panel\Desktop, max 1000 ms since Windows 10 1709) "the hook is silently removed
/// without being called" — https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc.
/// The UI thread can be busy for longer than that; this thread does nothing but pump the hook.
/// The callback only matches virtual keys and posts; it never blocks and never touches the UI.
/// </summary>
internal abstract class LowLevelHook : IDisposable
{
    private const uint WmMenuMask = Native.WM_APP + 1;

    private readonly ILog _log;
    private readonly string _name;
    private readonly Native.LowLevelKeyboardProc _proc; // kept in a field so the GC cannot collect the delegate
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;

    protected LowLevelHook(string name, ILog log)
    {
        _name = name;
        _log = log;
        _proc = HookProc;
    }

    /// <summary>Starts the hook thread and waits until the hook is installed. Returns false if installation failed.</summary>
    public bool Start()
    {
        if (_thread is not null) return _hook != 0;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = $"Dictum {_name}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return _hook != 0;
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            _log.Error($"Keyboard hook ({_name}) installation failed (Win32 error {Marshal.GetLastWin32Error()}).");
            ready.Set();
            return;
        }
        _log.Info($"Keyboard hook installed on thread {_threadId} ({_name}{Describe()}).");
        ready.Set();

        // simplification: no automatic re-install if Windows silently removes the hook after a timeout.
        // Ceiling: after a removal the hotkey is dead until Settings > About > Reload settings or a restart. Trigger to
        // retire: any report of the hotkey dying under load, or a log showing hook events stop while the app runs.
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WmMenuMask) SendMenuMask();
        }

        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
        _log.Info($"Keyboard hook removed ({_name}).");
    }

    /// <summary>For the log line: what this hook listens for.</summary>
    protected virtual string Describe() => "";

    /// <summary>Decides one key event on the hook thread. Returns whether to pass it on, and whether a menu mask is needed.</summary>
    protected abstract (bool PassOn, bool MenuMask) OnKey(int vk, bool isDown, uint time, bool injected);

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != Native.InjectedMarker)
            {
                var msg = (int)wParam;
                var isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                var (passOn, menuMask) = OnKey((int)data.vkCode, isDown, data.time, (data.flags & Native.LLKHF_INJECTED) != 0);
                if (menuMask)
                {
                    // Deferred to this thread's message loop so the callback itself stays match-and-post only.
                    Native.PostThreadMessage(_threadId, WmMenuMask, 0, 0);
                }
                if (!passOn) return 1;
            }
        }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private static void SendMenuMask()
    {
        var inputs = new Native.INPUT[2];
        for (var i = 0; i < 2; i++)
        {
            inputs[i].type = Native.INPUT_KEYBOARD;
            inputs[i].U.ki = new Native.KEYBDINPUT
            {
                wVk = Vk.MenuMask,
                dwFlags = i == 0 ? 0 : Native.KEYEVENTF_KEYUP,
                dwExtraInfo = Native.InjectedMarker,
            };
        }
        Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    public void Dispose()
    {
        if (_thread is null) return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(2))) _log.Warn($"Keyboard hook thread ({_name}) did not exit within 2 s.");
        _thread = null;
    }
}

/// <summary>The dictation hotkey (and the fix_hotkey): matches, posts signals, swallows the hotkey when told to.</summary>
internal sealed class KeyboardHook(HotkeyMatcher matcher, Action<HookEvent> post, ILog log) : LowLevelHook("keyboard hook", log), IHotkeyListener
{
    private HotkeyMatcher _matcher = matcher;
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void UpdateMatcher(HotkeyMatcher matcher) => Volatile.Write(ref _matcher, matcher);

    protected override string Describe() => $" for hotkey {Volatile.Read(ref _matcher).Spec.Display}";

    protected override (bool PassOn, bool MenuMask) OnKey(int vk, bool isDown, uint time, bool injected)
    {
        var decision = Volatile.Read(ref _matcher).OnKey(vk, isDown, _enabled, time);
        if (decision.Signal != HotkeySignal.None) post(new HookEvent(decision.Signal, Environment.TickCount64, Stopwatch.GetTimestamp()));
        return (decision.PassesThrough, decision.NeedsMenuMask);
    }
}

/// <summary>Reports every key exactly as Windows delivers it and passes all of them on (the hotkey capture box, --selftest).</summary>
internal sealed class KeyMonitor(Action<RawKeyEvent> onKey, ILog log) : LowLevelHook("key monitor", log), IKeyMonitor
{
    protected override (bool PassOn, bool MenuMask) OnKey(int vk, bool isDown, uint time, bool injected)
    {
        onKey(new RawKeyEvent(vk, isDown, injected));
        return (true, false);
    }
}
