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
        public TestTranscriber Transcriber { get; }
        public AppPaths Paths { get; }
        public IMeetingStore Store { get; }
        public MeetingsHost Host { get; }
        public int Imports;
        public bool ImportsEnabled;
        public string? Copied;
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
        public float CurrentLevel => Active ? 0.5f : 0;
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

    [Fact]
    public Task Screen_changes_move_focus_and_announce_the_screen() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.True(Find(window, PROPOSED_Start).IsFocused);
        window.BeginAgreement(); Dispatcher.UIThread.RunJobs();
        Assert.True(All<ComboBox>(window).Single().IsFocused);
        Assert.Contains(All<TextBlock>(window), t => t.Text == APPROVED_Before && AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
        Click(window, APPROVED_Refused);
        Assert.True(All<TextBox>(window).Single().IsFocused);
        Click(window, APPROVED_Cancel);
        Assert.True(Find(window, PROPOSED_Start).IsFocused);
        window.BeginAgreement(); Click(window, APPROVED_Start);
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
            window.BeginAgreement(); Click(window, APPROVED_Start);
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
        Click(window, APPROVED_Start); KeyboardReach(window);
        Click(window, PROPOSED_Joined);
        var joined = Assert.Single(window.OwnedWindows);
        KeyboardReach(joined);
        Click(joined, APPROVED_Continue);
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

    private static IReadOnlyList<MeetingConfirmation> Confirmations(Fixture fixture, MeetingId id) =>
        ((MeetingsHost.CurrentSettingsStore)fixture.Store).ReadConfirmations(id);

    [Theory]
    [InlineData(0, "spoken")]
    [InlineData(1, "written")]
    public Task Live_options_and_approved_confirmation_are_saved_before_capture(int choice, string answer) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var persistedBeforeCapture = false;
        var window = new MeetingsWindow(fixture.Host with
        {
            SaveConfirmation = (id, confirmation) =>
            {
                Assert.False(fixture.Capture.Active);
                ((MeetingsHost.CurrentSettingsStore)fixture.Store).WriteConfirmation(id, confirmation);
                persistedBeforeCapture = true;
            },
        });
        window.Show(); window.BeginAgreement();
        var choices = All<ComboBox>(window).Single();
        Assert.Equal(new[] { APPROVED_Spoken, APPROVED_Written }, choices.Items.Cast<string>());
        choices.SelectedIndex = choice;
        Assert.Contains(APPROVED_Access, Texts(window));
        Assert.Contains(APPROVED_Uses, Texts(window));
        Assert.Contains(APPROVED_ConfirmationNote, Texts(window));
        Assert.Equal(0, fixture.Capture.Starts);
        Click(window, APPROVED_Start);
        Assert.True(persistedBeforeCapture);
        var meta = Assert.Single(fixture.Store.List());
        var record = Assert.Single(Confirmations(fixture, meta.Id));
        Assert.Equal(answer, record.Answer);
        Assert.Equal(AgreementKind.Live, record.Kind);
        Assert.Equal(meta.Agreement!.AcknowledgedAt, record.At);
        Assert.Equal(ApprovedScriptVersion, meta.Agreement.ScriptVersion);
        Assert.Contains(APPROVED_CaptureMicrophone, record.Script);
        window.Shutdown();
    });

    [Fact]
    public Task Arrival_cancellation_keeps_capture_paused_and_resume_requires_saved_answer() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        Click(window, PROPOSED_Joined);
        Assert.False(fixture.Capture.Active);
        Click(Assert.Single(window.OwnedWindows), APPROVED_Cancel);
        Assert.False(fixture.Capture.Active);
        Assert.Equal("unconfirmed", Confirmations(fixture, id).Last().Answer);
        Click(window, PROPOSED_Resume);
        Assert.False(fixture.Capture.Active);
        var before = DateTimeOffset.UtcNow;
        Click(Assert.Single(window.OwnedWindows), APPROVED_Continue);
        Assert.True(fixture.Capture.Active);
        var record = Confirmations(fixture, id).Last();
        Assert.Equal("agreed", record.Answer);
        Assert.InRange(record.At, before, DateTimeOffset.UtcNow);
        Assert.Equal(APPROVED_Joined, record.Script);
        Assert.Contains(fixture.Store.Read(id).Markers, m => m.Kind == MarkerKind.Joined);
        window.Shutdown();
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Refusal_stops_capture_and_keeps_notes_and_discard(bool discard, bool arrival) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        if (arrival)
        {
            Click(window, PROPOSED_Joined);
            Click(Assert.Single(window.OwnedWindows), APPROVED_Refused);
        }
        else Click(window, APPROVED_Refused);
        Assert.False(fixture.Capture.Active);
        // Pausing for a joiner already releases the microphone, so also require the live source to end.
        await Until(() => Volatile.Read(ref fixture.Transcriber.Finished) > 0);
        Assert.Equal("refused", Confirmations(fixture, id).Last().Answer);
        Assert.Contains(APPROVED_RefusalNotes, Texts(window));
        KeyboardReach(window);
        if (discard)
        {
            Click(window, PROPOSED_Discard);
            Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
            await Until(() => fixture.Store.List().Count == 0);
        }
        else
        {
            All<TextBox>(window).Single().Text = "Typed after refusal";
            Click(window, PROPOSED_SaveNotes);
            await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate));
            Assert.Contains(fixture.Store.ReadTranscript(id), line => line.Text == "Typed after refusal");
            Assert.Equal(1, fixture.Capture.Starts);
        }
        window.Close(); return true;
    });

    [Theory]
    [InlineData(ZoomImport.Ask)]
    [InlineData(ZoomImport.Always)]
    [InlineData(ZoomImport.Never)]
    public Task Every_import_path_requires_notice_then_file_confirmation_and_saves_importer(ZoomImport mode) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { ZoomImport = mode };
        var path = Path.Combine(fixture.Files.Root, "import.wav");
        File.WriteAllBytes(path, MeetingFixture.Wav());
        var window = new MeetingsWindow(fixture.Host); window.Show();
        window.BeginImportAgreement(path);
        Assert.Contains(APPROVED_EnableImportsBody, Texts(window));
        Assert.Equal(0, fixture.Imports);
        Click(window, APPROVED_Cancel);
        Assert.False(fixture.ImportsEnabled);
        window.BeginImportAgreement(path); Click(window, APPROVED_EnableImports);
        Assert.True(fixture.ImportsEnabled);
        Assert.Equal(0, fixture.Imports);
        Assert.Contains(APPROVED_ImportAgreement, Texts(window));
        Assert.Contains(APPROVED_Access, Texts(window));
        Assert.Contains(APPROVED_KeepForever, Texts(window));
        Click(window, APPROVED_Cancel);
        window.BeginImportAgreement(path);
        Assert.DoesNotContain(APPROVED_EnableImportsBody, Texts(window));
        Assert.Equal(0, fixture.Imports);
        Click(window, APPROVED_Import);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        var id = Assert.Single(fixture.Store.List()).Id;
        var record = Assert.Single(Confirmations(fixture, id));
        Assert.Equal(Environment.UserName, record.ImporterIdentity);
        Assert.Equal(AgreementKind.Import, record.Kind);
        Assert.Equal(ApprovedScriptVersion, record.ScriptVersion);
        Assert.Equal(MeetingFixture.Wav(), File.ReadAllBytes(path));
        Click(window, PROPOSED_Meetings); window.BeginImportAgreement(path);
        Assert.Contains(APPROVED_ImportAgreement, Texts(window));
        Assert.Equal(1, fixture.Imports);
        Click(window, APPROVED_Cancel); window.Close(); return true;
    });

    [Theory]
    [InlineData(SummaryEngine.Local, false)]
    [InlineData(SummaryEngine.OwnKey, true)]
    [InlineData(SummaryEngine.Ollama, true)]
    public Task Exactly_one_speech_and_summary_notice_describe_actual_settings(SummaryEngine engine, bool cloudSpeech) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { SummaryEngine = engine, KeepAudio = KeepAudio.Days90 };
        var window = new MeetingsWindow(fixture.Host with { CloudSpeechHost = () => cloudSpeech ? "speech.example" : null });
        window.Show(); window.BeginAgreement();
        var speech = cloudSpeech ? string.Format(APPROVED_CloudSpeech, "speech.example") : APPROVED_LocalSpeech;
        var summary = engine switch { SummaryEngine.OwnKey => string.Format(APPROVED_OwnKey, "summary.example"), SummaryEngine.Ollama => APPROVED_Ollama, _ => APPROVED_Local };
        Assert.Equal(1, Texts(window).Split(speech).Length - 1);
        Assert.Equal(1, Texts(window).Split(summary).Length - 1);
        if (engine == SummaryEngine.Ollama) Assert.Contains(string.Format(PROPOSED_SummaryHost, "summary.example"), Texts(window));
        Assert.Contains(string.Format(APPROVED_KeepDays, 90), Texts(window));
        Assert.DoesNotContain(APPROVED_AudioOff, Texts(window));
        Click(window, APPROVED_Cancel); window.Close();
    });

    [Fact]
    public Task Changed_settings_require_a_fresh_notice_before_capture() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement();
        fixture.Settings = fixture.Settings with { KeepAudio = KeepAudio.Days30 };
        Click(window, APPROVED_Start);
        Assert.Equal(0, fixture.Capture.Starts);
        Assert.Contains(string.Format(APPROVED_KeepDays, 30), Texts(window));
        Click(window, APPROVED_Start); Assert.True(fixture.Capture.Active);
        window.Shutdown();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Each_copy_or_export_requires_the_sharing_reminder(bool export) => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var id = fixture.Store.Create(fixture.Files.Meta());
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        var action = export ? PROPOSED_Export : PROPOSED_Copy;
        Click(window, action);
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Contains(APPROVED_ShareReminder, Texts(dialog));
        Assert.Null(fixture.Copied);
        KeyboardReach(dialog);
        Click(dialog, APPROVED_Cancel);
        Assert.Null(fixture.Copied);
        Click(window, action);
        if (!export)
        {
            Click(Assert.Single(window.OwnedWindows), APPROVED_ShareContinue);
            Assert.NotNull(fixture.Copied);
            fixture.Copied = null;
            Click(window, action);
            Assert.Null(fixture.Copied);
        }
        Click(Assert.Single(window.OwnedWindows), APPROVED_Cancel);
        window.Close();
    });

    [Fact]
    public Task Setup_keeps_meetings_off_until_notice_is_confirmed() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        fixture.Settings = fixture.Settings with { Enabled = false };
        var page = new MeetingsPage(shell, fixture.Host);
        var window = new Window { Content = page, Width = 900, Height = 1000 }; window.Show(); page.Refresh();
        All<ToggleSwitch>(page).Single().IsChecked = true;
        Assert.False(shell.MeetingsSettings.Enabled);
        var notice = Assert.Single(window.OwnedWindows);
        Assert.Contains(APPROVED_Disclaimer, Texts(notice));
        Assert.Contains(APPROVED_Enable, Texts(notice));
        Click(notice, APPROVED_Cancel);
        All<ToggleSwitch>(page).Single().IsChecked = false;
        All<ToggleSwitch>(page).Single().IsChecked = true;
        Click(Assert.Single(window.OwnedWindows), APPROVED_EnableConfirm);
        Assert.True(shell.MeetingsSettings.Enabled);
        window.Close();
    });

    [Fact]
    public Task Loopback_records_both_tracks_and_pauses_both_before_arrival() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var others = new TestCapture();
        var window = new MeetingsWindow(fixture.Host with { LoopbackCapture = () => others });
        window.Show(); window.BeginAgreement();
        Assert.Contains(APPROVED_CaptureSystem, Texts(window));
        Assert.False(others.Active); Assert.False(fixture.Capture.Active);
        Click(window, APPROVED_Start);
        Assert.True(others.Active); Assert.True(fixture.Capture.Active);
        var id = Assert.Single(fixture.Store.List()).Id;
        Click(window, PROPOSED_Joined);
        Assert.False(others.Active); Assert.False(fixture.Capture.Active);
        Click(Assert.Single(window.OwnedWindows), APPROVED_Continue);
        Assert.True(others.Active); Assert.True(fixture.Capture.Active);
        Click(window, PROPOSED_Stop);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        foreach (var channel in new[] { Channel.You, Channel.Others })
        {
            using var audio = fixture.Store.OpenAudio(id, channel);
            Assert.True(AudioBytes(audio).Length > 44);
            Assert.Contains(fixture.Store.ReadTranscript(id), line => line.Channel == channel);
        }
        window.Close(); return true;
    });

    [Theory]
    [InlineData(KeepAudio.Forever, false)]
    [InlineData(KeepAudio.Days30, true)]
    [InlineData(KeepAudio.Days90, true)]
    public Task Saved_audio_retention_preserves_text_and_delete_removes_all_meeting_files(KeepAudio keep, bool removed)
        => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { KeepAudio = keep };
        var id = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.AppendSegments(id, MeetingFixture.Segments);
        fixture.Store.WriteSummary(id, MeetingFixture.Summary());
        fixture.Host.SaveConfirmation(id, new("spoken", DateTimeOffset.UtcNow, AgreementKind.Live, ApprovedScriptVersion, APPROVED_Agreement));
        using (var audio = new MemoryStream(MeetingFixture.Wav())) fixture.Store.WriteAudio(id, Channel.You, audio);
        fixture.Store.ApplyRetention(DateTimeOffset.UtcNow);
        if (removed) Assert.Throws<FileNotFoundException>(() => fixture.Store.OpenAudio(id, Channel.You));
        else { using var audio = fixture.Store.OpenAudio(id, Channel.You); Assert.Equal(MeetingFixture.Wav(), AudioBytes(audio)); }
        Assert.NotEmpty(fixture.Store.ReadTranscript(id)); Assert.NotNull(fixture.Store.ReadSummary(id));
        var folder = Path.Combine(fixture.Files.Root, "meetings", id.Value);
        var confirmationBytes = File.ReadAllBytes(Path.Combine(folder, "confirmations.json.enc"));
        Assert.DoesNotContain(APPROVED_Agreement, System.Text.Encoding.UTF8.GetString(confirmationBytes));
        fixture.Store.Delete(id); Assert.False(Directory.Exists(folder));
    });

    private sealed class Decoder : Dictum.Platform.IAudioDecoder
    {
        public int Calls;
        public CapturedAudio Decode(string path) { Calls++; return new(new float[1600], 0); }
    }

    [Fact]
    public async Task Two_track_watermark_advances_with_consumption_and_pause_drains_capture_tails()
    {
        var microphone = new TestCapture();
        var others = new TestCapture();
        using var files = new MeetingFixture();
        var store = files.Store(); var id = store.Create(files.Meta());
        using var saving = new LiveAudioWriter(store, id, Channel.You, Channel.Others);
        using var source = new TwoTrackMeetingSource(microphone, others, null, 0.01, saving);
        source.Start(); source.Pause();
        Assert.False(microphone.Active); Assert.False(others.Active);
        source.Resume(); source.Stop();
        var channels = new List<Channel>();
        await foreach (var part in source.ReadAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(source.Watermark <= source.Elapsed);
            if (part.Part.Samples.Length > 0) channels.Add(part.Channel);
        }
        Assert.Equal(new[] { Channel.You, Channel.Others, Channel.You, Channel.Others }, channels);
        source.CompleteAudio();
        foreach (var channel in source.Tracks)
        {
            using var audio = source.Audio(channel);
            Assert.True(AudioBytes(audio).Length > 44);
        }
    }

    [Fact]
    public Task Failed_arrival_confirmation_never_resumes_capture() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host with
        {
            SaveConfirmation = (id, confirmation) =>
            {
                if (confirmation.Answer == "agreed") throw new IOException("Synthetic confirmation failure");
                ((MeetingsHost.CurrentSettingsStore)fixture.Store).WriteConfirmation(id, confirmation);
            },
        });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        Click(window, PROPOSED_Joined);
        var dialog = Assert.Single(window.OwnedWindows);
        Click(dialog, APPROVED_Continue);
        Assert.False(fixture.Capture.Active);
        Assert.True(dialog.IsVisible);
        Click(dialog, APPROVED_Cancel);
        window.Shutdown();
    });

    private sealed class Factory : IMeetingTranscriberFactory
    {
        public IMeetingTranscriber Create(IMeetingStore store) => new TestTranscriber(store);
    }
    [Fact]
    public async Task Production_host_uses_supplied_platform_decoder_and_persists_import_notice()
    {
        using var fixture = new Fixture();
        var folder = Path.Combine(fixture.Files.Root, "chosen-meetings");
        fixture.Settings = fixture.Settings with { Folder = folder };
        var decoder = new Decoder();
        MeetingsHost Create() => MeetingsHost.Create(fixture.Paths, () => fixture.Settings, fixture.Files.Credentials,
            new Factory(), () => fixture.Capture, (_, _) => Task.FromResult<ILlmClient>(new TestLlm()), _ => { }, decoder);
        var host = Create();
        Assert.False(host.ImportsEnabled()); host.EnableImports(); Assert.True(Create().ImportsEnabled());
        var path = Path.Combine(fixture.Files.Root, "decoded.mp3"); File.WriteAllBytes(path, [1, 2, 3]);
        var id = await host.Import(path, new(DateTimeOffset.UtcNow, "written", ApprovedScriptVersion, AgreementKind.Import), TestContext.Current.CancellationToken);
        Assert.Equal(1, decoder.Calls);
        Assert.True(Directory.Exists(Path.Combine(folder, id.Value)));
        Assert.NotNull(fixture.Files.Credentials.Read(MeetingKeyring.Variable, "owner"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        using var saved = host.Store.OpenAudio(id, Channel.Mixed); Assert.True(AudioBytes(saved).Length > 44);
    }
    [Fact]
    public Task Chunk_write_failure_surfaces_audio_save_failure_while_recording() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var store = new FaultStore(fixture.Store) { FailChunk = true };
        var window = new MeetingsWindow(fixture.Host with { Store = store });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        fixture.Capture.Emit();
        await Until(() => Texts(window).Contains(PROPOSED_AudioSaveFailed) && window.IsEnabled);
        Assert.False(fixture.Capture.Active);
        Click(window, PROPOSED_Discard); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => fixture.Store.List().Count == 0);
        Assert.Empty(Directory.GetFiles(fixture.Files.Root, "*.chunks.enc", SearchOption.AllDirectories));
        window.Close(); return true;
    });

    [Fact]
    public Task Capture_start_rollback_deletes_created_chunk_files() => UiSession.Run(() =>
    {
        using var fixture = new Fixture(); fixture.Capture.FailStart = true;
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        Assert.Empty(fixture.Store.List());
        Assert.Empty(Directory.GetFiles(fixture.Files.Root, "*.chunks.enc", SearchOption.AllDirectories));
        window.Close();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Discard_deletes_all_live_chunk_files(bool dual) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = new MeetingsWindow(fixture.Host with { LoopbackCapture = () => dual ? new TestCapture() : null });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        Assert.NotEmpty(Directory.GetFiles(fixture.Files.Root, "*.chunks.enc", SearchOption.AllDirectories));
        Click(window, PROPOSED_Discard); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => fixture.Store.List().Count == 0);
        Assert.Empty(Directory.GetFiles(fixture.Files.Root, "*.chunks.enc", SearchOption.AllDirectories));
        window.Close(); return true;
    });

    [Fact]
    public Task Recording_route_change_pauses_until_fresh_confirmation_is_saved() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        string speech = "first.example";
        var failSave = false;
        var host = fixture.Host with
        {
            CloudSpeechHost = () => speech,
            SaveConfirmation = (id, confirmation) =>
            {
                if (failSave) throw new IOException("Synthetic confirmation failure");
                fixture.Host.SaveConfirmation(id, confirmation);
            },
        };
        var window = new MeetingsWindow(host); window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var id = Assert.Single(fixture.Store.List()).Id;
        Assert.Equal("first.example", Assert.Single(Confirmations(fixture, id)).Route!.SpeechHost);
        speech = "second.example"; window.RefreshSettings();
        Assert.False(fixture.Capture.Active);
        var notice = Assert.Single(window.OwnedWindows);
        Assert.Contains(string.Format(APPROVED_CloudSpeech, speech), Texts(notice));
        failSave = true; Click(notice, APPROVED_Continue);
        Assert.False(fixture.Capture.Active); Assert.True(notice.IsVisible);
        Assert.Contains(PROPOSED_Error, Texts(window));
        failSave = false; Click(notice, APPROVED_Continue);
        await Until(() => fixture.Capture.Active);
        Assert.Equal(speech, Confirmations(fixture, id).Last().Route!.SpeechHost);
        window.Shutdown(); return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Stop_and_regenerate_require_saved_current_summary_route(bool regenerate) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var summaryHost = "first.example";
        var sends = 0;
        var host = fixture.Host with
        {
            SummaryHost = () => summaryHost,
            SummaryClient = (_, _) => { sends++; return Task.FromResult<ILlmClient>(new TestLlm()); },
        };
        var window = new MeetingsWindow(host); window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        fixture.Settings = fixture.Settings with { SummaryEngine = SummaryEngine.OwnKey };
        if (regenerate)
        {
            // Confirm the first changed route, then change its host for Regenerate.
            window.RefreshSettings(); Click(Assert.Single(window.OwnedWindows), APPROVED_Continue);
            await Until(() => fixture.Capture.Active);
        }
        Click(window, PROPOSED_Stop);
        if (regenerate)
        {
            await Until(() => sends == 1 && Find(window, PROPOSED_Regenerate).IsEffectivelyEnabled);
            summaryHost = "second.example";
            Click(window, PROPOSED_Regenerate);
        }
        await Until(() => window.OwnedWindows.Count() == 1 && Assert.Single(window.OwnedWindows).IsEnabled);
        Assert.Equal(regenerate ? 1 : 0, sends);
        var notice = Assert.Single(window.OwnedWindows);
        Assert.Contains(string.Format(APPROVED_OwnKey, summaryHost), Texts(notice));
        Click(notice, APPROVED_Continue);
        await Until(() => sends == (regenerate ? 2 : 1));
        var id = Assert.Single(fixture.Store.List()).Id;
        Assert.Equal(summaryHost, Confirmations(fixture, id).Last().Route!.SummaryHost);
        window.Close(); return true;
    });

    [Fact]
    public Task Anthropic_notice_uses_the_effective_custom_endpoint() => UiSession.Run(() =>
    {
        using var fixture = new Fixture(); using var shell = fixture.Shell();
        var configured = SettingsLoader.LoadFile(fixture.Paths.SettingsFile).Settings;
        configured = configured with { Llm = configured.Llm with { Provider = LlmSettings.Anthropic, BaseUrl = "https://summary.example/v1" } };
        typeof(AppShell).GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(shell, configured);
        Assert.Equal("summary.example", shell.CreateMeetingsHost().SummaryHost());
    });

    private sealed class ControlledTranscriber : IMeetingTranscriber
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ended;
        public bool DelayCancellation;
        public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { if (DelayCancellation) await Release.Task; else await Release.Task.WaitAsync(cancellationToken); }
            finally { Ended = true; }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Shutdown_failed_or_timed_out_drain_vetoes_until_user_decides(bool timeout) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var controlled = new ControlledTranscriber(); var failing = new FailingTranscriber();
        var window = new MeetingsWindow(fixture.Host with { Transcriber = timeout ? controlled : failing }) { DrainTimeout = TimeSpan.FromMilliseconds(20) };
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        if (timeout) await controlled.Entered.Task; else failing.Fail.TrySetResult();
        Assert.False(window.Shutdown());
        if (timeout) Assert.True(controlled.Ended);
        Assert.True(window.IsVisible); Assert.Single(fixture.Store.List());
        Assert.Contains(PROPOSED_TranscriptionFailed, Texts(window));
        Assert.True(Find(window, PROPOSED_SaveAudioStop).IsEffectivelyEnabled);
        Click(window, PROPOSED_Discard); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => fixture.Store.List().Count == 0);
        window.Shutdown(); return true;
    });

    // A shutdown that skips the active stop blocks on the drain; the timeout turns that hang into a failure.
    [Fact(Timeout = 30000)]
    public Task Shutdown_awaits_confirmed_discard_and_never_saves_it() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var controlled = new ControlledTranscriber();
        var store = new FaultStore(fixture.Store);
        var window = new MeetingsWindow(fixture.Host with { Store = store, Transcriber = controlled });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start); await controlled.Entered.Task;
        var ready = false; window.ShutdownReady += () => ready = true;
        Click(window, PROPOSED_Discard); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        Assert.False(window.Shutdown()); Assert.False(ready); Assert.Single(fixture.Store.List());
        controlled.Release.TrySetResult();
        await Until(() => ready);
        Assert.Empty(fixture.Store.List()); Assert.Equal(0, store.AudioAttempts);
        Assert.True(controlled.Ended); window.Shutdown(); return true;
    });

    [Fact]
    public Task Shutdown_during_stop_drain_failure_waits_for_save_audio_decision() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var failing = new FailingTranscriber();
        var store = new FaultStore(fixture.Store);
        var window = new MeetingsWindow(fixture.Host with { Store = store, Transcriber = failing });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        var ready = false; window.ShutdownReady += () => ready = true;
        Click(window, PROPOSED_Stop); Assert.False(window.Shutdown());
        failing.Fail.TrySetResult();
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_SaveAudioStop && b.IsEffectivelyEnabled));
        Assert.False(ready); Assert.Equal(0, store.AudioAttempts);
        Click(window, PROPOSED_SaveAudioStop); await Until(() => ready);
        Assert.Single(fixture.Store.List()); Assert.Equal(1, store.AudioAttempts);
        window.Shutdown(); return true;
    });

    [Fact]
    public Task Declining_route_notice_while_worker_ends_stops_without_resuming() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var controlled = new ControlledTranscriber { DelayCancellation = true };
        var window = new MeetingsWindow(fixture.Host with { Transcriber = controlled });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start); await controlled.Entered.Task;
        fixture.Settings = fixture.Settings with { SummaryEngine = SummaryEngine.OwnKey };
        window.RefreshSettings(); var notice = Assert.Single(window.OwnedWindows);
        Click(notice, APPROVED_Continue); Click(notice, APPROVED_Cancel);
        controlled.Release.TrySetResult();
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Regenerate && b.IsEffectivelyEnabled));
        Assert.False(fixture.Capture.Active); Assert.Equal(1, fixture.Capture.Starts);
        window.Close(); return true;
    });

    [Fact]
    public Task Shutdown_cancels_import_and_awaits_rollback_with_identity_saved_before_transcription() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var controlled = new ControlledTranscriber();
        fixture.ImportsEnabled = true;
        var importer = new MeetingImporter(fixture.Store, controlled, new WavOnlyDecoder(),
            saveConfirmation: (id, agreement) => fixture.Host.SaveConfirmation(id, agreement.Confirmation!));
        var host = fixture.Host with { Import = (path, agreement, cancellation) => importer.ImportAsync(path, MeetingSource.ImportedFile, agreement, cancellationToken: cancellation) };
        var path = Path.Combine(fixture.Files.Root, "import.wav"); File.WriteAllBytes(path, MeetingFixture.Wav());
        var window = new MeetingsWindow(host); window.Show(); window.BeginImportAgreement(path); Click(window, APPROVED_Import);
        await controlled.Entered.Task;
        var id = Assert.Single(fixture.Store.List()).Id;
        Assert.Equal(Environment.UserName, Assert.Single(Confirmations(fixture, id)).ImporterIdentity);
        var ready = false; window.ShutdownReady += () => ready = true;
        Assert.False(window.Shutdown());
        await Until(() => ready);
        Assert.True(controlled.Ended); Assert.Empty(fixture.Store.List());
        window.Shutdown(); return true;
    });

    [Fact]
    public Task Failed_import_confirmation_rolls_back_before_transcription() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var controlled = new ControlledTranscriber();
        fixture.ImportsEnabled = true;
        var importer = new MeetingImporter(fixture.Store, controlled, new WavOnlyDecoder(),
            saveConfirmation: (_, _) => throw new IOException("Synthetic confirmation failure"));
        var host = fixture.Host with { Import = (path, agreement, cancellation) => importer.ImportAsync(path, MeetingSource.ImportedFile, agreement, cancellationToken: cancellation) };
        var path = Path.Combine(fixture.Files.Root, "import.wav"); File.WriteAllBytes(path, MeetingFixture.Wav());
        var window = new MeetingsWindow(host); window.Show(); window.BeginImportAgreement(path); Click(window, APPROVED_Import);
        await Until(() => Texts(window).Contains(PROPOSED_Error));
        Assert.False(controlled.Entered.Task.IsCompleted); Assert.Empty(fixture.Store.List());
        Assert.Equal(MeetingFixture.Wav(), File.ReadAllBytes(path)); window.Close(); return true;
    });

    [Fact]
    public Task Failed_discard_delete_preserves_discard_intent_on_retry() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var store = new FaultStore(fixture.Store) { FailDelete = true };
        var window = new MeetingsWindow(fixture.Host with { Store = store });
        window.Show(); window.BeginAgreement(); Click(window, APPROVED_Start);
        Click(window, PROPOSED_Discard); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => All<Button>(window).Any(b => AutomationProperties.GetName(b) == PROPOSED_Retry && b.IsEffectivelyEnabled));
        Assert.False(fixture.Capture.Active); Assert.Single(fixture.Store.List());
        store.FailDelete = false; Click(window, PROPOSED_Retry);
        await Until(() => fixture.Store.List().Count == 0);
        Assert.Equal(0, store.AudioAttempts); window.Close(); return true;
    });

    [Theory]
    [InlineData("mark")]
    [InlineData("cancel")]
    [InlineData("discard")]
    [InlineData("delete")]
    public Task Synchronous_storage_failures_are_contained_and_actions_remain_available(string action) => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); var failAnswer = false;
        var store = new FaultStore(fixture.Store);
        var window = new MeetingsWindow(fixture.Host with
        {
            Store = store,
            SaveConfirmation = (id, confirmation) =>
            {
                if (failAnswer) throw new IOException("Synthetic confirmation failure");
                fixture.Host.SaveConfirmation(id, confirmation);
            },
        });
        window.Show();
        if (action == "delete")
        {
            var id = store.Create(fixture.Files.Meta()); window.ShowMeeting(id);
            store.FailDelete = true;
            Click(window, PROPOSED_Delete); Click(Assert.Single(window.OwnedWindows), PROPOSED_Delete);
            Assert.Single(store.List()); Assert.Contains(PROPOSED_Error, Texts(window));
            store.FailDelete = false;
            Click(window, PROPOSED_Delete); Click(Assert.Single(window.OwnedWindows), PROPOSED_Delete);
            Assert.Empty(store.List()); window.Close(); return true;
        }
        window.BeginAgreement(); Click(window, APPROVED_Start);
        if (action == "mark")
        {
            store.FailMarker = true; Click(window, PROPOSED_Mark);
            Assert.True(fixture.Capture.Active);
        }
        else
        {
            Click(window, PROPOSED_Joined); failAnswer = true;
            var dialog = Assert.Single(window.OwnedWindows);
            Click(dialog, action == "cancel" ? APPROVED_Cancel : PROPOSED_Discard);
            Assert.False(fixture.Capture.Active); Assert.True(dialog.IsVisible);
            failAnswer = false; Click(dialog, APPROVED_Cancel);
        }
        Assert.Contains(PROPOSED_Error, Texts(window));
        Click(window, APPROVED_Refused);
        Assert.True(Find(window, PROPOSED_SaveNotes).IsEffectivelyEnabled);
        Click(window, PROPOSED_Discard); Click(Assert.Single(window.OwnedWindows), PROPOSED_Discard);
        await Until(() => store.List().Count == 0); window.Close(); return true;
    });

}
