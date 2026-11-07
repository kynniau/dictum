using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Meetings.Speakers;

namespace Dictum.Meetings.Summaries;

/// <summary>
/// Owner decision (Kyle, 07/11/2026): names typed for speakers never reach a summary engine outside this computer. For any engine
/// other than the in-process local model, the chunks carry "Other n" labels only; when the reply comes back, Dictum puts each
/// named speaker's name in place of its label before the summary is checked, stored and shown.
/// </summary>
internal static class SpeakerNameSwap
{
    // "Other {0}" today; built from the label text so a reworded label still round-trips. A whole label only: not inside a longer
    // word or number ("Other 1" never matches in "Other 10"), and "Others" (the channel in a citation) never matches.
    private static readonly Regex Label = BuildLabel(SpeakerText.PROPOSED_Other);

    /// <summary>The speakers as a cloud engine may see them: the same turns, every name removed, so each voice is "Other n".</summary>
    public static MeetingSpeakers? WithoutNames(MeetingSpeakers? speakers) =>
        speakers is null ? null : speakers with { Names = new Dictionary<string, string>() };

    /// <summary>
    /// The reply with each whole "Other n" label of a named speaker replaced by that name, in one pass (a name that reads like a
    /// label is never replaced again). Text inside a quotation is never changed: quotes are verbatim transcript and the validator
    /// checks them, with their [mm:ss Channel] citation, against the spoken words. Names go in literally, as typed.
    /// </summary>
    public static string Restore(string text, MeetingSpeakers? speakers)
    {
        if (speakers is null || speakers.Names.Count == 0 || text.Length == 0) return text;
        // Per line, as the validator reads quotes per line: an unpaired quote mark cannot shield the lines after it.
        return string.Join("\n", text.Split('\n').Select(line => RestoreLine(line, speakers)));
    }

    private static string RestoreLine(string line, MeetingSpeakers speakers)
    {
        var result = new StringBuilder(line.Length);
        var position = 0;
        foreach (Match quote in SummaryValidator.QuoteSpan.Matches(line))
        {
            result.Append(Swap(line[position..quote.Index], speakers)).Append(quote.Value);
            position = quote.Index + quote.Length;
        }
        return result.Append(Swap(line[position..], speakers)).ToString();
    }

    private static string Swap(string text, MeetingSpeakers speakers) => Label.Replace(text, match =>
        int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var speaker) &&
        speakers.Names.TryGetValue(speaker.ToString(CultureInfo.InvariantCulture), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim() : match.Value);

    private static Regex BuildLabel(string format)
    {
        var slot = format.IndexOf("{0}", StringComparison.Ordinal);
        if (slot < 0 || format.IndexOf("{0}", slot + 3, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The speaker label must contain the speaker number once.");
        return new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(format[..slot]) + "(?<n>[0-9]+)" + Regex.Escape(format[(slot + 3)..]) +
            @"(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant);
    }
}
