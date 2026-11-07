using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Dictum.Calibration;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Voice;
using Dictum.UI.Shell;
using Dictum.UI.Views.Meetings;
using static Dictum.Meetings.Voice.VoiceText;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// Settings > General > "Your voice" (design 1.6 C and D): make a voice print from the calibration recordings or a fresh recording,
/// the two uses as switches, and Delete my voice print. Every string is a PROPOSED row of .demo-preview/two-model-voiceprint.md (V1–V13),
/// or reused as it stands (V14: the speaker model's download row from Settings > Meetings). The print itself is read and written by
/// Dictum.Meetings.Voice; this card never sees a file's bytes.
/// </summary>
internal sealed class VoiceCard : UserControl
{
    private readonly AppShell _shell;
    private readonly Func<(string Key, JsonNode? Value)[], bool> _write;
    private readonly TextBlock _sentence = Ui.Text("");
    private readonly TextBlock _status = Ui.Text("");
    private readonly StackPanel _modelRow;
    private readonly StackPanel _enrolRow;
    private readonly Button _useCalibration;
    private readonly Button _recordNow;
    private readonly ToggleSwitch _useInMeetings;
    private readonly ToggleSwitch _yourVoiceOnly;
    private readonly Button _delete;
    private bool _refreshing;
    private bool _busy;

    public VoiceCard(AppShell shell, Func<(string Key, JsonNode? Value)[], bool> write)
    {
        _shell = shell;
        _write = write;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _modelRow = Ui.Stack(8, Ui.Text(SpeakerText.APPROVED_ModelsMissing),
            Ui.Row(8, MeetingsWindow.Button(SpeakerText.APPROVED_DownloadModels, () => MeetingsPage.DownloadSpeakerModel(_shell, Refresh))));
        _useCalibration = MeetingsWindow.Button(PROPOSED_UseCalibration, () => Guarded(() => Enrol(fromCalibration: true)));
        _recordNow = MeetingsWindow.Button(PROPOSED_RecordNow, () => Guarded(() => Enrol(fromCalibration: false)));
        _enrolRow = Ui.Row(8, _useCalibration, _recordNow);
        _useInMeetings = Ui.Switch(true, on => Save(VoiceEnrolment.UseInMeetingsKey, on));
        _yourVoiceOnly = Ui.Switch(false, on => Save(VoiceEnrolment.YourVoiceOnlyKey, on));
        _delete = MeetingsWindow.Button(PROPOSED_Delete, ConfirmDelete);
        Content = Ui.Card(Ui.Stack(10,
            _sentence,
            _modelRow,
            _enrolRow,
            Ui.Line(PROPOSED_UseInMeetings, _useInMeetings),
            Ui.Line(PROPOSED_YourVoiceOnly, _yourVoiceOnly, PROPOSED_YourVoiceOnlyNote),
            Ui.Row(8, _delete),
            _status), PROPOSED_CardTitle);
    }

