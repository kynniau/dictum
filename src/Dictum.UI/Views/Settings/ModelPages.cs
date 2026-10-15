using Avalonia.Controls;
using Avalonia.Layout;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>Settings → Models: the speech and clean-up catalogues with their state, a file chooser for each, decoding, and the language.</summary>
internal sealed class ModelsPage : SettingsPage
{
    private static readonly IReadOnlyList<(string Code, string Label)> Languages = WhisperLanguages.PickerItems();

    private readonly SettingsWindow _window;
    private readonly StackPanel _speech = new() { Spacing = 8 };
    private readonly StackPanel _cleanup = new() { Spacing = 8 };
    private readonly ComboBox _decoding = new() { ItemsSource = new[] { "Greedy (fastest)", "Beam search, 5 beams (more accurate, slower)" }, Width = 320 };
    private readonly ComboBox _language = new() { ItemsSource = Languages.Select(l => l.Label).ToList(), Width = 320 };
    private readonly ToggleSwitch _translate;
    private readonly TextBlock _languageNote = Ui.Text("", "caption");
    private readonly TextBlock _downloadText = Ui.Text("", "caption");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 4 };
    private readonly StackPanel _download;
    private string _drawn = "";

    public ModelsPage(AppShell shell, SettingsWindow window) : base(shell, "Models")
    {
        _window = window;
        _decoding.SelectionChanged += (_, _) =>
        {
            if (_decoding.SelectedIndex >= 0) Write(("whisper.beam_size", Json(_decoding.SelectedIndex == 0 ? 0 : 5)));
        };
        _language.SelectionChanged += (_, _) =>
        {
            if (Refreshing || _language.SelectedIndex < 0) return;
            var code = Languages[_language.SelectedIndex].Code;
            var w = Shell.Settings.Whisper;
            if (code == w.Language) return;
            // Judged against the model in use (a --model override included), not only whisper.model.
            if (WhisperLanguages.Problem(Shell.Models.ModelPath, code, w.Translate) is { } problem)
            {
                Say(problem);
                Refresh();
                return;
            }
            Write(("whisper.language", Json(code)));
            if (Shell.Settings.Whisper.Language != code) return; // not written (a balloon said why); Redraw puts the picker back
            Say(code == WhisperLanguages.English
                ? "Dictum listens for English again."
                : $"Dictum listens for {WhisperLanguages.Name(code).ToLowerInvariant()}{(code == WhisperLanguages.Auto ? "" : $" ({code})")}{(w.Translate ? " and writes English" : "")}. The decoder switches now; the model stays loaded.");
        };
        _translate = Ui.Switch(false, on =>
        {
            var w = Shell.Settings.Whisper;
            if (on && WhisperLanguages.Problem(Shell.Models.ModelPath, w.Language, translate: true) is { } problem)
            {
                Say(problem);
                Refresh();
                return;
            }
            Write(("whisper.translate", Json(on)));
            if (Shell.Settings.Whisper.Translate != on) return; // not written (a balloon said why)
            Say(on ? "Dictum writes English whatever language you speak." : "Dictum writes what you say in the language you spoke.");
        });
        _download = Ui.Stack(4, _downloadText, _progress, Ui.Button("Cancel download", "link", () => Shell.Models.CancelDownload()));
        Compose(
            _download,
            Ui.Card(Ui.Stack(10, Ui.Text("Turns your voice into words, on this PC. Bigger is more accurate and slower."), _speech,
                Ui.Row(8, Ui.Button("Choose model file…", "secondary", () => Run(Shell.Models.ChooseFile))),
                Ui.Line("Decoding", _decoding, "Beam search tries several readings of each phrase.")), "Speech model"),
            Ui.Card(Ui.Stack(10,
                Ui.Text("The language you speak. Anything but English needs a multilingual model (small, medium or large-v3), not an English-only \".en\" one; " +
                        "Hindi, Tagalog and the other Indian languages are listed first. Whisper has no Cebuano or other Philippine language apart from Tagalog."),
                Ui.Line("Spoken language", _language, "Detect automatically works it out from the first seconds of each dictation."),
                Ui.Line("Translate to English", _translate, "Writes English whatever you speak. OpenAI recommends medium or large-v3 for translation; large-v3-turbo cannot translate."),
                _languageNote), "Language"),
            Ui.Card(Ui.Stack(10, Ui.Text("Tidies the words: filler, punctuation, capitals. A different job from the speech model."), _cleanup,
                Ui.Row(8,
                    Ui.Button("Rule-based tidy only", "secondary", () => Run(Shell.Models.SelectRulesOnly)),
                    Ui.Button("Choose .gguf file…", "secondary", () => Run(Shell.Models.ChooseCleanupFile))),
                Ui.Text("Cloud clean-up is switched on in Clean-up, after a consent step.", "caption")), "Clean-up model"),
            Ui.Button("Storage: model sizes, free space and Delete", "link", () => _window.ShowPage("Storage")));
    }

    protected override void Redraw()
    {
        var status = Shell.Status;
        _download.IsVisible = status.Download is not null;
        _downloadText.Text = status.Download ?? "";
        _progress.IsIndeterminate = status.DownloadFraction is null;
        _progress.Value = status.DownloadFraction ?? 0;
        var s = Shell.Settings;
        _decoding.SelectedIndex = s.Whisper.BeamSize > 0 ? 1 : 0;
        var languageIndex = -1;
        for (var i = 0; i < Languages.Count && languageIndex < 0; i++) if (Languages[i].Code == s.Whisper.Language) languageIndex = i;
        _language.SelectedIndex = languageIndex;
        Ui.SetQuietly(_translate, s.Whisper.Translate);
        _languageNote.Text = WhisperLanguages.Problem(Shell.Models.ModelPath, s.Whisper.Language, s.Whisper.Translate) ?? "";
        _languageNote.IsVisible = _languageNote.Text.Length > 0;

        // The lists are rebuilt only when what they show changes, not on every download-progress tick.
        var downloaded = string.Join(",", ModelCatalog.Entries.Where(Shell.Models.IsDownloaded).Select(e => e.Name)) + "|" +
                         string.Join(",", CleanupCatalog.Entries.Where(Shell.Models.IsCleanupDownloaded).Select(e => e.Name));
        var key = $"{Shell.Models.CurrentName}|{s.Llm.Provider}|{s.Llm.Model}|{downloaded}|{status.Download is null}";
        if (key == _drawn) return;
        _drawn = key;

        _speech.Children.Clear();
        foreach (var e in ModelCatalog.Entries)
        {
            var inUse = e.Name == Shell.Models.CurrentName;
            var have = Shell.Models.IsDownloaded(e);
            _speech.Children.Add(ModelRow($"{e.Name} · {e.SizeText}", $"{(inUse ? "In use" : have ? "Downloaded" : "Not downloaded")} · {e.TradeOff}",
                inUse ? null : have ? "Use" : "Download…", () => Run(() => Shell.Models.Select(e.Name))));
        }
        if (ModelCatalog.Find(Shell.Models.CurrentName) is null)
            _speech.Children.Add(ModelRow(Shell.Models.CurrentName, $"In use · {Shell.Models.ModelPath}", null, () => { }));

        _cleanup.Children.Clear();
        var current = s.Llm.Provider == LlmSettings.Local ? CleanupCatalog.FindByFile(s.Llm.Model)?.Name ?? s.Llm.Model : null;
        foreach (var e in CleanupCatalog.Entries)
        {
            var inUse = e.Name == current;
            var have = Shell.Models.IsCleanupDownloaded(e);
            _cleanup.Children.Add(ModelRow($"{e.Name} · {e.SizeText}{e.Marker}", $"{(inUse ? "In use" : have ? "Downloaded" : "Not downloaded")} · {e.TradeOff}",
                inUse ? null : have ? "Use" : "Download…", () => Run(() => Shell.Models.SelectCleanup(e.Name))));
        }
        if (current is null)
            _cleanup.Children.Add(Ui.Text($"In use: {Shell.Models.CurrentCleanupName}{(s.Llm.Provider == LlmSettings.NoProvider ? " (rule-based tidy)" : "")}", "caption"));
    }

    private static Grid ModelRow(string title, string detail, string? action, Action click)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(Ui.Stack(2, Ui.Text(title, "body"), Ui.Text(detail, "hint")));
        var button = Ui.Button(action ?? "In use", action is null ? "quiet" : "secondary", click);
        button.IsEnabled = action is not null;
        button.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }
}

