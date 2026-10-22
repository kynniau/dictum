using System.Diagnostics;
using System.Globalization;
using System.Xml;
using Dictum.Platform;
using Dictum.Storage;

namespace Dictum.Mac.Shell;

/// <summary>
/// macOS's part of "Remove Dictum completely" (SPEC §17): the login item, then Dictum.app itself, moved to the Trash (not
/// deleted: the user can still put it back) by a detached <c>/bin/sh</c> step that waits for this process to exit, as the
/// Windows build's detached PowerShell step does (src/Dictum.Windows/Shell/RemovalSteps.cs). Only the verified bundle
/// moves: the Dictum.app this executable runs inside, whose Info.plist names Dictum's bundle identifier, and not while
/// macOS runs it from App Translocation's read-only copy. The bundle path and the process id reach the script only
/// through environment variables, never its text. There is no earlier build on a Mac and no system uninstall page.
/// </summary>
/// <param name="launch">Starts the detached step (tests capture it instead).</param>
/// <param name="waitForProcessId">The process the step waits for; this one by default, 0 for none (tests).</param>
/// <param name="trashFolder">Where the bundle goes: ~/.Trash by default (tests point it elsewhere).</param>
internal sealed class RemovalSteps(ILog log, AppBundle? bundle, string exeDir, Func<IStartupRegistration> startup,
    Action<ProcessStartInfo>? launch = null, int? waitForProcessId = null, string? trashFolder = null) : IRemovalSteps
{
    internal const string BundleVariable = "DICTUM_REMOVE_BUNDLE";
    internal const string WaitVariable = "DICTUM_REMOVE_AFTER_PID";
    internal const string TrashVariable = "DICTUM_REMOVE_TRASH";

    // PROPOSED 23/10/2026: the removal list's label for the login item, and for the bundle.
    internal const string LoginItemPath = "System Settings > General > Login Items > Dictum";
    internal const string BundleDescription = "Dictum.app, moved to the Trash after Dictum closes";

    // PROPOSED 23/10/2026: why the program is left in place.
    internal const string NotInstalledCopy = "This copy is not the installed Dictum.app, so it stays where it is. Move it to the Trash yourself.";
    internal const string Translocated = "macOS is running this copy from a temporary read-only location, so it cannot be moved. Drag Dictum.app to the Trash yourself.";

    private string TrashFolder => trashFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

    public IReadOnlyList<OwnedLocation> PlatformLocations()
    {
        var list = new List<OwnedLocation>();
        if (startup().IsEnabled()) list.Add(new(LoginItemPath, DataCategory.StartupEntry, "launch at login"));
        if (bundle is not null && ProgramFolderProblem() is null) list.Add(new(bundle.Path, DataCategory.ProgramFolder, BundleDescription));
        return list;
    }

    public string? ProgramFolderProblem()
    {
        if (bundle is null || BundleIdentifier(Path.Combine(bundle.Path, "Contents", "Info.plist")) != AppBundle.Identifier) return NotInstalledCopy;
        return bundle.Path.Contains("/AppTranslocation/", StringComparison.Ordinal) ? Translocated : null;
    }

    /// <summary>Unregisters the login item; always asked of SMAppService, since one waiting for approval is registered but not enabled.</summary>
    public IReadOnlyList<string> RemoveShortcutAndStartup()
    {
        if (bundle is null) return [];
        var problem = startup().SetEnabledAsync(false).GetAwaiter().GetResult(); // the unregister runs on a worker thread; nothing here waits on the UI thread
        return problem is null ? [] : [$"launch at login: {problem}"];
    }

    /// <summary>
    /// <paramref name="folder"/> is the program folder the UI knows (AppPaths.ExeDir, Dictum.app/Contents/MacOS) or the
    /// bundle itself; either way the whole verified bundle moves, and anything else is refused.
    /// </summary>
    public bool ScheduleProgramFolderRemoval(string folder, out string? reason)
    {
        reason = ProgramFolderProblem();
        if (reason is null && !IsThisBundle(folder)) reason = NotInstalledCopy;
        if (reason is not null) return false;
        var start = RemovalStart();
        if (launch is not null) launch(start);
        else Process.Start(start)?.Dispose();
        log.Info($"Scheduled moving {bundle!.Path} to the Trash after Dictum exits.");
        return true;
    }

    /// <summary>The bundle or its Contents/MacOS, and the bundle this executable runs from.</summary>
    private bool IsThisBundle(string folder)
    {
        if (bundle is null) return false;
        var macos = Path.Combine(bundle.Path, "Contents", "MacOS");
        return (PathRegistry.SamePath(folder, bundle.Path) || PathRegistry.SamePath(folder, macos)) && PathRegistry.SamePath(exeDir, macos);
    }

    /// <summary>The detached step: the fixed script, with the bundle, the process to wait for and the Trash as environment variables.</summary>
    internal ProcessStartInfo RemovalStart()
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = "/" }; // never inside the bundle being moved
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Script);
        start.Environment[BundleVariable] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle!.Path));
        start.Environment[WaitVariable] = (waitForProcessId ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture);
        start.Environment[TrashVariable] = TrashFolder;
        return start;
    }

    /// <summary>
    /// The detached step (POSIX sh). It takes nothing from its own text but rules: waits up to 60 s for the process to end
    /// (as the Windows step does), then checks again that the bundle is an absolute path ending in .app, a real folder (not
    /// a link) holding Contents/Info.plist, and moves it into the Trash folder, adding the date and time to its name when
    /// the Trash already holds one of that name.
    /// </summary>
    internal const string Script = """
        pid="$DICTUM_REMOVE_AFTER_PID"
        case "$pid" in ''|*[!0-9]*) pid=0 ;; esac
        n=0
        while [ "$pid" -gt 0 ] && kill -0 "$pid" 2>/dev/null && [ "$n" -lt 300 ]; do sleep 0.2; n=$((n + 1)); done
        b="$DICTUM_REMOVE_BUNDLE"
        t="$DICTUM_REMOVE_TRASH"
        case "$b" in /*.app) ;; *) exit 2 ;; esac
        case "$t" in /*) ;; *) exit 2 ;; esac
        if [ -L "$b" ] || [ ! -d "$b" ] || [ ! -f "$b/Contents/Info.plist" ]; then exit 2; fi
        mkdir -p "$t" || exit 3
        name=$(basename "$b" .app)
        dest="$t/$name.app"
        if [ -e "$dest" ] || [ -L "$dest" ]; then dest="$t/$name $(date +%Y-%m-%d-%H%M%S).app"; fi
        mv "$b" "$dest"
        """;

    /// <summary>
    /// CFBundleIdentifier from an XML Info.plist (the format packaging/mac writes), or null when the file is missing,
    /// binary or has no such key: an unverified bundle, which is never moved.
    /// </summary>
    internal static string? BundleIdentifier(string infoPlist)
    {
        try
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(infoPlist, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null })) document.Load(reader);
            if (document.SelectSingleNode("/plist/dict") is not XmlElement dict) return null;
            for (var node = dict.FirstChild; node is not null; node = node.NextSibling)
            {
                if (node is XmlElement { Name: "key" } key && key.InnerText == "CFBundleIdentifier")
                    return key.NextSibling is XmlElement { Name: "string" } value ? value.InnerText.Trim() : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    public void OpenUninstallSettings()
    {
    }

    public IReadOnlyList<string> EarlierBuildLeftovers() => [];

    public IReadOnlyList<string> RemoveEarlierBuild() => [];
}
