using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dictum.Settings;

/// <summary>Creates settings.json with documented defaults and updates single keys in place.</summary>
public static class SettingsFile
{
    /// <summary>Environment override for the model file, used by the integration tests and CLI verification.</summary>
    public const string ModelPathVariable = "DICTUM_MODEL_PATH";

    /// <summary>The same override under the earlier name, still read (after <see cref="ModelPathVariable"/>) so existing scripts keep working.</summary>
    public const string LegacyModelPathVariable = "DICTATE_MODEL_PATH";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The model path override from the environment (DICTUM_MODEL_PATH, else DICTATE_MODEL_PATH), or null.</summary>
    public static string? ModelPathOverride(Func<string, string?> environment) =>
        new[] { ModelPathVariable, LegacyModelPathVariable }.Select(environment).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// The model file to load. Precedence: the --model launch override (name or path), then DICTUM_MODEL_PATH (or its
    /// earlier name DICTATE_MODEL_PATH), then whisper.model. A name is looked up in the model folders (<see cref="ModelFolders"/>).
    /// </summary>
    /// <param name="bundledCheck">The full check for a file in the bundled folder (<see cref="ModelFolders.Find"/>); null: its size.</param>
    public static string EffectiveModelPath(AppSettings settings, AppPaths paths, Func<string, string?> environment, string? launchOverride = null,
        Func<string, bool>? bundledCheck = null)
    {
        if (!string.IsNullOrWhiteSpace(launchOverride)) return SpeechModelPath(launchOverride.Trim(), settings, paths, bundledCheck);
        var overridePath = ModelPathOverride(environment);
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath.Trim().Trim('"'));
        return SpeechModelPath(settings.Whisper.Model, settings, paths, bundledCheck);
    }

    /// <summary>A speech model name or path to a file: an absolute path as it is; a name looked up in the model folders.</summary>
    private static string SpeechModelPath(string model, AppSettings settings, AppPaths paths, Func<string, bool>? bundledCheck) =>
        Path.IsPathFullyQualified(model) ? model : ModelFolders.Resolve(ModelCatalog.FileName(model), ModelCatalog.KnownSize(model), paths, settings, bundledCheck);

    /// <summary>The local cleanup model file: llm.model as an absolute .gguf path, or a name looked up in the model folders.</summary>
    /// <param name="bundledCheck">As for <see cref="EffectiveModelPath"/>.</param>
    public static string CleanupModelPath(AppSettings settings, AppPaths paths, Func<string, bool>? bundledCheck = null) =>
        Path.IsPathFullyQualified(settings.Llm.Model)
            ? settings.Llm.Model
            : ModelFolders.Resolve(CleanupCatalog.FileNameFor(settings.Llm.Model), CleanupCatalog.Find(settings.Llm.Model)?.Bytes, paths, settings, bundledCheck);

    /// <summary>--cleanup-model: for this launch only, the local provider with that model.</summary>
    public static AppSettings WithCleanupOverride(AppSettings settings, string? cleanupModel) => cleanupModel is null
        ? settings
        : settings with { Llm = settings.Llm with { Provider = LlmSettings.Local, Model = cleanupModel } };

    public static string DefaultJson(string model, string? cleanupProvider = null, string? cleanupModel = null)
    {
        var d = new AppSettings();
        var root = new JsonObject
        {
            ["_doc"] = new JsonObject
            {
                ["about"] = "Dictum settings. Save the file and changes are picked up automatically (or use Settings > About > Reload settings). Keys starting with _ are comments. Secrets (API keys) go in the .env file next to this one, never here. Full reference: README.md > Settings reference.",
                ["enabled"] = "Master switch for the hotkey; the switch at the top of the tray panel writes this.",
                ["hotkey"] = "Single key (RightCtrl, CapsLock, F9, Pause, RightAlt...), a mouse button (RightMouse, MiddleMouse, Mouse4, Mouse5; never the left one) or modifiers+key (Ctrl+Win, Ctrl+Shift+Space, Ctrl+Mouse4). The Fn key cannot be used.",
                ["suppress_hotkey"] = "true: the focused app does not see the hotkey. Chords only ever suppress their final non-modifier key. A mouse-button hotkey is always hidden from the app under the pointer, whatever this says.",
                ["tap_ms"] = "A press shorter than this is a tap (start of a double-tap) rather than a hold.",
                ["double_tap_ms"] = "Window for the second tap that starts hands-free mode.",
                ["min_recording_ms"] = "Recordings shorter than this are discarded.",
                ["hands_free_max_seconds"] = "Hands-free stops by itself after this many seconds (30-3600; Settings > General). Hands-free types each part as you pause, and what is left is typed at the stop. A held key always stops after 300 seconds, whatever this says.",
                ["silence_threshold"] = "Recordings whose loudest 20 ms block (RMS, 0-1) stays below this are discarded as silence.",
                ["audio.device"] = "null = the system's default microphone, or a device ID / part of its name (see Settings > Microphone or Dictum.exe --selftest).",
                ["whisper"] = "model: short name (small.en, medium.en, medium.en-q5_0, large-v3-turbo, or the multilingual small, medium, large-v3...) looked up as ggml-<name>.bin in the model folders (see models), or an absolute path. language: a whisper code (en, es, de, ja...) or auto to detect it; anything but en needs a multilingual model (no .en). translate: true writes English whatever language is spoken (multilingual model; large-v3-turbo cannot translate). gpu: auto | vulkan | cpu (change needs a restart). threads: 0 = auto. beam_size: 0 = greedy decoding (fastest), 1-8 = beam search (Calibrate my voice can set it).",
                ["models"] = "folder: null = Dictum's own folder, %LOCALAPPDATA%\\Dictum\\models; or an absolute folder on another drive or a network share (models load slowly from a share), where Dictum looks first and downloads go. It then looks in its own folder, then in the models that come with it (base.en, beside Dictum.exe, never changed). Changing it moves no files. Settings > Models > Model folder writes it.",
                ["llm"] = $"provider: local (a small model on this PC, nothing leaves it; used when this key is absent) | anthropic | openai-compatible | none (rule-based tidy only; the first run writes it when base.en comes with Dictum, and Settings > Models > Add AI clean-up switches to local) | off (no clean-up: the text is pasted as whisper wrote it, with spaces tidied only; spoken commands such as \"new paragraph\" are typed as words). model: for local a cleanup model name ({string.Join(", ", CleanupCatalog.Entries.Select(e => e.Name))}) or an absolute .gguf path; for cloud providers the model id. gpu (local): auto | vulkan | cpu, needs a restart. base_url is required for openai-compatible (e.g. http://localhost:11434/v1 for Ollama; plain http:// only for a server on this PC); for anthropic it is optional and replaces https://api.anthropic.com (the ANTHROPIC_BASE_URL environment variable is ignored). A cloud provider runs only after Settings > Clean-up's consent step, which records it in consent.json; settings.json cannot switch it on. prompt_file: absolute path to a custom system prompt. grammar_assist: true also fixes articles, tenses, prepositions and word order (for speakers whose first language is not English).",
                ["insert"] = "method: paste (clipboard + Ctrl+V) or type (simulated typing for apps that block paste). restore_clipboard restores text formats only. clipboard_history false keeps transcripts out of Win+V history and cloud clipboard.",
                ["indicator"] = "mode: off | orb | edge | both. The orb is a large badge in a corner (orb_corner: bottom-right | bottom-left | top-right | top-left); edge is a red glow round every screen while recording. Both are click-through and never take focus.",
                ["sounds"] = "enabled: true plays a short sound when recording starts and stops.",
                ["profanity"] = "mode: off | mask (first letter kept, rest asterisks) | remove. The word list is profanity.txt beside Dictum.exe; a profanity.txt next to this file replaces it.",
                ["ui"] = "view: simple (the default: three plain speech choices, Standard, More accurate and Most accurate, and Tidy my words) | advanced (every model and its details). The Simple | Advanced switch at the top of Settings > Models writes it; the tray panel's pickers follow it.",
                ["fix_hotkey"] = "null, or a second hotkey (same format as hotkey) that opens Fix last dictation.",
                ["log_transcripts"] = "false: transcript text is never written to the log.",
                ["offline_only"] = "true: no network call of any kind. Cloud providers and model downloads are refused; copy model files in instead.",
            },
            ["enabled"] = d.Enabled,
            ["hotkey"] = d.Hotkey,
            ["suppress_hotkey"] = d.SuppressHotkey,
            ["tap_ms"] = d.TapMs,
            ["double_tap_ms"] = d.DoubleTapMs,
            ["min_recording_ms"] = d.MinRecordingMs,
            ["hands_free_max_seconds"] = d.HandsFreeMaxSeconds,
            ["silence_threshold"] = d.SilenceThreshold,
            ["audio"] = new JsonObject { ["device"] = null },
            ["whisper"] = new JsonObject
            {
                ["model"] = model,
                ["language"] = d.Whisper.Language,
                ["translate"] = d.Whisper.Translate,
                ["threads"] = d.Whisper.Threads,
                ["gpu"] = d.Whisper.Gpu,
                ["beam_size"] = d.Whisper.BeamSize,
            },
            ["models"] = new JsonObject { ["folder"] = null },
            ["llm"] = new JsonObject
            {
                ["provider"] = cleanupProvider ?? d.Llm.Provider,
                ["model"] = cleanupModel ?? d.Llm.Model,
                ["base_url"] = null,
                ["timeout_ms"] = d.Llm.TimeoutMs,
                ["prompt_file"] = null,
                ["gpu"] = d.Llm.Gpu,
                ["grammar_assist"] = d.Llm.GrammarAssist,
            },
            ["insert"] = new JsonObject
            {
                ["method"] = d.Insert.Method,
                ["restore_clipboard"] = d.Insert.RestoreClipboard,
                ["trailing_space"] = d.Insert.TrailingSpace,
                ["paste_delay_ms"] = d.Insert.PasteDelayMs,
                ["clipboard_history"] = d.Insert.ClipboardHistory,
            },
            ["pill"] = new JsonObject { ["enabled"] = d.Pill.Enabled },
            ["indicator"] = new JsonObject { ["mode"] = d.Indicator.Mode, ["orb_corner"] = d.Indicator.OrbCorner },
            ["sounds"] = new JsonObject { ["enabled"] = d.Sounds.Enabled },
            ["profanity"] = new JsonObject { ["mode"] = d.Profanity.Mode },
            ["ui"] = new JsonObject { ["view"] = d.Ui.View },
            ["fix_hotkey"] = null,
            ["log_level"] = d.LogLevel,
            ["log_transcripts"] = d.LogTranscripts,
            ["offline_only"] = d.OfflineOnly,
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Writes the default file if none exists. Returns true if it was created.</summary>
    public static bool CreateIfMissing(string path, string model, string? cleanupProvider = null, string? cleanupModel = null)
    {
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, DefaultJson(model, cleanupProvider, cleanupModel));
        return true;
    }

    /// <summary>
    /// Sets one key (dotted path, e.g. "whisper.model") in the existing file, keeping every other key. Returns false and
    /// leaves the file as it was (and no settings.json.tmp behind) when it cannot be parsed, holds a key twice, or cannot
    /// be written (read-only, locked). JSON comments are not preserved.
    /// </summary>
    public static bool SetValue(string path, string dottedKey, JsonNode? value) => SetValue(path, dottedKey, value, out _);

    /// <inheritdoc cref="SetValue(string, string, JsonNode?)"/>
    /// <param name="failure">Why the file was left as it was (<see cref="ShortReason"/>), or null when the key was written.</param>
    public static bool SetValue(string path, string dottedKey, JsonNode? value, out string? failure)
    {
        failure = null;
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "{}";
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is not JsonObject root)
            {
                failure = Short(SettingsLoader.NotAnObject, path);
                return false;
            }
            var parts = dottedKey.Split('.');
            var target = root;
            foreach (var part in parts[..^1])
            {
                if (target[part] is not JsonObject child)
                {
                    child = new JsonObject();
                    target[part] = child;
                }
                target = child;
            }
            target[parts[^1]] = value;
            WriteAtomic(path, root.ToJsonString(WriteOptions));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            failure = ShortReason(ex, path);
            return false; // ArgumentException: a key given twice ("An item with the same key has already been added")
        }
    }

    /// <summary>
    /// Why a file in the settings folder could not be changed, for a sentence that names the file itself: the exception's
    /// first sentence without the file's path (or the .tmp written beside it) and without the full stop, starting lower case
    /// unless it opens with an acronym ("access to the path is denied").
    /// </summary>
    internal static string ShortReason(Exception ex, string path) => Short(ex.Message, path) is { Length: > 0 } reason ? reason : ex.GetType().Name;

    private static string Short(string message, string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var named in new[] { full + ".tmp", full }) // the .tmp first: it contains the file's own path
            message = message.Replace($"'{named}'", "", StringComparison.OrdinalIgnoreCase).Replace(named, "", StringComparison.OrdinalIgnoreCase);
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        var reason = string.Join(' ', (end >= 0 ? message[..end] : message).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace(" .", ".").TrimEnd('.');
        return reason.Length > 1 && char.IsUpper(reason[0]) && char.IsLower(reason[1]) ? char.ToLowerInvariant(reason[0]) + reason[1..] : reason;
    }

    /// <summary>
    /// Writes <paramref name="content"/> beside <paramref name="path"/>, then moves it over, so a half-written file is never
    /// read. When either step fails the file is as it was, no .tmp is left behind, and the exception is thrown on.
    /// </summary>
    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content + Environment.NewLine);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // best effort; the original file is unchanged either way
            }
            throw;
        }
    }
}
