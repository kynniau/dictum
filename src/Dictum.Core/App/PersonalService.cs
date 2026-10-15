using System.Text.Json.Nodes;
using Dictum.Core;
using Dictum.Personal;
using Dictum.Settings;

namespace Dictum.App;

/// <summary>The dictation Fix last dictation works on: kept in memory only, never on disk (SPEC §16.3).</summary>
/// <param name="Target">The window it was pasted into (Paste fix hands the foreground back to it).</param>
/// <param name="AppName">That window's app ("Outlook"), for "pasted 40 s ago into Outlook".</param>
public sealed record LastDictation(string Text, nint Target, string? AppName, DateTime PastedAtUtc);

/// <summary>
/// Batch 2's non-UI side (SPEC §16): the personal text snapshot (dictionary, corrections, profanity list), the last pasted
/// text, the Mode presets and the writes behind "Add word", "Save corrections" and calibration's suggestions. The windows
/// that use it live in Dictum.UI, so none of this is scattered through the dictation code.
/// </summary>
public sealed class PersonalService(AppPaths paths, ILog log, Func<AppSettings> settings, ModelCoordinator models, Action reload, Func<bool> gpuMachine)
{
    /// <summary>What the calibration window's "Download and include" asks the coordinator to say about the download.</summary>
    public const string CalibrationDownload = "Needed for the calibration test. It is added to the test; your current model stays in use";

    private volatile PersonalText _text = PersonalText.Empty;
    private volatile LastDictation? _last;

    public PersonalText Text => _text;

    public LastDictation? Last => _last;

    /// <summary>Re-reads the four text files (load, reload, file watcher). Logs counts only.</summary>
    public PersonalText Load()
    {
        var text = PersonalText.Load(paths, log.Warn);
        _text = text;
        log.Info($"Personal files: {text.Summary(settings().Profanity)}.");
        return text;
    }

    /// <summary>The last step before a paste: corrections, the profanity filter, then the snippets (SPEC §16.3, §16.4, §16.6; order per the fix batch).</summary>
    public string Finish(string text) => _text.Finish(text, settings().Profanity);

    /// <summary>The pipeline pasted <paramref name="text"/> into <paramref name="target"/>: kept in memory for Fix last dictation.</summary>
    public void Remember(string text, nint target, string? appName) => _last = new LastDictation(text, target, appName, DateTime.UtcNow);

    /// <summary>Paste fix pasted the corrected text: it becomes the last dictation, still bound to the same window.</summary>
    public void ReplaceLast(string text)
    {
        if (_last is { } last) _last = last with { Text = text, PastedAtUtc = DateTime.UtcNow };
    }

    /// <summary>Why a term cannot be added to dictionary.txt, or null.</summary>
    public string? CheckNewTerm(string term)
    {
        if (term.Length == 0) return "Type a word or phrase first.";
        if (term.StartsWith('#')) return "A dictionary entry cannot start with #: in dictionary.txt that marks a comment line.";
        return _text.Dictionary.Contains(term) ? $"\"{term}\" is already in the dictionary." : null;
    }

    public void AddDictionaryTerms(IReadOnlyList<string> terms)
    {
        TextList.Append(paths.DictionaryFile, terms, PersonalDictionary.FileHeader);
        log.Info($"Added {terms.Count} term(s) to the dictionary.");
        reload();
    }

    public void AddCorrections(IReadOnlyList<CorrectionRule> rules)
    {
        TextList.Append(paths.CorrectionsFile, rules.Select(r => r.ToString()), Corrections.FileHeader);
        log.Info($"Added {rules.Count} correction(s).");
        reload();
    }

    /// <summary>Creates dictionary.txt (with its header) if needed, for "Open dictionary file".</summary>
    public string EnsureDictionaryFile()
    {
        if (!File.Exists(paths.DictionaryFile)) TextList.Append(paths.DictionaryFile, [], PersonalDictionary.FileHeader);
        return paths.DictionaryFile;
    }

