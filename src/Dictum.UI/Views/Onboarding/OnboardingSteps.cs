using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
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

    /// <summary>What has keyboard focus when the step is shown: the primary button (Enter or Space moves on), unless a step says otherwise.</summary>
    public virtual Control? InitialFocus(Button primary) => primary;

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
    private readonly TextBlock _intro = Ui.Text(OnboardingText.FolderText);
    private readonly Button _useSuggested;

    public FolderStep(OnboardingFlow flow) : base(flow, OnboardingText.FolderName, OnboardingText.FolderHeading)
    {
        AutomationProperties.SetName(_folder, OnboardingText.FolderBoxName);
        AutomationProperties.SetLiveSetting(_refusal, AutomationLiveSetting.Assertive);
        var choose = Ui.Button(OnboardingText.ChooseOther, "secondary", Choose);
        _useSuggested = Ui.Button(OnboardingText.UseSuggested, "secondary", () => Show(Flow.ChooseFolder(null)));
        Content = Ui.Stack(12, _intro, _folder, _free, _warning, new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { choose, _useSuggested } }, _refusal);
        Show(null);
    }

    /// <summary>"The included one" only when base.en is on this PC (looked up with the plan, as the speech step does).</summary>
    public override void Enter() => _intro.Text = Flow.Plan.BundledPath is not null ? OnboardingText.FolderText : OnboardingText.FolderTextNotIncluded;

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
                var sub = t.Tier == SpeechTier.Standard ? SimpleView.StandardSubLineNotIncluded : t.SubLine; // Standard here is small, or base.en without its bundled copy: never "Included"
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

    /// <summary>
    /// Nothing: a key pressed here is a hotkey being chosen, and Space on a focused button would choose it and press the button
    /// in one go. The first-run hotkey window gave focus to nothing either; Enter still presses Use this hotkey (lens on 6bacce9).
    /// </summary>
    public override Control? InitialFocus(Button primary) => null;

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
/// The microphone test's rule, apart from the window (lens on 6bacce9, 21/10/2026). "Heard you." needs about three seconds in
/// all at or above <c>silence_threshold</c> (settings.json, 0.01 by default: below it a dictation is discarded as silence), so a
/// microphone that delivers only background hiss is not "heard". "Nothing heard." is digital silence: five seconds in a row at
/// or below <see cref="CaptureBuffer.SoundFloor"/>, the "No sound from mic" rule; a level between the two is neither. "Nothing
/// heard." can still become "Heard you." while the test listens.
/// </summary>
internal sealed class MicCheck(double threshold)
{
    public const long HeardAfterMs = 3000;
    public const long NothingAfterMs = 5000;

    private long _loudMs;
    private long _silentMs;

    public MicResult Result { get; private set; }

    public MicResult Feed(float level, long elapsedMs)
    {
        if (level >= threshold && level > CaptureBuffer.SoundFloor) _loudMs += elapsedMs; // a threshold of 0 never makes digital silence "heard"
        _silentMs = level > CaptureBuffer.SoundFloor ? 0 : _silentMs + elapsedMs;
        if (_loudMs >= HeardAfterMs) Result = MicResult.Heard;
        else if (Result != MicResult.Heard && _silentMs >= NothingAfterMs) Result = MicResult.NothingHeard;
        return Result;
    }
}

/// <summary>
/// Step 5: the microphone test. The device list is Settings > Microphone's (same labels); a capture opened for this test only
/// drives a live meter from its CurrentLevel and is closed when the step is left or the window closes, about five seconds
/// after "Heard you.", or after a minute in any case (then "Test again" opens it again), so the microphone is never left
/// open. With reduced motion the meter moves at most twice a second.
/// </summary>
internal sealed class MicrophoneStep : OnboardingStep
{
    private const int TickMs = 50;
    internal const int ReducedMotionMeterMs = 500;

    /// <summary>The test closes the microphone this long after "Heard you." (lens on 6bacce9).</summary>
    internal const long StopAfterHeardMs = 5000;

    /// <summary>The test closes the microphone after this long whatever it heard.</summary>
    internal const long StopAfterMs = 60_000;

