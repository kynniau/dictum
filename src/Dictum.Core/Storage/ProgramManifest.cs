namespace Dictum.Storage;

/// <summary>One shipped file: its path relative to the program folder, its size and its SHA-256.</summary>
public sealed record ManifestEntry(string RelativePath, long Bytes, string Sha256);

/// <summary>
/// SPEC §17 (amended 16/10/2026): `manifest.txt`, written by scripts/publish.ps1 into the program folder, lists every
/// shipped file as "relative path TAB bytes TAB sha256" ('#' lines are comments). Removing the program deletes only the
/// files it lists that are still there with the same name and size, then the folders they emptied, then the program
/// folder if nothing else is left: a portable copy unzipped into Downloads never takes the user's own files with it.
/// </summary>
public static class ProgramManifest
{
    public const string FileName = "manifest.txt";

    /// <summary>The manifest in <paramref name="folder"/>, or null when there is none or it cannot be read.</summary>
    public static IReadOnlyList<ManifestEntry>? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var entries = new List<ManifestEntry>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3 || !long.TryParse(parts[1], out var bytes) || !IsSafeRelative(parts[0])) continue;
                entries.Add(new ManifestEntry(parts[0], bytes, parts[2]));
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A relative path that stays inside the folder: no root, no drive, no "..".</summary>
    public static bool IsSafeRelative(string relative) =>
        relative.Length > 0 && !Path.IsPathRooted(relative) && !relative.Contains(':')
        && !relative.Split('\\', '/').Any(part => part is ".." or "");

    /// <summary>
    /// The listed files present in <paramref name="folder"/> with the listed name and size, reached without crossing a
    /// junction or symbolic link: what removal deletes (the detached step applies the same rule).
    /// </summary>
    public static IReadOnlyList<(string Path, long Bytes)> ShippedFiles(string folder, IReadOnlyList<ManifestEntry> entries)
    {
        var files = new List<(string, long)>();
        foreach (var entry in entries)
        {
            var path = Path.Combine(folder, entry.RelativePath);
            if (!CrossesLink(folder, entry.RelativePath) && File.Exists(path) && new FileInfo(path).Length == entry.Bytes) files.Add((path, entry.Bytes));
        }
        return files;
    }

    /// <summary>True when any step from <paramref name="folder"/> down to the entry is missing, a junction or a symbolic link.</summary>
    public static bool CrossesLink(string folder, string relative)
    {
        var path = folder;
        foreach (var part in relative.Split('\\', '/'))
        {
            path = Path.Combine(path, part);
            if (!File.Exists(path) && !Directory.Exists(path)) return true;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }
}
