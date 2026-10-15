using System.Diagnostics;
using Dictum.Core;
using Dictum.Storage;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// Which copy the removal step hands to the Inno uninstaller (only the one Dictum's own Settings &gt; Apps entry names),
/// and Windows PowerShell started by full path (16/10/2026, after the lens's probes on the removal step).
/// </summary>
public class SetupUninstallerTests
{
    /// <summary>
    /// The portable copy plus what the setup leaves behind: unins000.exe, unins000.dat and Dictum's entry naming the
    /// folder. InstallLocation is upper-cased with a trailing backslash (Inno writes the backslash), so only a
    /// normalised, case-insensitive comparison accepts it.
    /// </summary>
    private static (string Folder, FakeSetupRecord Record) InstalledCopy(Scratch scratch)
    {
        var (folder, _, _) = RestructureFixTests.PortableCopy(scratch, name: "Dictum");
        File.WriteAllText(Path.Combine(folder, RemovalSteps.Uninstaller), "");
        File.WriteAllText(Path.Combine(folder, RemovalSteps.UninstallerRecord), "");
        var record = new FakeSetupRecord();
        record.Values["InstallLocation"] = folder.ToUpperInvariant() + @"\";
        record.Values["UninstallString"] = $"\"{Path.Combine(folder, RemovalSteps.Uninstaller)}\"";
        return (folder, record);
    }

    private static RemovalSteps Steps(string folder, string exeFolder, List<ProcessStartInfo> launched, ISetupRecord? setup, string? systemRoot = null) =>
        new(new FakeRunKey(), folder, Path.Combine(exeFolder, "Dictum.exe"), false, folder, folder, NullLog.Instance, launched.Add, waitForProcessId: 0, setup, systemRoot);

    [Fact]
    public void The_copy_its_own_setup_installed_runs_that_uninstaller_after_Dictum_exits()
    {
        using var scratch = new Scratch();
        var (folder, record) = InstalledCopy(scratch);
        var launched = new List<ProcessStartInfo>();
        var steps = Steps(folder, folder, launched, record);
        var listed = steps.PlatformLocations().Select(l => l.Path).ToList();
        Assert.Contains(Path.Combine(folder, RemovalSteps.Uninstaller), listed); // the first warning says so
        Assert.Contains(Path.Combine(folder, RemovalSteps.UninstallerRecord), listed);
        Assert.True(steps.ScheduleProgramFolderRemoval(folder, out _));
        var start = Assert.Single(launched);
        Assert.Equal("uninstaller", start.Environment[RemovalSteps.ModeVariable]);
        Assert.Equal(folder, start.Environment[RemovalSteps.FolderVariable]);
        Assert.Contains("'/VERYSILENT', '/SUPPRESSMSGBOXES'", RemovalSteps.Script);
    }

