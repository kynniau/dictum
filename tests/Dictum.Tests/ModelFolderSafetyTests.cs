using System.Diagnostics;
using System.Text.Json;
using Dictum.Cli;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// The lens on PR #11 (17/10/2026), its findings fixed and pinned: a chosen models folder that other users may share (its files
/// hashed again in each session, and "Delete all app data" leaving it alone), the bundled base.en as the replacement for a model
/// in use, a seeded clean-up provider kept at first run, the bundled folder by another name, the advice for a damaged file in
/// each folder, one lookup for calibration and the command line, and Settings > Models' lookups. The re-lens on fa73e02
/// (17/10/2026) adds the bundled folder by its volume name, a replacement the tray app's lookup would load, a copy of the model
/// in use deleted when another is next, a junction out of or into the profile, the models page's lookups with a model loaded, and
/// --bench-cleanup's lookup. Model files of the catalogue's size are made with SetLength (zeros), so a SHA-256 check of one
/// fails unless a record says it passed.
/// </summary>
public sealed class ModelFolderSafetyTests
{
    private static readonly long BaseEnBytes = ModelCatalog.Find("base.en")!.Bytes;
    private static readonly long TinyEnBytes = ModelCatalog.Find("tiny.en")!.Bytes;

    private static string Create(string folder, string fileName, long bytes)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(bytes);
        return path;
    }

    private static ModelManager FakeModels() => new(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);

    /// <summary>
    /// A loaded model stand-in that keeps its file open, without sharing deletion, until it is disposed, as a memory-mapped model
    /// does on Windows: the file cannot be deleted while the model is loaded, so a deletion has to release the model first.
    /// </summary>
    private sealed class HoldingEngine(string path, DecodeOptions decode) : ISpeechEngine
    {
        private readonly FileStream _held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        public string ModelPath { get; } = path;
        public DecodeOptions Decode { get; private set; } = decode;
        public void Reconfigure(DecodeOptions options) => Decode = options;
        public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken) => Task.FromResult(Path.GetFileName(ModelPath));
        public void Dispose() => _held.Dispose();
    }

    private static ModelManager HoldingModels() => new(NullLog.Instance, loader: (p, _, d) => new HoldingEngine(p, d), warmUp: () => false);

    /// <summary>Another user's swap: the same size, other bytes, the last-write time put back.</summary>
    private static void Swap(string path)
    {
        var when = File.GetLastWriteTimeUtc(path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.Write([1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, when);
    }

    /// <summary>A directory junction (mklink /J needs no elevation); false when one could not be made here.</summary>
    private static bool Junction(string link, string target)
    {
        if (SystemPrograms.Find(SystemPrograms.Root, @"System32\cmd.exe") is not { } cmd) return false;
        using var process = Process.Start(new ProcessStartInfo(cmd, $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private const string NoVolumeName = "mountvol could not give this machine's temporary drive a volume name, so a volume-name path cannot be tested here.";

    /// <summary>
    /// <paramref name="folder"/> by its volume name (\\?\Volume{GUID}\...), as mountvol prints the drive's: the same folder, which no
    /// comparison took for it; null when this machine gives the drive none.
    /// </summary>
    private static string? ByVolumeName(string folder)
    {
        if (SystemPrograms.Find(SystemPrograms.Root, @"System32\mountvol.exe") is not { } mountvol) return null;
        var root = Path.GetPathRoot(folder)!;
        using var process = Process.Start(new ProcessStartInfo(mountvol, $"{root} /L")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var volume = process.StandardOutput.ReadToEnd().Trim();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        var alias = volume + folder[root.Length..];
        return volume.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) && Directory.Exists(alias) ? alias : null;
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        return condition();
    }

    // ---- 1. A chosen folder that other users may share ----

    /// <summary>
    /// Pin (lens probe P7, inverted): the "verified earlier" record is trusted only in the user's own folders. A same-size file
    /// swapped into the chosen folder with its time put back is hashed again in the next session and refused; the same swap in
    /// the per-user folder still passes on its record, the ceiling the simplification in ModelIntegrity names.
    /// </summary>
    [Fact]
    public void A_same_size_swap_in_a_chosen_folder_is_hashed_again_in_the_next_session_and_refused()
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "E-drive shared models");
        var settings = new AppSettings { Models = new ModelsSettings { Folder = chosen } };
        IReadOnlyList<ModelFolder> Folders() => ModelFolders.LookupOrder(scratch.Paths, settings);
        var shared = Create(chosen, "ggml-tiny.en.bin", TinyEnBytes);
        var own = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        var first = new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance, Folders);
        first.Remember(shared); // this user's first session: both passed their check
        first.Remember(own);
        Swap(shared);
        Swap(own);

        var next = new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance, Folders); // the next start
        var refused = Assert.Throws<ModelIntegrityException>(() => next.Verify(shared)).Message;
        Assert.Contains("does not match the catalogue (SHA-256", refused, StringComparison.Ordinal);
        Assert.EndsWith("it was not loaded. Delete it and download it again.", refused, StringComparison.Ordinal);
        Assert.Equal("SHA-256 verified earlier", next.Verify(own));
    }

    /// <summary>
    /// Pin: "Delete all app data" leaves a chosen folder outside the user's profile alone and says so in its question, while
    /// Storage's Delete still removes a model there; a chosen folder inside the profile is emptied as before.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_all_app_data_leaves_a_chosen_folder_outside_the_profile_alone_and_says_so(bool insideProfile)
    {
        var (said, sharedLeft, ownLeft, question, deletedOne, sharedLeftAfter) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch();
            var profile = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "profile")).FullName;
            scratch.Paths = scratch.Paths with { ProfileDir = profile };
            var chosen = Path.Combine(insideProfile ? profile : scratch.Root.FullName, "Dictum models");
            File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", models = new { folder = chosen } }));
            var shared = Create(chosen, "ggml-small.en.bin", 10);
            var own = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", 10);
            var (shell, _, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);

            dialogs.Answers.Enqueue(0);
            var said = shell.DeleteData(RemovalScope.AllData);
            var question = Assert.Single(dialogs.Asked).Text;
            var (sharedLeft, ownLeft) = (File.Exists(shared), File.Exists(own));
            var deletedOne = "";
            if (sharedLeft)
            {
                dialogs.Answers.Enqueue(0);
                deletedOne = await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "small.en"));
            }
            var after = File.Exists(shared);
            shell.Dispose();
            return (said, sharedLeft, ownLeft, question.Replace(chosen, "<chosen>", StringComparison.Ordinal), deletedOne, after);
        });
        Assert.StartsWith("Deleted ", said, StringComparison.Ordinal);
        Assert.False(ownLeft);
        Assert.Equal(!insideProfile, sharedLeft);
        Assert.Equal(!insideProfile, question.EndsWith(" " + AppShell.LeftAlone("<chosen>"), StringComparison.Ordinal));
        Assert.Equal(insideProfile ? "" : "Deleted ggml-small.en.bin (0 MB freed).", deletedOne);
        Assert.False(sharedLeftAfter);
    }

    /// <summary>Pin: an item a deletion could not remove (a read-only file here) is counted as "in use or not permitted".</summary>
    [Fact]
    public Task A_file_that_could_not_be_deleted_is_counted_as_in_use_or_not_permitted() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var kept = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", 10);
        File.SetAttributes(kept, FileAttributes.ReadOnly); // Windows refuses to delete a read-only file
        try
        {
            var (shell, _, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);
            dialogs.Answers.Enqueue(0);
            Assert.Equal("Deleted 0 MB. 1 item(s) could not be deleted (in use or not permitted); see the log.", shell.DeleteData(RemovalScope.AllData));
            Assert.True(File.Exists(kept));
            shell.Dispose();
        }
        finally
        {
            File.SetAttributes(kept, FileAttributes.Normal);
        }
    });

    /// <summary>
    /// Pin (re-lens probe R12): InProfile follows links. A chosen folder that is a junction inside the profile pointing out of it
    /// counts as outside (its models are shared: "Delete all app data" leaves them alone), and one outside the profile pointing
    /// into it as inside (emptied, as before): the category follows where the files are, not what the folder is called.
    /// </summary>
    [Fact]
    public void A_chosen_folder_reached_through_a_junction_is_inside_or_outside_the_profile_by_where_its_files_are()
    {
        using var scratch = new Scratch();
        var profile = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "profile")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "outside")).FullName;
        var inside = Directory.CreateDirectory(Path.Combine(profile, "models-here")).FullName;
        var paths = scratch.Paths with { ProfileDir = profile };
        var (toOutside, toInside) = (Path.Combine(profile, "to-outside"), Path.Combine(scratch.Root.FullName, "to-inside"));
        try
        {
            if (!Junction(toOutside, outside) || !Junction(toInside, inside)) Assert.Skip(NoJunction);
            Assert.False(paths.InProfile(toOutside)); // inside the profile by its name, outside it by its files
            Assert.True(paths.InProfile(toInside)); // outside the profile by its name, inside it by its files
            Assert.Equal(DataCategory.SharedModels, Category(toOutside));
            Assert.Equal(DataCategory.Models, Category(toInside));
        }
        finally
        {
            foreach (var link in new[] { toOutside, toInside }) if (Directory.Exists(link)) Directory.Delete(link); // the link only
        }

        DataCategory Category(string chosen) =>
            PathRegistry.Locations(paths, new AppSettings { Models = new ModelsSettings { Folder = chosen } })
                .Single(l => l.Pattern == ModelFiles.Pattern && PathRegistry.SamePath(l.Path, chosen)).Category;
    }

    // ---- 2. The bundled base.en replaces a model in use ----

    /// <summary>Pin (lens probe P2, inverted): deleting the model in use offers the bundled base.en, switches to it, and never deletes it.</summary>
    [Fact]
    public async Task Deleting_the_model_in_use_switches_to_the_bundled_base_en_and_never_deletes_it()
    {
        var (said, asked, model, loaded, bundledLeft, tinyLeft, bundled) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" }, "llm": { "provider": "none" } }""");
            var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
            var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
            new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled); // as if hashed once already
            var models = FakeModels();
            var (shell, _, dialogs) = scratch.Shell(models, downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false);
            Assert.Equal(tiny, models.ModelPath);
            dialogs.Answers.Enqueue(0); // Switch and delete
            var said = await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "tiny.en"));
            var result = (said, dialogs.Asked.ToList(), shell.Settings.Whisper.Model, models.ModelPath, File.Exists(bundled), File.Exists(tiny), bundled);
            shell.Dispose();
            return result;
        });
        Assert.Equal([("Delete a model in use", "tiny.en is in use. Switch to base.en first, then delete tiny.en (77 MB)?")], asked);
        Assert.Equal("Deleted ggml-tiny.en.bin (77 MB freed).", said);
        Assert.Equal(("base.en", bundled), (model, loaded));
        Assert.True(bundledLeft);
        Assert.False(tinyLeft);
    }

    /// <summary>
    /// Pin (re-lens F3, inverted from "a second copy of the model in use is never its replacement", which still holds: no model
    /// is switched to): a downloaded copy of the model in use, the copy that came with Dictum next in the lookup, is deleted
    /// after a question of its own. The model is released, the file deleted and the settings reloaded, so Dictum goes on with
    /// the bundled copy. When that copy fails its SHA-256 the tray app's lookup would pass over it, there is nothing to go on
    /// with, and the model in use cannot be deleted, as before.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_downloaded_copy_of_the_model_in_use_is_deleted_when_the_copy_that_came_with_Dictum_is_next(bool bundledPasses)
    {
        var (said, asked, model, loaded, ownLeft, bundledLeft, own, bundled) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
            var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
            var own = Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", BaseEnBytes); // found first
            if (bundledPasses) new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled); // as if hashed once already
            var models = HoldingModels();
            var (shell, _, dialogs) = scratch.Shell(models, downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false);
            Assert.Equal(own, models.ModelPath);
            Assert.True(await Eventually(() => models.IsReady)); // loaded, and holding its file: the deletion has to release it first
            dialogs.Answers.Enqueue(0); // Delete
            var said = await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "base.en"));
            await Eventually(() => models.IsReady); // the reloaded model has finished loading: it holds its file until the shell is disposed
            var result = (said, dialogs.Asked.ToList(), shell.Settings.Whisper.Model, models.ModelPath, File.Exists(own), File.Exists(bundled), own, bundled);
            shell.Dispose();
            return result;
        });
        Assert.Equal("base.en", model); // whisper.model untouched: no other model was switched to
        Assert.True(bundledLeft); // never deleted
        if (bundledPasses)
        {
            Assert.Equal([("Delete a model", "Delete this copy of base.en (147 MB)? Dictum then uses the copy that came with it.")], asked);
            Assert.Equal("Deleted ggml-base.en.bin (147 MB freed).", said);
            Assert.False(ownLeft);
            Assert.Equal(bundled, loaded); // the reload loaded the copy that came with Dictum
        }
        else
        {
            Assert.Equal("base.en is in use and no other model is downloaded. Download or choose another model first.", said);
            Assert.Empty(asked);
            Assert.True(ownLeft);
            Assert.Equal(own, loaded);
        }
    }

    /// <summary>
    /// Pin: the same when the next copy is in another of Dictum's folders (the chosen folder's copy in use, the per-user one next):
    /// the question names that folder instead of "the copy that came with it".
    /// </summary>
    [Fact]
    public async Task A_copy_in_the_chosen_folder_is_deleted_when_the_per_user_copy_is_next()
    {
        var (said, asked, loaded, chosenLeft, ownLeft, own, ownFolder) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch();
            var chosenFolder = Path.Combine(scratch.Root.FullName, "E-drive models");
            File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", whisper = new { model = "base.en" }, llm = new { provider = "none" }, models = new { folder = chosenFolder } }));
            var chosen = Create(chosenFolder, "ggml-base.en.bin", BaseEnBytes); // found first
            var own = Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", BaseEnBytes);
            var models = HoldingModels();
            var (shell, _, dialogs) = scratch.Shell(models, downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false);
            Assert.Equal(chosen, models.ModelPath);
            Assert.True(await Eventually(() => models.IsReady)); // loaded, and holding its file
            dialogs.Answers.Enqueue(0); // Delete
            var said = await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "base.en" && m.Folder == ModelFolderKind.Chosen));
            await Eventually(() => models.IsReady); // the reloaded model has finished loading: it holds its file until the shell is disposed
            var result = (said, dialogs.Asked.ToList(), models.ModelPath, File.Exists(chosen), File.Exists(own), own, scratch.Paths.DefaultModelsDir);
            shell.Dispose();
            return result;
        });
        Assert.Equal([("Delete a model", $"Delete this copy of base.en (147 MB)? Dictum then uses the other copy, in {ownFolder}.")], asked);
        Assert.Equal("Deleted ggml-base.en.bin (147 MB freed).", said);
        Assert.False(chosenLeft);
        Assert.True(ownLeft);
        Assert.Equal(own, loaded);
    }

    /// <summary>
    /// Pin: when the copy in use cannot be deleted (a read-only file: Windows refuses), the model that was released is loaded
    /// again, whatever happens to the file, so dictation is never left without a model.
    /// </summary>
    [Fact]
    public async Task A_copy_in_use_that_cannot_be_deleted_leaves_its_model_loaded_again()
    {
        var (loaded, own, ownLeft) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
            var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
            var own = Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", BaseEnBytes);
            new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled); // the copy that came with Dictum is next
            var models = FakeModels();
            var (shell, _, dialogs) = scratch.Shell(models, downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false);
            File.SetAttributes(own, FileAttributes.ReadOnly); // Windows refuses to delete a read-only file
            try
            {
                dialogs.Answers.Enqueue(0); // Delete
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "base.en")));
                return (models.ModelPath, own, File.Exists(own));
            }
            finally
            {
                File.SetAttributes(own, FileAttributes.Normal);
                shell.Dispose();
            }
        });
        Assert.True(ownLeft);
        Assert.Equal(own, loaded); // released for the deletion, loaded again when it failed
    }

    /// <summary>
    /// Pin (re-lens probe R13, inverted): a bundled base.en that fails its SHA-256 is not offered as the replacement. The tray
    /// app's lookup passes over it (Storage shows base.en as not on this PC), so switching to it would leave whisper.model naming
    /// a file that is not loaded; with nothing else downloaded, the model in use cannot be deleted and nothing is asked.
    /// </summary>
    [Fact]
    public async Task A_bundled_base_en_that_fails_its_check_is_not_offered_as_the_replacement()
    {
        var (said, asked, model, tinyLeft, onThisPc) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" }, "llm": { "provider": "none" } }""");
            Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes); // zeros, no record: fails the SHA-256
            var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
            new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(tiny);
            var (shell, _, dialogs) = scratch.Shell(FakeModels(), downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false);
            var onThisPc = shell.Models.IsDownloaded(ModelCatalog.Find("base.en")!);
            var said = await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "tiny.en"));
            var result = (said, dialogs.Asked.ToList(), shell.Settings.Whisper.Model, File.Exists(tiny), onThisPc);
            shell.Dispose();
            return result;
        });
        Assert.False(onThisPc); // the tray app passes the failing bundled base.en over
        Assert.Equal("tiny.en is in use and no other model is downloaded. Download or choose another model first.", said);
        Assert.Empty(asked); // so it is not offered
        Assert.Equal("tiny.en", model);
        Assert.True(tinyLeft);
    }

    /// <summary>Pin: the same for a clean-up model whose file the tray app's lookup passes over (here the wrong size): it is not offered as the replacement.</summary>
    [Fact]
    public async Task A_cleanup_model_the_lookup_would_not_load_is_not_offered_as_the_replacement()
    {
        var (said, asked, inUseLeft) = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "local", "model": "qwen2.5-1.5b" } }""");
            var (shell, _, dialogs) = scratch.Shell(FakeModels(), downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false); // before the files exist, so no clean-up model is loaded
            var inUse = CleanupCatalog.Find("qwen2.5-1.5b")!;
            var inUseFile = Create(scratch.Paths.DefaultModelsDir, inUse.FileName, inUse.Bytes);
            Create(scratch.Paths.DefaultModelsDir, CleanupCatalog.Find("qwen2.5-0.5b")!.FileName, 10); // the wrong size: the lookup passes over it
            var said = await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "qwen2.5-1.5b"));
            var result = (said, dialogs.Asked.ToList(), File.Exists(inUseFile));
            shell.Dispose();
            return result;
        });
        Assert.Equal("qwen2.5-1.5b is in use and no other model is downloaded. Download or choose another model first.", said);
        Assert.Empty(asked);
        Assert.True(inUseLeft);
    }

    // ---- 3. A seeded clean-up provider ----

    /// <summary>
    /// Pin (lens probe P6, inverted): a seed with llm.provider local and no llm.model keeps local at a first run with base.en,
    /// gets the recommended model written, downloads nothing, and the notice says the rule-based tidy is used until it is.
    /// </summary>
    [Fact]
    public Task A_seeded_local_provider_is_kept_at_first_run_with_the_recommended_model_and_nothing_downloads() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "local" } }""");
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled);
        var server = new CountingHandler();
        var (shell, platform, dialogs) = scratch.Shell(FakeModels(), downloadHandler: server);
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 1), dialogs.FirstRunSteps); // no model question; the hotkey check stays
            var written = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings;
            var recommended = CleanupCatalog.Recommend(false).Model; // the test shell's machine has no GPU
            Assert.Equal(("base.en", LlmSettings.Local, recommended), (written.Whisper.Model, written.Llm.Provider, written.Llm.Model));
            Assert.Contains(platform.Tray!.Notices, n => n.Text == $"Cleanup model {CleanupCatalog.DisplayName(recommended)} is not downloaded; using the rule-based tidy. Download it in Settings > Models.");
            Assert.Equal(0, server.Requests);
            Assert.Empty(dialogs.DownloadQuestions);
        }
        finally
        {
            shell.Dispose();
        }
    });

    // ---- 4. The bundled folder by another name ----

    private const string NoJunction = "mklink /J could not make a junction on this machine, so a junction to the bundled folder cannot be tested here.";

    /// <summary>
    /// Pin (lens probe P1, inverted): a junction to the bundled folder is that folder: never the chosen folder (the settings load
    /// says it is ignored), never listed for deletion, never deleted through.
    /// </summary>
    [Fact]
    public void A_junction_to_the_bundled_folder_is_never_the_chosen_folder_and_nothing_in_it_is_deleted()
    {
        using var scratch = new Scratch();
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", 10);
        var alias = Path.Combine(scratch.Root.FullName, "alias");
        try
        {
            if (!Junction(alias, scratch.Paths.BundledModelsDir)) Assert.Skip(NoJunction);
            var settings = new AppSettings { Models = new ModelsSettings { Folder = alias } };
            Assert.True(scratch.Paths.IsBundled(alias));
            Assert.True(scratch.Paths.IsBundled(@"\\?\" + Path.Combine(alias, "more"))); // a \\?\ prefix, and a folder inside it
            Assert.Null(scratch.Paths.ChosenModelsDir(settings));
            Assert.Equal(scratch.Paths.DefaultModelsDir, scratch.Paths.ModelsDir(settings)); // no download lands in the bundled folder
            Assert.DoesNotContain(ModelFiles.List(scratch.Paths, settings), m => m.Name == "base.en"); // Storage offers no Delete
            foreach (var scope in new[] { RemovalScope.AllData, RemovalScope.Everything })
                Assert.DoesNotContain(RemovalPlan.Build(scratch.Paths, settings, scope).Items, i => i.Path.EndsWith("ggml-base.en.bin", StringComparison.OrdinalIgnoreCase));
            var integrity = new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance);
            Assert.Equal(ModelFiles.BundledNotDeleted("ggml-base.en.bin"), ModelFiles.Delete(Path.Combine(alias, "ggml-base.en.bin"), scratch.Paths, settings, [], integrity).Message);

            File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { models = new { folder = alias } }));
            var warnings = new List<string>();
            ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, warnings.Add);
            Assert.Contains(ConfigLoader.BundledFolderIgnored("models.folder", alias, scratch.Paths.DefaultModelsDir), warnings);
            Assert.True(File.Exists(bundled)); // the file that came with Dictum is still there
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias); // the link only, never what it points to
        }
    }

    /// <summary>
    /// Pin (re-lens probe R9, inverted): the bundled folder by its volume name (\\?\Volume{GUID}\...) can not be the chosen folder. No
    /// comparison took that name for the bundled folder, so models.folder was accepted, --print-models-dir printed the bundled
    /// folder, Storage listed its base.en with a Delete, and ModelFiles.Delete deleted it. The settings load refuses the value
    /// with the usual warning, and the per-user folder is used.
    /// </summary>
    [Fact]
    public void The_bundled_folder_by_its_volume_name_is_refused_as_the_chosen_folder_and_nothing_in_it_is_deleted()
    {
        using var scratch = new Scratch();
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", 10);
        var alias = ByVolumeName(scratch.Paths.BundledModelsDir) ?? "";
        if (alias.Length == 0) Assert.Skip(NoVolumeName);
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { models = new { folder = alias } }));

        var load = SettingsLoader.LoadFile(scratch.Paths.SettingsFile);
        var warning = Assert.Single(load.Warnings);
        Assert.Contains("models.folder", warning, StringComparison.Ordinal);
        Assert.Contains("expected an absolute folder path", warning, StringComparison.Ordinal);
        Assert.Null(load.Settings.Models.Folder);
        Assert.Equal(scratch.Paths.DefaultModelsDir, scratch.Paths.ModelsDir(load.Settings)); // no download lands in the bundled folder
        using var printed = new StringWriter();
        Assert.Equal(0, CliCommands.PrintModelsDir(scratch.Paths, printed, TextWriter.Null));
        Assert.Equal(scratch.Paths.DefaultModelsDir, printed.ToString().Trim()); // not the bundled folder
        Assert.DoesNotContain(ModelFiles.List(scratch.Paths, load.Settings), m => m.Name == "base.en"); // Storage offers no Delete
        foreach (var scope in new[] { RemovalScope.AllData, RemovalScope.Everything })
            Assert.DoesNotContain(RemovalPlan.Build(scratch.Paths, load.Settings, scope).Items, i => i.Path.EndsWith("ggml-base.en.bin", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(bundled)); // the file that came with Dictum is still there
    }

    /// <summary>
    /// Pin (re-lens): models.folder and its earlier name whisper.models_dir take a device path only as \\?\X:\ or \\?\UNC\, the two
    /// forms PathRegistry.FinalPath unwraps; every other (a volume or GLOBALROOT name, \\.\, \??\, either slash, a bare
    /// drive) is refused with the usual warning and the default folder is used.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\C:\Dictum models", true)]
    [InlineData(@"\\?\c:\Dictum models", true)]
    [InlineData(@"\\?\UNC\fileserver\dictum\models", true)]
    [InlineData(@"\\?\unc\fileserver\dictum\models", true)]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Dictum models", false)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Dictum models", false)]
    [InlineData(@"\\.\C:\Dictum models", false)]
    [InlineData(@"\\.\Volume{00000000-0000-0000-0000-000000000000}\Dictum models", false)]
    [InlineData(@"\??\C:\Dictum models", false)]
    [InlineData("//?/C:/Dictum models", false)]
    [InlineData("//?/Volume{00000000-0000-0000-0000-000000000000}/Dictum models", false)]
    [InlineData(@"\\?\C:", false)]
    public void A_device_path_is_taken_as_the_models_folder_only_as_a_drive_or_a_share(string folder, bool taken)
    {
        var load = SettingsLoader.Parse(JsonSerializer.Serialize(new { models = new { folder } }));
        var earlier = SettingsLoader.Parse(JsonSerializer.Serialize(new { whisper = new { models_dir = folder } }));
        Assert.Equal(taken ? folder : null, load.Settings.Models.Folder);
        Assert.Equal(taken ? folder : null, earlier.Settings.Whisper.ModelsDir);
        Assert.Equal(taken ? 0 : 1, load.Warnings.Count);
        Assert.Equal(taken ? 0 : 1, earlier.Warnings.Count);
    }

    /// <summary>
    /// Pin, the backstop: with the per-user models folder itself a junction to the bundled folder (which no setting refuses), the
    /// bundled file is listed as the per-user folder's, yet Storage's Delete and "Delete all app data" never reach it.
    /// </summary>
    [Fact]
    public void The_backstop_never_deletes_a_bundled_file_reached_through_the_per_user_folder()
    {
        using var scratch = new Scratch();
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", 10);
        Directory.CreateDirectory(scratch.Paths.LocalDir);
        try
        {
            if (!Junction(scratch.Paths.DefaultModelsDir, scratch.Paths.BundledModelsDir)) Assert.Skip(NoJunction);
            var settings = new AppSettings();
            var listed = ModelFiles.List(scratch.Paths, settings).Single(m => m.Name == "base.en");
            Assert.Equal(ModelFolderKind.Default, listed.Folder); // so Storage offers its Delete
            var refused = ModelFiles.Delete(listed.Path, scratch.Paths, settings, [], new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance));
            Assert.False(refused.Deleted);
            Assert.True(File.Exists(bundled)); // the file that came with Dictum is still there
            Assert.Equal(ModelFiles.BundledNotDeleted("ggml-base.en.bin"), refused.Message);
            Assert.DoesNotContain(RemovalPlan.Build(scratch.Paths, settings, RemovalScope.AllData).Items, i => i.Path.EndsWith("ggml-base.en.bin", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(scratch.Paths.DefaultModelsDir)) Directory.Delete(scratch.Paths.DefaultModelsDir); // the link only
        }
    }

    /// <summary>
    /// Pin (the lens's P5, added as it wrote it): "Delete all app data" and "Remove Dictum completely", packaged and not, never
    /// list or touch the bundled folder, whether no folder, the bundled folder or a folder inside it is chosen.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("bundled", false)]
    [InlineData("inside", false)]
    [InlineData(null, true)]
    [InlineData("bundled", true)]
    public Task Deletions_never_reach_the_bundled_folder(string? chosen, bool packaged) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var folder = chosen switch
        {
            "bundled" => scratch.Paths.BundledModelsDir,
            "inside" => Path.Combine(scratch.Paths.BundledModelsDir, "more"),
            _ => null,
        };
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", models = new { folder } }));
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", 10);
        var inside = Create(Path.Combine(scratch.Paths.BundledModelsDir, "more"), "ggml-tiny.en.bin", 10);
        Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", 10);
        var (shell, platform, dialogs) = scratch.Shell();
        platform.FakePackage.IsPackaged = packaged;
        shell.Reload(userRequested: false);
        foreach (var scope in new[] { RemovalScope.AllData, RemovalScope.AllDataIncludingSettings, RemovalScope.Everything })
        {
            var plan = RemovalPlan.Build(scratch.Paths, shell.Settings, scope, platform.Removal.PlatformLocations());
            Assert.DoesNotContain(plan.Items, i => scratch.Paths.IsBundled(i.Path) || PathRegistry.SamePath(i.Path, scratch.Paths.BundledModelsDir));
        }
        dialogs.Answers.Enqueue(0);
        Assert.DoesNotContain("could not be deleted", shell.DeleteData(RemovalScope.AllData));
        Assert.True(File.Exists(bundled) && File.Exists(inside));
        dialogs.Answers.Enqueue(0);
        dialogs.ConfirmWord = true;
        Assert.True(shell.RemoveEverything());
        Assert.DoesNotContain(scratch.Paths.BundledModelsDir, dialogs.Asked.First(a => a.Title == "Remove Dictum completely").Text);
        Assert.Empty(dialogs.Told); // no "Done, except"
        Assert.True(File.Exists(bundled) && File.Exists(inside));
    });

    // ---- 5. The advice for a damaged file ----

    /// <summary>
    /// Pin (lens probe P3, inverted): a damaged file in the chosen folder or in the per-user folder (while another is chosen) is
    /// Dictum's to delete and download again; one that came with Dictum has its own advice; one chosen from elsewhere is renamed.
    /// </summary>
    [Fact]
    public void Every_model_folder_gets_its_own_advice_for_a_damaged_file()
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "chosen");
        var settings = new AppSettings { Models = new ModelsSettings { Folder = chosen } };
        var integrity = new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance, () => ModelFolders.LookupOrder(scratch.Paths, settings));
        string Advice(string folder) => Assert.Throws<ModelIntegrityException>(() => integrity.Verify(Create(folder, "ggml-base.en.bin", 1000))).Message;

        Assert.EndsWith("it was not loaded. Delete it and download it again.", Advice(chosen), StringComparison.Ordinal);
        Assert.EndsWith("it was not loaded. Delete it and download it again.", Advice(scratch.Paths.DefaultModelsDir), StringComparison.Ordinal);
        Assert.EndsWith("it was not loaded. It came with Dictum: reinstall Dictum, or download base.en in Settings > Models.", Advice(scratch.Paths.BundledModelsDir), StringComparison.Ordinal);
        Assert.EndsWith("Rename it, then choose it again.", Advice(Path.Combine(scratch.Root.FullName, "picked")), StringComparison.Ordinal);
    }

    // ---- 6. One lookup everywhere ----

    /// <summary>Pin (lens probe P4, inverted): calibration passes over a bundled base.en that fails its SHA-256, as the tray app does, and offers it as a download.</summary>
    [Fact]
    public Task Calibration_passes_over_a_bundled_base_en_that_fails_its_check_as_the_tray_does() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" }, "llm": { "provider": "none" } }""");
        Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes); // zeros: the SHA-256 fails
        var (shell, _, _) = scratch.Shell(FakeModels(), downloadHandler: new CountingHandler());
        shell.Reload(userRequested: false);
        Assert.False(shell.Models.IsDownloaded(ModelCatalog.Find("base.en")!));
        var calibration = shell.CreateCalibrationHost();
        Assert.DoesNotContain(calibration.InstalledModels(), c => c.Name == "base.en");
        Assert.Contains(calibration.MissingModels(), m => m.Name == "base.en");
        shell.Dispose();
    });

    /// <summary>Pin: "--download-model base.en" finds the bundled copy through the app's lookup and downloads nothing.</summary>
    [Fact]
    public async Task Download_model_base_en_with_the_bundled_copy_present_downloads_nothing()
    {
        using var scratch = new Scratch();
        var bundled = Create(scratch.Paths.BundledModelsDir, "ggml-base.en.bin", BaseEnBytes);
        new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled); // as if hashed once already
        var server = new CountingHandler();
        var options = CliOptions.Parse(["--download-model", "base.en"]).Options!;
        Assert.Equal(0, await DownloadCommand.RunAsync(options, scratch.Paths, NullLog.Instance, new HttpClient(server)));
        Assert.Equal(0, server.Requests);
        Assert.False(Directory.Exists(scratch.Paths.DefaultModelsDir) && Directory.EnumerateFiles(scratch.Paths.DefaultModelsDir).Any()); // no second copy, not even a .part
    }

    /// <summary>
    /// Pin: --bench-cleanup finds a clean-up model through the app's lookup (chosen, per-user, bundled), not in the download
    /// folder alone: with another folder chosen, a model in the per-user folder is run (here its SHA-256 fails, which is logged)
    /// instead of being skipped as "not downloaded".
    /// </summary>
    [Fact]
    public async Task Bench_cleanup_looks_in_every_model_folder_not_only_the_download_folder()
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "E-drive models");
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { llm = new { provider = "local", model = "qwen2.5-1.5b" }, models = new { folder = chosen } }));
        var small = CleanupCatalog.Find("qwen2.5-0.5b")!;
        Create(scratch.Paths.DefaultModelsDir, small.FileName, small.Bytes); // zeros: found by its size, then refused by its SHA-256
        var input = Path.Combine(scratch.Root.FullName, "input.txt");
        File.WriteAllText(input, "um so this is a short test");
        var log = new RecordingLog();
        var options = CliOptions.Parse(["--bench-cleanup", input]).Options!;
        Assert.Equal(0, await BenchCleanupCommand.RunAsync(options, scratch.Paths, log, new SecretRedactor()));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.StartsWith(small.FileName + ": SHA-256 ", StringComparison.Ordinal));
    }

    // ---- 8. Settings > Models' lookups ----

    /// <summary>
    /// Pin: Settings > Models looks the catalogue up again only after a settings load, a download that ended, a deletion, or when
    /// it is shown again; a download's progress ticks (every open page redraws on each) make no lookup, with a model loaded not
    /// even the model in use's own (re-lens F4: the language note used to resolve it through the folders on every tick). The
    /// coordinator counts every way it reaches the folders, ModelPath included.
    /// </summary>
    [Fact]
    public async Task The_models_page_looks_the_catalogue_up_only_after_a_load_a_download_or_a_deletion()
    {
        var everyModel = ModelCatalog.Entries.Count + CleanupCatalog.Entries.Count; // the page's lookups when it looks again
        var steps = await UiSession.RunAsync(async () =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
            Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", BaseEnBytes);
            Create(scratch.Paths.DefaultModelsDir, "ggml-small.en.bin", 10); // a model to delete
            var models = FakeModels();
            var (shell, _, dialogs) = scratch.Shell(models, downloadHandler: new CountingHandler());
            shell.Reload(userRequested: false);
            Assert.NotNull(models.ModelPath); // a model is loaded: a redraw reads it from memory
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Models");
            var lookups = shell.Models.Lookups;
            var seen = new List<(string Step, int Lookups)>();
            void Step(string name)
            {
                seen.Add((name, shell.Models.Lookups - lookups));
                lookups = shell.Models.Lookups;
            }

            for (var i = 0; i < 5; i++) shell.StatusChanged(); // what each progress tick of a download does: every open page redraws
            Step("ticks");
            shell.Reload(userRequested: false);
            Step("settings load");
            dialogs.Answers.Enqueue(0);
            await shell.DeleteModelAsync(ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "small.en"));
            Step("deletion");
            dialogs.ConfirmDownloads = true;
            shell.Models.Select("tiny.en"); // asked (Select looks it up once itself), then fetched from a server that refuses it
            Assert.True(await Eventually(() => shell.Models.DownloadsEnded == 1));
            Step("download ended");
            window.ShowPage("Storage");
            lookups = shell.Models.Lookups;
            window.ShowPage("Models");
            Step("shown again");
            window.Close();
            shell.Dispose();
            return seen;
        });
        Assert.Equal(("ticks", 0), steps[0]); // a progress tick makes no lookup
        Assert.Equal(["settings load", "deletion", "download ended", "shown again"], steps.Skip(1).Select(s => s.Step));
        Assert.All(steps.Skip(1), s => Assert.True(s.Lookups >= everyModel, $"{s.Step}: {s.Lookups} lookups, not every model's {everyModel}"));
    }
}
