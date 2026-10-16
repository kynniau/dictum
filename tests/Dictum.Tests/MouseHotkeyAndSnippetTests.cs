using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;
using Dictum.Windows.Input;
using Dictum.Windows.Interop;

namespace Dictum.Tests;

/// <summary>Mouse-button hotkeys (16/10/2026): the parser, the matcher, the capture logic and the Windows hook's decoding.</summary>
public class MouseHotkeyTests
{
    [Theory]
    [InlineData("RightMouse", Vk.RButton)]
    [InlineData("right click", Vk.RButton)]
    [InlineData("RButton", Vk.RButton)]
    [InlineData("MiddleMouse", Vk.MButton)]
    [InlineData("wheel click", Vk.MButton)]
    [InlineData("Mouse4", Vk.XButton1)]
    [InlineData("XButton1", Vk.XButton1)]
    [InlineData("Mouse5", Vk.XButton2)]
    [InlineData("xbutton2", Vk.XButton2)]
    public void Mouse_buttons_parse_to_their_virtual_keys(string text, int vk)
    {
        var spec = HotkeyParser.Parse(text);
        Assert.False(spec.IsChord);
        Assert.Equal([vk], spec.Trigger.Vks);
        Assert.False(spec.Trigger.IsModifier);
        Assert.True(spec.UsesMouse);
    }

    [Fact]
    public void Canonical_names_round_trip_through_NameOf()
    {
        Assert.Equal("RightMouse", HotkeyParser.NameOf(Vk.RButton));
        Assert.Equal("MiddleMouse", HotkeyParser.NameOf(Vk.MButton));
        Assert.Equal("Mouse4", HotkeyParser.NameOf(Vk.XButton1));
        Assert.Equal("Mouse5", HotkeyParser.NameOf(Vk.XButton2));
        Assert.Equal("LeftMouse", HotkeyParser.NameOf(Vk.LButton)); // named for the capture box, never accepted as a hotkey
        Assert.Equal("RightMouse", HotkeyParser.Parse("rbutton").Display);
        Assert.Null(HotkeyParser.SideOf(Vk.RButton));
    }

    [Theory]
    [InlineData("LeftMouse")]
    [InlineData("left click")]
    [InlineData("Mouse1")]
    [InlineData("0x01")]
    [InlineData("Ctrl+LeftMouse")]
    public void The_left_button_is_refused_with_the_reason(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains("left mouse button", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RightMouse", error);
    }

    [Fact]
    public void A_mouse_button_may_end_a_chord_but_never_modify_one()
    {
        var chord = HotkeyParser.Parse("Ctrl+Mouse4");
        Assert.True(chord.IsChord);
        Assert.True(chord.UsesMouse);
        Assert.False(chord.AllModifiers);
        Assert.Equal("Ctrl+Mouse4", chord.Display);
        Assert.False(HotkeyParser.TryParse("Mouse4+F9", out _, out var error));
        Assert.Contains("only the last key", error);
        Assert.False(HotkeyParser.Parse("RightCtrl").UsesMouse);
        Assert.False(HotkeyParser.Parse("Ctrl+Shift+Space").UsesMouse);
    }

    [Fact]
    public void The_unknown_key_message_offers_the_mouse_buttons()
    {
        Assert.False(HotkeyParser.TryParse("Banana", out _, out var error));
        Assert.Contains(HotkeyParser.MouseButtonNames, error);
    }

    [Fact]
    public void The_parser_does_not_state_the_unverified_button_swap_as_fact()
    {
        // DECISIONS (16/10/2026) lists "Windows swaps the buttons before WH_MOUSE_LL" as recalled, not verified; so must the source.
        var source = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "src", "Dictum.Core", "Hotkey", "HotkeyParser.cs"));
        Assert.DoesNotContain("follows the swap", source, StringComparison.Ordinal);
        Assert.Contains("whether Windows swaps before WH_MOUSE_LL is recalled, not verified", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_hotkeys_on_different_mouse_buttons_do_not_conflict_but_the_same_button_does()
    {
        Assert.False(HotkeyParser.Parse("RightMouse").CanFireTogether(HotkeyParser.Parse("Mouse5")));
        Assert.True(HotkeyParser.Parse("Mouse5").CanFireTogether(HotkeyParser.Parse("Ctrl+Mouse5")));
    }

    [Fact]
    public void A_mouse_hotkey_is_swallowed_on_down_and_up_so_no_context_menu_opens()
    {
        var m = new HotkeyMatcher(HotkeyParser.Parse("RightMouse"), suppress: true);
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(Vk.RButton, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(Vk.RButton, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(Vk.MButton, true, true)); // other buttons untouched
        Assert.Equal(KeyDecision.Pass, m.OnKey(Vk.MButton, false, true));
    }

    [Fact]
    public void A_mouse_button_is_swallowed_even_with_suppression_off()
    {
        // With suppress_hotkey false the click would reach the app under the pointer on release (a context menu, a browser
        // Back) and wreck the dictation, so a mouse-button trigger is always swallowed. A key still follows the setting.
        var m = new HotkeyMatcher(HotkeyParser.Parse("Mouse5"), suppress: false);
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(Vk.XButton2, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(Vk.XButton2, false, true));

        var chord = new HotkeyMatcher(HotkeyParser.Parse("Ctrl+Mouse4"), suppress: false);
        Assert.Equal(KeyDecision.Pass, chord.OnKey(Vk.LControl, true, true)); // a modifier is never swallowed
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), chord.OnKey(Vk.XButton1, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), chord.OnKey(Vk.XButton1, false, true));

        var key = new HotkeyMatcher(HotkeyParser.Parse("F24"), suppress: false);
        Assert.Equal(new KeyDecision(false, HotkeySignal.Down, false), key.OnKey(Vk.F1 + 23, true, true));
        Assert.Equal(new KeyDecision(false, HotkeySignal.Up, false), key.OnKey(Vk.F1 + 23, false, true));
    }

