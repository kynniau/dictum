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
    private const string OrdinalWords = "first second third fourth fifth sixth seventh eighth ninth tenth eleventh twelfth thirteenth fourteenth fifteenth sixteenth seventeenth eighteenth nineteenth twentieth twenty-first twenty-second twenty-third twenty-fourth twenty-fifth twenty-sixth twenty-seventh twenty-eighth twenty-ninth thirtieth thirty-first";
    private const string QuantityWords = "dozen dozens score gross pair couple twice thrice double triple quadruple half halves quarter quarters single once several multiple " +
        "doubled doubles doubling tripled triples tripling quadrupled quadruples quadrupling halved halving [a-z]+fold";
    private static readonly string CurrencyCodes = string.Join("|", CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name).ISOCurrencySymbol).Distinct());
    private static readonly Regex Html = new(@"<[\p{L}/]|&(?:[a-z][a-z0-9]*|\#[^;\s]*);",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static readonly Regex FigureTrigger = new(
        @"[\p{N}\p{Sc}%‰‱]|\b(?:" + CurrencyCodes + "|" + NumberWords.Replace(" ", "|") + "|" + OrdinalWords.Replace(" ", "|") +
        "|" + QuantityWords.Replace(" ", "|") +
        @"|hundred|thousand|million|billion|trillion|AUD|USD|NZD|EUR|GBP|CAD|CHF|JPY|CNY|HKD|SGD|INR|KRW|ZAR|BRL|MXN|SEK|NOK|DKK|RUB|AED|SAR|k|m|bn|mn|percent|per|cent|" +
        @"Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?|" +
        @"Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|dollars?|cents?|pounds?|euros?|yen|rupees?|pesos?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Words = new(@"[\p{L}\p{Nd}]+(?:['\u2019\-][\p{L}\p{Nd}]+)*", RegexOptions.CultureInvariant);

    private const string FigureRejection = "A figure or date was not present in the transcript.";
    private static readonly Regex Quotation = new(
        """(?:"(?<text>[^"]+)"|\u201c(?<text>[^\u201c\u201d]+)\u201d)[ \t]+\[(?<time>(?:[0-9]+:[0-5][0-9]|[0-9]{2,}):[0-5][0-9]) (?<channel>You|Others|Mixed)\]""",
        RegexOptions.CultureInvariant);
    internal static readonly Regex QuoteSpan = new("\"[^\"]*\"|\u201c[^\u201c\u201d]*\u201d", RegexOptions.CultureInvariant);
    // A contraction stays one word, so "didn't" is seen whole as a negation.
    private static readonly Regex ContextWords = new(@"[\p{L}\p{N}]+(?:['\u2019][\p{L}]+)*|[-\u2013\u2212]", RegexOptions.CultureInvariant);
    private static readonly Regex Qualifier = new(
        // A qualifier keeps its meaning with a contracted suffix ("maximum's", "there're"), so the suffix is allowed.
        @"^(?:(?:to|through|-|and|or|per|not|no|never|none|cannot|without|up|at|least|most|about|around|approximately|roughly|under|over|below|above|less|more|than|between|nearly|almost|only|maximum|minimum|max|min)(?:['\u2019]\p{L}+)?|\p{L}+n['\u2019]t)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Continuation = new(
        @"[\p{N}\p{Sc}%\u2030\u2031]|\b(?:kg|mg|g|km|cm|mm|metres?|meters?|miles?|litres?|liters?|hours?|minutes?|seconds?|days?|weeks?|months?|years?|millions?|billions?|trillions?|thousands?|hundreds?|bps|basis|points?|degrees?|ounces?|tonnes?|tons?|feet|inches|kilograms?|milligrams?|grams?|kilometres?|kilometers?|kilowatts?|watts?|mph|kph|Australian|US|Canadian|New Zealand)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Prefix = new(
        @"[\p{N}\p{Sc}+\u2212\u2013\u00b1-]|\b(?:minus|negative|dollars?|cents?|pounds?|euros?|yen|rupees?|pesos?|" + CurrencyCodes + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="speakers">The meeting's speakers file, if it has one: its own "Other n" labels and typed names are not figures
    /// (see <see cref="SpeakerNameSwap.MaskLabels"/>), and each of them may own an action item. Null masks and admits nothing.</param>
    public static (MeetingSummary? Ok, string? Rejection) Validate(string reply,
        IReadOnlyList<Segment> transcript, SummaryTemplate template, string engine, Speakers.MeetingSpeakers? speakers = null)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, "The response was empty.");
        if (reply.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Format) || HasMixedScriptWord(reply))
            return (null, "Invisible formatting and mixed-script words are not permitted.");
        // Inspect the original characters before any Markdown is removed.
        if (QuoteSpan.Matches(reply).Any(m => m.Value.IndexOfAny(['*', '_', '`', '\\']) >= 0))
            return (null, "Quotes must not contain Markdown markers or escapes.");
        var lines = reply.Split('\n');
        var items = new List<ActionItem>();
        var inActions = false;
        var actionHeadingLevel = 0;
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            if (rawLine.StartsWith("    ") || rawLine.StartsWith('\t') || Html.IsMatch(rawLine) ||
                Regex.IsMatch(rawLine, @"<[^>]*>|&[^;\s]+;|!?\[[^\]]*\]\s*(?:\(|\[)|\[[^\]]+\]:|[|]|\x60{3}|~{3}"))
                return (null, "Use only Markdown headings and list items.");
            var line = rawLine.Trim();
            var heading = Regex.Match(line, @"^#+(.*)$");
            var bullet = Regex.Match(line, @"^(?:[-*+] |[0-9]+[.)] +)(?:\[[ xX]\]\s*)?(.*)$");
            if (!heading.Success && !bullet.Success)
                return (null, "Use only Markdown headings and list items.");
            var originalContent = heading.Success ? heading.Groups[1].Value.Trim().TrimEnd('#').Trim() : bullet.Groups[1].Value;
            var ownerContent = QuoteSpan.Replace(originalContent, match => new string(' ', match.Length));
            var ownerPrefix = bullet.Success ? Regex.Match(ownerContent, @"^(?<owner>.+?):\s+(?<text>.+)$") : Match.Empty;
            if (ownerPrefix.Success && Regex.IsMatch(ownerPrefix.Groups["owner"].Value, "[^" + Speakers.MeetingSpeakers.NameCharacters + "]"))
                return (null, "An action-item owner must not contain markup.");
            var content = StripMarkdown(originalContent);
            var invalidQuote = false;
            // Labels are masked before verified quotes are removed, so text either side of a removed quote cannot join into a label.
            var remainder = Quotation.Replace(SpeakerNameSwap.MaskLabels(content, speakers), match =>
            {
                if (QuoteIsSpoken(match, transcript)) return "";
                invalidQuote = true;
                return match.Value;
            });
            if (invalidQuote || FigureTrigger.IsMatch(remainder)) return (null, FigureRejection);
            if (heading.Success)
            {
                var level = line.TakeWhile(c => c == '#').Count();
                if (inActions && level > actionHeadingLevel)
                    return (null, "An action item must use Owner: action form.");
                inActions = Regex.IsMatch(content, @"^(?:action(?: items|s)?|next steps|tasks)\b", RegexOptions.IgnoreCase);
                if (inActions) actionHeadingLevel = level;
                continue;
            }
            var action = ownerPrefix.Success ? Regex.Match(content, @"^(?<owner>.+?):\s+(?<text>.+)$") : Match.Empty;
            if (!action.Success)
            {
                if (inActions) return (null, "An action item must use Owner: action form.");
                continue;
            }
            var owner = action.Groups["owner"].Value.Trim();
            // Owner decision (Kyle, 07/11/2026): one of this meeting's own speakers may own an action item even if never spoken.
            if (owner != "Others" && speakers?.IsSpeaker(owner) != true && !transcript.Any(s => ContainsWordSequence(s.Text, owner)))
                return (null, "An action-item owner was not present in the transcript.");
            items.Add(new(owner, action.Groups["text"].Value));
        }
        // simplification: quote lookup checks figures and named owners, not semantic entailment.
        return (new MeetingSummary(reply, engine, template.Id, DateTimeOffset.UtcNow, items), null);
    }

    private static bool QuoteIsSpoken(Match quotation, IReadOnlyList<Segment> transcript)
    {
        var parts = quotation.Groups["time"].Value.Split(':');
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var leading)) return false;
        var seconds = parts.Length == 3
            ? leading * 3600.0 + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[2], CultureInfo.InvariantCulture)
            : leading * 60.0 + int.Parse(parts[1], CultureInfo.InvariantCulture);
        var quote = CollapseWhitespace(quotation.Groups["text"].Value);
        if (quote.Length == 0) return false;
        foreach (var segment in transcript)
        {
            if (segment.Channel.ToString() != quotation.Groups["channel"].Value ||
                seconds < Math.Floor(segment.Start.TotalSeconds) || seconds >= Math.Floor(segment.End.TotalSeconds)) continue;
            var spoken = CollapseWhitespace(segment.Text);
            for (var start = spoken.IndexOf(quote, StringComparison.Ordinal); start >= 0;
                 start = spoken.IndexOf(quote, start + 1, StringComparison.Ordinal))
            {
                var end = start + quote.Length;
                if (start > 0 && IsWordCharacter(spoken[start - 1]) ||
                    end < spoken.Length && IsWordCharacter(spoken[end])) continue;
                var before = Regex.Match(spoken[..start], @"\S+\s*$").Value.Trim();
                var after = Regex.Match(spoken[end..], """^[\s,.;:!?()\[\]"\u2019\u201d]*([^\s,.;:!?()\[\]"\u2019\u201d]+)""").Groups[1].Value;
                if (Prefix.IsMatch(before) || FigureTrigger.IsMatch(after) || Continuation.IsMatch(after)) continue;
                if (FigureTrigger.IsMatch(quote) && ContextWords.Matches(spoken[..start]).TakeLast(3)
                    .Concat(ContextWords.Matches(spoken[end..]).Take(3))
                    .Any(m => FigureTrigger.IsMatch(m.Value) || Qualifier.IsMatch(m.Value) || Continuation.IsMatch(m.Value))) continue;
                return true;
            }
        }
        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_' ||
        CharUnicodeInfo.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.OtherNumber;

    private static string CollapseWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static bool ContainsWordSequence(string text, string owner)
    {
        var name = Words.Matches(owner).Select(m => m.Value).ToArray();
        if (name.Length == 0 || !Regex.IsMatch(owner, @"^[\p{L}\p{Nd}\s.'\u2019\-]+$")) return false;
        var words = Words.Matches(text).Select(m => m.Value).ToArray();
        for (var i = 0; i <= words.Length - name.Length; i++)
            if (words.Skip(i).Take(name.Length).SequenceEqual(name, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string StripMarkdown(string line)
    {
        // Quotes are copied verbatim; only text outside them may lose emphasis.
        var result = new StringBuilder();
        var position = 0;
        foreach (Match span in QuoteSpan.Matches(line))
        {
            result.Append(Regex.Replace(line[position..span.Index], @"[*_]+", ""));
            result.Append(span.Value);
            position = span.Index + span.Length;
        }
        return result.Append(Regex.Replace(line[position..], @"[*_]+", "")).ToString();
    }

    private static bool HasMixedScriptWord(string text)
    {
        var scripts = new HashSet<int>();
        foreach (var letter in text.EnumerateRunes())
        {
            if (Rune.IsLetter(letter))
            {
                var script = UnicodeLetterScripts.Get(letter.Value);
                if (script < 0) return true;
                if (script != 0) scripts.Add(script);
                if (scripts.Count > 1) return true;
            }
            else if (Rune.GetUnicodeCategory(letter) is not (UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation))
                scripts.Clear();
        }
        return false;
    }

}