    /// <summary>
    /// Why a snippet cannot be added to snippets.txt, or null (SPEC §16.6). The phrase must fit on one line of the file, and
    /// the profanity filter (which runs before the snippets) must leave it alone, or the snippet could never match.
    /// </summary>
    public string? CheckNewSnippet(string phrase, string text)
    {
        if (phrase.Length == 0) return "Type the words you will say first.";
        if (phrase.Contains('\r') || phrase.Contains('\n')) return "The words you say must fit on one line.";
        if (phrase.StartsWith('#')) return "A snippet cannot start with #: in snippets.txt that marks a comment line.";
        if (phrase.Contains(Corrections.Arrow, StringComparison.Ordinal)) return $"The words you say cannot contain {Corrections.Arrow}.";
        if (!phrase.Any(char.IsLetterOrDigit)) return "The words you say need a letter or a digit in them.";
        if (_text.Profanity.Apply(phrase, settings().Profanity.Mode) != phrase)
            return "The profanity filter would change these words before the snippet runs; choose another phrase.";
        if (text.Length == 0) return "Type what Dictum should paste.";
        return null;
    }

    /// <summary>Appends one snippet; true when an earlier line already had this phrase (the later line wins, so it is replaced).</summary>
    public bool AddSnippet(string phrase, string text)
    {
        var replaces = _text.Snippets.Rules.Any(r => r.Heard == WholeWords.Key(phrase));
        TextList.Append(paths.SnippetsFile, [Corrections.SnippetLine(phrase, text)], Corrections.SnippetsHeader);
        log.Info(replaces ? "Replaced a snippet." : "Added a snippet.");
        reload();
        return replaces;
    }

    /// <summary>Creates snippets.txt (with its header) if needed, for "Open snippets file".</summary>
    public string EnsureSnippetsFile()
    {
        if (!File.Exists(paths.SnippetsFile)) TextList.Append(paths.SnippetsFile, [], Corrections.SnippetsHeader);
        return paths.SnippetsFile;
    }

    /// <summary>The preset these settings match on this hardware (tray panel and Settings → Mode).</summary>
    public ModePreset CurrentPreset => ModePresets.Match(settings(), gpuMachine());

    /// <summary>Mode → Light or Full (SPEC §16.1): writes the preset's values, after downloading any model it needs (asked first).</summary>
    public void SelectPreset(ModePreset preset)
    {
        var gpu = gpuMachine();
        var values = ModePresets.For(preset, gpu);
        var dir = paths.ModelsDir(settings());
        var missing = new List<(DownloadSpec, string)>();
        var speechPath = ModelCatalog.ResolvePath(values.SpeechModel, dir);
        if (!File.Exists(speechPath)) missing.Add((ModelCatalog.Find(values.SpeechModel)!.Download, speechPath));
        if (values.CleanupModel is { } cleanup && CleanupCatalog.ResolvePath(cleanup, dir) is var cleanupPath && !File.Exists(cleanupPath))
            missing.Add((CleanupCatalog.Find(cleanup)!.Download, cleanupPath));
        log.Info($"Mode preset {preset} chosen ({(gpu ? "GPU" : "CPU")}): {values.SpeechModel} + {values.CleanupModel ?? "rule-based tidy"}, {missing.Count} model(s) to download.");
        models.ApplyWithDownloads($"Needed for {preset} mode. {ModelCoordinator.SwitchNote}", missing,
            [.. ModePresets.SettingsValues(values).Select(v => (v.Key, (JsonNode?)JsonValue.Create(v.Value)))]);
    }

    /// <summary>Calibration's "Download and include": the model joins the test; the current model stays in use.</summary>
    public void DownloadForCalibration(string name, Action then, Action<bool> finished)
    {
        var info = ModelCatalog.Find(name)!;
        models.ApplyWithDownloads(CalibrationDownload, [(info.Download, ModelCatalog.ResolvePath(name, paths.ModelsDir(settings())))], [], then, finished);
    }
}
