using System.Net;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// The Terms of Use exist twice: <c>packaging/TERMS.txt</c>, which Setup shows (<c>LicenseFile=</c> in Dictum.iss, so it asks for "I accept the
/// agreement"), and <c>site/terms.html</c>, which dictum.kynni.au serves and which Dictum's Welcome window links to. A change to the wording of one
/// that does not reach the other would mean a user accepts one text in the installer and is bound by another on the website, so the two must say
/// the same thing, block for block: every heading, paragraph and list item, in order. Added by the legal block's follow-up PR (the Terms were
/// APPROVED by the owner 23/10/2026 09:50 AEDT, and are pinned word for word elsewhere; this test pins the two copies to each other, so it needs no
/// edit when the owner approves a change, only that the change is made in both files).
///
/// What is ignored, on purpose: the title and the date line (TERMS.txt opens "Dictum Terms of Use" and "Dictum by Kynni. Terms of Use. Version 1.";
/// the page has an h1 and "Version 1, last updated 23/10/2026"), and everything outside the page's &lt;main&gt; (the meta tags, header, nav and footer).
/// What is smoothed: markup and character references (the page's), typographic apostrophes and quotation marks (the page uses ’ “ ”; the text file
/// uses ' and ", as a plain-text licence page should), runs of spaces, and the "- " that starts a list item in the text file.
/// </summary>
public sealed class TermsTextConsistencyTests
{
    private static string Repo(params string[] parts) => File.ReadAllText(Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]));

    /// <summary>One line of text, the way both copies are compared: typographic marks made plain, spaces collapsed.</summary>
    private static string Plain(string text) =>
        Regex.Replace(text.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Replace(' ', ' '), @"\s+", " ").Trim();

    /// <summary>
    /// The installer's blocks: every non-blank line after the two heading lines, a list item without its "- ". The text file keeps each paragraph
    /// on one line, so a line is a block.
    /// </summary>
    private static List<string> InstallerBlocks()
    {
        var lines = Repo("packaging", "TERMS.txt").Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal("Dictum Terms of Use", lines[0]); // the title line, ignored
        Assert.StartsWith("Dictum by Kynni. Terms of Use. Version ", lines[1], StringComparison.Ordinal); // the date line, ignored
        return [.. lines.Skip(2).Select(l => Plain(l.StartsWith("- ", StringComparison.Ordinal) ? l[2..] : l))];
    }

    /// <summary>
    /// The page's blocks: every h2, p and li inside &lt;main&gt;, in order, with comments and markup gone and character references decoded, after
    /// the h1 and the "last updated" line, which are ignored.
    /// </summary>
    private static List<string> SiteBlocks()
    {
        var html = Regex.Replace(Repo("site", "terms.html"), "<!--.*?-->", "", RegexOptions.Singleline);
        var main = Regex.Match(html, @"<main\b[^>]*>(.*?)</main>", RegexOptions.Singleline);
        Assert.True(main.Success, "site/terms.html has no <main>");
        var blocks = new List<string>();
        foreach (Match block in Regex.Matches(main.Groups[1].Value, @"<(h1|h2|p|li)\b([^>]*)>(.*?)</\1>", RegexOptions.Singleline))
        {
            if (block.Groups[1].Value == "h1" || Regex.IsMatch(block.Groups[2].Value, @"\bclass=""updated""")) continue; // the title and the date line, ignored
            blocks.Add(Plain(WebUtility.HtmlDecode(Regex.Replace(block.Groups[3].Value, "<[^>]+>", ""))));
        }
        return blocks;
    }

    [Fact]
    public void The_installers_Terms_say_what_the_websites_Terms_say_block_for_block()
    {
        var installer = InstallerBlocks();
        var site = SiteBlocks();

        // Not vacuous: all sixteen numbered sections were read from both copies, in order.
        foreach (var blocks in new[] { installer, site })
        {
            var headings = blocks.Where(b => Regex.IsMatch(b, @"^\d+\. ")).Select(b => int.Parse(Regex.Match(b, @"^\d+").Value)).ToList();
            Assert.Equal(Enumerable.Range(1, 16), headings);
            Assert.True(blocks.Count > 80, $"only {blocks.Count} blocks were read");
        }

        for (var i = 0; i < Math.Min(installer.Count, site.Count); i++)
            Assert.True(installer[i] == site[i],
                $"block {i + 1} differs.{Environment.NewLine}packaging/TERMS.txt: {installer[i]}{Environment.NewLine}site/terms.html:     {site[i]}");
        if (installer.Count != site.Count)
            Assert.Fail($"packaging/TERMS.txt has {installer.Count} blocks and site/terms.html has {site.Count}; the first block only one of them has is: "
                + (installer.Count > site.Count ? installer[site.Count] : site[installer.Count]));
    }
}
