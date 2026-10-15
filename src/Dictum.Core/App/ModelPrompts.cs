using Dictum.Settings;

namespace Dictum.App;

public enum MissingModelChoice { Download, ChooseFile, Quit }

/// <summary>The user's first-run choice: a catalogue model, a file on disk, or quit.</summary>
public sealed record ModelPick(string? Model, string? FilePath, bool Quit);

/// <summary>First-run cleanup choice: a catalogue model, a .gguf on disk, rule-based tidy only, or quit.</summary>
public sealed record CleanupPick(string? Model, string? FilePath, bool RulesOnly, bool Quit);

/// <summary>
/// The model dialogs, behind an interface so the model logic can be tested without UI. The UI implements them as modal
/// windows (the first-run picker, the missing-model prompt, the download question, the file chooser).
/// </summary>
public interface IModelPrompts
{
    ModelPick FirstRunPicker(string recommended, string reason, Func<ModelInfo, bool> isDownloaded, string modelsDir);

    CleanupPick CleanupPicker(string recommended, string reason, Func<CleanupModelInfo, bool> isDownloaded, string modelsDir);

    MissingModelChoice MissingModel(string shortName, bool canDownload);

    bool ConfirmDownload(string fileName, string sizeText, string note);

    string? ChooseFile(string initialDir, bool cleanupModel);
}

public static class ModelPromptText
{
    /// <summary>
    /// The download question. Whether Dictum switches to the model afterwards is the caller's <paramref name="note"/>: a
    /// calibration download only joins the test.
    /// </summary>
    public static string ConfirmText(string fileName, string sizeText, string note) =>
        $"Download {fileName} ({sizeText}) from {ModelCatalog.DownloadHost}?\n\n{note}. Its size and SHA-256 are checked before use; progress shows in the tray panel and the tray icon's tooltip.";

    /// <summary>SPEC §10: the configured model is missing at startup.</summary>
    public static string MissingText(string shortName, bool canDownload) => canDownload
        ? $"No speech model found. Download {ModelCatalog.FileName(shortName)} ({ModelCatalog.FormatSize(ModelCatalog.KnownSize(shortName))}) from {ModelCatalog.DownloadHost} now?"
        : "No speech model found at the configured path. Choose a ggml model file, or pick a model in Settings > Models.";
}
