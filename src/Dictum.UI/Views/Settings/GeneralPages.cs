using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Dictum.Hotkey;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>Settings → General: on/off, launch at login, the hotkey capture box (SPEC §20), hands-free, what shows on screen, pasting.</summary>
internal sealed class GeneralPage : SettingsPage
{
    private static readonly string[] IndicatorLabels = ["Off", "Orb", "Edge", "Both"];
    private static readonly string[] CornerLabels = ["Bottom right", "Bottom left", "Top right", "Top left"];

    private readonly ToggleSwitch _enabled;
    private readonly ToggleSwitch _login;
    private readonly TextBlock _loginHint = Ui.Text("", "hint");
    // The sentence pointing at hands_free_max_seconds in settings.json went with the Hands-free card below (16/10/2026).
    public const string MouseNote = "A mouse button works too: hold the right or middle button or a side button (Mouse 4, Mouse 5), and double-tap it for " +
                                    "hands-free. The left button cannot be used. " +
                                    "While Dictum is on and a speech model is ready, that button no longer reaches other apps at all (no right-click menu, no middle-click, " +
                                    "no Back); a side button you do not use is the best choice.";

    /// <summary>The hands-free auto-stop field's label. PROPOSED 16/10/2026, awaiting the product owner's approval.</summary>
    public const string HandsFreeLabel = "Hands-free stops after";

    /// <summary>The field's help text. PROPOSED 16/10/2026, awaiting the product owner's approval.</summary>
    public const string HandsFreeHelp = "Hands-free dictation types each sentence as you pause, so a long session reads back as you go. It stops by itself after this long.";

    private readonly HandsFreeLimit _handsFreeLimit;
    /// <summary>The fix-last listener has no speech model to wait for, so its button is swallowed whenever Dictum is on.</summary>
    private const string FixMouseNote = "While Dictum is on, a mouse button chosen here no longer reaches other apps at all.";
    private const string FixCaption = "Press the key for Fix last dictation";
    private const string FixPrompt = "Listening: press the key or mouse button that should open Fix last dictation.";

    private readonly HotkeyCaptureBox _capture;
    private readonly Button _saveHotkey;
    private readonly HotkeyCaptureBox _fixCapture;
    private readonly Button _saveFix;
    private readonly Button _clearFix;
    private string _savedHotkey; // what settings.json held at the last redraw, per box: a box still showing it follows the file
    private string _savedFix;
    private readonly ToggleSwitch _pill;
    private readonly Segments _indicator;
    private readonly ComboBox _corner = new() { ItemsSource = CornerLabels, Width = 200 };
    private readonly ToggleSwitch _sounds;
    private readonly ComboBox _insert = new() { ItemsSource = new[] { "Paste (Ctrl+V)", "Type the characters" }, Width = 200 };
    private readonly ToggleSwitch _trailing;
    private readonly ToggleSwitch _restore;

