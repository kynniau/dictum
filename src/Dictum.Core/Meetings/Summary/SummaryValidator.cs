using System.Globalization;
using System.Text.RegularExpressions;
using MeetingSummary = Dictum.Meetings.Summary;

namespace Dictum.Meetings.Summaries;

public static class SummaryValidator
{
    // Detect triggers, not a list of permitted formats. Unknown notation containing
    // a trigger is retained, so an unfamiliar format cannot evade validation.
    private const string NumberWords = "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty thirty forty fifty sixty seventy eighty ninety";
    private const string OrdinalWords = "first second third fourth fifth sixth seventh eighth ninth tenth eleventh twelfth thirteenth fourteenth fifteenth sixteenth seventeenth eighteenth nineteenth twentieth twenty-first twenty-second twenty-third twenty-fourth twenty-fifth twenty-sixth twenty-seventh twenty-eighth twenty-ninth thirtieth thirty-first";
    private static readonly string CurrencyCodes = string.Join("|", CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name).ISOCurrencySymbol).Distinct());
    private static readonly Regex Html = new(@"<[\p{L}/]|&(?:[a-z][a-z0-9]*|\#[^;\s]*);",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FigureTrigger = new(
        @"[\p{N}\p{Sc}%‰‱]|\b(?:" + CurrencyCodes + "|" + NumberWords.Replace(" ", "|") + "|" + OrdinalWords.Replace(" ", "|") +
        @"|hundred|thousand|million|billion|trillion|half|quarter|AUD|USD|NZD|EUR|GBP|CAD|CHF|JPY|CNY|HKD|SGD|INR|KRW|ZAR|BRL|MXN|SEK|NOK|DKK|RUB|AED|SAR|k|m|bn|mn|percent|per|cent|" +
        @"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?|" +
        @"Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|dollars?|cents?|pounds?|euros?|yen|rupees?|pesos?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+(?:['’\-][\p{L}\p{Nd}]+)*", RegexOptions.CultureInvariant);

    public static (MeetingSummary? Ok, string? Rejection) Validate(string reply,
        IReadOnlyList<Segment> transcript, SummaryTemplate template, string engine)
    {
        if (Html.IsMatch(reply)) return (null, "A figure or date was not present in the transcript.");
        if (string.IsNullOrWhiteSpace(reply)) return (null, "The response was empty.");
        var rawLines = reply.Split('\n');
        var lines = rawLines.Select(StripMarkdown).ToArray();
        // Removing an interior marker must not turn arithmetic into an amount.
        if (Regex.IsMatch(reply, @"\p{N}[*_`]+\p{N}"))
            return (null, "A figure or date was not present in the transcript.");
        // Segment separators are hard boundaries: a figure or owner cannot be assembled across speakers.
        var spoken = transcript.Select(s => NormaliseFigure(s.Text)).ToArray();
        var summaryText = string.Join("\n", lines);
        foreach (var figure in FigurePhrases(summaryText))
            if (!spoken.Any(segment => segment.Contains(figure, StringComparison.Ordinal)))
                return (null, "A figure or date was not present in the transcript.");
        var spokenCores = transcript.SelectMany(s => FigureCores(s.Text)).ToHashSet(StringComparer.Ordinal);
        foreach (var core in FigureCores(summaryText))
            if (!spokenCores.Contains(core))
                return (null, "A figure or date was not present in the transcript.");
        // A trailing rate connector omits its unit even though the amount core matches.
        if (Regex.Matches(summaryText, @"(?<amount>\S+)\s+per\s*(?:[.!?;:]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Any(match => FigureTrigger.IsMatch(match.Groups["amount"].Value)))
            return (null, "A figure or date was not present in the transcript.");

        var items = new List<ActionItem>();
        var inActions = false;
        var actionHeadingLevel = 0;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
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
            if (line.Length == 0)
            {
                if (inActions && !string.IsNullOrWhiteSpace(rawLines[lineIndex]))
                    return (null, "An action item must use Owner: action form.");
                continue;
            }
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

    private static string StripMarkdown(string line)
    {
        // Retain the list structure when an asterisk is a bullet rather than emphasis.
        var content = Regex.Replace(line, @"^(\s*)\*\s+", "$1- ");
        return Regex.Replace(content, @"[*_`]+", "");
    }

    private static IEnumerable<string> FigurePhrases(string text)
    {
        var pieces = Regex.Matches(text, @"\S+").Select(m => m.Value).ToArray();
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!FigureTrigger.IsMatch(pieces[i])) continue;
            var start = i > 0 && Regex.IsMatch(pieces[i - 1], @"^(?:[-−–+±]|minus|negative)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? i - 1 : i;
            var phrase = new List<string>();
            for (var j = start; j < pieces.Length && j <= i + 3; j++)
            {
                var piece = pieces[j];
                // Sentence punctuation ends lookahead. A point touching a numeric
                // character may be a decimal point, including a leading or trailing one.
                var boundary = Regex.Match(piece, @"(?<!\p{N})\.(?!\p{N})|[!?;:]");
                if (boundary.Success)
                {
                    // Never discard a trigger following punctuation in the same word.
                    if (FigureTrigger.IsMatch(piece[boundary.Index..])) phrase.Add(piece);
                    else if (boundary.Index > 0) phrase.Add(piece[..boundary.Index]);
                    break;
                }
                phrase.Add(piece);
            }
            yield return NormaliseFigure(string.Join(" ", phrase));
        }
    }

    private static IEnumerable<string> FigureCores(string text)
    {
        var pieces = Regex.Matches(NormaliseFigure(text), @"\S+").Select(m => m.Value).ToArray();
        var run = new List<string>();
        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i].TrimEnd('.', ',', '!', '?', ';', ':');
            // "per" is a trigger only as part of "per cent"; rate units are
            // still protected by the phrase check.
            var isTrigger = FigureTrigger.IsMatch(piece) &&
                (piece != "per" || i + 1 < pieces.Length && pieces[i + 1].TrimEnd('.', ',', '!', '?', ';', ':') == "cent");
            if (isTrigger)
            {
                if (run.Count == 0 && i > 0 && Regex.IsMatch(pieces[i - 1], @"^(?:[-−–+±]|minus|negative)$"))
                    run.Add(pieces[i - 1]);
                run.Add(piece);
            }
            if ((!isTrigger || Regex.IsMatch(pieces[i], @"[.!?;:]$")) && run.Count > 0)
            {
                yield return NormaliseFigure(string.Join(" ", run));
                run.Clear();
            }
        }
        if (run.Count > 0) yield return NormaliseFigure(string.Join(" ", run));
    }

    private static string NormaliseFigure(string figure)
    {
        var normal = figure.ToLowerInvariant();
        normal = Regex.Replace(normal, @"(?<=\p{Nd})[, \u00a0](?=\p{Nd}{3}(?:[^\p{Nd}]|$))", "");
        return Regex.Replace(normal, @"\s+", " ").Trim();
    }
}
