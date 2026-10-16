using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Overlays;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// A hands-free session typed part by part (product owner decision 5, 16/10/2026), through the real controller, state machine
/// and pipeline with a microphone that cuts when the test says (as the capture thread would) and a whisper stand-in.
/// </summary>
public class HandsFreeSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A microphone that cuts when told, hands back a set rest at the stop, and can be lost.</summary>
    private sealed class CuttingCapture : IAudioCapture
    {
        private Action<CapturedAudio, CutReason>? _part;
        private int _segmentCalls;
        private int _stops;

        public CapturedAudio Rest { get; set; } = CapturedAudio.Empty;
        public int SegmentCalls => Volatile.Read(ref _segmentCalls);
        public int Stops => Volatile.Read(ref _stops);
        public bool Cutting => Volatile.Read(ref _part) is not null;
        public float CurrentLevel => 0f;

        public event Action<Exception>? Failed;

        /// <summary>The device goes, as when a headset is unplugged: raised as the capture thread would.</summary>
        public void Lose() => Failed?.Invoke(new InvalidOperationException("the headset went flat"));

        public void Start(string? deviceSetting, long keyDownStamp) { }

        public CapturedAudio Stop()
        {
            Interlocked.Increment(ref _stops);
            Volatile.Write(ref _part, null);
            return Rest;
        }

        public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part)
        {
            Interlocked.Increment(ref _segmentCalls);
            Volatile.Write(ref _part, part);
        }

        public void Cut(CapturedAudio audio) => Volatile.Read(ref _part)!(audio, CutReason.Pause);
    }

    /// <summary>Part n of a session: 2 s plus n tenths, so whisper's stand-in knows which part it was given.</summary>
    private static CapturedAudio Part(int n) => new(new float[32000 + n * 1600], 0.2f);

    /// <summary>Whisper stand-in: each part's words by its length; records the carried words it was given; a part can be held or fail.</summary>
    private sealed class PartTranscriber(params string[] words) : ITranscriber
    {
        public ConcurrentQueue<(string Words, string? Carried)> Heard { get; } = new();
        public ConcurrentDictionary<int, TaskCompletionSource> Holds { get; } = new();
        public ConcurrentDictionary<int, bool> Fails { get; } = new();

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) => TranscribeAsync(samples, null, cancellationToken);

        public async Task<string> TranscribeAsync(float[] samples, string? carried, CancellationToken cancellationToken)
        {
            var n = (samples.Length - 32000) / 1600;
            Heard.Enqueue((words[n], carried));
            if (Holds.TryGetValue(n, out var hold)) await hold.Task.WaitAsync(cancellationToken);
            if (Fails.ContainsKey(n)) throw new InvalidOperationException("whisper failed on this part");
            return words[n];
        }
    }

    /// <summary>A clean-up model stand-in: a fixed reply per dictation; records what it was sent (the 3-argument call only, so carried words arrive the default way).</summary>
    private sealed class TableLlmClient(Dictionary<string, string> replies) : ILlmClient
    {
        public ConcurrentQueue<(string System, string User)> Sent { get; } = new();
        public string Name => "table";

        public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            Sent.Enqueue((systemPrompt, rawTranscript));
            return Task.FromResult(new LlmReply(replies[rawTranscript], "end_turn"));
        }
    }

    private sealed record Rig(DictationController Controller, CuttingCapture Capture, PartTranscriber Whisper, FakeUi Ui,
        ConcurrentQueue<string> Pasted, ConcurrentQueue<string> Remembered, DictationStateMachine Machine) : IDisposable
    {
        public void Dispose() => Controller.Dispose();
    }

    /// <summary>The trailing space is off by default here, so the separator each part is typed with shows in the pastes.</summary>
    /// <param name="finish">The step before the paste (corrections, the profanity filter, snippets); none by default.</param>
    private static Rig Make(PartTranscriber whisper, TranscriptCleaner? cleaner = null, StateMachineOptions? options = null, bool trailingSpace = false,
        Func<string, string>? finish = null)
    {
        var settings = new AppSettings { Insert = new InsertSettings { TrailingSpace = trailingSpace } };
        var capture = new CuttingCapture();
        var pasted = new ConcurrentQueue<string>();
        var remembered = new ConcurrentQueue<string>();
        var machine = new DictationStateMachine(options ?? StateMachineOptions.Default);
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(whisper), () => cleaner ?? TranscriptCleaner.RulesOnly("test"),
            (text, token) =>
            {
                token.ThrowIfCancellationRequested();
                pasted.Enqueue(text);
                return Task.CompletedTask;
            },
            () => settings, NullLog.Instance, finish, inserted: remembered.Enqueue, names: () => ["Kynni"]);
        var ui = new FakeUi();
        return new Rig(new DictationController(machine, capture, pipeline, ui, () => settings, NullLog.Instance), capture, whisper, ui, pasted, remembered, machine);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>Two taps: hands-free, which starts cutting.</summary>
    private static async Task StartHandsFree(Rig rig)
    {
        var t0 = Environment.TickCount64;
        rig.Controller.Post(new HookEvent(HotkeySignal.Down, t0));
        rig.Controller.Post(new HookEvent(HotkeySignal.Up, t0 + 50));
        rig.Controller.Post(new HookEvent(HotkeySignal.Down, t0 + 150));
        rig.Controller.Post(new HookEvent(HotkeySignal.Up, t0 + 200));
        await WaitUntil(() => rig.Capture.Cutting, "hands-free to start cutting");
    }

    private static void StopTap(Rig rig) => rig.Controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));

    private static CapturedAudio WholeRecording() => new(new float[32000], 0.2f);

    private static TranscriptCleaner ModelCleaner(ILlmClient client) =>
        new(client, CleanupPrompt.Default, TimeSpan.FromSeconds(2), NullLog.Instance, "fake", deadline: _ => TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Parts_are_typed_in_order_while_recording_goes_on()
    {
        using var rig = Make(new PartTranscriber("we met on tuesday", "and agreed the plan", "thanks everyone"));
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        await WaitUntil(() => rig.Pasted.Count == 1, "the first part");
        Assert.Equal(DictationState.RecordingHandsFree, rig.Machine.State); // typed while the microphone stays open
        rig.Capture.Cut(Part(1));
        await WaitUntil(() => rig.Pasted.Count == 2, "the second part");
        rig.Capture.Rest = Part(2);
        StopTap(rig);
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");

        Assert.Equal(["We met on tuesday.", " And agreed the plan.", " Thanks everyone."], rig.Pasted);
        Assert.Equal(10, rig.Ui.States.Last(s => s.View == PillView.Done).Words); // every part's words
        var pills = rig.Ui.Pills.ToList();
        Assert.Equal([PillView.RecordingHold, PillView.RecordingHandsFree, PillView.Transcribing, PillView.Done], pills); // no "Transcribing…" mid-session
    }

    [Fact]
    public async Task A_slow_part_delays_the_next_and_never_reorders_it()
    {
        var whisper = new PartTranscriber("first part words here", "second part words here");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        whisper.Holds[0] = hold;
        using var rig = Make(whisper);
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        rig.Capture.Cut(Part(1));
        await WaitUntil(() => whisper.Heard.Count == 1, "the first part to reach whisper");
        await Task.Delay(200, Ct);
        Assert.Single(whisper.Heard); // one at a time: the second part waits its turn
        Assert.Empty(rig.Pasted);

        hold.SetResult();
        await WaitUntil(() => rig.Pasted.Count == 2, "both parts");
        Assert.Equal(["First part words here.", " Second part words here."], rig.Pasted);
    }

    [Fact]
    public async Task Esc_drops_only_the_parts_not_yet_typed()
    {
        var whisper = new PartTranscriber("typed before esc", "in flight at esc", "queued at esc");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        whisper.Holds[1] = hold;
        using var rig = Make(whisper);
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        await WaitUntil(() => rig.Pasted.Count == 1, "the first part");
        rig.Capture.Cut(Part(1));
        await WaitUntil(() => whisper.Heard.Count == 2, "the second part in flight");
        rig.Capture.Cut(Part(2));

        rig.Controller.Post(new HookEvent(HotkeySignal.Escape, Environment.TickCount64));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.HandsFreeStopped), "the stopped flash");
        hold.SetResult(); // the part in flight would have finished now
        await Task.Delay(300, Ct);

        Assert.Equal(["Typed before esc."], rig.Pasted);    // what was typed stays, nothing more is typed
        Assert.Equal(2, whisper.Heard.Count);               // the queued part never reached whisper
        Assert.Equal(1, rig.Capture.Stops);                 // the microphone is closed
        Assert.Equal(DictationState.Idle, rig.Machine.State);
        Assert.DoesNotContain(PillView.Cancelled, rig.Ui.Pills);
        Assert.DoesNotContain(PillView.Done, rig.Ui.Pills);
    }

    [Fact]
    public async Task The_last_parts_words_reach_whisper_and_the_clean_up_as_context_and_are_not_typed_again()
    {
        var llm = new TableLlmClient(new()
        {
            ["we should move the meeting to"] = "We should move the meeting to",
            ["thursday and then we will see"] = "Thursday and then we will see.",
        });
        var whisper = new PartTranscriber("we should move the meeting to", "thursday and then we will see");
        using var rig = Make(whisper, ModelCleaner(llm));
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        rig.Capture.Cut(Part(1));
        await WaitUntil(() => rig.Pasted.Count == 2, "both parts");

        Assert.Equal([("we should move the meeting to", null), ("thursday and then we will see", "We should move the meeting to")], whisper.Heard);
        var second = llm.Sent.Last();
        Assert.Equal("thursday and then we will see", second.User); // the dictation is the part's own words
        Assert.Equal(CleanupPrompt.WithCarried(CleanupPrompt.Default, "We should move the meeting to"), second.System);
        Assert.Equal(" thursday and then we will see.", rig.Pasted.Last()); // nothing of the first part typed again
    }

    [Fact]
    public async Task The_carried_words_are_what_was_said_never_a_snippet_a_correction_or_the_filters_mask()
    {
        // Merge lens P1, 16/10/2026: a cloud clean-up gets the carried words in its system prompt, so they come from what the
        // part said, before corrections, the profanity filter and snippets put the user's own text in its place.
        var personal = new PersonalText(PersonalDictionary.Empty, new Corrections(Corrections.Parse(["kinney => Kynni Pty Ltd"])),
            new ProfanityFilter(["shit"], "test"), new Corrections(Corrections.Parse(["my email => kyle@example.com"], lineBreaks: true), verbatim: true));
        var mask = new ProfanitySettings { Mode = ProfanitySettings.Mask };
        var llm = new TableLlmClient(new()
        {
            ["send the draft to my email"] = "Send the draft to my email.",
            ["the shit report came from kinney"] = "The shit report came from kinney.",
            ["thanks everyone"] = "Thanks everyone.",
        });
        var whisper = new PartTranscriber("send the draft to my email", "the shit report came from kinney", "thanks everyone");
        using var rig = Make(whisper, ModelCleaner(llm), finish: text => personal.Finish(text, mask));
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        rig.Capture.Cut(Part(1));
        rig.Capture.Cut(Part(2));
        await WaitUntil(() => rig.Pasted.Count == 3, "three parts");

        Assert.Equal(["Send the draft to kyle@example.com.", " The s*** report came from Kynni Pty Ltd.", " Thanks everyone."], rig.Pasted);
        var sent = llm.Sent.ToList();
        Assert.Equal(CleanupPrompt.WithCarried(CleanupPrompt.Default, "Send the draft to my email."), sent[1].System); // the snippet's phrase, not its text
        Assert.Equal(CleanupPrompt.WithCarried(CleanupPrompt.Default, "The shit report came from kinney."), sent[2].System); // no correction, no mask
        Assert.All(sent, s => Assert.DoesNotContain("kyle@example.com", s.System + s.User, StringComparison.Ordinal));
        Assert.All(sent, s => Assert.DoesNotContain("Kynni", s.System + s.User, StringComparison.Ordinal));
        Assert.All(sent, s => Assert.DoesNotContain("s***", s.System + s.User, StringComparison.Ordinal));
        Assert.Equal([null, "Send the draft to my email.", "The shit report came from kinney."], whisper.Heard.Select(h => h.Carried)); // whisper's prompt too
    }

    [Fact]
    public async Task The_boundary_rule_types_each_part_after_the_last_both_ways()
    {
        var llm = new TableLlmClient(new()
        {
            ["we should move the meeting to"] = "We should move the meeting to",
            ["thursday and then we will see"] = "Thursday and then we will see.",
            ["great"] = "Great.",
        });
        using var rig = Make(new PartTranscriber("we should move the meeting to", "thursday and then we will see", "great"), ModelCleaner(llm));
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        rig.Capture.Cut(Part(1));
        rig.Capture.Rest = Part(2);
        await WaitUntil(() => rig.Pasted.Count == 2, "two parts");
        StopTap(rig);
        await WaitUntil(() => rig.Pasted.Count == 3, "the rest");

        // No terminal punctuation: a space and a small letter. A full stop: a space and the part's capital.
        Assert.Equal(["We should move the meeting to", " thursday and then we will see.", " Great."], rig.Pasted);
        Assert.Equal("Great.", rig.Remembered.Last()); // Fix last dictation: the last part typed, without its separator
    }

    [Fact]
    public async Task With_the_trailing_space_on_no_second_space_is_typed()
    {
        using var rig = Make(new PartTranscriber("we met on tuesday", "and agreed the plan"), trailingSpace: true);
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        rig.Capture.Cut(Part(1));
        await WaitUntil(() => rig.Pasted.Count == 2, "both parts");
        Assert.Equal(["We met on tuesday.", "And agreed the plan."], rig.Pasted); // the inserter adds the space after each paste
    }

    [Fact]
    public async Task Hold_to_talk_stays_one_recording()
    {
        using var rig = Make(new PartTranscriber("one whole dictation"));
        rig.Capture.Rest = Part(0);
        var t0 = Environment.TickCount64;
        rig.Controller.Post(new HookEvent(HotkeySignal.Down, t0));
        rig.Controller.Post(new HookEvent(HotkeySignal.Up, t0 + 500));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");
        Assert.Equal(0, rig.Capture.SegmentCalls); // never cut
        Assert.Equal(["One whole dictation."], rig.Pasted);
    }

    [Fact]
    public async Task The_auto_stop_ends_the_session_and_types_what_is_left()
    {
        // Two seconds, so a slow runner still cuts the first part before the auto-stop.
        using var rig = Make(new PartTranscriber("the first part", "the rest at the stop"), options: new StateMachineOptions(300, 400, HandsFreeMaxSeconds: 2));
        rig.Capture.Rest = Part(1);
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the auto-stop's Done pill");
        Assert.Equal(["The first part.", " The rest at the stop."], rig.Pasted);
    }

    [Fact]
    public async Task A_microphone_lost_mid_session_keeps_what_is_typed_and_types_what_it_captured()
    {
        // Merge lens P5, 16/10/2026: sending a hands-free session down the drop path when the microphone goes failed no test.
        using var rig = Make(new PartTranscriber("typed before the headset went", "and the rest it captured"));
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        await WaitUntil(() => rig.Pasted.Count == 1, "the first part");
        rig.Capture.Rest = Part(1);
        rig.Capture.Lose();
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");

        Assert.Equal(["Typed before the headset went.", " And the rest it captured."], rig.Pasted);
        Assert.Contains(PillView.MicError, rig.Ui.Pills);
        Assert.DoesNotContain(PillView.HandsFreeStopped, rig.Ui.Pills);
        Assert.Equal(1, rig.Capture.Stops);
    }

    [Fact]
    public async Task A_part_that_fails_does_not_end_the_session()
    {
        // Merge lens P6, 16/10/2026: stopping the worker at the first failed part failed no test.
        var whisper = new PartTranscriber("this part fails", "the next part is typed", "and so is the rest");
        whisper.Fails[0] = true;
        using var rig = Make(whisper);
        await StartHandsFree(rig);
        rig.Capture.Cut(Part(0));
        rig.Capture.Cut(Part(1));
        await WaitUntil(() => rig.Pasted.Count == 1, "the part after the failed one");
        rig.Capture.Rest = Part(2);
        StopTap(rig);
        await WaitUntil(() => rig.Pasted.Count == 2, "the rest at the stop");
        await WaitUntil(() => rig.Machine.State == DictationState.Idle, "the session to end");

        Assert.Equal(["The next part is typed.", " And so is the rest."], rig.Pasted);
        Assert.Equal(3, rig.Whisper.Heard.Count);
    }

    [Fact]
    public async Task A_microphone_that_cannot_cut_types_the_whole_session_at_the_stop()
    {
        var settings = new AppSettings();
        var pasted = new ConcurrentQueue<string>();
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber("hello there")), () => TranscriptCleaner.RulesOnly("test"),
            (text, _) =>
            {
                pasted.Enqueue(text);
                return Task.CompletedTask;
            }, () => settings, NullLog.Instance);
        var ui = new FakeUi();
        using var controller = new DictationController(new DictationStateMachine(StateMachineOptions.Default), new FakeCapture(), pipeline, ui, () => settings, NullLog.Instance);
        var t0 = Environment.TickCount64;
        controller.Post(new HookEvent(HotkeySignal.Down, t0));
        controller.Post(new HookEvent(HotkeySignal.Up, t0 + 50));
        controller.Post(new HookEvent(HotkeySignal.Down, t0 + 150));
        controller.Post(new HookEvent(HotkeySignal.Up, t0 + 200));
        await WaitUntil(() => ui.Pills.Contains(PillView.RecordingHandsFree), "hands-free");
        controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));
        await WaitUntil(() => ui.Pills.Contains(PillView.Done), "the Done pill");
        Assert.Equal(["Hello there."], pasted);
    }
}

