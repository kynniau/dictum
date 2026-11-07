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
    private const string NotWordBefore = @"(?<![\p{L}\p{N}_])", NotWordAfter = @"(?![\p{L}\p{N}_])";
    private static readonly string LabelPattern = BuildLabel(SpeakerText.APPROVED_Other);
    private static readonly Regex Label = new(NotWordBefore + LabelPattern + NotWordAfter, RegexOptions.CultureInvariant);

    /// <summary>The speakers as a cloud engine may see them: the same turns, every name removed, so each voice is "Other n".</summary>
    public static MeetingSpeakers? WithoutNames(MeetingSpeakers? speakers) =>
        speakers is null ? null : speakers with { Names = new Dictionary<string, string>() };

    /// <summary>
    /// The reply with each whole "Other n" label of a named speaker, written exactly as <see cref="MeetingSpeakers.Label"/> writes it
    /// ("Other 02" is not speaker 2), replaced by that name, in one pass (a name that reads like a label is never replaced again).
    /// Text inside a quotation is never changed: quotes are verbatim transcript and the validator checks them, with their
    /// [mm:ss Channel] citation, against the spoken words. Names go in literally, as typed.
    /// </summary>
    public static string Restore(string text, MeetingSpeakers? speakers)
    {
        if (speakers is null || speakers.Names.Count == 0 || text.Length == 0) return text;
        // Per line, as the validator reads quotes per line: an unpaired quote mark cannot shield the lines after it.
        return string.Join("\n", text.Split('\n').Select(line => OutsideQuotes(line, part => Label.Replace(part, match =>
            OwnSpeaker(match, speakers) is { } speaker && speakers.TypedName(speaker) is { } name ? name : match.Value))));
    }

    /// <summary>
    /// For the validator's figure rule only (never stored or shown): one line with each whole label of this meeting's own speakers,
    /// "Other 1" to "Other {Count}" exactly as <see cref="MeetingSpeakers.Label"/> writes it, and each whole name typed for them,
    /// replaced by a digit-free word. Any other number ("Other 7" in a three-speaker meeting, "Other 02", "R22" when the name is
    /// "R2") and any text inside a quotation stay as they are, so they are still checked. Without a speakers file nothing is masked.
    /// </summary>
    public static string MaskLabels(string line, MeetingSpeakers? speakers)
    {
        if (speakers is null || speakers.Count == 0 || line.Length == 0) return line;
        var matcher = Mask(speakers);
        return OutsideQuotes(line, part => matcher.Replace(part, match =>
            match.Groups["name"].Success || OwnSpeaker(match, speakers) is not null ? MaskedLabel : match.Value));
    }

    // Not a figure-rule trigger and contains no digit; the validator reads it, nobody sees it.
    private const string MaskedLabel = "Speaker";

    // One matcher: this meeting's typed names (longest first, as typed) or a label. A typed name is masked only when the only figure
    // triggers in it are digits ("R2"): a name that is itself a figure word ("June", "May Lee") stays visible to the figure rule,
    // because masking it would also hide that month wherever it appears. A name must also start with a letter: one that starts with a
    // digit ("40", "1.5", "500k", "007") would mask that number wherever the summary states it. Either kind fails closed: the line
    // is refused, as before.
    private static readonly Regex StartsWithLetter = new(@"^\p{L}", RegexOptions.CultureInvariant);

    private static Regex Mask(MeetingSpeakers speakers)
    {
        var names = speakers.TypedNames.Distinct(StringComparer.Ordinal)
            .Where(name => StartsWithLetter.IsMatch(name))
            .Where(name => SummaryValidator.FigureTrigger.Matches(name).All(m => m.Length == 1 && char.IsNumber(m.Value[0])))
            .OrderByDescending(name => name.Length).Select(Regex.Escape).ToList();
        var alternatives = names.Count == 0 ? LabelPattern : "(?<name>" + string.Join("|", names) + ")|" + LabelPattern;
        return new Regex(NotWordBefore + "(?:" + alternatives + ")" + NotWordAfter, RegexOptions.CultureInvariant);
    }

    // The speaker a label names: 1 to Count, digits written exactly as MeetingSpeakers.Label writes them (no leading zero).
    private static int? OwnSpeaker(Match label, MeetingSpeakers speakers)
    {
        var digits = label.Groups["n"].Value;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var speaker) && speaker >= 1 &&
            speaker <= speakers.Count && MeetingSpeakers.OtherLabel(speaker) == label.Value ? speaker : null;
    }

    private static string OutsideQuotes(string line, Func<string, string> change)
    {
        var result = new StringBuilder(line.Length);
        var position = 0;
        foreach (Match quote in SummaryValidator.QuoteSpan.Matches(line))
        {
            result.Append(change(line[position..quote.Index])).Append(quote.Value);
            position = quote.Index + quote.Length;
        }
        return result.Append(change(line[position..])).ToString();
    }

    private static string BuildLabel(string format)
    {
        var slot = format.IndexOf("{0}", StringComparison.Ordinal);
        if (slot < 0 || format.IndexOf("{0}", slot + 3, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The speaker label must contain the speaker number once.");
        return Regex.Escape(format[..slot]) + "(?<n>[0-9]+)" + Regex.Escape(format[(slot + 3)..]);
    }
}
