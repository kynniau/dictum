using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Dictum.Hotkey;

/// <summary>
/// Parses hotkey strings such as "RightCtrl", "F9", "CapsLock", "Ctrl+Win", "Ctrl+Shift+Space".
/// Tokens are separated by '+', case-insensitive. In a chord every token but the last must be a modifier.
/// </summary>
public static class HotkeyParser
{
    public const string FnMessage =
        "The Fn key is handled by keyboard firmware on nearly all keyboards and never reaches Windows as a key. " +
        "Use RightCtrl, CapsLock, an F-key (F9...F24) or Ctrl+Win instead.";

    public const string LeftMouseMessage =
        "The left mouse button cannot be the hotkey: it is the button that clicks everything, so holding it to dictate would " +
        "swallow every click. Use RightMouse, MiddleMouse, or a side button (Mouse4, Mouse5).";

    /// <summary>The mouse buttons a hotkey may name, for messages and the Settings page.</summary>
    public const string MouseButtonNames = "RightMouse, MiddleMouse, Mouse4, Mouse5";

    /// <summary>Tab is refused like Esc: a keyboard user tabbing to the capture box's buttons would otherwise choose it.</summary>
    public const string TabMessage = "Tab moves keyboard focus everywhere and cannot be the hotkey.";

    private static readonly Dictionary<string, KeyGroup> Keys = BuildTable();

