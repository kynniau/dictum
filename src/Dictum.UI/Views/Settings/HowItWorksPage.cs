using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Calibration;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.UI.Shell;
using Dictum.UI.Views.Onboarding;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// Settings → See how it works (SPEC §28): record one dictation (Record, or hold the dictation hotkey while this page is in
/// front) and watch its five stages fill in as the pipeline finishes each: the sound (a live meter, as the welcome window's
/// microphone test), what the speech model heard, the rule-based tidy, the AI clean-up with the validator's verdict, and what
/// would be pasted. The recording is a capture opened for this page only and kept in memory; it goes through the shell's own
/// pipeline (<see cref="AppShell.DemonstrateAsync"/>), so the speech model, clean-up and settings are the ones in use, and
/// nothing is pasted, saved or sent anywhere a dictation would not send it. Words: <see cref="HowItWorksText"/>.
/// </summary>
internal sealed class HowItWorksPage : SettingsPage
{
    private const int TickMs = 50;

    private readonly Meter _meter = new(28);
    private readonly Button _record;
    private readonly TextBlock _hold = Ui.Text("", "caption");
    private readonly TextBlock _status = Ui.Text("");
    private readonly TextBlock _sendsText = Ui.Text("", "caption");
    private readonly StageCard[] _cards;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _limit;
    private HowItWorksRun _run = new();
    private IAudioCapture? _capture;
    private CancellationTokenSource? _cancel;
    private IDisposable? _hotkey;
    private Window? _window;
    private bool _noMicrophone;
    private bool _speechReady = true;
    private bool _ownPress; // the page saw this press's key-down and kept it (decided once per press)
    private bool _recordKeyboardFocus; // Record's focus came from the keyboard (Tab or arrows), not a click
    private bool _listFailureLogged;
    private long _meterShownAt;
    private float _meterPeak;

