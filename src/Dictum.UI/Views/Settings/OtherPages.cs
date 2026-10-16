using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// Settings → Clean-up: what tidies the text now, the profanity setting, grammar assist, and cloud clean-up behind the
/// explicit consent dialog (SPEC §22): nothing is sent anywhere until the user agrees to the named host. The Service list
/// offers Anthropic and the presets in <see cref="CloudPresets"/>, which only fill in the fields below it.
/// </summary>
internal sealed class CleanupPage : SettingsPage
{
    private readonly TextBlock _now = Ui.Text("");
    private readonly Segments _profanity;
    private readonly ToggleSwitch _grammar;
    private readonly StackPanel _cloudOn = new() { Spacing = 10 };
    private readonly StackPanel _cloudOff = new() { Spacing = 10 };
    private readonly TextBlock _cloudState = Ui.Text("");
    private readonly ComboBox _provider = new() { ItemsSource = new[] { "Anthropic (Claude)" }.Concat(CloudPresets.All.Select(p => p.Name)).ToArray(), SelectedIndex = 0, Width = 320 };
    private readonly TextBox _baseUrl = new() { Width = 320 };
    private readonly TextBox _model = new() { Width = 320 };
    private readonly TextBlock _key = Ui.Text("", "caption");
    private readonly Button _enable;
    private readonly TextBlock _offline = Ui.Text("", "caption");

    public CleanupPage(AppShell shell) : base(shell, "Clean-up")
    {
        _profanity = new Segments(["Off", "Mask", "Remove"], 0, i => Write(("profanity.mode", Json(new[] { ProfanitySettings.Off, ProfanitySettings.Mask, ProfanitySettings.Remove }[i]))), "Profanity filter");
        _profanity.Width = 300;
        _profanity.HorizontalAlignment = HorizontalAlignment.Left;
        _grammar = Ui.Switch(false, on => Write(("llm.grammar_assist", Json(on))));
        _provider.SelectionChanged += (_, _) => DefaultsForProvider();
        _baseUrl.TextChanged += (_, _) => ShowAddressHints();
        DefaultsForProvider();
        _enable = Ui.Button("Use cloud clean-up…", "primary", EnableCloud);
        _cloudOff.Children.AddRange([
            Ui.Text("Optional. A cloud model can tidy longer dictations well, but the text of every dictation then goes to that service. Audio never leaves this PC."),
            Ui.Line("Service", _provider),
            Ui.Line("Server address", _baseUrl, "Leave empty for Anthropic. A plain http:// address must be on this PC."),
            Ui.Line("Model", _model),
            _key,
            Ui.Row(8, _enable, Ui.Button("Open the settings folder", "secondary", () => Say(Shell.OpenPath(Shell.RealPath(Shell.Paths.ConfigDir), folder: true) ?? ""))),
            _offline]);
        _cloudOn.Children.AddRange([_cloudState, Ui.Row(8, Ui.Button("Switch cloud clean-up off", "secondary", () => Run(() =>
        {
            Shell.DisableCloud();
            Say("Cloud clean-up is off; the local model tidies your dictations again.");
        })))]);

        Compose(
            Ui.Card(_now, "Now"),
            Ui.Card(Ui.Stack(8, _profanity,
                Ui.Text("Mask keeps the first letter (f***); Remove drops the word. The clean-up model keeps every word you said, swear words included: this setting is the only thing that changes them.", "caption")), "Profanity filter"),
            Ui.Card(Ui.Line("Grammar assist", _grammar, "For speakers whose first language is not English: also fixes articles, tenses, prepositions and word order, keeping every fact and name."), "Grammar"),
            Ui.Card(Ui.Stack(10, _cloudOn, _cloudOff), "Cloud clean-up"));
    }

