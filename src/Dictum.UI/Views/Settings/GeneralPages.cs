using Avalonia.Controls;
using Avalonia.Layout;
using Dictum.Hotkey;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>Settings → General: on/off, launch at login, the hotkey capture box (SPEC §20), what shows on screen, pasting.</summary>
internal sealed class GeneralPage : SettingsPage
{
    private static readonly string[] IndicatorLabels = ["Off", "Orb", "Edge", "Both"];
    private static readonly string[] CornerLabels = ["Bottom right", "Bottom left", "Top right", "Top left"];

    private readonly ToggleSwitch _enabled;
    private readonly ToggleSwitch _login;
    private readonly TextBlock _loginHint = Ui.Text("", "hint");
    private readonly HotkeyCaptureBox _capture;
    private readonly Button _saveHotkey;
    private readonly TextBox _fixHotkey = new() { Width = 200, Watermark = "none" };
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
        _capture = new HotkeyCaptureBox(shell, shell.Settings.Hotkey, listenAtOnce: false);
        _saveHotkey = Ui.Button("Use this hotkey", "primary", SaveHotkey);
        _capture.Captured += key => _saveHotkey.IsEnabled = key != Shell.Settings.Hotkey;
        var saveFix = Ui.Button("Save", "secondary", SaveFixHotkey);
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

        Compose(
            Ui.Card(Ui.Stack(4,
                Ui.Line("Dictation on", _enabled),
                Ui.Stack(0, Ui.Line("Launch at login", _login), _loginHint)), "Dictation"),
            Ui.Card(Ui.Stack(12,
                Ui.Text("Hold the hotkey to record and let go to paste; tap it to start hands-free and tap again to stop. Esc cancels."),
                _capture,
                Ui.Row(8, _saveHotkey),
                Ui.Text(HotkeyCheckWindow.MacNote, "caption"),
                Ui.Line("Fix last dictation hotkey", Ui.Row(8, _fixHotkey, saveFix), "Optional. A key the dictation hotkey does not use, for example F8.")), "Hotkey"),
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
        _saveHotkey.IsEnabled = _capture.Hotkey != s.Hotkey;
        _fixHotkey.Text = s.FixHotkey ?? "";
        Ui.SetQuietly(_pill, s.Pill.Enabled);
        _indicator.Select(Math.Max(0, Array.IndexOf(IndicatorSettings.Modes, s.Indicator.Mode)));
        _corner.SelectedIndex = Math.Max(0, Array.IndexOf(IndicatorSettings.Corners, s.Indicator.OrbCorner));
        _corner.IsEnabled = s.Indicator.ShowsOrb;
        Ui.SetQuietly(_sounds, s.Sounds.Enabled);
        _insert.SelectedIndex = s.Insert.Method == InsertSettings.Type ? 1 : 0;
        Ui.SetQuietly(_trailing, s.Insert.TrailingSpace);
        Ui.SetQuietly(_restore, s.Insert.RestoreClipboard);
    }

    public override void Leave() => _capture.Stop();

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
            Say($"{spec.Display} shares keys with the fix-last hotkey ({fixSpec.Display}); one press would do both. Choose another key.");
            return;
        }
        _capture.Stop();
        Write(("hotkey", Json(key)));
        Say($"The hotkey is now {spec.Display}.");
    }

    private void SaveFixHotkey()
    {
        var text = _fixHotkey.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            Write(("fix_hotkey", null));
            Say("The fix-last hotkey is off.");
            return;
        }
        if (!HotkeyParser.TryParse(text, out var spec, out var error))
        {
            Say(error);
            return;
        }
        if (HotkeyParser.TryParse(Shell.Settings.Hotkey, out var main, out _) && spec.CanFireTogether(main))
        {
            Say($"{spec.Display} shares keys with the dictation hotkey ({main.Display}); one press would do both. Choose another key.");
            return;
        }
        Write(("fix_hotkey", Json(text)));
        Say($"{spec.Display} now opens Fix last dictation.");
    }
}

/// <summary>Settings → Mode (SPEC §16.1): Light or Full, resolved for this PC's hardware.</summary>
internal sealed class ModePage : SettingsPage
{
    private readonly Segments _mode;
    private readonly TextBlock _custom = Ui.Text("", "caption");
    private readonly TextBlock _table = Ui.Text("");

    public ModePage(AppShell shell) : base(shell, "Mode")
    {
        _mode = new Segments(["Light", "Full"], -1, i => Run(() => Shell.Personal.SelectPreset(i == 0 ? ModePreset.Light : ModePreset.Full)), "Mode");
        _mode.HorizontalAlignment = HorizontalAlignment.Left;
        _mode.Width = 320;
        Compose(
            Ui.Card(Ui.Stack(10, _mode, _custom, _table,
                Ui.Text("Choosing a mode writes its models into settings.json, where you can still change each one, and asks before downloading a model that is missing.", "caption")), "Light or Full"));
    }

    protected override void Redraw()
    {
        var gpu = Shell.GpuMachine;
        var preset = Shell.Personal.CurrentPreset;
        _mode.Select(preset switch { ModePreset.Light => 0, ModePreset.Full => 1, _ => -1 });
        _custom.Text = preset == ModePreset.Custom ? "Custom: your settings match neither mode." : $"In use: {preset}.";
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
        var devices = Shell.Platform.ListCaptureDevices();
        _ids = [null, .. devices.Select(d => (string?)d.Name)];
        var labels = new List<string> { "System default" };
        labels.AddRange(devices.Select(d => d.IsDefault ? $"{d.Name} (default)" : d.Name));
        if (Shell.Settings.Audio.Device is { } chosen && !_ids.Contains(chosen))
        {
            _ids.Add(chosen);
            labels.Add($"{chosen} (not connected)");
        }
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
