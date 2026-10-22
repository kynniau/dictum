using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// Objective-C blocks, by hand, for the few Apple APIs that take or hand over a completion handler (UserNotifications).
/// The layout is Clang's Block ABI (https://clang.llvm.org/docs/Block-ABI-Apple.html, read 23/10/2026): a literal is
/// { isa, int flags, int reserved, invoke, descriptor* } and the descriptor { unsigned long reserved, unsigned long size }.
/// Only global blocks are made here: isa = &amp;_NSConcreteGlobalBlock (exported by libSystem; found by this Mac's dlsym on
/// 23/10/2026), flags = BLOCK_IS_GLOBAL (1 &lt;&lt; 28), no captured variables, so copying one returns it unchanged and
/// releasing it does nothing. The invoke function is a static <c>UnmanagedCallersOnly</c> method whose first argument is
/// the block itself.
/// </summary>
internal static unsafe class Blocks
{
    /// <summary>BLOCK_IS_GLOBAL (Block ABI, "flags bits").</summary>
    public const int IsGlobal = 1 << 28;

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Descriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Literal
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public Descriptor* Descriptor;
    }

    private static readonly Lazy<nint> GlobalBlockClass = new(() => NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock"));

    /// <summary>
    /// A global block calling <paramref name="invoke"/>, in unmanaged memory that is never freed: the system may call a
    /// completion handler at any later time, so the block lives as long as the process. Make each one once.
    /// </summary>
    public static nint Global(nint invoke)
    {
        var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
        descriptor->Size = (nuint)sizeof(Literal);
        var block = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));
        block->Isa = GlobalBlockClass.Value;
        block->Flags = IsGlobal;
        block->Invoke = invoke;
        block->Descriptor = descriptor;
        return (nint)block;
    }

    /// <summary>The invoke function of a block the system handed over (a completion handler Dictum must call).</summary>
    public static nint InvokeOf(nint block) => block == 0 ? 0 : ((Literal*)block)->Invoke;
}
