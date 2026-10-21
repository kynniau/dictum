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

        Assert.Equal(12, HelpText.Questions.Count);
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
        var mail = File.ReadAllText(Path.Combine(root, "src", "Dictum.Core", "Help", "SupportMail.cs")); // the builder every button's email comes from
        foreach (var source in new[] { copy, helpPage, mail })
            Assert.DoesNotMatch(new Regex(@"Http|WebRequest|WebClient|Socket|Dns|Ping|NetworkInformation|Process\.Start|ShellExecute|Download|CheckForUpdates|Shell\.Models"), source); // Shell.ReportProblem and SendFeedback are allowed: they only build a mail link for the platform seam
        return true;
    });

    [Fact]
    public void The_copy_makes_no_forbidden_claim_and_uses_Australian_spelling()
    {
        var forbidden = new Regex(@"always accurate|100\s?%|\bperfect|guarantee|telemetry|analytics|\btrack(s|ed|ing)?\b|usage data|crash report|\bcolor|\bfavor|organiz|recogniz|analyz|\bcenter\b|behavior",
            RegexOptions.IgnoreCase);
        // One exemption, for exactly one sentence: the Store-listing feature line (HelpText.FreeLine) says "no analytics, no
        // telemetry" on purpose. It is removed whole before the scan, so any other use of those words still fails.
        foreach (var text in HelpText.AllText().Select(t => t.Replace(HelpText.FreeLine, "", StringComparison.Ordinal)))
            Assert.False(forbidden.IsMatch(text), $"Forbidden in the help: \"{forbidden.Match(text).Value}\" in \"{text}\"");
        Assert.Equal("https://dictum.kynni.au/help", HelpText.GuideUrl);
        Assert.Equal("hello@kynni.com.au", SupportMail.Address);
    }

    /// <summary>
    /// The reconciliation with main (21/10/2026), all PROPOSED copy: the crash answer carries the 1.1.2 note's sentence, the
    /// free answer is the two approved paragraphs plus the licence and no-limit facts, the calibration feature goes by the name its button shows,
    /// the cloud answer names the dictionary and the on/off switch is described rather than named.
    /// </summary>
    [Fact]
    public void The_crash_free_and_calibration_answers_carry_the_reconciled_copy()
    {
        var crash = HelpText.Questions.Single(q => q.Question.StartsWith("Dictum closed straight away", StringComparison.Ordinal));
        Assert.Equal(
            "Dictum 1.1.2 fixes a start-up crash on PCs without a working Vulkan graphics driver (the app closed before its tray icon appeared; it now runs the speech model on the CPU there).",
            HelpText.CrashFixLine);
        Assert.StartsWith(HelpText.CrashFixLine, crash.Answer[0]);
        Assert.Equal(1, HelpText.AllText().Count(t => t.Contains("Vulkan", StringComparison.Ordinal))); // the crash is described in one place only
        Assert.DoesNotContain(HelpText.AllText(), t => t.Contains("1.1.1", StringComparison.Ordinal)); // no older wording left behind

        var free = HelpText.Questions.Single(q => q.Question == "Is Dictum free?");
        Assert.Equal(["Free: no subscription, no account, no ads, no analytics, no telemetry.",
            "The base product stays free to keep on your PC; optional paid extras may come later.",
            "There is no charge and no word limit. Dictum is open source, which means anyone can read the program's code. It is made by Kynni Pty Ltd and shared under the MIT licence."], free.Answer);

        // Calibration is "Calibrate my voice" in 1.1.x; the help uses that name and no other (a 1.2 rename must change both together).
        var calibration = new Regex(@"calibrat\w*", RegexOptions.IgnoreCase);
        foreach (var text in HelpText.AllText())
            Assert.Equal(calibration.Matches(text).Count, Regex.Matches(text, "Calibrate my voice").Count); // every "calibrat…" word sits inside "Calibrate my voice"
        Assert.Contains(HelpText.AllText(), t => t.Contains("Calibrate my voice", StringComparison.Ordinal));

        // Cloud clean-up sends the dictionary line too (CleanupPrompt.Build), so the answer says so (PROPOSED, 21/10/2026).
        var voice = HelpText.Questions.Single(q => q.Question == "Where does my voice go?");
        Assert.Contains(voice.Answer, p => p.Contains("cloud clean-up", StringComparison.Ordinal) && p.Contains("the words in your dictionary", StringComparison.Ordinal));

        // "Dictation on" is only the switch's screen-reader name (it shows no text), so the help describes where the switch is (PROPOSED, 21/10/2026).
        Assert.DoesNotContain(HelpText.AllText(), x => x.Contains("Dictation on", StringComparison.Ordinal));
        Assert.Contains(HelpText.AllText(), x => x.Contains("the on/off switch at the top right of the Dictum menu", StringComparison.Ordinal));
    }

    /// <summary>
    /// The help names controls and messages ("Calibrate my voice", "Try on the CPU", "Mic error"…). Each must be in the
    /// copy and in the app as built, so a rename in the app (a 1.2 lane renaming a button, say) fails here until the help
    /// follows it.
    /// </summary>
    [Fact]
    public Task Every_label_the_help_names_exists_in_the_app() => UiSession.Run(() =>
    {
        // The Simple view (ui.view's default) with the rule-based tidy and English, as the first run leaves a PC with base.en:
        // the state the help describes, in which "Add AI clean-up" is offered (final round on the Simple view, 21/10/2026).
        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.True(shell.Settings.Ui.IsSimple);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Collect(ILogical root)
        {
            foreach (var b in All<Button>(root)) if (b.Content is string s) seen.Add(s);
            foreach (var t in All<TextBlock>(root)) if (t.Text is { } s) seen.Add(s);
            foreach (var c in All<Control>(root)) if (AutomationProperties.GetName(c) is { Length: > 0 } s) seen.Add(s);
            foreach (var p in All<ComboBox>(root)) if (p.ItemsSource is IEnumerable<string> items) seen.UnionWith(items); // the tray pickers' entries
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
        seen.Add(Dictum.Core.NoSoundText.Pill); // the pill when the microphone gives no sound (PR #22); the help quotes it from the same constant

        var copy = string.Join("\n", HelpText.AllText());
        string[] named =
        [
            "Calibrate my voice", "Dictionary", "Fix last dictation", "Save corrections", "Settings",
            "General", "Models", "Storage", "Microphone", "Clean-up",
            "Press your hotkey", "Change…", "Use this hotkey", "Test microphone", "Model folder", "Browse…", "Remove Dictum completely…",
            "Try on the CPU", "Mic error", "No sound from mic", "Open the full guide", "Email support", "Help",
            // The Simple view's names (final round, 21/10/2026).
            "Standard", "More accurate", "Most accurate", "Advanced", "Tidy my words", "Off", "Translate to English",
        ];
        foreach (var label in named)
        {
            Assert.True(copy.Contains(label, StringComparison.Ordinal), $"the help does not name \"{label}\"");
            Assert.True(seen.Contains(label), $"the help names \"{label}\", which the app does not show");
        }
        // "Add AI clean-up" shows with its download size or mark after it ("Add AI clean-up (1.1 GB download)"), so it is
        // checked as the start of a label the app shows, on the page and in the tray.
        Assert.True(copy.Contains(ModelsPage.AddCleanupLabel, StringComparison.Ordinal), "the help does not name Add AI clean-up");
        Assert.Contains(seen, s => s.StartsWith(ModelsPage.AddCleanupLabel + " (", StringComparison.Ordinal));

        // Final lens (M2): in the Advanced view the tier names are gone, and the help's way back, "Simple", is on the page.
        shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(Dictum.Settings.UiSettings.Advanced)));
        Dispatcher.UIThread.RunJobs();
        var advanced = new SettingsWindow(shell);
        advanced.Show();
        advanced.ShowPage("Models");
        Dispatcher.UIThread.RunJobs();
        var shownInAdvanced = All<Button>(advanced).Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.Contains(Dictum.Settings.SimpleView.SimpleLabel, shownInAdvanced);
        Assert.True(copy.Contains("choose Simple at the top of that page", StringComparison.Ordinal), "the help does not say how to get the Simple view");
        advanced.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Final round on the Simple view (21/10/2026), PROPOSED copy: the help names the speech and clean-up choices as the
    /// Simple view (the default) shows them, says where Advanced is, and offers Add AI clean-up only where the app does.
    /// </summary>
    [Fact]
    public void The_help_names_the_simple_view_choices_as_the_app_does()
    {
        var all = HelpText.AllText().ToList();
        Assert.DoesNotContain(all, t => t.Contains("choose a bigger speech model", StringComparison.Ordinal)); // the Simple view has no such list
        var accuracy = string.Join("\n", HelpText.Accuracy);
        string[] tiers = [Dictum.Settings.SimpleView.StandardLabel, Dictum.Settings.SimpleView.MoreAccurateLabel, Dictum.Settings.SimpleView.MostAccurateLabel];
        Assert.All(tiers, tier => Assert.Contains(tier, accuracy));
        Assert.Equal(tiers, Dictum.Settings.SimpleView.Tiers(gpuMachine: true).Select(t => t.Label)); // the names the app offers
        Assert.DoesNotContain(Dictum.Settings.SimpleView.MostAccurateLabel, Dictum.Settings.SimpleView.Tiers(false, "es").Select(t => t.Label)); // hence "where it is listed"
        Assert.Contains("Most accurate where it is listed", accuracy);
        Assert.Contains("choose " + Dictum.Settings.SimpleView.AdvancedLabel, accuracy, StringComparison.Ordinal);

        var tidy = Assert.Single(HelpText.HowItWorks, p => p.StartsWith(Dictum.Settings.SimpleView.TidyLabel, StringComparison.Ordinal));
        Assert.Contains("choose " + Dictum.Settings.SimpleView.OffLabel + " there", tidy);
        Assert.Contains("only the spacing is tidied", tidy);

        var files = HelpText.Questions.Single(q => q.Question.StartsWith("Where are the model files", StringComparison.Ordinal));
        Assert.Contains(files.Answer, p => p.Contains("Add AI clean-up, shown under Tidy my words while the rule-based tidy is in use and the text is English", StringComparison.Ordinal));
        // Final lens (M2): the help says it names the Simple view's choices, and how to get that view back.
        Assert.Contains(HelpText.HowItWorks, p => p.Contains("if you see model names instead, choose " + Dictum.Settings.SimpleView.SimpleLabel + " at the top of that page", StringComparison.Ordinal));
        // Off still runs the user's own corrections, snippets and the profanity filter, so the help does not say "exactly".
        Assert.Contains("only your own corrections, snippets and the profanity filter (if on) still apply", tidy);
        Assert.DoesNotContain(all, t => t.Contains("exactly what was heard", StringComparison.Ordinal));
        var english = Dictum.Settings.SettingsLoader.Parse("""{ "llm": { "provider": "none" } }""").Settings;
        var other = Dictum.Settings.SettingsLoader.Parse("""{ "llm": { "provider": "none" }, "whisper": { "language": "es" } }""").Settings;
        Assert.True(Dictum.Settings.SimpleView.OffersAddCleanup(english));
        Assert.False(Dictum.Settings.SimpleView.OffersAddCleanup(other)); // as the answer says
    }

    /// <summary>
    /// Final round (21/10/2026), PROPOSED copy: every paragraph that tells the user how to turn on cloud clean-up also says
    /// that the dictation's text and the dictionary go to the service (CleanupPrompt.Build sends both); the voice never does.
    /// </summary>
    [Fact]
    public void Every_mention_of_turning_on_cloud_clean_up_says_what_it_sends()
    {
        var mentions = HelpText.AllText().Where(t => t.Contains("turn on cloud clean-up", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mentions.Count); // "How Dictum works" and "Where does my voice go?"
        Assert.All(mentions, t =>
        {
            Assert.Contains("the text of each dictation", t);
            Assert.Contains("the words in your dictionary", t);
            Assert.Contains("Your voice still stays here", t, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The name line, APPROVED verbatim by the owner (21/10/2026 22:21 AEDT), is one question of the help.</summary>
    [Fact]
    public void The_name_question_carries_the_approved_line_verbatim()
    {
        Assert.Equal("The name: a dictum is a saying, something stated. From the Latin dictum, “something said”.", HelpText.NameLine);
        var name = HelpText.Questions.Single(q => q.Question == "What does the name Dictum mean?");
        Assert.Equal([HelpText.NameLine], name.Answer);
        Assert.Equal(name, HelpText.Questions[^1]);
        Assert.Contains(HelpText.NameLine, HelpText.AllText());
    }

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
    public Task Tab_walks_the_three_buttons_in_order_guide_then_email_support_then_feedback() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell) { Width = 860, Height = 640 };
        window.Show();
        var page = OpenHelp(window);
        var guide = All<Button>(page).Single(b => b.Content as string == HelpText.GuideButton);
        guide.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        var order = new List<string?> { guide.Content as string };
        InputElement current = guide;
        for (var i = 0; i < 2; i++)
        {
            var next = Assert.IsType<Button>(KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next));
            next.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            order.Add(next.Content as string);
            current = next;
        }
        Assert.Equal([HelpText.GuideButton, HelpText.SupportButton, HelpText.FeedbackButton], order);
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
