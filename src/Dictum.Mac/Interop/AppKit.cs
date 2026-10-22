using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// The AppKit values and the <c>objc_msgSend</c> shapes the shell needs that <see cref="ObjC"/> does not declare (kept in
/// their own class, with their own names, so no other lane's additions to the partial <see cref="ObjC"/> can clash). Every
/// value below was read on 23/10/2026 in the macOS 27 SDK that ships with Xcode on this Mac (MacOSX.sdk, AppKit and
/// CoreGraphics headers, named at each constant).
/// </summary>
internal static class AppKit
{
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    /// <summary>NSStatusWindowLevel = kCGStatusWindowLevel = 25 (NSWindow.h:198, CGWindowLevel.h:74): above other apps' floating panels (3) and the menu bar (24).</summary>
    public const nint StatusWindowLevel = 25;

    // NSWindowCollectionBehavior (NSWindow.h:130-141).
    public const nuint CanJoinAllSpaces = 1 << 0;
    public const nuint Stationary = 1 << 4;
    public const nuint IgnoresCycle = 1 << 6;
    public const nuint FullScreenAuxiliary = 1 << 8;

    /// <summary>NSSquareStatusItemLength = -2.0 (NSStatusBar.h:21).</summary>
    public const double SquareStatusItemLength = -2.0;

    /// <summary>NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp = 1 &lt;&lt; 2 | 1 &lt;&lt; 4 (NSEvent.h:26-28, 95-97).</summary>
    public const nuint LeftOrRightMouseUp = (1 << 2) | (1 << 4);

    // CGWindowListOption (CGWindow.h:145, 160) and kCGNullWindowID (CGWindow.h:42).
    public const uint WindowListOnScreenOnly = 1 << 0;
    public const uint WindowListExcludeDesktopElements = 1 << 4;
    public const uint NullWindowId = 0;

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] public static extern nint SendDouble(nint self, nint sel, double a);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] public static extern void SendSize(nint self, nint sel, CGSize size);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel, nint a, nint b, [MarshalAs(UnmanagedType.I1)] bool c);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SendBool(nint self, nint sel);
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SendBool(nint self, nint sel, nint a);

    /// <summary>The top edge of the primary display in AppKit's global coordinates (NSMaxY of the first NSScreen's frame), or null when there is no screen.</summary>
    public static double? PrimaryScreenTop()
    {
        var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("screens"));
        var first = screens == 0 ? 0 : ObjC.Send(screens, ObjC.Sel("firstObject"));
        if (first == 0) return null;
        var frame = ObjC.SendRect(first, ObjC.Sel("frame"));
        return frame.Origin.Y + frame.Size.Height;
    }
}
