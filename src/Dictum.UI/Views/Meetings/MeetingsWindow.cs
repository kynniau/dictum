using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dictum.Core;
using Dictum.Meetings;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Store;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.UI.Views.Meetings;

internal sealed class MeetingsWindow : Window
{
    private readonly MeetingsHost _host;
    private readonly ContentControl _screen = new();
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private IHostMeetingSource? _source;
    private Dictum.Audio.IAudioCapture? _pendingLoopback;
    private bool _awaitingArrival;
    private Task? _transcription;
    private Task? _activeStop, _activeImport;
    private CancellationTokenSource? _importCancellation;
    private MeetingProcessingRoute? _confirmedRoute;
    private MeetingProcessingRoute? _importRoute;
    private Window? _routeDialog;
    private TextBlock? _liveLines;
    private bool _discardRequested;
    private bool _routeChangePending;
    internal TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _recordingCancellation;
    private MeetingId? _recording;
    private bool _paused, _busy;
    private bool _shuttingDown, _ending, _transcriptionFailed;
    private bool _exitPending;
    internal event Action? ShutdownReady;
    private Button? _pauseButton;
    private TextBlock? _captureState;
    private TextBlock? _elapsed;
    private ProgressBar? _meter;
    private string _search = "";
    private MeetingsEntries? _entries;
    private Button? _importButton;

