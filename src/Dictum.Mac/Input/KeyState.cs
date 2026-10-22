using Dictum.Hotkey;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Input;

/// <summary>
/// Whether a key or mouse button is down now, from the combined session state (CGEventSourceKeyState and
/// CGEventSourceButtonState): the matcher's probe for modifiers whose key-up it missed, and the input sender's check for
/// held modifiers. The generic Ctrl, Shift and Alt codes count either side, as GetAsyncKeyState does on Windows.
/// </summary>
internal static class KeyState
{
    public static bool IsDown(int vk)
    {
        switch (vk)
        {
            case Vk.Control: return IsDown(Vk.LControl) || IsDown(Vk.RControl);
            case Vk.Shift: return IsDown(Vk.LShift) || IsDown(Vk.RShift);
            case Vk.Menu: return IsDown(Vk.LMenu) || IsDown(Vk.RMenu);
        }
        var button = KeyCodeMap.VkToButton(vk);
        if (button >= 0) return CG.EventSourceButtonState(CG.SourceStateCombinedSession, (uint)button);
        var mac = KeyCodeMap.ToMac(vk);
        return mac >= 0 && IsMacKeyDown((ushort)mac);
    }

    /// <summary>Whether the key with this Mac key code is down now.</summary>
    public static bool IsMacKeyDown(ushort macKeyCode) => CG.EventSourceKeyState(CG.SourceStateCombinedSession, macKeyCode);
}