    [Fact]
    public void A_keyboard_and_mouse_chord_swallows_only_the_button()
    {
        var m = new HotkeyMatcher(HotkeyParser.Parse("Ctrl+Mouse4"), suppress: true);
        Assert.Equal(KeyDecision.Pass, m.OnKey(Vk.XButton1, true, true)); // Ctrl not held: an ordinary click
        Assert.Equal(KeyDecision.Pass, m.OnKey(Vk.XButton1, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(Vk.LControl, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(Vk.XButton1, true, true));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(Vk.XButton1, false, true));
        Assert.Equal(KeyDecision.Pass, m.OnKey(Vk.LControl, false, true));
    }

    [Fact]
    public void The_capture_logic_names_a_mouse_gesture_and_the_left_button_names_nothing()
    {
        var capture = new HotkeyCapture();
        Assert.Null(capture.Feed(new RawKeyEvent(Vk.XButton2, true, false)));
        Assert.Equal("Mouse5 down", capture.LastReceived);
        Assert.Equal("Mouse5", capture.Feed(new RawKeyEvent(Vk.XButton2, false, false)));

        Assert.Null(capture.Feed(new RawKeyEvent(Vk.LButton, true, false)));
        Assert.Null(capture.Feed(new RawKeyEvent(Vk.LButton, false, false))); // "LeftMouse" parses to nothing
        Assert.Equal("LeftMouse up", capture.LastReceived);

        capture.Feed(new RawKeyEvent(Vk.LControl, true, false));
        capture.Feed(new RawKeyEvent(Vk.RButton, true, false));
        capture.Feed(new RawKeyEvent(Vk.RButton, false, false));
        Assert.Equal("LeftCtrl+RightMouse", capture.Feed(new RawKeyEvent(Vk.LControl, false, false)));
        Assert.True(HotkeyParser.TryParse("LeftCtrl+RightMouse", out _, out _));
    }

    [Fact]
    public void The_mouse_hook_struct_matches_the_x64_layout()
    {
        // MSLLHOOKSTRUCT: POINT pt (8), DWORD mouseData, DWORD flags, DWORD time, then ULONG_PTR dwExtraInfo at 24 (8-byte aligned).
        Assert.Equal(32, Marshal.SizeOf<Native.MSLLHOOKSTRUCT>());
        Assert.Equal(8, (int)Marshal.OffsetOf<Native.MSLLHOOKSTRUCT>(nameof(Native.MSLLHOOKSTRUCT.mouseData)));
        Assert.Equal(16, (int)Marshal.OffsetOf<Native.MSLLHOOKSTRUCT>(nameof(Native.MSLLHOOKSTRUCT.time)));
        Assert.Equal(24, (int)Marshal.OffsetOf<Native.MSLLHOOKSTRUCT>(nameof(Native.MSLLHOOKSTRUCT.dwExtraInfo)));
    }

    private sealed class LineLog : ILog
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Write(LogLevel level, string message) => Lines.Enqueue(message);

        public bool Saw(string fragment, TimeSpan wait)
        {
            var until = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < until)
            {
                if (Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal))) return true;
                Thread.Sleep(20);
            }
            return false;
        }
    }

    [Fact]
    public void The_mouse_hook_is_installed_only_for_a_hotkey_that_names_a_mouse_button()
    {
        var mouseLog = new LineLog();
        using (var mouse = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("Mouse5"), suppress: true), _ => { }, mouseLog))
        {
            var started = mouse.Start(); // the hook thread logs before Start returns
            Assert.SkipWhen(mouseLog.Lines.Any(l => l.Contains("Input hook (hotkey listener) installation failed", StringComparison.Ordinal)),
                "SetWindowsHookEx refused a keyboard hook in this session; nothing to check.");
            Assert.True(started, $"Start failed with the keyboard hook installed, so the mouse hook was refused: {string.Join(" | ", mouseLog.Lines)}");
            Assert.True(mouseLog.Saw("Mouse hook installed", TimeSpan.FromSeconds(2)));
        }

        var keyLog = new LineLog();
        using var key = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("F24"), suppress: true), _ => { }, keyLog);
        Assert.SkipWhen(!key.Start(), "SetWindowsHookEx refused a keyboard hook in this session; nothing to check.");
        Assert.True(keyLog.Saw("Input hook installed", TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(keyLog.Lines, l => l.Contains("Mouse hook", StringComparison.Ordinal));
    }

    private const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201; // winuser.h; WM_MOUSEMOVE and WM_LBUTTONDOWN pages on Microsoft Learn, read 16/10/2026

    /// <summary>Runs the hook's decoder on a real MSLLHOOKSTRUCT in unmanaged memory, as Windows hands it over.</summary>
    private static (bool Decoded, int Vk, bool Down, Native.MSLLHOOKSTRUCT Data) Decode(int message, uint mouseData)
    {
        var sent = new Native.MSLLHOOKSTRUCT { ptX = 10, ptY = 20, mouseData = mouseData, flags = Native.LLMHF_INJECTED, time = 1234, dwExtraInfo = 7 };
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(sent, p, fDeleteOld: false);
            var decoded = LowLevelHook.Button(message, p, out var vk, out var down, out var data);
            return (decoded, vk, down, data);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [Theory]
    [InlineData(Native.WM_RBUTTONDOWN, 0u, Vk.RButton, true)]
    [InlineData(Native.WM_RBUTTONUP, 0u, Vk.RButton, false)]
    [InlineData(Native.WM_MBUTTONDOWN, 0u, Vk.MButton, true)]
    [InlineData(Native.WM_MBUTTONUP, 0u, Vk.MButton, false)]
    [InlineData(Native.WM_XBUTTONDOWN, (uint)Native.XBUTTON1 << 16, Vk.XButton1, true)]
    [InlineData(Native.WM_XBUTTONUP, (uint)Native.XBUTTON1 << 16, Vk.XButton1, false)]
    [InlineData(Native.WM_XBUTTONDOWN, (uint)Native.XBUTTON2 << 16, Vk.XButton2, true)]
    [InlineData(Native.WM_XBUTTONUP, (uint)Native.XBUTTON2 << 16, Vk.XButton2, false)]
    public void The_mouse_hook_decodes_the_six_button_messages(int message, uint mouseData, int vk, bool down)
    {
        var (decoded, decodedVk, decodedDown, data) = Decode(message, mouseData);
        Assert.True(decoded);
        Assert.Equal((vk, down), (decodedVk, decodedDown));
        Assert.Equal((1234u, Native.LLMHF_INJECTED, (nuint)7), (data.time, data.flags, data.dwExtraInfo)); // the struct was read
    }

    [Theory]
    [InlineData(WmMouseMove, 0u)]
    [InlineData(WmLButtonDown, 0u)] // the left button is never a hotkey, so it is never decoded
    public void A_move_or_the_left_button_is_passed_on_without_reading_the_struct(int message, uint mouseData)
    {
        var (decoded, _, _, data) = Decode(message, mouseData);
        Assert.False(decoded);
        Assert.Equal(default(Native.MSLLHOOKSTRUCT), data);
    }

    [Fact]
    public void A_side_button_that_is_neither_XBUTTON1_nor_XBUTTON2_is_not_a_hotkey_button()
    {
        var (decoded, _, _, data) = Decode(Native.WM_XBUTTONDOWN, 3u << 16);
        Assert.False(decoded);
        Assert.Equal(1234u, data.time); // read (to learn which button), then refused
    }

    /// <summary>
    /// Calls a hook callback the way Windows does: HC_ACTION (0), the message as wParam, the struct in unmanaged memory. The
    /// hook is never started, so nothing is installed; a passed event goes to CallNextHookEx, which is not 1.
    /// </summary>
    private static nint Call<T>(Func<int, nint, nint, nint> proc, int message, T data) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(data, p, fDeleteOld: false);
            return proc(0, message, p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static Native.MSLLHOOKSTRUCT ButtonData(uint xButton = 0) => new() { mouseData = xButton << 16, time = 1000 };

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // a mouse button is swallowed whatever suppress_hotkey says
    public void The_mouse_hook_callback_swallows_the_hotkey_button_and_passes_the_others(bool suppress)
    {
        var events = new List<HookEvent>();
        using var hook = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("Mouse5"), suppress), events.Add, NullLog.Instance);
        Assert.Equal((nint)1, Call(hook.MouseProc, Native.WM_XBUTTONDOWN, ButtonData(Native.XBUTTON2)));
        Assert.Equal((nint)1, Call(hook.MouseProc, Native.WM_XBUTTONUP, ButtonData(Native.XBUTTON2)));
        Assert.NotEqual((nint)1, Call(hook.MouseProc, Native.WM_RBUTTONDOWN, ButtonData()));
        Assert.NotEqual((nint)1, Call(hook.MouseProc, Native.WM_RBUTTONUP, ButtonData()));
        Assert.NotEqual((nint)1, Call(hook.MouseProc, Native.WM_XBUTTONDOWN, ButtonData(Native.XBUTTON1))); // the other side button
        Assert.Equal([HotkeySignal.Down, HotkeySignal.Up], events.Select(e => e.Signal));
    }

    [Fact]
    public void The_keyboard_hook_callback_swallows_the_hotkey_when_told_and_passes_other_keys()
    {
        const int f24 = Vk.F1 + 23, a = 'A';
        static Native.KBDLLHOOKSTRUCT Key(int vk) => new() { vkCode = (uint)vk, time = 1000 };

        var events = new List<HookEvent>();
        using var hook = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("F24"), suppress: true), events.Add, NullLog.Instance);
        Assert.Equal((nint)1, Call(hook.KeyboardProc, Native.WM_KEYDOWN, Key(f24)));
        Assert.Equal((nint)1, Call(hook.KeyboardProc, Native.WM_KEYUP, Key(f24)));
        Assert.NotEqual((nint)1, Call(hook.KeyboardProc, Native.WM_KEYDOWN, Key(a)));
        Assert.NotEqual((nint)1, Call(hook.KeyboardProc, Native.WM_KEYUP, Key(a)));
        Assert.Equal([HotkeySignal.Down, HotkeySignal.Up], events.Select(e => e.Signal));

        // suppress_hotkey off: the key reaches the app and the hotkey still fires.
        var passed = new List<HookEvent>();
        using var open = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("F24"), suppress: false), passed.Add, NullLog.Instance);
        Assert.NotEqual((nint)1, Call(open.KeyboardProc, Native.WM_KEYDOWN, Key(f24)));
        Assert.NotEqual((nint)1, Call(open.KeyboardProc, Native.WM_KEYUP, Key(f24)));
        Assert.Equal([HotkeySignal.Down, HotkeySignal.Up], passed.Select(e => e.Signal));
    }

    [Fact]
    public void Hook_thread_sees_an_injected_side_button_press()
    {
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 (injects a Mouse4 click system-wide; swallowed by the hook, so nothing under the pointer sees it).");

        var events = new BlockingCollection<HookEvent>();
        using var hook = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("Mouse4"), suppress: true), events.Add, NullLog.Instance);
        Assert.True(hook.Start(), "SetWindowsHookEx failed");

        var inputs = new Native.INPUT[2];
        inputs[0].type = Native.INPUT_MOUSE;
        inputs[0].U.mi = new Native.MOUSEINPUT { mouseData = Native.XBUTTON1, dwFlags = Native.MOUSEEVENTF_XDOWN };
        inputs[1].type = Native.INPUT_MOUSE;
        inputs[1].U.mi = new Native.MOUSEINPUT { mouseData = Native.XBUTTON1, dwFlags = Native.MOUSEEVENTF_XUP };
        var sent = Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
        // Windows refuses injection while the session has no input desktop (locked, or a minimised RDP client).
        Assert.SkipWhen(sent == 0, $"SendInput injected nothing (Win32 error {Marshal.GetLastWin32Error()}): no interactive input desktop right now.");
        Assert.Equal(2u, sent);

        Assert.True(events.TryTake(out var down, TimeSpan.FromSeconds(3)), "no hotkey-down event from the hook thread");
        Assert.True(events.TryTake(out var up, TimeSpan.FromSeconds(3)), "no hotkey-up event from the hook thread");
        Assert.Equal(HotkeySignal.Down, down.Signal);
        Assert.Equal(HotkeySignal.Up, up.Signal);
        Assert.False(events.TryTake(out var extra, TimeSpan.FromMilliseconds(300)), $"unexpected extra event {extra}");
    }
}

