using static Dictum.Llm.ValidatorText;

namespace Dictum.Llm;

public static partial class CleanupValidator
{
    // Spoken forms a clean-up writes as another word: not a swap.
    private static readonly Dictionary<string, string[]> Colloquial = new(StringComparer.Ordinal)
    {
        ["gonna"] = ["going"], ["wanna"] = ["want"], ["gotta"] = ["got", "have"], ["kinda"] = ["kind"], ["dunno"] = ["know"],
        ["yeah"] = ["yes"], ["yep"] = ["yes"], ["nope"] = ["no"], ["okay"] = ["ok"], ["cause"] = ["because"], ["cos"] = ["because"],
    };

    // Spoken numbers, units and punctuation a clean-up writes as digits or marks.
    private static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal)
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
        "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty", "sixty",
        "seventy", "eighty", "ninety", "hundred", "thousand", "million", "billion", "half", "quarter", "percent", "cent", "cents",
        "dollar", "dollars", "pound", "pounds", "euro", "euros", "point", "first", "second", "third", "fourth", "fifth",
    };

    private static readonly HashSet<string> SpokenMarks = new(StringComparer.Ordinal)
    {
        "exclamation", "colon", "semicolon", "dash", "hyphen", "apostrophe", "bracket", "brackets", "dot", "slash", "ampersand",
    };

    /// <summary>
    /// With About you in the prompt (SPEC §16.7, 21/10/2026), the line may help the model spell what was said and nothing else.
    /// Measured on the About you bench (DECISIONS, 21/10/2026): with the United Kingdom text qwen2.5-1.5b wrote "check the lvr
    /// with the lender" as "Check the loan with the lender.", and qwen2.5-0.5b wrote "hargreaves lansdown holds his stocks and
    /// shares isa" as "…shares at the Australian Stock Exchange." under either text; every other rule accepted both, and four
    /// wordings of the line did not stop either. So, only while About you is on, a reply is rejected when it (1) adds a word of
    /// the About you text the speaker did not say, or (2) swaps: drops a dictated word and adds one the speaker did not say.
    /// A word counts as said in any form the dictionary-term rule allows: sound-alike (<see cref="ValidatorText.SoundsLike"/>),
    /// with or without a final "s" or "'s", or joined or split ("comm bank" and "CommBank"). Words of three letters or more,
    /// not function words (<see cref="ValidatorText.IsCorrectionWord"/>), so "LVR", "ISA" and "NAB" count. Dropping alone
    /// (fillers, false starts, "tuesday no wednesday") and adding alone (a grammar fix's "are") pass, as without About you.
    /// Spoken numbers, units and marks written as digits or symbols, and a few spoken forms ("gonna" as "going"), are not swaps;
    /// an added number is an addition only when nothing was dictated as a number. Never names the word: About you and the
    /// dictation stay out of the log.
    /// </summary>
    // simplification: a set comparison of words, not an alignment of the reply with the dictation. Ceilings: a rephrasing
    // that swaps one word for another ("check whether" → "check which") is rejected while About you is on, and the tidy is
    // typed instead (a false rejection costs polish only; measured on the bench, see DECISIONS); a reply that only adds words
    // not in About you ("…shares ISA at the stock exchange") passes, as it does without About you; two words the reply swaps
    // where the dictation also said the new one elsewhere pass. Trigger to retire: a typed reply observed changing content with
    // About you on, or the bench's false-rejection count rising after a prompt change.
    internal static string? AboutYouRejection(string raw, string retracted, string textLower, string aboutYou)
    {
        var dictated = Comparable(Words(MapQuotes(raw).ToLowerInvariant())).Select(WithoutPossessive).ToList();
        var kept = Comparable(Words(WithoutFillers(WithoutCommands(retracted)))).Select(WithoutPossessive).ToList();
        var reply = Comparable(Words(textLower)).Select(WithoutPossessive).ToList();
        var replySet = reply.ToHashSet(StringComparer.Ordinal);
        var saidNumber = raw.Any(char.IsDigit) || dictated.Any(NumberWords.Contains);

        var added = new List<string>();
        for (var i = 0; i < reply.Count; i++)
        {
            var w = reply[i];
            if (!IsCorrectionWord(w) || (w.All(char.IsDigit) && saidNumber)) continue;
            if (!SaidIn(dictated, w) && !JoinsTo(reply, i, dictated) && !Colloquial.Any(c => c.Value.Contains(w) && dictated.Contains(c.Key))) added.Add(w);
        }
        var about = Comparable(Words(MapQuotes(aboutYou).ToLowerInvariant())).Where(IsCorrectionWord).ToHashSet(StringComparer.Ordinal);
        if (added.Any(about.Contains)) return "reply adds a word from About you that the speaker did not say";
        if (added.Count == 0) return null;

        for (var i = 0; i < kept.Count; i++)
        {
            var w = kept[i];
            if (!IsCorrectionWord(w) || NumberWords.Contains(w) || SpokenMarks.Contains(w)) continue;
            if (Colloquial.TryGetValue(w, out var written) && written.Any(replySet.Contains)) continue;
            if (!SaidIn(reply, w) && !JoinsTo(kept, i, reply)) return "reply swaps a dictated word for another while About you is on";
        }
        return null;
    }

    /// <summary>The words have <paramref name="word"/> in a form that counts as it: sound-alike, or with or without a final "s".</summary>
    private static bool SaidIn(List<string> words, string word) =>
        words.Any(w => SoundsLike(w, word) || SoundsLike(Singular(w), Singular(word)));

    private static string Singular(string word) => word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;

    /// <summary>
    /// The word at <paramref name="index"/> is part of a run of up to four words whose letters, joined, are one word of
    /// <paramref name="other"/> ("comm bank" and "commbank"), or is one word whose letters are such a run of <paramref name="other"/>.
    /// </summary>
    private static bool JoinsTo(List<string> words, int index, List<string> other)
    {
        var otherJoined = new HashSet<string>(other.Select(o => o.Replace("'", "")), StringComparer.Ordinal);
        for (var start = Math.Max(0, index - 3); start <= index; start++)
        {
            var run = "";
            for (var end = start; end < Math.Min(words.Count, start + 4); end++)
            {
                run += words[end].Replace("'", "");
                if (end >= index && end > start && otherJoined.Contains(run)) return true;
            }
        }
        var letters = words[index].Replace("'", "");
        for (var start = 0; start < other.Count; start++)
        {
            var run = "";
            for (var end = start; end < Math.Min(other.Count, start + 4) && run.Length < letters.Length; end++)
            {
                run += other[end].Replace("'", "");
                if (end > start && run == letters) return true;
            }
        }
        return false;
    }
}
