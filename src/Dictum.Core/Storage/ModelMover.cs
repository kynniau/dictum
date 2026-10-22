using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Storage;

/// <summary>One file the move would carry: a catalogue model file or one of their partial downloads (.part).</summary>
public sealed record MovableModel(string Path, long Bytes, bool Partial)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Where the move is: file <see cref="Index"/> of <see cref="Count"/>, named <see cref="FileName"/>.</summary>
public readonly record struct MoveStep(int Index, int Count, string FileName);

/// <summary>
/// A file the move could not carry cleanly: <see cref="Reason"/> says why. <see cref="Copied"/>: the checked copy is in the new
/// folder (it counts as moved) but the original could not be removed.
/// </summary>
public sealed record MoveProblem(string FileName, string Reason, bool Copied = false);

/// <summary>
/// What a move did: <see cref="Moved"/> of <see cref="Count"/> files are in the new folder; <see cref="Problems"/> in the order
/// met; <see cref="LeftBehind"/> the originals still in the old folder with no copy in the new one (failed, skipped, or not
/// reached because the move was cancelled, <see cref="Cancelled"/>).
/// </summary>
public sealed record MoveResult(int Moved, int Count, IReadOnlyList<MoveProblem> Problems, IReadOnlyList<string> LeftBehind, bool Cancelled = false)
{
    public bool Complete => Moved == Count && Problems.Count == 0;
}

/// <summary>
/// Moves the model files out of the model folder Dictum is leaving (1.3, owner approval 22/10/2026: "offer to move the model
/// files when the folder changes"). Each file is copied to the new folder under a temporary name (<see cref="TempSuffix"/>),
/// the copy is checked (its size against the original; for a finished catalogue file also its SHA-256 against the catalogue's
/// pinned hash, <see cref="ModelIntegrity.Expected"/>), renamed into place, and only then is the original deleted. A .part
/// moves unchecked (it is not finished, so there is nothing to compare it with; the downloader checks it when it resumes).
/// A file of the same name already in the new folder counts as moved when it is the same file (same size and, for a finished
/// catalogue file, the pinned SHA-256), and is skipped otherwise. A file that fails is left where it was, its copy is deleted,
/// and the move goes on with the next; the models in use go last (design brief B.7), so a failure earlier leaves them alone.
/// </summary>
public static class ModelMover
{
    /// <summary>The name a copy has until it is checked: "ggml-small.en.bin.move".</summary>
    public const string TempSuffix = ".move";

