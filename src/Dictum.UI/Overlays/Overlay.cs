using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using Dictum.Platform;

namespace Dictum.UI.Overlays;

/// <summary>
/// What the pill, the orb and the screen-edge glow share: they never take focus and never catch a click (SPEC §7, §21).
/// The platform's window flags are applied before the first show and kept through Avalonia's style callback, which runs
/// every time Avalonia recomputes the window's styles; Avalonia itself shows them with ShowActivated = false and moves them
/// with SWP_NOACTIVATE (Avalonia 11.3.22's Win32 WindowImpl, read 16/10/2026).
/// </summary>
internal static class Overlay
{
    public static void Prepare(Window window, IPlatform? platform)
    {
        if (platform is null) return; // a headless test has no native window to style
        Win32Properties.AddWindowStylesCallback(window, (style, exStyle) => platform.WindowFlags.OverlayStyles(style, exStyle));
        if (window.TryGetPlatformHandle()?.Handle is { } handle and not 0) platform.WindowFlags.MakeOverlay(handle);
    }

    /// <summary>The screen the user is working on: the one showing the foreground window, else the primary.</summary>
    public static Screen? ActiveScreen(Window window, IPlatform? platform)
    {
        var screens = window.Screens;
        if (platform?.WindowFlags.ForegroundBounds() is { } bounds && screens.ScreenFromPoint(new PixelPoint(bounds.Centre.X, bounds.Centre.Y)) is { } screen)
            return screen;
        return screens.Primary ?? screens.All.FirstOrDefault();
    }

    /// <summary>The 1.2 s breathing of the recording dot (docs/design/README.md: "the red dot breathes at 1.2 s").</summary>
    public static Animation Breathing() => new()
    {
        Duration = TimeSpan.FromSeconds(1.2),
        IterationCount = IterationCount.Infinite,
        Easing = new SineEaseInOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
            new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(Visual.OpacityProperty, 0.35) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
        },
    };

    /// <summary>"Everything else is a 150 ms fade"; none under reduced motion.</summary>
    public static Transitions Fade(bool reducedMotion) =>
        reducedMotion ? [] : [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) }];
}
