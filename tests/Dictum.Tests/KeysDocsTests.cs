using System.Text;
using System.Text.RegularExpressions;
using Dictum.Help;
using Dictum.Llm;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// K-Docs lane (04/11/2026), the API keys block's words placed where the owner approved them: the key file Dictum writes (row 43), the waiting line
/// (APPROVED 04/11/2026 17:14 AEDT, Windows only), Help rows 40, 44 and 45 (16:51, Windows only), the privacy policy's row 46 entry (16:49), section 10's
/// list and the short version's third bullet (17:37 and 17:28), and the downloadable key file on the site. Every expected string here is written out
/// from the owner's record (.demo-preview/two-model-api-keys-privacy.md and approve-api-keys.md), not read from the app, so a reworded constant fails.
/// The held rows (38a, 41, 42 and section 10's second paragraph pointer) must not show anywhere.
/// </summary>
public class KeysDocsTests
{
    private const string ApprovedHeader =
        "# Dictum keys, created by Settings > Clean-up. Replace your-key-here with your key and save this file: Dictum reads it again by itself.\n" +
        "# A real environment variable of the same name wins over this file. Keys are never written to settings.json or the log.\n";

    private const string Row40 =
        "The easiest way to add a key: choose the service in Settings > Clean-up, press Add a key…, paste the key, and press Save. " +
        "Dictum keeps it in Windows Credential Manager, never in a file, and uses it from the next dictation. " +
        "You can add more than one key for a service, such as a work one and a personal one, and choose which one Dictum uses.";

    private const string Row44 =
        "Dictum reads your key from the keys you add in Dictum (kept in Windows Credential Manager), the .env file or a Windows environment variable. It never saves the key in its settings, and never shows it on screen or writes it in its log.";

    private const string Row45 =
        "To check the key itself, press Check this key beside it in Settings > Clean-up once cloud clean-up is on. If your dictations come back only lightly tidied, check the key.";

    private const string OldRow44 =
        "Dictum reads your key only from the .env file (or from a Windows environment variable). It never saves the key in its settings, and never shows it on screen or writes it in its log.";

    private const string OldRow45 =
        "Dictum cannot check that the key itself is right: if your dictations come back only lightly tidied, check the key.";

    private const string OldWaiting = "On, but waiting for your OPENAI_API_KEY: nothing is sent until you add it to .env.";

    private const string NewWaiting = "On, but waiting for your OPENAI_API_KEY: nothing is sent until you add the key in Settings > Clean-up or in .env.";

    private static string Site(string file) => File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", file));

    private static IReadOnlyList<string> CloudAnswer(PlatformWords words) =>
        HelpText.QuestionsFor(words).Single(q => q.Question == HelpText.CloudQuestion).Answer;

    // ---- Row 43: the key file ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Row 43 (decided 04/11/2026 16:52 AEDT): the file starts with the approved header byte for byte, has no word of .env.example's "Copy to
    /// %APPDATA%…" paragraph (the folder path and Dictum.exe), and begins its template at the "# Syntax" line.
    /// </summary>
    [Fact]
    public void The_key_file_has_the_approved_header_then_the_template_from_the_Syntax_line_without_the_copy_paragraph()
    {
        var text = KeyFile.Contents();
        Assert.Equal(Encoding.UTF8.GetBytes(ApprovedHeader), Encoding.UTF8.GetBytes(text)[..Encoding.UTF8.GetByteCount(ApprovedHeader)]);
        Assert.DoesNotContain("%APPDATA%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictum.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy to", text, StringComparison.Ordinal);
        var lines = text.Split('\n');
        var headerLines = ApprovedHeader.Count(c => c == '\n');
        Assert.Equal("", lines[headerLines]); // a blank line after the header
        Assert.StartsWith("# Syntax", lines[headerLines + 1], StringComparison.Ordinal); // then the template from its Syntax line
    }

    /// <summary>The skip finds the "# Syntax" line wherever it is, and fails loudly without it (a hard-coded line count would do neither).</summary>
    [Fact]
    public void The_skip_finds_the_Syntax_line_and_fails_loudly_when_it_is_missing()
    {
        Assert.Equal("# Syntax: x\nKEY=\n", KeyFile.FromSyntaxLine("# one\n# two\n# Syntax: x\nKEY=\n"));
        Assert.Equal("# Syntax: x\nKEY=\n", KeyFile.FromSyntaxLine("# Syntax: x\nKEY=\n")); // the first line
        Assert.Equal("# Syntax: x\n", KeyFile.FromSyntaxLine("# a\n\n\n\n\n\n\n# Syntax: x\n")); // not a count of lines
        var ex = Assert.Throws<InvalidOperationException>(() => KeyFile.FromSyntaxLine("# one\n# two\nKEY=\n"));
        Assert.Contains("# Syntax", ex.Message, StringComparison.Ordinal);
    }

