using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>The Core Foundation calls only the input code needs: a run loop that can be stopped from another thread at any moment, and a one-entry dictionary for the Accessibility prompt option.</summary>
internal static partial class CF
{
    /// <summary>CFRunLoopRunInMode: kCFRunLoopRunFinished = 1, Stopped = 2, TimedOut = 3, HandledSource = 4 (CFRunLoop.h).</summary>
    [DllImport(Lib, EntryPoint = "CFRunLoopRunInMode")]
    public static extern int RunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);

    [DllImport(Lib, EntryPoint = "CFDictionaryCreate")]
    private static extern nint DictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallBacks, nint valueCallBacks);

    private static readonly Lazy<nint> DefaultMode = new(() => ReadGlobal("kCFRunLoopDefaultMode"));
    private static readonly Lazy<nint> TrueValue = new(() => ReadGlobal("kCFBooleanTrue"));

    /// <summary>kCFRunLoopDefaultMode (one of the common modes a tap source is added to).</summary>
    public static nint RunLoopDefaultMode => DefaultMode.Value;

    /// <summary>kCFBooleanTrue.</summary>
    public static nint BooleanTrue => TrueValue.Value;

    /// <summary>A new CFDictionary of one CF key and value, retained with the standard CFType callbacks (the caller releases it).</summary>
    public static nint DictionaryCreate1(nint key, nint value)
    {
        var lib = NativeLibrary.Load(Lib);
        // The callback structs are exported as data: their addresses are what CFDictionaryCreate takes.
        return DictionaryCreate(0, [key], [value], 1, NativeLibrary.GetExport(lib, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(lib, "kCFTypeDictionaryValueCallBacks"));
    }
}
