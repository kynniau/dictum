using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Platform;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Input;

/// <summary>
/// A WH_KEYBOARD_LL hook, plus a WH_MOUSE_LL hook when a mouse button is part of what it listens for (decided once, when
/// it starts), on a dedicated thread with its own message loop (SPEC §2). Why a dedicated thread: if a low-level hook does
/// not return within LowLevelHooksTimeout (HKCU\Control Panel\Desktop, max 1000 ms since Windows 10 1709) "the hook is
/// silently removed without being called" — https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc,
/// and the same words on lowlevelmouseproc (both read 16/10/2026). The UI thread can be busy for longer than that; this
/// thread does nothing but pump the hooks. Both hooks are "called in the context of the thread that installed it", so one
/// matcher serves keys and buttons with no locking and a chord such as Ctrl+Mouse4 needs nothing special. The callbacks
/// only match and post; they never block and never touch the UI. The mouse hook decodes button messages only (moves and
/// wheel turns are passed on without even reading the struct) and never the left button, which can never be a hotkey
/// (HotkeyParser).
/// </summary>
internal abstract class LowLevelHook : IDisposable
{
    private const uint WmMenuMask = Native.WM_APP + 1;

    private readonly ILog _log;
    private readonly string _name;
    private readonly Native.HookProc _keyboardProc; // kept in fields so the GC cannot collect the delegates
    private readonly Native.HookProc _mouseProc;
    private Thread? _thread;
    private uint _threadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private bool _mouseFailed;

    protected LowLevelHook(string name, ILog log)
    {
        _name = name;
        _log = log;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>
    /// Starts the hook thread and waits until the hooks are installed. False if the keyboard hook failed, or the mouse hook
    /// was wanted, is <see cref="MouseRequired"/> and failed (a mouse-button hotkey would never fire).
    /// </summary>
    public bool Start()
    {
        if (_thread is not null) return Installed;
        using var ready = new ManualResetEventSlim();
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

    private bool Installed => _keyboardHook != 0 && !(_mouseFailed && MouseRequired);

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, Native.GetModuleHandle(null), 0);
        if (_keyboardHook == 0)
        {
            _log.Error($"Input hook ({_name}) installation failed (Win32 error {Marshal.GetLastWin32Error()}).");
            ready.Set();
            return;
        }
        _log.Info($"Input hook installed on thread {_threadId} ({_name}{Describe()}).");
        ApplyMouse();
        ready.Set();

        // simplification: no automatic re-install if Windows silently removes a hook after a timeout.
        // Ceiling: after a removal the hotkey is dead until Settings > About > Reload settings or a restart. Trigger to
        // retire: any report of the hotkey dying under load, or a log showing hook events stop while the app runs.
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WmMenuMask) SendMenuMask();
        }

        if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        Native.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = 0;
        _keyboardHook = 0;
        _log.Info($"Input hook removed ({_name}).");
    }

    /// <summary>For the log line: what this hook listens for.</summary>
    protected virtual string Describe() => "";

    /// <summary>Whether this hook watches mouse buttons too; read once, on the hook thread, when it starts.</summary>
    protected abstract bool WantsMouse { get; }

    /// <summary>Whether a refused mouse hook makes <see cref="Start"/> fail; when false it is only logged.</summary>
    protected virtual bool MouseRequired => true;

    /// <summary>Decides one key or button event on the hook thread. Returns whether to pass it on, and whether a menu mask is needed.</summary>
    protected abstract (bool PassOn, bool MenuMask) OnKey(int vk, bool isDown, uint time, bool injected);

    /// <summary>On the hook thread, once at start: installs the mouse hook when <see cref="WantsMouse"/>.</summary>
    private void ApplyMouse()
    {
        if (!WantsMouse) return;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
        _mouseFailed = _mouseHook == 0;
        if (!_mouseFailed) _log.Info($"Mouse hook installed ({_name}): mouse buttons are watched.");
        else if (MouseRequired) _log.Error($"Mouse hook ({_name}) installation failed (Win32 error {Marshal.GetLastWin32Error()}); a mouse-button hotkey will not fire.");
        else _log.Warn($"Mouse hook ({_name}) installation failed (Win32 error {Marshal.GetLastWin32Error()}); keys are still reported, mouse buttons are not.");
    }

    private nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != Native.InjectedMarker)
            {
                var msg = (int)wParam;
                var isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                if (!Decide((int)data.vkCode, isDown, data.time, (data.flags & Native.LLKHF_INJECTED) != 0)) return 1;
            }
        }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && Button((int)wParam, lParam, out var vk, out var isDown, out var data) && data.dwExtraInfo != Native.InjectedMarker)
        {
            if (!Decide(vk, isDown, data.time, (data.flags & Native.LLMHF_INJECTED) != 0)) return 1;
        }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Runs <see cref="OnKey"/> and queues the menu mask when asked; false when the event is to be swallowed.</summary>
    private bool Decide(int vk, bool isDown, uint time, bool injected)
    {
        var (passOn, menuMask) = OnKey(vk, isDown, time, injected);
        // Deferred to this thread's message loop so the callback itself stays match-and-post only.
        if (menuMask) Native.PostThreadMessage(_threadId, WmMenuMask, 0, 0);
        return passOn;
    }

    /// <summary>
    /// The button a mouse message is about, as the virtual key the parser uses, with the event's data. False for moves,
    /// wheel turns and the left button, which are passed on without reading the struct, and for a side button that is
    /// neither XBUTTON1 nor XBUTTON2.
    /// </summary>
    internal static bool Button(int message, nint lParam, out int vk, out bool isDown, out Native.MSLLHOOKSTRUCT data)
    {
        (vk, isDown) = message switch
        {
            Native.WM_RBUTTONDOWN => (Vk.RButton, true),
            Native.WM_RBUTTONUP => (Vk.RButton, false),
            Native.WM_MBUTTONDOWN => (Vk.MButton, true),
            Native.WM_MBUTTONUP => (Vk.MButton, false),
            Native.WM_XBUTTONDOWN => (0, true),
            Native.WM_XBUTTONUP => (0, false),
            _ => (-1, false),
        };
        if (vk < 0)
        {
            data = default;
            return false;
        }
        data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        if (vk == 0)
        {
            // Which side button: the high word of mouseData (XBUTTON1 or XBUTTON2).
            vk = (ushort)(data.mouseData >> 16) switch { Native.XBUTTON1 => Vk.XButton1, Native.XBUTTON2 => Vk.XButton2, _ => -1 };
        }
        return vk > 0;
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
        if (!_thread.Join(TimeSpan.FromSeconds(2))) _log.Warn($"Input hook thread ({_name}) did not exit within 2 s.");
        _thread = null;
    }
}

