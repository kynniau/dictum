using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Help;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// "Send feedback or suggest a feature" (lane F, DECISIONS 21/10/2026): the email is a mailto string handed to the platform
/// seam, so the user's own mail program does the sending. Pins the exact link for a sample setup, its encoding, that no path,
/// folder, dictionary word or personal name can reach it, its length, the button, and the site's link. Dictum sends nothing.
/// </summary>
public class FeedbackTests
{
    private static readonly FeedbackSetup Sample = new("1.2.0", "Windows build 10.0.26100", "base.en", "Vulkan", "qwen2.5-1.5b on this PC");

    /// <summary>Text standing in for a personal-context file a later feature might add (a context.txt of names, clients or notes): no support email may carry it.</summary>
    private const string ContextSentinel = "SENTINEL-CONTEXT-Quillfeather-7f3a";

    private static void WriteContextFile(Scratch scratch) =>
        File.WriteAllText(Path.Combine(scratch.Paths.ConfigDir, "context.txt"), ContextSentinel + " Acme Holdings, client Jane Citizen");

    private const string SampleUri = "mailto:hello@kynni.com.au?subject=Dictum%20feedback&body=" +
        "What%20I%27d%20like%20to%20say%3A%0D%0A%0D%0A%0D%0A%28You%20can%20delete%20the%20lines%20below.%20They%20help%20us%20understand%20your%20setup.%29%0D%0A" +
        "Dictum%201.2.0%0D%0AWindows%20build%2010.0.26100%0D%0ASpeech%20model%3A%20base.en%20on%20Vulkan%0D%0AClean-up%3A%20qwen2.5-1.5b%20on%20this%20PC";

    /// <summary>The message lines: "What I'd like to say:", two blank lines, the note, then the four facts' lines (Dictum, system, speech model, clean-up).</summary>
    private const int BodyLines = 8;

    private static string BodyOf(string uri) => Uri.UnescapeDataString(uri[(uri.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);

    /// <summary>The version fact, after "Dictum " on the fifth line of the body.</summary>
    private static string VersionFact(string uri) => BodyOf(uri).Split("\r\n")[4]["Dictum ".Length..];

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static string Site(string page) => File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page));

    [Fact]
    public void The_feedback_link_for_a_sample_setup_is_exactly_this()
    {
        Assert.Equal(SampleUri, SupportMail.FeedbackUri(Sample));
        Assert.Equal("Dictum feedback", SupportMail.FeedbackSubject);
        Assert.Equal(
            "What I'd like to say:\r\n\r\n\r\n(You can delete the lines below. They help us understand your setup.)\r\nDictum 1.2.0\r\nWindows build 10.0.26100\r\nSpeech model: base.en on Vulkan\r\nClean-up: qwen2.5-1.5b on this PC",
            BodyOf(SampleUri));
    }

    [Fact]
    public void An_ampersand_an_accent_and_new_lines_are_encoded_so_they_survive_the_mail_program()
    {
        var uri = SupportMail.FeedbackUri(Sample with { SpeechModel = "R&D café" });
        Assert.Contains("R%26D%20caf%C3%A9", uri);
        Assert.Equal("R&D café", Regex.Match(BodyOf(uri), @"Speech model: (.*) on Vulkan").Groups[1].Value);
        Assert.Equal(1, uri.Count(c => c == '&')); // only the one that separates the subject and the body
        Assert.DoesNotContain('\n', uri);
        Assert.DoesNotContain('\r', uri);
        Assert.DoesNotContain(' ', uri);
        Assert.Equal(BodyLines - 1, Regex.Matches(uri, "%0D%0A").Count); // every line break is a CRLF
        Assert.DoesNotContain("%0A", Regex.Replace(uri, "%0D%0A", "")); // and no bare LF
    }