/// <summary>
/// Keyboard reach in the "Press your hotkey" boxes (16/10/2026): a keyboard user tabs to the box's buttons and presses them
/// with Enter or Space, so Tab never becomes a hotkey and a bare Enter or Space names none in the box.
/// </summary>
public class HotkeyKeyboardReachTests
{
    [Theory]
    [InlineData("Tab")]
    [InlineData("tab")]
    [InlineData("Ctrl+Tab")]
    [InlineData("0x09")]
    [InlineData("vk09")]
    public void Tab_is_refused_like_Esc(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains(HotkeyParser.TabMessage, error);
    }

    /// <summary>Presses the keys in order, releases them in reverse, and returns what the last release named.</summary>
    private static string? Gesture(HotkeyCapture capture, params int[] vks)
    {
        string? named = null;
        foreach (var vk in vks) named = capture.Feed(new RawKeyEvent(vk, true, false));
        for (var i = vks.Length - 1; i >= 0; i--) named = capture.Feed(new RawKeyEvent(vks[i], false, false));
        return named;
    }

    [Fact]
    public void Tab_in_any_gesture_and_a_bare_Enter_or_Space_name_no_hotkey()
    {
        var capture = new HotkeyCapture();
        Assert.Null(Gesture(capture, Vk.Tab));
        Assert.Equal("Tab up", capture.LastReceived); // still shown, so the user sees what arrived
        Assert.Null(Gesture(capture, Vk.LShift, Vk.Tab)); // Shift+Tab moves focus back
        Assert.Null(Gesture(capture, Vk.LControl, Vk.Tab));
        Assert.Null(Gesture(capture, Vk.Return));
        Assert.Null(Gesture(capture, Vk.Space));

        Assert.Equal("LeftCtrl+LeftShift+Space", Gesture(capture, Vk.LControl, Vk.LShift, Vk.Space)); // a chord still names one
        Assert.Equal("LeftCtrl+Enter", Gesture(capture, Vk.LControl, Vk.Return));
        Assert.Equal("F9", Gesture(capture, Vk.F1 + 8));
    }
}

