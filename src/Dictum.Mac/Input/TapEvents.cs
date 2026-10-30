using Dictum.Hotkey;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Input;

/// <summary>One key or button going down or up, as the matcher and the key monitor see it.</summary>
/// <param name="Repeat">The system's auto-repeat of a held key (kCGKeyboardEventAutorepeat).</param>
internal readonly record struct KeyChange(int Vk, bool Down, bool Repeat = false);

/// <summary>
/// The pure half of the event tap: turns one Core Graphics event (its type and the few fields read from it) into the key
/// changes it means, with no native call, so every rule is unit-tested on any OS. Not thread-safe: one instance per tap,
/// used only on the tap's thread.
/// </summary>
internal sealed class TapEvents(bool mouse)
{
    private static readonly KeyChange[] None = [];
    private readonly bool[] _modifierDown = new bool[128];

    /// <summary>Whether non-left mouse buttons are decoded (the tap's mask asks for them only then).</summary>
    public bool Mouse { get; } = mouse;

    /// <summary>The event types a tap asks for: keys and modifier changes, plus the right and other buttons when <paramref name="mouse"/>. Never the left button.</summary>
    public static ulong Mask(bool mouse)
    {
        ulong mask = 0;
        foreach (var type in Types(mouse)) mask |= CG.MaskBit(type);
        return mask;
    }

    /// <summary>The event types a tap asks for: the mask, and the HID counts the liveness check compares (never mouse moves, which the tap never sees).</summary>
    public static uint[] Types(bool mouse) => mouse
        ? [CG.EventKeyDown, CG.EventKeyUp, CG.EventFlagsChanged, CG.EventRightMouseDown, CG.EventRightMouseUp, CG.EventOtherMouseDown, CG.EventOtherMouseUp]
        : [CG.EventKeyDown, CG.EventKeyUp, CG.EventFlagsChanged];

    /// <summary>The key changes one event means; empty for events with nothing to report (Fn, an unmapped key, the left button).</summary>
    public KeyChange[] Decode(uint type, long keyCode, ulong flags, bool autorepeat, long buttonNumber)
    {
        switch (type)
        {
            case CG.EventKeyDown:
            case CG.EventKeyUp:
                var vk = KeyCodeMap.ToVk(keyCode);
                return vk == 0 ? None : [new KeyChange(vk, type == CG.EventKeyDown, type == CG.EventKeyDown && autorepeat)];
            case CG.EventFlagsChanged:
                return Modifier(keyCode, flags);
            case CG.EventRightMouseDown or CG.EventRightMouseUp when Mouse:
                return [new KeyChange(Vk.RButton, type == CG.EventRightMouseDown)];
            case CG.EventOtherMouseDown or CG.EventOtherMouseUp when Mouse:
                var button = KeyCodeMap.ButtonToVk(buttonNumber);
                return button is 0 or Vk.RButton ? None : [new KeyChange(button, type == CG.EventOtherMouseDown)];
            default:
                return None;
        }
    }

    /// <summary>
    /// A modifier key arrives as one flagsChanged event carrying that key's code and the flags after the change. Whether it
    /// went down or up is read from its device-dependent bit (NX_DEVICE*KEYMASK: one bit per side), so Right Option
    /// pressed while Left Option is held still reports Right Option down, and its release reports it up. An event without
    /// device bits for that modifier (some programs post only the generic mask) toggles the key instead. Each key reports
    /// only real changes: a repeated flagsChanged for a key already down reports nothing.
    /// </summary>
    private KeyChange[] Modifier(long keyCode, ulong flags)
    {
        if (keyCode == KeyCodeMap.MacCapsLock)
        {
            // simplification: Caps Lock reports a press and a release together on every flagsChanged it sends. Ceiling: macOS
            // sends Caps Lock as the lock toggling (recalled: one event per press, none on release), so Caps Lock can be
            // tapped but not held as a hold-to-talk hotkey on a Mac, and if a macOS version sends an event on release too, a
            // hold reads as two taps. Trigger to retire: a test on real hardware showing what macOS sends for Caps Lock.
            return [new KeyChange(Vk.Capital, true), new KeyChange(Vk.Capital, false)];
        }
        if (keyCode is < 0 or >= 128) return None;
        var (device, bothSides, generic) = keyCode switch
        {
            KeyCodeMap.MacControl => (CG.DeviceLControl, CG.DeviceLControl | CG.DeviceRControl, CG.FlagControl),
            KeyCodeMap.MacRightControl => (CG.DeviceRControl, CG.DeviceLControl | CG.DeviceRControl, CG.FlagControl),
            KeyCodeMap.MacShift => (CG.DeviceLShift, CG.DeviceLShift | CG.DeviceRShift, CG.FlagShift),
            KeyCodeMap.MacRightShift => (CG.DeviceRShift, CG.DeviceLShift | CG.DeviceRShift, CG.FlagShift),
            KeyCodeMap.MacOption => (CG.DeviceLAlternate, CG.DeviceLAlternate | CG.DeviceRAlternate, CG.FlagAlternate),
            KeyCodeMap.MacRightOption => (CG.DeviceRAlternate, CG.DeviceLAlternate | CG.DeviceRAlternate, CG.FlagAlternate),
            KeyCodeMap.MacCommand => (CG.DeviceLCommand, CG.DeviceLCommand | CG.DeviceRCommand, CG.FlagCommand),
            KeyCodeMap.MacRightCommand => (CG.DeviceRCommand, CG.DeviceLCommand | CG.DeviceRCommand, CG.FlagCommand),
            _ => (0UL, 0UL, 0UL), // Fn and anything else: no virtual key (KeyCodeMap)
        };
        if (device == 0) return None;

        var wasDown = _modifierDown[keyCode];
        bool down;
        if ((flags & device) != 0) down = true;
        else if ((flags & bothSides) == 0 && (flags & generic) != 0) down = !wasDown; // no device bits at all: toggle
        else down = false;
        if (down == wasDown) return None;
        _modifierDown[keyCode] = down;
        return [new KeyChange(KeyCodeMap.ToVk(keyCode), down)];
    }
}