    [Fact]
    public void A_path_a_folder_or_a_line_break_in_a_name_never_reaches_the_email()
    {
        var setup = new FeedbackSetup("1.2.0\r\nInjected: yes", "Windows 10.0.26100",
            @"C:\Users\Kyle Manson\AppData\Local\Dictum\models\ggml-base.en.bin", "Vulkan", "D:/secret share/cleanup-models/qwen2.5-1.5b.gguf");
        var body = BodyOf(SupportMail.FeedbackUri(setup));
        foreach (var leak in new[] { "Users", "Kyle", "Manson", "AppData", "Local", "secret", "share", "cleanup-models", @"models\", "C:", "D:" })
            Assert.DoesNotContain(leak, body);
        Assert.Contains("Speech model: ggml-base.en.bin on Vulkan", body); // the file's own name stays, as About shows it
        Assert.Contains("Clean-up: qwen2.5-1.5b.gguf", body);
        Assert.Equal(BodyLines, body.Split("\r\n").Length); // a line break inside a name adds no line
    }

    [Fact]
    public void The_whole_link_stays_under_the_common_mailto_limit()
    {
        Assert.Equal(2000, SupportMail.MaxUriLength);
        Assert.True(SupportMail.FeedbackUri(Sample).Length < 600); // the usual email is far under the limit

        var accents = new string('é', 500);
        var clipped = SupportMail.FeedbackUri(new FeedbackSetup(accents, "Windows", "base.en", "CPU", "rules"));
        Assert.True(clipped.Length <= SupportMail.MaxUriLength, $"{clipped.Length} characters");
        Assert.Equal(60, VersionFact(clipped).Length); // one fact is clipped at 60 characters, the "…" included
        Assert.EndsWith("…", VersionFact(clipped));

        // Five 60-character facts of 3-byte characters encode to 9 characters each: past the limit, so the setup lines go and the message space stays.
        var wide = new string('€', 60);
        var dropped = SupportMail.FeedbackUri(new FeedbackSetup(wide, wide, wide, wide, wide));
        Assert.True(dropped.Length <= SupportMail.MaxUriLength);
        Assert.Equal(SupportMail.FeedbackIntro, BodyOf(dropped));
    }

    [Fact]
    public Task The_button_is_on_the_Help_page_and_opens_one_email_through_the_platform_seam() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage(HelpText.PageTitle);
        Dispatcher.UIThread.RunJobs();
        var page = All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl);
        var buttons = All<Button>(page).Select(b => b.Content as string).ToList();
        Assert.Equal("Send feedback or suggest a feature", HelpText.FeedbackButton);
        Assert.Contains("Email support", buttons);
        Assert.Contains("Send feedback or suggest a feature", buttons);
        Assert.Contains(HelpText.FeedbackNote, All<TextBlock>(page).Select(t => t.Text));

        Assert.Empty(platform.Opened); // nothing opens until the button is pressed
        All<Button>(page).Single(b => b.Content as string == "Send feedback or suggest a feature").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var uri = Assert.Single(platform.Opened); // exactly one call
        Assert.StartsWith("mailto:hello@kynni.com.au?subject=Dictum%20feedback&body=", uri);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_email_from_a_real_shell_holds_the_five_facts_and_nothing_private() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Personal.AddDictionaryTerms(["Zorblaxquintet"]); // a dictionary word and a dictated sentence: neither may go
        shell.Personal.Remember("the secret merger figures", 42, "Outlook");
        WriteContextFile(scratch);
        shell.SendFeedback();
        var uri = Assert.Single(platform.Opened);
        var body = BodyOf(uri);
        Assert.Matches(@"^What I'd like to say:\r\n\r\n\r\n\(You can delete the lines below\. They help us understand your setup\.\)\r\nDictum \d+\.\d+\.\d+\r\nTest build \d+\.\d+\.\d+\r\nSpeech model: [^\r\n]+ on [^\r\n]+\r\nClean-up: [^\r\n]+$", body);
        Assert.Equal(BodyLines, body.Split("\r\n").Length);
        foreach (var private1 in new[] { ContextSentinel, "Acme", "Citizen", "Zorblaxquintet", "secret", "merger", "Outlook", scratch.Root.Name, scratch.Paths.ConfigDir, scratch.Paths.DefaultModelsDir, scratch.Paths.DictionaryFile, Environment.UserName, Environment.MachineName })
            Assert.DoesNotContain(private1, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", body.Replace("\r\n", ""));
        Assert.True(uri.Length <= SupportMail.MaxUriLength);
        shell.Dispose();
    });

