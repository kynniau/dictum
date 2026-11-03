using Dictum.Settings;

namespace Dictum.Storage;

/// <summary>What a stored item is, which decides which deletion removes it (SPEC §17).</summary>
public enum DataCategory
{
    /// <summary>settings.json (and its write-in-progress .tmp).</summary>
    Settings,

    /// <summary>The user's own text files: dictionary.txt, corrections.txt, snippets.txt, a profanity.txt override.</summary>
    PersonalFiles,

    /// <summary>.env files with API keys: only "Remove Dictum completely" deletes them.</summary>
    Secrets,

    /// <summary>Speech and clean-up model files the catalogue knows, and their partial downloads.</summary>
    Models,

    /// <summary>
    /// The same files in a chosen models folder outside the user's profile, which other users may share (the README's layout
    /// for shared virtual desktops; lens, 17/10/2026): Storage's Delete removes them one at a time and "Remove Dictum
    /// completely" lists each one, but "Delete all app data", whose question shows only a count and a size, leaves them alone.
    /// </summary>
    SharedModels,

    /// <summary>verified-models.json: which model files already passed their SHA-256 check; and the GPU-load markers (a model the GPU could not load).</summary>
    ModelVerification,

    CalibrationRecordings,

    Logs,

    /// <summary>One of the app's own folders: removed once empty.</summary>
    AppFolder,

    /// <summary>Platform items: the Start menu shortcut, the launch-at-login entry, the program folder and its shipped files.</summary>
    Shortcut,
    StartupEntry,
    ProgramFolder,

    /// <summary>One file the program shipped with, from its manifest.txt; removed after Dictum exits.</summary>
    ProgramFile,

    /// <summary>What the earlier build (Dictate) left in its own folders after the migration (skipped or unmovable items).</summary>
    EarlierBuild,
}

/// <summary>One place Dictum writes: a file, a set of files in a folder (by pattern), a folder, or a platform item.</summary>
/// <param name="Pattern">For a folder: the file names it holds that belong to this entry ("*.log"), or null for the folder itself.</param>
public sealed record OwnedLocation(string Path, DataCategory Category, string Description, string? Pattern = null)
{
    public bool IsFolder => Pattern is not null || Category is DataCategory.AppFolder or DataCategory.ProgramFolder;
}

/// <summary>
/// The app's own path registry (SPEC §17): every location Dictum writes, in one list, so Settings → Storage, "Delete all
/// app data" and "Remove Dictum completely" can never miss one. A test runs the app's writers in a scratch folder and
/// fails if any file they create is not covered here. Never lists a path outside the app's own folders, the configured
/// models folder (and there only catalogue files) or the platform's own items.
/// </summary>
public static class PathRegistry
{
    /// <summary>Every location under the app's folders (the platform adds its own: shortcut, startup entry, program folder).</summary>
    public static IReadOnlyList<OwnedLocation> Locations(AppPaths paths, AppSettings settings)
    {
        var list = new List<OwnedLocation>
        {
            new(paths.SettingsFile, DataCategory.Settings, "settings"),
            new(paths.SettingsFile + ".tmp", DataCategory.Settings, "settings being saved"),
            new(paths.ConsentFile, DataCategory.Settings, "consent to cloud clean-up and cloud speech"), // APPROVED label 22/10/2026 (proposed 22/10/2026)
            new(paths.ConsentFile + ".tmp", DataCategory.Settings, "consent being saved"),
            new(paths.DictionaryFile, DataCategory.PersonalFiles, "personal dictionary"),
            new(paths.CorrectionsFile, DataCategory.PersonalFiles, "corrections"),
            new(paths.SnippetsFile, DataCategory.PersonalFiles, "snippets"),
            new(paths.ProfanityOverrideFile, DataCategory.PersonalFiles, "your profanity word list"),
            new(paths.EnvFile, DataCategory.Secrets, "API keys (.env)"),
            new(paths.ExeEnvFile, DataCategory.Secrets, "API keys beside the program (.env)"),
            // PROPOSED 30/10/2026 (API keys, .demo-preview/approve-api-keys.md rows 6 and 7): labels only; the keys are in the credential store.
            new(paths.KeysFile, DataCategory.Secrets, "names of your stored API keys (keys.json)"),
            new(paths.KeysFile + ".tmp", DataCategory.Secrets, "names being saved"),
            new(paths.VerifiedModelsFile, DataCategory.ModelVerification, "record of verified model files"),
            new(paths.VerifiedModelsFile + ".tmp", DataCategory.ModelVerification, "record being saved"),
            new(paths.SpeechGpuMarkerFile, DataCategory.ModelVerification, "speech model the GPU could not load"),
            new(paths.CleanupGpuMarkerFile, DataCategory.ModelVerification, "clean-up model the GPU could not load"),
            new(paths.CalibrationDir, DataCategory.CalibrationRecordings, "calibration recordings", "clip-*"),
            new(paths.CalibrationDir, DataCategory.CalibrationRecordings, "calibration take list", "takes.txt"),
            new(paths.LogsDir, DataCategory.Logs, "logs", "*.log"),
        };
        foreach (var dir in ModelFolders(paths, settings))
        {
            var shared = !SamePath(dir, paths.DefaultModelsDir) && !paths.InProfile(dir);
            list.Add(new(dir, shared ? DataCategory.SharedModels : DataCategory.Models, "models", ModelFiles.Pattern));
        }
        list.Add(new(paths.DefaultModelsDir, DataCategory.AppFolder, "models folder"));
        list.Add(new(paths.CalibrationDir, DataCategory.AppFolder, "calibration folder"));
        list.Add(new(paths.LogsDir, DataCategory.AppFolder, "logs folder"));
        list.Add(new(paths.LocalDir, DataCategory.AppFolder, "local data folder"));
        list.Add(new(paths.ConfigDir, DataCategory.AppFolder, "settings folder"));
        return list;
    }