/// <summary>Snippets (SPEC §16.6): snippets.txt, pasted exactly as written, after corrections and the profanity filter.</summary>
public class SnippetTests
{
    private static Corrections Snippets(params string[] lines) => new(Corrections.Parse(lines, null, "snippets.txt", lineBreaks: true), verbatim: true);

    /// <summary>The service behind the Add-a-snippet window, over a scratch profile, with the profanity mode given.</summary>
    private static PersonalService Service(Scratch scratch, string profanityMode = ProfanitySettings.Mask)
    {
        var settings = new AppSettings { Profanity = new ProfanitySettings { Mode = profanityMode } };
        var log = NullLog.Instance;
        var coordinator = new ModelCoordinator(scratch.Paths, new ModelManager(log), new LocalLlmManager(log), new HttpClient(), log, () => settings, null,
            new ScriptedPrompts(), _ => null, () => { }, () => { }, () => { }, (_, _) => { }, gpuMachine: () => false);
        var personal = new PersonalService(scratch.Paths, log, () => settings, coordinator, reload: () => { }, gpuMachine: () => false);
        personal.Load();
        return personal;
    }

    [Fact]
    public void A_snippet_is_pasted_exactly_even_at_a_sentence_start()
    {
        var snippets = Snippets("my email => kyle@example.com", "my calendly => https://calendly.com/example/30min");
        Assert.True(snippets.Verbatim);
        Assert.Equal("https://calendly.com/example/30min works. Email kyle@example.com.", snippets.Apply("My calendly works. Email MY EMAIL."));
        // Whole words: "my emails" is another word, while "my email address" contains the phrase and is expanded.
        Assert.Equal("Email, my emails and kyle@example.com address.", snippets.Apply("Email, my emails and my email address."));

        // The same line as a correction would capitalise the URL at a sentence start: that is why snippets are verbatim.
        var correction = new Corrections(Corrections.Parse(["my calendly => https://calendly.com/example/30min"]));
        Assert.Equal("Https://calendly.com/example/30min works.", correction.Apply("My calendly works."));
    }