    public HowItWorksPage(AppShell shell) : base(shell, HowItWorksText.PageTitle)
    {
        _record = Ui.Button(HowItWorksText.RecordLabel, "danger", Toggle, Failed, name: HowItWorksText.RecordName);
        _record.MinHeight = 44;
        SpeechReady = () => Shell.SpeechReady;
        _record.GotFocus += (_, e) => _recordKeyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        AutomationProperties.SetName(_meter, HowItWorksText.Stages[0].Title);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.IsVisible = false;
        _cards = [.. HowItWorksText.Stages.Select((_, i) => new StageCard(i == 0 ? _meter : null))];
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(TickMs), DispatcherPriority.Background, (_, _) => Tick());
        _limit = new DispatcherTimer { Interval = CalibrationSession.TakeLimit };
        _limit.Tick += (_, _) => TakeLimitReached();
        Compose(
            Ui.Card(Ui.Stack(10, Ui.Text(HowItWorksText.Intro), Ui.Text(HowItWorksText.MemoryNote, "caption"), _sendsText)),
            Ui.Card(Ui.Stack(10, Ui.Row(10, _record, _hold), _status)),
            Ui.Stack(12, [.. _cards.Select(c => c.Root)]));
        if (Content is Panel { Children: [TextBlock title, ..] }) AutomationProperties.SetHeadingLevel(title, 1);
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
        Render();
    }

    /// <summary>The clock the reduced-motion meter rate is measured on (tests move it).</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>The pipeline run under way or last run (tests wait on it), or null before the first.</summary>
    internal Task? Running { get; private set; }

    /// <summary>How many times the meter was redrawn (tests: the reduced-motion rate).</summary>
    internal int MeterRedraws { get; private set; }

    internal bool IsRecording => _capture is not null;

    internal IReadOnlyList<StageView> Stages => _run.Stages();

    internal string Status => _status.IsVisible ? _status.Text ?? "" : "";

    /// <summary>A speech model is loaded (tests that put scripted engines in the shell's pipeline say so here).</summary>
    internal Func<bool> SpeechReady { get; set; }

    /// <summary>The cloud line above Record, or "" when the clean-up in use sends nothing (tests).</summary>
    internal string SendsTextLine => _sendsText.IsVisible ? _sendsText.Text ?? "" : "";

    protected override void Redraw()
    {
        _hold.Text = HotkeyParser.TryParse(Shell.Settings.Hotkey, out var spec, out _) ? HowItWorksText.HoldHint(HotkeyNames.Display(spec)) : "";
        var cleaner = Shell.Cleaner;
        _sendsText.Text = cleaner.SendsText ? HowItWorksText.SendsText(cleaner.Status) : "";
        _sendsText.IsVisible = _sendsText.Text.Length > 0;
        CheckMicrophone();
        CheckSpeech();
    }

    public override void Leave()
    {
        Release();
        if (_capture is not null) Cancel(HowItWorksText.Cancelled);
        _cancel?.Cancel();
    }

    /// <summary>The dictation hotkey records here while this page is shown in the window in front, as in calibration.</summary>
    private void Attach()
    {
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null) return;
        _window.Activated += Activated;
        _window.Deactivated += Deactivated;
        if (_window.IsActive) Grab();
    }

    private void Detach()
    {
        if (_window is not null)
        {
            _window.Activated -= Activated;
            _window.Deactivated -= Deactivated;
        }
        _window = null;
        Release();
    }

    private void Activated(object? sender, EventArgs e)
    {
        Grab();
        CheckMicrophone(); // one plugged in meanwhile turns Record back on
    }

    /// <summary>The window went behind another: the hotkey dictates again, and a take under way here is dropped (its key-up would not come here).</summary>
    private void Deactivated(object? sender, EventArgs e) => WindowDeactivated();

    /// <summary>The window's Deactivated (tests call it: the headless platform does not deactivate a window).</summary>
    internal void WindowDeactivated()
    {
        Release();
        if (_capture is not null) Cancel(HowItWorksText.Cancelled);
    }

    private void Grab() => _hotkey ??= Shell.RedirectHotkey(OnHotkey);

    private void Release()
    {
        _hotkey?.Dispose();
        _hotkey = null;
    }

    /// <summary>
    /// The hotkey while this page holds it, decided once per press (re-lens on aebd2fd): a press is the page's only if the page
    /// saw its key-down with no dictation under way (or with its own take open); then its key-up is the page's too. Any other
    /// press (one whose key-down went to a dictation, before or after the page took the hotkey) goes to the controller whole:
    /// its key-up and Esc as well, even once that dictation has gone idle, so the controller never keeps a key held that was
    /// let go (it would ignore the next real press as auto-repeat). Esc is the page's only while its own take is open.
    /// </summary>
    internal void OnHotkey(HookEvent e)
    {
        var mine = e.Signal switch
        {
            HotkeySignal.Down => _ownPress = _capture is not null || Shell.DictationIdle,
            HotkeySignal.Up => _ownPress,
            HotkeySignal.Escape => _capture is not null,
            _ => false,
        };
        if (e.Signal == HotkeySignal.Up) _ownPress = false;
        if (!mine)
        {
            Shell.ForwardHotkey(e); // a dictation started elsewhere (held, hands-free, toggle) keeps its key and its Esc
            return;
        }
        try
        {
            switch (e.Signal)
            {
                case HotkeySignal.Down when _capture is null:
                    Toggle();
                    break;
                case HotkeySignal.Up when _capture is not null:
                    Stop(limitReached: false);
                    break;
                case HotkeySignal.Escape when _capture is not null:
                    Cancel(HowItWorksText.Cancelled);
                    break;
            }
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private void Toggle()
    {
        if (_capture is not null)
        {
            Stop(limitReached: false);
            return;
        }
        if (Running is { IsCompleted: false }) return; // one demonstration at a time
        if (!Shell.DictationIdle)
        {
            Tell(HowItWorksText.Busy);
            return;
        }
        if (!CheckMicrophone() || !CheckSpeech()) return; // the hotkey, which a disabled Record does not stop
        var capture = Shell.Platform.CreateCapture(Shell.Log);
        try
        {
            capture.Start(Shell.Settings.Audio.Device, Stopwatch.GetTimestamp());
        }
        catch
        {
            (capture as IDisposable)?.Dispose();
            throw; // Failed says it: the no-microphone sentence for NoMicrophoneException
        }
        _capture = capture;
        capture.Failed += CaptureFailed;
        _run = new HowItWorksRun();
        _run.StartRecording();
        _meterShownAt = Clock();
        _meterPeak = 0;
        _tick.Start();
        _limit.Start();
        Tell("");
        Render();
    }

    /// <summary>Closes this page's microphone and returns the recording, in memory; null if none was open.</summary>
    private CapturedAudio? CloseMicrophone()
    {
        _tick.Stop();
        _limit.Stop();
        _meter.Show(null);
        if (_capture is not { } capture) return null;
        _capture = null;
        capture.Failed -= CaptureFailed;
        try
        {
            return capture.Stop();
        }
        finally
        {
            (capture as IDisposable)?.Dispose();
        }
    }

    /// <summary>The device failed mid-take (from the capture thread): said, and the take dropped, as calibration does.</summary>
    private void CaptureFailed(Exception ex) => Dispatcher.UIThread.Post(() => Failed(ex));

    /// <summary>The take reached calibration's limit: it stops and runs, with the limit said (the timer's; tests call it).</summary>
    internal void TakeLimitReached() => Stop(limitReached: true);

    private void Stop(bool limitReached)
    {
        var hadFocus = _record.IsFocused && _recordKeyboardFocus; // Record is off while the steps run: keyboard focus moves to them, not to nowhere
        if (CloseMicrophone() is not { } audio) return;
        _run.Recorded(audio.Duration);
        Tell(limitReached ? HowItWorksText.LimitReached : "");
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        Running = RunAsync(audio, _cancel.Token);
        Render();
        if (hadFocus) _cards[1].Focus();
    }

    /// <summary>Esc, or the page left mid-recording: the recording is dropped, nothing runs.</summary>
    private void Cancel(string why)
    {
        CloseMicrophone(); // the audio is dropped: nothing is kept
        _run.End();
        Tell(why);
        Render();
    }

    private async Task RunAsync(CapturedAudio audio, CancellationToken token)
    {
        void Post(Action change) => Dispatcher.UIThread.Post(() =>
        {
            change();
            Render();
        });
        try
        {
            await Shell.DemonstrateAsync(audio, token, view => Post(() => _run.Pill(view)), progress => Post(() => _run.Apply(progress)));
            Post(() =>
            {
                _run.End();
                if (Status.Length == 0) Tell(HowItWorksText.Done);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Post(_run.End);
        }
        catch (Exception ex)
        {
            Shell.Log.Error($"See how it works: {ex.GetType().Name}: {ex.Message}");
            Post(() =>
            {
                _run.End();
                Tell(HowItWorksText.Failed(ex.Message));
            });
        }
    }

    /// <summary>One look at the level: the meter moves every tick, or at most twice a second with reduced motion (the welcome window's rate).</summary>
    internal void Tick()
    {
        if (_capture is null) return;
        var now = Clock();
        _meterPeak = Math.Max(_meterPeak, _capture.CurrentLevel);
        if (Shell.Platform.WindowFlags.ReducedMotion && now - _meterShownAt < MicrophoneStep.ReducedMotionMeterMs) return;
        _meter.Show(_meterPeak);
        MeterRedraws++;
        _meterPeak = 0;
        _meterShownAt = now;
    }

    /// <summary>
    /// With no microphone listed, Record is off and the one no-microphone sentence stands under it (calibration's rule); one
    /// plugged in turns it back on. True when there is one.
    /// </summary>
    private bool CheckMicrophone()
    {
        var none = MicrophoneChoices.List(Shell.Platform, why =>
        {
            if (!_listFailureLogged) Shell.Log.Warn($"Settings > See how it works: {why}");
            _listFailureLogged = true;
        }).Count == 0;
        if (none) Tell(NoSoundText.NoMicrophoneBalloon);
        else if (_noMicrophone && Status == NoSoundText.NoMicrophoneBalloon) Tell("");
        _noMicrophone = none;
        Render();
        return !none;
    }

    /// <summary>
    /// With no speech model ready, Record is off and the status says why: the welcome window's sentence while one loads, else
    /// <see cref="HowItWorksText.NoSpeechModel"/>; it clears when one is ready. True when one is.
    /// </summary>
    private bool CheckSpeech()
    {
        var ready = SpeechReady();
        if (!ready) Tell(Shell.SpeechLoading ? OnboardingText.TryLoading : HowItWorksText.NoSpeechModel);
        else if (!_speechReady && (Status == OnboardingText.TryLoading || Status == HowItWorksText.NoSpeechModel)) Tell("");
        _speechReady = ready;
        Render();
        return ready;
    }

    private void Failed(Exception ex)
    {
        Shell.Log.Error($"See how it works: {ex.GetType().Name}: {ex.Message}");
        if (_capture is not null) CloseMicrophone();
        _run.End();
        Tell(ex is NoMicrophoneException ? NoSoundText.NoMicrophoneBalloon : HowItWorksText.Failed(ex.Message));
        if (ex is NoMicrophoneException) CheckMicrophone();
        Render();
    }

    private void Tell(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    private void Render()
    {
        var recording = _capture is not null;
        _record.Content = recording ? HowItWorksText.StopLabel : HowItWorksText.RecordLabel;
        AutomationProperties.SetName(_record, recording ? HowItWorksText.StopName : HowItWorksText.RecordName);
        _record.IsEnabled = recording || (!_noMicrophone && _speechReady && Running is not { IsCompleted: false }); // Stop always works; one run at a time
        var stages = _run.Stages();
        for (var i = 0; i < _cards.Length; i++) _cards[i].Show(stages[i]);
    }

    /// <summary>
    /// One stage: a card whose body is a tab stop named with everything it shows, so the keyboard reaches each stage and a
    /// screen reader reads it whole, with a ring only while the focus came from the keyboard.
    /// </summary>
    // simplification: the focusable block repeats Settings > Help's private Readable (OtherPages.cs, another lane's file).
    // Retire it by moving Readable to Ui and using it here, once OtherPages.cs is free to edit.
    private sealed class StageCard
    {
        private readonly TextBlock _title = Ui.Text("", "heading");
        private readonly TextBlock _state = Ui.Text("", "caption");
        private readonly TextBlock _description = Ui.Text("", "caption");
        private readonly TextBlock _text = Ui.Text("");
        private readonly TextBlock _timing = Ui.Text("", "caption");
        private readonly TextBlock _note = Ui.Text("");
        private readonly Border _block;

        public StageCard(Control? extra)
        {
            AutomationProperties.SetHeadingLevel(_title, 2);
            // The stage's text and verdict are read out as they arrive (Border has no automation peer, so the TextBlocks carry it).
            AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);
            AutomationProperties.SetLiveSetting(_note, AutomationLiveSetting.Polite);
            _text.FontSize = 16;
            var body = Ui.Stack(6, Ui.Row(10, _title, _state), _description);
            if (extra is not null) body.Children.Add(extra);
            body.Children.AddRange([_text, _timing, _note]);
            _block = new Border
            {
                Focusable = true,
                IsTabStop = true,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6),
                Child = body,
            };
            _block.GotFocus += (_, e) =>
            {
                if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
                    _block[!Border.BorderBrushProperty] = new DynamicResourceExtension("LinkBrush");
            };
            _block.LostFocus += (_, _) =>
            {
                _block.ClearValue(Border.BorderBrushProperty);
                _block.BorderBrush = Brushes.Transparent;
            };
            Root = Ui.Card(_block);
        }

        public Border Root { get; }

        public void Focus() => _block.Focus(NavigationMethod.Tab);

        public void Show(StageView view)
        {
            _title.Text = view.Title;
            _state.Text = view.StateLabel;
            _description.Text = view.Description;
            Set(_text, view.Text);
            Set(_timing, view.Timing);
            Set(_note, view.Note);
            AutomationProperties.SetName(_block, string.Join(". ", new[] { view.Title, view.StateLabel, view.Text, view.Timing, view.Note }.Where(s => !string.IsNullOrEmpty(s))));
        }

        private static void Set(TextBlock block, string? text)
        {
            block.Text = text ?? "";
            block.IsVisible = !string.IsNullOrEmpty(text);
        }
    }
}
