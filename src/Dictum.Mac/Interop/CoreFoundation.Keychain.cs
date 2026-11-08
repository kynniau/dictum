using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// The Core Foundation calls the keychain store needs (CFData.h, CFArray.h, CFNumber.h, CFDictionary.h of the macOS 27.2 SDK,
/// read 08/11/2026): a mutable CFData the key's bytes go through so they can be zeroed after the call, reading a returned
/// CFData, an n-entry query dictionary, kCFBooleanFalse, and the type ids the results are checked against.
/// </summary>
internal static partial class CF
{
    [DllImport(Lib, EntryPoint = "CFDataGetTypeID")] public static extern nuint DataGetTypeID(); // CFData.h:22
    [DllImport(Lib, EntryPoint = "CFDataCreateMutable")] public static extern nint DataCreateMutable(nint allocator, nint capacity); // CFData.h:35
    [DllImport(Lib, EntryPoint = "CFDataGetLength")] public static extern nint DataGetLength(nint data); // CFData.h:41
    [DllImport(Lib, EntryPoint = "CFDataGetBytePtr")] public static extern nint DataGetBytePtr(nint data); // CFData.h:44
    [DllImport(Lib, EntryPoint = "CFDataGetMutableBytePtr")] public static extern nint DataGetMutableBytePtr(nint data); // CFData.h:47
    [DllImport(Lib, EntryPoint = "CFDataAppendBytes")] public static extern void DataAppendBytes(nint data, byte[] bytes, nint length); // CFData.h:59
    [DllImport(Lib, EntryPoint = "CFArrayGetTypeID")] public static extern nuint ArrayGetTypeID(); // CFArray.h:121

    private static readonly Lazy<nint> FalseValue = new(() => ReadGlobal("kCFBooleanFalse")); // CFNumber.h:23

    /// <summary>kCFBooleanFalse.</summary>
    public static nint BooleanFalse => FalseValue.Value;

    /// <summary>
    /// A new CFDictionary of the CF keys and values at the same index, retained with the standard CFType callbacks
    /// (CFDictionary.h:284), so the caller may release its own references once this returns, and releases the dictionary.
    /// </summary>
    public static nint DictionaryCreate(nint[] keys, nint[] values)
    {
        if (keys.Length != values.Length) throw new ArgumentException("One value per key.", nameof(values));
        var lib = NativeLibrary.Load(Lib);
        // The callback structs are exported as data: their addresses are what CFDictionaryCreate takes.
        return DictionaryCreate(0, keys, values, keys.Length, NativeLibrary.GetExport(lib, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(lib, "kCFTypeDictionaryValueCallBacks"));
    }

    /// <summary>A new mutable CFData holding a copy of <paramref name="bytes"/> (the caller zeroes it with <see cref="DataZero"/> and releases it).</summary>
    public static nint DataCreateMutableCopy(byte[] bytes)
    {
        var data = DataCreateMutable(0, 0);
        if (data == 0) throw new OutOfMemoryException("CFDataCreateMutable returned null.");
        DataAppendBytes(data, bytes, bytes.Length);
        return data;
    }

    /// <summary>Overwrites a mutable CFData's bytes with zeros, so the key does not linger in freed memory.</summary>
    public static void DataZero(nint data)
    {
        if (data == 0) return;
        var length = (int)DataGetLength(data);
        if (length > 0) Marshal.Copy(new byte[length], 0, DataGetMutableBytePtr(data), length);
    }

    /// <summary>A copy of a CFData's bytes (the caller clears the array when done).</summary>
    public static byte[] DataBytes(nint data)
    {
        var bytes = new byte[(int)DataGetLength(data)];
        if (bytes.Length > 0) Marshal.Copy(DataGetBytePtr(data), bytes, 0, bytes.Length);
        return bytes;
    }
}
