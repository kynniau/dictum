using System.Globalization;
using System.Text.RegularExpressions;

namespace Dictum.Calibration;

public enum AlignOp { Match, Substitute, Delete }

/// <summary>One script word and what the transcript had in its place (empty when whisper dropped it).</summary>
public sealed record AlignedWord(string Reference, AlignOp Op, string Heard);

/// <param name="Edits">Substitutions + deletions + insertions.</param>
/// <param name="ReferenceWords">Words in the normalised script sentence.</param>
public sealed record WerResult(int Edits, int ReferenceWords, IReadOnlyList<AlignedWord> Alignment)
{
    public double Rate => ReferenceWords == 0 ? 0 : Edits / (double)ReferenceWords;
}

/// <summary>
/// Word error rate (SPEC §16.5): (substitutions + deletions + insertions) / script words, after both texts are
/// normalised the same way: lower case, punctuation stripped, and a small spoken-number → digit step so "twenty" and
/// "20", "fourteenth" and "14th", "four thousand two hundred and fifty dollars" and "$4,250", "ten thirty" and "10:30"
/// all compare equal. An e-mail address written as "accounts@northwind.com.au" compares equal to "accounts at
/// northwind dot com dot au".
/// </summary>
public static partial class WordErrorRate
{
    public static WerResult Compute(string reference, string hypothesis)
    {
        var r = Normalise(reference);
        var h = Normalise(hypothesis);
        var d = new int[r.Count + 1, h.Count + 1];
        for (var i = 0; i <= r.Count; i++) d[i, 0] = i;
        for (var j = 0; j <= h.Count; j++) d[0, j] = j;
        for (var i = 1; i <= r.Count; i++)
        {
            for (var j = 1; j <= h.Count; j++)
            {
                var cost = r[i - 1] == h[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return new WerResult(d[r.Count, h.Count], r.Count, Align(r, h, d));
    }

    /// <summary>
    /// Backtrace: each script word with its operation. Extra words next to a missed word join its "heard" text, so
    /// "Siobhan" heard as "shiv on" is one miss heard as "shiv on"; extra words between two matched words belong to none.
    /// </summary>
    private static List<AlignedWord> Align(List<string> r, List<string> h, int[,] d)
    {
        var steps = new List<(int Ref, int Hyp)>(); // -1 marks a gap on that side
        int i = r.Count, j = h.Count;
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && d[i, j] == d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1)) steps.Add((--i, --j));
            else if (i > 0 && d[i, j] == d[i - 1, j] + 1) steps.Add((--i, -1));
            else steps.Add((-1, --j));
        }
        steps.Reverse();

        var aligned = new List<AlignedWord>();
        var extra = new List<string>(); // inserted words not yet attached to a miss
        void AttachToPreviousMiss()
        {
            if (extra.Count > 0 && aligned.Count > 0 && aligned[^1].Op != AlignOp.Match)
            {
                var last = aligned[^1];
                aligned[^1] = last with { Op = AlignOp.Substitute, Heard = string.Join(' ', new[] { last.Heard }.Concat(extra)).Trim() };
            }
            extra.Clear();
        }
        foreach (var (ri, hi) in steps)
        {
            if (ri < 0)
            {
                extra.Add(h[hi]);
                continue;
            }
            var op = hi < 0 ? AlignOp.Delete : r[ri] == h[hi] ? AlignOp.Match : AlignOp.Substitute;
            if (op == AlignOp.Match)
            {
                AttachToPreviousMiss();
                aligned.Add(new AlignedWord(r[ri], op, h[hi]));
                continue;
            }
            var heard = string.Join(' ', extra.Append(hi < 0 ? "" : h[hi])).Trim();
            extra.Clear();
            aligned.Add(new AlignedWord(r[ri], heard.Length == 0 ? AlignOp.Delete : AlignOp.Substitute, heard));
        }
        AttachToPreviousMiss();
        return aligned;
    }

    [GeneratedRegex(@"\b(?:\p{L}\.){2,}(?!\p{L})")]
    private static partial Regex Initialism();