    // ---- The waiting line ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// APPROVED 04/11/2026 17:14 AEDT (Windows only): the Clean-up card's waiting line says where to add the key; the Mac keeps the 16/10/2026 line, and
    /// cloud speech's line is the 16/10/2026 one (its keys are added under Settings > Models).
    /// </summary>
    [Fact]
    public void The_waiting_line_is_the_approved_Windows_words_on_Windows_and_the_old_line_on_a_Mac()
    {
        Assert.Equal(NewWaiting, CloudPolicy.WaitingForKeyLine("OPENAI_API_KEY", PlatformWords.Windows));
        Assert.Equal(OldWaiting, CloudPolicy.WaitingForKeyLine("OPENAI_API_KEY", PlatformWords.Mac));
        Assert.Equal(OldWaiting, CloudPolicy.WaitingForKeyLine("OPENAI_API_KEY"));
        Assert.Equal("On, but waiting for your GROQ_API_KEY: nothing is sent until you add it to .env.", SpeechStatus.WaitingForKey("GROQ_API_KEY"));
    }

    // ---- Help rows 40, 44, 45 ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows 40, 44 and 45 (APPROVED 04/11/2026 16:51 AEDT, Windows only): Windows Help carries them, row 40 directly above the approved key-file
    /// paragraph, row 44 as the last paragraph, row 45 ending the "When Dictum has found your key…" paragraph, and quotes the new waiting line.
    /// </summary>
    [Fact]
    public void Windows_help_carries_rows_40_44_and_45_and_not_the_sentences_they_replace()
    {
        var answer = CloudAnswer(PlatformWords.Windows);
        var keyFile = answer.ToList().FindIndex(p => p.StartsWith("To add a key for cloud clean-up, choose the service", StringComparison.Ordinal));
        Assert.True(keyFile > 0);
        Assert.Equal(Row40, answer[keyFile - 1]);
        Assert.Equal(Row44, answer[^1]);
        var found = Assert.Single(answer, p => p.StartsWith("When Dictum has found your key,", StringComparison.Ordinal));
        Assert.Equal("When Dictum has found your key, the Cloud clean-up card on that page starts with \"On: the text of each dictation is sent to\" and names the service, and the Dictum menu says \"Cloud clean-up on · text leaves this PC\". " + Row45, found);
        var all = string.Join("\n", answer);
        Assert.Contains("\"" + NewWaiting + "\"", all, StringComparison.Ordinal);
        Assert.DoesNotContain(OldRow44, all, StringComparison.Ordinal);
        Assert.DoesNotContain(OldRow45, all, StringComparison.Ordinal);
        Assert.DoesNotContain(OldWaiting, all, StringComparison.Ordinal);
        Assert.Same(HelpText.CloudAnswer, answer);
    }

    /// <summary>The Mac's Help keeps today's two approved sentences, the old waiting line and shows no row 40 (there is no credential store to name).</summary>
    [Fact]
    public void Mac_help_keeps_the_approved_sentences_and_shows_no_row_40()
    {
        var answer = CloudAnswer(PlatformWords.Mac);
        var all = string.Join("\n", answer);
        Assert.Equal(OldRow44, answer[^1]);
        Assert.Contains(" " + OldRow45, all, StringComparison.Ordinal);
        Assert.Contains("\"" + OldWaiting + "\"", all, StringComparison.Ordinal);
        Assert.DoesNotContain(Row40, all, StringComparison.Ordinal);
        Assert.DoesNotContain(Row44, all, StringComparison.Ordinal);
        Assert.DoesNotContain(Row45, all, StringComparison.Ordinal);
        Assert.DoesNotContain("Credential Manager", all, StringComparison.Ordinal);
        Assert.Equal(HelpText.CloudAnswer.Count - 1, answer.Count); // the Windows list less row 40
    }