/// <summary>
/// The dictation hotkey (and the fix_hotkey): matches keys and mouse buttons, posts signals, swallows the hotkey when told
/// to. The mouse hook is installed only when the hotkey names a mouse button; a hotkey change replaces the whole listener
/// (the shell disposes this one and starts another).
/// </summary>
internal sealed class InputHook(HotkeyMatcher matcher, Action<HookEvent> post, ILog log) : LowLevelHook("hotkey listener", log), IHotkeyListener
{
    private readonly HotkeyMatcher _matcher = matcher;
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    protected override string Describe() => $" for {_matcher.Spec.Display}";

    protected override bool WantsMouse => _matcher.Spec.UsesMouse;

    protected override (bool PassOn, bool MenuMask) OnKey(int vk, bool isDown, uint time, bool injected)
    {
        var decision = _matcher.OnKey(vk, isDown, _enabled, time);
        if (decision.Signal != HotkeySignal.None) post(new HookEvent(decision.Signal, Environment.TickCount64, Stopwatch.GetTimestamp()));
        return (decision.PassesThrough, decision.NeedsMenuMask);
    }
}

/// <summary>
/// Reports every key and mouse button (never the left one) exactly as Windows delivers it and passes all of them on (the
/// hotkey capture box, --selftest). Short-lived, so it always installs the mouse hook, best-effort: if Windows refuses it,
/// the refusal is logged and keys are still reported (Start succeeds).
/// </summary>
internal sealed class KeyMonitor(Action<RawKeyEvent> onKey, ILog log) : LowLevelHook("key monitor", log), IKeyMonitor
{
    protected override bool WantsMouse => true;

    protected override bool MouseRequired => false;

    protected override (bool PassOn, bool MenuMask) OnKey(int vk, bool isDown, uint time, bool injected)
    {
        onKey(new RawKeyEvent(vk, isDown, injected));
        return (true, false);
    }
}
