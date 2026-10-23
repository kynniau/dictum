using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dictum.Core;
using Dictum.Platform;
using Dictum.Storage;

namespace Dictum.Windows.Shell;

/// <summary>
/// Windows' part of "Remove Dictum completely" (SPEC §17, amended 16/10/2026, and §18.2): the Start menu shortcut
/// install.ps1 made, the launch-at-login Run value, and the program's own files. A running exe cannot delete itself, so
/// those go last, in a detached step (Windows PowerShell, by full path) that waits for this process to exit. When the
/// Inno setup installed this copy (see UsesUninstaller), that step runs its uninstaller, so the Apps &amp; features entry
/// goes too; otherwise (the portable zip) it deletes only the files the folder's manifest.txt lists (same name and
/// size, never through a junction or symbolic link), the folders that leaves empty, and the program folder only if it
/// is then empty. No manifest, nothing is deleted there. The folder reaches the script through an environment
/// variable, never its text.
/// A packaged build cannot remove itself at all: Windows' Settings → Apps does it. Also the earlier build's (Dictate's)
/// leftovers after a migration (SPEC §21, amended).
/// </summary>
/// <param name="launch">Starts the detached step (tests capture it instead).</param>
/// <param name="waitForProcessId">The process the step waits for; this one by default, 0 for none (tests).</param>
/// <param name="setup">Dictum's Settings &gt; Apps entry; null means the setup did not install this copy.</param>
/// <param name="systemRoot">Where Windows' own programs are: %SystemRoot% by default (tests point it elsewhere).</param>
internal sealed class RemovalSteps(IRunKey runKey, string programFolder, string exePath, bool packaged, string roamingAppData, string localAppData, ILog log,
    Action<ProcessStartInfo>? launch = null, int? waitForProcessId = null, ISetupRecord? setup = null, string? systemRoot = null) : IRemovalSteps
{
    private const string EarlierRunValue = "Dictate";
    internal const string Uninstaller = "unins000.exe";
    internal const string UninstallerRecord = "unins000.dat";
    private readonly string? _systemRoot = systemRoot ?? SystemPrograms.Root;
    internal const string FolderVariable = "DICTUM_REMOVE_FOLDER";
    internal const string WaitVariable = "DICTUM_REMOVE_AFTER_PID";
    internal const string ModeVariable = "DICTUM_REMOVE_MODE";

    internal const string StartupItems = "the Start menu shortcut and the launch-at-login entry";

    public string StartupItemsPhrase => StartupItems;

    private string ShortcutPath => Path.Combine(roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Dictum.lnk");

    private string EarlierShortcut => Path.Combine(roamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Dictate.lnk");

    /// <summary>Where install.ps1 put the earlier build: the only folder of it Dictum will ever delete.</summary>
    private string EarlierProgramFolder => Path.Combine(localAppData, "Programs", "Dictate");

    public IReadOnlyList<OwnedLocation> PlatformLocations()
    {
        var list = new List<OwnedLocation>();
        if (File.Exists(ShortcutPath)) list.Add(new(ShortcutPath, DataCategory.Shortcut, "Start menu shortcut"));
        if (runKey.Get(RunKeyStartup.ValueName) is not null)
            list.Add(new($@"HKCU\{RegistryRunKey.RunPath}\{RunKeyStartup.ValueName}", DataCategory.StartupEntry, "launch at login"));
        if (ProgramFolderProblem() is null && ProgramManifest.Read(programFolder) is { } manifest)
        {
            foreach (var (path, _) in ProgramManifest.ShippedFiles(programFolder, manifest)) list.Add(new(path, DataCategory.ProgramFile, "the program"));
            list.Add(new(Path.Combine(programFolder, ProgramManifest.FileName), DataCategory.ProgramFile, "the program's file list"));
            if (UsesUninstaller(programFolder))
            {
                list.Add(new(Path.Combine(programFolder, Uninstaller), DataCategory.ProgramFile, "the uninstaller, which also removes Dictum from Settings > Apps"));
                list.Add(new(Path.Combine(programFolder, UninstallerRecord), DataCategory.ProgramFile, "the uninstaller's record of the install"));
            }
            list.Add(new(programFolder, DataCategory.ProgramFolder, "the program folder, if nothing else is left in it"));
        }
        return list;
    }

    public string? ProgramFolderProblem() => Refusal(programFolder, packaged) ?? PowerShellProblem();

    /// <summary>Why the detached step cannot start, or null: it needs Windows PowerShell at its full path.</summary>
    private string? PowerShellProblem() => SystemPrograms.WindowsPowerShell(_systemRoot) is null
        ? $@"Windows PowerShell is not at {_systemRoot ?? "%SystemRoot%"}\System32\WindowsPowerShell\v1.0\powershell.exe, so nothing can remove the program's files after Dictum closes. Delete them yourself."
        : null;

    public IReadOnlyList<string> RemoveShortcutAndStartup()
    {
        var problems = new List<string>();
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{ShortcutPath}: {ex.Message}");
        }
        try
        {
            // Only the value that starts this copy (as uninstall.ps1 does): another copy's entry is not ours to remove.
            if (new RunKeyStartup(runKey, exePath).IsEnabled()) runKey.Delete(RunKeyStartup.ValueName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problems.Add($"launch at login: {ex.Message}");
        }
        return problems;
    }

    public bool ScheduleProgramFolderRemoval(string folder, out string? reason)
    {
        reason = Refusal(folder, packaged) ?? PowerShellProblem();
        if (reason is not null) return false;
        var start = RemovalStart(folder);
        if (launch is not null) launch(start);
        else Process.Start(start)?.Dispose();
        log.Info($"Scheduled the removal of {folder} after Dictum exits ({start.Environment[ModeVariable]}).");
        return true;
    }

    /// <summary>
    /// Run the Inno setup's uninstaller, not the file-by-file step, only for the copy that setup installed: the running
    /// exe's folder, holding unins000.exe, unins000.dat and manifest.txt, with Dictum's own Settings &gt; Apps entry
    /// naming that folder as InstallLocation and its unins000.exe as UninstallString. A unins000.exe beside a portable
    /// copy, or a copy unzipped into another program's folder, gets the manifest step (lens probe, 16/10/2026: a
    /// planted unins000.exe ran silently).
    /// </summary>
    private bool UsesUninstaller(string folder)
    {
        if (!PathRegistry.SamePath(folder, Path.GetDirectoryName(Path.GetFullPath(exePath))!)) return false;
        if (!new[] { Uninstaller, UninstallerRecord, ProgramManifest.FileName }.All(name => File.Exists(Path.Combine(folder, name)))) return false;
        if (setup?.Get("InstallLocation") is not { } location || !Path.IsPathFullyQualified(location) || !PathRegistry.SamePath(location, folder)) return false;
        return RegistrySetupRecord.UninstallerPath(setup.Get("UninstallString")) is { } uninstaller && PathRegistry.SamePath(uninstaller, Path.Combine(folder, Uninstaller));
    }

    /// <summary>The detached step: the fixed script, and the folder, the process to wait for and the mode as environment variables.</summary>
    internal ProcessStartInfo RemovalStart(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)); // -EncodedCommand takes UTF-16LE Base64: no script file is left behind
        var powershell = SystemPrograms.WindowsPowerShell(_systemRoot) ?? throw new InvalidOperationException(PowerShellProblem());
        var start = new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(), // never the folder being emptied
        };
        start.Environment[FolderVariable] = full;
        start.Environment[WaitVariable] = (waitForProcessId ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture);
        start.Environment[ModeVariable] = UsesUninstaller(full) ? "uninstaller" : "manifest";
        return start;
    }

    /// <summary>Why the program's files are not removed, or null. Never packaged, a drive root, a folder without Dictum.exe or manifest.txt, or a source tree's build output.</summary>
    internal static string? Refusal(string folder, bool packaged)
    {
        if (packaged) return "A Microsoft Store install is removed from Windows Settings > Apps.";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "The program folder is a drive root; it is not touched.";
        if (!File.Exists(Path.Combine(full, "Dictum.exe"))) return "The program folder does not hold Dictum.exe; it is not touched.";
        for (var dir = Directory.GetParent(full); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dictum.sln")))
                return "This copy runs from a source tree's build folder; delete that yourself.";
        }
        if (ProgramManifest.Read(full) is null)
            return $"{full} has no {ProgramManifest.FileName}, so Dictum cannot tell its own files from anything else there: the program is left in place. Delete Dictum's files yourself.";
        return null;
    }

    /// <summary>
    /// The detached step (PowerShell 5.1). It takes nothing from its own text but rules: the folder, the process to wait
    /// for and the mode come from environment variables and are checked again here (an absolute path, not a drive root,
    /// holding Dictum.exe and manifest.txt). Uninstaller mode starts unins000.exe silently. Manifest mode deletes each
    /// listed file whose path crosses no junction or symbolic link and whose size matches, then manifest.txt, then the
    /// listed folders once empty (never a link), then the program folder if it is empty.
    /// </summary>
    internal const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $wait = 0
        [void][int]::TryParse($env:DICTUM_REMOVE_AFTER_PID, [ref]$wait)
        if ($wait -gt 0) { Wait-Process -Id $wait -Timeout 60; Start-Sleep -Milliseconds 500 }
        $f = $env:DICTUM_REMOVE_FOLDER
        if (-not $f -or -not [IO.Path]::IsPathRooted($f)) { exit 2 }
        $f = [IO.Path]::GetFullPath($f).TrimEnd('\')
        if ([IO.Path]::GetPathRoot($f).TrimEnd('\') -eq $f) { exit 2 }
        if (-not (Test-Path -LiteralPath (Join-Path $f 'Dictum.exe') -PathType Leaf)) { exit 2 }
        $m = Join-Path $f 'manifest.txt'
        if (-not (Test-Path -LiteralPath $m -PathType Leaf)) { exit 3 }
        if ($env:DICTUM_REMOVE_MODE -eq 'uninstaller') {
          $u = Join-Path $f 'unins000.exe'
          if (Test-Path -LiteralPath $u -PathType Leaf) { Start-Process -FilePath $u -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES' }
          exit 0
        }
        function Test-Link($path) {
          $i = Get-Item -LiteralPath $path -Force
          return (-not $i) -or [bool]($i.Attributes -band [IO.FileAttributes]::ReparsePoint)
        }
        $dirs = @{}
        foreach ($line in [IO.File]::ReadAllLines($m)) {
          if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
          $parts = $line.Split("`t")
          if ($parts.Count -lt 3) { continue }
          $rel = $parts[0]
          if ($rel.Contains('..') -or $rel.Contains(':') -or $rel.StartsWith('\') -or $rel.StartsWith('/')) { continue }
          $p = $f
          $crosses = $false
          foreach ($s in $rel.Split([char[]]@('\', '/'))) { $p = Join-Path $p $s; if (Test-Link $p) { $crosses = $true; break } }
          if (-not $crosses) {
            $i = Get-Item -LiteralPath $p -Force
            if (-not $i.PSIsContainer -and $i.Length -eq [long]$parts[1]) { Remove-Item -LiteralPath $p -Force }
          }
          $d = Split-Path -Parent $rel
          while ($d) { $dirs[$d] = $true; $d = Split-Path -Parent $d }
        }
        Remove-Item -LiteralPath $m -Force
        foreach ($d in ($dirs.Keys | Sort-Object { $_.Length } -Descending)) {
          $p = Join-Path $f $d
          if ((Test-Path -LiteralPath $p -PathType Container) -and -not (Test-Link $p) -and -not (Get-ChildItem -LiteralPath $p -Force)) { Remove-Item -LiteralPath $p -Force }
        }
        if (-not (Get-ChildItem -LiteralPath $f -Force)) { Remove-Item -LiteralPath $f -Force }
        """;

    public void OpenUninstallSettings() => Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true })?.Dispose();

    public IReadOnlyList<string> EarlierBuildLeftovers()
    {
        var items = new List<string>();
        if (EarlierRunCommand() is { } command) items.Add($"its launch-at-login entry (HKCU\\{RegistryRunKey.RunPath}\\{EarlierRunValue}: {command})");
        if (File.Exists(EarlierShortcut)) items.Add($"its Start menu shortcut ({EarlierShortcut})");
        if (EarlierProgramFolderIsOurs())
        {
            var files = new DirectoryInfo(EarlierProgramFolder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
            items.Add($"its program folder {EarlierProgramFolder} ({files.Count} files, {Settings.ModelCatalog.FormatSize(files.Sum(f => f.Length))})");
        }
        return items;
    }

    public IReadOnlyList<string> RemoveEarlierBuild()
    {
        var problems = new List<string>();
        void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                problems.Add($"{what}: {ex.Message}");
            }
        }
        if (EarlierRunCommand() is not null) Try("Dictate's launch at login", () => runKey.Delete(EarlierRunValue));
        if (File.Exists(EarlierShortcut)) Try(EarlierShortcut, () => File.Delete(EarlierShortcut));
        if (EarlierProgramFolderIsOurs()) Try(EarlierProgramFolder, () => DeleteTree(new DirectoryInfo(EarlierProgramFolder)));
        log.Info(problems.Count == 0 ? "Removed the earlier build (Dictate) as offered after the migration." : $"Removing the earlier build left {problems.Count} item(s).");
        return problems;
    }

    /// <summary>Deletes a folder's contents and the folder, removing junctions and symbolic links as links (never what they point to).</summary>
    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (entry is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteTree(sub);
            else if (entry is DirectoryInfo link) link.Delete();
            else entry.Delete();
        }
        dir.Delete();
    }

    /// <summary>The Run value named Dictate, when it starts a Dictate.exe; another program's value of that name is left alone.</summary>
    private string? EarlierRunCommand() =>
        runKey.Get(EarlierRunValue) is { } value && value.Trim().Trim('"').EndsWith(@"\Dictate.exe", StringComparison.OrdinalIgnoreCase) ? value : null;

    /// <summary>Exactly %LOCALAPPDATA%\Programs\Dictate, holding Dictate.exe and not itself a link: install.ps1's folder, so all of it is the earlier build.</summary>
    private bool EarlierProgramFolderIsOurs() =>
        File.Exists(Path.Combine(EarlierProgramFolder, "Dictate.exe")) && !new DirectoryInfo(EarlierProgramFolder).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
