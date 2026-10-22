using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// "Launch at login" on macOS: the running app's own login item through SMAppService (ServiceManagement, macOS 13+;
/// docs/DECISIONS.md, Dictum for macOS, 22/10/2026), which lists Dictum in System Settings &gt; General &gt; Login Items and
/// follows the app if the user moves it. Only Dictum.app registers: SMAppService also registers a bare executable (seen on
/// this Mac, 23/10/2026: a scratch apphost went from NotFound to Enabled), and a build tree's copy starting at every login
/// is not what the switch means. The register and unregister calls run off the UI thread.
/// </summary>
internal sealed class LoginItem(ILog log, AppBundle? bundle) : IStartupRegistration
{
    // PROPOSED 23/10/2026 (shown as "Uses the {Mechanism}." on Settings > General).
    internal const string MechanismText = "macOS login item (System Settings > General > Login Items)";

    // PROPOSED 23/10/2026.
    internal const string NeedsBundle = "Launch at login works only in the installed Dictum.app, not in this copy.";

    // PROPOSED 23/10/2026.
    internal const string NeedsApproval = "macOS needs your approval first: turn Dictum on in System Settings > General > Login Items, which is open now.";

    public string Mechanism => MechanismText;

    public bool IsEnabled()
    {
        if (bundle is null) return false;
        using var pool = ObjC.AutoreleasePool.Begin();
        return ServiceManagement.Status(ServiceManagement.MainAppService()) == ServiceManagement.Enabled;
    }

    public Task<string?> SetEnabledAsync(bool enabled)
    {
        if (bundle is null) return Task.FromResult<string?>(enabled ? NeedsBundle : null);
        return Task.Run(() =>
        {
            using var pool = ObjC.AutoreleasePool.Begin();
            var service = ServiceManagement.MainAppService();
            var ok = ServiceManagement.Call(service, enabled ? "registerAndReturnError:" : "unregisterAndReturnError:", out var code, out var text);
            var status = ServiceManagement.Status(service);
            var problem = Problem(enabled, ok, code, text, status);
            if (problem == NeedsApproval) ServiceManagement.OpenLoginItemsSettings();
            if (problem is null) log.Info($"Launch at login turned {(enabled ? "on" : "off")} (SMAppService status {status}).");
            else log.Warn($"Launch at login could not be turned {(enabled ? "on" : "off")}: SMAppService error {code} ({text ?? "no description"}), status {status}.");
            return problem;
        });
    }

    /// <summary>
    /// What the user is told after a register or unregister, from its result and the status that followed: null when the
    /// switch now matches. Turning on: already registered counts as done; denied by the user, or registered but waiting for
    /// approval, asks for the approval in System Settings. Turning off: not registered at all counts as done.
    /// </summary>
    internal static string? Problem(bool enabling, bool ok, long errorCode, string? errorText, long status)
    {
        if (enabling)
        {
            if (status == ServiceManagement.Enabled && (ok || errorCode == ServiceManagement.ErrorAlreadyRegistered)) return null;
            if (status == ServiceManagement.RequiresApproval || errorCode == ServiceManagement.ErrorLaunchDeniedByUser) return NeedsApproval;
            return $"macOS did not turn on launch at login: {errorText ?? $"status {status}"}"; // PROPOSED 23/10/2026
        }
        if (ok || errorCode == ServiceManagement.ErrorJobNotFound) return null;
        return $"macOS did not turn off launch at login: {errorText ?? $"status {status}"}"; // PROPOSED 23/10/2026
    }
}
