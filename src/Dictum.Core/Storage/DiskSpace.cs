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

    /// <summary>
    /// The root of the volume holding <paramref name="path"/> (which need not exist yet): on Windows its drive or share root, as
    /// <see cref="Path.GetPathRoot(string?)"/> gives it; elsewhere every path's root is "/", so it is the deepest mount point the
    /// path lies under ("/Volumes/Backup" for a folder on that disk). Null when the path has no root.
    /// </summary>
    // simplification: mount points are matched by their text (ordinal), not through links. Ceiling: a folder reached through a
    // symbolic link to another volume counts as on the volume its text names. Trigger to retire: a report of a free-space line or
    // a refusal naming the wrong disk; then resolve the path's links first (PathRegistry.FinalPath).
    public static string? VolumeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return Path.GetPathRoot(full);
        var best = Path.GetPathRoot(full);
        foreach (var drive in DriveInfo.GetDrives())
        {
            var mount = drive.Name;
            var inside = full == mount || full.StartsWith(Path.TrimEndingDirectorySeparator(mount) + "/", StringComparison.Ordinal);
            if (inside && (best is null || mount.Length > best.Length)) best = mount;
        }
        return best;
    }

    /// <summary>Bytes free to this user on the drive holding <paramref name="folder"/> (which need not exist yet), or null when unknown.</summary>
    public static long? FreeBytes(string folder)
    {
        try
        {
            var root = VolumeRoot(folder);
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
    /// APPROVED 22/10/2026 copy (proposed 17/10/2026): why a download of <paramref name="bytes"/> into
    /// <paramref name="folder"/> is refused, when the drive has less free than the file plus <see cref="SpareBytes"/>; null
    /// otherwise, or when the free space cannot be read (a network share, say: the download then goes ahead).
    /// <paramref name="pendingBytes"/> is what the downloads already under way will still write there (lens, 19/10/2026): the
    /// file must fit beside them, and the message then says so (APPROVED 22/10/2026; proposed 19/10/2026).
    /// </summary>
    public static string? DownloadRefusal(string folder, string fileName, long bytes, Func<string, long?>? freeBytes = null, long pendingBytes = 0) =>
        (freeBytes ?? FreeBytes)(folder) is long free && free - pendingBytes < bytes + SpareBytes
            ? pendingBytes > 0
                ? $"Not enough space for {fileName} ({ModelCatalog.FormatSize(bytes)}): {ModelCatalog.FormatSize(free)} is free on that drive, the downloads already under way need {ModelCatalog.FormatSize(pendingBytes)} of it, and Dictum keeps 200 MB spare. Free some space, or choose another model folder in Settings > Models."
                : $"Not enough space for {fileName} ({ModelCatalog.FormatSize(bytes)}): {ModelCatalog.FormatSize(free)} is free on that drive, and Dictum keeps 200 MB spare. Free some space, or choose another model folder in Settings > Models."
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
            return VolumeRoot(OperatingSystem.IsWindows() ? path : folder) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// "Warning: …" when downloading <paramref name="bytes"/> into <paramref name="folder"/> would leave under 2 GB free; null
    /// otherwise or when the free space cannot be read. No final full stop: the download question adds its own.
    /// <paramref name="pendingBytes"/>: what the downloads already under way will still write there, counted as used.
    /// </summary>
    public static string? DownloadWarning(string folder, long bytes, Func<string, long?>? freeBytes = null, long pendingBytes = 0)
    {
        if ((freeBytes ?? FreeBytes)(folder) is not long free) return null;
        var after = free - pendingBytes - bytes;
        if (after >= WarnBelowBytes) return null;
        return after < 0
            ? pendingBytes > 0
                ? $"Warning: only {ModelCatalog.FormatSize(free)} is free on that drive, not enough for this download beside the downloads already under way" // APPROVED 22/10/2026 copy (proposed 19/10/2026)
                : $"Warning: only {ModelCatalog.FormatSize(free)} is free on that drive, not enough for this download"
            : $"Warning: this leaves only {ModelCatalog.FormatSize(after)} free on that drive (under 2 GB)";
    }
}
