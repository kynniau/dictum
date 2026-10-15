namespace Dictum.Hotkey;

/// <summary>One logical key: a name and the virtual-key codes that count as it (e.g. Ctrl = LCtrl or RCtrl).</summary>
public sealed record KeyGroup(string Name, int[] Vks, bool IsModifier)
{
    public bool Contains(int vk) => Array.IndexOf(Vks, vk) >= 0;

    public bool Equals(KeyGroup? other) => other is not null && Name == other.Name;

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// A parsed hotkey: either a single key, or modifiers plus a final trigger key.
/// The trigger may itself be a modifier (e.g. Ctrl+Win), in which case the chord is order-free.
/// </summary>
public sealed record HotkeySpec(IReadOnlyList<KeyGroup> Modifiers, KeyGroup Trigger)
{
    public bool IsChord => Modifiers.Count > 0;

    /// <summary>True when every key in the chord is a modifier (Ctrl+Win): any press order completes it.</summary>
    public bool AllModifiers => IsChord && Trigger.IsModifier;

    public string Display => string.Join("+", Modifiers.Select(m => m.Name).Append(Trigger.Name));

    public IEnumerable<KeyGroup> AllKeys => Modifiers.Append(Trigger);

    public override string ToString() => Display;

    /// <summary>
    /// Whether pressing one of the two hotkeys can also complete the other. The matcher works on virtual keys, tolerates
    /// extra held keys and fires a single-key hotkey whatever else is held (DECISIONS.md), so "Ctrl+Space" completes
    /// inside "Ctrl+Shift+Space", "RightCtrl" inside "RightCtrl+F", and "RightCtrl" inside "Ctrl+Shift+F" pressed with the
    /// right-hand Ctrl. Two hotkeys that can fire together would both act on one keystroke.
    /// </summary>
    public bool CanFireTogether(HotkeySpec other) => FiresWhilePressing(this, other) || FiresWhilePressing(other, this);

    /// <summary>Whether <paramref name="spec"/> completes while every key of <paramref name="pressed"/> is held (any of each group's keys).</summary>
    private static bool FiresWhilePressing(HotkeySpec spec, HotkeySpec pressed)
    {
        var held = pressed.AllKeys.SelectMany(g => g.Vks).ToHashSet();
        bool Reachable(KeyGroup group) => group.Vks.Any(held.Contains);
        return spec.AllModifiers ? spec.AllKeys.All(Reachable) : Reachable(spec.Trigger) && spec.Modifiers.All(Reachable);
    }
}

/// <summary>Virtual-key codes used by the hotkey code (winuser.h values).</summary>
public static class Vk
{
    public const int Back = 0x08, Tab = 0x09, Return = 0x0D, Pause = 0x13, Capital = 0x14, Escape = 0x1B, Space = 0x20;
    public const int Control = 0x11, Shift = 0x10, Menu = 0x12;
    public const int LWin = 0x5B, RWin = 0x5C, Apps = 0x5D;
    public const int LShift = 0xA0, RShift = 0xA1, LControl = 0xA2, RControl = 0xA3, LMenu = 0xA4, RMenu = 0xA5;
    public const int V = 0x56;
    public const int F1 = 0x70;

    /// <summary>
    /// An unassigned virtual key (0xE8) injected while Win or Alt is held so that releasing them does not
    /// open the Start menu or activate a menu bar. Same technique AutoHotkey calls the "menu mask key".
    /// </summary>
    public const int MenuMask = 0xE8;

    public static bool IsWinOrAlt(int vk) => vk is LWin or RWin or LMenu or RMenu or Menu;
}