    private readonly ComboBox _device = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Meter _meter = new(28);
    private readonly TextBlock _result = Ui.Text(OnboardingText.SayWords);
    private readonly Button _testAgain;
    private readonly DispatcherTimer _timer;
    private List<string?> _ids = [];
    private IAudioCapture? _capture;
    private MicCheck _check = new(0.01);
    private long _last;
    private long _startedAt;
    private long? _heardAt;
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
        _testAgain = Ui.Button(OnboardingText.TestAgain, "secondary", StartTest);
        _testAgain.IsVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(TickMs), DispatcherPriority.Background, (_, _) => Tick());
        Content = Ui.Stack(14,
            Ui.Line("Microphone", _device),
            Ui.Row(14, _meter, _result),
            _testAgain,
            Line(OnboardingText.MicSkipNote, "caption"));
        _result.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _result.MaxWidth = 460;
    }

    public override string? SecondaryLabel => OnboardingText.Skip;

    /// <summary>The capture open for the test, or null (tests).</summary>
    internal IAudioCapture? Capture => _capture;

    internal string Result => _result.Text ?? "";

    internal bool TestAgainShown => _testAgain.IsVisible;

    /// <summary>The device id chosen (null: the system default).</summary>
    internal string? ChosenDevice => _device.SelectedIndex >= 0 && _device.SelectedIndex < _ids.Count ? _ids[_device.SelectedIndex] : Flow.Device;

    internal ComboBox DevicePicker => _device;

    /// <summary>
    /// The microphones the system lists, or none when it cannot say (the audio service stopped: the Windows device list throws
    /// then; lens on 6bacce9). Logged, never fatal.
    /// </summary>
    private IReadOnlyList<AudioDeviceInfo> Devices()
    {
        try
        {
            return Flow.Shell.Platform.ListCaptureDevices();
        }
        catch (Exception ex)
        {
            Flow.Shell.Log.Warn($"Onboarding microphone test: the microphones could not be listed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    public override void Enter()
    {
        var devices = Devices();
        _filling = true;
        (_ids, var labels) = MicrophoneChoices.Build(devices, Flow.Device);
        _device.ItemsSource = labels;
        _device.SelectedIndex = Math.Max(0, _ids.IndexOf(Flow.Device));
        _filling = false;
        if (devices.Count == 0)
        {
            StopTest();
            _testAgain.IsVisible = false;
            _result.Text = NoSoundText.NoMicrophoneBalloon; // nothing to open
            return;
        }
        StartTest();
    }

    private void StartTest()
    {
        StopTest();
        _testAgain.IsVisible = false;
        _check = new MicCheck(Flow.SilenceThreshold);
        _heardAt = null;
        _result.Text = OnboardingText.SayWords;
        var capture = Flow.Shell.Platform.CreateCapture(Flow.Shell.Log);
        try
        {
            capture.Start(ChosenDevice, Stopwatch.GetTimestamp());
        }
        catch (Exception ex)
        {
            (capture as IDisposable)?.Dispose();
            _result.Text = ex is NoMicrophoneException || Devices().Count == 0 ? NoSoundText.NoMicrophoneBalloon : OnboardingText.CouldNotOpen(ex.Message);
            _testAgain.IsVisible = true;
            return;
        }
        _capture = capture;
        _last = _startedAt = _meterShownAt = Flow.Clock();
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
        // One late tick (a busy UI thread, a suspended PC) counts as at most two ticks, never as seconds of speech.
        // simplification: the clamp caps each tick at 2 x TickMs (100 ms), so on a PC whose timer always fires later than 100 ms
        // "about 3 s" of speech takes longer to count than 3 s. Retire it when the check is fed the capture's own sample time (the
        // frames it delivered) instead of the UI timer's gaps, or when a report shows the test taking noticeably longer than 3 s.
        var result = _check.Feed(level, Math.Clamp(now - _last, 0, 2 * TickMs));
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
        if (result == MicResult.Heard) _heardAt ??= now;
        if (_heardAt is long heard && now - heard >= StopAfterHeardMs || now - _startedAt >= StopAfterMs)
        {
            StopTest(); // the result line stays; Test again opens the microphone again
            if (result == MicResult.Listening) _result.Text = OnboardingText.NothingHeard; // only hiss for a minute: not a dead "Say a few words."
            _testAgain.IsVisible = true;
        }
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
    private readonly TextBlock _status = Ui.Text(OnboardingText.TryLoading);
    private readonly TextBlock _explainer = Ui.Text(OnboardingText.AccentExplainer);
    private readonly TextBlock _nothing = Ui.Text(OnboardingText.NothingCame, "caption");
    private readonly DispatcherTimer _timer;
    private LastDictation? _before;
    private long? _readyAt;
    private bool _waiting = true;

    public TryItStep(OnboardingFlow flow) : base(flow, OnboardingText.TryName, OnboardingText.TryHeading)
    {
        AutomationProperties.SetName(_box, OnboardingText.TryBoxName);
        AutomationProperties.SetLiveSetting(_instruction, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_explainer, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_nothing, AutomationLiveSetting.Polite);
        _explainer.IsVisible = false;
        _nothing.IsVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        // While the model gets ready its status sits inside the practice box, over the empty box as a placeholder does (1.2
        // polish, 22/10/2026: a status line over an empty box read as a box that does nothing). Not the box's own watermark: the
        // theme's template draws that at half opacity, which a style cannot change, and this status is to be read (body text).
        _status.IsHitTestVisible = false; // a click goes to the box under it
        _status.Margin = new Thickness(12, 8);
        _status.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _box.TextChanged += (_, _) => ShowStatus();
        Content = Ui.Stack(12, _instruction, new Grid { Children = { _box, _status } }, _explainer, _nothing);
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
        var failedLoad = Flow.TryItLive && !shell.SpeechLoading && !shell.SpeechReady && shell.SpeechError is not null;
        // The instruction names the key from the start (the one chosen on the hotkey step until the settings are loaded); the
        // model's status is in the box until it is ready. A model that cannot load gets the try-later line instead.
        _instruction.Text = OnboardingText.TryInstruction(HotkeyNames.Display(Flow.TryItLive ? shell.Settings.Hotkey : Flow.Hotkey));
        _waiting = !ready && !Succeeded && !failedLoad;
        if (!Succeeded && Flow.TryItLive && !ReferenceEquals(shell.Personal.Last, _before) && shell.Personal.Last is not null)
        {
            Succeeded = true;
            _waiting = false;
            ShowStatus();
            _explainer.Text = Explainer(shell.Settings);
            _explainer.IsVisible = true;
            _nothing.IsVisible = false;
            RaiseButtonsChanged();
            return;
        }
        ShowStatus();
        if (Succeeded) return;
        if (failedLoad || _readyAt is long at && Flow.Clock() - at >= NothingAfterMs) _nothing.IsVisible = true;
    }

    /// <summary>The status over the practice box: shown while the model gets ready and the box is empty.</summary>
    private void ShowStatus() => _status.IsVisible = _waiting && string.IsNullOrEmpty(_box.Text);

    /// <summary>What the empty practice box shows: the model's status while it gets ready, else null (tests).</summary>
    internal string? Placeholder => _status.IsVisible ? _status.Text : null;

    internal TextBlock StatusText => _status;

    /// <summary>The Standard model's explainer only when the model in use is the Standard choice for its language; else "the model you chose".</summary>
    internal string Explainer(AppSettings s) =>
        SimpleView.TierOf(s.Whisper.Model, Flow.Shell.GpuMachine, s.Whisper.Language, s.Whisper.Translate)?.Tier == SpeechTier.Standard
            ? OnboardingText.AccentExplainer
            : OnboardingText.AccentExplainerChosen;

    /// <summary>The practice box: a hotkey held without clicking first pastes there, not into a button (lens on 6bacce9).</summary>
    public override Control? InitialFocus(Button primary) => _box;


    public override bool Secondary()
    {
        Flow.Shell.OpenCalibration();
        return true; // and the window closes: calibration takes over
    }
}
