using System.Text.Json;
using Dictum.Calibration;
using Dictum.Audio;
using Dictum.Personal;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>SPEC §17: the path registry, the deletions it drives, model deletion and the low-disk warning.</summary>
public class StorageTests
{
    /// <summary>Runs every writer the app has against a scratch profile, as a dictating, calibrating, downloading user would.</summary>
    private static void WriteEverything(AppPaths paths, AppSettings settings)
    {
        SettingsFile.CreateIfMissing(paths.SettingsFile, "small.en");
        SettingsFile.SetValue(paths.SettingsFile, "hotkey", System.Text.Json.Nodes.JsonValue.Create("F24"));
        TextList.Append(paths.DictionaryFile, ["Kynni"], PersonalDictionary.FileHeader);
        TextList.Append(paths.CorrectionsFile, ["teh => the"], Corrections.FileHeader);
        File.WriteAllText(paths.ProfanityOverrideFile, "bloody\n");
        File.WriteAllText(paths.EnvFile, "LLM_API_KEY=x\n");
        var models = paths.ModelsDir(settings);
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, "ggml-small.en.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(models, "ggml-base.en.bin.part"), new byte[300]);
        File.WriteAllBytes(Path.Combine(models, "qwen2.5-1.5b-instruct-q4_k_m.gguf"), new byte[500]);
        new ModelIntegrity(paths.VerifiedModelsFile, NullLog.Instance).Remember(Path.Combine(models, "ggml-small.en.bin"));
        CloudConsentFile.Write(paths.ConsentFile, new CloudConsentRecord("anthropic", "api.anthropic.com", DateTimeOffset.UtcNow, "test"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), paths.CalibrationScriptFile, overwrite: true);
        var recordings = CalibrationRecordings.For(paths);
        recordings.Save(1, new CapturedAudio(new float[16000], 0.3f));
        using (var log = new FileLog(paths.LogsDir, new SecretRedactor(), LogLevel.Info)) log.Info("hello");
    }

