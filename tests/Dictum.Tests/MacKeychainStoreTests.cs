using System.Diagnostics;
using Dictum.Mac;
using Dictum.Mac.Interop;
using Dictum.Mac.Shell;
using Dictum.Meetings.Store;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// The macOS keychain store (src/Dictum.Mac/Shell/KeychainStore.cs) and the team-identity gate in front of it
/// (src/Dictum.Mac/Shell/CodeSignature.cs). The real-keychain tests write scratch items under a service prefix
/// "DictumTest-{guid}/" to the login keychain and delete every one of them in <see cref="Dispose"/>, which then asserts the
/// store lists nothing. The test host is <c>dotnet</c>, not Dictum.app, so these prove the SecItem calls, not the access-control
/// behaviour of a signed or ad-hoc Dictum.app (that is the separate measurement probe). The fake-store contract sibling is
/// MeetingCryptoTests.No_credential_store_means_MeetingsLockedException_and_no_file_written.
/// </summary>
public sealed class MacKeychainStoreTests : IDisposable
{
    private const string Gate = "Set DICTUM_UI_TESTS=1 on a Mac (writes scratch items to the login keychain and deletes them).";
    private const string ValidId = "0123456789ab";

    private readonly string _prefix = $"DictumTest-{Guid.NewGuid():N}/";
    private readonly RecordingLog _log = new();
    private readonly List<(string Variable, string Id)> _written = [];
    private KeychainStore? _store;

