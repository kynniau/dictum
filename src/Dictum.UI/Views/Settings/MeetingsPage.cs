using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dictum.Meetings;
using Dictum.UI.Shell;
using Dictum.UI.Views.Meetings;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.UI.Views.Settings;

internal sealed class MeetingsPage : SettingsPage
{
    private readonly MeetingsHost _host;
    private readonly ToggleSwitch _enabled;
    private readonly TextBlock _folder = Ui.Text("");
    private readonly ComboBox _retention = MeetingsWindow.Named(new ComboBox { ItemsSource = new[] { PROPOSED_Forever, PROPOSED_Days30, PROPOSED_Days90 } }, PROPOSED_Retention);
    private readonly ComboBox _engine = MeetingsWindow.Named(new ComboBox { ItemsSource = new[] { APPROVED_LocalEngine, APPROVED_OllamaEngine, APPROVED_OwnKeyEngine } }, PROPOSED_Engine);
    private readonly ComboBox _template = MeetingsWindow.Named(new ComboBox { ItemsSource = PROPOSED_TemplateNames }, PROPOSED_Template);
    private readonly StackPanel _warning;
    private int _warningVersion;

    public MeetingsPage(AppShell shell, MeetingsHost? host = null) : base(shell, PROPOSED_Meetings)
    {
        _host = host ?? shell.CreateMeetingsHost();
        _enabled = Ui.Switch(false, on =>
        {
            if (Refreshing) return;
            if (!on) { Write(("meetings.enabled", Json(false))); return; }
            var dialog = new Window { Title = PROPOSED_Enable, Width = 500, SizeToContent = SizeToContent.Height };
            dialog.Closed += (_, _) => Refresh();
            dialog.Content = MeetingsWindow.DialogContent(dialog, Ui.Text(APPROVED_Enable), Ui.Text(APPROVED_Disclaimer), MeetingsWindow.Button(APPROVED_Cancel, () => { dialog.Close(); Refresh(); }),
                MeetingsWindow.Button(APPROVED_EnableConfirm, () => { Write(("meetings.enabled", Json(true))); dialog.Close(); Refresh(); }));
            if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner);
        });
        _retention.SelectionChanged += (_, _) => { if (_retention.SelectedIndex >= 0) Write(("meetings.keep_audio", Json(((KeepAudio)_retention.SelectedIndex).ToString()))); };
        _engine.SelectionChanged += (_, _) => { if (_engine.SelectedIndex >= 0) Write(("meetings.summary_engine", Json(((SummaryEngine)_engine.SelectedIndex).ToString()))); };
        _template.SelectionChanged += (_, _) => { if (_template.SelectedIndex >= 0) Write(("meetings.summary_template", Json(TemplateIds[_template.SelectedIndex]))); };
        _warning = Ui.Stack(8, Ui.Text(APPROVED_DriveWarning), MeetingsWindow.Button(APPROVED_Dismiss, () =>
        {
            try { _host.DismissWarning(); _warning!.IsVisible = false; }
            catch { Say(PROPOSED_Error); }
        }));
        AutomationProperties.SetLiveSetting(_warning, AutomationLiveSetting.Polite);
        _warning.IsVisible = false;
        Compose(Ui.Card(Ui.Stack(8, Ui.Line(PROPOSED_Enable, _enabled), Ui.Text(APPROVED_Enable), Ui.Text(APPROVED_Protection))),
            Ui.Card(Ui.Stack(8, Ui.Text(PROPOSED_Folder), _folder, Ui.Text(PROPOSED_FolderNote, "caption"), MeetingsWindow.Button(PROPOSED_Change, () => _ = ChangeFolder()),
                Ui.Line(PROPOSED_Retention, _retention))),
            _warning, Ui.Card(Ui.Stack(8, Ui.Line(PROPOSED_Engine, _engine),
                Ui.Text(APPROVED_EngineNote), Ui.Text(PROPOSED_ConfigureSummary, "caption"), Ui.Line(PROPOSED_Template, _template))),
            MeetingsWindow.Button(PROPOSED_Existing, () => shell.OpenMeetings()));
    }
    protected override void Redraw()
    {
        var settings = _host.Settings();
        Ui.SetQuietly(_enabled, settings.Enabled);
        _folder.Text = settings.Folder ?? Path.Combine(_host.Paths.LocalDir, "meetings");
        _retention.SelectedIndex = (int)settings.KeepAudio;
        _engine.SelectedIndex = (int)settings.SummaryEngine;
        _template.SelectedIndex = Math.Max(0, Array.IndexOf(TemplateIds, settings.SummaryTemplate));
        _ = RefreshWarning(++_warningVersion, _folder.Text);
    }
    internal async Task RefreshWarning(int version, string folder)
    {
        _warning.IsVisible = false;
        try
        {
            if (_host.WarningDismissed()) return;
            var encrypted = await _host.DriveEncrypted(folder);
            if (version == _warningVersion) _warning.IsVisible = encrypted != true && !_host.WarningDismissed();
        }
        catch { if (version == _warningVersion) _warning.IsVisible = !_host.WarningDismissed(); }
    }
    private async Task ChangeFolder()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = PROPOSED_Folder, AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder) Write(("meetings.folder", Json(folder)));
    }
}
