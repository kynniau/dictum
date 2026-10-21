using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Settings;

namespace Dictum.Personal;

/// <summary>
/// profanity.mode (SPEC §16.4): off | mask | remove, applied last, after clean-up and the corrections (SPEC §22). The word list is
/// profanity.txt beside Dictum.exe (plural, -ing and -ed forms listed explicitly), replaced as a whole by a
/// profanity.txt in the config folder. Whole-word and case-insensitive, so "class", "assess" and "Scunthorpe" are left
/// alone. mask keeps each word's first letter and stars the rest ("f***"); remove drops the word and tidies only what
/// the removal left behind: the double space, a dangling comma, and a sentence-start capital passed to the next word.
/// </summary>
public sealed class ProfanityFilter
{
    public static readonly ProfanityFilter Empty = new([], "none");

    private readonly Regex? _pattern;

    public ProfanityFilter(IEnumerable<string> words, string source)
    {
        Words = words.Select(WholeWords.Key).Where(w => w.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        Source = source;
        _pattern = WholeWords.Pattern(Words);
    }

    public IReadOnlyList<string> Words { get; }

    /// <summary>Where the list came from, for the log and --selftest ("shipped list", or the override's path).</summary>
    public string Source { get; }

    /// <summary>The override in the config folder if it exists (even if empty), else the shipped list.</summary>
    public static ProfanityFilter Load(string overrideFile, string shippedFile, Action<string>? warn = null)
    {
        if (File.Exists(overrideFile)) return new ProfanityFilter(TextList.Read(overrideFile, warn), $"override {overrideFile}");
        if (!File.Exists(shippedFile)) warn?.Invoke($"The shipped word list {shippedFile} is missing; the profanity filter has no words.");
        return new ProfanityFilter(TextList.Read(shippedFile, warn), "shipped list");
    }

    public string Apply(string text, string mode)
    {
        if (_pattern is null || string.IsNullOrEmpty(text)) return text;
        return mode switch
        {
            ProfanitySettings.Mask => _pattern.Replace(text, m => Mask(m.Value)),
            ProfanitySettings.Remove => Remove(text, _pattern.Matches(text)),
            _ => text,
        };
    }

    /// <summary>
    /// "fucking" → "f******": the first letter of each word kept, every other letter starred, and so every combining mark
    /// (<see cref="WholeWords.IsWordChar"/>): a vowel sign such as U+0940 is starred with the letters, never left showing.
    /// </summary>
    private static string Mask(string match)
    {
        var sb = new StringBuilder(match.Length);
        var wordStart = true;
        foreach (var c in match)
        {
            var letter = WholeWords.IsWordChar(c);
            sb.Append(letter && !wordStart ? '*' : c);
            wordStart = !letter;
        }
        return sb.ToString();
    }

    private static string Remove(string text, MatchCollection matches)
    {
        if (matches.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var at = 0;
        var capitalise = new List<int>(); // output positions whose next letter takes a removed word's sentence-start capital
        foreach (Match m in matches)
        {
            sb.Append(text, at, m.Index - at);
            var start = SentenceStart.AtEnd(sb);
            var next = m.Index + m.Length;
            next += SuffixLength(text, next); // "shit's" goes with its suffix: "The 's hitting…" is worse than "The hitting…"
            while (next < text.Length && text[next] is ' ' or '\t') next++;
            if (next < text.Length && text[next] is ',' or ';' or ':' && (start || EndsWith(sb, ',')))
            {
                next++; // "Damn, that…" → "That…"; "Well, shit, that…" → "Well, that…"
                while (next < text.Length && text[next] is ' ' or '\t') next++;
            }
            else if (start && next < text.Length && SentenceStart.IsEndMark(text[next]))
            {
                next++; // a sentence of its own: "Shit! I forgot…" → "I forgot…", not "! I forgot…"
                while (next < text.Length && text[next] is ' ' or '\t') next++;
            }
            else if (next >= text.Length || SentenceStart.IsEndMark(text[next]) || text[next] is ',' or ';' or ':' or '\n' or '\r')
            {
                TrimEndSpaces(sb); // "It's shit." → "It's."; a removed word before U+0964 leaves no space before it
            }
            if (start)
            {
                TrimEndSpaces(sb);
                if (sb.Length > 0 && sb[^1] is not '\n') sb.Append(' '); // "Done. Shit, it…" → "Done. It…"
                if (char.IsUpper(m.Value[0])) capitalise.Add(sb.Length);
            }
            else if (sb.Length > 0 && sb[^1] is not (' ' or '\t' or '\n') && next < text.Length && WholeWords.IsWordChar(text[next])) sb.Append(' ');
            at = next;
        }
        sb.Append(text, at, text.Length - at);
        foreach (var position in capitalise) CapitaliseFirstLetter(sb, position);
        return sb.ToString().Trim(' ', '\t');
    }

    /// <summary>The length of an apostrophe suffix ('s 're 'll 've 'd) at <paramref name="at"/>, or 0.</summary>
    private static int SuffixLength(string text, int at)
    {
        if (at >= text.Length || text[at] is not ('\'' or '’')) return 0;
        foreach (var suffix in new[] { "s", "re", "ll", "ve", "d" })
        {
            var end = at + 1 + suffix.Length;
            if (end <= text.Length && string.Compare(text, at + 1, suffix, 0, suffix.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == text.Length || !WholeWords.IsWordChar(text[end])))
                return 1 + suffix.Length;
        }
        return 0;
    }

    private static bool EndsWith(StringBuilder sb, char c)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] is ' ' or '\t') continue;
            return sb[i] == c;
        }
        return false;
    }

    private static void TrimEndSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] is ' ' or '\t') sb.Length--;
    }

    /// <summary>After a removed sentence-initial word, the sentence's next word takes the capital it lost.</summary>
    private static void CapitaliseFirstLetter(StringBuilder sb, int from)
    {
        for (var i = from; i < sb.Length; i++)
        {
            if (char.IsDigit(sb[i]) || sb[i] is '.' or '!' or '?' or '\n') return;
            if (!char.IsLetter(sb[i])) continue;
            sb[i] = char.ToUpper(sb[i], CultureInfo.InvariantCulture);
            return;
        }
    }
}
