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
    /// <summary>
    /// Whose names the keys carry on screen: the parser's tokens are Windows names ("RightAlt", "Win"), and a Mac keyboard
    /// prints Option and Command on those keys. The platform's entry point sets this once (Dictum.Mac: Program.Main); the
    /// default keeps every Windows string as it was. The settings tokens never change with it.
    /// </summary>
    public static KeyDialect Dialect { get; set; } = KeyDialect.Windows;

    /// <summary>
    /// The hotkey a fresh settings.json gets: Right Ctrl on Windows; on a Mac, where most keyboards have no right-hand Control
    /// key, Right Option (token "RightAlt"; PROPOSED 22/10/2026, docs/DECISIONS.md "Dictum for macOS").
    /// </summary>
    public static string DefaultHotkey => DefaultHotkeyFor(Dialect);

    /// <summary>The default hotkey for a given keyboard (pure; the tests use it so no test touches <see cref="Dialect"/>).</summary>
    public static string DefaultHotkeyFor(KeyDialect dialect) => dialect == KeyDialect.Mac ? "RightAlt" : "RightCtrl";

    /// <summary>A settings value shown to a person: each key's display name joined by "+"; a value the parser refuses is shown as it is.</summary>
    public static string Display(string? hotkey) => Display(hotkey, Dialect);

    /// <summary>The same for a given keyboard (pure).</summary>
    public static string Display(string? hotkey, KeyDialect dialect) =>
        HotkeyParser.TryParse(hotkey, out var spec, out _) ? Display(spec, dialect) : hotkey ?? "";

    /// <summary>A parsed hotkey shown to a person.</summary>
    public static string Display(HotkeySpec spec) => Display(spec, Dialect);

    /// <summary>The same for a given keyboard (pure).</summary>
    public static string Display(HotkeySpec spec, KeyDialect dialect) => string.Join("+", spec.AllKeys.Select(k => Key(k.Name, dialect)));

    /// <summary>
    /// One key name, split into words: a space before a capital that follows a small letter ("RightCtrl" to "Right Ctrl",
    /// "CapsLock" to "Caps Lock") and before the digits of a name longer than one letter ("Mouse4" to "Mouse 4",
    /// "Numpad5" to "Numpad 5"). "F9", "A", "`" and a raw code ("0x7B") stay as they are.
    /// </summary>
    public static string Key(string name) => Key(name, Dialect);

    /// <summary>The same for a given keyboard (pure).</summary>
    public static string Key(string name, KeyDialect dialect)
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
        return dialect == KeyDialect.Mac ? MacWords(text.ToString()) : text.ToString();
    }

    /// <summary>The Mac keyboard's words for the two keys named differently there: Alt is Option, Win is Command.</summary>
    private static string MacWords(string display) =>
        string.Join(' ', display.Split(' ').Select(word => word switch { "Alt" => "Option", "Win" => "Command", _ => word }));
}

/// <summary>Which keyboard's words a key name is shown with (<see cref="HotkeyNames.Dialect"/>).</summary>
public enum KeyDialect
{
    Windows,
    Mac,
}
