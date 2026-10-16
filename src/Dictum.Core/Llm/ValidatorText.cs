using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Llm;

/// <summary>One "scratch that": the words it may retract at least (the end of the clause before it) and the correction after it.</summary>
internal readonly record struct Retraction(IReadOnlyList<string> MinSpan, IReadOnlyList<string> Replacement);

/// <summary>
/// The text handling behind <see cref="CleanupValidator"/>: Unicode words (so a reply in another script or with curly
/// apostrophes is measured, not skipped), whole-word phrase matching, "scratch that" spans, spoken commands, negations,
/// numbers and words that sound alike. Pure functions on lower-cased text.
/// </summary>
internal static partial class ValidatorText
{
    public static readonly char[] Marks = ['.', ',', ';', ':', '!', '?'];

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}']*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\d+(?:[.,:]\d+)*")]
    private static partial Regex Number();

    [GeneratedRegex(@"\b(?:new line|new paragraph|full stop|question mark|open quote|close quote|scratch that|comma)\b|\b(?:umm?|uhh?|erm?|ah|hmm)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Command();

    /// <summary>Typographic quotes and apostrophes as their plain forms ("Here’s" is "here's").</summary>
    public static string MapQuotes(string text) =>
        text.Replace('’', '\'').Replace('‘', '\'').Replace('ʼ', '\'').Replace('“', '"').Replace('”', '"');

    /// <summary>
    /// The words of a text, read in composed Unicode (NFC): "Zoë" saved as "e" plus a combining accent is the same word as
    /// "Zoë" typed with the single letter, in a term, the dictation and the reply alike.
    /// </summary>
    public static List<string> Words(string lower) => Word().Matches(Composed(lower)).Select(m => m.Value).ToList();

    private static string Composed(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text; // an unpaired surrogate cannot be normalised; the text is read as it is
        }
    }

    public static List<string> Comparable(IEnumerable<string> words) => words.Select(SpellingVariants.Normalise).ToList();

    /// <summary>Every run of five consecutive words.</summary>
    public static HashSet<string> Shingles(string text)
    {
        var words = Words(MapQuotes(text).ToLowerInvariant());
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 5 <= words.Count; i++) set.Add(string.Join(' ', words.Skip(i).Take(5)));
        return set;
    }

    /// <summary>Whole-word match of a phrase: "here is" is not inside "there is", nor "sure" inside "ensure".</summary>
    public static bool ContainsPhrase(string lower, string phrase) =>
        Regex.IsMatch(lower, $@"(?<![\p{{L}}\p{{N}}']){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}'])");

    /// <summary>The text without spoken formatting commands and hesitations (in any case), which a correct reply drops.</summary>
    public static string WithoutCommands(string text) => Command().Replace(text, " ");

    [GeneratedRegex(@"\b(?:" + DeterministicCleaner.FillerPhrases + @")\b", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    /// <summary>
    /// The text without the fillers the clean-up prompt tells the model to remove ("like", "you know", "sort of", "kind of",
    /// "I mean"; <see cref="DeterministicCleaner.FillerPhrases"/>), wherever they are and in any case. Each is also an
    /// ordinary word or phrase ("I like it"), so read a text both with and without them.
    /// </summary>
    public static string WithoutFillers(string text) => Filler().Replace(text, " ");

    /// <summary>"\n" or "\n\n" when the dictation ends with a spoken "new line" / "new paragraph", else null.</summary>
    public static string? FinalBreak(string lower)
    {
        var end = lower.TrimEnd().TrimEnd(Marks).TrimEnd();
        return end.EndsWith("new paragraph", StringComparison.Ordinal) ? "\n\n" : end.EndsWith("new line", StringComparison.Ordinal) ? "\n" : null;
    }

    /// <summary>The dictation with leading hesitations and punctuation removed, words single-spaced.</summary>
    public static string Opening(string lower)
    {
        var words = Words(lower).SkipWhile(w => w is "um" or "umm" or "uh" or "uhh" or "er" or "erm" or "ah" or "hmm" or "so");
        return string.Join(' ', words);
    }

    /// <summary>Letters outside Latin (Basic to Extended-B): a reply in a script the dictation did not use.</summary>
    public static bool HasNonLatinLetters(string text) => text.Any(c => char.IsLetter(c) && c > 'ɏ');

    // Negations, after n't and cannot are spelled out, so "don't" and "do not" count the same.
    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "nor", "none", "nothing", "nobody", "neither", "nowhere",
    };

    public static Dictionary<string, int> NegatorCounts(string lower)
    {
        var spelled = Regex.Replace(lower, @"\bcan't\b|\bcannot\b", "can not");
        spelled = Regex.Replace(spelled, @"\bwon't\b", "will not");
        spelled = Regex.Replace(spelled, @"n't\b", " not");
        return Count(Words(spelled).Where(Negators.Contains));
    }

    /// <summary>Digit groups with their separators removed, so 10:30 and 10.30, $4,250 and 4250 count the same.</summary>
    public static Dictionary<string, int> NumberCounts(string text) =>
        Count(Number().Matches(text).Select(m => Regex.Replace(m.Value, "[.,:]", "")));

    /// <summary>True when every item of <paramref name="needed"/> appears at least as often in <paramref name="have"/>.</summary>
    public static bool Contains(Dictionary<string, int> have, Dictionary<string, int> needed) =>
        needed.All(kv => have.TryGetValue(kv.Key, out var n) && n >= kv.Value);

    private static Dictionary<string, int> Count(IEnumerable<string> items) =>
        items.GroupBy(i => i, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>The dictation with the clause before each "scratch that" (and the phrase) removed: the longest retraction.</summary>
    public static string WithoutRetractedClauses(string lower)
    {
        const string phrase = "scratch that";
        for (var i = lower.IndexOf(phrase, StringComparison.Ordinal); i >= 0; i = lower.IndexOf(phrase, StringComparison.Ordinal))
        {
            var left = lower[..i].TrimEnd().TrimEnd(Marks).TrimEnd(); // the retracted clause may end with its own mark
            var boundary = left.LastIndexOfAny(Marks);
            var kept = boundary >= 0 ? left[..(boundary + 1)] : "";
            lower = kept + " " + lower[(i + phrase.Length)..].TrimStart().TrimStart(Marks).TrimStart();
        }
        return lower;
    }

    /// <summary>
    /// Each "scratch that" with its shortest plausible retraction (the last words of the clause before it, as many as
    /// the correction has plus one) and the correction (to the next punctuation mark). Without punctuation the longest
    /// retraction runs back to the start of the dictation, so these two estimates bound what was meant.
    /// </summary>
    public static List<Retraction> Retractions(string lower)
    {
        const string phrase = "scratch that";
        var result = new List<Retraction>();
        for (var i = lower.IndexOf(phrase, StringComparison.Ordinal); i >= 0; i = lower.IndexOf(phrase, i + phrase.Length, StringComparison.Ordinal))
        {
            var left = lower[..i].TrimEnd().TrimEnd(Marks).TrimEnd();
            var boundary = left.LastIndexOfAny(Marks);
            var span = Words(boundary >= 0 ? left[(boundary + 1)..] : left);
            var rest = lower[(i + phrase.Length)..].TrimStart().TrimStart(Marks).TrimStart();
            var end = rest.IndexOfAny(Marks);
            var replacement = Words(end >= 0 ? rest[..end] : rest);
            result.Add(new Retraction(span.Skip(Math.Max(0, span.Count - (replacement.Count + 1))).ToList(), replacement));
        }
        return result;
    }

    /// <summary>A word that carries content: four letters or more, not a filler or a spoken command.</summary>
    public static bool IsContent(string word) => word.Length >= 4 && !NotContent.Contains(word);

    /// <summary>A shorter content word for the "scratch that" checks: three letters or more, not a function word.</summary>
    public static bool IsCorrectionWord(string word) => word.Length >= 3 && !NotContent.Contains(word) && !FunctionWords.Contains(word);

    // Words a correct cleanup may legitimately remove: fillers and spoken formatting commands.
    private static readonly HashSet<string> NotContent = new(StringComparer.Ordinal)
    {
        "umm", "uhm", "erm", "hmm", "know", "mean", "sort", "kind", "like", "scratch", "that", "line", "paragraph",
        "comma", "full", "stop", "question", "mark", "open", "close", "quote",
    };

    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "you", "with", "this", "from", "have", "has", "was", "were", "will", "would", "can",
        "could", "should", "its", "our", "their", "they", "them", "his", "her", "him", "she", "into", "onto", "than", "then",
        "there", "here", "what", "when", "where", "who", "why", "how", "all", "any", "some", "also", "just", "too", "very",
    };

    /// <summary>
    /// True when two words sound alike: the same word, or the same <see cref="SoundKey"/>. "kinney", "kinny", "kimmy" and
    /// "Kynni" sound alike, and so do "dictim" and "Dictum"; "name" and "Kynni" do not.
    /// </summary>
    public static bool SoundsLike(string a, string b) => a == b || SoundKey(a) is { Length: > 0 } key && key == SoundKey(b);

    /// <summary>A lower-case word's sound classes, or null when it has a character the classes do not cover.</summary>
    // simplification: a spelling-based sound key in the manner of Soundex instead of a pronunciation dictionary or a full
    // phonetic algorithm such as Double Metaphone. Each letter becomes its sound class (vowels and y; b f p v; c g k q;
    // s z; d t; m n; j; l; r), x is s as the first letter ("Xero" and "zero") and k then s after it ("laxmi" and
    // "Lakshmi", "dixon" and "dickson"), h and w are dropped, accents are removed and a run of one class counts once, so
    // the vowels keep their places but not their spelling: "kinney", "kimmy" and "Kynni" are all k-vowel-n-vowel.
    // Ceiling: a real word or name that sounds like a term passes as its misspelling. Measured 16/10/2026 against the
    // 49,823 stems of LibreOffice's en_AU.dic: Kynni's key is also the key of 73 of them (come, game, gonna, canny, Kenny,
    // Connie...), Xero's of 37 (zero, sorry, Sarah...), Priya's of 29 (free, pray...), Dictum's of 3 (diction, tighten,
    // touchdown) and Parramatta's of none. A mishearing that adds or drops a sound ("kidney" for "Kynni") or splits the
    // word ("kin knee") is not a near miss; a word with a digit or a letter outside a-z (after accents are removed) only
    // matches itself. Trigger to retire: a typed reply that swapped a sound-alike word for a term, or a wanted dictionary
    // fix rejected as an addition.
    // The dictionary-term rule built on it (CleanupValidator.AddsTerm) counts a term's forms on each side instead of pairing
    // each reply word with the dictated word it came from, which adds three ceilings. Real words whose letters spell a term
    // pass as its respelling: {PowerPoint} "the power point in the kitchen is broken" (a wall socket) → "The PowerPoint in
    // the kitchen is broken." is accepted; trigger to retire: a typed reply that turned dictated words into a term with
    // another meaning. A sound-alike the reply rewrites pays for an insertion elsewhere: "i'm gonna add a name to the
    // dictionary" → "I'm going to add Kynni to the dictionary." and {Xero} "put zero on the invoice" → "Put 0 on the Xero
    // invoice." are accepted; trigger to retire: a typed reply that added a term while rewriting a sound-alike of it. A
    // term with a spoken command or hesitation in it is counted with those kept, so one the speaker dictated pays for it:
    // {ER} "er the patient was seen this morning" → "The patient was seen in the ER this morning." and {Comma} "yes comma
    // i agree" → "Yes, Comma, I agree." are accepted; trigger to retire: a typed reply that turned a dictated command or
    // hesitation into a term.
    private static string? SoundKey(string word)
    {
        var key = new StringBuilder(word.Length + 1);
        var letters = word.Normalize(NormalizationForm.FormD);
        for (var i = 0; i < letters.Length; i++)
        {
            var c = letters[i];
            if (c == 'x' && i > 0) Add(key, 'k'); // then its s below
            var sound = c switch
            {
                'a' or 'e' or 'i' or 'o' or 'u' or 'y' => 'a',
                'b' or 'f' or 'p' or 'v' => 'p',
                'c' or 'g' or 'k' or 'q' => 'k',
                's' or 'x' or 'z' => 's',
                'd' or 't' => 't',
                'm' or 'n' => 'n',
                'j' or 'l' or 'r' => c,
                'h' or 'w' or '\'' => '\0',
                _ when CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark => '\0', // an accent, after FormD
                _ => '?',
            };
            if (sound == '?') return null;
            Add(key, sound);
        }
        return key.ToString();

        static void Add(StringBuilder key, char sound)
        {
            if (sound != '\0' && (key.Length == 0 || key[^1] != sound)) key.Append(sound);
        }
    }
}
