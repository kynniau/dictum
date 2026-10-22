using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Help;
using Dictum.Hotkey;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// 1.2 polish (22/10/2026): the defects the demo lane found driving the real 1.2 build in Windows Sandbox, each pinned where it
/// lives. Headless: nothing reaches the desktop. The welcome window's own pins are in OnboardingTests (layout, Try it).
/// </summary>
public class PolishTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string?> Texts(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button ButtonLabelled(ILogical root, string label) => All<Button>(root).Single(b => b.Content as string == label);

    // ---- 4. One display name for a key ----

    /// <summary>Pin (item 4): every key a person sees goes through one helper; the settings token is unchanged.</summary>
    [Theory]
    [InlineData("RightCtrl", "Right Ctrl")]
    [InlineData("RightAlt", "Right Alt")]
    [InlineData("LeftWin", "Left Win")]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space")]
    [InlineData("ctrl+win", "Ctrl+Win")]
    [InlineData("Mouse4", "Mouse 4")]
    [InlineData("RightMouse", "Right Mouse")]
    [InlineData("CapsLock", "Caps Lock")]
    [InlineData("F9", "F9")]
    [InlineData("F24", "F24")]
    [InlineData("Numpad5", "Numpad 5")]
    [InlineData("PageUp", "Page Up")]
    [InlineData("0x7B", "F12")]
    [InlineData("Ctrl+Mouse4", "Ctrl+Mouse 4")]
    [InlineData("not a key", "not a key")]
    public void A_hotkey_is_shown_by_its_display_name(string token, string shown) => Assert.Equal(shown, HotkeyNames.Display(token));

    /// <summary>Pin (item 4): a raw code with no name stays a code; the token the parser writes is unchanged.</summary>
    [Fact]
    public void A_raw_code_stays_a_code_and_the_token_is_unchanged()
    {
        Assert.Equal("0xE8", HotkeyNames.Key("0xE8"));
        Assert.Equal("RightCtrl", new AppSettings().Hotkey); // the settings value stays the token
        Assert.Equal("RightCtrl", HotkeyParser.Parse("rctrl").Display);
    }

    /// <summary>
    /// Pin (item 4): no window builds a key's name from the parser's token (HotkeySpec.Display) any more: every one goes through
    /// HotkeyNames. The CLI, the logs and the settings loader keep the token on purpose (settings.json is written with it).
    /// </summary>
    [Fact]
    public void No_window_shows_the_parser_token()
    {
        var ui = Path.Combine(FixBatchDownloadTests.RepoRoot(), "src", "Dictum.UI");
        var offenders = Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"[sS]pec\??\.Display\b|_settings\.Hotkey\}|Settings\.Hotkey\}"))
            .Where(x => !Regex.IsMatch(x.line, @"\b_?[lL]og\.(Info|Warn|Error)\(")) // the log keeps the token
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>Pin (item 4): the tray panel's header, its tooltip, the hotkey box and calibration show "Right Ctrl".</summary>
    [Fact]
    public Task The_panel_box_and_calibration_show_the_display_name() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Equal("RightCtrl", shell.Settings.Hotkey);
        Assert.EndsWith(" · Right Ctrl", shell.Status.Models);
        Assert.EndsWith(" · Right Ctrl", shell.Status.Tooltip);
        Assert.DoesNotContain("RightCtrl", shell.Status.Models + shell.Status.Tooltip);

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(shell.Status.Models, Texts(flyout));
        flyout.Close();

        var box = new HotkeyCaptureBox(shell, shell.Settings.Hotkey, listenAtOnce: false);
        var window = new Window { Content = box };
        window.Show();
        Assert.Contains("Right Ctrl", Texts(box));
        box.Start();
        platform.LastMonitor!.Press(Vk.RMenu, down: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Windows received: Right Alt down", Texts(box)); // the display name says the side
        box.Stop();
        window.Close();

        var calibration = new CalibrationWindow(shell);
        calibration.Show();
        Assert.Contains("or hold Right Ctrl", Texts(calibration));
        calibration.Close();
        shell.Dispose();
    });

    // ---- 5 and 6. No microphone ----

    /// <summary>
    /// Pin (item 5): with no microphone listed the tray panel shows PR #22's no-microphone sentence under its header, and its
    /// picker says "No microphone found", not "System default".
    /// </summary>
    [Fact]
    public Task With_no_microphone_the_panel_says_so() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        platform.Devices.Clear();
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(Texts(flyout), t => t is not null && t.Split('\n').Contains(NoSoundText.NoMicrophoneBalloon));
        var mic = All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == "Microphone");
        Assert.Equal([MicrophoneChoices.NoneFound], Assert.IsAssignableFrom<IEnumerable<string>>(mic.ItemsSource));
        Assert.Equal("No microphone found", MicrophoneChoices.NoneFound);
        flyout.Close();

        platform.Devices.Add(new AudioDeviceInfo("id-1", "Test microphone", true)); // one again: no line, the usual list
        flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.DoesNotContain(Texts(flyout), t => t is not null && t.Contains(NoSoundText.NoMicrophoneBalloon, StringComparison.Ordinal));
        mic = All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == "Microphone");
        Assert.Equal([MicrophoneChoices.SystemDefault, "Test microphone (default)"], Assert.IsAssignableFrom<IEnumerable<string>>(mic.ItemsSource));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 5): a list that throws (the audio service stopped) is none, as the welcome window treats it, not a crash.</summary>
    [Fact]
    public Task A_microphone_list_that_throws_is_none_in_the_panel() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        platform.ListDevicesThrows = true;
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(Texts(flyout), t => t is not null && t.Contains(NoSoundText.NoMicrophoneBalloon, StringComparison.Ordinal));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 5): the problem line keeps a model problem and adds the sentence under it.</summary>
    [Fact]
    public void The_no_microphone_sentence_joins_the_problem_line()
    {
        Assert.Null(FlyoutWindow.WithNoMicrophone(null, none: false));
        Assert.Equal("x", FlyoutWindow.WithNoMicrophone("x", none: false));
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, FlyoutWindow.WithNoMicrophone(null, none: true));
        Assert.Equal($"x\n{NoSoundText.NoMicrophoneBalloon}", FlyoutWindow.WithNoMicrophone("x", none: true));
    }

    /// <summary>
    /// Pin (item 6): one no-microphone sentence. The platform's "no microphone" failure carries the wizard's line, byte for
    /// byte, and the Windows capture throws it rather than a sentence of its own.
    /// </summary>
    [Fact]
    public void The_platform_says_no_microphone_with_the_one_sentence()
    {
        Assert.Equal("No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.", new NoMicrophoneException().Message);
        var root = FixBatchDownloadTests.RepoRoot();
        var devices = File.ReadAllText(Path.Combine(root, "src", "Dictum.Windows", "Audio", "CaptureDevices.cs"));
        Assert.Contains("throw new NoMicrophoneException()", devices);
        var literals = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("\"No microphone found.", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["NoSoundText.cs"], literals); // the sentence is written once
    }

    // ---- 6 and 7. Calibration ----

    private sealed class NoMicrophone : IAudioCapture
    {
        public float CurrentLevel => 0f;
#pragma warning disable CS0067 // never raised: the start fails
        public event Action<Exception>? Failed;
#pragma warning restore CS0067
        public void Start(string? deviceSetting, long keyDownTickMs) => throw new NoMicrophoneException();
        public CapturedAudio Stop() => new([], 0f);
    }

    /// <summary>
    /// Pin (items 6 and 7): with no microphone, Record says the wizard's sentence, in the body text size, in the same panel as
    /// the Record button, and nothing under Run test.
    /// </summary>
    [Fact]
    public Task Calibration_says_no_microphone_next_to_Record() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        platform.CaptureFactory = () => new NoMicrophone();
        var window = new CalibrationWindow(shell);
        window.Show();
        var record = ButtonLabelled(window, "Record");
        Click(record);
        Dispatcher.UIThread.RunJobs();

        var said = All<TextBlock>(window).Where(t => t.Text == NoSoundText.NoMicrophoneBalloon).ToList();
        var line = Assert.Single(said);
        Assert.True(line.Classes.Contains("body"), "the failure is not in the body text style");
        Assert.Equal(13, line.FontSize); // Theme/Controls.axaml's body size, not the 12 px caption
        var card = line.FindLogicalAncestorOfType<Border>()!;
        Assert.Contains(record, All<Button>(card)); // the same card as Record
        Assert.DoesNotContain(ButtonLabelled(window, "Run test"), All<Button>(card));
        Assert.DoesNotContain(Texts(window), t => t is not null && t.StartsWith("That did not work", StringComparison.Ordinal));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 6): anything else that stops a take keeps "That did not work: …".</summary>
    [Fact]
    public void Other_failures_keep_their_words()
    {
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, CalibrationWindow.FailureText(new NoMicrophoneException()));
        Assert.Equal("That did not work: busy", CalibrationWindow.FailureText(new InvalidOperationException("busy")));
    }

    // ---- 8. Calibration names no folder ----

    /// <summary>
    /// Pin (item 8): the calibration intro puts no path on screen (a path names the Windows user); a button opens the folder
    /// through the platform instead.
    /// </summary>
    [Fact]
    public Task Calibration_shows_no_path_and_opens_the_folder() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new CalibrationWindow(shell);
        window.Show();
        var texts = Texts(window);
        Assert.Contains($"{CalibrationWindow.Framing} {CalibrationWindow.RecordingsNote}", texts);
        Assert.DoesNotContain(texts, t => t is not null && (t.Contains(scratch.Root.FullName, StringComparison.OrdinalIgnoreCase)
                                                            || t.Contains(scratch.Paths.CalibrationDir, StringComparison.OrdinalIgnoreCase)
                                                            || Regex.IsMatch(t, @"[A-Za-z]:\\")));
        Click(ButtonLabelled(window, CalibrationWindow.OpenRecordingsLabel));
        Assert.Equal([scratch.Paths.CalibrationDir], platform.Opened);
        window.Close();
        shell.Dispose();
    });

    // ---- 10. The tray panel in the Simple view ----

    /// <summary>
    /// Pin (item 10): in the Simple view the panel's header names the tier and the clean-up as Settings > Models does, with no
    /// model file name or runtime; Advanced keeps them. Only Advanced shows the Mode line under Light | Full (owner decision 5,
    /// 22/10/2026).
    /// </summary>
    [Theory]
    [InlineData("simple")]
    [InlineData("advanced")]
    public Task The_panel_header_follows_the_view(string view) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var models = shell.Status.Models;
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var texts = Texts(flyout);
        Assert.Contains(models, texts);
        if (view == "simple")
        {
            Assert.StartsWith($"{SimpleView.StandardLabel} · ", models);
            Assert.EndsWith($" · {SimpleView.TidyLabel} · Right Ctrl", models);
            Assert.DoesNotMatch(@"base\.en|Vulkan|CPU|rules", models);
            Assert.DoesNotContain(texts, t => t is not null && t.Contains("Light: ", StringComparison.Ordinal)); // owner decision 5
        }
        else
        {
            Assert.StartsWith("base.en · ", models);
            Assert.Contains(" · rules · Right Ctrl", models);
            Assert.Contains("Full: small.en and qwen2.5-1.5b.\nLight: base.en and the rule-based tidy.", texts);
        }
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 10): the Simple names come from SimpleView's one table, for every clean-up the Simple view knows.</summary>
    [Fact]
    public void The_simple_names_come_from_the_one_table()
    {
        Assert.Equal(SimpleView.StandardLabel, SimpleView.SpeechName("base.en", gpuMachine: false));
        Assert.Equal(SimpleView.MoreAccurateLabel, SimpleView.SpeechName("small.en", gpuMachine: false));
        Assert.Equal(SimpleView.MostAccurateLabel, SimpleView.SpeechName("large-v3-turbo", gpuMachine: true));
        Assert.Equal("Custom: tiny.en", SimpleView.SpeechName("tiny.en", gpuMachine: false));
        Assert.Equal(SimpleView.StandardLabel, SimpleView.SpeechName("small", gpuMachine: false, "es"));

        var tidy = SimpleView.TidyModel(gpuMachine: false);
        Assert.Equal(SimpleView.TidyLabel, SimpleView.CleanupName(new LlmSettings { Provider = LlmSettings.NoProvider }, false, "en", false, "rules"));
        Assert.Equal(SimpleView.AiCleanupLabel, SimpleView.CleanupName(new LlmSettings { Provider = LlmSettings.Local, Model = tidy }, false, "en", false, tidy));
        Assert.Equal(SimpleView.TidyLabel, SimpleView.CleanupName(new LlmSettings { Provider = LlmSettings.Local, Model = tidy }, false, "es", false, tidy)); // no model runs on it
        Assert.Equal(SimpleView.OffHeaderLabel, SimpleView.CleanupName(new LlmSettings { Provider = LlmSettings.Off }, false, "en", false, "no clean-up")); // owner decision 6
        Assert.Equal("Custom: anthropic", SimpleView.CleanupName(new LlmSettings { Provider = "anthropic" }, false, "en", false, "anthropic"));
    }

    /// <summary>
    /// Pin (item 10): the runtime is the one that loaded, never the configured order; a Vulkan runtime that reported no device
    /// leaves the model on the CPU, so it says CPU. The sandbox's log (21/10/2026) had Vulkan with one device: "Vulkan" was true there.
    /// </summary>
    [Theory]
    [InlineData("Vulkan", false, 1, "Vulkan")]
    [InlineData("Vulkan", false, -1, "Vulkan")]
    [InlineData("Vulkan", false, 0, "CPU")]
    [InlineData("Vulkan", true, 1, "CPU")]
    [InlineData("CPU", false, -1, "CPU")]
    [InlineData("CPU (no AVX)", true, 0, "CPU (no AVX)")]
    [InlineData("not loaded", false, -1, "not loaded")]
    public void The_runtime_is_where_the_model_runs(string loaded, bool runsOnCpu, int devices, string shown) =>
        Assert.Equal(shown, AppShell.RuntimeLabel(loaded, runsOnCpu, devices));

    // ---- 11, 12, 13. Settings > Models ----

    private static ModelsPage OpenModels(SettingsWindow window)
    {
        window.ShowPage("Models");
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<ModelsPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
    }

    /// <summary>Pin (item 11): the Simple view's Language card names no model; the Advanced one has no capital after its semicolon.</summary>
    [Theory]
    [InlineData("simple")]
    [InlineData("advanced")]
    public Task The_language_card_speaks_plainly_in_the_simple_view(string view) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        var texts = Texts(OpenModels(window));
        var (shown, hidden) = view == "simple"
            ? (ModelsPage.LanguageIntroSimple, ModelsPage.LanguageIntroAdvanced)
            : (ModelsPage.LanguageIntroAdvanced, ModelsPage.LanguageIntroSimple);
        Assert.Contains(shown, texts);
        Assert.DoesNotContain(hidden, texts);
        Assert.Equal("Dictum comes with an English-only model. For other languages it downloads a model that understands them; choose the language here.", ModelsPage.LanguageIntroSimple);
        Assert.DoesNotMatch(@"\.en|multilingual|large-v3|small|medium", ModelsPage.LanguageIntroSimple);
        Assert.DoesNotMatch(@"; [A-Z]", ModelsPage.LanguageIntroAdvanced);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 12): "In use" is a tag in the link colour, not a greyed button, and keeps its accessible name.</summary>
    [Theory]
    [InlineData("simple")]
    [InlineData("advanced")]
    public Task In_use_is_a_tag_not_a_disabled_button(string view) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        var page = OpenModels(window);
        Assert.DoesNotContain(All<Button>(page), b => b.Content as string == ModelsPage.InUseLabel);
        var tags = All<Border>(page).Where(b => b.Child is TextBlock { Text: ModelsPage.InUseLabel } && b.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(tags);
        var app = Application.Current!;
        var link = (ISolidColorBrush)app.FindResource(app.ActualThemeVariant, "LinkBrush")!;
        foreach (var tag in tags)
        {
            Assert.False(tag.Focusable);
            var label = Assert.IsType<TextBlock>(tag.Child);
            Assert.Equal(ModelsPage.InUseLabel, label.Text);
            Assert.Equal(link.Color, Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
            Assert.True(label.IsEnabled && tag.IsEffectivelyEnabled, "the tag is drawn as disabled");
            var peer = ControlAutomationPeer.CreatePeerForElement(label); // a Border has no peer: the label is what a screen reader reads
            Assert.True(peer.IsControlElement());
            Assert.Equal(ModelsPage.InUseLabel, peer.GetName());
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (item 13): the model folder is trimmed from the left, so its end stays in view, with the whole path in the tooltip
    /// and the accessible name; it is a tab stop.
    /// </summary>
    [Fact]
    public Task The_model_folder_shows_its_end() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell) { Width = 640, Height = 480 };
        window.Show();
        var page = OpenModels(window);
        var box = All<PathBox>(page).Single();
        var real = shell.RealPath(shell.ModelsDir);
        Assert.Equal(real, box.Path);
        Assert.Equal(real, ToolTip.GetTip(box));
        var peer = ControlAutomationPeer.CreatePeerForElement(box); // a tab stop a screen reader can read (lens on 4abba54)
        Assert.True(peer.IsControlElement());
        Assert.Equal(ModelsPage.FolderLabel, peer.GetName());
        Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
        var value = Assert.IsAssignableFrom<IValueProvider>(peer);
        Assert.Equal(real, value.Value);
        Assert.True(value.IsReadOnly);
        Assert.Equal(TextTrimming.LeadingCharacterEllipsis, box.Text.TextTrimming);
        Assert.True(box.Focusable && box.IsTabStop);
        box.Focus(NavigationMethod.Tab); // keyboard focus opens the whole path, as the text box could be read before
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolTip.GetIsOpen(box), "keyboard focus did not open the tooltip");
        ButtonLabelled(page, ModelsPage.BrowseLabel).Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.False(ToolTip.GetIsOpen(box));
        Assert.DoesNotContain(All<TextBox>(page), t => t.Text == real); // not the read-only text box that cut the end off

        box.Path = @"C:\Users\someone with a long name\AppData\Local\Dictum\a folder with a long name\models";
        box.Width = 200;
        Dispatcher.UIThread.RunJobs();
        box.UpdateLayout();
        var line = Assert.Single(box.Text.TextLayout.TextLines);
        Assert.True(line.HasCollapsed, "the long path was not trimmed");
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 13): a chooser starts in the folder, or the nearest folder above it that exists (the default one is made by a download).</summary>
    [Fact]
    public void The_folder_chooser_starts_at_the_nearest_folder_that_exists()
    {
        var there = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TestPaths.Native(@"C:\Users\x\AppData\Local\Dictum"), TestPaths.Native(@"C:\Users\x\AppData\Local"), TestPaths.Native(@"C:\") };
        Assert.Equal(TestPaths.Native(@"C:\Users\x\AppData\Local\Dictum"), ShellDialogs.StartFolder(TestPaths.Native(@"C:\Users\x\AppData\Local\Dictum\models"), there.Contains));
        Assert.Equal(TestPaths.Native(@"C:\Users\x\AppData\Local\Dictum"), ShellDialogs.StartFolder(TestPaths.Native(@"C:\Users\x\AppData\Local\Dictum"), there.Contains));
        Assert.Null(ShellDialogs.StartFolder(TestPaths.Native(@"Q:\nowhere\models"), there.Contains));
    }

    // ---- 14. Help: the questions first ----

    private static HelpPage OpenHelp(SettingsWindow window)
    {
        window.ShowPage(HelpText.PageTitle);
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<HelpPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
    }

    /// <summary>
    /// Pin (item 14): the Help page opens with a list of its questions, one link each, in order; Tab walks the list; pressing a
    /// link moves keyboard focus to its answer, which scrolls into view with its ring on.
    /// </summary>
    [Fact]
    public Task Help_lists_its_questions_and_each_jumps_to_its_answer() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell) { Width = 860, Height = 640 };
        window.Show();
        var page = OpenHelp(window);
        var scroll = All<ScrollViewer>(window).Single(s => s.Content is Border { Child: ContentControl });
        var links = All<Button>(page).Where(b => b.Classes.Contains("link") && b.Content is TextBlock).ToList();
        Assert.Equal(HelpText.Questions.Select(q => q.Question), links.Select(AutomationProperties.GetName));
        Assert.Equal(HelpText.Questions.Select(q => q.Question), links.Select(b => ((TextBlock)b.Content!).Text));
        Assert.All(links, l => Assert.Equal(HelpText.IndexLinkHelp, AutomationProperties.GetHelpText(l)));
        var guide = ButtonLabelled(page, HelpText.GuideButton);
        Assert.True(links[^1].TranslatePoint(default, scroll)!.Value.Y < guide.TranslatePoint(default, scroll)!.Value.Y, "the list is not above the rest");

        // Tab walks the list in order, then goes on to the guide button.
        links[0].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i <= links.Count; i++)
        {
            var next = KeyboardNavigationHandler.GetNext(TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement()!, NavigationDirection.Next);
            Assert.Same(i < links.Count ? links[i] : guide, next);
            ((InputElement)next!).Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
        }

        // Every link moves focus to its own answer; the last one is scrolled into view with its ring on.
        var answers = All<Border>(page).Where(b => b.Focusable && b.IsTabStop).ToDictionary(b => AutomationProperties.GetName(b)!);
        foreach (var link in links)
        {
            scroll.Offset = default;
            Dispatcher.UIThread.RunJobs();
            Click(link);
            Dispatcher.UIThread.RunJobs();
            var answer = answers[AutomationProperties.GetName(link)!];
            Assert.True(answer.IsFocused, $"{AutomationProperties.GetName(link)}: its answer has no focus");
            Assert.NotEqual(Brushes.Transparent, answer.BorderBrush);
            var top = answer.TranslatePoint(default, scroll)!.Value.Y;
            Assert.True(top >= -0.5 && top < scroll.Viewport.Height, $"{AutomationProperties.GetName(link)}: its answer's top is at {top:0} of {scroll.Viewport.Height:0}");
        }
        window.Close();
        shell.Dispose();
    });

    // ---- Lens fixes (22/10/2026), lens on 4abba54 ----

    /// <summary>
    /// Pin (major 1): when the recordings folder cannot be opened (a file stands where the folder goes), the window says so with
    /// no path; the reason, with the path, goes to the log.
    /// </summary>
    [Fact]
    public Task Calibration_says_an_open_failure_without_a_path() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var log = new RecordingLog();
        var (shell, platform, _) = scratch.Shell(log: log);
        shell.Reload(userRequested: false);
        var window = new CalibrationWindow(shell);
        window.Show();
        Directory.CreateDirectory(Path.GetDirectoryName(scratch.Paths.CalibrationDir)!);
        File.WriteAllText(scratch.Paths.CalibrationDir, "not a folder"); // the folder cannot be made or opened
        Click(ButtonLabelled(window, CalibrationWindow.OpenRecordingsLabel));
        Dispatcher.UIThread.RunJobs();
        var texts = Texts(window);
        Assert.Contains(CalibrationWindow.OpenRecordingsFailed, texts);
        Assert.DoesNotContain(texts, t => t is not null && Regex.IsMatch(t, @"[A-Za-z]:\\"));
        Assert.Empty(platform.Opened);
        Assert.Contains("the recordings folder could not be opened", log.All, StringComparison.Ordinal);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (major 4): Settings > Microphone draws with the audio service stopped (the list throws) and says no microphone was found.</summary>
    [Fact]
    public Task Settings_microphone_survives_a_list_that_throws() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        platform.ListDevicesThrows = true;
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Microphone");
        Dispatcher.UIThread.RunJobs();
        var page = All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl);
        var picker = All<ComboBox>(page).Single();
        Assert.Equal([MicrophoneChoices.NoneFound], Assert.IsAssignableFrom<IEnumerable<string>>(picker.ItemsSource));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (major 5): the tooltip on screen names the key by its display name; the log keeps the settings token.</summary>
    [Fact]
    public Task The_tooltip_log_keeps_the_token() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        var log = new RecordingLog();
        var models = new Dictum.Transcribe.ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model), log: log);
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith(" · Right Ctrl", platform.Tray!.Tooltip);
            var logged = log.Lines.Select(l => l.Message).Where(m => m.StartsWith("Tray tooltip: ", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(logged);
            Assert.All(logged, m => Assert.EndsWith(" · RightCtrl", m));
        }
        finally
        {
            shell.Dispose();
        }
    });
}