    [Fact]
    public void Line_breaks_are_written_as_backslash_n_and_read_back()
    {
        var line = Corrections.SnippetLine(" my signature ", "Kyle Manson\r\nCFC Finance\n0400 000 000");
        Assert.Equal(@"my signature => Kyle Manson\nCFC Finance\n0400 000 000", line);
        Assert.Equal("Regards,\nKyle Manson\nCFC Finance\n0400 000 000", Snippets(line).Apply("Regards,\nmy signature"));
        Assert.Equal(@"a\nb", new Corrections(Corrections.Parse([@"x => a\nb"])).Apply("x")); // corrections.txt keeps the characters
    }

    [Fact]
    public void Snippets_run_last_so_nothing_rewrites_the_pasted_text_and_a_correction_can_repair_the_trigger()
    {
        var corrections = new Corrections(Corrections.Parse(["kinney => Kynni", "calendar lee => calendly"]));
        var profanity = new ProfanityFilter(["damn"], "test");
        var snippets = Snippets("my calendly => https://calendly.com/kinney/30min", "my motto => damn the torpedoes");
        var personal = new PersonalText(PersonalDictionary.Empty, corrections, profanity, snippets);
        var mask = new ProfanitySettings { Mode = ProfanitySettings.Mask };

        Assert.Equal("Book Kynni at https://calendly.com/kinney/30min.", personal.Finish("Book kinney at my calendar lee.", mask));
        Assert.Equal("D*** it. damn the torpedoes", personal.Finish("Damn it. my motto", mask)); // dictated word masked, typed text kept
        Assert.Contains("snippets 2", personal.Summary(mask));
    }

    [Fact]
    public void A_bad_line_is_reported_under_the_file_name_without_its_text()
    {
        var warnings = new List<string>();
        Assert.Empty(Corrections.Parse(["no arrow in this line"], warnings.Add, "snippets.txt", lineBreaks: true).ToList());
        var warning = Assert.Single(warnings);
        Assert.StartsWith("snippets.txt:", warning);
        Assert.DoesNotContain("no arrow", warning);
    }

    [Fact]
    public void The_file_loads_verbatim_a_missing_file_is_empty_and_it_is_registered_with_the_personal_files()
    {
        using var scratch = new Scratch();
        Assert.Empty(Corrections.LoadSnippets(scratch.Paths.SnippetsFile).Rules);
        Assert.Equal(PersonalText.Empty.Snippets.Rules.Count, PersonalText.Load(scratch.Paths, _ => { }).Snippets.Rules.Count);

        TextList.Append(scratch.Paths.SnippetsFile, [Corrections.SnippetLine("my email", "kyle@example.com")], Corrections.SnippetsHeader);
        var loaded = Corrections.LoadSnippets(scratch.Paths.SnippetsFile);
        Assert.True(loaded.Verbatim);
        Assert.Equal("kyle@example.com", Assert.Single(loaded.Rules).Written);
        Assert.StartsWith("# Dictum snippets", File.ReadAllText(scratch.Paths.SnippetsFile));

        Assert.Contains(PathRegistry.Locations(scratch.Paths, new AppSettings()),
            l => l.Path == scratch.Paths.SnippetsFile && l.Category == DataCategory.PersonalFiles);
    }

    [Theory]
    [InlineData("", "x", "Type the words")]
    [InlineData("# note", "x", "comment")]
    [InlineData("a => b", "x", "=>")]
    [InlineData("...", "x", "letter or a digit")]
    [InlineData("my email", "", "Type what")]
    [InlineData("my\nemail", "x", "one line")] // the phrase is one line of snippets.txt: a break in it would split the line
    [InlineData("my\r\nemail", "x", "one line")]
    [InlineData("my email\r", "x", "one line")]
    public void A_snippet_needs_a_one_line_phrase_with_a_word_in_it_and_some_text(string phrase, string text, string fragment)
    {
        using var scratch = new Scratch();
        Assert.Contains(fragment, Service(scratch).CheckNewSnippet(phrase, text));
    }

    [Fact]
    public void A_good_snippet_passes_the_check()
    {
        using var scratch = new Scratch();
        Assert.Null(Service(scratch).CheckNewSnippet("my email", "kyle@example.com"));
    }

    [Fact]
    public void A_phrase_the_profanity_filter_would_change_is_refused()
    {
        // The filter runs before the snippets, so a masked or removed phrase could never match.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.ProfanityOverrideFile, "damn\n");
        Assert.Equal("The profanity filter would change these words before the snippet runs; choose another phrase.",
            Service(scratch, ProfanitySettings.Mask).CheckNewSnippet("damn it", "x"));
        Assert.NotNull(Service(scratch, ProfanitySettings.Remove).CheckNewSnippet("Damn it", "x"));
        Assert.Null(Service(scratch, ProfanitySettings.Mask).CheckNewSnippet("darn it", "x"));
        Assert.Null(Service(scratch, ProfanitySettings.Off).CheckNewSnippet("damn it", "x")); // the filter is off: it changes nothing
    }

    [Fact]
    public void A_phrase_a_correction_would_change_is_refused_but_a_change_of_case_passes()
    {
        // Corrections also run before the snippets: "my e mail" is "my email" by then, so its snippet could never fire.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.CorrectionsFile, "e mail => email\nkynni => Kynni\n");
        var personal = Service(scratch);
        Assert.Equal("A correction in corrections.txt changes these words before the snippet runs; use the corrected form as the phrase.",
            personal.CheckNewSnippet("my e mail", "kyle@example.com"));
        Assert.Equal("Send it to my email.", personal.Finish("Send it to my e mail.")); // why: the snippet would see this
        Assert.Null(personal.CheckNewSnippet("my email", "kyle@example.com")); // the corrected form

        Assert.Null(personal.CheckNewSnippet("my kynni link", "https://kynni.au")); // a correction that only changes case
        personal.AddSnippet("my kynni link", "https://kynni.au");
        personal.Load(); // this service's reload callback does nothing
        Assert.Equal("Book at https://kynni.au.", personal.Finish("Book at my kynni link.")); // snippets match any case: it fires
    }

