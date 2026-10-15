using Dictum.Settings;

namespace Dictum.Personal;

/// <summary>
/// One immutable snapshot of the user's plain text files (SPEC §16.2–16.4), loaded at every settings load by the tray
/// app and the CLI alike: the dictionary (whisper's prompt and the cleanup prompt), the corrections and the profanity
/// list. <see cref="Finish"/> is the last step before a paste: corrections, then the profanity filter (SPEC §22).
/// </summary>
public sealed class PersonalText(PersonalDictionary dictionary, Corrections corrections, ProfanityFilter profanity)
{
    public static readonly PersonalText Empty = new(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty);

    public PersonalDictionary Dictionary { get; } = dictionary;
    public Corrections Corrections { get; } = corrections;
    public ProfanityFilter Profanity { get; } = profanity;

    public static PersonalText Load(AppPaths paths, Action<string> warn) => new(
        PersonalDictionary.Load(paths.DictionaryFile, warn),
        Corrections.Load(paths.CorrectionsFile, warn),
        ProfanityFilter.Load(paths.ProfanityOverrideFile, paths.ProfanityShippedFile, warn));

    /// <summary>
    /// After clean-up: the corrections, then the profanity filter, then the paste. Corrections first (orchestrator decision,
    /// 15/10/2026, reversing SPEC §16.3/16.4's order): the user's own fix of a mishearing applies even when the misheard
    /// word is on the list ("ass => ask" under mask), and the filter still has the last word on what is pasted.
    /// </summary>
    public string Finish(string text, ProfanitySettings profanity) => Profanity.Apply(Corrections.Apply(text), profanity.Mode);

    /// <summary>
    /// Dictated words a cleanup reply may not drop: the profanity list, unless profanity.mode = remove (then dropping them
    /// is what the user asked for). The model would otherwise censor on its own, so profanity.mode is the only control.
    /// </summary>
    public IReadOnlyCollection<string>? WordsTheCleanupMustKeep(ProfanitySettings profanity) =>
        profanity.Mode == ProfanitySettings.Remove || Profanity.Words.Count == 0 ? null : Profanity.Words;

    /// <summary>Counts only, never the words themselves (they can be names, so they stay out of the log).</summary>
    public string Summary(ProfanitySettings profanity) =>
        $"dictionary {Dictionary.Terms.Count} term(s), corrections {Corrections.Rules.Count} rule(s), profanity {profanity.Mode} ({Profanity.Words.Count} words, {Profanity.Source})";
}
