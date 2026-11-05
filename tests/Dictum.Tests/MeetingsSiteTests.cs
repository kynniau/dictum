using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Dictum.UI.Views.Meetings;

namespace Dictum.Tests;

public sealed class MeetingsSiteTests
{
    private static string Site(string page) => Regex.Replace(
        File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page)),
        "<!--.*?-->", "", RegexOptions.Singleline);

    [Theory]
    [InlineData("index.html")]
    [InlineData("help.html")]
    public void Meetings_links_to_the_approved_privacy_section(string page)
    {
        var html = Site(page);
        var section = Regex.Match(html, "<section[^>]* id=\"meetings\"[^>]*>(.*?)</section>", RegexOptions.Singleline);
        Assert.True(section.Success, $"{page} needs a Meetings section");
        Assert.Contains("href=\"privacy.html#meetings\"", section.Value);
        Assert.Contains("id=\"meetings\"", Site("privacy.html"));
        Assert.DoesNotContain("img/demos/meetings", html); // no images until the demo has been recorded and reviewed
    }

    [Fact]
    public void The_guide_labels_match_the_shipped_Meetings_constants()
    {
        var html = Site("help.html");
        var labels = Regex.Matches(html, "<b data-meetings-label=\"([^\"]+)\">([^<]+)</b>");
        Assert.NotEmpty(labels);
        foreach (Match label in labels)
        {
            var field = typeof(MeetingsText).GetField(label.Groups[1].Value, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(field);
            Assert.Equal(field.GetRawConstantValue(), WebUtility.HtmlDecode(label.Groups[2].Value));
        }

        // Cover every flow, so removing a label and its annotation cannot silently pass.
        string[] required =
        [
            nameof(MeetingsText.PROPOSED_Enable), nameof(MeetingsText.APPROVED_EnableConfirm),
            nameof(MeetingsText.PROPOSED_Existing), nameof(MeetingsText.PROPOSED_Start),
            nameof(MeetingsText.APPROVED_Before), nameof(MeetingsText.APPROVED_How),
            nameof(MeetingsText.APPROVED_Spoken), nameof(MeetingsText.APPROVED_Written), nameof(MeetingsText.APPROVED_Start),
            nameof(MeetingsText.PROPOSED_Joined), nameof(MeetingsText.APPROVED_Continue), nameof(MeetingsText.APPROVED_Refused),
            nameof(MeetingsText.PROPOSED_Notes), nameof(MeetingsText.PROPOSED_SaveNotes), nameof(MeetingsText.PROPOSED_Discard),
            nameof(MeetingsText.PROPOSED_Pause), nameof(MeetingsText.PROPOSED_Resume), nameof(MeetingsText.PROPOSED_Mark),
            nameof(MeetingsText.PROPOSED_Live), nameof(MeetingsText.PROPOSED_Stop), nameof(MeetingsText.PROPOSED_Transcript),
            nameof(MeetingsText.PROPOSED_Summary), nameof(MeetingsText.PROPOSED_Template), nameof(MeetingsText.PROPOSED_Regenerate),
            nameof(MeetingsText.PROPOSED_Import), nameof(MeetingsText.APPROVED_EnableImportsTitle),
            nameof(MeetingsText.APPROVED_EnableImports), nameof(MeetingsText.APPROVED_BeforeImport), nameof(MeetingsText.APPROVED_Import),
            nameof(MeetingsText.PROPOSED_Copy), nameof(MeetingsText.PROPOSED_Export), nameof(MeetingsText.APPROVED_ShareContinue),
            nameof(MeetingsText.PROPOSED_Retention), nameof(MeetingsText.PROPOSED_Forever),
            nameof(MeetingsText.PROPOSED_Days30), nameof(MeetingsText.PROPOSED_Days90),
            nameof(MeetingsText.PROPOSED_Delete), nameof(MeetingsText.APPROVED_Cancel)
        ];
        foreach (var name in required)
            Assert.Contains(labels, label => label.Groups[1].Value == name);
        Assert.Contains("href=\"#meetings\"", html);
        Assert.Contains("href=\"help.html#meetings\"", Site("index.html"));
    }

    [Fact]
    public void The_guide_reuses_complete_approved_agreement_and_sharing_texts()
    {
        var html = Site("help.html");
        var prose = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        prose = Regex.Replace(prose, @"\s+", " ");
        foreach (var text in new[]
        {
            MeetingsText.APPROVED_Enable, MeetingsText.APPROVED_Disclaimer, MeetingsText.APPROVED_Agreement,
            MeetingsText.APPROVED_Uses, MeetingsText.APPROVED_ConfirmationNote, MeetingsText.APPROVED_Joined,
            MeetingsText.APPROVED_RefusalNotes, MeetingsText.APPROVED_EnableImportsBody,
            MeetingsText.APPROVED_ImportAgreement, MeetingsText.APPROVED_ShareReminder
        })
            Assert.Contains(Regex.Replace(text, @"\s+", " "), prose, StringComparison.Ordinal);

        // Team sharing is unavailable in MeetingsHost. Reuse the other approved paragraphs unchanged.
        foreach (var paragraph in MeetingsText.APPROVED_EngineNote.Split("\n\n").SkipLast(1))
            Assert.Contains(paragraph, prose, StringComparison.Ordinal);
    }
}
