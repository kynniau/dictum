using Dictum.Hotkey;

namespace Dictum.Mac.Input;

/// <summary>
/// macOS virtual key codes (the kVK_* values in Carbon HIToolbox Events.h, read in the macOS SDK on this Mac 22/10/2026)
/// to the Windows virtual-key codes the whole app uses as key identity (<see cref="Vk"/>, <see cref="HotkeyParser"/>), and
/// back. Mapped once, at the tap, so Core never sees a Mac key code. Key codes name physical positions, not characters:
/// the key in the position of the "A" key is 0x00 whatever the keyboard layout prints on it, which is also what the
/// Windows hook reports for letters on the default layout.
/// <para>Decisions recorded here:</para>
/// <list type="bullet">
/// <item>The function key (kVK_Function, 0x3F) has no virtual key and is not reported: the parser refuses "Fn" (Core).</item>
/// <item>The keypad Enter reports <see cref="Vk.Return"/>, as Windows reports it (one-way: Return maps back to the main key).</item>
/// <item>Help (0x72) reports Insert and keypad Clear (0x47) reports NumLock: each sits where a PC keyboard has that key,
/// and a PC keyboard's Insert and NumLock arrive on a Mac as these codes (recalled, not verified).</item>
/// <item>No Mac key code reports PrintScreen, ScrollLock, Pause or F21 to F24: there are no such kVK codes. (A PC
/// keyboard's PrintScreen, ScrollLock and Pause arrive as F13 to F15, recalled; they report F13 to F15 here.)</item>
/// <item>The punctuation keys report the OEM codes (0xBA to 0xDE, 0xE2) Windows gives the key in the same position on its
/// default layout; those Windows values are recalled from winuser.h, not checked on this Mac.</item>
/// </list>
/// </summary>
internal static class KeyCodeMap
{
    // kVK_* values used by name elsewhere (Events.h).
    public const ushort MacV = 0x09, MacReturn = 0x24, MacTab = 0x30, MacSpace = 0x31, MacEscape = 0x35;
    public const ushort MacCommand = 0x37, MacShift = 0x38, MacCapsLock = 0x39, MacOption = 0x3A, MacControl = 0x3B;
    public const ushort MacRightCommand = 0x36, MacRightShift = 0x3C, MacRightOption = 0x3D, MacRightControl = 0x3E, MacFunction = 0x3F;
    public const ushort MacKeypadEnter = 0x4C;

    /// <summary>The eight modifier keys, left and right, as Mac key codes.</summary>
    public static readonly ushort[] ModifierKeys = [MacControl, MacRightControl, MacShift, MacRightShift, MacOption, MacRightOption, MacCommand, MacRightCommand];