    [Theory]
    [InlineData("no setup entry")]
    [InlineData("a unins000.exe planted in a portable copy")]
    [InlineData("the entry names another folder")]
    [InlineData("no unins000.dat")]
    [InlineData("the uninstall string names another folder")]
    [InlineData("Dictum runs from another folder")]
    public void Any_other_copy_gets_the_manifest_step_never_the_uninstaller(string change)
    {
        using var scratch = new Scratch();
        var (folder, record) = InstalledCopy(scratch);
        var exeFolder = folder;
        var elsewhere = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "Programs", "Dictum")).FullName;
        switch (change)
        {
            case "no setup entry":
                record.Values.Clear();
                break;
            case "a unins000.exe planted in a portable copy":
                record.Values.Clear();
                File.Delete(Path.Combine(folder, RemovalSteps.UninstallerRecord));
                break;
            case "the entry names another folder":
                record.Values["InstallLocation"] = elsewhere + @"\";
                break;
            case "no unins000.dat":
                File.Delete(Path.Combine(folder, RemovalSteps.UninstallerRecord));
                break;
            case "the uninstall string names another folder":
                record.Values["UninstallString"] = $"\"{Path.Combine(elsewhere, RemovalSteps.Uninstaller)}\"";
                break;
            case "Dictum runs from another folder":
                exeFolder = elsewhere;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
        var launched = new List<ProcessStartInfo>();
        var steps = Steps(folder, exeFolder, launched, record);
        Assert.DoesNotContain(steps.PlatformLocations(), l => l.Path.EndsWith(RemovalSteps.Uninstaller, StringComparison.OrdinalIgnoreCase));
        Assert.True(steps.ScheduleProgramFolderRemoval(folder, out _));
        Assert.Equal("manifest", Assert.Single(launched).Environment[RemovalSteps.ModeVariable]);
    }

    [Fact]
    public void Without_a_manifest_nothing_is_started_even_for_the_setups_copy()
    {
        using var scratch = new Scratch();
        var (folder, record) = InstalledCopy(scratch);
        File.Delete(Path.Combine(folder, ProgramManifest.FileName));
        var launched = new List<ProcessStartInfo>();
        Assert.False(Steps(folder, folder, launched, record).ScheduleProgramFolderRemoval(folder, out var reason));
        Assert.Contains("no manifest.txt", reason);
        Assert.Empty(launched);
    }

    [Fact]
    public void In_uninstaller_mode_the_detached_step_leaves_every_file_to_the_uninstaller()
    {
        using var scratch = new Scratch();
        var (folder, shipped, _) = RestructureFixTests.PortableCopy(scratch);
        var start = Steps(folder, folder, [], setup: null).RemovalStart(folder);
        start.Environment[RemovalSteps.ModeVariable] = "uninstaller"; // with no unins000.exe present, so nothing is started
        using (var process = Process.Start(start)!) Assert.True(process.WaitForExit(60_000), "the removal script did not finish");
        Assert.All(shipped, f => Assert.True(File.Exists(f), $"{f} was deleted before the uninstaller ran"));
        Assert.True(File.Exists(Path.Combine(folder, ProgramManifest.FileName)));
    }

    [Fact]
    public void The_setup_entry_is_looked_up_under_the_AppId_in_Dictum_iss()
    {
        var iss = File.ReadAllLines(Path.Combine(FixBatchDownloadTests.RepoRoot(), "packaging", "Dictum.iss"));
        var line = Assert.Single(iss, l => l.StartsWith("AppId=", StringComparison.Ordinal));
        var appId = line["AppId=".Length..].Trim().Replace("{{", "{", StringComparison.Ordinal); // Inno's doubled brace stands for one
        Assert.Equal(RegistrySetupRecord.AppId, appId);
        Assert.Equal(RegistrySetupRecord.KeyPath, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + appId + "_is1");
    }

    [Theory]
    [InlineData(@"""C:\Programs\Dictum\unins000.exe""", @"C:\Programs\Dictum\unins000.exe")]
    [InlineData(@"C:\Programs\Dictum\unins000.exe", @"C:\Programs\Dictum\unins000.exe")]
    [InlineData(@"""C:\Programs\Dictum\unins000.exe"" /SILENT", null)]
    [InlineData(@"unins000.exe", null)]
    [InlineData(@"""C:\Programs\Dictum\unins000.exe", null)]
    [InlineData(null, null)]
    public void An_uninstall_string_is_one_absolute_path_or_nothing(string? command, string? expected) =>
        Assert.Equal(expected, RegistrySetupRecord.UninstallerPath(command));

    [Fact]
    public void The_detached_step_starts_Windows_PowerShell_by_full_path_never_one_beside_Dictum()
    {
        using var scratch = new Scratch();
        var (folder, _, _) = RestructureFixTests.PortableCopy(scratch);
        File.WriteAllText(Path.Combine(folder, "powershell.exe"), ""); // planted beside Dictum.exe
        var start = Steps(folder, folder, [], setup: null).RemovalStart(folder);
        var expected = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.Equal(expected, start.FileName, ignoreCase: true);
    }

    [Fact]
    public void Without_Windows_PowerShell_the_program_stays_and_the_first_warning_says_why()
    {
        using var scratch = new Scratch();
        var (folder, _, _) = RestructureFixTests.PortableCopy(scratch);
        var noWindows = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "no-windows")).FullName;
        var launched = new List<ProcessStartInfo>();
        var steps = Steps(folder, folder, launched, setup: null, systemRoot: noWindows);
        Assert.Contains("Windows PowerShell", steps.ProgramFolderProblem());
        Assert.DoesNotContain(steps.PlatformLocations(), l => l.Category is DataCategory.ProgramFile or DataCategory.ProgramFolder);
        Assert.False(steps.ScheduleProgramFolderRemoval(folder, out var reason));
        Assert.Contains("Windows PowerShell", reason);
        Assert.Empty(launched);
    }

    /// <summary>
    /// Gated on DICTUM_SETUP_INSTALL_DIR: a copy installed by Dictum-Setup-&lt;version&gt;.exe (for example with
    /// /VERYSILENT /DIR="&lt;scratch folder&gt;"), which this test UNINSTALLS: the real Settings &gt; Apps entry must select
    /// the uninstaller, and the detached step must then remove the folder and the entry.
    /// </summary>
    [Fact]
    public void A_real_setup_install_is_recognised_and_its_uninstaller_removes_it()
    {
        var folder = TestEnv.Get("SETUP_INSTALL_DIR");
        Assert.SkipWhen(folder is null, "DICTUM_SETUP_INSTALL_DIR is not set (a copy installed by the setup; the test uninstalls it).");
        var record = new RegistrySetupRecord();
        Assert.True(PathRegistry.SamePath(record.Get("InstallLocation")!, folder), "the setup's entry does not name this folder");
        var start = Steps(folder, folder, [], record).RemovalStart(folder);
        Assert.Equal("uninstaller", start.Environment[RemovalSteps.ModeVariable]);
        using (var process = Process.Start(start)!) Assert.True(process.WaitForExit(60_000), "the removal script did not finish");
        var deadline = DateTime.UtcNow.AddSeconds(90); // the uninstaller runs on its own after the step starts it
        while ((File.Exists(Path.Combine(folder, "Dictum.exe")) || record.Get("InstallLocation") is not null) && DateTime.UtcNow < deadline) Thread.Sleep(500);
        Assert.False(File.Exists(Path.Combine(folder, "Dictum.exe")), "the uninstaller did not remove the program");
        Assert.Null(record.Get("InstallLocation")); // the Settings > Apps entry is gone
    }
}
