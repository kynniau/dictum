using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// Core Graphics event services (CGEvent.h, CGEventSource.h, CGEventTypes.h): the event tap the hotkey and the key monitor
/// run on, key and button state, and the keyboard events the input sender posts. Every constant below was read from the
/// macOS SDK headers on this Mac (Xcode MacOSX.sdk, CoreGraphics.framework/Headers and IOKit hidsystem/IOLLEvent.h, which
/// CGEventTypes.h defines its values from), 22/10/2026. C <c>bool</c> and <c>Boolean</c> are one byte, so every bool
/// crossing here is marshalled as I1.
/// </summary>
internal static partial class CG
{
    private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // CGEventType (CGEventTypes.h; the NX_* values from IOLLEvent.h).
    public const uint EventRightMouseDown = 3, EventRightMouseUp = 4, EventKeyDown = 10, EventKeyUp = 11, EventFlagsChanged = 12;
    public const uint EventOtherMouseDown = 25, EventOtherMouseUp = 26;
    public const uint EventTapDisabledByTimeout = 0xFFFFFFFE, EventTapDisabledByUserInput = 0xFFFFFFFF;

    // CGEventField.
    public const int FieldMouseButtonNumber = 3; // kCGMouseEventButtonNumber
    public const int FieldKeyboardAutorepeat = 8; // kCGKeyboardEventAutorepeat
    public const int FieldKeyboardKeycode = 9; // kCGKeyboardEventKeycode
    public const int FieldSourceUserData = 42; // kCGEventSourceUserData
    public const int FieldSourceStateId = 45; // kCGEventSourceStateID

    // CGEventFlags: the generic masks (NX_*MASK) and the device-dependent left/right bits (NX_DEVICE*KEYMASK).
    public const ulong FlagAlphaShift = 0x00010000, FlagShift = 0x00020000, FlagControl = 0x00040000, FlagAlternate = 0x00080000, FlagCommand = 0x00100000;
    public const ulong DeviceLControl = 0x00000001, DeviceLShift = 0x00000002, DeviceRShift = 0x00000004, DeviceLCommand = 0x00000008;
    public const ulong DeviceRCommand = 0x00000010, DeviceLAlternate = 0x00000020, DeviceRAlternate = 0x00000040, DeviceRControl = 0x00002000;

    // CGEventTapLocation, CGEventTapPlacement, CGEventTapOptions, CGEventSourceStateID.
    public const uint HidEventTap = 0, SessionEventTap = 1;
    public const uint HeadInsertEventTap = 0;
    public const uint TapOptionDefault = 0, TapOptionListenOnly = 1;
    public const int SourceStatePrivate = -1, SourceStateCombinedSession = 0, SourceStateHidSystem = 1;

    /// <summary>CGEventMaskBit(type).</summary>
    public static ulong MaskBit(uint eventType) => 1UL << (int)eventType;

    [DllImport(Lib, EntryPoint = "CGEventTapCreate")]
    public static extern nint EventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, nint callback, nint userInfo);

    [DllImport(Lib, EntryPoint = "CGEventTapEnable")]
    public static extern void EventTapEnable(nint tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(Lib, EntryPoint = "CGEventGetIntegerValueField")]
    public static extern long EventGetIntegerValueField(nint ev, int field);

    [DllImport(Lib, EntryPoint = "CGEventSetIntegerValueField")]
    public static extern void EventSetIntegerValueField(nint ev, int field, long value);

    [DllImport(Lib, EntryPoint = "CGEventGetFlags")]
    public static extern ulong EventGetFlags(nint ev);

    [DllImport(Lib, EntryPoint = "CGEventSetFlags")]
    public static extern void EventSetFlags(nint ev, ulong flags);

    /// <summary>An empty event (CGEvent.h); the tap liveness probe gives it a type, flags and its marker. Present on macOS 27.2, checked 29/10/2026.</summary>
    [DllImport(Lib, EntryPoint = "CGEventCreate")]
    public static extern nint EventCreate(nint source);

    [DllImport(Lib, EntryPoint = "CGEventSetType")]
    public static extern void EventSetType(nint ev, uint type);

    /// <summary>The modifier flags the source state holds now (CGEventSource.h): the probe carries them, so it changes nothing.</summary>
    [DllImport(Lib, EntryPoint = "CGEventSourceFlagsState")]
    public static extern ulong EventSourceFlagsState(int stateId);

    [DllImport(Lib, EntryPoint = "CGEventCreateKeyboardEvent")]
    public static extern nint EventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(Lib, EntryPoint = "CGEventKeyboardSetUnicodeString")]
    public static extern unsafe void EventKeyboardSetUnicodeString(nint ev, nuint length, char* unicodeString);

    [DllImport(Lib, EntryPoint = "CGEventPost")]
    public static extern void EventPost(uint tap, nint ev);

    [DllImport(Lib, EntryPoint = "CGEventSourceCreate")]
    public static extern nint EventSourceCreate(int stateId);

    [DllImport(Lib, EntryPoint = "CGEventSourceKeyState")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EventSourceKeyState(int stateId, ushort key);

    [DllImport(Lib, EntryPoint = "CGEventSourceButtonState")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EventSourceButtonState(int stateId, uint button);

    /// <summary>How many events of this type the source state has seen (CGEventSource.h); called for the HID system state by the tap liveness check. Present on macOS 27.2, checked 29/10/2026.</summary>
    [DllImport(Lib, EntryPoint = "CGEventSourceCounterForEventType")]
    public static extern uint EventSourceCounterForEventType(int stateId, uint eventType);
}
