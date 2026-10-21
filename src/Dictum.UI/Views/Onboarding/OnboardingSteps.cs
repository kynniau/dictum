using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Audio;
using Dictum.Hotkey;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Views.Settings;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// One step of the Welcome to Dictum window: its name for the step line, its body, and what its buttons do. The window owns Back
/// and the button row; a step names its primary button (Enter presses it) and an optional second one.
/// </summary>
internal abstract class OnboardingStep : UserControl
{
    protected OnboardingStep(OnboardingFlow flow, string stepName, string heading)
    {
        Flow = flow;
        StepName = stepName;
        Heading = heading;
    }

    protected OnboardingFlow Flow { get; }

    public string StepName { get; }

    public string Heading { get; }

    public virtual string PrimaryLabel => OnboardingText.Next;

    /// <summary>The second button ("Skip"), or null for none.</summary>
    public virtual string? SecondaryLabel => null;

    /// <summary>Back is offered from this step (not from the first, and not once the settings are loaded).</summary>
    public virtual bool AllowsBack => true;

    /// <summary>Raised when the step's buttons change (Try it shows Calibrate my voice after the first dictation).</summary>
    public event Action? ButtonsChanged;

    protected void RaiseButtonsChanged() => ButtonsChanged?.Invoke();

    public virtual bool SecondaryVisible => SecondaryLabel is not null;

    /// <summary>The step is shown (again).</summary>
    public virtual void Enter()
    {
    }

    /// <summary>The step is left (Back, Next, Skip, or the window closing): anything it opened is closed.</summary>
    public virtual void Leave()
    {
    }

    /// <summary>The primary button: completes the step; false keeps it on screen (a refusal to read).</summary>
    public virtual bool Primary() => true;

    /// <summary>The second button: true moves on.</summary>
    public virtual bool Secondary() => true;

    protected static TextBlock Line(string text, string cls = "body") => Ui.Text(text, cls);
}

/// <summary>Step 1: how Dictum works, in one paragraph.</summary>
internal sealed class WelcomeStep : OnboardingStep
{
    public WelcomeStep(OnboardingFlow flow) : base(flow, OnboardingText.WelcomeName, OnboardingText.WelcomeHeading) =>
        Content = Ui.Stack(14, Line(OnboardingText.WelcomeText), Line(OnboardingText.WelcomeNext));

    public override bool AllowsBack => false;
}

/// <summary>
/// Step 2: where the model files go. The suggested folder (the per-user default) is shown with its drive's free space and kept
/// unless another is chosen with the system's folder chooser; Settings > Models' rules refuse the bundled folder and device
/// paths (ModelCoordinator.FolderRefusal), with their own words. models.folder is written only when the choice changed.
/// </summary>
internal sealed class FolderStep : OnboardingStep
{
    private readonly TextBox _folder = new() { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _free = Ui.Text("", "caption");
    private readonly TextBlock _warning = Ui.Text("", "caption");
    private readonly TextBlock _refusal = Ui.Text("", "error");
    private readonly Button _useSuggested;

    public FolderStep(OnboardingFlow flow) : base(flow, OnboardingText.FolderName, OnboardingText.FolderHeading)
    {
        AutomationProperties.SetName(_folder, OnboardingText.FolderBoxName);
        AutomationProperties.SetLiveSetting(_refusal, AutomationLiveSetting.Assertive);
        var choose = Ui.Button(OnboardingText.ChooseOther, "secondary", Choose);
        _useSuggested = Ui.Button(OnboardingText.UseSuggested, "secondary", () => Show(Flow.ChooseFolder(null)));
        Content = Ui.Stack(12, Line(OnboardingText.FolderText), _folder, _free, _warning, new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { choose, _useSuggested } }, _refusal);
        Show(null);
    }

    private void Choose()
    {
        if (Flow.Shell.Dialogs.ChooseFolder(Flow.Folder) is not { } folder) return;
        Show(Flow.ChooseFolder(folder));
    }

    /// <summary>Tests: the chooser's answer, without the system's window.</summary>
    internal void Pick(string? folder) => Show(Flow.ChooseFolder(folder));

