using System.Collections.Concurrent;
using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Fix batch, items 17 and 18 (#1, #2, #57, #59, #82, #84): overlapping hotkeys, lost key-ups, a lost microphone, capture timing.</summary>
public class FixBatchHotkeyTests
{
    private const int LCtrl = 0xA2, RCtrl = 0xA3, LWin = 0x5B, Escape = 0x1B;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Ctrl+Space", "Ctrl+Shift+Space", true)] // the natural "same chord plus Shift"
    [InlineData("Ctrl+Shift+Space", "Shift+Ctrl+Space", true)] // same keys, other order
    [InlineData("LeftCtrl+Space", "Ctrl+Space", true)] // a differently named subset
    [InlineData("RightCtrl", "RightCtrl+F", true)] // the default hotkey inside the fix chord
    [InlineData("RightCtrl", "Ctrl+Shift+F", true)] // pressed with the right-hand Ctrl
    [InlineData("Ctrl+Win", "Ctrl+Win+Space", true)]
    [InlineData("RightCtrl", "LeftCtrl+Shift+F", false)]
    [InlineData("Ctrl+Space", "Alt+Space", false)]
    [InlineData("F9", "F10", false)]
    [InlineData("RightAlt", "Ctrl+Shift+F", false)]
    public void Hotkeys_that_one_press_can_complete_together_are_found(string a, string b, bool together)
    {
        var (x, y) = (HotkeyParser.Parse(a), HotkeyParser.Parse(b));
        Assert.Equal(together, x.CanFireTogether(y));
        Assert.Equal(together, y.CanFireTogether(x));
    }

    [Fact]
    public void A_fix_hotkey_that_overlaps_the_hotkey_is_ignored_with_both_named()
    {
        var load = SettingsLoader.Parse("""{ "hotkey": "Ctrl+Space", "fix_hotkey": "Ctrl+Shift+Space" }""");
        Assert.Null(load.Settings.FixHotkey);
        var warning = Assert.Single(load.Warnings, w => w.Contains("fix_hotkey", StringComparison.Ordinal));
        Assert.Contains("Ctrl+Shift+Space", warning, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Space", warning, StringComparison.Ordinal);
        Assert.Equal("LeftCtrl+Shift+F", SettingsLoader.Parse("""{ "fix_hotkey": "LeftCtrl+Shift+F" }""").Settings.FixHotkey);
    }

    [Theory]
    [InlineData("Ctrl+RightCtrl", "Ctrl", "RightCtrl")]
    [InlineData("Shift+RightShift", "Shift", "RightShift")]
    [InlineData("Win+LeftWin+Space", "Win", "LeftWin")]
    public void A_chord_that_names_one_key_twice_under_two_names_is_refused(string text, string first, string second)
    {
        Assert.False(HotkeyParser.TryParse(text, out _, out var error));
        Assert.Contains(first, error, StringComparison.Ordinal);
        Assert.Contains(second, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_press_after_a_lost_key_up_starts_a_new_hold()
    {
        var m = new HotkeyMatcher(HotkeyParser.Parse("RightCtrl"), suppress: true);
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(RCtrl, true, true, eventTimeMs: 1_000));
        for (var t = 1_500L; t <= 3_000; t += 33) Assert.Equal(new KeyDecision(true, HotkeySignal.None, false), m.OnKey(RCtrl, true, true, t)); // auto-repeat
        // Released on the secure desktop (UAC, Win+L): no key-up reached the hook. Minutes later the next press:
        Assert.Equal(new KeyDecision(true, HotkeySignal.Down, false), m.OnKey(RCtrl, true, true, eventTimeMs: 300_000));
        Assert.Equal(new KeyDecision(true, HotkeySignal.Up, false), m.OnKey(RCtrl, false, true, eventTimeMs: 300_400));
    }

    [Fact]
    public void An_auto_stop_forgets_the_held_key_so_the_next_press_records()
    {
        var m = new DictationStateMachine(StateMachineOptions.Default);
        m.HotkeyDown(0); // its key-up is lost
        Assert.Contains(m.Tick(300_000), e => e is BeginProcessingEffect { AutoStopped: true });
        m.ProcessingFinished(m.SessionId, ProcessingOutcome.Inserted);
        Assert.Contains(m.HotkeyDown(400_000), e => e is StartCaptureEffect);
    }

    [Fact]
    public void An_all_modifier_chord_pressed_in_either_order_never_swallows_a_modifier()
    {
        var m = new HotkeyMatcher(HotkeyParser.Parse("Ctrl+Win"), suppress: true);
        Assert.Equal(KeyDecision.Pass, m.OnKey(LCtrl, true, true));
        Assert.Equal(new KeyDecision(false, HotkeySignal.Down, true), m.OnKey(LWin, true, true)); // Win passes; the menu mask keeps Start shut
    }

    [Fact]
    public void The_hook_passes_Esc_through_to_the_focused_app()
    {
        var m = new HotkeyMatcher(HotkeyParser.Parse("RightCtrl"), suppress: true);
        Assert.True((m.OnKey(Escape, true, true)).PassesThrough);
        Assert.True((new KeyDecision(false, HotkeySignal.Escape, false)).PassesThrough);
        Assert.True((KeyDecision.Pass).PassesThrough);
        Assert.False((m.OnKey(RCtrl, true, true)).PassesThrough);
    }

    [Fact]
    public void A_microphone_lost_mid_recording_still_transcribes_what_was_captured()
    {
        var m = new DictationStateMachine(StateMachineOptions.Default);
        m.HotkeyDown(0);
        var lost = m.CaptureLost(m.SessionId);
        Assert.Contains(lost, e => e is BeginProcessingEffect); // logged by the controller as a lost microphone, not as an auto-stop
        Assert.Contains(lost, e => e is PillEffect { View: PillView.MicError });
        Assert.DoesNotContain(lost, e => e is DiscardCaptureEffect);

        var tap = new DictationStateMachine(StateMachineOptions.Default); // a lone tap is not a dictation yet: discarded as before
        tap.HotkeyDown(0);
        tap.HotkeyUp(100);
        Assert.Contains(tap.CaptureLost(tap.SessionId), e => e is DiscardCaptureEffect);
    }

    [Fact]
    public async Task The_controller_transcribes_a_recording_whose_microphone_was_lost()
    {
        var transcriber = new FakeTranscriber("hello there");
        var inserted = new ConcurrentQueue<string>();
        var settings = new AppSettings();
        var cleaner = TranscriptCleaner.RulesOnly("test");
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(transcriber), () => cleaner,
            (text, _) => { inserted.Enqueue(text); return Task.CompletedTask; }, () => settings, NullLog.Instance);
        var capture = new FakeCapture();
        var ui = new FakeUi();
        using var controller = new DictationController(new DictationStateMachine(StateMachineOptions.Default), capture, pipeline, ui, () => settings, NullLog.Instance);
        controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));
        var sw = Stopwatch.StartNew();
        while (!ui.Pills.Contains(PillView.RecordingHold) && sw.ElapsedMilliseconds < 3000) await Task.Delay(10, Ct);
        capture.RaiseFailed(); // a headset battery dies mid-dictation

        while (inserted.IsEmpty && sw.ElapsedMilliseconds < 5000) await Task.Delay(10, Ct);
        Assert.Equal(["Hello there."], inserted.ToArray());
        Assert.Contains(PillView.MicError, ui.Pills);
    }

    [Theory]
    [InlineData(10_000_000L, 12_500_000L, 10_000_000L, 250.0)] // QPC at 10 MHz: key-down at 1 s, first frame at 1.25 s
    [InlineData(3_000_000L, 12_500_000L, 3_000_000L, 250.0)] // QPC at 3 MHz: the stamp is converted, the position is already 100 ns
    [InlineData(20_000_000L, 19_000_000L, 10_000_000L, -100.0)] // captured from before key-down
    public void The_first_frame_is_timed_against_key_down_on_one_clock(long keyDownStamp, long qpcPosition, long frequency, double expectedMs) =>
        Assert.Equal(expectedMs, Dictum.Windows.Audio.WasapiCapture.FirstFrameAfterKeyDownMs(qpcPosition, keyDownStamp, frequency), precision: 6);
}
