using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia;
using Dictum.App;
using Dictum.Audio;
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
public sealed class OnboardingTests
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

    // ---- 5. The microphone test ----

    private static (MicrophoneStep Step, OnboardingWindow Window, LevelCapture Capture, long[] Clock) MicTest(Scratch scratch, AppShell shell, FakePlatform platform, LevelCapture? capture = null)
    {
        var mic = capture ?? new LevelCapture();
        platform.CaptureFactory = () => mic;
        var clock = new long[] { 1_000_000 };
        var (_, window) = Open(shell, scratch, clock);
        var step = GoTo<MicrophoneStep>(window);
        return (step, window, mic, clock);
    }

    private static void Advance(MicrophoneStep step, long[] clock, long ms)
    {
        for (var t = 0L; t < ms; t += 50)
        {
            clock[0] += 50;
            step.Tick();
        }
    }

    /// <summary>Pin: a level above the floor for about three seconds says "Heard you." (and not before).</summary>
    [Fact]
    public Task The_microphone_test_hears_a_level_above_the_floor() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        Assert.True(mic.Open);
        Assert.Contains(OnboardingText.SayWords, Visible(window));
        mic.Level = 0.02f;
        Advance(step, clock, 2_900);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        Advance(step, clock, 150);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: five seconds of silence gives the advice; a sound later can still turn it into "Heard you.".</summary>
    [Fact]
    public Task The_microphone_test_gives_the_advice_after_five_seconds_of_silence() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0f; // a virtual desktop with no microphone redirection: digital silence
        Advance(step, clock, 4_900);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        Advance(step, clock, 150);
        Assert.Equal(OnboardingText.NothingHeard, step.Result);
        mic.Level = 0.05f;
        Advance(step, clock, 3_050);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: with no microphone listed, the no-microphone advice and no capture opened.</summary>
    [Fact]
    public Task With_no_microphone_listed_the_test_says_so_and_opens_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.Devices.Clear();
        var captures = platform.Captures.Count; // the shell's own, made when it was built
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.Equal(OnboardingText.NoMicrophone, step.Result);
        Assert.Contains(OnboardingText.NoMicrophone, Visible(window));
        Assert.Equal(captures, platform.Captures.Count);
        Assert.False(mic.Open);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the test's capture is closed on every way out of the step: Next, Skip, Back, and closing the window.</summary>
    [Theory]
    [InlineData("next")]
    [InlineData("skip")]
    [InlineData("back")]
    [InlineData("close")]
    public Task The_test_capture_is_stopped_on_leaving_the_step(string how) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.True(mic.Open);
        Assert.Same(mic, step.Capture);
        switch (how)
        {
            case "next": Click(window.PrimaryButton); break;
            case "skip": Click(window.SecondaryButton); break;
            case "back": Click(window.BackButton); break;
            default: window.Close(); break;
        }
        Dispatcher.UIThread.RunJobs();
        Assert.False(mic.Open, $"the microphone stayed open after {how}");
        Assert.Null(step.Capture);
        if (window.IsVisible) window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: another microphone chosen is tested at once and written at Next; Skip writes nothing.</summary>
    [Fact]
    public Task The_microphone_chosen_is_tested_and_written_at_Next() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.Devices.Add(new AudioDeviceInfo("id-2", "Headset", false));
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.Null(mic.Device); // the system default first
        Assert.Equal(["System default", "Test microphone (default)", "Headset"], Assert.IsAssignableFrom<IEnumerable<string>>(step.DevicePicker.ItemsSource));
        step.DevicePicker.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Headset", mic.Device);
        Assert.True(mic.Open);
        Next(window);
        Assert.Equal("Headset", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Audio.Device);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: with reduced motion the meter moves at most twice a second; otherwise on every look.</summary>
    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 4)]
    public Task The_meter_respects_reduced_motion(bool reduced, int redraws) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.Flags.Reduced = reduced;
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.1f;
        var before = step.MeterRedraws;
        Advance(step, clock, 2_000);
        Assert.Equal(redraws, step.MeterRedraws - before);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: a microphone that cannot be opened says why, in Settings > Microphone's words.</summary>
    [Fact]
    public Task A_microphone_that_cannot_be_opened_says_why() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, _, _) = MicTest(scratch, shell, platform, new LevelCapture { Refuse = new InvalidOperationException("Device in use") });
        Assert.Equal(OnboardingText.CouldNotOpen("Device in use"), step.Result);
        window.Close();
        shell.Dispose();
    });

    // ---- 6. Try it, and the shell's own path ----

    /// <summary>
    /// Pin: through the shell's own first run (Start, with the window driven as a person would), steps 1-5 write their answers,
    /// the settings load, Try it waits for the speech model, and a dictation through the real hotkey path shows the accent
    /// explainer and Calibrate my voice; Finish closes the window.
    /// </summary>
    [Fact]
    public Task Try_it_after_the_first_dictation_shows_the_accent_explainer() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, dialogs) = scratch.Shell(FakeModels());
        OnboardingWindow? window = null;
        dialogs.Onboarding = flow =>
        {
            window = new OnboardingWindow(flow);
            window.Show();
            while (window.Current is not TryItStep) Next(window);
        };
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 1), dialogs.FirstRunSteps); // the window, once; no picker
            var tryIt = Assert.IsType<TryItStep>(window!.Current);
            Assert.True(dialogs.LastOnboarding!.TryItLive);
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            tryIt.Tick();
            Assert.Equal(OnboardingText.TryInstruction("RightCtrl"), tryIt.Instruction);
            Assert.False(window.SecondaryButton.IsVisible); // Calibrate my voice comes after the first dictation

            var listener = platform.LastListener!;
            var t0 = Environment.TickCount64 + 60_000;
            listener.Post(new HookEvent(HotkeySignal.Down, t0));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            listener.Post(new HookEvent(HotkeySignal.Up, t0 + 1_500));
            Assert.True(Eventually(() => shell.Personal.Last is not null), "nothing was pasted");
            tryIt.Tick();
            Assert.True(tryIt.Succeeded);
            Assert.Contains(OnboardingText.AccentExplainer, Visible(window));
            Assert.True(window.SecondaryButton.IsVisible);
            Assert.Equal(ModelsPage.CalibrateLabel, window.SecondaryButton.Content);
            Click(window.PrimaryButton); // Finish
            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window?.IsVisible == true) window.Close();
            shell.Dispose();
        }
    });

    /// <summary>Pin: with nothing pasted 30 seconds after the model is ready, the try-later line; Finish still closes.</summary>
    [Fact]
    public Task Try_it_says_nothing_came_through_after_thirty_seconds() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, dialogs) = scratch.Shell(FakeModels());
        var clock = new long[] { 5_000_000 };
        OnboardingWindow? window = null;
        dialogs.Onboarding = flow =>
        {
            flow.Clock = () => clock[0];
            window = new OnboardingWindow(flow);
            window.Show();
            while (window.Current is not TryItStep) Next(window);
        };
        try
        {
            Assert.True(shell.Start(lifetime: null));
            var tryIt = Assert.IsType<TryItStep>(window!.Current);
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            tryIt.Tick(); // ready from now
            clock[0] += TryItStep.NothingAfterMs - 1;
            tryIt.Tick();
            Assert.False(tryIt.NothingShown);
            clock[0] += 1;
            tryIt.Tick();
            Assert.True(tryIt.NothingShown);
            Assert.Contains(OnboardingText.NothingCame, Visible(window));
            Assert.True(window.PrimaryButton.IsEnabled);
            Click(window.PrimaryButton);
            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window?.IsVisible == true) window.Close();
            shell.Dispose();
        }
    });

    /// <summary>Pin: before the shell has loaded (steps 1-5 just ended), Try it says the model is getting ready, not the instruction.</summary>
    [Fact]
    public Task Try_it_waits_for_the_shell() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var tryIt = GoTo<TryItStep>(window);
        Assert.False(flow.TryItLive);
        Assert.Equal(OnboardingText.TryLoading, tryIt.Instruction);
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

    // ---- Accessibility and size ----

    private const double WorkAreaDip = 512 - 48;
    private const double CaptionDip = 32;

    /// <summary>
    /// Pin: every step fits a 1366 × 768 screen at 150 % (911 × 512 DIP, less a 48 px taskbar: 480 DIP), keeps its buttons inside
    /// the window (as the busy question's layout pin), names every control a keyboard reaches, and uses the larger text.
    /// </summary>
    [Fact]
    public Task Every_step_fits_at_150_percent_and_names_its_controls() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell(); // without base.en: the speech step's longest form
        var (_, window) = Open(shell, scratch);
        // 1366 × 768 at 150 % is 911 × 512 DIP; less the Windows 11 taskbar (recalled: 48 DIP) the work area is 464 DIP, and
        // the whole window is the client plus a caption (recalled: about 32 DIP).
        Assert.True(OnboardingWindow.WindowWidth <= 911 && OnboardingWindow.WindowHeight + CaptionDip <= WorkAreaDip, $"{OnboardingWindow.WindowHeight} + {CaptionDip} > {WorkAreaDip}");
        while (true)
        {
            var step = window.Current!;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(OnboardingWindow.WindowHeight, window.ClientSize.Height);
            foreach (var button in new[] { window.PrimaryButton, window.SecondaryButton, window.BackButton }.Where(b => b.IsEffectivelyVisible))
            {
                var right = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width, 0), window)!.Value.X;
                var bottom = button.TranslatePoint(new Avalonia.Point(0, button.Bounds.Height), window)!.Value.Y;
                Assert.True(right <= window.ClientSize.Width - 24 + 0.5, $"{step.StepName}: \"{button.Content}\" ends at {right:0} of {window.ClientSize.Width:0}");
                Assert.True(bottom <= window.ClientSize.Height + 0.5, $"{step.StepName}: \"{button.Content}\" is below the window");
            }
            foreach (var control in All<Control>(step).Where(c => c is Button or RadioButton or ComboBox or TextBox && c.IsEffectivelyVisible))
            {
                var name = AutomationProperties.GetName(control);
                var label = control is ContentControl { Content: string s } ? s : null;
                Assert.False(string.IsNullOrWhiteSpace(name ?? label), $"{step.StepName}: a {control.GetType().Name} has no name");
                Assert.True(control.Focusable && control.IsTabStop, $"{step.StepName}: {name ?? label} is not reached by Tab");
            }
            foreach (var body in All<TextBlock>(step).Where(t => t.Classes.Contains("body") && t.IsEffectivelyVisible))
                Assert.True(body.FontSize >= 17, $"{step.StepName}: body text at {body.FontSize}");
            if (step is TryItStep) break;
            Next(window);
        }
        window.Close();
        shell.Dispose();
    });

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

    /// <summary>Pin (lens 2): background hiss (0.005, under silence_threshold 0.01) is not "Heard you." and not digital silence either.</summary>
    [Fact]
    public Task Hiss_under_the_silence_threshold_is_not_heard() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.005f;
        Advance(step, clock, 6_000);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        mic.Level = 0.02f;
        Advance(step, clock, 3_050);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 2): the threshold is settings.json's silence_threshold, not a constant.</summary>
    [Fact]
    public Task The_microphone_test_uses_the_settings_silence_threshold() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "silence_threshold": 0.05 }""");
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.02f;
        Advance(step, clock, 3_500);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 3): the client height is clamped to the work area less the frame; a roomy screen keeps it.</summary>
    [Fact]
    public void The_window_height_is_clamped_to_the_work_area()
    {
        Assert.Equal(OnboardingWindow.WindowHeight, OnboardingWindow.FitHeight(OnboardingWindow.WindowHeight, CaptionDip, 1040));
        Assert.Equal(400, OnboardingWindow.FitHeight(OnboardingWindow.WindowHeight, 40, 440));
        Assert.Equal(300, OnboardingWindow.FitHeight(OnboardingWindow.WindowHeight, 40, 340)); // under the minimum: fits all the same
    }

    /// <summary>Pin (lens 4): Try it puts the keyboard focus in the practice box, not on Finish.</summary>
    [Fact]
    public Task Try_it_focuses_the_practice_box() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        var tryIt = GoTo<TryItStep>(window);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(tryIt.Box, window.FocusManager!.GetFocusedElement());
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 5): the hotkey step gives focus to nothing, as the old hotkey window did, so Space cannot press a button.</summary>
    [Fact]
    public Task The_hotkey_step_focuses_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        GoTo<HotkeyStep>(window);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.FocusManager!.GetFocusedElement() is Button, $"focus is on {window.FocusManager.GetFocusedElement()}");
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 7): the test closes the microphone about 5 s after "Heard you." and offers Test again, which opens it again.</summary>
    [Fact]
    public Task The_microphone_closes_after_hearing_you_and_can_test_again() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.05f;
        Advance(step, clock, 3_000);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        Assert.True(mic.Open);
        Advance(step, clock, (int)MicrophoneStep.StopAfterHeardMs);
        Assert.False(mic.Open);
        Assert.Null(step.Capture);
        Assert.True(step.TestAgainShown);
        Assert.Equal(OnboardingText.HeardYou, step.Result); // the result stays
        Click(All<Button>(step).Single(b => b.Content as string == OnboardingText.TestAgain));
        Assert.True(mic.Open);
        Assert.False(step.TestAgainShown);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 7): whatever it hears, the test closes the microphone after a minute.</summary>
    [Fact]
    public Task The_microphone_closes_after_a_minute() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.005f; // hiss: never heard, never digital silence
        Advance(step, clock, (int)MicrophoneStep.StopAfterMs - 100);
        Assert.True(mic.Open);
        Advance(step, clock, 100);
        Assert.False(mic.Open);
        Assert.True(step.TestAgainShown);
        window.Close();
        shell.Dispose();
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

    /// <summary>Pin (lens 9): a microphone list that throws (the audio service stopped) is the no-microphone sentence, and nothing is opened.</summary>
    [Fact]
    public Task A_microphone_list_that_throws_says_no_microphone() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.ListDevicesThrows = true;
        var captures = platform.Captures.Count;
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.Equal(OnboardingText.NoMicrophone, step.Result);
        Assert.Equal(captures, platform.Captures.Count);
        Assert.False(mic.Open);
        window.Close();
        shell.Dispose();
    });
}
