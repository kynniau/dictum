using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Core;
using Dictum.Platform;

namespace Dictum.UI.Overlays;

/// <summary>
/// The pill (SPEC §7, docs/design/Pill.dc.html): bottom-centre of the monitor with the focused window, 48 px above the
/// taskbar. Never activates and is click-through (see <see cref="Overlay"/>). Recording: the breathing red dot and five
/// bars following the microphone; working: a turning arc (blue transcribing, amber polishing); done: a green check with
/// the word count; cancelled: a grey cross. Under reduced motion the dot does not breathe, the arc does not turn and
/// nothing fades.
/// </summary>
public partial class PillWindow : Window
{
    private const double AboveTaskbar = 48;
    private static readonly double[] BarShape = [0.4, 0.9, 0.6, 1.0, 0.35]; // the design's 6/14/9/16/5 px at full level
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2457E6"));
    private static readonly IBrush BlueTrack = new SolidColorBrush(Color.Parse("#592457E6"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IBrush AmberTrack = new SolidColorBrush(Color.Parse("#59F5A524"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#30A46C"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#5B6B85"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5484D"));

    private readonly IPlatform? _platform;
    private readonly DispatcherTimer _frame;
    private readonly DispatcherTimer _flash;
    private readonly Rectangle[] _bars;
    private Func<float> _level = () => 0f;
    private CancellationTokenSource? _motion;
    private PillState _state = new(PillView.Hidden, 0);
    private PillView _resumeAfterBusy = PillView.Hidden;
    private double _spin;

    public PillWindow() : this(null)
    {
    }

    public PillWindow(IPlatform? platform)
    {
        _platform = platform;
        InitializeComponent();
        _bars = [.. Meter.Children.OfType<Rectangle>()];
        Overlay.Prepare(this, platform);
        _frame = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnFrame()) { IsEnabled = false };
        _flash = new DispatcherTimer { IsEnabled = false };
        _flash.Tick += (_, _) => OnFlashElapsed();
        SpinnerArc.RenderTransform = new RotateTransform();
    }

    public PillView View => _state.View;

    /// <summary>The label and the detail as shown, for tests and the log.</summary>
    public string Text => Label.Text + (Detail.IsVisible ? " " + Detail.Text : "");

    private bool ReducedMotion => _platform?.WindowFlags.ReducedMotion ?? false;

    /// <summary>Where the level meter reads the current input RMS from.</summary>
    public void SetLevelSource(Func<float> level) => _level = level;

    public void Present(PillState state)
    {
        if (state.View == PillView.Hidden)
        {
            _flash.Stop();
            _state = state;
            FadeOut();
            return;
        }
        if (state.View == PillView.Busy)
        {
            if (_state.View == PillView.Busy) return;
            _resumeAfterBusy = PillText.IsProcessing(_state.View) ? _state.View : PillView.Hidden;
            state = state with { CaptureStartTickMs = _state.CaptureStartTickMs };
        }
        _state = state;
        Render();
        Pill.Transitions = Overlay.Fade(ReducedMotion);
        if (!IsVisible)
        {
            Pill.Opacity = 1;
            PlaceOnActiveScreen();
            Show();
        }
        Pill.Opacity = 1;
        _flash.Stop();
        if (PillText.FlashMs(state.View) is int ms)
        {
            _flash.Interval = TimeSpan.FromMilliseconds(ms);
            _flash.Start();
        }
        _frame.IsEnabled = PillText.IsRecording(state.View) || (PillText.IsProcessing(state.View) && !ReducedMotion);
    }

    private void Render()
    {
        var view = _state.View;
        Label.Text = PillText.For(view, Elapsed());
        Label.Foreground = view == PillView.Cancelled ? new SolidColorBrush(Color.Parse("#C7D0E0")) : Brushes.White;
        var detail = PillText.Detail(_state, Elapsed());
        Detail.Text = detail ?? "";
        Detail.IsVisible = detail is not null;
        Hint.Text = PillText.HandsFreeHint;
        Hint.IsVisible = view == PillView.RecordingHandsFree;
        var recording = PillText.IsRecording(view);
        Meter.IsVisible = recording;
        Dot.IsVisible = DotRing.IsVisible = recording;
        Spinner.IsVisible = PillText.IsProcessing(view) || view == PillView.Busy;
        SpinnerArc.Stroke = view is PillView.Polishing or PillView.PolishingSkipped or PillView.Busy ? Amber : Blue;
        SpinnerTrack.Stroke = view is PillView.Polishing or PillView.PolishingSkipped or PillView.Busy ? AmberTrack : BlueTrack;
        Badge.IsVisible = view is PillView.Done or PillView.Cancelled or PillView.MicError;
        (BadgeDisc.Fill, BadgeGlyph.Data) = view switch
        {
            PillView.Done => (Green, Geometry.Parse("M4.5,8.5 L6.8,10.8 L11.5,6")),
            PillView.MicError => (Red, Geometry.Parse("M8,4 L8,9 M8,11.5 L8,12")),
            _ => (Grey, Geometry.Parse("M5.5,5.5 L10.5,10.5 M10.5,5.5 L5.5,10.5")),
        };
        _motion?.Cancel();
        _motion = null;
        Dot.Opacity = 1;
        if (recording && !ReducedMotion)
        {
            _motion = new CancellationTokenSource();
            _ = Overlay.Breathing().RunAsync(Dot, _motion.Token);
        }
    }

    private TimeSpan Elapsed() => TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _state.CaptureStartTickMs));

    private void OnFrame()
    {
        var view = _state.View;
        if (PillText.IsProcessing(view) || view == PillView.Busy)
        {
            _spin = (_spin + 12) % 360; // one turn a second at 30 frames
            ((RotateTransform)SpinnerArc.RenderTransform!).Angle = _spin;
            SpinnerArc.RenderTransformOrigin = new RelativePoint(8, 8, RelativeUnit.Absolute);
            return;
        }
        if (!PillText.IsRecording(view))
        {
            _frame.IsEnabled = false;
            return;
        }
        if (view == PillView.RecordingHandsFree) Detail.Text = PillText.Timer(Elapsed());
        // RMS → 0..1 on a -50..-10 dBFS scale, so normal speech moves the bars and room noise barely does.
        var norm = Math.Clamp((20 * Math.Log10(Math.Max(_level(), 1e-6f)) + 50) / 40, 0, 1);
        for (var i = 0; i < _bars.Length; i++) _bars[i].Height = 4 + 12 * norm * BarShape[i];
    }

    private void OnFlashElapsed()
    {
        _flash.Stop();
        if (_state.View == PillView.Busy && _resumeAfterBusy != PillView.Hidden)
        {
            Present(_state with { View = _resumeAfterBusy });
            return;
        }
        Present(new PillState(PillView.Hidden, 0));
    }

    private void FadeOut()
    {
        _frame.IsEnabled = false;
        _motion?.Cancel();
        if (!IsVisible) return;
        if (ReducedMotion)
        {
            Hide();
            return;
        }
        Pill.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (_state.View == PillView.Hidden) Hide();
        }, TimeSpan.FromMilliseconds(160));
    }

    /// <summary>Bottom-centre of the active monitor's working area, 48 DIP above the taskbar, in physical pixels.</summary>
    private void PlaceOnActiveScreen()
    {
        if (Overlay.ActiveScreen(this, _platform) is not { } screen) return;
        Measure(Size.Infinity);
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var w = (int)Math.Round(DesiredSize.Width * scale);
        var h = (int)Math.Round(DesiredSize.Height * scale);
        // The window carries the shadow's margin (10 above, 26 below): the pill itself sits 48 DIP above the work area's foot.
        Position = new PixelPoint(work.X + (work.Width - w) / 2, work.Bottom - (int)Math.Round((AboveTaskbar - 26) * scale) - h);
    }
}
