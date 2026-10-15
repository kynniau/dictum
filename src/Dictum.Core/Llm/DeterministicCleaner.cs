using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Llm;

/// <summary>
/// The `none` provider and the fallback tier for every other provider (SPEC §5): a rule-based tidy that must never
/// change meaning, so it only touches what cannot be content. It removes the standalone hesitation tokens
/// um/umm/uh/uhh/erm/ah/hmm (never "er" or "mm", which are also words and units, and never right after a number);
/// removes "you know", "I mean", "sort of", "kind of" and "like" only when they sit between a sentence start or a
/// comma and a following comma; collapses immediate repeats ("the the"); applies "new line" / "new paragraph" only
/// when the command is a clause of its own (between punctuation marks); capitalises the first letter of each sentence and a
/// standalone "i", without re-casing anything else or any token with a full stop inside ("i.e.", "a.m."); tidies
/// spaces before punctuation; and ends with a full stop.
/// </summary>
public static partial class DeterministicCleaner
{
    // Whole-token hesitations, with a following comma if any. Not after a digit ("10 ah" is ampere-hours), and lower case
    // or capitalised only: all capitals ("ERM", "HMM", "UM") is an acronym, never a hesitation (lens probe, 15/10/2026).
    // "Ah" as a unit after a word ("rated in Ah") is still removed: known, unfixed.
    [GeneratedRegex(@"(?<!\d\s*)(?<![\w.'-])(?:[Uu]mm?|[Uu]hh?|[Ee]rm|[Aa]h|[Hh]mm)(?![\w'-]),?")]
    private static partial Regex Hesitation();

    // A multi-word filler only between a sentence start or a comma and a following comma: "you know, we…",
    // "it was, sort of, long". "I mean it." and "Kind of Blue" are left alone.
    [GeneratedRegex(@"(?:(?<=^)|(?<=[.!?]\s+)|(?<=,)\s*)(?:you know|i mean|sort of|kind of|like)\s*,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CommaFiller();

    // Immediate repeats ("the the"); "that that", "had had" and "is is" ("what it is is") are left alone because they are
    // often correct, and so is a capitalised reduplication ("Wagga Wagga", "Woy Woy"): see KeepOrCollapse.
    [GeneratedRegex(@"\b(?!(?:that|had|is)\b)([A-Za-z']+)(\s+\1\b)+", RegexOptions.IgnoreCase)]
    private static partial Regex Repeat();

    // "scratch that" as a clause of its own retracts the clause before it; inside a clause it is left for the reader,
    // because which words it retracts cannot be known without punctuation.
    [GeneratedRegex(@"(?<lead>^|[.,;:!?])\s*scratch that\s*(?:[.,;:!?]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BoundedRetraction();

    // "new line" / "new paragraph" as a clause of its own: between punctuation marks, or ending the text after one
    // ("Thanks, new line"). Never inside a clause: "a new line of credit", "we need a new line." stay as they are.
    [GeneratedRegex(@"(?<lead>^|[.,;:!?])\s*new (?<kind>line|paragraph)\s*(?:[.,;:!?]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SpokenBreak();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    // A full stop that starts a word (".NET", ".5") is not punctuation to pull back.
    [GeneratedRegex(@"[ \t]+([,!?;:]|\.(?![\p{L}\p{N}]))")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([,;:])(?:\s*[,;:])+")]
    private static partial Regex RepeatedCommas();

    [GeneratedRegex(@",(?=\s*[.!?])")]
    private static partial Regex CommaBeforeStop();

    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = Spaces().Replace(raw.Replace('\r', ' ').Replace('\n', ' '), " ").Trim();

        text = Hesitation().Replace(text, " ");
        text = Spaces().Replace(text, " ").Trim();
        text = CommaFiller().Replace(text, " ");
        text = Repeat().Replace(text, KeepOrCollapse);
        text = ApplyBoundedRetractions(text);

        text = SpokenBreak().Replace(text, m => m.Groups["lead"].Value + Break(m.Groups["kind"].Value));

        var lines = text.Split('\n').Select(TidyLine);
        return string.Join("\n", lines).TrimStart('\n', ' ').TrimEnd(' ');
    }

    private static string Break(string kind) => kind.Equals("paragraph", StringComparison.OrdinalIgnoreCase) ? "\n\n" : "\n";

    /// <summary>"the the" → "the", "I I" → "I"; "Wagga Wagga" stays (a word of two letters or more repeated with a capital).</summary>
    private static string KeepOrCollapse(Match m)
    {
        var word = m.Groups[1].Value;
        var repeats = m.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1);
        return word.Length > 1 && repeats.Any(r => char.IsUpper(r[0])) ? m.Value : word;
    }

    /// <summary>Removes each punctuation-bounded "scratch that" with the clause before it (back to the previous mark).</summary>
    private static string ApplyBoundedRetractions(string text)
    {
        char[] marks = ['.', ',', ';', ':', '!', '?'];
        for (var m = BoundedRetraction().Match(text); m.Success; m = BoundedRetraction().Match(text))
        {
            var lead = m.Groups["lead"];
            int start;
            if (lead.Length == 0)
            {
                start = m.Index; // "scratch that" opens the text: only the phrase goes
            }
            else
            {
                // The retracted clause runs from just after the mark before it up to and including its own mark.
                var previous = lead.Index > 0 ? text.LastIndexOfAny(marks, lead.Index - 1) : -1;
                start = previous + 1;
            }
            text = (text[..start] + " " + text[(m.Index + m.Length)..]).Trim();
        }
        return text;
    }

    private static string TidyLine(string line)
    {
        var t = Spaces().Replace(line, " ").Trim();
        t = SpaceBeforePunctuation().Replace(t, "$1");
        t = RepeatedCommas().Replace(t, "$1");
        t = CommaBeforeStop().Replace(t, "");
        t = t.TrimStart(',', ';', ':', ' ').TrimEnd(',', ';', ':', ' ');
        if (t.Length == 0) return t;
        t = Capitalise(t);
        if (char.IsLetterOrDigit(t[^1])) t += ".";
        return t;
    }

    /// <summary>First letter of each sentence and a standalone "i" (also "i'm", "i've"…); nothing is lower-cased.</summary>
    private static string Capitalise(string line)
    {
        var tokens = line.Split(' ');
        var sentenceStart = true;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0) continue;
            var core = token.TrimEnd(',', ';', ':', '.', '!', '?', '"', '\'', ')');
            var dotted = core.Contains('.'); // "i.e.", "e.g.", "a.m.": never re-cased, never a sentence end
            if (!dotted && (core == "i" || core.StartsWith("i'", StringComparison.Ordinal)))
            {
                token = "I" + token[1..];
            }
            else if (!dotted && sentenceStart)
            {
                var at = token.TakeWhile(ch => !char.IsLetterOrDigit(ch)).Count();
                if (at < token.Length && char.IsLower(token[at]))
                {
                    var sb = new StringBuilder(token);
                    sb[at] = char.ToUpper(token[at], CultureInfo.InvariantCulture);
                    token = sb.ToString();
                }
            }
            tokens[i] = token;
            if (!dotted) sentenceStart = token.TrimEnd('"', '\'', ')').EndsWith('.') || token.TrimEnd('"', '\'', ')').EndsWith('!') || token.TrimEnd('"', '\'', ')').EndsWith('?');
            else sentenceStart = false;
        }
        return string.Join(' ', tokens);
    }
}
