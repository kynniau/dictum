using Dictum.Transcribe;

namespace Dictum.Settings;

/// <summary>The plain speech choices of the Simple view (ui.view = "simple"), from the least to the most accurate.</summary>
public enum SpeechTier { Standard, MoreAccurate, MostAccurate }

/// <param name="Tier">Which choice it is.</param>
/// <param name="Label">The plain name the Simple view shows ("More accurate").</param>
/// <param name="SubLine">The line under it that says what choosing it means on this machine class.</param>
/// <param name="Model">The speech catalogue entry it selects (<see cref="ModelCatalog"/>).</param>
/// <param name="Guard">
/// Why the Simple view cannot offer it with the language settings in force (the row shows this line and no button, and the tray
/// leaves it out), or null when it can.
/// </param>
public sealed record TierChoice(SpeechTier Tier, string Label, string SubLine, string Model, string? Guard = null);

/// <summary>
/// The Simple view of Settings > Models and of the tray panel's pickers (Dictum 1.2, product owner 21/10/2026): plain speech
/// tiers mapped onto the catalogue, and "Tidy my words" or "Off" for clean-up. It holds no models and no download logic of its
/// own: a tier is one catalogue entry, chosen through the same Select path as the Advanced list. The mapping reuses the
/// catalogue's recommendation and the Mode presets' models rather than a second rule (DECISIONS, 21/10/2026):
/// <code>
/// | whisper.language "en" (no translate) | CPU only  | GPU machine (WhisperEngine.GpuMachine) |
/// | Standard                             | base.en   | base.en                                |
/// | More accurate                        | small.en  | medium.en                              |
/// | Most accurate                        | medium.en | large-v3-turbo                         |
///
/// | any other language, or translate     | CPU only  | GPU machine                            |
/// | Standard                             | small     | small                                  |
/// | More accurate                        | medium    | medium                                 |
/// | Most accurate                        | (none)    | large-v3-turbo; large-v3 to translate  |
/// </code>
/// </summary>
public static class SimpleView
{
    // PROPOSED copy (21/10/2026), for the product owner to approve before release (DECISIONS, 21/10/2026). Never reword the
    // approved copy elsewhere to match it.
    public const string SimpleLabel = "Simple";
    public const string AdvancedLabel = "Advanced";
    public const string SwitchHelp = "Simple shows what each choice means. Advanced shows every model and its details.";

    /// <summary>
    /// The explainer at the top of the Simple speech card. The brief's wording named "Settings > Test my setup", which does not
    /// exist in this build; the existing calibration test ("Calibrate my voice", the button under this text) is named instead.
    /// </summary>
    public const string Explainer = "Dictum turns your voice into text on this PC. The speech model does the listening. Standard works for most people. " +
                                    "If words come out wrong, try a more accurate model, or press Calibrate my voice to see which hears you best.";

    public const string StandardLabel = "Standard";
    public const string MoreAccurateLabel = "More accurate";
    public const string MostAccurateLabel = "Most accurate";
    public const string StandardSubLine = "Included. Works well for most people.";

    /// <summary>Standard's line when its model did not come with this copy of Dictum (a multilingual Standard, or a build without base.en).</summary>
    public const string StandardSubLineNotIncluded = "Works well for most people.";

    /// <summary>
    /// The lines on a GPU machine. On a CPU (lens on PR #23, 21/10/2026) the measured times say more: on the development PC's
    /// CPU (README "Measured speed", 15/10/2026, a 10.46 s clip) base.en took 1.90 s, small.en 6.43 s and medium.en 20.1 s.
    /// </summary>
    public const string MoreAccurateSubLine = "Bigger download, a bit slower.";
    public const string MostAccurateSubLine = "Best results. Needs a strong PC or patience.";

    /// <summary>A small model on a CPU: small.en measured 6.43 s for a 10.46 s clip, 4.5 s more than base.en.</summary>
    public const string CpuMoreAccurateSubLine = "Bigger download. Takes a few seconds longer on this PC.";

    /// <summary>A medium model on a CPU: medium.en measured 20.1 s for a 10.46 s clip, longer than the speech itself.</summary>
    public const string CpuMostAccurateSubLine = "Slow without a graphics card: can take longer than you spoke.";

    public const string TidyLabel = "Tidy my words";
    public const string TidyTitle = "Tidy my words (recommended)";
    public const string TidySubLine = "Fixes punctuation and removes ums and ahs. Runs on this PC.";

    /// <summary>The Tidy row's state while the recommended clean-up model (not the rule-based tidy) does the tidying.</summary>
    public const string TidyWithAiState = "In use, with AI clean-up";

    // PROPOSED copy (21/10/2026, the clean-up Off lane), for the product owner to approve before release (DECISIONS, 21/10/2026).
    /// <summary>The Simple view's no-clean-up row (llm.provider = off), and the tray panel's Simple clean-up entry.</summary>
    public const string OffLabel = "Off";

