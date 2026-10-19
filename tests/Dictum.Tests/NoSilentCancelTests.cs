using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Dictum.App;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Store certification 19/10/2026 (10.1.2.10, "download cancelled when trying to download any model"): a model choice never
/// cancels a download in progress without asking, and only the Stop answer cancels it. The downloads come from an in-memory
/// server that holds each file until the test releases it (<see cref="GateHandler"/>); no real download is made.
/// </summary>
public sealed class NoSilentCancelTests : IDisposable
{
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-nocancel-");
    private readonly ScriptedPrompts _prompts = new() { Confirm = true };
    private readonly ConcurrentQueue<string> _balloons = new();
    private readonly ConcurrentQueue<string> _switches = new(); // whisper.model after each settings write, in order
    private readonly GateHandler _server = new(Payload);
    private readonly HttpClient _client;
    private readonly AppPaths _paths;
    private AppSettings _settings = new();

    public NoSilentCancelTests()
    {
        _client = new HttpClient(_server);
        _paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), _dir.FullName);
        Directory.CreateDirectory(_paths.ConfigDir);
        Directory.CreateDirectory(_paths.DefaultModelsDir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>What the in-memory server can serve under a catalogue file's name (its real size and SHA-256 cannot be).</summary>
    internal static DownloadSpec Spec(string file) =>
        new(new Uri($"https://dictate-test.invalid/{file}"), file, Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)));

    private string Model(string file) => Path.Combine(_paths.DefaultModelsDir, file);

    private static void Sized(string path, long bytes)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(bytes); // the catalogue size, so the lookup counts it as on this PC (nothing reads its bytes here)
    }

    private ModelCoordinator Make(LocalLlmManager? llm = null) =>
        new(_paths, new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false),
            llm ?? new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p)), _client, NullLog.Instance, () => _settings, null, _prompts, _ => null,
            reload: () =>
            {
                _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
                _switches.Enqueue(_settings.Whisper.Model);
            },
            shutdown: () => { }, refreshTooltip: () => { }, balloon: (text, _) => _balloons.Enqueue(text), gpuMachine: () => false)
        {
            TestSpecs = s => Spec(s.FileName),
        };

    /// <summary>Starts tiny.en (asked, then downloading: the server holds it) and waits until its request is in flight.</summary>
    private async Task<ModelCoordinator> TinyDownloading()
    {
        var coordinator = Make();
        coordinator.Select("tiny.en");
        Assert.True(await Eventually(() => _server.InFlight == 1), "tiny.en never started downloading");
        Assert.Equal(1, _prompts.ConfirmCalls);
        return coordinator;
    }

    private bool AnyCancelBalloon => _balloons.Any(b => b.Contains("cancelled", StringComparison.Ordinal) || b.Contains("stopped", StringComparison.Ordinal));

    /// <summary>Pin (rule 1): choosing the model being downloaded again cancels nothing and says it is already downloading; queued, waiting.</summary>
    [Fact]
    public async Task Choosing_the_downloading_model_again_does_not_cancel_it_and_says_it_is_already_downloading()
    {
        var coordinator = await TinyDownloading();
        coordinator.Select("tiny.en"); // the Store tester's retry
        await Task.Delay(200, Ct);
        Assert.Equal(0, _server.Cancelled);
        Assert.Equal(1, _server.InFlight);
        Assert.True(coordinator.IsDownloading);
        Assert.Contains("tiny.en is already downloading (0%).", _balloons);
        Assert.Equal(1, _prompts.ConfirmCalls); // not asked again
        Assert.Empty(_prompts.BusyQuestions);
        Assert.False(AnyCancelBalloon);

        _prompts.Busy = BusyDownloadChoice.DownloadAfter;
        coordinator.Select("base.en"); // queued behind tiny.en
        coordinator.Select("base.en");
        Assert.Contains("base.en is already waiting to download.", _balloons);
        Assert.Single(_prompts.BusyQuestions); // the second choice of base.en asked nothing
        Assert.Equal(DownloadState.Running, coordinator.DownloadStateOf("ggml-tiny.en.bin"));
        Assert.Equal(DownloadState.Waiting, coordinator.DownloadStateOf("ggml-base.en.bin"));

        _server.Release("ggml-tiny.en.bin");
        _server.Release("ggml-base.en.bin");
        Assert.True(await Eventually(() => !coordinator.IsDownloading && File.Exists(Model("ggml-base.en.bin"))));
        Assert.True(await Eventually(() => _settings.Whisper.Model == "base.en"), "the last choice was not applied: " + string.Join(", ", _switches));
        Assert.Equal(0, _server.Cancelled);
    }

    /// <summary>Pin (rule 2): another model is asked about before anything is cancelled, and Cancel leaves the running download going.</summary>
    [Fact]
    public async Task Choosing_another_model_asks_before_cancelling_and_Cancel_leaves_the_running_download_going()
    {
        var coordinator = await TinyDownloading();
        var cancelledWhileAsking = -1;
        var inFlightWhileAsking = -1;
        _prompts.WhenBusyAsked = () => (cancelledWhileAsking, inFlightWhileAsking) = (_server.Cancelled, _server.InFlight);
        _prompts.Busy = BusyDownloadChoice.Cancel;

        coordinator.Select("base.en");
        var (text, buttons) = Assert.Single(_prompts.BusyQuestions);
        Assert.Equal((0, 1), (cancelledWhileAsking, inFlightWhileAsking)); // asked while tiny.en still ran
        Assert.StartsWith($"tiny.en is downloading (0% done). Download ggml-base.en.bin ({ModelCatalog.Find("base.en")!.SizeText}) from huggingface.co after it, or stop tiny.en and download base.en now?", text);
        Assert.Equal(["Download after it", "Stop tiny.en and download base.en", "Cancel"], buttons);
        Assert.Equal(1, _prompts.ConfirmCalls); // the plain question was not asked as well

        await Task.Delay(200, Ct);
        Assert.Equal(0, _server.Cancelled);
        Assert.Equal(1, _server.InFlight);
        Assert.Equal(["ggml-tiny.en.bin"], _server.Requests.ToArray());
        Assert.Equal(DownloadState.None, coordinator.DownloadStateOf("ggml-base.en.bin"));
        Assert.False(AnyCancelBalloon);

        _server.Release("ggml-tiny.en.bin");
        Assert.True(await Eventually(() => _settings.Whisper.Model == "tiny.en"), "tiny.en arrived but was not switched to");
    }

    /// <summary>Pin (rule 2): Download after it keeps the running download's bytes; both arrive in order and the last choice is applied.</summary>
    [Fact]
    public async Task Download_after_it_queues_and_both_arrive_in_order_with_the_last_choice_applied()
    {
        var coordinator = await TinyDownloading();
        _prompts.Busy = BusyDownloadChoice.DownloadAfter;
        coordinator.Select("base.en");
        await Task.Delay(200, Ct);
        Assert.Equal(0, _server.Cancelled);
        Assert.Equal(["ggml-tiny.en.bin"], _server.Requests.ToArray()); // base.en waits its turn

        _server.Release("ggml-base.en.bin"); // served the moment it is asked for
        _server.Release("ggml-tiny.en.bin");
        Assert.True(await Eventually(() => !coordinator.IsDownloading && File.Exists(Model("ggml-base.en.bin"))), "base.en never arrived");
        Assert.Equal(["ggml-tiny.en.bin", "ggml-base.en.bin"], _server.Requests.ToArray());
        Assert.True(File.Exists(Model("ggml-tiny.en.bin")));
        Assert.True(await Eventually(() => _settings.Whisper.Model == "base.en"));
        Assert.Equal(["base.en"], _switches.ToArray()); // tiny.en's arrival did not switch: base.en was chosen after it
        Assert.Equal(0, _server.Cancelled);
        Assert.False(AnyCancelBalloon);
    }

    /// <summary>Pin (rules 2 and 5): only the Stop answer cancels, after the question, and its balloon says why.</summary>
    [Fact]
    public async Task Stop_cancels_the_running_download_with_the_new_balloon_and_downloads_the_chosen_model()
    {
        var coordinator = await TinyDownloading();
        var cancelledWhileAsking = -1;
        _prompts.WhenBusyAsked = () => cancelledWhileAsking = _server.Cancelled;
        _prompts.Busy = BusyDownloadChoice.StopAndDownload;
        _server.Release("ggml-base.en.bin");

        coordinator.Select("base.en");
        Assert.Equal(0, cancelledWhileAsking);
        Assert.True(await Eventually(() => _server.Cancelled == 1), "Stop did not cancel the running download");
        Assert.True(await Eventually(() => _balloons.Contains("Download of ggml-tiny.en.bin stopped: you chose base.en.")),
            "the Stop balloon is missing: " + string.Join(" | ", _balloons));
        Assert.DoesNotContain("Download of ggml-tiny.en.bin cancelled.", _balloons);
        Assert.True(await Eventually(() => _settings.Whisper.Model == "base.en"), "base.en was not downloaded and switched to after Stop");
        Assert.Equal(["ggml-tiny.en.bin", "ggml-base.en.bin"], _server.Requests.ToArray());
        Assert.False(File.Exists(Model("ggml-tiny.en.bin")));
        Assert.False(File.Exists(Model("ggml-tiny.en.bin.part"))); // the stopped download's partial file is gone
    }

    /// <summary>Pin (rule 5): the Cancel download link keeps its own words; quit only logs.</summary>
    [Fact]
    public async Task The_cancel_link_says_cancelled_and_quit_says_nothing()
    {
        var coordinator = await TinyDownloading();
        coordinator.CancelDownload(DownloadCancelReason.CancelLink);
        Assert.True(await Eventually(() => _balloons.Contains("Download of ggml-tiny.en.bin cancelled.")));

        _balloons.Clear();
        coordinator.Select("tiny.en");
        Assert.True(await Eventually(() => _server.InFlight == 1));
        coordinator.CancelDownload(DownloadCancelReason.Quit);
        Assert.True(await Eventually(() => !coordinator.IsDownloading && _server.InFlight == 0));
        await Task.Delay(100, Ct);
        Assert.False(AnyCancelBalloon);
    }

    /// <summary>Pin (rule 3): a model already on this PC is switched to at once, the download carries on, and its late arrival does not switch back.</summary>
    [Fact]
    public async Task Choosing_a_downloaded_model_during_a_download_switches_at_once_and_the_late_arrival_does_not_override_it()
    {
        var coordinator = await TinyDownloading();
        Sized(Model("ggml-small.en.bin"), ModelCatalog.Find("small.en")!.Bytes);

        coordinator.Select("small.en");
        Assert.Equal("small.en", _settings.Whisper.Model); // at once
        Assert.Empty(_prompts.BusyQuestions);
        await Task.Delay(200, Ct);
        Assert.Equal(0, _server.Cancelled);
        Assert.Equal(1, _server.InFlight);

        _server.Release("ggml-tiny.en.bin");
        Assert.True(await Eventually(() => _balloons.Contains("Downloaded and verified ggml-tiny.en.bin.")));
        await Task.Delay(200, Ct);
        Assert.Equal("small.en", _settings.Whisper.Model); // last choice wins
        Assert.Equal(["small.en"], _switches.ToArray());
        Assert.False(AnyCancelBalloon);
    }

    /// <summary>
    /// Pin (rules 1 and 3): a model chosen again while it is on its way becomes the latest choice again, so it is switched to
    /// when it arrives, also when the download was started for calibration (which switches to nothing by itself).
    /// </summary>
    [Fact]
    public async Task Choosing_a_model_on_its_way_again_makes_it_the_latest_choice()
    {
        var coordinator = await TinyDownloading();
        Sized(Model("ggml-small.en.bin"), ModelCatalog.Find("small.en")!.Bytes);
        coordinator.Select("small.en"); // switched to at once: tiny.en's arrival would no longer switch
        coordinator.Select("tiny.en"); // ...until it is chosen again
        Assert.Contains("tiny.en is already downloading (0%).", _balloons);
        _server.Release("ggml-tiny.en.bin");
        Assert.True(await Eventually(() => _settings.Whisper.Model == "tiny.en"), "tiny.en, chosen last, was not switched to: " + string.Join(", ", _switches));

        var calibrated = new List<bool>();
        coordinator.ApplyWithDownloads(PersonalService.CalibrationDownload, [(ModelCatalog.Find("base.en")!.Download, Model("ggml-base.en.bin"))], [], finished: calibrated.Add);
        Assert.True(await Eventually(() => _server.InFlight == 1));
        coordinator.Select("base.en"); // in the tray, while calibration downloads it
        Assert.Contains("base.en is already downloading (0%).", _balloons);
        _server.Release("ggml-base.en.bin");
        Assert.True(await Eventually(() => _settings.Whisper.Model == "base.en"), "base.en, chosen in the tray during its calibration download, was not switched to");
        Assert.Equal([true], calibrated);
        Assert.Equal(0, _server.Cancelled);
    }

    /// <summary>Pin (last choice wins, sibling caller): the first run's clean-up download, arriving after another clean-up model was chosen, is not loaded over it.</summary>
    [Fact]
    public async Task A_first_run_cleanup_download_arriving_late_does_not_replace_a_newer_cleanup_choice()
    {
        var llm = new LocalLlmManager(NullLog.Instance, loader: (p, _) => new FakeLocalEngine(p));
        _prompts.Speech = new ModelPick("tiny.en", null, false);
        _prompts.Cleanup = new CleanupPick("qwen2.5-0.5b", null, false, false);
        var coordinator = Make(llm);
        Assert.True(coordinator.FirstRun());
        _settings = SettingsLoader.LoadFile(_paths.SettingsFile).Settings;
        coordinator.EnsureCleanup(); // starts the first run's clean-up download
        Assert.True(await Eventually(() => _server.InFlight == 1));

        var larger = CleanupCatalog.Find("qwen2.5-1.5b")!;
        Sized(Model(larger.FileName), larger.Bytes);
        coordinator.SelectCleanup("qwen2.5-1.5b"); // on this PC: switched to at once
        coordinator.EnsureCleanup(); // as the reload after the write does in the app
        Assert.Equal(Model(larger.FileName), llm.ModelPath);

        _server.Release(CleanupCatalog.Find("qwen2.5-0.5b")!.FileName);
        Assert.True(await Eventually(() => !coordinator.IsDownloading));
        await Task.Delay(100, Ct);
        Assert.Equal(Model(larger.FileName), llm.ModelPath); // the newer choice stays loaded
        Assert.Equal(("local", "qwen2.5-1.5b"), (_settings.Llm.Provider, _settings.Llm.Model));
    }

    /// <summary>Pin (last choice wins): a Mode preset whose download arrives after another model was chosen writes nothing, and says so to its caller.</summary>
    [Fact]
    public async Task A_preset_whose_download_arrives_after_a_newer_choice_writes_nothing()
    {
        var coordinator = Make();
        var finished = new List<bool>();
        coordinator.ApplyWithDownloads("Needed for Full mode", [(ModelCatalog.Find("tiny.en")!.Download, Model("ggml-tiny.en.bin"))],
            [("whisper.model", JsonValue.Create("tiny.en")), ("whisper.beam_size", JsonValue.Create(5))], finished: finished.Add);
        Assert.True(await Eventually(() => _server.InFlight == 1));
        Sized(Model("ggml-small.en.bin"), ModelCatalog.Find("small.en")!.Bytes);
        coordinator.Select("small.en");

        _server.Release("ggml-tiny.en.bin");
        Assert.True(await Eventually(() => finished.Count == 1), "the preset never finished");
        Assert.Equal([false], finished);
        Assert.Equal(("small.en", 0), (_settings.Whisper.Model, _settings.Whisper.BeamSize));
        Assert.Equal(0, _server.Cancelled);
    }

    /// <summary>Pin (rule 4): a Mode preset or calibration with every model on this PC applies its values without cancelling a download.</summary>
    [Fact]
    public async Task ApplyWithDownloads_with_nothing_missing_does_not_cancel_a_download_in_progress()
    {
        var coordinator = await TinyDownloading();
        var finished = new List<bool>();
        coordinator.ApplyWithDownloads("Needed for Light mode", [], [("whisper.model", JsonValue.Create("base.en")), ("llm.provider", JsonValue.Create("none"))],
            finished: finished.Add);
        Assert.Equal([true], finished);
        Assert.Equal("base.en", _settings.Whisper.Model);
        await Task.Delay(200, Ct);
        Assert.Equal(0, _server.Cancelled);
        Assert.Equal(1, _server.InFlight);
        Assert.True(coordinator.IsDownloading);

        _server.Release("ggml-tiny.en.bin");
        Assert.True(await Eventually(() => !coordinator.IsDownloading && File.Exists(Model("ggml-tiny.en.bin"))));
        await Task.Delay(100, Ct);
        Assert.Equal("base.en", _settings.Whisper.Model); // the preset was chosen after tiny.en: it stands
        Assert.False(AnyCancelBalloon);
    }

    public void Dispose()
    {
        _client.Dispose();
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The same rules where the user meets them: the tray panel's Mode segments and pickers, and Settings > Models.</summary>
public sealed class NoSilentCancelUiTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private const string LightSettings = """{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""";

    /// <summary>A shell whose downloads come from <paramref name="server"/>, with tiny.en downloading (asked and agreed).</summary>
    private static async Task<(Dictum.UI.Shell.AppShell Shell, ScriptedDialogs Dialogs)> TinyDownloading(Scratch scratch, GateHandler server, ILog? log = null)
    {
        var (shell, _, dialogs) = scratch.Shell(downloadHandler: server, log: log);
        shell.Reload(userRequested: false);
        dialogs.ConfirmDownloads = true;
        shell.Models.Select("tiny.en");
        for (var waited = 0; server.InFlight != 1 && waited < 5000; waited += 10) await Task.Delay(10, TestContext.Current.CancellationToken); // yields to this UI thread
        Assert.True(server.InFlight == 1, "tiny.en never started downloading");
        return (shell, dialogs);
    }

    /// <summary>Pin (rule 4): pressing the Light segment that is already checked, while a download runs, changes nothing and cancels nothing.</summary>
    [Fact]
    public Task Pressing_the_checked_Light_segment_during_a_download_does_not_cancel_it() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch(LightSettings);
        var baseEn = Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin");
        Directory.CreateDirectory(scratch.Paths.DefaultModelsDir);
        using (var file = new FileStream(baseEn, FileMode.Create, FileAccess.Write)) file.SetLength(ModelCatalog.Find("base.en")!.Bytes); // Light needs no download
        var server = new GateHandler([1, 2, 3, 4]);
        var log = new RecordingLog();
        var (shell, _) = await TinyDownloading(scratch, server, log);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var light = All<ToggleButton>(flyout).Single(b => AutomationProperties.GetName(b) == "Mode: Light");
        Assert.True(light.IsChecked); // the settings match Light
        var written = File.ReadAllText(scratch.Paths.SettingsFile);

        light.IsChecked = false; // what a click on the checked segment does to it
        await Task.Delay(300, TestContext.Current.CancellationToken); // lets a cancellation, had one been made, run on this thread

        Assert.True(light.IsChecked); // still chosen
        Assert.Equal(0, server.Cancelled);
        Assert.Equal(1, server.InFlight);
        Assert.True(shell.Models.IsDownloading);
        Assert.Equal(written, File.ReadAllText(scratch.Paths.SettingsFile));
        Assert.DoesNotContain(log.Lines, l => l.Message.StartsWith("Mode preset Light chosen", StringComparison.Ordinal)); // the preset did not run again
        flyout.Close();
        shell.Dispose();
        return true;
    });

    /// <summary>Pin (rule 6): Settings > Models shows the downloading row as Downloading with its button disabled and named for why; the tray pickers mark it.</summary>
    [Fact]
    public Task The_downloading_model_shows_as_downloading_with_a_disabled_button() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var server = new GateHandler([1, 2, 3, 4]);
        var (shell, dialogs) = await TinyDownloading(scratch, server);
        dialogs.BusyDownload = BusyDownloadChoice.DownloadAfter;
        shell.Models.Select("small.en"); // waits its turn behind tiny.en
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var buttons = All<Button>(window).ToList();
        var downloading = buttons.Single(b => b.Content as string == ModelsPage.DownloadingButton);
        Assert.False(downloading.IsEnabled);
        Assert.Equal("tiny.en is already downloading", AutomationProperties.GetName(downloading));
        var waiting = buttons.Single(b => b.Content as string == ModelsPage.WaitingButton);
        Assert.False(waiting.IsEnabled);
        Assert.Equal("small.en is already waiting to download", AutomationProperties.GetName(waiting));
        var texts = string.Join("\n", All<TextBlock>(window).Select(t => t.Text));
        Assert.Contains("Downloading 0% · ", texts);
        Assert.Contains(ModelsPage.WaitingState + " · ", texts);
        Assert.Contains(buttons, b => b.Content as string == "Download…" && b.IsEnabled); // the other rows stay usable
        window.Close();

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var speech = All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == "Speech model");
        var labels = Assert.IsAssignableFrom<IEnumerable<string>>(speech.ItemsSource).ToList();
        Assert.Contains("tiny.en" + FlyoutWindow.DownloadingMark, labels);
        Assert.Contains("small.en" + FlyoutWindow.WaitingMark, labels);
        flyout.Close();

        Assert.Equal(0, server.Cancelled);
        shell.Dispose();
        return true;
    });
}
