using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views;

/// <summary>
/// The tray panel (docs/design/Flyout.dc.html), opened by a click on the tray icon and closed by another click, Esc, or
/// focus moving elsewhere: status and the on/off switch, any model problem (with Try on the CPU and Open log folder),
/// Light/Full, the two model pickers, the microphone with its meter,
/// the indicator and the three toggles, four actions, the privacy line and Quit. Opened on purpose, so it takes focus.
/// </summary>
internal sealed class FlyoutWindow : Window
{
    private const double PanelWidth = 380;
    private const double Shadow = 14; // room around the panel for its shadow
    private static readonly string[] IndicatorLabels = ["Off", "Orb", "Edge", "Both"];

    private readonly AppShell _shell;
    private readonly TextBlock _models = Ui.Text("", "caption");
    private readonly TextBlock _problem = Ui.Text("", "caption");
    private readonly Button _tryCpu;
    private readonly StackPanel _problemBox;
    private readonly Ellipse _dot = new() { Width = 8, Height = 8 };
    private readonly TextBlock _state = Ui.Text("", "caption");
    private readonly ToggleSwitch _enabled;
    private readonly Segments _mode;
    private readonly TextBlock _modeHint = Ui.Text("", "hint");
    private readonly ComboBox _speech = new();
    private readonly ComboBox _cleanup = new();
    private readonly ComboBox _mic = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly Meter _meter = new();
    private readonly Segments _indicator;
    private readonly ToggleSwitch _profanity;
    private readonly ToggleSwitch _grammar;
    private readonly ToggleSwitch _sounds;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 4 };
    private readonly TextBlock _downloadText = Ui.Text("", "caption");
    private readonly StackPanel _download;
    private readonly TextBlock _privacy = Ui.Text("", "caption");
    private readonly Control _lock = Ui.Icon(Ui.LockIcon, 14);
    private readonly DispatcherTimer _meterTimer;
    private List<string?> _micIds = [];
    private bool _refreshing;

    public FlyoutWindow(AppShell shell)
    {
        _shell = shell;
        Title = "Dictum";
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Width = PanelWidth + 2 * Shadow;
        SizeToContent = SizeToContent.Height;

        _enabled = Ui.Switch(shell.Settings.Enabled, shell.SetEnabled);
        AutomationProperties.SetName(_enabled, "Dictation on");
        _mode = new Segments(["Light", "Full"], -1, i => Act(() => shell.Personal.SelectPreset(i == 0 ? ModePreset.Light : ModePreset.Full)), "Mode");
        _indicator = new Segments(IndicatorLabels, 0, i => Write("indicator.mode", IndicatorSettings.Modes[i]), "Big recording indicator");
        _profanity = Ui.Switch(false, on => Write("profanity.mode", on ? ProfanitySettings.Mask : ProfanitySettings.Off));
        _grammar = Ui.Switch(false, on => Write("llm.grammar_assist", on));
        _sounds = Ui.Switch(false, on => Write("sounds.enabled", on));
        _speech.SelectionChanged += (_, _) => OnSpeechPicked();
        _cleanup.SelectionChanged += (_, _) => OnCleanupPicked();
        _mic.SelectionChanged += (_, _) => OnMicPicked();
        AutomationProperties.SetName(_speech, "Speech model");
        AutomationProperties.SetName(_cleanup, "Clean-up");
        AutomationProperties.SetName(_mic, "Microphone");
        _download = Ui.Stack(4, _downloadText, _progress, Ui.Button("Cancel download", "link", () => shell.Models.CancelDownload(DownloadCancelReason.CancelLink)));
        // Why a model could not load or runs on the CPU, under the header, with what to do about it (Store investigation, 16/10/2026).
        _tryCpu = Ui.Button(AppShell.TryOnCpuLabel, "link", () =>
        {
            // The reload hides this button and its box while the model loads: keyboard focus goes first to the panel's first control.
            if (_tryCpu!.IsFocused) _enabled.Focus(NavigationMethod.Tab); // set by then: the button exists before it can be pressed
            Act(() => shell.TryOnCpu());
        });
        var openLogs = Ui.Button("Open log folder", "link", () => Act(() =>
        {
            if (shell.OpenPath(shell.RealPath(shell.Paths.LogsDir), folder: true) is { } why) shell.Notify("Dictum", why, warning: true);
        }));
        AutomationProperties.SetLiveSetting(_problem, AutomationLiveSetting.Polite);
        _problemBox = Ui.Stack(4, _problem, Ui.Row(12, _tryCpu, openLogs));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        header.Children.Add(new MarkIcon(28));
        var titles = Ui.Stack(1, new TextBlock { Text = "Dictum", FontSize = 15, FontWeight = FontWeight.SemiBold }, _models);
        Grid.SetColumn(titles, 1);
        var state = Ui.Row(6, _dot, _state);
        Grid.SetColumn(state, 2);
        Grid.SetColumn(_enabled, 3);
        header.Children.AddRange([titles, state, _enabled]);

        var micRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        micRow.Children.Add(Ui.Icon(Ui.MicIcon, 18, "SecondaryTextBrush"));
        Grid.SetColumn(_mic, 1);
        Grid.SetColumn(_meter, 2);
        micRow.Children.AddRange([_mic, _meter]);
        var micBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6), Child = micRow };
        micBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SegmentBrush");

        var settings = Wide(Ui.Button("Settings", "secondary", () => shell.OpenSettings()));
        var actions = Ui.Columns(2, 8,
            Wide(Ui.Button("Calibrate my voice", "secondary", shell.OpenCalibration)),
            Wide(Ui.Button("Dictionary", "secondary", shell.OpenAddWord)),
            Wide(Ui.Button("Snippets", "secondary", shell.OpenAddSnippet)),
            Wide(Ui.Button("Fix last dictation", "secondary", () => shell.OpenFixLast())),
            settings, Wide(Ui.Button("Help", "secondary", () => shell.OpenSettings("Help")))); // six buttons: three full rows

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(Ui.Row(6, _lock, _privacy));
        var quit = Ui.Button("Quit", "quiet", shell.Quit);
        Grid.SetColumn(quit, 1);
        footer.Children.Add(quit);
        var footerBox = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Child = footer };
        footerBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("HairlineBrush");

        var body = Ui.Stack(14,
            header,
            _problemBox,
            _download,
            Ui.Stack(6, Ui.Text("Mode", "caption"), _mode, _modeHint),
            Ui.Columns(2, 10, Labelled("Speech model", _speech), Labelled("Clean-up", _cleanup)),
            micBox,
            Ui.Stack(2,
                Ui.Line("Big recording indicator", _indicator),
                Ui.Line("Profanity filter", _profanity),
                Ui.Line("Grammar assist", _grammar),
                Ui.Line("Start and stop sounds", _sounds)),
            actions,
            footerBox);
        var panel = new Border
        {
            Margin = new Thickness(Shadow),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #73000000"),
            Child = body,
        };
        panel[!Border.BackgroundProperty] = new DynamicResourceExtension("SurfaceBrush");
        panel[!Border.BorderBrushProperty] = new DynamicResourceExtension("HairlineBrush");
        Content = panel;

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
            (_, _) => _meter.Show(_shell.DictationIdle ? null : _shell.CurrentLevel));
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Deactivated += (_, _) => Close(); // a click anywhere else closes it, like the system's own flyouts
        Opened += (_, _) =>
        {
            shell.Changed += Refresh;
            _meterTimer.Start();
        };
        Closed += (_, _) =>
        {
            shell.Changed -= Refresh;
            _meterTimer.Stop();
        };
        Refresh();
    }

    /// <summary>Shows the panel beside the tray icon (above a bottom taskbar, below a top one), 12 px in from the work area.</summary>
    public void ShowNear(ScreenPoint? anchor)
    {
        var screen = anchor is { } a ? Screens.ScreenFromPoint(new PixelPoint(a.X, a.Y)) : null;
        screen ??= Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null)
        {
            Measure(new Size(Width, double.PositiveInfinity));
            var scale = screen.Scaling;
            var work = screen.WorkingArea;
            var w = (int)Math.Round(Width * scale);
            var h = (int)Math.Round(DesiredSize.Height * scale);
            var gap = (int)Math.Round((12 - Shadow) * scale);
            var point = anchor ?? new ScreenPoint(work.Right, work.Bottom);
            var x = point.X < work.X ? work.X + gap : point.X >= work.Right ? work.Right - w - gap : Math.Clamp(point.X - w / 2, work.X + gap, work.Right - w - gap);
            var y = point.Y < work.Y ? work.Y + gap : point.Y >= work.Bottom ? work.Bottom - h - gap : Math.Clamp(point.Y - h / 2, work.Y + gap, work.Bottom - h - gap);
            Position = new PixelPoint(x, y);
        }
        Show();
        Activate(); // the click on the tray icon gives this process the right to take the foreground
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var s = _shell.Settings;
            var status = _shell.Status;
            _models.Text = status.Models;
            _problem.Text = status.Problem ?? "";
            _problemBox.IsVisible = status.Problem is not null;
            _tryCpu.IsVisible = status.TryCpu;
            _state.Text = status.State;
            _dot[!Shape.FillProperty] = new DynamicResourceExtension(status.Tone switch
            {
                "ready" => "DoneBrush",
                "recording" => "RecordingBrush",
                "working" => "WorkingBrush",
                "error" => "RecordingBrush",
                _ => "MutedBrush",
            });
            Ui.SetQuietly(_enabled, s.Enabled);
            _download.IsVisible = status.Download is not null;
            _downloadText.Text = status.Download ?? "";
            _progress.IsIndeterminate = status.DownloadFraction is null;
            _progress.Value = status.DownloadFraction ?? 0;

            var preset = _shell.Personal.CurrentPreset;
            _mode.Select(preset switch { ModePreset.Light => 0, ModePreset.Full => 1, _ => -1 });
            var full = ModePresets.For(ModePreset.Full, _shell.GpuMachine);
            _modeHint.Text = (preset == ModePreset.Custom ? "Custom: your settings match neither preset. " : "") +
                             $"Full: {full.SpeechModel} and {full.CleanupModel}. Light: {ModePresets.LightSpeech} and the rule-based tidy.";

            FillSpeech();
            FillCleanup(s);
            FillMicrophones(s);
            _indicator.Select(Math.Max(0, Array.IndexOf(IndicatorSettings.Modes, s.Indicator.Mode)));
            Ui.SetQuietly(_profanity, s.Profanity.Mode != ProfanitySettings.Off);
            Ui.SetQuietly(_grammar, s.Llm.GrammarAssist);
            Ui.SetQuietly(_sounds, s.Sounds.Enabled);
            // Each line after "Nothing leaves this PC": Approved by the product owner on 16/10/2026 as written: never reword it.
            var nothingLeaves = _shell.NothingLeaves;
            _lock.IsVisible = nothingLeaves; // the lock stands only beside "Nothing leaves this PC"
            _privacy.Text = nothingLeaves ? "Nothing leaves this PC"
                : Dictum.Llm.CloudPolicy.LocalServerHost(s.Llm) is not null ? "Clean-up via a server on this PC"
                : "Cloud clean-up on · text leaves this PC";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void FillSpeech()
    {
        var current = _shell.Models.CurrentName;
        var names = ModelCatalog.Entries.Select(e => e.Name).ToList();
        if (!names.Contains(current)) names.Insert(0, current); // a model file chosen from disk
        Fill(_speech, names, [.. names.Select(n => ModelCatalog.Find(n) is { } info && !_shell.Models.IsDownloaded(info) ? $"{n}{DownloadMark(info.FileName)}" : n)], current);
    }

    // PROPOSED copy (19/10/2026), for the product owner to approve: a picker entry whose file is on its way (Store 10.1.2.10).
    internal const string DownloadingMark = " (downloading)";
    internal const string WaitingMark = " (waiting to download)";

    /// <summary>What a catalogue entry that is not on this PC carries: downloading, waiting its turn, or "(download)".</summary>
    private string DownloadMark(string fileName) => _shell.Models.DownloadStateOf(fileName) switch
    {
        Dictum.App.DownloadState.Running => DownloadingMark,
        Dictum.App.DownloadState.Waiting => WaitingMark,
        _ => " (download)",
    };

    /// <summary>
    /// Sets a picker's entries and its selection, touching each only when it changed: a download's progress refreshes the panel
    /// many times a second, and resetting the list each time closed an open drop-down and put the choice back mid-pick.
    /// </summary>
    private static void Fill(ComboBox picker, List<string> names, List<string> labels, string current)
    {
        picker.Tag = names;
        SetItems(picker, labels, names.IndexOf(current));
    }

    /// <summary>The compare-before-set of <see cref="Fill"/>, for any picker (the microphone list too).</summary>
    private static void SetItems(ComboBox picker, List<string> labels, int index)
    {
        if (picker.ItemsSource is not List<string> shown || !shown.SequenceEqual(labels)) picker.ItemsSource = labels;
        if (picker.SelectedIndex != index) picker.SelectedIndex = index;
    }

    private void FillCleanup(AppSettings s)
    {
        var names = CleanupCatalog.Entries.Select(e => e.Name).ToList();
        var labels = names.Select(n => CleanupCatalog.Find(n)! is var info && _shell.Models.IsCleanupDownloaded(info) ? $"{n} · local" : $"{n} · local{DownloadMark(info.FileName)}").ToList();
        names.Add(LlmSettings.NoProvider);
        labels.Add("Rule-based tidy");
        var current = s.Llm.Provider switch
        {
            LlmSettings.Local => CleanupCatalog.FindByFile(s.Llm.Model)?.Name ?? s.Llm.Model,
            LlmSettings.NoProvider => LlmSettings.NoProvider,
            _ => s.Llm.Provider,
        };
        if (!names.Contains(current))
        {
            names.Insert(0, current);
            labels.Insert(0, s.Llm.Provider == LlmSettings.Local ? $"{System.IO.Path.GetFileName(current)} · local" : $"{_shell.Models.CurrentCleanupName} · cloud");
        }
        Fill(_cleanup, names, labels, current);
    }

    private void FillMicrophones(AppSettings s)
    {
        var devices = _shell.Platform.ListCaptureDevices();
        _micIds = [null, .. devices.Select(d => (string?)d.Name)];
        var labels = new List<string> { "System default" };
        labels.AddRange(devices.Select(d => d.IsDefault ? $"{d.Name} (default)" : d.Name));
        if (s.Audio.Device is { } chosen && !_micIds.Contains(chosen))
        {
            _micIds.Add(chosen);
            labels.Add($"{chosen} (not connected)");
        }
        SetItems(_mic, labels, Math.Max(0, _micIds.IndexOf(s.Audio.Device))); // a progress tick leaves an open list alone (lens, 19/10/2026)
    }

    private void OnSpeechPicked()
    {
        if (_refreshing || _speech.Tag is not List<string> names || _speech.SelectedIndex < 0) return;
        var name = names[_speech.SelectedIndex];
        if (name != _shell.Models.CurrentName && ModelCatalog.Find(name) is not null) Act(() => _shell.Models.Select(name));
    }

    private void OnCleanupPicked()
    {
        if (_refreshing || _cleanup.Tag is not List<string> names || _cleanup.SelectedIndex < 0) return;
        var name = names[_cleanup.SelectedIndex];
        if (name == LlmSettings.NoProvider) Act(_shell.Models.SelectRulesOnly);
        else if (CleanupCatalog.Find(name) is not null) Act(() => _shell.Models.SelectCleanup(name));
    }

    private void OnMicPicked()
    {
        if (_refreshing || _mic.SelectedIndex < 0 || _mic.SelectedIndex >= _micIds.Count) return;
        var id = _micIds[_mic.SelectedIndex];
        if (id != _shell.Settings.Audio.Device) _shell.Write(("audio.device", id is null ? null : JsonValue.Create(id)));
    }

    private void Write(string key, string value) => Act(() => _shell.Write((key, JsonValue.Create(value))));

    private void Write(string key, bool value) => Act(() => _shell.Write((key, JsonValue.Create(value))));

    /// <summary>Runs a change; a question it asks (a download) opens its own window, so this panel closes first.</summary>
    private void Act(Action action)
    {
        if (_refreshing) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _shell.Log.Error($"Tray panel: {ex.GetType().Name}: {ex.Message}");
            _shell.Notify("Dictum", $"That did not work: {ex.Message}", warning: true);
        }
    }

    private static Control Labelled(string label, Control control) => Ui.Stack(6, Ui.Text(label, "caption"), control);

    private static Button Wide(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.MinHeight = 40;
        return button;
    }
}