    /// <summary>
    /// Off's line. Off skips the tidy only: the user's own corrections, snippets and the profanity filter (when on) still run
    /// after it, as with every clean-up, so the line says so rather than "exactly" (final lens, orchestrator's decision, 21/10/2026).
    /// </summary>
    public const string OffSubLine = "Paste what was heard. Only your own corrections, snippets and the profanity filter (if on) still apply.";

    /// <summary>The same choice in the Advanced view: Settings > Models' clean-up buttons and the tray panel's Advanced clean-up picker.</summary>
    public const string OffAdvancedLabel = "Off (paste what was heard)";

    /// <summary>The Advanced view's "In use:" line suffix while clean-up is off, after the tray name "no clean-up".</summary>
    public const string OffInUseNote = " (paste what was heard; your corrections, snippets and the profanity filter still apply)";

    /// <summary>
    /// "Add AI clean-up" is offered (Settings > Models and the tray panel): while the rule-based tidy is in use, and, in the
    /// Simple view, only while the text is English, since no clean-up model runs on any other (TranscriptCleaner.Plain; lens
    /// on 787b300). The Advanced view offers it whatever the language, with every other detail. One predicate for both places.
    /// </summary>
    public static bool OffersAddCleanup(AppSettings s) =>
        s.Llm.Provider == LlmSettings.NoProvider && (!s.Ui.IsSimple || WhisperLanguages.OutputIsEnglish(s.Whisper.Language, s.Whisper.Translate));

    /// <summary>"Off" is in use: no clean-up, the text pasted as whisper wrote it with whitespace tidied only.</summary>
    public static bool OffInUse(LlmSettings llm) => llm.Provider == LlmSettings.Off;

    /// <summary>
    /// The line of a Custom row (lens on PR #23): the model in use is none of the choices above. It may have come from the
    /// Advanced view, calibration, a Mode preset, the first run, --model or DICTUM_MODEL_PATH, so the line does not say which.
    /// </summary>
    public const string CustomNote = "In use. Not one of the choices above.";

    /// <summary>A model in use that is none of the plain choices, so nothing is hidden: "Custom: tiny.en".</summary>
    public static string Custom(string name) => $"Custom: {name}";

    /// <summary>A Simple row's button, named for its tier so a screen reader hears more than "Use": "Use More accurate", "Download More accurate".</summary>
    public static string ActionName(string action, string label) => action == "Use" ? $"Use {label}" : $"Download {label}";

    /// <summary>
    /// The Tidy row's line. Clean-up is written for English: a dictation whose text is not English gets whitespace tidied only
    /// (TranscriptCleaner.Plain), so the line says so (lens on PR #23).
    /// </summary>
    public static string TidyLine(string language, bool translate) =>
        WhisperLanguages.OutputIsEnglish(language, translate) ? TidySubLine
        : language == WhisperLanguages.Auto ? "Tidies spacing only while the language is detected automatically."
        : $"Tidies spacing only for {WhisperLanguages.Name(language)}.";

    /// <summary>
    /// The line of a tier the Simple view cannot offer with these language settings, or null: the existing
    /// <see cref="WhisperLanguages.Problem"/> sentence (Cantonese on a model without it, say). An English-only model never needs
    /// a line of its own: <see cref="Tiers"/> offers one only for plain English without translate, which it can do (lens on
    /// 787b300, 21/10/2026, removed the unreachable "English only: …" lines).
    /// </summary>
    public static string? GuardLine(string model, string language, bool translate) => WhisperLanguages.Problem(model, language, translate);

    /// <summary>
    /// The CPU's most accurate English choice. The catalogue's own trade-off says "high accuracy; wants a GPU or a fast desktop
    /// CPU", and every larger entry says "needs a GPU"; it is also the model <see cref="ModelCatalog.Recommend"/> gives a GPU machine.
    /// </summary>
    public const string MostAccurateCpu = "medium.en";

    /// <summary>The multilingual choices (lens on PR #23, orchestrator's decision, 21/10/2026): small, then medium.</summary>
    public const string MultilingualStandard = "small";
    public const string MultilingualMoreAccurate = "medium";

    /// <summary>The tiers are the multilingual ones: the text is not plain English speech (another language, Detect automatically, or translate).</summary>
    public static bool Multilingual(string language, bool translate) => language != WhisperLanguages.English || translate;