/// <summary>The state machine's side of hands-free parts (SPEC §3 amendment, 16/10/2026).</summary>
public class HandsFreeStateMachineTests
{
    private static DictationStateMachine HandsFree(out CancellationToken partsToken, StateMachineOptions? options = null)
    {
        var m = new DictationStateMachine(options ?? new StateMachineOptions(300, 400, 300));
        m.HotkeyDown(0);
        m.HotkeyUp(100);
        partsToken = Assert.Single(m.HotkeyDown(300).OfType<BeginSegmentsEffect>()).Token;
        m.HotkeyUp(350);
        return m;
    }

    [Fact]
    public void Esc_during_hands_free_stops_and_keeps_what_is_typed()
    {
        var m = HandsFree(out var token);
        var effects = m.Escape(5000);
        Assert.True(token.IsCancellationRequested); // parts in flight or queued are dropped
        Assert.Equal(DictationState.Idle, m.State);
        Assert.Single(effects.OfType<DiscardCaptureEffect>());
        Assert.True(Assert.Single(effects.OfType<CancelProcessingEffect>()).KeepsTyped);
        Assert.Contains(new PillEffect(PillView.HandsFreeStopped), effects);
        Assert.DoesNotContain(new PillEffect(PillView.Cancelled), effects);
    }