    /// <summary>Redraws from the print on disk, the speaker model and settings.json. Never throws: a print that cannot be read is said.</summary>
    public void Refresh()
    {
        _refreshing = true;
        try
        {
            VoicePrint? print = null;
            var state = VoicePrintState.Absent;
            try { (print, state) = _shell.ReadVoicePrint(); }
            catch (Exception error) { Fail(error); }
            var model = SpeakerModels.Locate(_shell.Paths, _shell.Settings) is not null;
            _sentence.Text = state switch
            {
                VoicePrintState.Ready when print is not null => string.Format(CultureInfo.InvariantCulture, PROPOSED_CardPrint,
                    print.EnrolledAt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), Math.Round(print.Seconds).ToString(CultureInfo.InvariantCulture)),
                VoicePrintState.OtherModel => PROPOSED_OtherModel,
                _ => PROPOSED_CardNoPrint,
            };
            _modelRow.IsVisible = !model;
            _enrolRow.IsVisible = model;
            var calibration = CalibrationRecordings.For(_shell.Paths);
            _useCalibration.IsVisible = Enumerable.Range(1, calibration.Script.Count).Any(calibration.Has); // clip-01.wav … (design C.1)
            _useCalibration.IsEnabled = _recordNow.IsEnabled = !_busy;
            // The uses only mean something with a usable print: greyed otherwise, the card sentence above saying why (V2 or V13).
            var ready = state == VoicePrintState.Ready;
            foreach (var toggle in new[] { _useInMeetings, _yourVoiceOnly })
            {
                toggle.IsEnabled = ready && !_busy;
                AutomationProperties.SetHelpText(toggle, ready ? null : _sentence.Text);
            }
            Ui.SetQuietly(_useInMeetings, ready && _shell.MeetingsSettings.UseVoicePrint);
            Ui.SetQuietly(_yourVoiceOnly, ready && _shell.Settings.Audio.YourVoiceOnly);
            _delete.IsVisible = VoicePrintStore.Exists(VoicePrintStore.PathFor(_shell.Paths));
            _delete.IsEnabled = !_busy;
        }
        finally { _refreshing = false; }
    }

    private void Save(string key, bool on)
    {
        if (_refreshing) return;
        try { _write([(key, JsonValue.Create(on))]); }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>
    /// Makes a print: from the calibration recordings, or from a new recording on the calibration screen into a temporary folder that
    /// is deleted when the attempt ends (the window closes), whatever the outcome. Building runs off the UI thread.
    /// </summary>
    private void Enrol(bool fromCalibration)
    {
        if (_busy) return;
        if (fromCalibration)
        {
            Build(() =>
            {
                using var embedder = _shell.CreateVoiceEmbedder();
                return VoiceEnrolment.FromCalibration(_shell.Paths, embedder, _shell.VoiceKeyring, DateTimeOffset.Now);
            });
            return;
        }
        var folder = VoiceEnrolment.NewTemporaryFolder(_shell.Paths);
        CalibrationWindow window;
        try { window = new CalibrationWindow(_shell, folder); }
        catch
        {
            VoiceEnrolment.DeleteLeftovers(_shell.Paths);
            throw;
        }
        var script = CalibrationRecordings.For(_shell.Paths).Script;
        window.Closed += (_, _) => Build(() =>
        {
            try
            {
                using var embedder = _shell.CreateVoiceEmbedder();
                return VoiceEnrolment.FromTemporaryFolder(_shell.Paths, folder, script, embedder, _shell.VoiceKeyring, DateTimeOffset.Now);
            }
            finally { VoiceEnrolment.DeleteLeftovers(_shell.Paths); } // also when the model fails its check before the folder is read
        });
        SetBusy(true);
        window.Show();
        window.Activate();
    }

    private void Build(Func<EnrolmentResult> build)
    {
        SetBusy(true);
        _ = Task.Run(() =>
        {
            try
            {
                var result = build();
                Dispatcher.UIThread.Post(() => Finished(result, null));
            }
            catch (Exception error) { Dispatcher.UIThread.Post(() => Finished(null, error)); }
        });
    }

    private void Finished(EnrolmentResult? result, Exception? error)
    {
        SetBusy(false);
        if (error is not null) { Fail(error); return; }
        // A new print turns use 1 on (design C.4: on once a print exists); "your voice only" stays as the user left it.
        if (result!.Print is not null) Save(VoiceEnrolment.UseInMeetingsKey, true);
        _status.Text = result.Refusal ?? "";
        Refresh();
    }

    private void ConfirmDelete()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = PROPOSED_Delete, Width = 480, SizeToContent = SizeToContent.Height };
        dialog.Content = MeetingsWindow.DialogContent(dialog, Ui.Text(PROPOSED_DeleteConfirm),
            MeetingsWindow.Button(PROPOSED_Delete, () =>
            {
                dialog.Close();
                Delete();
            }),
            MeetingsWindow.Button(MeetingsText.APPROVED_Cancel, dialog.Close));
        _ = dialog.ShowDialog(owner);
    }

    /// <summary>Deletes the print and turns both uses off (design C.3); the card returns to its un-enrolled state.</summary>
    internal void Delete()
    {
        try
        {
            if (!VoiceEnrolment.Delete(_shell.Paths, keys => _write([.. keys.Select(k => (k.Key, (JsonNode?)JsonValue.Create(k.Value)))])))
                _status.Text = Dictum.App.ModelCoordinator.WriteFailed;
            else _status.Text = "";
        }
        catch (Exception error) { Fail(error); }
        Refresh();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Refresh();
    }

    private void Fail(Exception error)
    {
        _shell.Log.Error($"Your voice card: {error.GetType().Name}");
        _status.Text = $"That did not work: {error.Message}";
    }

    private void Guarded(Action action)
    {
        try { action(); }
        catch (Exception error) { SetBusy(false); Fail(error); }
    }
}
