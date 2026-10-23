using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Cli;
using Dictum.Core;
using Dictum.Help;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views.Onboarding;

namespace Dictum.Tests;

/// <summary>
/// The Terms of Use acceptance (legal block, owner 23/10/2026 09:50 AEDT: section C rows O12 and O14, section D item 2): the
/// Welcome window's first step says, beside Continue, "By clicking Continue you agree to Dictum's Terms of Use." with a link,
/// and below it "The Privacy Policy explains what Dictum keeps and sends." with a link; it shows at the start of any copy whose
/// settings.json records no acceptance of the current version by the account it runs as (a new install, an upgrade, a seeded
/// file, a model override); closing it before Continue quits; Continue records terms.accepted and terms.accepted_by. Replaces
/// PlainWordsTests' "asks for no acceptance" pin (O12). Also the installer's TERMS.txt (section D item 5).
/// </summary>
public sealed class TermsAcceptanceTests
{
    private static readonly string Account = Environment.UserName;

    /// <summary>A copy that has its first-run answers but no acceptance: an upgrade from 1.3.0, or an administrator's seed.</summary>
    private const string Upgrade = """{ "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""";

    private static string Accepted(string by, string version = TermsText.Version) =>
        $$"""{ "whisper": { "model": "base.en" }, "llm": { "provider": "none" }, "terms": { "accepted": {{JsonSerializer.Serialize(version)}}, "accepted_by": {{JsonSerializer.Serialize(by)}} } }""";

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A notice line as it reads: its texts and its link's words, in order.</summary>
    private static string Read(Panel line) =>
        string.Concat(line.Children.Select(c => c switch { TextBlock t => t.Text, Button { Content: string s } => s, _ => "?" }));

    private static Button Link(Panel line) => line.Children.OfType<Button>().Single();

    private static List<Panel> NoticeLines(OnboardingWindow window) => [.. window.TermsNotice.Children.OfType<Panel>()];

    /// <summary>The "terms" object of settings.json, or null.</summary>
    private static Dictionary<string, string?>? TermsRecord(string settingsFile)
    {
        if (!File.Exists(settingsFile)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
        return doc.RootElement.TryGetProperty("terms", out var terms)
            ? terms.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString())
            : null;
    }

    private static Rect InWindow(Control control, Window window) =>
        new(control.TranslatePoint(new Point(0, 0), window)!.Value, control.Bounds.Size);

    // ---- The rule ----

    /// <summary>
    /// Pin: the step shows until settings.json records this account's acceptance of the version in force. No file, an upgrade's
    /// file, another version, another account, no account, the name in another case, a file that cannot be read: all ask. The
    /// loader knows the two keys, so a recorded acceptance is no "unknown setting".
    /// </summary>
    [Fact]
    public void The_step_shows_until_this_account_has_accepted_the_version_in_force()
    {
        Assert.Equal("1", TermsText.Version);
        Assert.True(AppRules.AsksTerms(SettingsLoader.LoadFile(Path.Combine(Path.GetTempPath(), "dictum-no-such-dir", "settings.json")), "alex"));
        Assert.True(AppRules.AsksTerms(SettingsLoader.Parse("{}"), "alex"));
        Assert.True(AppRules.AsksTerms(SettingsLoader.Parse(Upgrade), "alex"));
        var accepted = SettingsLoader.Parse(Accepted("alex"));
        Assert.False(AppRules.AsksTerms(accepted, "alex"));
        Assert.Empty(accepted.Warnings);
        Assert.Equal(("1", "alex"), (accepted.Settings.Terms.Accepted, accepted.Settings.Terms.AcceptedBy));
        Assert.True(AppRules.AsksTerms(accepted, "someone-else")); // an acceptance by another account does not count
        Assert.True(AppRules.AsksTerms(SettingsLoader.Parse(Accepted("alex", version: "0")), "alex")); // another version
        Assert.True(AppRules.AsksTerms(SettingsLoader.Parse("""{ "terms": { "accepted": "1" } }"""), "alex")); // no account
        Assert.True(AppRules.AsksTerms(SettingsLoader.Parse(Accepted("ALEX")), "alex")); // the name exactly as recorded: a doubt asks again
        Assert.True(AppRules.AsksTerms(SettingsLoader.Parse("{ not json"), "alex")); // what it records is unknown
    }

