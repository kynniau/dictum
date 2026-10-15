using Dictum.Platform;

namespace Dictum.Hotkey;

/// <summary>
/// The logic of the "Press your hotkey" box (SPEC §20), apart from any window: it is fed every key event the system
/// delivers and reports the hotkey a press gesture names (the keys held together at the gesture's peak, modifiers first,
/// the last other key as the trigger), and whether a hotkey's keys are all held now (the "Hold to test" light).
/// </summary>
public sealed class HotkeyCapture
{
    private readonly List<int> _down = []; // held now, in press order
    private List<int> _peak = [];

    /// <summary>What the system delivered last, in words: "RightAlt (right side) down".</summary>
    public string? LastReceived { get; private set; }

    /// <summary>Feeds one key event; returns the hotkey text once every key of a gesture is released, else null.</summary>
    /// <remarks>Esc on its own names no hotkey (the parser refuses it: it cancels a dictation), so it returns null.</remarks>
    public string? Feed(RawKeyEvent e)
    {
        var side = HotkeyParser.SideOf(e.Vk) is { } s ? $" ({s} side)" : "";
        LastReceived = $"{(e.Vk == Vk.Escape ? "Esc" : HotkeyParser.NameOf(e.Vk))}{side} {(e.Down ? "down" : "up")}{(e.Injected ? ", sent by a program" : "")}";
        if (e.Down)
        {
            if (!_down.Contains(e.Vk)) _down.Add(e.Vk);
            if (_down.Count >= _peak.Count) _peak = [.. _down];
            return null;
        }
        _down.Remove(e.Vk);
        if (_down.Count > 0 || _peak.Count == 0) return null;
        var keys = _peak;
        _peak = [];
        var trigger = keys.LastOrDefault(k => !HotkeyParser.IsModifierVk(k));
        if (trigger == 0) trigger = keys[^1];
        var text = string.Join("+", keys.Where(k => k != trigger && HotkeyParser.IsModifierVk(k)).Select(HotkeyParser.NameOf).Append(HotkeyParser.NameOf(trigger)));
        return HotkeyParser.TryParse(text, out _, out _) ? text : null;
    }

    /// <summary>True while every key of <paramref name="spec"/> is held (any key of each group).</summary>
    public bool IsHeld(HotkeySpec? spec) => spec is not null && spec.AllKeys.All(g => g.Vks.Any(_down.Contains));

    /// <summary>Forgets held keys (the box stopped listening; their key-ups will not be seen).</summary>
    public void Reset()
    {
        _down.Clear();
        _peak = [];
    }
}
