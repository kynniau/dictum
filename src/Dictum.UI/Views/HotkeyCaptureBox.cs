using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Hotkey;
using Dictum.Platform;
using Dictum.UI.Shell;

namespace Dictum.UI.Views;

/// <summary>
/// "Press your hotkey" (SPEC §20): while listening it shows exactly what the system received (key name and side, or a
/// mouse button) and a "Hold to test" light that is on while every key of the chosen hotkey is held, so a key that never
/// arrives (a Mac over Remote Desktop has no right Ctrl) is obvious in seconds. Both hotkeys are paused while it listens, so
/// holding the key here does not start a dictation. Pressing a new key, chord or mouse button chooses it; <see cref="Hotkey"/>
/// is the choice ("" when none, for the optional fix-last hotkey).
/// </summary>
internal sealed class HotkeyCaptureBox : UserControl, IDisposable
{
    public const string DictationPrompt = "Listening: press the key or mouse button you want to hold for dictation.";

    private readonly AppShell _shell;
    private readonly HotkeyCapture _capture = new();
    private readonly string _caption;
    private readonly string _prompt;
    private readonly TextBlock _key = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _received = Ui.Text("", "caption");
    private readonly Ellipse _light = new() { Width = 12, Height = 12 };
    private readonly TextBlock _lightText = Ui.Text("Hold to test", "caption");
    private readonly Button _listen;
    private readonly Border _box;
    private IKeyMonitor? _monitor;
    private IDisposable? _pause;
    private HotkeySpec? _spec;

    /// <param name="current">The hotkey configured now, or null/"" when none is set.</param>
    /// <param name="caption">The line above the box ("Press your hotkey").</param>
    /// <param name="prompt">What the box says while it listens and nothing has arrived yet.</param>
    public HotkeyCaptureBox(AppShell shell, string? current, bool listenAtOnce, string caption = "Press your hotkey", string prompt = DictationPrompt)
    {
        _shell = shell;
        _caption = caption;
        _prompt = prompt;
        Hotkey = current ?? "";
        HotkeyParser.TryParse(Hotkey, out _spec, out _);
        _listen = Ui.Button("Change…", "secondary", () =>
        {
            if (_monitor is null) Start();
            else Stop();
        });
        _box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Child = Ui.Stack(4, _key, _received),
        };
        _box[!Border.BackgroundProperty] = new DynamicResourceExtension("ControlBrush");
        AutomationProperties.SetName(_box, caption);
        AutomationProperties.SetLiveSetting(_received, AutomationLiveSetting.Polite);
        Content = Ui.Stack(8, Ui.Text(caption, "caption"), _box, Ui.Row(8, _light, _lightText, _listen));
        if (listenAtOnce) AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
        Render();
    }

    /// <summary>The hotkey chosen: the configured one until a new key is pressed; "" when none.</summary>
    public string Hotkey { get; private set; }

    /// <summary>Raised on the UI thread when a press names a new hotkey.</summary>
    public event Action<string>? Captured;

    /// <summary>Raised when the box starts listening (a page with two boxes stops the other one).</summary>
    public event Action? Started;

    public bool Listening => _monitor is not null;

    public void Start()
    {
        if (_monitor is not null) return;
        _pause = _shell.PauseHotkey();
        _capture.Reset();
        var monitor = _shell.Platform.CreateKeyMonitor(e => Dispatcher.UIThread.Post(() => OnKey(e)), _shell.Log);
        if (!monitor.Start())
        {
            monitor.Dispose();
            _pause.Dispose();
            _pause = null;
            _received.Text = "The system refused to report keys, so the hotkey cannot be checked here. The log has details.";
            return;
        }
        _monitor = monitor;
        Render();
        Started?.Invoke();
    }

    public void Stop()
    {
        _monitor?.Dispose();
        _monitor = null;
        _pause?.Dispose();
        _pause = null;
        _capture.Reset();
        Render();
    }

    /// <summary>Shows <paramref name="current"/> as the choice again (after the setting was saved or switched off).</summary>
    public void Reset(string? current)
    {
        Hotkey = current ?? "";
        HotkeyParser.TryParse(Hotkey, out _spec, out _);
        Render();
    }

    private void OnKey(RawKeyEvent e)
    {
        if (_monitor is null) return;
        if (_capture.Feed(e) is { } hotkey && hotkey != Hotkey)
        {
            Hotkey = hotkey;
            HotkeyParser.TryParse(hotkey, out _spec, out _);
            Captured?.Invoke(hotkey);
        }
        Render();
    }

    private void Render()
    {
        _key.Text = _spec?.Display ?? (Hotkey.Length > 0 ? Hotkey : "None");
        _received.Text = _monitor is null ? "Not listening. Press Change… to try a key."
            : _capture.LastReceived is { } last ? $"Windows received: {last}"
            : _prompt;
        var held = _monitor is not null && _capture.IsHeld(_spec);
        _light[!Shape.FillProperty] = new DynamicResourceExtension(held ? "DoneBrush" : "MeterOffBrush");
        _lightText.Text = held ? "Held: Dictum sees this key" : "Hold to test";
        _box[!Border.BorderBrushProperty] = new DynamicResourceExtension(_monitor is null ? "ControlBorderBrush" : "AccentBrush");
        _listen.Content = _monitor is null ? "Change…" : "Stop listening";
        // Two boxes on one page each have this button: its accessible name says which hotkey it changes.
        AutomationProperties.SetName(_listen, _monitor is null ? $"{_caption}: change" : $"{_caption}: stop listening");
    }

    public void Dispose() => Stop();
}