    /// <summary>Every physical key with a virtual key, one row each; the reverse map is built from these rows.</summary>
    private static readonly (ushort Mac, int Vk)[] Rows =
    [
        // Letters (kVK_*_A … _Z): positions, the Windows codes are the capital letters.
        (0x00, 'A'), (0x0B, 'B'), (0x08, 'C'), (0x02, 'D'), (0x0E, 'E'), (0x03, 'F'), (0x05, 'G'), (0x04, 'H'), (0x22, 'I'),
        (0x26, 'J'), (0x28, 'K'), (0x25, 'L'), (0x2E, 'M'), (0x2D, 'N'), (0x1F, 'O'), (0x23, 'P'), (0x0C, 'Q'), (0x0F, 'R'),
        (0x01, 'S'), (0x11, 'T'), (0x20, 'U'), (MacV, 'V'), (0x0D, 'W'), (0x07, 'X'), (0x10, 'Y'), (0x06, 'Z'),
        // Digits on the main block.
        (0x1D, '0'), (0x12, '1'), (0x13, '2'), (0x14, '3'), (0x15, '4'), (0x17, '5'), (0x16, '6'), (0x1A, '7'), (0x1C, '8'), (0x19, '9'),
        // Punctuation, by position (class summary).
        (0x29, 0xBA), (0x18, 0xBB), (0x2B, 0xBC), (0x1B, 0xBD), (0x2F, 0xBE), (0x2C, 0xBF), (0x32, 0xC0), (0x21, 0xDB),
        (0x2A, 0xDC), (0x1E, 0xDD), (0x27, 0xDE), (0x0A, 0xE2),
        // Keypad.
        (0x52, 0x60), (0x53, 0x61), (0x54, 0x62), (0x55, 0x63), (0x56, 0x64), (0x57, 0x65), (0x58, 0x66), (0x59, 0x67), (0x5B, 0x68), (0x5C, 0x69),
        (0x43, 0x6A), (0x45, 0x6B), (0x4E, 0x6D), (0x41, 0x6E), (0x4B, 0x6F), (0x47, 0x90),
        // Whitespace, editing and navigation.
        (MacReturn, Vk.Return), (MacTab, Vk.Tab), (MacSpace, Vk.Space), (0x33, Vk.Back), (MacEscape, Vk.Escape),
        (0x72, 0x2D), (0x75, 0x2E), (0x73, 0x24), (0x77, 0x23), (0x74, 0x21), (0x79, 0x22),
        (0x7B, 0x25), (0x7E, 0x26), (0x7C, 0x27), (0x7D, 0x28), (0x6E, Vk.Apps),
        // Modifiers: Control, Shift, Option (Alt) and Command (the Win key's place), each side its own code.
        (MacControl, Vk.LControl), (MacRightControl, Vk.RControl), (MacShift, Vk.LShift), (MacRightShift, Vk.RShift),
        (MacOption, Vk.LMenu), (MacRightOption, Vk.RMenu), (MacCommand, Vk.LWin), (MacRightCommand, Vk.RWin), (MacCapsLock, Vk.Capital),
        // F1 … F20 (Mac key codes are not in F-key order).
        (0x7A, Vk.F1), (0x78, Vk.F1 + 1), (0x63, Vk.F1 + 2), (0x76, Vk.F1 + 3), (0x60, Vk.F1 + 4), (0x61, Vk.F1 + 5), (0x62, Vk.F1 + 6),
        (0x64, Vk.F1 + 7), (0x65, Vk.F1 + 8), (0x6D, Vk.F1 + 9), (0x67, Vk.F1 + 10), (0x6F, Vk.F1 + 11), (0x69, Vk.F1 + 12),
        (0x6B, Vk.F1 + 13), (0x71, Vk.F1 + 14), (0x6A, Vk.F1 + 15), (0x40, Vk.F1 + 16), (0x4F, Vk.F1 + 17), (0x50, Vk.F1 + 18), (0x5A, Vk.F1 + 19),
    ];

    private static readonly int[] ToVkTable = BuildToVk();
    private static readonly Dictionary<int, ushort> ToMacTable = Rows.ToDictionary(r => r.Vk, r => r.Mac);

    private static int[] BuildToVk()
    {
        var table = new int[128];
        foreach (var (mac, vk) in Rows) table[mac] = vk;
        table[MacKeypadEnter] = Vk.Return; // one-way (class summary)
        return table;
    }

    /// <summary>The virtual key for a Mac key code, or 0 when the key has none (Fn, keypad =, media keys, unknown codes).</summary>
    public static int ToVk(long macKeyCode) => macKeyCode is >= 0 and < 128 ? ToVkTable[macKeyCode] : 0;

    /// <summary>The Mac key code for a virtual key, or -1 when no Mac key reports it (PrintScreen, F21, the generic Ctrl/Shift/Alt codes, mouse buttons).</summary>
    public static int ToMac(int vk) => ToMacTable.TryGetValue(vk, out var mac) ? mac : -1;

    /// <summary>Every row (Mac key code, virtual key), for the tests.</summary>
    internal static IReadOnlyList<(ushort Mac, int Vk)> AllRows => Rows;

    /// <summary>
    /// A non-left mouse button by its Core Graphics button number (kCGMouseEventButtonNumber: 1 is right, 2 the centre,
    /// per kCGMouseButtonRight/Center in CGEventTypes.h; 3 and 4 the first two side buttons, recalled), as the parser's
    /// mouse virtual key; 0 for the left button and any other number.
    /// </summary>
    public static int ButtonToVk(long buttonNumber) => buttonNumber switch
    {
        1 => Vk.RButton,
        2 => Vk.MButton,
        3 => Vk.XButton1,
        4 => Vk.XButton2,
        _ => 0,
    };

    /// <summary>The Core Graphics button number for a mouse virtual key (the left button included, for the key-state probe), or -1.</summary>
    public static int VkToButton(int vk) => vk switch
    {
        Vk.LButton => 0,
        Vk.RButton => 1,
        Vk.MButton => 2,
        Vk.XButton1 => 3,
        Vk.XButton2 => 4,
        _ => -1,
    };
}
