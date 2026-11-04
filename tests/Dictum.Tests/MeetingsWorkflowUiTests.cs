using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
using Dictum.UI.Views;
using Dictum.UI.Views.Meetings;
using Dictum.UI.Views.Settings;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.Tests;

public sealed class MeetingsWorkflowUiTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();
    private static Button Find(ILogical root, string name) => All<Button>(root).Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(ILogical root, string name)
    {
        var button = Find(root, name);
        Assert.True(button.IsEffectivelyEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
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
        public AppPaths Paths { get; }
        public IMeetingStore Store { get; }
        public MeetingsHost Host { get; }
        public int Imports;
        public string? Copied;
        public Fixture()
        {
            Directory.CreateDirectory(Files.Root);
            Paths = new(Path.Combine(Files.Root, "config"), Path.Combine(Files.Root, "local"), Path.Combine(Files.Root, "exe"));
            Directory.CreateDirectory(Paths.ConfigDir);
            Directory.CreateDirectory(Paths.ExeDir);
            Store = new FileMeetingStore(Path.Combine(Files.Root, "meetings"), Settings, Files.Keyring, MeetingsHost.Caption);
            var transcriber = new TestTranscriber(Store);
            var importer = new MeetingImporter(Store, transcriber, new WavOnlyDecoder());
            Host = new(Paths, () => Settings, Store, async (path, agreement, cancellation) =>
            {
                Imports++;
                return await importer.ImportAsync(path, MeetingSource.ImportedFile, agreement, cancellationToken: cancellation);
            }, transcriber, () => Capture, (_, _) => Task.FromResult<ILlmClient>(new TestLlm()), text => Copied = text,
                (_, _) => { }, _ => Task.FromResult<bool?>(null), () => false, () => { });
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
        public bool Active;
        public float CurrentLevel => Active ? 0.5f : 0;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start(string? deviceSetting, long keyDownStamp) { Active = true; Starts++; }
        public CapturedAudio Stop() { Active = false; Stops++; return new(Enumerable.Range(0, 1600).Select(i => (float)(0.2 * Math.Sin(i * 0.2))).ToArray(), 0.2f); }
    }
    private sealed class TestTranscriber(IMeetingStore store) : IMeetingTranscriber
    {
        public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken)
        {
            await foreach (var part in source.ReadAsync(cancellationToken))
            {
                var segment = new Segment(part.Offset, part.Offset + part.Part.Duration, part.Channel, MeetingFixture.TranscriptText);
                store.AppendSegments(id, [segment]);
                onSegment(segment);
            }
        }
    }

    [Fact]
    public Task Agreement_blocks_capture_until_acknowledged_and_pause_releases_the_microphone() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host);
        window.Show(); window.BeginAgreement(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, fixture.Capture.Starts);
        Assert.Empty(fixture.Store.List());
        Click(window, PROPOSED_Cancel);
        Assert.Equal(0, fixture.Capture.Starts);
        window.BeginAgreement();
        Click(window, PLACEHOLDER_Start);
        Assert.Equal(1, fixture.Capture.Starts);
        var meta = Assert.Single(fixture.Store.List());
        Assert.Equal(AgreementKind.Live, meta.Agreement!.Kind);
        Assert.Equal("spoken", meta.Agreement.How);
        Assert.Contains(PROPOSED_MicOnly, Texts(window));
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
        Click(window, PLACEHOLDER_Refused); KeyboardReach(window);
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
        window.Show(); window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
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
        var expected = SummaryCaption.For(engine, engine == "OwnKey" ? MeetingsHost.PLACEHOLDER_UnrecordedProvider : null);
        Assert.Contains(expected, Texts(window));
        Assert.Contains(expected, fixture.Store.ExportMarkdown(id));
        Click(window, PROPOSED_Copy);
        Assert.Contains(expected, fixture.Copied);
        Assert.Contains(MeetingsHost.PLACEHOLDER_ExportReminder, fixture.Copied);
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
        Click(window, PLACEHOLDER_Import);
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
    [InlineData(null, false)]
    public Task Drive_warning_shows_only_when_known_off_and_dismissal_survives_reopening(bool? encrypted, bool visible) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        // Exercise the real per-computer persistence, with an in-memory platform credential store.
        var production = shell.CreateMeetingsHost();
        var host = fixture.Host with { DriveEncrypted = _ => Task.FromResult(encrypted), WarningDismissed = production.WarningDismissed, DismissWarning = production.DismissWarning };
        var page = new MeetingsPage(shell, host);
        var window = new Window { Content = page, Width = 850, Height = 900 }; window.Show(); page.Refresh(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(visible, Find(page, PROPOSED_Dismiss).IsEffectivelyVisible);
        if (visible)
        {
            Click(page, PROPOSED_Dismiss);
            Assert.False(Find(page, PROPOSED_Dismiss).IsEffectivelyVisible);
            var reopened = new MeetingsPage(shell, host); window.Content = reopened; reopened.Refresh(); Dispatcher.UIThread.RunJobs();
            Assert.False(Find(reopened, PROPOSED_Dismiss).IsEffectivelyVisible);
            Assert.True(shell.CreateMeetingsHost().WarningDismissed());
        }
        window.Close();
    });

    private static void KeyboardReach(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var expected = All<Control>(window).Where(c => c is Button or TextBox or ComboBox or ToggleSwitch)
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToArray();
        Assert.NotEmpty(expected);
        foreach (var control in expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), control.GetType().Name);
            Assert.True(control.Focusable);
            Assert.True(control.IsTabStop);
        }
        expected[0].Focus(NavigationMethod.Tab);
        IInputElement? current = expected[0];
        var reached = new HashSet<IInputElement>();
        for (var index = 0; index < expected.Length * 8; index++)
        {
            if (current is null) break;
            reached.Add(current);
            current = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next);
        }
        foreach (var control in expected) Assert.Contains(control, reached);
    }

    [Fact]
    public Task All_controls_are_named_and_reachable_by_keyboard_on_each_screen() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); KeyboardReach(window);
        window.BeginAgreement(); KeyboardReach(window);
        Click(window, PLACEHOLDER_Start); KeyboardReach(window);
        Click(window, PROPOSED_Joined);
        var joined = Assert.Single(window.OwnedWindows);
        KeyboardReach(joined);
        Click(joined, PLACEHOLDER_Continue);
        Click(window, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate));
        await Until(() => Find(window, PROPOSED_Regenerate).IsEffectivelyEnabled);
        KeyboardReach(window);
        window.Close();
        using var shell = fixture.Shell();
        var page = new MeetingsPage(shell, fixture.Host with { DriveEncrypted = _ => Task.FromResult<bool?>(false) });
        var settings = new Window { Content = page, Width = 900, Height = 1000 }; settings.Show(); page.Refresh(); KeyboardReach(settings); settings.Close();
        return true;
    });
}