    // ---- The privacy policy ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Row 46 (APPROVED 04/11/2026 16:49 AEDT), section 10's list (17:37), the short version's bullet 3 (17:28) and the date (section 11), word for word
    /// on site/privacy.html; and the held text (rows 38a, 41, 42, section 10's second-paragraph pointer) is not on it.
    /// </summary>
    [Fact]
    public void The_privacy_policy_carries_the_approved_key_wording_and_none_of_the_held_text()
    {
        var html = Site("privacy.html");
        Assert.Contains("<h3>Check this key (only when you press it)</h3>\n    <p>Only if you have switched on cloud clean-up or cloud speech, agreed to the service in its consent step, and press Check this key beside a key in Settings. " +
            "Dictum then sends one request to that service, or to the address you entered, asking it to list its models. The request carries your key and nothing you dictated or wrote. " +
            "It goes to the same address your dictations go to, and nowhere else. The answer is shown to you with anything that looks like a key hidden. " +
            "Offline only refuses it unless that address is on your own computer.</p>", html, StringComparison.Ordinal);
        var network = html.IndexOf("<h2 id=\"network\">", StringComparison.Ordinal);
        var entry = html.IndexOf("<h3>Check this key (only when you press it)</h3>", StringComparison.Ordinal);
        var updates = html.IndexOf("<h2 id=\"updates\">", StringComparison.Ordinal);
        Assert.True(network > 0 && network < entry && entry < updates, "the row 46 entry sits inside section 4");

        Assert.Contains("That deletes everything Dictum has stored: models, recordings, logs, settings, consent records, dictionary, corrections, snippets, <code>.env</code> and the API keys you added in Dictum. It also deletes", html, StringComparison.Ordinal);
        Assert.Contains("or either to a server you choose. If you press Check this key beside a key you added, it sends that key, and nothing you dictated or wrote, to the same address your dictations go to.</li>", html, StringComparison.Ordinal);
        Assert.Contains("Last updated <time datetime=\"2026-11-04\">04/11/2026</time>", html, StringComparison.Ordinal);

        // Held for the Store uninstall check (owner, 04/11/2026 15:23 AEDT): rows 38a, 41, 42 and the section 10 pointer.
        foreach (var held in new[] { "Uninstalling Dictum, from the Store", "Uninstalling does not remove API keys", "They stay there also if you uninstall", "are not in those folders", "Unnamed key", "Remove all keys" })
            Assert.DoesNotContain(held, html, StringComparison.Ordinal);
    }

    // ---- The site's key file and guides ---------------------------------------------------------------------------------------------

    /// <summary>
    /// site/dictum-keys.env is the file Dictum writes (KeyFile.Contents), byte for byte, so it cannot drift. Run with DICTUM_WRITE_SITE_KEY_FILE=1 to
    /// write it from KeyFile.Contents, then commit it.
    /// </summary>
    [Fact]
    public void The_downloadable_key_file_is_the_one_Dictum_writes()
    {
        var path = Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "dictum-keys.env");
        var expected = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(KeyFile.Contents());
        if (Environment.GetEnvironmentVariable("DICTUM_WRITE_SITE_KEY_FILE") == "1") File.WriteAllBytes(path, expected);
        Assert.True(File.Exists(path), "site/dictum-keys.env is missing: run this test once with DICTUM_WRITE_SITE_KEY_FILE=1");
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Contains("href=\"dictum-keys.env\"", Site("help.html"), StringComparison.Ordinal);
    }

    /// <summary>The dialog's "How do I get a key?" link opens a section that exists (row 22), and each provider's part names its source and the date read.</summary>
    [Theory]
    [InlineData(SecretStore.AnthropicKey, "key-anthropic")]
    [InlineData(SecretStore.OpenAiKey, "key-openai")]
    [InlineData(SecretStore.GeminiKey, "key-gemini")]
    [InlineData(SecretStore.AzureOpenAiKey, "key-azure")]
    [InlineData(SecretStore.GroqKey, "key-groq")]
    [InlineData(SecretStore.LlmKey, "key-custom")]
    [InlineData(SecretStore.SpeechKey, "key-custom")]
    public void Every_key_guide_anchor_the_dialog_opens_is_a_section_of_the_help_page(string variable, string id)
    {
        Assert.Equal("#" + id, AddKeyWindow.GuideAnchor(variable));
        var html = Site("help.html");
        Assert.Single(Regex.Matches(html, "id=\"" + id + "\""));
        if (id == "key-custom") return; // no provider: it describes only what Dictum does
        var start = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
        var head = html[start..html.IndexOf("<ol>", start, StringComparison.Ordinal)];
        Assert.Contains("https://", head, StringComparison.Ordinal);
        Assert.Contains("read 04/11/2026", head, StringComparison.Ordinal);
    }
}
