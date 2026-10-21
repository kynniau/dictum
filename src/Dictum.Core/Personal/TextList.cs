using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Personal;

/// <summary>
/// The one reader and writer for Dictum's plain text files (dictionary.txt, corrections.txt, profanity.txt and the
/// calibration sentences): one entry per line, '#' starts a comment line, blank lines ignored, entries trimmed. A missing
/// or unreadable file is an empty list, never an error, so deleting a file simply switches its feature off.
/// </summary>
public static class TextList
{
    public static IReadOnlyList<string> Read(string path, Action<string>? warn = null)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"{Path.GetFileName(path)} could not be read ({ex.GetType().Name}); it is ignored until it can be.");
            return [];
        }
    }

    public static IReadOnlyList<string> Parse(string content) =>
        content.Split('\n')
            .Select(line => line.TrimEnd('\r').Trim().TrimStart('﻿'))
            .Where(line => line.Length > 0 && line[0] != '#')
            .ToList();

    /// <summary>Appends lines, creating the file with <paramref name="header"/> comment lines first if it does not exist.</summary>
    public static void Append(string path, IEnumerable<string> lines, string header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        if (!File.Exists(path))
        {
            foreach (var h in header.Split('\n')) sb.Append("# ").Append(h.TrimEnd('\r')).Append(Environment.NewLine);
        }
        else if (File.ReadAllText(path) is { Length: > 0 } existing && !existing.EndsWith('\n'))
        {
            sb.Append(Environment.NewLine); // the last line had no line break
        }
        foreach (var line in lines) sb.Append(line).Append(Environment.NewLine);
        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>
/// Whole-word, case-insensitive matching of literal phrases, shared by corrections, snippets and the profanity filter (SPEC
/// §16.3, §16.4, §16.6). A word character is a letter, a digit or a combining mark: "ER" never matches inside "her", "ass"
/// never inside "class", and "राम" never inside "विराम", whose vowel sign (a mark, not a letter) belongs to the word.
/// An apostrophe joins letters, except before a possessive "s": "Kinney's" contains Kinney, but "don't", "won't" and
/// "o'clock" contain no "don", "won" or "clock". Longer phrases are tried first, so overlapping entries resolve
/// longest-first; whitespace inside a phrase matches any run of whitespace.
/// </summary>
public static class WholeWords
{
    public static Regex? Pattern(IEnumerable<string> phrases)
    {
        var alternatives = phrases
            .Select(Key)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Select(p => string.Join(@"\s+", p.Split(' ').Select(Regex.Escape)))
            .ToList();
        if (alternatives.Count == 0) return null;
        return new Regex($@"(?<![\p{{L}}\p{{M}}\p{{N}}]|\p{{L}}['’])(?:{string.Join("|", alternatives)})(?![\p{{L}}\p{{M}}\p{{N}}]|['’](?!s\b)\p{{L}})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Part of a word, for the checks that walk a word one character at a time (the Fix-last diff, the profanity mask and
    /// removal): a letter, a digit, or a combining mark such as a vowel sign, virama or anusvara ("काम" is not
    /// "कम"; "हैं" is not "है").
    /// </summary>
    public static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    /// <summary>The lookup key for a phrase or a matched span: lower-case, single spaces.</summary>
    public static string Key(string phrase) =>
        string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>
/// Where a sentence starts, read back from a point in the text past spaces, quotes and brackets (straight and curly, opening
/// and closing): at the start of the text, after a line break, or after a mark that ends a sentence (<see cref="IsEndMark"/>).
/// One rule for the corrections (a capital that belongs to the sentence, not the word), the profanity filter (a removed
/// word's capital and comma, and the mark it takes with it) and the hands-free boundary rule (whether the last part ended a
/// sentence), so the three agree.
/// </summary>
public static class SentenceStart
{
    /// <summary>
    /// A mark that ends a sentence: . ! ? … and the danda । and double danda ॥, which end a sentence in scripts whose
    /// sentence marks differ from English (orchestrator, 16/10/2026: the languages lens found "वह कमीना।" filtered to "वह ।").
    /// </summary>
    public static bool IsEndMark(char c) => c is '.' or '!' or '?' or '…' or '।' or '॥';

    /// <summary>A sentence starts at <paramref name="index"/> of <paramref name="text"/> (at its length: whatever comes next).</summary>
    public static bool At(string text, int index) => At(index, i => text[i]);

    /// <summary>A sentence starts at the end of <paramref name="text"/>: whatever is appended next.</summary>
    public static bool AtEnd(StringBuilder text) => At(text.Length, i => text[i]);

    private static bool At(int index, Func<int, char> charAt)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = charAt(i);
            if (IsEndMark(c) || c == '\n') return true;
            if (!char.IsWhiteSpace(c) && c is not ('"' or '\'' or '“' or '”' or '‘' or '’' or '«' or '»' or '(' or ')' or '[' or ']')) return false;
        }
        return true;
    }
}
