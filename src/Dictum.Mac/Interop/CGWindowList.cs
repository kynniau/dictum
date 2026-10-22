using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// Core Graphics' window list (CGWindow.h, macOS 27 SDK, read 23/10/2026): the on-screen windows front to back, each a
/// CFDictionary with its owner's process id (kCGWindowOwnerPID), its layer (kCGWindowLayer, 0 for ordinary app windows)
/// and its bounds (kCGWindowBounds, a dictionary for CGRectMakeWithDictionaryRepresentation, "with the origin at the
/// upper-left corner of the main display", CGWindow.h:66-69). Bounds and owner are readable without the Screen Recording
/// permission; window titles are not, and Dictum never reads them.
/// </summary>
internal static class CGWindowList
{
    private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(Lib, EntryPoint = "CGWindowListCopyWindowInfo")] private static extern nint CopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(Lib, EntryPoint = "CGRectMakeWithDictionaryRepresentation")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool RectFromDictionary(nint dictionary, out CGRect rect);

    private static readonly Lazy<(nint OwnerPid, nint Layer, nint Bounds)> Keys = new(() =>
    {
        var lib = NativeLibrary.Load(Lib);
        nint Key(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));
        return (Key("kCGWindowOwnerPID"), Key("kCGWindowLayer"), Key("kCGWindowBounds"));
    });

    /// <summary>The bounds (points, top-left origin) of the frontmost on-screen layer-0 window <paramref name="pid"/> owns, or null.</summary>
    public static CGRect? FrontWindowBounds(int pid)
    {
        var list = CopyWindowInfo(AppKit.WindowListOnScreenOnly | AppKit.WindowListExcludeDesktopElements, AppKit.NullWindowId);
        if (list == 0) return null; // no window server session
        try
        {
            var keys = Keys.Value;
            var count = CF.ArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                var window = CF.ArrayGetValueAtIndex(list, i);
                if (Int(window, keys.OwnerPid) != pid || Int(window, keys.Layer) != 0) continue;
                var bounds = CF.DictionaryGetValue(window, keys.Bounds);
                if (bounds != 0 && CF.GetTypeID(bounds) == CF.DictionaryGetTypeID() && RectFromDictionary(bounds, out var rect)) return rect;
            }
            return null;
        }
        finally
        {
            CF.Release(list);
        }
    }

    private static long? Int(nint dictionary, nint key)
    {
        var value = CF.DictionaryGetValue(dictionary, key);
        return value != 0 && CF.GetTypeID(value) == CF.NumberGetTypeID() && CF.NumberGetValue(value, CF.NumberSInt64, out long number) ? number : null;
    }
}
