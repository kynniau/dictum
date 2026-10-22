using Dictum.App;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// The model folder's two actions, shared by Settings > Models (Browse…, Use default) and Settings > Storage (Change…, Use default):
/// one chooser seam and one write, so the two pages cannot drift apart. Both write models.folder through
/// <see cref="ModelCoordinator.SetModelsFolder"/>, which refuses the bundled folder and reloads the settings. Since 1.3 (owner
/// approval 22/10/2026) a change away from a folder that holds model files Dictum knows first offers to move them
/// (<see cref="ModelMover"/>); leaving them is the earlier behaviour, and no file moves without the user's choice.
/// </summary>
internal static class ModelFolderActions
{
    /// <summary>The accessible name of "Use default" on both pages.</summary>
    internal const string UseDefaultName = "Use the default model folder";

    // PROPOSED 22/10/2026 copy (1.3, move-on-change): the offer, its buttons, the refusals and the progress and result lines.
    internal const string MoveTitle = "Move your model files?";
    internal const string MoveLabel = "Move the files";
    internal const string LeaveLabel = "Leave them where they are";
    internal const string DownloadRunning = "A download is running; wait for it to finish, then change the folder again to move the files.";
    internal const string NestedRefused = "One of these folders is inside the other, so Dictum does not move the files. Choose a folder outside the current one.";

    /// <summary>PROPOSED 22/10/2026: the offer's body ("1 file" for one).</summary>
    internal static string MoveQuestion(string from, string to, int count, long bytes) =>
        $"Your model files are in {from}. Move them to {to} so Dictum finds them there? {Files(count)}, {ModelCatalog.FormatSize(bytes)}. " +
        "Dictum copies each file, checks it, then removes the original.";

    /// <summary>PROPOSED 22/10/2026: the progress line while a file moves.</summary>
    internal static string Moving(string fileName, int index, int count) => $"Moving {fileName} ({index} of {count})…";

    /// <summary>PROPOSED 22/10/2026: every file moved.</summary>
    internal static string Moved(int count, string to) => $"Moved {Files(count)} to {to}.";

    /// <summary>PROPOSED 22/10/2026: the move stopped at <paramref name="fileName"/>; it and the files after it are still in <paramref name="from"/>.</summary>
    internal static string MovedSome(int moved, int count, string fileName, string reason, string from) =>
        $"Moved {moved} of {count}; {fileName} could not be moved: {reason}. The rest stay in {from}.";

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>
    /// The system's folder chooser (opened in the folder in use), then <see cref="ChangeFolderAsync"/>; cancelling changes nothing.
    /// <paramref name="then"/> runs when the change, and any move, is over.
    /// </summary>
    internal static Task BrowseFolder(AppShell shell, Action<string> say, Action? then = null)
    {
        if (shell.MovingModels) return Task.CompletedTask; // the progress line is showing; one move at a time
        if (shell.Dialogs.ChooseFolder(shell.ModelsDir) is not { } folder) return Task.CompletedTask;
        return ChangeFolderAsync(shell, folder, say, then);
    }

    /// <summary>
    /// models.folder goes back to the per-user default folder. No move is offered: the owner's approval names Browse… and
    /// Change… only, so Use default keeps the earlier behaviour (an owner decision whether it should offer the move too).
    /// </summary>
    internal static void UseDefaultFolder(AppShell shell, Action<string> say, Action? then = null)
    {
        if (!shell.MovingModels) say(shell.Models.SetModelsFolder(null) ?? "");
        then?.Invoke();
    }

    /// <summary>
    /// Changes the model folder to <paramref name="folder"/> (Browse… and Change…). When the folder Dictum is leaving holds
    /// model files it knows (<see cref="ModelMover.Known"/>), asks first: "Leave them where they are" writes the folder as before;
    /// "Move the files" is refused while a download runs or when one folder is inside the other, otherwise releases both engines,
    /// moves the files (copy, check, rename, then delete the original), then writes the folder and reloads, so the models load
    /// from the new folder. Closing the question changes nothing. Runs synchronously up to the move, so a change with nothing
    /// to move is done when this returns.
    /// </summary>
    internal static async Task ChangeFolderAsync(AppShell shell, string folder, Action<string> say, Action? then = null)
    {
        try
        {
            await ChangeAsync(shell, folder, say);
        }
        catch (Exception ex)
        {
            shell.Log.Error($"Settings: changing the model folder: {ex.GetType().Name}: {ex.Message}");
            say($"That did not work: {ex.Message}");
        }
        finally
        {
            then?.Invoke();
        }
    }

    private static async Task ChangeAsync(AppShell shell, string folder, Action<string> say)
    {
        if (shell.Models.FolderRefusal(folder) is { } refused)
        {
            say(refused); // the bundled folder or a device path: as before, nothing is asked or written
            return;
        }
        var from = shell.ModelsDir;
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var files = PathRegistry.SamePath(from, to) || shell.Paths.IsBundled(from) ? [] : ModelMover.Known(from);
        if (files.Count == 0)
        {
            say(shell.Models.SetModelsFolder(folder) ?? "");
            return;
        }
        var choice = shell.Dialogs.Ask(MoveTitle, MoveQuestion(shell.RealPath(from), shell.RealPath(to), files.Count, files.Sum(f => f.Bytes)),
            new DialogChoice(MoveLabel, IsDefault: true), new DialogChoice(LeaveLabel));
        if (choice == 1)
        {
            say(shell.Models.SetModelsFolder(folder) ?? ""); // the approved "Changing it does not move any files." stays true here
            return;
        }
        if (choice != 0) return; // closed: nothing changes
        // The downloader holds its .part open and writes into the folder in use: no move is attempted under it.
        if (shell.Models.IsDownloading)
        {
            say(DownloadRunning);
            return;
        }
        if (ModelMover.Nested(from, to))
        {
            say(NestedRefused);
            return;
        }
        shell.MovingModels = true;
        var finished = false; // a progress report posted before the end must not overwrite the result line
        try
        {
            await shell.ReleaseModelsAsync(); // before the first copy: a loaded clean-up model holds its file open
            var progress = new Progress<MoveStep>(step =>
            {
                if (!finished) say(Moving(step.FileName, step.Index, step.Count));
            });
            var result = await ModelMover.MoveAsync(files, to, shell.Integrity, progress, shell.MoveHasher);
            shell.Log.Info(result.Complete
                ? $"Moved {result.Count} model file(s) from {from} to {to}."
                : $"Moved {result.Moved} of {result.Count} model file(s) from {from} to {to}; {result.FailedFile} was not moved: {result.Reason}");
            // Written after the move, not before: the settings watcher's reload would otherwise load a model mid-move.
            var written = shell.Models.SetModelsFolder(folder, reloadAfter: false);
            finished = true;
            var line = result.Complete
                ? Moved(result.Count, shell.RealPath(to))
                : MovedSome(result.Moved, result.Count, result.FailedFile!, result.Reason!, shell.RealPath(from));
            say(written is null ? line : $"{line} {written}");
        }
        finally
        {
            finished = true;
            shell.MovingModels = false;
            shell.Reload(userRequested: false); // the models load again: from the new folder, or from where they still are
        }
    }
}
