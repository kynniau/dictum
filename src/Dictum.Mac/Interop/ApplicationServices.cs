using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// The Accessibility trust check (HIServices AXUIElement.h, read in the macOS SDK on this Mac 22/10/2026). An active event
/// tap and posted key events both need the process to be a trusted accessibility client. <c>Boolean</c> is one byte.
/// </summary>
internal static class AX
{
    private const string Lib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(Lib, EntryPoint = "AXIsProcessTrusted")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsProcessTrusted();

    [DllImport(Lib, EntryPoint = "AXIsProcessTrustedWithOptions")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool IsProcessTrustedWithOptions(nint options);

    /// <summary>
    /// <c>AXIsProcessTrustedWithOptions({kAXTrustedCheckOptionPrompt: true})</c>: the same answer as
    /// <see cref="IsProcessTrusted"/>, and when the answer is no, macOS shows its own "would like to control this computer"
    /// prompt. The header says the prompting "occurs asynchronously and does not affect the return value".
    /// </summary>
    public static bool IsProcessTrustedPrompting()
    {
        var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Lib), "kAXTrustedCheckOptionPrompt"));
        var options = CF.DictionaryCreate1(key, CF.BooleanTrue);
        try
        {
            return IsProcessTrustedWithOptions(options);
        }
        finally
        {
            CF.Release(options);
        }
    }
}

/// <summary>
/// Secure Event Input (Carbon HIToolbox, CarbonEvents.h): on while a password field or Terminal's Secure Keyboard Entry has the
/// keyboard, and then macOS keeps keystrokes from event taps (recalled; the tap liveness check pauses for it). Present on macOS
/// 27.2, checked 29/10/2026. <c>Boolean</c> is one byte.
/// </summary>
internal static class HIToolbox
{
    private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [DllImport(Lib, EntryPoint = "IsSecureEventInputEnabled")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsSecureEventInputEnabled();
}