/// <summary>
/// Settings → Storage (SPEC §17, §18.2): where everything is (the real path when packaged), free space, every model file
/// with its size and Delete, the data deletions with the size each frees, and "Remove Dictum completely".
/// </summary>
internal sealed class StoragePage : SettingsPage
{
    private readonly TextBlock _folder = Ui.Text("", "caption");
    private readonly TextBlock _free = Ui.Text("", "caption");
    private readonly StackPanel _models = new() { Spacing = 8 };
    private readonly TextBlock _total = Ui.Text("", "caption");
    private readonly TextBlock _recordings = Ui.Text("", "hint");
    private readonly TextBlock _logs = Ui.Text("", "hint");
    private readonly TextBlock _all = Ui.Text("", "hint");
    private readonly CheckBox _including = new() { Content = "Including settings, dictionary and corrections (never your .env keys)" };
    private readonly StackPanel _places = new() { Spacing = 6 };
    private long _drawnAt;

    public StoragePage(AppShell shell) : base(shell, "Storage")
    {
        _including.IsCheckedChanged += (_, _) => Draw();
        Compose(
            Ui.Card(Ui.Stack(8, _folder, _free, _models, _total), "Models"),
            Ui.Card(Ui.Stack(10,
                DataRow("Calibration recordings", _recordings, RemovalScope.CalibrationRecordings),
                DataRow("Logs", _logs, RemovalScope.Logs),
                DataRow("All app data", _all, null),
                _including,
                Ui.Text("Dictum keeps no dictation history: the last dictation is held in memory for Fix last dictation and is gone when Dictum quits.", "caption")), "Data"),
            Ui.Card(_places, "Where Dictum keeps things"),
            Ui.Card(Ui.Stack(10,
                Ui.Text("Deletes every model, recording, log, setting, your dictionary, corrections and .env keys, the Start menu shortcut and the " +
                        "launch-at-login entry, then the program itself. Two warnings come first; the second needs DELETE typed."),
                Ui.Row(8, Ui.Button("Remove Dictum completely…", "danger", () => Run(() => Shell.RemoveEverything())))), "Remove everything"));
    }

