using Dictum.Settings;

namespace Dictum.App;

public enum MissingModelChoice { Download, ChooseFile, Quit }

/// <summary>
/// The answer to a download asked for while another runs (decision of 19/10/2026): queue it behind the running one, stop the
/// running one and download this instead, or change nothing (also what Esc or closing the window means).
/// </summary>
public enum BusyDownloadChoice { DownloadAfter, StopAndDownload, Cancel }

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

    /// <summary>
    /// The download question when another download runs: <paramref name="running"/> (its short name) has got
    /// <paramref name="runningProgress"/> ("42%") of the way; <paramref name="chosen"/> is the short name of the model asked for.
    /// The text and the buttons are <see cref="ModelPromptText.BusyText"/> and <see cref="ModelPromptText.BusyChoices"/>.
    /// </summary>
    /// <param name="canWait">False when the file fits only if the running download is stopped: then only Stop and Cancel are offered.</param>
    BusyDownloadChoice ConfirmDownloadWhileBusy(string fileName, string sizeText, string note, string chosen, string running, string runningProgress, bool canWait = true);

    string? ChooseFile(string initialDir, bool cleanupModel);

    /// <summary>
    /// The download question in plain words, for a choice made in the Simple view (1.2): <paramref name="plainName"/> ("More
    /// accurate") instead of the file name, and no catalogue detail (<see cref="ModelPromptText.PlainConfirmText"/>).
    /// <paramref name="warning"/> is the low-space warning, if any. The default asks the catalogue question, so a stand-in that
    /// implements only <see cref="ConfirmDownload"/> still answers it.
    /// </summary>
    bool ConfirmPlainDownload(string fileName, string plainName, string sizeText, string? warning) =>
        ConfirmDownload(fileName, sizeText, warning is null ? ModelPromptText.PlainCheck : $"{ModelPromptText.PlainCheck}. {warning}");

    /// <summary>The busy question in plain words (<see cref="ModelPromptText.PlainBusyText"/>); the default asks the catalogue one.</summary>
    BusyDownloadChoice ConfirmPlainDownloadWhileBusy(string fileName, string plainName, string sizeText, string? warning, string running, string runningProgress, bool canWait = true) =>
        ConfirmDownloadWhileBusy(fileName, sizeText, warning is null ? ModelPromptText.PlainCheck : $"{ModelPromptText.PlainCheck}. {warning}", plainName, running, runningProgress, canWait);
}

public static class ModelPromptText
{
    /// <summary>
    /// The download question. Whether Dictum switches to the model afterwards is the caller's <paramref name="note"/>: a
    /// calibration download only joins the test.
    /// </summary>
    public static string ConfirmText(string fileName, string sizeText, string note) =>
        $"Download {fileName} ({sizeText}) from {ModelCatalog.DownloadHost}?\n\n{note}. Its size and SHA-256 are checked before use; progress shows in the tray panel and the tray icon's tooltip.";

    /// <summary>
    /// PROPOSED copy (21/10/2026, lens on PR #23): what the Simple view's download question says about where the file comes
    /// from and the size and SHA-256 check, without naming the host or the hash.
    /// </summary>
    public const string PlainCheck = "It comes from the model makers' official download page and is checked before use";

    /// <summary>PROPOSED copy (21/10/2026): "Download More accurate (487 MB)? It comes from … checked before use." plus any low-space warning.</summary>
    public static string PlainConfirmText(string plainName, string sizeText, string? warning) =>
        $"Download {plainName} ({sizeText})? {PlainCheck}." + (warning is null ? "" : $"\n\n{warning}.");

    /// <summary>PROPOSED copy (21/10/2026): <see cref="BusyText"/> in plain words, naming the models by their Simple names where they have one.</summary>
    public static string PlainBusyText(string plainName, string sizeText, string? warning, string running, string runningProgress, bool canWait = true) =>
        (canWait
            ? $"{running} is downloading ({runningProgress} done). Download {plainName} ({sizeText}) after it, " +
              $"or stop {running} and download {plainName} now? Stopping deletes what {running} has downloaded so far.\n\n"
            : $"{running} is downloading ({runningProgress} done). There is not enough space to download it after {running}. " +
              $"Stop {running} and download {plainName} ({sizeText}) now? Stopping deletes what {running} has downloaded so far.\n\n") +
        $"{PlainCheck}." + (warning is null ? "" : $" {warning}.");

    /// <summary>
    /// PROPOSED copy (19/10/2026), for the product owner to approve: the download question while another download runs. It
    /// says what is downloading and how far it has got, and what each answer does; nothing is cancelled before it is answered.
    /// </summary>
    /// When <paramref name="canWait"/> is false (it fits only if the running download stops; second lens, 19/10/2026), the
    /// text offers only to stop it and says why (PROPOSED copy, 19/10/2026).
    public static string BusyText(string fileName, string sizeText, string note, string chosen, string running, string runningProgress, bool canWait = true) =>
        (canWait
            ? $"{running} is downloading ({runningProgress} done). Download {fileName} ({sizeText}) from {ModelCatalog.DownloadHost} after it, " +
              $"or stop {running} and download {chosen} now? Stopping deletes what {running} has downloaded so far.\n\n"
            : $"{running} is downloading ({runningProgress} done). There is not enough space to download it after {running}. " +
              $"Stop {running} and download {fileName} ({sizeText}) from {ModelCatalog.DownloadHost} now? Stopping deletes what {running} has downloaded so far.\n\n") +
        $"{note}. Its size and SHA-256 are checked before use; progress shows in the tray panel and the tray icon's tooltip.";

    /// <summary>
    /// PROPOSED copy (19/10/2026): the three buttons of <see cref="BusyText"/>, in the order of <see cref="BusyDownloadChoice"/>.
    /// The Stop button says "it" (the text names the running model), so the label stays short (lens, 19/10/2026).
    /// </summary>
    public static string[] BusyChoices(string chosen, bool canWait = true) =>
        canWait ? ["Download after it", $"Stop it and download {chosen}", "Cancel"] : [$"Stop it and download {chosen}", "Cancel"];

    /// <summary>The answer for the index of the button pressed (-1, the window closed: Cancel).</summary>
    public static BusyDownloadChoice BusyAnswer(int index, bool canWait = true) => (index < 0 ? -1 : canWait ? index : index + 1) switch
    {
        0 => BusyDownloadChoice.DownloadAfter,
        1 => BusyDownloadChoice.StopAndDownload,
        _ => BusyDownloadChoice.Cancel,
    };

    /// <summary>SPEC §10: the configured model is missing at startup.</summary>
    public static string MissingText(string shortName, bool canDownload) => canDownload
        ? $"No speech model found. Download {ModelCatalog.FileName(shortName)} ({ModelCatalog.FormatSize(ModelCatalog.KnownSize(shortName))}) from {ModelCatalog.DownloadHost} now?"
        : "No speech model found at the configured path. Choose a ggml model file, or pick a model in Settings > Models.";
}
