using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.Storage;

/// <summary>What a deletion in Settings → Storage covers (SPEC §17).</summary>
public enum RemovalScope
{
    CalibrationRecordings,
    Logs,

    /// <summary>
    /// "Delete all app data": models, recordings, logs, the verified record; settings and personal files kept; never .env. A
    /// chosen models folder outside the user's profile is left alone (<see cref="DataCategory.SharedModels"/>).
    /// </summary>
    AllData,

    /// <summary>"Delete all app data" with "including settings" ticked: also settings.json and the personal files; still never .env.</summary>
    AllDataIncludingSettings,

    /// <summary>"Remove Dictum completely": everything, .env and the platform's items included.</summary>
    Everything,
}

/// <summary>One file or folder a deletion removes, with its size read from the file system (a folder: removed if empty).</summary>
/// <param name="IsLink">A junction or symbolic link: removed as a link, never followed, so its target is never touched.</param>
public sealed record RemovalItem(string Path, long Bytes, DataCategory Category, bool IsFolder, bool IsLink = false);

/// <summary>What happened: bytes freed and what could not be removed (in use, access denied).</summary>
public sealed record RemovalResult(long FreedBytes, IReadOnlyList<string> Failures);

/// <summary>
/// The list a deletion shows before it asks and then removes, built from <see cref="PathRegistry"/> and what is on disk
/// (sizes from the file system, not from a stored count). Files the app does not own (an unknown file in the models
/// folder, a note the user left in the settings folder) are never in it, so their folder stays.
/// </summary>
public sealed class RemovalPlan
{
    private RemovalPlan(IReadOnlyList<RemovalItem> items) => Items = items;

    public IReadOnlyList<RemovalItem> Items { get; }

    public long TotalBytes => Items.Sum(i => i.Bytes);

    public static IReadOnlyList<DataCategory> CategoriesFor(RemovalScope scope) => scope switch
    {
        RemovalScope.CalibrationRecordings => [DataCategory.CalibrationRecordings],
        RemovalScope.Logs => [DataCategory.Logs],
        RemovalScope.AllData => [DataCategory.Models, DataCategory.ModelVerification, DataCategory.CalibrationRecordings, DataCategory.Logs],
        RemovalScope.AllDataIncludingSettings =>
            [DataCategory.Models, DataCategory.ModelVerification, DataCategory.CalibrationRecordings, DataCategory.Logs, DataCategory.Settings, DataCategory.PersonalFiles],
        _ => Enum.GetValues<DataCategory>(),
    };

