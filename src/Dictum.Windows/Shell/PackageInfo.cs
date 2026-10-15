using Dictum.Core;
using Dictum.Platform;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Shell;

/// <summary>
/// Package identity (SPEC §18.2): GetCurrentPackageFullName answers ERROR_INSUFFICIENT_BUFFER for a packaged process and
/// APPMODEL_ERROR_NO_PACKAGE for an unpackaged one ("Detect package identity and runtime context", Microsoft Learn, read
/// 16/10/2026); any other answer is logged and treated as unpackaged.
/// </summary>
public sealed class PackageInfo : IPackageInfo
{
    /// <summary>The Store build's package name (SPEC §18.1); the test build shares it with its own publisher hash.</summary>
    public const string PackageName = "Kynni.Dictum";

    private readonly string _localAppData;
    private readonly string _roamingAppData;

    public PackageInfo(bool isPackaged, string? fullName, string? familyName, string localAppData, string roamingAppData)
    {
        IsPackaged = isPackaged;
        FullName = fullName;
        FamilyName = familyName;
        _localAppData = localAppData;
        _roamingAppData = roamingAppData;
    }

    public bool IsPackaged { get; }

    public string? FullName { get; }

    public string? FamilyName { get; }

    public static PackageInfo Detect(ILog log, string localAppData, string roamingAppData)
    {
        uint length = 0;
        var result = NativeShell.GetCurrentPackageFullName(ref length, null);
        if (result == NativeShell.APPMODEL_ERROR_NO_PACKAGE) return new PackageInfo(false, null, null, localAppData, roamingAppData);
        if (result != NativeShell.ERROR_INSUFFICIENT_BUFFER)
        {
            log.Warn($"GetCurrentPackageFullName returned {result}; treating this copy as unpackaged.");
            return new PackageInfo(false, null, null, localAppData, roamingAppData);
        }
        var full = Read(NativeShell.GetCurrentPackageFullName);
        var family = Read(NativeShell.GetCurrentPackageFamilyName);
        return new PackageInfo(true, full, family, localAppData, roamingAppData);
    }

    private delegate int Getter(ref uint length, char[]? buffer);

    private static string? Read(Getter getter)
    {
        uint length = 0;
        if (getter(ref length, null) != NativeShell.ERROR_INSUFFICIENT_BUFFER || length == 0) return null;
        var buffer = new char[length];
        return getter(ref length, buffer) == NativeShell.ERROR_SUCCESS ? new string(buffer, 0, (int)length - 1) : null;
    }

    /// <summary>
    /// For a packaged build, the per-package folder Windows redirects new app-data files to, when it exists (Microsoft Learn
    /// says only "a private per-user, per-app location"; the LocalCache\Roaming and LocalCache\Local layout under
    /// %LOCALAPPDATA%\Packages\&lt;family&gt; is recalled, not verified). Files that already existed in the real folder are
    /// modified in place, so the real path is kept when no redirected copy exists.
    /// </summary>
    public string RealPath(string path)
    {
        if (!IsPackaged || FamilyName is null) return path;
        var cache = Path.Combine(_localAppData, "Packages", FamilyName, "LocalCache");
        string? redirected = Inside(path, _roamingAppData) is { } r ? Path.Combine(cache, "Roaming", r)
            : Inside(path, _localAppData) is { } l ? Path.Combine(cache, "Local", l) : null;
        return redirected is not null && (Directory.Exists(redirected) || File.Exists(redirected)) ? redirected : path;
    }

    private static string? Inside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) ? null : relative;
    }

    /// <summary>
    /// SPEC §18.2: both builds on one machine is unsupported. The unpackaged build looks for the package's data folder
    /// (%LOCALAPPDATA%\Packages\Kynni.Dictum_*, inferred: Windows creates it for an installed package); the packaged build
    /// looks for the direct-download install (install.ps1's %LOCALAPPDATA%\Programs\Dictum\Dictum.exe).
    /// </summary>
    public string? OtherInstall()
    {
        try
        {
            if (IsPackaged)
            {
                return File.Exists(Path.Combine(_localAppData, "Programs", "Dictum", "Dictum.exe"))
                    ? "The direct-download Dictum is also installed on this PC. Running both is not supported: uninstall one of them."
                    : null;
            }
            var packages = Path.Combine(_localAppData, "Packages");
            return Directory.Exists(packages) && Directory.EnumerateDirectories(packages, PackageName + "_*").Any()
                ? "The Microsoft Store Dictum is also installed on this PC. Running both is not supported: uninstall one of them."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
