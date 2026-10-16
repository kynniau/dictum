using Dictum.Llm;

namespace Dictum.Core;

/// <summary>
/// One hands-free part on its way through the pipeline (SPEC §3 amendment, 16/10/2026), with what the last part that typed
/// something left behind: its text as typed, for the boundary rule, and the closing words of what it said, carried to
/// whisper's prompt and the clean-up.
/// </summary>
/// <param name="number">1 for the session's first part, and so on (for the log).</param>
/// <param name="last">The last part of this session that typed something, or null before anything is typed.</param>
public sealed class HandsFreePart(int number, HandsFreePart? last)
{
    public int Number { get; } = number;

    /// <summary>The text the last part typed, as typed (null before anything is typed in this session): the boundary rule reads it.</summary>
    public string? Before { get; } = last?.Typed;

    /// <summary>
    /// The closing words of what the last part said (its <see cref="Spoken"/> text), given to whisper and the clean-up as
    /// context; never typed again. Never taken from the typed text: corrections, the profanity filter and snippets put the
    /// user's own text there (a snippet's address, a correction's name), and a cloud clean-up gets these words in its prompt.
    /// </summary>
    public string? Carried { get; } = last?.Spoken is { } spoken ? SegmentJoin.Tail(spoken) : null;

    /// <summary>What this part typed (without the separator), once it is pasted; null until then, or when nothing was typed.</summary>
    public string? Typed { get; internal set; }

    /// <summary>
    /// What this part said: its text after the clean-up and the boundary rule, before corrections, the profanity filter and
    /// snippets. Set with <see cref="Typed"/>.
    /// </summary>
    public string? Spoken { get; internal set; }
}

/// <summary>
/// How a hands-free part is typed after the one before it (SPEC §3 amendment, 16/10/2026). The boundary rule: when the last
/// part did not end with terminal punctuation, this one goes on in lower case after a space; when it did, a space and this
/// part's capital. A spoken "new line" or "new paragraph" at either side of the boundary still works. Pure functions.
/// </summary>
public static class SegmentJoin
{
    /// <summary>Words of the last part carried to the next: about a phrase, so whisper and the clean-up hear how the sentence was going.</summary>
    // simplification: a fixed count of whitespace-separated words, not tokens. Ceiling: a long dictionary prompt plus these
    // words can pass whisper's 224-token prompt budget, and whisper.cpp then drops the oldest prompt tokens itself (the oldest
    // dictionary terms in the prompt). Trigger to retire: a dictionary term observed missed only in hands-free parts.
    public const int CarriedWords = 10;

    /// <summary>The last <paramref name="words"/> words of <paramref name="typed"/>, on one line; null when it has none.</summary>
    public static string? Tail(string typed, int words = CarriedWords)
    {
        var all = typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return all.Length == 0 ? null : string.Join(' ', all[Math.Max(0, all.Length - words)..]);
    }

    /// <summary>The text ends a sentence: past closing quotes and brackets, its last character is . ! ? or …</summary>
    public static bool EndsSentence(string typed)
    {
        var end = typed.TrimEnd().TrimEnd('"', '\'', ')', ']', '”', '’', '»');
        return end.Length > 0 && end[^1] is '.' or '!' or '?' or '…';
    }

    /// <summary>The text ends with a line break (a spoken "new line" or "new paragraph" closed the part).</summary>
    public static bool EndsWithBreak(string typed) => typed.TrimEnd(' ', '\t').EndsWith('\n');

    /// <summary>
    /// "\n" or "\n\n" when the part opened with a spoken "new line" or "new paragraph" that the clean-up applied: a clean-up
    /// trims a break at the start of a text, so the break goes between the parts instead. Null when the part did not open with
    /// one, or the clean-up kept the words ("New line of credit…").
    /// </summary>
    public static string? LeadingBreak(string raw, string cleaned)
    {
        var said = Opening(raw);
        var spoken = Starts(said, "new paragraph") ? "\n\n" : Starts(said, "new line") ? "\n" : null;
        if (spoken is null) return null;
        var kept = Opening(cleaned);
        return Starts(kept, "new paragraph") || Starts(kept, "new line") ? null : spoken;
    }

