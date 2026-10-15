using Dictum.Settings;

namespace Dictum.Storage;

/// <summary>Free space beside the models folder (SPEC §17): shown in Settings → Storage, and a warning before a download that would leave too little.</summary>
public static class DiskSpace
{
    /// <summary>SPEC §17: warn before any download that would leave under 2 GB (decimal, as every size Dictum shows) free.</summary>
    public const long WarnBelowBytes = 2_000_000_000;

    /// <summary>Bytes free to this user on the drive holding <paramref name="folder"/> (which need not exist yet), or null when unknown.</summary>
    public static long? FreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Warning: …" when downloading <paramref name="bytes"/> into <paramref name="folder"/> would leave under 2 GB free; null
    /// otherwise or when the free space cannot be read. No final full stop: the download question adds its own.
    /// </summary>
    public static string? DownloadWarning(string folder, long bytes, Func<string, long?>? freeBytes = null)
    {
        if ((freeBytes ?? FreeBytes)(folder) is not long free) return null;
        var after = free - bytes;
        if (after >= WarnBelowBytes) return null;
        return after < 0
            ? $"Warning: only {ModelCatalog.FormatSize(free)} is free on that drive, not enough for this download"
            : $"Warning: this leaves only {ModelCatalog.FormatSize(after)} free on that drive (under 2 GB)";
    }
}
