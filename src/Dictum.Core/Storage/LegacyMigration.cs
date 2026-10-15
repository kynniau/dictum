using System.Text.Json.Nodes;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Storage;

/// <summary>What the migration did, for the log and the start-up notice ("logs the move", SPEC §21).</summary>
/// <param name="Left">Items that could not be moved (held open); tried again at the next start.</param>
/// <param name="Skipped">Items left in the Dictate folders because Dictum already had a file of that name. They stay
/// there, are listed to the user, and "Remove Dictum completely" deletes them with the rest of those folders.</param>
public sealed record MigrationReport(IReadOnlyList<string> Moved, IReadOnlyList<string> Left, IReadOnlyList<string> Skipped)
{
    public static readonly MigrationReport None = new([], [], []);

    /// <summary>The files were copied, not moved: Dictate's folders are left as they were (a packaged Dictum, below).</summary>
    public bool Copied { get; init; }

    public bool Any => Moved.Count > 0 || Left.Count > 0 || Skipped.Count > 0;
}

/// <summary>
/// The one-time move from the earlier build's folders (named Dictate) to Dictum's (SPEC §21, amended 16/10/2026):
/// settings, .env, the dictionary, corrections and profanity list, models, the verified-model record, calibration
/// recordings and logs. Each item moves only if Dictum has nothing of that name yet, so it never overwrites Dictum's own
/// data and needs no marker; a same-named item is recorded as skipped. An item that cannot be moved (held open) stays
/// where it is and is reported; the next start tries again. Settings that point into the old folder are re-pointed.
/// It never runs while the earlier build is running (<see cref="RunWhenSafe"/>).
/// </summary>
public static class LegacyMigration
{
    private static readonly string[] ConfigFiles = ["settings.json", ".env", "dictionary.txt", "corrections.txt", "profanity.txt"];
    private static readonly string[] LocalFolders = ["models", "calibration", "logs"];
    private const string VerifiedRecord = "verified-models.json";

    /// <summary>True when the earlier build left something that would move now (not counting items already skipped).</summary>
    public static bool HasWork(AppPaths paths, LegacyFolders legacy) => Candidates(paths, legacy).Any(c => !Exists(c.To));

    /// <summary>
    /// The start-up step: when there is something to move and the earlier build is running, asks the user to quit it
    /// (<paramref name="askRetry"/>: true = Retry, false = Quit Dictum) and does nothing else until it has quit. Returns
    /// null when the user chose to quit, else what was moved.
    /// </summary>
    public static MigrationReport? RunWhenSafe(AppPaths paths, LegacyFolders legacy, Func<bool> earlierBuildRunning, Func<bool> askRetry, bool copy = false)
    {
        if (!HasWork(paths, legacy)) return MigrationReport.None;
        while (earlierBuildRunning())
        {
            if (!askRetry()) return null;
        }
        return Run(paths, legacy, copy);
    }

    /// <param name="copy">
    /// Copy instead of move, and leave Dictate's folders in place: a packaged Dictum's app data is redirected into the
    /// package's private folder, which Windows deletes when the package is uninstalled (packaging lane, from Microsoft
    /// Learn on package data redirection; inferred, not observed), so moving would put the user's only copy there.
    /// </param>
    public static MigrationReport Run(AppPaths paths, LegacyFolders legacy, bool copy = false)
    {
        var moved = new List<string>();
        var left = new List<string>();
        var skipped = new List<string>();
        foreach (var (from, to, isFolder) in Candidates(paths, legacy))
        {
            if (isFolder) MoveFolder(from, to, copy, moved, left, skipped);
            else if (MoveFile(from, to, copy, moved, left, skipped) && Path.GetFileName(from) == VerifiedRecord)
            {
                new ModelIntegrity(paths.VerifiedModelsFile, Core.NullLog.Instance).Rebase(Path.Combine(legacy.LocalDir, "models"), paths.DefaultModelsDir);
            }
        }
        if (!copy)
        {
            RemoveIfEmpty(legacy.ConfigDir);
            foreach (var folder in LocalFolders) RemoveIfEmpty(Path.Combine(legacy.LocalDir, folder));
            RemoveIfEmpty(legacy.LocalDir);
        }
        if (moved.Count > 0) Repoint(paths, legacy);
        return new MigrationReport(moved, left, skipped) { Copied = copy };
    }

