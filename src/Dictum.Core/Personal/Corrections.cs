using System.Globalization;
using System.Text.RegularExpressions;

namespace Dictum.Personal;

/// <param name="Heard">What whisper writes (matched whole-word, case-insensitively).</param>
/// <param name="Written">What should be pasted instead.</param>
public sealed record CorrectionRule(string Heard, string Written)
{
    public override string ToString() => $"{Heard} => {Written}";
}

/// <summary>
/// corrections.txt (SPEC §16.3): lines "heard => written", literal phrases, whole-word and case-insensitive, applied
/// in one pass after clean-up and before the profanity filter (SPEC §22), just before the paste. Overlapping rules resolve
/// longest-first, and one rule's output is never re-matched by another (no chains). Case: a written form with no
/// capitals takes a capital first letter only at a sentence start where the heard word had one (an all-capitals acronym
/// such as "ER" does not count), so "Bill => bill" lower-cases "the Bill" mid-sentence; a written form with capitals
/// ("Kynni", "iPhone") is used exactly as written.
/// </summary>
public sealed class Corrections
{
    public const string Arrow = "=>";
    public const string FileHeader =
        "Dictum corrections: one per line, \"heard => written\". Whole words, any case. Applied before every paste.\n" +
        "Example: kinney => Kynni";

    public static readonly Corrections Empty = new([]);

    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);
    private readonly Regex? _pattern;

    public Corrections(IEnumerable<CorrectionRule> rules)
    {
        foreach (var rule in rules)
        {
            var key = WholeWords.Key(rule.Heard);
            if (key.Length > 0) _written[key] = rule.Written.Trim(); // a later line for the same phrase wins
        }
        Rules = _written.Select(kv => new CorrectionRule(kv.Key, kv.Value)).ToList();
        _pattern = WholeWords.Pattern(_written.Keys);
    }

    public IReadOnlyList<CorrectionRule> Rules { get; }

    public static Corrections Load(string path, Action<string>? warn = null) => new(Parse(TextList.Read(path, warn), warn));

    public static IEnumerable<CorrectionRule> Parse(IEnumerable<string> lines, Action<string>? warn = null)
    {
        foreach (var line in lines)
        {
            var arrow = line.IndexOf(Arrow, StringComparison.Ordinal);
            var heard = arrow < 0 ? "" : line[..arrow].Trim();
            var written = arrow < 0 ? "" : line[(arrow + Arrow.Length)..].Trim();
            if (heard.Length == 0 || written.Length == 0)
            {
                warn?.Invoke($"corrections.txt: a line without \"heard {Arrow} written\" on both sides was ignored ({line.Length} characters)."); // never the text
                continue;
            }
            yield return new CorrectionRule(heard, written);
        }
    }

    public string Apply(string text)
    {
        if (_pattern is null || string.IsNullOrEmpty(text)) return text;
        return _pattern.Replace(text, m => Cased(m.Value, _written[WholeWords.Key(m.Value)], AtSentenceStart(text, m.Index)));
    }

    /// <summary>The text start, or after ". ", "! ", "? " or a line break: where a capital is the sentence's, not the word's.</summary>
    private static bool AtSentenceStart(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && text[i] is ' ' or '\t' or '"' or '\'' or '(') i--;
        return i < 0 || text[i] is '.' or '!' or '?' or '\n';
    }

    /// <summary>
    /// A written form with capitals is used as written. One without takes a capital only where the heard word's capital
    /// belonged to the sentence (its start), so "Bill => bill" can lower-case "the Bill" mid-sentence.
    /// </summary>
    private static string Cased(string heard, string written, bool sentenceStart)
    {
        if (written.Length == 0 || written.Any(char.IsUpper) || !sentenceStart) return written;
        var letters = heard.Where(char.IsLetter).ToList();
        if (letters.Count == 0 || !char.IsUpper(letters[0])) return written;
        if (letters.Count > 1 && letters.All(char.IsUpper)) return written; // an acronym ("ER"), not a capitalised word
        var at = written.TakeWhile(c => !char.IsLetter(c)).Count();
        return at >= written.Length ? written : written[..at] + char.ToUpper(written[at], CultureInfo.InvariantCulture) + written[(at + 1)..];
    }
}
