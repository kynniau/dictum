using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dictum.Help;
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
            Say(Shell.DisableCloud() ?? "Cloud clean-up is off; the local model tidies your dictations again."))))]);

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
        // A server on this PC tidies the text only while the clean-up in use asks it (NothingLeaves is false then): text that is not
        // English (SPEC §23) gets the plain cleaner, which asks no server, so it falls through to the last "On, but" line below.
        // Offline only refuses a host off this PC before the key is looked at, so at most one of the waiting and offline-only lines applies.
        // The last "On, but" line is for whatever else leaves nothing sent although cloud was agreed to, such as an address refused as insecure,
        // or text that is not English, where switching offline only off would send nothing (OfflineOnlyIsWhyNothingIsSent).
        _cloudState.Text = !on ? ""
            : server is not null && !Shell.NothingLeaves ? CloudPolicy.LocalServerLine(server, s.Llm.Model)
            : Shell.CleanupWaitingForKey is { } waitingFor ? CloudPolicy.WaitingForKeyLine(waitingFor)
            : OfflineOnlyIsWhyNothingIsSent(Shell) ? CloudPolicy.OfflineOnlyLine
            : Shell.NothingLeaves ? CloudPolicy.NothingSentLine
            : $"On: the text of each dictation is sent to {host} ({s.Llm.Provider}, model {s.Llm.Model}). You agreed to this host in the consent step. {CloudPolicy.StateLineWarning}";
        _enable.IsEnabled = !s.OfflineOnly;
        _offline.Text = s.OfflineOnly ? "Offline only is on (Privacy), so cloud clean-up cannot be switched on." : "";
    }

    /// <summary>
    /// True when offline only is why cloud clean-up sends nothing (<see cref="TranscriptCleaner.OfflineRefusal"/>) and switching it
    /// off would change that. Not while the clean-up in use is the plain cleaner (text that is not English, SPEC §23): it never asks a
    /// provider, so nothing would be sent either way, and the pages say that the clean-up in use runs on this PC. Settings > Privacy
    /// decides the same way.
    /// </summary>
    internal static bool OfflineOnlyIsWhyNothingIsSent(AppShell shell) =>
        !shell.Cleaner.IsPlain && TranscriptCleaner.OfflineRefusal(shell.Settings.Llm, shell.Settings.OfflineOnly) is not null;

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
        _offline = Ui.Switch(false, on =>
        {
            if (!Refreshing) Run(() => Say(Shell.SetOfflineOnly(on) ?? "")); // on even when it cannot be saved; the switch follows the settings in use
        });
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
            : CleanupPage.OfflineOnlyIsWhyNothingIsSent(Shell) ? CloudPolicy.OfflineOnlyLine
            : Shell.NothingLeaves ? "Nothing leaves this PC: speech recognition and clean-up both run here."
            : server is not null ? CloudPolicy.LocalServerLine(server, s.Llm.Model)
            : $"The text of each dictation goes to {CloudPolicy.CloudHost(s.Llm)} for clean-up, because you switched cloud clean-up on. Audio never leaves this PC. {CloudPolicy.StateLineWarning}";
        Ui.SetQuietly(_offline, s.OfflineOnly);
        Ui.SetQuietly(_history, !s.Insert.ClipboardHistory);
        Ui.SetQuietly(_transcripts, s.LogTranscripts);
    }
}

