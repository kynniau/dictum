using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The one screen coordinate system the UI works in on a Mac, and the conversions into it. Avalonia.Native 11.3.22 reports
/// every screen with Scaling = 1 and its bounds in points, with Y flipped against the primary display's top
/// (native/Avalonia.Native/src/OSX/Screens.mm and ConvertPointY in main.mm, tag 11.3.22, read 23/10/2026), and positions
/// windows the same way (WindowBaseImpl.mm SetPosition: setFrameTopLeftPoint of ConvertPointY). So the "pixels" of
/// <see cref="ScreenPoint"/>, <see cref="ScreenRect"/> and Avalonia's PixelPoint are points from the top-left of the
/// primary display, never backing pixels. AppKit's own global coordinates (NSEvent.mouseLocation, NSWindow frames) have
/// their origin at the primary display's bottom-left; Core Graphics' window list already uses the top-left. Pure
/// arithmetic, tested on any OS.
/// </summary>
internal static class ScreenSpace
{
    /// <summary>An AppKit point (bottom-left origin) in the UI's top-left coordinates, given the primary display's top edge.</summary>
    public static ScreenPoint FromAppKit(CGPoint point, double primaryTop) =>
        new((int)Math.Round(point.X), (int)Math.Round(primaryTop - point.Y));

    /// <summary>A UI point (top-left origin) as the AppKit point to hand to <c>setFrameTopLeftPoint:</c>.</summary>
    public static CGPoint ToAppKit(int x, int y, double primaryTop) => new(x, primaryTop - y);

    /// <summary>A Core Graphics window rectangle (points, top-left origin) as a <see cref="ScreenRect"/>: the same coordinates, rounded.</summary>
    public static ScreenRect FromCoreGraphics(CGRect rect) => new(
        (int)Math.Round(rect.Origin.X), (int)Math.Round(rect.Origin.Y), (int)Math.Round(rect.Size.Width), (int)Math.Round(rect.Size.Height));
}
