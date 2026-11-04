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
            Store = new MeetingsHost.CurrentSettingsStore(Path.Combine(Files.Root, "meetings"), () => Settings, Files.Keyring);
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

    private sealed class FaultStore(IMeetingStore inner) : IMeetingStore
    {
        public bool FailCreate, FailAppend, FailAudioOnce, ForeignOwner, Unreadable;
        public int AudioAttempts, Deletes;
        public MeetingId Create(MeetingMeta meta)
        {
            var id = inner.Create(ForeignOwner ? meta with { Title = "Another meeting" } : meta);
            if (FailCreate) throw new IOException("Synthetic partial create failure");
            return id;
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
        public void Delete(MeetingId id) { Deletes++; inner.Delete(id); }
        public void AddMarker(MeetingId id, Marker marker) => inner.AddMarker(id, marker);
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
        Click(window, PLACEHOLDER_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        Click(window, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Retry && b.IsEffectivelyEnabled));
        Assert.False(fixture.Capture.Active);
        Assert.Contains(PROPOSED_AudioSaveFailed, Texts(window));
        window.Close(); Assert.True(window.IsVisible);
        Click(window, PROPOSED_Discard);
        var confirm = Assert.Single(window.OwnedWindows);
        Assert.Contains(PROPOSED_DeleteQuestion, Texts(confirm));
        Click(confirm, PROPOSED_Cancel);
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
        window.Show(); window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
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
        window.Show(); window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
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
            Click(Assert.Single(window.OwnedWindows), PROPOSED_Cancel);
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
            Click(window, PLACEHOLDER_Refused);
            All<TextBox>(window).Single().Text = MeetingFixture.TranscriptText;
            Click(window, PROPOSED_SaveNotes);
        }
        else Click(window, PLACEHOLDER_Start);
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
        Click(window, PLACEHOLDER_Refused);
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
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
        Click(window, PROPOSED_Joined); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        var confirm = Assert.Single(window.OwnedWindows);
        Assert.Contains(PROPOSED_DeleteQuestion, Texts(confirm));
        Assert.True(fixture.Capture.Active); Assert.Single(fixture.Store.List());
        Click(confirm, PROPOSED_Cancel);
        Assert.True(fixture.Capture.Active);
        Click(window, PROPOSED_Joined); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => fixture.Store.List().Count == 0 && !fixture.Capture.Active);
        window.Close(); return true;
    });

    [Fact]
    public Task Agreement_and_recording_explain_audio_is_saved_at_stop() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement();
        Assert.Contains(PROPOSED_AudioUntilStop, Texts(window));
        Click(window, PLACEHOLDER_Start);
        Assert.Contains(PROPOSED_AudioUntilStop, Texts(window));
        window.Shutdown();
    });

    [Fact]
    public Task Transcription_fault_is_announced_immediately_and_audio_can_be_saved() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var transcriber = new FailingTranscriber();
        var window = new MeetingsWindow(fixture.Host with { Transcriber = transcriber });
        window.Show(); window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
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

    [Fact]
    public Task Screen_changes_move_focus_and_announce_the_screen() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.True(Find(window, PROPOSED_Start).IsFocused);
        window.BeginAgreement(); Dispatcher.UIThread.RunJobs();
        Assert.True(All<ComboBox>(window).Single().IsFocused);
        Assert.Contains(All<TextBlock>(window), t => t.Text == PLACEHOLDER_Before && AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
        Click(window, PLACEHOLDER_Refused);
        Assert.True(All<TextBox>(window).Single().IsFocused);
        Click(window, PROPOSED_Cancel);
        Assert.True(Find(window, PROPOSED_Start).IsFocused);
        window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
        Assert.True(Find(window, PROPOSED_Mark).IsFocused);
        Assert.Contains(All<TextBlock>(window), t => t.Text == PROPOSED_Recording && AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
        Click(window, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find(window, PROPOSED_Meetings).IsFocused);
        window.Close(); return true;
    });

    [Fact]
    public Task Two_recordings_share_one_timer_handler() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show();
        var timer = (DispatcherTimer)typeof(MeetingsWindow).GetField("_timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var tick = typeof(DispatcherTimer).GetField("Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (var index = 0; index < 2; index++)
        {
            window.BeginAgreement(); Click(window, PLACEHOLDER_Start);
            Assert.Single(((Delegate)tick.GetValue(timer)!).GetInvocationList());
            Click(window, PROPOSED_Stop);
            await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
            Assert.False(timer.IsEnabled);
            Assert.Single(((Delegate)tick.GetValue(timer)!).GetInvocationList());
            Click(window, PROPOSED_Meetings);
        }
        Assert.Equal(2, fixture.Capture.Starts); Assert.Equal(2, fixture.Capture.Stops);
        window.Close(); return true;
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