    /// <param name="platform">The platform's own items (shortcut, startup entry, program files and folder); only "Everything" uses them.</param>
    /// <param name="legacy">The earlier build's folders: "Everything" deletes whatever the migration left in them (SPEC §21).</param>
    public static RemovalPlan Build(AppPaths paths, AppSettings settings, RemovalScope scope, IReadOnlyList<OwnedLocation>? platform = null,
        LegacyFolders? legacy = null)
    {
        var categories = CategoriesFor(scope).ToHashSet();
        var items = new List<RemovalItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(RemovalItem item)
        {
            if (seen.Add(item.Path)) items.Add(item);
        }
        foreach (var location in PathRegistry.Locations(paths, settings).Where(l => categories.Contains(l.Category)))
        {
            if (location.Category == DataCategory.AppFolder)
            {
                if (scope == RemovalScope.Everything && Directory.Exists(location.Path)) Add(new RemovalItem(location.Path, 0, location.Category, true));
                continue;
            }
            if (location.Pattern is null)
            {
                if (File.Exists(location.Path)) Add(new RemovalItem(location.Path, new FileInfo(location.Path).Length, location.Category, false));
                continue;
            }
            if (!Directory.Exists(location.Path)) continue;
            foreach (var file in new DirectoryInfo(location.Path).EnumerateFiles().Where(f => Matches(location.Pattern, f.Name)))
            {
                // The backstop (lens, 17/10/2026): a model file that is in the bundled folder under another name is never listed.
                if (location.Category is DataCategory.Models or DataCategory.SharedModels && paths.IsBundled(file.FullName)) continue;
                Add(new RemovalItem(file.FullName, file.Length, location.Category, false));
            }
        }
        if (scope == RemovalScope.Everything && platform is not null)
        {
            foreach (var location in platform) Add(new RemovalItem(location.Path, FolderBytes(location), location.Category, location.IsFolder));
        }
        if (scope == RemovalScope.Everything && legacy is not null)
        {
            // Whatever the migration left behind in Dictate's own folders (a same-named .env it skipped, a file it could not move):
            // every file and folder in them, which only the earlier build and the migration ever wrote.
            // Junctions and symbolic links inside them are removed as links and never followed (lens probe, 16/10/2026: a
            // junction to another drive had that drive's file listed and deleted).
            foreach (var root in new[] { legacy.ConfigDir, legacy.LocalDir }.Where(Directory.Exists))
            {
                var dir = new DirectoryInfo(root);
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Add(new RemovalItem(dir.FullName, 0, DataCategory.EarlierBuild, true, IsLink: true));
                    continue;
                }
                Walk(dir, Add);
                Add(new RemovalItem(dir.FullName, 0, DataCategory.EarlierBuild, true));
            }
        }
        // Files before the folders that hold them; an app folder after the folders inside it.
        return new RemovalPlan([.. items.OrderBy(i => i.IsFolder).ThenByDescending(i => i.Path.Length)]);
    }

    /// <summary>Every file and folder under <paramref name="dir"/>, without entering any junction or symbolic link (those are listed as links).</summary>
    private static void Walk(DirectoryInfo dir, Action<RemovalItem> add)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            var link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo sub)
            {
                if (!link) Walk(sub, add);
                add(new RemovalItem(sub.FullName, 0, DataCategory.EarlierBuild, true, link));
            }
            else
            {
                add(new RemovalItem(entry.FullName, link ? 0 : ((FileInfo)entry).Length, DataCategory.EarlierBuild, false, link));
            }
        }
    }

    /// <summary>A platform item's size: a file's length; folders and registry entries count 0 (their files are listed on their own).</summary>
    private static long FolderBytes(OwnedLocation location) => !location.IsFolder && File.Exists(location.Path) ? new FileInfo(location.Path).Length : 0;

    private static bool Matches(string pattern, string name) => pattern switch
    {
        ModelFiles.Pattern => ModelFiles.IsModelFile(name),
        "*.log" => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase),
        "clip-*" => name.StartsWith("clip-", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Deletes the files, then each listed folder that is now empty. Platform items (shortcut, startup entry, program
    /// folder) are skipped here: <see cref="Platform.IRemovalSteps"/> removes those.
    /// </summary>
    public RemovalResult Execute()
    {
        long freed = 0;
        var failures = new List<string>();
        foreach (var item in Items.Where(i => i.Category is not (DataCategory.Shortcut or DataCategory.StartupEntry or DataCategory.ProgramFolder or DataCategory.ProgramFile)))
        {
            try
            {
                if (item.IsLink)
                {
                    // The link itself: RemoveDirectory / DeleteFile on a reparse point never touch what it points to.
                    if (item.IsFolder && Directory.Exists(item.Path)) Directory.Delete(item.Path);
                    else if (!item.IsFolder && File.Exists(item.Path)) File.Delete(item.Path);
                    continue;
                }
                if (item.IsFolder)
                {
                    if (Directory.Exists(item.Path) && !Directory.EnumerateFileSystemEntries(item.Path).Any()) Directory.Delete(item.Path);
                    continue;
                }
                if (!File.Exists(item.Path)) continue;
                File.Delete(item.Path);
                freed += item.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{item.Path}: {ex.Message}");
            }
        }
        return new RemovalResult(freed, failures);
    }
}
