using System.Net;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// Site: Store-first (owner decision, 22/10/2026; docs/DECISIONS.md). The Microsoft Store is the one install path the site
/// shows; there is no direct-download button, no SmartScreen or signing explanation, and one quiet footer line points pro
/// users at GitHub. Also checks that every same-site anchor on the three pages lands on an id that exists.
/// </summary>
public sealed class SiteStoreFirstTests
{
    private const string StoreUrl = "https://apps.microsoft.com/detail/9N0DG1DHTR57";
    private const string MacUrl = "https://github.com/kynniau/dictum/releases/tag/mac-v1.3.0"; // the Mac beta announcement, APPROVED by the owner 23/10/2026 07:10 AEDT: do not reword
    private const string GitHubLine = """Source code and release files are on <a href="https://github.com/kynniau/dictum">GitHub</a>.""";
    private static readonly string[] Pages = ["index.html", "help.html", "privacy.html", "plain-words.html"];

    /// <summary>A page of the site without its HTML comments.</summary>
    private static string VisibleHtml(string page) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page)), "<!--.*?-->", "", RegexOptions.Singleline);

    [Fact]
    public void Every_button_on_the_landing_page_is_the_Microsoft_Store_or_the_Mac_beta_and_the_first_is_the_Store_in_the_hero()
    {
        var html = VisibleHtml("index.html");
        var buttons = Regex.Matches(html, "<a class=\"btn[^\"]*\" href=\"([^\"]+)\"");
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Contains(b.Groups[1].Value, new[] { StoreUrl, MacUrl }));
        Assert.Equal(StoreUrl, buttons[0].Groups[1].Value);
        Assert.Contains(buttons, b => b.Groups[1].Value == MacUrl);
        var heroStart = html.IndexOf("<section class=\"hero\"", StringComparison.Ordinal);
        Assert.True(heroStart >= 0, "index.html has no hero section");
        var heroEnd = html.IndexOf("</section>", heroStart, StringComparison.Ordinal);
        Assert.InRange(buttons[0].Index, heroStart, heroEnd);
    }

    /// <summary>The Mac beta announcement, word for word (APPROVED by the owner 23/10/2026 07:10 AEDT: do not reword).</summary>
    [Theory]
    [InlineData("index.html", "Free dictation for Windows PCs · Now on Mac (beta)")]
    [InlineData("index.html", "Free for Windows 10 and 11 (x64); the Microsoft Store installs it and keeps it up to date. Free for Apple Silicon Macs on macOS 15 or later, as a beta you download from GitHub.")]
    [InlineData("index.html", "Dictum for Mac, in beta The same Dictum, built for Apple Silicon Macs (M1 or later) on macOS 15 or later.")]
    [InlineData("index.html", "Check the download against SHA256SUMS-mac.txt from the same page.")]
    [InlineData("index.html", "Windows 10 and 11. Apple Silicon Macs (beta).")]
    [InlineData("index.html", "Done Mac version (beta) The same app, built for Apple Silicon Macs. In beta while it earns its stripes.")]
    [InlineData("index.html", "Windows 10 or 11, 64-bit, or an Apple Silicon Mac on macOS 15 or later (beta).")]
    [InlineData("index.html", "The Microsoft Store on Windows; the Store installs everything else Dictum needs. On a Mac, the download from GitHub.")]
    [InlineData("index.html", "A microphone. Any one your computer can use.")]
    [InlineData("privacy.html", "whether you install it from the Microsoft Store or as the direct download, the Dictum for Mac beta, and this website, dictum.kynni.au. Dictum is made by Kynni Pty Ltd")]
    public void The_Mac_beta_sentences_are_on_the_page(string page, string sentence) =>
        Assert.Contains(sentence, Prose(page), StringComparison.Ordinal);

    [Theory]
    [InlineData("SmartScreen")]
    [InlineData("code-signed")]
    [InlineData("Edge held the download")]
    [InlineData("Visual C++")]
    [InlineData("Run anyway")]
    [InlineData("More info")]
    [InlineData("Keep anyway")]
    [InlineData("Windows protected your PC")]
    [InlineData("Download for Windows")]
    [InlineData("Dictum-Setup")]
    [InlineData("vc_redist")]
    [InlineData("releases/latest")]
    [InlineData("#smartscreen")]
    [InlineData("img/demos/install")]
    public void No_page_explains_the_direct_download_or_the_warning(string phrase)
    {
        foreach (var page in Pages)
            Assert.False(VisibleHtml(page).Contains(phrase, StringComparison.OrdinalIgnoreCase), $"site/{page} still says \"{phrase}\"");
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    [InlineData("privacy.html")]
    [InlineData("plain-words.html")]
    public void Every_page_has_the_same_one_quiet_GitHub_line_in_its_footer(string page)
    {
        var html = VisibleHtml(page);
        var footer = html[html.IndexOf("<footer", StringComparison.Ordinal)..];
        Assert.Contains(GitHubLine, footer);
        Assert.Single(Regex.Matches(html, Regex.Escape(GitHubLine)));
        Assert.DoesNotContain("Source on GitHub", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guides_Get_Dictum_section_leads_only_to_the_Store()
    {
        var html = VisibleHtml("help.html");
        var start = html.IndexOf("<section id=\"get\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "help.html has no Get Dictum section");
        var get = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        var links = Regex.Matches(get, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal([StoreUrl], links);
    }

    // ---- Cloud speech: the public pages say the voice stays on the PC unless cloud speech is on (APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword) ----

    /// <summary>The words a visitor reads on a page: comments, scripts and tags gone (inline tags leave no gap, so an SVG badge's lines join up), entities decoded, spaces collapsed.</summary>
    private static string Prose(string page) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(Regex.Replace(VisibleHtml(page), @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline),
            @"</?(a|b|em|strong|code|span|tspan)(\s[^>]*)?>", ""), "<[^>]+>", " ")), @"\s+", " ").Trim();

    /// <summary>
    /// The release gate: no page of the site says the voice "never leaves" the PC without an "unless" in the same sentence, and none says the
    /// voice "stays" on or here, or is "turned into text on" the PC, without "unless" or "by default". Mutations (observed failing, 22/10/2026):
    /// "Your voice never leaves this PC." put back in site/help.html, section 7; and the privacy short version put back to "Your speech is turned into
    /// text on your PC, and the text is tidied on your PC."
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    [InlineData("privacy.html")]
    [InlineData("plain-words.html")]
    public void No_page_says_the_voice_never_leaves_the_PC_without_an_unless(string page)
    {
        var sentences = Regex.Split(Prose(page), @"(?<=[.!?])\s+");
        foreach (var s in sentences.Where(s => Regex.IsMatch(s, @"never\s+leaves\s+(this|your)\s+PC", RegexOptions.IgnoreCase)))
            Assert.True(s.Contains("unless", StringComparison.OrdinalIgnoreCase), $"site/{page} says \"{s}\" with no \"unless\" in the same sentence");
        foreach (var s in sentences.Where(s => Regex.IsMatch(s, @"voice\s+(still\s+)?stays\s+(on|here)", RegexOptions.IgnoreCase)))
            Assert.True(s.Contains("unless", StringComparison.OrdinalIgnoreCase) || s.Contains("by default", StringComparison.OrdinalIgnoreCase),
                $"site/{page} says \"{s}\" with neither \"unless\" nor \"by default\"");
        foreach (var s in sentences.Where(s => Regex.IsMatch(s, @"turned\s+into\s+text\s+on\s+(this|your(\s+own)?)\s+PC", RegexOptions.IgnoreCase)))
            Assert.True(s.Contains("unless", StringComparison.OrdinalIgnoreCase) || s.Contains("by default", StringComparison.OrdinalIgnoreCase),
                $"site/{page} says \"{s}\" with neither \"unless\" nor \"by default\"");
        Assert.DoesNotMatch(new Regex(@"never\s+sends?\s+it\s+anywhere\.", RegexOptions.IgnoreCase), Prose(page));
    }

    /// <summary>Every replaced or added sentence of the cloud speech copy, on the page that carries it, word for word.</summary>
    [Theory]
    [InlineData("index.html", "Hold a key, talk, let go. Dictum types what you said into whatever you’re working in. Your voice stays on your PC unless you turn on cloud speech.")]
    [InlineData("index.html", "Cloud clean-up and cloud speech only if you turn them on. Once your models are downloaded it works with the internet off.")]
    [InlineData("index.html", "Cloud speech (off by default): the recording of each dictation goes to the service you chose, OpenAI, Azure OpenAI, Groq or a server you name, with your own key where the service needs one, behind its own consent step. Your dictionary words go with it as a hint. Dictum does not validate or certify how that service handles your voice.")]
    [InlineData("index.html", "api.openai.com, {resource}.openai.azure.com, api.groq.com Off by default")]
    [InlineData("index.html", "No. Nothing is trained, and your voice stays on your PC unless you turn on cloud speech. More in the help guide.")]
    [InlineData("index.html", "Where your speech is processed On your PC by default")]
    [InlineData("index.html", "Runs on your PC by default")] // owner, 22/10/2026 22:05 AEDT (qualifier added)
    [InlineData("index.html", "Dictum does the work on your own computer by default, for free.")] // owner, 22/10/2026 22:05 AEDT (qualifier added)
    [InlineData("plain-words.html", "Your voice is turned into text on your own PC unless you turn on cloud speech. The text is tidied on your own PC.")] // owner, 22/10/2026 22:05 AEDT
    [InlineData("help.html", "First, your voice is turned into text on this PC (unless you turn on cloud speech) by a speech model.")] // owner, 22/10/2026 22:05 AEDT
    [InlineData("help.html", "Nowhere, by default. Dictum turns your voice into text on this PC")] // owner, 22/10/2026 22:05 AEDT
    [InlineData("index.html", "Dictum’s optional cloud clean-up and cloud speech are off by default and come with a warning.")]
    [InlineData("index.html", "Only when you open the privacy policy, Dictum in plain words or the full guide from Settings, or click Check for updates")]
    [InlineData("help.html", "Dictum turns your voice into text on this PC, then throws the recording away. Your voice stays on this PC unless you turn on cloud speech, an advanced option that is off by default.")]
    [InlineData("help.html", "are sent to the service you chose. Your voice still stays on this PC unless you also turn on cloud speech.")]
    [InlineData("help.html", "No. Nothing is trained, and your voice stays on this PC unless you turn on cloud speech. Calibrate my voice")]
    [InlineData("help.html", "Dictum goes online only when you ask it to. That means four things: downloading a model you choose; checking for updates, in the direct-download version; cloud clean-up, if you turn it on; cloud speech, if you turn it on. Offline only, in Settings > Privacy, stops all four.")]
    [InlineData("privacy.html", "Your speech is turned into text on your PC, unless you turn on cloud speech, and the text is tidied on your PC.")]
    [InlineData("privacy.html", "to send text to a cloud clean-up service, or your voice to a cloud speech service, or a server you choose.")]
    [InlineData("privacy.html", "It is never written to disk, except the calibration recordings you make on purpose (below). With cloud speech on, the recording is sent to the service you chose and then discarded here.")]
    [InlineData("privacy.html", "an API key, only if you add one for cloud clean-up or cloud speech.")]
    [InlineData("privacy.html", "Cloud speech (off by default) Only if you switch on cloud speech in Settings > Models, agree to the service in its own consent step and, for OpenAI, Azure OpenAI or Groq, add your own API key (a server you name may need none).")]
    [InlineData("privacy.html", "sends that dictation’s recording, as a WAV file, to the service you chose, together with the words in your personal dictionary as a hint and, in hands-free, the closing words of the part before.")]
    [InlineData("privacy.html", "The service is OpenAI (api.openai.com), Azure OpenAI ({resource}.openai.azure.com, your own resource), Groq (api.groq.com) or a custom https:// address you enter.")]
    [InlineData("privacy.html", "This is an advanced option: Dictum cannot validate or certify how that service stores, protects or uses your voice. Check its terms before dictating anything sensitive.")]
    [InlineData("privacy.html", "downloads, cloud clean-up, cloud speech and servers that aren’t on your PC are all refused. A clean-up or speech server running on your own PC still works.")]
    [InlineData("privacy.html", "Hugging Face, Anthropic, OpenAI, Azure OpenAI, Groq, Google Gemini and any server you name handle that under their own policies. Model downloads and requests to Anthropic, OpenAI, Azure OpenAI, Groq and Google Gemini use HTTPS.")]
    public void The_cloud_speech_sentences_are_on_the_page(string page, string sentence) =>
        Assert.Contains(sentence, Prose(page), StringComparison.Ordinal);

    [Fact]
    public void The_landing_page_badges_and_lines_say_by_default_and_the_og_text_names_cloud_speech()
    {
        Assert.Equal(4, Regex.Matches(Prose("index.html"), "Your voice stays on your PC by default").Count); // the wide badge, the narrow badge, the list item, the card heading
        var raw = VisibleHtml("index.html");
        Assert.Contains("nothing you say leaves your computer unless you turn on cloud clean-up or cloud speech. Now on Mac too, in beta.", raw);
        Assert.Contains("<meta property=\"og:description\" content=\"Free dictation for Windows PCs. Speech becomes text on your own computer, in almost any app. No account, no subscription, nothing leaves your PC unless you turn on cloud clean-up or cloud speech.\">", raw);
        Assert.Contains("viewBox=\"0 0 360 780\" width=\"360\" height=\"780\"", raw); // the narrow graphic made room for the two-line badge
    }

    /// <summary>The old, unqualified sentences are gone from the page that had them (each quoted verbatim in the PR body and DECISIONS).</summary>
    [Theory]
    [InlineData("index.html", "Your voice never leaves your PC")]
    [InlineData("index.html", "your voice never leaves your PC")]
    [InlineData("index.html", "<li>Your voice stays on your PC.</li>")]
    [InlineData("index.html", "<h3>Your voice stays on your PC</h3>")]
    [InlineData("index.html", "Your voice stays on your PC</text>")]
    [InlineData("index.html", "unless you turn on cloud clean-up.")]
    [InlineData("index.html", "Cloud clean-up only if you turn it on.")]
    [InlineData("index.html", "open the privacy policy or the full guide")]
    [InlineData("help.html", "Your voice never leaves this PC")]
    [InlineData("help.html", "your voice never leaves this PC")]
    [InlineData("help.html", "Your voice still stays on this PC.")]
    [InlineData("help.html", "That means three things")]
    [InlineData("help.html", "stops all three")]
    [InlineData("privacy.html", "<strong>Your speech is turned into text on your PC</strong>")]
    [InlineData("privacy.html", "a cloud clean-up service or a server you choose")]
    [InlineData("privacy.html", "only if you add one for cloud clean-up.")]
    [InlineData("privacy.html", "downloads, cloud clean-up and servers")]
    [InlineData("privacy.html", "A clean-up server running on your own PC still works.")]
    public void The_old_unqualified_sentences_are_gone(string page, string old) =>
        Assert.DoesNotContain(old, VisibleHtml(page), StringComparison.Ordinal);

    [Fact]
    public void Every_same_site_anchor_lands_on_an_id_that_exists()
    {
        var ids = Pages.ToDictionary(p => p, p => Regex.Matches(VisibleHtml(p), "\\sid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet());
        var route = new Dictionary<string, string> { ["/"] = "index.html", ["/help"] = "help.html", ["/privacy"] = "privacy.html", ["/plain-words"] = "plain-words.html" };
        foreach (var page in Pages)
            foreach (Match link in Regex.Matches(VisibleHtml(page), "href=\"(/[a-z-]*)?#([^\"]+)\""))
            {
                var target = link.Groups[1].Success && link.Groups[1].Value.Length > 0 ? route[link.Groups[1].Value] : page;
                Assert.True(ids[target].Contains(link.Groups[2].Value), $"site/{page} links to {link.Value}, which site/{target} does not have");
            }
    }
}