    protected override void Redraw()
    {
        var s = Shell.Settings;
        _now.Text = Shell.CleanupStatus;
        _profanity.Select(Math.Max(0, Array.IndexOf([ProfanitySettings.Off, ProfanitySettings.Mask, ProfanitySettings.Remove], s.Profanity.Mode)));
        Ui.SetQuietly(_grammar, s.Llm.GrammarAssist);
        // On for anthropic or openai-compatible: a host off this PC the user agreed to, or a server on this PC (no consent step).
        var host = CloudPolicy.CloudHost(s.Llm);
        var server = CloudPolicy.LocalServerHost(s.Llm);
        var on = (host is not null && CloudPolicy.HasConsent(s.Llm)) || server is not null;
        _cloudOn.IsVisible = on;
        _cloudOff.IsVisible = !on;
        // The settings and the clean-up in use (both from the last load) decide the line, never the .env files as they are now.
        // Offline only refuses a host off this PC before the key is looked at, so at most one of the waiting and offline-only lines applies.
        // The last "On, but" line is for whatever else leaves nothing sent although cloud was agreed to, such as an address refused as insecure.
        _cloudState.Text = !on ? ""
            : server is not null ? CloudPolicy.LocalServerLine(server, s.Llm.Model)
            : Shell.CleanupWaitingForKey is { } waitingFor ? CloudPolicy.WaitingForKeyLine(waitingFor)
            : TranscriptCleaner.OfflineRefusal(s.Llm, s.OfflineOnly) is not null ? CloudPolicy.OfflineOnlyLine
            : Shell.NothingLeaves ? CloudPolicy.NothingSentLine
            : $"On: the text of each dictation is sent to {host} ({s.Llm.Provider}, model {s.Llm.Model}). You agreed to this host in the consent step. {CloudPolicy.StateLineWarning}";
        _enable.IsEnabled = !s.OfflineOnly;
        _offline.Text = s.OfflineOnly ? "Offline only is on (Privacy), so cloud clean-up cannot be switched on." : "";
    }

    /// <summary>The preset chosen in the Service list; null for Anthropic, which is its own provider.</summary>
    private CloudPreset? Preset => _provider.SelectedIndex > 0 ? CloudPresets.All[_provider.SelectedIndex - 1] : null;

    /// <summary>Fills the fields for the chosen service: its address, then the hints that follow it. Nothing is saved here.</summary>
    private void DefaultsForProvider()
    {
        var preset = Preset;
        _baseUrl.Text = preset?.BaseUrl;
        _model.Text = null;
        _baseUrl.Watermark = preset is null ? "(Anthropic's own address)" : preset.BaseUrl ?? "the server's OpenAI-compatible address";
        ShowAddressHints();
    }

    /// <summary>The address switching on would use: the one typed, else the chosen preset's own.</summary>
    private string? Address(CloudPreset? preset) => string.IsNullOrWhiteSpace(_baseUrl.Text) ? preset?.BaseUrl : _baseUrl.Text.Trim();

    /// <summary>
    /// The Model hint and the key line follow the address, not just the Service list: an address typed over a preset's is
    /// another server, which gets neither that preset's default model nor its key (<see cref="CloudPresets.ForAddress"/>).
    /// </summary>
    private void ShowAddressHints()
    {
        var preset = Preset;
        if (preset is null)
        {
            _model.Watermark = LlmSettings.AnthropicDefaultModel;
            _key.Text = $"The key goes in .env in the settings folder: {SecretStore.AnthropicKey}=your Anthropic API key.";
            return;
        }
        var address = Address(preset);
        _model.Watermark = CloudPresets.IsPresetAddress(preset, address) ? preset.DefaultModel ?? preset.ModelHint : "the model name the server uses";
        var service = CloudPresets.ForAddress(address);
        _key.Text = service.KeyVariable is null ? $"{service.Name} needs no key."
            : service.KeyRequired ? $"The key goes in .env in the settings folder: {service.KeyVariable}=your {service.Name} API key. Without it nothing is sent."
            : $"If the server needs a key, it goes in .env in the settings folder: {service.KeyVariable}=your key.";
    }

    /// <summary>A preset only filled the fields: switching on goes through the shell's consent step, as for a typed address.</summary>
    private void EnableCloud()
    {
        var preset = Preset;
        var provider = preset?.Provider ?? LlmSettings.Anthropic;
        var baseUrl = Address(preset);
        var model = !string.IsNullOrWhiteSpace(_model.Text) ? _model.Text.Trim()
            : preset is null ? LlmSettings.AnthropicDefaultModel
            : CloudPresets.DefaultModelFor(preset, baseUrl); // a typed-over address is another server: no preset default there
        if (model is null)
        {
            Say("Type the model name the server uses.");
            return;
        }
        if (provider == LlmSettings.OpenAiCompatible && baseUrl is null)
        {
            Say("Type the server address, for example http://localhost:11434/v1.");
            return;
        }
        Run(() => Say(Shell.EnableCloud(new LlmSettings { Provider = provider, BaseUrl = baseUrl, Model = model }) ?? "Cloud clean-up is on."));
    }
}

