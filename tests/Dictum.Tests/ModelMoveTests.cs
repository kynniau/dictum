using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// 1.3 move-on-change (owner approval 22/10/2026): changing the model folder offers to move the model files Dictum knows;
/// each file is copied under a temporary name, checked against the catalogue's pinned SHA-256 (a .part unchecked), renamed,
/// and only then is the original deleted; the engines are released first; a running download or nested folders refuse the
/// move. Model files are made with SetLength (zeros), so a fake hasher stands in for the real SHA-256.
/// </summary>
public sealed class ModelMoveTests : IDisposable
{
    internal static readonly long TinyEnBytes = ModelCatalog.Find("tiny.en")!.Bytes;
    internal const string Settings = """{ "hotkey": "F24", "whisper": { "model": "tiny.en" }, "llm": { "provider": "none" } }""";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-model-move-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string Create(string folder, string fileName, long bytes = 10, byte fill = 0)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        if (fill == 0) file.SetLength(bytes);
        else file.Write(Enumerable.Repeat(fill, (int)bytes).ToArray());
        return path;
    }

    /// <summary>The catalogue's pinned SHA-256 for every file, so a copy passes; a file named in <paramref name="corrupt"/> gets another hash.</summary>
    internal static Func<string, CancellationToken, Task<string>> FakeHasher(params string[] corrupt) => (path, _) =>
    {
        var name = Path.GetFileName(path);
        var pinned = ModelIntegrity.Expected(name.EndsWith(ModelMover.TempSuffix, StringComparison.Ordinal) ? name[..^ModelMover.TempSuffix.Length] : name)!.Sha256!;
        return Task.FromResult(corrupt.Any(c => name.StartsWith(c, StringComparison.Ordinal)) ? new string('0', 64) : pinned);
    };

    // ---- The mover ----

    [Fact]
    public void Known_lists_the_catalogue_files_and_their_part_downloads_in_the_folder_itself()
    {
        var folder = Path.Combine(_dir.FullName, "old");
        Create(folder, "ggml-small.en.bin");
        Create(folder, "ggml-tiny.en.bin.part");
        Create(folder, "qwen2.5-1.5b-instruct-q4_k_m.gguf");
        Create(folder, "notes.txt");
        Create(folder, "ggml-base.en.bin.move"); // a copy left by an interrupted move is not a model file
        Create(Path.Combine(folder, "inner"), "ggml-base.en.bin"); // not in the folder itself
        Assert.Equal(["ggml-small.en.bin", "ggml-tiny.en.bin.part", "qwen2.5-1.5b-instruct-q4_k_m.gguf"], ModelMover.Known(folder).Select(f => f.FileName));
        Assert.True(ModelMover.Known(folder).Single(f => f.FileName.EndsWith(".part", StringComparison.Ordinal)).Partial);
        Assert.Empty(ModelMover.Known(Path.Combine(_dir.FullName, "missing")));
    }

    /// <summary>
    /// Pin: per file, the copy is checked while the original is still there and before the copy has its final name; a copy
    /// that fails the SHA-256 check is deleted and its original survives; files before it stay moved (no rollback) and the
    /// move goes on with the next file (lens on f5ec71c); the verified record follows the moved files.
    /// </summary>
    [Fact]
    public async Task Each_file_is_copied_checked_renamed_then_deleted_and_a_failed_check_keeps_the_original()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var baseEn = Create(from, "ggml-base.en.bin", fill: 1);
        var small = Create(from, "ggml-small.en.bin", fill: 2);
        var tiny = Create(from, "ggml-tiny.en.bin", fill: 3);
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "verified-models.json"), NullLog.Instance);
        var observed = new List<(string Name, bool OriginalThere, bool FinalThere)>();
        var hasher = FakeHasher("ggml-small.en.bin");
        var steps = new List<MoveStep>();

        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, integrity, new ListProgress(steps), (path, ct) =>
        {
            var name = Path.GetFileName(path)[..^ModelMover.TempSuffix.Length];
            observed.Add((name, File.Exists(Path.Combine(from, name)), File.Exists(Path.Combine(to, name))));
            return hasher(path, ct);
        }, cancellationToken: Ct);

        // Each copy is checked while its original is still there and before it has its final name.
        Assert.Equal([("ggml-base.en.bin", true, false), ("ggml-small.en.bin", true, false), ("ggml-tiny.en.bin", true, false)], observed);
        Assert.Equal((2, 3, false), (result.Moved, result.Count, result.Cancelled));
        Assert.Equal([new MoveProblem("ggml-small.en.bin", ModelMover.CopyDoesNotMatch)], result.Problems);
        Assert.Equal([small], result.LeftBehind);
        Assert.Equal([new MoveStep(1, 3, "ggml-base.en.bin"), new MoveStep(2, 3, "ggml-small.en.bin"), new MoveStep(3, 3, "ggml-tiny.en.bin")], steps);
        Assert.False(File.Exists(baseEn)); // moved
        Assert.Equal(Enumerable.Repeat((byte)1, 10), File.ReadAllBytes(Path.Combine(to, "ggml-base.en.bin")));
        Assert.Equal(Enumerable.Repeat((byte)2, 10), File.ReadAllBytes(small)); // the original survives its failed copy
        Assert.False(File.Exists(Path.Combine(to, "ggml-small.en.bin")));
        Assert.False(File.Exists(Path.Combine(to, "ggml-small.en.bin" + ModelMover.TempSuffix))); // and its copy is gone
        Assert.False(File.Exists(tiny)); // the move went on with the next file
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(Path.Combine(to, "ggml-tiny.en.bin")));
        var record = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(_dir.FullName, "verified-models.json")))!;
        Assert.Equal([Path.Combine(to, "ggml-base.en.bin"), Path.Combine(to, "ggml-tiny.en.bin")], record.Keys.Order()); // the checked copies; nothing under the old folder
    }

    /// <summary>
    /// Pin (lens on f5ec71c): the rename comes before the delete. The hasher puts a file under the final name after the
    /// existence check, so the rename fails: the original survives byte for byte, no .move is left, and the reason is reported
    /// without a closing full stop.
    /// </summary>
    [Fact]
    public async Task A_rename_that_fails_keeps_the_original_and_leaves_no_copy()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-tiny.en.bin", fill: 4);
        var hasher = FakeHasher();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: (path, ct) =>
        {
            Create(to, "ggml-tiny.en.bin", fill: 9); // appears under the final name between the check and the rename
            return hasher(path, ct);
        }, cancellationToken: Ct);
        Assert.Equal(0, result.Moved);
        Assert.Equal([original], result.LeftBehind);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("ggml-tiny.en.bin", problem.FileName);
        Assert.False(string.IsNullOrWhiteSpace(problem.Reason));
        Assert.False(problem.Reason.EndsWith('.'));
        Assert.Equal(Enumerable.Repeat((byte)4, 10), File.ReadAllBytes(original));
        Assert.False(File.Exists(Path.Combine(to, "ggml-tiny.en.bin" + ModelMover.TempSuffix)));
    }

    /// <summary>Pin (lens on f5ec71c): a copy of the wrong size is deleted and its original kept.</summary>
    [Fact]
    public async Task A_copy_of_the_wrong_size_is_deleted_and_the_original_kept()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-tiny.en.bin", fill: 4);
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: FakeHasher(),
            copier: (_, target, _) => File.WriteAllBytesAsync(target, [1, 2, 3], Ct), cancellationToken: Ct);
        Assert.Equal([new MoveProblem("ggml-tiny.en.bin", ModelMover.CopySizeDiffers)], result.Problems);
        Assert.Equal(Enumerable.Repeat((byte)4, 10), File.ReadAllBytes(original));
        Assert.False(File.Exists(Path.Combine(to, "ggml-tiny.en.bin" + ModelMover.TempSuffix)));
        Assert.False(File.Exists(Path.Combine(to, "ggml-tiny.en.bin")));
    }

    /// <summary>Pin (lens on f5ec71c): the models in use are moved last, whatever their names.</summary>
    [Fact]
    public async Task The_models_in_use_move_last()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var baseEn = Create(from, "ggml-base.en.bin");
        Create(from, "ggml-small.en.bin");
        Create(from, "ggml-tiny.en.bin");
        var steps = new List<MoveStep>();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, progress: new ListProgress(steps), hasher: FakeHasher(), inUse: [baseEn], cancellationToken: Ct);
        Assert.True(result.Complete);
        Assert.Equal(["ggml-small.en.bin", "ggml-tiny.en.bin", "ggml-base.en.bin"], steps.Select(s => s.FileName));
    }

    /// <summary>
    /// Pin (lens on f5ec71c): a file of the same name already in the new folder counts as moved when it is the same file (size
    /// and pinned SHA-256: the original is deleted and the record follows), and is skipped otherwise, the move going on.
    /// </summary>
    [Fact]
    public async Task A_name_already_in_the_new_folder_counts_as_moved_when_it_is_the_same_file_and_is_skipped_otherwise()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var baseEn = Create(from, "ggml-base.en.bin", fill: 1);
        var small = Create(from, "ggml-small.en.bin", fill: 2);
        var tiny = Create(from, "ggml-tiny.en.bin", fill: 3);
        var medium = Create(from, "ggml-medium.en.bin", fill: 5);
        var sameThere = Create(to, "ggml-base.en.bin", fill: 1);
        var otherSize = Create(to, "ggml-small.en.bin", 11, fill: 8);
        var otherHash = Create(to, "ggml-tiny.en.bin", fill: 6); // the same size, another SHA-256
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "verified-models.json"), NullLog.Instance);
        var hasher = FakeHasher();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, integrity,
            hasher: (path, ct) => PathRegistry.SamePath(path, otherHash) ? Task.FromResult(new string('0', 64)) : hasher(path, ct), cancellationToken: Ct);
        Assert.Equal((2, 4), (result.Moved, result.Count)); // base.en (already there) and medium.en (copied)
        Assert.Equal([new MoveProblem("ggml-small.en.bin", ModelMover.AlreadyThere), new MoveProblem("ggml-tiny.en.bin", ModelMover.AlreadyThere)], result.Problems);
        Assert.Equal([small, tiny], result.LeftBehind);
        Assert.False(File.Exists(baseEn));
        Assert.True(File.Exists(sameThere));
        Assert.Equal(Enumerable.Repeat((byte)2, 10), File.ReadAllBytes(small));
        Assert.Equal(Enumerable.Repeat((byte)8, 11), File.ReadAllBytes(otherSize));
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(tiny));
        Assert.Equal(Enumerable.Repeat((byte)6, 10), File.ReadAllBytes(otherHash));
        Assert.False(File.Exists(medium));
        var record = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(_dir.FullName, "verified-models.json")))!;
        Assert.Contains(sameThere, record.Keys);
    }

    /// <summary>Pin (lens on f5ec71c): an original that cannot be removed after its checked copy is in place counts as moved, with the reason.</summary>
    [Fact]
    public async Task An_original_that_cannot_be_removed_counts_as_moved_and_is_reported()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-tiny.en.bin", fill: 4);
        MoveResult result;
        using (new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read)) // read allowed, delete refused
            result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: FakeHasher(), cancellationToken: Ct);
        Assert.Equal(1, result.Moved);
        Assert.Equal([new MoveProblem("ggml-tiny.en.bin", ModelMover.OriginalKept, Copied: true)], result.Problems);
        Assert.Empty(result.LeftBehind);
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(Path.Combine(to, "ggml-tiny.en.bin")));
    }

    /// <summary>Pin: cancelling (quit) lets a checked copy finish its rename and delete, and starts no other file.</summary>
    [Fact]
    public async Task Cancelling_finishes_the_checked_file_and_starts_no_other()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var baseEn = Create(from, "ggml-base.en.bin", fill: 1);
        var tiny = Create(from, "ggml-tiny.en.bin", fill: 3);
        using var cancel = new CancellationTokenSource();
        var hasher = FakeHasher();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: (path, ct) =>
        {
            cancel.Cancel(); // quit asked while the first copy is checked
            return hasher(path, ct);
        }, cancellationToken: cancel.Token);
        Assert.Equal((1, true), (result.Moved, result.Cancelled));
        Assert.Equal([tiny], result.LeftBehind);
        Assert.False(File.Exists(baseEn));
        Assert.True(File.Exists(Path.Combine(to, "ggml-base.en.bin")));
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(tiny));
        Assert.False(File.Exists(Path.Combine(to, "ggml-tiny.en.bin" + ModelMover.TempSuffix)));
    }

    [Fact]
    public void Display_names_are_the_catalogue_names()
    {
        Assert.Equal("small.en", ModelMover.DisplayName("ggml-small.en.bin"));
        Assert.Equal("qwen2.5-1.5b", ModelMover.DisplayName("qwen2.5-1.5b-instruct-q4_k_m.gguf"));
        Assert.Equal("unfinished small.en download", ModelMover.DisplayName("ggml-small.en.bin.part"));
    }

    [Fact]
    public async Task A_part_file_moves_without_a_hash_check()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var part = Create(from, "ggml-small.en.bin.part", fill: 7);
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: (_, _) => throw new InvalidOperationException("a .part is never hashed"),
            cancellationToken: Ct);
        Assert.True(result.Complete);
        Assert.False(File.Exists(part));
        Assert.Equal(Enumerable.Repeat((byte)7, 10), File.ReadAllBytes(Path.Combine(to, "ggml-small.en.bin.part")));
    }

    [Theory]
    [InlineData(@"C:\models", @"C:\models\inner", true)]
    [InlineData(@"C:\models\inner", @"C:\models", true)]
    [InlineData(@"C:\models", @"C:\models-new", false)]
    [InlineData(@"C:\models", @"D:\models", false)]
    [InlineData(@"D:\", @"D:\Models", true)] // a drive root (lens on PR #61)
    [InlineData(@"D:\Models", @"D:\", true)]
    public void Nested_folders_are_one_inside_the_other(string from, string to, bool nested) => Assert.Equal(nested, ModelMover.Nested(from, to));

    /// <summary>Pin (lens on PR #61): a folder is inside a drive root; a root is not inside itself.</summary>
    [Fact]
    public void A_folder_is_inside_its_drive_root()
    {
        Assert.True(PathRegistry.IsInside(@"D:\Models", @"D:\"));
        Assert.True(PathRegistry.IsInside(@"D:\Models\ggml-tiny.en.bin", @"D:\"));
        Assert.False(PathRegistry.IsInside(@"D:\", @"D:\Models"));
        Assert.False(PathRegistry.IsInside(@"D:\", @"D:\"));
        Assert.False(PathRegistry.IsInside(@"D:\Models", @"D:\Models"));
        Assert.False(PathRegistry.IsInside(@"D:\Models-new", @"D:\Models"));
    }

    // ---- The release seam ----

    /// <summary>
    /// Pin (the seam the move relies on): a loaded clean-up model's file cannot be moved while its engine holds it (llama.cpp maps
    /// the .gguf; the fake holds the file open the same way); after LocalLlmManager.ReleaseAsync it can, and ReleaseAsync waits
    /// for a load still under way, which then releases its engine.
    /// </summary>
    [Fact]
    public async Task ReleaseAsync_closes_the_clean_up_models_file_and_waits_for_a_load_under_way()
    {
        var path = Create(Path.Combine(_dir.FullName, "old"), "qwen2.5-1.5b-instruct-q4_k_m.gguf");
        var gate = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var llm = new LocalLlmManager(NullLog.Instance, loader: (p, _) =>
        {
            var engine = new HoldingLocalEngine(p);
            started.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return engine;
        }, gpuInUse: () => false);

        gate.Set();
        llm.BeginLoad(path, "cpu");
        Assert.True(await Eventually(() => llm.Ready is not null));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved")); // held while loaded
        await llm.ReleaseAsync();
        File.Move(path, path + ".moved"); // released
        File.Move(path + ".moved", path);

        gate.Reset();
        started.Reset();
        llm.BeginLoad(path, "cpu");
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct)); // the load holds the file and has not finished
        var release = llm.ReleaseAsync();
        await Task.Delay(50, Ct);
        Assert.False(release.IsCompleted); // waits for the load under way
        gate.Set();
        await release.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        File.Move(path, path + ".moved"); // which released its engine as stale
        llm.Dispose();
    }

    [Fact]
    public async Task ReleaseAsync_closes_the_speech_models_file()
    {
        var path = Create(Path.Combine(_dir.FullName, "old"), "ggml-tiny.en.bin");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new HoldingSpeechEngine(p, d), gpuInUse: () => false);
        models.BeginLoad(path, new WhisperSettings { Gpu = "cpu" });
        Assert.True(await Eventually(() => models.IsReady));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
        await models.ReleaseAsync();
        File.Move(path, path + ".moved");
        models.Dispose();
    }

    // ---- Settings: the offer, the refusals, the move ----

    [Fact]
    public Task No_offer_when_the_old_folder_holds_no_model_files() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        Create(scratch.Paths.DefaultModelsDir, "notes.txt");
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        await ModelFolderActions.BrowseFolder(shell, _ => { });
        Assert.Empty(dialogs.Asked);
        Assert.Equal(chosen, shell.Settings.Models.Folder); // written as before
        shell.Dispose();
        return true;
    });

    /// <summary>Pin: the offer names both folders, the count and the size; "Leave them where they are" writes the folder and moves nothing; closing the question changes nothing.</summary>
    [Fact]
    public Task Leaving_the_files_changes_the_folder_and_moves_nothing_and_closing_changes_nothing() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-small.en.bin.part", 1000);
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;

        await ModelFolderActions.BrowseFolder(shell, _ => { }); // no answer queued: the question is closed
        Assert.Null(shell.Settings.Models.Folder);

        dialogs.Answers.Enqueue(1);
        await ModelFolderActions.BrowseFolder(shell, _ => { });
        Assert.Equal(("Move your model files?",
            $"Your model files are in {scratch.Paths.DefaultModelsDir}. Move them to {chosen} so Dictum finds them there? 2 files, " +
            $"{ModelCatalog.FormatSize(TinyEnBytes + 1000)}. Dictum copies each file, checks it, then removes the original."), dialogs.Asked[^1]);
        Assert.Equal(["Move the files", "Leave them where they are"], dialogs.AskedChoices[^1]);
        Assert.Equal(1, dialogs.AskedDefault[^1]); // Leave is the default: Enter never starts a long move (orchestrator, lens on PR #61)
        Assert.Equal(chosen, shell.Settings.Models.Folder);
        Assert.True(File.Exists(tiny)); // nothing moved
        Assert.False(Directory.Exists(chosen) && Directory.EnumerateFiles(chosen).Any());
        shell.Dispose();
        return true;
    });

    /// <summary>
    /// Pin, end to end through Settings > Storage > Change…: the engines are released (the fake speech engine holds its file open,
    /// as a mapped clean-up model would), every file moves, the folder is written, and the model loads from the new folder,
    /// which the lookup now searches first; the page shows the result line.
    /// </summary>
    [Fact]
    public Task Moving_releases_the_models_moves_the_files_and_loads_them_from_the_new_folder() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        var part = Create(scratch.Paths.DefaultModelsDir, "ggml-small.en.bin.part", 1000);
        var speech = SpeechModels();
        var (shell, _, dialogs) = scratch.Shell(speech);
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => shell.Models.ModelPath == tiny && speech.IsReady));
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Storage");

        var said = new List<string>();
        await ModelFolderActions.BrowseFolder(shell, said.Add);

        // The progress lines are posted to the page as they come; one still queued at the end is dropped, never shown over the result.
        Assert.Equal($"Moved 2 files to {chosen}.", said[^1]);
        Assert.All(said[..^1], s => Assert.Contains(s, new[] { "Moving unfinished small.en download (1 of 2)…", "Moving tiny.en (2 of 2)…" }));
        Assert.False(File.Exists(tiny));
        Assert.False(File.Exists(part));
        var moved = Path.Combine(chosen, "ggml-tiny.en.bin");
        Assert.True(File.Exists(moved));
        Assert.True(File.Exists(Path.Combine(chosen, "ggml-small.en.bin.part")));
        Assert.Equal(chosen, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        Assert.Equal(moved, shell.Models.ModelPath); // loaded from the new folder
        Assert.Equal(ModelFolderKind.Chosen, ModelFolders.LookupOrder(scratch.Paths, shell.Settings)[0].Kind);
        Assert.Contains(ModelFiles.List(scratch.Paths, shell.Settings), m => m.Path == moved && m.Folder == ModelFolderKind.Chosen);

        // Through the page's own button, back to the default folder: the result line is on the Storage page.
        dialogs.Folder = scratch.Paths.DefaultModelsDir;
        dialogs.Answers.Enqueue(0);
        Click(All<Button>(window).First(b => b.Content as string == StoragePage.ChangeLabel));
        Assert.True(await Eventually(() => Texts(window).Contains($"Moved 2 files to {scratch.Paths.DefaultModelsDir}.", StringComparison.Ordinal)));
        Assert.True(File.Exists(tiny));
        window.Close();
        shell.Dispose();
        return true;
    });

    /// <summary>
    /// Pin (owner decision 22/10/2026 21:45 AEDT): Settings > Storage > Use default asks the same question and moves the files
    /// back to the per-user default folder; models.folder is cleared and the model loads from the default folder.
    /// </summary>
    [Fact]
    public Task Use_default_offers_the_move_and_moves_the_files_back_to_the_default_folder() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", whisper = new { model = "tiny.en" }, llm = new { provider = "none" }, models = new { folder = chosen } }));
        var inChosen = Create(chosen, "ggml-tiny.en.bin", TinyEnBytes);
        var speech = SpeechModels();
        var (shell, _, dialogs) = scratch.Shell(speech);
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => shell.Models.ModelPath == inChosen && speech.IsReady));
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Storage");
        dialogs.Answers.Enqueue(0);

        Click(All<Button>(window).First(b => b.Content as string == ModelsPage.UseDefaultLabel));

        var inDefault = Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin");
        Assert.True(await Eventually(() => Texts(window).Contains($"Moved 1 file to {scratch.Paths.DefaultModelsDir}.", StringComparison.Ordinal)));
        Assert.Equal(ModelFolderActions.MoveTitle, Assert.Single(dialogs.Asked).Title);
        Assert.StartsWith($"Your model files are in {chosen}. Move them to {scratch.Paths.DefaultModelsDir} so Dictum finds them there? 1 file, ", dialogs.Asked[0].Text);
        Assert.False(File.Exists(inChosen));
        Assert.True(File.Exists(inDefault));
        Assert.Null(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        Assert.Equal(inDefault, shell.Models.ModelPath);
        window.Close();
        shell.Dispose();
        return true;
    });

    /// <summary>Pin: "Move the files" while a download runs is refused, and nothing changes: the folder, the files, the download.</summary>
    [Fact]
    public Task Moving_is_refused_while_a_download_runs() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        var server = new GateHandler([1, 2, 3, 4]);
        var (shell, _, dialogs) = scratch.Shell(SpeechModels(), downloadHandler: server);
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        dialogs.ConfirmDownloads = true;
        shell.Models.Select("small.en");
        Assert.True(await Eventually(() => server.InFlight == 1), "small.en never started downloading");
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        await ModelFolderActions.BrowseFolder(shell, said.Add);
        Assert.Equal(["A download is running; wait for it to finish, then change the folder again to move the files."], said);
        Assert.Null(shell.Settings.Models.Folder);
        Assert.True(File.Exists(tiny));
        Assert.False(Directory.Exists(chosen));
        Assert.True(shell.Models.IsDownloading);
        shell.Dispose();
        return true;
    });

    [Fact]
    public Task Moving_is_refused_when_one_folder_is_inside_the_other() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        var inner = Path.Combine(scratch.Paths.DefaultModelsDir, "inner");
        dialogs.Folder = inner;
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        await ModelFolderActions.BrowseFolder(shell, said.Add);
        Assert.Equal([ModelFolderActions.NestedRefused], said);
        Assert.Null(shell.Settings.Models.Folder);
        Assert.True(File.Exists(tiny));
        shell.Dispose();
        return true;
    });

    /// <summary>
    /// Pin, the lens's reproduction (f5ec71c): the chosen folder holds base.en and tiny.en (in use) and the default folder
    /// already holds the same base.en. Use default, Move the files: base.en counts as moved (the same file is there), tiny.en
    /// moves last, the folder is cleared, and the model loads from the default folder.
    /// </summary>
    [Fact]
    public Task Use_default_with_the_same_file_already_in_the_default_folder_moves_everything() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", whisper = new { model = "tiny.en" }, llm = new { provider = "none" }, models = new { folder = chosen } }));
        var baseChosen = Create(chosen, "ggml-base.en.bin", fill: 1);
        var tinyChosen = Create(chosen, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", fill: 1);
        var speech = SpeechModels();
        var (shell, _, dialogs) = scratch.Shell(speech);
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => shell.Models.ModelPath == tinyChosen && speech.IsReady));
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        await ModelFolderActions.UseDefaultFolder(shell, said.Add);
        Assert.Equal($"Moved 2 files to {scratch.Paths.DefaultModelsDir}.", said[^1]);
        Assert.False(File.Exists(baseChosen));
        Assert.False(File.Exists(tinyChosen));
        var tinyDefault = Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin");
        Assert.Null(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        Assert.Equal(tinyDefault, shell.Models.ModelPath);
        Assert.True(File.Exists(tinyDefault));
        shell.Dispose();
        return true;
    });

    /// <summary>
    /// Pin (lens on f5ec71c): when the model in use cannot move (a different file of its name is in the new folder), the
    /// other files move, the folder is not changed, the model loads from where it is, and the line says so.
    /// </summary>
    [Fact]
    public Task The_folder_is_not_changed_when_the_model_in_use_stays_behind() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", whisper = new { model = "tiny.en" }, llm = new { provider = "none" }, models = new { folder = chosen } }));
        var baseChosen = Create(chosen, "ggml-base.en.bin", fill: 1);
        var tinyChosen = Create(chosen, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", fill: 7); // another tiny.en: a different size
        var speech = SpeechModels();
        var (shell, _, dialogs) = scratch.Shell(speech);
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => shell.Models.ModelPath == tinyChosen && speech.IsReady));
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        await ModelFolderActions.UseDefaultFolder(shell, said.Add);
        Assert.Equal(ModelFolderActions.MovedSome(1, 2, "tiny.en", ModelMover.AlreadyThere, chosen) + ModelFolderActions.FolderKept(chosen, scratch.Paths.DefaultModelsDir, someMoved: true),
            said[^1]);
        Assert.Equal("Moved 1 of 2; tiny.en could not be moved: a different file of that name is already in the new folder. The rest stay in " + chosen +
                     ". The model folder is still " + chosen + ", because the model in use did not move; the files that did are in " + scratch.Paths.DefaultModelsDir +
                     "; choose that folder again to use them.", said[^1]);
        Assert.Equal(chosen, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder); // not changed
        Assert.Equal(tinyChosen, shell.Models.ModelPath); // dictation goes on with the model where it is
        Assert.True(await Eventually(() => speech.IsReady));
        Assert.False(File.Exists(baseChosen));
        Assert.True(File.Exists(Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin")));
        shell.Dispose();
        return true;
    });

    /// <summary>A model folder whose first file's check waits until the test lets it go, so the test can act mid-move.</summary>
    internal sealed class HeldMove
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<string, CancellationToken, Task<string>> _pinned = FakeHasher();
        public volatile bool Entered;

        public async Task<string> Hash(string path, CancellationToken ct)
        {
            if (Path.GetFileName(path).StartsWith("ggml-base.en.bin", StringComparison.Ordinal))
            {
                Entered = true;
                await _gate.Task;
            }
            return await _pinned(path, ct);
        }

        public void Release() => _gate.TrySetResult();
    }

    /// <summary>
    /// Pin S2 (lens on f5ec71c): while files move, a download is refused (it would land in the folder being emptied) and so is
    /// Storage's Delete of a model; the move then finishes.
    /// </summary>
    [Fact]
    public Task Downloads_and_deletions_are_refused_while_files_move() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", fill: 1); // not in use: moved first, and its check is held
        var server = new GateHandler([1, 2, 3, 4]);
        var speech = SpeechModels();
        var (shell, _, dialogs) = scratch.Shell(speech, downloadHandler: server);
        var held = new HeldMove();
        shell.MoveHasher = held.Hash;
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => speech.IsReady));
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        var move = ModelFolderActions.BrowseFolder(shell, said.Add);
        Assert.True(await Eventually(() => held.Entered));
        Assert.True(shell.Models.MovingFiles);

        dialogs.ConfirmDownloads = true;
        shell.Models.Select("small.en");
        await Task.Delay(100, Ct);
        Assert.Empty(server.Requests);
        Assert.False(shell.Models.IsDownloading);
        Assert.False(File.Exists(Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-small.en.bin.part")));
        var stored = ModelFiles.List(scratch.Paths, shell.Settings).Single(m => m.Name == "tiny.en");
        Assert.Equal("Model files are being moved; try again when it finishes.", await shell.DeleteModelAsync(stored));
        Assert.True(File.Exists(tiny));

        held.Release();
        await move;
        Assert.Equal($"Moved 2 files to {chosen}.", said[^1]);
        Assert.False(shell.Models.MovingFiles);
        shell.Dispose();
        return true;
    });

    /// <summary>
    /// Pin S3 (lens on f5ec71c): a settings reload while files move (the watcher's, after any settings.json write) loads no
    /// model, so no engine opens a file in the folder being emptied; the reload after the move loads it from the new folder.
    /// </summary>
    [Fact]
    public Task A_reload_while_files_move_loads_no_model() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", fill: 1);
        var speech = SpeechModels();
        var (shell, _, dialogs) = scratch.Shell(speech);
        var held = new HeldMove();
        shell.MoveHasher = held.Hash;
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => speech.IsReady));
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        var move = ModelFolderActions.BrowseFolder(shell, said.Add);
        Assert.True(await Eventually(() => held.Entered));

        shell.Reload(userRequested: false);
        Assert.Null(speech.ModelPath); // nothing loads mid-move

        held.Release();
        await move;
        Assert.Equal($"Moved 2 files to {chosen}.", said[^1]);
        Assert.Equal(Path.Combine(chosen, "ggml-tiny.en.bin"), speech.ModelPath);
        shell.Dispose();
        return true;
    });

    /// <summary>Pin: Quit while files move stops the move after the file being checked, changes no folder, then quits (no reload).</summary>
    [Fact]
    public Task Quit_while_files_move_stops_the_move_then_quits() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", fill: 1);
        var speech = SpeechModels();
        var log = new RecordingLog();
        var (shell, _, dialogs) = scratch.Shell(speech, log: log);
        var held = new HeldMove();
        shell.MoveHasher = held.Hash;
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => speech.IsReady));
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        var move = ModelFolderActions.BrowseFolder(shell, _ => { });
        Assert.True(await Eventually(() => held.Entered));

        shell.Quit();
        Assert.True(shell.Models.MovingFiles); // deferred until the move stops
        held.Release();
        await move;
        Assert.Contains("Quit asked while model files move", log.All);
        Assert.True(File.Exists(Path.Combine(chosen, "ggml-base.en.bin"))); // the checked file finished
        Assert.True(File.Exists(tiny)); // the next did not start
        Assert.Null(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        Assert.Null(speech.ModelPath); // quit, not reloaded
        shell.Dispose();
        return true;
    });

    // ---- Helpers ----

    internal static ModelManager SpeechModels() => new(NullLog.Instance, loader: (p, _, d) => new HoldingSpeechEngine(p, d), gpuInUse: () => false);

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    internal static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>Records each step as it is reported (not posted later, as <see cref="Progress{T}"/> would).</summary>
    internal sealed class ListProgress(List<MoveStep> steps) : IProgress<MoveStep>
    {
        public void Report(MoveStep value)
        {
            lock (steps) steps.Add(value);
        }
    }

    /// <summary>A speech engine that holds its model file open until disposed.</summary>
    internal sealed class HoldingSpeechEngine(string path, DecodeOptions decode) : ISpeechEngine
    {
        private readonly FakeSpeechEngine _inner = new(path, decode);
        private readonly FileStream _held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public string ModelPath => _inner.ModelPath;
        public DecodeOptions Decode => _inner.Decode;
        public void Reconfigure(DecodeOptions options) => _inner.Reconfigure(options);
        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) => _inner.TranscribeAsync(samples, cancellationToken);

        public void Dispose()
        {
            _held.Dispose();
            _inner.Dispose();
        }
    }

    /// <summary>A clean-up engine that holds its .gguf open until disposed, as llama.cpp's memory map does.</summary>
    private sealed class HoldingLocalEngine(string path) : ILocalLlmEngine
    {
        private readonly FakeLocalEngine _inner = new(path);
        private readonly FileStream _held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        public string ModelPath => _inner.ModelPath;
        public (double Prompt, double Generation) Rates => _inner.Rates;
        public Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default) => _inner.WarmUpAsync(systemPrompt, cancellationToken);
        public int CountTokens(string text) => _inner.CountTokens(text);
        public TimeSpan? Predict(int dictationTokens) => _inner.Predict(dictationTokens);

        public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
            CancellationToken cancellationToken) => _inner.CompleteAsync(systemPrompt, examples, raw, maxTokens, cancellationToken);

        public void Dispose()
        {
            _held.Dispose();
            _inner.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
