using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Personal;
using Dictum.UI.Shell;

namespace Dictum.UI.Views;

/// <summary>
/// Fix last dictation (SPEC §16.3, docs/design/FixLast.dc.html): a small always-on-top editor holding the last pasted text,
/// opened on purpose (tray panel or fix_hotkey), so it takes focus. As the text is edited, each replaced word or phrase is
/// listed as an unticked "Add correction: heard → written" (a number written differently is marked as probably a one-off);
/// Save corrections writes only the ticked ones. Copy, and Paste fix into the window the dictation went to.
/// </summary>
internal sealed class FixLastWindow : Window
{
    private readonly AppShell _shell;
    private readonly LastDictation _last;
    private readonly TextBox _text;
    private readonly StackPanel _changes = new() { Spacing = 2 };
    private readonly Border _changesCard;
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly TextBlock _when = Ui.Text("", "caption");
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _clock;
    private readonly HashSet<string> _ticked = [];
    private readonly HashSet<string> _saved = [];

    public FixLastWindow(AppShell shell, LastDictation last)
    {
        _shell = shell;
        _last = last;
        Title = "Dictum: fix last dictation";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _text = new TextBox { Text = last.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 260, FontSize = 14 };
        AutomationProperties.SetName(_text, "Last dictation");
        _changesCard = Ui.Card(_changes, "Changes found");
        _changesCard.IsVisible = false;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => FindChanges()) { IsEnabled = false };
        _text.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => ShowWhen());

        var header = new Border { Padding = new Thickness(16, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = Ui.Row(10, new MarkIcon(20), new TextBlock { Text = "Fix last dictation", FontSize = 13, FontWeight = FontWeight.SemiBold }, _when) };
        header.Classes.Add("headerbar");
        var copy = Ui.Button("Copy", "secondary", () =>
        {
            _shell.CopyText(_text.Text ?? "");
            _status.Text = "Copied to the clipboard.";
        }, Failed);
        var paste = Ui.Button("Paste fix", "secondary", () => _ = PasteAsync(), Failed);
        var save = Ui.Button("Save corrections", "primary", SaveTicked, Failed);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        Grid.SetColumn(paste, 1);
        Grid.SetColumn(save, 3);
        footer.Children.AddRange([copy, paste, save]);
        var footerBar = new Border { Padding = new Thickness(20, 10), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        footerBar.Classes.Add("headerbar");

        Content = new DockPanel
        {
            Children =
            {
                Dock(header, Avalonia.Controls.Dock.Top),
                Dock(footerBar, Avalonia.Controls.Dock.Bottom),
                new Border
                {
                    Padding = new Thickness(20, 16),
                    Child = Ui.Stack(12, Ui.Text("Edit the text, then save the words you changed as corrections, or paste the fix where the dictation went (select the wrong text there first to replace it).", "caption"),
                        _text, _changesCard, _status),
                },
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        // Keyboard focus goes to the text once the opener has activated the window, never before (focusing an element of an
        // inactive window would activate it).
        EventHandler? first = null;
        first = (_, _) =>
        {
            Activated -= first;
            _text.Focus();
            _text.CaretIndex = _text.Text?.Length ?? 0;
        };
        Activated += first;
        Opened += (_, _) => _clock.Start();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _debounce.Stop();
        };
        ShowWhen();
    }

    private void ShowWhen()
    {
        var ago = DateTime.UtcNow - _last.PastedAtUtc;
        var when = ago.TotalSeconds < 60 ? $"{Math.Max(1, (int)ago.TotalSeconds)} s ago" : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago" : $"at {_last.PastedAtUtc.ToLocalTime():HH:mm}";
        _when.Text = _last.AppName is { } app ? $"pasted {when} into {app}" : $"pasted {when}";
    }

    /// <summary>Lists the replaced words; ticks survive further edits as long as the same change is still there.</summary>
    private void FindChanges()
    {
        _debounce.Stop();
        var rules = CorrectionSuggestions.Suggest(_last.Text, _text.Text ?? "");
        _changes.Children.Clear();
        foreach (var rule in rules)
        {
            var key = rule.ToString();
            var line = Ui.Row(6, new TextBlock { Text = "Add correction:" }, new TextBlock { Text = $"{rule.Heard} → {rule.Written}", FontWeight = FontWeight.SemiBold });
            if (CorrectionSuggestions.LikelyOneOff(rule)) line.Children.Add(Ui.Text("(probably a one-off, leave unticked)", "hint"));
            var box = new CheckBox { Content = line, IsChecked = _ticked.Contains(key) || _saved.Contains(key), IsEnabled = !_saved.Contains(key), Tag = rule, MinHeight = 32 };
            AutomationProperties.SetName(box, $"Add correction: {rule.Heard} to {rule.Written}");
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _ticked.Add(key);
                else _ticked.Remove(key);
            };
            _changes.Children.Add(box);
        }
        _changesCard.IsVisible = rules.Count > 0;
        if (rules.Count == 0 && (_text.Text ?? "") != _last.Text)
            _status.Text = "No replaced words yet: added or deleted words, punctuation and capitals are not corrections.";
        else if (rules.Count > 0) _status.Text = "";
    }

    private void SaveTicked()
    {
        FindChanges();
        var ticked = _changes.Children.OfType<CheckBox>().Where(c => c.IsChecked == true && c.IsEnabled).Select(c => (CorrectionRule)c.Tag!).ToList();
        if (ticked.Count == 0)
        {
            _status.Text = "Nothing ticked, so nothing was saved. Tick the changes Dictum should make every time.";
            return;
        }
        _shell.Personal.AddCorrections(ticked);
        foreach (var rule in ticked) _saved.Add(rule.ToString());
        FindChanges();
        _status.Text = $"Saved {ticked.Count} correction(s) to corrections.txt; they apply to every dictation from now on.";
    }

    private async Task PasteAsync()
    {
        try
        {
            _status.Text = await _shell.PasteFixAsync(_text.Text ?? "");
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private void Failed(Exception ex)
    {
        _shell.Log.Error($"Fix last dictation: {ex.GetType().Name}: {ex.Message}");
        _status.Text = $"That did not work: {ex.Message}";
    }

    private static Control Dock(Control control, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}

/// <summary>
/// Dictionary (SPEC §16.2): add one word or name that whisper and the clean-up model should spell your way, and open the
/// dictionary and corrections files. Opened on purpose, so it takes focus.
/// </summary>
internal sealed class AddWordWindow : DialogWindow
{
    /// <summary>One name or term: a pasted list would crowd every other term out of whisper's prompt.</summary>
    public const int MaxLength = 100;

    public AddWordWindow(AppShell shell) : base("Add a word", 460)
    {
        Topmost = true;
        var term = new TextBox { MaxLength = MaxLength, Watermark = "For example Kynni or Parramatta" };
        AutomationProperties.SetName(term, "Word or phrase");
        var status = Ui.Text("", "caption");
        var add = Ui.Button("Add", "primary", () =>
        {
            var text = term.Text?.Trim() ?? "";
            if (shell.Personal.CheckNewTerm(text) is { } problem)
            {
                status.Text = problem;
                return;
            }
            shell.Personal.AddDictionaryTerms([text]);
            status.Text = $"Added \"{text}\". It is used from the next dictation.";
            term.Text = "";
        }, ex => status.Text = $"That did not work: {ex.Message}");
        add.IsDefault = true;
        var close = Ui.Button("Close", "secondary", Close);
        var links = Ui.Row(12,
            Ui.Button("Open dictionary file", "link", () => status.Text = shell.OpenPath(shell.Personal.EnsureDictionaryFile(), folder: false) ?? ""),
            Ui.Button("Open corrections file", "link", () => status.Text = shell.OpenPath(shell.Paths.CorrectionsFile, folder: false) ?? "corrections.txt is created when you save your first correction."),
            Ui.Button("Add a snippet…", "link", shell.OpenAddSnippet));
        Compose(Ui.Stack(10,
            Ui.Text("A name, product or term that whisper and the clean-up model should spell your way."),
            term, status, links), close, add);
        Opened += (_, _) => term.Focus();
    }
}

/// <summary>
/// Snippets (SPEC §16.6): a short phrase you say and the text Dictum pastes in its place, exactly as typed here: an email
/// address, a booking link, a signature. Writes one line to snippets.txt. Opened on purpose, so it takes focus.
/// </summary>
internal sealed class AddSnippetWindow : DialogWindow
{
    public const int MaxPhraseLength = 100;
    public const int MaxTextLength = 2000;

    /// <summary>SendInputSender types a line break as the Enter key, which sends the message in some chat apps.</summary>
    public const string TypeModeNote = "In Type mode each line break is typed as Enter, which sends the message in some chat apps.";

    public AddSnippetWindow(AppShell shell) : base("Add a snippet", 520)
    {
        Topmost = true;
        var phrase = new TextBox { MaxLength = MaxPhraseLength, Watermark = "For example: my email, or my booking link" };
        AutomationProperties.SetName(phrase, "When you say");
        var text = new TextBox
        {
            MaxLength = MaxTextLength,
            Watermark = "For example: kyle@example.com, or https://calendly.com/example/30min",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 180,
        };
        AutomationProperties.SetName(text, "Dictum pastes");
        var status = Ui.Text("", "caption");
        var add = Ui.Button("Add", "primary", () =>
        {
            var said = phrase.Text?.Trim() ?? "";
            var pasted = text.Text?.Trim(' ', '\t') ?? ""; // a line break at either end is the user's: kept (SPEC §16.6)
            if (shell.Personal.CheckNewSnippet(said, pasted) is { } problem)
            {
                status.Text = problem;
                return;
            }
            var replaced = shell.Personal.AddSnippet(said, pasted);
            status.Text = $"{(replaced ? "Updated" : "Added")}. Say \"{said}\" in a dictation and the text is pasted in its place.";
            phrase.Text = "";
            text.Text = "";
            phrase.Focus();
        }, ex => status.Text = $"That did not work: {ex.Message}");
        add.IsDefault = true;
        var close = Ui.Button("Close", "secondary", Close);
        var open = Ui.Button("Open snippets file", "link", () => status.Text = shell.OpenPath(shell.Personal.EnsureSnippetsFile(), folder: false) ?? "");
        Compose(Ui.Stack(10,
            Ui.Text("Say a short phrase and Dictum pastes your text in its place: an email address, a booking link, a signature. " +
                    "The text is pasted exactly as you type it here, line breaks included. Whole words, any case."),
            Ui.Stack(4, Ui.Text("When you say", "caption"), phrase),
            Ui.Stack(4, Ui.Text("Dictum pastes", "caption"), text,
                Ui.Text(TypeModeNote + " In snippets.txt this window writes each line break as \\n and each backslash as \\\\.", "caption")),
            status, open), close, add);
        Opened += (_, _) => phrase.Focus();
    }
}
