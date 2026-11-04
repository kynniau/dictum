using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Calibration;
using Dictum.Personal;
using Dictum.UI.Views.Settings;

namespace Dictum.UI.Views;

/// <summary>The calibration window's lower half: the test run, the results table, the models not installed, the suggestions.</summary>
internal sealed partial class CalibrationWindow
{
    private readonly Grid ResultsGrid = new() { ColumnDefinitions = new ColumnDefinitions("1.6*,1.2*,0.7*,Auto"), ColumnSpacing = 10, RowSpacing = 6 };
    private readonly StackPanel MissingList = new() { Spacing = 6 };
    private readonly List<(ComboResult Combo, Button Use, bool Best)> _useButtons = []; // the buttons RenderResults drew, for ShowInUse
    private readonly StackPanel SuggestionList = new() { Spacing = 4 };

    private async Task RunAsync(IReadOnlyList<SpeechCandidate> models)
    {
        if (!IsVisible) return; // a download finished after the window was closed
        if (!_session.CanRun)
        {
            _progress.Text = _session.Recording != 0 ? "Stop the recording first." : "A test is already running; this model can be included when it has finished (Run test).";
            return;
        }
        if (_host.Refusal() is { } refused) // model files are moving (re-lens on PR #61): said before anything else is checked
        {
            _progress.Text = refused;
            return;
        }
        var clips = _recordings.LoadClips();
        if (clips.Count == 0)
        {
            _progress.Text = "Record at least one sentence first (all ten give the most reliable result).";
            return;
        }
        if (models.Count == 0)
        {
            _progress.Text = "No speech model is installed in the models folder.";
            return;
        }
        _runCts = new CancellationTokenSource();
        SetRunning(true);
        try
        {
            var progress = new Progress<string>(line => _progress.Text = line);
            var clipSet = _recordings.ClipSet();
            var finished = new Progress<ComboResult>(combo =>
            {
                _session.Add(combo, clipSet); // kept even if the test is cancelled later
                RenderResults();
            });
            await _host.CreateEngine().RunAsync(clips, models, _host.Personal().Dictionary.WhisperPrompt, progress, _runCts.Token, finished);
            _progress.Text = $"Tested {models.Count} model(s) on {clips.Count} recording(s).";
        }
        catch (OperationCanceledException)
        {
            _progress.Text = "Test cancelled; the results so far are kept.";
        }
        catch (Exception ex)
        {
            _host.Log.Error($"Calibration failed: {ex.GetType().Name}: {ex.Message}");
            _progress.Text = $"The test failed: {ex.Message}";
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetRunning(false);
            RenderResults();
            RenderMissing();
            RenderSuggestions();
        }
    }

    private void SetRunning(bool running)
    {
        _session.SetRunning(running);
        RefreshTake();
    }

    private void RenderResults()
    {
        ResultsGrid.Children.Clear();
        ResultsGrid.RowDefinitions.Clear();
        _useButtons.Clear();
        if (_session.Results.Count == 0)
        {
            ResultsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ResultsGrid.Children.Add(Ui.Text("No results yet: record the sentences, then Run test.", "caption"));
            Grid.SetColumnSpan(ResultsGrid.Children[0], 4);
            return;
        }
        var recommended = CalibrationRanking.Recommend(_session.Results);
        AddCells(0, [Ui.Text("Model", "hint"), Ui.Text("Errors", "hint"), Ui.Text("Per clip", "hint")]);
        var row = 1;
        foreach (var combo in _session.Results.OrderBy(r => r.Wer).ThenBy(r => r.AverageSeconds))
        {
            var best = ReferenceEquals(combo, recommended);
            var slow = !CalibrationRanking.WithinCeiling(combo);
            var name = Ui.Text($"{combo.Model} · {combo.Decoding}", slow ? "caption" : "body");
            if (best) name.FontWeight = FontWeight.SemiBold;
            var errors = Ui.Text(combo.ErrorsText, slow ? "caption" : "body");
            var time = Ui.Text(slow ? $"{combo.AverageSeconds:0.0} s (too slow here)" : $"{combo.AverageSeconds:0.0} s", slow ? "caption" : "body");
            var use = Ui.Button(UseLabel, best ? "primary" : "secondary", () => Use(combo), Failed, name: UseName(combo, best, inUse: false));
            use.MinHeight = 30;
            _useButtons.Add((combo, use, best));
            AddCells(row++, [name, errors, time, use]);
        }
        ShowInUse();
    }