    private static IEnumerable<string> FilesUnder(params string[] roots) =>
        roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories));

    private static bool Covered(string file, IEnumerable<OwnedLocation> locations) => locations.Any(l =>
        l.Pattern is null ? !l.IsFolder && PathRegistry.SamePath(l.Path, file)
        : PathRegistry.SamePath(Path.GetDirectoryName(file)!, l.Path)
          && (l.Pattern == ModelFiles.Pattern ? ModelFiles.IsModelFile(Path.GetFileName(file)) : System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(l.Pattern, Path.GetFileName(file))));

    [Fact]
    public void The_registry_covers_every_file_the_app_writes_and_full_removal_deletes_them_all()
    {
        using var scratch = new Scratch();
        var settings = new AppSettings();
        WriteEverything(scratch.Paths, settings);
        var outside = Path.Combine(scratch.Root.FullName, "not-dictum.txt");
        File.WriteAllText(outside, "keep");
        var foreign = Path.Combine(scratch.Paths.DefaultModelsDir, "my-notes.txt");
        File.WriteAllText(foreign, "keep"); // not a catalogue file: never Dictum's to delete

        var registry = PathRegistry.Locations(scratch.Paths, settings);
        var written = FilesUnder(scratch.Paths.ConfigDir, scratch.Paths.LocalDir).Where(f => f != foreign).ToList();
        Assert.True(written.Count >= 10, $"only {written.Count} files were written");
        Assert.All(written, f => Assert.True(Covered(f, registry), $"{f} is written by the app but missing from the path registry"));

        var plan = RemovalPlan.Build(scratch.Paths, settings, RemovalScope.Everything);
        Assert.All(written, f => Assert.Contains(plan.Items, i => PathRegistry.SamePath(i.Path, f)));
        Assert.Equal(written.Sum(f => new FileInfo(f).Length), plan.TotalBytes);
        var result = plan.Execute();
        Assert.Empty(result.Failures);
        Assert.All(written, f => Assert.False(File.Exists(f), $"{f} survived"));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(foreign));
        Assert.False(Directory.Exists(scratch.Paths.ConfigDir)); // emptied folders go too
        Assert.False(Directory.Exists(scratch.Paths.LogsDir));
    }

    [Fact]
    public void Delete_all_app_data_never_touches_env_or_the_program_folder()
    {
        using var scratch = new Scratch();
        var settings = new AppSettings();
        WriteEverything(scratch.Paths, settings);
        File.WriteAllText(Path.Combine(scratch.Paths.ExeDir, "Dictum.exe"), "");
        File.WriteAllText(scratch.Paths.ExeEnvFile, "LLM_API_KEY=y\n");

        var all = RemovalPlan.Build(scratch.Paths, settings, RemovalScope.AllData);
        Assert.DoesNotContain(all.Items, i => i.Category is DataCategory.Secrets or DataCategory.Settings or DataCategory.PersonalFiles or DataCategory.ProgramFolder);
        Assert.DoesNotContain(all.Items, i => PathRegistry.IsInside(i.Path, scratch.Paths.ExeDir));
        all.Execute();
        Assert.True(File.Exists(scratch.Paths.EnvFile));
        Assert.True(File.Exists(scratch.Paths.SettingsFile));
        Assert.True(File.Exists(scratch.Paths.DictionaryFile));
        Assert.Empty(ModelFiles.List(scratch.Paths, settings));

        RemovalPlan.Build(scratch.Paths, settings, RemovalScope.AllDataIncludingSettings).Execute();
        Assert.False(File.Exists(scratch.Paths.SettingsFile));
        Assert.False(File.Exists(scratch.Paths.CorrectionsFile));
        Assert.True(File.Exists(scratch.Paths.EnvFile)); // keys go only with "Remove Dictum completely"
        Assert.True(File.Exists(scratch.Paths.ExeEnvFile));
        Assert.True(File.Exists(Path.Combine(scratch.Paths.ExeDir, "Dictum.exe")));
    }

    [Fact]
    public void Recordings_and_logs_are_deleted_on_their_own()
    {
        using var scratch = new Scratch();
        var settings = new AppSettings();
        WriteEverything(scratch.Paths, settings);
        var recordings = RemovalPlan.Build(scratch.Paths, settings, RemovalScope.CalibrationRecordings);
        Assert.All(recordings.Items, i => Assert.Equal(DataCategory.CalibrationRecordings, i.Category is DataCategory.AppFolder ? DataCategory.CalibrationRecordings : i.Category));
        recordings.Execute();
        Assert.Empty(Directory.GetFiles(scratch.Paths.CalibrationDir)); // the empty folder stays; only full removal deletes the app folders
        Assert.NotEmpty(Directory.GetFiles(scratch.Paths.LogsDir));
        RemovalPlan.Build(scratch.Paths, settings, RemovalScope.Logs).Execute();
        Assert.Empty(Directory.GetFiles(scratch.Paths.LogsDir));
        Assert.NotEmpty(ModelFiles.List(scratch.Paths, settings));
    }

    [Fact]
    public void Deleting_a_model_refuses_other_folders_unknown_files_and_the_model_in_use()
    {
        using var scratch = new Scratch();
        var settings = new AppSettings();
        WriteEverything(scratch.Paths, settings);
        var integrity = new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance);
        var small = Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-small.en.bin");
        var elsewhere = Path.Combine(scratch.Root.FullName, "ggml-small.en.bin");
        File.WriteAllBytes(elsewhere, new byte[10]);

        Assert.False(ModelFiles.Delete(elsewhere, scratch.Paths, settings, [], integrity).Deleted);
        Assert.True(File.Exists(elsewhere)); // a model chosen by path is only forgotten
        File.WriteAllText(Path.Combine(scratch.Paths.DefaultModelsDir, "notes.bin"), "x");
        Assert.False(ModelFiles.Delete(Path.Combine(scratch.Paths.DefaultModelsDir, "notes.bin"), scratch.Paths, settings, [], integrity).Deleted);
        Assert.False(ModelFiles.Delete(small, scratch.Paths, settings, [small], integrity).Deleted);
        Assert.True(File.Exists(small));

        Assert.Contains(Path.GetFullPath(small), File.ReadAllText(scratch.Paths.VerifiedModelsFile).Replace("\\\\", "\\"));
        File.WriteAllBytes(small + ".part", new byte[200]);
        var deleted = ModelFiles.Delete(small, scratch.Paths, settings, [], integrity);
        Assert.True(deleted.Deleted);
        Assert.Equal(1200, deleted.FreedBytes); // the file and its partial download
        Assert.False(File.Exists(small));
        Assert.False(File.Exists(small + ".part"));
        Assert.DoesNotContain("ggml-small.en.bin", File.ReadAllText(scratch.Paths.VerifiedModelsFile)); // the integrity record is cleaned
    }

    [Fact]
    public void A_download_that_would_leave_under_2_GB_free_is_warned_about()
    {
        Assert.NotNull(DiskSpace.DownloadWarning(@"C:\models", 1_600_000_000, _ => 3_000_000_000));
        Assert.Null(DiskSpace.DownloadWarning(@"C:\models", 1_600_000_000, _ => 4_000_000_000));
        Assert.Null(DiskSpace.DownloadWarning(@"C:\models", 1_600_000_000, _ => null)); // unknown free space: no false alarm
    }
}

/// <summary>SPEC §21: the one-time move from the earlier build's folders (named Dictate) to Dictum's.</summary>
public class LegacyMigrationTests
{
    private static (AppPaths Paths, LegacyFolders Legacy) Layout(Scratch scratch, bool configOverridden = false)
    {
        var legacy = new LegacyFolders(Path.Combine(scratch.Root.FullName, "roaming", "Dictate"), Path.Combine(scratch.Root.FullName, "local", "Dictate"));
        var paths = AppPaths.ForRoots(Path.Combine(scratch.Root.FullName, "roaming"), Path.Combine(scratch.Root.FullName, "local"), scratch.Paths.ExeDir,
            configOverridden ? scratch.Paths.ConfigDir : null);
        return (paths, legacy);
    }

