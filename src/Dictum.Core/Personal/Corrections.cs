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
/// The same class <see cref="Verbatim"/> is snippets.txt (SPEC §16.6): "what you say => what is pasted", the written text
/// used exactly as written (a URL at a sentence start stays lower-case, a leading or trailing line break is kept), with
/// "\n" in the file standing for a line break and "\\" for a backslash.
/// </summary>
public sealed class Corrections
{
    public const string Arrow = "=>";
    public const string FileHeader =
        "Dictum corrections: one per line, \"heard => written\". Whole words, any case. Applied before every paste.\n" +
        "Example: kinney => Kynni";
    public const string SnippetsHeader =
        "Dictum snippets: one per line, \"what you say => what is pasted\". Whole words, any case. The text is pasted exactly\n" +
        "as written, after the clean-up, corrections and profanity filter. Write \\n for a line break and \\\\ for a backslash.\n" +
        "Examples: my email => kyle@example.com\n" +
        "          my calendly => https://calendly.com/example/30min";

    /// <summary>A snippet's escapes in snippets.txt: "\\" is a backslash and "\n" a line break, read in one left-to-right pass.</summary>
    private static readonly Regex SnippetEscape = new(@"\\(\\|n)", RegexOptions.CultureInvariant);

    public static readonly Corrections Empty = new([]);

    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);
    private readonly Regex? _pattern;

    /// <param name="verbatim">Snippets: the written text is pasted exactly, never re-cased or trimmed.</param>
    public Corrections(IEnumerable<CorrectionRule> rules, bool verbatim = false)
    {
        Verbatim = verbatim;
        foreach (var rule in rules)
        {
            var key = WholeWords.Key(rule.Heard);
            // A later line for the same phrase wins. A snippet keeps a line break at either end (or one that is all of it).
            if (key.Length > 0) _written[key] = Verbatim ? rule.Written : rule.Written.Trim();
        }
        Rules = _written.Select(kv => new CorrectionRule(kv.Key, kv.Value)).ToList();
        _pattern = WholeWords.Pattern(_written.Keys);
    }

    public IReadOnlyList<CorrectionRule> Rules { get; }

    /// <summary>True for snippets: the written text is used exactly as written.</summary>
    public bool Verbatim { get; }

    public static Corrections Load(string path, Action<string>? warn = null) => new(Parse(TextList.Read(path, warn), warn));

    /// <summary>snippets.txt: the same line format, the written text verbatim with "\n" for a line break and "\\" for a backslash.</summary>
    public static Corrections LoadSnippets(string path, Action<string>? warn = null) =>
        new(Parse(TextList.Read(path, warn), warn, Path.GetFileName(path), lineBreaks: true), verbatim: true);

    /// <param name="fileName">Named in the warning for a bad line (never the line's text).</param>
    /// <param name="lineBreaks">Snippets: "\n" in the written text becomes a line break and "\\" a backslash.</param>
    public static IEnumerable<CorrectionRule> Parse(IEnumerable<string> lines, Action<string>? warn = null, string fileName = "corrections.txt", bool lineBreaks = false)
    {
        foreach (var line in lines)
        {
            var arrow = line.IndexOf(Arrow, StringComparison.Ordinal);
            var heard = arrow < 0 ? "" : line[..arrow].Trim();
            var written = arrow < 0 ? "" : line[(arrow + Arrow.Length)..].Trim();
            if (heard.Length == 0 || written.Length == 0)
            {
                warn?.Invoke($"{fileName}: a line without \"heard {Arrow} written\" on both sides was ignored ({line.Length} characters)."); // never the text
                continue;
            }
            if (lineBreaks) written = SnippetEscape.Replace(written, m => m.Groups[1].Value == "n" ? "\n" : "\\");
            yield return new CorrectionRule(heard, written);
        }
    }

    /// <summary>
    /// One snippets.txt line: the phrase, the arrow, and the text with each backslash written as "\\" and then each line
    /// break as "\n", so a typed "\\server\new-clients" reads back as typed rather than with a line break in it.
    /// </summary>
    public static string SnippetLine(string phrase, string text) =>
        $"{phrase.Trim()} {Arrow} {text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}";

    public string Apply(string text)
    {
        if (_pattern is null || string.IsNullOrEmpty(text)) return text;
        return _pattern.Replace(text, m =>
        {
            var written = _written[WholeWords.Key(m.Value)];
            return Verbatim ? written : Cased(m.Value, written, AtSentenceStart(text, m.Index));
        });
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
