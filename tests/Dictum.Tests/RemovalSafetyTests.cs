using System.Diagnostics;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Shell;
using Dictum.Windows.Interop;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// The final removal fixes (16/10/2026): the folder passed outside the script text, links never followed, the packaged
/// migration copying, and the tray's idle icon. The uninstaller's tests are in <see cref="SetupUninstallerTests"/>.
/// </summary>
public class RemovalSafetyTests
{
    /// <summary>A directory junction (mklink /J needs no elevation, unlike a symbolic link); cmd.exe by full path.</summary>
    private static void Junction(string link, string target)
    {
        var cmd = SystemPrograms.Find(SystemPrograms.Root, @"System32\cmd.exe")!;
        using var process = Process.Start(new ProcessStartInfo(cmd, $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit(10_000);
        Assert.True(Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), $"junction {link} was not created");
    }

    [Theory]
    [InlineData("O’Brien Dictum")]
    [InlineData("a;b ‘c’ ‚d‛ Dictum")]
    [InlineData("Dictum’; New-Item -ItemType File -Path {0}; ‘x")]
    public void A_folder_name_with_typographic_quotes_or_semicolons_is_only_ever_data(string name)
    {
        using var scratch = new Scratch();
        var marker = $"dictum-injected-{Guid.NewGuid():N}.txt";
        var (folder, shipped, foreign) = RestructureFixTests.PortableCopy(scratch, name: string.Format(name, marker));
        RestructureFixTests.RunRemovalScript(folder);
        Assert.All(shipped, f => Assert.False(File.Exists(f), $"{f} was not removed"));
        Assert.All(foreign, f => Assert.True(File.Exists(f), $"{f} was deleted"));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), marker)), "a command in the folder name ran");
    }

    [Fact]
    public void A_junction_in_Dictates_leftovers_is_removed_as_a_link_and_its_target_kept()
    {
        using var scratch = new Scratch();
        var elsewhere = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "other-drive")).FullName;
        var precious = Path.Combine(elsewhere, "precious.bin");
        File.WriteAllBytes(precious, new byte[64]);
        var legacy = new LegacyFolders(Path.Combine(scratch.Root.FullName, "roaming", "Dictate"), Path.Combine(scratch.Root.FullName, "local", "Dictate"));
        Directory.CreateDirectory(legacy.LocalDir);
        File.WriteAllText(Path.Combine(legacy.LocalDir, "leftover.log"), "x");
        Junction(Path.Combine(legacy.LocalDir, "models"), elsewhere);

        var plan = RemovalPlan.Build(scratch.Paths, new AppSettings(), RemovalScope.Everything, legacy: legacy);
        Assert.DoesNotContain(plan.Items, i => i.Path.EndsWith("precious.bin", StringComparison.OrdinalIgnoreCase)); // not listed through the link
        Assert.Contains(plan.Items, i => i.IsLink && i.Path.EndsWith("models", StringComparison.Ordinal));
        Assert.Empty(plan.Execute().Failures);
        Assert.True(File.Exists(precious)); // the target is untouched
        Assert.False(Directory.Exists(legacy.LocalDir)); // the link and the folder are gone
    }

    [Fact]
    public void A_Dictate_folder_that_is_itself_a_junction_is_unlinked_not_emptied()
    {
        using var scratch = new Scratch();
        var elsewhere = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "other-drive")).FullName;
        var precious = Path.Combine(elsewhere, "precious.bin");
        File.WriteAllBytes(precious, new byte[64]);
        var legacy = new LegacyFolders(Path.Combine(scratch.Root.FullName, "roaming", "Dictate"), Path.Combine(scratch.Root.FullName, "local", "Dictate"));
        Directory.CreateDirectory(Path.GetDirectoryName(legacy.LocalDir)!);
        Junction(legacy.LocalDir, elsewhere);

        var plan = RemovalPlan.Build(scratch.Paths, new AppSettings(), RemovalScope.Everything, legacy: legacy);
        var item = Assert.Single(plan.Items, i => i.Category == DataCategory.EarlierBuild);
        Assert.True(item.IsLink);
        Assert.Empty(plan.Execute().Failures);
        Assert.True(File.Exists(precious));
        Assert.False(Directory.Exists(legacy.LocalDir));
    }

    [Fact]
    public void The_detached_step_never_deletes_through_a_junction()
    {
        using var scratch = new Scratch();
        var (folder, _, _) = RestructureFixTests.PortableCopy(scratch);
        var elsewhere = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "other-drive")).FullName;
        var precious = Path.Combine(elsewhere, "x.dll");
        File.WriteAllBytes(precious, new byte[7]);
        Junction(Path.Combine(folder, "runtimes", "link"), elsewhere);
        File.AppendAllLines(Path.Combine(folder, ProgramManifest.FileName), ["runtimes\\link\\x.dll\t7\tabc"]); // a crafted entry
        Assert.DoesNotContain(ProgramManifest.ShippedFiles(folder, ProgramManifest.Read(folder)!), f => f.Path.EndsWith("x.dll", StringComparison.Ordinal));
        RestructureFixTests.RunRemovalScript(folder);
        Assert.True(File.Exists(precious), "the step deleted a file through a junction");
    }

    [Fact]
    public void Removing_the_earlier_build_unlinks_junctions_and_never_empties_their_targets()
    {
        using var scratch = new Scratch();
        var (roaming, local) = (Path.Combine(scratch.Root.FullName, "roaming"), Path.Combine(scratch.Root.FullName, "local"));
        var elsewhere = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "other-drive")).FullName;
        var precious = Path.Combine(elsewhere, "precious.bin");
        File.WriteAllBytes(precious, new byte[64]);
        var install = Directory.CreateDirectory(Path.Combine(local, "Programs", "Dictate")).FullName;
        File.WriteAllText(Path.Combine(install, "Dictate.exe"), "");
        Junction(Path.Combine(install, "linked"), elsewhere);
        var steps = new RemovalSteps(new FakeRunKey(), scratch.Paths.ExeDir, Path.Combine(scratch.Paths.ExeDir, "Dictum.exe"), false, roaming, local, NullLog.Instance);
        Assert.Contains("(1 files,", Assert.Single(steps.EarlierBuildLeftovers())); // the junction's target is not counted
        Assert.Empty(steps.RemoveEarlierBuild());
        Assert.False(Directory.Exists(install));
        Assert.True(File.Exists(precious));

        File.WriteAllText(Path.Combine(elsewhere, "Dictate.exe"), "");
        Junction(install, elsewhere); // the program folder itself a link: not install.ps1's folder, so not touched
        Assert.Empty(steps.EarlierBuildLeftovers());
        Assert.Empty(steps.RemoveEarlierBuild());
        Assert.True(File.Exists(Path.Combine(elsewhere, "Dictate.exe")));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void A_packaged_first_start_copies_and_leaves_Dictates_folders_whole()
    {
        using var scratch = new Scratch();
        var paths = AppPaths.ForRoots(Path.Combine(scratch.Root.FullName, "roaming"), Path.Combine(scratch.Root.FullName, "local"), scratch.Paths.ExeDir, null);
        var legacy = new LegacyFolders(Path.Combine(scratch.Root.FullName, "roaming", "Dictate"), Path.Combine(scratch.Root.FullName, "local", "Dictate"));
        Directory.CreateDirectory(legacy.ConfigDir);
        Directory.CreateDirectory(Path.Combine(legacy.LocalDir, "models"));
        File.WriteAllText(Path.Combine(legacy.ConfigDir, "settings.json"), """{ "hotkey": "RightAlt" }""");
        File.WriteAllBytes(Path.Combine(legacy.LocalDir, "models", "ggml-tiny.en.bin"), new byte[10]);

        var report = LegacyMigration.Run(paths, legacy, copy: true);
        Assert.True(report.Copied);
        Assert.All(report.Moved, m => Assert.EndsWith("(copied)", m));
        Assert.True(File.Exists(Path.Combine(legacy.ConfigDir, "settings.json"))); // the originals stay
        Assert.True(File.Exists(Path.Combine(legacy.LocalDir, "models", "ggml-tiny.en.bin")));
        Assert.Equal("RightAlt", SettingsLoader.LoadFile(paths.SettingsFile).Settings.Hotkey);
        Assert.True(File.Exists(Path.Combine(paths.DefaultModelsDir, "ggml-tiny.en.bin")));
        Assert.False(LegacyMigration.HasWork(paths, legacy)); // copied once, not again at every start

        var moved = LegacyMigration.Run(AppPaths.ForRoots(Path.Combine(scratch.Root.FullName, "r2"), Path.Combine(scratch.Root.FullName, "l2"), scratch.Paths.ExeDir, null), legacy);
        Assert.False(moved.Copied); // unpackaged: moved, so the Dictate folders empty out
        Assert.False(Directory.Exists(legacy.ConfigDir));
    }

    [Fact]
    public void The_notice_says_a_copy_left_Dictates_folders_and_offers_no_removal()
    {
        var copied = AppShell.MigrationNotices(new MigrationReport(["a -> b (copied)"], [], ["c (Dictum already has c)"]) { Copied = true });
        Assert.Contains(copied.Notices, n => n.Text.Contains("copied into this Store copy") && n.Text.Contains("Dictate's own folders are left as they were"));
        Assert.DoesNotContain(copied.Notices, n => n.Text.Contains("moved") || n.Text.Contains("Remove Dictum completely deletes them"));
        Assert.False(copied.OfferRemoval);

        var moved = AppShell.MigrationNotices(new MigrationReport(["a -> b"], [], []));
        Assert.Equal("Your Dictate settings, models and recordings were moved to Dictum.", Assert.Single(moved.Notices).Text);
        Assert.True(moved.OfferRemoval);
    }

    [Fact]
    public Task A_packaged_full_removal_never_lists_or_deletes_Dictates_folders() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, dialogs) = scratch.Shell();
        platform.FakePackage.IsPackaged = true;
        var leftover = Path.GetFullPath(Path.Combine(platform.LegacyFolders().ConfigDir, ".env"));
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "ANTHROPIC_API_KEY=the-users-own\n");
        dialogs.Answers.Enqueue(0);
        dialogs.ConfirmWord = true;
        Assert.True(shell.RemoveEverything());
        Assert.DoesNotContain(leftover, dialogs.Asked.First().Text);
        Assert.True(File.Exists(leftover)); // Dictate's data is the user's, not this package's
        Assert.False(File.Exists(scratch.Paths.SettingsFile));
    });

    [Fact]
    public void The_tray_idle_icon_is_the_apps_own_icon()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Dictum.ico")), "Dictum.ico is not beside the program");
        var icon = TrayHost.AppIcon(32);
        Assert.NotEqual(0, icon);
        NativeShell.DestroyIcon(icon);
    }
}