    private void Show(string? refusal)
    {
        var folder = Flow.Folder;
        _folder.Text = Flow.Shell.RealPath(folder);
        _free.Text = FreeLine(folder) ?? "";
        _free.IsVisible = _free.Text.Length > 0;
        _warning.Text = ModelsPage.FolderWarning(folder) ?? "";
        _warning.IsVisible = _warning.Text.Length > 0;
        _refusal.Text = refusal ?? "";
        _refusal.IsVisible = refusal is not null;
        _useSuggested.IsVisible = Flow.ChosenFolder is not null;
    }

    /// <summary>"C: has 41.0 GB free", or null when the free space cannot be read (a share that does not say).</summary>
    internal static string? FreeLine(string folder, Func<string, long?>? freeBytes = null)
    {
        if ((freeBytes ?? DiskSpace.FreeBytes)(folder) is not long free) return null;
        var root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
        return OnboardingText.FreeSpace(root.Length > 3 ? Path.TrimEndingDirectorySeparator(root) : root.TrimEnd('\\', '/'), free);
    }

    public override bool Primary()
    {
        var failure = Flow.CompleteFolder();
        Show(failure);
        return failure is null;
    }
}

/// <summary>
/// Step 3: the speech model. With base.en on this PC (every package ships it, Decision 16) one line and no choice; otherwise
/// the Simple view's three tiers, the recommendation chosen, and "Show every model" for the whole catalogue and a file.
/// </summary>
internal sealed class SpeechStep : OnboardingStep
{
    private readonly StackPanel _choices = new() { Spacing = 6 };
    private readonly Button _everyModel;
    private readonly Button _chooseFile;
    private bool _everything;
    private string? _file;

    public SpeechStep(OnboardingFlow flow) : base(flow, OnboardingText.SpeechName, OnboardingText.SpeechHeading)
    {
        _everyModel = Ui.Button(OnboardingText.ShowEveryModel, "link", () =>
        {
            _everything = true;
            Fill();
        });
        _chooseFile = Ui.Button("Choose model file…", "secondary", ChooseFile); // the first-run picker's own label
    }

    public override void Enter()
    {
        if (!Flow.AsksSpeech)
        {
            Content = Line(OnboardingText.SpeechIncluded);
            return;
        }
        Content = Ui.Stack(12, Line(OnboardingText.SpeechChoice(ModelCatalog.DownloadHost)), _choices, _everyModel, _chooseFile);
        Fill();
    }

    private void Fill()
    {
        var plan = Flow.Plan;
        var current = SelectedName ?? plan.Speech;
        _choices.Children.Clear();
        if (!_everything)
        {
            // The Simple view's tiers for the language settings.json names (lane A's mapping): multilingual models when it is
            // not plain English; a tier the language rules out (an English-only model) is not offered here.
            var tiers = SimpleView.Tiers(plan.Gpu, Flow.Language, Flow.Translate).Where(t => t.Guard is null).ToList();
            var tier = tiers.FirstOrDefault(t => string.Equals(t.Model, current, StringComparison.OrdinalIgnoreCase))
                       ?? tiers.FirstOrDefault(t => string.Equals(t.Model, plan.Speech, StringComparison.OrdinalIgnoreCase)) ?? tiers.FirstOrDefault();
            foreach (var t in tiers)
            {
                var info = ModelCatalog.Find(t.Model)!;
                var sub = t.Tier == SpeechTier.Standard ? SimpleView.StandardSubLineNotIncluded : t.SubLine; // this step shows only without base.en
                if (!OnThisPc(info)) sub = $"{sub} ({info.SizeText})";
                _choices.Children.Add(Radio(t.Model, t.Label, sub, ReferenceEquals(t, tier)));
            }
        }
        else
        {
            foreach (var e in ModelCatalog.Entries)
                _choices.Children.Add(Radio(e.Name, e.ItemText(OnThisPc(e)) + (e.Name == plan.Speech ? " · recommended" : ""), e.TradeOff, e.Name == current));
        }
        _everyModel.IsVisible = !_everything;
        _chooseFile.IsVisible = _everything;
    }