    public GeneralPage(AppShell shell) : base(shell, "General")
    {
        _enabled = Ui.Switch(true, on => Run(() => Shell.SetEnabled(on)));
        _login = Ui.Switch(false, on => _ = SetLoginAsync(on));
        _savedHotkey = shell.Settings.Hotkey;
        _savedFix = shell.Settings.FixHotkey ?? "";
        _capture = new HotkeyCaptureBox(shell, shell.Settings.Hotkey, listenAtOnce: false);
        _saveHotkey = Ui.Button("Use this hotkey", "primary", SaveHotkey);
        _capture.Captured += key => _saveHotkey.IsEnabled = key != Shell.Settings.Hotkey;
        _fixCapture = new HotkeyCaptureBox(shell, shell.Settings.FixHotkey, listenAtOnce: false, FixCaption, FixPrompt);
        _saveFix = Ui.Button("Use for Fix last dictation", "primary", SaveFixHotkey);
        _clearFix = Ui.Button("Turn off", "secondary", ClearFixHotkey);
        _fixCapture.Captured += key => _saveFix.IsEnabled = key != (Shell.Settings.FixHotkey ?? "");
        _capture.Started += _fixCapture.Stop; // one box listens at a time, so a press names one hotkey, never both
        _fixCapture.Started += _capture.Stop;
        _pill = Ui.Switch(true, on => Write(("pill.enabled", Json(on))));
        _indicator = new Segments(IndicatorLabels, 0, i => Write(("indicator.mode", Json(IndicatorSettings.Modes[i]))), "Big recording indicator");
        _corner.SelectionChanged += (_, _) =>
        {
            if (_corner.SelectedIndex >= 0) Write(("indicator.orb_corner", Json(IndicatorSettings.Corners[_corner.SelectedIndex])));
        };
        _sounds = Ui.Switch(false, on => Write(("sounds.enabled", Json(on))));
        _insert.SelectionChanged += (_, _) =>
        {
            if (_insert.SelectedIndex >= 0) Write(("insert.method", Json(_insert.SelectedIndex == 0 ? InsertSettings.Paste : InsertSettings.Type)));
        };
        _trailing = Ui.Switch(true, on => Write(("insert.trailing_space", Json(on))));
        _restore = Ui.Switch(false, on => Write(("insert.restore_clipboard", Json(on))));
        _handsFreeLimit = new HandsFreeLimit(shell.Settings.HandsFreeMaxSeconds, seconds =>
        {
            if (seconds != Shell.Settings.HandsFreeMaxSeconds) Write(("hands_free_max_seconds", Json(seconds)));
        });

        Compose(
            Ui.Card(Ui.Stack(4,
                Ui.Line("Dictation on", _enabled),
                Ui.Stack(0, Ui.Line("Launch at login", _login), _loginHint)), "Dictation"),
            Ui.Card(Ui.Stack(12,
                Ui.Text("Hold the hotkey to record and let go to paste; tap it to start hands-free and tap again to stop. Esc cancels."),
                _capture,
                Ui.Row(8, _saveHotkey),
                Ui.Text(MouseNote, "caption"),
                Ui.Text(HotkeyCheck.MacNote, "caption")), "Hotkey"),
            Ui.Card(Ui.Stack(10,
                Ui.Line(HandsFreeLabel, _handsFreeLimit.Value, HandsFreeHelp),
                _handsFreeLimit.Slider,
                _handsFreeLimit.Presets), "Hands-free"),
            Ui.Card(Ui.Stack(12,
                Ui.Text("Optional: a key or mouse button that opens Fix last dictation. It must not share a key with the dictation hotkey; F8 or Mouse 5 work well. " + FixMouseNote),
                _fixCapture,
                Ui.Row(8, _saveFix, _clearFix)), "Fix last dictation hotkey"),
            Ui.Card(Ui.Stack(4,
                Ui.Line("Pill", _pill, "The small status bar above the taskbar."),
                Ui.Line("Big recording indicator", _indicator, "Orb: a badge in a corner. Edge: a red glow around the screen."),
                Ui.Line("Orb corner", _corner),
                Ui.Line("Start and stop sounds", _sounds)), "On screen"),
            Ui.Card(Ui.Stack(4,
                Ui.Line("Insert text by", _insert),
                Ui.Line("Add a space after each dictation", _trailing),
                Ui.Line("Put the clipboard back afterwards", _restore, "Off: the dictation stays on the clipboard.")), "Pasting"));
    }

