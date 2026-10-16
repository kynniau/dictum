using Dictum.Hotkey;

namespace Dictum.Tests;

public class HotkeyParserTests
{
    [Theory]
    [InlineData("RightCtrl", 0xA3)]
    [InlineData("RCtrl", 0xA3)]
    [InlineData("RightControl", 0xA3)]
    [InlineData("rctrl", 0xA3)]
    [InlineData("Right Ctrl", 0xA3)]
    [InlineData("F9", 0x78)]
    [InlineData("f24", 0x87)]
    [InlineData("CapsLock", 0x14)]
    [InlineData("Pause", 0x13)]
    [InlineData("RightAlt", 0xA5)]
    [InlineData("AltGr", 0xA5)]
    [InlineData("0x7B", 0x7B)]
    public void Single_keys_and_aliases(string text, int vk)
    {
        var spec = HotkeyParser.Parse(text);
        Assert.False(spec.IsChord);
        Assert.Equal([vk], spec.Trigger.Vks);
    }

    [Fact]
    public void Aliases_resolve_to_the_same_canonical_name()
    {
        Assert.Equal("RightCtrl", HotkeyParser.Parse("RCtrl").Display);
        Assert.Equal("RightCtrl", HotkeyParser.Parse("RightControl").Display);
    }

    [Fact]
    public void Ctrl_win_is_an_all_modifier_chord()
    {
        var spec = HotkeyParser.Parse("Ctrl+Win");
        Assert.True(spec.IsChord);
        Assert.True(spec.AllModifiers);
        Assert.Equal("Ctrl", Assert.Single(spec.Modifiers).Name);
        Assert.Equal([0x5B, 0x5C], spec.Trigger.Vks);
        Assert.Equal("Ctrl+Win", spec.Display);
    }