    public static HotkeySpec Parse(string text)
    {
        if (!TryParse(text, out var spec, out var error)) throw new FormatException(error);
        return spec;
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeySpec? spec, [NotNullWhen(false)] out string? error)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Hotkey is empty. Example values: \"RightCtrl\", \"F9\", \"Ctrl+Win\", \"Ctrl+Shift+Space\".";
            return false;
        }

        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Any(t => t.Length == 0))
        {
            error = $"Hotkey \"{text}\" has an empty part. Separate keys with a single '+', e.g. \"Ctrl+Shift+Space\".";
            return false;
        }

        var groups = new List<KeyGroup>();
        foreach (var token in tokens)
        {
            if (!TryKey(token, out var group, out error))
            {
                error = $"Hotkey \"{text}\": {error}";
                return false;
            }
            // Compared on virtual keys, not names: "Ctrl+RightCtrl" would complete on a lone RightCtrl press.
            if (groups.FirstOrDefault(g => g.Vks.Intersect(group.Vks).Any()) is { } clash)
            {
                error = clash.Name == group.Name
                    ? $"Hotkey \"{text}\" names {group.Name} twice."
                    : $"Hotkey \"{text}\" names {clash.Name} and {group.Name}, which share a key, so one key press would count as both. Name each key once.";
                return false;
            }
            groups.Add(group);
        }

        var modifiers = groups.Take(groups.Count - 1).ToList();
        var nonModifier = modifiers.FirstOrDefault(m => !m.IsModifier);
        if (nonModifier is not null)
        {
            error = $"Hotkey \"{text}\": only the last key of a chord may be a non-modifier, but {nonModifier.Name} is not a modifier (Ctrl, Shift, Alt, Win).";
            return false;
        }

        spec = new HotkeySpec(modifiers, groups[^1]);
        error = null;
        return true;
    }

    private static bool TryKey(string token, [NotNullWhen(true)] out KeyGroup? group, [NotNullWhen(false)] out string? error)
    {
        var key = token.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        if (key == "fn" || key == "function")
        {
            group = null;
            error = FnMessage;
            return false;
        }
        if (key is "esc" or "escape")
        {
            group = null;
            error = "Esc is reserved for cancelling a recording and cannot be the hotkey.";
            return false;
        }
        if (key == "tab")
        {
            group = null;
            error = TabMessage;
            return false;
        }
        if (key is "leftmouse" or "leftclick" or "leftbutton" or "lbutton" or "mouseleft" or "mouse1")
        {
            group = null;
            error = LeftMouseMessage;
            return false;
        }
        if (Keys.TryGetValue(key, out group))
        {
            error = null;
            return true;
        }
        // Raw virtual-key code for keys without a name: "0x7B" or "vk7B".
        var hex = key.StartsWith("0x", StringComparison.Ordinal) ? key[2..] : key.StartsWith("vk", StringComparison.Ordinal) ? key[2..] : null;
        if (hex is not null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vk) && vk is > 0 and < 0xFF && vk != Vk.Escape)
        {
            if (vk is Vk.LButton or Vk.Tab)
            {
                error = vk == Vk.Tab ? TabMessage : LeftMouseMessage;
                return false;
            }
            group = new KeyGroup(NameOf(vk), [vk], IsModifierVk(vk));
            error = null;
            return true;
        }
        error = $"unknown key \"{token}\". Use a key name such as RightCtrl, LeftAlt, Win, CapsLock, Pause, Space, F1-F24, A-Z, 0-9, " +
                $"a mouse button ({MouseButtonNames}), or a virtual-key code like 0x7B.";
        return false;
    }

    internal static bool IsModifierVk(int vk) => vk is Vk.LControl or Vk.RControl or Vk.LShift or Vk.RShift or Vk.LMenu or Vk.RMenu or Vk.LWin or Vk.RWin or Vk.Control or Vk.Shift or Vk.Menu;

    private static readonly Lazy<Dictionary<int, string>> Names = new(() =>
    {
        var names = new Dictionary<int, string>();
        foreach (var group in Keys.Values.Where(g => g.Vks.Length == 1)) names.TryAdd(group.Vks[0], group.Name);
        return names;
    });

    /// <summary>
    /// The name the parser accepts for one virtual key, sided where the key has sides ("RightAlt", "LeftCtrl", "F24"),
    /// or a raw code ("0xE8") for a key without a name: what the "Press your hotkey" box shows and writes.
    /// </summary>
    public static string NameOf(int vk) => vk == Vk.LButton ? "LeftMouse" : Names.Value.TryGetValue(vk, out var name) ? name : $"0x{vk:X2}";

    /// <summary>Which side of the keyboard a sided key is on ("right"), or null.</summary>
    public static string? SideOf(int vk) => vk switch
    {
        Vk.LControl or Vk.LShift or Vk.LMenu or Vk.LWin => "left",
        Vk.RControl or Vk.RShift or Vk.RMenu or Vk.RWin => "right",
        _ => null,
    };

    private static Dictionary<string, KeyGroup> BuildTable()
    {
        var t = new Dictionary<string, KeyGroup>(StringComparer.Ordinal);
        void Add(KeyGroup g, params string[] names) { foreach (var n in names) t[n] = g; }

        Add(new("Ctrl", [Vk.LControl, Vk.RControl], true), "ctrl", "control", "ctl");
        Add(new("LeftCtrl", [Vk.LControl], true), "leftctrl", "lctrl", "leftcontrol", "lcontrol");
        Add(new("RightCtrl", [Vk.RControl], true), "rightctrl", "rctrl", "rightcontrol", "rcontrol");
        Add(new("Shift", [Vk.LShift, Vk.RShift], true), "shift");
        Add(new("LeftShift", [Vk.LShift], true), "leftshift", "lshift");
        Add(new("RightShift", [Vk.RShift], true), "rightshift", "rshift");
        Add(new("Alt", [Vk.LMenu, Vk.RMenu], true), "alt");
        Add(new("LeftAlt", [Vk.LMenu], true), "leftalt", "lalt");
        Add(new("RightAlt", [Vk.RMenu], true), "rightalt", "ralt", "altgr");
        Add(new("Win", [Vk.LWin, Vk.RWin], true), "win", "windows", "super", "meta", "cmd");
        Add(new("LeftWin", [Vk.LWin], true), "leftwin", "lwin", "leftwindows");
        Add(new("RightWin", [Vk.RWin], true), "rightwin", "rwin", "rightwindows");

        Add(new("CapsLock", [Vk.Capital], false), "capslock", "caps", "capital");
        Add(new("Pause", [Vk.Pause], false), "pause", "break", "pausebreak");
        Add(new("ScrollLock", [0x91], false), "scrolllock", "scroll");
        Add(new("NumLock", [0x90], false), "numlock");
        Add(new("Space", [Vk.Space], false), "space", "spacebar");
        Add(new("Tab", [Vk.Tab], false), "tab"); // named for the capture box ("Tab down"); refused as a hotkey in TryKey
        Add(new("Enter", [Vk.Return], false), "enter", "return");
        Add(new("Backspace", [Vk.Back], false), "backspace", "back");
        Add(new("Insert", [0x2D], false), "insert", "ins");
        Add(new("Delete", [0x2E], false), "delete", "del");
        Add(new("Home", [0x24], false), "home");
        Add(new("End", [0x23], false), "end");
        Add(new("PageUp", [0x21], false), "pageup", "pgup");
        Add(new("PageDown", [0x22], false), "pagedown", "pgdn", "pgdown");
        Add(new("Up", [0x26], false), "up", "uparrow");
        Add(new("Down", [0x28], false), "down", "downarrow");
        Add(new("Left", [0x25], false), "left", "leftarrow");
        Add(new("Right", [0x27], false), "right", "rightarrow");
        Add(new("PrintScreen", [0x2C], false), "printscreen", "prtsc", "prtscr", "print");
        Add(new("Apps", [Vk.Apps], false), "apps", "contextmenu", "menu", "application");
        Add(new("`", [0xC0], false), "`", "backtick", "grave", "tilde");

        // Mouse buttons: whichever physical button Windows reports as that button (so a swapped mouse follows the swap).
        // The left button is refused in TryKey; the matcher never sees it (the mouse hook does not decode it).
        Add(new("RightMouse", [Vk.RButton], false), "rightmouse", "rightclick", "rightbutton", "rbutton", "mouseright", "mouse2");
        Add(new("MiddleMouse", [Vk.MButton], false), "middlemouse", "middleclick", "middlebutton", "mbutton", "mousemiddle", "wheelclick", "mouse3");
        Add(new("Mouse4", [Vk.XButton1], false), "mouse4", "xbutton1", "sidebutton1", "mouseside1");
        Add(new("Mouse5", [Vk.XButton2], false), "mouse5", "xbutton2", "sidebutton2", "mouseside2");

        for (var i = 1; i <= 24; i++) Add(new($"F{i}", [Vk.F1 + i - 1], false), $"f{i}");
        for (var c = 'A'; c <= 'Z'; c++) Add(new(c.ToString(), [c], false), char.ToLowerInvariant(c).ToString());
        for (var d = 0; d <= 9; d++)
        {
            Add(new(d.ToString(CultureInfo.InvariantCulture), ['0' + d], false), d.ToString(CultureInfo.InvariantCulture));
            Add(new($"Numpad{d}", [0x60 + d], false), $"numpad{d}", $"num{d}");
        }
        return t;
    }
}