    protected override void Redraw()
    {
        var s = Shell.Settings;
        Ui.SetQuietly(_enabled, s.Enabled);
        Ui.SetQuietly(_login, Shell.Platform.Startup.IsEnabled());
        _loginHint.Text = $"Uses the {Shell.Platform.Startup.Mechanism}.";
        // A box the user has not captured a new key in follows settings.json (an edit in the file, a reload); one holding a
        // new capture keeps it until it is saved.
        if (!_capture.Listening && _capture.Hotkey == _savedHotkey) _capture.Reset(s.Hotkey);
        _savedHotkey = s.Hotkey;
        if (!_fixCapture.Listening && _fixCapture.Hotkey == _savedFix) _fixCapture.Reset(s.FixHotkey);
        _savedFix = s.FixHotkey ?? "";
        _saveHotkey.IsEnabled = _capture.Hotkey != s.Hotkey;
        _saveFix.IsEnabled = _fixCapture.Hotkey != (s.FixHotkey ?? "");
        _clearFix.IsEnabled = s.FixHotkey is not null;
        Ui.SetQuietly(_pill, s.Pill.Enabled);
        _indicator.Select(Math.Max(0, Array.IndexOf(IndicatorSettings.Modes, s.Indicator.Mode)));
        _corner.SelectedIndex = Math.Max(0, Array.IndexOf(IndicatorSettings.Corners, s.Indicator.OrbCorner));
        _corner.IsEnabled = s.Indicator.ShowsOrb;
        Ui.SetQuietly(_sounds, s.Sounds.Enabled);
        _insert.SelectedIndex = s.Insert.Method == InsertSettings.Type ? 1 : 0;
        Ui.SetQuietly(_trailing, s.Insert.TrailingSpace);
        Ui.SetQuietly(_restore, s.Insert.RestoreClipboard);
        _handsFreeLimit.Show(s.HandsFreeMaxSeconds); // not while a slider value waits to be written: that one is newer
    }

    public override void Leave()
    {
        _capture.Stop();
        _fixCapture.Stop();
        _handsFreeLimit.Flush(); // a slider or typed value still settling is written, not lost
    }

    private async Task SetLoginAsync(bool on)
    {
        if (Refreshing) return;
        var problem = await Shell.Platform.Startup.SetEnabledAsync(on);
        Say(problem ?? (on ? "Dictum starts when you sign in." : "Dictum no longer starts when you sign in."));
        Refresh();
    }

    private void SaveHotkey()
    {
        var key = _capture.Hotkey;
        if (!HotkeyParser.TryParse(key, out var spec, out var error))
        {
            Say(error);
            return;
        }
        if (Shell.Settings.FixHotkey is { } fix && HotkeyParser.TryParse(fix, out var fixSpec, out _) && spec.CanFireTogether(fixSpec))
        {
            Say($"{HotkeyNames.Display(spec)} shares keys with the fix-last hotkey ({HotkeyNames.Display(fixSpec)}); one press would do both. Choose another key.");
            return;
        }
        _capture.Stop();
        Write(("hotkey", Json(key)));
        Say($"The hotkey is now {HotkeyNames.Display(spec)}.");
    }

    private void SaveFixHotkey()
    {
        var text = _fixCapture.Hotkey;
        if (text.Length == 0)
        {
            Say("Press Change… and then the key you want, or Turn off.");
            return;
        }
        if (!HotkeyParser.TryParse(text, out var spec, out var error))
        {
            Say(error);
            return;
        }
        if (HotkeyParser.TryParse(Shell.Settings.Hotkey, out var main, out _) && spec.CanFireTogether(main))
        {
            Say($"{HotkeyNames.Display(spec)} shares keys with the dictation hotkey ({HotkeyNames.Display(main)}); one press would do both. Choose another key.");
            return;
        }
        _fixCapture.Stop();
        Write(("fix_hotkey", Json(text)));
        Say($"{HotkeyNames.Display(spec)} now opens Fix last dictation.");
    }

    private void ClearFixHotkey()
    {
        _fixCapture.Stop();
        _fixCapture.Reset(null);
        Write(("fix_hotkey", null));
        Say("The fix-last hotkey is off.");
    }
}

