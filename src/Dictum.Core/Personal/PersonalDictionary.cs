namespace Dictum.Personal;

/// <summary>
/// dictionary.txt (SPEC §16.2): one term per line, '#' comments. Used as whisper's initial prompt (a comma-separated
/// vocabulary line within whisper's prompt budget, keeping the most recently added terms), as a "Known names and terms"
/// line in the cleanup prompt, and by the cleanup validator, which rejects a reply that adds a term of that line the
/// speaker did not say. An empty or missing file changes no prompt.
/// </summary>
public sealed class PersonalDictionary
{
    /// <summary>whisper.cpp's prompt budget: about 224 tokens (SPEC §16.2).</summary>
    public const int WhisperPromptTokens = 224;

    /// <summary>The cleanup prompt line is capped too; the prompt shares a 4,096-token context with the dictation.</summary>
    public const int CleanupLineChars = 1000;

    public const string CleanupLinePrefix = Llm.CleanupPrompt.DictionaryLinePrefix;

    public const string FileHeader =
        "Dictum personal dictionary: one name or term per line (for example Kynni or Parramatta).\n" +
        "Whisper hears these in its prompt and the cleanup model keeps their spelling. Newest terms last.";

    public static readonly PersonalDictionary Empty = new([]);

    /// <param name="terms">In file order, oldest first; later duplicates move a term to the end (most recent).</param>
    public PersonalDictionary(IEnumerable<string> terms)
    {
        // Newest first, so the later of two duplicate lines is the one kept; then back to file order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in terms.Reverse())
        {
            var term = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (term.Length > 0 && seen.Add(term)) list.Add(term);
        }
        list.Reverse();
        Terms = list;
        var prompt = FitTerms(list, Estimate, WhisperPromptTokens, out var skipped);
        WhisperPromptTerms = prompt.Count;
        SkippedTerms = skipped;
        WhisperPrompt = prompt.Count == 0 ? null : string.Join(", ", prompt);
        var kept = FitTerms(list, t => t.Length + 2, CleanupLineChars - CleanupLinePrefix.Length, out _);
        CleanupTerms = kept;
        CleanupLine = kept.Count == 0 ? null : CleanupLinePrefix + string.Join(", ", kept) + ".";
    }

    /// <summary>The dictionary from its file; a term too long for the prompt on its own is reported by count, never by text.</summary>
    public static PersonalDictionary Load(string path, Action<string>? warn = null)
    {
        var dictionary = new PersonalDictionary(TextList.Read(path, warn));
        if (dictionary.SkippedTerms > 0)
            warn?.Invoke($"dictionary.txt: {dictionary.SkippedTerms} entr{(dictionary.SkippedTerms == 1 ? "y is" : "ies are")} too long for whisper's prompt on its own and left out of it (one name or term per line).");
        return dictionary;
    }

    /// <summary>How many of the terms fit in whisper's prompt (the newest ones).</summary>
    public int WhisperPromptTerms { get; }

    /// <summary>Terms too long for whisper's prompt on their own, left out (a warning gives the count).</summary>
    public int SkippedTerms { get; }

    /// <summary>Distinct terms, oldest first.</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>whisper's initial prompt ("Kynni, Siobhan, Parramatta"), or null when there are no terms.</summary>
    public string? WhisperPrompt { get; }

    /// <summary>The line added to the cleanup system prompt, or null when there are no terms.</summary>
    public string? CleanupLine { get; }

    /// <summary>
    /// The terms in <see cref="CleanupLine"/>, oldest first: the newest that fit its 1,000 characters. The cleanup validator
    /// checks only these; a term the model was never shown cannot have been copied from the prompt.
    /// </summary>
    public IReadOnlyList<string> CleanupTerms { get; }

    public bool Contains(string term) => Terms.Any(t => string.Equals(t, term.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tokens a term may cost in whisper's GPT-2-style vocabulary, plus one for the ", " separator.
    /// </summary>
    // simplification: a character estimate (one token per 3 characters, rounded up) instead of whisper's own tokenizer,
    // which Whisper.net 1.9.1 does not expose. Ceiling: common words cost fewer tokens than estimated, so a long list
    // keeps fewer terms than would fit; a term in a non-Latin script may cost more (whisper.cpp then drops the oldest
    // prompt tokens itself). Trigger to retire: a tokenizer in the binding, or users reporting dropped terms.
    internal static int Estimate(string term) => (term.Length + 2) / 3 + 1;

    /// <summary>
    /// The newest terms whose total cost fits the budget, returned oldest first (newest last, nearest the audio). A single
    /// term over the whole budget is skipped rather than ending the list, so one pasted paragraph cannot empty the prompt.
    /// </summary>
    private static List<string> FitTerms(List<string> terms, Func<string, int> cost, int budget, out int skipped)
    {
        var kept = new List<string>();
        var used = 0;
        skipped = 0;
        for (var i = terms.Count - 1; i >= 0; i--)
        {
            var c = cost(terms[i]);
            if (c > budget)
            {
                skipped++;
                continue;
            }
            if (used + c > budget) break;
            used += c;
            kept.Add(terms[i]);
        }
        kept.Reverse();
        return kept;
    }
}
