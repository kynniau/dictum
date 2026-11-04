using System.Reflection;
using System.Text.RegularExpressions;
using Dictum.UI.Views.Meetings;

namespace Dictum.Tests;

public sealed class MeetingsApprovedWordingTests
{
    [Fact]
    public void Every_recommended_final_matches_the_approved_document_exactly()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".demo-preview", "approve-meetings-wording.md"))) root = root.Parent;
        Assert.NotNull(root);
        var document = File.ReadAllText(Path.Combine(root.FullName, ".demo-preview", "approve-meetings-wording.md")).Replace("\r\n", "\n");
        var privacy = File.ReadAllText(Path.Combine(root.FullName, ".demo-preview", "meetings-privacy-section.md")).Replace("\r\n", "\n");
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
}