    [Fact]
    public void Ctrl_shift_space_chord()
    {
        var spec = HotkeyParser.Parse(" ctrl + shift + space ");
        Assert.Equal(["Ctrl", "Shift"], spec.Modifiers.Select(m => m.Name));
        Assert.Equal([0x20], spec.Trigger.Vks);
        Assert.False(spec.AllModifiers);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Ctrl+", "empty part")]
    [InlineData("Banana", "unknown key")]
    [InlineData("Space+Ctrl", "only the last key")]
    [InlineData("Ctrl+Ctrl", "twice")]
    [InlineData("Fn", "firmware")]
    [InlineData("Ctrl+Fn", "firmware")]
    [InlineData("Esc", "reserved")]
    public void Invalid_strings_are_rejected_with_a_clear_message(string text, string expectedFragment)
    {
        Assert.False(HotkeyParser.TryParse(text, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<FormatException>(() => HotkeyParser.Parse(text));
    }
}

public class HotkeyMatcherTests
{
    private const int LCtrl = 0xA2, RCtrl = 0xA3, LShift = 0xA0, LWin = 0x5B, Space = 0x20, Esc = 0x1B, A = 0x41;

    private static HotkeyMatcher Matcher(string hotkey, bool suppress = true, Func<int, bool>? probe = null) =>
        new(HotkeyParser.Parse(hotkey), suppress, probe);

    [Fact]
    public void Single_key_is_suppressed_down_repeat_and_up()
    {
        var m = Matcher("RightCtrl");
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(RCtrl, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.None, false), m.OnKey(RCtrl, true, true)); // auto-repeat
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(RCtrl, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(LCtrl, true, true)); // the other Ctrl is untouched
    }

    [Fact]
    public void Suppression_off_passes_the_key_through()
    {
        var m = Matcher("F9", suppress: false);
        Assert.Equal(new KeyDecision(false, HotkeySignal.Down, false), m.OnKey(0x78, true, true));
        Assert.Equal(new KeyDecision(false, HotkeySignal.Up, false), m.OnKey(0x78, false, true));
    }

    [Fact]
    public void Esc_is_always_passed_through_and_watched_only_when_enabled()
    {
        var m = Matcher("RightCtrl");
        Assert.Equal(new KeyDecision(false, HotkeySignal.Escape, false), m.OnKey(Esc, true, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(Esc, true, true));  // repeat
        Assert.Equal(KeyDecision.Pass, m.OnKey(Esc, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(Esc, true, false)); // disabled: inert
    }

    [Fact]
    public void Chord_suppresses_only_the_final_non_modifier_key()
    {
        var m = Matcher("Ctrl+Shift+Space");
        Assert.Equal(KeyDecision.Pass, m.OnKey(LCtrl, true, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(LShift, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(Space, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(Space, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(LShift, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(LCtrl, false, true));
    }

    [Fact]
    public void Chord_key_alone_or_incomplete_chord_is_not_touched()
    {
        var m = Matcher("Ctrl+Shift+Space");
        Assert.Equal(KeyDecision.Pass, m.OnKey(Space, true, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(Space, false, true));
        m.OnKey(LCtrl, true, true);
        Assert.Equal(KeyDecision.Pass, m.OnKey(Space, true, true)); // Shift missing
    }

    [Fact]
    public void Releasing_a_modifier_first_ends_the_hold_and_the_trigger_up_stays_swallowed()
    {
        var m = Matcher("Ctrl+Space");
        m.OnKey(LCtrl, true, true);
        Assert.Equal(HotkeySignal.Down, m.OnKey(Space, true, true).Signal);
        Assert.Equal(new KeyDecision(false, HotkeySignal.Up, false), m.OnKey(LCtrl, false, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.None, false), m.OnKey(Space, false, true));
    }

    [Fact]
    public void All_modifier_chord_is_order_free_never_suppressed_and_masks_win()
    {
        var m = Matcher("Ctrl+Win");
        Assert.Equal(KeyDecision.Pass, m.OnKey(LWin, true, true));
        Assert.Equal(new KeyDecision(false, HotkeySignal.Down, true), m.OnKey(LCtrl, true, true));
        Assert.Equal(new KeyDecision(false, HotkeySignal.Up, false), m.OnKey(LWin, false, true));
    }

    [Fact]
    public void Disabled_passes_the_hotkey_through_without_signals()
    {
        var m = Matcher("RightCtrl");
        Assert.Equal(KeyDecision.Pass, m.OnKey(RCtrl, true, false));
        Assert.Equal(KeyDecision.Pass, m.OnKey(RCtrl, false, false));
    }

    [Fact]
    public void A_swallowed_key_still_held_when_disabled_keeps_its_repeats_and_release_swallowed()
    {
        // Switching the listener off stops new presses only. Passing on the auto-repeat of a key whose press was swallowed,
        // while swallowing its release, would show the app a key going down that never comes up.
        var m = Matcher("RightCtrl");
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(RCtrl, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.None, false), m.OnKey(RCtrl, true, false)); // auto-repeat, listener off
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(RCtrl, false, false));
        Assert.Equal(KeyDecision.Pass, m.OnKey(RCtrl, true, false)); // the next press, still off, passes untouched
        Assert.Equal(KeyDecision.Pass, m.OnKey(RCtrl, false, false));
    }

    [Fact]
    public void Stale_modifier_is_dropped_when_the_probe_says_it_is_up()
    {
        var physicallyDown = new HashSet<int>();
        var m = Matcher("Ctrl+Space", probe: physicallyDown.Contains);
        m.OnKey(LCtrl, true, true); // its key-up is then lost (e.g. secure desktop)
        Assert.Equal(KeyDecision.Pass, m.OnKey(Space, true, true));
        m.OnKey(Space, false, true);
        physicallyDown.Add(LCtrl);
        m.OnKey(LCtrl, true, true);
        Assert.Equal(HotkeySignal.Down, m.OnKey(Space, true, true).Signal);
    }

    [Fact]
    public void Other_keys_are_never_touched()
    {
        var m = Matcher("RightCtrl");
        Assert.Equal(KeyDecision.Pass, m.OnKey(A, true, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(A, false, true));
    }
}