    protected override void Redraw()
    {
        if (Environment.TickCount64 - _drawnAt < 1000) return; // a download's progress ticks; the file sizes are re-read once a second at most
        Draw();
    }

    private void Draw()
    {
        _drawnAt = Environment.TickCount64;
        var s = Shell.Settings;
        var dir = Shell.ModelsDir;
        _folder.Text = $"Models folder: {Shell.RealPath(dir)}";
        _free.Text = DiskSpace.FreeBytes(dir) is long free ? $"{ModelCatalog.FormatSize(free)} free on this drive." : "Free space on this drive could not be read.";
        var files = ModelFiles.List(Shell.Paths, s);
        _models.Children.Clear();
        foreach (var model in files)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
            grid.Children.Add(Ui.Stack(2, Ui.Text(model.Name + (model.Partial ? " (unfinished download)" : ""), "body"), Ui.Text(Shell.RealPath(model.Path), "hint")));
            var size = Ui.Text(ModelCatalog.FormatSize(model.Bytes), "caption");
            size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1);
            var delete = Ui.Button("Delete…", "secondary", () => _ = DeleteAsync(model), name: $"Delete {model.Name}");
            Grid.SetColumn(delete, 2);
            grid.Children.AddRange([size, delete]);
            _models.Children.Add(grid);
        }
        if (files.Count == 0) _models.Children.Add(Ui.Text("No model files in this folder.", "caption"));
        _total.Text = $"Total: {ModelCatalog.FormatSize(files.Sum(f => f.Bytes))}.";
        _recordings.Text = Size(RemovalScope.CalibrationRecordings);
        _logs.Text = Size(RemovalScope.Logs);
        _all.Text = Size(AllScope) + (_including.IsChecked == true ? ": models, recordings, logs, settings and your lists." : ": models, recordings and logs; settings and your lists are kept.");

        _places.Children.Clear();
        _places.Children.Add(Place("Settings, dictionary, corrections, .env", Shell.Paths.ConfigDir));
        _places.Children.Add(Place("Models, logs, calibration recordings", Shell.Paths.LocalDir));
        _places.Children.Add(Place("Program", Shell.Paths.ExeDir));
        if (Shell.Platform.Package.IsPackaged)
            _places.Children.Add(Ui.Text("This copy is installed from a package, so Windows keeps its files in the package's own folder: the paths above are the real ones.", "caption"));
    }

    private RemovalScope AllScope => _including.IsChecked == true ? RemovalScope.AllDataIncludingSettings : RemovalScope.AllData;

    private string Size(RemovalScope scope)
    {
        var plan = RemovalPlan.Build(Shell.Paths, Shell.Settings, scope);
        return $"{ModelCatalog.FormatSize(plan.TotalBytes)} in {plan.Items.Count(i => !i.IsFolder)} file(s)";
    }

    private Grid DataRow(string title, TextBlock size, RemovalScope? scope)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(Ui.Stack(2, Ui.Text(title, "body"), size));
        var delete = Ui.Button("Delete…", "secondary", () => Run(() =>
        {
            Say(Shell.DeleteData(scope ?? AllScope));
            Draw();
        }), name: $"Delete {title.ToLowerInvariant()}");
        delete.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(delete, 1);
        grid.Children.Add(delete);
        return grid;
    }

    private Grid Place(string what, string path)
    {
        var real = Shell.RealPath(path);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(Ui.Stack(2, Ui.Text(what, "body"), Ui.Text(real, "hint")));
        var open = Ui.Button("Open", "secondary", () => Say(Shell.OpenPath(real, folder: true) ?? ""), name: $"Open the folder for {what}");
        open.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);
        return grid;
    }

    private async Task DeleteAsync(StoredModel model)
    {
        try
        {
            Say(await Shell.DeleteModelAsync(model));
        }
        catch (Exception ex)
        {
            Say($"{model.Name} was not deleted: {ex.Message}");
        }
        Draw();
    }
}
