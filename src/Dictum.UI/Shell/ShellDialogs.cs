using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dictum.App;
using Dictum.Settings;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;

namespace Dictum.UI.Shell;

/// <summary>
/// The shell's questions as Dictum windows (every one modal, centred, on the UI thread; a call from another thread waits).
/// Replaces the WinForms TaskDialogs and message boxes of the earlier build.
/// </summary>
public sealed class ShellDialogs : IShellDialogs
{
    public int Ask(string title, string text, params DialogChoice[] choices) => Modal.OnUi(() =>
    {
        var window = new QuestionWindow(title, text, choices);
        Modal.Run(window);
        return window.Choice;
    });

    public void Tell(string title, string text) => Ask(title, text, new DialogChoice("OK", IsDefault: true));

    public bool TypeToConfirm(string title, string text, string word) => Modal.OnUi(() =>
    {
        var window = new ConfirmWordWindow(title, text, word);
        Modal.Run(window);
        return window.Confirmed;
    });

    public void Onboard(OnboardingFlow flow) => Modal.OnUi(() =>
    {
        new OnboardingWindow(flow).RunFirstSteps();
        return true;
    });

    public ModelPick FirstRunPicker(string recommended, string reason, Func<ModelInfo, bool> isDownloaded, string modelsDir)
    {
        var options = ModelCatalog.Entries.Select(e => new PickerOption(e.Name,
            e.ItemText(isDownloaded(e)) + (e.Name == recommended ? " · recommended" : ""), e.TradeOff)).ToList();
        var text = "Bigger models are more accurate but slower: on a PC without a GPU, base.en keeps dictation quick and small.en is more accurate, " +
                   $"a few seconds slower. Recommended here: {recommended} ({reason}). A model that is not downloaded yet is fetched from " +
                   $"{ModelCatalog.DownloadHost}. You can change this later in Settings > Models.";
        var window = Modal.OnUi(() =>
        {
            var w = new ModelPickerWindow("Choose a speech model", text, options, recommended, "Use selected model", "Choose model file…");
            Modal.Run(w);
            return w;
        });
        if (window.Picked is { } picked) return new ModelPick(picked, null, false);
        if (!window.ChooseFile) return new ModelPick(null, null, true);
        return ChooseFile(modelsDir, cleanupModel: false) is { } file ? new ModelPick(null, file, false) : new ModelPick(null, null, true);
    }

    public CleanupPick CleanupPicker(string recommended, string reason, Func<CleanupModelInfo, bool> isDownloaded, string modelsDir)
    {
        var options = CleanupCatalog.Entries.Select(e => new PickerOption(e.Name,
                e.ItemText(isDownloaded(e)) + (e.Name == recommended ? " · recommended" : ""), e.TradeOff))
            .Append(new PickerOption(ModelPickerWindow.RulesOnlyName, "No AI clean-up: rule-based tidy only", "Removes um and uh and capitalises; nothing to download."))
            .ToList();
        var text = "A small language model on this PC removes filler words and fixes punctuation after transcription; nothing leaves the PC. " +
                   "It is a different job from the speech model, which turns audio into words. " +
                   $"Recommended here: {recommended} ({reason}). A model that is not downloaded yet is fetched from {ModelCatalog.DownloadHost} and checked against its SHA-256.";
        var window = Modal.OnUi(() =>
        {
            var w = new ModelPickerWindow("Choose a clean-up model", text, options, recommended, "Use selected", "Choose .gguf file…");
            Modal.Run(w);
            return w;
        });
        if (window.Picked is { } picked) return picked == ModelPickerWindow.RulesOnlyName ? new CleanupPick(null, null, true, false) : new CleanupPick(picked, null, false, false);
        if (!window.ChooseFile) return new CleanupPick(null, null, false, true);
        return ChooseFile(modelsDir, cleanupModel: true) is { } file ? new CleanupPick(null, file, false, false) : new CleanupPick(null, null, true, false);
    }

