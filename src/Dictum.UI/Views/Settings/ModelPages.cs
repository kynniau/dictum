using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Dictum.App;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// Settings → Models: the speech and clean-up catalogues with their state, a file chooser for each, decoding, the language,
/// "Add AI clean-up" while the rule-based tidy is in use, and the model folder (Decision 15). Since 1.2 (21/10/2026) a
/// Simple | Advanced switch (ui.view) heads it: Simple, the default, lists the three plain tiers of <see cref="SimpleView"/>
/// and "Tidy my words"; Advanced is the full catalogue, decoding and the file choosers, as before.
/// </summary>
internal sealed class ModelsPage : SettingsPage
{
    // APPROVED 22/10/2026 copy (proposed 17/10/2026) (Decisions 15 and 16).
    internal const string FolderLabel = "Model folder";
    internal const string BrowseLabel = "Browse…";
    internal const string UseDefaultLabel = "Use default";
    internal const string FolderNote = "Dictum looks here first and downloads go here. Changing it does not move any files.";
    internal const string NetworkWarning = "Models load slowly from a network folder; a folder on this PC is faster.";
    internal const string IncludedState = "Included";
    internal const string AddCleanupLabel = "Add AI clean-up";

    // APPROVED 22/10/2026 copy (proposed 19/10/2026): a catalogue row whose file is downloading or waiting its
    // turn (Store 10.1.2.10). Its button is disabled, so choosing it again cannot restart or cancel anything.
    internal const string WaitingState = "Waiting to download";
    internal const string DownloadingButton = "Downloading…";
    internal const string WaitingButton = "Waiting…";

    /// <summary>APPROVED 22/10/2026: the state of the row whose file is downloading now, "Downloading 42%".</summary>
    internal static string DownloadingState(string? progress) => progress is null ? "Downloading" : $"Downloading {progress}";

    /// <summary>APPROVED 22/10/2026: the disabled button's accessible name, saying why it cannot be pressed.</summary>
    internal static string BusyButtonName(string model, DownloadState state) =>
        state == DownloadState.Running ? $"{model} is already downloading" : $"{model} is already waiting to download";

    /// <summary>APPROVED 22/10/2026: "Add AI clean-up (1.1 GB download)", the size read from the catalogue; without the download part once the model is on this PC.</summary>
    internal static string AddCleanupText(CleanupModelInfo model, bool onThisPc) => onThisPc ? AddCleanupLabel : $"{AddCleanupLabel} ({model.SizeText} download)";

    // APPROVED 22/10/2026 copy (proposed 21/10/2026): the Simple | Advanced segments' accessible name ("Models view: Simple"); the rest is in SimpleView.
    internal const string ViewName = "Models view";

    /// <summary>The Language card in the Simple view: no model names (APPROVED 22/10/2026; proposed 1.2 polish, 22/10/2026, the orchestrator's wording).</summary>
    internal const string LanguageIntroSimple =
        "Dictum comes with an English-only model. For other languages it downloads a model that understands them; choose the language here.";

    /// <summary>
    /// The Language card in the Advanced view, as before but for the capital after its semicolon (1.2 polish: "…\".en\" one;
    /// any of …"; APPROVED 22/10/2026 as changed).
    /// </summary>
    internal const string LanguageIntroAdvanced =
        "The language you speak. Anything but English needs a multilingual model (small, medium or large-v3), not an English-only \".en\" one; " +
        "any of the languages Whisper supports can be chosen, in alphabetical order after English; some languages are not in Whisper's list, so they cannot be chosen.";

    /// <summary>The state shown, as a tag and not a button, on the row of the model in use (1.2 polish: a greyed button read as broken).</summary>
    internal const string InUseLabel = "In use";

    /// <summary>The calibration test's label, as on the tray panel's button that opens the same window.</summary>
    internal const string CalibrateLabel = "Calibrate my voice";

    /// <summary>The line under the folder when models would load from the network (a UNC path or a mapped network drive), else null.</summary>
    internal static string? FolderWarning(string folder) => DiskSpace.IsNetwork(folder) ? NetworkWarning : null;

    private static readonly IReadOnlyList<(string Code, string Label)> Languages = WhisperLanguages.PickerItems();