    private static RadioButton Radio(string name, string label, string detail, bool chosen)
    {
        var radio = new RadioButton { GroupName = "onboarding-speech", Tag = name, IsChecked = chosen, Content = Ui.Stack(1, Ui.Text(label, "body"), Ui.Text(detail, "hint")) };
        AutomationProperties.SetName(radio, label);
        return radio;
    }

    /// <summary>On this PC in any model folder, the folder chosen on the folder step included (the shell's settings are not loaded yet).</summary>
    private bool OnThisPc(ModelInfo info)
    {
        var file = SettingsLoader.LoadFile(Flow.Shell.Paths.SettingsFile);
        return ModelFolders.Find(info.FileName, info.Bytes, Flow.Shell.Paths, file.Usable ? file.Settings : new AppSettings()) is not null;
    }

    private string? SelectedName => _choices.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private void ChooseFile()
    {
        if (Flow.Shell.Dialogs.ChooseFile(Flow.Shell.Paths.DefaultModelsDir, cleanupModel: false) is not { } file) return;
        _file = file;
        foreach (var radio in _choices.Children.OfType<RadioButton>()) radio.IsChecked = false;
        _choices.Children.Add(Radio("", Path.GetFileName(file), file, chosen: true));
    }

    public override bool Primary()
    {
        if (!Flow.AsksSpeech)
        {
            Flow.CompleteSpeech(null);
            return true;
        }
        var name = SelectedName;
        Flow.CompleteSpeech(name is { Length: > 0 } ? new ModelPick(name, null, false) : _file is not null ? new ModelPick(null, _file, false) : null);
        return true;
    }
}

/// <summary>Step 4: the first-run hotkey check, its box, words and buttons unchanged (HotkeyCheck).</summary>
internal sealed class HotkeyStep : OnboardingStep
{
    private readonly HotkeyCaptureBox _box;

    public HotkeyStep(OnboardingFlow flow) : base(flow, OnboardingText.HotkeyName, HotkeyCheck.Heading)
    {
        _box = new HotkeyCaptureBox(flow.Shell, flow.Hotkey, listenAtOnce: true); // listens while the step is on screen
        Content = Ui.Stack(12, Line(HotkeyCheck.Intro), _box, Line(HotkeyCheck.MacNote, "caption"));
    }

    public override string PrimaryLabel => HotkeyCheck.UseLabel;

    public override string? SecondaryLabel => HotkeyCheck.SkipLabel;

    internal HotkeyCaptureBox Box => _box;

    public override void Leave() => _box.Stop();

    public override bool Primary()
    {
        Flow.CompleteHotkey(_box.Hotkey);
        return true;
    }

    public override void Enter() => _box.Reset(Flow.Hotkey);
}

/// <summary>What the microphone test has heard so far.</summary>
internal enum MicResult { Listening, Heard, NothingHeard }

/// <summary>
/// The microphone test's rule, apart from the window: "Heard you." after about three seconds of sound in all, "Nothing heard."
/// after five seconds of silence in a row (it can still become "Heard you." while the test listens). A level above
/// <see cref="SoundFloor"/> is sound.
/// </summary>
internal sealed class MicCheck
{
    public const long HeardAfterMs = 3000;
    public const long NothingAfterMs = 5000;

    /// <summary>merge: PR #22's CaptureBuffer.SoundFloor (1e-5, -100 dBFS: below one 16-bit step), the same rule as "No sound from mic".</summary>
    public const float SoundFloor = 1e-5f;

    private long _soundMs;
    private long _silentMs;

    public MicResult Result { get; private set; }

    public MicResult Feed(float level, long elapsedMs)
    {
        if (level > SoundFloor)
        {
            _soundMs += elapsedMs;
            _silentMs = 0;
        }
        else
        {
            _silentMs += elapsedMs;
        }
        if (_soundMs >= HeardAfterMs) Result = MicResult.Heard;
        else if (Result != MicResult.Heard && _silentMs >= NothingAfterMs) Result = MicResult.NothingHeard;
        return Result;
    }
}

/// <summary>
/// Step 5: the microphone test. The device list is Settings > Microphone's (same labels); a capture opened for this test only
/// drives a live meter from its CurrentLevel and is closed when the step is left or the window closes, so the microphone is
/// never left open. With reduced motion the meter moves at most twice a second.
/// </summary>
internal sealed class MicrophoneStep : OnboardingStep
{
    private const int TickMs = 50;
    internal const int ReducedMotionMeterMs = 500;