    [Fact]
    public void The_stop_keeps_the_token_the_parts_are_typed_under_so_Esc_still_stops_them_all()
    {
        var m = HandsFree(out var token);
        var stop = m.HotkeyDown(8000);
        Assert.Equal(token, Assert.Single(stop.OfType<BeginProcessingEffect>()).Token);
        var esc = m.Escape(8100);
        Assert.True(token.IsCancellationRequested);
        Assert.True(Assert.Single(esc.OfType<CancelProcessingEffect>()).KeepsTyped);
        Assert.Contains(new PillEffect(PillView.HandsFreeStopped), esc);
    }

    [Fact]
    public void Hold_to_talk_never_starts_cutting_and_Esc_still_cancels_it()
    {
        var m = new DictationStateMachine(new StateMachineOptions(300, 400, 300));
        Assert.DoesNotContain(m.HotkeyDown(0), e => e is BeginSegmentsEffect);
        var token = Assert.Single(m.HotkeyUp(500).OfType<BeginProcessingEffect>()).Token;
        var esc = m.Escape(600);
        Assert.True(token.IsCancellationRequested);
        Assert.False(Assert.Single(esc.OfType<CancelProcessingEffect>()).KeepsTyped);
        Assert.Contains(new PillEffect(PillView.Cancelled), esc);
    }

