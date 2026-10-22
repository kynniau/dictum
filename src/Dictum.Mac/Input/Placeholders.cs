// PLACEHOLDER: replaced by the input lane (docs/MAC-HANDOVER.md §5). Nothing here ships.
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Platform;

namespace Dictum.Mac.Input;

internal sealed class EventTapHotkey(HotkeyMatcher matcher, Action<HookEvent> post, ILog log) : IHotkeyListener
{
    public bool Enabled { get; set; } = true;
    public bool Start() => throw new NotImplementedException("input lane");
    public void Dispose() { }
}

internal sealed class EventTapKeyMonitor(Action<RawKeyEvent> onKey, ILog log) : IKeyMonitor
{
    public bool Start() => throw new NotImplementedException("input lane");
    public void Dispose() { }
}

internal static class KeyState
{
    public static bool IsDown(int vk) => throw new NotImplementedException("input lane");
}

internal sealed class CGEventSender(ILog log) : IInputSender
{
    public void ReleaseModifiers() => throw new NotImplementedException("input lane");
    public void SendCtrlV() => throw new NotImplementedException("input lane");
    public Task TypeTextAsync(string text) => throw new NotImplementedException("input lane");
}

internal sealed class Pasteboard : IClipboard, IDisposable
{
    public ClipboardSnapshot Snapshot() => throw new NotImplementedException("input lane");
    public void SetText(string text, bool keepOutOfHistory) => throw new NotImplementedException("input lane");
    public void Restore(ClipboardSnapshot snapshot) => throw new NotImplementedException("input lane");
    public void Dispose() { }
}
