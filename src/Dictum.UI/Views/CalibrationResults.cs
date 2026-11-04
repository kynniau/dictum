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
    /// Use this: writes the combination's model and decoding, and says what that means. The settings are read back through the same
    /// questions the "In use" mark asks (<see cref="CalibrationHost.InUse"/>), so a line is only said when this is the model the app
    /// holds and its load has not failed: a refusal, a failed write and a load that already failed each say that instead.
    /// When it takes effect (verified in ModelCoordinator.TryWrite, AppShell.Reload and ModelManager.SetDecoding, 04/11/2026): at
    /// once, with no restart. A new model loads in the background and the hotkey is ignored until it has (AppShell: hotkeyBlocked is
    /// "not ready"); a changed decoding alone rebuilds the decoder in the background and loads no model. A load that fails after
    /// the click is caught when it finishes (<see cref="OnModelStateChanged"/>).
    /// </summary>
    private void Use(ComboResult combo)
    {
        if (_host.InUse(combo)) return; // already what dictation uses: nothing to write, and the button says so
        if (_host.Refusal() is { } refused) // model files are moving: nothing is loaded now, so nothing is claimed
        {
            _progress.Text = refused;
            return;
        }
        // Before the write: a changed model loads and a changed decoding alone does not, but the same model still loading (an earlier
        // press) keeps the hotkey ignored until it has, so that too is waiting for a load.
        var loads = !_host.ModelInUse(combo.Model) || _host.SpeechReady?.Invoke() == false;
        if (!_host.Write([("whisper.model", JsonValue.Create(combo.Model)), ("whisper.beam_size", JsonValue.Create(combo.BeamSize))]))
        {
            _progress.Text = ModelCoordinator.WriteFailed; // the tray's balloon says it too; nothing was written or reloaded
            return;
        }
        ShowInUse(); // the same buttons, so the one pressed keeps the keyboard focus
        Say(combo, loads);
    }

    /// <summary>What Use this says once the write went through: the tray's text if the load has already failed, else the line (if the app holds this model).</summary>
    private void Say(ComboResult combo, bool loads)
    {
        if (_host.SpeechLoadProblem?.Invoke() is { } problem) // a load has failed: the tray's own text, and no claim, but only for the model it was
        {
            // The failure is the app's latest one, not this row's: pressing a model whose file is gone starts no load (Ensure only
            // balloons), so base.en's old failure must not be reported against it.
            if (_host.HoldsModel(combo.Model)) _progress.Text = problem;
            return;
        }
        if (!_host.InUse(combo)) return;
        _progress.Text = UsingLine(combo, loads, _host.CloudSpeechHost?.Invoke());
        _claim = (combo, _progress.Text);
    }

    /// <summary>The line Use this said, while it is still the status line: a load that fails after it was said replaces it.</summary>
    private (ComboResult Combo, string Line)? _claim;

    /// <summary>
    /// The app's model state changed (a load started, finished or failed: AppShell.Changed, subscribed while the window is open).
    /// The marks follow it; and a success line still showing for a load that has now failed is replaced by the tray's own text for
    /// that failure, because the line was said when the click came, before the background load had ended.
    /// </summary>
    private void OnModelStateChanged()
    {
        ShowInUse();
        if (_claim is not { } claim || _progress.Text != claim.Line || _host.SpeechLoadProblem?.Invoke() is not { } problem) return;
        _progress.Text = problem;
        _claim = null;
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
    /// ("small.en with greedy (recommended): in use"). APPROVED 04/11/2026 22:49 AEDT (owner): do not reword (approve-calibration-use.md row 4).
    /// </summary>
    private static string UseName(ComboResult combo, bool best, bool inUse)
    {
        var what = $"{combo.Model} with {combo.Decoding}{(best ? " (recommended)" : "")}";
        return inUse ? $"{what}: in use" : $"Use {what}";
    }

    /// <summary>
    /// The status line after Use this took effect. approve-calibration-use.md rows 1 to 3. Local, a new model or one still loading ("loads": the hotkey is ignored until it
    /// has) and local, the same model with other decoding (nothing loads), are APPROVED 04/11/2026 22:49 AEDT (owner): do not reword.
    /// Cloud speech on, where the audio goes to <paramref name="cloudHost"/> and the model here is only the fallback, so no claim that
    /// it hears dictation, is APPROVED 04/11/2026 22:48 AEDT (row 3, below).
    /// </summary>
    internal static string UsingLine(ComboResult combo, bool loads, string? cloudHost)
    {
        var model = $"{combo.Model} ({combo.Decoding})";
        // APPROVED 04/11/2026 22:48 AEDT (owner; GPT 6.1 sol's wording from the two-model check, .demo-preview/two-model-calibration-cloud-line.md): do not reword.
        if (cloudHost is not null)
            return $"Saved: {model} is now the fallback. Cloud speech is on, so each recording is sent to {cloudHost} to turn your voice into words; Dictum uses this model only if the cloud request fails.";
        return loads ? $"Dictum now uses {model} from the next dictation, once the model has loaded." : $"Dictum now uses {model} from the next dictation."; // APPROVED 04/11/2026 22:49 AEDT (owner): do not reword (rows 1 and 2)
    }

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