    // A clock time: "10:30", or "10.30" when it is not money, a decimal in a longer number, or a percentage.
    [GeneratedRegex(@"(?<![\d$,.])(\d{1,2})([:.])(\d{2})(?!\d)(?!\s*(?:%|percent|per cent|dollar))")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\bat (\d{1,2})(\d{2})(?![\d,.])")]
    private static partial Regex BareClockAfterAt();

    [GeneratedRegex(@"(?<=\d),(?=\d{3}(?!\d))")]
    private static partial Regex ThousandsComma();

    [GeneratedRegex(@"\$\s*(\d+)(\.\d+)?")]
    private static partial Regex Dollars();

    [GeneratedRegex(@"(\d)\.(\d)")]
    private static partial Regex DecimalPoint();

    [GeneratedRegex(@"\bper cent\b")]
    private static partial Regex PerCent();

    [GeneratedRegex(@"\b\p{L}+(?:-\p{L}+)+\b")]
    private static partial Regex Hyphenated();

    [GeneratedRegex(@"(?<=\p{L})\.(?=\p{L})")]
    private static partial Regex DotInName();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex Punctuation();

    /// <summary>
    /// The comparable words of a text (shared by the script and every transcript). Written and spoken forms of the same
    /// thing compare equal: "14 March", "March 14" and "the fourteenth of March"; "10:30", "10.30", "at 1030" and "ten
    /// thirty" (with or without am/pm); "12%", "12 per cent" and "twelve percent"; "$4,250.00" and "four thousand two
    /// hundred and fifty dollars"; "e-mail" and "email"; "I'll" and "I will"; "A.U." and "a u" after "dot". Different amounts,
    /// times and ranks stay different: "$42.50" is not 4,250 dollars, "1,030" is not ten thirty, "fourteen" is not "14th".
    /// </summary>
    public static List<string> Normalise(string text)
    {
        var t = Initialism().Replace(text.ToLowerInvariant().Replace('’', '\''), m => m.Value.Replace(".", "", StringComparison.Ordinal));
        t = t.Replace("'", "", StringComparison.Ordinal);
        t = ClockTime().Replace(t, m => Clock(m.Groups[1].Value, m.Groups[3].Value, m.Groups[2].Value == ":") ?? m.Value);
        t = BareClockAfterAt().Replace(t, m => Clock(m.Groups[1].Value, m.Groups[2].Value, colon: false) is { } clock ? "at " + clock : m.Value);
        t = ThousandsComma().Replace(t, "");
        t = Dollars().Replace(t, m => m.Groups[1].Value + (m.Groups[2].Value is "" or ".00" ? "" : m.Groups[2].Value) + " dollars");
        t = DecimalPoint().Replace(t, "$1 point $2");
        t = t.Replace(":", " ", StringComparison.Ordinal).Replace("%", " percent ", StringComparison.Ordinal)
            .Replace("@", " at ", StringComparison.Ordinal).Replace("&", " and ", StringComparison.Ordinal);
        t = PerCent().Replace(t, "percent");
        // A hyphenated word is one word ("e-mail"), except number words, which stay apart so "twenty-three" is 23.
        t = Hyphenated().Replace(t, m => m.Value.Split('-') is var parts && parts.Any(SpokenNumbers.IsNumberWord) ? string.Join(' ', parts) : string.Concat(parts));
        t = DotInName().Replace(t, " dot ");
        t = Punctuation().Replace(t, " ");
        var words = SpokenNumbers.ToDigits(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return SpokenForms.Apply(JoinClockTimes(SpokenForms.Dates(SpokenForms.Contractions(words))));
    }

    /// <summary>"10h30" for an hour (1-24; 0-24 after a colon) and minutes 00-59, or null.</summary>
    private static string? Clock(string hourText, string minuteText, bool colon)
    {
        var hour = int.Parse(hourText, CultureInfo.InvariantCulture);
        var minute = int.Parse(minuteText, CultureInfo.InvariantCulture);
        return hour <= 24 && (hour >= 1 || colon) && minute <= 59 ? $"{hour}h{minuteText}" : null;
    }

    /// <summary>"10 30" (from "ten thirty") → "10h30", the form the written times take: an hour 1–24 then minutes 10–59.</summary>
    private static List<string> JoinClockTimes(List<string> words)
    {
        for (var i = words.Count - 2; i >= 0; i--)
        {
            if (int.TryParse(words[i], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) && hour is >= 1 and <= 24 && words[i][0] != '0'
                && words[i + 1].Length == 2 && int.TryParse(words[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) && minute is >= 10 and <= 59)
            {
                words[i] = $"{hour}h{words[i + 1]}";
                words.RemoveAt(i + 1);
            }
        }
        return words;
    }
}

/// <summary>
/// The small spoken-number → digit step of the WER normalisation: runs of number words become one number
/// ("twenty three" → 23, "four thousand two hundred and fifty" → 4250, "fourteenth" → 14th); a number word that cannot
/// continue the run starts a new number ("ten thirty" → 10 30). Years and decimals said as words are not handled
/// (the calibration sentences avoid them).
/// </summary>
public static class SpokenNumbers
{
    private static readonly string[] UnitWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
         "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] OrdinalUnitWords =
        ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth",
         "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth", "nineteenth"];
    private static readonly string[] TensWords = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    private static readonly string[] OrdinalTensWords = ["", "", "twentieth", "thirtieth", "fortieth", "fiftieth", "sixtieth", "seventieth", "eightieth", "ninetieth"];

    private enum Kind { None, Small, Tens, Hundred, Scale }

    public static List<string> ToDigits(IReadOnlyList<string> words)
    {
        var output = new List<string>();
        var i = 0;
        while (i < words.Count)
        {
            if (!TryNumber(words, ref i, out var value, out var ordinal)) output.Add(words[i++]);
            else output.Add(value.ToString(CultureInfo.InvariantCulture) + (ordinal ? Suffix(value) : ""));
        }
        return output;
    }

    private static bool TryNumber(IReadOnlyList<string> words, ref int index, out long value, out bool ordinal)
    {
        long total = 0, current = 0;
        ordinal = false;
        var last = Kind.None;
        var i = index;
        while (i < words.Count)
        {
            var w = words[i];
            var small = Array.IndexOf(UnitWords, w);
            var smallOrdinal = Array.IndexOf(OrdinalUnitWords, w);
            var tens = Array.IndexOf(TensWords, w) is var tw and >= 2 ? tw * 10 : -1;
            var tensOrdinal = Array.IndexOf(OrdinalTensWords, w) is var to and >= 2 ? to * 10 : -1;
            var isOrdinal = smallOrdinal >= 0 || tensOrdinal >= 0;
            var n = small >= 0 ? small : smallOrdinal >= 0 ? smallOrdinal : tens >= 0 ? tens : tensOrdinal;
            if (n >= 0)
            {
                var isSmall = small >= 0 || smallOrdinal >= 0;
                var allowed = last is Kind.None or Kind.Hundred or Kind.Scale || (last == Kind.Tens && isSmall && n < 10);
                if (!allowed) break;
                current += n;
                last = isSmall ? Kind.Small : Kind.Tens;
                i++;
                if (isOrdinal)
                {
                    ordinal = true; // "twenty first" ends the number, as 21st
                    break;
                }
                continue;
            }
            if (w == "hundred" && last is Kind.None or Kind.Small or Kind.Tens)
            {
                current = (current == 0 ? 1 : current) * 100;
                last = Kind.Hundred;
                i++;
                continue;
            }
            if (w is "thousand" or "million" && last is Kind.Small or Kind.Tens or Kind.Hundred)
            {
                total += (current == 0 ? 1 : current) * (w == "thousand" ? 1_000 : 1_000_000);
                current = 0;
                last = Kind.Scale;
                i++;
                continue;
            }
            if (w == "and" && last is Kind.Hundred or Kind.Scale && i + 1 < words.Count && IsNumberWord(words[i + 1]))
            {
                i++;
                continue;
            }
            break;
        }
        if (i == index)
        {
            value = 0;
            return false;
        }
        value = total + current;
        index = i;
        return true;
    }

    internal static bool IsNumberWord(string w) =>
        Array.IndexOf(UnitWords, w) >= 0 || Array.IndexOf(OrdinalUnitWords, w) >= 0 || Array.IndexOf(TensWords, w) >= 2 || Array.IndexOf(OrdinalTensWords, w) >= 2;

    /// <summary>"st", "nd", "rd" or "th", as a written ordinal ends (21st, 12th), so "fourteenth" and "14th" compare equal and "fourteen" does not.</summary>
    private static string Suffix(long n) => n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
}
