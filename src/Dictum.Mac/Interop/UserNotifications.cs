using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// UserNotifications (UNUserNotificationCenter.h, UNNotificationRequest.h, UNNotificationContent.h in the macOS 27 SDK,
/// read 23/10/2026): <c>+currentNotificationCenter</c> (:48), <c>-requestAuthorizationWithOptions:completionHandler:</c>
/// (:53, the handler is a non-null block), <c>-addNotificationRequest:withCompletionHandler:</c> (:63, nullable handler),
/// the <c>delegate</c> (:42, weak) and its <c>userNotificationCenter:willPresentNotification:withCompletionHandler:</c>
/// (:97), <c>+requestWithIdentifier:content:trigger:</c> (UNNotificationRequest.h:28, nullable trigger = deliver now).
/// The center needs the process to be an app bundle with an identifier; outside one it is never touched.
/// </summary>
internal static class UserNotifications
{
    private const string Framework = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    /// <summary>UNAuthorizationOptionAlert = 1 &lt;&lt; 2 (UNUserNotificationCenter.h:25).</summary>
    public const nint AuthorizationAlert = 1 << 2;

    /// <summary>UNNotificationPresentationOptionList | UNNotificationPresentationOptionBanner = 1 &lt;&lt; 3 | 1 &lt;&lt; 4 (UNUserNotificationCenter.h:85-86).</summary>
    public const nuint PresentListAndBanner = (1 << 3) | (1 << 4);

    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(Framework, out _));

    public static nint CurrentCenter()
    {
        if (!Loaded.Value) throw new DllNotFoundException("UserNotifications.framework is not on this system.");
        return ObjC.Send(ObjC.Class("UNUserNotificationCenter"), ObjC.Sel("currentNotificationCenter"));
    }
}