    /// <summary>
    /// The model files Dictum knows in <paramref name="folder"/> itself (not in folders inside it), in name order: the
    /// speech and clean-up catalogues' files and their .part downloads. None for a folder that does not exist.
    /// </summary>
    public static IReadOnlyList<MovableModel> Known(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => ModelFiles.IsModelFile(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new MovableModel(f.FullName, f.Length, f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>True when one folder is inside the other (not when they are the same folder).</summary>
    public static bool Nested(string from, string to) => PathRegistry.IsInside(to, from) || PathRegistry.IsInside(from, to);

    /// <summary>
    /// PROPOSED 22/10/2026: the name a file goes by in the progress and result lines: the catalogue's ("small.en",
    /// "qwen2.5-1.5b"), or "unfinished small.en download" for a .part.
    /// </summary>
    public static string DisplayName(string fileName)
    {
        var partial = fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
        var finalName = partial ? fileName[..^5] : fileName;
        var name = ModelCatalog.FindByFile(finalName)?.Name ?? CleanupCatalog.FindByFile(finalName)?.Name ?? finalName;
        return partial ? $"unfinished {name} download" : name;
    }

    /// <summary>
    /// Moves <paramref name="files"/> into <paramref name="to"/>: those not in <paramref name="inUse"/> first, in order, then the
    /// models in use. The engines that load these files must be released first (an engine holds its model file open, so the
    /// original could not be deleted): the caller's job, pinned in ModelMoveTests.
    /// </summary>
    /// <param name="integrity">Told about each file: the checked copy is remembered, the original forgotten. Null: no record kept.</param>
    /// <param name="hasher">The SHA-256 of a file; tests pass a fake. Null: <see cref="ModelDownloader.HashFileAsync"/>.</param>
    /// <param name="inUse">The model files loaded before the engines were released; moved last.</param>
    /// <param name="copier">Copies a file (source, target); tests pass a fake. Null: a stream copy.</param>
    /// <param name="cancellationToken">Quit: the file being copied stops (its copy deleted, its original kept) and no other starts; a file whose copy was checked finishes its rename and delete.</param>
    // simplification: no rollback. Files already moved stay moved when a later one fails, and the result says how many did.
    // Ceiling: after a failure the models are split across two folders, and those left in a chosen folder Dictum no longer
    // looks in read "Not downloaded" until that folder is chosen again. Trigger to retire: a report of a split folder that
    // confused someone, or the archive folder (design brief D) adding a second folder that the lookup searches anyway.
    public static async Task<MoveResult> MoveAsync(IReadOnlyList<MovableModel> files, string to, ModelIntegrity? integrity = null,
        IProgress<MoveStep>? progress = null, Func<string, CancellationToken, Task<string>>? hasher = null, IReadOnlyCollection<string>? inUse = null,
        Func<string, string, CancellationToken, Task>? copier = null, CancellationToken cancellationToken = default)
    {
        hasher ??= ModelDownloader.HashFileAsync;
        copier ??= CopyAsync;
        var ordered = files.OrderBy(f => inUse?.Any(p => PathRegistry.SamePath(p, f.Path)) == true ? 1 : 0).ToList(); // stable: name order within each
        Directory.CreateDirectory(to);
        var problems = new List<MoveProblem>();
        var leftBehind = new List<string>();
        var moved = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var file = ordered[i];
            if (cancellationToken.IsCancellationRequested)
            {
                leftBehind.AddRange(ordered.Skip(i).Select(f => f.Path));
                return new MoveResult(moved, ordered.Count, problems, leftBehind, Cancelled: true);
            }
            progress?.Report(new MoveStep(i + 1, ordered.Count, file.FileName));
            var (arrived, problem) = await MoveOneAsync(file, to, integrity, hasher, copier, cancellationToken).ConfigureAwait(false);
            if (arrived) moved++;
            else leftBehind.Add(file.Path);
            if (problem is not null) problems.Add(problem);
        }
        return new MoveResult(moved, ordered.Count, problems, leftBehind, Cancelled: cancellationToken.IsCancellationRequested && leftBehind.Count > 0);
    }

    /// <summary>PROPOSED 22/10/2026: why a file was not moved, after "{name} could not be moved: ".</summary>
    public const string AlreadyThere = "a different file of that name is already in the new folder";

    /// <summary>PROPOSED 22/10/2026: the copy was not the original's size.</summary>
    public const string CopySizeDiffers = "the copy is not the same size as the original, so the copy was deleted";

    /// <summary>PROPOSED 22/10/2026: the copy failed its SHA-256 check against the catalogue.</summary>
    public const string CopyDoesNotMatch = "the copy did not match the catalogue, so the copy was deleted";

    /// <summary>PROPOSED 22/10/2026 (lens on f5ec71c): the checked copy is in the new folder, but the original is still in the old one.</summary>
    public const string OriginalKept = "it was copied, but the original could not be removed";

    /// <summary>
    /// Copy, check, rename, then delete the original. Arrived: a checked copy (or the same file) is in the new folder; the
    /// problem, if any, says what went wrong. Not arrived: the original is untouched and no copy is left.
    /// </summary>
    private static async Task<(bool Arrived, MoveProblem? Problem)> MoveOneAsync(MovableModel file, string to, ModelIntegrity? integrity,
        Func<string, CancellationToken, Task<string>> hasher, Func<string, string, CancellationToken, Task> copier, CancellationToken cancellationToken)
    {
        var final = Path.Combine(to, file.FileName);
        var pinned = file.Partial ? null : ModelIntegrity.Expected(file.FileName)?.Sha256;
        if (File.Exists(final))
        {
            // The same file already there (an earlier move that stopped, or a copy made by hand) counts as moved; another is skipped.
            try
            {
                var same = new FileInfo(final).Length == new FileInfo(file.Path).Length
                           && (pinned is null || string.Equals(await hasher(final, cancellationToken).ConfigureAwait(false), pinned, StringComparison.OrdinalIgnoreCase));
                if (!same) return (false, new MoveProblem(file.FileName, AlreadyThere));
                if (pinned is not null) integrity?.Remember(final);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return (false, new MoveProblem(file.FileName, Reason(ex)));
            }
            return (true, DeleteOriginal(file, integrity));
        }
        var temp = final + TempSuffix;
        try
        {
            await copier(file.Path, temp, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(temp).Length != new FileInfo(file.Path).Length)
            {
                TryDelete(temp);
                return (false, new MoveProblem(file.FileName, CopySizeDiffers));
            }
            // A finished catalogue file is checked against its pinned SHA-256; a .part has nothing to be checked against yet.
            if (pinned is not null && !string.Equals(await hasher(temp, cancellationToken).ConfigureAwait(false), pinned, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                return (false, new MoveProblem(file.FileName, CopyDoesNotMatch));
            }
            File.Move(temp, final, overwrite: false);
            if (pinned is not null) integrity?.Remember(final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(temp);
            return (false, new MoveProblem(file.FileName, Reason(ex)));
        }
        // Only now, with the checked copy in place under its own name, does the original go.
        return (true, DeleteOriginal(file, integrity));
    }

    /// <summary>Deletes the original once its copy is in place; null when it went, else the problem (two copies now; the new one is the one used).</summary>
    private static MoveProblem? DeleteOriginal(MovableModel file, ModelIntegrity? integrity)
    {
        try
        {
            File.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MoveProblem(file.FileName, OriginalKept, Copied: true);
        }
        integrity?.Forget(file.Path);
        return null;
    }

    private static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var into = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await from.CopyToAsync(into, 1 << 20, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The system's message without its closing full stop, as it goes mid-sentence ("could not be moved: {reason}. The rest…").</summary>
    private static string Reason(Exception ex) => ex.Message.Trim().TrimEnd('.');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover .move is never loaded (it is not a catalogue file name) and is overwritten by the next attempt
        }
    }
}
