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
    private const string GitHubLine = """Source code and release files are on <a href="https://github.com/kynniau/dictum">GitHub</a>.""";
    private static readonly string[] Pages = ["index.html", "help.html", "privacy.html"];

    /// <summary>A page of the site without its HTML comments.</summary>
    private static string VisibleHtml(string page) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page)), "<!--.*?-->", "", RegexOptions.Singleline);

    private static List<string> ButtonLinks(string html) =>
        Regex.Matches(html, "<a class=\"btn[^\"]*\" href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Every_button_on_the_landing_page_is_the_Microsoft_Store_and_the_first_is_in_the_hero()
    {
        var html = VisibleHtml("index.html");
        var buttons = ButtonLinks(html);
        Assert.NotEmpty(buttons);
        Assert.All(buttons, href => Assert.Equal(StoreUrl, href));
        var hero = html[html.IndexOf("<section class=\"hero\"", StringComparison.Ordinal)..];
        hero = hero[..hero.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains($"href=\"{StoreUrl}\"", hero);
    }

    [Theory]
    [InlineData("SmartScreen")]
    [InlineData("code-signed")]
    [InlineData("Unknown Publisher")]
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

    [Fact]
    public void The_landing_page_has_one_quiet_GitHub_line_in_its_footer()
    {
        var html = VisibleHtml("index.html");
        var footer = html[html.IndexOf("<footer", StringComparison.Ordinal)..];
        Assert.Contains(GitHubLine, footer);
        Assert.Single(Regex.Matches(html, Regex.Escape(GitHubLine)));
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

    [Fact]
    public void Every_same_site_anchor_lands_on_an_id_that_exists()
    {
        var ids = Pages.ToDictionary(p => p, p => Regex.Matches(VisibleHtml(p), "\\sid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet());
        var route = new Dictionary<string, string> { ["/"] = "index.html", ["/help"] = "help.html", ["/privacy"] = "privacy.html" };
        foreach (var page in Pages)
            foreach (Match link in Regex.Matches(VisibleHtml(page), "href=\"(/[a-z]*)?#([^\"]+)\""))
            {
                var target = link.Groups[1].Success && link.Groups[1].Value.Length > 0 ? route[link.Groups[1].Value] : page;
                Assert.True(ids[target].Contains(link.Groups[2].Value), $"site/{page} links to {link.Value}, which site/{target} does not have");
            }
    }
}
