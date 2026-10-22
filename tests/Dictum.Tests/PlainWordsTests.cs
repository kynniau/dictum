using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Dictum.Help;
using Dictum.UI.Views.Onboarding;

namespace Dictum.Tests;

/// <summary>
/// "Dictum in plain words" (docs/DECISIONS.md), APPROVED by the owner 22/10/2026 17:36 AEDT: the site page /plain-words (section 2a),
/// the short version for Settings > About and the Welcome window (2b), no acceptance gate on first run (2c), the Store listing
/// sentence (section 3), and the landing page's "Free to keep on your PC" (17:42 AEDT). Each pinned word for word, independently of
/// the app's constants.
/// </summary>
public sealed class PlainWordsTests
{
    private static string Site(string page) => File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page));

    /// <summary>The text of a page's main element, one line per block, tags removed and entities decoded.</summary>
    private static List<string> MainLines(string page)
    {
        var html = Regex.Replace(Site(page), "<!--.*?-->", "", RegexOptions.Singleline);
        var start = html.IndexOf("<main", StringComparison.Ordinal);
        var main = html[start..html.IndexOf("</main>", start, StringComparison.Ordinal)];
        main = Regex.Replace(main, "</(p|li|h[1-6])>", "\n");
        main = Regex.Replace(main, "<[^>]+>", "");
        return [.. WebUtility.HtmlDecode(main).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    /// <summary>The short version, section 2b, one entry per paragraph.</summary>
    internal static readonly string[] ShortVersion =
    [
        "Dictum is free. Free: no subscription, no account, no ads, no analytics, no telemetry. No spyware: Dictum sends nothing about you to Kynni.",
        "Dictum is open source under the MIT licence. It is provided as is, without warranty. We built it with care, but we cannot promise it will always hear you right.",
        "Nothing you say leaves this PC unless you switch on a cloud option and agree to it. If you do, you take responsibility for what is sent to that service, and its own terms apply.",
        "Read the whole statement at dictum.kynni.au/plain-words.",
    ];

    /// <summary>Pin (2a): the site page says the approved statement, word for word, in the approved order.</summary>
    [Fact]
    public void The_plain_words_page_is_the_approved_statement_word_for_word()
    {
        string[] expected =
        [
            "Dictum in plain words",
            "Dictum is free. Free: no subscription, no account, no ads, no analytics, no telemetry. The base product stays free to keep on your PC; optional paid extras may come later.",
            "There is no spyware in Dictum. It does not watch what you type. It keeps no usage statistics for anyone but you: its log stays on your PC and never records what you said. It does not phone home: the only request to Kynni's website is Check for updates, which you press, and it sends nothing about you.",
            "Dictum is open source, which means anyone can read the program's code. It is made by Kynni Pty Ltd and shared under the MIT licence. That licence lets anyone use, copy and change Dictum, for free.",
            "How we protect you",
            "Your voice is turned into text on your own PC unless you turn on cloud speech. The text is tidied on your own PC.", // APPROVED 22/10/2026 22:05 AEDT (qualifier added)
            "Nothing you say leaves your PC unless you switch on a cloud option and agree to it. Dictum tells you what would be sent, and to where, before you agree.",
            "Dictum refuses to send your words over an unencrypted connection.",
            "Offline only, in Settings > Privacy, stops Dictum itself from connecting to any other computer at all.",
            "Every model file Dictum downloads is checked against a fingerprint built into Dictum. A file that does not match is not used.",
            "If you add a key for a cloud service, Dictum keeps it in a file on your PC. It never puts it in your settings, on screen or in a log.",
            "This website uses no cookies, no scripts and no tracking.",
            "The code is public. Anyone can check that all of this is true.",
            "What we cannot take responsibility for",
            "Dictum is free and we give it to you as it is. The MIT licence says it in legal words: the software is provided \"as is\", without warranty of any kind, and the authors are not liable for any claim or damage arising from its use. We say it in plain words: we have built it with care, but we cannot promise it will always hear you right, and we cannot pay for a loss it causes. If you need a promise, please do not rely on Dictum for it.",
            "If you choose a cloud service for clean-up or for speech, your text or your voice goes to that service. You choose the service. You hold the key. That service's own terms apply. Dictum encrypts the connection but cannot see, check or certify what that service keeps or does with your words. By switching a cloud option on, you take responsibility for the security and privacy of what is sent there. Do not dictate confidential, personal or client information with a cloud option on unless you have checked that service's terms yourself.",
            "The files Dictum keeps on your PC (your settings, dictionary, corrections, recordings and any key) are ordinary files under your Windows account. Looking after your PC, your account and your backups is up to you.",
            "If you installed Dictum from the Microsoft Store, Microsoft handles the download and updates under its own privacy statement, and gives us summary reports such as install counts and crash summaries. Dictum itself sends none of this.",
            "Questions: hello@kynni.com.au. The full privacy policy names every address Dictum can connect to.",
        ];
        Assert.Equal(expected, MainLines("plain-words.html"));
        Assert.Contains("APPROVED by the owner 22/10/2026 17:36 AEDT", Site("plain-words.html"));
        Assert.Contains("APPROVED 22/10/2026 20:45 AEDT (replaces the 17:36 sentence)", Site("plain-words.html"));
    }

    /// <summary>Pin: the page prints no file path (the brief's code citations stay out of public copy).</summary>
    [Fact]
    public void The_plain_words_page_prints_no_code_path()
    {
        var text = string.Join("\n", MainLines("plain-words.html"));
        foreach (var bad in new[] { ".cs", "src/", "src\\", ".json", ".txt", "%APPDATA%", "%LOCALAPPDATA%" })
            Assert.DoesNotContain(bad, text, StringComparison.Ordinal);
    }

    /// <summary>Pin: the page names no language, country, region or team (the public-copy rule of 21/10/2026).</summary>
    [Fact]
    public void The_plain_words_page_names_no_language_country_region_or_team()
    {
        var text = Site("plain-words.html");
        foreach (var word in PublicCopyNeutralityTests.Words)
            Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"site/plain-words.html names \"{word}\"");
    }

    /// <summary>Pin: all four pages link to /plain-words from the footer; the page itself marks it as current.</summary>
    [Theory]
    [InlineData("index.html", "<li><a href=\"/plain-words\">Dictum in plain words</a></li>")]
    [InlineData("help.html", "<li><a href=\"/plain-words\">Dictum in plain words</a></li>")]
    [InlineData("privacy.html", "<li><a href=\"/plain-words\">Dictum in plain words</a></li>")]
    [InlineData("plain-words.html", "<li><a href=\"/plain-words\" aria-current=\"page\">Dictum in plain words</a></li>")]
    public void Every_page_has_the_plain_words_link_in_its_footer(string page, string link)
    {
        var html = Site(page);
        var footer = html[html.IndexOf("<footer", StringComparison.Ordinal)..];
        Assert.Contains(link, footer);
    }

    /// <summary>Pin (2b): the app's short version equals the approved text, paragraph by paragraph, and names the page it opens.</summary>
    [Fact]
    public void The_short_version_is_pinned_word_for_word()
    {
        Assert.Equal(ShortVersion, PlainWordsText.Paragraphs);
        Assert.Equal("In plain words", PlainWordsText.CardTitle);
        Assert.Equal("Open in your browser", PlainWordsText.OpenLabel);
        Assert.Equal("https://dictum.kynni.au/plain-words", PlainWordsText.Url);
    }

    /// <summary>Pin (2b, 2c): the Welcome window's first step shows the short version after the approved text and before the Next line, with no checkbox and no "I understand".</summary>
    [Fact]
    public Task The_welcome_step_shows_the_short_version_and_asks_for_no_acceptance() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell();
        var flow = new OnboardingFlow(shell, Dictum.Settings.SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true);
        var window = new OnboardingWindow(flow);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        var at = texts.IndexOf(OnboardingText.WelcomeText);
        Assert.True(at >= 0, "the approved Welcome text is not shown");
        Assert.Equal(ShortVersion, texts.Skip(at + 1).Take(ShortVersion.Length));
        Assert.Equal(OnboardingText.WelcomeNext, texts[at + 1 + ShortVersion.Length]);
        Assert.Empty(window.GetVisualDescendants().OfType<CheckBox>());
        Assert.DoesNotContain(texts, t => t.Contains("I understand", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(OnboardingText.Next, window.PrimaryButton.Content);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (2b): Settings > About ends with the card "In plain words": the four paragraphs word for word, after the Files card,
    /// and a button "Open in your browser" that opens the site page and nothing else.
    /// </summary>
    [Fact]
    public Task The_about_page_ends_with_the_plain_words_card() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, platform, _) = scratch.Shell();
        var window = new Dictum.UI.Views.SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        var files = texts.IndexOf("FILES");
        var card = texts.IndexOf("IN PLAIN WORDS");
        Assert.True(files >= 0 && card > files, "the \"In plain words\" card does not come after the Files card");
        Assert.Equal(ShortVersion, texts.Skip(card + 1).Take(ShortVersion.Length));
        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Open in your browser" && b.IsEffectivelyVisible);
        Assert.Equal("Open in your browser", Avalonia.Automation.AutomationProperties.GetName(button) is { Length: > 0 } name ? name : (string)button.Content!);
        Assert.Empty(platform.Opened);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["https://dictum.kynni.au/plain-words"], platform.Opened);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (20:47 AEDT): the privacy policy's "This website" list names the plain-words route, and the README's network table does too.</summary>
    [Fact]
    public void The_privacy_policy_and_the_readme_name_the_plain_words_route()
    {
        var privacy = Site("privacy.html");
        Assert.Contains("<p>Only when you open the privacy policy, Dictum in plain words or the full guide from Settings, or click Check for updates in the direct-download version, which reads one small file and sends nothing about you;", privacy);
        Assert.Contains("APPROVED 22/10/2026 20:47 AEDT (plain-words route added)", privacy);
        var readme = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "README.md"));
        Assert.Contains(readme.Split('\n'), l => l.StartsWith("| Only when you open the privacy policy, Dictum in plain words", StringComparison.Ordinal) && l.Contains("/plain-words"));
    }

    /// <summary>Pin (section 3): the Store description ends PRIVATE BY DESIGN with the approved sentence, and the sentence has no URL.</summary>
    [Fact]
    public void The_store_listing_carries_the_approved_sentence()
    {
        const string sentence = "Dictum is provided as is under the MIT licence; if you choose a cloud service, you take responsibility for what is sent to it.";
        var store = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "docs", "STORE.md"));
        var line = store.Split('\n').Single(l => l.StartsWith("Speech recognition and clean-up run on your PC by default.", StringComparison.Ordinal)).TrimEnd();
        Assert.EndsWith(" " + sentence, line);
        Assert.Contains("APPROVED by the owner 22/10/2026 17:36 AEDT", store);
        Assert.DoesNotContain("http", sentence);
    }

    /// <summary>Pin (17:42 AEDT): the landing page says "Free to keep on your PC" everywhere it said the old phrase, and the approved Free lines are unchanged.</summary>
    [Fact]
    public void The_landing_page_says_free_to_keep_on_your_PC()
    {
        var html = Regex.Replace(Site("index.html"), "<!--.*?-->", "", RegexOptions.Singleline);
        Assert.Equal(4, Regex.Matches(html, "Free to keep on your PC", RegexOptions.None).Count); // meta description, at-a-glance list, Free card heading, comparison table
        Assert.DoesNotContain("Free for life", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p class=\"section-intro\">Free: no subscription, no account, no ads, no analytics, no telemetry.</p>", html);
        Assert.Contains("<p class=\"section-outro\">The base product stays free to keep on your PC; optional paid extras may come later.</p>", html);
        Assert.Contains("APPROVED by the owner 22/10/2026 17:42 AEDT", Site("index.html"));
    }

    // ---- Cloud speech: README, Store and Mac handover (APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword) ----

    private static string Repo(params string[] path) => File.ReadAllText(Path.Combine([FixBatchDownloadTests.RepoRoot(), .. path]));

    /// <summary>Whitespace collapsed, so a sentence the file wraps across lines is found as one.</summary>
    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ");

    /// <summary>README: the network table has a cloud speech row, and the consent sentence and the offline_only and audio lines are brought into line with cloud speech.</summary>
    [Fact]
    public void The_readme_says_what_cloud_speech_sends_and_when()
    {
        var readme = Repo("README.md");
        var row = Assert.Single(readme.Split('\n'), l => l.StartsWith("| After each dictation (each part in hands-free), only if you switch cloud speech on in Settings > Models", StringComparison.Ordinal));
        Assert.Contains("agree to the host in its own consent step, and a key is set where the service needs one | the host in `speech.base_url` (", row);
        Assert.Contains("that dictation's audio as WAV, your dictionary terms as a hint, and in hands-free the carried words |", row);
        var flat = Flat(readme);
        Assert.Contains("A dialog says what is sent (every dictation's text, \"not the audio: cloud speech, if you turn it on, sends the audio to the service you choose for it\", together with Dictum's clean-up instructions, which include the words in your personal dictionary) and to which host", flat);
        Assert.DoesNotContain("every dictation's text, never the audio", flat); // the consent's old parenthesis (the log line "never the audio or text" is about the failure reason and stays)
        Assert.Contains("It refuses cloud speech to any service that is not on this machine too, and the speech model on this PC hears the dictation.", flat);
        Assert.Contains("With cloud speech on, it is also sent to the service you chose (see the table above), then discarded here.", flat);
        Assert.Contains("The consent step is in Settings > Models (Advanced view), under Cloud speech.", flat);
        Assert.DoesNotContain("Settings does not have its consent step yet", flat);
        Assert.Contains("**Nothing leaves the machine by default.**", readme); // the approved heading line is unchanged
    }

    /// <summary>Store: the listing text, the age-rating answer, the privacy-policy hosts note, row 13, and the 1.3.0 What's new block (APPROVED 22/10/2026 22:52 AEDT) and the 1.3.0 tester notes (still PROPOSED: they go with the Store submission), above/after the approved 1.2.1 ones.</summary>
    [Fact]
    public void The_store_text_names_cloud_speech_and_the_1_3_0_blocks_are_proposed()
    {
        var store = Repo("docs", "STORE.md");
        var flat = Flat(store);
        Assert.Contains("or, if you switch it on, to send your text to a cloud clean-up service (Anthropic, OpenAI or Google Gemini) with your own API key, or your voice to a cloud speech service (OpenAI, Azure OpenAI or Groq) with your own API key, or to your own server, an advanced option; that service's own terms apply.", flat);
        Assert.Contains("The clean-up model sees only your words, never your audio, and is instructed not to add, answer or summarise anything. If its answer fails Dictum's checks, a rule-based tidy is used instead, and you can switch AI clean-up off completely. With cloud speech on (off by default), the recording goes to the speech service you chose instead of whisper.cpp; if that request fails, whisper.cpp on your PC hears it. To report", flat);
        Assert.Contains("or cloud speech (their own OpenAI, Azure OpenAI or Groq key, or their own server), the audio of each dictation goes to that service.", flat);
        Assert.Contains("it also lists the cloud speech hosts (api.openai.com, {resource}.openai.azure.com, api.groq.com and a custom address) under \"Cloud speech (off by default)\"", flat);
        Assert.Contains("the cloud speech consent dialog (its own, apart from clean-up's", flat);
        // The approved sentence is unchanged (see The_store_listing_carries_the_approved_sentence); the opening line gained "by default" (owner, 22/10/2026 22:05 AEDT).
        Assert.Contains("Speech recognition and clean-up run on your PC by default. There is no account", flat);
        Assert.Contains("Speech recognition runs on your PC by default with whisper.cpp and works offline", flat); // the feature bullet (owner, 22/10/2026 22:05 AEDT)
        Assert.DoesNotContain("\nSpeech recognition runs on your PC with whisper.cpp and works offline\n", store.Replace("\r\n", "\n")); // the old bullet line (the note quotes it as history)

        var now = store.IndexOf("### What's new in this version (1.3.0)", StringComparison.Ordinal);
        var before = store.IndexOf("### What's new in this version (1.2.1)", StringComparison.Ordinal);
        Assert.True(now > 0 && now < before, "the 1.3.0 block sits above the 1.2.1 one");
        var block = store[now..before];
        Assert.Contains("APPROVED 22/10/2026 22:52 AEDT", block);
        Assert.Contains("Adds optional cloud speech: an advanced setting, off by default, that sends each recording to OpenAI, Azure OpenAI or Groq with your own API key, after a consent step of its own.", block);
        Assert.Contains("your own clean-up instructions and a Create the key file button", Flat(block));
        Assert.Contains("Settings > Storage offers to move your model files", block);
        Assert.Contains("\"In plain words\"", block);
        Assert.DoesNotContain("http", block); // the Store says no URLs in plain text

        var notes = store.IndexOf("### Notes for certification: PROPOSED for 1.3.0", StringComparison.Ordinal);
        Assert.True(notes > store.IndexOf("Notes written 22/10/2026 for Dictum 1.2.1.", StringComparison.Ordinal), "the 1.3.0 notes come after the approved 1.2.1 notes");
        var changed = Flat(store[notes..store.IndexOf("## 8. What to expect from certification", notes, StringComparison.Ordinal)]);
        Assert.Contains("CHANGED SINCE THE LAST SUBMISSION: Dictum 1.3.0 adds optional cloud speech.", changed);
        Assert.Contains("It is off by default. It asks for express consent in its own dialog", changed);
        Assert.Contains("Cloud clean-up (Anthropic, OpenAI or Google Gemini with the user's own key, or the user's own server) and cloud speech (OpenAI, Azure OpenAI or Groq with the user's own key, or the user's own server) are optional and off by default", changed);
        Assert.Contains("| Dictum 1.3.0 prepared (version bump; listing text and What's new approved 22/10/2026; tester notes PROPOSED).", store); // the certification-record row (release lane, 23/10/2026); the tester notes above stay PROPOSED
        Assert.Contains("Dictum 1.3.0 tagged (v1.3.0 on b8b072d, 02:21 AEDT)", store); // the release record (docs lane, 23/10/2026)
        Assert.Contains("Dictum-1.3.0-store.msix built from b8b072d: 240,138,818 bytes", store);
        Assert.Contains("APPROVED by the owner 22/10/2026 14:33 AEDT", store); // the 1.2.1 approvals are untouched
    }

    /// <summary>Mac handover: the owner's 22/10/2026 decisions are recorded, and the lines that said Intel too are brought into line.</summary>
    [Fact]
    public void The_mac_handover_records_the_owners_22_10_2026_decisions()
    {
        var mac = Repo("docs", "MAC-HANDOVER.md");
        var flat = Flat(mac);
        Assert.Contains("## 0. Owner decisions of 22/10/2026 (these win over anything below that says otherwise)", flat);
        Assert.Contains("**Developer ID signed and notarised GitHub pre-release.** The owner joined the Apple Developer Program on 22/10/2026", flat);
        Assert.Contains("The Mac session never handles Apple credentials", flat);
        Assert.Contains("to be re-evaluated by the Mac lane now that signing is available", flat);
        Assert.DoesNotContain("ad-hoc signed GitHub pre-release", flat);
        Assert.Contains("**Apple Silicon only (arm64) for the beta.** One zip:", flat);
        Assert.Contains("Intel (osx-x64) is built **only if a tester needs it**", flat);
        Assert.Contains("**Timing.** The Mac build ships after 1.3 is live.", flat);
        Assert.DoesNotContain("Apple Silicon and Intel (two zips) unless Kyle says", flat);
        Assert.DoesNotContain("publishes both RIDs", flat);
        Assert.Contains("the cloud clean-up or cloud speech the user turns on", flat);
    }
}