    /// <summary>
    /// The default models folder, plus the chosen one (models.folder, or its earlier name whisper.models_dir) when it is elsewhere:
    /// only catalogue files are touched there, and when it is outside the user's profile "Delete all app data" leaves it alone
    /// (<see cref="DataCategory.SharedModels"/>). Never the bundled folder beside Dictum.exe, whose models came with Dictum.
    /// </summary>
    public static IEnumerable<string> ModelFolders(AppPaths paths, AppSettings settings)
    {
        yield return paths.DefaultModelsDir;
        var configured = paths.ModelsDir(settings);
        if (!SamePath(configured, paths.DefaultModelsDir)) yield return configured;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (not the folder itself).</summary>
    public static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        // A drive root keeps its separator through TrimEndingDirectorySeparator ("D:\"), so one is added only when missing
        // (lens on PR #61: "D:\Models" was not inside "D:\" because the root became "D:\\").
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length;
    }

    /// <summary>
    /// The place <paramref name="path"/> names, for telling whether two paths are the same folder by different names (lens,
    /// 17/10/2026: a junction to the bundled folder was taken for another folder, and the base.en in it was deleted): full,
    /// with 8.3 short names expanded (Path.GetFullPath does that), without a \\?\ or \\.\ prefix (\\?\UNC\server\share is
    /// \\server\share), without a trailing separator, and with each junction or symbolic link along it replaced by its target.
    /// The parts that do not exist are kept as written.
    /// </summary>
    // simplification: links are followed only on this PC's own drives (on a network folder each step would be a round trip to
    // the server), and a drive letter made with subst, or a volume mounted in a folder, is not followed (that needs a native
    // call, which this assembly does not make: ArchitectureTests). Ceiling: a subst drive, a mounted volume, or a share that
    // points back at a folder on this PC is not seen as the folder it names (a \\?\Volume{...} name for a folder is refused
    // where models.folder is read: SettingsLoader). Trigger to retire: a report of any of them; then ask the platform for the
    // final path (GetFinalPathNameByHandle).
    public static string FinalPath(string path) => FinalPath(path, 0);

    private static string FinalPath(string path, int depth)
    {
        var full = Plain(path);
        var root = Path.GetPathRoot(full);
        if (depth > 31 || string.IsNullOrEmpty(root) || DiskSpace.IsNetwork(full)) return full; // 31: a cycle of links ends
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileSystemInfo? target;
            try
            {
                target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true); // null: not a link
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Path.Combine([current, .. parts[(i + 1)..]]); // missing or unreadable: nothing further along is a link to follow
            }
            if (target is not null) return FinalPath(Path.Combine([target.FullName, .. parts[(i + 1)..]]), depth + 1);
        }
        return current;
    }

    /// <summary>
    /// True for a path in the device namespace that <see cref="FinalPath"/> cannot see through (re-lens, 17/10/2026: through
    /// \\?\Volume{GUID}\ a models.folder named the bundled folder by a name that no comparison took for it, and the bundled
    /// base.en was deleted): one that starts with \\?\, \\.\ or \??\ (either slash) other than \\?\X:\ and \\?\UNC\, the two
    /// forms <see cref="Plain"/> unwraps (it unwraps them behind \\.\ too; those are refused all the same, so only the
    /// \\?\ forms are taken). The settings load refuses such a models.folder (SettingsLoader).
    /// </summary>
    internal static bool IsUnsupportedDevicePath(string path)
    {
        var slashes = path.Replace('/', '\\'); // //?/ and //./ are device paths too; only the backslash forms below are unwrapped
        if (!(slashes.StartsWith(@"\\?\", StringComparison.Ordinal) || slashes.StartsWith(@"\\.\", StringComparison.Ordinal) || slashes.StartsWith(@"\??\", StringComparison.Ordinal)))
            return false;
        var drive = path.Length >= 7 && path.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\';
        return !drive && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full (8.3 short names expanded), without a \\?\ or \\.\ prefix before a drive or UNC path and without a trailing separator.</summary>
    private static string Plain(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = path[4..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + rest[4..];
            else if (rest.Length >= 2 && rest[1] == ':') path = rest;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
