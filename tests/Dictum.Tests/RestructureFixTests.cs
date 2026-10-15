using System.Diagnostics;
using System.Text;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Views;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// The restructure lens's fix batch (16/10/2026, SPEC §22's three new amendments): program removal by manifest, the
/// migration's skipped items and leftover folders, the earlier build running, consent only from consent.json, and the fix
/// hotkey paused with the dictation hotkey.
/// </summary>
public class RestructureFixTests
{
    /// <summary>A "Downloads" folder the portable zip was unzipped into: the shipped files, their manifest, and two of the user's own files.</summary>
    internal static (string Folder, string[] Shipped, string[] Foreign) PortableCopy(Scratch scratch, bool manifest = true, string name = "Downloads")
    {
        var folder = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, name)).FullName;
        var shipped = new[] { "Dictum.exe", "Dictum.dll", @"runtimes\vulkan\win-x64\whisper.dll" };
        foreach (var (rel, i) in shipped.Select((r, i) => (r, i)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, rel))!);
            File.WriteAllBytes(Path.Combine(folder, rel), new byte[100 + i]);
        }
        if (manifest)
        {
            File.WriteAllLines(Path.Combine(folder, ProgramManifest.FileName),
                ["# Dictum shipped files", .. shipped.Select((rel, i) => $"{rel}\t{100 + i}\tabc{i}")]);
        }
        Directory.CreateDirectory(Path.Combine(folder, "photos"));
        var foreign = new[] { Path.Combine(folder, "thesis-final.docx"), Path.Combine(folder, "photos", "holiday.jpg") };
        foreach (var file in foreign) File.WriteAllText(file, "the user's own");
        return (folder, [.. shipped.Select(r => Path.Combine(folder, r))], foreign);
    }

    /// <summary>
    /// Runs the real detached step against <paramref name="folder"/>, started exactly as Dictum starts it (the production
    /// ProcessStartInfo: script, environment variables, working folder), waiting for no process.
    /// </summary>
    internal static void RunRemovalScript(string folder, string? exeFolder = null)
    {
        var steps = new RemovalSteps(new FakeRunKey(), folder, Path.Combine(exeFolder ?? folder, "Dictum.exe"), false, folder, folder, NullLog.Instance, waitForProcessId: 0);
        using var process = Process.Start(steps.RemovalStart(folder))!;
        Assert.True(process.WaitForExit(60_000), "the removal script did not finish");
    }

    [Fact]
    public void Removing_the_program_deletes_only_the_files_its_manifest_lists()
    {
        using var scratch = new Scratch();
        var (folder, shipped, foreign) = PortableCopy(scratch);
        File.WriteAllBytes(Path.Combine(folder, "Dictum.dll"), new byte[999]); // changed since it shipped: not ours to delete any more
        Assert.Null(RemovalSteps.Refusal(folder, packaged: false));
        RunRemovalScript(folder);
        Assert.False(File.Exists(shipped[0]));
        Assert.False(File.Exists(shipped[2]));
        Assert.False(Directory.Exists(Path.Combine(folder, "runtimes"))); // emptied, so removed
        Assert.False(File.Exists(Path.Combine(folder, ProgramManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(folder, "Dictum.dll"))); // size differs from the manifest: kept
        Assert.All(foreign, f => Assert.True(File.Exists(f), $"{f} was deleted"));
        Assert.True(Directory.Exists(folder)); // not empty, so kept
    }

    [Fact]
    public void A_program_folder_holding_only_shipped_files_goes_entirely()
    {
        using var scratch = new Scratch();
        var (folder, _, foreign) = PortableCopy(scratch);
        foreach (var file in foreign) File.Delete(file);
        Directory.Delete(Path.Combine(folder, "photos"));
        RunRemovalScript(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_program_folder_without_a_manifest_is_left_untouched_and_the_user_is_told()
    {
        using var scratch = new Scratch();
        var (folder, shipped, foreign) = PortableCopy(scratch, manifest: false);
        Assert.Contains("no manifest.txt", RemovalSteps.Refusal(folder, packaged: false));
        RunRemovalScript(folder);
        Assert.All(shipped.Concat(foreign), f => Assert.True(File.Exists(f), $"{f} was deleted"));
    }

    [Fact]
    public void The_first_warning_lists_the_exact_program_files_and_never_the_users()
    {
        using var scratch = new Scratch();
        var (folder, shipped, foreign) = PortableCopy(scratch);
        var steps = new RemovalSteps(new FakeRunKey(), folder, Path.Combine(folder, "Dictum.exe"), false, scratch.Root.FullName, scratch.Root.FullName, NullLog.Instance);
        var listed = steps.PlatformLocations().Select(l => l.Path).ToList();
        Assert.All(shipped, f => Assert.Contains(f, listed));
        Assert.Contains(Path.Combine(folder, ProgramManifest.FileName), listed);
        Assert.All(foreign, f => Assert.DoesNotContain(f, listed));
        var plan = RemovalPlan.Build(scratch.Paths, new AppSettings(), RemovalScope.Everything, steps.PlatformLocations());
        Assert.Equal(100 + 101 + 102, plan.Items.Where(i => i.Category == DataCategory.ProgramFile && Path.GetFileName(i.Path) != ProgramManifest.FileName).Sum(i => i.Bytes));
        Assert.Empty(plan.Execute().Failures);
        Assert.All(shipped, f => Assert.True(File.Exists(f))); // the running program's files go only in the detached step, after it exits
    }

    private static LegacyFolders Legacy(Scratch scratch) => new(Path.Combine(scratch.Root.FullName, "roaming", "Dictate"), Path.Combine(scratch.Root.FullName, "local", "Dictate"));

    private static AppPaths DictumPaths(Scratch scratch) => AppPaths.ForRoots(Path.Combine(scratch.Root.FullName, "roaming"), Path.Combine(scratch.Root.FullName, "local"), scratch.Paths.ExeDir, null);

    [Fact]
    public void A_skipped_secret_is_recorded_and_full_removal_deletes_the_leftover_folders()
    {
        using var scratch = new Scratch();
        var (paths, legacy) = (DictumPaths(scratch), Legacy(scratch));
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(paths.EnvFile, "LLM_API_KEY=dictum\n");
        Directory.CreateDirectory(legacy.ConfigDir);
        File.WriteAllText(Path.Combine(legacy.ConfigDir, ".env"), "ANTHROPIC_API_KEY=sk-ant-left-behind\n");
        File.WriteAllText(Path.Combine(legacy.ConfigDir, "dictionary.txt"), "Kynni\n");

        var report = LegacyMigration.Run(paths, legacy);
        Assert.Contains(report.Skipped, s => s.Contains(".env", StringComparison.Ordinal));
        Assert.Contains(report.Moved, m => m.Contains("dictionary.txt", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(legacy.ConfigDir, ".env"))); // Dictum's own .env was never replaced

        var plan = RemovalPlan.Build(paths, new AppSettings(), RemovalScope.Everything, legacy: legacy);
        Assert.Contains(plan.Items, i => i.Category == DataCategory.EarlierBuild && i.Path.EndsWith(".env", StringComparison.Ordinal));
        Assert.Empty(plan.Execute().Failures);
        Assert.False(Directory.Exists(legacy.ConfigDir)); // the key that was left behind is gone with its folder
        Assert.False(File.Exists(paths.EnvFile));
    }

    [Fact]
    public void Nothing_moves_while_the_earlier_build_runs()
    {
        using var scratch = new Scratch();
        var (paths, legacy) = (DictumPaths(scratch), Legacy(scratch));
        Directory.CreateDirectory(legacy.ConfigDir);
        File.WriteAllText(Path.Combine(legacy.ConfigDir, "settings.json"), """{ "hotkey": "RightAlt" }""");
        var asked = 0;
        Assert.Null(LegacyMigration.RunWhenSafe(paths, legacy, () => true, () => { asked++; return false; })); // "Quit Dictum"
        Assert.Equal(1, asked);
        Assert.True(File.Exists(Path.Combine(legacy.ConfigDir, "settings.json")));
        Assert.False(File.Exists(paths.SettingsFile));

        var running = true;
        var report = LegacyMigration.RunWhenSafe(paths, legacy, () => running, () => { running = false; return true; }); // quit it, then Retry
        Assert.NotNull(report);
        Assert.Contains(report.Moved, m => m.Contains("settings.json", StringComparison.Ordinal));
        Assert.Equal("RightAlt", SettingsLoader.LoadFile(paths.SettingsFile).Settings.Hotkey);
    }

    [Fact]
    public void With_nothing_to_move_the_earlier_build_is_not_even_asked_about()
    {
        using var scratch = new Scratch();
        var probes = 0;
        Assert.Same(MigrationReport.None, LegacyMigration.RunWhenSafe(DictumPaths(scratch), Legacy(scratch), () => { probes++; return true; }, () => false));
        Assert.Equal(0, probes);
    }

    [Fact]
    public void The_earlier_build_is_removed_only_from_where_install_ps1_put_it()
    {
        using var scratch = new Scratch();
        var (roaming, local) = (Path.Combine(scratch.Root.FullName, "roaming"), Path.Combine(scratch.Root.FullName, "local"));
        var install = Directory.CreateDirectory(Path.Combine(local, "Programs", "Dictate")).FullName;
        File.WriteAllText(Path.Combine(install, "Dictate.exe"), "");
        var shortcut = Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Dictate.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        File.WriteAllText(shortcut, "");
        var runKey = new FakeRunKey();
        runKey.Set("Dictate", $"\"{Path.Combine(install, "Dictate.exe")}\"");
        var steps = new RemovalSteps(runKey, scratch.Paths.ExeDir, Path.Combine(scratch.Paths.ExeDir, "Dictum.exe"), false, roaming, local, NullLog.Instance);
        Assert.Equal(3, steps.EarlierBuildLeftovers().Count);
        Assert.Empty(steps.RemoveEarlierBuild());
        Assert.False(Directory.Exists(install));
        Assert.False(File.Exists(shortcut));
        Assert.Empty(runKey.Values);

        var elsewhere = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "Tools", "Dictate")).FullName; // a copy run from elsewhere
        File.WriteAllText(Path.Combine(elsewhere, "Dictate.exe"), "");
        runKey.Set("Dictate", @"""C:\Other\app.exe"""); // another program's value of that name
        Assert.Empty(steps.EarlierBuildLeftovers());
        Assert.Empty(steps.RemoveEarlierBuild());
        Assert.True(File.Exists(Path.Combine(elsewhere, "Dictate.exe")));
        Assert.Equal(@"""C:\Other\app.exe""", runKey.Get("Dictate"));
    }

    [Fact]
    public void A_consent_written_into_settings_json_does_not_switch_cloud_clean_up_on()
    {
        using var scratch = new Scratch("""{ "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m", "cloud_consent": "llm.example.com" } }""");
        var warnings = new List<string>();
        var seeded = ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, warnings.Add);
        Assert.Equal(LlmSettings.Local, seeded.Settings.Llm.Provider);
        Assert.Contains(warnings, w => w.Contains("llm.cloud_consent", StringComparison.Ordinal) && w.Contains("ignored", StringComparison.Ordinal));

        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord("openai-compatible", "llm.example.com", DateTimeOffset.UtcNow, "test"));
        Assert.Equal(LlmSettings.OpenAiCompatible, ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }).Settings.Llm.Provider);
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord("anthropic", "llm.example.com", DateTimeOffset.UtcNow, "test"));
        Assert.Equal(LlmSettings.Local, ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }).Settings.Llm.Provider); // given for another provider
    }

    [Fact]
    public Task The_hotkey_box_pauses_the_fix_hotkey_too() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var hooks = platform.Listeners.Where(l => !l.Disposed).ToList();
        Assert.Equal(2, hooks.Count); // the dictation hotkey and fix_hotkey
        var box = new HotkeyCaptureBox(shell, "F24", listenAtOnce: false);
        box.Start();
        Assert.All(hooks, h => Assert.False(h.Enabled));
        box.Stop();
        Assert.All(hooks, h => Assert.True(h.Enabled));
        shell.Dispose();
    });

    [Fact]
    public Task Settings_About_says_Offline_only_when_it_is_on() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "offline_only": true }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Assert.Contains(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.TextBlock>(),
            t => t.IsVisible && t.Text == "Offline only: Dictum connects to no other machine.");
        window.Close();
        shell.Dispose();
    });
}
