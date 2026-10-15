using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Dictum.Platform;

namespace Dictum.UI.Overlays;

/// <summary>
/// The screen-edge glow (docs/design/Indicators.dc.html): a red inner glow around one monitor while recording, a 4 px line
/// at 90% and a 48 px glow at 35%. One click-through, never-activated window per monitor, covering it; the glow does not
/// move, so reduced motion changes nothing here.
/// </summary>
public sealed class EdgeWindow : Window
{
    // simplification: one transparent, topmost, click-through window covers each whole monitor. Ceiling: a full-monitor window
    // costs a monitor-sized surface (GPU memory on large displays) and could in principle trip fullscreen-app detection.
    // Trigger to retire: a report of either; then four edge strips per monitor, each drawing its part of the same glow.
    public EdgeWindow(IPlatform? platform)
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "Dictum";
        Content = new Border
        {
            Background = Brushes.Transparent,
            BoxShadow = new BoxShadows(
                new BoxShadow { IsInset = true, Spread = 4, Color = Color.Parse("#E6E5484D") },
                [new BoxShadow { IsInset = true, Blur = 48, Spread = 12, Color = Color.Parse("#59E5484D") }]),
        };
        Overlay.Prepare(this, platform);
    }

    /// <summary>Covers <paramref name="screen"/> exactly (its bounds are physical pixels; the size is in DIPs at its scale).</summary>
    public void Cover(Screen screen)
    {
        Position = screen.Bounds.TopLeft;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }
}
