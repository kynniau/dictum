namespace Dictum.Settings;

/// <summary>Which of the three model folders a model file is in (Decisions 15 and 16, 17/10/2026).</summary>
public enum ModelFolderKind
{
    /// <summary>models.folder: chosen in Settings > Models (or its earlier name, whisper.models_dir).</summary>
    Chosen,

    /// <summary>The per-user default, %LOCALAPPDATA%\Dictum\models.</summary>
    Default,

    /// <summary>models beside Dictum.exe, shown as "Included with Dictum": never downloaded into, written or deleted.</summary>
    Bundled,
}

/// <summary>One model folder, as <see cref="ModelFolders.LookupOrder"/> lists it.</summary>
public sealed record ModelFolder(string Path, ModelFolderKind Kind);

/// <summary>
/// Where a model is found (Decisions 15 and 16, 17/10/2026), for both kinds: whisper .bin files and clean-up .gguf files. A
/// model name resolves to the first folder, in <see cref="LookupOrder"/>, holding its file with the catalogue's exact size: the
/// chosen folder (models.folder, if set), then the per-user default, then the folder beside Dictum.exe that ships base.en.
/// A file that fails is passed over for the next folder; a file in the bundled folder can be held to the full catalogue check
/// (size and SHA-256) by the caller. Nothing found: the name resolves to where a download puts it, the chosen folder or else
/// the per-user default (<see cref="AppPaths.ModelsDir"/>), never the bundled folder. Every model file is still checked (size
/// and SHA-256) before it is loaded, wherever it was found (ModelIntegrity).
/// </summary>
public static class ModelFolders
{
    /// <summary>The folders, in the order a model is looked for: the chosen one (if set and not the default), the default, the bundled one.</summary>
    public static IReadOnlyList<ModelFolder> LookupOrder(AppPaths paths, AppSettings settings)
    {
        var folders = new List<ModelFolder>();
        if (paths.ChosenModelsDir(settings) is { } chosen && !Storage.PathRegistry.SamePath(chosen, paths.DefaultModelsDir))
            folders.Add(new ModelFolder(chosen, ModelFolderKind.Chosen));
        folders.Add(new ModelFolder(paths.DefaultModelsDir, ModelFolderKind.Default));
        folders.Add(new ModelFolder(paths.BundledModelsDir, ModelFolderKind.Bundled));
        return folders;
    }

    /// <summary>
    /// The first file named <paramref name="fileName"/>, in lookup order, that has the catalogue's size (any size when
    /// <paramref name="bytes"/> is null: a name outside the catalogue), with the folder it is in; null when no folder has one.
    /// </summary>
    /// <param name="bundledCheck">
    /// Replaces the size check for a file in the bundled folder (the tray app's: size and SHA-256, said once in the log when it
    /// fails); null: the size check, as for the other folders.
    /// </param>
    public static (string Path, ModelFolderKind Kind)? Locate(string fileName, long? bytes, AppPaths paths, AppSettings settings, Func<string, bool>? bundledCheck = null)
    {
        foreach (var folder in LookupOrder(paths, settings))
        {
            var path = Path.Combine(folder.Path, fileName);
            var file = new FileInfo(path);
            if (!file.Exists) continue;
            // simplification: in the chosen and per-user folders only the size is checked here; the SHA-256 comes at load, as
            // before (ModelIntegrity.Verify), because Settings > Models looks every catalogue model up on each redraw, on the
            // UI thread, and a copied-in 1.5 GB file takes seconds to hash. Ceiling: a same-size file with the wrong bytes in
            // one of those folders is found, then refused at load, instead of being passed over for a good copy further on.
            // Trigger to retire: a report of that; then hash here off the UI thread and remember failures too.
            if (folder.Kind == ModelFolderKind.Bundled && bundledCheck is not null ? bundledCheck(path) : bytes is not long want || file.Length == want)
                return (path, folder.Kind);
        }
        return null;
    }

    /// <summary>The file <see cref="Locate"/> finds, or null.</summary>
    public static string? Find(string fileName, long? bytes, AppPaths paths, AppSettings settings, Func<string, bool>? bundledCheck = null) =>
        Locate(fileName, bytes, paths, settings, bundledCheck)?.Path;

    /// <summary>The file a model name means now: the first that passes (<see cref="Find"/>), else where a download puts it.</summary>
    public static string Resolve(string fileName, long? bytes, AppPaths paths, AppSettings settings, Func<string, bool>? bundledCheck = null) =>
        Find(fileName, bytes, paths, settings, bundledCheck) ?? Path.Combine(paths.ModelsDir(settings), fileName);
}
