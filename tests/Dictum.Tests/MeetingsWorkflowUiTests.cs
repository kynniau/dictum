using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Meetings;
using Dictum.Meetings.Import;
using Dictum.Meetings.Store;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Transcribe;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Overlays;
using Dictum.Hotkey;
using Dictum.Transcribe;
using Dictum.UI.Views;
using Dictum.UI.Views.Meetings;
using Dictum.UI.Views.Settings;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.Tests;

public sealed partial class MeetingsWorkflowUiTests
{
    [Fact]
    public Task Bundle_buttons_are_keyboard_reachable_and_open_their_screens() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); using var recipient = new Fixture();
        var id = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.AppendSegments(id, MeetingFixture.Segments);
        var path = Path.Combine(fixture.Files.Root, "portable.dictum");
        var host = fixture.Host with { ChooseBundleDestination = _ => Task.FromResult<string?>(path) };
        var window = new MeetingsWindow(host); window.Show(); window.ShowMeeting(id);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var move = Find(window, APPROVED_MoveToAnotherDevice);
        Assert.True(move.Focusable); Assert.True(move.IsTabStop);
        Assert.True(move.Focus(NavigationMethod.Tab)); Assert.True(move.IsFocused);
        Click(window, APPROVED_MoveToAnotherDevice); Click(Assert.Single(window.OwnedWindows), APPROVED_ShareContinue);
        await Until(() => Texts(window).Contains(APPROVED_BundleNeedsCode) && window.IsEnabled);
        Click(window, APPROVED_CopyCode); var code = fixture.CopiedOutOfHistory!; Assert.Null(fixture.Copied);
        Assert.Equal(6, code.Split(' ').Length); Assert.True(Find(window, APPROVED_CopyCode).Focusable);
        Click(window, APPROVED_Done); window.Close();
        var incoming = new MeetingsWindow(recipient.Host with { ChooseBundle = () => Task.FromResult<string?>(path) });
        incoming.Show(); var import = Find(incoming, APPROVED_ImportBundle);
        incoming.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.True(import.Focusable); Assert.True(import.IsTabStop); Assert.True(import.Focus(NavigationMethod.Tab)); Assert.True(import.IsFocused);
        Click(incoming, APPROVED_ImportBundle);
        var boxes = All<TextBox>(incoming).ToArray(); Assert.Equal(6, boxes.Length);
        Assert.True(boxes[0].IsFocused); boxes[0].Text = code.ToUpperInvariant(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(code, string.Join(' ', boxes.Select(b => b.Text)));
        Click(incoming, APPROVED_CheckBundle);
        await Until(() => Texts(incoming).Contains(APPROVED_EnableImportsTitle));
        Click(incoming, APPROVED_EnableImports);
        Assert.Contains(APPROVED_ImportAgreement, Texts(incoming)); Assert.Empty(recipient.Store.List());
        var origin = fixture.Store.Read(id).Agreement!; // the origin record, in the approved record wording, above the notice
        Assert.Contains(MeetingsWindow.RecordText(origin.Kind, origin.AcknowledgedAt, origin.How), Texts(incoming));
        Click(incoming, APPROVED_Import);
        await Until(() => Texts(incoming).Contains(PROPOSED_Transcript) && incoming.IsEnabled);
        Assert.Equal(id, Assert.Single(recipient.Store.List()).Id);
        Assert.Equal(AgreementKind.Import, recipient.Host.ReadConfirmations(id).Last().Kind);
        incoming.Close(); return true;
    });
    [Fact]
    public Task Settings_custom_types_create_edit_and_confirm_delete_with_builtins_read_only() => UiSession.Run(() =>
    {
        using var fixture = new Fixture(); using var shell = fixture.Shell();
        var page = new MeetingsPage(shell, fixture.Host);
        var window = new Window { Content = page, Width = 900, Height = 1000 }; window.Show(); page.Refresh();
        Assert.Contains(string.Format(PROPOSED_BuiltinType, "General meeting"), Texts(page));
        Assert.Empty(All<Button>(page).Where(b => AutomationProperties.GetName(b) == PROPOSED_EditType));
        Click(page, PROPOSED_NewType);
        var dialog = Assert.Single(window.OwnedWindows);
        All<TextBox>(dialog).Single(t => AutomationProperties.GetName(t) == PROPOSED_TypeName).Text = "Review";
        All<TextBox>(dialog).Single(t => AutomationProperties.GetName(t) == PROPOSED_TypeInstructions).Text = "Focus on decisions.";
        Click(dialog, PROPOSED_SaveType);
        var type = Assert.Single(SummaryTemplates.Load(fixture.Paths.ConfigDir).Where(t => !t.IsBuiltin));
        Assert.Equal("Focus on decisions.", type.Instructions);
        Click(page, PROPOSED_EditType); dialog = Assert.Single(window.OwnedWindows);
        All<TextBox>(dialog).Single(t => AutomationProperties.GetName(t) == PROPOSED_TypeName).Text = "Revised review";
        Click(dialog, PROPOSED_SaveType);
        Assert.Equal("Revised review", SummaryTemplates.Get(type.Id, fixture.Paths.ConfigDir).Name);
        Click(page, PROPOSED_DeleteType); Click(Assert.Single(window.OwnedWindows), APPROVED_Cancel);
        Assert.Equal(5, SummaryTemplates.Load(fixture.Paths.ConfigDir).Count);
        Click(page, PROPOSED_DeleteType); Click(Assert.Single(window.OwnedWindows), PROPOSED_DeleteType);
        Assert.Equal(4, SummaryTemplates.Load(fixture.Paths.ConfigDir).Count);
        window.Close();
    });

    [Fact]
    public Task Summary_flow_creates_another_edits_shows_original_and_exports_selected() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { SummaryModel = "invented.gguf" };
        var id = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.AppendSegments(id, MeetingFixture.Segments);
        var original = MeetingFixture.Summary() with { TemplateName = "Original type", Model = "original.gguf" };
        fixture.Store.WriteSummary(id, original);
        fixture.Host.SaveConfirmation(id, new("agreed", DateTimeOffset.UtcNow, AgreementKind.Live, ApprovedScriptVersion, "Synthetic notice", Route: fixture.Host.ProcessingRoute()));
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        Click(window, PROPOSED_NewSummary);
        await Until(() => fixture.Store.ReadSummaries(id).Count == 2 && Find(window, PROPOSED_NewSummary).IsEffectivelyEnabled);
        var latest = fixture.Store.ReadSummary(id)!;
        Assert.Equal("invented.gguf", latest.Model);
        Click(window, PROPOSED_EditSummary);
        var editor = All<TextBox>(window).Single(t => AutomationProperties.GetName(t) == PROPOSED_SummaryEditor);
        Assert.True(editor.IsFocused);
        editor.Text = "## User revision\n- Revised discussion";
        Click(window, PROPOSED_SaveSummary);
        Assert.Equal(latest.Markdown, fixture.Store.ReadSummary(id)!.Markdown);
        Assert.Contains("Revised discussion", Texts(window));
        Click(window, PROPOSED_ShowOriginal);
        Assert.Contains(latest.Markdown, Texts(window));
        Assert.DoesNotContain("Revised discussion", Texts(window));
        Click(window, PROPOSED_Copy);
        Click(Assert.Single(window.OwnedWindows), APPROVED_ShareContinue);
        Assert.Contains("Revised discussion", fixture.Copied);
        Assert.Contains(string.Format(PROPOSED_SummaryAttribution, "General meeting", latest.Model, latest.At), fixture.Copied);
        Assert.Contains("Edited by you:", fixture.Copied);
        var list = All<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == PROPOSED_SummaryList);
        list.SelectedIndex = 1;
        Click(window, PROPOSED_Copy);
        Click(Assert.Single(window.OwnedWindows), APPROVED_ShareContinue);
        Assert.Contains(original.Markdown, fixture.Copied);
        Assert.DoesNotContain("Revised discussion", fixture.Copied);
        Click(window, PROPOSED_DeleteSummary);
        Click(Assert.Single(window.OwnedWindows), APPROVED_Cancel);
        Assert.Equal(2, fixture.Store.ReadSummaries(id).Count);
        Click(window, PROPOSED_DeleteSummary);
        Click(Assert.Single(window.OwnedWindows), PROPOSED_DeleteSummary);
        Assert.Single(fixture.Store.ReadSummaries(id));
        window.Close(); return true;
    });

    [Fact]
    public Task Local_summary_model_change_requires_a_fresh_named_notice() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { SummaryModel = "first.gguf" };
        var id = fixture.Store.Create(fixture.Files.Meta()); fixture.Store.AppendSegments(id, MeetingFixture.Segments);
        var before = fixture.Host.ProcessingRoute();
        fixture.Host.SaveConfirmation(id, new("agreed", DateTimeOffset.UtcNow, AgreementKind.Live, ApprovedScriptVersion, "Synthetic notice", Route: before));
        fixture.Settings = fixture.Settings with { SummaryModel = "second.gguf" };
        Assert.NotEqual(before, fixture.Host.ProcessingRoute());
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        Click(window, PROPOSED_NewSummary);
        var notice = Assert.Single(window.OwnedWindows);
        Assert.Contains(string.Format(PROPOSED_LocalSummaryModel, "second.gguf"), Texts(notice));
        Assert.Empty(fixture.Store.ReadSummaries(id));
        Click(notice, APPROVED_Continue);
        await Until(() => fixture.Store.ReadSummary(id) is not null && Find(window, PROPOSED_NewSummary).IsEffectivelyEnabled);
        Assert.Equal("second.gguf", fixture.Store.ReadSummary(id)!.Model);
        window.Close(); return true;
    });

    [Fact]
    public Task Missing_summary_model_offers_settings_without_summarising() => UiSession.Run(() =>
    {
        using var fixture = new Fixture(); var opened = false;
        var host = fixture.Host with { SummaryModelInstalled = () => false, OpenModels = () => opened = true };
        var id = fixture.Store.Create(fixture.Files.Meta());
        var window = new MeetingsWindow(host); window.Show(); window.ShowMeeting(id);
        Click(window, PROPOSED_NewSummary);
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Contains(PROPOSED_ModelMissing, Texts(dialog));
        Click(dialog, PROPOSED_OpenModels);
        Assert.True(opened); Assert.Empty(fixture.Store.ReadSummaries(id));
        window.Close();
    });
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();
    private static Button Find(ILogical root, string name) => All<Button>(root).Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(ILogical root, string name)
    {
        var button = Find(root, name);
        Assert.True(button.IsEffectivelyEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
    private static byte[] AudioBytes(Stream audio)
    {
        using var copy = new MemoryStream();
        audio.CopyTo(copy);
        return copy.ToArray();
    }
    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
    private static async Task Until(Func<bool> done)
    {
        for (var attempt = 0; attempt < 300 && !done(); attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(done());
    }

    private sealed class Fixture : IDisposable
    {
        public MeetingFixture Files { get; } = new();
        public MeetingsSettings Settings = new() { Enabled = true };
        public TestCapture Capture { get; } = new();
        public TestTranscriber Transcriber { get; }
        public AppPaths Paths { get; }
        public IMeetingStore Store { get; }
        public MeetingsHost Host { get; }
        public int Imports;
        public bool ImportsEnabled;
        public string? Copied, CopiedOutOfHistory;
        public Fixture()
        {
            Directory.CreateDirectory(Files.Root);
            Paths = new(Path.Combine(Files.Root, "config"), Path.Combine(Files.Root, "local"), Path.Combine(Files.Root, "exe"));
            Directory.CreateDirectory(Paths.ConfigDir);
            Directory.CreateDirectory(Paths.ExeDir);
            Store = new MeetingsHost.CurrentSettingsStore(Path.Combine(Files.Root, "meetings"), () => Settings, Files.Keyring);
            var transcriber = Transcriber = new TestTranscriber(Store);
            var importer = new MeetingImporter(Store, transcriber, new WavOnlyDecoder(),
                saveConfirmation: (id, agreement) => ((MeetingsHost.CurrentSettingsStore)Store).WriteConfirmation(id, agreement.Confirmation!));
            Host = new(Paths, () => Settings, Store, async (path, agreement, cancellation) =>
            {
                Imports++;
                return await importer.ImportAsync(path, MeetingSource.ImportedFile, agreement, cancellationToken: cancellation);
            }, transcriber, () => Capture, (_, _) => Task.FromResult<ILlmClient>(new TestLlm()), text => Copied = text,
                (_, _) => { }, _ => Task.FromResult<bool?>(null), () => false, () => { })
            {
                ImportsEnabled = () => ImportsEnabled,
                CopyOutOfHistory = text => CopiedOutOfHistory = text,
                EnableImports = () => ImportsEnabled = true,
                SaveConfirmation = ((MeetingsHost.CurrentSettingsStore)Store).WriteConfirmation,
                ReadConfirmations = ((MeetingsHost.CurrentSettingsStore)Store).ReadConfirmations,
                SummaryHost = () => "summary.example",
            };
        }
        public AppShell Shell() => new(new FakePlatform(Paths), Paths, NullLog.Instance, new SecretRedactor(), new ShellOptions(), gpuMachine: () => false);
        public void Dispose() => Files.Dispose();
    }
    private sealed class TestLlm : ILlmClient
    {
        public string Name => "synthetic";
        public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken) => Task.FromResult(new LlmReply("# Summary\nA. Example discussed the budget.", "stop"));
    }
    private sealed class TestCapture : IAudioCapture
    {
        public int Starts, Stops;
        public bool Active, FailStart;
        private Action<CapturedAudio, CutReason>? _part;
        public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part) => _part = part;
        public void Emit() => _part?.Invoke(new(new float[1600], .2f), default);
        public float Level = 0.5f;
        public float CurrentLevel => Active ? Level : 0;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start(string? deviceSetting, long keyDownStamp) { if (FailStart) throw new IOException("Synthetic start failure"); Active = true; Starts++; }
        public CapturedAudio Stop() { Active = false; Stops++; return new(Enumerable.Range(0, 1600).Select(i => (float)(0.2 * Math.Sin(i * 0.2))).ToArray(), 0.2f); }
    }
    private sealed class TestTranscriber(IMeetingStore store) : IMeetingTranscriber
    {
        // Counts live or import runs whose audio source ended, so a test can tell "stopped" from "paused".
        public int Finished;
        public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken)
        {
            await foreach (var part in source.ReadAsync(cancellationToken))
            {
                var segment = new Segment(part.Offset, part.Offset + part.Part.Duration, part.Channel, MeetingFixture.TranscriptText);
                store.AppendSegments(id, [segment]);
                onSegment(segment);
            }
            Interlocked.Increment(ref Finished);
        }
    }

    [Fact]
    public Task Meeting_pill_pause_toggles_halo_and_stop_saves_normally() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        var system = new TestCapture { Level = 0.8f };
        var window = shell.CreateMeetingsWindow(fixture.Host with { LoopbackCapture = () => system });
        window.Show(); window.BeginAgreement();
        Assert.Contains(PROPOSED_HeadphonesTip, Texts(window));
        Click(window, APPROVED_Start);
        var overlays = shell.Overlays!;
        var pill = overlays.Pill!;
        Assert.Equal(PillView.MeetingRecording, pill.View);
        Assert.Equal(PROPOSED_MeetingRecording, pill.Text);
        Assert.True(overlays.EdgesShown);
        Assert.Equal(0.8f, shell.CurrentLevel);
        system.Level = 0.1f;
        Assert.Equal(0.5f, shell.CurrentLevel);
        Assert.True(shell.MeetingOwnsPill);
        Assert.False(pill.ShowActivated);
        Assert.True(pill.Focusable);
        Assert.True(pill.IsHitTestVisible);
        Assert.True(Find(pill, PROPOSED_Pause).Focusable);
        Assert.True(Find(pill, PROPOSED_Pause).Focus(NavigationMethod.Tab));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(pill.FindControl<TextBlock>("Label")!));
        Click(pill, PROPOSED_Pause);
        Assert.Equal(PillView.MeetingPaused, pill.View);
        Assert.Equal(PROPOSED_MeetingPaused, pill.Text);
        Assert.False(overlays.EdgesShown);
        Assert.False(fixture.Capture.Active);
        Assert.True(shell.MeetingOwnsPill);
        Click(pill, PROPOSED_Resume);
        Assert.Equal(PillView.MeetingRecording, pill.View);
        Assert.True(overlays.EdgesShown);
        Assert.True(fixture.Capture.Active);
        var meta = Assert.Single(fixture.Store.List());
        window.WindowState = WindowState.Minimized;
        Click(pill, PROPOSED_Stop);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(PillView.Hidden, pill.View);
        Assert.False(overlays.EdgesShown);
        Assert.False(shell.MeetingOwnsPill);
        Assert.Equal(0, shell.CurrentLevel);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        Assert.Equal(meta.Id, Assert.Single(fixture.Store.List()).Id);
        Assert.NotEmpty(fixture.Store.ReadTranscript(meta.Id));
        Assert.Contains(fixture.Store.Read(meta.Id).Markers, m => m.Kind == MarkerKind.Paused);
        Assert.Contains(fixture.Store.Read(meta.Id).Markers, m => m.Kind == MarkerKind.Resumed);
        Assert.Equal(1, fixture.Transcriber.Finished);
        window.Close();
        return true;
    });

    [Fact]
    public Task Meeting_pill_uses_orb_and_reduced_motion_and_hides_meeting_controls_for_dictation() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "indicator": { "mode": "both" } }""");
        var (shell, platform, _) = scratch.Shell();
        using (shell)
        using (var overlays = new OverlayPresenter(shell))
        {
            shell.Reload(userRequested: false);
            platform.Flags.Reduced = true;
            overlays.Show(new(PillView.MeetingRecording, 0));
            Assert.Equal(PillView.MeetingRecording, overlays.Orb!.View);
            Assert.Empty(overlays.Pill!.FindControl<Border>("Pill")!.Transitions ?? []);
            overlays.Show(new(PillView.MeetingPaused, 0));
            Assert.False(overlays.EdgesShown);
            Assert.False(overlays.Orb.IsVisible);
            overlays.Show(new(PillView.Hidden, 0));
            Assert.False(overlays.Pill!.IsVisible);
            overlays.Show(new(PillView.RecordingHold, 0));
            Assert.False(overlays.Pill.MeetingControls);
            Assert.False(overlays.Pill.IsHitTestVisible);
            Assert.False(overlays.Pill.FindControl<Button>("MeetingPause")!.IsVisible);
            Assert.False(overlays.Pill.FindControl<Button>("MeetingStop")!.IsVisible);
        }
    });

    [Fact]
    public Task Dictation_hotkey_during_meeting_is_busy_and_keeps_meeting_pill() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" } }""");
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var log = new RecordingLog();
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model), log: log);
        using (shell)
        {
            Assert.True(shell.Start(null));
            await Until(() => shell.SpeechReady);
            var window = shell.CreateMeetingsWindow(fixture.Host);
            window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
            var pill = shell.Overlays!.Pill!;
            platform.LastListener!.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));
            await Until(() => log.All.Contains("Hotkey ignored:"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.DictationIdle);
            Assert.True(fixture.Capture.Active);
            Assert.Equal(PillView.MeetingRecording, pill.View);
            Assert.True(shell.Overlays.EdgesShown);
            Click(pill, PROPOSED_Pause);
            platform.LastListener.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.DictationIdle);
            Assert.Equal(PillView.MeetingPaused, pill.View);
            Click(pill, PROPOSED_Stop);
            await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
            window.Close();
        }
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Meeting_pill_resume_keeps_route_and_arrival_agreement_checks(bool arrival) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        var speech = "first.example";
        var window = shell.CreateMeetingsWindow(fixture.Host with { CloudSpeechHost = () => speech });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var pill = shell.Overlays!.Pill!;
        if (arrival)
        {
            Click(window, PROPOSED_Joined);
            Click(Assert.Single(window.OwnedWindows), APPROVED_Cancel);
        }
        else
        {
            Click(pill, PROPOSED_Pause);
            speech = "second.example";
        }
        Click(pill, PROPOSED_Resume);
        Assert.False(fixture.Capture.Active);
        Assert.Equal(PillView.MeetingPaused, pill.View);
        Assert.False(shell.Overlays.EdgesShown);
        Click(Assert.Single(window.OwnedWindows), APPROVED_Continue);
        await Until(() => fixture.Capture.Active);
        Assert.Equal(PillView.MeetingRecording, pill.View);
        Click(pill, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        window.Close();
        return true;
    });

    [Fact]
    public Task Meeting_start_refuses_active_dictation_including_after_agreement_opens() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var idle = false;
        var window = new MeetingsWindow(fixture.Host, () => idle);
        window.Show(); window.BeginAgreement();
        Assert.Contains(PROPOSED_DictationBusy, Texts(window));
        Assert.Equal(0, fixture.Capture.Starts);
        idle = true; window.BeginAgreement();
        idle = false; Click(window, APPROVED_Start);
        Assert.Contains(PROPOSED_DictationBusy, Texts(window));
        Assert.Equal(0, fixture.Capture.Starts);
        Assert.Empty(fixture.Store.List());
        window.Close();
    });

    [Fact]
    public Task Agreement_blocks_capture_until_acknowledged_and_pause_releases_the_microphone() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host);
        window.Show(); window.BeginAgreement(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, fixture.Capture.Starts);
        Assert.Empty(fixture.Store.List());
        Click(window, APPROVED_Cancel);
        Assert.Equal(0, fixture.Capture.Starts);
        window.BeginAgreement();
        Click(window, APPROVED_Start);
        Assert.Equal(1, fixture.Capture.Starts);
        var meta = Assert.Single(fixture.Store.List());
        Assert.Equal(AgreementKind.Live, meta.Agreement!.Kind);
        Assert.Equal("spoken", meta.Agreement.How);
        Assert.Contains(APPROVED_MicOnly, Texts(window));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(All<TextBlock>(window).Single(t => AutomationProperties.GetName(t) == PROPOSED_Live)));
        Click(window, PROPOSED_Pause);
        Assert.False(fixture.Capture.Active);
        Click(window, PROPOSED_Resume);
        Assert.True(fixture.Capture.Active);
        Click(window, PROPOSED_Mark);
        Click(window, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate) && window.IsEnabled && !fixture.Capture.Active);
        await Until(() => Find(window, PROPOSED_Regenerate).IsEffectivelyEnabled);
        Assert.Contains(fixture.Store.ReadTranscript(meta.Id), s => s.Channel == Channel.You);
        Assert.Contains(fixture.Store.Read(meta.Id).Markers, m => m.Kind == MarkerKind.Moment);
        window.Close();
        return true;
    });

    [Fact]
    public Task Refusal_can_save_typed_notes_without_opening_capture() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement();
        Click(window, APPROVED_Refused); KeyboardReach(window);
        Assert.Equal(0, fixture.Capture.Starts);
        All<TextBox>(window).Single().Text = MeetingFixture.TranscriptText;
        Click(window, PROPOSED_SaveNotes);
        var meta = Assert.Single(fixture.Store.List());
        Assert.Null(meta.Agreement);
        Assert.Equal(MeetingFixture.TranscriptText, Assert.Single(fixture.Store.ReadTranscript(meta.Id)).Text);
        Assert.Equal(0, fixture.Capture.Starts);
        window.Close();
    });

    [Fact]
    public Task Capture_creation_failure_rolls_back_the_meeting_and_allows_closing() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host with { Capture = () => throw new IOException("synthetic failure") });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        Assert.Empty(fixture.Store.List());
        Assert.Contains(PROPOSED_Error, Texts(window));
        window.Close(); Assert.False(window.IsVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Tray_and_main_entries_follow_enabled(bool enabled) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { Enabled = enabled };
        Assert.True(SettingsFile.SetValue(fixture.Paths.SettingsFile, "meetings", SettingsFile.MeetingsJson(fixture.Settings)));
        using var shell = fixture.Shell();
        var flyout = new FlyoutWindow(shell); flyout.Show();
        var main = new MeetingsWindow(fixture.Host); main.Show();
        foreach (var root in new Window[] { flyout, main })
        {
            Assert.Equal(enabled, Find(root, PROPOSED_Start).IsEffectivelyVisible);
            Assert.Equal(enabled, Find(root, PROPOSED_Meetings).IsEffectivelyVisible);
        }
        main.Close(); flyout.Close();
    });

    [Theory]
    [InlineData("Local")]
    [InlineData("Ollama")]
    [InlineData("OwnKey")]
    public Task Caption_follows_the_engine_that_wrote_the_summary(string engine) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { SummaryEngine = SummaryEngine.OwnKey };
        var id = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.WriteSummary(id, MeetingFixture.Summary(engine));
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        var expected = SummaryCaption.For(engine, engine == "OwnKey" ? MeetingsHost.PROPOSED_UnrecordedProvider : null);
        Assert.Contains(expected, Texts(window));
        Assert.Contains(expected, fixture.Store.ExportMarkdown(id));
        Click(window, PROPOSED_Copy);
        Assert.Null(fixture.Copied);
        Click(Assert.Single(window.OwnedWindows), APPROVED_ShareContinue);
        Assert.Contains(expected, fixture.Copied);
        Assert.Contains(MeetingsHost.PROPOSED_ExportReminder, fixture.Copied);
        window.Close();
    });

    [Fact]
    public Task Own_key_provider_is_kept_with_the_summary_after_settings_change() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { SummaryEngine = SummaryEngine.OwnKey };
        var id = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.AppendSegments(id, MeetingFixture.Segments);
        await fixture.Host.Regenerate(id, "general", TestContext.Current.CancellationToken);
        fixture.Settings = fixture.Settings with { SummaryEngine = SummaryEngine.Local };
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        var expected = SummaryCaption.For("OwnKey", "synthetic");
        Assert.Contains(expected, Texts(window));
        Assert.Contains(expected, fixture.Store.ExportMarkdown(id));
        window.Close();
        return true;
    });

    [Fact]
    public Task Import_from_UI_calls_importer_only_after_acknowledgement_and_lists_the_meeting() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Files.Root, "synthesised.wav");
        var original = MeetingFixture.Wav(); File.WriteAllBytes(path, original);
        var window = new MeetingsWindow(fixture.Host with { ChooseImport = () => Task.FromResult<string?>(path) }); window.Show();
        Click(window, PROPOSED_Import);
        Assert.Equal(0, fixture.Imports); Assert.Empty(fixture.Store.List());
        Assert.Contains(APPROVED_EnableImportsBody, Texts(window));
        Click(window, APPROVED_EnableImports);
        Click(window, APPROVED_Import);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate));
        Assert.Equal(1, fixture.Imports);
        var meta = Assert.Single(fixture.Store.List());
        Assert.Equal(AgreementKind.Import, meta.Agreement!.Kind);
        Assert.Equal(original, File.ReadAllBytes(path));
        Click(window, PROPOSED_Meetings);
        Assert.Contains(All<Button>(window), b => b.Content?.ToString()?.Contains("synthesised", StringComparison.Ordinal) == true);
        var search = All<TextBox>(window).Single(); search.Text = "not in the fixture"; Dispatcher.UIThread.RunJobs();
        Assert.Contains(PROPOSED_Empty, Texts(window));
        search.Text = "budget"; Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(PROPOSED_Empty, Texts(window));
        window.Close();
        return true;
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, true)]
    public Task Drive_warning_shows_unless_encryption_confirmed_and_dismissal_survives_reopening(bool? encrypted, bool visible) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        // Exercise the real per-computer persistence, with an in-memory platform credential store.
        var production = shell.CreateMeetingsHost();
        var host = fixture.Host with { DriveEncrypted = _ => Task.FromResult(encrypted), WarningDismissed = production.WarningDismissed, DismissWarning = production.DismissWarning };
        var page = new MeetingsPage(shell, host);
        var window = new Window { Content = page, Width = 850, Height = 900 }; window.Show(); page.Refresh(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(visible, Find(page, APPROVED_Dismiss).IsEffectivelyVisible);
        if (visible)
        {
            Click(page, APPROVED_Dismiss);
            Assert.False(Find(page, APPROVED_Dismiss).IsEffectivelyVisible);
            var reopened = new MeetingsPage(shell, host); window.Content = reopened; reopened.Refresh(); Dispatcher.UIThread.RunJobs();
            Assert.False(Find(reopened, APPROVED_Dismiss).IsEffectivelyVisible);
            Assert.True(shell.CreateMeetingsHost().WarningDismissed());
        }
        window.Close();
    });

    private sealed class FaultStore(IMeetingStore inner) : IMeetingStore
    {
        public bool FailCreate, FailAppend, FailAudioOnce, FailChunk, ForeignOwner, Unreadable, FailMarker, FailDelete;
        public int AudioAttempts, Deletes;
        public MeetingId Create(MeetingMeta meta)
        {
            var id = inner.Create(ForeignOwner ? meta with { Title = "Another meeting" } : meta);
            if (FailCreate) throw new IOException("Synthetic partial create failure");
            return id;
        }
        public IMeetingAudioWriter BeginAudio(MeetingId id, Channel channel) => new FaultWriter(this, inner.BeginAudio(id, channel));
        private sealed class FaultWriter(FaultStore owner, IMeetingAudioWriter writer) : IMeetingAudioWriter
        {
            public void Append(float[] samples)
            {
                if (owner.FailChunk) throw new IOException("Synthetic chunk failure");
                writer.Append(samples);
            }
            public void Complete()
            {
                owner.AudioAttempts++;
                if (owner.FailAudioOnce) { owner.FailAudioOnce = false; throw new IOException("Synthetic save failure"); }
                writer.Complete();
            }
            public void Dispose() => writer.Dispose();
        }
        public void WriteAudio(MeetingId id, Channel channel, Stream audio)
        {
            AudioAttempts++;
            if (FailAudioOnce) { FailAudioOnce = false; throw new IOException("Synthetic save failure"); }
            inner.WriteAudio(id, channel, audio);
        }
        public void AppendSegments(MeetingId id, IEnumerable<Segment> segments)
        {
            if (FailAppend) throw new IOException("Synthetic notes failure");
            inner.AppendSegments(id, segments);
        }
        public MeetingMeta Read(MeetingId id) => Unreadable ? throw new InvalidDataException("Synthetic unreadable metadata") : inner.Read(id);
        public void Delete(MeetingId id) { Deletes++; if (FailDelete) throw new IOException("Synthetic delete failure"); inner.Delete(id); }
        public void AddMarker(MeetingId id, Marker marker)
        {
            if (FailMarker) throw new IOException("Synthetic marker failure");
            inner.AddMarker(id, marker);
        }
        public void WriteSummary(MeetingId id, Summary summary) => inner.WriteSummary(id, summary);
        public void WriteVideo(MeetingId id, string path) => inner.WriteVideo(id, path);
        public IReadOnlyList<Segment> ReadTranscript(MeetingId id) => inner.ReadTranscript(id);
        public Summary? ReadSummary(MeetingId id) => inner.ReadSummary(id);
        public Stream OpenAudio(MeetingId id, Channel channel) => inner.OpenAudio(id, channel);
        public IReadOnlyList<MeetingMeta> List() => inner.List();
        public IReadOnlyList<(MeetingMeta, string Snippet)> Search(string text) => inner.Search(text);
        public long SizeOf(MeetingId id) => inner.SizeOf(id);
        public void ApplyRetention(DateTimeOffset now) => inner.ApplyRetention(now);
        public string ExportMarkdown(MeetingId id) => inner.ExportMarkdown(id);
    }

    private sealed class DelayedTranscriber(IMeetingStore store) : IMeetingTranscriber
    {
        public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken)
        {
            await foreach (var part in source.ReadAsync(cancellationToken))
            {
                await Task.Delay(80, cancellationToken);
                var segment = new Segment(part.Offset, part.Offset + part.Part.Duration, part.Channel, "Final segment");
                store.AppendSegments(id, [segment]);
                onSegment(segment);
            }
        }
    }

    private sealed class FailingTranscriber : IMeetingTranscriber
    {
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken)
        {
            await Fail.Task;
            throw new IOException("Synthetic transcription failure");
        }
    }

    [Fact]
    public Task Retention_uses_changed_settings_while_window_stays_open() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { KeepAudio = KeepAudio.Days30 };
        var window = new MeetingsWindow(fixture.Host); window.Show();
        var id = fixture.Store.Create(fixture.Files.Meta());
        using (var audio = new MemoryStream(MeetingFixture.Wav())) fixture.Store.WriteAudio(id, Channel.You, audio);
        fixture.Settings = fixture.Settings with { KeepAudio = KeepAudio.Forever };
        window.ShowList();
        using var saved = fixture.Store.OpenAudio(id, Channel.You);
        Assert.Equal(MeetingFixture.Wav(), AudioBytes(saved));
        window.Close();
    });

    [Fact]
    public Task Failed_audio_save_keeps_audio_for_retry_and_discard_requires_confirmation() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var store = new FaultStore(fixture.Store) { FailAudioOnce = true };
        var window = new MeetingsWindow(fixture.Host with { Store = store }); window.Show(); window.BeginAgreement();
        Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        Click(window, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Retry && b.IsEffectivelyEnabled));
        Assert.False(fixture.Capture.Active);
        Assert.Contains(PROPOSED_AudioSaveFailed, Texts(window));
        window.Close(); Assert.True(window.IsVisible);
        Click(window, PROPOSED_Discard);
        var confirm = Assert.Single(window.OwnedWindows);
        Assert.Contains(PROPOSED_DeleteQuestion, Texts(confirm));
        Click(confirm, APPROVED_Cancel);
        Assert.Single(fixture.Store.List());
        Click(window, PROPOSED_Retry);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        using var audio = fixture.Store.OpenAudio(id, Channel.You);
        Assert.True(AudioBytes(audio).Length > 44);
        Assert.Equal(2, store.AudioAttempts);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(1, fixture.Capture.Stops);
        window.Close(); return true;
    });

    [Fact]
    public Task Shutdown_drains_final_segments_before_closing() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host with { Transcriber = new DelayedTranscriber(fixture.Store) });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        Assert.True(window.Shutdown());
        Assert.False(window.IsVisible);
        Assert.Equal("Final segment", Assert.Single(fixture.Store.ReadTranscript(id)).Text);
        using var audio = fixture.Store.OpenAudio(id, Channel.You);
        Assert.True(AudioBytes(audio).Length > 44);
    });

    [Theory]
    [InlineData("quit", false)]
    [InlineData("session", false)]
    [InlineData("dispose", false)]
    [InlineData("quit", true)]
    [InlineData("session", true)]
    [InlineData("dispose", true)]
    public Task Exit_save_failure_cancels_until_retry_or_confirmed_discard(string exitPath, bool discard) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        using var lifetime = new ClassicDesktopStyleApplicationLifetime();
        shell.ConfigureMeetingsShutdown(lifetime);
        // Keep completion on the real shell disposal path without stopping the shared headless dispatcher.
        typeof(AppShell).GetField("_lifetime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(shell, null);
        var store = new FaultStore(fixture.Store) { FailAudioOnce = true };
        var window = new MeetingsWindow(fixture.Host with { Store = store });
        typeof(AppShell).GetField("_meetingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(shell, window);
        bool Disposed() => (bool)typeof(AppShell).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shell)!;
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        window.WindowState = WindowState.Minimized;
        window.Hide();
        if (exitPath == "session")
        {
            var request = new ShutdownRequestedEventArgs();
            var handler = (EventHandler<ShutdownRequestedEventArgs>)typeof(ClassicDesktopStyleApplicationLifetime)
                .GetField("ShutdownRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lifetime)!;
            handler(lifetime, request);
            Assert.True(request.Cancel);
        }
        else if (exitPath == "dispose") shell.Dispose();
        else shell.Quit();
        Assert.False(Disposed());
        Assert.Equal(1, store.AudioAttempts);
        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Contains(All<TextBlock>(window), text => text.Text == PROPOSED_AudioSaveFailed && AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
        Assert.True(Find(window, PROPOSED_Retry).IsEffectivelyEnabled);
        window.Close(); Assert.True(window.IsVisible);
        if (discard)
        {
            Click(window, PROPOSED_Discard);
            Click(Assert.Single(window.OwnedWindows), APPROVED_Cancel);
            Assert.False(Disposed()); Assert.Single(fixture.Store.List());
            Click(window, PROPOSED_Discard);
            Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        }
        else Click(window, PROPOSED_Retry);
        await Until(Disposed);
        Assert.False(window.IsVisible);
        Assert.True(Disposed());
        Assert.Equal(discard ? 1 : 2, store.AudioAttempts);
        Assert.Equal(discard ? 1 : 0, store.Deletes);
        if (discard) Assert.Empty(fixture.Store.List());
        else
        {
            using var audio = fixture.Store.OpenAudio(id, Channel.You);
            Assert.True(AudioBytes(audio).Length > 44);
        }
        Assert.Equal(1, fixture.Capture.Stops);
        return true;
    });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Partial_create_rolls_back_only_owned_live_meetings_and_notes(bool notes, bool foreign, bool unreadable) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var existing = fixture.Store.Create(fixture.Files.Meta());
        var store = new FaultStore(fixture.Store) { FailCreate = true, ForeignOwner = foreign, Unreadable = unreadable };
        var window = new MeetingsWindow(fixture.Host with { Store = store }); window.Show(); window.BeginAgreement();
        if (notes)
        {
            Click(window, APPROVED_Refused);
            All<TextBox>(window).Single().Text = MeetingFixture.TranscriptText;
            Click(window, PROPOSED_SaveNotes);
        }
        else Click(window, APPROVED_Start);
        Assert.Equal(foreign ? 2 : 1, fixture.Store.List().Count);
        Assert.Equal(foreign ? 0 : 1, store.Deletes);
        Assert.Equal(existing, fixture.Store.Read(existing).Id);
        Assert.Contains(PROPOSED_Error, Texts(window));
        window.Close(); Assert.False(window.IsVisible);
    });

    [Fact]
    public Task Notes_append_failure_rolls_back_only_new_meeting() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var existing = fixture.Store.Create(fixture.Files.Meta());
        var store = new FaultStore(fixture.Store) { FailAppend = true };
        var window = new MeetingsWindow(fixture.Host with { Store = store }); window.Show(); window.BeginAgreement();
        Click(window, APPROVED_Refused);
        All<TextBox>(window).Single().Text = MeetingFixture.TranscriptText;
        Click(window, PROPOSED_SaveNotes);
        Assert.Equal(existing, Assert.Single(fixture.Store.List()).Id);
        Assert.Equal(1, store.Deletes);
        window.Close();
    });

    [Fact]
    public Task Late_joiner_discard_uses_recording_confirmation() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        Click(window, PROPOSED_Joined); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        var confirm = Assert.Single(window.OwnedWindows);
        Assert.Contains(PROPOSED_DeleteQuestion, Texts(confirm));
        Assert.False(fixture.Capture.Active); Assert.Single(fixture.Store.List());
        Click(confirm, APPROVED_Cancel);
        Assert.False(fixture.Capture.Active);
        Click(window, PROPOSED_Joined); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => fixture.Store.List().Count == 0 && !fixture.Capture.Active);
        window.Close(); return true;
    });

    [Fact]
    public Task Agreement_and_recording_retire_the_audio_until_stop_notice() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement();
        Assert.DoesNotContain(APPROVED_AudioUntilStop, Texts(window));
        Click(window, APPROVED_Start);
        Assert.DoesNotContain(APPROVED_AudioUntilStop, Texts(window));
        window.Shutdown();
    });

    [Fact]
    public Task Transcription_fault_is_announced_immediately_and_audio_can_be_saved() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var transcriber = new FailingTranscriber();
        var window = new MeetingsWindow(fixture.Host with { Transcriber = transcriber });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        Assert.True(fixture.Capture.Active);
        transcriber.Fail.SetResult();
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_SaveAudioStop));
        Assert.False(fixture.Capture.Active);
        Assert.Contains(All<TextBlock>(window), t => t.Text == PROPOSED_TranscriptionFailed && AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
        Click(window, PROPOSED_SaveAudioStop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        using var audio = fixture.Store.OpenAudio(id, Channel.You); Assert.True(AudioBytes(audio).Length > 44);
        window.Close(); return true;
    });

}
