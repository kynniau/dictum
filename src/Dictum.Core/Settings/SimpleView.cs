namespace Dictum.Settings;

/// <summary>The three plain speech choices of the Simple view (ui.view = "simple"), from the least to the most accurate.</summary>
public enum SpeechTier { Standard, MoreAccurate, MostAccurate }

/// <param name="Tier">Which of the three it is.</param>
/// <param name="Label">The plain name the Simple view shows ("More accurate").</param>
/// <param name="SubLine">The line under it that says what choosing it means.</param>
/// <param name="Model">The speech catalogue entry it selects on this machine class (<see cref="ModelCatalog"/>).</param>
public sealed record TierChoice(SpeechTier Tier, string Label, string SubLine, string Model);

/// <summary>
/// The Simple view of Settings > Models and of the tray panel's pickers (Dictum 1.2, product owner 21/10/2026): three plain
/// speech tiers mapped onto the catalogue, and "Tidy my words" or "Off" for clean-up. It holds no models and no download logic of its
/// own: a tier is one catalogue entry, chosen through the same Select path as the Advanced list. The mapping reuses the
/// catalogue's recommendation and the Mode presets' models rather than a second rule (DECISIONS, 21/10/2026):
/// <code>
/// | Tier          | CPU only (no discrete GPU) | GPU machine (WhisperEngine.GpuMachine) |
/// | Standard      | base.en                    | base.en                                |
/// | More accurate | small.en                   | medium.en                              |
/// | Most accurate | medium.en                  | large-v3-turbo                         |
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

    /// <summary>Standard's line when base.en did not come with this copy of Dictum (a build without the bundled model): "Included" would be untrue.</summary>
    public const string StandardSubLineNotIncluded = "Works well for most people.";

    public const string MoreAccurateSubLine = "Bigger download, a bit slower.";
    public const string MostAccurateSubLine = "Best results. Needs a strong PC or patience.";

    public const string TidyLabel = "Tidy my words";
    public const string TidyTitle = "Tidy my words (recommended)";
    public const string TidySubLine = "Fixes punctuation and removes ums and ahs. Runs on this PC.";

    /// <summary>The Tidy row's state while the recommended clean-up model (not the rule-based tidy) does the tidying.</summary>
    public const string TidyWithAiState = "In use, with AI clean-up";

    // PROPOSED copy (21/10/2026, the clean-up Off lane), for the product owner to approve before release (DECISIONS, 21/10/2026).
    /// <summary>The Simple view's no-clean-up row (llm.provider = off), and the tray panel's Simple clean-up entry.</summary>
    public const string OffLabel = "Off";

    public const string OffSubLine = "Paste exactly what was heard.";

    /// <summary>The same choice in the Advanced view: Settings > Models' clean-up buttons and the tray panel's Advanced clean-up picker.</summary>
    public const string OffAdvancedLabel = "Off (paste exactly what was heard)";

    /// <summary>The Advanced view's "In use:" line suffix while clean-up is off, after the tray name "off".</summary>
    public const string OffInUseNote = " (paste exactly what was heard)";

    /// <summary>"Off" is in use: no clean-up, the text pasted as whisper wrote it with whitespace tidied only.</summary>
    public static bool OffInUse(LlmSettings llm) => llm.Provider == LlmSettings.Off;

    /// <summary>The line under a Custom row: the model was chosen in the Advanced view (or from a file) and is not one of the plain choices.</summary>
    public const string CustomNote = "Chosen in Advanced view";

    /// <summary>A model in use that is none of the plain choices, so nothing is hidden: "Custom: tiny.en".</summary>
    public static string Custom(string name) => $"Custom: {name}";

    /// <summary>A tier whose file is not on this PC: "487 MB download".</summary>
    public static string DownloadSize(string sizeText) => $"{sizeText} download";

    /// <summary>A Simple row's button, named for its tier so a screen reader hears more than "Use": "Use More accurate", "Download More accurate".</summary>
    public static string ActionName(string action, string label) => action == "Use" ? $"Use {label}" : $"Download {label}";

    /// <summary>
    /// The CPU's most accurate choice. The catalogue's own trade-off says "high accuracy; wants a GPU or a fast desktop CPU", and
    /// every larger entry says "needs a GPU"; it is also the model <see cref="ModelCatalog.Recommend"/> gives a GPU machine.
    /// </summary>
    public const string MostAccurateCpu = "medium.en";

    // simplification: the tiers are the English ladder (".en" models, and large-v3-turbo on a GPU). A user whose
    // whisper.language is not English gets the existing language warning after choosing an English-only tier, as in the Advanced
    // list. Retire it when Simple-view users dictate in another language (the Hindi-speaking staff, say): map the tiers to the
    // multilingual entries (small, medium, large-v3-turbo) when whisper.language is not "en".

    /// <summary>
    /// The three tiers on this machine class. Standard is the bundled model (<see cref="ModelCatalog.Bundled"/>, also the Light
    /// preset's). On a CPU, More accurate is the model the catalogue's recommendation offers as "more accurate, a few seconds
    /// slower" (the Full preset's CPU model); on a GPU machine it is the catalogue's GPU recommendation, and Most accurate is the
    /// Full preset's GPU model.
    /// </summary>
    public static IReadOnlyList<TierChoice> Tiers(bool gpuMachine) =>
    [
        new(SpeechTier.Standard, StandardLabel, StandardSubLine, ModePresets.LightSpeech),
        new(SpeechTier.MoreAccurate, MoreAccurateLabel, MoreAccurateSubLine, gpuMachine ? ModelCatalog.Recommend(gpuRuntime: true).Model : ModePresets.FullSpeechCpu),
        new(SpeechTier.MostAccurate, MostAccurateLabel, MostAccurateSubLine, gpuMachine ? ModePresets.FullSpeechGpu : MostAccurateCpu),
    ];

    /// <summary>The tier whose model this is on this machine class, or null (a Custom row).</summary>
    public static TierChoice? TierOf(string model, bool gpuMachine) =>
        Tiers(gpuMachine).FirstOrDefault(t => string.Equals(t.Model, model, StringComparison.OrdinalIgnoreCase));

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