    [Fact]
    public Task Email_support_opens_the_report_email_with_the_version_and_models_and_nothing_private() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Personal.AddDictionaryTerms(["Zorblaxquintet"]);
        WriteContextFile(scratch);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage(HelpText.PageTitle);
        Dispatcher.UIThread.RunJobs();
        var page = All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl);
        All<Button>(page).Single(b => b.Content as string == "Email support").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var uri = Assert.Single(platform.Opened);
        var body = BodyOf(uri);
        Assert.StartsWith("mailto:hello@kynni.com.au?subject=Dictum%20", uri);
        Assert.Contains("problem%20report", uri);
        Assert.Matches(@"Dictum \d+\.\d+\.\d+ on Test\nModels: [^\n]+", body); // the version and the models line, as Report a problem fills them
        var hotkey = shell.Status.Models.Split(" · ")[^1]; // the key as the header shows it: the email must not carry it
        foreach (var private1 in new[] { "F24", hotkey, ContextSentinel, "Acme", "Citizen", "Zorblaxquintet", scratch.Root.Name, scratch.Paths.ConfigDir, scratch.Paths.DefaultModelsDir, scratch.Paths.DictionaryFile })
            Assert.DoesNotContain(private1, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dictum sends nothing by itself", HelpText.SupportNote);
        Assert.Contains("version of Dictum and the models", HelpText.SupportNote);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public void A_models_line_is_cleaned_part_by_part_so_a_path_cannot_reach_the_report()
    {
        Assert.Equal("base.en · Vulkan · rules · Right Ctrl", SupportMail.Facts("base.en · Vulkan · rules · Right Ctrl"));
        Assert.Equal("ggml-base.en.bin · qwen.gguf · ok", SupportMail.Facts(@"C:\Users\Kyle\models\ggml-base.en.bin · D:/priv/qwen.gguf · ok"));
        Assert.Equal("a  b", SupportMail.Facts("a\r\nb"));
        Assert.Equal("", SupportMail.Facts(null));
    }

    [Fact]
    public Task About_calls_its_card_Support_so_that_Help_means_only_the_Help_page() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Dispatcher.UIThread.RunJobs();
        var about = All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl);
        var texts = All<TextBlock>(about).Select(t => t.Text).ToList();
        Assert.Contains("SUPPORT", texts); // card titles are upper-cased overlines
        Assert.DoesNotContain("HELP", texts);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public void Report_a_problem_still_builds_the_link_it_built_before_the_builder_was_shared()
    {
        var body = "What happened (and what you expected):\n\n\nDictum 1.2.0 on Windows\nModels: base.en · Vulkan · rules\n\n" +
                   "To report text Dictum produced that should not have been there, describe it here; nothing is attached automatically.";
        Assert.Equal($"mailto:hello@kynni.com.au?subject={Uri.EscapeDataString("Dictum 1.2.0: problem report")}&body={Uri.EscapeDataString(body)}",
            SupportMail.ReportUri("1.2.0", "Windows", "base.en · Vulkan · rules"));
    }

    [Fact]
    public void The_report_email_is_cleaned_and_kept_under_the_link_limit_too()
    {
        // A path in the models line is cut to its last part inside the builder, so no caller can skip that.
        var cleaned = Uri.UnescapeDataString(SupportMail.ReportUri("1.2.0", "Windows", @"C:\Users\Kyle\models\ggml-x.bin · rules"));
        Assert.Contains("Models: ggml-x.bin · rules", cleaned);
        Assert.DoesNotContain("Users", cleaned);

        // Past 2,000 characters the models line goes and the rest stays.
        var wide = string.Join(" · ", Enumerable.Repeat(new string('€', 60), 6));
        var uri = SupportMail.ReportUri("1.2.0", "Windows", wide);
        Assert.True(uri.Length <= SupportMail.MaxUriLength, $"{uri.Length} characters");
        var body = Uri.UnescapeDataString(uri);
        Assert.DoesNotContain("Models:", body);
        Assert.Contains("Dictum 1.2.0 on Windows", body);
        Assert.Contains("nothing is attached automatically", body);
    }

    [Fact]
    public void Only_SupportMail_makes_a_mailto_link_and_the_support_address_is_written_once_in_code()
    {
        var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var files = Directory.EnumerateFiles(Path.Combine(FixBatchDownloadTests.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(bin) && !f.Contains(obj)).ToList();
        // Code lines only: a line whose first characters are // or /// is a comment.
        IEnumerable<string> Code(string file) => File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

        var address = files.Select(f => (File: Path.GetFileName(f), Count: Code(f).Sum(l => Regex.Matches(l, @"hello@kynni\.com\.au").Count))).Where(x => x.Count > 0).ToList();
        Assert.Equal([("SupportMail.cs", 1)], address); // exactly one occurrence in all code, a raw multi-line literal included

        var mailto = files.Where(f => Code(f).Any(l => l.Contains("mailto:", StringComparison.OrdinalIgnoreCase))).Select(Path.GetFileName).ToList();
        Assert.Equal(["SupportMail.cs"], mailto); // no other code writes a mailto link, so nothing else can email other content
    }

    [Fact]
    public Task The_cleanup_fact_matches_the_header_when_the_local_model_is_not_downloaded() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        Assert.Equal("rules until downloaded", Regex.Match(shell.ModelsLine, "rules until downloaded").Value); // what the Advanced header says (this unstarted shell is in the default Simple view, whose header says Tidy my words: 1.2 polish)
        Assert.EndsWith($" · {Dictum.Settings.SimpleView.TidyLabel}", shell.SimpleModelsLine); // the rule-based tidy, as the Simple header names it
        Assert.Equal("rule-based tidy (clean-up model not downloaded)", shell.FeedbackFacts().Cleanup); // so the email does not claim a model is in use
        shell.Dispose();
    });

    /// <summary>
    /// Pin (1.2.1 lens): with clean-up off, Send feedback says "Clean-up: no clean-up", as the header ("No clean-up") and Report
    /// a problem do, never "off", which reads as Dictum being off.
    /// </summary>
    [Fact]
    public Task The_cleanup_fact_says_no_clean_up_when_clean_up_is_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "off" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Equal("no clean-up", shell.FeedbackFacts().Cleanup);
        Assert.EndsWith("\r\nClean-up: no clean-up", Uri.UnescapeDataString(SupportMail.FeedbackUri(shell.FeedbackFacts())), StringComparison.Ordinal);
        Assert.Contains(" · No clean-up", shell.ModelsLine, StringComparison.Ordinal); // what Report a problem carries
        shell.Dispose();
    });

    [Fact]
    public void The_mic_help_answer_quotes_the_no_sound_message_from_the_same_constants()
    {
        var answer = HelpText.Questions.Single(q => q.Question.Contains("Mic error")).Answer;
        Assert.Contains(answer, p => p.Contains(Dictum.Core.NoSoundText.Pill) && p.Contains(Dictum.Core.NoSoundText.SilentBalloon));
        Assert.Contains(answer, p => p.Contains(Dictum.Core.NoSoundText.NoMicrophoneBalloon));
        Assert.Contains(Dictum.Core.NoSoundText.Pill, HelpText.Questions.Single(q => q.Question.Contains("Mic error")).Question);
    }

    [Fact]
    public Task The_header_keeps_the_hotkey_and_the_support_models_line_does_not() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var hotkey = shell.Status.Models.Split(" · ")[^1]; // the key as the header shows it (this unstarted shell shows its default Right Ctrl, not the file's F24; the Email support pin opens Settings, where the shell shows F24, and checks both)
        Assert.False(string.IsNullOrWhiteSpace(hotkey));
        // The header in the view in use (the default Simple view here: its plain names since 1.2 polish), then the key; the support
        // email's line is the Advanced one, without the key, whatever the view.
        Assert.True(shell.Settings.Ui.IsSimple);
        Assert.Equal(shell.Status.Models, shell.SimpleModelsLine + " · " + hotkey); // the header and the tray panel still show the key
        Assert.DoesNotContain(hotkey, shell.ModelsLine);
        Assert.DoesNotContain(hotkey, shell.SimpleModelsLine);
        Assert.NotEqual(shell.ModelsLine, shell.SimpleModelsLine);
        shell.Dispose();
    });

    [Fact]
    public void The_feedback_note_says_build_so_Windows_11_users_are_not_confused()
    {
        Assert.Contains("your Windows build", HelpText.FeedbackNote);
        Assert.DoesNotContain("Windows version", HelpText.FeedbackNote);
    }

    [Fact]
    public void The_support_address_constants_agree()
    {
        Assert.Equal("hello@kynni.com.au", SupportMail.Address);
        Assert.Equal(SupportMail.Address, AppShell.SupportEmail);
    }

    [Fact]
    public void The_site_links_to_the_same_email_from_its_footer_and_has_no_form()
    {
        var index = Site("index.html");
        Assert.Contains("""<a href="mailto:hello@kynni.com.au?subject=Dictum%20feedback">Feedback and feature requests</a>""", index);
        Assert.Contains($"mailto:{SupportMail.Address}?subject={Uri.EscapeDataString(SupportMail.FeedbackSubject)}\"", index); // the site and the app agree
        Assert.DoesNotContain("<form", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Feedback and feature requests", Site("privacy.html")); // site/privacy.html is not this lane's to touch
    }
}
