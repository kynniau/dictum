using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App;
using Dictum.Calibration;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using static Dictum.Tests.ModelMoveTests;

namespace Dictum.Tests;

/// <summary>
/// Calibration window, "Use this" (owner report 04/11/2026: pressing it appeared to do nothing, though the settings changed):
/// the status line says it took effect, the row in use is marked, and keyboard focus stays on the button pressed. Headless, over
/// a real shell on a scratch profile (real settings.json, real writer and reload, a fake speech loader), so what the window says
/// is read back from what the app actually holds.
/// </summary>
public class CalibrationUseTests
{
    private const string Profile = """{ "hotkey": "F24", "whisper": { "model": "tiny.en", "beam_size": 0 }, "llm": { "provider": "none" } }""";

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The Use this button of a row, by its accessible name (which carries the row's model and decoding).</summary>
    private static Button Row(ILogical window, string name) => All<Button>(window).Single(b => AutomationProperties.GetName(b) == name);

    private static ComboResult Combo(string model, int beam, int edits) =>
        new(model, beam, [new ClipOutcome(1, "x", Seconds: 1, AudioSeconds: 10, new WerResult(edits, 100, []))]);

    /// <summary>
    /// Three results: tiny.en greedy (what the profile uses), tiny.en with beam search, and base.en greedy, which has the fewest
    /// errors, so it is the recommended one. Both models are on disk (empty, of the catalogue's sizes), so a switch to either loads.
    /// </summary>
    private static (AppShell Shell, CalibrationWindow Window) Open(Scratch scratch, ILog? log = null, ModelManager? models = null, HttpMessageHandler? providers = null)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", ModelCatalog.Find("base.en")!.Bytes);
        var (shell, _, _) = scratch.Shell(models ?? SpeechModels(), log: log, providerHandler: providers);
        shell.Reload(userRequested: false);
        var window = new CalibrationWindow(shell);
        window.Show();
        var session = typeof(CalibrationWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var add = session.GetType().GetMethod("Add")!;
        foreach (var combo in new[] { Combo("tiny.en", 0, 9), Combo("tiny.en", 5, 7), Combo("base.en", 0, 3), Combo("base.en", 5, 4), Combo("small.en", 0, 10) }) add.Invoke(session, [combo, "clips"]);
        typeof(CalibrationWindow).GetMethod("RenderResults", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        Dispatcher.UIThread.RunJobs();
        return (shell, window);
    }

    private const string TinyGreedy = "tiny.en with greedy";
    private const string TinyBeam = "tiny.en with beam search (5)";
    private const string BaseGreedy = "base.en with greedy (recommended)";

    /// <summary>
    /// Pin 1 (the report): after Use this the status line says the app now uses that model and decoding, and says from when (the
    /// next dictation, once the model has loaded: the hotkey is ignored while it loads, AppShell's hotkeyBlocked). The line is a
    /// polite live region, so a screen reader announces it. The setting really changed (the line is read back from the app).
    /// </summary>
    [Fact]
    public Task Use_this_says_the_model_and_decoding_now_in_use() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        var (shell, window) = Open(scratch);
        Assert.DoesNotContain("Dictum now uses", Texts(window));

        Click(Row(window, $"Use {BaseGreedy}"));

        Assert.Equal("base.en", shell.Settings.Whisper.Model);
        Assert.Contains("Dictum now uses base.en (greedy) from the next dictation, once the model has loaded.", Texts(window));
        var line = All<TextBlock>(window).Single(t => t.Text?.StartsWith("Dictum now uses", StringComparison.Ordinal) == true);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin 2: while model files move, Use this says the approved refusal, writes nothing, claims nothing, and marks nothing.</summary>
    [Fact]
    public Task Use_this_while_files_move_says_the_refusal_and_no_success() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        var (shell, window) = Open(scratch);
        shell.Models.MovingFiles = true;

        Click(Row(window, $"Use {BaseGreedy}"));

        Assert.Contains(ModelCoordinator.MovingRefusal, Texts(window));
        Assert.DoesNotContain("Dictum now uses", Texts(window));
        Assert.Equal("tiny.en", shell.Settings.Whisper.Model);
        Assert.Equal("Use this", Row(window, $"Use {BaseGreedy}").Content);
        shell.Models.MovingFiles = false;
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 3: when settings.json cannot be written (read-only), the window says so in the status line (the writer's own balloon text,
    /// which until now was the only place it was said), claims nothing, and marks nothing. The writer returns false and does not
    /// throw, so the failure handler never ran: the result of Write was being dropped.
    /// </summary>
    [Fact]
    public Task Use_this_when_the_file_cannot_be_written_says_so_and_no_success() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        var (shell, window) = Open(scratch);
        File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            Click(Row(window, $"Use {BaseGreedy}"));

            Assert.Contains(ModelCoordinator.WriteFailed, Texts(window));
            Assert.DoesNotContain("Dictum now uses", Texts(window));
            Assert.Equal("Use this", Row(window, $"Use {BaseGreedy}").Content);
            Assert.Equal("In use", Row(window, $"{TinyGreedy}: in use").Content);
        }
        finally
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 4: the row in use is marked on first render (model and beam size both matter: tiny.en greedy, not tiny.en with beam
    /// search), follows a click (the row left reads Use this again), and follows a change made elsewhere once the window is active.
    /// The name of the row in use says so.
    /// </summary>
    [Fact]
    public Task The_in_use_mark_follows_the_current_settings() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        var (shell, window) = Open(scratch);
        string Label(string name) => (string)Row(window, name).Content!;
        Dispatcher.UIThread.RunJobs();
        window.Activate(); // whatever the window does on activation has happened…
        Dispatcher.UIThread.RunJobs();
        typeof(CalibrationWindow).GetMethod("RenderResults", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null); // …so this draw (a finished combination, a test run's end) marks by itself

        Assert.Equal("In use", Label($"{TinyGreedy}: in use")); // first render: the profile's model and greedy
        Assert.Equal("Use this", Label($"Use {TinyBeam}")); // same model, other decoding: not in use
        Assert.Equal("Use this", Label($"Use {BaseGreedy}"));

        Click(Row(window, $"Use {TinyBeam}"));
        Assert.Equal("In use", Label($"{TinyBeam}: in use"));
        Assert.Equal("Use this", Label($"Use {TinyGreedy}")); // the row left is offered again
        Assert.Equal(5, shell.Settings.Whisper.BeamSize);

        shell.Write(("whisper.beam_size", System.Text.Json.Nodes.JsonValue.Create(0))); // Settings > Models, say, meanwhile
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("In use", Label($"{TinyGreedy}: in use"));
        Assert.Equal("Use this", Label($"Use {TinyBeam}"));
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 5: keyboard focus is still on the button pressed afterwards. The grid is not rebuilt (its buttons are changed in place),
    /// so the person who pressed Enter on it is not thrown out to the top of the window; the same button now reads "In use".
    /// </summary>
    [Fact]
    public Task Keyboard_focus_stays_on_the_button_pressed() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        var (shell, window) = Open(scratch);
        var use = Row(window, $"Use {BaseGreedy}");
        Assert.True(use.Focus());
        Assert.Same(use, window.FocusManager!.GetFocusedElement());

        Click(use);

        var now = Row(window, $"{BaseGreedy}: in use");
        Assert.Same(use, now); // not rebuilt
        Assert.NotNull(now.GetVisualRoot());
        Assert.Same(now, window.FocusManager!.GetFocusedElement());
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 6: "in use" is the model the app has loaded or is loading, not only what settings.json says: with a different model
    /// running (a launch override), the row for the file's model is not marked; and a model whose load has failed is not marked at all.
    /// </summary>
    [Fact]
    public void In_use_is_the_model_the_app_holds_not_only_the_file()
    {
        using var scratch = new Scratch();
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = "tiny.en", BeamSize = 0 } };
        CalibrationHost Host(Func<string>? current, Func<string?>? problem = null) => new(scratch.Paths, () => settings, () => PersonalText.Empty,
            new ModelIntegrity(Path.Combine(scratch.Root.FullName, "verified-models.json"), NullLog.Instance), NullLog.Instance,
            FindSpeechModel: _ => null, Write: _ => true, DownloadSpeechModel: (_, _, _, _) => { }, AddDictionaryTerms: _ => { }, AddCorrections: _ => { },
            CurrentModel: current, SpeechLoadProblem: problem);

        Assert.True(Host(null).InUse(Combo("tiny.en", 0, 0))); // no app to ask: the file's model
        Assert.False(Host(null).InUse(Combo("tiny.en", 5, 0)));
        Assert.True(Host(() => "tiny.en").InUse(Combo("tiny.en", 0, 0)));
        Assert.False(Host(() => "base.en").InUse(Combo("tiny.en", 0, 0))); // the file says tiny.en, the app runs base.en
        Assert.True(Host(() => "base.en").InUse(Combo("base.en", 0, 0)));
        Assert.False(Host(() => "base.en", () => "Model failed").InUse(Combo("base.en", 0, 0))); // the app names it, but its load failed
        Assert.False(Host(() => "base.en", () => "Model failed").ModelInUse("base.en"));
        Assert.True(Host(() => "base.en", () => null).InUse(Combo("base.en", 0, 0)));
    }

    private const string GrammarOn = """{ "hotkey": "F24", "whisper": { "model": "tiny.en", "beam_size": 0 }, "llm": { "provider": "none", "grammar_assist": true } }""";

    private static CheckBox Grammar(ILogical window) => All<CheckBox>(window).Single(c => c.Content as string == "Grammar assist for this user");

    /// <summary>
    /// Pin 7 (sibling caller of Write): when settings.json cannot be written, unticking "Grammar assist for this user" says the
    /// write-failed text in the status line and puts the box back to the saved value (ticked), and that put-back is not written
    /// again: the writer's refusal ("Notice: ...", logged once per refusal) is seen exactly once.
    /// </summary>
    [Fact]
    public Task Grammar_box_when_the_file_cannot_be_written_says_so_and_goes_back_to_the_saved_value() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(GrammarOn);
        var log = new RecordingLog();
        var (shell, window) = Open(scratch, log);
        var box = Grammar(window);
        Assert.True(box.IsChecked);
        File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            box.IsChecked = false;

            Assert.Contains(ModelCoordinator.WriteFailed, Texts(window));
            Assert.True(box.IsChecked); // the saved value, not the unsaved choice
            Assert.True(shell.Settings.Llm.GrammarAssist);
            Assert.Equal(1, log.Lines.Count(l => l.Message == $"Notice: {ModelCoordinator.WriteFailed}")); // the put-back did not write again
        }
        finally
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>The control for pin 7: with a writable file the same untick is saved, the box stays unticked and nothing is said.</summary>
    [Fact]
    public Task Grammar_box_when_the_file_is_written_stays_as_chosen() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(GrammarOn);
        var (shell, window) = Open(scratch);
        var box = Grammar(window);

