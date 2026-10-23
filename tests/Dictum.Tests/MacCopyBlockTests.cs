using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Core;
using Dictum.Help;
using Dictum.Hotkey;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// The Mac copy block (APPROVED by the owner 23/10/2026 14:06 AEDT, docs/DECISIONS.md): where an approved Windows string says
/// "this PC", "Windows", "Start menu" or "Win+V", the Mac build shows the same sentence with the Mac word; the Windows build is
/// untouched. One pin per row: the Windows words give the approved Windows string byte for byte, the Mac words the approved Mac
/// string, and where the row is on a window the window itself is built on a fake platform with each set of words.
/// </summary>
public sealed class MacCopyBlockTests
{
    public static TheoryData<bool> BothPlatforms => [false, true];

    private static PlatformWords WordsFor(bool mac) => mac ? PlatformWords.Mac : PlatformWords.Windows;

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string> Visible(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    private static (AppShell Shell, FakePlatform Platform) Shell(Scratch scratch, bool mac)
    {
        var (shell, platform, _) = scratch.Shell();
        platform.Words = WordsFor(mac);
        shell.Reload(userRequested: false);
        return (shell, platform);
    }

    private static List<string> PageLines(AppShell shell, string page)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage(page);
        var lines = Visible(window);
        window.Close();
        return lines;
    }

