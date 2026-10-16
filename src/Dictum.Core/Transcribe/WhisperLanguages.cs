using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// The languages Whisper can hear, from openai/whisper's tokenizer.py LANGUAGES table (100 entries, read 16/10/2026),
/// with the names it gives them. A code outside this table means nothing to the model, so settings refuse it. The
/// multilingual models (small, medium, large-v3…) hear all of them; the ".en" models hear English only, and every
/// multilingual model except large-v3-turbo can be asked to write English for any of them
/// (<see cref="WhisperSettings.Translate"/>): OpenAI's README says the turbo model "is not trained for translation tasks".
/// Not in the table, so not possible with Whisper: Cebuano, Ilocano, Hiligaynon and the other Philippine languages
/// apart from Tagalog. <see cref="Problem"/> is the one place that knows these limits; Settings, the engine's load
/// warning, every speech-model load and --selftest all ask it.
/// </summary>
public static class WhisperLanguages
{
    /// <summary>The settings value that lets whisper detect the language from the first seconds of each dictation.</summary>
    public const string Auto = "auto";

    public const string English = "en";

    /// <summary>The catalogue models that hear every language, as the messages name them.</summary>
    public const string MultilingualChoices = "small, medium or large-v3";

    /// <summary>The catalogue models that translate, as the messages name them (OpenAI's README: medium or large).</summary>
    public const string TranslationChoices = "medium or large-v3";

    private static readonly (string Code, string Name)[] Table =
    [
        ("en", "English"), ("zh", "Chinese"), ("de", "German"), ("es", "Spanish"), ("ru", "Russian"), ("ko", "Korean"),
        ("fr", "French"), ("ja", "Japanese"), ("pt", "Portuguese"), ("tr", "Turkish"), ("pl", "Polish"), ("ca", "Catalan"),
        ("nl", "Dutch"), ("ar", "Arabic"), ("sv", "Swedish"), ("it", "Italian"), ("id", "Indonesian"), ("hi", "Hindi"),
        ("fi", "Finnish"), ("vi", "Vietnamese"), ("he", "Hebrew"), ("uk", "Ukrainian"), ("el", "Greek"), ("ms", "Malay"),
        ("cs", "Czech"), ("ro", "Romanian"), ("da", "Danish"), ("hu", "Hungarian"), ("ta", "Tamil"), ("no", "Norwegian"),
        ("th", "Thai"), ("ur", "Urdu"), ("hr", "Croatian"), ("bg", "Bulgarian"), ("lt", "Lithuanian"), ("la", "Latin"),
        ("mi", "Maori"), ("ml", "Malayalam"), ("cy", "Welsh"), ("sk", "Slovak"), ("te", "Telugu"), ("fa", "Persian"),
        ("lv", "Latvian"), ("bn", "Bengali"), ("sr", "Serbian"), ("az", "Azerbaijani"), ("sl", "Slovenian"), ("kn", "Kannada"),
        ("et", "Estonian"), ("mk", "Macedonian"), ("br", "Breton"), ("eu", "Basque"), ("is", "Icelandic"), ("hy", "Armenian"),
        ("ne", "Nepali"), ("mn", "Mongolian"), ("bs", "Bosnian"), ("kk", "Kazakh"), ("sq", "Albanian"), ("sw", "Swahili"),
        ("gl", "Galician"), ("mr", "Marathi"), ("pa", "Punjabi"), ("si", "Sinhala"), ("km", "Khmer"), ("sn", "Shona"),
        ("yo", "Yoruba"), ("so", "Somali"), ("af", "Afrikaans"), ("oc", "Occitan"), ("ka", "Georgian"), ("be", "Belarusian"),
        ("tg", "Tajik"), ("sd", "Sindhi"), ("gu", "Gujarati"), ("am", "Amharic"), ("yi", "Yiddish"), ("lo", "Lao"),
        ("uz", "Uzbek"), ("fo", "Faroese"), ("ht", "Haitian Creole"), ("ps", "Pashto"), ("tk", "Turkmen"), ("nn", "Nynorsk"),
        ("mt", "Maltese"), ("sa", "Sanskrit"), ("lb", "Luxembourgish"), ("my", "Myanmar"), ("bo", "Tibetan"), ("tl", "Tagalog"),
        ("mg", "Malagasy"), ("as", "Assamese"), ("tt", "Tatar"), ("haw", "Hawaiian"), ("ln", "Lingala"), ("ha", "Hausa"),
        ("ba", "Bashkir"), ("jw", "Javanese"), ("su", "Sundanese"), ("yue", "Cantonese"),
    ];

    /// <summary>
    /// The 100th entry: openai/whisper's tokenizer takes the first num_languages of the table (99 unless the model's
    /// vocabulary has the extra token; read in tokenizer.py 16/10/2026), and the large-v3 family is the one with 100
    /// (recalled from openai/whisper's model loading, not re-read).
    /// </summary>
    private const string Cantonese = "yue";

