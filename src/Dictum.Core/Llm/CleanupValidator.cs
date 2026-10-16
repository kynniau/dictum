using System.Text.RegularExpressions;
using static Dictum.Llm.ValidatorText;

namespace Dictum.Llm;

/// <param name="SystemPrompt">The prompt actually sent, so a reply that echoes a custom or dictionary prompt is caught.</param>
/// <param name="MustKeepWords">Words the reply may not drop when dictated (the profanity list unless profanity.mode = remove).</param>
/// <param name="DictionaryTerms">
/// The personal dictionary's terms in the prompt's dictionary line (SPEC §16.2, PersonalDictionary.CleanupTerms): the reply
/// may not add one the speaker did not say. Terms left out of the line are not checked: the model never saw them.
/// </param>
public sealed record ValidationContext(string? SystemPrompt, IReadOnlyCollection<string>? MustKeepWords = null,
    IReadOnlyCollection<string>? DictionaryTerms = null);

/// <summary>
/// Decides whether a model's reply is the cleaned dictation (SPEC §5, applied to every provider). Rejected replies
/// fall back to the deterministic cleanup; the reason is logged, never the text (no reason quotes the dictation or the
/// reply). A false rejection costs only polish; a false acceptance pastes the wrong words.
/// Spec rules: empty; word count outside 0.4×–1.6× of the input (8+ words, spoken commands and hesitations not counted);
/// starts with commentary; contains the system prompt's own wording; contains a chat-template token. Beyond the spec:
/// framing and labels the speaker did not say (whole words, curly quotes mapped), a script the dictation did not use, a
/// final line or bracket the speaker did not say, a dictionary term the speaker did not say, the dictated "?" lost, too
/// few shared words, a negation or a number dropped or changed, a kept word dropped (profanity), dropped content (overall
/// and per sentence), and "scratch that" handled both ways round: the correction must be in the reply and the retracted
/// words must not.
/// </summary>
public static partial class CleanupValidator
{
    private static readonly string[] TemplateTokens =
    [
        "<|im_start|>", "<|im_end|>", "<|endoftext|>", "<|eot_id|>", "<|start_header_id|>", "<|end_header_id|>",
        "<|assistant|>", "<|user|>", "<|system|>", "<s>", "</s>", "[INST]", "[/INST]", "<start_of_turn>", "<end_of_turn>",
    ];

    // Framing a model adds around (or instead of) the text; checked anywhere, as whole words, unless the speaker said it.
    private static readonly string[] FramingPhrases =
    [
        "here is", "here's", "here are", "sure,", "sure!", "sure.", "certainly", "of course", "the cleaned", "cleaned text",
        "cleaned-up", "i've cleaned", "i have cleaned", "i can't", "i cannot", "i'm sorry", "i am sorry", "i'm unable",
        "i am unable", "as an ai", "it seems", "it looks like you", "there is no text", "no text was provided",
        "let me know", "i hope this helps", "feel free to", "note:", "(note",
    ];

    // The fixed wording any prompt can carry: the built-in prompt and the batch-2 additions.
    private static readonly Lazy<HashSet<string>> FixedPromptShingles = new(() =>
        Shingles(CleanupPrompt.Default + " " + CleanupPrompt.GrammarAssist + " " + CleanupPrompt.DictionaryLinePrefix));