    public MeetingsWindow(MeetingsHost host)
    {
        _host = host;
        Title = PROPOSED_Meetings;
        Width = 860; Height = 680; MinWidth = 640; MinHeight = 420;
        AutomationProperties.SetName(this, PROPOSED_Meetings);
        AutomationProperties.SetName(_status, PROPOSED_Status);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Content = new DockPanel
        {
            Margin = new Thickness(24), LastChildFill = true,
            Children = { Ui.Stack(12, _status, _screen) },
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            if (_source is null) return;
            if (_elapsed is not null) _elapsed.Text = _source.Elapsed.ToString(@"hh\:mm\:ss");
            if (_meter is not null) _meter.Value = _source.Level;
        };
        Closing += (_, e) =>
        {
            if (!_shuttingDown && (_recording is not null || _busy)) { e.Cancel = true; Say(PROPOSED_CloseRecording); }
        };
        Closed += (_, _) => { _timer.Stop(); _source?.Dispose(); (_pendingLoopback as IDisposable)?.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); };
        ShowList();
    }

    internal static Button Button(string text, Action action) => Ui.Button(text, "secondary", action, name: text);
    private Button SafeButton(string text, Action action) => Ui.Button(text, "secondary", () =>
    {
        try { action(); }
        catch { Say(PROPOSED_Error); }
    }, name: text);
    internal static Control DialogContent(Window dialog, params Control[] controls)
    {
        var content = new ScrollViewer { MaxHeight = 600, Margin = new Thickness(24), Content = Ui.Stack(16, controls) };
        if (controls.FirstOrDefault() is { } notice) AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            content.GetLogicalDescendants().OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
        return content;
    }
    internal static T Named<T>(T control, string name) where T : Control
    {
        AutomationProperties.SetName(control, name);
        return control;
    }
    private void Say(string text) => _status.Text = text;
    private void Screen(params Control[] controls)
    {
        var content = Named(new ScrollViewer { Content = Ui.Stack(16, controls) }, PROPOSED_Meetings);
        _screen.Content = content;
        var heading = content.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault();
        Say(heading?.Text ?? PROPOSED_Meetings);
        FocusScreen(content);
    }
    private void FocusScreen(Control content)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_screen.Content, content)) return;
            var first = content.GetLogicalDescendants().OfType<Control>()
                .FirstOrDefault(c => (c is Avalonia.Controls.Button or TextBox or ComboBox or ToggleSwitch) && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
            if (first is not null) first.Focus(NavigationMethod.Tab);
            else if (content.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault() is { } heading)
            { heading.Focusable = true; heading.Focus(NavigationMethod.Tab); }
        }, DispatcherPriority.Background);
    }
    private async Task Work(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _screen.IsEnabled = false;
        Say(PROPOSED_Busy);
        try { await action(); if (_status.Text == PROPOSED_Busy) Say(""); }
        catch (OperationCanceledException) { }
        catch { Say(PROPOSED_Error); }
        finally
        {
            _busy = false; _screen.IsEnabled = true;
            if (_screen.Content is Control content) FocusScreen(content);
        }
    }

    internal void ShowList()
    {
        if (_recording is not null) return;
        (_pendingLoopback as IDisposable)?.Dispose(); _pendingLoopback = null;
        var results = Ui.Stack(8);
        void Search()
        {
            results.Children.Clear();
            try
            {
                _host.Store.ApplyRetention(DateTimeOffset.UtcNow);
                var found = string.IsNullOrWhiteSpace(_search) ? _host.Store.List()
                    : _host.Store.Search(_search.Trim()).Select(row => row.Item1)
                        .Concat(_host.Store.List().Where(meta => meta.Title.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)))
                        .DistinctBy(meta => meta.Id).ToArray();
                foreach (var meta in found.OrderByDescending(m => m.StartedAt))
                {
                    var label = string.Format(PROPOSED_ListRow, meta.Title, meta.StartedAt.ToLocalTime(), meta.Duration, _host.Store.SizeOf(meta.Id));
                    results.Children.Add(SafeButton(label, () => ShowMeeting(meta.Id)));
                }
                if (results.Children.Count == 0) results.Children.Add(Ui.Text(PROPOSED_Empty));
            }
            catch { Say(PROPOSED_Error); }
        }
        var search = Named(new TextBox { Text = _search, Watermark = PROPOSED_Search }, PROPOSED_Search);
        search.TextChanged += (_, _) => { _search = search.Text ?? ""; Search(); };
        _entries = new MeetingsEntries(_host.Settings, BeginAgreement, ShowList);
        _importButton = SafeButton(PROPOSED_Import, () => _ = PickImport());
        _importButton.IsVisible = _host.Settings().Enabled;
        Screen(Ui.Heading(PROPOSED_Meetings), _entries, _importButton, search, results);
        Search();
    }

    internal void RefreshSettings()
    {
        _entries?.Refresh();
        if (_importButton is not null) _importButton.IsVisible = _host.Settings().Enabled;
        if (_recording is { } id && !_ending && _confirmedRoute != _host.ProcessingRoute())
        {
            _routeChangePending = true;
            _recordingCancellation?.Cancel();
            _source?.Pause();
            _paused = true;
            UpdatePauseState();
            ShowRouteNotice(id, true, null);
        }
        if (_activeImport is { IsCompleted: false } && _importRoute != _host.ProcessingRoute()) _importCancellation?.Cancel();
    }

    private async Task PickImport()
    {
        if (_host.ChooseImport is { } choose)
        {
            if (await choose() is { } chosen) BeginImportAgreement(chosen);
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = PROPOSED_Import, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(PROPOSED_Import) { Patterns = ["*.wav", "*.mp3", "*.m4a", "*.mp4", "*.flac", "*.wma", "*.aac"] }] });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) BeginImportAgreement(path);
    }
    internal void BeginImportAgreement(string path)
    {
        if (_recording is not null || _busy || !_host.Settings().Enabled) return;
        if (!_host.ImportsEnabled())
        {
            Screen(Ui.Heading(APPROVED_EnableImportsTitle), Ui.Text(APPROVED_EnableImportsBody),
                Ui.Row(8, SafeButton(APPROVED_Cancel, ShowList), SafeButton(APPROVED_EnableImports, () =>
                {
                    try { _host.EnableImports(); if (_host.ImportsEnabled()) Agreement(path); }
                    catch { Say(PROPOSED_Error); }
                })));
            return;
        }
        Agreement(path);
    }
    internal void BeginAgreement() { if (_recording is null && !_busy && _host.Settings().Enabled) Agreement(null); }
    private void Agreement(string? importPath)
    {
        var importing = importPath is not null;
        (_pendingLoopback as IDisposable)?.Dispose(); _pendingLoopback = null;
        if (!importing) _pendingLoopback = _host.LoopbackCapture();
        var how = Named(new ComboBox { ItemsSource = new[] { APPROVED_Spoken, APPROVED_Written }, SelectedIndex = 0 }, APPROVED_How);
        var settings = _host.Settings();
        var speechHostAtNotice = _host.CloudSpeechHost();
        var summaryHostAtNotice = _host.SummaryHost();
        var routeAtNotice = _host.ProcessingRoute();
        var summaryRoute = settings.SummaryEngine switch { SummaryEngine.Ollama => APPROVED_Ollama, SummaryEngine.OwnKey => string.Format(APPROVED_OwnKey, summaryHostAtNotice ?? PROPOSED_UnconfiguredProvider), _ => APPROVED_Local };
        if (settings.SummaryEngine == SummaryEngine.Ollama && summaryHostAtNotice is not null)
            summaryRoute += Environment.NewLine + string.Format(PROPOSED_SummaryHost, summaryHostAtNotice);
        summaryRoute = (speechHostAtNotice is { } speechHost ? string.Format(APPROVED_CloudSpeech, speechHost)
            : APPROVED_LocalSpeech) + Environment.NewLine + summaryRoute;
        var retention = settings.KeepAudio == KeepAudio.Forever ? APPROVED_KeepForever
            : string.Format(APPROVED_KeepDays, settings.KeepAudio == KeepAudio.Days30 ? 30 : 90);
        var script = string.Join("\n\n", importing ? APPROVED_ImportAgreement : APPROVED_Agreement,
            importing ? "" : (_pendingLoopback is null ? APPROVED_CaptureMicrophone : APPROVED_CaptureSystem),
            importing ? "" : APPROVED_Uses, APPROVED_Access, summaryRoute, retention, APPROVED_ConfirmationNote);
        var acknowledge = SafeButton(importing ? APPROVED_Import : APPROVED_Start, () => _ = Work(async () =>
        {
            if (!_host.Settings().Enabled) { ShowList(); return; }
            if (_host.Settings() != settings || _host.CloudSpeechHost() != speechHostAtNotice || _host.SummaryHost() != summaryHostAtNotice)
            { Agreement(importPath); return; }
            if (importing && !_host.ImportsEnabled()) { BeginImportAgreement(importPath!); return; }
            var agreement = new Agreement(DateTimeOffset.UtcNow,
                how.SelectedIndex == 1 ? "written" : "spoken", ApprovedScriptVersion,
                importing ? AgreementKind.Import : AgreementKind.Live);
            agreement = agreement with
            {
                Confirmation = new(how.SelectedIndex == 1 ? "written" : "spoken", agreement.AcknowledgedAt,
                    importing ? AgreementKind.Import : AgreementKind.Live, ApprovedScriptVersion, script,
                    importing ? Environment.UserName : null, routeAtNotice),
            };
            if (importing)
            {
                _importCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _importRoute = routeAtNotice;
                try
                {
                    _activeImport = ImportMeeting(importPath!, agreement, _importCancellation.Token);
                    await _activeImport;
                }
                finally { _importCancellation.Dispose(); _importCancellation = null; }
            }
            else StartRecording(agreement, script);
        }));
        Screen(Ui.Heading(importing ? APPROVED_BeforeImport : APPROVED_Before),
            Ui.Card(Ui.Stack(8, Ui.Text(script))),
            Ui.Text(APPROVED_How), how, SafeButton(APPROVED_Refused, TakeNotes), Ui.Row(8, SafeButton(APPROVED_Cancel, ShowList), acknowledge));
    }

    private void TakeNotes()
    {
        var notes = Named(new TextBox { AcceptsReturn = true, MinHeight = 180, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, PROPOSED_Notes);
        Screen(Ui.Heading(PROPOSED_Notes), Ui.Text(_recording is null ? PROPOSED_NotesText : APPROVED_RefusalNotes), notes,
            Ui.Row(8, SafeButton(_recording is null ? APPROVED_Cancel : PROPOSED_Discard, () => { if (_recording is null) ShowList(); else ConfirmDiscard(); }), SafeButton(PROPOSED_SaveNotes, () => _ = Work(async () =>
            {
                if (string.IsNullOrWhiteSpace(notes.Text)) return;
                if (_recording is { } existing)
                {
                    _host.Store.AppendSegments(existing, [new Segment(_source!.Elapsed, _source.Elapsed, Channel.Mixed, notes.Text)]);
                    await StopRecording(false);
                    return;
                }
                var meta = new MeetingMeta(MeetingId.New(), PROPOSED_Notes, DateTimeOffset.UtcNow,
                    TimeSpan.Zero, MeetingSource.Recorded, null, [], [], false, null, null);
                try
                {
                    var id = _host.Store.Create(meta);
                    _host.Store.AppendSegments(id, [new Segment(TimeSpan.Zero, TimeSpan.Zero, Channel.Mixed, notes.Text)]);
                    ShowMeeting(id);
                }
                catch { Rollback(meta); throw; }
            }))));
        notes.Focus(NavigationMethod.Tab);
    }

    private void StartRecording(Agreement agreement, string script)
    {
        var meta = new MeetingMeta(MeetingId.New(), PROPOSED_Meetings, agreement.AcknowledgedAt, TimeSpan.Zero,
            MeetingSource.Recorded, agreement, [], [], false, null, null);
        try
        {
            var id = _host.Store.Create(meta);
            _recording = id;
            _host.SaveConfirmation(id, agreement.Confirmation!);
            _confirmedRoute = agreement.Confirmation!.Route;
            _transcriptionFailed = false;
            var lines = Named(Ui.Text(""), PROPOSED_Live);
            _liveLines = lines;
            AutomationProperties.SetLiveSetting(lines, AutomationLiveSetting.Polite);
            var state = Named(Ui.Text(PROPOSED_Recording), PROPOSED_Status);
            AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
            var elapsed = Named(Ui.Text(""), PROPOSED_Elapsed);
            var meter = Named(new ProgressBar { Minimum = 0, Maximum = 1, Height = 16 }, PROPOSED_You);
            var saving = new LiveAudioWriter(_host.Store, id, _pendingLoopback is null ? [Channel.You] : [Channel.You, Channel.Others]);
            try
            {
                _source = _pendingLoopback is { } system
                    ? new TwoTrackMeetingSource(_host.Capture(), system, _host.MicrophoneDevice(), _host.SilenceThreshold(), saving)
                    : new MicrophoneMeetingSource(_host.Capture(), _host.MicrophoneDevice(), _host.SilenceThreshold(), saving);
            }
            catch { saving.Dispose(); throw; }
            _pendingLoopback = null;
            _source.Failed += error => Dispatcher.UIThread.Post(() => _ = Work(async () =>
            {
                if (error is AudioSaveException) { StopCapture(); await DrainTranscription(); ShowSaveFailure(); }
                else { await StopRecording(false); Say(PROPOSED_Error); }
            }));
            _source.Start();
            var source = _source;
            var pause = SafeButton(PROPOSED_Pause, () => { });
            _pauseButton = pause; _captureState = state;
            pause.Click += (_, _) =>
            {
                try
                {
                    if (_confirmedRoute != _host.ProcessingRoute()) { ShowRouteNotice(id, true, null); return; }
                    if (_awaitingArrival) { ShowJoined(); return; }
                    if (_paused) _source.Resume(); else _source.Pause();
                    _paused = !_paused;
                    pause.Content = _paused ? PROPOSED_Resume : PROPOSED_Pause;
                    AutomationProperties.SetName(pause, _paused ? PROPOSED_Resume : PROPOSED_Pause);
                    state.Text = _paused ? PROPOSED_Paused : PROPOSED_Recording;
                    _host.Store.AddMarker(id, new(_source.Elapsed, _paused ? MarkerKind.Paused : MarkerKind.Resumed));
                }
                catch { Say(PROPOSED_Error); }
            };
            Screen(Ui.Heading(PROPOSED_Recording), Ui.Text(_source.Tracks.Count == 2 ? APPROVED_CaptureSystem : APPROVED_MicOnly),
                Ui.Text(string.Format(APPROVED_Acknowledged, agreement.AcknowledgedAt.ToLocalTime())), state, elapsed,
                Ui.Card(Ui.Stack(8, Ui.Text(PROPOSED_You), meter)), Ui.Card(Ui.Stack(8, Ui.Heading(PROPOSED_Live), lines)),
                Ui.Row(8, SafeButton(PROPOSED_Mark, () => { _host.Store.AddMarker(id, new(_source.Elapsed, MarkerKind.Moment)); Say(PROPOSED_Marked); }),
                    SafeButton(PROPOSED_Joined, ShowJoined), pause),
                Ui.Row(8, SafeButton(APPROVED_Refused, RefuseRecording), SafeButton(PROPOSED_Discard, ConfirmDiscard), SafeButton(PROPOSED_Stop, () => _ = Work(() => StopRecording(false)))));
            _elapsed = elapsed;
            _meter = meter;
            _timer.Start();
            // Start the writer only once capture and the recording screen are ready.
            _recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var cancellation = _recordingCancellation.Token;
            var route = _confirmedRoute!;
            _transcription = Task.Run(async () =>
            {
                using var consent = _host.UseConfirmedRoute(route);
                await _host.Transcriber.RunAsync(id, source, segment => Dispatcher.UIThread.Post(() =>
                    lines.Text += FormatSegment(segment) + Environment.NewLine), cancellation);
            });
            _ = ObserveTranscription(_transcription, source, cancellation);
        }
        catch
        {
            _timer.Stop();
            try { _source?.Dispose(); }
            finally { ReleaseRecording(); Rollback(meta); }
            throw;
        }
    }

    private void Rollback(MeetingMeta meta)
    {
        MeetingMeta? stored = null;
        try { stored = _host.Store.Read(meta.Id); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        if (stored is null || (stored.StartedAt == meta.StartedAt && stored.Title == meta.Title)) _host.Store.Delete(meta.Id);
    }

    private async Task ObserveTranscription(Task transcription, IHostMeetingSource source, CancellationToken cancellation)
    {
        try { await transcription; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _confirmedRoute != _host.ProcessingRoute()) { }
        catch
        {
            if (_ending || !ReferenceEquals(_source, source)) return;
            _transcriptionFailed = true;
            try { source.Stop(); }
            catch (Exception error) { _host.Log.Warn("Meeting capture stop failed: " + error.GetType().Name); }
            _timer.Stop();
            Screen(Ui.Heading(PROPOSED_Recording), Ui.Text(PROPOSED_TranscriptionFailed),
                SafeButton(PROPOSED_SaveAudioStop, () => _ = Work(() => StopRecording(false, true))),
                SafeButton(PROPOSED_Discard, ConfirmDiscard));
            Say(PROPOSED_TranscriptionFailed);
        }
    }

    private void ConfirmDiscard() => Confirm(PROPOSED_DeleteQuestion, PROPOSED_Discard,
        () => _ = Work(() => StopRecording(true)));

    private void RefuseRecording()
    {
        if (_recording is not { } id || _source is null) return;
        StopCapture();
        Exception? failure = null;
        try { _host.SaveConfirmation(id, new("refused", DateTimeOffset.UtcNow, AgreementKind.Live, ApprovedScriptVersion, APPROVED_RefusalNotes)); }
        catch (Exception error) { failure = error; }
        TakeNotes();
        if (failure is not null) Say(PROPOSED_Error);
    }

    private void ShowJoined()
    {
        if (_source is not { } source || _recording is not { } id) return;
        source.Pause();
        _paused = true; _awaitingArrival = true;
        UpdatePauseState();
        Say(PROPOSED_Paused);
        var answered = false;
        void Answer(string answer)
        {
            _host.SaveConfirmation(id, new(answer, DateTimeOffset.UtcNow, AgreementKind.Live, ApprovedScriptVersion, APPROVED_Joined));
            answered = true;
        }
        var dialog = new Window { Title = PROPOSED_Joined, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false };
        dialog.Closed += (_, _) =>
        {
            if (!answered) { try { Answer("unconfirmed"); } catch { Say(PROPOSED_Error); } }
        };
        dialog.Content = DialogContent(dialog, Ui.Text(APPROVED_Joined),
            SafeButton(APPROVED_Cancel, () => { Answer("unconfirmed"); dialog.Close(); }),
            SafeButton(PROPOSED_Discard, () => { Answer("discard-requested"); dialog.Close(); ConfirmDiscard(); }),
            SafeButton(APPROVED_Refused, () =>
            {
                StopCapture();
                var failed = false;
                try { Answer("refused"); } catch { failed = true; }
                dialog.Close();
                TakeNotes();
                if (failed) Say(PROPOSED_Error);
            }),
            SafeButton(APPROVED_Continue, () =>
            {
                try
                {
                    Answer("agreed");
                    _host.Store.AddMarker(id, new(source.Elapsed, MarkerKind.Joined));
                    if (_confirmedRoute != _host.ProcessingRoute()) { dialog.Close(); ShowRouteNotice(id, true, null); return; }
                    source.Resume();
                    _awaitingArrival = false; _paused = false;
                    UpdatePauseState();
                    Say(PROPOSED_Recording);
                    dialog.Close();
                }
                catch { Say(PROPOSED_Error); }
            }));
        _ = dialog.ShowDialog(this);
    }
    private void UpdatePauseState()
    {
        if (_pauseButton is not null)
        {
            _pauseButton.Content = _paused ? PROPOSED_Resume : PROPOSED_Pause;
            AutomationProperties.SetName(_pauseButton, _paused ? PROPOSED_Resume : PROPOSED_Pause);
        }
        if (_captureState is not null) _captureState.Text = _paused ? PROPOSED_Paused : PROPOSED_Recording;
    }
    private void Confirm(string message, string accept, Action action)
    {
        var dialog = new Window { Title = accept, Width = 500, SizeToContent = SizeToContent.Height, CanResize = false };
        dialog.Content = DialogContent(dialog, Ui.Text(message),
            Ui.Row(8, SafeButton(APPROVED_Cancel, dialog.Close), SafeButton(accept, () => { dialog.Close(); action(); })));
        _ = dialog.ShowDialog(this);
    }
    private void StopCapture()
    {
        _ending = true;
        _timer.Stop();
        _elapsed = null; _meter = null;
        _source?.Stop();
    }

    private async Task<bool> DrainTranscription()
    {
        if (_transcription is null) return !_transcriptionFailed;
        try { await _transcription.WaitAsync(DrainTimeout).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_routeChangePending) { }
        catch (Exception error)
        {
            _transcriptionFailed = true;
            _host.Log.Warn("Meeting transcription failed: " + error.GetType().Name);
            _recordingCancellation?.Cancel();
            // Retain ownership until cancellation and all persistence have finished.
            try { await _transcription.ConfigureAwait(false); } catch { }
        }
        return !_transcriptionFailed;
    }

    private void ReleaseRecording()
    {
        _source?.Dispose(); _source = null; _recording = null; _paused = false;
        _transcription = null; _ending = false; _awaitingArrival = false;
        _recordingCancellation?.Dispose(); _recordingCancellation = null;
        _discardRequested = false; _liveLines = null;
        _routeChangePending = false;
    }

    private void ShowSaveFailure()
    {
        Screen(Ui.Heading(PROPOSED_AudioSaveFailed), SafeButton(PROPOSED_Retry, () => _ = Work(() => StopRecording(false))),
            SafeButton(APPROVED_Refused, TakeNotes), SafeButton(PROPOSED_Discard, ConfirmDiscard));
        Say(PROPOSED_AudioSaveFailed);
    }

    private Task StopRecording(bool discard, bool acceptFailure = false)
    {
        _discardRequested |= discard;
        if (_activeStop is { IsCompleted: false }) return _activeStop;
        return _activeStop = StopRecordingCore(_discardRequested, acceptFailure);
    }

    private async Task StopRecordingCore(bool discard, bool acceptFailure)
    {
        if (_recording is not { } id || _source is null) return;
        try
        {
            StopCapture();
            var drained = await DrainTranscription();
            if (!drained && _exitPending && !discard && !acceptFailure)
            {
                ShowTranscriptionFailure();
                return;
            }
            if (discard) { _source.Dispose(); _host.Store.Delete(id); }
            else
            {
                SaveTracks(id, _source);
                _host.Finish(id, _source.Elapsed);
            }
        }
        catch { ShowSaveFailure(); return; }
        ReleaseRecording();
        if (_exitPending)
        {
            _exitPending = false;
            ShutdownReady?.Invoke();
            return;
        }
        if (discard) { ShowList(); return; }
        ShowMeeting(id);
        if (_transcriptionFailed) { Say(PROPOSED_TranscriptionFailed); return; }
        await Regenerate(id, _host.Settings().SummaryTemplate);
    }

    // AppShell disposal is synchronous. Drain without capturing the UI context;
    // transcript persistence runs on the worker and display callbacks are posted.
    internal bool Shutdown()
    {
        if (_shuttingDown) return true;
        if (_activeStop is { IsCompleted: false } || _activeImport is { IsCompleted: false })
        {
            if (!_exitPending)
            {
                _exitPending = true;
                _importCancellation?.Cancel();
                _ = FinishPendingShutdown();
            }
            return false;
        }
        if (_exitPending) { if (_transcriptionFailed) ShowTranscriptionFailure(); else PresentSaveFailure(); return false; }
        _shuttingDown = true;
        try
        {
            if (_recording is { } id && _source is { } source)
            {
                StopCapture();
                if (!DrainTranscription().GetAwaiter().GetResult())
                {
                    _shuttingDown = false;
                    _exitPending = true;
                    ShowTranscriptionFailure();
                    return false;
                }
                if (_discardRequested) { source.Dispose(); _host.Store.Delete(id); }
                else
                {
                    SaveTracks(id, source);
                    _host.Finish(id, source.Elapsed);
                }
            }
            ReleaseRecording();
            Close();
            return true;
        }
        catch
        {
            _shuttingDown = false;
            _exitPending = true;
            PresentSaveFailure();
            return false;
        }
    }

    private async Task FinishPendingShutdown()
    {
        var operation = _activeStop is { IsCompleted: false } ? _activeStop : _activeImport;
        try { if (operation is not null) await operation; }
        catch (OperationCanceledException) { }
        catch { _exitPending = false; Say(PROPOSED_Error); return; }
        if (!_exitPending) return;
        if (_recording is not null)
        {
            if (_transcriptionFailed) ShowTranscriptionFailure(); else PresentSaveFailure();
            return;
        }
        _exitPending = false;
        ShutdownReady?.Invoke();
    }

    private void ShowTranscriptionFailure()
    {
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        Screen(Ui.Heading(PROPOSED_Recording), Ui.Text(PROPOSED_TranscriptionFailed),
            SafeButton(PROPOSED_SaveAudioStop, () => _ = Work(() => StopRecording(false, true))),
            SafeButton(APPROVED_Refused, TakeNotes), SafeButton(PROPOSED_Discard, ConfirmDiscard));
        Say(PROPOSED_TranscriptionFailed);
    }

    private async Task ImportMeeting(string path, Agreement agreement, CancellationToken cancellation)
    {
        MeetingId id;
        using (var consent = _host.UseConfirmedRoute(agreement.Confirmation!.Route!))
            id = await _host.Import(path, agreement, cancellation);
        if (_exitPending) return;
        ShowMeeting(id);
        await Regenerate(id, _host.Settings().SummaryTemplate);
    }

    private string RouteScript(MeetingProcessingRoute route)
    {
        var summary = route.SummaryEngine switch
        {
            SummaryEngine.Ollama => APPROVED_Ollama + Environment.NewLine + string.Format(PROPOSED_SummaryHost, route.SummaryHost ?? PROPOSED_UnconfiguredProvider),
            SummaryEngine.OwnKey => string.Format(APPROVED_OwnKey, route.SummaryHost ?? PROPOSED_UnconfiguredProvider),
            _ => APPROVED_Local,
        };
        return (route.SpeechHost is { } speech ? string.Format(APPROVED_CloudSpeech, speech) : APPROVED_LocalSpeech)
            + Environment.NewLine + summary + Environment.NewLine + APPROVED_ConfirmationNote;
    }

    private async Task Regenerate(MeetingId id, string template)
    {
        var route = _host.ProcessingRoute();
        if (!_host.ReadConfirmations(id).Any(c => c.Route == route))
        {
            ShowRouteNotice(id, false, template);
            return;
        }
        await _host.Regenerate(id, template, _lifetime.Token, route);
        ShowMeeting(id);
    }

    private void ShowRouteNotice(MeetingId id, bool resume, string? template)
    {
        if (resume)
        {
            _routeChangePending = true;
            _recordingCancellation?.Cancel();
            _source?.Pause();
            _paused = true;
            UpdatePauseState();
        }
        if (_routeDialog is { IsVisible: true }) return;
        var route = _host.ProcessingRoute();
        var script = RouteScript(route);
        var dialog = new Window { Title = APPROVED_Before, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false };
        _routeDialog = dialog;
        var answered = false;
        var declined = false;
        dialog.Closed += (_, _) =>
        {
            if (resume && !answered) { declined = true; _ = Work(() => StopRecording(false)); }
        };
        dialog.Content = DialogContent(dialog, Ui.Text(script),
            SafeButton(APPROVED_Cancel, () =>
            {
                declined = true;
                answered = true;
                dialog.Close();
                if (resume) _ = Work(() => StopRecording(false));
            }),
            SafeButton(APPROVED_Continue, () => _ = Work(async () =>
            {
                if (route != _host.ProcessingRoute()) { answered = true; dialog.Close(); ShowRouteNotice(id, resume, template); return; }
                _host.SaveConfirmation(id, new("agreed", DateTimeOffset.UtcNow, _host.Store.Read(id).Agreement?.Kind ?? AgreementKind.Live,
                    ApprovedScriptVersion, script, Route: route));
                if (resume && _source is { } source)
                {
                    if (_transcription is not null) { try { await _transcription; } catch { } }
                    if (declined) { await StopRecording(false); return; }
                    if (route != _host.ProcessingRoute()) { answered = true; dialog.Close(); ShowRouteNotice(id, resume, template); return; }
                    _confirmedRoute = route;
                    _routeChangePending = false;
                    _recordingCancellation?.Dispose();
                    _recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    var cancellation = _recordingCancellation.Token;
                    _transcription = Task.Run(async () =>
                    {
                        using var consent = _host.UseConfirmedRoute(route);
                        await _host.Transcriber.RunAsync(id, source, segment => Dispatcher.UIThread.Post(() =>
                        {
                            if (_liveLines is not null) _liveLines.Text += FormatSegment(segment) + Environment.NewLine;
                        }), cancellation);
                    });
                    _ = ObserveTranscription(_transcription, source, cancellation);
                    if (!_awaitingArrival) { source.Resume(); _paused = false; UpdatePauseState(); }
                }
                answered = true;
                dialog.Close();
                if (!resume) await Regenerate(id, template!);
            })));
        _ = dialog.ShowDialog(this);
    }

    private void SaveTracks(MeetingId id, IHostMeetingSource source)
    {
        source.CompleteAudio();
    }

    private void PresentSaveFailure()
    {
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        ShowSaveFailure();
    }

    internal void ShowMeeting(MeetingId id)
    {
        var meta = _host.Store.Read(id);
        var summary = _host.Store.ReadSummary(id);
        var template = Named(new ComboBox { ItemsSource = PROPOSED_TemplateNames,
            SelectedIndex = Math.Max(0, Array.IndexOf(TemplateIds, summary?.Template ?? _host.Settings().SummaryTemplate)) }, PROPOSED_Template);
        var transcriptCard = Ui.Card(Ui.Stack(8, Ui.Heading(PROPOSED_Transcript), Ui.Text(string.Join(Environment.NewLine, _host.Store.ReadTranscript(id).Select(FormatSegment))),
                Ui.Text(string.Join(Environment.NewLine, meta.Markers.Select(marker => string.Format(PROPOSED_Marker, marker.At,
                    marker.Kind switch { MarkerKind.Moment => PROPOSED_Mark, MarkerKind.Joined => PROPOSED_Joined, MarkerKind.Paused => PROPOSED_Paused, _ => PROPOSED_Resume }))))));
        var summaryCard = Ui.Card(Ui.Stack(8, Ui.Heading(PROPOSED_Summary), template,
                SafeButton(PROPOSED_Regenerate, () => _ = Work(() => Regenerate(id, TemplateIds[template.SelectedIndex]))),
                Ui.Text(summary is null ? PROPOSED_NoSummary : MeetingsHost.Caption(summary.Engine), "caption"), Ui.Text(summary?.Markdown ?? "")));
        var record = meta.Agreement is { } agreement ? Ui.Text(string.Format(APPROVED_Record,
            agreement.Kind == AgreementKind.Live ? APPROVED_LiveKind : APPROVED_ImportKind, agreement.AcknowledgedAt.ToLocalTime(),
            agreement.How switch { "written" => APPROVED_Written, "platform-announced" => PROPOSED_Platform, _ => APPROVED_Spoken })) : Ui.Text(PROPOSED_NotesText);
        Screen(Ui.Row(8, SafeButton(PROPOSED_Meetings, ShowList)), Ui.Heading(meta.Title), Ui.Text(meta.AudioIncomplete ? PROPOSED_AudioRecovered : ""),
            Ui.Text(string.Format(PROPOSED_ListRow, meta.Title, meta.StartedAt.ToLocalTime(), meta.Duration, _host.Store.SizeOf(id))),
            Ui.Columns(2, 16, transcriptCard, Ui.Stack(12, summaryCard, Ui.Card(record, PROPOSED_Record))),
            Ui.Row(8, SafeButton(PROPOSED_Export, () => Confirm(APPROVED_ShareReminder, APPROVED_ShareContinue, () => _ = Work(async () =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = PROPOSED_Export, DefaultExtension = "md" });
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync();
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(_host.Export(id)); Say(PROPOSED_Saved);
            }))), SafeButton(PROPOSED_Copy, () => Confirm(APPROVED_ShareReminder, APPROVED_ShareContinue, () => { _host.Copy(_host.Export(id)); Say(PROPOSED_Copied); })),
                SafeButton(PROPOSED_Delete, () => Confirm(PROPOSED_DeleteQuestion, PROPOSED_Delete, () => { _host.Store.Delete(id); ShowList(); }))));
    }
    private static string FormatSegment(Segment segment) => string.Format(PROPOSED_Segment, segment.Start,
        segment.Channel switch { Channel.You => PROPOSED_YouChannel, Channel.Others => PROPOSED_OthersChannel, _ => PROPOSED_MixedChannel }, segment.Text);
}
