namespace Dictum.Personal;

/// <summary>
/// "Fix last dictation → Save corrections" (SPEC §16.3): a word-level diff between what was pasted and what the user
/// fixed it to. Each substitution (some words replaced by others, between unchanged words) becomes one suggested
/// "heard => written" rule; pure insertions, pure deletions, punctuation-only changes and rewrites of more than
/// <see cref="MaxWords"/> words on either side are not corrections and are not suggested.
/// </summary>
public static class CorrectionSuggestions
{
    /// <summary>A substitution longer than this is a rewrite, not a mishearing.</summary>
    public const int MaxWords = 4;

    // simplification: the diff is a full LCS table, O(n×m) in time and memory. Ceiling: two 2,000-word texts need
    // 4 million cells (about 16 MB) and some milliseconds; longer texts are compared only up to that length. Trigger
    // to retire: fixes of dictations longer than a few pages.
    private const int MaxTokens = 2000;

    /// <summary>
    /// A change that is probably about this one text rather than a mishearing: a number or date written differently
    /// ("fourteen" to "14th", docs/design/FixLast.dc.html). Shown with a note to leave it unticked; never ticked for the user.
    /// </summary>
    public static bool LikelyOneOff(CorrectionRule rule) => rule.Heard.Any(char.IsDigit) || rule.Written.Any(char.IsDigit);

    public static IReadOnlyList<CorrectionRule> Suggest(string original, string fixedText)
    {
        var a = Tokens(original);
        var b = Tokens(fixedText);
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = Same(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var suggestions = new List<CorrectionRule>();
        var removed = new List<string>();
        var added = new List<string>();
        void Flush()
        {
            if (removed.Count is > 0 and <= MaxWords && added.Count is > 0 and <= MaxWords)
            {
                var heard = Join(removed);
                var written = Join(added);
                if (heard.Length > 0 && written.Length > 0 && heard != written && !heard.StartsWith('#') // a '#' line is a comment in corrections.txt
                    && !heard.Contains(Corrections.Arrow, StringComparison.Ordinal) && !written.Contains(Corrections.Arrow, StringComparison.Ordinal)
                    && !suggestions.Any(s => WholeWords.Key(s.Heard) == WholeWords.Key(heard)))
                {
                    suggestions.Add(new CorrectionRule(heard, written));
                }
            }
            removed.Clear();
            added.Clear();
        }

        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && Same(a[x], b[y]))
            {
                Flush();
                x++;
                y++;
            }
            else if (y < b.Count && (x >= a.Count || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                added.Add(b[y++]);
            }
            else
            {
                removed.Add(a[x++]);
            }
        }
        Flush();
        return suggestions;
    }

    private static List<string> Tokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxTokens).ToList();

    /// <summary>
    /// Words compare on their letters, digits and combining marks only (<see cref="WholeWords.IsWordChar"/>), in any case, so
    /// "kinney," and "Kinney" are the same word and so are "its"/"it's", "10.30"/"10:30" and "e-mail"/"email": punctuation
    /// inside a word is not a correction (README, DECISIONS). A combining vowel sign or anusvara is part of the word, so
    /// "कम"/"काम" and "है"/"हैं" are different words.
    /// </summary>
    private static bool Same(string a, string b) => string.Equals(Core(a), Core(b), StringComparison.OrdinalIgnoreCase);

    private static string Core(string token) => new(token.Where(WholeWords.IsWordChar).ToArray());

    private static readonly char[] Edge = ['.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '“', '”', '‘', '’'];

    /// <summary>The phrase without the punctuation at its two ends ("Kinney," → "Kinney").</summary>
    private static string Join(List<string> words) => string.Join(' ', words).Trim(Edge).Trim();
}