    // ---- The window ----

    /// <summary>
    /// Pin (mutation-checked): the first step's button is Continue; beside it, in the same row, the approved sentence with its link
    /// to the Terms, and below it the approved privacy sentence with its link to the policy, word for word. Each link is named,
    /// reached by Tab and opens its page through the platform (the shell's IPlatform.Open, as Settings opens pages); opening a page
    /// accepts nothing. Each line's parts sit on one row at the window's size. The notice is not shown on the next step.
    /// </summary>
    [Fact]
    public Task The_first_step_says_the_approved_sentences_beside_Continue_and_links_both_pages() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, platform, _) = scratch.Shell();
        var flow = new OnboardingFlow(shell, SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true);
        var window = new OnboardingWindow(flow);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.IsType<WelcomeStep>(window.Current);
            Assert.Equal("Continue", window.PrimaryButton.Content);
            var lines = NoticeLines(window);
            string[] approved = ["By clicking Continue you agree to Dictum's Terms of Use.", "The Privacy Policy explains what Dictum keeps and sends."];
            Assert.Equal(approved, lines.Select(Read));
            Assert.True(window.TermsNotice.IsEffectivelyVisible);

            var terms = Link(lines[0]);
            var privacy = Link(lines[1]);
            Assert.Equal(("Terms of Use", "Privacy Policy"), (terms.Content, privacy.Content));
            foreach (var link in new[] { terms, privacy })
                Assert.True(link.IsEffectivelyVisible && link.Focusable && link.IsTabStop, $"{link.Content} is not reached by Tab");

            // Beside Continue: the notice ends left of the button and shares its row; the privacy sentence is below the other.
            var notice = InWindow(window.TermsNotice, window);
            var button = InWindow(window.PrimaryButton, window);
            Assert.True(notice.Right <= button.Left + 0.5, $"the notice ends at {notice.Right:0}, Continue starts at {button.Left:0}");
            Assert.True(notice.Top < button.Bottom && button.Top < notice.Bottom, $"the notice ({notice.Top:0}-{notice.Bottom:0}) is not in Continue's row ({button.Top:0}-{button.Bottom:0})");
            Assert.True(InWindow(lines[1], window).Top >= InWindow(lines[0], window).Bottom - 0.5, "the privacy sentence is not below the acceptance sentence");
            foreach (var line in lines) // each sentence on one row: its parts share a vertical band
            {
                var parts = line.Children.Select(c => InWindow(c, window)).ToList();
                Assert.True(parts.All(p => p.Top < parts[0].Bottom && parts[0].Top < p.Bottom), $"\"{Read(line)}\" is broken over rows at the default size");
            }

            Assert.Empty(platform.Opened);
            Click(terms);
            Assert.Equal(["https://dictum.kynni.au/terms"], platform.Opened);
            Click(privacy);
            Assert.Equal(["https://dictum.kynni.au/terms", "https://dictum.kynni.au/privacy"], platform.Opened);
            Assert.False(flow.TermsAccepted); // reading the pages is not Continue
            Assert.Null(TermsRecord(scratch.Paths.SettingsFile));

            Click(window.PrimaryButton);
            Assert.True(flow.TermsAccepted);
            Assert.IsNotType<WelcomeStep>(window.Current);
            Assert.False(window.TermsNotice.IsVisible);
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin (mutation-checked): closing the window before Continue quits Dictum and writes nothing, for a new install (the whole
    /// window, closed on its first step) and for an upgrade (the Terms step alone).
    /// </summary>
    [Fact]
    public Task Closing_the_window_before_Continue_quits_and_writes_nothing() => UiSession.Run(() =>
    {
        foreach (var seed in new string?[] { null, Upgrade })
        {
            using var scratch = new Scratch(seed);
            OnboardingTests.Bundle(scratch);
            var log = new RecordingLog();
            var (shell, platform, dialogs) = scratch.Shell(OnboardingTests.FakeModels(), log: log);
            dialogs.Onboarding = flow =>
            {
                var window = new OnboardingWindow(flow);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.Close(); // Esc does the same (OnboardingWindow's KeyDown)
            };
            try
            {
                Assert.False(shell.Start(lifetime: null), $"Dictum started after the window was closed before Continue ({seed ?? "new install"})");
                Assert.False(dialogs.LastOnboarding!.TermsAccepted);
                Assert.Null(platform.Tray); // it never got as far as the tray icon
                Assert.Contains("Welcome window closed before Continue: the Terms of Use were not accepted, so Dictum quits.", log.All);
                if (seed is null) Assert.False(File.Exists(scratch.Paths.SettingsFile), "a new install closed on its first step wrote settings.json");
                else Assert.Equal(seed, File.ReadAllText(scratch.Paths.SettingsFile));
            }
            finally
            {
                shell.Dispose();
            }
        }
    });

    /// <summary>
    /// Pin (mutation-checked): an upgrade (settings.json names whisper.model, records no acceptance) sees the window with the
    /// first step alone, without the step line or the "next few steps" line; its Continue records the acceptance, closes the
    /// window and Dictum starts on the settings it had. No model question or hotkey step is shown.
    /// </summary>
    [Fact]
    public Task An_upgrade_sees_the_Terms_step_alone_and_Continue_starts_Dictum() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Upgrade);
        OnboardingTests.Bundle(scratch);
        var (shell, _, dialogs) = scratch.Shell(OnboardingTests.FakeModels());
        OnboardingWindow? window = null;
        dialogs.Onboarding = flow =>
        {
            window = new OnboardingWindow(flow);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(flow.TermsOnly);
            var step = Assert.Single(window.Steps);
            Assert.IsType<WelcomeStep>(step);
            Assert.Equal("Continue", window.PrimaryButton.Content);
            Assert.True(window.TermsNotice.IsEffectivelyVisible);
            var visible = step.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(OnboardingText.WelcomeText, visible);
            Assert.DoesNotContain(OnboardingText.WelcomeNext, visible); // no steps follow
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith("Step 1 of", StringComparison.Ordinal));
            Click(window.PrimaryButton);
            Assert.False(window.IsVisible); // Continue ended the window
        };
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal(1, dialogs.TermsWindows);
            Assert.Equal((0, 0, 0), dialogs.FirstRunSteps); // no picker, no full Welcome window
            Assert.Equal(new Dictionary<string, string?> { ["accepted"] = "1", ["accepted_by"] = Account }, TermsRecord(scratch.Paths.SettingsFile));
            Assert.Equal("base.en", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Whisper.Model);
        }
        finally
        {
            if (window?.IsVisible == true) window.Close();
            shell.Dispose();
        }
    });

    /// <summary>Pin (mutation-checked): once this account's acceptance of the version in force is recorded, no window opens.</summary>
    [Fact]
    public Task Once_this_account_has_accepted_no_window_opens() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Accepted(Account));
        OnboardingTests.Bundle(scratch);
        var (shell, _, dialogs) = scratch.Shell(OnboardingTests.FakeModels());
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Null(dialogs.LastOnboarding);
            Assert.Equal((0, (0, 0, 0)), (dialogs.TermsWindows, dialogs.FirstRunSteps));
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin (mutation-checked): an acceptance recorded for another account name (an administrator's seeded settings.json) does not
    /// count: the step shows, and its Continue records this account.
    /// </summary>
    [Fact]
    public Task An_acceptance_by_another_account_does_not_count() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Accepted(Account + "-admin"));
        OnboardingTests.Bundle(scratch);
        var (shell, _, dialogs) = scratch.Shell(OnboardingTests.FakeModels());
        try
        {
            Assert.True(shell.Start(lifetime: null)); // ScriptedDialogs presses Continue
            Assert.Equal(1, dialogs.TermsWindows);
            Assert.True(dialogs.LastOnboarding!.TermsOnly);
            Assert.Equal(Account, TermsRecord(scratch.Paths.SettingsFile)!["accepted_by"]);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin (mutation-checked): Continue records exactly terms.accepted = the version and terms.accepted_by = the account, and keeps
    /// every other key. A new install's settings.json does not exist at Continue, so its acceptance is written with the first-run
    /// answer, in the file's documented layout (its "_doc" comments kept). A launch with a model override and no settings.json
    /// gets the Terms step alone, and its acceptance is written after the first run's own answer.
    /// </summary>
    [Fact]
    public Task Continue_records_the_version_and_the_account() => UiSession.Run(() =>
    {
        var expected = new Dictionary<string, string?> { ["accepted"] = TermsText.Version, ["accepted_by"] = Account };
        using (var upgrade = new Scratch(Upgrade))
        {
            OnboardingTests.Bundle(upgrade);
            var (shell, _, _) = upgrade.Shell(OnboardingTests.FakeModels());
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal(expected, TermsRecord(upgrade.Paths.SettingsFile));
            using var doc = JsonDocument.Parse(File.ReadAllText(upgrade.Paths.SettingsFile));
            Assert.Equal(["whisper", "llm", "terms"], doc.RootElement.EnumerateObject().Select(p => p.Name)); // nothing else changed
            shell.Dispose();
        }
        using (var fresh = new Scratch())
        {
            OnboardingTests.Bundle(fresh);
            var (shell, _, dialogs) = fresh.Shell(OnboardingTests.FakeModels());
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 1), dialogs.FirstRunSteps); // the whole Welcome window, whose first step held the acceptance
            Assert.True(dialogs.LastOnboarding!.TermsRecorded);
            Assert.Equal(expected, TermsRecord(fresh.Paths.SettingsFile));
            using var doc = JsonDocument.Parse(File.ReadAllText(fresh.Paths.SettingsFile));
            Assert.True(doc.RootElement.TryGetProperty("_doc", out _), "the first run's settings.json lost its documented layout");
            shell.Dispose();
        }
        using (var scripted = new Scratch())
        {
            var model = Path.Combine(scripted.Root.FullName, "fake-model.bin");
            File.WriteAllText(model, "");
            var (shell, _, dialogs) = scripted.Shell(OnboardingTests.FakeModels(), new ShellOptions(ModelOverride: model));
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((1, (0, 0, 0)), (dialogs.TermsWindows, dialogs.FirstRunSteps));
            Assert.Equal(expected, TermsRecord(scripted.Paths.SettingsFile));
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin (lens on PR #76, mutation-checked): a screen reader hears each notice row as its whole sentence, and the acceptance
    /// sentence with the Continue button (its help text, as Settings > About's Check for updates carries its line); the next
    /// step's button carries none.
    /// </summary>
    [Fact]
    public Task A_screen_reader_hears_the_acceptance_sentence_with_Continue() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell();
        var flow = new OnboardingFlow(shell, SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true);
        var window = new OnboardingWindow(flow);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(["By clicking Continue you agree to Dictum's Terms of Use.", "The Privacy Policy explains what Dictum keeps and sends."],
                NoticeLines(window).Select(l => AutomationProperties.GetName(l)));
            Assert.Equal("By clicking Continue you agree to Dictum's Terms of Use.", AutomationProperties.GetHelpText(window.PrimaryButton));
            Click(window.PrimaryButton);
            Assert.IsNotType<WelcomeStep>(window.Current);
            Assert.Null(AutomationProperties.GetHelpText(window.PrimaryButton));
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    // ---- A settings.json that cannot record the acceptance (lens on PR #76) ----

    /// <summary>
    /// Pin (mutation-checked): a settings.json that is read-only, or that does not parse, cannot record the acceptance. Continue
    /// still starts Dictum (the person did agree); the file is left as it was; the warning is logged once, not once per try;
    /// and the next start asks again, because nothing is recorded.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task A_settings_file_that_cannot_record_the_acceptance_starts_warns_once_and_asks_again(bool readOnly) => UiSession.Run(() =>
    {
        using var scratch = new Scratch(readOnly ? Upgrade : "{ not json");
        OnboardingTests.Bundle(scratch);
        var before = File.ReadAllBytes(scratch.Paths.SettingsFile);
        if (readOnly) File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
        var log = new RecordingLog();
        var (shell, _, dialogs) = scratch.Shell(OnboardingTests.FakeModels(), log: log);
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal(1, dialogs.TermsWindows);
            Assert.True(dialogs.LastOnboarding!.TermsAccepted);
            Assert.False(dialogs.LastOnboarding.TermsRecorded);
            Assert.Equal(before, File.ReadAllBytes(scratch.Paths.SettingsFile));
            Assert.Single(log.Lines, l => l.Message.Contains("settings.json could not record it", StringComparison.Ordinal));
            Assert.True(AppRules.AsksTerms(SettingsLoader.LoadFile(scratch.Paths.SettingsFile), AppRules.Account)); // the next start asks again
        }
        finally
        {
            shell.Dispose();
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
        }
    });

    // ---- The command line (lens on PR #76: the owner's decision) ----

    /// <summary>
    /// Pin (mutation-checked): --transcribe and --bench-cleanup, the two ways of using Dictum from the command line, refuse and exit
    /// 1 until this account has accepted the Terms of Use in the app (the PROPOSED line on stderr, a line in the log); once it has,
    /// they go on past that check. The administrator and CI modes (--selftest, the downloads, --print-models-dir) are not gated.
    /// </summary>
    [Fact]
    public async Task Transcribe_and_bench_cleanup_refuse_until_the_Terms_are_accepted()
    {
        Assert.Equal("Start Dictum once and press Continue to accept its Terms of Use.", CliCommands.TermsNotAccepted);
        using var scratch = new Scratch(Upgrade);
        var input = Path.Combine(scratch.Root.FullName, "input.wav");
        File.WriteAllText(input, "not audio"); // never read while the Terms are not accepted
        foreach (var mode in new[] { "--transcribe", "--bench-cleanup" })
        {
            var log = new RecordingLog();
            var options = CliOptions.Parse([mode, input]).Options!;
            var exit = mode == "--transcribe"
                ? await CliCommands.TranscribeAsync(options, scratch.Paths, log, new SecretRedactor())
                : await BenchCleanupCommand.RunAsync(options, scratch.Paths, log, new SecretRedactor());
            Assert.Equal(1, exit);
            Assert.Contains(log.Lines, l => l.Message == $"{mode} refused: settings.json records no acceptance of the Terms of Use version 1 by this account.");
        }
        File.WriteAllText(scratch.Paths.SettingsFile, Accepted(Account));
        foreach (var mode in new[] { "--transcribe", "--bench-cleanup" })
        {
            var log = new RecordingLog();
            var options = CliOptions.Parse([mode, input]).Options!;
            _ = mode == "--transcribe"
                ? await CliCommands.TranscribeAsync(options, scratch.Paths, log, new SecretRedactor())
                : await BenchCleanupCommand.RunAsync(options, scratch.Paths, log, new SecretRedactor());
            Assert.DoesNotContain(log.Lines, l => l.Message.Contains("refused: settings.json records no acceptance", StringComparison.Ordinal));
        }
    }

    // ---- Settings (section D items 3 and 4; PROPOSED 23/10/2026) ----

    /// <summary>
    /// Pin (mutation-checked): Settings > About's Support card has "Terms of use" right after "Privacy policy", opening the Terms
    /// page, and "Third-party notices", opening the shipped THIRD-PARTY-NOTICES.txt; both through the platform.
    /// </summary>
    [Fact]
    public Task Settings_About_opens_the_Terms_and_the_third_party_notices() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var notices = Path.Combine(scratch.Paths.ExeDir, "THIRD-PARTY-NOTICES.txt");
        File.WriteAllText(notices, "notices");
        var (shell, platform, _) = scratch.Shell();
        var window = new Dictum.UI.Views.SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Dispatcher.UIThread.RunJobs();
        try
        {
            var buttons = window.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            var labels = buttons.Select(b => b.Content as string).ToList();
            var privacy = labels.IndexOf("Privacy policy");
            Assert.True(privacy >= 0, "Settings > About has no Privacy policy button");
            Assert.Equal(["Privacy policy", "Terms of use", "Third-party notices"], labels.Skip(privacy).Take(3));
            Assert.Empty(platform.Opened);
            Click(buttons[privacy + 1]);
            Assert.Equal(["https://dictum.kynni.au/terms"], platform.Opened);
            Click(buttons[privacy + 2]);
            Assert.Equal(["https://dictum.kynni.au/terms", notices], platform.Opened);
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>Pin: the notices file is the one beside the program (Windows), else the one in Dictum.app's Contents/Resources (Mac).</summary>
    [Fact]
    public void The_notices_file_is_found_beside_the_program_or_in_the_app_bundle()
    {
        using var scratch = new Scratch();
        var macOs = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "Dictum.app", "Contents", "MacOS")).FullName;
        var resources = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "Dictum.app", "Contents", "Resources")).FullName;
        var paths = new AppPaths(scratch.Paths.ConfigDir, scratch.Paths.LocalDir, macOs);
        Assert.Equal(Path.Combine(macOs, "THIRD-PARTY-NOTICES.txt"), paths.ThirdPartyNoticesFile); // neither there yet: the Windows place
        File.WriteAllText(Path.Combine(resources, "THIRD-PARTY-NOTICES.txt"), "notices");
        Assert.Equal(Path.Combine(resources, "THIRD-PARTY-NOTICES.txt"), paths.ThirdPartyNoticesFile);
        File.WriteAllText(Path.Combine(macOs, "THIRD-PARTY-NOTICES.txt"), "notices");
        Assert.Equal(Path.Combine(macOs, "THIRD-PARTY-NOTICES.txt"), paths.ThirdPartyNoticesFile);
    }

    /// <summary>Pin (mutation-checked): Settings > Privacy describes Offline only with the proposed words, which name Check for updates, cloud speech and the server on this PC.</summary>
    [Fact]
    public Task Settings_Privacy_says_what_Offline_only_refuses() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell();
        var window = new Dictum.UI.Views.SettingsWindow(shell);
        window.Show();
        window.ShowPage("Privacy");
        Dispatcher.UIThread.RunJobs();
        try
        {
            var texts = window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("No network at all: downloads, Check for updates, cloud clean-up and cloud speech are refused; a server on this PC still works.", texts);
            Assert.DoesNotContain("No network at all: downloads and cloud clean-up are refused.", texts);
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    // ---- The installer ----

    private static string Repo(params string[] parts) => File.ReadAllText(Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]));

    /// <summary>
    /// Pin (section D item 5): Setup shows the Terms of Use as plain text (LicenseFile, so it asks for "I accept the agreement"),
    /// and that text carries section 9's cap sentence and section 4's guarantees paragraph byte for byte, section 16 as it reads
    /// until the owner supplies the ACN and postal address, and no Markdown or bracketed note.
    /// </summary>
    [Fact]
    public void Setup_shows_the_Terms_with_the_cap_and_the_guarantees_word_for_word()
    {
        Assert.Contains("\nLicenseFile=TERMS.txt\n", Repo("packaging", "Dictum.iss").Replace("\r\n", "\n"));
        var terms = Repo("packaging", "TERMS.txt");
        Assert.Contains("For any other loss, the most we will pay you, for all claims about Dictum or this website added together, is A$100 (one hundred Australian dollars).", terms);
        Assert.Contains("The law's guarantees still apply. Depending on where you live, the law may give you guarantees about Dictum that cannot be excluded or limited by agreement, for example under the Australian Consumer Law. Nothing in these terms excludes, limits or changes any such guarantee or right. Section 9 does not reduce any such guarantee, or what the law entitles you to if we fail to meet one.", terms);
        Assert.Contains("\n16. Contact\n\nKynni Pty Ltd, Australia. Email hello@kynni.com.au. Dictum's source code and release files are at github.com/kynniau/dictum.\n", terms.Replace("\r\n", "\n"));
        Assert.StartsWith("Dictum Terms of Use\n\nDictum by Kynni. Terms of Use. Version 1.\n", terms.Replace("\r\n", "\n"));
        foreach (var markup in new[] { "**", "##", "[", "]", "Draft 3", "PROPOSED" })
            Assert.DoesNotContain(markup, terms, StringComparison.Ordinal);
    }
}

/// <summary>The Terms of Use accepted by the account the tests run as: for tests that reach a gated command-line mode.</summary>
internal static class TestTerms
{
    /// <summary>The settings.json "terms" object, for an anonymous object that JsonSerializer writes.</summary>
    public static object Record => new { accepted = TermsText.Version, accepted_by = AppRules.Account };

    /// <summary>A settings.json that records the acceptance and nothing else.</summary>
    public static string Json => JsonSerializer.Serialize(new { terms = Record });
}