/// <summary>
/// Settings → General → Hands-free (product owner decisions 3 and 5, 16/10/2026): "Hands-free stops after" as a number with
/// its unit, a slider from 30 s to 60 min and quick picks of 1, 2, 5, 10, 30 and 60 min, all showing one value in seconds
/// (hands_free_max_seconds). Every control is reached and used from the keyboard and has a name for screen readers. A pick
/// is reported at once. The slider reports once it settles (<see cref="SettleMs"/> without a change). The number box
/// reports only on Enter, when it loses focus or when the page is left, never on a pause: its value does not change while
/// the digits typed clamp to the same one ("1" and "12" are both 30 s), so a pause timer would count from the first key
/// and write 30 s at ordinary typing speed. Leaving the page reports either one still waiting; all clamped to 30 s–60 min.
/// So a drag, or typing "120", writes settings.json once: never each step, nor the 30 s that "1" is clamped to on the way
/// (a reload hands every write to the state machine). Changing the unit shows the same time and reports nothing.
/// </summary>
internal sealed class HandsFreeLimit
{
    // The quick-pick labels, the unit names and the card's "Hands-free" title are this change's own wording (16/10/2026),
    // listed for the product owner beside the proposed strings.
    public static readonly int[] PresetMinutes = [1, 2, 5, 10, 30, 60];
    public static readonly string[] Units = ["minutes", "seconds"];

    /// <summary>
    /// How long the slider stays unchanged before its value is reported. The number box has no pause: it reports on Enter,
    /// when it loses focus or when the page is left.
    /// </summary>
    public const int SettleMs = 400;

    private const int Min = AppSettings.HandsFreeMinimumSeconds;
    private const int Max = AppSettings.HandsFreeMaximumSeconds;

    private readonly Action<int> _chosen;
    private readonly DispatcherTimer _settle;
    private bool _quiet; // the controls are being set from a value, not by the user
    private bool _typed; // the number box holds a value that waits for Enter, focus leaving the box or the page being left

