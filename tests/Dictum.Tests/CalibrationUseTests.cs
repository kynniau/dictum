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
    private static (AppShell Shell, CalibrationWindow Window) Open(Scratch scratch, ILog? log = null)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", ModelCatalog.Find("base.en")!.Bytes);
        var (shell, _, _) = scratch.Shell(SpeechModels(), log: log);
        shell.Reload(userRequested: false);
        var window = new CalibrationWindow(shell);
        window.Show();
        var session = typeof(CalibrationWindow).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var add = session.GetType().GetMethod("Add")!;
        foreach (var combo in new[] { Combo("tiny.en", 0, 9), Combo("tiny.en", 5, 7), Combo("base.en", 0, 3) }) add.Invoke(session, [combo, "clips"]);
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
    /// running (a launch override, or a switch that could not load), the row for the file's model is not marked.
    /// </summary>
    [Fact]
    public void In_use_is_the_model_the_app_holds_not_only_the_file()
    {
        using var scratch = new Scratch();
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = "tiny.en", BeamSize = 0 } };
        CalibrationHost Host(Func<string>? current) => new(scratch.Paths, () => settings, () => PersonalText.Empty,
            new ModelIntegrity(Path.Combine(scratch.Root.FullName, "verified-models.json"), NullLog.Instance), NullLog.Instance,
            FindSpeechModel: _ => null, Write: _ => true, DownloadSpeechModel: (_, _, _, _) => { }, AddDictionaryTerms: _ => { }, AddCorrections: _ => { },
            CurrentModel: current);

        Assert.True(Host(null).InUse(Combo("tiny.en", 0, 0))); // no app to ask: the file's model
        Assert.False(Host(null).InUse(Combo("tiny.en", 5, 0)));
        Assert.True(Host(() => "tiny.en").InUse(Combo("tiny.en", 0, 0)));
        Assert.False(Host(() => "base.en").InUse(Combo("tiny.en", 0, 0))); // the file says tiny.en, the app runs base.en
        Assert.True(Host(() => "base.en").InUse(Combo("base.en", 0, 0)));
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
}
