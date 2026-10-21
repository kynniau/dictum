using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Dictum 1.2 (product owner, 21/10/2026): the first run is one "Welcome to Dictum" window, how Dictum works, where the model
/// files go, the speech model, the hotkey check, a microphone test and a first dictation, replacing the chain of first-run
/// dialogs. Headless, as DialogTests: the window is built on a flow and its buttons pressed; the nested frame of
/// RunFirstSteps needs a real desktop and is not exercised (the shell's own path is, through ScriptedDialogs.Onboard).
/// </summary>
public sealed partial class OnboardingTests
{
    private static readonly long BaseEnBytes = ModelCatalog.Find("base.en")!.Bytes;

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static string Visible(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>base.en "comes with Dictum": a file of its size beside the exe, recorded as having passed its SHA-256.</summary>
    private static void Bundle(Scratch scratch)
    {
        Directory.CreateDirectory(scratch.Paths.BundledModelsDir);
        var path = Path.Combine(scratch.Paths.BundledModelsDir, "ggml-base.en.bin");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write)) file.SetLength(BaseEnBytes);
        new ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(path);
    }

    private static ModelManager FakeModels() => new(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);

    private static (OnboardingFlow Flow, OnboardingWindow Window) Open(AppShell shell, Scratch scratch, long[]? clock = null)
    {
        var flow = new OnboardingFlow(shell, SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true);
        if (clock is not null) flow.Clock = () => clock[0];
        var window = new OnboardingWindow(flow);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (flow, window);
    }

    private static void Next(OnboardingWindow window)
    {
        Click(window.PrimaryButton);
        Dispatcher.UIThread.RunJobs();
    }

    private static T GoTo<T>(OnboardingWindow window) where T : OnboardingStep
    {
        while (window.Current is not T) Next(window);
        return (T)window.Current!;
    }

    private static JsonElement? Key(string settingsFile, string a, string b)
    {
        if (!File.Exists(settingsFile)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
        return doc.RootElement.TryGetProperty(a, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(b, out var v) ? v.Clone() : null;
    }

    /// <summary>A microphone whose level the test sets; it records whether it is open.</summary>
    private sealed class LevelCapture : IAudioCapture
    {
        public float Level { get; set; }
        public bool Open { get; private set; }
        public string? Device { get; private set; }
        public Exception? Refuse { get; init; }
        public float CurrentLevel => Open ? Level : 0f;

        public event Action<Exception>? Failed { add { } remove { } }

        public void Start(string? deviceSetting, long keyDownStamp)
        {
            if (Refuse is not null) throw Refuse;
            Device = deviceSetting;
            Open = true;
        }

        public CapturedAudio Stop()
        {
            Open = false;
            return new CapturedAudio(new float[16000], 0f);
        }
    }

    // ---- The window: each step renders and advances ----

    /// <summary>
    /// Pin: the six steps in order, each with its step line and heading, each moved on by its primary button; leaving the
    /// microphone test ends steps 1-5 (the shell then loads the settings) and shows Try it, whose Back is gone.
    /// </summary>
    [Fact]
    public Task Each_step_renders_and_advances_to_Try_it() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var ended = 0;
        window.FirstStepsEnded += () => ended++;
        Assert.Equal([OnboardingText.WelcomeName, OnboardingText.FolderName, OnboardingText.SpeechName, OnboardingText.HotkeyName, OnboardingText.MicName, OnboardingText.TryName],
            window.Steps.Select(s => s.StepName));
        Assert.Equal(OnboardingText.Title, window.Title);
        for (var i = 0; i < window.Steps.Count; i++)
        {
            var step = window.Current!;
            Assert.Same(window.Steps[i], step);
            var text = Visible(window);
            Assert.Contains(OnboardingText.StepLine(i + 1, 6, step.StepName), text);
            Assert.Contains(step.Heading, text);
            if (i == 0) Assert.Contains(OnboardingText.WelcomeText, text);
            Assert.Equal(step.PrimaryLabel, window.PrimaryButton.Content);
            Assert.True(window.PrimaryButton.IsDefault);
            Assert.Equal(i is > 0 and < 5, window.BackButton.IsVisible); // none on Welcome, none once the settings are loaded
            if (i == 5) break;
            Assert.False(flow.FirstStepsDone);
            Next(window);
        }
        Assert.IsType<TryItStep>(window.Current);
        Assert.True(flow.FirstStepsDone);
        Assert.False(flow.Abandoned);
        Assert.Equal(1, ended);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: Back returns to the step before and keeps what it showed.</summary>
    [Fact]
    public Task Back_returns_to_the_step_before() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        GoTo<SpeechStep>(window);
        Click(window.BackButton);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<FolderStep>(window.Current);
        window.Close();
        shell.Dispose();
    });

    // ---- 2. The model folder ----

    /// <summary>Pin: Next on the suggested folder writes no models.folder; another folder is written as chosen, and only then.</summary>
    [Fact]
    public Task The_folder_step_writes_models_folder_only_when_changed() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var folderStep = GoTo<FolderStep>(window);
        Assert.Contains(OnboardingText.FolderText, Visible(window));
        Assert.Equal(scratch.Paths.DefaultModelsDir, All<TextBox>(folderStep).Single(t => AutomationProperties.GetName(t) == OnboardingText.FolderBoxName).Text);
        Next(window);
        Assert.IsType<SpeechStep>(window.Current);
        Assert.False(File.Exists(scratch.Paths.SettingsFile)); // nothing changed: nothing written

        Click(window.BackButton);
        Dispatcher.UIThread.RunJobs();
        var other = Path.Combine(scratch.Root.FullName, "D-drive", "Dictum models");
        folderStep.Pick(other);
        Assert.Equal(other, flow.ChosenFolder);
        Assert.Contains(other, Visible(window) + All<TextBox>(folderStep).Single().Text);
        Next(window);
        Assert.Equal(other, Key(scratch.Paths.SettingsFile, "models", "folder")?.GetString());

        // Back, then the suggested folder again: models.folder goes back to null (the default), as Settings > Models' Use default.
        Click(window.BackButton);
        Dispatcher.UIThread.RunJobs();
        Click(All<Button>(folderStep).Single(b => b.Content as string == OnboardingText.UseSuggested));
        Next(window);
        Assert.Equal(JsonValueKind.Null, Key(scratch.Paths.SettingsFile, "models", "folder")?.ValueKind);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the folder of the models that come with Dictum, and a device path, are refused with their own words; nothing is written.</summary>
    [Fact]
    public Task The_folder_step_refuses_the_bundled_folder_and_a_device_path() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var folderStep = GoTo<FolderStep>(window);
        folderStep.Pick(scratch.Paths.BundledModelsDir);
        Assert.Contains(ModelCoordinator.BundledFolderRefused, Visible(window));
        Assert.Null(flow.ChosenFolder);
        folderStep.Pick(Path.Combine(scratch.Paths.BundledModelsDir, "inside"));
        Assert.Contains(ModelCoordinator.BundledFolderRefused, Visible(window));
        folderStep.Pick(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Models");
        Assert.Contains(ModelCoordinator.DevicePathRefused, Visible(window));
        Assert.Null(flow.ChosenFolder);
        Next(window);
        Assert.False(File.Exists(scratch.Paths.SettingsFile));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: a settings.json that already names a model folder (an admin's seed, or a start after the folder step) skips the step.</summary>
    [Fact]
    public Task A_named_model_folder_skips_the_folder_step() => UiSession.Run(() =>
    {
        var seeded = Path.Combine(Path.GetTempPath(), "dictum-seeded-models").Replace("\\", "\\\\");
        using var scratch = new Scratch($$"""{ "models": { "folder": "{{seeded}}" } }""");
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        Assert.DoesNotContain(window.Steps, s => s is FolderStep);
        Assert.Equal(5, window.Steps.Count);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the free-space line names the drive and its free space as Dictum writes sizes; unknown free space shows no line.</summary>
    [Fact]
    public void The_free_space_line_names_the_drive()
    {
        Assert.Equal("C: has 41.0 GB free", FolderStep.FreeLine(@"C:\Users\Kyle\AppData\Local\Dictum\models", _ => 41_000_000_000));
        Assert.Equal(@"\\server\share has 812 MB free", FolderStep.FreeLine(@"\\server\share\models", _ => 812_400_000));
        Assert.Null(FolderStep.FreeLine(@"C:\models", _ => null));
    }

    // ---- 3. The speech model ----

    /// <summary>Pin: with base.en on this PC the speech step is one line and no choice, and Next writes base.en and the rule-based tidy.</summary>
    [Fact]
    public Task The_speech_step_asks_nothing_when_base_en_is_on_this_PC() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        var speech = GoTo<SpeechStep>(window);
        Assert.Contains(OnboardingText.SpeechIncluded, Visible(window));
        Assert.Empty(All<RadioButton>(speech));
        Next(window);
        var written = SettingsLoader.LoadFile(scratch.Paths.SettingsFile);
        Assert.True(written.Usable);
        Assert.Equal(("base.en", LlmSettings.NoProvider), (written.Settings.Whisper.Model, written.Settings.Llm.Provider));
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin: without base.en the speech step offers the Simple view's three tiers, the recommendation chosen, and every model on
    /// request; the choice is written as the first-run picker's was.
    /// </summary>
    [Fact]
    public Task Without_base_en_the_speech_step_offers_the_three_tiers() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell(); // a CPU-only machine
        var (_, window) = Open(shell, scratch);
        var speech = GoTo<SpeechStep>(window);
        var radios = All<RadioButton>(speech).ToList();
        Assert.Equal([SimpleView.StandardLabel, SimpleView.MoreAccurateLabel, SimpleView.MostAccurateLabel], radios.Select(AutomationProperties.GetName));
        Assert.Equal(ModelCatalog.Recommend(false).Model, radios.Single(r => r.IsChecked == true).Tag);
        Assert.Contains(SimpleView.StandardSubLineNotIncluded, Visible(window));
        Assert.DoesNotContain(SimpleView.StandardSubLine, Visible(window)); // "Included" would be untrue here
        Assert.Contains($"({ModelCatalog.Find("small.en")!.SizeText})", Visible(window)); // its size until it is on this PC

        Click(All<Button>(speech).Single(b => b.Content as string == OnboardingText.ShowEveryModel));
        Assert.Equal(ModelCatalog.Entries.Count, All<RadioButton>(speech).Count());

        All<RadioButton>(speech).Single(r => (string?)r.Tag == "small.en").IsChecked = true;
        Next(window);
        Assert.Equal("small.en", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Whisper.Model);
        // m1a (final lens): no clean-up question in the window, so the rule-based tidy is written, as with base.en.
        Assert.Equal(LlmSettings.NoProvider, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (merge with lane A's lens fixes): a seeded language that is not English gets the multilingual tiers in the speech
    /// step, with no English-only model offered, and the one chosen is written.
    /// </summary>
    [Fact]
    public Task A_seeded_language_that_is_not_English_gets_the_multilingual_tiers() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "whisper": { "language": "es" } }""");
        var (shell, _, _) = scratch.Shell(); // a CPU-only machine, no base.en
        var (_, window) = Open(shell, scratch);
        var speech = GoTo<SpeechStep>(window);
        var tags = All<RadioButton>(speech).Select(r => (string?)r.Tag).ToList();
        Assert.Equal(SimpleView.Tiers(false, "es").Where(t => t.Guard is null).Select(t => t.Model), tags);
        Assert.DoesNotContain(tags, t => t!.EndsWith(".en", StringComparison.Ordinal));
        Assert.Single(All<RadioButton>(speech), r => r.IsChecked == true);
        All<RadioButton>(speech).Single(r => (string?)r.Tag == SimpleView.MultilingualMoreAccurate).IsChecked = true;
        Next(window);
        Assert.Equal(SimpleView.MultilingualMoreAccurate, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Whisper.Model);
        window.Close();
        shell.Dispose();
    });

    // ---- 4. The hotkey (the first-run hotkey check's pins, moved with it) ----

    /// <summary>
    /// Pin (moved from DialogTests' hotkey check): the step listens from the start, keeps the key pressed when "Use this hotkey"
    /// is pressed and writes it; Skip keeps the current one and writes nothing; leaving the step stops listening.
    /// </summary>
    [Fact]
    public Task The_hotkey_step_keeps_the_key_pressed_or_the_current_one() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "RightCtrl" }""");
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        GoTo<HotkeyStep>(window);
        var text = Visible(window);
        Assert.Contains(HotkeyCheck.MacNote, text);
        Assert.Contains(HotkeyCheck.Intro, text);
        Assert.Equal(HotkeyCheck.UseLabel, window.PrimaryButton.Content);
        Assert.Equal(HotkeyCheck.SkipLabel, window.SecondaryButton.Content);
        var monitor = platform.LastMonitor!; // listening from the start
        Assert.True(monitor.Started);
        monitor.Press(Vk.RMenu, down: true);
        monitor.Press(Vk.RMenu, down: false);
        Dispatcher.UIThread.RunJobs();
        Next(window);
        Assert.Equal("RightAlt", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Hotkey);
        Assert.True(monitor.Disposed); // leaving the step stops listening

        Click(window.BackButton); // and Skip keeps the hotkey as it is
        Dispatcher.UIThread.RunJobs();
        var again = platform.LastMonitor!;
        again.Press(Vk.Pause, down: true);
        again.Press(Vk.Pause, down: false);
        Dispatcher.UIThread.RunJobs();
        Click(window.SecondaryButton);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<MicrophoneStep>(window.Current);
        Assert.Equal("RightAlt", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Hotkey);
        window.Close();
        shell.Dispose();
    });

    // ---- Esc, and the flows that must not change ----

    /// <summary>
    /// Pin: Esc mid-way (a folder chosen but its step not completed) closes the window, writes today's defaults for what is left
    /// (base.en and the rule-based tidy), leaves the folder unwritten, and gives a valid settings.json; Try it never shows.
    /// </summary>
    [Fact]
    public Task Esc_mid_way_leaves_the_defaults_and_a_valid_settings_file() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var folderStep = GoTo<FolderStep>(window);
        folderStep.Pick(Path.Combine(scratch.Root.FullName, "elsewhere"));
        window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.True(flow.Abandoned);
        var written = SettingsLoader.LoadFile(scratch.Paths.SettingsFile);
        Assert.True(written.Usable);
        Assert.Empty(written.Warnings);
        Assert.Equal(("base.en", LlmSettings.NoProvider, null), (written.Settings.Whisper.Model, written.Settings.Llm.Provider, written.Settings.Models.Folder));
        Assert.Equal(new AppSettings().Hotkey, written.Settings.Hotkey);
        flow.BeginTryIt();
        Assert.False(flow.TryItLive);
        shell.Dispose();
    });

    /// <summary>Pin: the shell's first run with the window closed at once (ScriptedDialogs skips) writes the defaults and starts.</summary>
    [Fact]
    public Task A_first_run_with_every_step_skipped_starts_on_the_defaults() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, dialogs) = scratch.Shell(FakeModels());
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 1), dialogs.FirstRunSteps);
            var written = SettingsLoader.LoadFile(scratch.Paths.SettingsFile);
            Assert.Equal(("base.en", LlmSettings.NoProvider), (written.Settings.Whisper.Model, written.Settings.Llm.Provider));
            Assert.True(dialogs.LastOnboarding!.Abandoned); // every step skipped is the window closed early: Try it does not follow
            Assert.False(dialogs.LastOnboarding.TryItLive);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Pin: a seeded settings.json that names whisper.model, and a --model override, open no window (as no hotkey check opened before).</summary>
    [Fact]
    public Task A_seeded_model_or_a_model_override_opens_no_window() => UiSession.Run(() =>
    {
        using (var seeded = new Scratch("""{ "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }"""))
        {
            Bundle(seeded);
            var (shell, _, dialogs) = seeded.Shell(FakeModels());
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 0), dialogs.FirstRunSteps);
            shell.Dispose();
        }
        using (var scripted = new Scratch())
        {
            var model = Path.Combine(scripted.Root.FullName, "fake-model.bin");
            File.WriteAllText(model, "");
            var (shell, _, dialogs) = scripted.Shell(FakeModels(), new ShellOptions(ModelOverride: model));
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 0), dialogs.FirstRunSteps);
            Assert.Equal(ModelCatalog.Recommend(false).Model, SettingsLoader.LoadFile(scripted.Paths.SettingsFile).Settings.Whisper.Model); // FirstRun's own answer, unchanged
            shell.Dispose();
        }
    });

    /// <summary>Pin: the rule the shell uses: the window only while the speech choice is open and no override names the model; the folder step only while no folder is named.</summary>
    [Fact]
    public void The_window_and_its_folder_step_follow_settings_json()
    {
        Assert.True(AppRules.Onboards(SettingsLoader.Parse("{}"), modelOverride: false));
        Assert.False(AppRules.Onboards(SettingsLoader.Parse("{}"), modelOverride: true));
        Assert.False(AppRules.Onboards(SettingsLoader.Parse("""{ "whisper": { "model": "small.en" } }"""), modelOverride: false));
        Assert.False(AppRules.Onboards(SettingsLoader.Parse("{ not json"), modelOverride: false)); // never written over
        Assert.True(AppRules.AsksFolder(SettingsLoader.Parse("{}")));
        Assert.False(AppRules.AsksFolder(SettingsLoader.Parse("""{ "models": { "folder": "D:\\Models" } }""")));
        Assert.False(AppRules.AsksFolder(SettingsLoader.Parse("""{ "whisper": { "models_dir": "D:\\Models" } }""")));
    }

    // ---- Lens fixes (21/10/2026), lens on 6bacce9 ----

    /// <summary>
    /// Pin (lens 1): base.en on this PC and a seeded language that is not English: the speech step offers the multilingual
    /// tiers instead of "included and ready", and the pick is written over base.en; Esc there keeps base.en.
    /// </summary>
    [Fact]
    public Task With_base_en_a_seeded_language_that_is_not_English_still_asks() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "whisper": { "language": "es" } }""");
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        var speech = GoTo<SpeechStep>(window);
        Assert.DoesNotContain(OnboardingText.SpeechIncluded, Visible(window));
        var tags = All<RadioButton>(speech).Select(r => (string?)r.Tag).ToList();
        Assert.Equal(SimpleView.Tiers(false, "es").Where(t => t.Guard is null).Select(t => t.Model), tags);
        All<RadioButton>(speech).Single(r => (string?)r.Tag == SimpleView.MultilingualStandard).IsChecked = true;
        Next(window);
        Assert.Equal(SimpleView.MultilingualStandard, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Whisper.Model);
        window.Close();
        shell.Dispose();

        using var skipped = new Scratch("""{ "whisper": { "language": "es" } }""");
        Bundle(skipped);
        var (shell2, _, _) = skipped.Shell();
        var (_, window2) = Open(shell2, skipped);
        GoTo<SpeechStep>(window2);
        window2.Close(); // Esc: skip the rest
        Assert.Equal("base.en", SettingsLoader.LoadFile(skipped.Paths.SettingsFile).Settings.Whisper.Model);
        shell2.Dispose();
    });

    /// <summary>Pin (lens 8): "the included one" only with base.en on this PC; the accent explainer names the Standard model only when it is in use.</summary>
    [Fact]
    public Task The_copy_follows_what_is_on_this_PC_and_what_was_chosen() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell(); // no base.en
        var (_, window) = Open(shell, scratch);
        GoTo<FolderStep>(window);
        Assert.Contains(OnboardingText.FolderTextNotIncluded, Visible(window));
        Assert.DoesNotContain(OnboardingText.FolderText, Visible(window));
        var tryIt = (TryItStep)window.Steps.Last();
        Assert.Equal(OnboardingText.AccentExplainer, tryIt.Explainer(new AppSettings { Whisper = new WhisperSettings { Model = "base.en" } }));
        Assert.Equal(OnboardingText.AccentExplainerChosen, tryIt.Explainer(new AppSettings { Whisper = new WhisperSettings { Model = "small.en" } }));
        Assert.Equal(OnboardingText.AccentExplainer, tryIt.Explainer(new AppSettings { Whisper = new WhisperSettings { Model = "small", Language = "es" } }));
        window.Close();
        shell.Dispose();

        using var bundled = new Scratch();
        Bundle(bundled);
        var (shell2, _, _) = bundled.Shell();
        var (_, window2) = Open(shell2, bundled);
        GoTo<FolderStep>(window2);
        Assert.Contains(OnboardingText.FolderText, Visible(window2));
        window2.Close();
        shell2.Dispose();
    });

    // ---- Final lens (21/10/2026), lens on 4b5ee4f ----

    /// <summary>
    /// Pin (M3, PR #21's safety on the wizard path): the plan's GPU probe is given settings.json's whisper.gpu, so a seeded
    /// "cpu" is what the runtime probe sees (the shell's own settings are not loaded yet, and default to auto).
    /// </summary>
    [Fact]
    public Task The_wizard_probes_the_runtime_with_the_seeded_gpu_setting() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "whisper": { "gpu": "cpu" } }""");
        var probed = new List<string>();
        var (shell, _, _) = scratch.Shell(gpuMachine: null, runtimeProbe: (gpu, _) =>
        {
            probed.Add(gpu);
            return "CPU";
        });
        var flow = new OnboardingFlow(shell, SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true);
        _ = flow.Plan;
        Assert.Equal(["cpu"], probed);
        shell.Dispose();
    });

    /// <summary>
    /// Pin (m1b): the plan is looked up again after a folder is chosen. A build without base.en, and a folder chosen that
    /// already holds ggml-base.en.bin: the speech step says the Standard model is ready, and asks nothing.
    /// </summary>
    [Fact]
    public Task The_plan_is_looked_up_again_after_a_folder_is_chosen() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell(); // no base.en beside the exe
        var (flow, window) = Open(shell, scratch);
        var folderStep = GoTo<FolderStep>(window);
        Assert.Contains(OnboardingText.FolderTextNotIncluded, Visible(window)); // the plan was read here: no base.en yet
        var other = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "D-drive", "Dictum models")).FullName;
        using (var file = new FileStream(Path.Combine(other, "ggml-base.en.bin"), FileMode.Create, FileAccess.Write)) file.SetLength(BaseEnBytes);
        folderStep.Pick(other);
        Next(window);
        var speech = Assert.IsType<SpeechStep>(window.Current);
        Assert.NotNull(flow.Plan.BundledPath);
        Assert.Contains(OnboardingText.SpeechIncluded, Visible(window));
        Assert.Empty(All<RadioButton>(speech));
        window.Close();
        shell.Dispose();
    });
}