    private readonly ComboBox _device = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Meter _meter = new(28);
    private readonly TextBlock _result = Ui.Text(OnboardingText.SayWords);
    private readonly DispatcherTimer _timer;
    private List<string?> _ids = [];
    private IAudioCapture? _capture;
    private MicCheck _check = new();
    private long _last;
    private long _meterShownAt;
    private float _meterPeak;
    private bool _filling;

    public MicrophoneStep(OnboardingFlow flow) : base(flow, OnboardingText.MicName, OnboardingText.MicHeading)
    {
        AutomationProperties.SetName(_device, "Microphone"); // Settings > Microphone's own label
        AutomationProperties.SetName(_meter, OnboardingText.MeterName);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        _device.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            StartTest(); // another microphone: test that one
        };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(TickMs), DispatcherPriority.Background, (_, _) => Tick());
        Content = Ui.Stack(14,
            Ui.Line("Microphone", _device),
            Ui.Row(14, _meter, _result),
            Line(OnboardingText.MicSkipNote, "caption"));
        _result.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _result.MaxWidth = 460;
    }

    public override string? SecondaryLabel => OnboardingText.Skip;

    /// <summary>The capture open for the test, or null (tests).</summary>
    internal IAudioCapture? Capture => _capture;

    internal string Result => _result.Text ?? "";

    /// <summary>The device id chosen (null: the system default).</summary>
    internal string? ChosenDevice => _device.SelectedIndex >= 0 && _device.SelectedIndex < _ids.Count ? _ids[_device.SelectedIndex] : Flow.Device;

    internal ComboBox DevicePicker => _device;

    public override void Enter()
    {
        var devices = Flow.Shell.Platform.ListCaptureDevices();
        _filling = true;
        _ids = [null, .. devices.Select(d => (string?)d.Name)];
        var labels = new List<string> { "System default" };
        labels.AddRange(devices.Select(d => d.IsDefault ? $"{d.Name} (default)" : d.Name));
        if (Flow.Device is { } chosen && !_ids.Contains(chosen))
        {
            _ids.Add(chosen);
            labels.Add($"{chosen} (not connected)");
        }
        _device.ItemsSource = labels;
        _device.SelectedIndex = Math.Max(0, _ids.IndexOf(Flow.Device));
        _filling = false;
        if (devices.Count == 0)
        {
            StopTest();
            _result.Text = OnboardingText.NoMicrophone; // nothing to open
            return;
        }
        StartTest();
    }

    private void StartTest()
    {
        StopTest();
        _check = new MicCheck();
        _result.Text = OnboardingText.SayWords;
        var capture = Flow.Shell.Platform.CreateCapture(Flow.Shell.Log);
        try
        {
            capture.Start(ChosenDevice, Stopwatch.GetTimestamp());
        }
        catch (Exception ex)
        {
            (capture as IDisposable)?.Dispose();
            _result.Text = Flow.Shell.Platform.ListCaptureDevices().Count == 0 ? OnboardingText.NoMicrophone : OnboardingText.CouldNotOpen(ex.Message);
            return;
        }
        _capture = capture;
        _last = _meterShownAt = Flow.Clock();
        _meterPeak = 0;
        _timer.Start();
    }

    /// <summary>Closes the test's microphone, if open; the system's microphone indicator goes off.</summary>
    private void StopTest()
    {
        _timer.Stop();
        _meter.Show(null);
        if (_capture is not { } capture) return;
        _capture = null;
        try
        {
            capture.Stop(); // the audio is dropped: nothing is kept
        }
        catch (Exception ex)
        {
            Flow.Shell.Log.Warn($"Onboarding microphone test: closing the microphone failed: {ex.Message}");
        }
        (capture as IDisposable)?.Dispose();
    }

    /// <summary>One look at the level (the timer's, or a test's after moving its clock).</summary>
    internal void Tick()
    {
        if (_capture is null) return;
        var now = Flow.Clock();
        var level = _capture.CurrentLevel;
        var result = _check.Feed(level, Math.Max(0, now - _last));
        _last = now;
        _meterPeak = Math.Max(_meterPeak, level);
        if (!Flow.Shell.Platform.WindowFlags.ReducedMotion || now - _meterShownAt >= ReducedMotionMeterMs)
        {
            _meter.Show(_meterPeak);
            MeterRedraws++;
            _meterPeak = 0;
            _meterShownAt = now;
        }
        _result.Text = result switch
        {
            MicResult.Heard => OnboardingText.HeardYou,
            MicResult.NothingHeard => OnboardingText.NothingHeard,
            _ => OnboardingText.SayWords,
        };
    }

    /// <summary>How many times the meter was redrawn (tests: the reduced-motion rate).</summary>
    internal int MeterRedraws { get; private set; }

    public override void Leave() => StopTest();

    public override bool Primary()
    {
        Flow.CompleteMicrophone(ChosenDevice);
        return true;
    }
}