/// <summary>Settings → Privacy: what leaves the PC, offline only, clipboard history and transcripts in the log.</summary>
internal sealed class PrivacyPage : SettingsPage
{
    private readonly TextBlock _summary = Ui.Text("");
    private readonly ToggleSwitch _offline;
    private readonly ToggleSwitch _history;
    private readonly ToggleSwitch _transcripts;

    public PrivacyPage(AppShell shell) : base(shell, "Privacy")
    {
        _offline = Ui.Switch(false, on => Write(("offline_only", Json(on))));
        _history = Ui.Switch(true, on => Write(("insert.clipboard_history", Json(!on))));
        _transcripts = Ui.Switch(false, on => Write(("log_transcripts", Json(on))));
        Compose(
            Ui.Card(Ui.Stack(8, _summary,
                Ui.Text($"Models download from {ModelCatalog.DownloadHost} only when you choose one, and each file's size and SHA-256 are checked before use.", "caption")), "What leaves this PC"),
            Ui.Card(Ui.Stack(4,
                Ui.Line("Offline only", _offline, "No network at all: downloads and cloud clean-up are refused."),
                Ui.Line("Keep dictations out of clipboard history", _history, "Windows clipboard history (Win+V) and cloud clipboard sync."),
                Ui.Line("Write dictated text to the log", _transcripts, "Off by default. For troubleshooting; the log stays on this PC.")), "Settings"),
            Ui.Card(Ui.Stack(8,
                Ui.Text("The last dictation is held in memory for Fix last dictation and is gone when Dictum quits. Calibration recordings stay in your local Dictum folder until you delete them (Storage)."),
                Ui.Row(8, Ui.Button("Privacy policy", "link", () => Shell.Platform.Open(AppShell.PrivacyUrl)))), "Kept on this PC"));
    }

    protected override void Redraw()
    {
        var s = Shell.Settings;
        var server = CloudPolicy.LocalServerHost(s.Llm);
        // Cloud clean-up that is on but sends nothing says why first, decided as on the Clean-up page.
        _summary.Text = Shell.CleanupWaitingForKey is { } waitingFor ? CloudPolicy.WaitingForKeyLine(waitingFor)
            : TranscriptCleaner.OfflineRefusal(s.Llm, s.OfflineOnly) is not null ? CloudPolicy.OfflineOnlyLine
            : Shell.NothingLeaves ? "Nothing leaves this PC: speech recognition and clean-up both run here."
            : server is not null ? CloudPolicy.LocalServerLine(server, s.Llm.Model)
            : $"The text of each dictation goes to {CloudPolicy.CloudHost(s.Llm)} for clean-up, because you switched cloud clean-up on. Audio never leaves this PC. {CloudPolicy.StateLineWarning}";
        Ui.SetQuietly(_offline, s.OfflineOnly);
        Ui.SetQuietly(_history, !s.Insert.ClipboardHistory);
        Ui.SetQuietly(_transcripts, s.LogTranscripts);
    }
}

