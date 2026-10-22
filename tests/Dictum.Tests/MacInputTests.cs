using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Mac.Input;
using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Tests;

/// <summary>
/// The macOS input layer (src/Dictum.Mac/Input): the key-code table, the event decoding, the hotkey decision, the typing
/// batches and the pasteboard payload are pure and run on every OS. Anything that creates a tap, posts an event or touches
/// the pasteboard runs only on a Mac with DICTUM_UI_TESTS=1; the taps and posting also need Accessibility.
/// </summary>
public class MacInputTests
{
    // ---- KeyCodeMap -------------------------------------------------------------------------------------------------

    [Fact]
    public void MacInput_key_codes_match_the_Events_h_values()
    {
        // Spot checks against kVK_* in Carbon HIToolbox Events.h (macOS SDK, read 22/10/2026).
        Assert.Equal('A', KeyCodeMap.ToVk(0x00));
        Assert.Equal(Vk.V, KeyCodeMap.ToVk(0x09));
        Assert.Equal('0', KeyCodeMap.ToVk(0x1D));
        Assert.Equal(Vk.Return, KeyCodeMap.ToVk(0x24));
        Assert.Equal(Vk.Back, KeyCodeMap.ToVk(0x33)); // kVK_Delete is the backspace key
        Assert.Equal(0x2E, KeyCodeMap.ToVk(0x75)); // kVK_ForwardDelete
        Assert.Equal(Vk.LWin, KeyCodeMap.ToVk(0x37));
        Assert.Equal(Vk.RWin, KeyCodeMap.ToVk(0x36));
        Assert.Equal(Vk.LMenu, KeyCodeMap.ToVk(0x3A));
        Assert.Equal(Vk.RMenu, KeyCodeMap.ToVk(0x3D));
        Assert.Equal(Vk.LControl, KeyCodeMap.ToVk(0x3B));
        Assert.Equal(Vk.RControl, KeyCodeMap.ToVk(0x3E));
        Assert.Equal(Vk.LShift, KeyCodeMap.ToVk(0x38));
        Assert.Equal(Vk.RShift, KeyCodeMap.ToVk(0x3C));
        Assert.Equal(Vk.Capital, KeyCodeMap.ToVk(0x39));
        Assert.Equal(Vk.F1, KeyCodeMap.ToVk(0x7A));
        Assert.Equal(Vk.F1 + 19, KeyCodeMap.ToVk(0x5A)); // F20
        Assert.Equal(0x60, KeyCodeMap.ToVk(0x52)); // keypad 0
        Assert.Equal(0x69, KeyCodeMap.ToVk(0x5C)); // keypad 9
        Assert.Equal(0xC0, KeyCodeMap.ToVk(0x32)); // grave
        Assert.Equal(Vk.Apps, KeyCodeMap.ToVk(0x6E)); // kVK_ContextualMenu
        Assert.Equal(Vk.Return, KeyCodeMap.ToVk(KeyCodeMap.MacKeypadEnter));
        Assert.Equal(0, KeyCodeMap.ToVk(KeyCodeMap.MacFunction)); // Fn: no virtual key, not reported
        Assert.Equal(0, KeyCodeMap.ToVk(0x51)); // keypad =
        Assert.Equal(0, KeyCodeMap.ToVk(-1));
        Assert.Equal(0, KeyCodeMap.ToVk(500));
    }

    [Fact]
    public void MacInput_every_physical_key_has_its_own_virtual_key()
    {
        var rows = KeyCodeMap.AllRows;
        Assert.Equal(rows.Count, rows.Select(r => r.Mac).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.Vk).Distinct().Count());
        Assert.DoesNotContain(rows, r => r.Vk == 0 || r.Mac >= 128);

