using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictum.App;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Views.Settings;
using static Dictum.Tests.ModelMoveTests;

namespace Dictum.Tests;

/// <summary>
/// Re-lens on PR #61 (8001fa1/ea6303b): two paths to one folder never lose a file; the models in use move as one unit; a model
/// chosen by its full path follows the move; a file with no pinned hash counts as "the same" only byte for byte; and every guard
/// the move relies on, pinned one by one. Split from ModelMoveTests to keep that file under 1,000 lines.
/// </summary>
public sealed class ModelMoveSafetyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-model-move-safety-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A directory junction at <paramref name="link"/> to <paramref name="target"/> (no administrator rights needed); false if it could not be made.</summary>
    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    // ---- 1. Two paths to one folder ----

    /// <summary>Pin 1a: the probe tells a junction to the same folder from another folder.</summary>
    [Fact]
    public void SameFolder_sees_a_junction_as_the_same_folder()
    {
        var real = Path.Combine(_dir.FullName, "models");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_dir.FullName, "link");
        if (!Junction(link, real)) Assert.Skip("This test host cannot make a junction.");
        var other = Path.Combine(_dir.FullName, "other");
        Directory.CreateDirectory(other);
        Assert.True(ModelMover.SameFolder(real, link));
        Assert.True(ModelMover.SameFolder(link, real));
        Assert.False(ModelMover.SameFolder(real, other));
        Assert.Empty(Directory.EnumerateFiles(real)); // the probe is gone
    }

    /// <summary>
    /// Pin 1a, through Settings: choosing a junction to the folder in use asks nothing, moves nothing, deletes nothing, and
    /// writes the folder (the lens's reproduction lost tiny.en here).
    /// </summary>
    [Fact]
    public Task Choosing_another_path_to_the_same_folder_moves_nothing() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        var link = Path.Combine(scratch.Root.FullName, "link to models");
        if (!Junction(link, scratch.Paths.DefaultModelsDir)) Assert.Skip("This test host cannot make a junction.");
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        dialogs.Folder = link;
        dialogs.Answers.Enqueue(0); // were it asked, Move the files
        await ModelFolderActions.BrowseFolder(shell, _ => { });
        Assert.Empty(dialogs.Asked);
        Assert.True(File.Exists(tiny));
        Assert.Equal(link, shell.Settings.Models.Folder);
        shell.Dispose();
        return true;
    });

    /// <summary>Pin 1b: the mover itself, sent through a junction to the same folder, never deletes the original.</summary>
    [Fact]
    public async Task The_mover_never_deletes_an_original_that_is_the_target_itself()
    {
        var real = Path.Combine(_dir.FullName, "models");
        var tiny = Create(real, "ggml-tiny.en.bin", fill: 3);
        var part = Create(real, "ggml-small.en.bin.part", fill: 4);
        var link = Path.Combine(_dir.FullName, "link");
        if (!Junction(link, real)) Assert.Skip("This test host cannot make a junction.");
        var result = await ModelMover.MoveAsync(ModelMover.Known(real), link, hasher: FakeHasher(), inUse: [tiny], cancellationToken: Ct);
        Assert.Equal(2, result.Moved); // each is in place already: the same file
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(tiny));
        Assert.Equal(Enumerable.Repeat((byte)4, 10), File.ReadAllBytes(part));
    }

    // ---- 2. The models in use, as one unit ----

    /// <summary>
    /// Pin 2 (the lens's reproduction): two models in use; the second fails. The first's copy is deleted, both originals are
    /// kept, and a file of the same contents that was already in the new folder stays.
    /// </summary>
    [Fact]
    public async Task When_one_model_in_use_fails_no_model_in_use_leaves()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var baseEn = Create(from, "ggml-base.en.bin", fill: 1);
        var speech = Create(from, "ggml-small.en.bin", fill: 2);
        var cleanup = Create(from, "qwen2.5-1.5b-instruct-q4_k_m.gguf", fill: 3);
        var alreadyThere = Create(to, "ggml-base.en.bin", fill: 1); // the same base.en, already in the new folder
        var hasher = FakeHasher("qwen2.5-1.5b-instruct-q4_k_m.gguf");
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: hasher, inUse: [baseEn, speech, cleanup], cancellationToken: Ct);
        Assert.Equal((0, false), (result.Moved, result.Cancelled));
        Assert.Equal([new MoveProblem("qwen2.5-1.5b-instruct-q4_k_m.gguf", ModelMover.CopyDoesNotMatch)], result.Problems);
        Assert.Equal([baseEn, speech, cleanup], result.LeftBehind);
        Assert.Equal(Enumerable.Repeat((byte)1, 10), File.ReadAllBytes(baseEn));
        Assert.Equal(Enumerable.Repeat((byte)2, 10), File.ReadAllBytes(speech));
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(cleanup));
        Assert.False(File.Exists(Path.Combine(to, "ggml-small.en.bin"))); // the copy this move made is gone
        Assert.True(File.Exists(alreadyThere)); // a file that was there before stays
        Assert.Empty(Directory.EnumerateFiles(to, "*" + ModelMover.TempSuffix));
    }

    /// <summary>Pin 2 (Quit): stopped while the second model in use is checked, both originals stay and the copies go.</summary>
    [Fact]
    public async Task Quit_while_the_models_in_use_move_keeps_every_original()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var speech = Create(from, "ggml-small.en.bin", fill: 2);
        var cleanup = Create(from, "qwen2.5-1.5b-instruct-q4_k_m.gguf", fill: 3);
        using var cancel = new CancellationTokenSource();
        var hasher = FakeHasher();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: (path, ct) =>
        {
            if (Path.GetFileName(path).StartsWith("qwen", StringComparison.Ordinal)) cancel.Cancel(); // Quit, during the second
            return hasher(path, ct);
        }, inUse: [speech, cleanup], cancellationToken: cancel.Token);
        Assert.Equal((0, true), (result.Moved, result.Cancelled));
        Assert.Equal(Enumerable.Repeat((byte)2, 10), File.ReadAllBytes(speech));
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(cleanup));
        Assert.Empty(Directory.EnumerateFiles(to));
    }

    // ---- 3. A model chosen by its full path ----

    /// <summary>Pin 3: whisper.model set to a file path in the old folder is rewritten to the new folder in the same write.</summary>
    [Fact]
    public Task A_model_chosen_by_its_path_follows_the_move() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch();
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin", TinyEnBytes);
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", whisper = new { model = tiny }, llm = new { provider = "none" } }));
        var speech = SpeechModels();
        var log = new RecordingLog();
        var (shell, _, dialogs) = scratch.Shell(speech, log: log);
        shell.MoveHasher = FakeHasher();
        shell.Reload(userRequested: false);
        Assert.True(await Eventually(() => speech.IsReady && speech.ModelPath == tiny));
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        await ModelFolderActions.BrowseFolder(shell, _ => { });
        var moved = Path.Combine(chosen, "ggml-tiny.en.bin");
        var written = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings;
        Assert.Equal((chosen, moved), (written.Models.Folder, written.Whisper.Model));
        Assert.Equal(moved, speech.ModelPath);
        // The new path is written before models.folder (round-3 lens): the keys go one by one, and a failure after the first leaves
        // whisper.model naming the new copy, which exists, rather than the original, which is gone.
        var set = log.Lines.Select(l => l.Message).Single(m => m.StartsWith("Set from the app: ", StringComparison.Ordinal) && m.Contains("models.folder", StringComparison.Ordinal));
        Assert.True(set.IndexOf("whisper.model = ", StringComparison.Ordinal) is var model && model >= 0 && model < set.IndexOf("models.folder = ", StringComparison.Ordinal), set);
        shell.Dispose();
        return true;
    });

    // ---- 4. No pinned hash: byte for byte ----

    /// <summary>Pin 4: a file of the same name and size but other bytes, with no pinned hash (a .part), is not "the same": skipped, original kept.</summary>
    [Fact]
    public async Task Equal_size_but_other_bytes_is_not_the_same_file()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-small.en.bin.part", fill: 2);
        var there = Create(to, "ggml-small.en.bin.part", fill: 9);
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: (_, _) => throw new InvalidOperationException("no hash for a .part"), cancellationToken: Ct);
        Assert.Equal([new MoveProblem("ggml-small.en.bin.part", ModelMover.AlreadyThere)], result.Problems);
        Assert.Equal(Enumerable.Repeat((byte)2, 10), File.ReadAllBytes(original));
        Assert.Equal(Enumerable.Repeat((byte)9, 10), File.ReadAllBytes(there));

        File.WriteAllBytes(there, File.ReadAllBytes(original)); // now the same bytes: it counts, and the original goes
        var again = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: (_, _) => throw new InvalidOperationException("no hash for a .part"), cancellationToken: Ct);
        Assert.True(again.Complete);
        Assert.False(File.Exists(original));
    }

    // ---- 5. The guards, one test each ----

    private ModelCoordinator Coordinator(AppPaths paths, Func<AppSettings> settings, LocalLlmManager llm, ScriptedPrompts prompts, List<string> balloons) =>
        new(paths, new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false), llm,
            new HttpClient(new GateHandler([1])), NullLog.Instance, settings, null, prompts, _ => null, reload: () => { }, shutdown: () => { },
            refreshTooltip: () => { }, balloon: (text, _) => balloons.Add(text), integrity: null, gpuMachine: () => false, freeBytes: _ => 100_000_000_000);

    [Fact]
    public void EnsureCleanup_loads_nothing_while_files_move()
    {
        using var scratch = new Scratch();
        var gguf = Create(Path.Combine(scratch.Root.FullName, "models"), "qwen2.5-1.5b-instruct-q4_k_m.gguf");
        var settings = new AppSettings { Llm = new LlmSettings { Provider = LlmSettings.Local, Model = gguf } };
        var llm = new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p), gpuInUse: () => false);
        var coordinator = Coordinator(scratch.Paths, () => settings, llm, new ScriptedPrompts(), []);
        coordinator.MovingFiles = true;
        coordinator.EnsureCleanup();
        Assert.Null(llm.ModelPath);
        coordinator.MovingFiles = false;
        coordinator.EnsureCleanup();
        Assert.Equal(gguf, llm.ModelPath); // the same call loads it once the move is over
        llm.Dispose();
    }

    [Fact]
    public void ApplyWithDownloads_is_refused_while_files_move_before_anything_is_asked()
    {
        using var scratch = new Scratch();
        var prompts = new ScriptedPrompts { Confirm = true };
        var balloons = new List<string>();
        var coordinator = Coordinator(scratch.Paths, () => new AppSettings(), new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p)), prompts, balloons);
        coordinator.MovingFiles = true;
        bool? finished = null;
        var spec = ModelCatalog.Find("small.en")!.Download;
        coordinator.ApplyWithDownloads("note", [(spec, Path.Combine(scratch.Paths.DefaultModelsDir, spec.FileName))], [("whisper.model", JsonValue.Create("small.en"))],
            finished: ok => finished = ok);
        Assert.Equal(0, prompts.ConfirmCalls);
        Assert.False(finished);
        Assert.Equal([ModelCoordinator.MovingRefusal], balloons);
    }

    [Fact]
    public Task Browse_opens_no_chooser_while_files_move() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings);
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        shell.Models.MovingFiles = true;
        dialogs.Folder = Path.Combine(scratch.Root.FullName, "elsewhere");
        await ModelFolderActions.BrowseFolder(shell, _ => { });
        Assert.Empty(dialogs.FolderChooserOpenedIn);
        Assert.Null(shell.Settings.Models.Folder);
        shell.Models.MovingFiles = false;
        shell.Dispose();
        return true;
    });

    [Fact]
    public Task Use_default_changes_nothing_while_files_move() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch();
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        Directory.CreateDirectory(chosen);
        File.WriteAllText(scratch.Paths.SettingsFile, JsonSerializer.Serialize(new { hotkey = "F24", llm = new { provider = "none" }, models = new { folder = chosen } }));
        var (shell, _, _) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        shell.Models.MovingFiles = true;
        await ModelFolderActions.UseDefaultFolder(shell, _ => { });
        Assert.Equal(chosen, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        shell.Models.MovingFiles = false;
        shell.Dispose();
        return true;
    });

    /// <summary>Pin: stopped by Quit with no model in use among the files, the folder is still not changed.</summary>
    [Fact]
    public Task A_stopped_move_never_changes_the_folder() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings); // tiny.en is chosen but on this PC nowhere: no model in use among the files
        Create(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin", fill: 1);
        var small = Create(scratch.Paths.DefaultModelsDir, "ggml-small.en.bin", fill: 2);
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        var held = new HeldMove();
        shell.MoveHasher = held.Hash;
        shell.Reload(userRequested: false);
        var chosen = Path.Combine(scratch.Root.FullName, "D-drive models");
        dialogs.Folder = chosen;
        dialogs.Answers.Enqueue(0);
        var said = new List<string>();
        var move = ModelFolderActions.BrowseFolder(shell, said.Add);
        Assert.True(await Eventually(() => held.Entered));
        shell.Quit();
        held.Release();
        await move;
        Assert.Equal("Moved 1 of 2.", said[^1]); // only the approved count: no folder sentence (round-3 lens)
        Assert.True(File.Exists(small)); // not reached
        Assert.Null(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Models.Folder);
        shell.Dispose();
        return true;
    });

    [Fact]
    public async Task Moving_a_file_forgets_the_originals_verified_record()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-tiny.en.bin", fill: 3);
        var record = Path.Combine(_dir.FullName, "verified-models.json");
        var integrity = new ModelIntegrity(record, NullLog.Instance);
        integrity.Remember(original);
        Assert.Contains(original, JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(record))!.Keys);
        await ModelMover.MoveAsync(ModelMover.Known(from), to, integrity, hasher: FakeHasher(), cancellationToken: Ct);
        Assert.Equal([Path.Combine(to, "ggml-tiny.en.bin")], JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(record))!.Keys);
    }

    /// <summary>Pin: the default copy stops when cancelled (here, before its first read): no copy, the original kept.</summary>
    [Fact]
    public async Task The_default_copy_stops_when_cancelled()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-tiny.en.bin", fill: 3);
        using var cancel = new CancellationTokenSource();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to, progress: new CancelOnReport(cancel), hasher: FakeHasher(), cancellationToken: cancel.Token);
        Assert.Equal((0, true), (result.Moved, result.Cancelled));
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(original));
        Assert.Empty(Directory.EnumerateFiles(to));
    }

    /// <summary>Pin: with a copy that ignores the token, the check before each file is what stops the next one.</summary>
    [Fact]
    public async Task No_file_starts_after_a_cancel()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        Create(from, "ggml-base.en.bin", fill: 1);
        var tiny = Create(from, "ggml-tiny.en.bin", fill: 3);
        using var cancel = new CancellationTokenSource();
        var copies = 0;
        var hasher = FakeHasher();
        var result = await ModelMover.MoveAsync(ModelMover.Known(from), to,
            hasher: (path, ct) =>
            {
                cancel.Cancel();
                return hasher(path, CancellationToken.None);
            },
            copier: (source, target, _) =>
            {
                copies++;
                File.Copy(source, target, overwrite: true);
                return Task.CompletedTask;
            }, cancellationToken: cancel.Token);
        Assert.Equal(1, copies);
        Assert.Equal((1, true), (result.Moved, result.Cancelled));
        Assert.Equal(Enumerable.Repeat((byte)3, 10), File.ReadAllBytes(tiny));
    }

    private sealed class CancelOnReport(CancellationTokenSource cancel) : IProgress<MoveStep>
    {
        public void Report(MoveStep value) => cancel.Cancel();
    }

    // ---- 6. Deleting data while files move ----

    [Fact]
    public Task Delete_all_app_data_is_refused_while_files_move() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin");
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        shell.Models.MovingFiles = true;
        Assert.Equal(ModelCoordinator.MovingRefusal, shell.DeleteData(RemovalScope.AllData));
        Assert.Empty(dialogs.Asked);
        Assert.True(File.Exists(tiny));
        shell.Models.MovingFiles = false;
        shell.Dispose();
    });

    [Fact]
    public Task Remove_Dictum_completely_is_refused_while_files_move() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings);
        var tiny = Create(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin");
        var (shell, _, dialogs) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        shell.Models.MovingFiles = true;
        Assert.False(shell.RemoveEverything());
        Assert.Empty(dialogs.Asked);
        Assert.Equal([ModelCoordinator.MovingRefusal], dialogs.Told);
        Assert.True(File.Exists(tiny));
        shell.Models.MovingFiles = false;
        shell.Dispose();
    });

    // ---- 7. Calibration ----

    [Fact]
    public Task Calibration_loads_no_model_while_files_move() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings);
        var (shell, _, _) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        var host = shell.CreateCalibrationHost();
        Assert.Null(host.Refusal());
        shell.Models.MovingFiles = true;
        Assert.Equal(ModelCoordinator.MovingRefusal, host.Refusal());
        Assert.Equal(ModelCoordinator.MovingRefusal, Assert.Throws<InvalidOperationException>(() => host.CreateEngine()).Message);
        shell.Models.MovingFiles = false;
        shell.Dispose();
    });

    /// <summary>
    /// Pin (round-3 lens): a run that began before the move refuses its next load once files move: the loader is never called
    /// and the run says the approved refusal for that model.
    /// </summary>
    [Fact]
    public Task A_calibration_run_already_under_way_loads_no_model_once_files_move() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(ModelMoveTests.Settings);
        var (shell, _, _) = scratch.Shell(SpeechModels());
        shell.Reload(userRequested: false);
        var loads = 0;
        var host = shell.CreateCalibrationHost() with { LoadSpeech = (p, _, d) => { loads++; return new FakeSpeechEngine(p, d); } };
        var engine = host.CreateEngine(); // before the move: allowed
        shell.Models.MovingFiles = true;
        var lines = new List<string>();
        var model = Create(Path.Combine(scratch.Root.FullName, "models"), "ggml-tiny.en.bin");
        await engine.RunAsync([new Dictum.Calibration.CalibrationClip(1, "the cat sat", new float[16000])], [new Dictum.Calibration.SpeechCandidate("tiny.en", model)], null,
            new ListLines(lines), Ct);
        Assert.Equal(0, loads);
        Assert.Contains($"tiny.en could not be loaded: {ModelCoordinator.MovingRefusal}", lines);
        shell.Models.MovingFiles = false;
        shell.Dispose();
        return true;
    });

    /// <summary>Pin (round-3 lens): Run test in the calibration window says the refusal and runs nothing while files move.</summary>
    [Fact]
    public Task The_calibration_window_refuses_to_run_while_files_move() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new Dictum.UI.Views.CalibrationWindow(shell);
        window.Show();
        shell.Models.MovingFiles = true;
        var run = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.Button>().First(b => b.Content as string == "Run test");
        run.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var texts = string.Join("\n", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        Assert.Contains(ModelCoordinator.MovingRefusal, texts);
        Assert.DoesNotContain("Record at least one sentence first", texts); // the refusal comes before anything else is checked
        shell.Models.MovingFiles = false;
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (round-3 lens): a file of the same contents already in the new folder, whose original cannot be opened for itself
    /// alone (so it may be the same file), keeps its original and says so with the approved reason; it counts as moved.
    /// </summary>
    [Fact]
    public async Task A_kept_original_beside_a_same_file_is_reported()
    {
        var from = Path.Combine(_dir.FullName, "old");
        var to = Path.Combine(_dir.FullName, "new");
        var original = Create(from, "ggml-tiny.en.bin", fill: 3);
        Create(to, "ggml-tiny.en.bin", fill: 3);
        MoveResult result;
        using (new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read)) // someone else holds it: no exclusive open
            result = await ModelMover.MoveAsync(ModelMover.Known(from), to, hasher: FakeHasher(), cancellationToken: Ct);
        Assert.Equal(1, result.Moved);
        Assert.Equal([new MoveProblem("ggml-tiny.en.bin", ModelMover.OriginalKept, Copied: true)], result.Problems);
        Assert.True(File.Exists(original));
    }

    private sealed class ListLines(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value);
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var link in _dir.EnumerateDirectories("link*")) link.Delete(); // a junction first, so its target's files are not followed
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