    [Fact]
    public void A_lost_microphone_types_the_rest_under_the_sessions_token()
    {
        // Merge lens P5 at the state machine: the rest goes after the parts already typed, which nothing cancels.
        var m = HandsFree(out var token);
        var lost = m.CaptureLost(m.SessionId);
        Assert.Equal(token, Assert.Single(lost.OfType<BeginProcessingEffect>()).Token);
        Assert.False(token.IsCancellationRequested);
        Assert.Contains(new PillEffect(PillView.MicError), lost);
        Assert.DoesNotContain(lost, e => e is DiscardCaptureEffect or CancelProcessingEffect);
        Assert.Equal(DictationState.Processing, m.State);
    }

    [Fact]
    public void Turning_dictation_off_mid_session_drops_the_parts_not_yet_typed()
    {
        var m = HandsFree(out var token);
        var effects = m.SetEnabled(false);
        Assert.True(token.IsCancellationRequested);
        Assert.Single(effects.OfType<DiscardCaptureEffect>());
        Assert.True(Assert.Single(effects.OfType<CancelProcessingEffect>()).KeepsTyped);
    }

    [Fact]
    public void The_state_machine_honours_a_long_auto_stop_from_the_settings()
    {
        var m = HandsFree(out _, AppRules.Options(new AppSettings { HandsFreeMaxSeconds = 1800 }));
        Assert.Empty(m.Tick(300_000)); // the old five minutes no longer stop it
        Assert.Empty(m.Tick(1_799_999));
        Assert.True(Assert.Single(m.Tick(1_800_000).OfType<BeginProcessingEffect>()).AutoStopped);
    }