        box.IsChecked = false;

        Assert.False(box.IsChecked);
        Assert.False(shell.Settings.Llm.GrammarAssist);
        Assert.DoesNotContain(ModelCoordinator.WriteFailed, Texts(window));
        window.Close();
        shell.Dispose();
    });

    // ---- the line's three wordings, and a load that fails after the click ----

    private const string SpeechHost = "speech.example.com";

    private const string CloudProfile = $$"""{ "hotkey": "F24", "whisper": { "model": "tiny.en", "beam_size": 0 }, "llm": { "provider": "none" }, "speech": { "provider": "openai-compatible", "base_url": "https://{{SpeechHost}}/v1", "model": "speech-model" } }""";

    /// <summary>Fails any request: these pins never make one.</summary>
    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no request is expected");
    }

    private static bool Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>
    /// A speech model manager whose loader throws for base.en once <paramref name="release"/> is set (tiny.en, the profile's model,
    /// loads at once): a real faulted load through ModelManager, which sets its path first and does not reset it on a fault.
    /// </summary>
    private static ModelManager FailingBase(ManualResetEventSlim release) => new(NullLog.Instance, loader: (path, _, decode) =>
    {
        if (!path.EndsWith("ggml-base.en.bin", StringComparison.Ordinal)) return new HoldingSpeechEngine(path, decode);
        release.Wait(TestContext.Current.CancellationToken);
        throw new InvalidOperationException("the file is damaged");
    }, gpuInUse: () => false);

    /// <summary>
    /// Pin 8 (copy, lens minor 3): when only the decoding changes no model loads, so the line does not say "once the model has
    /// loaded" (the hotkey is not ignored: IsReady stays true). With the model changing it does (pin 1).
    /// </summary>
    [Fact]
    public Task Use_this_with_only_the_decoding_changed_does_not_wait_for_a_model() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        var (shell, window) = Open(scratch);
        Assert.True(Eventually(() => shell.SpeechReady), "tiny.en never finished loading"); // a model still loading would keep the hotkey ignored (pin 12)

        Click(Row(window, $"Use {TinyBeam}"));

        Assert.Contains("Dictum now uses tiny.en (beam search (5)) from the next dictation.", Texts(window).Split('\n'));
        Assert.DoesNotContain("once the model has loaded", Texts(window));
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 9 (privacy, lens major 1): while cloud speech sends each recording to a service, the line does not say Dictum uses the
    /// model (the service hears dictation; the model is the fallback). It says what was saved and uses the owner's APPROVED words
    /// for the fallback (Settings > Models, 22/10/2026 18:41 AEDT), byte for byte.
    /// </summary>
    [Fact]
    public Task Use_this_while_cloud_speech_is_on_does_not_claim_the_model_hears_dictation() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(CloudProfile);
        CloudConsentFile.WriteSpeech(scratch.Paths.ConsentFile, new CloudConsentRecord(SpeechSettings.OpenAiCompatible, SpeechHost, DateTimeOffset.UtcNow, "test"));
        var (shell, window) = Open(scratch, providers: new NoRequests());
        Assert.True(shell.SpeechSendsAudio);

        Click(Row(window, $"Use {BaseGreedy}"));

        var lines = Texts(window).Split('\n');
        Assert.Contains("Saved: base.en (greedy) is the speech model on this PC; with cloud speech on, speech.example.com does this and the model here is the fallback.", lines);
        Assert.DoesNotContain("Dictum now uses", Texts(window));
        Assert.Equal("with cloud speech on, speech.example.com does this and the model here is the fallback", CalibrationWindow.CloudFallbackClause(SpeechHost));
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 10 (honesty, lens major 2): a model whose load fails AFTER the click is not "in use". Through a real faulted load: the
    /// line said at the click is replaced by the tray's own text for the failure, the row goes back to Use this, and the row of
    /// the model still running (tiny.en, kept by ModelManager) is not claimed either.
    /// </summary>
    [Fact]
    public Task A_load_that_fails_after_the_click_replaces_the_line_and_the_mark() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        using var release = new ManualResetEventSlim();
        var (shell, window) = Open(scratch, models: FailingBase(release));

        Click(Row(window, $"Use {BaseGreedy}"));
        Assert.Contains("Dictum now uses base.en (greedy) from the next dictation, once the model has loaded.", Texts(window).Split('\n')); // said at the click
        Assert.Equal("In use", Row(window, $"{BaseGreedy}: in use").Content);

        release.Set(); // the load now fails, in the background
        Assert.True(Eventually(() => !Texts(window).Contains("Dictum now uses")), "the success line was never replaced");

        Assert.NotNull(shell.SpeechLoadProblem);
        Assert.Contains(shell.SpeechLoadProblem!, Texts(window).Split('\n')); // the tray balloon's text, not new words
        Assert.Contains("(Speech model: Settings > Models)", shell.SpeechLoadProblem);
        Assert.Equal("Use this", Row(window, $"Use {BaseGreedy}").Content);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// The same failure when it has finished before the click's own check (a load can fail within microseconds): what Use this says
    /// after its write is the failure, never the success line, and the row is not marked. The check is run here once the failure
    /// has happened and the status line is cleared, because the real timing cannot be held still.
    /// </summary>
    [Fact]
    public Task A_load_that_has_already_failed_is_never_marked_in_use() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        using var release = new ManualResetEventSlim(initialState: true); // fails as soon as it starts
        var (shell, window) = Open(scratch, models: FailingBase(release));
        Click(Row(window, $"Use {BaseGreedy}"));
        Assert.True(Eventually(() => shell.SpeechLoadProblem is not null), "the load never failed");
        Dispatcher.UIThread.RunJobs();
        var progress = (TextBlock)typeof(CalibrationWindow).GetField("_progress", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        progress.Text = "";

        var combo = Combo("base.en", 0, 3);
        typeof(CalibrationWindow).GetMethod("Say", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [combo, true]);

        Assert.Equal(shell.SpeechLoadProblem, progress.Text);
        Assert.DoesNotContain("Dictum now uses", Texts(window));
        Assert.Equal("Use this", Row(window, $"Use {BaseGreedy}").Content);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin 11: the window stops following the app's model state when it is closed (no call after close, nothing leaks).</summary>
    [Fact]
    public Task A_closed_window_does_not_follow_a_later_load_failure() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        using var release = new ManualResetEventSlim();
        var (shell, window) = Open(scratch, models: FailingBase(release));
        Click(Row(window, $"Use {BaseGreedy}"));
        var said = "Dictum now uses base.en (greedy) from the next dictation, once the model has loaded.";
        Assert.Contains(said, Texts(window).Split('\n'));
        window.Close();

        release.Set();
        Assert.True(Eventually(() => shell.SpeechLoadProblem is not null), "the load never failed");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(said, Texts(window).Split('\n')); // untouched: the closed window was not told
        shell.Dispose();
    });

    /// <summary>A speech model manager whose loader holds base.en until <paramref name="release"/> is set, then loads it (tiny.en loads at once).</summary>
    private static ModelManager HeldBase(ManualResetEventSlim release) => new(NullLog.Instance, loader: (path, _, decode) =>
    {
        if (path.EndsWith("ggml-base.en.bin", StringComparison.Ordinal)) release.Wait(TestContext.Current.CancellationToken);
        return new HoldingSpeechEngine(path, decode);
    }, gpuInUse: () => false);

    /// <summary>
    /// Pin 12 (lens minor 1): pressing the same model with other decoding while that model is still loading (an earlier press) still
    /// waits for the load, because the hotkey is ignored until it has: the line says "once the model has loaded". Once it has loaded,
    /// the same press is the decoding-only wording (pin 8).
    /// </summary>
    [Fact]
    public Task A_decoding_press_while_the_same_model_is_still_loading_still_says_once_loaded() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        using var release = new ManualResetEventSlim();
        var (shell, window) = Open(scratch, models: HeldBase(release));
        try
        {
            Click(Row(window, $"Use {BaseGreedy}")); // base.en starts loading and is held
            Assert.False(shell.SpeechReady);

            Click(Row(window, "Use base.en with beam search (5)"));

            Assert.Contains("Dictum now uses base.en (beam search (5)) from the next dictation, once the model has loaded.", Texts(window).Split('\n'));
        }
        finally
        {
            release.Set();
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin 13 (lens minor 2): a failure belongs to the model that failed. After base.en's load failed, pressing a row whose model file
    /// is not on this PC starts no load (the tray only says it is not downloaded), so base.en's failure is not shown against it, and
    /// no success line is either: the status line says nothing.
    /// </summary>
    [Fact]
    public Task An_old_failure_is_not_reported_against_a_different_model() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Profile);
        using var release = new ManualResetEventSlim(initialState: true);
        var (shell, window) = Open(scratch, models: FailingBase(release));
        Click(Row(window, $"Use {BaseGreedy}"));
        Assert.True(Eventually(() => shell.SpeechLoadProblem is not null), "the load never failed");
        Dispatcher.UIThread.RunJobs();
        var progress = (TextBlock)typeof(CalibrationWindow).GetField("_progress", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        progress.Text = "";

        Click(Row(window, "Use small.en with greedy")); // small.en is not on disk in this scratch profile
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("", progress.Text);
        window.Close();
        shell.Dispose();
    });
}
