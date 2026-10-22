using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The overlays' NSWindow flags and the "foreground window" questions, on macOS (SPEC §7, §21). The handle the UI passes is
/// what Avalonia.Native's <c>TryGetPlatformHandle()</c> returns for a Window: HandleDescriptor "NSWindow" and the NSWindow
/// pointer (MacOSTopLevelHandle in src/Avalonia.Native/TopLevelImpl.cs, tag 11.3.22, read 23/10/2026). macOS has no
/// foreground-window handle a process may hold, so the "window" of <see cref="ForegroundWindow"/>, <see cref="IsWindow"/>,
/// <see cref="AppName"/> and <see cref="TryActivate"/> is the frontmost app's process id: stable while that app runs, and
/// what activation works on. Coordinates are <see cref="ScreenSpace"/>'s (points from the primary display's top-left).
/// Selectors, all in the macOS 27 SDK (read 23/10/2026): NSWindow.h setLevel: / setIgnoresMouseEvents: (:795) /
/// setCollectionBehavior: / setHidesOnDeactivate: (:416) / setFrameTopLeftPoint: (:351); NSWorkspace.h
/// frontmostApplication (:144); NSAccessibility.h accessibilityDisplayShouldReduceMotion (:103, an NSWorkspace property);
/// NSRunningApplication.h processIdentifier (:107), localizedName (:92), terminated (:67), activateWithOptions: (:148),
/// runningApplicationWithProcessIdentifier: (:165).
/// </summary>
internal sealed class WindowFlags : IWindowFlags
{
    /// <summary>
    /// Every Space, including a full-screen app's (the pill must show over it), never moved by Mission Control, never in the
    /// window cycle.
    /// </summary>
    internal const nuint OverlayBehaviour = AppKit.CanJoinAllSpaces | AppKit.Stationary | AppKit.IgnoresCycle | AppKit.FullScreenAuxiliary;

    /// <summary>No window styles to keep on a Mac: Avalonia's Win32 style callback never runs here.</summary>
    public (uint Style, uint ExStyle) OverlayStyles(uint style, uint exStyle) => (style, exStyle);

    /// <summary>
    /// Status level (above other apps' floating panels; Avalonia's Topmost would give the floating level, 3, and the
    /// overlays set Topmost before this runs), click-through, on every Space and over full-screen apps, and still shown
    /// while Dictum is not the active app. Avalonia already shows them without activating (ShowActivated = false).
    /// </summary>
    public void MakeOverlay(nint handle)
    {
        if (handle == 0) return;
        using var pool = ObjC.AutoreleasePool.Begin();
        ObjC.SendVoid(handle, ObjC.Sel("setLevel:"), AppKit.StatusWindowLevel);
        ObjC.SendVoid(handle, ObjC.Sel("setIgnoresMouseEvents:"), true);
        ObjC.SendVoid(handle, ObjC.Sel("setCollectionBehavior:"), (nint)OverlayBehaviour);
        ObjC.SendVoid(handle, ObjC.Sel("setHidesOnDeactivate:"), false);
    }

    /// <summary>Puts the window's top-left corner at (x, y) in the UI's coordinates, as Avalonia's own Position setter does; moving never activates on macOS.</summary>
    public void Move(nint handle, int x, int y)
    {
        if (handle == 0) return;
        using var pool = ObjC.AutoreleasePool.Begin();
        if (AppKit.PrimaryScreenTop() is not { } top) return;
        ObjC.SendVoid(handle, ObjC.Sel("setFrameTopLeftPoint:"), ScreenSpace.ToAppKit(x, y, top));
    }

    /// <summary>The frontmost app's process id (the app a paste goes to), or 0 when there is none.</summary>
    public nint ForegroundWindow()
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var app = ObjC.Send(Workspace, ObjC.Sel("frontmostApplication"));
        return app == 0 ? 0 : ObjC.SendInt(app, ObjC.Sel("processIdentifier"));
    }

    /// <summary>The frontmost app's frontmost ordinary window (layer 0, on screen), or null when it shows none.</summary>
    public ScreenRect? ForegroundBounds()
    {
        var pid = (int)ForegroundWindow();
        return pid != 0 && CGWindowList.FrontWindowBounds(pid) is { } rect ? ScreenSpace.FromCoreGraphics(rect) : null;
    }

    /// <summary>System Settings &gt; Accessibility &gt; Display &gt; Reduce motion.</summary>
    public bool ReducedMotion
    {
        get
        {
            using var pool = ObjC.AutoreleasePool.Begin();
            return AppKit.SendBool(Workspace, ObjC.Sel("accessibilityDisplayShouldReduceMotion"));
        }
    }

    public string? AppName(nint window)
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var app = RunningApp(window);
        return app == 0 ? null : ObjC.FromNSString(ObjC.Send(app, ObjC.Sel("localizedName")));
    }

    /// <summary>Whether the app with this process id still runs.</summary>
    public bool IsWindow(nint handle)
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var app = RunningApp(handle);
        return app != 0 && !AppKit.SendBool(app, ObjC.Sel("isTerminated"));
    }

    /// <summary>
    /// Asks macOS to make that app active again. True when the request was sent (activation is asynchronous and not
    /// guaranteed, NSRunningApplication.h:128-142; the caller checks <see cref="ForegroundWindow"/> after a pause).
    /// </summary>
    public bool TryActivate(nint handle)
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var app = RunningApp(handle);
        return app != 0 && AppKit.SendBool(app, ObjC.Sel("activateWithOptions:"), 0);
    }

    private static nint Workspace => ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));

    /// <summary>The NSRunningApplication for a process id (autoreleased), or 0 for none.</summary>
    private static nint RunningApp(nint pid) =>
        pid is <= 0 or > int.MaxValue ? 0 : ObjC.Send(ObjC.Class("NSRunningApplication"), ObjC.Sel("runningApplicationWithProcessIdentifier:"), pid);
}