    [Fact]
    public void A_short_hands_free_limit_does_not_cut_a_hold_short()
    {
        // Merge lens P3b, 16/10/2026: with the field at 30 s a 45 s hold was stopped at 30 s. The hold has its own limit.
        var m = new DictationStateMachine(AppRules.Options(new AppSettings { HandsFreeMaxSeconds = 30 }));
        m.HotkeyDown(0);
        Assert.Empty(m.Tick(45_000));
        Assert.Equal(DictationState.RecordingHold, m.State);
        Assert.False(Assert.Single(m.HotkeyUp(45_100).OfType<BeginProcessingEffect>()).AutoStopped); // the release ends it
    }

    [Fact]
    public void A_long_hands_free_limit_does_not_let_a_stuck_key_record_for_an_hour()
    {
        // Merge lens P3, 16/10/2026: with the field at 60 minutes a stuck key recorded for an hour. A hold still stops at 5 minutes.
        var m = new DictationStateMachine(AppRules.Options(new AppSettings { HandsFreeMaxSeconds = 3600 }));
        m.HotkeyDown(0);
        Assert.Empty(m.Tick(299_999));
        Assert.True(Assert.Single(m.Tick(300_000).OfType<BeginProcessingEffect>()).AutoStopped);
        Assert.Equal(DictationState.Processing, m.State);
    }
}