/// <summary>
/// Settings → About: version and runtime, Check for updates (decision 11: the direct download only; the Store build says
/// the Store updates it and has no button), Report a problem (SPEC §22), reload, and the files and folders.
/// </summary>
internal sealed class AboutPage : SettingsPage
{
    private readonly TextBlock _runtime = Ui.Text("");
    private readonly TextBlock _offline = new() { Text = "Offline only: Dictum connects to no other machine.", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _update = Ui.Text("");
    private readonly TextBlock _notes = Ui.Text("", "caption");
    private readonly Button _downloadPage;
    private bool _checking;

    public AboutPage(AppShell shell) : base(shell, "About")
    {
        var version = typeof(AboutPage).Assembly.GetName().Version?.ToString(3) ?? "?";
        var title = new TextBlock { Text = "Dictum", FontSize = 22, FontWeight = FontWeight.SemiBold };
        _downloadPage = Ui.Button(UpdateCheck.DownloadPageLabel, "primary", () => Run(() => Shell.Platform.Open(UpdateCheck.DownloadPageUrl)));
        _downloadPage.HorizontalAlignment = HorizontalAlignment.Left;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_update, Avalonia.Automation.AutomationLiveSetting.Polite);
        ShowUpdate(null);
        Control updates;
        if (Shell.Platform.Package.IsPackaged)
        {
            updates = Ui.Text(UpdateCheck.StoreLine); // the sentence alone: no button, so the Store build can never check
        }
        else
        {
            var check = Ui.Button(UpdateCheck.ButtonLabel, "secondary", () => _ = CheckForUpdatesAsync());
            var explainer = Ui.Text(UpdateCheck.PrivacyLine, "caption");
            explainer.VerticalAlignment = VerticalAlignment.Center;
            Avalonia.Automation.AutomationProperties.SetHelpText(check, UpdateCheck.PrivacyLine); // a screen reader says it with the button
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 }; // the explainer wraps beside the button
            Grid.SetColumn(explainer, 1);
            row.Children.AddRange([check, explainer]);
            updates = Ui.Stack(8, row, _update, _downloadPage, _notes);
        }
        Compose(
            Ui.Card(Ui.Stack(10,
                Ui.Row(14, new MarkIcon(48), Ui.Stack(2, title, Ui.Text($"Version {version} · Kynni Pty Ltd · MIT licence", "caption"))),
                _offline, _runtime, updates), "Dictum"),
            Ui.Card(Ui.Stack(10,
                Ui.Text("Report a problem opens an email to " + AppShell.SupportEmail + " in your own mail app, with the version and models filled in. " +
                        "Dictum sends nothing itself and never attaches what you dictated."),
                Ui.Row(8,
                    Ui.Button("Report a problem", "primary", () => Run(Shell.ReportProblem)),
                    Ui.Button("GitHub issues", "secondary", () => Run(() => Shell.Platform.Open(AppShell.IssuesUrl))),
                    Ui.Button("Privacy policy", "secondary", () => Run(() => Shell.Platform.Open(AppShell.PrivacyUrl))))), "Help"),
            Ui.Card(Ui.Stack(10,
                Ui.Text("settings.json is the source of truth: every change here is written to it, and an edit there is picked up within a second. " +
                        "Reload settings also re-installs the hotkey if Windows removed it."),
                Ui.Row(8,
                    Ui.Button("Reload settings", "secondary", () => Run(() =>
                    {
                        Shell.Reload(userRequested: true);
                        Say("Settings reloaded.");
                    })),
                    Ui.Button("Open settings file", "secondary", () => Say(Shell.OpenSettingsFile() ?? "")),
                    Ui.Button("Open log folder", "secondary", () => Say(Shell.OpenPath(Shell.RealPath(Shell.Paths.LogsDir), folder: true) ?? "")))), "Files"));
    }

    protected override void Redraw()
    {
        var packaged = Shell.Platform.Package.IsPackaged ? "installed from a package" : "installed from the download";
        _runtime.Text = $"On {Shell.Platform.Name}, {packaged}.\nModels: {Shell.Status.Models}.\nClean-up: {Shell.CleanupStatus}.";
        _offline.IsVisible = Shell.Settings.OfflineOnly; // SPEC §5: the About box says "Offline only"
    }

    /// <summary>
    /// One check per press; a press while one is running is ignored, so it is still one request. The press clears the last
    /// result first, so it visibly did something and the line that follows is this check's.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_checking) return;
        _checking = true;
        ShowUpdate(null);
        try
        {
            ShowUpdate(await Shell.CheckForUpdatesAsync());
        }
        catch (Exception ex)
        {
            Shell.Log.Error($"Check for updates: {ex.GetType().Name}: {ex.Message}");
            ShowUpdate(new UpdateCheckResult(UpdateStatus.Failed));
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>The result line; for a newer version also the download-page button and, under it, the release notes.</summary>
    private void ShowUpdate(UpdateCheckResult? result)
    {
        _update.Text = result?.Line ?? "";
        _update.IsVisible = result is not null;
        var notes = result is { Status: UpdateStatus.NewerAvailable } ? result.Notes : null;
        _downloadPage.IsVisible = result is { Status: UpdateStatus.NewerAvailable };
        _notes.Text = notes ?? "";
        _notes.IsVisible = notes is not null;
    }
}
