using System.Text;

namespace Dictum.Hotkey;

/// <summary>
/// How a hotkey is shown to a person (1.2 polish, 22/10/2026): "Right Ctrl", "Ctrl+Shift+Space", "Mouse 4", "Caps Lock". The
/// settings token stays as the parser names it ("RightCtrl"): settings.json, the logs, the self-test and the settings
/// loader's warnings keep the token, because that is what a person types into settings.json. Every window that shows a key
/// (the tray panel, the hotkey box, the calibration window, the welcome window's Try it, Settings > General) goes through here,
/// so the Help's "the Ctrl key on the right-hand side" and the screen agree.
/// </summary>
public static class HotkeyNames
{
    /// <summary>A settings value shown to a person: each key's display name joined by "+"; a value the parser refuses is shown as it is.</summary>
    public static string Display(string? hotkey) =>
        HotkeyParser.TryParse(hotkey, out var spec, out _) ? Display(spec) : hotkey ?? "";

    /// <summary>A parsed hotkey shown to a person.</summary>
    public static string Display(HotkeySpec spec) => string.Join("+", spec.AllKeys.Select(k => Key(k.Name)));

    /// <summary>
    /// One key name, split into words: a space before a capital that follows a small letter ("RightCtrl" to "Right Ctrl",
    /// "CapsLock" to "Caps Lock") and before the digits of a name longer than one letter ("Mouse4" to "Mouse 4",
    /// "Numpad5" to "Numpad 5"). "F9", "A", "`" and a raw code ("0x7B") stay as they are.
    /// </summary>
    public static string Key(string name)
    {
        if (name.StartsWith("0x", StringComparison.Ordinal)) return name;
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0)
            {
                var previous = name[i - 1];
                if (char.IsUpper(c) && char.IsLower(previous)) text.Append(' ');
                else if (char.IsDigit(c) && char.IsLetter(previous) && i > 1) text.Append(' ');
            }
            text.Append(c);
        }
        return text.ToString();
    }
}