/// <summary>
/// Settings → About: version and runtime, any model problem (with Try on the CPU; Open log folder is under Files), Check for
/// updates (decision 11: the direct download only; the Store build says
/// the Store updates it and has no button), Report a problem (SPEC §22), reload, and the files and folders.
/// </summary>
internal sealed class AboutPage : SettingsPage
{
    private readonly TextBlock _runtime = Ui.Text("");
    private readonly TextBlock _problem = Ui.Text("");
    private readonly Button _tryCpu;
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
        _tryCpu = Ui.Button(AppShell.TryOnCpuLabel, "secondary", () =>
        {
            // The reload hides this button and the problem line while the model loads: keyboard focus goes first where Tab would
            // take it (Check for updates, or Report a problem in the Store build), a control that stays and shows its focus ring.
            if (_tryCpu!.IsFocused && KeyboardNavigationHandler.GetNext(_tryCpu, NavigationDirection.Next) is InputElement next) next.Focus(NavigationMethod.Tab);
            Run(() => Shell.TryOnCpu());
        });
        _tryCpu.HorizontalAlignment = HorizontalAlignment.Left;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_problem, Avalonia.Automation.AutomationLiveSetting.Polite);
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
                _offline, _runtime, _problem, _tryCpu, updates), "Dictum"),
            Ui.Card(Ui.Stack(10,
                Ui.Text("Report a problem opens an email to " + AppShell.SupportEmail + " in your own mail app, with the version and models filled in. " +
                        "Dictum sends nothing itself and never attaches what you dictated."),
                Ui.Row(8,
                    Ui.Button("Report a problem", "primary", () => Run(Shell.ReportProblem)),
                    Ui.Button("GitHub issues", "secondary", () => Run(() => Shell.Platform.Open(AppShell.IssuesUrl))),
                    Ui.Button("Privacy policy", "secondary", () => Run(() => Shell.Platform.Open(AppShell.PrivacyUrl))))), "Support"),
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
        var status = Shell.Status;
        // The clean-up in use says "on this PC" while its model failed to load; the tidy is what runs then. Text that is not
        // English has the plain cleaner (SPEC §23), whose own line already says what runs.
        var cleanup = Shell.Settings.Llm.Provider == LlmSettings.Local && Shell.CleanupError is not null && !Shell.CleanupLoading && !Shell.Cleaner.IsPlain
            ? CleanupFailedLine(Shell.Models.CurrentCleanupName)
            : $"Clean-up: {Shell.CleanupStatus}.";
        _runtime.Text = $"On {Shell.Platform.Name}, {packaged}.\nModels: {status.Models}.\n{cleanup}";
        _problem.Text = status.Problem ?? "";
        _problem.IsVisible = status.Problem is not null;
        _tryCpu.IsVisible = status.TryCpu;
        _offline.IsVisible = Shell.Settings.OfflineOnly; // SPEC §5: the About box says "Offline only"
    }

    /// <summary>The clean-up line while the local model failed to load. PROPOSED copy (17/10/2026), awaiting the product owner's review: do not reword.</summary>
    internal static string CleanupFailedLine(string model) => $"Clean-up: {model} could not be loaded, so the rule-based tidy is used.";

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

/// <summary>
/// Settings → Help (docs/DECISIONS.md, "In-app Help", 21/10/2026): "How Dictum works" and "Questions and answers", all from
/// <see cref="HelpText"/>, so it reads the same with no internet. The page makes no network request: its two buttons hand a
/// fixed address to the system (<see cref="Dictum.Platform.IPlatform.Open"/>), whose browser or mail app does the rest.
/// For the keyboard and screen readers, the headings carry heading levels, and each block of text (each question with its
/// answer) is a tab stop named after its heading, scrolled into view when focused, with a ring while it has keyboard focus;
/// with focus there, Page Up and Page Down scroll the page.
/// </summary>
internal sealed class HelpPage : SettingsPage
{
    public HelpPage(AppShell shell) : base(shell, HelpText.PageTitle)
    {
        var guide = Ui.Button(HelpText.GuideButton, "primary", () => Run(() => Shell.Platform.Open(HelpText.GuideUrl)));
        var support = Ui.Button(HelpText.SupportButton, "secondary", () => Run(Shell.ReportProblem)); // the prefilled email About's Report a problem opens
        var feedback = Ui.Button(HelpText.FeedbackButton, "secondary", () => Run(Shell.SendFeedback));
        AutomationProperties.SetHelpText(guide, HelpText.GuideNote); // a screen reader says it with the button
        AutomationProperties.SetHelpText(support, HelpText.SupportNote);
        AutomationProperties.SetHelpText(feedback, HelpText.FeedbackNote);
        var answers = HelpText.Questions.Select(q => Readable(q.Question, Heading(q.Question, 3, 15), [.. q.Answer.Select(Paragraph)])).ToList();
        // The questions first, each a link to its answer (1.2 polish, 22/10/2026: one long page with no list of what it answers).
        var index = Section(HelpText.IndexHeading, [.. HelpText.Questions.Select((q, i) => JumpLink(q.Question, answers[i]))]);
        Compose(
            index,
            Section(HelpText.MoreHelpHeading, Paragraph(HelpText.GuideNote), Ui.Row(8, guide), Paragraph(HelpText.SupportNote), Paragraph(HelpText.FeedbackNote), Ui.Row(8, support, feedback)),
            Section(HelpText.HowItWorksHeading,
                Readable(HelpText.HowItWorksHeading, null, [.. HelpText.HowItWorks.Select(Paragraph)]),
                Readable(HelpText.AccuracyHeading, Heading(HelpText.AccuracyHeading, 3, 15), [.. HelpText.Accuracy.Select(Paragraph)])),
            Section(HelpText.QuestionsHeading, [.. answers]));
        if (Content is Panel { Children: [TextBlock title, ..] }) AutomationProperties.SetHeadingLevel(title, 1); // the page title Compose put first
    }

