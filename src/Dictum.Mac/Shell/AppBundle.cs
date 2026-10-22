namespace Dictum.Mac.Shell;

/// <summary>
/// Where this copy of Dictum runs from: inside Dictum.app (the shipped layout, Dictum.app/Contents/MacOS/Dictum) or a bare
/// folder (a build tree, <c>dotnet run</c>). What needs the bundle, and only works with one: the login item (SMAppService
/// registers the running app's bundle), notifications (UNUserNotificationCenter needs a bundle identifier) and "Remove
/// Dictum completely" (which moves the bundle to the Trash). Pure path arithmetic, so it is unit-tested on any OS.
/// </summary>
internal sealed record AppBundle(string Path)
{
    /// <summary>CFBundleIdentifier of Dictum.app (packaging/mac/Info.plist), also the login item's and the notifications' identity.</summary>
    public const string Identifier = "au.kynni.dictum";

    /// <summary>The bundle whose Contents/MacOS folder is <paramref name="exeDir"/>, or null when the executable is not inside one.</summary>
    public static AppBundle? Containing(string exeDir)
    {
        var macos = new DirectoryInfo(exeDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (!string.Equals(macos.Name, "MacOS", StringComparison.Ordinal)) return null;
        var contents = macos.Parent;
        if (contents is null || !string.Equals(contents.Name, "Contents", StringComparison.Ordinal)) return null;
        var bundle = contents.Parent;
        return bundle is not null && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? new AppBundle(bundle.FullName) : null;
    }

    public string Name => System.IO.Path.GetFileName(Path);
}
