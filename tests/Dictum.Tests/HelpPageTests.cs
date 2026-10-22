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
using Dictum.Llm;
using Dictum.Settings;
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

        Assert.Equal(13, HelpText.Questions.Count);
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
    /// The reconciliation with main (21/10/2026), all APPROVED 22/10/2026 copy: the crash answer carries the 1.1.2 note's sentence, the
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

        // Cloud clean-up sends the dictionary line too (CleanupPrompt.Build), so the answer says so (APPROVED 22/10/2026; proposed 21/10/2026).
        var voice = HelpText.Questions.Single(q => q.Question == "Where does my voice go?");
        Assert.Contains(voice.Answer, p => p.Contains("cloud clean-up", StringComparison.Ordinal) && p.Contains("the words in your dictionary", StringComparison.Ordinal));

        // "Dictation on" is only the switch's screen-reader name (it shows no text), so the help describes where the switch is (APPROVED 22/10/2026; proposed 21/10/2026).
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
    /// Final round on the Simple view (21/10/2026), APPROVED 22/10/2026 copy: the help names the speech and clean-up choices as the
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
    /// Final round (21/10/2026), APPROVED 22/10/2026 copy: every paragraph that tells the user how to turn on cloud clean-up also says
    /// that the dictation's text and the dictionary go to the service (CleanupPrompt.Build sends both); the voice never does.
    /// </summary>
    [Fact]
    public void Every_mention_of_turning_on_cloud_clean_up_says_what_it_sends()
    {
        // The question "How do I turn on cloud clean-up?" is a heading, not a paragraph: its answer's first paragraph says it.
        var questions = HelpText.Questions.Select(q => q.Question).ToHashSet(StringComparer.Ordinal);
        var mentions = HelpText.AllText().Where(t => !questions.Contains(t) && t.Contains("turn on cloud clean-up", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, mentions.Count); // "How Dictum works", "Where does my voice go?" and "How do I turn on cloud clean-up?"
        Assert.Contains(HelpText.CloudAnswer[0], mentions);
        Assert.All(mentions, t =>
        {
            Assert.Contains("the text of each dictation", t);
            Assert.Contains("the words in your dictionary", t);
            Assert.Contains("Your voice still stays here", t, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Pin (owner, 22/10/2026 17:22 AEDT; mutation-checked): the three answers that say the voice stays on this PC carry the approved
    /// qualifier "unless you also turn on cloud speech", in the app and on the site, and no answer says it without the qualifier.
    /// </summary>
    [Fact]
    public void The_voice_stays_here_answers_carry_the_cloud_speech_qualifier()
    {
        const string qualified = "still stays here unless you also turn on cloud speech.";
        var questions = HelpText.Questions.Select(q => q.Question).ToHashSet(StringComparer.Ordinal);
        var answers = HelpText.AllText().Where(t => !questions.Contains(t) && t.Contains("still stays here", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, answers.Count); // "How Dictum works", "Where does my voice go?" and "How do I turn on cloud clean-up?"
        Assert.All(answers, t => Assert.Contains(qualified, t, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("; your voice still stays here unless you also turn on cloud speech.", HelpText.HowItWorks.First(t => t.Contains("still stays here", StringComparison.Ordinal)));
        Assert.Contains("to be tidied; your voice still stays here unless you also turn on cloud speech. Most people", HelpText.CloudAnswer[0]);
        Assert.Contains(" Your voice still stays here unless you also turn on cloud speech.", HelpText.Questions.Single(q => q.Question == "Where does my voice go?").Answer[^1]);
        var html = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "help.html"));
        Assert.Equal(4, Regex.Matches(html, Regex.Escape(qualified), RegexOptions.IgnoreCase).Count); // the cloud answer is on the page twice
        Assert.Equal(4, Regex.Matches(html, "still stays here", RegexOptions.IgnoreCase).Count); // none without it

        // Owner, 22/10/2026 ~17:45 AEDT: "Where does my voice go?"'s first paragraph, in the app and on the site, byte for byte.
        const string first = "Nowhere. Dictum turns your voice into text on this PC, then throws the recording away. It never saves your voice to a file and never sends it anywhere unless you turn on cloud speech.";
        Assert.Equal(first, HelpText.Questions.Single(q => q.Question == "Where does my voice go?").Answer[0]);
        Assert.Contains($"<p>{first}</p>", html);
        Assert.DoesNotContain("never sends it anywhere.", html);

        // Owner, 22/10/2026 18:41 AEDT: "How Dictum works" names cloud speech beside cloud clean-up, in the app and on the site.
        const string nothing = "Nothing you say is sent anywhere unless you turn on cloud clean-up or cloud speech in Settings";
        Assert.Contains(nothing, HelpText.HowItWorks.Single(t => t.Contains("Nothing you say is sent anywhere", StringComparison.Ordinal)));
        Assert.Contains(nothing, html);
        Assert.DoesNotContain("unless you turn on cloud clean-up in Settings", html);
    }

    // Approved by the product owner on 16/10/2026 as written; pinned here word for word, independently of the app's copy
    // (the same literals as CloudWarningAndPresetsTests), so the help can only quote them, never reword them.
    private static string ApprovedWarning(string host) =>
        $"WARNING: this is an advanced option and it is less private than local clean-up. Your text leaves this PC to be processed on {host}'s servers. Dictum encrypts the connection (HTTPS) but cannot see, validate or certify how that service stores, protects or uses your text, or who else can read it. Do not dictate confidential, personal or client information with cloud clean-up on unless you have checked that service's terms yourself.";

    private const string ApprovedWaitingLine = "On, but waiting for your OPENAI_API_KEY: nothing is sent until you add it to .env.";

    private const string ApprovedTrayLine = "Cloud clean-up on · text leaves this PC";

    /// <summary>
    /// "How do I turn on cloud clean-up?" (APPROVED 22/10/2026; proposed 22/10/2026) quotes the approved copy word for word, uses only the placeholder
    /// key, and names every key variable that .env.example lists, so the help cannot drift from the file it tells people to write.
    /// </summary>
    [Fact]
    public void The_cloud_answer_quotes_the_approved_copy_and_only_the_placeholder_key()
    {
        var cloud = HelpText.Questions.Single(q => q.Question == "How do I turn on cloud clean-up?");
        Assert.Same(HelpText.CloudAnswer, cloud.Answer);
        Assert.Equal(HelpText.NameQuestion, HelpText.Questions[^1].Question); // still the last question
        var answer = string.Join("\n", cloud.Answer);

        Assert.Equal("api.anthropic.com", HelpText.CloudExampleHost);
        Assert.Contains("\"" + ApprovedWarning("api.anthropic.com") + "\"", answer);
        Assert.Contains("\"" + ApprovedWaitingLine + "\"", answer);
        Assert.Contains("\"" + ApprovedTrayLine + "\"", answer);
        Assert.Equal(ApprovedTrayLine, CloudPolicy.TrayLine); // one constant, used by the tray panel and quoted by the help

        // The four services, and how to get a key, in the words the brief allows.
        foreach (var service in new[] { "Anthropic", "OpenAI", "Google Gemini", "your own server" })
            Assert.Contains(service, answer);
        Assert.Contains("sign in to the service's website and create an API key", answer);
        Assert.Contains("The key is like a password", answer);

        // Every clean-up key is in .env.example as the start of its own line (KEY=), and the help names each one the same way. Other
        // keys may join that file (cloud speech, say): only the clean-up ones are the help's business, so the list is not pinned whole.
        var example = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), ".env.example"));
        var variables = Regex.Matches(example, @"^([A-Z_]+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        foreach (var key in new[] { SecretStore.AnthropicKey, SecretStore.OpenAiKey, SecretStore.GeminiKey, SecretStore.LlmKey })
        {
            Assert.Contains(key, variables);
            Assert.Contains(key + "=", answer);
        }
        Assert.Contains(SecretStore.AnthropicKey + "=your-key-here", answer);
        Assert.Contains("your own key in place of your-key-here", answer);
        foreach (var text in HelpText.AllText())
            Assert.DoesNotMatch(new Regex(@"sk-[A-Za-z0-9]|sk_[A-Za-z0-9]|AIza[0-9A-Za-z_-]|[A-Z_]+_API_KEY=(?!your-key-here)[^\s;.]"), text); // no real-looking key, ever

        // Where the file goes and what it is called: the folder the button opens, named .env exactly.
        Assert.Contains("Open the settings folder", answer);
        Assert.Contains("Type \\.env straight after it", answer);
        Assert.Contains("The file must be called .env exactly", answer);
        Assert.Contains("never saves the key in its settings", answer);
    }

    /// <summary>
    /// The website guide's section "Optional: turn on cloud clean-up" (site/help.html, #cloud) carries every paragraph of the in-app
    /// answer word for word, in the same order (markup, the step numbers and quotation marks aside), and plays the cloud-cleanup demo.
    /// </summary>
    [Fact]
    public void The_site_guide_carries_the_cloud_answer_word_for_word()
    {
        var html = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "help.html"));
        var start = html.IndexOf("<section id=\"cloud\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "site/help.html has no #cloud section");
        var section = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        static string Plain(string s) // inline tags go without a trace, block tags leave a space
        {
            var inline = Regex.Replace(s, @"</?(b|code|em|a)(\s[^>]*)?>", "");
            return Regex.Replace(Regex.Replace(inline, "<[^>]+>", " ").Replace("&gt;", ">").Replace("&amp;", "&").Replace("\"", ""), @"\s+", " ").Trim();
        }
        var site = Plain(section);
        var at = 0;
        foreach (var paragraph in HelpText.CloudAnswer)
        {
            var words = Plain(Regex.Replace(paragraph, @"^\d+\. ", ""));
            var found = site.IndexOf(words, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"the site's cloud section does not carry, in order: \"{words}\"");
            at = found + words.Length;
        }
        Assert.Contains("<li><a href=\"#cloud\">Optional: turn on cloud clean-up</a></li>", html); // in the page's contents
        Assert.Contains("<summary>How do I turn on cloud clean-up?</summary>", html); // and in its questions
        Assert.Contains("src=\"img/demos/cloud-cleanup-poster.png\"", section);
        Assert.Contains("src=\"img/demos/cloud-cleanup.gif\"", section);
        foreach (var file in new[] { "cloud-cleanup-poster.png", "cloud-cleanup.gif" })
            Assert.True(File.Exists(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "img", "demos", file)), $"site/img/demos/{file} is missing");
    }

    /// <summary>
    /// The cloud answer, walked through as it says on a scratch profile: every label it names is on Settings > Clean-up, the tray
    /// panel or the consent dialog; Open the settings folder opens the folder Dictum reads .env from; a .env.txt there is not read
    /// (the card shows the approved "On, but waiting for your ANTHROPIC_API_KEY" line, as the help's troubleshooting paragraph says);
    /// the line the help gives, saved as .env, is read; the consent dialog carries the warning the help
    /// quotes; after Allow the card and the tray panel say what the help says; Switch cloud clean-up off puts the tidying back on
    /// this PC; and the key never reaches settings.json or the screen. Nothing reaches the network: no dictation is made.
    /// </summary>
    [Fact]
    public Task The_cloud_answer_walks_through_the_app_as_built()
    {
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SecretStore.AnthropicKey)), "ANTHROPIC_API_KEY is set in this machine's environment, which wins over .env.");
        return UiSession.Run(() =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "none" } }""");
            var (shell, platform, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);
            var answer = string.Join("\n", HelpText.CloudAnswer);
            var window = new SettingsWindow(shell);
            window.Show();

            List<string> Shown(ILogical root)
            {
                var seen = new List<string>();
                foreach (var b in All<Button>(root)) if (b.IsEffectivelyVisible && b.Content is string s) seen.Add(s);
                foreach (var t in All<TextBlock>(root)) if (t.IsEffectivelyVisible && t.Text is { } s) seen.Add(s);
                foreach (var c in All<Control>(root)) if (c.IsEffectivelyVisible && AutomationProperties.GetName(c) is { Length: > 0 } s) seen.Add(s);
                foreach (var p in All<ComboBox>(root)) if (p.ItemsSource is IEnumerable<string> items) seen.AddRange(items);
                return seen;
            }
            List<string> CleanUpPage()
            {
                window.ShowPage("Privacy");
                window.ShowPage("Clean-up");
                Dispatcher.UIThread.RunJobs();
                return Shown(window);
            }
            List<string> Tray()
            {
                var flyout = new FlyoutWindow(shell);
                flyout.ShowNear(null);
                var lines = Shown(flyout);
                flyout.Close();
                return lines;
            }

            // Step 1: the page, the card (its overline is in capitals on screen), the Service list and its entries, the Model field
            // and the key line under it.
            var page = CleanUpPage();
            string[] named = ["Clean-up", "Service", "Anthropic (Claude)", "OpenAI", "Google Gemini", "Ollama on this PC", "Custom server (OpenAI-compatible)",
                "Server address", "Model", "Open the settings folder", "Use cloud clean-up…"];
            foreach (var label in named)
            {
                Assert.True(answer.Contains(label, StringComparison.Ordinal), $"the cloud answer does not name \"{label}\"");
                Assert.True(page.Contains(label), $"the cloud answer names \"{label}\", which Settings > Clean-up does not show");
            }
            Assert.Contains("Cloud clean-up".ToUpperInvariant(), page);
            Assert.Contains("Under Cloud clean-up", answer);
            Assert.Contains($"The key goes in .env in the settings folder: {SecretStore.AnthropicKey}=your Anthropic API key.", page);

            // Step 2: the button opens the folder Dictum reads .env from.
            Click(window, "Open the settings folder");
            Assert.Equal(scratch.Paths.ConfigDir, Assert.Single(platform.Opened));
            Assert.Equal(scratch.Paths.ConfigDir, Path.GetDirectoryName(scratch.Paths.EnvFile));

            // Step 6 before the key: the consent dialog, cancelled. Its title, its Allow button and the warning the help quotes.
            dialogs.Answers.Enqueue(1); // Cancel
            Click(window, "Use cloud clean-up…");
            var (title, text) = Assert.Single(dialogs.Asked);
            Assert.Equal("Send dictations to a cloud service?", title);
            Assert.Contains("Allow cloud clean-up", Assert.Single(dialogs.AskedChoices));
            Assert.Contains("Allow cloud clean-up", answer);
            Assert.Contains(ApprovedWarning(HelpText.CloudExampleHost), text);
            Assert.Contains("Nothing is sent until you agree.", text);
            Assert.True(CloudPolicy.NothingLeaves(shell.Settings.Llm, usesModel: true));

            // Allowed with the key saved under the wrong name (.env.txt): the card is on but waiting, with the approved waiting line the
            // help's troubleshooting paragraph names ("On, but waiting for your …"), never "On:"; and nothing is sent (PR #52).
            File.WriteAllText(Path.Combine(scratch.Paths.ConfigDir, ".env.txt"), SecretStore.AnthropicKey + "=your-key-here");
            dialogs.Answers.Enqueue(0); // Allow
            Click(window, "Use cloud clean-up…");
            var withoutKey = CleanUpPage();
            Assert.DoesNotContain(withoutKey, l => l.StartsWith("On:", StringComparison.Ordinal));
            Assert.Contains(CloudPolicy.WaitingForKeyLine(SecretStore.AnthropicKey), withoutKey);
            Assert.Contains("Switch cloud clean-up off", withoutKey);
            Assert.True(shell.NothingLeaves);
            Assert.Contains("If the card says \"On, but waiting for your …\", Dictum cannot find your key.", answer);

            // Steps 4 and 5: the line the help gives, saved as .env in that folder (as Notepad saves it: UTF-8, no line end). Dictum
            // reloads on a saved .env (AppRules.ReloadsOn); the shell here is not started, so the reload is called as the watcher would.
            File.WriteAllText(scratch.Paths.EnvFile, SecretStore.AnthropicKey + "=your-key-here");
            shell.Reload(userRequested: false);
            Assert.Null(shell.CleanupWaitingForKey);
            Assert.False(shell.NothingLeaves);
            var on = CleanUpPage();
            Assert.Contains(on, l => l.StartsWith("On: the text of each dictation is sent to api.anthropic.com", StringComparison.Ordinal));
            Assert.Contains("\"On: the text of each dictation is sent to\"", answer);
            Assert.Contains("Switch cloud clean-up off", on);
            Assert.Contains("Switch cloud clean-up off", answer);
            var tray = Tray();
            Assert.Contains(ApprovedTrayLine, tray);

            // The key is never in settings.json, the page or the tray panel.
            Assert.DoesNotContain("your-key-here", File.ReadAllText(scratch.Paths.SettingsFile));
            Assert.DoesNotContain(on.Concat(tray), l => l.Contains("your-key-here", StringComparison.Ordinal));

            // Turning it off: the tidying runs on this PC again, and the tray panel no longer says text leaves it.
            Click(window, "Switch cloud clean-up off");
            Assert.True(shell.NothingLeaves);
            Assert.Contains("Use cloud clean-up…", CleanUpPage());
            Assert.DoesNotContain(ApprovedTrayLine, Tray());
            window.Close();
            shell.Dispose();
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
        Assert.Equal([HelpText.IndexHeading, HelpText.MoreHelpHeading, HelpText.HowItWorksHeading, HelpText.QuestionsHeading], // the question list first (1.2 polish)
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
