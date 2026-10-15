using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.Core;
using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.UI.Overlays;

/// <summary>
/// The orb (docs/design/Indicators.dc.html): a 96 px badge on Ink with the microphone, in a corner of the monitor with the
/// focused window. Its ring follows the voice while recording (red), and turns blue while transcribing and amber while
/// polishing. Click-through and never activated like the pill, so it is placed by Settings (indicator.orb_corner) rather
/// than dragged; under reduced motion the ring stays still.
/// </summary>
public sealed class OrbWindow : Window
{
    private const double Diameter = 96;
    private const double Reach = 40; // room around the badge for the rings and the drop shadow
    private const double FromEdge = 56 - Reach; // the design places the badge 56 px in from the work area's edge
    private static readonly Color Red = Color.Parse("#E5484D");
    private static readonly Color Blue = Color.Parse("#2457E6");
    private static readonly Color Amber = Color.Parse("#F5A524");

    private readonly IPlatform? _platform;
    private readonly Border _badge;
    private readonly Path _glyph;
    private readonly Rectangle _capsule;
    private readonly DispatcherTimer _frame;
    private Func<float> _level = () => 0f;
    private PillView _view = PillView.Hidden;

    public OrbWindow(IPlatform? platform)
    {
        _platform = platform;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Focusable = false;
        IsHitTestVisible = false;
        Width = Height = Diameter + 2 * Reach;
        Title = "Dictum";

        _capsule = new Rectangle { Width = 6, Height = 11, RadiusX = 3, RadiusY = 3 };
        Canvas.SetLeft(_capsule, 9);
        Canvas.SetTop(_capsule, 3);
        _glyph = new Path { Data = Geometry.Parse("M5,11 A7,7 0 0 0 19,11 M12,18 L12,21"), StrokeThickness = 2.2, StrokeLineCap = PenLineCap.Round };
        var mic = new Viewbox { Width = 44, Height = 44, Child = new Canvas { Width = 24, Height = 24, Children = { _capsule, _glyph } } };
        _badge = new Border
        {
            Width = Diameter,
            Height = Diameter,
            CornerRadius = new CornerRadius(Diameter / 2),
            Background = new SolidColorBrush(Color.Parse("#0B1220")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = mic,
        };
        Content = _badge;
        Overlay.Prepare(this, platform);
        _frame = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Paint()) { IsEnabled = false };
    }

    private bool ReducedMotion => _platform?.WindowFlags.ReducedMotion ?? false;

    public PillView View => _view;

    public void SetLevelSource(Func<float> level) => _level = level;

    /// <summary>Shows the orb for a live dictation (recording or processing) in <paramref name="corner"/>; hides it otherwise.</summary>
    public void Present(PillView view, string corner)
    {
        var live = PillText.IsRecording(view) || PillText.IsProcessing(view);
        if (view == PillView.Busy) return; // a refused key press changes nothing here
        _view = live ? view : PillView.Hidden;
        if (!live)
        {
            _frame.IsEnabled = false;
            Hide();
            return;
        }
        Paint();
        if (!IsVisible)
        {
            Place(corner);
            Show();
        }
        _frame.IsEnabled = PillText.IsRecording(view) && !ReducedMotion;
    }

    private void Paint()
    {
        var colour = PillText.IsRecording(_view) ? Red : _view == PillView.Transcribing ? Blue : Amber;
        var brush = new SolidColorBrush(colour);
        _capsule.Fill = brush;
        _glyph.Stroke = brush;
        // The design's rings, 6 px at 35% and 14 px at 15%, grow with the voice while recording (-50..-10 dBFS → 0..1).
        var norm = PillText.IsRecording(_view) && !ReducedMotion ? Math.Clamp((20 * Math.Log10(Math.Max(_level(), 1e-6f)) + 50) / 40, 0, 1) : 0;
        _badge.BoxShadow = new BoxShadows(
            new BoxShadow { Spread = 6 + 8 * norm, Color = Color.FromArgb(0x59, colour.R, colour.G, colour.B) },
            [
                new BoxShadow { Spread = 14 + 12 * norm, Color = Color.FromArgb(0x26, colour.R, colour.G, colour.B) },
                new BoxShadow { OffsetY = 12, Blur = 30, Color = Color.FromArgb(0x80, 0, 0, 0) },
            ]);
    }

    /// <summary>The chosen corner of the active monitor's working area, the badge 56 DIP in from both edges.</summary>
    private void Place(string corner)
    {
        if (Overlay.ActiveScreen(this, _platform) is not { } screen) return;
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var size = (int)Math.Round(Width * scale);
        var inset = (int)Math.Round(FromEdge * scale);
        var right = corner.EndsWith("right", StringComparison.Ordinal) || !IndicatorSettings.Corners.Contains(corner);
        var bottom = corner.StartsWith("bottom", StringComparison.Ordinal) || !IndicatorSettings.Corners.Contains(corner);
        Position = new PixelPoint(right ? work.Right - inset - size : work.X + inset, bottom ? work.Bottom - inset - size : work.Y + inset);
    }

    public void Stop()
    {
        _frame.IsEnabled = false;
        Close();
    }
}
