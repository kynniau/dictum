using Dictum.Settings;

namespace Dictum.Personal;

/// <summary>
/// One immutable snapshot of the user's plain text files (SPEC §16.2–16.4, §16.6), loaded at every settings load by the
/// tray app and the CLI alike: the dictionary (whisper's prompt and the cleanup prompt), the corrections, the profanity
/// list and the snippets. <see cref="Finish"/> is the last step before a paste: corrections, the profanity filter, then the
/// snippets (SPEC §22, §16.6).
/// </summary>
public sealed class PersonalText(PersonalDictionary dictionary, Corrections corrections, ProfanityFilter profanity, Corrections? snippets = null)
{
    public static readonly PersonalText Empty = new(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty);

    public PersonalDictionary Dictionary { get; } = dictionary;
    public Corrections Corrections { get; } = corrections;
    public ProfanityFilter Profanity { get; } = profanity;

    /// <summary>snippets.txt: "what you say => what is pasted", pasted exactly as written (SPEC §16.6).</summary>
    public Corrections Snippets { get; } = snippets ?? Corrections.Empty;

    public static PersonalText Load(AppPaths paths, Action<string> warn) => new(
        PersonalDictionary.Load(paths.DictionaryFile, warn),
        Corrections.Load(paths.CorrectionsFile, warn),
        ProfanityFilter.Load(paths.ProfanityOverrideFile, paths.ProfanityShippedFile, warn),
        Corrections.LoadSnippets(paths.SnippetsFile, warn));

    /// <summary>
    /// After clean-up: the corrections, then the profanity filter, then the snippets, then the paste. Corrections first
    /// (orchestrator decision, 15/10/2026, reversing SPEC §16.3/16.4's order): the user's own fix of a mishearing applies
    /// even when the misheard word is on the list ("ass => ask" under mask), and the filter still has the last word on what
    /// was dictated. Snippets last (16/10/2026): their text is something the user typed, not dictated, and is pasted exactly;
    /// running them last also means a correction can repair a misheard trigger ("calendar lee => calendly") before the
    /// snippet matches, and no correction or filter can rewrite a word inside a pasted URL or address.
    /// </summary>
    public string Finish(string text, ProfanitySettings profanity) => Snippets.Apply(Profanity.Apply(Corrections.Apply(text), profanity.Mode));

    /// <summary>
    /// Dictated words a cleanup reply may not drop: the profanity list, unless profanity.mode = remove (then dropping them
    /// is what the user asked for). The model would otherwise censor on its own, so profanity.mode is the only control.
    /// </summary>
    public IReadOnlyCollection<string>? WordsTheCleanupMustKeep(ProfanitySettings profanity) =>
        profanity.Mode == ProfanitySettings.Remove || Profanity.Words.Count == 0 ? null : Profanity.Words;

    /// <summary>Counts only, never the words themselves (they can be names, so they stay out of the log).</summary>
    public string Summary(ProfanitySettings profanity) =>
        $"dictionary {Dictionary.Terms.Count} term(s), corrections {Corrections.Rules.Count} rule(s), snippets {Snippets.Rules.Count}, profanity {profanity.Mode} ({Profanity.Words.Count} words, {Profanity.Source})";
}
