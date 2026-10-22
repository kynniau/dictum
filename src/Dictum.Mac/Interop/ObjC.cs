using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// The Objective-C runtime (libobjc) for the few AppKit and Foundation objects Dictum needs: classes by name, selectors,
/// and <c>objc_msgSend</c> declared once per signature the callers use. Apple Silicon only: on arm64 every message goes
/// through <c>objc_msgSend</c> (there is no <c>objc_msgSend_stret</c> or <c>_fpret</c> there); an x64 build would need the
/// stret variant for struct returns larger than two registers. Every id and SEL is a plain pointer (nint); nothing here
/// retains or releases on the caller's behalf: an object from <c>alloc</c>/<c>init</c>/<c>new</c>/<c>copy</c> is the
/// caller's to <see cref="Release"/>, everything else is autoreleased and lives until the enclosing
/// <see cref="AutoreleasePool"/> drains.
/// </summary>
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [DllImport(Lib, EntryPoint = "objc_getClass")] private static extern nint objc_getClass(string name);
    [DllImport(Lib, EntryPoint = "sel_registerName")] private static extern nint sel_registerName(string name);
    [DllImport(Lib, EntryPoint = "object_getClassName")] private static extern nint object_getClassName(nint obj);
    [DllImport(Lib, EntryPoint = "objc_allocateClassPair")] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(Lib, EntryPoint = "objc_registerClassPair")] private static extern void objc_registerClassPair(nint cls);
    [DllImport(Lib, EntryPoint = "class_addMethod")] private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    // objc_msgSend, one declaration per signature. Names say the argument shapes: P = pointer-sized (id, SEL, nint,
    // NSInteger, NSUInteger), B = BOOL, D = double, and the return type after the underscore.
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint self, nint sel, nint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint self, nint sel, nint a, nint b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint self, nint sel, nint a, nint b, nint c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint Send(nint self, nint sel, nint a, nint b, nint c, nint d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern bool SendBool(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern bool SendBool(nint self, nint sel, nint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern bool SendBool(nint self, nint sel, nint a, nint b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel, nint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel, nint a, nint b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel, CGPoint point);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(nint self, nint sel, CGRect rect, [MarshalAs(UnmanagedType.I1)] bool display);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGPoint SendPoint(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGRect SendRect(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern double SendDouble(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern int SendInt(nint self, nint sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(nint self, nint sel);

    private static readonly Dictionary<string, nint> Selectors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, nint> Classes = new(StringComparer.Ordinal);
    private static readonly Lazy<bool> FrameworksLoaded = new(LoadFrameworks);

    /// <summary>
    /// Loads Foundation and AppKit into the process once, so their classes resolve in the command-line modes too (the
    /// tray app loads them through Avalonia's native backend anyway). Returns false when either is missing, which on a
    /// Mac means the process is not running on macOS at all.
    /// </summary>
    private static bool LoadFrameworks() =>
        NativeLibrary.TryLoad(Foundation, out _) && NativeLibrary.TryLoad(AppKit, out _);

    /// <summary>A selector by name ("stringWithUTF8String:"); registered once and cached.</summary>
    public static nint Sel(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out var sel))
            {
                sel = sel_registerName(name);
                Selectors[name] = sel;
            }
            return sel;
        }
    }

    /// <summary>A class by name ("NSWorkspace"); throws when it does not exist, which means a wrong name or a framework not loaded.</summary>
    public static nint Class(string name)
    {
        lock (Classes)
        {
            if (!Classes.TryGetValue(name, out var cls))
            {
                _ = FrameworksLoaded.Value;
                cls = objc_getClass(name);
                if (cls == 0) throw new EntryPointNotFoundException($"Objective-C class {name} was not found.");
                Classes[name] = cls;
            }
            return cls;
        }
    }

    /// <summary>The class's name of an object, for logs ("NSWindow"); "null" for nil.</summary>
    public static string ClassName(nint obj) => obj == 0 ? "null" : Marshal.PtrToStringUTF8(object_getClassName(obj)) ?? "?";

    /// <summary>An autoreleased NSString (<c>stringWithUTF8String:</c>) for <paramref name="text"/>.</summary>
    public static nint NSString(string text)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>The text of an NSString (<c>UTF8String</c>), or null for nil.</summary>
    public static string? FromNSString(nint nsString) => nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    /// <summary>Sends <c>release</c>: for objects the caller owns (alloc/init, new, copy, retain).</summary>
    public static void Release(nint obj)
    {
        if (obj != 0) SendVoid(obj, Sel("release"));
    }

    /// <summary>
    /// Defines a new Objective-C class with one or more methods implemented in C# (<c>UnmanagedCallersOnly</c> function
    /// pointers), for targets of AppKit callbacks such as a notification delegate. The type encodings follow Apple's
    /// runtime type encoding ("v@:@" for <c>- (void)method:(id)arg</c>). Returns the registered class; call once per name.
    /// </summary>
    public static unsafe nint DefineClass(string name, string superclass, params (string Selector, nint Imp, string Types)[] methods)
    {
        var cls = objc_allocateClassPair(Class(superclass), name, 0);
        if (cls == 0) throw new InvalidOperationException($"Objective-C class {name} could not be allocated (already defined?).");
        foreach (var (selector, imp, types) in methods)
        {
            if (!class_addMethod(cls, Sel(selector), imp, types)) throw new InvalidOperationException($"Method {selector} could not be added to {name}.");
        }
        objc_registerClassPair(cls);
        return cls;
    }

    /// <summary>
    /// An NSAutoreleasePool for a block of messaging outside AppKit's own run loop (a worker thread, a CLI mode): every
    /// autoreleased object made inside is freed when it is disposed. On the UI thread Avalonia's run loop drains its own.
    /// </summary>
    public readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint _pool;

        public static AutoreleasePool Begin() => new(Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init")));

        private AutoreleasePool(nint pool) => _pool = pool;

        public void Dispose() => SendVoid(_pool, Sel("drain"));
    }
}

/// <summary>CGPoint / NSPoint: two doubles (CGFloat is double on 64-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint(double x, double y)
{
    public double X = x;
    public double Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height)
{
    public double Width = width;
    public double Height = height;
}

/// <summary>CGRect / NSRect. AppKit's screen coordinates have their origin at the bottom-left of the primary display; Core Graphics' window lists use the top-left.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect(double x, double y, double width, double height)
{
    public CGPoint Origin = new(x, y);
    public CGSize Size = new(width, height);
}
