using System.Globalization;
using System.Text.Json;
using Dictum.Hotkey;

namespace Dictum.Settings;

/// <param name="Usable">False when settings.json exists but could not be read or parsed: its values are unknown.</param>
/// <param name="NamesSpeechModel">The file sets whisper.model (otherwise the first-run speech picker still has a choice to make).</param>
/// <param name="NamesCleanupModel">The file chooses cleanup: llm.model, or a provider other than local.</param>
/// <param name="NamesCleanupProvider">The file sets llm.provider (a first run with base.en on this PC keeps it: ModelCoordinator.FirstRun).</param>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings, bool FileExisted, bool Usable = true,
    bool NamesSpeechModel = true, bool NamesCleanupModel = true, bool NamesCleanupProvider = false);

/// <summary>
/// Tolerant settings.json reader: every key is validated on its own. An unknown key or a malformed value
/// produces a warning and the default for that key; nothing here throws for bad input (SPEC §9).
/// Keys starting with "_" (the "_doc" comments) are ignored.
/// </summary>
public static class SettingsLoader
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The warning for a file whose top level is not an object; also why <see cref="SettingsFile.SetValue(string, string, System.Text.Json.Nodes.JsonNode?, out string?)"/> refuses one.</summary>
    internal const string NotAnObject = "settings.json must contain a JSON object.";

    public static SettingsLoadResult LoadFile(string path)
    {
        if (!File.Exists(path)) return new SettingsLoadResult(new AppSettings(), [], false, NamesSpeechModel: false, NamesCleanupModel: false);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new AppSettings(), [$"Could not read {path} ({ex.GetType().Name})."], true, Usable: false);
        }
        var result = Parse(text);
        return result with { FileExisted = true };
    }

    /// <summary>
    /// A file that does not parse is not usable (<see cref="SettingsLoadResult.Usable"/>): its values are unknown, so the
    /// callers fail closed (<see cref="ConfigLoader"/>) instead of quietly running on defaults.
    /// </summary>
    public static SettingsLoadResult Parse(string json)
    {
        var warnings = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add($"settings.json is not valid JSON (line {ex.LineNumber + 1}). The file was left untouched.");
            return new SettingsLoadResult(new AppSettings(), warnings, true, Usable: false);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add(NotAnObject);
                return new SettingsLoadResult(new AppSettings(), warnings, true, Usable: false);
            }
            var settings = Bind(new ObjectReader(root, "", warnings));
            var namesSpeech = HasString(root, "whisper", "model");
            var namesProvider = HasString(root, "llm", "provider");
            var namesCleanup = HasString(root, "llm", "model")
                || (namesProvider && !string.Equals(settings.Llm.Provider, LlmSettings.Local, StringComparison.Ordinal));
            return new SettingsLoadResult(settings, warnings, true, Usable: true, namesSpeech, namesCleanup, namesProvider);
        }
    }

    private static bool HasString(JsonElement root, string section, string key) =>
        root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
        && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

    private static AppSettings Bind(ObjectReader r)
    {
        var d = new AppSettings();
        var settings = new AppSettings
        {
            Enabled = r.Bool("enabled", d.Enabled),
            Hotkey = r.String("hotkey", d.Hotkey, v => HotkeyParser.TryParse(v, out _, out var e) ? null : e),
            SuppressHotkey = r.Bool("suppress_hotkey", d.SuppressHotkey),
            TapMs = r.Int("tap_ms", d.TapMs, 50, 2000),
            DoubleTapMs = r.Int("double_tap_ms", d.DoubleTapMs, 100, 2000),
            MinRecordingMs = r.Int("min_recording_ms", d.MinRecordingMs, 0, 10000),
            HandsFreeMaxSeconds = r.Int("hands_free_max_seconds", d.HandsFreeMaxSeconds, 5, 3600),
            SilenceThreshold = r.Double("silence_threshold", d.SilenceThreshold, 0, 1),
            Audio = BindAudio(r.Child("audio")),
            Whisper = BindWhisper(r.Child("whisper")),
            Models = BindModels(r.Child("models")),
            Llm = BindLlm(r.Child("llm")),
            Insert = BindInsert(r.Child("insert")),
            Pill = BindPill(r.Child("pill")),
            Indicator = BindIndicator(r.Child("indicator")),
            Sounds = BindSounds(r.Child("sounds")),
            Profanity = BindProfanity(r.Child("profanity")),
            FixHotkey = r.NullableString("fix_hotkey", v => HotkeyParser.TryParse(v, out _, out var e) ? null : e),
            LogLevel = r.String("log_level", d.LogLevel, OneOf("debug", "info", "warning", "error")).ToLowerInvariant(),
            LogTranscripts = r.Bool("log_transcripts", d.LogTranscripts),
            OfflineOnly = r.FailClosedBool("offline_only"),
        };
        r.WarnUnknown();
        if (settings.FixHotkey is { } fix && HotkeyParser.TryParse(fix, out var fixSpec, out _) && HotkeyParser.TryParse(settings.Hotkey, out var main, out _)
            && fixSpec.CanFireTogether(main))
        {
            r.Warn($"Setting \"fix_hotkey\" ({fixSpec.Display}) shares keys with \"hotkey\" ({main.Display}): one press could start a dictation and open Fix last dictation together, so fix_hotkey is ignored. Choose keys the hotkey does not use (for example LeftCtrl instead of Ctrl).");
            settings = settings with { FixHotkey = null };
        }
        if (settings.Models.Folder is { } folder && settings.Whisper.ModelsDir is { } earlier && !string.Equals(folder, earlier, StringComparison.OrdinalIgnoreCase))
            r.Warn("Setting \"whisper.models_dir\" is ignored: \"models.folder\" chooses the models folder.");
        return settings;
    }

    /// <summary>
    /// models.folder and its earlier name whisper.models_dir: an absolute folder (a drive or a network share), or empty for the
    /// default. A device path is refused unless it is \\?\X:\ or \\?\UNC\ (re-lens, 17/10/2026: \\?\Volume{GUID}\ names the
    /// bundled folder by a name that no comparison takes for it, and the bundled base.en was deleted through it): the usual
    /// warning and the default follow.
    /// </summary>
    private static string? FolderProblem(string value) =>
        Path.IsPathFullyQualified(value) && value.IndexOfAny(Path.GetInvalidPathChars()) < 0 && !Storage.PathRegistry.IsUnsupportedDevicePath(value)
            ? null
            : "expected an absolute folder path";

    private static ModelsSettings BindModels(ObjectReader? r)
    {
        if (r is null) return new ModelsSettings();
        var s = new ModelsSettings { Folder = r.NullableString("folder", FolderProblem) };
        r.WarnUnknown();
        return s;
    }

    private static ProfanitySettings BindProfanity(ObjectReader? r)
    {
        if (r is null) return new ProfanitySettings();
        var s = new ProfanitySettings
        {
            Mode = r.String("mode", ProfanitySettings.Off, OneOf(ProfanitySettings.Off, ProfanitySettings.Mask, ProfanitySettings.Remove)).ToLowerInvariant(),
        };
        r.WarnUnknown();
        return s;
    }

    private static PillSettings BindPill(ObjectReader? r)
    {
        if (r is null) return new PillSettings();
        var s = new PillSettings { Enabled = r.Bool("enabled", true) };
        r.WarnUnknown();
        return s;
    }

    private static IndicatorSettings BindIndicator(ObjectReader? r)
    {
        var d = new IndicatorSettings();
        if (r is null) return d;
        var s = new IndicatorSettings
        {
            Mode = r.String("mode", d.Mode, OneOf(IndicatorSettings.Modes)).ToLowerInvariant(),
            OrbCorner = r.String("orb_corner", d.OrbCorner, OneOf(IndicatorSettings.Corners)).ToLowerInvariant(),
        };
        r.WarnUnknown();
        return s;
    }

    private static SoundSettings BindSounds(ObjectReader? r)
    {
        if (r is null) return new SoundSettings();
        var s = new SoundSettings { Enabled = r.Bool("enabled", false) };
        r.WarnUnknown();
        return s;
    }

    /// <summary>
    /// SPEC §22: a plain http:// endpoint is accepted only on this PC (loopback), because anything else would carry the
    /// dictation unencrypted over the network. Null when the URL is acceptable, else the problem.
    /// </summary>
    internal static string? BaseUrlProblem(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "expected an absolute http(s) URL such as \"http://localhost:11434/v1\"";
        return u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback
            ? "http:// is allowed only for a server on this PC (localhost, 127.0.0.1, ::1); use https:// for any other host, so dictations are never sent unencrypted"
            : null;
    }

    private static AudioSettings BindAudio(ObjectReader? r)
    {
        if (r is null) return new AudioSettings();
        var s = new AudioSettings { Device = r.NullableString("device") };
        r.WarnUnknown();
        return s;
    }

    private static WhisperSettings BindWhisper(ObjectReader? r)
    {
        var d = new WhisperSettings();
        if (r is null) return d;
        var s = new WhisperSettings
        {
            Model = r.String("model", d.Model, v => ModelCatalog.IsValidModelSetting(v) ? null : "expected a model name like \"small.en\" or an absolute path to a .bin file"),
            ModelsDir = r.NullableString("models_dir", FolderProblem),
            Language = r.String("language", d.Language, v => Transcribe.WhisperLanguages.IsValid(v.ToLowerInvariant()) ? null : "expected a whisper language code such as \"en\", \"es\" or \"de\" (openai/whisper's list), or \"auto\"").ToLowerInvariant(),
            Translate = r.Bool("translate", d.Translate),
            Threads = r.Int("threads", d.Threads, 0, 64),
            Gpu = r.String("gpu", d.Gpu, OneOf("auto", "vulkan", "cpu")).ToLowerInvariant(),
            BeamSize = r.Int("beam_size", d.BeamSize, 0, 8),
        };
        r.WarnUnknown();
        return s;
    }

    private static LlmSettings BindLlm(ObjectReader? r)
    {
        var d = new LlmSettings();
        if (r is null) return d;
        var s = new LlmSettings
        {
            Provider = r.String("provider", d.Provider, OneOf(LlmSettings.Local, LlmSettings.Anthropic, LlmSettings.OpenAiCompatible, LlmSettings.NoProvider)).ToLowerInvariant(),
            Model = r.String("model", d.Model, v => string.IsNullOrWhiteSpace(v) ? "expected a model id" : null),
            BaseUrl = r.NullableString("base_url", BaseUrlProblem),
            TimeoutMs = r.Int("timeout_ms", d.TimeoutMs, 1000, 120000),
            Gpu = r.String("gpu", d.Gpu, OneOf("auto", "vulkan", "cpu")).ToLowerInvariant(),
            PromptFile = r.NullableString("prompt_file", v => Path.IsPathFullyQualified(v) ? null : "expected an absolute file path"),
            GrammarAssist = r.Bool("grammar_assist", d.GrammarAssist),
        };
        if (r.NullableString("cloud_consent") is not null)
            r.Warn("Setting \"llm.cloud_consent\" is ignored: cloud clean-up is agreed to only in Settings > Clean-up, which records it in consent.json.");
        r.WarnUnknown();
        return s;
    }

    private static InsertSettings BindInsert(ObjectReader? r)
    {
        var d = new InsertSettings();
        if (r is null) return d;
        var s = new InsertSettings
        {
            Method = r.String("method", d.Method, OneOf(InsertSettings.Paste, InsertSettings.Type)).ToLowerInvariant(),
            RestoreClipboard = r.Bool("restore_clipboard", d.RestoreClipboard),
            TrailingSpace = r.Bool("trailing_space", d.TrailingSpace),
            PasteDelayMs = r.Int("paste_delay_ms", d.PasteDelayMs, 0, 2000),
            ClipboardHistory = r.Bool("clipboard_history", d.ClipboardHistory),
        };
        r.WarnUnknown();
        return s;
    }

    private static Func<string, string?> OneOf(params string[] allowed) =>
        v => allowed.Contains(v, StringComparer.OrdinalIgnoreCase) ? null : $"expected one of {string.Join(", ", allowed.Select(a => $"\"{a}\""))}";

    /// <summary>Reads one JSON object, remembering which keys were consumed and collecting warnings.</summary>
    private sealed class ObjectReader(JsonElement obj, string path, List<string> warnings)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private bool TryGet(string key, out JsonElement value)
        {
            _seen.Add(key);
            return obj.TryGetProperty(key, out value);
        }

        private string Full(string key) => path.Length == 0 ? key : $"{path}.{key}";

        /// <summary>A cross-key problem found after binding.</summary>
        public void Warn(string warning) => warnings.Add(warning);

        private T Bad<T>(string key, JsonElement value, string expected, T fallback)
        {
            warnings.Add($"Setting \"{Full(key)}\" has invalid value {Short(value)}: {expected}. Using default {Show(fallback)}.");
            return fallback;
        }

        public bool Bool(string key, bool fallback)
        {
            if (!TryGet(key, out var v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => Bad(key, v, "expected true or false", fallback),
            };
        }

        /// <summary>
        /// A safety switch fails closed: present but neither true nor false (a typo such as "true" in quotes) counts as
        /// true, so a mistake can never quietly turn offline_only off. Absent means false, the default.
        /// </summary>
        public bool FailClosedBool(string key)
        {
            if (!TryGet(key, out var v)) return false;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.ValueKind == JsonValueKind.True;
            warnings.Add($"Setting \"{Full(key)}\" has invalid value {Short(v)}: expected true or false. It is treated as true until it says true or false.");
            return true;
        }

        public int Int(string key, int fallback, int min, int max)
        {
            if (!TryGet(key, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= min && i <= max) return i;
            return Bad(key, v, $"expected a whole number from {min} to {max}", fallback);
        }

        public double Double(string key, double fallback, double min, double max)
        {
            if (!TryGet(key, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) && x >= min && x <= max && double.IsFinite(x)) return x;
            return Bad(key, v, $"expected a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}", fallback);
        }

        public string String(string key, string fallback, Func<string, string?>? validate = null)
        {
            if (!TryGet(key, out var v)) return fallback;
            if (v.ValueKind != JsonValueKind.String) return Bad(key, v, "expected a string", fallback);
            var s = v.GetString()!.Trim();
            var problem = validate?.Invoke(s);
            return problem is null ? s : Bad(key, v, problem, fallback);
        }

        public string? NullableString(string key, Func<string, string?>? validate = null)
        {
            if (!TryGet(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.String) return Bad<string?>(key, v, "expected a string or null", null);
            var s = v.GetString()!.Trim();
            if (s.Length == 0) return null;
            var problem = validate?.Invoke(s);
            return problem is null ? s : Bad<string?>(key, v, problem, null);
        }

        public ObjectReader? Child(string key)
        {
            if (!TryGet(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Object) return new ObjectReader(v, Full(key), warnings);
            warnings.Add($"Setting \"{Full(key)}\" must be an object; using defaults for that section.");
            return null;
        }

        public void WarnUnknown()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in obj.EnumerateObject())
            {
                // JSON allows a repeated name and the reader then sees only one of the two (System.Text.Json matches the last).
                if (!names.Add(p.Name)) warnings.Add($"Setting \"{Full(p.Name)}\" appears more than once; only the last one is used. Remove the other.");
                if (p.Name.StartsWith('_') || _seen.Contains(p.Name)) continue;
                _seen.Add(p.Name);
                warnings.Add($"Unknown setting \"{Full(p.Name)}\" ignored.");
            }
        }

        private static string Short(JsonElement v)
        {
            var raw = v.GetRawText();
            return raw.Length > 60 ? raw[..57] + "..." : raw;
        }

        private static string Show<T>(T value) => value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }
}