        // The one intended duplicate: the keypad Enter reports Return, one-way, as Windows reports it.
        var shared = Enumerable.Range(0, 128).Where(mac => KeyCodeMap.ToVk(mac) != 0).GroupBy(mac => KeyCodeMap.ToVk(mac)).Where(g => g.Count() > 1).ToList();
        var only = Assert.Single(shared);
        Assert.Equal(Vk.Return, only.Key);
        Assert.Equal([KeyCodeMap.MacReturn, KeyCodeMap.MacKeypadEnter], only.Select(m => (ushort)m).Order());
    }

    [Fact]
    public void MacInput_key_codes_round_trip()
    {
        foreach (var (mac, vk) in KeyCodeMap.AllRows)
        {
            Assert.Equal(vk, KeyCodeMap.ToVk(mac));
            Assert.Equal(mac, KeyCodeMap.ToMac(vk));
        }
        Assert.Equal(KeyCodeMap.MacReturn, KeyCodeMap.ToMac(Vk.Return)); // never the keypad Enter
        Assert.Equal(-1, KeyCodeMap.ToMac(Vk.Control)); // generic codes are expanded by KeyState, not mapped
        Assert.Equal(-1, KeyCodeMap.ToMac(Vk.RButton));
        foreach (var vk in new[] { Vk.RButton, Vk.MButton, Vk.XButton1, Vk.XButton2 }) Assert.Equal(vk, KeyCodeMap.ButtonToVk(KeyCodeMap.VkToButton(vk)));
        Assert.Equal(0, KeyCodeMap.ButtonToVk(0)); // the left button is never a hotkey key
        Assert.Equal(0, KeyCodeMap.ButtonToVk(5));
    }

    [Fact]
    public void MacInput_every_parser_key_a_Mac_keyboard_has_is_reachable()
    {
        // Read the parser's own table so a key added there later is checked here too.
        var field = typeof(HotkeyParser).GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var groups = ((Dictionary<string, KeyGroup>)field.GetValue(null)!).Values.Distinct().ToList();
        Assert.NotEmpty(groups);

        // No kVK code exists for these (KeyCodeMap summary); every other group must be reachable from a Mac key or button.
        string[] noMacKey = ["PrintScreen", "ScrollLock", "Pause", "F21", "F22", "F23", "F24"];
        var unreachable = groups
            .Where(g => !g.Vks.Any(vk => KeyCodeMap.ToMac(vk) >= 0 || KeyCodeMap.ButtonToVk(KeyCodeMap.VkToButton(vk)) == vk))
            .Select(g => g.Name)
            .Order()
            .ToList();
        Assert.Equal(noMacKey.Order(), unreachable);
        // A sided or generic group is reachable on every side it names.
        foreach (var group in groups.Where(g => g.IsModifier)) Assert.All(group.Vks, vk => Assert.True(KeyCodeMap.ToMac(vk) >= 0, $"{group.Name} {vk:X2}"));
    }

    // ---- TapEvents --------------------------------------------------------------------------------------------------

    [Fact]
    public void MacInput_right_option_reports_one_down_and_one_up()
    {
        var events = new TapEvents(mouse: false);
        Assert.Equal([new KeyChange(Vk.RMenu, true)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, CG.FlagAlternate | CG.DeviceRAlternate, false, -1));
        // A repeated flagsChanged for a key already down reports nothing.
        Assert.Empty(events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, CG.FlagAlternate | CG.DeviceRAlternate, false, -1));
        Assert.Equal([new KeyChange(Vk.RMenu, false)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, 0, false, -1));
        Assert.Empty(events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, 0, false, -1));
    }

    [Fact]
    public void MacInput_each_side_of_a_modifier_is_tracked_on_its_own()
    {
        var events = new TapEvents(mouse: false);
        const ulong both = CG.FlagAlternate | CG.DeviceLAlternate | CG.DeviceRAlternate;
        Assert.Equal([new KeyChange(Vk.LMenu, true)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacOption, CG.FlagAlternate | CG.DeviceLAlternate, false, -1));
        Assert.Equal([new KeyChange(Vk.RMenu, true)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, both, false, -1));
        // Right released while left is still held: the generic Option flag stays set, the right device bit clears.
        Assert.Equal([new KeyChange(Vk.RMenu, false)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, CG.FlagAlternate | CG.DeviceLAlternate, false, -1));
        Assert.Equal([new KeyChange(Vk.LMenu, false)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacOption, 0, false, -1));

        // Every modifier key, both sides.
        (ushort Mac, ulong Flags, int Vk)[] keys =
        [
            (KeyCodeMap.MacControl, CG.FlagControl | CG.DeviceLControl, Vk.LControl), (KeyCodeMap.MacRightControl, CG.FlagControl | CG.DeviceRControl, Vk.RControl),
            (KeyCodeMap.MacShift, CG.FlagShift | CG.DeviceLShift, Vk.LShift), (KeyCodeMap.MacRightShift, CG.FlagShift | CG.DeviceRShift, Vk.RShift),
            (KeyCodeMap.MacCommand, CG.FlagCommand | CG.DeviceLCommand, Vk.LWin), (KeyCodeMap.MacRightCommand, CG.FlagCommand | CG.DeviceRCommand, Vk.RWin),
        ];
        foreach (var (mac, flags, vk) in keys)
        {
            Assert.Equal([new KeyChange(vk, true)], events.Decode(CG.EventFlagsChanged, mac, flags, false, -1));
            Assert.Equal([new KeyChange(vk, false)], events.Decode(CG.EventFlagsChanged, mac, 0, false, -1));
        }
    }

    [Fact]
    public void MacInput_modifier_without_device_bits_toggles()
    {
        var events = new TapEvents(mouse: false);
        Assert.Equal([new KeyChange(Vk.RControl, true)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightControl, CG.FlagControl, false, -1));
        Assert.Equal([new KeyChange(Vk.RControl, false)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightControl, CG.FlagControl, false, -1));
    }

    [Fact]
    public void MacInput_fn_unknown_keys_and_caps_lock()
    {
        var events = new TapEvents(mouse: true);
        Assert.Empty(events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacFunction, 0x00800000, false, -1)); // NX_SECONDARYFNMASK
        Assert.Empty(events.Decode(CG.EventKeyDown, 0x51, 0, false, -1)); // keypad =: no virtual key
        // simplification in TapEvents: Caps Lock reports a tap on every flagsChanged.
        Assert.Equal([new KeyChange(Vk.Capital, true), new KeyChange(Vk.Capital, false)], events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacCapsLock, CG.FlagAlphaShift, false, -1));
    }

    [Fact]
    public void MacInput_keys_and_autorepeat_decode()
    {
        var events = new TapEvents(mouse: false);
        Assert.Equal([new KeyChange(Vk.F1 + 8, true)], events.Decode(CG.EventKeyDown, 0x65, 0, false, -1));
        Assert.Equal([new KeyChange(Vk.F1 + 8, true, Repeat: true)], events.Decode(CG.EventKeyDown, 0x65, 0, true, -1));
        Assert.Equal([new KeyChange(Vk.F1 + 8, false)], events.Decode(CG.EventKeyUp, 0x65, 0, false, -1));
    }

    [Fact]
    public void MacInput_mouse_buttons_only_when_asked_and_never_the_left()
    {
        var keysOnly = new TapEvents(mouse: false);
        Assert.Empty(keysOnly.Decode(CG.EventRightMouseDown, -1, 0, false, 1));
        Assert.Empty(keysOnly.Decode(CG.EventOtherMouseDown, -1, 0, false, 3));

        var mouse = new TapEvents(mouse: true);
        Assert.Equal([new KeyChange(Vk.RButton, true)], mouse.Decode(CG.EventRightMouseDown, -1, 0, false, 1));
        Assert.Equal([new KeyChange(Vk.RButton, false)], mouse.Decode(CG.EventRightMouseUp, -1, 0, false, 1));
        Assert.Equal([new KeyChange(Vk.MButton, true)], mouse.Decode(CG.EventOtherMouseDown, -1, 0, false, 2));
        Assert.Equal([new KeyChange(Vk.XButton1, false)], mouse.Decode(CG.EventOtherMouseUp, -1, 0, false, 3));
        Assert.Equal([new KeyChange(Vk.XButton2, true)], mouse.Decode(CG.EventOtherMouseDown, -1, 0, false, 4));
        Assert.Empty(mouse.Decode(CG.EventOtherMouseDown, -1, 0, false, 7));
        Assert.Empty(mouse.Decode(1, -1, 0, false, 0)); // kCGEventLeftMouseDown

        // The tap's mask: never the left button (types 1 and 2), mouse buttons only when asked.
        var withMouse = TapEvents.Mask(true);
        var withoutMouse = TapEvents.Mask(false);
        Assert.Equal(0UL, withMouse & (CG.MaskBit(1) | CG.MaskBit(2)));
        Assert.Equal(CG.MaskBit(10) | CG.MaskBit(11) | CG.MaskBit(12), withoutMouse);
        Assert.Equal(withoutMouse | CG.MaskBit(3) | CG.MaskBit(4) | CG.MaskBit(25) | CG.MaskBit(26), withMouse);
    }

    // ---- The hotkey decision ----------------------------------------------------------------------------------------

    [Fact]
    public void MacInput_held_hotkey_with_autorepeat_signals_once_and_stays_swallowed()
    {
        var matcher = new HotkeyMatcher(HotkeyParser.Parse("F9"), suppress: true);
        var posted = new List<HotkeySignal>();
        void Post(HookEvent e) => posted.Add(e.Signal);

        Assert.False(EventTapHotkey.Decide(matcher, Post, new KeyChange(Vk.F1 + 8, true), enabled: true));
        for (var i = 0; i < 5; i++) Assert.False(EventTapHotkey.Decide(matcher, Post, new KeyChange(Vk.F1 + 8, true, Repeat: true), enabled: true));
        Assert.False(EventTapHotkey.Decide(matcher, Post, new KeyChange(Vk.F1 + 8, false), enabled: true));
        Assert.Equal([HotkeySignal.Down, HotkeySignal.Up], posted);

        // Other keys pass on untouched and signal nothing.
        Assert.True(EventTapHotkey.Decide(matcher, Post, new KeyChange('A', true), enabled: true));
        Assert.Equal(2, posted.Count);
    }

    [Fact]
    public void MacInput_right_option_hotkey_from_flags_changed()
    {
        var events = new TapEvents(mouse: false);
        var matcher = new HotkeyMatcher(HotkeyParser.Parse("RightAlt"), suppress: true);
        var posted = new List<HotkeySignal>();
        foreach (var change in events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, CG.FlagAlternate | CG.DeviceRAlternate, false, -1))
            Assert.False(EventTapHotkey.Decide(matcher, e => posted.Add(e.Signal), change, enabled: true));
        foreach (var change in events.Decode(CG.EventFlagsChanged, KeyCodeMap.MacRightOption, 0, false, -1))
            Assert.False(EventTapHotkey.Decide(matcher, e => posted.Add(e.Signal), change, enabled: true));
        Assert.Equal([HotkeySignal.Down, HotkeySignal.Up], posted);
    }

    [Fact]
    public void MacInput_tap_refused_without_accessibility_fails_cleanly_and_says_where_to_allow_it()
    {
        var log = new RecordingLog();
        using (var hotkey = new EventTapHotkey(new HotkeyMatcher(HotkeyParser.Parse("RightAlt"), suppress: true), _ => { }, log, trusted: () => false))
        {
            Assert.False(hotkey.Start());
            Assert.False(hotkey.Start()); // a second call answers the same, without a second thread
        }
        using (var monitor = new EventTapKeyMonitor(_ => { }, log, trusted: () => false)) Assert.False(monitor.Start());

        var errors = log.Lines.Where(l => l.Level == LogLevel.Error).Select(l => l.Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("System Settings > Privacy & Security > Accessibility", e, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("(hotkey listener)", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("(key monitor)", StringComparison.Ordinal));
    }

    // ---- CGEventSender ----------------------------------------------------------------------------------------------

    [Fact]
    public void MacInput_cmd_v_is_command_down_v_down_v_up_command_up()
    {
        Assert.Equal(
            [
                new CGEventSender.KeyEvent(0x37, true, CG.FlagCommand),
                new CGEventSender.KeyEvent(0x09, true, CG.FlagCommand),
                new CGEventSender.KeyEvent(0x09, false, CG.FlagCommand),
                new CGEventSender.KeyEvent(0x37, false, 0),
            ],
            CGEventSender.CmdV);
    }

    [Fact]
    public void MacInput_typing_batches_chunks_line_breaks_and_surrogates()
    {
        static string Texts(IEnumerable<CGEventSender.KeyEvent[]> batches) =>
            string.Join("|", batches.Select(b => b[0].Text ?? (b[0].Key == KeyCodeMap.MacReturn ? "<Return>" : "?")));

        Assert.Equal("ab|<Return>|cd|<Return>|<Return>|e", Texts(CGEventSender.TypingEvents("ab\r\ncd\n\re")));
        var long45 = new string('x', 45);
        Assert.Equal($"{new string('x', 20)}|{new string('x', 20)}|xxxxx", Texts(CGEventSender.TypingEvents(long45)));

        // A surrogate pair that would straddle the 20-unit boundary starts the next chunk instead.
        var emoji = char.ConvertFromUtf32(0x1F600);
        var text = new string('y', 19) + emoji + "z";
        var batches = CGEventSender.TypingEvents(text).ToList();
        Assert.Equal([new string('y', 19), emoji + "z"], batches.Select(b => b[0].Text!));
        Assert.All(batches, b =>
        {
            Assert.Equal(2, b.Length);
            Assert.True(b[0].Down);
            Assert.False(b[1].Down);
            Assert.Equal(b[0].Text, b[1].Text);
            Assert.All(b, e => Assert.Equal(0UL, e.Flags)); // typed text never carries a held modifier
        });
        Assert.Empty(CGEventSender.TypingEvents(""));
    }

    [Fact]
    public async Task MacInput_sender_without_accessibility_posts_nothing_and_logs()
    {
        var log = new RecordingLog();
        var sender = new CGEventSender(log, trusted: () => false);
        sender.SendCtrlV();
        await sender.TypeTextAsync("hello");
        var warnings = log.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.StartsWith("Cmd+V not sent", warnings[0], StringComparison.Ordinal);
        Assert.StartsWith("typing not sent", warnings[1], StringComparison.Ordinal);
    }

    // ---- Pasteboard -------------------------------------------------------------------------------------------------

    [Fact]
    public void MacInput_pasteboard_payload_marks_the_item_out_of_history_only_when_asked()
    {
        var kept = Pasteboard.Payload("secret", keepOutOfHistory: true);
        Assert.Equal(["public.utf8-plain-text", "org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.source"], kept.Select(p => p.Type));
        Assert.Equal("secret", Encoding.UTF8.GetString(kept[0].Bytes));
        Assert.Empty(kept[1].Bytes);
        Assert.Empty(kept[2].Bytes);
        Assert.Equal("au.kynni.dictum", Encoding.UTF8.GetString(kept[3].Bytes));

        var allowed = Pasteboard.Payload("secret", keepOutOfHistory: false);
        Assert.Equal(["public.utf8-plain-text"], allowed.Select(p => p.Type));
        Assert.Equal(["public.utf8-plain-text", "public.rtf", "public.html"], Pasteboard.TextTypes);
    }

    /// <summary>
    /// A dictation is written current-host-only (NSPasteboardContentsCurrentHostOnly = 1 &lt;&lt; 0, NSPasteboard.h), so Universal
    /// Clipboard cannot hand it to the user's other devices; allowing clipboard history lifts it, as the same switch lets
    /// Windows upload to its cloud clipboard.
    /// </summary>
    [Fact]
    public void MacInput_pasteboard_dictation_is_current_host_only_unless_history_is_allowed()
    {
        Assert.Equal((nuint)1, Pasteboard.CurrentHostOnly);
        Assert.Equal(Pasteboard.CurrentHostOnly, Pasteboard.ContentsOptions(keepOutOfHistory: true));
        Assert.Equal((nuint)0, Pasteboard.ContentsOptions(keepOutOfHistory: false));
    }

    // ---- On a Mac ---------------------------------------------------------------------------------------------------

    [Fact]
    public void MacInput_key_state_answers_without_any_permission()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only (Core Graphics).");
        Assert.False(KeyState.IsDown(Vk.F1 + 19)); // F20: no test machine holds it down
        Assert.False(KeyState.IsDown(Vk.XButton2));
        Assert.False(KeyState.IsDown(0x2C)); // PrintScreen: no Mac key, false without a native call
        _ = KeyState.IsDown(Vk.Control); // the generic code expands to both sides without throwing
    }

    [Fact]
    public void MacInput_pasteboard_sets_and_restores_text()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 on a Mac (uses the real pasteboard, restores its text after).");
        var board = new Pasteboard();
        var original = board.Snapshot();
        try
        {
            board.SetText("Dictum pasteboard test ✓", keepOutOfHistory: true);
            var set = board.Snapshot();
            Assert.Equal("Dictum pasteboard test ✓", Encoding.UTF8.GetString((byte[])set.Formats[Pasteboard.PlainText]));
            Assert.Equal(PasteboardTypes(), PasteboardTypes().Union([Pasteboard.ConcealedType, Pasteboard.TransientType, Pasteboard.SourceType]).ToHashSet());

            board.Restore(new ClipboardSnapshot(new Dictionary<string, object> { [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("restored"), [Pasteboard.Html] = Encoding.UTF8.GetBytes("<b>restored</b>") }));
            var restored = board.Snapshot();
            Assert.Equal("restored", Encoding.UTF8.GetString((byte[])restored.Formats[Pasteboard.PlainText]));
            Assert.Equal("<b>restored</b>", Encoding.UTF8.GetString((byte[])restored.Formats[Pasteboard.Html]));
            Assert.DoesNotContain(Pasteboard.ConcealedType, PasteboardTypes());

            // A type the pasteboard refuses (the legacy plain-text name, refused on macOS 27.2) is left out; the rest is
            // still written, never an empty pasteboard.
            board.Restore(new ClipboardSnapshot(new Dictionary<string, object> { ["NSStringPboardType"] = Encoding.UTF8.GetBytes("legacy"), [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("kept") }));
            Assert.Equal("kept", Encoding.UTF8.GetString((byte[])board.Snapshot().Formats[Pasteboard.PlainText]));
        }
        finally
        {
            board.Restore(original);
        }
    }

    /// <summary>Every type name on the general pasteboard now.</summary>
    private static HashSet<string> PasteboardTypes()
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var types = ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard")), ObjC.Sel("types"));
        var count = (int)ObjC.Send(types, ObjC.Sel("count"));
        return [.. Enumerable.Range(0, count).Select(i => ObjC.FromNSString(ObjC.Send(types, ObjC.Sel("objectAtIndex:"), i))!)];
    }

    [Fact]
    public void MacInput_monitor_sees_a_posted_key_and_skips_dictums_own()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 on a Mac (installs an event tap, posts F20 system-wide).");
        Assert.SkipUnless(AX.IsProcessTrusted(), "This process is not allowed under System Settings > Privacy & Security > Accessibility.");

        var seen = new BlockingCollection<RawKeyEvent>();
        using var monitor = new EventTapKeyMonitor(seen.Add, NullLog.Instance, trusted: () => true);
        Assert.True(monitor.Start());

        // Dictum's own events (the marker) are skipped; an unmarked posted event is reported as injected.
        new CGEventSender(NullLog.Instance).SendCtrlV();
        PostF20(marked: false);
        Assert.True(seen.TryTake(out var down, TimeSpan.FromSeconds(2)));
        Assert.Equal(new RawKeyEvent(Vk.F1 + 19, true, true), down);
        Assert.True(seen.TryTake(out var up, TimeSpan.FromSeconds(2)));
        Assert.Equal(new RawKeyEvent(Vk.F1 + 19, false, true), up);
        Assert.False(seen.TryTake(out _, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void MacInput_hotkey_tap_swallows_and_signals_a_posted_hotkey()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 on a Mac (installs an event tap, posts F20 system-wide; swallowed by the tap).");
        Assert.SkipUnless(AX.IsProcessTrusted(), "This process is not allowed under System Settings > Privacy & Security > Accessibility.");

        var signals = new BlockingCollection<HookEvent>();
        using var hotkey = new EventTapHotkey(new HotkeyMatcher(HotkeyParser.Parse("F20"), suppress: true), signals.Add, NullLog.Instance, trusted: () => true);
        Assert.True(hotkey.Start());
        PostF20(marked: false);
        Assert.True(signals.TryTake(out var down, TimeSpan.FromSeconds(2)));
        Assert.Equal(HotkeySignal.Down, down.Signal);
        Assert.True(signals.TryTake(out var up, TimeSpan.FromSeconds(2)));
        Assert.Equal(HotkeySignal.Up, up.Signal);
    }

    private static void PostF20(bool marked)
    {
        foreach (var keyDown in new[] { true, false })
        {
            var ev = CG.EventCreateKeyboardEvent(0, 0x5A, keyDown);
            if (marked) CG.EventSetIntegerValueField(ev, CG.FieldSourceUserData, EventTap.InjectedMarker);
            CG.EventPost(CG.HidEventTap, ev);
            CF.Release(ev);
        }
    }
}
