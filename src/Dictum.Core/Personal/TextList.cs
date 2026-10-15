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
/// Whole-word, case-insensitive matching of literal phrases, shared by corrections and the profanity filter (SPEC
/// §16.3, §16.4). A word character is a letter or a digit: "ER" never matches inside "her", "ass" never inside "class".
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
        return new Regex($@"(?<![\p{{L}}\p{{N}}]|\p{{L}}['’])(?:{string.Join("|", alternatives)})(?![\p{{L}}\p{{N}}]|['’](?!s\b)\p{{L}})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The lookup key for a phrase or a matched span: lower-case, single spaces.</summary>
    public static string Key(string phrase) =>
        string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