    /// <param name="seconds">The value to show first.</param>
    /// <param name="chosen">Told each value the user settles on (already clamped).</param>
    public HandsFreeLimit(int seconds, Action<int> chosen)
    {
        _chosen = chosen;
        Box = new NumericUpDown { Width = 110, FormatString = "0.#", ParsingNumberStyle = NumberStyles.Number, ClipValueToMinMax = true };
        AutomationProperties.SetName(Box, GeneralPage.HandsFreeLabel);
        Unit = new ComboBox { ItemsSource = Units, Width = 120 };
        AutomationProperties.SetName(Unit, $"{GeneralPage.HandsFreeLabel}: unit");
        Value = Ui.Row(8, Box, Unit);
        Slider = new Slider { Minimum = Min, Maximum = Max, SmallChange = 30, LargeChange = 300, TickFrequency = 30, IsSnapToTickEnabled = true, MinWidth = 360 };
        AutomationProperties.SetName(Slider, $"{GeneralPage.HandsFreeLabel}, in seconds");
        Presets = new Segments([.. PresetMinutes.Select(m => $"{m} min")], -1, i => Choose(PresetMinutes[i] * 60), GeneralPage.HandsFreeLabel)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 420,
        };
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(SettleMs), DispatcherPriority.Background, (_, _) => Flush()) { IsEnabled = false };

        Box.ValueChanged += (_, e) =>
        {
            if (_quiet || e.NewValue is not decimal value) return;
            // Typed or stepped: the box is left as the user has it and its value waits for Enter, focus leaving the box or the
            // page being left (Flush). Not for a pause: ValueChanged does not fire while the clamped value stays the same ("1"
            // and "12" both clamp to 30 s), so a timer started here would count from the first key and write 30 s mid-number.
            Seconds = (int)Math.Round(value * UnitSeconds, MidpointRounding.AwayFromZero);
            _settle.Stop(); // a slider value still settling is older than this one
            _typed = true;
        };
        Box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) Flush(); // after the box's own Enter handling has committed the text
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Box.LostFocus += (_, _) => Flush();
        Unit.SelectionChanged += (_, _) =>
        {
            if (!_quiet && Unit.SelectedIndex >= 0) Display(Seconds, keepUnit: true);
        };
        Slider.ValueChanged += (_, e) =>
        {
            if (_quiet) return;
            Display((int)Math.Round(e.NewValue), keepUnit: false, moveSlider: false);
            _settle.Stop();
            _settle.Start();
        };
        Display(seconds, keepUnit: false);
    }

    public NumericUpDown Box { get; }

    public ComboBox Unit { get; }

    /// <summary>The number and its unit, side by side.</summary>
    public Control Value { get; }

    public Slider Slider { get; }

    public Segments Presets { get; }

    /// <summary>The time the controls show, in seconds.</summary>
    public int Seconds { get; private set; }

    /// <summary>A slider value is settling or a typed one is waiting to be reported.</summary>
    public bool Settling => _settle.IsEnabled || _typed;

    private int UnitSeconds => Unit.SelectedIndex == 1 ? 1 : 60;

    /// <summary>Shows the value in use (settings.json), unless a slider or typed value is still settling: that one is newer.</summary>
    public void Show(int seconds)
    {
        if (!Settling) Display(seconds, keepUnit: false);
    }

    /// <summary>Reports a value still waiting now (the slider's timer, Enter or focus leaving the box, the page being left).</summary>
    public void Flush()
    {
        if (Settling) Choose(Seconds);
    }

    private void Choose(int seconds)
    {
        _settle.Stop();
        _typed = false; // a pick, or the flush: nothing older is left waiting
        var clamped = Math.Clamp(seconds, Min, Max);
        Display(clamped, keepUnit: true);
        _chosen(clamped);
    }

    /// <summary>
    /// Sets every control to <paramref name="seconds"/>. The unit stays as the user chose it when it shows the time exactly
    /// (minutes in halves, seconds always); otherwise whole minutes show in minutes and anything else in seconds.
    /// </summary>
    private void Display(int seconds, bool keepUnit, bool moveSlider = true)
    {
        _quiet = true;
        try
        {
            Seconds = seconds;
            var minutes = keepUnit && Unit.SelectedIndex >= 0
                ? Unit.SelectedIndex == 0
                : Unit.SelectedIndex switch
                {
                    0 when seconds % 30 == 0 => true,
                    1 => false,
                    _ => seconds % 60 == 0,
                };
            Unit.SelectedIndex = minutes ? 0 : 1;
            (Box.Minimum, Box.Maximum, Box.Increment) = minutes ? (Min / 60m, Max / 60m, 1m) : (Min, Max, 30m);
            Box.Value = minutes ? seconds / 60m : seconds;
            if (moveSlider) Slider.Value = seconds;
            Presets.Select(seconds % 60 == 0 ? Array.IndexOf(PresetMinutes, seconds / 60) : -1);
        }
        finally
        {
            _quiet = false;
        }
    }
}

/// <summary>Settings → Mode (SPEC §16.1): Light or Full, resolved for this PC's hardware.</summary>
internal sealed class ModePage : SettingsPage
{
    private readonly Segments _mode;
    private readonly TextBlock _custom = Ui.Text("", "caption");
    private readonly TextBlock _table = Ui.Text("");
    private readonly TextBlock _languageNote = Ui.Text("", "caption");

    public ModePage(AppShell shell) : base(shell, "Mode")
    {
        _mode = new Segments(["Light", "Full"], -1, i => Run(() => Shell.Personal.SelectPreset(i == 0 ? ModePreset.Light : ModePreset.Full)), "Mode");
        _mode.HorizontalAlignment = HorizontalAlignment.Left;
        _mode.Width = 320;
        Compose(
            Ui.Card(Ui.Stack(10, _mode, _custom, _table, _languageNote,
                Ui.Text("Choosing a mode writes its models into settings.json, where you can still change each one, and asks before downloading a model that is missing.", "caption")), "Light or Full"));
    }