    private static OnboardingWindow Onboarding(AppShell shell, Scratch scratch)
    {
        var window = new OnboardingWindow(new OnboardingFlow(shell, SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void GoTo<T>(OnboardingWindow window) where T : OnboardingStep
    {
        while (window.Current is not T)
        {
            window.PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The fake hands out the Windows words: every window test before this block saw them.</summary>
    [Fact]
    public void The_fake_platform_hands_out_the_windows_words()
    {
        using var scratch = new Scratch();
        Assert.Same(PlatformWords.Windows, scratch.Shell().Platform.Words);
    }

    // Each real platform class is built only on its own system: WindowsPlatform calls kernel32 when it is constructed, so on the
    // macOS runner it throws DllNotFoundException (PR #79's first CI runs); MacPlatform is built only on a Mac for symmetry.

    /// <summary>The real Windows platform: its words and its Remove everything phrase. Windows only.</summary>
    [Fact]
    public void The_windows_platform_hands_out_the_windows_words()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsPlatform is built only on Windows (it calls kernel32 when constructed).");
        WindowsPlatformChecks();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WindowsPlatformChecks()
    {
        using var windows = new Dictum.Windows.WindowsPlatform(NullLog.Instance, AppContext.BaseDirectory);
        Assert.Same(PlatformWords.Windows, windows.Words);
        Assert.Equal(Dictum.Windows.Shell.RemovalSteps.StartupItems, windows.Removal.StartupItemsPhrase);
    }

    /// <summary>The real Mac platform: its words and its Remove everything phrase. macOS only.</summary>
    [Fact]
    public void The_mac_platform_hands_out_the_mac_words()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "MacPlatform is built only on macOS.");
        MacPlatformChecks();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MacPlatformChecks()
    {
        using var mac = new Dictum.Mac.MacPlatform(NullLog.Instance, Path.GetTempPath());
        Assert.Same(PlatformWords.Mac, mac.Words);
        Assert.Equal(Dictum.Mac.Shell.RemovalSteps.StartupItems, mac.Removal.StartupItemsPhrase);
    }

    /// <summary>Row 1 (PR #71, approved as is): the Storage folder row once a model folder is chosen, the same words on both.</summary>
    [Fact]
    public void Row01_the_storage_folder_row_is_the_same_on_both()
    {
        Assert.Equal("Logs, calibration recordings", StoragePage.DataFolderRow(modelFolderChosen: true));
        Assert.Equal("Models, logs, calibration recordings", StoragePage.DataFolderRow(modelFolderChosen: false));
    }

    /// <summary>
    /// Row 2 (PR #71): the Storage page's Remove everything sentence, from each platform's removal-steps phrase (the constants, read on
    /// any system; each real platform hands out its own above, on its own system).
    /// </summary>
    [Fact]
    public void Row02_remove_everything_names_the_login_item_on_a_mac()
    {
        Assert.Equal("Deletes every model, recording, log, setting, your dictionary, corrections and .env keys, the Start menu shortcut and the launch-at-login entry, then the program itself. " +
                     "Two warnings come first; the second needs DELETE typed.", StoragePage.RemoveEverythingText(Dictum.Windows.Shell.RemovalSteps.StartupItems));
        Assert.Equal("Deletes every model, recording, log, setting, your dictionary, corrections and .env keys, the login item, then the program itself. " +
                     "Two warnings come first; the second needs DELETE typed.", StoragePage.RemoveEverythingText(Dictum.Mac.Shell.RemovalSteps.StartupItems));
    }

    /// <summary>Rows 3 and 4: Welcome steps 1 and 2 say "this Mac" on a Mac.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row03_and_04_the_welcome_steps_name_the_computer(bool mac) => UiSession.Run(() =>
    {
        var device = mac ? "this Mac" : "this PC";
        var welcome = "Dictum types what you say. Hold down your hotkey, speak, then let go: your words appear wherever you were typing, in any program. " +
                      $"Everything happens on {device} unless you turn on a cloud option.";
        var folder = $"Dictum keeps its speech models on {device}. The included one is about 150 MB; adding more models can take several gigabytes more.";
        var notIncluded = $"Dictum keeps its speech models on {device}. The Standard model is about 150 MB to download; adding more models can take several gigabytes more.";
        Assert.Equal(welcome, OnboardingText.WelcomeText(WordsFor(mac)));
        Assert.Equal(folder, OnboardingText.FolderText(WordsFor(mac)));
        Assert.Equal(notIncluded, OnboardingText.FolderTextNotIncluded(WordsFor(mac)));

        using var scratch = new Scratch();
        OnboardingTests.Bundle(scratch);
        var (shell, _) = Shell(scratch, mac);
        var window = Onboarding(shell, scratch);
        Assert.Contains(welcome, Visible(window));
        GoTo<FolderStep>(window);
        Assert.Contains(folder, Visible(window));
        Assert.DoesNotContain(Visible(window), t => t.Contains(mac ? "this PC" : "this Mac", StringComparison.Ordinal));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Rows 5 and 7: the hotkey step's intro says what macOS receives, and the Remote Desktop caption is not shown on a Mac.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row05_and_07_the_hotkey_step_names_the_system_and_hides_the_remote_desktop_note_on_a_mac(bool mac) => UiSession.Run(() =>
    {
        var intro = $"Dictum records while you hold this key and pastes when you let go. Press it now: the box shows exactly what {(mac ? "macOS" : "Windows")} " +
                    "receives, and the light comes on while the key is held. Press a different key, or a mouse button other than the left one, to choose it instead.";
        Assert.Equal(intro, HotkeyCheck.Intro(WordsFor(mac)));
        Assert.Equal("On a Mac over Remote Desktop use the right Option key (Right Alt) or right Command (Right Win).", HotkeyCheck.MacNote);
        Assert.Equal(!mac, WordsFor(mac).ShowsRemoteDesktopNote);

        using var scratch = new Scratch();
        OnboardingTests.Bundle(scratch);
        var (shell, _) = Shell(scratch, mac);
        var window = Onboarding(shell, scratch);
        GoTo<HotkeyStep>(window);
        var text = Visible(window);
        Assert.Contains(intro, text);
        Assert.Equal(!mac, text.Contains(HotkeyCheck.MacNote));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Row 7's rule on Settings > General: the same Remote Desktop caption under the hotkey box is not shown on a Mac.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row07_the_general_page_hides_the_remote_desktop_note_on_a_mac(bool mac) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _) = Shell(scratch, mac);
        Assert.Equal(!mac, PageLines(shell, "General").Contains(HotkeyCheck.MacNote));
        shell.Dispose();
    });

    /// <summary>Row 6: the capture box's "… received:" line. The key's name follows HotkeyNames.Dialect (the Mac build says "Right Option").</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row06_the_capture_box_says_what_the_system_received(bool mac) => UiSession.Run(() =>
    {
        Assert.Equal(mac ? "macOS received: Right Option up" : "Windows received: Right Option up", HotkeyCaptureBox.Received(WordsFor(mac), "Right Option up"));

        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform) = Shell(scratch, mac);
        var box = new HotkeyCaptureBox(shell, "F24", listenAtOnce: false);
        var window = new Window { Content = box };
        window.Show();
        box.Start();
        platform.LastMonitor!.Press(Vk.RMenu, down: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains($"{(mac ? "macOS" : "Windows")} received: Right Alt down", Visible(box));
        box.Stop();
        window.Close();
        shell.Dispose();
    });

    /// <summary>Row 8: the menu-bar panel's footer.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row08_the_panel_footer_says_nothing_leaves_this_mac(bool mac) => UiSession.Run(() =>
    {
        var line = mac ? "Nothing leaves this Mac" : "Nothing leaves this PC";
        Assert.Equal(line, FlyoutWindow.NothingLeavesLine(WordsFor(mac)));

        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "none" } }""");
        var (shell, _) = Shell(scratch, mac);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(line, Visible(flyout));
        Assert.DoesNotContain(mac ? "Nothing leaves this PC" : "Nothing leaves this Mac", Visible(flyout));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Rows 9 and 10: Settings > Privacy's default line and the clipboard caption.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row09_and_10_the_privacy_page_names_the_computer_and_its_clipboard(bool mac) => UiSession.Run(() =>
    {
        var nothingLeaves = mac ? "Nothing leaves this Mac: speech recognition and clean-up both run here." : "Nothing leaves this PC: speech recognition and clean-up both run here.";
        var clipboard = mac ? "Clipboard managers that follow the concealed-item convention, and Universal Clipboard to your other devices."
                            : "Windows clipboard history (Win+V) and cloud clipboard sync.";
        Assert.Equal(nothingLeaves, PrivacyPage.NothingLeavesLine(WordsFor(mac)));
        Assert.Equal(clipboard, WordsFor(mac).ClipboardHistoryCaption);

        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "none" } }""");
        var (shell, _) = Shell(scratch, mac);
        var privacy = PageLines(shell, "Privacy");
        Assert.Contains(nothingLeaves, privacy);
        Assert.Contains(clipboard, privacy);
        Assert.DoesNotContain(privacy, l => l.Contains(mac ? "Win+V" : "Universal Clipboard", StringComparison.Ordinal));
        shell.Dispose();
    });

    /// <summary>Row 11: the calibration window's intro, and (the rule, 23/10/2026 lens on 32c6c1e) its results hint.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row11_the_calibration_intro_names_the_computer(bool mac) => UiSession.Run(() =>
    {
        var note = $"Nothing is uploaded: the recordings stay in Dictum's folder on {(mac ? "this Mac" : "this PC")} (Settings > Storage lists it), so the test can be re-run when you add a model.";
        Assert.Equal(note, CalibrationWindow.RecordingsNote(WordsFor(mac)));
        var hint = $"Recommended: lowest errors within {Dictum.Calibration.CalibrationRanking.CeilingSecondsPer10s} s per 10 s of speech on {(mac ? "this Mac" : "this PC")}; within one point, the faster one. " +
                   "A recording shorter than 10 s counts as 10 s, because whisper's time per recording is mostly fixed.";

        using var scratch = new Scratch();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, _) = Shell(scratch, mac);
        var window = new CalibrationWindow(shell);
        window.Show();
        Assert.Contains($"{CalibrationWindow.Framing} {note}", Visible(window));
        Assert.Contains(hint, Visible(window));
        Assert.DoesNotContain(Visible(window), t => t.Contains(mac ? "this PC" : "this Mac", StringComparison.Ordinal));
        window.Close();
        shell.Dispose();
    });

    private const string WindowsRemoveAnswer =
        "It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys, its Start menu shortcut and its start-with-Windows entry, then closes. " +
        "If you got Dictum from the Microsoft Store, Windows Settings then opens so you can uninstall the app itself; otherwise Dictum removes the program too, unless its first warning says it cannot.";

    private const string MacRemoveAnswer =
        "It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys and its login item, then closes and moves Dictum.app to the Trash, unless its first warning says it cannot.";

    /// <summary>Row 12: Settings > Help, "How do I remove Dictum completely?". Only its last paragraph differs; every other answer is the same object.</summary>
    [Theory]
    [MemberData(nameof(BothPlatforms))]
    public Task Row12_the_help_remove_answer_is_the_platforms(bool mac) => UiSession.Run(() =>
    {
        var expected = mac ? MacRemoveAnswer : WindowsRemoveAnswer;
        Assert.Equal(WindowsRemoveAnswer, HelpText.Questions.Single(q => q.Question == HelpText.RemoveQuestion).Answer[^1]); // the Windows list, unchanged
        var questions = HelpText.QuestionsFor(WordsFor(mac));
        Assert.Equal(HelpText.Questions.Select(q => q.Question), questions.Select(q => q.Question));
        var remove = questions.Single(q => q.Question == HelpText.RemoveQuestion).Answer;
        Assert.Equal([.. HelpText.Questions.Single(q => q.Question == HelpText.RemoveQuestion).Answer.SkipLast(1), expected], remove);
        foreach (var (before, after) in HelpText.Questions.Zip(questions).Where(p => p.First.Question != HelpText.RemoveQuestion))
            Assert.Same(before, after);

        using var scratch = new Scratch();
        var (shell, _) = Shell(scratch, mac);
        var help = PageLines(shell, HelpText.PageTitle);
        Assert.Contains(expected, help);
        Assert.DoesNotContain(mac ? WindowsRemoveAnswer : MacRemoveAnswer, help);
        shell.Dispose();
    });
}
