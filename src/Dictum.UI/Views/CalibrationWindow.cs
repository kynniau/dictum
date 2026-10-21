using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Calibration;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.UI.Shell;

namespace Dictum.UI.Views;

/// <summary>
/// Calibrate my voice (SPEC §16.5, docs/design/Calibrate.dc.html). One sentence at a time: Record (or hold the dictation
/// hotkey while this window is active), Stop, Skip. Then the test measures every installed speech model with greedy and
/// beam-search decoding, showing each result as it finishes, with the error rate, the raw error count and seconds per
/// clip; the recommendation is the lowest error rate under the latency ceiling, and within one point of it the faster one
/// wins. Missed words become unticked suggestions; Save adds the ticked ones. Nothing is uploaded; recordings stay local.
/// </summary>
internal sealed partial class CalibrationWindow : Window
{
    public const string Framing = "Calibration measures which installed model hears you best and suggests words for your dictionary. It does not train or change the model.";

    /// <summary>
    /// Where the recordings are, without the path (PROPOSED, 1.2 polish, 22/10/2026). The folder is Dictum's own
    /// (AppPaths.CalibrationDir), not one the user chose, and Settings > Storage lists it under "Models, logs, calibration recordings".
    /// </summary>
    public const string RecordingsNote = "Nothing is uploaded: the recordings stay in Dictum's folder on this PC (Settings > Storage lists it), so the test can be re-run when you add a model.";

    /// <summary>The button beside <see cref="RecordingsNote"/> (PROPOSED, 1.2 polish).</summary>
    public const string OpenRecordingsLabel = "Open the recordings folder";

    /// <summary>Said when the recordings folder could not be opened; the reason, with the path, goes to the log (PROPOSED, lens on 4abba54).</summary>
    public const string OpenRecordingsFailed = "The recordings folder could not be opened.";

