using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The beta is a Developer ID build downloaded from GitHub, never a sandboxed package: nothing is redirected and there is
/// no other build to clash with (docs/DECISIONS.md, Dictum for macOS, 22/10/2026). The Mac App Store train, when it
/// comes, detects its sandbox container here.
/// </summary>
internal sealed class MacPackageInfo : IPackageInfo
{
    public bool IsPackaged => false;

    public string? FamilyName => null;

    public string RealPath(string path) => path;

    public string? OtherInstall() => null;
}