    private static string Seed(LegacyFolders legacy)
    {
        Directory.CreateDirectory(Path.Combine(legacy.LocalDir, "models"));
        Directory.CreateDirectory(Path.Combine(legacy.LocalDir, "logs"));
        Directory.CreateDirectory(Path.Combine(legacy.LocalDir, "calibration"));
        Directory.CreateDirectory(legacy.ConfigDir);
        var model = Path.Combine(legacy.LocalDir, "models", "ggml-small.en.bin");
        File.WriteAllBytes(model, new byte[100]);
        File.WriteAllText(Path.Combine(legacy.LocalDir, "logs", "dictate-20261015.log"), "old log");
        File.WriteAllText(Path.Combine(legacy.LocalDir, "calibration", "clip-01.wav"), "riff");
        File.WriteAllText(Path.Combine(legacy.LocalDir, "verified-models.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [model] = "100:1:abc" }));
        File.WriteAllText(Path.Combine(legacy.ConfigDir, "settings.json"), JsonSerializer.Serialize(new { hotkey = "RightAlt", whisper = new { model } }));
        File.WriteAllText(Path.Combine(legacy.ConfigDir, "dictionary.txt"), "Kynni\n");
        File.WriteAllText(Path.Combine(legacy.ConfigDir, ".env"), "LLM_API_KEY=x\n");
        return model;
    }

    [Fact]
    public void Settings_models_recordings_and_logs_move_once_and_paths_are_rebased()
    {
        using var scratch = new Scratch();
        var (paths, legacy) = Layout(scratch);
        var oldModel = Seed(legacy);

        var report = LegacyMigration.Run(paths, legacy);
        Assert.Empty(report.Left);
        Assert.Contains(report.Moved, m => m.Contains("settings.json", StringComparison.Ordinal));
        var newModel = Path.Combine(paths.DefaultModelsDir, "ggml-small.en.bin");
        Assert.True(File.Exists(newModel));
        Assert.True(File.Exists(Path.Combine(paths.LogsDir, "dictate-20261015.log")));
        Assert.True(File.Exists(Path.Combine(paths.CalibrationDir, "clip-01.wav")));
        Assert.True(File.Exists(paths.DictionaryFile));
        Assert.True(File.Exists(paths.EnvFile));
        var settings = SettingsLoader.LoadFile(paths.SettingsFile).Settings;
        Assert.Equal("RightAlt", settings.Hotkey);
        Assert.Equal(newModel, settings.Whisper.Model); // an absolute path into the old folder now points at the new one
        var record = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.VerifiedModelsFile))!;
        Assert.Equal([newModel], record.Keys); // the verified record follows the file, so it is not hashed again
        Assert.False(Directory.Exists(legacy.ConfigDir));
        Assert.False(Directory.Exists(legacy.LocalDir));
        Assert.False(File.Exists(oldModel));

        Assert.False(LegacyMigration.Run(paths, legacy).Any); // once only
    }

    [Fact]
    public void Dictum_files_are_never_replaced_and_an_overridden_config_folder_is_left_alone()
    {
        using var scratch = new Scratch();
        var (paths, legacy) = Layout(scratch);
        Seed(legacy);
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(paths.SettingsFile, """{ "hotkey": "F9" }""");
        LegacyMigration.Run(paths, legacy);
        Assert.Equal("F9", SettingsLoader.LoadFile(paths.SettingsFile).Settings.Hotkey);
        Assert.True(File.Exists(Path.Combine(legacy.ConfigDir, "settings.json"))); // left where it was

        using var other = new Scratch();
        var (overridden, legacy2) = Layout(other, configOverridden: true);
        Seed(legacy2);
        var report = LegacyMigration.Run(overridden, legacy2);
        Assert.DoesNotContain(report.Moved, m => m.Contains("settings.json", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(legacy2.ConfigDir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(overridden.DefaultModelsDir, "ggml-small.en.bin"))); // local data still moves
    }

    [Fact]
    public void A_file_held_open_is_reported_and_left_for_the_next_start()
    {
        using var scratch = new Scratch();
        var (paths, legacy) = Layout(scratch);
        Seed(legacy);
        using (new FileStream(Path.Combine(legacy.ConfigDir, "dictionary.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = LegacyMigration.Run(paths, legacy);
            Assert.Contains(report.Left, l => l.Contains("dictionary.txt", StringComparison.Ordinal));
        }
        Assert.Contains(LegacyMigration.Run(paths, legacy).Moved, m => m.Contains("dictionary.txt", StringComparison.Ordinal));
    }
}
