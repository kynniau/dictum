using System.Net;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// Site: Store-first (owner decision, 22/10/2026; docs/DECISIONS.md). The Microsoft Store is the one install path the site
/// shows; there is no direct-download button, no SmartScreen or signing explanation, and one quiet footer line points pro
/// users at GitHub. Also checks that every same-site anchor on the site's pages lands on an id that exists. The legal block
/// (APPROVED by the owner 23/10/2026 09:50 AEDT) added terms.html to every page-wide gate here.
/// </summary>
public sealed class SiteStoreFirstTests
{
    private const string StoreUrl = "https://apps.microsoft.com/detail/9N0DG1DHTR57";
    private const string MacUrl = "https://github.com/kynniau/dictum/releases/tag/mac-v1.3.1"; // the Mac beta announcement, APPROVED by the owner 23/10/2026 07:10 AEDT: do not reword; the tag advances with each Mac release (DECISIONS), 1.3.1 published 27/10/2026 19:25 AEDT
    private const string GitHubLine = """Source code and release files are on <a href="https://github.com/kynniau/dictum">GitHub</a>.""";
    private static readonly string[] Pages = ["index.html", "help.html", "privacy.html", "plain-words.html", "terms.html"];

    /// <summary>A page of the site without its HTML comments.</summary>
    private static string VisibleHtml(string page) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page)), "<!--.*?-->", "", RegexOptions.Singleline);

    [Fact]
    public void The_two_Next_items_follow_the_Done_items_in_the_approved_order()
    {
        var html = VisibleHtml("index.html");
        var lastDone = html.LastIndexOf("status-done", StringComparison.Ordinal);
        var first = html.IndexOf("<span class=\"status status-next\">Next</span><div><h3>Models on another drive</h3>", StringComparison.Ordinal);
        var second = html.IndexOf("<span class=\"status status-next\">Next</span><div><h3>Add a word by hotkey</h3>", StringComparison.Ordinal);
        Assert.True(lastDone >= 0 && first > lastDone && second > first, "the Next items must follow every Done item: Models on another drive, then Add a word by hotkey");
        Assert.Equal(2, Regex.Matches(html, "status-next").Count);
    }

    [Fact]
    public void Every_button_on_the_landing_page_is_the_Microsoft_Store_or_the_Mac_beta_and_the_first_is_the_Store_in_the_hero()
    {
        var html = VisibleHtml("index.html");
        var buttons = Regex.Matches(html, "<a class=\"btn[^\"]*\" href=\"([^\"]+)\"");
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Contains(b.Groups[1].Value, new[] { StoreUrl, MacUrl }));
        Assert.Equal(StoreUrl, buttons[0].Groups[1].Value);
        Assert.Equal(3, buttons.Count(b => b.Groups[1].Value == MacUrl)); // hero, the Mac section, the closing band
        Assert.Equal(2, buttons.Count(b => b.Groups[1].Value == StoreUrl)); // hero, the closing band
        string Slice(string open) { var a = html.IndexOf(open, StringComparison.Ordinal); Assert.True(a >= 0, $"index.html has no {open}"); return html[a..html.IndexOf("</section>", a, StringComparison.Ordinal)]; }
        Assert.Equal(1, Regex.Matches(Slice("<section class=\"hero\""), Regex.Escape(MacUrl)).Count);
        Assert.Equal(1, Regex.Matches(Slice("<section class=\"section\" id=\"mac\""), Regex.Escape(MacUrl)).Count);
        Assert.Equal(1, Regex.Matches(Slice("<section class=\"section cta-band\""), Regex.Escape(MacUrl)).Count);
        Assert.Equal(1, Regex.Matches(Slice("<section class=\"section cta-band\""), Regex.Escape(StoreUrl)).Count);
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
    [InlineData("index.html", "It lives in your menu bar: hold the right Option key, talk, let go, and your words are typed into the app you are in.")]
    [InlineData("index.html", "The speech model comes inside the app, so nothing is downloaded to get started, and your voice stays on your Mac unless you turn on cloud speech.")]
    [InlineData("index.html", "It is a beta: signed and notarised with our Apple Developer ID, so it opens like any other Mac app, but it has had fewer hours on fewer Macs than the Windows version.")]
    [InlineData("index.html", "Unzip it, move Dictum to Applications and open it.")]
    [InlineData("index.html", "macOS asks for Microphone access and for Accessibility access (to see your hotkey and to paste into other apps).")]
    [InlineData("index.html", "Intel Macs are not supported.")]
    [InlineData("index.html", "Found something? Tell us from Settings > Help.")]
    [InlineData("index.html", "Check the download against SHA256SUMS-mac.txt from the same page.")]
    [InlineData("index.html", "Next Models on another drive Keep the big model files on a second drive or a shared folder, for shared desktops with little disk.")] // owner, 23/10/2026 07:47 AEDT
    [InlineData("index.html", "Next Add a word by hotkey Say a name once, fix its spelling, and Dictum gets it right from then on.")] // owner, 23/10/2026 07:47 AEDT
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
        // One exemption, on privacy.html (exactly four uses of one phrase) and on index.html (exactly one, see below): the policy's "in the direct download for Windows and in the
        // Mac beta" (legal block, APPROVED by the owner 23/10/2026 09:50 AEDT; once with a capital "In", starting section 5) names the build and
        // explains nothing, but contains "Download for Windows" case-blind. The fourth use is the website-route sentence of section 4 (O10, same
        // approval, applied in the follow-up PR; three before it). Those four are removed before the scan, so a fifth use, the phrase on
        // any other page, or a "Download for Windows" button still fails. Mutations (observed failing, 23/10/2026): "Download for Windows." added to
        // site/terms.html; "Prefer the direct download for Windows? It is on GitHub" added to site/index.html; "Get the direct download for Windows
        // from our GitHub releases." added to site/help.html; a fourth use added to site/privacy.html (before O10; now a fifth); and, with this exemption removed,
        // site/privacy.html fails.
        var policyName = new Regex("[Ii]n the direct download for Windows and in the Mac beta");
        foreach (var page in Pages)
        {
            var html = VisibleHtml(page);
            if (page == "privacy.html" || page == "index.html")
            {
                // index.html: exactly one use, the dictum.kynni.au card, which carries the policy's O10 sentence word for word (APPROVED by the owner
                // 23/10/2026 13:57 AEDT, legal strings block 2); a second use there still fails.
                Assert.Equal(page == "privacy.html" ? 4 : 1, policyName.Matches(html).Count);
                html = policyName.Replace(html, "");
            }
            Assert.False(html.Contains(phrase, StringComparison.OrdinalIgnoreCase), $"site/{page} still says \"{phrase}\"");
        }
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    [InlineData("privacy.html")]
    [InlineData("plain-words.html")]
    [InlineData("terms.html")]
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
    /// text on your PC, and the text is tidied on your PC." The legal block (APPROVED by the owner 23/10/2026 09:50 AEDT) made every pattern that
    /// matches "PC" match "computer" too, since the pages now cover the Mac. Mutations (observed failing, 23/10/2026): "Your voice never leaves your
    /// computer." and, separately, "Your speech is turned into text on your computer." added to site/terms.html; and, with the localhost exemption
    /// below removed, site/privacy.html fails on its localhost sentence.
    /// </summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    [InlineData("privacy.html")]
    [InlineData("plain-words.html")]
    [InlineData("terms.html")]
    public void No_page_says_the_voice_never_leaves_the_PC_without_an_unless(string page)
    {
        // One exemption, for exactly one sentence of the privacy policy (legal block): it is about a clean-up server's http://localhost connection,
        // not about the voice or the text leaving, and has no "unless" because there is none. It is removed whole before the scan, so any
        // other "never leaves your computer" still needs its "unless".
        const string localhostSentence = "A clean-up server on your own computer may use http://localhost, which never leaves your computer.";
        var sentences = Regex.Split(Prose(page).Replace(localhostSentence, "", StringComparison.Ordinal), @"(?<=[.!?])\s+");
        foreach (var s in sentences.Where(s => Regex.IsMatch(s, @"never\s+leaves\s+(this|your)\s+(PC|computer)", RegexOptions.IgnoreCase)))
            Assert.True(s.Contains("unless", StringComparison.OrdinalIgnoreCase), $"site/{page} says \"{s}\" with no \"unless\" in the same sentence");
        foreach (var s in sentences.Where(s => Regex.IsMatch(s, @"voice\s+(still\s+)?stays\s+(on|here)", RegexOptions.IgnoreCase)))
            Assert.True(s.Contains("unless", StringComparison.OrdinalIgnoreCase) || s.Contains("by default", StringComparison.OrdinalIgnoreCase),
                $"site/{page} says \"{s}\" with neither \"unless\" nor \"by default\"");
        foreach (var s in sentences.Where(s => Regex.IsMatch(s, @"turned\s+into\s+text\s+on\s+(this|your(\s+own)?)\s+(PC|computer)", RegexOptions.IgnoreCase)))
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
    [InlineData("index.html", "Only when you open the privacy policy, the Terms of Use, Dictum in plain words or the full guide from Dictum, or click Check for updates in the direct download for Windows and in the Mac beta, which reads one small file and sends nothing about you; the Store build is updated by the Store.")] // owner, 23/10/2026 13:57 AEDT (the policy's O10 sentence)
    [InlineData("help.html", "Dictum turns your voice into text on this PC, then throws the recording away. Your voice stays on this PC unless you turn on cloud speech, an advanced option that is off by default.")]
    [InlineData("help.html", "are sent to the service you chose. Your voice still stays on this PC unless you also turn on cloud speech.")]
    [InlineData("help.html", "No. Nothing is trained, and your voice stays on this PC unless you turn on cloud speech. Calibrate my voice")]
    [InlineData("help.html", "Dictum goes online only when you ask it to. That means four things: downloading a model you choose; checking for updates, in the direct-download version; cloud clean-up, if you turn it on; cloud speech, if you turn it on. Offline only, in Settings > Privacy, stops all four.")]
    // The legal block (APPROVED by the owner 23/10/2026 09:50 AEDT) replaced the policy's sentences pinned on the next four lines and on the last two
    // privacy lines of this list; the new sentences are pinned in their place, word for word.
    [InlineData("privacy.html", "Your speech is turned into text on your computer, unless you turn on cloud speech, and the text is tidied on your computer unless you turn on cloud clean-up.")]
    [InlineData("privacy.html", "If you switch them on, it also sends text to a cloud clean-up service, your voice to a cloud speech service, or either to a server you choose.")]
    [InlineData("privacy.html", "It is never saved to a file, except the calibration recordings you make on purpose (below). With cloud speech on, the recording is sent to the service you chose and then discarded here.")]
    [InlineData("privacy.html", ".env: an API key, only if you add one for cloud clean-up or cloud speech. The direct download also reads a .env you place beside Dictum.exe.")]
    [InlineData("privacy.html", "Cloud speech (off by default) Only if you switch on cloud speech in Settings > Models, agree to the service in its own consent step and, for OpenAI, Azure OpenAI or Groq, add your own API key (a server you name may need none).")]
    [InlineData("privacy.html", "sends that dictation’s recording, as a WAV file, to the service you chose, together with the words in your personal dictionary as a hint and, in hands-free, the closing words of the part before.")]
    [InlineData("privacy.html", "The service is OpenAI (api.openai.com), Azure OpenAI ({resource}.openai.azure.com, your own resource), Groq (api.groq.com) or a custom https:// address you enter.")]
    [InlineData("privacy.html", "This is an advanced option: Dictum cannot validate or certify how that service stores, protects or uses your voice. Check its terms before dictating anything sensitive.")]
    [InlineData("privacy.html", "Downloads, Check for updates, cloud clean-up, cloud speech and servers that are not on your computer are all refused. A clean-up or speech server running on your own computer still works.")]
    [InlineData("privacy.html", "Like any internet connection, each of these reveals your IP address (your internet address) to the server Dictum connects to. Each of them handles that under its own policy. Model downloads and requests to those services use HTTPS; Dictum refuses a plain http:// address for any service that is not on your own computer.")]
    public void The_cloud_speech_sentences_are_on_the_page(string page, string sentence) =>
        Assert.Contains(sentence, Prose(page), StringComparison.Ordinal);

    // ---- The legal block: Terms of Use (new page) and the Privacy Policy rewrite, APPROVED by the owner 23/10/2026 09:50 AEDT: do not reword ----

    /// <summary>
    /// The sentences of the legal block that carry its legal weight, word for word on the page that carries them (prose: markup gone, so a bold lead
    /// joins its sentence). Mutations (observed failing, 23/10/2026, one at a time): terms.html's section 9 cap sentence deleted; the same sentence
    /// with "A$100 (one hundred Australian dollars)" changed to "A$1,000 (one thousand Australian dollars)"; section 4's "Section 9 does not reduce
    /// any such guarantee, or what the law entitles you to if we fail to meet one." deleted.
    /// </summary>
    [Theory]
    [InlineData("terms.html", "The law’s guarantees still apply. Depending on where you live, the law may give you guarantees about Dictum that cannot be excluded or limited by agreement, for example under the Australian Consumer Law. Nothing in these terms excludes, limits or changes any such guarantee or right. Section 9 does not reduce any such guarantee, or what the law entitles you to if we fail to meet one.")]
    [InlineData("terms.html", "For any other loss, the most we will pay you, for all claims about Dictum or this website added together, is A$100 (one hundred Australian dollars).")]
    [InlineData("terms.html", "if you live in the European Union, the United Kingdom or anywhere else whose law lets you sue at home, you can take a claim to your home courts. We can sue you only there.")]
    [InlineData("terms.html", "Dictum for Mac beta. The Mac beta is signed with our Apple Developer ID and notarised so that macOS will open it. Notarisation does not mean that Apple has reviewed or approved Dictum; we, not Apple, are responsible for it, and Apple is not part of these terms. If there is an issue with malware, or if Apple revokes our certificate or the app’s notarisation ticket, Dictum for Mac may stop running; we will say so on dictum.kynni.au and on the release page.")]
    [InlineData("privacy.html", "Dictum sends nothing about you to Kynni. It has no account, no analytics, no telemetry (automatic reports on how you use an app) and no crash reporting. We never receive your audio or your text unless you send it to us yourself.")]
    [InlineData("privacy.html", "Your speech is turned into text on your computer, unless you turn on cloud speech, and the text is tidied on your computer unless you turn on cloud clean-up. Both are off until you switch them on and agree in their own consent step.")]
    [InlineData("privacy.html", "Dictum goes online only when you ask it to: to download a model, or to check for updates in the direct download for Windows and in the Mac beta. If you switch them on, it also sends text to a cloud clean-up service, your voice to a cloud speech service, or either to a server you choose.")]
    [InlineData("privacy.html", "What we do hold is small: the records our web host keeps of visits to this website, which we can see for one day (the host keeps its own copy for as long as it decides, section 7), and the emails you choose to send us.")]
    [InlineData("privacy.html", "If you are in Australia. You can complain to us at hello@kynni.com.au. We are a small business that the Australian Privacy Principles do not currently cover, so the Office of the Australian Information Commissioner (oaic.gov.au) may not be able to deal with a complaint about us; we handle your information as this policy says regardless.")]
    [InlineData("privacy.html", "When you email hello@kynni.com.au, we keep your email address, your name if you give it, and what you wrote, in our email account, which is hosted by Zoho Mail (Zoho Corporation) under Zoho’s privacy policy; Zoho may store it outside Australia, including in the United States.")]
    [InlineData("index.html", "No subscription, no word limits. Dictum is open source under the MIT licence")]
    [InlineData("index.html", "Every file Dictum downloads is checked against a fingerprint built into Dictum before it is used.")]
    [InlineData("index.html", "The speech and tidy-up models are made by other organisations and carry their own licences, listed in the Terms of Use. The recommended tidy-up model, Qwen2.5 1.5B Instruct, is licensed Apache-2.0.")]
    public void The_legal_block_sentences_are_on_the_page(string page, string sentence) =>
        Assert.Contains(sentence, Prose(page), StringComparison.Ordinal);

    /// <summary>
    /// The three section 9 bullets that keep the law's remedies whole sit under "We never exclude or limit liability for:", in that list, word for
    /// word: a bullet moved into the list of losses we are not liable for fails here although its words are still on the page.
    /// </summary>
    [Theory]
    [InlineData("any guarantee, right or remedy (a way the law puts things right) that the law gives you and that cannot be excluded or limited by agreement, including under the Australian Consumer Law;")]
    [InlineData("your right under the United Kingdom’s Consumer Rights Act 2015 to have a device or other digital content repaired, or to be paid for the damage, if Dictum damages it because we did not take reasonable care and skill;")]
    [InlineData("anything we must pay under product-liability law (laws that make a maker pay for harm a faulty product causes) that cannot be limited by agreement;")]
    public void The_terms_never_exclude_the_laws_remedies(string bullet)
    {
        var html = VisibleHtml("terms.html");
        const string lead = "<p><strong>We never exclude or limit liability for:</strong></p>";
        var start = html.IndexOf(lead, StringComparison.Ordinal);
        Assert.True(start >= 0, $"terms.html has no {lead}");
        Assert.Single(Regex.Matches(html, Regex.Escape(lead)));
        var list = html[start..html.IndexOf("</ul>", start, StringComparison.Ordinal)];
        Assert.Contains($"<li>{bullet}</li>", list, StringComparison.Ordinal); // the bullets hold no character HTML escapes
        Assert.Single(Regex.Matches(html, Regex.Escape($"<li>{bullet}</li>"))); // a copy of it in the not-liable list fails
    }

    /// <summary>The replaced lines are gone from the page that had them (each quoted in the PR body and DECISIONS).</summary>
    [Theory]
    [InlineData("index.html", "no upsell")]
    [InlineData("index.html", "Every file is checked against a fingerprint")]
    [InlineData("index.html", "which Dictum shows when you pick one")]
    [InlineData("index.html", "Qwen2.5 3B is for non-commercial use only")]
    [InlineData("help.html", "We collect nothing.")]
    [InlineData("privacy.html", "We collect nothing.")]
    [InlineData("privacy.html", "It is never written to disk")]
    public void The_lines_the_legal_block_replaced_are_gone(string page, string old) =>
        Assert.DoesNotContain(old, Prose(page), StringComparison.Ordinal);

    /// <summary>Every page of the site links to /terms from the footer, right after the privacy policy (plain-words.html joined in the follow-up).</summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    [InlineData("privacy.html")]
    [InlineData("plain-words.html")]
    [InlineData("terms.html")]
    public void Every_page_links_to_the_terms_from_its_footer_after_the_privacy_policy(string page)
    {
        var html = VisibleHtml(page);
        var footer = html[html.IndexOf("<footer", StringComparison.Ordinal)..];
        var privacy = page == "privacy.html" ? " aria-current=\"page\"" : ""; // a page marks its own footer link as the current page
        var terms = page == "terms.html" ? " aria-current=\"page\"" : "";
        Assert.Matches(new Regex($"<li><a href=\"/privacy\"{privacy}>Privacy policy</a></li>\\s*<li><a href=\"/terms\"{terms}>Terms of use</a></li>"), footer);
        Assert.Single(Regex.Matches(footer, "href=\"/terms\""));
    }

    /// <summary>No page of the site names one of the words public copy may not name (PublicCopyNeutralityTests sweeps every public file; this names the page).</summary>
    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    [InlineData("privacy.html")]
    [InlineData("plain-words.html")]
    [InlineData("terms.html")]
    public void No_page_names_a_word_public_copy_may_not_name(string page)
    {
        var text = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page));
        Assert.Empty(PublicCopyNeutralityTests.Offences($"site/{page}", text)); // the sweep's own rule, not a copy of it
    }

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
        var route = new Dictionary<string, string> { ["/"] = "index.html", ["/help"] = "help.html", ["/privacy"] = "privacy.html", ["/plain-words"] = "plain-words.html", ["/terms"] = "terms.html" };
        foreach (var page in Pages)
            foreach (Match link in Regex.Matches(VisibleHtml(page), "href=\"(/[a-z-]*)?#([^\"]+)\""))
            {
                var target = link.Groups[1].Success && link.Groups[1].Value.Length > 0 ? route[link.Groups[1].Value] : page;
                Assert.True(ids[target].Contains(link.Groups[2].Value), $"site/{page} links to {link.Value}, which site/{target} does not have");
            }
    }
}
