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

    /// <summary>"Windows received: Right Alt down"; on a Mac "macOS received: …" (<see cref="PlatformWords.OsName"/>; APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block, row 6): do not reword).</summary>
    internal static string Received(PlatformWords words, string key) => $"{words.OsName} received: {key}";

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
    private readonly InputAccessLine? _access;
    private IKeyMonitor? _monitor;
    private IDisposable? _pause;
    private HotkeySpec? _spec;
    private bool _refusedForAccess;

    /// <param name="current">The hotkey configured now, or null/"" when none is set.</param>
    /// <param name="caption">The line above the box ("Press your hotkey").</param>
    /// <param name="prompt">What the box says while it listens and nothing has arrived yet.</param>
    /// <param name="showsAccessLine">Whether the box carries the input-access line and its Request access button after a refusal for want of the grant; false on a page that already shows the line (Settings &gt; General).</param>
    public HotkeyCaptureBox(AppShell shell, string? current, bool listenAtOnce, string caption = "Press your hotkey", string prompt = DictationPrompt, bool showsAccessLine = true)
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
        // A box that carries the grant line keeps it under the "received" line, shown only while the monitor was refused for want of it.
        _access = showsAccessLine ? new InputAccessLine(shell, showsItself: false) { IsVisible = false } : null;
        Content = _access is null
            ? Ui.Stack(8, Ui.Text(caption, "caption"), _box, Ui.Row(8, _light, _lightText, _listen))
            : Ui.Stack(8, Ui.Text(caption, "caption"), _box, _access, Ui.Row(8, _light, _lightText, _listen));
        if (listenAtOnce) AttachedToVisualTree += (_, _) => Start();
        AttachedToVisualTree += (_, _) => shell.Access.Changed += AccessChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            shell.Access.Changed -= AccessChanged;
            Stop();
        };
        Render();
    }

    /// <summary>
    /// The grant arrived while this box was waiting on it: listen again, as the user asked to before the refusal. Only a box that
    /// carries its own line (the Welcome hotkey step): on Settings &gt; General a refused Start raises no Started, so both boxes
    /// could still be waiting and both would resume, the fix box last; there the card's line is the cue and Change… starts one
    /// (merge lens, PR #82, 27/10/2026).
    /// </summary>
    private void AccessChanged()
    {
        if (_access is not null && _refusedForAccess && _shell.Access.State == InputAccessState.Granted && _monitor is null) Start();
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
            // Refused for want of the grant: the box says so and carries the Request access button; any other refusal keeps the log pointer.
            // A fresh check, not the shell's cached answer: the Welcome window runs before the shell installs its own listener, so
            // nothing has asked yet on a new install (merge lens, PR #82, 27/10/2026). It also starts the watch for the grant.
            _shell.Access.ListenerRefused();
            _refusedForAccess = _shell.Access.State == InputAccessState.Denied;
            if (_access is not null) _access.IsVisible = _refusedForAccess;
            _received.Text = _refusedForAccess ? InputAccessText.BoxDenied(_shell.Platform.Words)
                : "The system refused to report keys, so the hotkey cannot be checked here. The log has details.";
            return;
        }
        _monitor = monitor;
        _refusedForAccess = false;
        if (_access is not null) _access.IsVisible = false;
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
        _key.Text = _spec is not null ? HotkeyNames.Display(_spec) : Hotkey.Length > 0 ? Hotkey : "None";
        _received.Text = _monitor is null ? "Not listening. Press Change… to try a key."
            : _capture.LastReceived is { } last ? Received(_shell.Platform.Words, last)
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