    private static readonly Lazy<string[]> ExampleLines = new(() =>
        CleanupExamples.Pairs.SelectMany(p => p.Clean.Split('\n')).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).Distinct().ToArray());

    [GeneratedRegex(@"[.!?\n]+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^\W*(here is|here's|here are|sure|certainly|of course|okay, here|ok, here|cleaned text|the cleaned)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommentaryStart();

    [GeneratedRegex(@"^\W*([\p{L}][\p{L}\p{N} ]{0,24}):\s")]
    private static partial Regex LeadingLabel();

    [GeneratedRegex(@"\(([^()]{1,200})\)[\s.!?]*$")]
    private static partial Regex FinalBracket();

    public static (string? Text, string? Rejection) Validate(string raw, string? reply) => Validate(raw, reply, null);

    public static (string? Text, string? Rejection) Validate(string raw, string? reply, ValidationContext? context)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, "empty response");
        var rawLower = MapQuotes(raw).ToLowerInvariant();
        var text = reply.Trim();
        if (!rawLower.Contains("quote", StringComparison.Ordinal)) text = StripWrappingQuotes(text, raw); // keep dictated quotes
        if (text.Length == 0) return (null, "empty response");
        if (FinalBreak(rawLower) is { } lineBreak) text += lineBreak; // a dictated closing "new line" is kept

        foreach (var token in TemplateTokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) return (null, $"chat-template token {token} in the reply");
        }

        var textLower = MapQuotes(text).ToLowerInvariant();
        var rawWords = Words(rawLower);
        var replyWords = Words(textLower);
        var rawSet = Comparable(rawWords).ToHashSet(StringComparer.Ordinal);
        var retracted = WithoutRetractedClauses(rawLower);

        if (Ratio(Words(WithoutCommands(rawLower)).Count, replyWords.Count) is > 1.6 and var high)
            return (null, $"reply has {high:0.00}x the dictation's words (limit 1.6x)");
        if (Ratio(Words(WithoutCommands(retracted)).Count, replyWords.Count) is < 0.4 and var low)
            return (null, $"reply has {low:0.00}x the dictation's words (minimum 0.4x)");

        if (Framing(rawLower, text, textLower) is { } framing) return (null, framing);

        var promptShingles = context?.SystemPrompt is { } sent ? Shingles(sent) : [];
        if (Shingles(text).Where(s => FixedPromptShingles.Value.Contains(s) || promptShingles.Contains(s)).Except(Shingles(raw)).Any())
            return (null, "reply repeats five or more words of the system prompt");

        if (retracted.Contains('?') && !text.Contains('?'))
            return (null, "the dictated question is missing from the reply (answered instead of cleaned?)");

        if (replyWords.Count >= 6 && Comparable(replyWords).Count(rawSet.Contains) is var known && known < replyWords.Count / 2.0)
            return (null, $"reply shares too few words with the dictation ({known}/{replyWords.Count})");

        if (HasNonLatinLetters(text) && !HasNonLatinLetters(raw)) return (null, "reply is in a script the dictation did not use");

        if (Unsaid(textLower, rawSet) is { } unsaid) return (null, unsaid);
        if (context?.DictionaryTerms is { Count: > 0 } terms && AddsTerm(TermText.Of(raw), TermText.Of(text), terms))
            return (null, "reply adds a dictionary term the speaker did not say"); // never which term: names stay out of the log

        if (!ValidatorText.Contains(NegatorCounts(textLower), NegatorCounts(retracted))) return (null, "reply drops a negation");
        if (!ValidatorText.Contains(NumberCounts(textLower), NumberCounts(retracted))) return (null, "reply drops or changes a number");
        if (context?.MustKeepWords is { Count: > 0 } keep)
        {
            var replySet = replyWords.ToHashSet(StringComparer.Ordinal);
            if (Words(retracted).Any(w => keep.Contains(w) && !replySet.Contains(w)))
                return (null, "reply drops a dictated word that the profanity setting keeps");
        }

        var dropped = DroppedContent(retracted, replyWords) ?? Retraction(rawLower, replyWords);
        return dropped is null ? (text, null) : (null, dropped);
    }

    /// <summary>Reply words over dictation words, or null under eight dictation words (SPEC §5: short inputs are exempt).</summary>
    private static double? Ratio(int rawWords, int replyWords) => rawWords >= 8 ? replyWords / (double)rawWords : null;

    /// <summary>Commentary at the start, framing anywhere, or a leading label, unless the speaker said it.</summary>
    private static string? Framing(string rawLower, string text, string textLower)
    {
        var opening = Opening(rawLower);
        var start = CommentaryStart().Match(MapQuotes(text));
        if (start.Success && !SaidFirst(opening, start.Groups[1].Value)) return $"reply starts with commentary (\"{start.Groups[1].Value.ToLowerInvariant()}\")";
        foreach (var phrase in FramingPhrases)
        {
            if (ContainsPhrase(textLower, phrase) && !ContainsPhrase(rawLower, phrase.TrimEnd(',', '!', '.', ':')))
                return $"commentary-shaped reply (contains \"{phrase}\")";
        }
        var label = LeadingLabel().Match(MapQuotes(text));
        return label.Success && !SaidFirst(opening, label.Groups[1].Value) ? "reply starts with a label the speaker did not say" : null;
    }

    private static bool SaidFirst(string opening, string phrase)
    {
        var words = string.Join(' ', Words(MapQuotes(phrase).ToLowerInvariant()));
        return opening == words || opening.StartsWith(words + " ", StringComparison.Ordinal);
    }

    /// <summary>A final line, a final bracket or a few-shot example line that the speaker did not say (the model's own addition).</summary>
    private static string? Unsaid(string textLower, HashSet<string> rawSet)
    {
        static bool MostlyUnsaid(List<string> words, HashSet<string> said) => words.Count > 0 && Comparable(words).Count(said.Contains) * 2 < words.Count;
        var lines = textLower.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count >= 2 && MostlyUnsaid(Words(lines[^1]), rawSet)) return "reply ends with a line the speaker did not say";
        if (FinalBracket().Match(textLower) is { Success: true } bracket && MostlyUnsaid(Words(bracket.Groups[1].Value), rawSet))
            return "reply ends with a remark in brackets the speaker did not say";
        foreach (var example in ExampleLines.Value)
        {
            if (lines.Contains(example) && Comparable(Words(example)).Any(w => !rawSet.Contains(w))) return "reply contains a line from the built-in examples";
        }
        return null;
    }

    /// <summary>
    /// A dictionary term the speaker did not say. Observed 16/10/2026: with the dictionary {Dictum, Kynni, Kynni Pty Ltd},
    /// qwen2.5-1.5b turned "Add a name to the dictionary once. ... Dictum, by Kynni." into "Add Kynni Pty Ltd to the
    /// dictionary once. ... Dictum, by Kynni Pty Ltd.", which every other rule accepted and which was typed; with {Dictum,
    /// Kynni} it still wrote "Add Kynni to the dictionary once". Each term is counted whole, and so is each content word of
    /// a multi-word term. A unit was added when the reply spells it more often than the dictation (<see cref="TermText.Count"/>)
    /// and also has it in some form more often than the dictation does (<see cref="TermText.Allowance"/>): writing "kinney"
    /// as "Kynni", the fix the dictionary exists for, swaps one form for another, so the second count does not grow. Both
    /// counts read the reply and the dictation alike, so a plural or split word the reply keeps ("bills", "git hub") pays for
    /// nothing. A term with a capital letter is counted as spelt in dictionary.txt, capitals included and the possessive
    /// removed, on both sides (the dictation's capitals are whisper's); a term with no capital matches any case. So the
    /// everyday words a grammar fix adds or changes are not the term ("will" is not "Will", "it" not "IT", "loan" not "Loan
    /// Market"), and a dictated "will" does not cover an added "Will". Spoken formatting commands and hesitations ("comma",
    /// "question mark", "open quote", "new line", "um") are instructions, not words, and are left out of the allowance on
    /// both sides: "comma" never pays for "Kynni", whose sound key it shares, and a "comma" the reply keeps as a word is not
    /// counted against it. Counted against everything dictated, retracted clauses included: what "scratch that" retracts is
    /// ambiguous ("tell kynni it is at 3, scratch that, at 4"), and a retracted word left in the reply is the retraction
    /// rule's to judge.
    /// Ceilings: a term inserted in other capitals than its own ("Add kynni to the dictionary once.") is not counted. An
    /// everyday word the reply adds at the start of a sentence is counted when it spells a capitalised term ("Will you"), and
    /// so is one whisper wrote in lower case when a grammar fix adds the word again: "will you come tomorrow i see you then"
    /// → "Will you come tomorrow? I will see you then." is rejected, and accepted with whisper's own capitals (a false
    /// rejection costs polish only). "Sounds like" is a spelling-based sound key whose measure and ceiling are documented at
    /// <see cref="ValidatorText.SoundsLike"/>: a real word that sounds like a term ("canny" for "Kynni") passes as its
    /// misspelling, and a mishearing that adds a sound ("kidney") or splits the word ("kin knee") is rejected as an addition
    /// and left to the tidy and the corrections. Each side's forms are counted rather than each reply word paired with the
    /// dictated word it came from, so two insertions pass: real words whose letters spell a term ({PowerPoint} "the power
    /// point in the kitchen is broken", a wall socket → "The PowerPoint in the kitchen is broken."), and one paid for by a
    /// sound-alike the reply rewrites ("i'm gonna add a name to the dictionary" → "I'm going to add Kynni to the
    /// dictionary.", {Xero} "put zero on the invoice" → "Put 0 on the Xero invoice."). Trigger to retire those two: a typed
    /// reply observed adding a term either way.
    /// </summary>
    private static bool AddsTerm(TermText dictated, TermText reply, IEnumerable<string> terms)
    {
        foreach (var term in terms)
        {
            var whole = TermUnit.Of(Words(MapQuotes(term)).Select(WithoutPossessive).ToList());
            if (whole.Spelt.Count == 0) continue;
            var caseMatters = term.Any(char.IsUpper);
            int? wholeSaid = null; // a part of the term is covered wherever the whole was said in another form; counted when needed
            foreach (var unit in whole.WithParts())
            {
                if (reply.Count(unit, caseMatters) > dictated.Count(unit, caseMatters)
                    && reply.Allowance(unit) > Math.Max(dictated.Allowance(unit), wholeSaid ??= dictated.Allowance(whole)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The dictation or the reply as the term rule reads it: its words as written, after the possessive is removed (where a
    /// term with a capital is counted), and as spoken: lower case, without spoken formatting commands and hesitations (where
    /// a term without capitals is counted, and every allowance).
    /// </summary>
    private sealed class TermText
    {
        private readonly List<string> _asWritten;
        private readonly List<string> _spoken;
        private readonly List<string> _compared;

        private TermText(List<string> asWritten, List<string> spoken)
        {
            _asWritten = asWritten;
            _spoken = spoken;
            _compared = Comparable(spoken);
        }

        public static TermText Of(string text)
        {
            var mapped = MapQuotes(text);
            return new(Words(mapped).Select(WithoutPossessive).ToList(), Words(WithoutCommands(mapped.ToLowerInvariant())).Select(WithoutPossessive).ToList());
        }

        /// <summary>How often the text spells the unit: capitals included when the term has any, else in any case.</summary>
        public int Count(TermUnit unit, bool caseMatters) =>
            caseMatters ? Spans(_asWritten, unit.Spelt, string.Equals) : Spans(_compared, unit.Lower, string.Equals);

        /// <summary>
        /// How often the text has the unit in some form: a word that sounds like it, or would without a final "s" ("kinneys"
        /// for "Kynni's", "loans" for "Loan", "mcdonalds" for "McDonald's"), or a run of words that spells it
        /// (<see cref="JoinedRuns"/>).
        /// </summary>
        public int Allowance(TermUnit unit) => Math.Max(Spans(_compared, unit.Lower, SoundsLikeOrStem), JoinedRuns(_spoken, unit));
    }

    /// <summary>A dictionary term, or one content word of a multi-word term: as spelt, as compared, and its letters joined.</summary>
    private sealed record TermUnit(List<string> Spelt, List<string> Lower, string Letters)
    {
        public static TermUnit Of(List<string> spelt)
        {
            var lower = spelt.Select(w => w.ToLowerInvariant()).ToList();
            return new TermUnit(spelt, Comparable(lower), string.Concat(lower).Replace("'", ""));
        }

        /// <summary>The term, then each content word of a multi-word term on its own.</summary>
        public IEnumerable<TermUnit> WithParts() =>
            Spelt.Count == 1 ? [this] : [this, .. Spelt.Where(w => IsCorrectionWord(w.ToLowerInvariant())).Select(w => Of([w]))];
    }

    private static bool SoundsLikeOrStem(string word, string term) =>
        SoundsLike(word, term) || word.Length > 3 && word.EndsWith('s') && SoundsLike(word[..^1], term);

    // Spoken between the parts of an address: "dictum dot kynni dot com dot au", "kyle at kynni dot com dot au".
    private static readonly HashSet<string> SpokenSeparators = new(StringComparer.Ordinal) { "dot", "at", "dash", "hyphen" };

    /// <summary>
    /// How many runs of consecutive words spell the unit exactly when their letters are joined, either with a spoken "dot",
    /// "at", "dash" or "hyphen" after the first word left out or with every word kept (a term can contain one: "door dash"
    /// spells DoorDash): "pay pal" spells PayPal, "git hub" GitHub, "email" and "e dash mail" e-mail, and "dictum dot kynni
    /// dot com dot au" dictum.kynni.com.au. Exact letters, not sounds, so "can i" never spells Kynni.
    /// </summary>
    private static int JoinedRuns(List<string> words, TermUnit unit)
    {
        var runs = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (Joins(words, i, unit.Letters, skipSeparators: true) || Joins(words, i, unit.Letters, skipSeparators: false)) runs++;
        }
        return runs;
    }

    /// <summary>True when the words from <paramref name="start"/> on, joined, spell exactly <paramref name="letters"/>.</summary>
    private static bool Joins(List<string> words, int start, string letters, bool skipSeparators)
    {
        var run = words[start].Replace("'", "");
        for (var j = start + 1; run.Length < letters.Length && j < words.Count; j++)
        {
            if (!skipSeparators || !SpokenSeparators.Contains(words[j])) run += words[j].Replace("'", "");
        }
        return run == letters;
    }

    /// <summary>"Kynni's" counts as "Kynni".</summary>
    private static string WithoutPossessive(string word) => word.EndsWith("'s", StringComparison.Ordinal) ? word[..^2] : word;

    /// <summary>How many times the words spell the unit, compared word by word.</summary>
    private static int Spans(List<string> words, IReadOnlyList<string> unit, Func<string, string, bool> same)
    {
        var count = 0;
        for (var i = 0; i + unit.Count <= words.Count; i++)
        {
            var j = 0;
            while (j < unit.Count && same(words[i + j], unit[j])) j++;
            if (j == unit.Count) count++;
        }
        return count;
    }

    /// <summary>
    /// Coverage: cleanup may drop fillers, never content. Measured against the dictation with every retracted clause
    /// removed (the longest reading of each "scratch that"), so a correct retraction is not counted as a drop and the rest
    /// of the dictation is still protected. Spelling variants (colour/color) count as the same word.
    /// </summary>
    private static string? DroppedContent(string retracted, List<string> replyWords)
    {
        var rawWords = Comparable(Words(retracted));
        var replySet = Comparable(replyWords).ToHashSet(StringComparer.Ordinal);

        // Observed 15/10/2026: qwen2.5-0.5b on Vulkan returned only the second of two sentences, which every other rule accepted.
        var content = rawWords.Where(IsContent).ToHashSet(StringComparer.Ordinal);
        if (content.Count >= 8 && content.Count(replySet.Contains) is var kept && kept < content.Count * 0.6)
        {
            return $"reply drops dictated content ({kept}/{content.Count} content words kept, minimum 60%)";
        }

        // Per sentence. Observed 15/10/2026 on a 60 s clip with CPU forced: qwen2.5-1.5b dropped one sentence and
        // qwen2.5-0.5b three, and the 60% check accepted both because the rest of the text survived.
        foreach (var sentence in SentenceEnd().Split(retracted))
        {
            var words = Comparable(Words(sentence)).Where(IsContent).ToHashSet(StringComparer.Ordinal);
            if (words.Count < 4) continue;
            var keptHere = words.Count(replySet.Contains);
            if (keptHere * 3 < words.Count) return $"reply drops a dictated sentence ({keptHere}/{words.Count} of its content words kept, minimum a third)";
        }
        return null;
    }

    /// <summary>
    /// "Scratch that" both ways round (lens probes, 15/10/2026): every word of the correction must be in the reply, and the
    /// reply may not keep two thirds or more of the words the shortest retraction removes. Before, "the colour is blue
    /// scratch that the colour is green" → "The colour is blue." was accepted and a correct "Cancel the booking." rejected.
    /// </summary>
    private static string? Retraction(string rawLower, List<string> replyWords)
    {
        var replySet = Comparable(replyWords).ToHashSet(StringComparer.Ordinal);
        foreach (var r in Retractions(rawLower))
        {
            var correction = Comparable(r.Replacement.Where(IsCorrectionWord)).ToHashSet(StringComparer.Ordinal);
            if (correction.Any(w => !replySet.Contains(w))) return "reply drops the correction made with \"scratch that\"";
            var retractedWords = Comparable(r.MinSpan.Where(IsCorrectionWord)).Where(w => !correction.Contains(w)).ToHashSet(StringComparer.Ordinal);
            if (retractedWords.Count > 0 && retractedWords.Count(replySet.Contains) * 3 >= retractedWords.Count * 2)
                return "reply keeps the words retracted with \"scratch that\"";
        }
        return null;
    }

    /// <summary>The dictation with the clause before each "scratch that" (and the phrase) removed.</summary>
    internal static string WithoutRetractedClauses(string lower) => ValidatorText.WithoutRetractedClauses(lower);

    private static string StripWrappingQuotes(string text, string raw)
    {
        if (text.Length < 2) return text;
        var wrapped = (text[0], text[^1]) is ('"', '"') or ('“', '”') or ('\'', '\'');
        var rawWrapped = raw.TrimStart().StartsWith('"') || raw.TrimStart().StartsWith('“');
        return wrapped && !rawWrapped ? text[1..^1].Trim() : text;
    }
}
