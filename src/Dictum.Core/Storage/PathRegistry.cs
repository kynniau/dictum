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

    /// <summary>verified-models.json: which model files already passed their SHA-256 check.</summary>
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
            new(paths.ConsentFile, DataCategory.Settings, "consent to cloud clean-up"),
            new(paths.ConsentFile + ".tmp", DataCategory.Settings, "consent being saved"),
            new(paths.DictionaryFile, DataCategory.PersonalFiles, "personal dictionary"),
            new(paths.CorrectionsFile, DataCategory.PersonalFiles, "corrections"),
            new(paths.SnippetsFile, DataCategory.PersonalFiles, "snippets"),
            new(paths.ProfanityOverrideFile, DataCategory.PersonalFiles, "your profanity word list"),
            new(paths.EnvFile, DataCategory.Secrets, "API keys (.env)"),
            new(paths.ExeEnvFile, DataCategory.Secrets, "API keys beside the program (.env)"),
            new(paths.VerifiedModelsFile, DataCategory.ModelVerification, "record of verified model files"),
            new(paths.VerifiedModelsFile + ".tmp", DataCategory.ModelVerification, "record being saved"),
            new(paths.CalibrationDir, DataCategory.CalibrationRecordings, "calibration recordings", "clip-*"),
            new(paths.CalibrationDir, DataCategory.CalibrationRecordings, "calibration take list", "takes.txt"),
            new(paths.LogsDir, DataCategory.Logs, "logs", "*.log"),
        };
        foreach (var dir in ModelFolders(paths, settings))
        {
            list.Add(new(dir, DataCategory.Models, "models", ModelFiles.Pattern));
        }
        list.Add(new(paths.DefaultModelsDir, DataCategory.AppFolder, "models folder"));
        list.Add(new(paths.CalibrationDir, DataCategory.AppFolder, "calibration folder"));
        list.Add(new(paths.LogsDir, DataCategory.AppFolder, "logs folder"));
        list.Add(new(paths.LocalDir, DataCategory.AppFolder, "local data folder"));
        list.Add(new(paths.ConfigDir, DataCategory.AppFolder, "settings folder"));
        return list;
    }

    /// <summary>The default models folder, plus whisper.models_dir when it points elsewhere (only catalogue files are touched there).</summary>
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
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
