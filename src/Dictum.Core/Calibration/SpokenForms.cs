using System.Globalization;
using System.Text.RegularExpressions;

namespace Dictum.Calibration;

/// <summary>
/// Word-level steps of the WER normalisation (<see cref="WordErrorRate.Normalise"/>), run on both the script and the
/// transcript after numbers became digits, so a written form and its spoken form compare equal (fix batch, #9).
/// </summary>
internal static partial class SpokenForms
{
    // simplification: only contractions whose apostrophe-less form is not itself a common word. Ceiling: "it is"/"it's",
    // "we will"/"we'll", "we are"/"we're", "he will" and "she will" still count as edits, because "its", "well", "were",
    // "hell" and "shell" are words. Trigger to retire: a calibration sentence that uses one of them.
    private static readonly Dictionary<(string, string), string> Contracted = new()
    {
        [("i", "will")] = "ill", [("i", "am")] = "im", [("i", "have")] = "ive",
        [("you", "are")] = "youre", [("you", "will")] = "youll", [("you", "have")] = "youve",
        [("they", "are")] = "theyre", [("they", "will")] = "theyll", [("they", "have")] = "theyve", [("we", "have")] = "weve",
        [("do", "not")] = "dont", [("does", "not")] = "doesnt", [("did", "not")] = "didnt", [("is", "not")] = "isnt",
        [("are", "not")] = "arent", [("was", "not")] = "wasnt", [("were", "not")] = "werent", [("can", "not")] = "cant",
        [("will", "not")] = "wont", [("would", "not")] = "wouldnt", [("could", "not")] = "couldnt", [("should", "not")] = "shouldnt",
        [("have", "not")] = "havent", [("has", "not")] = "hasnt", [("that", "is")] = "thats", [("there", "is")] = "theres",
    };

    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    /// <summary>"I will" → "ill", the form "I'll" takes once apostrophes are dropped; "cannot" → "cant".</summary>
    public static List<string> Contractions(List<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] == "cannot") words[i] = "cant";
            else if (i + 1 < words.Count && Contracted.TryGetValue((words[i], words[i + 1]), out var joined))
            {
                words[i] = joined;
                words.RemoveAt(i + 1);
            }
        }
        return words;
    }

    /// <summary>"[the] 14[th] [of] March" and "March [the] 14[th]" → "14 march" (days 1-31 next to a month name).</summary>
    public static List<string> Dates(List<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (Array.IndexOf(Months, words[i]) < 0) continue;
            var j = i - 1;
            if (j >= 0 && words[j] == "of") j--;
            if (j >= 0 && Day(words[j]) is int before)
            {
                var start = j > 0 && words[j - 1] == "the" ? j - 1 : j;
                words.RemoveRange(start, i - start);
                words.Insert(start, before.ToString(CultureInfo.InvariantCulture));
                i = start + 1;
                continue;
            }
            var k = i + 1 < words.Count && words[i + 1] == "the" ? i + 2 : i + 1;
            if (k < words.Count && Day(words[k]) is int after)
            {
                words.RemoveRange(i + 1, k - i);
                words.Insert(i, after.ToString(CultureInfo.InvariantCulture));
                i++;
            }
        }
        return words;
    }

    [GeneratedRegex(@"^(\d{1,2})(?:st|nd|rd|th)?$")]
    private static partial Regex DayNumber();

    private static int? Day(string word) =>
        DayNumber().Match(word) is { Success: true } m && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var d and >= 1 and <= 31 ? d : null;

    [GeneratedRegex(@"^(\d{1,2}h\d{2})(am|pm)$")]
    private static partial Regex ClockWithMeridiem();

    [GeneratedRegex(@"^\d{1,2}h\d{2}$")]
    private static partial Regex ClockToken();

    /// <summary>
    /// Letters spelled out after "dot" join ("dot a u" → "dot au", as in northwind.com.au), and am/pm after a clock time
    /// is dropped (the script says "ten thirty"; whisper may add "a.m.").
    /// </summary>
    public static List<string> Apply(List<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (ClockWithMeridiem().Match(words[i]) is { Success: true } m) words[i] = m.Groups[1].Value;
            else if (ClockToken().IsMatch(words[i]) && i + 1 < words.Count && words[i + 1] is "am" or "pm") words.RemoveAt(i + 1);
            else if (words[i] == "dot" && i + 2 < words.Count && Letter(words[i + 1]) && Letter(words[i + 2]))
            {
                var end = i + 1;
                while (end < words.Count && Letter(words[end])) end++;
                var joined = string.Concat(words.GetRange(i + 1, end - i - 1));
                words.RemoveRange(i + 1, end - i - 1);
                words.Insert(i + 1, joined);
            }
        }
        return words;
    }

    private static bool Letter(string word) => word.Length == 1 && char.IsLetter(word[0]);
}