    private KeychainStore Store()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        return _store ??= new KeychainStore(_log, _prefix);
    }

    private void Write(string variable, string id, string value)
    {
        _written.Add((variable, id));
        Store().Write(variable, id, value);
    }

    public void Dispose()
    {
        if (_store is null) return;
        // Every delete is attempted before anything is asserted, so one failure never leaves the rest in the keychain.
        var failures = new List<string>();
        foreach (var (variable, id) in _written.Append((MeetingKeyring.Variable, "owner")).Distinct())
        {
            try { _store.Delete(variable, id); }
            catch (Exception e) { failures.Add($"{variable}: {e.Message}"); }
        }
        Assert.Empty(failures);
        Assert.Empty(_store.List());
        foreach (var (variable, id) in _written) Assert.Null(_store.Read(variable, id));
        Assert.Null(_store.Read(MeetingKeyring.Variable, "owner"));
    }

    // ---- Everywhere (no native call) ---------------------------------------------------------------------------------

    [Fact]
    public void MacKeychain_status_names_and_entry_names_match_the_headers_and_windows()
    {
        Assert.Equal("errSecSuccess", Sec.StatusName(0));
        Assert.Equal("errSecDuplicateItem", Sec.StatusName(-25299));
        Assert.Equal("errSecItemNotFound", Sec.StatusName(-25300));
        Assert.Equal("errSecInteractionNotAllowed", Sec.StatusName(-25308));
        Assert.Equal("errSecMissingEntitlement", Sec.StatusName(-34018));
        Assert.Equal("errSecUserCanceled", Sec.StatusName(-128));
        Assert.Equal("OSStatus 12345", Sec.StatusName(12345));
        Assert.Equal(Sec.DuplicateItem, -25299);
        Assert.Equal(Sec.ItemNotFound, -25300);
        Assert.Equal(2u, Sec.CSSigningInformation);

        Assert.Equal("Dictum/ANTHROPIC_API_KEY", KeychainStore.Service("Dictum/", SecretStore.AnthropicKey));
        // The label Keychain Access shows is the target name Credential Manager shows on Windows.
        Assert.Equal(CredentialManagerStore.Target(SecretStore.AnthropicKey, ValidId), KeychainStore.Label("Dictum/", SecretStore.AnthropicKey, ValidId));
        Assert.Equal("macOS Keychain", KeychainStore.PROPOSED_Name);
    }

    [Fact]
    public void MacKeychain_only_item_not_found_means_no_entry_and_a_refusal_locks_the_store()
    {
        Assert.Equal(KeychainStore.Outcome.Success, KeychainStore.Classify(0));
        Assert.Equal(KeychainStore.Outcome.NotFound, KeychainStore.Classify(-25300)); // errSecItemNotFound
        foreach (var refused in new[] { -25308, -25293, -128, -25299, -34018, 12345 }) // InteractionNotAllowed, AuthFailed, UserCanceled, DuplicateItem, MissingEntitlement, unknown
            Assert.Equal(KeychainStore.Outcome.Refused, KeychainStore.Classify(refused));

        // Read's own step: found, none, and a thrown refusal, never null for a refusal (design D2: a refused owner-key read
        // that looked like no key would re-key Meetings).
        var reader = new KeychainStore(_log, _prefix);
        Assert.True(reader.ReadFound(0, MeetingKeyring.Variable));
        Assert.False(reader.ReadFound(-25300, MeetingKeyring.Variable));
        foreach (var refused in new[] { -25308, -25293, -128 })
        {
            var error = Assert.Throws<CredentialStoreException>(() => reader.ReadFound(refused, MeetingKeyring.Variable));
            Assert.Equal(refused, error.Code);
        }

        // A refusal on a fresh store makes it unavailable without the probe running (no native call) and carries the code.
        var store = new KeychainStore(_log, _prefix);
        var refusal = store.Refused("SecItemCopyMatching", SecretStore.AnthropicKey, -25308);
        Assert.Equal(-25308, refusal.Code);
        Assert.Contains("errSecInteractionNotAllowed", refusal.Message);
        Assert.False(store.Available);
        Assert.Null(store.Name);
        Assert.Throws<CredentialStoreException>(() => store.Write(SecretStore.AnthropicKey, ValidId, "refused"));
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("errSecInteractionNotAllowed", StringComparison.Ordinal));
    }

    [Fact]
    public void MacKeychain_store_is_on_only_for_dictums_own_team()
    {
        Assert.Equal("PX6W25UNPP", CodeSignature.DictumTeam);
        Assert.Equal((true, (string?)null), CodeSignature.Decide("PX6W25UNPP"));
        Assert.Equal((false, (string?)"Keychain store off: this build is not signed with a team identity"), CodeSignature.Decide(null));
        Assert.Equal((false, (string?)"Keychain store off: this build is not signed with Dictum's team identity (team UBF8T346G9 present)"), CodeSignature.Decide("UBF8T346G9"));
    }

    // ---- On a Mac ----------------------------------------------------------------------------------------------------

    [Fact]
    public void MacKeychain_signature_gate_agrees_with_codesign_and_decides_the_store()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only (Security.framework, codesign).");
        var team = CodeSignature.TeamIdentifier(_log);
        var expected = CodesignTeam(Environment.ProcessPath!);
        // The test host's own signature decides: the Microsoft-built dotnet host carries a team identifier, an ad-hoc one does not.
        Assert.Equal(expected, team);

        var log = new RecordingLog();
        using var platform = new MacPlatform(log, Path.GetTempPath());
        var (on, offLine) = CodeSignature.Decide(team);
        if (on)
        {
            Assert.IsType<KeychainStore>(platform.Credentials);
            Assert.DoesNotContain(log.Lines, l => l.Message.StartsWith("Keychain store off", StringComparison.Ordinal));
        }
        else
        {
            // The test host is never Dictum's team (an ad-hoc apphost, or Microsoft's dotnet host).
            Assert.Same(NoCredentialStore.Instance, platform.Credentials);
            Assert.Single(log.Lines, l => l.Message == offLine);
        }
    }

    /// <summary>The TeamIdentifier codesign reports for a binary; null for "not set" or an unsigned binary.</summary>
    private static string? CodesignTeam(string path)
    {
        var info = new ProcessStartInfo("/usr/bin/codesign") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "-dv", "--verbose=2", path }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("TeamIdentifier=", StringComparison.Ordinal));
        var value = line?["TeamIdentifier=".Length..].Trim();
        return value is null or "" or "not set" ? null : value;
    }

    [Fact]
    public void MacKeychain_is_available_on_a_signed_in_Mac()
    {
        var store = Store();
        Assert.True(store.Available);
        Assert.Equal(KeychainStore.PROPOSED_Name, store.Name);
        Assert.DoesNotContain(_log.Lines, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public void MacKeychain_write_then_read_returns_the_same_value()
    {
        Write(SecretStore.AnthropicKey, ValidId, "clé-🔑 test value");
        Assert.Equal("clé-🔑 test value", Store().Read(SecretStore.AnthropicKey, ValidId));
        Assert.DoesNotContain(_log.Lines, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public void MacKeychain_write_twice_replaces_the_value()
    {
        Write(SecretStore.OpenAiKey, ValidId, "first");
        Write(SecretStore.OpenAiKey, ValidId, "second");
        Assert.Equal("second", Store().Read(SecretStore.OpenAiKey, ValidId));
        Assert.Single(Store().List());
    }

    [Fact]
    public void MacKeychain_delete_removes_and_reports()
    {
        Write(SecretStore.GroqKey, ValidId, "to delete");
        Assert.True(Store().Delete(SecretStore.GroqKey, ValidId));
        Assert.Null(Store().Read(SecretStore.GroqKey, ValidId));
        Assert.False(Store().Delete(SecretStore.GroqKey, ValidId));
    }

    [Fact]
    public void MacKeychain_read_of_a_missing_item_is_null()
    {
        Assert.Null(Store().Read(SecretStore.GeminiKey, "abcdefabcdef"));
        Assert.True(Store().Available); // not-found is not a failure
    }

    [Fact]
    public void MacKeychain_list_names_only_valid_dictum_entries()
    {
        Write(SecretStore.AnthropicKey, ValidId, "listed");
        Write(SecretStore.SpeechKey, "ba9876543210", "listed too");
        Write(SecretStore.AnthropicKey, "not-an-id", "never listed"); // not a stored-key id
        Write(MeetingKeyring.Variable, "owner", "never listed"); // not a variable Dictum lists
        var listed = Store().List().OrderBy(e => e.Variable, StringComparer.Ordinal).ToList();
        Assert.Equal([(SecretStore.AnthropicKey, ValidId), (SecretStore.SpeechKey, "ba9876543210")], listed);
    }

    [Fact]
    public void MacKeychain_meeting_keyring_creates_then_rereads_the_owner_key()
    {
        var first = new MeetingKeyring(Store()).OwnerKey();
        Assert.Equal(32, first.Length);
        Assert.Equal(Convert.ToBase64String(first), Store().Read(MeetingKeyring.Variable, "owner"));
        Assert.Equal(first, new MeetingKeyring(Store()).OwnerKey());
        Assert.Empty(Store().List()); // the owner key is never listed
    }
}