    /// <summary>
    /// The tiers on this machine class for these language settings. English: Standard is the bundled model
    /// (<see cref="ModelCatalog.Bundled"/>, also the Light preset's); on a CPU, More accurate is the model the catalogue's
    /// recommendation offers as "more accurate, a few seconds slower" (the Full preset's CPU model); on a GPU machine it is the
    /// catalogue's GPU recommendation, and Most accurate is the Full preset's GPU model. Any other language: small and medium,
    /// and on a GPU large-v3-turbo (large-v3 when translating, which turbo cannot do). A CPU has no multilingual Most accurate:
    /// the catalogue has nothing above medium whose trade-off line does not say "needs a GPU". Every tier carries a
    /// <see cref="TierChoice.Guard"/> when the language settings rule its model out.
    /// </summary>
    public static IReadOnlyList<TierChoice> Tiers(bool gpuMachine, string language = WhisperLanguages.English, bool translate = false)
    {
        List<TierChoice> tiers = Multilingual(language, translate)
            ?
            [
                new(SpeechTier.Standard, StandardLabel, StandardSubLine, MultilingualStandard),
                new(SpeechTier.MoreAccurate, MoreAccurateLabel, gpuMachine ? MoreAccurateSubLine : CpuMostAccurateSubLine, MultilingualMoreAccurate),
                .. gpuMachine ? [new TierChoice(SpeechTier.MostAccurate, MostAccurateLabel, MostAccurateSubLine, translate ? "large-v3" : ModePresets.FullSpeechGpu)] : Array.Empty<TierChoice>(),
            ]
            :
            [
                new(SpeechTier.Standard, StandardLabel, StandardSubLine, ModePresets.LightSpeech),
                new(SpeechTier.MoreAccurate, MoreAccurateLabel, gpuMachine ? MoreAccurateSubLine : CpuMoreAccurateSubLine,
                    gpuMachine ? ModelCatalog.Recommend(gpuRuntime: true).Model : ModePresets.FullSpeechCpu),
                new(SpeechTier.MostAccurate, MostAccurateLabel, gpuMachine ? MostAccurateSubLine : CpuMostAccurateSubLine, gpuMachine ? ModePresets.FullSpeechGpu : MostAccurateCpu),
            ];
        return [.. tiers.Select(t => t with { Guard = GuardLine(t.Model, language, translate) })];
    }

    /// <summary>The tier whose model this is with these settings, or null (a Custom row).</summary>
    public static TierChoice? TierOf(string model, bool gpuMachine, string language = WhisperLanguages.English, bool translate = false) =>
        Tiers(gpuMachine, language, translate).FirstOrDefault(t => string.Equals(t.Model, model, StringComparison.OrdinalIgnoreCase));

    // PROPOSED copy (1.2 polish, 22/10/2026), for the product owner to approve before release.
    /// <summary>The tray panel header's clean-up name in the Simple view while the recommended clean-up model does the tidying.</summary>
    public const string AiCleanupLabel = "AI clean-up";

    /// <summary>A speech model by its Simple name: its tier's label with these settings, else "Custom: name" (the tray panel's header).</summary>
    public static string SpeechName(string model, bool gpuMachine, string language = WhisperLanguages.English, bool translate = false) =>
        TierOf(model, gpuMachine, language, translate)?.Label ?? Custom(model);

    /// <summary>
    /// The clean-up in use by its Simple name (the tray panel's header, 1.2 polish): "Off"; "AI clean-up" while the recommended
    /// model is chosen and the text is English (no clean-up model runs on any other: TranscriptCleaner.Plain); "Tidy my words"
    /// for the rest of the Tidy row; "Custom: name" for anything else (<paramref name="currentName"/>, the coordinator's name for it).
    /// </summary>
    public static string CleanupName(LlmSettings llm, bool gpuMachine, string language, bool translate, string currentName) =>
        OffInUse(llm) ? OffLabel
        : !TidyInUse(llm, gpuMachine) ? Custom(currentName)
        : llm.Provider == LlmSettings.Local && WhisperLanguages.OutputIsEnglish(language, translate) ? AiCleanupLabel
        : TidyLabel;

    /// <summary>
    /// The tray panel's line under Light | Full in the Simple view (PROPOSED, 1.2 polish): the presets by their Simple names,
    /// "Full: More accurate and AI clean-up. Light: Standard and Tidy my words." The Advanced view keeps the model names.
    /// </summary>
    public static string ModeHint(bool gpuMachine, string language, bool translate) =>
        $"Full: {SpeechName(ModePresets.For(ModePreset.Full, gpuMachine).SpeechModel, gpuMachine, language, translate)} and {AiCleanupLabel}. " +
        $"Light: {SpeechName(ModePresets.LightSpeech, gpuMachine, language, translate)} and {TidyLabel}.";

    /// <summary>The clean-up model "Add AI clean-up" adds, and the Tidy row uses when it is on this PC.</summary>
    public static string TidyModel(bool gpuMachine) => CleanupCatalog.Recommend(gpuMachine).Model;

    /// <summary>
    /// "Tidy my words" is in use: the rule-based tidy (llm.provider none), or the local recommended clean-up model. Off is its
    /// own row (<see cref="OffInUse"/>). Anything else (another local model, a .gguf file, a cloud provider chosen in the
    /// Advanced view) is a Custom row.
    /// </summary>
    public static bool TidyInUse(LlmSettings llm, bool gpuMachine) => llm.Provider switch
    {
        LlmSettings.NoProvider => true,
        LlmSettings.Local => string.Equals(CleanupCatalog.FindByFile(llm.Model)?.Name ?? llm.Model, TidyModel(gpuMachine), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