    /// <summary>
    /// Use this: writes the combination's model and decoding, and says it took effect. The settings file is read back through the
    /// same question the "In use" mark asks (<see cref="CalibrationHost.InUse"/>), so the line is only said when the model is the
    /// one loaded or loading: a refusal, a failed write and a model that could not be switched to each say nothing of the kind.
    /// When it takes effect (verified in ModelCoordinator.TryWrite, AppShell.Reload and ModelManager.SetDecoding, 04/11/2026): at
    /// once, with no restart. The new model loads in the background and the hotkey is ignored until it has (AppShell: hotkeyBlocked
    /// is "not ready"); a changed decoding rebuilds the decoder in the background.
    /// </summary>
    private void Use(ComboResult combo)
    {
        if (_host.InUse(combo)) return; // already what dictation uses: nothing to write, and the button says so
        if (_host.Refusal() is { } refused) // model files are moving: nothing is loaded now, so nothing is claimed
        {
            _progress.Text = refused;
            return;
        }
        if (!_host.Write([("whisper.model", JsonValue.Create(combo.Model)), ("whisper.beam_size", JsonValue.Create(combo.BeamSize))]))
        {
            _progress.Text = ModelCoordinator.WriteFailed; // the tray's balloon says it too; nothing was written or reloaded
            return;
        }
        ShowInUse(); // the same buttons, so the one pressed keeps the keyboard focus
        if (_host.InUse(combo)) _progress.Text = UsingLine(combo);
    }

    /// <summary>
    /// Marks the row in use (<see cref="CalibrationHost.InUse"/>): its button reads "In use" (Settings > Models' label) and its
    /// name says so; the others read "Use this". The buttons are changed, not rebuilt, so keyboard focus stays on the one pressed.
    /// </summary>
    private void ShowInUse()
    {
        foreach (var (combo, use, best) in _useButtons)
        {
            var inUse = _host.InUse(combo);
            use.Content = inUse ? ModelsPage.InUseLabel : UseLabel;
            AutomationProperties.SetName(use, UseName(combo, best, inUse));
        }
    }

    private const string UseLabel = "Use this";

    /// <summary>
    /// The button's name: as it always was ("Use small.en with greedy (recommended)"); on the row in use the state replaces the verb
    /// ("small.en with greedy (recommended): in use"). PROPOSED (not yet approved) 04/11/2026: approve-calibration-use.md row 2.
    /// </summary>
    private static string UseName(ComboResult combo, bool best, bool inUse)
    {
        var what = $"{combo.Model} with {combo.Decoding}{(best ? " (recommended)" : "")}";
        return inUse ? $"{what}: in use" : $"Use {what}";
    }

    /// <summary>The status line after Use this took effect. PROPOSED (not yet approved) 04/11/2026: approve-calibration-use.md row 1.</summary>
    internal static string UsingLine(ComboResult combo) => $"Dictum now uses {combo.Model} ({combo.Decoding}) from the next dictation, once the model has loaded.";