/// <summary>hands_free_max_seconds: the range, a legacy value, and Settings > General's field (decision 3, 16/10/2026).</summary>
public class HandsFreeSettingsTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Select(t => t.Text));

    [Fact]
    public void A_value_under_30_from_an_earlier_version_is_raised_to_30_with_a_log_line()
    {
        using var scratch = new Scratch("""{ "hands_free_max_seconds": 10 }""");
        var lines = new List<string>();
        var config = ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, lines.Add);
        Assert.Equal(30, config.Settings.HandsFreeMaxSeconds);
        Assert.Contains(lines, l => l.Contains("hands_free_max_seconds 10", StringComparison.Ordinal) && l.Contains("using 30 s", StringComparison.Ordinal));
        Assert.Empty(config.File.Warnings); // a raised value is not "invalid": no balloon
        Assert.Equal("""{ "hands_free_max_seconds": 10 }""", File.ReadAllText(scratch.Paths.SettingsFile)); // the file is not rewritten
    }

    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(3600)]
    public void A_value_in_range_is_used_as_written(int seconds)
    {
        using var scratch = new Scratch($$"""{ "hands_free_max_seconds": {{seconds}} }""");
        var lines = new List<string>();
        Assert.Equal(seconds, ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, lines.Add).Settings.HandsFreeMaxSeconds);
        Assert.DoesNotContain(lines, l => l.Contains("hands_free_max_seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void The_proposed_hands_free_wording_is_pinned_word_for_word()
    {
        // PROPOSED 16/10/2026, awaiting the product owner's approval: every word as proposed.
        Assert.Equal("Hands-free stops after", GeneralPage.HandsFreeLabel);
        Assert.Equal("Hands-free dictation types each sentence as you pause, so a long session reads back as you go. It stops by itself after this long.",
            GeneralPage.HandsFreeHelp);
        Assert.Equal("Hands-free · typing as you pause · tap to stop · Esc stops and keeps what's typed",
            $"{PillText.For(PillView.RecordingHandsFree, TimeSpan.Zero)} · {PillText.HandsFreeHint}");
        Assert.Equal("Stopped. Text already typed stays.", PillText.For(PillView.HandsFreeStopped, TimeSpan.Zero));
        Assert.Equal(1800, PillText.FlashMs(PillView.HandsFreeStopped));
        Assert.DoesNotContain("hands_free_max_seconds", GeneralPage.MouseNote, StringComparison.Ordinal); // the field says it now
    }

    [Fact]
    public Task The_pill_says_what_Esc_kept() => UiSession.Run(() =>
    {
        var pill = new PillWindow(null);
        var start = Environment.TickCount64;
        pill.Present(new PillState(PillView.RecordingHandsFree, start));
        Assert.Contains(PillText.HandsFreeHint, Texts(pill));
        pill.Present(new PillState(PillView.HandsFreeStopped, start));
        Assert.Equal("Stopped. Text already typed stays.", pill.Text);
        Assert.True(pill.FindControl<Panel>("Badge")!.IsVisible);
        pill.Close();
    });

    private static (SettingsWindow Window, Dictum.UI.Shell.AppShell Shell) OpenGeneral(Scratch scratch)
    {
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        return (window, shell);
    }

    private static ComboBox UnitOf(ILogical window) =>
        All<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == $"{GeneralPage.HandsFreeLabel}: unit");

    private static ToggleButton Preset(ILogical window, string label) =>
        All<ToggleButton>(window).Single(b => AutomationProperties.GetName(b) == $"{GeneralPage.HandsFreeLabel}: {label}");

    [Fact]
    public Task The_field_writes_hands_free_max_seconds_clamped_to_its_range() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (window, shell) = OpenGeneral(scratch);
        int Saved() => SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.HandsFreeMaxSeconds;
        var box = All<NumericUpDown>(window).Single();
        var unit = UnitOf(window);
        Assert.Equal((5m, 0), (box.Value, unit.SelectedIndex)); // the default: 5 minutes

        box.Value = 12m;
        Assert.Equal(720, Saved());
        Assert.Equal(720, shell.Settings.HandsFreeMaxSeconds); // reloaded: the state machine gets it (AppRules.Options)
        unit.SelectedIndex = 1;
        Assert.Equal(720m, box.Value); // the same time in seconds; nothing new to write
        box.Value = 10m;
        Assert.Equal(30, Saved());     // under 30 s: 30 s
        box.Value = 99_999m;
        Assert.Equal(3600, Saved());   // over 60 minutes: 60 minutes
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_quick_picks_write_their_minutes() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (window, shell) = OpenGeneral(scratch);
        int Saved() => SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.HandsFreeMaxSeconds;
        Assert.True(Preset(window, "5 min").IsChecked); // the default is one of them
        foreach (var (label, seconds) in new[] { ("1 min", 60), ("2 min", 120), ("10 min", 600), ("30 min", 1800), ("60 min", 3600), ("5 min", 300) })
        {
            Preset(window, label).IsChecked = true;
            Assert.Equal(seconds, Saved());
            Assert.Equal(seconds / 60m, All<NumericUpDown>(window).Single().Value);
        }
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_slider_writes_once_it_settles_or_the_page_is_left() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (window, shell) = OpenGeneral(scratch);
        int Saved() => SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.HandsFreeMaxSeconds;
        var slider = All<Slider>(window).Single();
        Assert.Equal((30, 3600, 300), ((int)slider.Minimum, (int)slider.Maximum, (int)slider.Value));
        slider.Value = 840;
        slider.Value = 900; // a drag: each step shows, none is written yet
        Assert.Equal(15m, All<NumericUpDown>(window).Single().Value);
        Assert.Equal(300, Saved());
        window.ShowPage("Mode"); // leaving the page writes the value it settled on
        Assert.Equal(900, Saved());
        window.Close();
        shell.Dispose();
    });

    [Theory]
    [InlineData(90, 90, 1, null)]
    [InlineData(600, 10, 0, "10 min")]
    [InlineData(450, 7.5, 0, null)] // 7.5 minutes: the unit in use shows it exactly
    [InlineData(10, 30, 1, null)]   // a legacy value shows as the 30 s it runs at
    public Task The_field_shows_the_value_in_use(int written, double shown, int unitIndex, string? preset) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "hands_free_max_seconds": {{written}} }""");
        var (window, shell) = OpenGeneral(scratch);
        if (written == 450) UnitOf(window).SelectedIndex = 0; // a minutes user
        Assert.Equal(((decimal)shown, unitIndex), (All<NumericUpDown>(window).Single().Value, UnitOf(window).SelectedIndex));
        Assert.Equal(Math.Max(written, 30), (int)All<Slider>(window).Single().Value);
        foreach (var minutes in HandsFreeLimit.PresetMinutes)
        {
            Assert.Equal($"{minutes} min" == preset, Preset(window, $"{minutes} min").IsChecked == true);
        }
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_field_is_named_and_reachable_from_the_keyboard() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (window, shell) = OpenGeneral(scratch);
        Assert.Contains(GeneralPage.HandsFreeLabel, Texts(window));
        Assert.Contains(GeneralPage.HandsFreeHelp, Texts(window));
        Control[] parts = [All<NumericUpDown>(window).Single(), UnitOf(window), All<Slider>(window).Single(), .. HandsFreeLimit.PresetMinutes.Select(m => Preset(window, $"{m} min"))];
        Assert.All(parts, c =>
        {
            Assert.StartsWith(GeneralPage.HandsFreeLabel, AutomationProperties.GetName(c));
            Assert.True(c.Focusable && c.IsEnabled, $"{AutomationProperties.GetName(c)} cannot take keyboard focus");
        });
        window.Close();
        shell.Dispose();
    });
}
