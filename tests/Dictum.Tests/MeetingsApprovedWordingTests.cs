using System.Reflection;
using System.Text.RegularExpressions;
using Dictum.UI.Views.Meetings;

namespace Dictum.Tests;

public sealed class MeetingsApprovedWordingTests
{
    [Fact]
    public void Every_recommended_final_matches_the_approved_document_exactly()
    {
        // The fixtures hold only the approved final texts; the drafting record stays out of the repository.
        var fixtures = Path.Combine(FixBatchDownloadTests.RepoRoot(), "tests", "Dictum.Tests", "Fixtures", "Meetings");
        var document = File.ReadAllText(Path.Combine(fixtures, "approved-wording.md")).Replace("\r\n", "\n");
        var privacy = File.ReadAllText(Path.Combine(fixtures, "privacy-section.md")).Replace("\r\n", "\n");
        var values = typeof(MeetingsText).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.Name.StartsWith("APPROVED_", StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!).ToArray();
        var sections = Regex.Matches(document, @"(?ms)^#{2,3} (?<id>\d[a-z](?: - [^\n]+|\.[^\n]*))\n(?<body>.*?)(?=^#{2,3} \d[a-z]|\z)");
        Assert.Equal(57, sections.Count); // 55 numbered texts and two supplied variants.
        foreach (Match section in sections)
        {
            var recommended = Regex.Match(section.Groups["body"].Value, @"Recommended final: PROPOSED\n\n(?:```text\n(?<text>[\s\S]*?)\n```|(?<text>[^\n]+))");
            Assert.True(recommended.Success, section.Groups["id"].Value);
            var wording = recommended.Groups["text"].Value;
            if (section.Groups["id"].Value.StartsWith("7", StringComparison.Ordinal)) Assert.Contains(wording, privacy);
            else Assert.Contains(wording, values);
        }
        Assert.DoesNotContain("What Kynni receives.", privacy);
        Assert.DoesNotContain("PLACEHOLDER", string.Join("\n", values));
    }

    [Fact]
    public void The_privacy_policy_carries_every_approved_meetings_paragraph_word_for_word()
    {
        var fixtures = Path.Combine(FixBatchDownloadTests.RepoRoot(), "tests", "Dictum.Tests", "Fixtures", "Meetings");
        var approved = File.ReadAllText(Path.Combine(fixtures, "privacy-section.md")).Replace("\r\n", "\n");
        var html = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "privacy.html")).Replace("\r\n", "\n");
        var paragraphs = approved.Split("\n\n").Select(block => block.Trim())
            .Where(block => block.Length > 0 && !block.StartsWith("<!--", StringComparison.Ordinal) && !block.StartsWith('#')).ToArray();
        Assert.Equal(19, paragraphs.Length);
        var heading = html.IndexOf("<h3 id=\"meetings\">Meetings</h3>", StringComparison.Ordinal);
        Assert.True(heading > html.IndexOf("<h2 id=\"where\">", StringComparison.Ordinal) && heading < html.IndexOf("<h2 id=\"network\">", StringComparison.Ordinal),
            "the Meetings section sits inside section 3");
        foreach (var paragraph in paragraphs)
            Assert.Contains("<p>" + paragraph.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") + "</p>", html, StringComparison.Ordinal);
    }
}
