using System.Text.RegularExpressions;
using static Dictum.Llm.ValidatorText;

namespace Dictum.Llm;

/// <param name="SystemPrompt">The prompt actually sent, so a reply that echoes a custom or dictionary prompt is caught.</param>
/// <param name="MustKeepWords">Words the reply may not drop when dictated (the profanity list unless profanity.mode = remove).</param>
/// <param name="DictionaryTerms">The personal dictionary's terms (SPEC §16.2): the reply may not add one the speaker did not say.</param>
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
        if (context?.DictionaryTerms is { Count: > 0 } terms && AddsTerm(rawWords, replyWords, terms))
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
    /// a multi-word term: when the reply has more of it than the dictation, and also more of it and the words that sound
    /// like it together, it was added, not respelt. Writing "kinney" as "Kynni", the fix the dictionary exists for, swaps
    /// one sound-alike for another, so the second count does not grow. "Sound like" is a spelling-based sound key (the same
    /// consonant sounds in the same order, vowels in the same places); its measure and ceiling are documented at
    /// <see cref="ValidatorText.SoundsLike"/>: a real word that sounds like a term ("canny" for "Kynni") passes as its
    /// misspelling, and a mishearing that adds a sound ("kidney") is rejected as an addition and left to the tidy. Counted
    /// against everything dictated, retracted clauses included: what "scratch that" retracts is ambiguous ("tell kynni it is
    /// at 3, scratch that, at 4"), and a retracted word left in the reply is the retraction rule's to judge. A term that is
    /// also an everyday word ("Will") is counted as the term, so a reply that adds the everyday word is rejected as well.
    /// </summary>
    private static bool AddsTerm(List<string> rawWords, List<string> replyWords, IEnumerable<string> terms)
    {
        var said = Comparable(rawWords.Select(WithoutPossessive));
        var written = Comparable(replyWords.Select(WithoutPossessive));
        foreach (var term in terms)
        {
            var words = Comparable(Words(MapQuotes(term).ToLowerInvariant()).Select(WithoutPossessive));
            var units = new List<IReadOnlyList<string>> { words };
            if (words.Count > 1) units.AddRange(words.Where(IsCorrectionWord).Select(w => new[] { w }));
            foreach (var unit in units.Where(u => u.Count > 0))
            {
                if (Spans(written, unit, string.Equals) > Spans(said, unit, string.Equals) && Spans(written, unit, SoundsLike) > Spans(said, unit, SoundsLike))
                    return true;
            }
        }
        return false;
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