    private readonly AppShell _shell;
    private readonly CalibrationHost _host;
    private readonly CalibrationRecordings _recordings;
    private readonly IAudioCapture _capture;
    private readonly CalibrationSession _session = new();
    private readonly TextBlock _counter = Ui.Text("", "caption");
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBlock _sentence = new() { FontSize = 22, LineHeight = 30, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _takeStatus = Ui.Text(""); // body size, under Record: what happened to the take, and why one failed (1.2 polish)
    private readonly Button _record;
    private readonly Button _skip;
    private readonly TextBlock _holdHint = Ui.Text("", "caption");
    private readonly TextBlock _progress = Ui.Text("", "caption");
    private readonly Button _run;
    private readonly Button _cancel;
    private readonly Button _delete;
    private readonly DispatcherTimer _takeLimit;
    private IDisposable? _hotkey;
    private CancellationTokenSource? _runCts;
    private int _current = 1;
    private bool _noMicrophone; // the system lists none (CheckMicrophone)
    private bool _listFailureLogged;

    public CalibrationWindow(AppShell shell)
    {
        _shell = shell;
        _host = shell.CreateCalibrationHost();
        _recordings = CalibrationRecordings.For(_host.Paths, _host.Log.Warn);
        _capture = shell.Platform.CreateCapture(_host.Log);
        Title = "Dictum: calibrate my voice";
        Width = 820;
        Height = 720;
        MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _record = Ui.Button("Record", "danger", () => Toggle(), TakeFailed);
        _record.MinHeight = 44;
        _skip = Ui.Button("Skip", "secondary", () => MoveNext(), TakeFailed);
        _skip.MinHeight = 44;
        _run = Ui.Button("Run test", "primary", () => _ = RunAsync(_host.InstalledModels()), Failed);
        _cancel = Ui.Button("Cancel test", "secondary", () => _runCts?.Cancel(), Failed);
        _delete = Ui.Button("Delete recordings", "secondary", DeleteRecordings, Failed);
        AutomationProperties.SetLiveSetting(_progress, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_takeStatus, AutomationLiveSetting.Polite);

        // No folder path on screen (1.2 polish, 22/10/2026): a path names the Windows user, and a screenshot of this window
        // would carry it. The button opens the folder instead; Settings > Storage lists it with the rest.
        var openRecordings = Ui.Button(OpenRecordingsLabel, "secondary", OpenRecordings, Failed);
        var framing = Ui.Card(Ui.Stack(10, Ui.Text($"{Framing} {RecordingsNote}"), Ui.Row(8, openRecordings)));
        var counterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_dots, 1);
        counterRow.Children.AddRange([_counter, _dots]);
        var sentenceCard = Ui.Card(Ui.Stack(10, counterRow, _sentence, Ui.Row(10, _record, _skip, _holdHint), _takeStatus));
        var results = Ui.Card(Ui.Stack(8, ResultsGrid, Ui.Text(
            $"Recommended: lowest errors within {CalibrationRanking.CeilingSecondsPer10s} s per 10 s of speech on this PC; within one point, the faster one. " +
            "A recording shorter than 10 s counts as 10 s, because whisper's time per recording is mostly fixed.", "hint"),
            Ui.Row(8, _run, _cancel), _progress, Ui.Overline("Not installed"), MissingList), "Results so far");
        var top = Ui.Columns(2, 16, sentenceCard, results);
        var missed = Ui.Card(SuggestionList, "Words it keeps missing");

        var save = Ui.Button("Save", "primary", AddTicked, Failed, name: "Save the ticked suggestions");
        var close = Ui.Button("Close", "secondary", Close);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        Grid.SetColumn(close, 2);
        Grid.SetColumn(save, 3);
        footer.Children.AddRange([_delete, close, save]);
        var footerBar = new Border { Padding = new Thickness(24, 10), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        footerBar.Classes.Add("headerbar");
        var header = new Border { Padding = new Thickness(16, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(10, new MarkIcon(20), new TextBlock { Text = "Calibrate my voice", FontSize = 13, FontWeight = FontWeight.SemiBold }) };
        header.Classes.Add("headerbar");
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footerBar, Dock.Bottom);
        Content = new DockPanel
        {
            Children = { header, footerBar, new ScrollViewer { Content = new Border { Padding = new Thickness(24, 20), Child = Ui.Stack(16, framing, top, missed) } } },
        };

        _takeLimit = new DispatcherTimer { Interval = CalibrationSession.TakeLimit };
        _takeLimit.Tick += (_, _) => AbandonTake($"Longer than {CalibrationSession.TakeLimit.TotalSeconds:0} s, so it was not kept: record it again.");
        _capture.Failed += _ => Dispatcher.UIThread.Post(() => AbandonTake("The microphone failed: record it again."));
        Activated += (_, _) =>
        {
            _hotkey ??= shell.RedirectHotkey(OnHotkey); // the hotkey records here only while this window is active
            CheckMicrophone(); // one plugged in while the person was elsewhere turns Record back on
        };
        Deactivated += (_, _) =>
        {
            _hotkey?.Dispose();
            _hotkey = null;
        };
        Closed += (_, _) =>
        {
            _hotkey?.Dispose();
            _takeLimit.Stop();
            _runCts?.Cancel();
            if (_session.Recording != 0) _capture.Stop();
            (_capture as IDisposable)?.Dispose();
        };
        _current = FirstUnrecorded(1);
        CheckMicrophone();
        RefreshTake();
        RenderResults();
        RenderMissing();
        RenderSuggestions();
    }

    private int Count => _recordings.Script.Count;

    /// <summary>The hotkey while this window is active: hold to record the sentence shown, Esc drops the take.</summary>
    private void OnHotkey(HookEvent e)
    {
        try
        {
            switch (e.Signal)
            {
                case HotkeySignal.Down when _session.Recording == 0:
                    Toggle();
                    break;
                case HotkeySignal.Up when _session.Recording != 0:
                    Toggle();
                    break;
                case HotkeySignal.Escape when _session.Recording != 0:
                    AbandonTake("Cancelled: record it again when ready.");
                    break;
            }
        }
        catch (Exception ex)
        {
            TakeFailed(ex);
        }
    }

    private void Toggle()
    {
        if (Count == 0 || !_session.CanRecord(_current)) return;
        var settings = _host.Settings();
        if (_session.Recording == 0)
        {
            if (!CheckMicrophone()) return; // the hotkey, which a disabled Record does not stop: the sentence is already shown
            _capture.Start(settings.Audio.Device, Stopwatch.GetTimestamp());
            _session.StartTake(_current);
            _takeLimit.Start();
            _takeStatus.Text = "Recording: read the sentence at your normal pace, then press Stop.";
            RefreshTake();
            return;
        }
        _takeLimit.Stop();
        var audio = _capture.Stop();
        var index = _session.Recording;
        _session.EndTake();
        if (CalibrationSession.RejectTake(audio, settings.SilenceThreshold) is { } reject)
        {
            _takeStatus.Text = $"Not kept ({reject}): record it again.";
            RefreshTake();
            return;
        }
        _recordings.Save(index, audio);
        _session.RecordingsChanged(); // results measured on the earlier take no longer apply
        _takeStatus.Text = $"Sentence {index} recorded.";
        RenderResults();
        RenderSuggestions();
        MoveNext();
    }

    /// <summary>Closes the microphone and discards the take in progress (too long, the device failed, or Esc).</summary>
    private void AbandonTake(string why)
    {
        _takeLimit.Stop();
        if (_session.Recording == 0) return;
        _capture.Stop();
        _session.EndTake();
        _takeStatus.Text = why;
        RefreshTake();
    }

    private void MoveNext()
    {
        if (_session.Recording != 0 || Count == 0) return;
        _current = FirstUnrecorded(_current % Count + 1);
        RefreshTake();
    }

    /// <summary>The first sentence from <paramref name="start"/> onwards (wrapping) not yet recorded, else the one after the current.</summary>
    private int FirstUnrecorded(int start)
    {
        if (Count == 0) return 1;
        var stale = _recordings.StaleTakes();
        for (var i = 0; i < Count; i++)
        {
            var index = (start - 1 + i) % Count + 1;
            if (!_recordings.Has(index) || stale.Contains(index)) return index;
        }
        return Math.Clamp(start, 1, Count);
    }

    private void RefreshTake()
    {
        var recording = _session.Recording != 0;
        if (Count == 0)
        {
            _sentence.Text = $"The sentences file {_host.Paths.CalibrationScriptFile} is missing, so there is nothing to record. Reinstall Dictum to restore it.";
            _record.IsEnabled = _skip.IsEnabled = false;
        }
        else
        {
            _counter.Text = $"Sentence {_current} of {Count}";
            _sentence.Text = $"“{_recordings.Script[_current - 1]}”";
            _record.Content = recording ? "Stop" : _recordings.Has(_current) ? "Record again" : "Record";
            AutomationProperties.SetName(_record, $"{(recording ? "Stop recording" : "Record")} sentence {_current}");
            _record.IsEnabled = _session.CanRecord(_current) && !(_noMicrophone && !recording); // Stop always works
            _skip.IsEnabled = !recording;
        }
        _holdHint.Text = HotkeyParser.TryParse(_host.Settings().Hotkey, out var spec, out _) ? $"or hold {HotkeyNames.Display(spec)}" : "";
        var stale = _recordings.StaleTakes();
        _dots.Children.Clear();
        for (var i = 1; i <= Count; i++)
        {
            var dot = new Rectangle { Width = 14, Height = 4, RadiusX = 2, RadiusY = 2 };
            var done = _recordings.Has(i) && !stale.Contains(i);
            dot[!Shape.FillProperty] = new DynamicResourceExtension(i == _current ? "AccentBrush" : done ? "DoneBrush" : "MeterOffBrush");
            _dots.Children.Add(dot);
        }
        AutomationProperties.SetName(_dots, $"{Enumerable.Range(1, Count).Count(i => _recordings.Has(i) && !stale.Contains(i))} of {Count} sentences recorded");
        _run.IsEnabled = _session.CanRun;
        _cancel.IsEnabled = _session.Running;
        _delete.IsEnabled = _session.CanDelete;
    }

    private void DeleteRecordings()
    {
        if (!_session.CanDelete) return;
        var deleted = _recordings.DeleteAll();
        _session.RecordingsChanged();
        _current = 1;
        RenderResults();
        RenderSuggestions();
        RefreshTake();
        _progress.Text = $"Deleted {deleted} recording(s).";
    }

    /// <summary>A failure of Run test, Delete recordings, Save or the folder button: said under Run test, where the results are.</summary>
    private void Failed(Exception ex)
    {
        Recover(ex);
        _progress.Text = FailureText(ex);
    }

    /// <summary>
    /// A take that failed to start, stop or save (Record, Skip or the hotkey): said under Record in the body text size, where
    /// the person is looking, not in the results' small print (1.2 polish, 22/10/2026).
    /// </summary>
    private void TakeFailed(Exception ex)
    {
        Recover(ex);
        _takeStatus.Text = FailureText(ex);
        // The capture found no microphone: look again, so a list that is now empty disables Record. One that still names a
        // device leaves Record on (the sentence says what to check), as the list is what says one is there.
        if (ex is NoMicrophoneException) CheckMicrophone();
    }

    /// <summary>
    /// Looks for a microphone (on opening, on activation, and before a take the hotkey starts). With none listed, Record is
    /// disabled and the one no-microphone sentence (<see cref="NoSoundText.NoMicrophoneBalloon"/>) stands under it, the
    /// sentence to read still shown; when one appears, Record works again and the sentence goes (1.2 polish round 2: Record
    /// stayed active on a PC with none). True when there is one.
    /// </summary>
    internal bool CheckMicrophone()
    {
        var none = MicrophoneChoices.List(_shell.Platform, why =>
        {
            if (!_listFailureLogged) _host.Log.Warn($"Calibration window: {why}");
            _listFailureLogged = true;
        }).Count == 0;
        if (none) _takeStatus.Text = NoSoundText.NoMicrophoneBalloon;
        else if (_noMicrophone && _takeStatus.Text == NoSoundText.NoMicrophoneBalloon) _takeStatus.Text = "";
        if (none != _noMicrophone)
        {
            _noMicrophone = none;
            RefreshTake();
        }
        return !none;
    }

    private void Recover(Exception ex)
    {
        _host.Log.Error($"Calibration window: {ex.GetType().Name}: {ex.Message}");
        if (_session.Recording != 0) AbandonTake("Not saved: record it again.");
        else RefreshTake(); // a take that failed to start or to save leaves the controls as they were
    }

    /// <summary>No microphone: the one no-microphone sentence on its own (<see cref="NoSoundText.NoMicrophoneBalloon"/>); anything else: "That did not work: …".</summary>
    internal static string FailureText(Exception ex) => ex is NoMicrophoneException ? NoSoundText.NoMicrophoneBalloon : $"That did not work: {ex.Message}";

    /// <summary>
    /// Opens the recordings folder in the system's file manager (OpenPath makes it first, so it opens before the first recording).
    /// A failure is logged with OpenPath's reason, which names the path, and said on screen without it (lens on 4abba54: the
    /// window shows no path, so the Windows account name stays off screen in a screenshot).
    /// </summary>
    private void OpenRecordings()
    {
        if (_shell.OpenPath(_shell.RealPath(_recordings.Directory), folder: true) is not { } why) return;
        _host.Log.Warn($"Calibration window: the recordings folder could not be opened: {why}");
        _progress.Text = OpenRecordingsFailed;
    }

    private void WriteGrammar(bool on)
    {
        try
        {
            _host.Write([("llm.grammar_assist", JsonValue.Create(on))]);
        }
        catch (Exception ex)
        {
            _progress.Text = $"Grammar assist could not be saved: {ex.Message}";
        }
    }
}
