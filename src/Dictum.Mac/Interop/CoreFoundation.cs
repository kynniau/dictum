using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// Core Foundation: the C types the Core Graphics, Core Audio and Objective-C calls hand back (CFString, CFArray,
/// CFDictionary, CFNumber) and the run loop an event tap or audio queue runs on. Every CF object a Create/Copy function
/// returns is the caller's to <see cref="Release"/>; a Get function's result is not. Partial so each lane adds the
/// declarations its framework needs in its own file.
/// </summary>
internal static partial class CF
{
    private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint EncodingUtf8 = 0x08000100; // kCFStringEncodingUTF8

    // CFNumberType values (CFNumber.h).
    public const int NumberSInt32 = 3, NumberSInt64 = 4, NumberDouble = 13, NumberInt = 9;

    [DllImport(Lib, EntryPoint = "CFRelease")] public static extern void Release(nint cf);
    [DllImport(Lib, EntryPoint = "CFRetain")] public static extern nint Retain(nint cf);
    [DllImport(Lib, EntryPoint = "CFGetTypeID")] public static extern nuint GetTypeID(nint cf);
    [DllImport(Lib, EntryPoint = "CFStringGetTypeID")] public static extern nuint StringGetTypeID();
    [DllImport(Lib, EntryPoint = "CFNumberGetTypeID")] public static extern nuint NumberGetTypeID();
    [DllImport(Lib, EntryPoint = "CFDictionaryGetTypeID")] public static extern nuint DictionaryGetTypeID();
    [DllImport(Lib, EntryPoint = "CFStringCreateWithCString")] public static extern nint StringCreateWithCString(nint allocator, string cString, uint encoding);
    [DllImport(Lib, EntryPoint = "CFStringGetLength")] public static extern nint StringGetLength(nint str);
    [DllImport(Lib, EntryPoint = "CFStringGetMaximumSizeForEncoding")] public static extern nint StringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(Lib, EntryPoint = "CFStringGetCString")] public static extern bool StringGetCString(nint str, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(Lib, EntryPoint = "CFArrayGetCount")] public static extern nint ArrayGetCount(nint array);
    [DllImport(Lib, EntryPoint = "CFArrayGetValueAtIndex")] public static extern nint ArrayGetValueAtIndex(nint array, nint index);
    [DllImport(Lib, EntryPoint = "CFDictionaryGetValue")] public static extern nint DictionaryGetValue(nint dictionary, nint key);
    [DllImport(Lib, EntryPoint = "CFNumberGetValue")] public static extern bool NumberGetValue(nint number, int type, out long value);
    [DllImport(Lib, EntryPoint = "CFNumberGetValue")] public static extern bool NumberGetValue(nint number, int type, out double value);
    [DllImport(Lib, EntryPoint = "CFBooleanGetValue")] public static extern bool BooleanGetValue(nint boolean);

    [DllImport(Lib, EntryPoint = "CFRunLoopGetCurrent")] public static extern nint RunLoopGetCurrent();
    [DllImport(Lib, EntryPoint = "CFRunLoopRun")] public static extern void RunLoopRun();
    [DllImport(Lib, EntryPoint = "CFRunLoopStop")] public static extern void RunLoopStop(nint runLoop);
    [DllImport(Lib, EntryPoint = "CFRunLoopAddSource")] public static extern void RunLoopAddSource(nint runLoop, nint source, nint mode);
    [DllImport(Lib, EntryPoint = "CFRunLoopRemoveSource")] public static extern void RunLoopRemoveSource(nint runLoop, nint source, nint mode);
    [DllImport(Lib, EntryPoint = "CFMachPortCreateRunLoopSource")] public static extern nint MachPortCreateRunLoopSource(nint allocator, nint port, nint order);
    [DllImport(Lib, EntryPoint = "CFMachPortInvalidate")] public static extern void MachPortInvalidate(nint port);

    private static readonly Lazy<nint> CommonModes = new(() => ReadGlobal("kCFRunLoopCommonModes"));

    /// <summary>kCFRunLoopCommonModes, the mode a tap or queue source is added to.</summary>
    public static nint RunLoopCommonModes => CommonModes.Value;

    /// <summary>The value of an exported CFStringRef constant (a global whose storage holds the pointer).</summary>
    private static nint ReadGlobal(string symbol)
    {
        var lib = NativeLibrary.Load(Lib);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, symbol));
    }

    /// <summary>A new CFString (the caller releases it).</summary>
    public static nint String(string text) => StringCreateWithCString(0, text, EncodingUtf8);

    /// <summary>The text of a CFString; null for a null reference.</summary>
    public static string? ToString(nint str)
    {
        if (str == 0) return null;
        var size = StringGetMaximumSizeForEncoding(StringGetLength(str), EncodingUtf8) + 1;
        var buffer = new byte[(int)size];
        if (!StringGetCString(str, buffer, size, EncodingUtf8)) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>A CFDictionary value as a whole number (CFNumber), or null when absent or not a number.</summary>
    public static long? DictionaryGetInt64(nint dictionary, string key)
    {
        var cfKey = String(key);
        try
        {
            var value = DictionaryGetValue(dictionary, cfKey);
            return value != 0 && GetTypeID(value) == NumberGetTypeID() && NumberGetValue(value, NumberSInt64, out long number) ? number : null;
        }
        finally
        {
            Release(cfKey);
        }
    }

    /// <summary>A CFDictionary value as text (CFString), or null.</summary>
    public static string? DictionaryGetString(nint dictionary, string key)
    {
        var cfKey = String(key);
        try
        {
            var value = DictionaryGetValue(dictionary, cfKey);
            return value != 0 && GetTypeID(value) == StringGetTypeID() ? ToString(value) : null;
        }
        finally
        {
            Release(cfKey);
        }
    }

    /// <summary>A CFDictionary value that is itself a CFDictionary, or 0.</summary>
    public static nint DictionaryGetDictionary(nint dictionary, string key)
    {
        var cfKey = String(key);
        try
        {
            var value = DictionaryGetValue(dictionary, cfKey);
            return value != 0 && GetTypeID(value) == DictionaryGetTypeID() ? value : 0;
        }
        finally
        {
            Release(cfKey);
        }
    }
}
