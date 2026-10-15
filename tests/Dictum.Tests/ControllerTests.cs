using System.Collections.Concurrent;
using System.Diagnostics;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.App;

namespace Dictum.Tests;

/// <summary>The controller's inbox loop with fake capture, UI and pipeline pieces (no threads of our own to manage).</summary>
public class ControllerTests
{
    private sealed record Rig(DictationController Controller, FakeUi Ui, ConcurrentQueue<string> Inserted, DictationStateMachine Machine) : IDisposable
    {
        public void Dispose() => Controller.Dispose();
    }

    private static Rig Make(AppSettings settings, ITranscriber transcriber)
    {
        var ui = new FakeUi();
        var inserted = new ConcurrentQueue<string>();
        var machine = new DictationStateMachine(StateMachineOptions.Default);
        var cleaner = new TranscriptCleaner(FakeLlmClient.Replying("Hello there."), CleanupPrompt.Default, TimeSpan.FromSeconds(2), NullLog.Instance, "fake");
        var pipeline = new DictationPipeline(
            _ => Task.FromResult(transcriber),
            () => cleaner,
            (text, token) =>
            {
                token.ThrowIfCancellationRequested();
                inserted.Enqueue(text);
                return Task.CompletedTask;
            },
            () => settings,
            NullLog.Instance);
        var controller = new DictationController(machine, new FakeCapture(), pipeline, ui, () => settings, NullLog.Instance);
        return new Rig(controller, ui, inserted, machine);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            await Task.Delay(10);
        }
    }

    private static void Hold(DictationController controller)
    {
        var t0 = Environment.TickCount64;
        controller.Post(new HookEvent(HotkeySignal.Down, t0));
        controller.Post(new HookEvent(HotkeySignal.Up, t0 + 500));
    }

    [Fact]
    public void Settings_reload_keeps_the_hook_unless_the_hotkey_changed()
    {
        var before = new AppSettings();
        Assert.False(AppRules.HotkeyChanged(before, before with { TapMs = 250, Insert = new InsertSettings { Method = "type" } }));
        Assert.True(AppRules.HotkeyChanged(before, before with { Hotkey = "F9" }));
        Assert.True(AppRules.HotkeyChanged(before, before with { SuppressHotkey = false }));
    }

    [Fact]
    public async Task Normal_session_shows_recording_transcribing_polishing_done()
    {
        using var rig = Make(new AppSettings(), new FakeTranscriber("um hello there"));
        Hold(rig.Controller);
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");
        Assert.Equal([PillView.RecordingHold, PillView.Transcribing, PillView.Polishing, PillView.Done], rig.Ui.Pills.ToArray());
        Assert.Equal(["Hello there."], rig.Inserted.ToArray());
    }

    [Fact]
    public async Task Polishing_is_not_shown_when_the_pill_is_disabled()
    {
        var settings = new AppSettings { Pill = new PillSettings { Enabled = false } };
        using var rig = Make(settings, new FakeTranscriber("um hello there"));
        Hold(rig.Controller);
        await WaitUntil(() => !rig.Inserted.IsEmpty, "the paste");
        await Task.Delay(200, TestContext.Current.CancellationToken); // let the completion message drain
        Assert.Equal(["Hello there."], rig.Inserted.ToArray());
        Assert.Empty(rig.Ui.Pills); // a normal session sends no "hide", and nothing else may reach a disabled pill
    }

    [Fact]
    public async Task Late_polishing_report_after_Esc_does_not_replace_Cancelled()
    {
        var gate = new TaskCompletionSource();
        using var rig = Make(new AppSettings(), new FakeTranscriber("um hello there", gate: gate.Task));
        Hold(rig.Controller);
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Transcribing), "the Transcribing pill");
        var session = rig.Machine.SessionId;

        rig.Controller.Post(new HookEvent(HotkeySignal.Escape, Environment.TickCount64));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Cancelled), "the Cancelled pill");
        rig.Controller.ReportPolishing(session); // the pipeline passed its token check just before Esc landed
        gate.SetResult();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var pills = rig.Ui.Pills.ToArray();
        Assert.Equal(PillView.Cancelled, pills[^1]);
        Assert.DoesNotContain(PillView.Polishing, pills);
        Assert.Empty(rig.Inserted);
    }
}