    /// <summary>
    /// What is typed between the last part and this one, and this part's text with its first letter in the boundary rule's case.
    /// The separator is the opening break (<see cref="LeadingBreak"/>), else nothing after a line break or a space (the trailing
    /// space insert.trailing_space adds included), else a space.
    /// </summary>
    /// <param name="before">What the last part typed (not empty).</param>
    /// <param name="raw">This part's transcript, as whisper wrote it.</param>
    /// <param name="cleaned">This part's text after the clean-up.</param>
    /// <param name="trailingSpace">insert.trailing_space: the last paste already ended with a space.</param>
    /// <param name="names">The personal dictionary's terms: one spelt with a capital keeps it mid-sentence.</param>
    public static (string Separator, string Text) Join(string before, string raw, string cleaned, bool trailingSpace, IEnumerable<string> names)
    {
        if (LeadingBreak(raw, cleaned) is { } spokenBreak) return (spokenBreak, Capitalised(cleaned));
        var separator = before.Length == 0 || char.IsWhiteSpace(before[^1]) || trailingSpace ? "" : " ";
        return (separator, EndsSentence(before) || EndsWithBreak(before) ? Capitalised(cleaned) : Continued(cleaned, names));
    }

    /// <summary>A plain lower-case first word gets its capital; anything else ("iPhone", "3pm", a quote) is left as it is.</summary>
    private static string Capitalised(string text)
    {
        var (at, word) = FirstWord(text);
        if (at < 0 || !Bare(word).All(c => char.IsLower(c) || c is '\'' or '’' or '-')) return text;
        return text[..at] + char.ToUpperInvariant(text[at]) + text[(at + 1)..];
    }

    /// <summary>The word without the punctuation that can follow it.</summary>
    private static string Bare(string word) => word.TrimEnd('.', ',', ';', ':', '!', '?', '"', '\'', ')', '’', '”', '…');

    /// <summary>
    /// A part that goes on with the sentence starts with a small letter, unless its first word keeps its capital mid-sentence:
    /// "I" ("I'm", "I'll"…), a word with another capital in it ("NASA", "PowerPoint", "McDonald's") or a dictionary term spelt
    /// with a capital ("Kynni"). A text that does not start with a letter is left as it is.
    /// </summary>
    // simplification: a name the dictionary does not list is lower-cased ("…meeting with" then "sarah tomorrow."); only the
    // dictionary knows which words are names. Trigger to retire: a name observed lower-cased at a part boundary that the user
    // would not add to the dictionary.
    private static string Continued(string text, IEnumerable<string> names)
    {
        var (at, word) = FirstWord(text);
        if (at < 0 || !char.IsUpper(text[at]) || KeepsCapital(word, names)) return text;
        return text[..at] + char.ToLowerInvariant(text[at]) + text[(at + 1)..];
    }

    private static bool KeepsCapital(string word, IEnumerable<string> names)
    {
        var core = Bare(word);
        if (core.EndsWith("'s", StringComparison.Ordinal) || core.EndsWith("’s", StringComparison.Ordinal)) core = core[..^2];
        if (core == "I" || core.StartsWith("I'", StringComparison.Ordinal) || core.StartsWith("I’", StringComparison.Ordinal)) return true;
        if (core.Skip(1).Any(char.IsUpper)) return true;
        return names.Any(term => term.Any(char.IsUpper) && string.Equals(FirstOf(term), core, StringComparison.Ordinal));
    }

    private static string FirstOf(string term)
    {
        var space = term.IndexOfAny([' ', '\t']);
        return space < 0 ? term : term[..space];
    }

    /// <summary>Where the text's first letter is and the whitespace-delimited word it starts; (-1, "") when the text does not start with a letter.</summary>
    private static (int At, string Word) FirstWord(string text)
    {
        var at = 0;
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        if (at >= text.Length || !char.IsLetter(text[at])) return (-1, "");
        var end = at;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (at, text[at..end]);
    }

    private static string Opening(string text) => ValidatorText.Opening(ValidatorText.MapQuotes(text).ToLowerInvariant());

    private static bool Starts(string words, string command) => words == command || words.StartsWith(command + " ", StringComparison.Ordinal);
}
