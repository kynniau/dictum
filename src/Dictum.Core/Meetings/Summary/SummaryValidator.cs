using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeetingSummary = Dictum.Meetings.Summary;

namespace Dictum.Meetings.Summaries;

public static class SummaryValidator
{
    // Detect triggers, not a list of permitted formats. Unknown notation containing
    // a trigger is retained, so an unfamiliar format cannot evade validation.
    private const string NumberWords = "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty thirty forty fifty sixty seventy eighty ninety";
    private static readonly Dictionary<string, string> SimpleNumbers = NumberWords.Split(' ')
        .Select((word, index) => (word, value: index < 20 ? index : (index - 18) * 10))
        .ToDictionary(pair => pair.word, pair => pair.value.ToString(CultureInfo.InvariantCulture));
    private static readonly Regex FigureTrigger = new(
        @"[\p{Nd}\p{Sc}%]|\b(?:" + NumberWords.Replace(" ", "|") +
        @"|hundred|thousand|million|billion|trillion|half|quarter|AUD|USD|NZD|EUR|GBP|k|m|bn|mn|percent|per|cent|" +
        @"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?|" +
        @"Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+(?:['’\-][\p{L}\p{Nd}]+)*", RegexOptions.CultureInvariant);

    public static (MeetingSummary? Ok, string? Rejection) Validate(string reply,
        IReadOnlyList<Segment> transcript, SummaryTemplate template, string engine)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, "The response was empty.");
        // Segment separators are hard boundaries: a figure or owner cannot be assembled across speakers.
        var figures = transcript.SelectMany(s => FigureKeys(s.Text)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var figure in FigureKeys(reply))
            if (!figures.Contains(figure))
                return (null, "A figure or date was not present in the transcript.");

        var items = new List<ActionItem>();
        var inActions = false;
        var actionHeadingLevel = 0;
        foreach (var rawLine in reply.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#'))
            {
                var level = line.TakeWhile(c => c == '#').Count();
                if (inActions && level > actionHeadingLevel)
                    return (null, "An action item must use Owner: action form.");
                var heading = line.TrimStart('#').Trim().TrimEnd('#').Trim();
                inActions = Regex.IsMatch(heading, @"^(?:action(?: items|s)?|next steps|tasks)\b", RegexOptions.IgnoreCase);
                if (inActions) actionHeadingLevel = level;
                continue;
            }
            if (line.Length == 0) continue;
            var bullet = Regex.Match(line, @"^(?:[-*+] |\d+[.)]\s+)(?:\[[ xX]\]\s*)?(.*)$");
            if (!inActions && !bullet.Success) continue;
            if (inActions && !bullet.Success)
                return (null, "An action item must use Owner: action form.");
            var content = bullet.Success ? bullet.Groups[1].Value : line;
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

    private static IEnumerable<string> FigureKeys(string text)
    {
        // Only transcript segments are hard boundaries. A line wrap must not
        // detach a magnitude or unit. Include the next unknown word as a possible
        // unit, conservatively (e.g. kg).
        var run = new List<string>();
        foreach (Match match in Regex.Matches(NormaliseDigits(text), @"\S+"))
        {
            // Strip only outer Markdown markers. Interior punctuation may be
            // arithmetic, and parentheses may denote a negative amount.
            var piece = match.Value.Trim('*', '`', '_', '#')
                .Trim('.', ',', ';', ':', '!', '?');
            if (piece.Length == 0) continue;
            if (FigureTrigger.IsMatch(piece)) run.Add(piece);
            else if (run.Count > 0)
            {
                run.Add(piece);
                yield return NormaliseFigure(string.Join(" ", run));
                run.Clear();
            }
        }
        if (run.Count > 0) yield return NormaliseFigure(string.Join(" ", run));
    }

    private static string NormaliseFigure(string figure)
    {
        var normal = figure.ToLowerInvariant();
        normal = Regex.Replace(normal, @"\b[a-z]+\b", match =>
            SimpleNumbers.GetValueOrDefault(match.Value, match.Value));
        normal = Regex.Replace(normal, @"\bper\s+cent\b|\bpercent\b", "%");
        // Keep decimal points, signs, date separators, magnitude and currency
        // position: 1.5%, 1.5$, $1.5 and 1/5 must have distinct keys.
        normal = Regex.Replace(normal, @"(?<=\d)[, \u00a0](?=\d{3}(?:\D|$))", "");
        normal = Regex.Replace(normal, @"(?<=\p{Sc})\s+|\s+(?=[%\p{Sc}])", "");
        return normal;
    }
}
