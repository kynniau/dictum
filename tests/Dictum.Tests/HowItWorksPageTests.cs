using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Settings > See how it works, the page (SPEC §29): it is in the Settings list (keyboard included), a recording made on it
/// fills the five stages in order with the pipeline's own texts and timings, a rejected reply shows the validator's reason,
/// nothing is pasted and no file is written, no microphone gives the one no-microphone sentence, the meter keeps the
/// reduced-motion rate, and the dictation hotkey records here while the page is in front. Headless: nothing reaches the desktop.
/// </summary>
public class HowItWorksPageTests
{
    private static readonly FieldInfo PipelineField = typeof(AppShell).GetField("_pipeline", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo RedirectField = typeof(AppShell).GetField("_hotkeyRedirect", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static Button Record(HowItWorksPage page) => All<Button>(page).Single(b => b.Content is HowItWorksText.RecordLabel or HowItWorksText.StopLabel);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static List<string?> Texts(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text)];

    /// <param name="speechReady">
    /// True: the page takes a speech model as ready (an unstarted shell has none; the tests that use it put scripted engines in
    /// the shell's pipeline). False: the shell's own state.
    /// </param>
    private static HowItWorksPage Open(SettingsWindow window, bool speechReady = true)
    {
        window.ShowPage(HowItWorksText.PageTitle);
        Dispatcher.UIThread.RunJobs();
        var page = Assert.IsType<HowItWorksPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
        if (speechReady)
        {
            page.SpeechReady = () => true;
            page.Refresh();
        }
        return page;
    }

    private static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    /// <summary>Records one take on the page with Record, then Stop, and waits for the pipeline run to end.</summary>
    private static void RecordOnce(HowItWorksPage page)
    {
        Click(Record(page));
        Assert.True(page.IsRecording);
        Click(Record(page));
        Assert.False(page.IsRecording);
        Assert.True(Eventually(() => page.Running is { IsCompleted: true }), "the demonstration did not finish");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A pipeline over scripted parts in place of the shell's (the speech model and the clean-up in use); records any paste.</summary>
    private static List<string> UsePipeline(AppShell shell, ITranscriber transcriber, TranscriptCleaner cleaner)
    {
        var pasted = new List<string>();
        PipelineField.SetValue(shell, new DictationPipeline(_ => Task.FromResult(transcriber), () => cleaner, (text, _) =>
        {
            pasted.Add(text);
            return Task.CompletedTask;
        }, () => shell.Settings, shell.Log));
        return pasted;
    }

    [Fact]
    public Task The_page_is_in_the_settings_list_before_Help_and_the_keyboard_reaches_it_and_every_control() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        var nav = All<ListBox>(window).Single();
        var names = Assert.IsAssignableFrom<IEnumerable<string>>(nav.ItemsSource).ToList();
        Assert.Equal("See how it works", HowItWorksText.PageTitle);
        Assert.Equal(HowItWorksText.PageTitle, names[^2]); // just before Help, which stays last
        Assert.True(nav.ContainerFromIndex(names.Count - 2) is ListBoxItem { Focusable: true, IsEnabled: true }); // its entry takes keyboard focus

        nav.SelectedItem = "About";
        Dispatcher.UIThread.RunJobs();
        ((ListBoxItem)nav.ContainerFromItem("About")!).Focus(NavigationMethod.Tab); // Tab lands on the chosen entry
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(HowItWorksText.PageTitle, nav.SelectedItem);
        var page = Assert.IsType<HowItWorksPage>(All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl));
        page.SpeechReady = () => true; // an unstarted shell has no speech model
        page.Refresh();

        var shown = Texts(page);
        foreach (var text in new[] { HowItWorksText.PageTitle, HowItWorksText.Intro, HowItWorksText.MemoryNote, "or hold Right Ctrl" } // the hotkey in use (an unstarted shell: the default)
                     .Concat(HowItWorksText.Stages.SelectMany(s => new[] { s.Title, s.Description })))
            Assert.Contains(text, shown);
        var record = Record(page);
        Assert.Equal(HowItWorksText.RecordName, AutomationProperties.GetName(record));
        Assert.True(record.IsEnabled && record.IsTabStop);
        Assert.Equal(HowItWorksText.Stages[0].Title, AutomationProperties.GetName(All<Meter>(page).Single()));
        // Tab from Record reaches each stage in order, each named with its title and state.
        record.Focus(NavigationMethod.Tab);
        var stops = new List<string?>();
        IInputElement? current = record;
        for (var i = 0; i < 5; i++)
        {
            current = KeyboardNavigationHandler.GetNext(current!, NavigationDirection.Next);
            stops.Add(AutomationProperties.GetName((AvaloniaObjectOf(current))));
        }
        Assert.Equal(HowItWorksText.Stages.Select(s => $"{s.Title}. {HowItWorksText.Waiting}"), stops);
        window.Close();
        shell.Dispose();
    });

    private static Avalonia.StyledElement AvaloniaObjectOf(IInputElement? element) => Assert.IsAssignableFrom<Avalonia.StyledElement>(element);

    [Fact]
    public Task A_recording_fills_the_five_stages_in_order_with_the_pipelines_texts_and_timings_and_pastes_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polished = new TaskCompletionSource<LlmReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaner = new TranscriptCleaner(new FakeLlmClient(_ => polished.Task), CleanupPrompt.Default, TimeSpan.FromSeconds(30), NullLog.Instance, "fake/model (test)");
        var pasted = UsePipeline(shell, new FakeTranscriber("um so the invoice went out today", gate: heard.Task), cleaner);
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);

        Click(Record(page));
        Assert.Equal(HowItWorksText.StopName, AutomationProperties.GetName(Record(page)));
        Assert.Equal(StageState.Working, page.Stages[0].State);
        Assert.Contains(HowItWorksText.Recording, Texts(page));
        Click(Record(page)); // Stop: the speech model is working
        Assert.True(Eventually(() => page.Stages[1].Note == "Transcribing…"));
        Assert.Equal(["Done", "Working…", "Waiting", "Waiting", "Waiting"], page.Stages.Select(s => s.StateLabel));
        Assert.False(Record(page).IsEnabled); // one demonstration at a time

        heard.SetResult(); // the speech model finishes: its text shows, then the clean-up works
        Assert.True(Eventually(() => page.Stages[3].Note == "Polishing…"));
        Assert.Equal(["Done", "Done", "Working…", "Working…", "Waiting"], page.Stages.Select(s => s.StateLabel));
        Assert.Contains("um so the invoice went out today", Texts(page));
        // Each stage's text is a polite live region, read out as it arrives.
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(All<TextBlock>(page).Single(t => t.Text == "um so the invoice went out today")));

        polished.SetResult(new LlmReply("So the invoice went out today.", "end_turn"));
        Assert.True(Eventually(() => page.Running is { IsCompleted: true }));
        Dispatcher.UIThread.RunJobs();
        var stages = page.Stages;
        Assert.Equal(["Done", "Done", "Not used", "Done", "Done"], stages.Select(s => s.StateLabel));
        Assert.Equal(["2.0 s of sound", stages[1].Timing, null, stages[3].Timing, null], stages.Select(s => s.Timing));
        Assert.Matches(@"^Took \d+ ms$", stages[1].Timing);
        Assert.Matches(@"^Took \d+ ms$", stages[3].Timing);
        var shown = Texts(page);
        foreach (var stage in stages)
        {
            foreach (var text in new[] { stage.Text, stage.Timing, stage.Note }.Where(t => t is not null)) Assert.Contains(text, shown);
        }
        Assert.Equal("So the invoice went out today.", stages[4].Text);
        Assert.Contains(HowItWorksText.Accepted, shown);
        Assert.Contains(HowItWorksText.WouldPaste, shown);
        Assert.Equal(HowItWorksText.Done, page.Status);
        Assert.True(Record(page).IsEnabled); // Record again

        Assert.Empty(pasted); // the pipeline's paste was never called
        Assert.Empty(platform.Desktop.Ops); // nothing reached the clipboard or the keyboard
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_rejected_reply_shows_the_validators_reason_in_plain_words() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var cleaner = new TranscriptCleaner(FakeLlmClient.Replying("The fox does jump."), CleanupPrompt.Default, TimeSpan.FromSeconds(30), NullLog.Instance, "fake");
        var pasted = UsePipeline(shell, new FakeTranscriber("the fox does not jump"), cleaner);
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        RecordOnce(page);
        var shown = Texts(page);
        Assert.Contains("Not used: reply drops a negation. Dictum used the rule-based tidy (step 3) instead.", shown);
        Assert.Contains(HowItWorksText.TidyUsed, shown);
        Assert.Equal(DeterministicCleaner.Clean("the fox does not jump"), page.Stages[4].Text);
        Assert.Empty(pasted);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Through_the_shells_own_pipeline_nothing_is_pasted_and_no_file_is_written() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""
            { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false }, "llm": { "provider": "none" } }
            """);
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin"); // the fake engine transcribes every recording as this name
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model));
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            var window = new SettingsWindow(shell);
            window.Show();
            var page = Open(window, speechReady: false); // the shell's own model state: loaded
            var before = Files(scratch);
            var captures = platform.Captures.Count;

            RecordOnce(page);

            Assert.Equal(captures + 1, platform.Captures.Count); // a capture opened for this page only
            Assert.Equal("fake-model.bin", page.Stages[1].Text); // the speech model in use heard it
            Assert.Equal(HowItWorksText.TidyIsAll, page.Stages[2].Note); // the clean-up in use (provider none)
            Assert.Equal(DeterministicCleaner.Clean("fake-model.bin"), page.Stages[4].Text);
            Assert.Empty(platform.Desktop.Ops); // no paste: no clipboard, no keystrokes
            Assert.Equal(before, Files(scratch)); // no recording, transcript or anything else written
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Every file under the scratch profile with its size and last write: a written or changed file shows.</summary>
    private static List<string> Files(Scratch scratch) =>
        [.. Directory.EnumerateFiles(scratch.Root.FullName, "*", SearchOption.AllDirectories).Order().Select(f => $"{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}")];

    [Fact]
    public Task No_microphone_turns_Record_off_and_says_the_one_no_microphone_sentence() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Devices.Clear();
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, page.Status);
        Assert.Contains(NoSoundText.NoMicrophoneBalloon, Texts(page));
        Assert.False(Record(page).IsEnabled);
        page.OnHotkey(new HookEvent(HotkeySignal.Down, 0)); // nor does the hotkey open anything
        Assert.False(page.IsRecording);

        platform.Devices.Add(new AudioDeviceInfo("id-1", "Test microphone", true)); // one plugged in
        page.Refresh();
        Assert.True(Record(page).IsEnabled);
        Assert.Equal("", page.Status);

        platform.CaptureFactory = () => new ScriptedCapture { Refuse = new NoMicrophoneException() }; // listed, but none to open
        Click(Record(page));
        Assert.False(page.IsRecording);
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, page.Status);
        window.Close();
        shell.Dispose();
    });

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 2)]
    public Task The_meter_moves_every_tick_or_twice_a_second_with_reduced_motion(bool reduced, int redraws) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Flags.Reduced = reduced;
        platform.CaptureFactory = () => new ScriptedCapture { Level = 0.2f };
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        var clock = 0L;
        page.Clock = () => clock;
        Click(Record(page));
        for (var i = 0; i < 20; i++)
        {
            clock += 50; // one second of 50 ms ticks
            page.Tick();
        }
        Assert.Equal(redraws, page.MeterRedraws);
        page.Leave(); // leaving the page drops the recording
        Assert.False(page.IsRecording);
        Assert.Equal(HowItWorksText.Cancelled, page.Status);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_dictation_hotkey_records_here_only_while_the_page_is_shown() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var pasted = UsePipeline(shell, new FakeTranscriber("hello there"), TranscriptCleaner.RulesOnly("rules"));
        var window = new SettingsWindow(shell);
        window.Show();
        window.Activate();
        var page = Open(window);
        Assert.True(Eventually(() => RedirectField.GetValue(shell) is not null), "the page did not take the hotkey");

        page.OnHotkey(new HookEvent(HotkeySignal.Down, 0));
        Assert.True(page.IsRecording);
        page.OnHotkey(new HookEvent(HotkeySignal.Up, 1000));
        Assert.True(Eventually(() => page.Running is { IsCompleted: true }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DeterministicCleaner.Clean("hello there"), page.Stages[4].Text);
        Assert.Empty(pasted);

        page.OnHotkey(new HookEvent(HotkeySignal.Down, 2000));
        page.OnHotkey(new HookEvent(HotkeySignal.Escape, 2100)); // Esc drops the take
        Assert.False(page.IsRecording);
        Assert.Equal(HowItWorksText.Cancelled, page.Status);

        window.ShowPage("About"); // another page: the hotkey dictates again
        Dispatcher.UIThread.RunJobs();
        Assert.Null(RedirectField.GetValue(shell));
        window.Close();
        shell.Dispose();
    });

    private static readonly FieldInfo CleanerField = typeof(AppShell).GetField("_cleaner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public Task The_page_says_when_the_clean_up_sends_text_to_another_program_and_only_then() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        using var http = new HttpClient(); // the cleaners are built, never asked: nothing is sent
        var secrets = SecretStore.FromContents(null, null, _ => null);
        TranscriptCleaner Make(LlmSettings settings) => TranscriptCleaner.Create(settings, offlineOnly: false, secrets, http, NullLog.Instance);
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);

        var server = Make(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://127.0.0.1:9/v1", Model = "m" }); // a server on this PC: no key
        Assert.True(server.SendsText);
        CleanerField.SetValue(shell, server);
        page.Refresh();
        Assert.Equal(HowItWorksText.SendsText(server.Status), page.SendsTextLine);
        Assert.Contains(HowItWorksText.SendsText(server.Status), Texts(page));

        foreach (var quiet in new[] { Make(new LlmSettings { Provider = LlmSettings.Local }), Make(new LlmSettings { Provider = LlmSettings.NoProvider }), TranscriptCleaner.Off() })
        {
            Assert.False(quiet.SendsText);
            CleanerField.SetValue(shell, quiet);
            page.Refresh();
            Assert.Equal("", page.SendsTextLine);
            Assert.DoesNotContain(Texts(page), t => t?.StartsWith("Your clean-up sends text", StringComparison.Ordinal) == true);
        }
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_dictation_started_elsewhere_keeps_its_hotkey_and_Record_says_busy() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""
            { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false }, "llm": { "provider": "none" } }
            """);
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model));
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            var key = platform.LastListener!;
            var window = new SettingsWindow(shell);
            window.Show();
            window.Activate();
            window.ShowPage("About");
            Dispatcher.UIThread.RunJobs();

            var t0 = Environment.TickCount64 + 60_000; // ahead of the controller's clock, so its tick expires nothing meanwhile
            key.Post(new HookEvent(HotkeySignal.Down, t0)); // a dictation starts while another page is shown
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            var page = Open(window, speechReady: false);
            Assert.True(Eventually(() => RedirectField.GetValue(shell) is not null), "the page did not take the hotkey");

            Click(Record(page)); // Record while that dictation runs
            Assert.False(page.IsRecording);
            Assert.Equal(HowItWorksText.Busy, page.Status);

            key.Post(new HookEvent(HotkeySignal.Up, t0 + 1_000)); // its key-up reaches the dictation, not the page
            Assert.True(Eventually(() => shell.DictationIdle), "the dictation was not stopped by its own hotkey");
            Assert.True(Eventually(() => platform.Desktop.Ops.Contains($"set-text:{DeterministicCleaner.Clean("fake-model.bin")} ")), "the dictation was not pasted");
            Assert.False(page.IsRecording);
            Assert.Null(page.Running); // the page ran nothing
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task Stop_from_the_keyboard_moves_focus_to_the_steps() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        UsePipeline(shell, new FakeTranscriber("hello there"), TranscriptCleaner.RulesOnly("rules"));
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        var record = Record(page);
        record.Focus(NavigationMethod.Tab);
        Click(record);
        Click(record); // Stop: Record turns off while the steps run
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
        Assert.StartsWith(HowItWorksText.Stages[1].Title, AutomationProperties.GetName(AvaloniaObjectOf(focused)));
        Assert.True(Eventually(() => page.Running is { IsCompleted: true }));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_microphone_failure_drops_the_take_and_says_so() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        var capture = new ScriptedCapture();
        platform.CaptureFactory = () => capture;
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        Click(Record(page));
        Assert.True(page.IsRecording);
        capture.Fail(new InvalidOperationException("The device was unplugged.")); // the capture's Failed event, as WASAPI raises it
        Assert.True(Eventually(() => !page.IsRecording));
        Assert.Equal(HowItWorksText.Failed("The device was unplugged."), page.Status);
        Assert.Null(page.Running); // nothing ran
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_take_limit_stops_the_take_runs_it_and_says_so() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        UsePipeline(shell, new FakeTranscriber("hello there"), TranscriptCleaner.RulesOnly("rules"));
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        Click(Record(page));
        page.TakeLimitReached(); // what the 30 s timer calls
        Assert.False(page.IsRecording);
        Assert.Equal("Stopped at 30 s, the longest this page records.", page.Status);
        Assert.True(Eventually(() => page.Running is { IsCompleted: true }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DeterministicCleaner.Clean("hello there"), page.Stages[4].Text);
        Assert.Equal(HowItWorksText.LimitReached, page.Status); // the limit line stays, not "Done"
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Record_is_off_until_a_speech_model_is_ready() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell(); // not started: no speech model, none loading
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window, speechReady: false);
        Assert.False(Record(page).IsEnabled);
        Assert.Equal(HowItWorksText.NoSpeechModel, page.Status);
        page.OnHotkey(new HookEvent(HotkeySignal.Down, 0)); // nor does the hotkey record
        Assert.False(page.IsRecording);

        page.SpeechReady = () => true; // one has loaded
        page.Refresh();
        Assert.True(Record(page).IsEnabled);
        Assert.Equal("", page.Status);
        window.Close();
        shell.Dispose();
    });

    /// <summary>A started shell whose speech model is the fake engine (every recording is heard as "fake-model.bin"), clean-up none.</summary>
    private static (AppShell Shell, FakePlatform Platform, string Model) Started(Scratch scratch, ModelManager? models = null)
    {
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        models ??= new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model));
        Assert.True(shell.Start(lifetime: null));
        return (shell, platform, model);
    }

    private const string StartedSettings = """
        { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false }, "llm": { "provider": "none" } }
        """;

    [Fact]
    public Task A_press_that_began_as_a_dictation_ends_as_one_so_the_next_press_still_dictates() => UiSession.Run(() =>
    {
        // Re-lens on aebd2fd: Esc during a hold that started on another page was forwarded and cancelled the dictation; the
        // release then reached the page (now idle) and was dropped, the controller kept the key held, and the next press was
        // ignored as auto-repeat. The press is decided once: its release goes to the dictation too.
        using var scratch = new Scratch(StartedSettings);
        var (shell, platform, _) = Started(scratch);
        try
        {
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            var key = platform.LastListener!;
            var window = new SettingsWindow(shell);
            window.Show();
            window.Activate();
            window.ShowPage("About");
            Dispatcher.UIThread.RunJobs();

            var t0 = Environment.TickCount64 + 60_000;
            key.Post(new HookEvent(HotkeySignal.Down, t0)); // hold to dictate from About
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            var page = Open(window, speechReady: false);
            Assert.True(Eventually(() => RedirectField.GetValue(shell) is not null), "the page did not take the hotkey");
            key.Post(new HookEvent(HotkeySignal.Escape, t0 + 500)); // Esc while still holding: the dictation is cancelled
            Assert.True(Eventually(() => shell.DictationIdle), "Esc did not reach the dictation");
            key.Post(new HookEvent(HotkeySignal.Up, t0 + 1_000)); // the release: that press's, so the dictation's
            Dispatcher.UIThread.RunJobs();
            Assert.False(page.IsRecording);
            Assert.Null(page.Running);

            window.ShowPage("About"); // the page lets the hotkey go
            Dispatcher.UIThread.RunJobs();
            key.Post(new HookEvent(HotkeySignal.Down, t0 + 3_000)); // the next press dictates
            Assert.True(Eventually(() => !shell.DictationIdle), "the next press was ignored as auto-repeat");
            key.Post(new HookEvent(HotkeySignal.Up, t0 + 4_000));
            Assert.True(Eventually(() => platform.Desktop.Ops.Contains($"set-text:{DeterministicCleaner.Clean("fake-model.bin")} ")), "the next dictation was not pasted");
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task The_window_going_behind_another_drops_the_take_and_lets_the_hotkey_go() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new SettingsWindow(shell);
        window.Show();
        window.Activate();
        var page = Open(window);
        Assert.True(Eventually(() => RedirectField.GetValue(shell) is not null), "the page did not take the hotkey");
        Click(Record(page));
        Assert.True(page.IsRecording);
        page.WindowDeactivated(); // what the window's Deactivated calls
        Assert.False(page.IsRecording);
        Assert.Equal(HowItWorksText.Cancelled, page.Status);
        Assert.Null(page.Running); // nothing ran
        Assert.Null(RedirectField.GetValue(shell)); // the hotkey dictates again
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Stop_by_mouse_leaves_focus_where_the_click_put_it() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        UsePipeline(shell, new FakeTranscriber("hello there"), TranscriptCleaner.RulesOnly("rules"));
        var window = new SettingsWindow(shell);
        window.Show();
        var page = Open(window);
        var record = Record(page);
        record.Focus(NavigationMethod.Pointer); // a click focuses it
        Click(record);
        Click(record);
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Avalonia.StyledElement;
        Assert.False(focused is not null && AutomationProperties.GetName(focused)?.StartsWith(HowItWorksText.Stages[1].Title, StringComparison.Ordinal) == true,
            "focus was moved for a mouse user");
        Assert.True(Eventually(() => page.Running is { IsCompleted: true }));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Record_turns_on_when_the_speech_model_finishes_loading_through_the_shells_own_change_events() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(StartedSettings);
        var loads = new GatedLoads();
        var (shell, _, model) = Started(scratch, new ModelManager(NullLog.Instance, loader: loads.LoadSpeech, gpuInUse: () => false));
        try
        {
            var window = new SettingsWindow(shell);
            window.Show();
            var page = Open(window, speechReady: false); // the shell's own model state
            Assert.True(Eventually(() => loads.Started.Contains(model)), "the speech model load did not start");
            Dispatcher.UIThread.RunJobs();
            Assert.False(Record(page).IsEnabled);
            Assert.Equal("Getting the speech model ready…", page.Status); // the welcome window's sentence, while it loads

            loads.Release(model); // ModelManager.Changed → ModelStateChanged → StatusChanged → Changed → SettingsWindow → Redraw
            Assert.True(Eventually(() => Record(page).IsEnabled), "Record stayed off after the model loaded");
            Assert.Equal("", page.Status);
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>A microphone stand-in: a level while open, two seconds of "speech" at Stop, or a refusal at Start.</summary>
    private sealed class ScriptedCapture : IAudioCapture
    {
        public float Level { get; init; }
        public Exception? Refuse { get; init; }
        private bool _open;

        public float CurrentLevel => _open ? Level : 0f;


        public void Start(string? deviceSetting, long keyDownStamp)
        {
            if (Refuse is not null) throw Refuse;
            _open = true;
        }

        public CapturedAudio Stop()
        {
            _open = false;
            return new CapturedAudio(new float[32000], 0.2f);
        }

        public void Fail(Exception ex) => FailedHandlers?.Invoke(ex);

        private Action<Exception>? FailedHandlers;

        event Action<Exception>? IAudioCapture.Failed
        {
            add => FailedHandlers += value;
            remove => FailedHandlers -= value;
        }
    }
}