    [Fact]
    public void A_backslash_is_written_as_two_and_read_back_as_typed()
    {
        var line = Corrections.SnippetLine("my share", @"\\server\new-clients");
        Assert.Equal(@"my share => \\\\server\\new-clients", line); // without the escape, "\new" would read back as a line break
        Assert.Equal(@"Save it to \\server\new-clients.", Snippets(line).Apply("Save it to my share."));
        Assert.Equal("a\\nb\nc", Snippets(Corrections.SnippetLine("x", "a\\nb\nc")).Apply("x")); // a typed \n stays two characters
    }

    [Fact]
    public void A_snippet_keeps_a_line_break_at_either_end_or_one_that_is_all_of_it()
    {
        var newLine = Snippets(@"new line => \n");
        Assert.Equal("\n", Assert.Single(newLine.Rules).Written);
        Assert.Equal("Thanks \n Kyle", newLine.Apply("Thanks new line Kyle")); // the spaces around the phrase stay where they were
        Assert.Equal("\nKyle Manson\n", Snippets(@"my sign off => \nKyle Manson\n").Apply("my sign off"));
        Assert.Equal("Kynni", new Corrections(Corrections.Parse(["kinney =>   Kynni"])).Rules.Single().Written); // corrections still trim
    }

    [Fact]
    public async Task The_clean_up_sees_the_phrase_as_spoken_and_the_paste_has_the_snippet_text()
    {
        const string raw = "send it to my email please";
        var client = FakeLlmClient.Replying("Send it to my email, please.");
        var cleaner = new TranscriptCleaner(client, CleanupPrompt.Default, TimeSpan.FromSeconds(1), NullLog.Instance, "fake");
        var personal = new PersonalText(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty, Snippets("my email => kyle@example.com"));
        var inserted = new List<string>();
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber(raw)), () => cleaner,
            (text, _) =>
            {
                inserted.Add(text);
                return Task.CompletedTask;
            },
            () => new AppSettings(), NullLog.Instance, finish: text => personal.Finish(text, new ProfanitySettings()));

        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(new CapturedAudio(new float[32000], 0.2f), CancellationToken.None, _ => { }));
        Assert.Equal(1, client.Calls);
        Assert.Equal(raw, client.LastUser); // the clean-up provider received the spoken phrase
        Assert.DoesNotContain("kyle@example.com", client.LastUser + client.LastSystem, StringComparison.Ordinal); // never the snippet
        Assert.Equal(["Send it to kyle@example.com, please."], inserted);
    }
}

