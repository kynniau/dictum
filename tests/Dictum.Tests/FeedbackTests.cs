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
    private static readonly FeedbackSetup Sample = new("1.2.0", "Windows 10.0.26100", "base.en", "Vulkan", "qwen2.5-1.5b on this PC");

    private const string SampleUri = "mailto:hello@kynni.com.au?subject=Dictum%20feedback&body=" +
        "What%20I%27d%20like%20to%20say%3A%0D%0A%0D%0A%0D%0A%28You%20can%20delete%20the%20lines%20below.%20They%20help%20us%20understand%20your%20setup.%29%0D%0A" +
        "Dictum%201.2.0%0D%0AWindows%2010.0.26100%0D%0ASpeech%20model%3A%20base.en%20on%20Vulkan%0D%0AClean-up%3A%20qwen2.5-1.5b%20on%20this%20PC";

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
            "What I'd like to say:\r\n\r\n\r\n(You can delete the lines below. They help us understand your setup.)\r\nDictum 1.2.0\r\nWindows 10.0.26100\r\nSpeech model: base.en on Vulkan\r\nClean-up: qwen2.5-1.5b on this PC",
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
        shell.SendFeedback();
        var uri = Assert.Single(platform.Opened);
        var body = BodyOf(uri);
        Assert.Matches(@"^What I'd like to say:\r\n\r\n\r\n\(You can delete the lines below\. They help us understand your setup\.\)\r\nDictum \d+\.\d+\.\d+\r\nTest \d+\.\d+\.\d+\r\nSpeech model: [^\r\n]+ on [^\r\n]+\r\nClean-up: [^\r\n]+$", body);
        Assert.Equal(BodyLines, body.Split("\r\n").Length);
        foreach (var private1 in new[] { "Zorblaxquintet", "secret", "merger", "Outlook", scratch.Root.Name, scratch.Paths.ConfigDir, scratch.Paths.DefaultModelsDir, scratch.Paths.DictionaryFile, Environment.UserName, Environment.MachineName })
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
        foreach (var private1 in new[] { "Zorblaxquintet", scratch.Root.Name, scratch.Paths.ConfigDir, scratch.Paths.DefaultModelsDir, scratch.Paths.DictionaryFile })
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
        var body = "What happened (and what you expected):\n\n\nDictum 1.2.0 on Windows\nModels: base.en · Vulkan · rules · F24\n\n" +
                   "To report text Dictum produced that should not have been there, describe it here; nothing is attached automatically.";
        Assert.Equal($"mailto:hello@kynni.com.au?subject={Uri.EscapeDataString("Dictum 1.2.0: problem report")}&body={Uri.EscapeDataString(body)}",
            AppShell.ReportUri("1.2.0", "Windows", "base.en · Vulkan · rules · F24"));
    }

    [Fact]
    public void The_support_address_is_written_once_in_code()
    {
        Assert.Equal("hello@kynni.com.au", SupportMail.Address);
        Assert.Equal(SupportMail.Address, AppShell.SupportEmail);
        var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var copies = Directory.EnumerateFiles(Path.Combine(FixBatchDownloadTests.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(bin) && !f.Contains(obj))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"""[^""\r\n]*hello@kynni\.com\.au"))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["SupportMail.cs"], copies); // the one literal; everything else in code is an alias of it
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