    private readonly SettingsWindow _window;
    private readonly StackPanel _speech = new() { Spacing = 8 };
    private readonly StackPanel _cleanup = new() { Spacing = 8 };
    private readonly PathBox _folder = new(FolderLabel); // trimmed from the left: the folder's own name stays in view (1.2 polish)
    private readonly Button _useDefault;
    private readonly TextBlock _folderWarning = Ui.Text("", "caption");
    private readonly Button _addCleanup;
    private readonly ComboBox _decoding = new() { ItemsSource = new[] { "Greedy (fastest)", "Beam search, 5 beams (more accurate, slower)" }, Width = 320 };
    private readonly ComboBox _language = new() { ItemsSource = Languages.Select(l => l.Label).ToList(), Width = 320 };
    private readonly ToggleSwitch _translate;
    private readonly TextBlock _languageNote = Ui.Text("", "caption");
    private readonly TextBlock _downloadText = Ui.Text("", "caption");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 4 };
    private readonly StackPanel _download;
    private readonly Segments _view;
    private readonly StackPanel _simpleSpeech = new() { Spacing = 8 };
    private readonly StackPanel _simpleCleanup = new() { Spacing = 8 };
    private readonly StackPanel _simpleOff = new() { Spacing = 8 }; // the Off row, under "Add AI clean-up" so that button stays under the Tidy row
    private readonly Control[] _simpleOnly;
    private readonly Control[] _advancedOnly;
    private string _drawn = "";
    private (int, int)? _lookedUpAt; // the shell's ModelFilesVersion when the lookups below were made; null: look up at the next redraw
    private List<ModelFolderKind?> _sources = []; // the folder each speech catalogue entry is used from, in catalogue order
    private List<string> _cleanupOnPc = []; // the clean-up catalogue entries on this PC
    private (TextBlock Detail, string TradeOff)? _downloadingRow; // the row whose file downloads now: its percentage changes on every tick

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
            var listens = code == WhisperLanguages.Auto
                ? "Dictum works out the language from the first seconds of each dictation" // the card's own words for Detect automatically
                : $"Dictum listens for {WhisperLanguages.Name(code)} ({code})";
            Say(code == WhisperLanguages.English
                ? "Dictum listens for English again."
                : $"{listens}{(w.Translate ? " and writes English" : "")}. The decoder switches now; the model stays loaded.");
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
        _download = Ui.Stack(4, _downloadText, _progress, Ui.Button("Cancel download", "link", () => Shell.Models.CancelDownload(DownloadCancelReason.CancelLink)));
        // The recommended clean-up model, through the catalogue's own path (asked first, then downloaded and switched to).
        _addCleanup = Ui.Button("", "primary", () => Run(() => Shell.Models.SelectCleanup(CleanupCatalog.Recommend(Shell.GpuMachine).Model)));
        _useDefault = Ui.Button(UseDefaultLabel, "secondary", () => Run(() => ModelFolderActions.UseDefaultFolder(Shell, Say)), name: ModelFolderActions.UseDefaultName);
        var browse = Ui.Button(BrowseLabel, "secondary", () => Run(() => ModelFolderActions.BrowseFolder(Shell, Say)), name: "Browse for a model folder");
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(_useDefault, 2);
        folderRow.Children.AddRange([_folder, browse, _useDefault]);
        // ui.view (1.2): Simple | Advanced, written through the settings writer like every other key; the reload redraws the page.
        _view = new Segments([SimpleView.SimpleLabel, SimpleView.AdvancedLabel], -1, i => Write(("ui.view", Json(UiSettings.Views[i]))), ViewName);
        // The Simple view's own controls, and the Advanced ones it hides; the language, the folder and Storage are in both.
        var speechIntro = Ui.Text("Turns your voice into words, on this PC. Bigger is more accurate and slower.");
        var explainer = Ui.Stack(8, Ui.Text(SimpleView.Explainer), Ui.Row(8, Ui.Button(CalibrateLabel, "secondary", () => Run(Shell.OpenCalibration))));
        var chooseFile = Ui.Row(8, Ui.Button("Choose model file…", "secondary", () => Run(Shell.Models.ChooseFile)));
        var decoding = Ui.Line("Decoding", _decoding, "Beam search tries several readings of each phrase.");
        var cleanupButtons = Ui.Row(8,
            Ui.Button("Rule-based tidy only", "secondary", () => Run(Shell.Models.SelectRulesOnly)),
            Ui.Button(SimpleView.OffAdvancedLabel, "secondary", () => Run(Shell.Models.SelectOff)),
            Ui.Button("Choose .gguf file…", "secondary", () => Run(Shell.Models.ChooseCleanupFile)));
        var cloudNote = Ui.Text("Cloud clean-up is switched on in Clean-up, after a consent step.", "caption");
        var languageSimple = Ui.Text(LanguageIntroSimple);
        var languageAdvanced = Ui.Text(LanguageIntroAdvanced);
        _simpleOnly = [explainer, _simpleSpeech, _simpleCleanup, _simpleOff, languageSimple];
        _advancedOnly = [speechIntro, _speech, chooseFile, decoding, _cleanup, cleanupButtons, cloudNote, languageAdvanced];
        Compose(
            Ui.Stack(6, _view, Ui.Text(SimpleView.SwitchHelp, "caption")),
            _download,
            Ui.Card(Ui.Stack(10, speechIntro, explainer, _simpleSpeech, _speech, chooseFile, decoding), "Speech model"),
            Ui.Card(Ui.Stack(10,
                languageSimple,
                languageAdvanced,
                Ui.Line("Spoken language", _language, "Detect automatically works it out from the first seconds of each dictation."),
                Ui.Line("Translate to English", _translate, "Writes English whatever you speak. OpenAI recommends medium or large-v3 for translation; large-v3-turbo cannot translate."),
                _languageNote), "Language"),
            // "Add AI clean-up" is one button in both views: under the Tidy row in Simple, above the catalogue in Advanced.
            Ui.Card(Ui.Stack(10, Ui.Text("Tidies the words: filler, punctuation, capitals. A different job from the speech model."), _simpleCleanup, _addCleanup, _simpleOff, _cleanup,
                cleanupButtons, cloudNote), "Clean-up model"),
            Ui.Card(Ui.Stack(8, folderRow, Ui.Text(FolderNote, "caption"), _folderWarning), FolderLabel),
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
        var simple = s.Ui.IsSimple;
        _view.Select(simple ? 0 : 1); // quiet, and a no-op when it already shows: the segment keeps keyboard focus
        foreach (var control in _simpleOnly) control.IsVisible = simple;
        foreach (var control in _advancedOnly) control.IsVisible = !simple;
        _decoding.SelectedIndex = s.Whisper.BeamSize > 0 ? 1 : 0;
        var languageIndex = -1;
        for (var i = 0; i < Languages.Count && languageIndex < 0; i++) if (Languages[i].Code == s.Whisper.Language) languageIndex = i;
        _language.SelectedIndex = languageIndex;
        Ui.SetQuietly(_translate, s.Whisper.Translate);
        // Judged against the model in use, which a redraw reads from memory (a progress tick looks in no folder; re-lens, 17/10/2026).
        _languageNote.Text = WhisperLanguages.Problem(Shell.Models.CurrentPath, s.Whisper.Language, s.Whisper.Translate) ?? "";
        _languageNote.IsVisible = _languageNote.Text.Length > 0;
        var folder = Shell.ModelsDir;
        _folder.Path = Shell.RealPath(folder);
        _useDefault.IsEnabled = Shell.Paths.ChosenModelsDir(s) is not null;
        _folderWarning.Text = FolderWarning(folder) ?? "";
        _folderWarning.IsVisible = _folderWarning.Text.Length > 0;

        // Every catalogue model is looked up again only after a settings load, a download that ended or a deletion, or when the
        // page is shown again (Leave), never on a download's progress ticks: each lookup reads up to three folders, on the UI
        // thread, and a folder on the network could stall every redraw (lens, 17/10/2026).
        var version = Shell.ModelFilesVersion;
        if (_lookedUpAt != version)
        {
            _lookedUpAt = version;
            _sources = [.. ModelCatalog.Entries.Select(Shell.Models.Source)];
            _cleanupOnPc = [.. CleanupCatalog.Entries.Where(Shell.Models.IsCleanupDownloaded).Select(e => e.Name)];
        }
        // The lists are rebuilt only when what they show changes, not on every download-progress tick (which would also take
        // keyboard focus off a row's button): a tick only rewrites the downloading row's percentage.
        var downloaded = string.Join(",", _sources) + "|" + string.Join(",", _cleanupOnPc);
        var fetching = string.Join(",", ModelCatalog.Entries.Select(e => Shell.Models.DownloadStateOf(e.FileName)))
                       + "|" + string.Join(",", CleanupCatalog.Entries.Select(e => Shell.Models.DownloadStateOf(e.FileName)));
        var gpu = Shell.GpuMachine;
        var key = $"{Shell.Models.CurrentName}|{s.Llm.Provider}|{s.Llm.Model}|{downloaded}|{status.Download is null}|{fetching}|{s.Ui.View}|{gpu}|{s.Whisper.Language}|{s.Whisper.Translate}";
        if (key != _drawn)
        {
            _drawn = key;
            DrawLists(s, gpu);
        }
        if (_downloadingRow is var (detail, tradeOff)) detail.Text = $"{DownloadingState(Shell.Models.DownloadProgress)} · {tradeOff}";
    }

    private void DrawLists(AppSettings s, bool gpu)
    {
        _downloadingRow = null;

        var recommended = CleanupCatalog.Find(CleanupCatalog.Recommend(gpu).Model)!;
        _addCleanup.Content = AddCleanupText(recommended, _cleanupOnPc.Contains(recommended.Name));
        _addCleanup.IsVisible = SimpleView.OffersAddCleanup(s); // not in Simple while the text is not English: the download would change nothing
        // While that model downloads or waits its turn, the button is disabled and named for why, as its row is (lens, 19/10/2026).
        var fetch = Shell.Models.DownloadStateOf(recommended.FileName);
        _addCleanup.IsEnabled = fetch == DownloadState.None;
        AutomationProperties.SetName(_addCleanup, fetch == DownloadState.None ? null : BusyButtonName(recommended.Name, fetch));

        // Only the view shown is built: the other's rows are dropped, and built afresh when the view is switched.
        _simpleSpeech.Children.Clear();
        _simpleCleanup.Children.Clear();
        _simpleOff.Children.Clear();
        _speech.Children.Clear();
        _cleanup.Children.Clear();
        if (s.Ui.IsSimple)
        {
            DrawSimple(s, gpu);
            return;
        }

        for (var i = 0; i < ModelCatalog.Entries.Count; i++)
        {
            var e = ModelCatalog.Entries[i];
            var inUse = e.Name == Shell.Models.CurrentName;
            var have = _sources[i] is not null;
            var state = inUse ? "In use" : !have ? "Not downloaded" : _sources[i] == ModelFolderKind.Bundled ? IncludedState : "Downloaded";
            _speech.Children.Add(CatalogueRow(e.Name, $"{e.Name} · {e.SizeText}", state, e.TradeOff, inUse, have, e.FileName, () => Run(() => Shell.Models.Select(e.Name))));
        }
        if (ModelCatalog.Find(Shell.Models.CurrentName) is null)
            _speech.Children.Add(ModelRow(Shell.Models.CurrentName, Shell.Models.CurrentPath, null, () => { })); // the tag says "In use"

        var current = s.Llm.Provider == LlmSettings.Local ? CleanupCatalog.FindByFile(s.Llm.Model)?.Name ?? s.Llm.Model : null;
        foreach (var e in CleanupCatalog.Entries)
        {
            var inUse = e.Name == current;
            var have = _cleanupOnPc.Contains(e.Name);
            _cleanup.Children.Add(CatalogueRow(e.Name, $"{e.Name} · {e.SizeText}{e.Marker}", inUse ? "In use" : have ? "Downloaded" : "Not downloaded", e.TradeOff,
                inUse, have, e.FileName, () => Run(() => Shell.Models.SelectCleanup(e.Name))));
        }
        if (current is null)
        {
            var note = s.Llm.Provider switch { LlmSettings.NoProvider => " (rule-based tidy)", LlmSettings.Off => SimpleView.OffInUseNote, _ => "" };
            _cleanup.Children.Add(Ui.Text($"In use: {Shell.Models.CurrentCleanupName}{note}", "caption"));
        }
    }

    /// <summary>
    /// The Simple view (ui.view = simple): the speech tiers of <see cref="SimpleView.Tiers"/> for this machine class and these
    /// language settings, a Custom row for a model in use that is none of them, "Tidy my words" and "Off". Each row is a catalogue
    /// row (the same states, Downloading 42% included, and the same Select path, given the tier's name for its question); a tier
    /// the language rules out has no button, only its reason. No cloud provider is offered here.
    /// </summary>
    private void DrawSimple(AppSettings s, bool gpu)
    {
        var current = Shell.Models.CurrentName;
        var (language, translate) = (s.Whisper.Language, s.Whisper.Translate);
        foreach (var tier in SimpleView.Tiers(gpu, language, translate))
        {
            var i = IndexOfModel(tier.Model);
            var e = ModelCatalog.Entries[i];
            var inUse = e.Name == current;
            if (tier.Guard is { } guard) // never offered: choosing it would switch to a model that cannot hear this language
            {
                _simpleSpeech.Children.Add(inUse ? ModelRow(tier.Label, guard, null, () => { }) : TextRow(tier.Label, guard)); // the tag says "In use"
                continue;
            }
            var source = _sources[i];
            var bundled = source == ModelFolderKind.Bundled;
            // "Included" only when it is: a multilingual Standard, or a build without the bundled base.en, says it without.
            var subLine = tier.Tier == SpeechTier.Standard && !bundled ? SimpleView.StandardSubLineNotIncluded : tier.SubLine;
            // Not on this PC: its size alone, as the line itself may say "download" (lens nit, 21/10/2026).
            string? state = inUse ? "In use" : bundled ? null : source is not null ? "Downloaded" : e.SizeText;
            _simpleSpeech.Children.Add(CatalogueRow(tier.Label, tier.Label, state, subLine, inUse, source is not null, e.FileName,
                () => Run(() => Shell.Models.Select(e.Name, tier.Label)), simple: true));
        }
        if (SimpleView.TierOf(current, gpu, language, translate) is null)
            _simpleSpeech.Children.Add(ModelRow(SimpleView.Custom(current), SimpleView.CustomNote, null, () => { }));

        var tidy = SimpleView.TidyInUse(s.Llm, gpu);
        var english = Dictum.Transcribe.WhisperLanguages.OutputIsEnglish(language, translate);
        var tidyLine = SimpleView.TidyLine(language, translate);
        // With text that is not English no clean-up model runs (whitespace only), so "with AI clean-up" would be untrue.
        // In use, the row has the "In use" tag, so its line does not say it again (1.2 polish round 2).
        var tidyState = tidy && s.Llm.Provider == LlmSettings.Local && english ? SimpleView.TidyWithAiState : null;
        _simpleCleanup.Children.Add(ModelRow(SimpleView.TidyTitle, tidyState is null ? tidyLine : $"{tidyState} · {tidyLine}",
            tidy ? null : "Use", () => Run(() => UseTidy(Shell)), out _, name: tidy ? null : SimpleView.ActionName("Use", SimpleView.TidyLabel)));
        var off = SimpleView.OffInUse(s.Llm);
        if (!tidy && !off)
            _simpleCleanup.Children.Add(ModelRow(SimpleView.Custom(Shell.Models.CurrentCleanupName), SimpleView.CustomNote, null, () => { }));
        // "Add AI clean-up", drawn by the caller and shown while the rule-based tidy is in use on English text, sits under this row; Off under it.
        // Off has no language dependency (it never runs a clean-up), so unlike the tiers it is offered for every language.
        _simpleOff.Children.Add(ModelRow(SimpleView.OffLabel, SimpleView.OffSubLine,
            off ? null : "Use", () => Run(Shell.Models.SelectOff), out _, name: off ? null : SimpleView.ActionName("Use", SimpleView.OffLabel)));
    }

    /// <summary>A row with a title and a line and no button: a tier the Simple view cannot offer, with why.</summary>
    private static Control TextRow(string title, string line) => Ui.Stack(2, Ui.Text(title, "body"), Ui.Text(line, "hint"));

    private static int IndexOfModel(string name)
    {
        for (var i = 0; i < ModelCatalog.Entries.Count; i++)
            if (string.Equals(ModelCatalog.Entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new InvalidOperationException($"{name} is not in the speech catalogue."); // pinned: every tier model is (SimpleViewTests)
    }

    /// <summary>
    /// "Tidy my words" chosen (Settings or the tray panel in the Simple view): the recommended clean-up model when it is on this
    /// PC, else the rule-based tidy. Both go through the catalogue's own paths, so nothing is downloaded from here.
    /// </summary>
    internal static void UseTidy(AppShell shell)
    {
        var model = CleanupCatalog.Find(SimpleView.TidyModel(shell.GpuMachine))!;
        if (shell.Models.IsCleanupDownloaded(model)) shell.Models.SelectCleanup(model.Name);
        else shell.Models.SelectRulesOnly();
    }

    /// <summary>Shown again later, the page looks every model up afresh (a file copied in meanwhile is seen).</summary>
    public override void Leave()
    {
        _lookedUpAt = null;
        base.Leave();
    }

    /// <summary>
    /// A catalogue row: In use (no action), Use, or Download…; a model whose file is downloading shows "Downloading 42%" (or
    /// "Waiting to download") with its button disabled and named for why, so it cannot be chosen again mid-download.
    /// </summary>
    /// <param name="state">The row's state before its line ("Downloaded"), or null for none (the bundled Standard tier says "Included" in its line). A row in use shows its tag instead, and its line drops the state.</param>
    /// <param name="simple">A Simple-view row: its Use or Download… button is named for its tier ("Use More accurate").</param>
    private Grid CatalogueRow(string name, string title, string? state, string tradeOff, bool inUse, bool have, string fileName, Action click, bool simple = false)
    {
        var fetch = inUse || have ? DownloadState.None : Shell.Models.DownloadStateOf(fileName);
        if (fetch == DownloadState.None)
        {
            var action = inUse ? null : have ? "Use" : "Download…";
            // The model in use has the "In use" tag, so its line does not say it again (1.2 polish round 2: "In use" twice on a row).
            return ModelRow(title, state is null || inUse ? tradeOff : $"{state} · {tradeOff}", action, click, out _,
                name: simple && action is not null ? SimpleView.ActionName(action, name) : null);
        }
        var running = fetch == DownloadState.Running;
        var row = ModelRow(title, running ? $"{DownloadingState(Shell.Models.DownloadProgress)} · {tradeOff}" : $"{WaitingState} · {tradeOff}",
            running ? DownloadingButton : WaitingButton, click, out var detail, enabled: false, name: BusyButtonName(name, fetch));
        if (running) _downloadingRow = (detail, tradeOff);
        return row;
    }

    private static Grid ModelRow(string title, string detail, string? action, Action click) => ModelRow(title, detail, action, click, out _);

    private static Grid ModelRow(string title, string detail, string? action, Action click, out TextBlock detailText, bool enabled = true, string? name = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        detailText = Ui.Text(detail, "hint");
        grid.Children.Add(Ui.Stack(2, Ui.Text(title, "body"), detailText));
        // The model in use has nothing to press: a tag in the link colour (accent, legible in both themes), not a greyed
        // button that looks broken (1.2 polish, 22/10/2026). It keeps the button's accessible name, "In use".
        Control trailing = action is null ? Ui.Tag(InUseLabel) : Ui.Button(action, "secondary", click, name: name);
        if (trailing is Button button) button.IsEnabled = enabled;
        trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return grid;
    }
}

/// <summary>
/// Settings → Storage (SPEC §17, §18.2): where everything is (the real path when packaged), the model folders in lookup order,
/// free space, every model file with its size, its folder and Delete (none for the read-only models that came with Dictum),
/// the data deletions with the size each frees, and "Remove Dictum completely".
/// </summary>
internal sealed class StoragePage : SettingsPage
{
    // APPROVED 22/10/2026 copy (proposed 17/10/2026) (Decisions 15 and 16).
    internal const string LookupIntro = "Dictum looks for a model in these folders, in this order:";
    internal const string NoModels = "No model files yet.";

    // APPROVED 22/10/2026 copy (proposed 22/10/2026; Decision "Storage page can change the model folder"): the model folder's two actions, the same
    // handlers as Settings > Models (ModelFolderActions), its note repeated (ModelsPage.FolderNote) and one sentence on drive speed.
    internal const string ChangeLabel = "Change…";
    internal const string ChangeName = "Change the model folder";
    internal const string SlowerDriveNote = "A slower drive makes the speech model slower to load and to switch.";

    internal static string DownloadsGoTo(string folder) => $"Downloads go to: {folder}";

    internal static string FolderName(ModelFolderKind kind) => kind switch
    {
        ModelFolderKind.Chosen => "Chosen folder",
        ModelFolderKind.Default => "Default folder",
        _ => "Included with Dictum (read-only)",
    };

    private readonly TextBlock _folder = Ui.Text("", "caption");
    private readonly TextBlock _folderWarning = Ui.Text("", "caption"); // Models' network-folder warning, the same approved words
    private readonly Button _useDefault;
    private readonly TextBlock _free = Ui.Text("", "caption");
    private readonly StackPanel _folders = new() { Spacing = 2 };
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
        // The model folder's row: where downloads go, with the two actions of Settings > Models. Only this folder can be moved:
        // the settings, dictionary and log folders get no button.
        _folder.VerticalAlignment = VerticalAlignment.Center;
        var change = Ui.Button(ChangeLabel, "secondary", () => Run(() => { ModelFolderActions.BrowseFolder(Shell, Say); Draw(); }), name: ChangeName);
        _useDefault = Ui.Button(ModelsPage.UseDefaultLabel, "secondary", () => Run(() => { ModelFolderActions.UseDefaultFolder(Shell, Say); Draw(); }), name: ModelFolderActions.UseDefaultName);
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(change, 1);
        Grid.SetColumn(_useDefault, 2);
        folderRow.Children.AddRange([_folder, change, _useDefault]);
        Compose(
            Ui.Card(Ui.Stack(8, folderRow, Ui.Text(ModelsPage.FolderNote, "caption"), Ui.Text(SlowerDriveNote, "caption"), _folderWarning, _free, _folders, _models, _total), "Models"),
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
        _folder.Text = DownloadsGoTo(Shell.RealPath(dir));
        _useDefault.IsEnabled = Shell.Paths.ChosenModelsDir(s) is not null; // as on Settings > Models: nothing to return to while none is chosen
        _folderWarning.Text = ModelsPage.FolderWarning(dir) ?? "";
        _folderWarning.IsVisible = _folderWarning.Text.Length > 0;
        _free.Text = DiskSpace.FreeBytes(dir) is long free ? $"{ModelCatalog.FormatSize(free)} free on this drive." : "Free space on this drive could not be read.";
        _folders.Children.Clear();
        _folders.Children.Add(Ui.Text(LookupIntro, "caption"));
        var order = ModelFolders.LookupOrder(Shell.Paths, s);
        for (var i = 0; i < order.Count; i++) _folders.Children.Add(Ui.Text($"{i + 1}. {FolderName(order[i].Kind)}: {Shell.RealPath(order[i].Path)}", "hint"));
        var files = ModelFiles.List(Shell.Paths, s, withBundled: true);
        _models.Children.Clear();
        foreach (var model in files)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
            grid.Children.Add(Ui.Stack(2, Ui.Text(model.Name + (model.Partial ? " (unfinished download)" : ""), "body"),
                Ui.Text($"{FolderName(model.Folder)} · {Shell.RealPath(model.Path)}", "hint")));
            var size = Ui.Text(ModelCatalog.FormatSize(model.Bytes), "caption");
            size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1);
            grid.Children.Add(size);
            if (!model.ReadOnly) // the models that came with Dictum are never deleted (ModelFiles.Delete refuses them too)
            {
                var delete = Ui.Button("Delete…", "secondary", () => _ = DeleteAsync(model), name: $"Delete {model.Name}");
                Grid.SetColumn(delete, 2);
                grid.Children.Add(delete);
            }
            _models.Children.Add(grid);
        }
        if (files.Count == 0) _models.Children.Add(Ui.Text(NoModels, "caption"));
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

/// <summary>
/// The model folder's two actions, shared by Settings > Models (Browse…, Use default) and Settings > Storage (Change…, Use default):
/// one chooser seam and one write, so the two pages cannot drift apart. Both write models.folder through
/// <see cref="ModelCoordinator.SetModelsFolder"/>, which refuses the bundled folder and reloads the settings.
/// </summary>
internal static class ModelFolderActions
{
    /// <summary>The accessible name of "Use default" on both pages.</summary>
    internal const string UseDefaultName = "Use the default model folder";

    /// <summary>The system's folder chooser (opened in the folder in use), then models.folder is written; cancelling changes nothing.</summary>
    internal static void BrowseFolder(AppShell shell, Action<string> say)
    {
        if (shell.Dialogs.ChooseFolder(shell.ModelsDir) is not { } folder) return;
        say(shell.Models.SetModelsFolder(folder) ?? "");
    }

    /// <summary>models.folder goes back to the per-user default folder.</summary>
    internal static void UseDefaultFolder(AppShell shell, Action<string> say) => say(shell.Models.SetModelsFolder(null) ?? "");
}
