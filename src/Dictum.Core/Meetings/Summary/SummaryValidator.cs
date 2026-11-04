using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeetingSummary = Dictum.Meetings.Summary;

namespace Dictum.Meetings.Summaries;

public static class SummaryValidator
{
    private const string Number = @"\p{Nd}+(?:[,\u00a0 ]\p{Nd}{3})*(?:\.\p{Nd}+)?";
    private const string Month = @"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";
    private static readonly Regex Figures = new(
        @"(?<![\p{L}\p{Nd}])(?:" +
        @"\p{Nd}{1,4}[-/.]\p{Nd}{1,2}(?:[-/.]\p{Nd}{1,4})?|" +
        @"\p{Nd}{1,2}(?:st|nd|rd|th)?\s+" + Month + @"\b(?:\s*,?\s*\p{Nd}{4})?|" +
        Month + @"\s+\p{Nd}{1,2}(?:st|nd|rd|th)?(?:\s*,?\s*\p{Nd}{4})?|" +
        @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)|" +
        @"[+-]?\p{Sc}\s*[+-]?" + Number + @"|[+-]?" + Number + @"\s*(?:%|percent\b|per cent\b|\p{Sc})" +
        @")(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+(?:['’\-][\p{L}\p{Nd}]+)*", RegexOptions.CultureInvariant);

    public static (MeetingSummary? Ok, string? Rejection) Validate(string reply,
        IReadOnlyList<Segment> transcript, SummaryTemplate template, string engine)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, "The response was empty.");
        // Segment separators are hard boundaries: a figure or owner cannot be assembled across speakers.
        var figures = transcript.SelectMany(s => Figures.Matches(NormaliseDigits(s.Text)).Cast<Match>())
            .Select(m => NormaliseFigure(m.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match figure in Figures.Matches(NormaliseDigits(reply)))
            if (!figures.Contains(NormaliseFigure(figure.Value)))
                return (null, "A figure or date was not present in the transcript.");

        var items = new List<ActionItem>();
        var inActions = false;
        foreach (var rawLine in reply.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#').Trim().TrimEnd('#').Trim();
                inActions = Regex.IsMatch(heading, @"^(?:action(?: items|s)?|next steps|tasks)\b", RegexOptions.IgnoreCase);
                continue;
            }
            if (line.Length == 0) continue;
            var bullet = Regex.Match(line, @"^(?:[-*+] |\d+[.)]\s+)(?:\[[ xX]\]\s*)?(.*)$");
            if (!inActions && !bullet.Success) continue;
            var content = bullet.Success ? bullet.Groups[1].Value : line;
            if (inActions && Regex.IsMatch(content, @"^(?:none\.?|no action items\.?)$", RegexOptions.IgnoreCase)) continue;
            var action = Regex.Match(content, @"^(?:\*\*)?(?<owner>[^:]+?)(?:\*\*)?:(?:\*\*)?\s+(?<text>.+)$");
            if (!action.Success)
            {
                if (inActions) return (null, "An action item must use Owner: action form.");
                continue;
            }
            var owner = action.Groups["owner"].Value.Trim().Trim('*');
            if (owner != "Others" && !transcript.Any(s => ContainsWordSequence(s.Text, owner)))
                return (null, "An action-item owner was not present in the transcript.");
            items.Add(new(owner, action.Groups["text"].Value));
        }
        // simplification: validation checks figures and named owners, not semantic entailment; retire when a measured claim verifier is available.
        return (new MeetingSummary(reply, engine, template.Id, DateTimeOffset.UtcNow, items), null);
    }

    private static bool ContainsWordSequence(string text, string owner)
    {
        var name = Words.Matches(owner).Select(m => m.Value).ToArray();
        if (name.Length == 0 || !Regex.IsMatch(owner, @"^[\p{L}\p{Nd}\s.'’\-]+$")) return false;
        var words = Words.Matches(text).Select(m => m.Value).ToArray();
        for (var i = 0; i <= words.Length - name.Length; i++)
            if (words.Skip(i).Take(name.Length).SequenceEqual(name, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string NormaliseDigits(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
                result.Append((int)Rune.GetNumericValue(rune));
            else result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static string NormaliseFigure(string figure)
    {
        // Preserve decimal points, signs, date separators and currency: $10 is never matched inside $100.
        var normal = Regex.Replace(figure, @"\s+", " ").Trim();
        normal = Regex.Replace(normal, @"(?<=\d)[, ](?=\d{3}(?:\D|$))", "");
        normal = Regex.Replace(normal, @"(?<=\p{Sc})\s+|\s+(?=[%\p{Sc}])", "");
        return normal;
    }
}
