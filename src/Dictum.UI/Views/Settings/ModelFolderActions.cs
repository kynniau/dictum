using Dictum.App;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// The model folder's two actions, shared by Settings > Models (Browse…, Use default) and Settings > Storage (Change…, Use default):
/// one chooser seam and one write, so the two pages cannot drift apart. Both write models.folder through
/// <see cref="ModelCoordinator.SetModelsFolder"/>, which refuses the bundled folder and reloads the settings. Since 1.3 (owner
/// approval 22/10/2026; Use default too, owner decision 22/10/2026 21:45 AEDT) a change away from a folder that holds model
/// files Dictum knows first offers to move them (<see cref="ModelMover"/>); leaving them is the earlier behaviour, and no file
/// moves without the user's choice.
/// </summary>
internal static class ModelFolderActions
{
    /// <summary>The accessible name of "Use default" on both pages.</summary>
    internal const string UseDefaultName = "Use the default model folder";

    // PROPOSED 22/10/2026 copy (1.3, move-on-change): the offer, its buttons, the refusals and the progress and result lines.
    internal const string MoveTitle = "Move your model files?";
    internal const string MoveLabel = "Move the files";
    internal const string LeaveLabel = "Leave them where they are"; // the default button (orchestrator, lens on PR #61): Enter never starts a long move
    internal const string DownloadRunning = "A download is running; wait for it to finish, then change the folder again to move the files.";
    internal const string NestedRefused = "One of these folders is inside the other, so Dictum does not move the files. Choose a folder outside the current one.";

    /// <summary>PROPOSED 22/10/2026: the offer's body ("1 file" for one).</summary>
    internal static string MoveQuestion(string from, string to, int count, long bytes) =>
        $"Your model files are in {from}. Move them to {to} so Dictum finds them there? {Files(count)}, {ModelCatalog.FormatSize(bytes)}. " +
        "Dictum copies each file, checks it, then removes the original.";

    /// <summary>PROPOSED 22/10/2026: the progress line while a file moves, by its catalogue name (<see cref="ModelMover.DisplayName"/>).</summary>
    internal static string Moving(string name, int index, int count) => $"Moving {name} ({index} of {count})…";

    /// <summary>PROPOSED 22/10/2026: every file moved.</summary>
    internal static string Moved(int count, string to) => $"Moved {Files(count)} to {to}.";

    /// <summary>PROPOSED 22/10/2026: <paramref name="name"/> is the first file that met a problem; the files not moved are still in <paramref name="from"/>.</summary>
    internal static string MovedSome(int moved, int count, string name, string reason, string from) =>
        $"Moved {moved} of {count}; {name} could not be moved: {reason}. The rest stay in {from}.";

    /// <summary>PROPOSED 22/10/2026: added when more than one file met a problem.</summary>
    internal static string MoreProblems(int more) => more == 1 ? " 1 other file could not be moved either; the log says why." : $" {more} other files could not be moved either; the log says why.";

    /// <summary>
    /// PROPOSED 22/10/2026 (lens on PR #61): added when a model in use stayed behind (or the move was stopped), so the folder
    /// was not changed: dictation goes on with the models where they are.
    /// </summary>
    internal static string FolderKept(string from, string to, bool someMoved) => someMoved
        ? $" The model folder is still {from}, because the model in use did not move; the files that did are in {to}; choose that folder again to use them."
        : $" The model folder is still {from}, because the model in use did not move.";

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>
    /// The system's folder chooser (opened in the folder in use), then <see cref="ChangeFolderAsync"/>; cancelling changes nothing.
    /// <paramref name="then"/> runs when the change, and any move, is over.
    /// </summary>
    internal static Task BrowseFolder(AppShell shell, Action<string> say, Action? then = null)
    {
        if (shell.Models.MovingFiles) return Task.CompletedTask; // the progress line is showing; one move at a time
        if (shell.Dialogs.ChooseFolder(shell.ModelsDir) is not { } folder) return Task.CompletedTask;
        return ChangeFolderAsync(shell, folder, say, then);
    }

    /// <summary>
    /// models.folder goes back to the per-user default folder, offering to move the files there as Browse… does (owner decision,
    /// 22/10/2026 21:45 AEDT: all three ways of changing the folder behave alike).
    /// </summary>
    internal static Task UseDefaultFolder(AppShell shell, Action<string> say, Action? then = null) =>
        shell.Models.MovingFiles ? Task.CompletedTask : ChangeFolderAsync(shell, null, say, then);

