using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Summaries;
using Dictum.Settings;
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
    private readonly ComboBox _model = MeetingsWindow.Named(new ComboBox(), PROPOSED_SummaryModel);
    private readonly StackPanel _typesList = Ui.Stack(8);
    private IReadOnlyList<SummaryTemplate> _types = [];
    private string[] _modelFiles = [];
    private readonly StackPanel _warning;
    private readonly TextBlock _speakerModels = Ui.Text("");
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
        _template.SelectionChanged += (_, _) => { if (!Refreshing && _template.SelectedIndex >= 0 && _template.SelectedIndex < _types.Count) Write(("meetings.summary_type", Json(_types[_template.SelectedIndex].Id))); };
        _model.SelectionChanged += (_, _) => { if (!Refreshing && _model.SelectedIndex >= 0) Write(("meetings.summary_model", Json(_model.SelectedIndex == 0 ? null : _modelFiles[_model.SelectedIndex - 1]))); };
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
                Ui.Text(APPROVED_EngineNote), Ui.Text(PROPOSED_ConfigureSummary, "caption"), Ui.Line(PROPOSED_SummaryModel, _model),
                MeetingsWindow.Button(PROPOSED_OpenModels, () => shell.OpenSettings("Models")), Ui.Line(PROPOSED_Template, _template))),
            Ui.Card(Ui.Stack(8, Ui.Heading(SpeakerText.APPROVED_SettingsRow), Ui.Text(SpeakerText.APPROVED_SettingsNote, "caption"), _speakerModels,
                MeetingsWindow.Button(SpeakerText.APPROVED_DownloadModels, DownloadSpeakerModels))),
            Ui.Card(Ui.Stack(8, Ui.Heading(PROPOSED_SummaryTypes), Ui.Text(PROPOSED_TypeFileCaption, "caption"), _typesList,
                MeetingsWindow.Button(PROPOSED_NewType, () => EditType(null)))),
            MeetingsWindow.Button(PROPOSED_Existing, () => shell.OpenMeetings()));
    }
    protected override void Redraw()
    {
        var settings = _host.Settings();
        Ui.SetQuietly(_enabled, settings.Enabled);
        _folder.Text = settings.Folder ?? Path.Combine(_host.Paths.LocalDir, "meetings");
        _retention.SelectedIndex = (int)settings.KeepAudio;
        _engine.SelectedIndex = (int)settings.SummaryEngine;
        _types = SummaryTemplates.Load(_host.Paths.ConfigDir);
        _template.ItemsSource = _types.Select(t => t.Name).ToArray();
        _template.SelectedIndex = Math.Max(0, _types.ToList().FindIndex(t => t.Id == settings.SummaryTemplate));
        var installed = CleanupCatalog.Entries.Where(Shell.Models.IsCleanupDownloaded).ToArray();
        _modelFiles = installed.Select(m => m.FileName).ToArray();
        if (settings.SummaryModel is { } selected && !_modelFiles.Contains(selected)) _modelFiles = [.. _modelFiles, selected];
        _model.ItemsSource = new[] { PROPOSED_DefaultSummaryModel }.Concat(_modelFiles.Select(file =>
            installed.FirstOrDefault(m => m.FileName == file)?.Name ?? file + " · " + PROPOSED_ModelMissing)).ToArray();
        _model.SelectedIndex = settings.SummaryModel is null ? 0 : Array.IndexOf(_modelFiles, settings.SummaryModel) + 1;
        _model.IsEnabled = settings.SummaryEngine == SummaryEngine.Local;
        _typesList.Children.Clear();
        foreach (var type in _types)
        {
            if (type.IsBuiltin) _typesList.Children.Add(Ui.Text(string.Format(PROPOSED_BuiltinType, type.Name)));
            else _typesList.Children.Add(Ui.Stack(6, Ui.Text(type.Name),
                MeetingsWindow.Button(PROPOSED_EditType, () => EditType(type)),
                MeetingsWindow.Button(PROPOSED_DeleteType, () => DeleteType(type))));
        }
        _speakerModels.Text = _host.SpeakerModelsReady() ? SpeakerText.APPROVED_ModelsReady : SpeakerText.APPROVED_ModelsNotReady;
        _ = RefreshWarning(++_warningVersion, _folder.Text);
    }
    /// <summary>
    /// Downloads the speaker model, if it is missing, through the one model-download path (1.6 B): the usual question (name, size,
    /// huggingface.co), refused under offline_only, the file held to its pinned size and SHA-256.
    /// </summary>
    private void DownloadSpeakerModels()
    {
        if (SpeakerModels.Locate(Shell.Paths, Shell.Settings) is not null) { Refresh(); return; }
        var target = Path.Combine(Shell.Paths.ModelsDir(Shell.Settings), SpeakerModels.Embedding.FileName);
        Shell.Models.ApplyWithDownloads(SpeakerText.APPROVED_DownloadNote, [(SpeakerModels.Embedding, target)], [],
            finished: _ => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh));
    }
    private void EditType(SummaryTemplate? type)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = type is null ? PROPOSED_NewType : PROPOSED_EditType, Width = 520, SizeToContent = SizeToContent.Height };
        var name = MeetingsWindow.Named(new TextBox { Text = type?.Name ?? "" }, PROPOSED_TypeName);
        var instructions = MeetingsWindow.Named(new TextBox { Text = type?.Instructions ?? "", AcceptsReturn = true, MinHeight = 160, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, PROPOSED_TypeInstructions);
        var error = Ui.Text("");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        dialog.Content = MeetingsWindow.DialogContent(dialog, Ui.Text(PROPOSED_TypeName), name, Ui.Text(PROPOSED_TypeInstructions), instructions, error,
            MeetingsWindow.Button(PROPOSED_SaveType, () =>
            {
                try
                {
                    SummaryTemplates.Save(_host.Paths.ConfigDir, new(type?.Id ?? "custom-" + Guid.NewGuid().ToString("N"), name.Text ?? "", type?.Markdown ?? "")
                    { Instructions = instructions.Text ?? "", IsBuiltin = false });
                    Shell.Reload(userRequested: false);
                    dialog.Close(); Refresh(); Say(PROPOSED_Saved);
                }
                catch { error.Text = PROPOSED_Error; }
            }), MeetingsWindow.Button(APPROVED_Cancel, () => dialog.Close()));
        dialog.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => name.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        _ = dialog.ShowDialog(owner);
    }
    private void DeleteType(SummaryTemplate type)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = PROPOSED_DeleteType, Width = 480, SizeToContent = SizeToContent.Height };
        dialog.Content = MeetingsWindow.DialogContent(dialog, Ui.Text(PROPOSED_DeleteTypeQuestion),
            MeetingsWindow.Button(PROPOSED_DeleteType, () =>
            {
                try
                {
                    SummaryTemplates.Delete(_host.Paths.ConfigDir, type.Id);
                    Shell.Reload(userRequested: false);
                    if (_host.Settings().SummaryTemplate == type.Id) Write(("meetings.summary_type", Json("general")));
                    dialog.Close(); Refresh(); Say(PROPOSED_Saved);
                }
                catch { Say(PROPOSED_Error); }
            }), MeetingsWindow.Button(APPROVED_Cancel, () => dialog.Close()));
        _ = dialog.ShowDialog(owner);
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
