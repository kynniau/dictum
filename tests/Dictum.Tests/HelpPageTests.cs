using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Help;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Settings → Help (docs/DECISIONS.md, "In-app Help", 21/10/2026): the page is in the navigation, every sentence of
/// <see cref="HelpText"/> renders, the two buttons hand exactly their fixed address to the platform seam and nothing else
/// reaches the network, the copy makes no forbidden claim, the labels it names exist in the app, and it lays out and reads
/// with the keyboard at the default size and at 150 % scaling. Headless: nothing reaches the desktop.
/// </summary>
public class HelpPageTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static void Click(ILogical root, string label) =>
        All<Button>(root).Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static HelpPage OpenHelp(SettingsWindow window)
    {
        window.ShowPage(HelpText.PageTitle);
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<HelpPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
    }

    [Fact]
    public Task Help_is_in_the_settings_navigation_and_opens_its_page() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        var nav = All<ListBox>(window).Single();
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(nav.ItemsSource).ToList();
        Assert.Equal("Help", HelpText.PageTitle);
        Assert.Equal(HelpText.PageTitle, names[^1]); // the last entry, after About

        nav.SelectedItem = HelpText.PageTitle; // chosen in the list, as a click or the arrow keys do
        Dispatcher.UIThread.RunJobs();
        var shown = All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl);
        Assert.IsType<HelpPage>(shown);
        Assert.Equal(HelpText.PageTitle, shown.PageTitle);

        window.ShowPage("About");
        OpenHelp(window); // and by name, as the tray menu's Help does
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Every_question_and_every_sentence_of_the_help_renders() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        var page = OpenHelp(window);
        var shown = All<TextBlock>(page).Select(t => t.Text).Concat(All<Button>(page).Select(b => b.Content as string)).ToList();
        foreach (var text in HelpText.AllText())
            Assert.Contains(text, shown);

        Assert.Equal(11, HelpText.Questions.Count);
        var blocks = All<Border>(page).Where(b => b.Focusable).Select(AutomationProperties.GetName).ToList();
        foreach (var entry in HelpText.Questions)
            Assert.Contains(entry.Question, blocks); // each question is its own tab stop, named after it
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_full_guide_button_opens_exactly_the_guide_address_through_the_platform() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        var page = OpenHelp(window);

        Click(page, "Open the full guide");
        Assert.Equal(["https://dictum.kynni.au/help"], platform.Opened);

        Click(page, "Email support");
        Assert.Equal(2, platform.Opened.Count);
        Assert.Equal("https://dictum.kynni.au/help", platform.Opened[0]);
        Assert.StartsWith($"mailto:{AppShell.SupportEmail}?subject=Dictum%20", platform.Opened[1]); // the prefilled email About's Report a problem opens
        Assert.Contains("problem%20report", platform.Opened[1]);
        Assert.Equal(SupportMail.Address, AppShell.SupportEmail); // one support address across the app
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_page_reaches_the_network_only_through_the_platform_seam() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var downloads = new CountingHandler();
        var updates = new CountingHandler();
        var (shell, platform, _) = scratch.Shell(downloadHandler: downloads, updateHandler: updates);
        var window = new SettingsWindow(shell);
        window.Show();
        var page = OpenHelp(window);
        foreach (var button in All<Button>(page).ToList())
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(300); // long enough for any request a click started to reach a handler
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, downloads.Requests);
        Assert.Equal(0, updates.Requests);
        Assert.Equal(3, platform.Opened.Count); // the three buttons, one call each, nothing else
        Assert.Equal(HelpText.GuideUrl, platform.Opened[0]);
        Assert.StartsWith("mailto:hello@kynni.com.au?subject=Dictum%20", platform.Opened[1]); // Email support: the same prefilled email as Report a problem
        Assert.Contains("problem%20report", platform.Opened[1]);
        Assert.StartsWith("mailto:hello@kynni.com.au?subject=Dictum%20feedback&body=", platform.Opened[2]); // Send feedback: a mail link, handed to the system
        window.Close();
        shell.Dispose();

        // And in the source: the page and its copy name no network API and no shell call that downloads or checks.
        var root = FixBatchDownloadTests.RepoRoot();
        var copy = File.ReadAllText(Path.Combine(root, "src", "Dictum.Core", "Help", "HelpText.cs"));
        var pages = File.ReadAllText(Path.Combine(root, "src", "Dictum.UI", "Views", "Settings", "OtherPages.cs"));
        var helpPage = pages[pages.IndexOf("internal sealed class HelpPage", StringComparison.Ordinal)..];
        foreach (var source in new[] { copy, helpPage })
            Assert.DoesNotMatch(new Regex(@"Http|WebRequest|WebClient|Socket|Download|CheckForUpdates|Shell\.Models|Dns|Ping|NetworkInformation|Process\.Start|ShellExecute"), source); // Shell.ReportProblem and SendFeedback are allowed: they only build a mail link for the platform seam
        return true;
    });

    [Fact]
    public void The_copy_makes_no_forbidden_claim_and_uses_Australian_spelling()
    {
        var forbidden = new Regex(@"always accurate|100\s?%|\bperfect|guarantee|telemetry|analytics|\btrack(s|ed|ing)?\b|usage data|crash report|\bcolor|\bfavor|organiz|recogniz|analyz|\bcenter\b|behavior",
            RegexOptions.IgnoreCase);
        foreach (var text in HelpText.AllText())
            Assert.False(forbidden.IsMatch(text), $"Forbidden in the help: \"{forbidden.Match(text).Value}\" in \"{text}\"");
        Assert.Equal("https://dictum.kynni.au/help", HelpText.GuideUrl);
        Assert.Equal("hello@kynni.com.au", SupportMail.Address);
    }

    /// <summary>
    /// The help names controls and messages ("Calibrate my voice", "Try on the CPU", "Mic error"…). Each must be in the
    /// copy and in the app as built, so a rename in the app (a 1.2 lane renaming a button, say) fails here until the help
    /// follows it.
    /// </summary>
    [Fact]
    public Task Every_label_the_help_names_exists_in_the_app() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Collect(ILogical root)
        {
            foreach (var b in All<Button>(root)) if (b.Content is string s) seen.Add(s);
            foreach (var t in All<TextBlock>(root)) if (t.Text is { } s) seen.Add(s);
            foreach (var c in All<Control>(root)) if (AutomationProperties.GetName(c) is { Length: > 0 } s) seen.Add(s);
        }

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Collect(flyout);
        flyout.Close();
        var window = new SettingsWindow(shell);
        window.Show();
        foreach (var name in Assert.IsAssignableFrom<IEnumerable<string>>(All<ListBox>(window).Single().ItemsSource).ToList())
        {
            seen.Add(name);
            window.ShowPage(name);
            Dispatcher.UIThread.RunJobs();
            Collect(window);
        }
        window.Close();
        var fix = new FixLastWindow(shell, new LastDictation("Ask kinney about the cafe.", 42, "Outlook", DateTime.UtcNow));
        fix.Show();
        Collect(fix);
        fix.Close();
        seen.Add(ModelsPage.FolderLabel); // shown as a small-caps card title, so in capitals on screen
        seen.Add(AppShell.TryOnCpuLabel); // shown only beside a model problem
        seen.Add(PillText.For(PillView.MicError, TimeSpan.Zero)); // on the pill

        var copy = string.Join("\n", HelpText.AllText());
        string[] named =
        [
            "Calibrate my voice", "Dictionary", "Fix last dictation", "Save corrections", "Settings", "Dictation on",
            "General", "Models", "Storage", "Microphone", "Clean-up",
            "Press your hotkey", "Change…", "Use this hotkey", "Test microphone", "Model folder", "Browse…", "Remove Dictum completely…",
            "Try on the CPU", "Mic error", "Open the full guide", "Email support", "Help",
        ];
        foreach (var label in named)
        {
            Assert.True(copy.Contains(label, StringComparison.Ordinal), $"the help does not name \"{label}\"");
            Assert.True(seen.Contains(label), $"the help names \"{label}\", which the app does not show");
        }
        shell.Dispose();
    });

    /// <summary>
    /// Readable at 100 % and 150 %. Scaling shrinks the window's room in DIPs rather than changing the layout, so the page is
    /// measured at the default window (860 × 640), at a 1280 × 720 screen at 150 % (853 × 480 DIPs) and at the smallest
    /// the window allows (640 × 420): no text or button may run past the visible width, and the page scrolls only down.
    /// </summary>
    [Theory]
    [InlineData(860, 640)]
    [InlineData(853, 480)]
    [InlineData(640, 420)]
    public Task The_page_fits_the_window_width_at_100_and_150_percent(double width, double height) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell) { Width = width, Height = height };
        window.Show();
        var page = OpenHelp(window);
        var scroll = All<ScrollViewer>(window).Single(s => s.Content is Border { Child: ContentControl });
        Assert.True(scroll.Viewport.Width > 0);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"page {scroll.Extent.Width:0} wide in a {scroll.Viewport.Width:0} view");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height); // long enough to scroll, so the scroll pins below mean something
        foreach (var control in All<Control>(page).Where(c => c is TextBlock or Button && c.IsEffectivelyVisible))
        {
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), scroll)!.Value.X;
            Assert.True(right <= scroll.Viewport.Width + 0.5, $"\"{(control as TextBlock)?.Text ?? (control as Button)?.Content}\" ends at {right:0} of {scroll.Viewport.Width:0}");
            if (control is TextBlock text && text.FindLogicalAncestorOfType<Button>() is null) Assert.Equal(TextWrapping.Wrap, text.TextWrapping); // a button label is one line by design
        }
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_page_has_headings_and_reads_with_the_keyboard() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell) { Width = 860, Height = 640 };
        window.Show();
        var page = OpenHelp(window);

        var headings = All<TextBlock>(page).Where(t => AutomationProperties.GetHeadingLevel(t) > 0).ToList();
        Assert.Equal([HelpText.PageTitle], headings.Where(t => AutomationProperties.GetHeadingLevel(t) == 1).Select(t => t.Text));
        Assert.Equal([HelpText.MoreHelpHeading, HelpText.HowItWorksHeading, HelpText.QuestionsHeading],
            headings.Where(t => AutomationProperties.GetHeadingLevel(t) == 2).Select(t => t.Text));
        Assert.Equal([HelpText.AccuracyHeading, .. HelpText.Questions.Select(q => q.Question)],
            headings.Where(t => AutomationProperties.GetHeadingLevel(t) == 3).Select(t => t.Text));
        foreach (var button in All<Button>(page))
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetHelpText(button)), $"{button.Content} has no help text for a screen reader");

        var scroll = All<ScrollViewer>(window).Single(s => s.Content is Border { Child: ContentControl });
        var blocks = All<Border>(page).Where(b => b.Focusable && b.IsTabStop).ToList();
        Assert.Equal(2 + HelpText.Questions.Count, blocks.Count); // how it works, why it mishears, and each question

        // Tab reaches each block in order, from the last button, and the last block scrolls into view with its ring on.
        var lastButton = All<Button>(page).Single(b => b.Content as string == HelpText.FeedbackButton); // after Email support, in the same row
        lastButton.Focus(NavigationMethod.Tab);
        foreach (var expected in blocks)
        {
            var next = KeyboardNavigationHandler.GetNext(TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement()!, NavigationDirection.Next);
            Assert.Same(expected, next);
            ((InputElement)next!).Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
        }
        var last = blocks[^1];
        Assert.True(last.IsFocused);
        Assert.NotEqual(Brushes.Transparent, last.BorderBrush); // the focus ring
        Assert.True(scroll.Offset.Y > 0, "the focused block did not scroll into view");
        var lastTop = last.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        Assert.True(lastTop >= -0.5 && lastTop < scroll.Viewport.Height, $"the focused block's top is at {lastTop:0} of {scroll.Viewport.Height:0}");

        // Leaving a block takes its ring away; a click (pointer focus) shows none.
        blocks[0].Focus(NavigationMethod.Pointer);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Brushes.Transparent, last.BorderBrush);
        Assert.Equal(Brushes.Transparent, blocks[0].BorderBrush);

        // With focus in the text, Page Down scrolls the page.
        scroll.Offset = new Vector(0, 0);
        Dispatcher.UIThread.RunJobs();
        blocks[0].Focus(NavigationMethod.Tab);
        scroll.Offset = new Vector(0, 0);
        Dispatcher.UIThread.RunJobs();
        blocks[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageDown, Source = blocks[0] });
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroll.Offset.Y > 0, "Page Down did not scroll");
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_tray_panel_Help_button_opens_Settings_Help() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var help = All<Button>(flyout).Single(b => b.Content as string == "Help");
        var settings = All<Button>(flyout).Single(b => b.Content as string == "Settings");
        Assert.Equal(1, Grid.GetColumnSpan(settings)); // six buttons in three full rows: nothing overlaps
        Assert.NotEqual((Grid.GetRow(settings), Grid.GetColumn(settings)), (Grid.GetRow(help), Grid.GetColumn(help)));

        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        // The shell keeps the window it opened in a private field (no window list on the headless platform).
        var window = Assert.IsType<SettingsWindow>(typeof(AppShell).GetField("_settingsWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(shell));
        Assert.IsType<HelpPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
        window.Close();
        shell.Dispose();
    });
}