    /// <summary>Every item the migration considers, in order: the settings folder's files (unless --config is used), then the local data.</summary>
    private static IEnumerable<(string From, string To, bool IsFolder)> Candidates(AppPaths paths, LegacyFolders legacy)
    {
        if (!paths.ConfigOverridden && Directory.Exists(legacy.ConfigDir) && !PathRegistry.SamePath(legacy.ConfigDir, paths.ConfigDir))
        {
            foreach (var name in ConfigFiles.Where(n => File.Exists(Path.Combine(legacy.ConfigDir, n))))
                yield return (Path.Combine(legacy.ConfigDir, name), Path.Combine(paths.ConfigDir, name), false);
        }
        if (Directory.Exists(legacy.LocalDir) && !PathRegistry.SamePath(legacy.LocalDir, paths.LocalDir))
        {
            foreach (var folder in LocalFolders.Where(f => Directory.Exists(Path.Combine(legacy.LocalDir, f))))
            {
                var from = Path.Combine(legacy.LocalDir, folder);
                if (!Directory.Exists(Path.Combine(paths.LocalDir, folder))) yield return (from, Path.Combine(paths.LocalDir, folder), true);
                else foreach (var file in Directory.GetFiles(from)) yield return (file, Path.Combine(paths.LocalDir, folder, Path.GetFileName(file)), false);
            }
            if (File.Exists(Path.Combine(legacy.LocalDir, VerifiedRecord)))
                yield return (Path.Combine(legacy.LocalDir, VerifiedRecord), paths.VerifiedModelsFile, false);
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool MoveFile(string from, string to, bool copy, List<string> moved, List<string> left, List<string> skipped)
    {
        if (!File.Exists(from)) return false;
        if (File.Exists(to))
        {
            skipped.Add($"{from} (Dictum already has {Path.GetFileName(to)})");
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (copy) File.Copy(from, to);
            else File.Move(from, to);
            moved.Add($"{from} -> {to}{(copy ? " (copied)" : "")}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add($"{from} ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// The whole folder in one rename when Dictum has none yet; otherwise file by file, never replacing Dictum's files.
    /// Copying goes file by file through the whole tree, never into a junction or symbolic link.
    /// </summary>
    private static void MoveFolder(string from, string to, bool copy, List<string> moved, List<string> left, List<string> skipped)
    {
        if (!Directory.Exists(from)) return;
        if (copy)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(from, "*", options))
                MoveFile(file, Path.Combine(to, Path.GetRelativePath(from, file)), copy: true, moved, left, skipped);
            return;
        }
        if (!Directory.Exists(to))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                moved.Add($"{from} -> {to}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file inside is held open: fall back to moving what can be moved.
            }
        }
        foreach (var file in Directory.GetFiles(from)) MoveFile(file, Path.Combine(to, Path.GetFileName(file)), copy: false, moved, left, skipped);
        RemoveIfEmpty(from);
    }

    private static void RemoveIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left in place; harmless
        }
    }

    /// <summary>whisper.model, whisper.models_dir, llm.model and llm.prompt_file values inside the old folder now point at the new one.</summary>
    private static void Repoint(AppPaths paths, LegacyFolders legacy)
    {
        var load = SettingsLoader.LoadFile(paths.SettingsFile);
        if (!load.Usable || !load.FileExisted) return;
        var s = load.Settings;
        foreach (var (key, value) in new (string, string?)[] { ("whisper.model", s.Whisper.Model), ("whisper.models_dir", s.Whisper.ModelsDir), ("llm.model", s.Llm.Model), ("llm.prompt_file", s.Llm.PromptFile) })
        {
            if (value is null || !Path.IsPathFullyQualified(value)) continue;
            foreach (var (from, to) in new[] { (legacy.LocalDir, paths.LocalDir), (legacy.ConfigDir, paths.ConfigDir) })
            {
                if (!PathRegistry.SamePath(value, from) && !PathRegistry.IsInside(value, from)) continue;
                var rebased = PathRegistry.SamePath(value, from) ? to : Path.Combine(to, Path.GetRelativePath(from, value));
                SettingsFile.SetValue(paths.SettingsFile, key, JsonValue.Create(rebased));
                break;
            }
        }
    }
}
