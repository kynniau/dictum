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
/// A file of the same name already in the new folder counts as moved when it has the same contents (the pinned SHA-256, or
/// byte for byte where there is none), and is skipped otherwise; an original is never deleted when it and that file are the
/// same file on disk (two paths to one folder: a junction, a mapped drive, a packaged app's redirected folder). A file that
/// fails is left where it was, its copy is deleted, and the move goes on with the next. The models in use go last and as one
/// unit: no original of theirs is deleted until all of them have arrived, and if one fails, or the move is stopped, the
/// copies this move made of them are deleted and every original kept (re-lens on PR #61).
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
    /// True when <paramref name="from"/> and <paramref name="to"/> are one folder on disk, by name or through another path to it
    /// (a junction, a subst or mapped drive, a share, a packaged app's redirected folder): a uniquely named probe file written
    /// in <paramref name="to"/> appears in <paramref name="from"/>. False when either does not exist or the probe cannot be written.
    /// </summary>
    public static bool SameFolder(string from, string to)
    {
        if (PathRegistry.SamePath(from, to)) return true;
        if (!Directory.Exists(from) || !Directory.Exists(to)) return false;
        var name = $".dictum-probe-{Guid.NewGuid():N}.tmp";
        var probe = Path.Combine(to, name);
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            return File.Exists(Path.Combine(from, name));
        }
        finally
        {
            TryDelete(probe);
        }
    }

    /// <summary>
    /// APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. The name a file goes by in the progress and result lines: the catalogue's ("small.en",
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
    /// Moves <paramref name="files"/> into <paramref name="to"/>: those not in <paramref name="inUse"/> first, in order, each
    /// copied, checked, renamed and its original deleted; then the models in use as one unit. The engines that load these files
    /// must be released first (an engine holds its model file open, so the original could not be deleted): the caller's job,
    /// pinned in ModelMoveTests.
    /// </summary>
    /// <param name="integrity">Told about each file: the checked copy is remembered, the original forgotten. Null: no record kept.</param>
    /// <param name="hasher">The SHA-256 of a file; tests pass a fake. Null: <see cref="ModelDownloader.HashFileAsync"/>.</param>
    /// <param name="inUse">The model files loaded before the engines were released; moved last, as one unit.</param>
    /// <param name="copier">Copies a file (source, target); tests pass a fake. Null: a stream copy that stops when cancelled.</param>
    /// <param name="cancellationToken">Quit: the file being copied stops (its copy deleted, its original kept) and no other starts; a file not in use whose copy was checked finishes its rename and delete; the models in use keep every original.</param>
    // simplification: no rollback for the files not in use. Those already moved stay moved when a later one fails, and the
    // result says how many did. Ceiling: after a failure the models are split across two folders, and those left in a chosen
    // folder Dictum no longer looks in read "Not downloaded" until that folder is chosen again. Trigger to retire: a report of
    // a split folder that confused someone, or the archive folder (design brief D) adding a second folder the lookup searches.
    public static async Task<MoveResult> MoveAsync(IReadOnlyList<MovableModel> files, string to, ModelIntegrity? integrity = null,
        IProgress<MoveStep>? progress = null, Func<string, CancellationToken, Task<string>>? hasher = null, IReadOnlyCollection<string>? inUse = null,
        Func<string, string, CancellationToken, Task>? copier = null, CancellationToken cancellationToken = default)
    {
        hasher ??= ModelDownloader.HashFileAsync;
        copier ??= CopyAsync;
        bool InUse(MovableModel f) => inUse?.Any(p => PathRegistry.SamePath(p, f.Path)) == true;
        var others = files.Where(f => !InUse(f)).ToList();
        var used = files.Where(InUse).ToList();
        var count = files.Count;
        Directory.CreateDirectory(to);
        var problems = new List<MoveProblem>();
        var leftBehind = new List<string>();
        var moved = 0;
        var step = 0;

        for (var i = 0; i < others.Count; i++)
        {
            var file = others[i];
            if (cancellationToken.IsCancellationRequested)
            {
                leftBehind.AddRange(others.Skip(i).Concat(used).Select(f => f.Path));
                return new MoveResult(moved, count, problems, leftBehind, Cancelled: true);
            }
            progress?.Report(new MoveStep(++step, count, file.FileName));
            var (placed, problem) = await PlaceAsync(file, to, integrity, hasher, copier, cancellationToken).ConfigureAwait(false);
            if (placed is null)
            {
                leftBehind.Add(file.Path);
                if (problem is not null && !cancellationToken.IsCancellationRequested) problems.Add(problem);
                continue;
            }
            moved++;
            if (Release(file, placed, integrity) is { } kept) problems.Add(kept);
        }

        // The models in use: every one arrives before any original goes, or none of their originals goes.
        var arrived = new List<(MovableModel File, Placed Placed)>();
        MoveProblem? failure = null;
        foreach (var file in used)
        {
            if (cancellationToken.IsCancellationRequested) break;
            progress?.Report(new MoveStep(++step, count, file.FileName));
            var (placed, problem) = await PlaceAsync(file, to, integrity, hasher, copier, cancellationToken).ConfigureAwait(false);
            if (placed is null)
            {
                failure = problem;
                break;
            }
            arrived.Add((file, placed));
        }
        if (failure is not null || cancellationToken.IsCancellationRequested || arrived.Count < used.Count)
        {
            foreach (var (_, placed) in arrived.Where(a => a.Placed.Created))
            {
                TryDelete(placed.Final); // a copy this move made; a file that was already there stays
                integrity?.Forget(placed.Final);
            }
            leftBehind.AddRange(used.Select(f => f.Path));
            if (failure is not null && !cancellationToken.IsCancellationRequested) problems.Add(failure);
            return new MoveResult(moved, count, problems, leftBehind, Cancelled: cancellationToken.IsCancellationRequested);
        }
        foreach (var (file, placed) in arrived)
        {
            moved++;
            if (Release(file, placed, integrity) is { } kept) problems.Add(kept);
        }
        return new MoveResult(moved, count, problems, leftBehind);
    }

    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. Why a file was not moved, after "{name} could not be moved: ".</summary>
    public const string AlreadyThere = "a different file of that name is already in the new folder";

    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. The copy was not the original's size.</summary>
    public const string CopySizeDiffers = "the copy is not the same size as the original, so the copy was deleted";

    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. The copy failed its SHA-256 check against the catalogue.</summary>
    public const string CopyDoesNotMatch = "the copy did not match the catalogue, so the copy was deleted";

    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword (lens on f5ec71c). The checked copy is in the new folder, but the original is still in the old one.</summary>
    public const string OriginalKept = "it was copied, but the original could not be removed";

    /// <summary>
    /// A file in place in the new folder: <see cref="Created"/> when this move made it (and may delete it again);
    /// <see cref="KeepOriginal"/> when it is the original itself, reached by another path, so the original must never be deleted.
    /// </summary>
    private sealed record Placed(string Final, bool Created, bool KeepOriginal);

    /// <summary>
    /// Copy, check and rename; the original is not touched. Placed: a checked copy, or a file with the same contents, is in the new
    /// folder. Not placed: no copy is left, and the problem says why.
    /// </summary>
    private static async Task<(Placed? Placed, MoveProblem? Problem)> PlaceAsync(MovableModel file, string to, ModelIntegrity? integrity,
        Func<string, CancellationToken, Task<string>> hasher, Func<string, string, CancellationToken, Task> copier, CancellationToken cancellationToken)
    {
        var final = Path.Combine(to, file.FileName);
        var pinned = file.Partial ? null : ModelIntegrity.Expected(file.FileName)?.Sha256;
        if (File.Exists(final))
        {
            try
            {
                // Maybe the original itself, through another path to its folder (null): then its original is never deleted.
                var identity = SameFileOnDisk(file.Path, final);
                // Another file of the same name (an earlier move that stopped, or a copy made by hand) counts when its contents match.
                var same = new FileInfo(final).Length == new FileInfo(file.Path).Length
                           && (pinned is not null
                               ? string.Equals(await hasher(final, cancellationToken).ConfigureAwait(false), pinned, StringComparison.OrdinalIgnoreCase)
                               : await SameBytesAsync(file.Path, final, cancellationToken).ConfigureAwait(false));
                if (!same) return (null, new MoveProblem(file.FileName, AlreadyThere));
                if (pinned is not null) integrity?.Remember(final);
                return (new Placed(final, Created: false, KeepOriginal: identity is null), null); // unknown identity: keep both rather than risk the only copy
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return (null, new MoveProblem(file.FileName, Reason(ex)));
            }
        }
        var temp = final + TempSuffix;
        try
        {
            await copier(file.Path, temp, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(temp).Length != new FileInfo(file.Path).Length)
            {
                TryDelete(temp);
                return (null, new MoveProblem(file.FileName, CopySizeDiffers));
            }
            // A finished catalogue file is checked against its pinned SHA-256; a .part has nothing to be checked against yet.
            if (pinned is not null && !string.Equals(await hasher(temp, cancellationToken).ConfigureAwait(false), pinned, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                return (null, new MoveProblem(file.FileName, CopyDoesNotMatch));
            }
            File.Move(temp, final, overwrite: false);
            if (pinned is not null) integrity?.Remember(final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(temp);
            return (null, new MoveProblem(file.FileName, Reason(ex)));
        }
        return (new Placed(final, Created: true, KeepOriginal: false), null);
    }

    /// <summary>
    /// With the file in place under its own name in the new folder, the original goes (unless it is that very file); null when
    /// done, else the problem (two copies now; the new one is the one used).
    /// </summary>
    private static MoveProblem? Release(MovableModel file, Placed placed, ModelIntegrity? integrity)
    {
        // It may be the only copy: kept, and said (round-3 lens: kept silently, it read as moved).
        if (placed.KeepOriginal) return new MoveProblem(file.FileName, OriginalKept, Copied: true);
        if (SameFileOnDisk(file.Path, placed.Final) != false) return new MoveProblem(file.FileName, OriginalKept, Copied: true); // cannot tell two files: keep both
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

    /// <summary>The two files hold the same bytes (for a file with no pinned SHA-256: a .part, or a catalogue entry without one).</summary>
    private static async Task<bool> SameBytesAsync(string a, string b, CancellationToken cancellationToken)
    {
        await using var x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        await using var y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
        if (x.Length != y.Length) return false;
        var bx = new byte[1 << 20];
        var by = new byte[1 << 20];
        while (true)
        {
            var nx = await x.ReadAtLeastAsync(bx, bx.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            var ny = await y.ReadAtLeastAsync(by, by.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (nx != ny || !bx.AsSpan(0, nx).SequenceEqual(by.AsSpan(0, ny))) return false;
            if (nx == 0) return true;
        }
    }

    /// <summary>
    /// Whether two paths may name one file on disk (another path to its folder: a junction, a mapped drive, a packaged app's
    /// redirected folder). Managed only (Dictum.Core declares no native call, PlatformTests): <paramref name="a"/> is opened
    /// for itself alone, then <paramref name="b"/> is opened; if that open is refused, they may be one file, or something else
    /// holds <paramref name="b"/>; either way null ("cannot tell"), and a caller never deletes then. False when both open:
    /// two files. Null also when <paramref name="a"/> cannot be opened for itself alone.
    /// </summary>
    internal static bool? SameFileOnDisk(string a, string b)
    {
        FileStream exclusive;
        try
        {
            exclusive = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        using (exclusive)
        {
            try
            {
                using var other = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
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