    public MissingModelChoice MissingModel(string shortName, bool canDownload)
    {
        var text = ModelPromptText.MissingText(shortName, canDownload);
        if (!canDownload)
            return Ask("No speech model found", text, new DialogChoice("Choose file…", IsDefault: true), new DialogChoice("Quit")) == 0 ? MissingModelChoice.ChooseFile : MissingModelChoice.Quit;
        return Ask("No speech model found", text, new DialogChoice("Download", IsDefault: true), new DialogChoice("Choose file…"), new DialogChoice("Quit")) switch
        {
            0 => MissingModelChoice.Download,
            1 => MissingModelChoice.ChooseFile,
            _ => MissingModelChoice.Quit,
        };
    }

    public bool ConfirmDownload(string fileName, string sizeText, string note) =>
        Ask("Download a model", ModelPromptText.ConfirmText(fileName, sizeText, note), new DialogChoice("Download", IsDefault: true), new DialogChoice("Cancel")) == 0;

    /// <summary>
    /// The same question window with three answers: Download after it (Enter), Stop … and download … (it deletes progress),
    /// Cancel. Without room to wait (<paramref name="canWait"/> false), only Stop and Cancel, and Enter presses Cancel.
    /// </summary>
    public BusyDownloadChoice ConfirmDownloadWhileBusy(string fileName, string sizeText, string note, string chosen, string running, string runningProgress, bool canWait = true) =>
        ModelPromptText.BusyAnswer(Ask("Download a model", ModelPromptText.BusyText(fileName, sizeText, note, chosen, running, runningProgress, canWait),
            BusyDialogChoices(chosen, canWait)), canWait);

    /// <summary>The busy question's buttons as the window shows them (a layout test builds the same window from these).</summary>
    internal static DialogChoice[] BusyDialogChoices(string chosen, bool canWait = true)
    {
        var labels = ModelPromptText.BusyChoices(chosen, canWait);
        return canWait
            ? [new DialogChoice(labels[0], IsDefault: true), new DialogChoice(labels[1], IsDanger: true), new DialogChoice(labels[2])]
            : [new DialogChoice(labels[0], IsDanger: true), new DialogChoice(labels[1], IsDefault: true)]; // the safe answer is the default
    }

    /// <summary>The system's file chooser.</summary>
    public string? ChooseFile(string initialDir, bool cleanupModel) => WithStorage(initialDir, (storage, start) =>
    {
        var files = Modal.Wait(storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = cleanupModel ? "Choose a GGUF clean-up model" : "Choose a whisper.cpp ggml model",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = cleanupModel
                ? [new FilePickerFileType("GGUF models") { Patterns = ["*.gguf"] }]
                : [new FilePickerFileType("Whisper ggml models") { Patterns = ["*.bin"] }, FilePickerFileTypes.All],
        }));
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    });

    /// <summary>The system's folder chooser (Avalonia's, which is Windows' own; PROPOSED title, 17/10/2026).</summary>
    public string? ChooseFolder(string initialDir) => WithStorage(initialDir, (storage, start) =>
    {
        var folders = Modal.Wait(storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for Dictum's models",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        }));
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    });

    /// <summary>
    /// Runs a system chooser on the UI thread, starting in <paramref name="initialDir"/> when it exists. A tray app has no window
    /// to own it, so a window that is never shown lends its handle.
    /// </summary>
    private static string? WithStorage(string initialDir, Func<IStorageProvider, IStorageFolder?, string?> choose) => Modal.OnUi(() =>
    {
        var owner = new Window { Title = "Dictum", ShowInTaskbar = false };
        try
        {
            var storage = owner.StorageProvider;
            var start = Directory.Exists(initialDir) ? Modal.Wait(storage.TryGetFolderFromPathAsync(initialDir)) : null;
            return choose(storage, start);
        }
        finally
        {
            owner.Close();
        }
    });
}
