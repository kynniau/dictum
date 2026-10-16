using Dictum.Settings;

namespace Dictum.Storage;

/// <summary>
/// Free space beside the models folder (SPEC §17): shown in Settings → Storage, a warning before a download that would leave
/// under 2 GB, and a refusal when it would not fit with 200 MB to spare (Decision 15). Also whether a folder is on the network.
/// </summary>
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

    /// <summary>Decision 15 (17/10/2026): a download is refused when the drive would be left with less than this (decimal, 200 MB).</summary>
    public const long SpareBytes = 200_000_000;

    /// <summary>
    /// PROPOSED copy (17/10/2026), for the product owner to approve: why a download of <paramref name="bytes"/> into
    /// <paramref name="folder"/> is refused, when the drive has less free than the file plus <see cref="SpareBytes"/>; null
    /// otherwise, or when the free space cannot be read (a network share, say: the download then goes ahead).
    /// </summary>
    public static string? DownloadRefusal(string folder, string fileName, long bytes, Func<string, long?>? freeBytes = null) =>
        (freeBytes ?? FreeBytes)(folder) is long free && free < bytes + SpareBytes
            ? $"Not enough space for {fileName} ({ModelCatalog.FormatSize(bytes)}): {ModelCatalog.FormatSize(free)} is free on that drive, and Dictum keeps 200 MB spare. Free some space, or choose another model folder in Settings > Models."
            : null;

    /// <summary>
    /// True for a folder on a network share: a UNC path (\\server\share\…, \\?\UNC\…), read from the text alone, or a mapped
    /// network drive (<see cref="DriveType.Network"/>). Models load slowly from such a folder (Decision 15).
    /// </summary>
    public static bool IsNetwork(string folder)
    {
        var path = folder.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = path[4..]; // a device path: \\?\UNC\server\share is a share, \\?\D:\ the drive D:
            if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
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
