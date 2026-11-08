using Dictum.Mac.Interop;

namespace Dictum.Mac.Shell;

/// <summary>
/// Whether the running binary is signed with Dictum's own team identity (<see cref="DictumTeam"/>), which decides whether
/// <see cref="MacPlatform"/> uses the keychain (orchestrator rulings 08/11/2026). SecCodeCopySelf (SecCode.h:63) gives the
/// running code; SecCodeCopySigningInformation (SecCode.h:528) with kSecCSSigningInformation (SecCode.h:470) adds the "Signing"
/// keys, kSecCodeInfoTeamIdentifier among them (SecCode.h:512). Ad-hoc code has no certificates (SecCode.h:371-373), so (inferred) no
/// team identifier; unsigned code still succeeds, with no identifier at all (SecCode.h:341-343). The signature is not validated
/// first (SecCode.h:345-348 says the data may then be partial): this is an on/off switch for a store the keychain's own access
/// control protects, not a security check.
/// </summary>
internal static class CodeSignature
{
    /// <summary>
    /// Dictum's own team: the store is on only for a binary signed by it. Verified 08/11/2026 (about 14:45 AEDT, by the
    /// orchestrator) from <c>security find-identity -v -p codesigning</c> on Kyle's Mac: "Developer ID Application: Kynni Pty Ltd
    /// (PX6W25UNPP)". A team identifier is public (it is in every signature it makes), so it may be logged.
    /// </summary>
    public const string DictumTeam = "PX6W25UNPP";

    /// <summary>
    /// Whether a binary with <paramref name="team"/> (null: none) gets the keychain store, and the one log line when it does not.
    /// No native call.
    /// </summary>
    public static (bool KeychainOn, string? OffLine) Decide(string? team) => team switch
    {
        DictumTeam => (true, null),
        null => (false, "Keychain store off: this build is not signed with a team identity"),
        _ => (false, $"Keychain store off: this build is not signed with Dictum's team identity (team {team} present)"),
    };

    /// <summary>The running binary's team identifier, or null when it has none or the check fails (one log line in that case).</summary>
    public static string? TeamIdentifier(ILog log)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        nint code = 0, information = 0;
        try
        {
            var status = Sec.CodeCopySelf(Sec.CSDefaultFlags, out code);
            if (status != Sec.Success)
            {
                log.Warn($"Code signature: SecCodeCopySelf returned {Sec.Describe(status)}; treating this build as having no team identity.");
                return null;
            }
            status = Sec.CodeCopySigningInformation(code, Sec.CSSigningInformation, out information);
            if (status != Sec.Success)
            {
                log.Warn($"Code signature: SecCodeCopySigningInformation returned {Sec.Describe(status)}; treating this build as having no team identity.");
                return null;
            }
            if (information == 0 || CF.GetTypeID(information) != CF.DictionaryGetTypeID()) return null;
            var team = CF.DictionaryGetValue(information, Sec.K.CodeInfoTeamIdentifier);
            return team != 0 && CF.GetTypeID(team) == CF.StringGetTypeID() && CF.ToString(team) is { Length: > 0 } text ? text : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            log.Warn($"Code signature: Security.framework could not be loaded ({e.GetType().Name}); treating this build as having no team identity.");
            return null;
        }
        finally
        {
            if (information != 0) CF.Release(information);
            if (code != 0) CF.Release(code);
        }
    }
}