    private void AddCells(int row, Control[] cells)
    {
        ResultsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < cells.Length; c++)
        {
            cells[c].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(cells[c], row);
            Grid.SetColumn(cells[c], c);
            ResultsGrid.Children.Add(cells[c]);
        }
    }

    private void RenderMissing()
    {
        MissingList.Children.Clear();
        foreach (var info in _host.MissingModels())
        {
            var name = info.Name;
            var download = Ui.Button(_session.IsDownloading(name) ? "Downloading…" : "Download and include", "secondary", () =>
            {
                // The button and the line under the results change only once the download is agreed (Mac beta, 23/10/2026: they
                // said "Downloading…" while the question was still open). A declined question changes nothing here; a refused
                // download (offline_only, no space) still says it was not downloaded, beside the coordinator's balloon.
                var declined = false;
                _host.DownloadSpeechModel(name,
                    goesAhead =>
                    {
                        declined = !goesAhead;
                        if (declined) return;
                        _session.StartDownload(name); // one click per model: a second click used to cancel the download in progress and lose its bytes
                        RenderMissing(); // from the session, so a list redrawn while the question was open (a test run ended) is right too
                        _progress.Text = $"Downloading {name}: progress shows in the tray panel. It joins the test when it arrives.";
                    },
                    () => Dispatcher.UIThread.Post(() => _ = RunAsync(_session.ModelsToRun(_host.InstalledModels(), name, _recordings.ClipSet()))),
                    ok => Dispatcher.UIThread.Post(() =>
                    {
                        if (declined) return;
                        _session.EndDownload(name);
                        RenderMissing();
                        if (!ok) _progress.Text = $"{name} was not downloaded, so it was not added to the test.";
                    }));
            }, Failed, name: $"Download {name} and include it in the test");
            download.IsEnabled = !_session.IsDownloading(name);
            download.MinHeight = 30;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            grid.Children.Add(Ui.Text($"{info.Name} · {info.SizeText}", "caption"));
            Grid.SetColumn(download, 1);
            grid.Children.Add(download);
            MissingList.Children.Add(grid);
        }
        if (MissingList.Children.Count == 0) MissingList.Children.Add(Ui.Text("Every catalogue model is installed.", "caption"));
    }

    private void RenderSuggestions()
    {
        SuggestionList.Children.Clear();
        var boxes = new List<Control>();
        var recommended = CalibrationRanking.Recommend(_session.Results);
        if (recommended is not null)
        {
            var personal = _host.Personal();
            foreach (var s in MissedWords.Find(recommended, _recordings.Script, personal.Dictionary, personal.Corrections))
            {
                boxes.Add(Tick("Add to dictionary:", s.Word, s.Word));
                if (s.Correction is { } rule) boxes.Add(Tick("Add correction:", $"{rule.Heard} → {rule.Written}", rule));
            }
        }
        var grammar = new CheckBox { Content = "Grammar assist for this user", IsChecked = _host.Settings().Llm.GrammarAssist };
        ToolTip.SetTip(grammar, "For speakers whose first language is not English: the clean-up also fixes articles, tenses, prepositions and word order, keeping every fact and name.");
        grammar.IsCheckedChanged += (_, _) => WriteGrammar(grammar);
        boxes.Add(grammar);
        var note = recommended is null
            ? Ui.Text(_session.Results.Count == 0 ? "Run the test to see suggestions." : "No combination is under the latency ceiling, so there is nothing to base suggestions on.", "caption")
            : boxes.Count == 1 ? Ui.Text($"{recommended.Model} ({recommended.Decoding}) missed no word consistently.", "caption") : null;
        if (note is not null) SuggestionList.Children.Add(note);
        SuggestionList.Children.Add(Ui.Columns(2, 6, [.. boxes]));
    }

    private static CheckBox Tick(string what, string value, object tag)
    {
        var box = new CheckBox { Content = Ui.Row(6, new TextBlock { Text = what }, new TextBlock { Text = value, FontWeight = FontWeight.SemiBold }), Tag = tag, IsChecked = false, MinHeight = 32 };
        AutomationProperties.SetName(box, $"{what} {value}");
        return box;
    }

    private void AddTicked()
    {
        var ticked = SuggestionList.GetLogicalDescendants().OfType<CheckBox>().Where(c => c.Tag is not null && c.IsChecked == true && c.IsEnabled).ToList();
        var words = ticked.Select(c => c.Tag).OfType<string>().ToList();
        var rules = ticked.Select(c => c.Tag).OfType<CorrectionRule>().ToList();
        if (words.Count > 0) _host.AddDictionaryTerms(words);
        if (rules.Count > 0) _host.AddCorrections(rules);
        foreach (var c in ticked) c.IsEnabled = false;
        _progress.Text = ticked.Count == 0 ? "Nothing ticked, so nothing was added." : $"Added {words.Count} dictionary word(s) and {rules.Count} correction(s).";
    }
}
