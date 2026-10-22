using System.Runtime.InteropServices;
using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The tray's notices as macOS notifications (UNUserNotificationCenter). The center needs an app bundle with an identifier,
/// so a copy outside Dictum.app never touches it: the notice goes to the log at Warn instead. In the bundle, permission is
/// asked once, when the tray starts (macOS shows its prompt only the first time), and a delegate lets banners show while
/// Dictum itself is the active app (the panel's own notices come while it is open), which macOS otherwise suppresses.
/// Every notice uses one identifier, so a new one replaces the last, as a new balloon does on Windows. macOS banners have
/// no warning icon: <c>warning</c> decides only the log level.
/// </summary>
internal sealed class Notifications
{
    /// <summary>The one request identifier (UNUserNotificationCenter.h:62: a request with an existing identifier replaces it).</summary>
    internal const string RequestIdentifier = AppBundle.Identifier + ".notice";

    private static ILog s_log = NullLog.Instance;
    private static readonly Lazy<nint> AuthorizationHandler = new(MakeAuthorizationHandler);
    private static readonly Lazy<nint> Delegate = new(MakeDelegate);

    private readonly ILog _log;
    private readonly bool _available;

    public Notifications(ILog log, AppBundle? bundle)
    {
        _log = log;
        _available = bundle is not null;
        if (!_available) return;
        s_log = log;
        try
        {
            using var pool = ObjC.AutoreleasePool.Begin();
            var center = UserNotifications.CurrentCenter();
            ObjC.SendVoid(center, ObjC.Sel("setDelegate:"), Delegate.Value);
            ObjC.SendVoid(center, ObjC.Sel("requestAuthorizationWithOptions:completionHandler:"), UserNotifications.AuthorizationAlert, AuthorizationHandler.Value);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
        {
            _available = false;
            log.Warn($"macOS notifications are unavailable ({ex.Message}); notices go to this log only.");
        }
    }

    /// <summary>What a notice becomes: its title (Dictum's name when empty), its body, and the shared identifier.</summary>
    internal static (string Title, string Body, string Identifier) Compose(string title, string text) =>
        (string.IsNullOrWhiteSpace(title) ? "Dictum" : title.Trim(), text.Trim(), RequestIdentifier);

    public void Show(string title, string text, bool warning)
    {
        var (heading, body, identifier) = Compose(title, text);
        if (!_available)
        {
            _log.Warn($"Notice (not shown: macOS notifications need the installed Dictum.app): {heading}: {body}");
            return;
        }
        var line = $"Notice: {heading}: {body}";
        if (warning) _log.Warn(line);
        else _log.Info(line);
        using var pool = ObjC.AutoreleasePool.Begin();
        var content = ObjC.Send(ObjC.Send(ObjC.Class("UNMutableNotificationContent"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        try
        {
            ObjC.SendVoid(content, ObjC.Sel("setTitle:"), ObjC.NSString(heading));
            ObjC.SendVoid(content, ObjC.Sel("setBody:"), ObjC.NSString(body));
            var request = ObjC.Send(ObjC.Class("UNNotificationRequest"), ObjC.Sel("requestWithIdentifier:content:trigger:"), ObjC.NSString(identifier), content, 0);
            ObjC.SendVoid(UserNotifications.CurrentCenter(), ObjC.Sel("addNotificationRequest:withCompletionHandler:"), request, 0);
        }
        finally
        {
            ObjC.Release(content);
        }
    }

    private static unsafe nint MakeAuthorizationHandler() => Blocks.Global((nint)(delegate* unmanaged<nint, byte, nint, void>)&OnAuthorization);

    /// <summary>requestAuthorization's completion handler, <c>void (^)(BOOL granted, NSError *error)</c>, on a system queue.</summary>
    [UnmanagedCallersOnly]
    private static void OnAuthorization(nint block, byte granted, nint error)
    {
        try
        {
            if (granted != 0) s_log.Info("macOS notifications are allowed.");
            else s_log.Warn($"macOS notifications are not allowed ({(error == 0 ? "turned off for Dictum in System Settings > Notifications" : ObjC.FromNSString(ObjC.Send(error, ObjC.Sel("localizedDescription"))))}); notices go to this log only.");
        }
        catch
        {
            // never let an exception cross back into the system's queue
        }
    }

    /// <summary>One instance of a small NSObject subclass implementing the delegate's will-present method; owned for the process (the center's delegate is weak).</summary>
    private static unsafe nint MakeDelegate()
    {
        var cls = ObjC.DefineClass("DictumNotificationDelegate", "NSObject",
            ("userNotificationCenter:willPresentNotification:withCompletionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?"));
        return ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>Shows the banner and lists it in Notification Centre even while Dictum is active: calls the handler block with List | Banner.</summary>
    [UnmanagedCallersOnly]
    private static unsafe void WillPresent(nint self, nint sel, nint center, nint notification, nint handler)
    {
        var invoke = Blocks.InvokeOf(handler);
        if (invoke != 0) ((delegate* unmanaged<nint, nuint, void>)invoke)(handler, UserNotifications.PresentListAndBanner);
    }
}
