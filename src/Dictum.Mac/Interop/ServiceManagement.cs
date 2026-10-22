using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// ServiceManagement's SMAppService (macOS 13+), the login item of the running app: <c>+mainAppService</c>,
/// <c>-registerAndReturnError:</c>, <c>-unregisterAndReturnError:</c>, <c>status</c> and <c>+openSystemSettingsLoginItems</c>
/// (SMAppService.h:91, 172, 201, 248, 283). Status and error values from SMAppService.h:30-34 and SMErrors.h:59-69 in the
/// macOS 27 SDK, read 23/10/2026.
/// </summary>
internal static class ServiceManagement
{
    private const string Framework = "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";

    // SMAppServiceStatus (SMAppService.h:30-34).
    public const long NotRegistered = 0;
    public const long Enabled = 1;
    public const long RequiresApproval = 2;
    public const long NotFound = 3;

    // SMErrors.h:59-69 (kSMErrorInternalFailure = 2, then one each).
    public const long ErrorJobNotFound = 6;
    public const long ErrorLaunchDeniedByUser = 11;
    public const long ErrorAlreadyRegistered = 12;

    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(Framework, out _));

    /// <summary><c>[SMAppService mainAppService]</c> (autoreleased).</summary>
    public static nint MainAppService()
    {
        if (!Loaded.Value) throw new DllNotFoundException("ServiceManagement.framework is not on this system.");
        return ObjC.Send(ObjC.Class("SMAppService"), ObjC.Sel("mainAppService"));
    }

    public static long Status(nint service) => ObjC.SendLong(service, ObjC.Sel("status"));

    /// <summary>Sends a <c>…AndReturnError:</c> message; false with the NSError's code and description when it failed.</summary>
    public static unsafe bool Call(nint service, string selector, out long errorCode, out string? errorText)
    {
        nint error = 0;
        var ok = AppKit.SendBool(service, ObjC.Sel(selector), (nint)(&error));
        errorCode = error == 0 ? 0 : ObjC.SendLong(error, ObjC.Sel("code"));
        errorText = error == 0 ? null : ObjC.FromNSString(ObjC.Send(error, ObjC.Sel("localizedDescription")));
        return ok;
    }

    public static void OpenLoginItemsSettings() => ObjC.SendVoid(ObjC.Class("SMAppService"), ObjC.Sel("openSystemSettingsLoginItems"));
}