    /// <summary>
    /// A question in the list at the top: a link button (a tab stop with the theme's focus ring) whose label wraps, so a long
    /// question stays inside a narrow window. Pressing it moves keyboard focus to the answer, which, as with Tab, shows its ring,
    /// and scrolls the page so the answer's heading is at the top (<see cref="ScrollToTop"/>).
    /// </summary>
    private static Button JumpLink(string question, Border answer)
    {
        var link = Ui.Button("", "link", () =>
        {
            answer.Focus(NavigationMethod.Tab);
            ScrollToTop(answer);
        });
        link.Content = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        link.HorizontalContentAlignment = HorizontalAlignment.Left;
        link.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(link, question);
        AutomationProperties.SetHelpText(link, HelpText.IndexLinkHelp);
        return link;
    }

    /// <summary>Space left above an answer scrolled to the top, so its focus ring is not on the window's edge.</summary>
    internal const double JumpGap = 8;

    /// <summary>
    /// Scrolls the page so <paramref name="answer"/> starts <see cref="JumpGap"/> below the top of the view (as far as the page
    /// goes: the last answers stop where the page ends). Focus alone scrolled only as far as needed, which left the answer at
    /// the bottom edge with its last line cut off (1.2 polish round 2).
    /// </summary>
    private static void ScrollToTop(Control answer)
    {
        // Measured against the scrolled content, not the view: focus has already moved the offset, and the view's coordinates
        // follow it only at the next layout pass.
        if (answer.FindAncestorOfType<ScrollViewer>() is not { Content: Visual content } scroll || answer.TranslatePoint(default, content) is not { } at) return;
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(at.Y - JumpGap, 0, max));
    }

    /// <summary>Nothing on the page depends on the settings.</summary>
    protected override void Redraw()
    {
    }

    /// <summary>A card under a level-2 heading.</summary>
    private static Border Section(string heading, params Control[] children) =>
        Ui.Card(Ui.Stack(10, [Heading(heading, 2, 16), .. children]));

    private static TextBlock Heading(string text, int level, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(block, level);
        return block;
    }

    /// <summary>Help text a step larger than the other pages' body text (14 against 13), for reading rather than glancing.</summary>
    private static TextBlock Paragraph(string text)
    {
        var block = Ui.Text(text);
        block.FontSize = 14;
        block.LineHeight = 21;
        return block;
    }

    /// <summary>
    /// A block of text the keyboard can reach: a tab stop named <paramref name="name"/>, scrolled into view when focused,
    /// that shows a ring (the link colour, light or dark) only while focus came from the keyboard.
    /// </summary>
    private static Border Readable(string name, TextBlock? heading, Control[] body)
    {
        var block = new Border
        {
            Focusable = true,
            IsTabStop = true,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6),
            Child = Ui.Stack(6, heading is null ? body : [heading, .. body]),
        };
        AutomationProperties.SetName(block, name);
        block.GotFocus += (_, e) =>
        {
            // Avalonia scrolls a focused control into view itself (observed in HelpPageTests), so the ring is all this adds.
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
                block[!Border.BorderBrushProperty] = new DynamicResourceExtension("LinkBrush");
        };
        block.LostFocus += (_, _) =>
        {
            block.ClearValue(Border.BorderBrushProperty); // drops the resource binding the ring set
            block.BorderBrush = Brushes.Transparent;
        };
        return block;
    }
}