    private static readonly Dictionary<string, string> Names = Table.ToDictionary(t => t.Code, t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// The languages offshore teams most often speak, first in the picker: English, then Hindi and Tagalog (the two the
    /// request named), then the other Indian-subcontinent languages Whisper has. Tagalog is the only Philippine language
    /// in Whisper.
    /// </summary>
    public static readonly string[] Featured = ["en", "hi", "tl", "bn", "te", "mr", "ta", "ur", "gu", "kn", "ml", "pa", "as", "ne", "sd", "si"];

    /// <summary>Every code the table has, in its order.</summary>
    public static IReadOnlyList<string> Codes { get; } = Table.Select(t => t.Code).ToList();

    /// <summary>True for "auto" or a code in the table.</summary>
    public static bool IsValid(string code) => code == Auto || Names.ContainsKey(code);

    /// <summary>"Hindi" for "hi"; "Detect automatically" for "auto"; the code itself when unknown.</summary>
    public static string Name(string code) => code == Auto ? "Detect automatically" : Names.TryGetValue(code, out var name) ? name : code;

    /// <summary>"hi (Hindi)" for logs and --selftest; "auto (detected per dictation)".</summary>
    public static string Describe(string code) => code == Auto ? "auto (detected per dictation)" : $"{code} ({Name(code)})";

    /// <summary>
    /// Whether the text whisper writes is English: always with translate on, otherwise only for language "en" ("auto"
    /// without translate writes whatever was spoken). The clean-up is written for English and is skipped otherwise.
    /// </summary>
    public static bool OutputIsEnglish(string language, bool translate) => translate || language == English;

    /// <summary>
    /// The picker's order: "auto", the featured languages, then the rest by name. Each item is (code, label), the label
    /// "Hindi (hi)" so the settings.json value is visible.
    /// </summary>
    public static IReadOnlyList<(string Code, string Label)> PickerItems()
    {
        var rest = Table.Where(t => !Featured.Contains(t.Code)).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Code);
        return new[] { Auto }.Concat(Featured).Concat(rest).Select(c => (c, c == Auto ? Name(c) : $"{Name(c)} ({c})")).ToList();
    }

    /// <summary>Whether a catalogue model name or file is one of the English-only ".en" models.</summary>
    public static bool IsEnglishOnlyModel(string modelNameOrPath)
    {
        var name = ModelCatalog.DisplayName(modelNameOrPath);
        return name.EndsWith(".en", StringComparison.OrdinalIgnoreCase) || name.Contains(".en-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a model is a large-v3-turbo file, which transcribes any language but cannot translate (OpenAI's README).</summary>
    public static bool CannotTranslate(string modelNameOrPath) =>
        ModelCatalog.DisplayName(modelNameOrPath).StartsWith("large-v3-turbo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why this model cannot do what the language settings ask, in one sentence for Settings, the warning balloon, the
    /// log and --selftest; null when it can. Judged from the model's name (a catalogue name, or a file named like one), so
    /// an unknown file is trusted for every language but Cantonese, which is allowed only for names starting with large-v3
    /// (<see cref="Cantonese"/>).
    /// </summary>
    public static string? Problem(string model, string language, bool translate)
    {
        var name = ModelCatalog.DisplayName(model);
        if (language != English && IsEnglishOnlyModel(model))
            return $"{name} is an English-only model, so language \"{language}\" cannot be heard: choose a multilingual model ({MultilingualChoices}) in Settings > Models.";
        if (translate && IsEnglishOnlyModel(model))
            return $"{name} is an English-only model and cannot translate: turn Translate off, or choose {TranslationChoices} in Settings > Models.";
        if (translate && CannotTranslate(model))
            return $"{name} cannot translate (OpenAI: the turbo model \"is not trained for translation tasks\"): choose {TranslationChoices} in Settings > Models.";
        if (language == Cantonese && !name.StartsWith("large-v3", StringComparison.OrdinalIgnoreCase))
            return $"{name} has no Cantonese (yue): only the large-v3 models' vocabulary has it; choose large-v3 in Settings > Models.";
        return null;
    }

    /// <summary>
    /// The Mode page's warning: each preset whose speech model (Light: base.en; Full: small.en on a CPU, large-v3-turbo on a
    /// GPU) cannot do what the language settings ask, with the reason; null when every preset can.
    /// </summary>
    public static string? ModeNote(string language, bool translate, bool gpuMachine)
    {
        var failing = new List<string>();
        foreach (var preset in new[] { ModePreset.Light, ModePreset.Full })
        {
            var model = ModePresets.For(preset, gpuMachine).SpeechModel;
            if (Problem(model, language, translate) is { } problem) failing.Add($"{preset} would write {model}: {problem}");
        }
        return failing.Count == 0 ? null : string.Join(" ", failing);
    }
}
