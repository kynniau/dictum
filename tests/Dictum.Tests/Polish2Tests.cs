using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Help;
using Dictum.Hotkey;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// 1.2 polish round 2 (22/10/2026): the defects a second Sandbox recording of the round-1 build found, each pinned where it
/// lives. Headless: nothing reaches the desktop. Try it's focus pin is in OnboardingTests.Window.
/// </summary>
public class Polish2Tests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string?> Texts(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button ButtonLabelled(ILogical root, string label) => All<Button>(root).Single(b => b.Content as string == label);

    /// <summary>Each line of a laid-out text block, as the text it shows.</summary>
    private static List<string> Lines(TextBlock block) =>
        [.. block.TextLayout.TextLines.Select(l => block.Text!.Substring(l.FirstTextSourceIndex, Math.Min(l.Length, block.Text!.Length - l.FirstTextSourceIndex)).TrimEnd())];

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    // ---- 1. The tray panel's header and its Mode line ----

    /// <summary>
    /// The header lines the pin measures: every Simple line (each tier, each clean-up, the longest key names), and the
    /// Advanced line of each preset on a CPU and on a GPU machine with the runtime that loaded.
    /// </summary>
    public static TheoryData<string> HeaderLines()
    {
        var data = new TheoryData<string>();
        string[] keys = [HotkeyNames.Display("RightCtrl"), HotkeyNames.Display("Mouse5")];
        foreach (var key in keys)
        {
            foreach (var speech in new[] { SimpleView.StandardLabel, SimpleView.MoreAccurateLabel, SimpleView.MostAccurateLabel })
            foreach (var cleanup in new[] { SimpleView.TidyLabel, SimpleView.AiCleanupLabel, SimpleView.OffLabel })
                data.Add($"{speech} · {cleanup} · {key}");
            foreach (var gpu in new[] { false, true })
            {
                var runtime = gpu ? "Vulkan" : "CPU";
                var full = ModePresets.For(ModePreset.Full, gpu);
                data.Add($"{ModePresets.LightSpeech} · {runtime} · rules · {key}");
                data.Add($"{full.SpeechModel} · {runtime} · {full.CleanupModel} · {runtime} · {key}");
            }
        }
        return data;
    }

    /// <summary>
    /// Pin (item 1): at the panel's width the models line is one line, whole: no wrap ("Right" and "Ctrl" fell on two lines)
    /// and no ellipsis, with the longest state word beside the switch.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeaderLines))]
    public Task The_panel_header_is_one_whole_line(string line) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "ui": { "view": "simple" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var status = shell.Status;
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Equal(FlyoutWindow.PanelWidth + 28, flyout.Width); // the panel's width, with its shadow's margin
        var models = All<TextBlock>(flyout).Single(t => t.Text == status.Models);
        All<TextBlock>(flyout).Single(t => t.Text == status.State).Text = "Downloading"; // the longest state word
        models.Text = line;
        Layout(flyout);
        var lines = models.TextLayout.TextLines;
        Assert.True(lines.Count == 1, $"\"{line}\" took {lines.Count} lines: {string.Join(" | ", Lines(models))}");
        Assert.False(lines[0].HasCollapsed, $"\"{line}\" was trimmed at {models.Bounds.Width:0} px");
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 1): the reported line, drawn by the panel itself, is one line; a line too long for the panel ends in an ellipsis, whole in its tooltip.</summary>
    [Fact]
    public Task The_panel_header_never_wraps() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "ui": { "view": "simple" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Equal($"{SimpleView.StandardLabel} · {SimpleView.TidyLabel} · Right Ctrl", shell.Status.Models); // first-run-poster.png's line
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Layout(flyout);
        var models = All<TextBlock>(flyout).Single(t => t.Text == shell.Status.Models);
        Assert.Equal([shell.Status.Models], Lines(models));
        Assert.Equal(shell.Status.Models, ToolTip.GetTip(models));

        const string Long = "Custom: a-model-file-with-a-very-long-name-q5_0 · loading… · Custom: another-long-clean-up-model · error · Ctrl+Shift+Mouse 5";
        models.Text = Long;
        ToolTip.SetTip(models, Long);
        Layout(flyout);
        var line = Assert.Single(models.TextLayout.TextLines);
        Assert.True(line.HasCollapsed, "the long line was not trimmed");
        Assert.Equal(Long, ToolTip.GetTip(models));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>The Mode lines the pin measures: Simple and model names, on a CPU and a GPU machine, with and without the Custom note.</summary>
    public static TheoryData<bool, bool, bool> ModeLines() => new()
    {
        { true, false, false }, { true, true, false }, { true, false, true }, { true, true, true },
        { false, false, false }, { false, true, false }, { false, false, true }, { false, true, true },
    };

    /// <summary>
    /// Pin (item 1): the line under Light | Full puts one sentence on each line and never breaks inside one ("Tidy my / words."):
    /// at the panel's width it has exactly one line per sentence, each ending with its full stop.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModeLines))]
    public Task The_mode_line_breaks_between_presets_only(bool simple, bool gpu, bool custom) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "ui": { "view": "simple" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var hint = All<TextBlock>(flyout).Single(t => t.Classes.Contains("hint") && (t.Text ?? "").Contains("Light: ", StringComparison.Ordinal));
        string[] presets = simple ? SimpleView.ModeHint(gpu, "en", translate: false)! : FlyoutWindow.ModelModeHint(gpu);
        hint.Text = FlyoutWindow.ModeLine(custom, presets);
        Layout(flyout);
        string[] sentences = custom ? [FlyoutWindow.CustomPresetNote, .. presets] : presets;
        Assert.Equal(sentences, Lines(hint));
        flyout.Close();
        shell.Dispose();
    });

    // ---- 2. Open log folder only for errors ----

    /// <summary>
    /// Pin (item 2): the no-microphone sentence alone has no "Open log folder" under it (that link is for errors); a model
    /// problem keeps it, with the sentence under the problem.
    /// </summary>
    [Fact]
    public Task Open_log_folder_is_for_errors_not_the_microphone_line() => UiSession.Run(() =>
    {
        using (var scratch = new Scratch())
        {
            var (shell, platform, _) = scratch.Shell();
            shell.Reload(userRequested: false);
            platform.Devices.Clear();
            Assert.Null(shell.Status.Problem);
            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.Contains(NoSoundText.NoMicrophoneBalloon, Texts(flyout));
            Assert.False(ButtonLabelled(flyout, "Open log folder").IsEffectivelyVisible, "the log link stands under the no-microphone line");
            flyout.Close();
            shell.Dispose();
        }

        using (var scratch = new Scratch())
        {
            var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
            File.WriteAllText(model, "");
            File.WriteAllText(scratch.Paths.SettingsFile, $$"""{ "hotkey": "F24", "whisper": { "model": {{System.Text.Json.JsonSerializer.Serialize(model)}} }, "llm": { "provider": "none" } }""");
            var models = new ModelManager(NullLog.Instance, loader: (_, _, _) => throw new InvalidOperationException("the model would not load"), gpuInUse: () => false);
            var (shell, platform, _) = scratch.Shell(models);
            shell.Reload(userRequested: false);
            Assert.True(Eventually(() => shell.Status.Problem is not null), "the load never failed");
            platform.Devices.Clear();
            var flyout = new FlyoutWindow(shell);
            flyout.ShowNear(null);
            Assert.Contains(Texts(flyout), t => t is not null && t.EndsWith($"\n{NoSoundText.NoMicrophoneBalloon}", StringComparison.Ordinal));
            Assert.True(ButtonLabelled(flyout, "Open log folder").IsEffectivelyVisible, "a model problem lost its log link");
            flyout.Close();
            shell.Dispose();
        }
    });

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

    // ---- 4. Help: an answer jumped to starts at the top ----

    /// <summary>
    /// Pin (item 4): every question in Help's list scrolls its answer's heading to the top of the view (<see cref="HelpPage.JumpGap"/>
    /// below it), not to the bottom edge with its last line cut off; an answer the page cannot scroll that far (the last ones)
    /// is shown whole. Focus is on the answer, ring on.
    /// </summary>
    [Fact]
    public Task A_help_jump_puts_the_answer_at_the_top() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell) { Width = 860, Height = 640 };
        window.Show();
        window.ShowPage(HelpText.PageTitle);
        Dispatcher.UIThread.RunJobs();
        var page = Assert.IsType<HelpPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
        var scroll = All<ScrollViewer>(window).Single(s => s.Content is Border { Child: ContentControl });
        var links = All<Button>(page).Where(b => b.Classes.Contains("link") && b.Content is TextBlock).ToList();
        var answers = All<Border>(page).Where(b => b.Focusable && b.IsTabStop).ToDictionary(b => AutomationProperties.GetName(b)!);
        Assert.Equal(HelpText.Questions.Count, links.Count);
        var atTop = 0;
        foreach (var link in links)
        {
            var name = AutomationProperties.GetName(link)!;
            scroll.Offset = default;
            Layout(window);
            Click(link);
            Layout(window);
            var answer = answers[name];
            Assert.True(answer.IsFocused, $"{name}: its answer has no focus");
            var top = answer.TranslatePoint(default, scroll)!.Value.Y;
            var max = scroll.Extent.Height - scroll.Viewport.Height;
            if (scroll.Offset.Y < max - 0.5)
            {
                Assert.True(Math.Abs(top - HelpPage.JumpGap) < 1, $"{name}: its answer starts {top:0} px into the view, not at the top");
                atTop++;
            }
            else
            {
                Assert.True(top >= 0 && top + answer.Bounds.Height <= scroll.Viewport.Height + 0.5,
                    $"{name}: at the page's end its answer runs from {top:0} to {top + answer.Bounds.Height:0} of {scroll.Viewport.Height:0}");
            }
        }
        Assert.True(atTop >= 2, $"only {atTop} answers could be scrolled to the top: the pin checked nothing"); // the two in help/07 and help/09 among them
        Assert.Contains("Where does my voice go?", links.Take(atTop).Select(l => AutomationProperties.GetName(l)));
        window.Close();
        shell.Dispose();
    });

    // ---- 5. "In use" once on a row ----

    /// <summary>The rows of Settings > Models that carry the "In use" tag: each row's title and its line.</summary>
    internal static List<(string Title, string Line)> InUseRows(ILogical root) =>
    [
        .. All<Grid>(root)
            .Where(g => g.IsEffectivelyVisible && g.Children.OfType<Border>().Any(b => b.Child is TextBlock { Text: ModelsPage.InUseLabel }))
            .Select(g => ((StackPanel)g.Children[0]).Children.OfType<TextBlock>().ToList())
            .Select(t => (t[0].Text ?? "", t[1].Text ?? "")),
    ];

    /// <summary>
    /// Pin (item 5): a row with the "In use" tag does not say "In use" again in its line, in either view, for each kind of row
    /// that can be in use: a tier, Tidy (with and without AI clean-up), Off, a Custom speech and clean-up row, a tier the
    /// language rules out, and the Advanced catalogue. The line keeps its other words.
    /// </summary>
    [Theory]
    [InlineData("simple", """ "whisper": { "model": "base.en" }, "llm": { "provider": "none" } """, SimpleView.StandardLabel)]
    [InlineData("simple", """ "whisper": { "model": "base.en" }, "llm": { "provider": "off" } """, SimpleView.OffLabel)]
    [InlineData("simple", """ "whisper": { "model": "base.en" }, "llm": { "provider": "local", "model": "qwen2.5-1.5b" } """, SimpleView.TidyTitle)]
    [InlineData("simple", """ "whisper": { "model": "tiny.en" }, "llm": { "provider": "local", "model": "qwen2.5-0.5b" } """, "Custom: tiny.en")]
    [InlineData("simple", """ "whisper": { "model": "small", "language": "yue" }, "llm": { "provider": "none" } """, SimpleView.StandardLabel)] // a tier the language rules out, in use
    [InlineData("advanced", """ "whisper": { "model": "base.en" }, "llm": { "provider": "local", "model": "qwen2.5-1.5b" } """, "base.en · 147 MB")]
    public Task In_use_is_said_once_on_a_row(string view, string json, string expectedRow) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "{{view}}" }, {{json}} }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        Dispatcher.UIThread.RunJobs();
        var rows = InUseRows(window);
        Assert.Contains(rows, r => r.Title == expectedRow);
        foreach (var (title, line) in rows)
        {
            Assert.DoesNotContain("In use", line, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(line), $"{title}: its line lost its other words");
        }
        if (json.Contains("qwen2.5-1.5b", StringComparison.Ordinal) && view == "simple")
            Assert.Contains(rows, r => r.Title == SimpleView.TidyTitle && r.Line.StartsWith($"{SimpleView.TidyWithAiState} · ", StringComparison.Ordinal));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 5): the words the rows keep (PROPOSED, 1.2 polish round 2).</summary>
    [Fact]
    public void The_rows_keep_their_other_words()
    {
        Assert.Equal("With AI clean-up", SimpleView.TidyWithAiState);
        Assert.Equal("Not one of the choices above.", SimpleView.CustomNote);
    }

    // ---- 6. Calibration waits for a microphone ----

    /// <summary>
    /// Pin (item 6): with no microphone listed, calibration shows the sentence to read, the no-microphone sentence beside
    /// Record, and Record disabled; when one appears and the window is activated again, Record works and the line goes.
    /// </summary>
    [Fact]
    public Task Calibration_record_waits_for_a_microphone() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        platform.Devices.Clear();
        var window = new CalibrationWindow(shell);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var record = ButtonLabelled(window, "Record");
        Assert.False(record.IsEnabled, "Record is active with no microphone");
        Assert.Contains(NoSoundText.NoMicrophoneBalloon, Texts(window));
        Assert.Contains(Texts(window), t => t is not null && t.StartsWith('“')); // the sentence to read stays
        Assert.True(ButtonLabelled(window, "Skip").IsEnabled);

        window.Activate(); // still none: nothing changes
        Dispatcher.UIThread.RunJobs();
        Assert.False(record.IsEnabled);

        platform.Devices.Add(new AudioDeviceInfo("id-1", "Test microphone", true));
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        Assert.True(record.IsEnabled, "Record did not come back when a microphone appeared");
        Assert.DoesNotContain(NoSoundText.NoMicrophoneBalloon, Texts(window));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 6): a list that throws (the audio service stopped) is none: Record waits, nothing crashes.</summary>
    [Fact]
    public Task Calibration_with_a_list_that_throws_waits_for_a_microphone() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        platform.ListDevicesThrows = true;
        var window = new CalibrationWindow(shell);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(ButtonLabelled(window, "Record").IsEnabled);
        Assert.Contains(NoSoundText.NoMicrophoneBalloon, Texts(window));
        window.Close();
        shell.Dispose();
    });
}