    /// <summary>
    /// Changes the model folder to <paramref name="folder"/> (null: the per-user default; Browse…, Change… and Use default). When
    /// the folder Dictum is leaving holds model files it knows (<see cref="ModelMover.Known"/>), asks first: "Leave them where
    /// they are" writes the folder as before; "Move the files" is refused while a download runs or when one folder is inside the
    /// other, otherwise marks the move (<see cref="ModelCoordinator.MovingFiles"/>), releases both engines, moves the files (the
    /// models in use last), then writes the folder unless a model in use stayed behind, and reloads. Closing the question changes
    /// nothing. Runs synchronously up to the move, so a change with nothing to move is done when this returns.
    /// </summary>
    internal static async Task ChangeFolderAsync(AppShell shell, string? folder, Action<string> say, Action? then = null)
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

    private static async Task ChangeAsync(AppShell shell, string? folder, Action<string> say)
    {
        if (folder is not null && shell.Models.FolderRefusal(folder) is { } refused)
        {
            say(refused); // the bundled folder or a device path: as before, nothing is asked or written
            return;
        }
        var from = shell.ModelsDir;
        var to = folder is null ? shell.Paths.DefaultModelsDir : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var files = PathRegistry.SamePath(from, to) || shell.Paths.IsBundled(from) ? [] : ModelMover.Known(from);
        if (files.Count == 0)
        {
            say(shell.Models.SetModelsFolder(folder) ?? "");
            return;
        }
        var choice = shell.Dialogs.Ask(MoveTitle, MoveQuestion(shell.RealPath(from), shell.RealPath(to), files.Count, files.Sum(f => f.Bytes)),
            new DialogChoice(MoveLabel), new DialogChoice(LeaveLabel, IsDefault: true));
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
        var inUse = shell.ModelFilesInUse(); // before the release, which forgets them
        shell.Models.MovingFiles = true; // downloads and deletions refused, and no model loads, until the reload below
        using var cancel = new CancellationTokenSource();
        shell.MoveCancel = cancel;
        var finished = false; // a progress report posted before the end must not overwrite the result line
        try
        {
            await shell.ReleaseModelsAsync(); // before the first copy: an engine holds its model file open
            var progress = new Progress<MoveStep>(step =>
            {
                if (!finished) say(Moving(ModelMover.DisplayName(step.FileName), step.Index, step.Count));
            });
            var result = await ModelMover.MoveAsync(files, to, shell.Integrity, progress, shell.MoveHasher, inUse, cancellationToken: cancel.Token);
            shell.Log.Info($"Moved {result.Moved} of {result.Count} model file(s) from {from} to {to}{(result.Cancelled ? " (stopped: quitting)" : "")}." +
                string.Concat(result.Problems.Select(p => $" {p.FileName}: {p.Reason}.")));
            // A model in use left in the old folder would not be found once the folder changes: then the folder stays as it is.
            var kept = result.Cancelled || result.LeftBehind.Any(left => inUse.Any(used => PathRegistry.SamePath(used, left)));
            // Written after the move, not before: the settings watcher's reload would otherwise reload the models mid-move.
            var written = kept ? null : shell.Models.SetModelsFolder(folder, reloadAfter: false);
            finished = true;
            var line = result.Problems.Count == 0 && result.Moved == result.Count
                ? Moved(result.Count, shell.RealPath(to))
                : result.Problems.Count == 0
                    ? $"Moved {result.Moved} of {result.Count}."
                    : MovedSome(result.Moved, result.Count, ModelMover.DisplayName(result.Problems[0].FileName), result.Problems[0].Reason, shell.RealPath(from)) +
                      (result.Problems.Count > 1 ? MoreProblems(result.Problems.Count - 1) : "");
            if (kept) line += FolderKept(shell.RealPath(from), shell.RealPath(to), result.Moved > 0);
            say(written is null ? line : $"{line} {written}");
        }
        finally
        {
            finished = true;
            shell.MoveCancel = null;
            shell.Models.MovingFiles = false;
            // The models load again, from the new folder or from where they still are; or Dictum quits if Quit was asked meanwhile.
            if (!shell.QuitIfAskedDuringMove()) shell.Reload(userRequested: false);
        }
    }
}