/// <summary>The Settings page's two capture boxes and the Add-a-snippet window, on Avalonia's headless platform.</summary>
public class MouseHotkeyAndSnippetUiTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static Button FindButton(ILogical root, string label) => All<Button>(root).First(b => b.Content as string == label);

    /// <summary>Clicks a button the way a user can: only when it is enabled (raising the event would bypass that).</summary>
    private static void Click(ILogical root, string label)
    {
        var button = FindButton(root, label);
        Assert.True(button.IsEffectivelyEnabled, $"\"{label}\" is disabled, so a user could not press it.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Select(t => t.Text));

    /// <summary>Runs the UI thread's queued work until <paramref name="condition"/> holds (a model loads on a worker thread).</summary>
    private static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [Fact]
    public Task A_mouse_hotkey_lets_its_button_through_until_a_speech_model_is_ready() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "RightMouse" }""");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);
        var (shell, platform, _) = scratch.Shell(models);
        shell.Reload(userRequested: true); // no model file: nothing loads (the first-run download, a failed load)
        var mouse = platform.LastListener!;
        Assert.True(mouse.Matcher.Spec.UsesMouse);
        Assert.False(shell.SpeechReady);
        Assert.False(mouse.Enabled); // the button keeps its normal job while there is nothing to dictate with

        File.WriteAllText(scratch.Paths.SettingsFile, """{ "hotkey": "F24" }""");
        shell.Reload(userRequested: false); // the hotkey changed: a new listener
        var key = platform.LastListener!;
        Assert.NotSame(mouse, key);
        Assert.True(key.Enabled); // a keyboard hotkey stays on (the controller ignores a press while no model is ready)

        File.WriteAllText(scratch.Paths.SettingsFile, """{ "hotkey": "RightMouse" }""");
        shell.Reload(userRequested: false);
        mouse = platform.LastListener!;
        Assert.False(mouse.Enabled);

        models.BeginLoad(Path.Combine(scratch.Paths.LocalDir, "fake-model.bin"), new WhisperSettings());
        Assert.True(Eventually(() => mouse.Enabled), "the mouse listener did not come on when the speech model became ready");
        Assert.True(shell.SpeechReady);

        using (shell.PauseHotkey()) Assert.False(mouse.Enabled); // a listening capture box still pauses it
        Assert.True(mouse.Enabled);
        shell.Dispose();
    });

    /// <summary>
    /// The same rule with a dictation under way (lens 2, 16/10/2026), so on a started shell: the real controller, with the
    /// hotkey routed through the shell and each tray state reported back to it. The test above cannot have a dictation: its
    /// shell is never started, so it has no controller.
    /// </summary>
    [Fact]
    public Task A_mouse_hotkey_stays_on_through_a_dictation_while_a_model_loads_and_goes_off_when_it_ends() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "Mouse4", "whisper": { "model": "base.en" }, "llm": { "provider": "none" }, "pill": { "enabled": false } }""");
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        using var loads = new ManualResetEventSlim(initialState: true); // a load finishes at once unless the test holds it
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) =>
        {
            loads.Wait();
            return new FakeSpeechEngine(p, d);
        }, warmUp: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model));
        try
        {
            Assert.True(shell.Start(lifetime: null));
            var mouse = platform.LastListener!;
            Assert.True(mouse.Matcher.Spec.UsesMouse);
            Assert.True(Eventually(() => mouse.Enabled), "the mouse listener did not come on when the speech model became ready");

            // Event times a minute ahead of the controller's own clock, so its tick can expire nothing while the test runs.
            var t0 = Environment.TickCount64 + 60_000;

            // Hands-free on Mouse4 (tap, tap); then a model load starts (a download finishing, a model swap).
            foreach (var (signal, at) in new[] { (HotkeySignal.Down, 0), (HotkeySignal.Up, 50), (HotkeySignal.Down, 150), (HotkeySignal.Up, 200) })
                mouse.Post(new HookEvent(signal, t0 + at));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            loads.Reset();
            models.BeginLoad(model, new WhisperSettings());
            Dispatcher.UIThread.RunJobs(); // the shell hears the load start
            Assert.False(shell.SpeechReady);
            Assert.True(mouse.Enabled, "a model load mid-dictation turned the listener off: the stop tap would reach the app and Esc would post nothing");

            // Esc ends it with no model ready: the button goes back to its normal job until the model is ready again.
            mouse.Post(new HookEvent(HotkeySignal.Escape, t0 + 300));
            Assert.True(Eventually(() => !mouse.Enabled), "the listener stayed on after the dictation ended with no speech model ready");
            Assert.True(shell.DictationIdle);
            loads.Set();
            Assert.True(Eventually(() => mouse.Enabled), "the listener did not come back when the load finished");

            // A dictation that ends in processing: the model is removed mid-hold (Settings > Storage), the release stops the
            // recording, the transcription fails for want of a model, and the listener goes off as the dictation ends.
            mouse.Post(new HookEvent(HotkeySignal.Down, t0 + 1_000));
            Assert.True(Eventually(() => !shell.DictationIdle), "the second dictation did not start");
            models.Unload();
            Dispatcher.UIThread.RunJobs();
            Assert.False(shell.SpeechReady);
            Assert.True(mouse.Enabled, "removing the model mid-hold turned the listener off: the release would be lost");
            mouse.Post(new HookEvent(HotkeySignal.Up, t0 + 2_000));
            Assert.True(Eventually(() => !mouse.Enabled), "the listener stayed on after a dictation that failed with no speech model");
            Assert.True(shell.DictationIdle);
        }
        finally
        {
            loads.Set(); // never leave a load blocked on a worker thread
            shell.Dispose();
        }
    });

    [Fact]
    public Task A_fix_hotkey_listener_the_system_refuses_is_disposed() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var fix = platform.Listeners.Single(l => l.Matcher.Spec.Display == "F23");
        Assert.True(fix.Disposed); // its thread and keyboard hook must not live on unreferenced
        shell.Dispose();
    });

    [Fact]
    public Task Quitting_closes_the_add_a_snippet_window() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.OpenAddSnippet();
        var window = (Window)typeof(AppShell).GetField("_addSnippet", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        shell.Dispose();
        Assert.True(closed);
    });

    [Fact]
    public Task A_box_with_no_new_capture_follows_an_edit_to_settings_json() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var page = All<GeneralPage>(window).Single();
        var boxes = All<HotkeyCaptureBox>(window).ToList();
        var (main, fix) = (boxes[0], boxes[1]);

        File.WriteAllText(scratch.Paths.SettingsFile, """{ "hotkey": "F21", "fix_hotkey": "F22" }""");
        shell.Reload(userRequested: false);
        page.Refresh();
        Assert.Equal("F22", fix.Hotkey);
        Assert.Contains("F22", Texts(fix));
        Assert.False(FindButton(window, "Use for Fix last dictation").IsEnabled);
        Assert.Equal("F21", main.Hotkey);
        Assert.False(FindButton(window, "Use this hotkey").IsEnabled);

        // A key captured but not saved yet is kept through the next edit, and can still be saved.
        fix.Start();
        platform.LastMonitor!.Press(Vk.XButton2, down: true);
        platform.LastMonitor!.Press(Vk.XButton2, down: false);
        Dispatcher.UIThread.RunJobs();
        fix.Stop();
        File.WriteAllText(scratch.Paths.SettingsFile, """{ "hotkey": "F21", "fix_hotkey": "F20" }""");
        shell.Reload(userRequested: false);
        page.Refresh();
        Assert.Equal("Mouse5", fix.Hotkey);
        Assert.True(FindButton(window, "Use for Fix last dictation").IsEnabled);

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Tab_and_a_bare_Enter_or_Space_in_a_listening_box_choose_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var main = All<HotkeyCaptureBox>(window).First();
        main.Start();
        foreach (var vk in new[] { Vk.Tab, Vk.Return, Vk.Space }) // tabbing to "Use this hotkey" and pressing it
        {
            platform.LastMonitor!.Press(vk, down: true);
            platform.LastMonitor!.Press(vk, down: false);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("F24", main.Hotkey);
        Assert.Contains("Windows received: Space up", Texts(main));
        Assert.False(FindButton(window, "Use this hotkey").IsEnabled);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_fix_hotkey_is_chosen_by_pressing_it_and_turned_off_with_a_button() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var boxes = All<HotkeyCaptureBox>(window).ToList();
        Assert.Equal(2, boxes.Count); // the dictation hotkey and the fix-last hotkey
        var fix = boxes[1];
        Assert.Contains("None", Texts(fix));
        Assert.Contains(GeneralPage.MouseNote, Texts(window));

        fix.Start();
        var monitor = platform.LastMonitor!;
        monitor.Press(Vk.XButton2, down: true);
        monitor.Press(Vk.XButton2, down: false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Mouse5", fix.Hotkey);
        Assert.Contains("Windows received: Mouse5 up", Texts(fix));

        Click(window, "Use for Fix last dictation");
        Assert.False(fix.Listening); // saving stops the box
        shell.Reload(userRequested: false);
        Assert.Equal("Mouse5", shell.Settings.FixHotkey);
        Assert.Contains("Mouse5 now opens Fix last dictation.", Texts(window));

        Click(window, "Turn off");
        shell.Reload(userRequested: false);
        Assert.Null(shell.Settings.FixHotkey);
        Assert.Equal("", fix.Hotkey);
        Assert.Contains("None", Texts(fix));

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_fix_hotkey_that_shares_the_dictation_button_is_refused() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "RightMouse" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var fix = All<HotkeyCaptureBox>(window).Last();
        fix.Start();
        platform.LastMonitor!.Press(Vk.RButton, down: true);
        platform.LastMonitor!.Press(Vk.RButton, down: false);
        Dispatcher.UIThread.RunJobs();
        Click(window, "Use for Fix last dictation");
        Assert.Contains("shares keys with the dictation hotkey", Texts(window));
        shell.Reload(userRequested: false);
        Assert.Null(shell.Settings.FixHotkey);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Only_one_capture_box_listens_at_a_time() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var boxes = All<HotkeyCaptureBox>(window).ToList();
        var (main, fix) = (boxes[0], boxes[1]);
        Assert.Equal("F23", fix.Hotkey);
        // Each box's Change… button is named for its own hotkey, so a screen reader can tell the two apart.
        var (mainChange, fixChange) = (All<Button>(main).Single(), All<Button>(fix).Single());
        Assert.Equal("Press your hotkey: change", AutomationProperties.GetName(mainChange));
        Assert.Equal("Press the key for Fix last dictation: change", AutomationProperties.GetName(fixChange));

        fix.Start();
        Assert.Equal("Press the key for Fix last dictation: stop listening", AutomationProperties.GetName(fixChange));
        var fixMonitor = platform.LastMonitor!;
        main.Start();
        Assert.True(main.Listening);
        Assert.False(fix.Listening);
        Assert.True(fixMonitor.Disposed);
        fix.Start();
        Assert.False(main.Listening);
        Assert.True(fix.Listening);
        var hooks = platform.Listeners.Where(l => !l.Disposed).ToList();
        Assert.Equal(2, hooks.Count);
        Assert.All(hooks, h => Assert.False(h.Enabled)); // both hotkeys stay paused while either box listens

        window.Close();
        Assert.All(hooks, h => Assert.True(h.Enabled));
        shell.Dispose();
    });

    [Fact]
    public Task Adding_a_snippet_writes_the_file_and_the_next_paste_uses_it() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new AddSnippetWindow(shell);
        window.Show();
        var fields = All<TextBox>(window).ToList();
        Assert.Equal(2, fields.Count);

        Click(window, "Add");
        Assert.Contains("Type the words you will say first.", Texts(window));
        Assert.False(File.Exists(scratch.Paths.SnippetsFile));

        fields[0].Text = "my email";
        fields[1].Text = "kyle@example.com";
        Click(window, "Add");
        Assert.Contains("Added.", Texts(window));
        Assert.Contains("my email => kyle@example.com", File.ReadAllText(scratch.Paths.SnippetsFile));
        Assert.Equal("Send it to kyle@example.com.", shell.Personal.Finish("Send it to my email."));
        Assert.Equal("", fields[0].Text);

        fields[0].Text = "My Email";
        fields[1].Text = "kyle@kynni.au\nKynni Pty Ltd";
        Click(window, "Add");
        Assert.Contains("Updated.", Texts(window)); // a later line for the same phrase wins
        Assert.Equal("kyle@kynni.au\nKynni Pty Ltd", shell.Personal.Finish("my email"));

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_line_break_typed_at_either_end_of_a_snippet_is_kept() => UiSession.Run(() =>
    {
        // The window says "pasted exactly as you type it here, line breaks included" (SPEC §16.6: never trimmed).
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new AddSnippetWindow(shell);
        window.Show();
        var fields = All<TextBox>(window).ToList();

        fields[0].Text = "my sign off";
        fields[1].Text = "\r\nKyle Manson\r\nKynni Pty Ltd\r\n";
        Click(window, "Add");
        Assert.Contains("Added.", Texts(window));
        Assert.Contains(@"my sign off => \nKyle Manson\nKynni Pty Ltd\n", File.ReadAllLines(scratch.Paths.SnippetsFile));
        Assert.Equal("Regards,\nKyle Manson\nKynni Pty Ltd\n", shell.Personal.Finish("Regards,my sign off"));

        // Spaces and tabs at either end are still dropped: a "phrase => text" line cannot hold them.
        fields[0].Text = "my email";
        fields[1].Text = " \tkyle@example.com\t ";
        Click(window, "Add");
        Assert.Contains("my email => kyle@example.com", File.ReadAllLines(scratch.Paths.SnippetsFile));

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Settings_says_a_mouse_button_hotkey_no_longer_reaches_other_apps() => UiSession.Run(() =>
    {
        // A quick click is swallowed too, not only a hold: the user must hear that before giving up right-click or Back.
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var texts = Texts(window);
        Assert.Contains("While Dictum is on and a speech model is ready, that button no longer reaches other apps at all (no right-click menu, " +
                        "no middle-click, no Back); a side button you do not use is the best choice.", texts);
        Assert.Contains("While Dictum is on, a mouse button chosen here no longer reaches other apps at all.", texts); // the fix-last box
        window.Close();
        shell.Dispose();
    });
}
