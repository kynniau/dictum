using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Storage;

/// <summary>A model file on disk, for Settings → Storage.</summary>
/// <param name="Name">Catalogue name ("small.en", "qwen2.5-1.5b"), or the file name for a partial download.</param>
/// <param name="Partial">An unfinished download (.part).</param>
/// <param name="Folder">Which model folder it is in (<see cref="ModelFolders.LookupOrder"/>).</param>
public sealed record StoredModel(string Name, string Path, long Bytes, bool IsCleanup, bool Partial, ModelFolderKind Folder = ModelFolderKind.Default)
{
    /// <summary>It came with Dictum (the bundled folder): read-only, so it has no Delete.</summary>
    public bool ReadOnly => Folder == ModelFolderKind.Bundled;
}

/// <summary>What a model deletion did.</summary>
public sealed record ModelDeletion(bool Deleted, long FreedBytes, string Message);

/// <summary>
/// The model files Dictum may delete (SPEC §17): only files the catalogue knows (and their .part downloads), only inside
/// a models folder. A model chosen by path from elsewhere is only ever forgotten, never deleted, and the model in use is
/// never deleted: another must be chosen first.
/// </summary>
public static class ModelFiles
{
    /// <summary>The registry pattern for "catalogue model files and their partial downloads".</summary>
    public const string Pattern = "<catalogue model files>";

    /// <summary>PROPOSED copy (17/10/2026), for the product owner to approve: why a model that came with Dictum is not deleted.</summary>
    internal static string BundledNotDeleted(string fileName) => $"{fileName} came with Dictum, so Dictum does not delete it.";

    /// <summary>A catalogue model file name, or one of their partial downloads.</summary>
    public static bool IsModelFile(string fileName)
    {
        var name = fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? fileName[..^5] : fileName;
        return ModelCatalog.FindByFile(name) is not null || CleanupCatalog.FindByFile(name) is not null;
    }

    /// <summary>
    /// Every catalogue model file and partial download in Dictum's own models folders (the chosen one and the default), in
    /// lookup order, with sizes read from the file system; with <paramref name="withBundled"/>, the read-only files that came
    /// with Dictum too (Settings → Storage shows them; nothing that deletes or replaces a model sees them).
    /// </summary>
    public static IReadOnlyList<StoredModel> List(AppPaths paths, AppSettings settings, bool withBundled = false)
    {
        var models = new List<StoredModel>();
        foreach (var folder in ModelFolders.LookupOrder(paths, settings).Where(f => (withBundled || f.Kind != ModelFolderKind.Bundled) && Directory.Exists(f.Path)))
        {
            foreach (var file in new DirectoryInfo(folder.Path).EnumerateFiles().Where(f => IsModelFile(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var partial = file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                var finalName = partial ? file.Name[..^5] : file.Name;
                var speech = ModelCatalog.FindByFile(finalName);
                var name = partial ? file.Name : speech?.Name ?? CleanupCatalog.FindByFile(finalName)!.Name;
                models.Add(new StoredModel(name, file.FullName, file.Length, speech is null, partial, folder.Kind));
            }
        }
        return models;
    }

    /// <summary>
    /// Deletes one catalogue model: the file, its .part and its verified record. Refuses a file that came with Dictum (in the
    /// bundled folder, also by a name <see cref="AppPaths.IsBundled"/> sees through), a path outside the models folders, a file the catalogue
    /// does not know, and the model in use (<paramref name="inUse"/>).
    /// </summary>
    public static ModelDeletion Delete(string path, AppPaths paths, AppSettings settings, IEnumerable<string?> inUse, ModelIntegrity integrity)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        // The backstop (lens, 17/10/2026): whichever folder it was listed in, a file that is in the bundled folder under another
        // name (through a junction, say) is never deleted.
        if (paths.IsBundled(full)) return new ModelDeletion(false, 0, BundledNotDeleted(name));
        if (!PathRegistry.ModelFolders(paths, settings).Any(dir => PathRegistry.IsInside(full, dir) && PathRegistry.SamePath(Path.GetDirectoryName(full)!, dir)))
            return new ModelDeletion(false, 0, $"{name} is not in Dictum's models folder, so Dictum does not delete it (a model chosen by path is only forgotten).");
        if (!IsModelFile(name))
            return new ModelDeletion(false, 0, $"{name} is not a catalogue model, so Dictum does not delete it.");
        var finalPath = full.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? full[..^5] : full;
        if (inUse.Any(p => p is not null && PathRegistry.SamePath(p, finalPath)))
            return new ModelDeletion(false, 0, $"{Path.GetFileName(finalPath)} is in use. Choose another model first, then delete it.");
        long freed = 0;
        foreach (var file in new[] { finalPath, finalPath + ".part" })
        {
            if (!File.Exists(file)) continue;
            var bytes = new FileInfo(file).Length;
            File.Delete(file);
            freed += bytes;
        }
        integrity.Forget(finalPath);
        return new ModelDeletion(true, freed, $"Deleted {Path.GetFileName(finalPath)} ({ModelCatalog.FormatSize(freed)} freed).");
    }
}