    protected override void Redraw()
    {
        var gpu = Shell.GpuMachine;
        var preset = Shell.Personal.CurrentPreset;
        _mode.Select(preset switch { ModePreset.Light => 0, ModePreset.Full => 1, _ => -1 });
        _custom.Text = preset == ModePreset.Custom ? "Custom: your settings match neither mode." : $"In use: {preset}.";
        var w = Shell.Settings.Whisper;
        _languageNote.Text = Dictum.Transcribe.WhisperLanguages.ModeNote(w.Language, w.Translate, gpu) ?? "";
        _languageNote.IsVisible = _languageNote.Text.Length > 0;
        _table.Text = $"On this PC ({(gpu ? "a GPU runtime is loaded" : "CPU only")}):\n" +
                      $"• {ModePresets.Label(ModePreset.Light, gpu)}: quickest, lightest on memory.\n" +
                      $"• {ModePresets.Label(ModePreset.Full, gpu)}: most accurate, with AI clean-up.";
    }
}

/// <summary>Settings → Microphone: the device, and a five-second test with a live meter. Nothing is recorded or kept.</summary>
internal sealed class MicrophonePage : SettingsPage
{
    private readonly ComboBox _device = new() { MinWidth = 360 };
    private readonly Meter _meter = new(22);
    private readonly Button _test;
    private readonly TextBlock _result = Ui.Text("", "caption");
    private List<string?> _ids = [];
    private bool _listFailureLogged;
    private CancellationTokenSource? _testing;

    public MicrophonePage(AppShell shell) : base(shell, "Microphone")
    {
        _device.SelectionChanged += (_, _) =>
        {
            if (Refreshing || _device.SelectedIndex < 0 || _device.SelectedIndex >= _ids.Count) return;
            var id = _ids[_device.SelectedIndex];
            if (id != Shell.Settings.Audio.Device) Write(("audio.device", Json(id)));
        };
        _test = Ui.Button("Test microphone", "secondary", () => _ = TestAsync());
        Compose(
            Ui.Card(Ui.Stack(10, Ui.Line("Microphone", _device), Ui.Row(12, _test, _meter), _result), "Input"),
            Ui.Card(Ui.Text("Over Remote Desktop the microphone is compressed on the way in, which costs some accuracy; a headset close to " +
                            "the mouth helps most. Calibrate my voice measures which model hears you best on this connection."), "Remote Desktop"));
    }

    protected override void Redraw()
    {
        // The guarded lister (lens on 4abba54): with the audio service stopped the list threw here and the page failed to draw.
        var devices = MicrophoneChoices.List(Shell.Platform, why =>
        {
            if (!_listFailureLogged) Shell.Log.Warn($"Settings > Microphone: {why}");
            _listFailureLogged = true; // once per page: a download redraws it many times a second
        });
        (_ids, var labels) = MicrophoneChoices.Build(devices, Shell.Settings.Audio.Device);
        _device.ItemsSource = labels;
        _device.SelectedIndex = Math.Max(0, _ids.IndexOf(Shell.Settings.Audio.Device));
    }

    private async Task TestAsync()
    {
        if (_testing is not null)
        {
            _testing.Cancel();
            return;
        }
        if (!Shell.DictationIdle)
        {
            _result.Text = "A dictation is in progress; test again when it has finished.";
            return;
        }
        _testing = new CancellationTokenSource();
        _test.Content = "Stop test";
        _result.Text = "Listening for five seconds: say a few words.";
        try
        {
            var peak = await Shell.TestMicrophoneAsync(Shell.Settings.Audio.Device, level => Avalonia.Threading.Dispatcher.UIThread.Post(() => _meter.Show(level)), _testing.Token);
            _result.Text = peak >= Shell.Settings.SilenceThreshold
                ? "Dictum heard you. If the bars barely moved, move the microphone closer or raise its level in Windows sound settings."
                : "Dictum heard nothing above the silence threshold. Check the device and that it is not muted.";
        }
        catch (Exception ex)
        {
            _result.Text = $"The microphone could not be opened: {ex.Message}";
        }
        finally
        {
            _testing.Dispose();
            _testing = null;
            _test.Content = "Test microphone";
            _meter.Show(null);
        }
    }

    public override void Leave() => _testing?.Cancel();
}
