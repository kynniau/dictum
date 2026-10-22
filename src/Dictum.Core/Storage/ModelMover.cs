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
/// What a move did: <see cref="Moved"/> of <see cref="Count"/> files arrived in the new folder; the first that could not
/// (<see cref="FailedFile"/>, with <see cref="Reason"/>) and every file after it are still in the old folder.
/// </summary>
public sealed record MoveResult(int Moved, int Count, string? FailedFile, string? Reason)
{
    public bool Complete => FailedFile is null;
}

/// <summary>
/// Moves the model files out of the model folder Dictum is leaving (1.3, owner approval 22/10/2026: "offer to move the model
/// files when the folder changes"). Each file is copied to the new folder under a temporary name (<see cref="TempSuffix"/>),
/// the copy is checked (its size against the original; for a finished catalogue file also its SHA-256 against the catalogue's
/// pinned hash, <see cref="ModelIntegrity.Expected"/>), renamed into place, and only then is the original deleted. A .part
/// moves unchecked (it is not finished, so there is nothing to compare it with; the downloader checks it when it resumes).
/// A file that fails is left where it was, its copy is deleted, and the move stops there.
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
    /// Moves <paramref name="files"/> into <paramref name="to"/> in order, as the class says; stops at the first that fails.
    /// The engines that load these files must be released first (a loaded clean-up model holds its file open): the caller's
    /// job, pinned in ModelMoveTests.
    /// </summary>
    /// <param name="integrity">Told about each file: the checked copy is remembered, the original forgotten. Null: no record kept.</param>
    /// <param name="hasher">The SHA-256 of a file; tests pass a fake. Null: <see cref="ModelDownloader.HashFileAsync"/>.</param>
    // simplification: no rollback. Files already moved stay moved when a later one fails, and the result says how many did.
    // Ceiling: after a failure the models are split across two folders, and those left in a chosen folder Dictum no longer
    // looks in read "Not downloaded" until that folder is chosen again. Trigger to retire: a report of a split folder that
    // confused someone, or the archive folder (design brief D) adding a second folder that the lookup searches anyway.
    public static async Task<MoveResult> MoveAsync(IReadOnlyList<MovableModel> files, string to, ModelIntegrity? integrity = null,
        IProgress<MoveStep>? progress = null, Func<string, CancellationToken, Task<string>>? hasher = null, CancellationToken cancellationToken = default)
    {
        hasher ??= ModelDownloader.HashFileAsync;
        Directory.CreateDirectory(to);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            progress?.Report(new MoveStep(i + 1, files.Count, file.FileName));
            var reason = await MoveOneAsync(file, to, integrity, hasher, cancellationToken).ConfigureAwait(false);
            if (reason is not null) return new MoveResult(i, files.Count, file.FileName, reason);
        }
        return new MoveResult(files.Count, files.Count, null, null);
    }

    /// <summary>PROPOSED 22/10/2026: why a file was not moved, after "{name} could not be moved: ".</summary>
    public const string AlreadyThere = "a file of that name is already in the new folder";

    /// <summary>PROPOSED 22/10/2026: the copy was not the original's size.</summary>
    public const string CopySizeDiffers = "the copy is not the same size as the original, so the copy was deleted";

    /// <summary>PROPOSED 22/10/2026: the copy failed its SHA-256 check against the catalogue.</summary>
    public const string CopyDoesNotMatch = "the copy did not match the catalogue, so the copy was deleted";

    /// <summary>Copy, check, rename, then delete the original; null when moved, else why not (the original is then untouched).</summary>
    private static async Task<string?> MoveOneAsync(MovableModel file, string to, ModelIntegrity? integrity,
        Func<string, CancellationToken, Task<string>> hasher, CancellationToken cancellationToken)
    {
        var final = Path.Combine(to, file.FileName);
        if (File.Exists(final)) return AlreadyThere;
        var temp = final + TempSuffix;
        try
        {
            await using (var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await source.CopyToAsync(target, 1 << 20, cancellationToken).ConfigureAwait(false);
            }
            if (new FileInfo(temp).Length != new FileInfo(file.Path).Length)
            {
                TryDelete(temp);
                return CopySizeDiffers;
            }
            // A finished catalogue file is checked against its pinned SHA-256; a .part has nothing to be checked against yet.
            var verified = false;
            if (!file.Partial && ModelIntegrity.Expected(file.FileName)?.Sha256 is { } pinned)
            {
                var actual = await hasher(temp, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temp);
                    return CopyDoesNotMatch;
                }
                verified = true;
            }
            File.Move(temp, final, overwrite: false);
            if (verified) integrity?.Remember(final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(temp);
            return ex.Message;
        }
        // Only now, with the checked copy in place, does the original go.
        try
        {
            File.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Two copies now: the checked one in the new folder stays (the folder Dictum uses from now on); the original is reported.
            return ex.Message;
        }
        integrity?.Forget(file.Path);
        return null;
    }

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
