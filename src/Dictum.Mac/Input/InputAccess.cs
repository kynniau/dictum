using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Input;

/// <summary>
/// The Accessibility grant on macOS (IPlatform.InputAccess; docs/DECISIONS.md "Request access", 27/10/2026): the active event
/// taps and the posted Cmd-V both need this process trusted under System Settings > Privacy &amp; Security > Accessibility.
/// <see cref="Check"/> is <c>AXIsProcessTrusted</c>, polled by the shell after a refusal so a grant is seen without a restart:
/// proved on hardware 27/10/2026 18:37 AEDT (macOS 27.2), the taps installed three seconds after the switch, no relaunch.
/// <see cref="Request"/> runs the prompting check, which shows the system's own "would like to control this computer" dialog
/// where macOS still shows it, then opens System Settings at the Accessibility page whatever the check answered: on that same
/// day the prompting check answered "trusted" for a build whose taps macOS refused (a row for the same bundle id, signed
/// differently, shown on: the owner's stale-entry loop), so the answer alone cannot decide, and the page is where the switch is.
/// </summary>
internal sealed class MacInputAccess(Action<string> open, ILog log) : IInputAccess
{
    /// <summary>
    /// The Settings deep link. Verified 27/10/2026 on macOS 27.2 (build 26B118): `open` on it shows Privacy &amp; Security >
    /// Accessibility, and the SecurityPrivacyExtension binary on that Mac carries the `Privacy_Accessibility` anchor.
    /// </summary>
    internal const string SettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    public event Action? Revoked;

    /// <summary>From a tap's own thread: it gave up after macOS kept disabling it (<see cref="EventTap"/>).</summary>
    internal void ReportRevoked() => Revoked?.Invoke();

    public InputAccessState Check() => AX.IsProcessTrusted() ? InputAccessState.Granted : InputAccessState.Denied;

    public void Request()
    {
        var trusted = AX.IsProcessTrustedPrompting();
        log.Info($"Accessibility requested: AXIsProcessTrustedWithOptions(prompt) answered {(trusted ? "trusted" : "not trusted")}, AXIsProcessTrusted {(AX.IsProcessTrusted() ? "trusted" : "not trusted")}; opening System Settings at the Accessibility page.");
        try
        {
            open(SettingsUrl);
        }
        catch (Exception ex)
        {
            log.Warn($"System Settings did not open at the Accessibility page ({ex.GetType().Name}): open it by hand and turn Dictum on.");
        }
    }
}