/// <summary>
/// Step 6: a first dictation into a box in this window, through the real path (the hotkey listener is live once the shell has
/// loaded the settings; the paste goes to the focused box). After the first dictation the accent explainer and Calibrate my
/// voice; with nothing after <see cref="NothingAfterMs"/> of a ready model (or a model that cannot load), the try-later line.
/// Finish is always there.
/// </summary>
internal sealed class TryItStep : OnboardingStep
{
    public const long NothingAfterMs = 30_000;

    private readonly TextBlock _instruction = Ui.Text("");
    private readonly TextBox _box = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 90 };
    private readonly TextBlock _explainer = Ui.Text(OnboardingText.AccentExplainer);
    private readonly TextBlock _nothing = Ui.Text(OnboardingText.NothingCame, "caption");
    private readonly DispatcherTimer _timer;
    private LastDictation? _before;
    private long? _readyAt;

    public TryItStep(OnboardingFlow flow) : base(flow, OnboardingText.TryName, OnboardingText.TryHeading)
    {
        AutomationProperties.SetName(_box, OnboardingText.TryBoxName);
        AutomationProperties.SetLiveSetting(_instruction, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_explainer, AutomationLiveSetting.Polite);
        _explainer.IsVisible = false;
        _nothing.IsVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        Content = Ui.Stack(12, _instruction, _box, _explainer, _nothing);
    }

    public override string PrimaryLabel => OnboardingText.Finish;

    public override string? SecondaryLabel => ModelsPage.CalibrateLabel;

    public override bool SecondaryVisible => Succeeded;

    public override bool AllowsBack => false;

    public bool Succeeded { get; private set; }

    internal TextBox Box => _box;

    internal bool NothingShown => _nothing.IsVisible;

    internal string Instruction => _instruction.Text ?? "";

    public override void Enter()
    {
        _before = Flow.Shell.Personal.Last;
        Tick();
        _timer.Start();
    }

    public override void Leave() => _timer.Stop();

    internal void Tick()
    {
        var shell = Flow.Shell;
        var ready = Flow.TryItLive && shell.SpeechReady;
        if (ready && _readyAt is null) _readyAt = Flow.Clock();
        _instruction.Text = ready || Succeeded ? OnboardingText.TryInstruction(HotkeyName(shell.Settings.Hotkey)) : OnboardingText.TryLoading;
        if (!Succeeded && Flow.TryItLive && !ReferenceEquals(shell.Personal.Last, _before) && shell.Personal.Last is not null)
        {
            Succeeded = true;
            _explainer.IsVisible = true;
            _nothing.IsVisible = false;
            RaiseButtonsChanged();
            return;
        }
        if (Succeeded) return;
        var failedLoad = Flow.TryItLive && !shell.SpeechLoading && !shell.SpeechReady && shell.SpeechError is not null;
        if (failedLoad || _readyAt is long at && Flow.Clock() - at >= NothingAfterMs) _nothing.IsVisible = true;
    }

    private static string HotkeyName(string hotkey) => HotkeyParser.TryParse(hotkey, out var spec, out _) ? spec.Display : hotkey;

    public override bool Secondary()
    {
        Flow.Shell.OpenCalibration();
        return true; // and the window closes: calibration takes over
    }
}
