using System.Runtime.InteropServices;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// API keys on Windows (decision D1, 30/10/2026): the real Credential Manager under the account running the tests. Each test
/// removes only the entries it made, by id; RemoveAll is never called here, so no key the user stored is touched.
/// </summary>
public class CredentialManagerTests
{
    private const int ErrorNoSuchLogonSession = 1312;

    private static readonly string Value = "k-dictum-test-" + Guid.NewGuid().ToString("N");

    /// <summary>A session with no credential set (a service account on a build agent) cannot use Credential Manager at all: skipped, with why.</summary>
    private static void SkipWithoutCredentialSet(ICredentialStore store)
    {
        try
        {
            store.List();
        }
        catch (CredentialStoreException ex) when (ex.Message.Contains($"({ErrorNoSuchLogonSession})", StringComparison.Ordinal))
        {
            Assert.Skip("This logon session has no credential set (ERROR_NO_SUCH_LOGON_SESSION).");
        }
    }

    [Fact]
    public void A_key_round_trips_through_Credential_Manager_by_its_id_and_is_gone_after_remove()
    {
        TestEnv.WindowsOnly("Calls Windows Credential Manager (advapi32 CredWrite, CredRead, CredEnumerate, CredDelete).");
        var store = new CredentialManagerStore();
        SkipWithoutCredentialSet(store);
        using var scratch = new Scratch();
        var vault = new SecretVault(scratch.Paths, store);
        string? id = null;
        try
        {
            id = vault.Add(SecretStore.SpeechKey, "Dictum test " + new string('x', 70), Value);
            var listed = Assert.Single(vault.List(SecretStore.SpeechKey), k => k.Id == id);
            Assert.Equal(StoredKey.LabelMaxLength, listed.Label.Length);
            Assert.Equal(Value, vault.Get(id));
            Assert.Contains((SecretStore.SpeechKey, id), store.List());
            Assert.Equal(Value, store.Read(SecretStore.SpeechKey, id));
            Assert.DoesNotContain(Value, File.ReadAllText(scratch.Paths.KeysFile), StringComparison.Ordinal);
            Assert.True(vault.Remove(id));
            Assert.Null(store.Read(SecretStore.SpeechKey, id));
            Assert.DoesNotContain(vault.List(SecretStore.SpeechKey), k => k.Id == id);
            Assert.False(store.Delete(SecretStore.SpeechKey, id)); // already gone: false, not an exception
        }
        finally
        {
            if (id is not null) store.Delete(SecretStore.SpeechKey, id);
        }
    }

    [Fact]
    public void An_unknown_variable_is_refused_before_anything_is_written()
    {
        TestEnv.WindowsOnly("Calls Windows Credential Manager (advapi32 CredWrite, CredRead, CredEnumerate, CredDelete).");
        var store = new CredentialManagerStore();
        SkipWithoutCredentialSet(store);
        using var scratch = new Scratch();
        var before = store.List().Count;
        Assert.Throws<ArgumentException>(() => new SecretVault(scratch.Paths, store).Add("NOT_A_KEY", "Test", Value));
        Assert.Equal(before, store.List().Count);
    }

    [Fact]
    public void The_credential_is_persisted_for_this_computer_with_the_variable_as_its_user_name()
    {
        TestEnv.WindowsOnly("Calls Windows Credential Manager (advapi32 CredWrite, CredRead, CredEnumerate, CredDelete).");
        var store = new CredentialManagerStore();
        SkipWithoutCredentialSet(store);
        var id = StoredKeyId.New();
        try
        {
            store.Write(SecretStore.SpeechKey, id, Value);
            Assert.True(CredReadW(CredentialManagerStore.Target(SecretStore.SpeechKey, id), 1, 0, out var pointer));
            try
            {
                var raw = Marshal.PtrToStructure<RawCredential>(pointer);
                Assert.Equal(2u, raw.Persist); // CRED_PERSIST_LOCAL_MACHINE: not visible to this user on other computers
                Assert.Equal(1u, raw.Type); // CRED_TYPE_GENERIC
                Assert.Equal(SecretStore.SpeechKey, Marshal.PtrToStringUni(raw.UserName));
                Assert.Equal($"Dictum/{SecretStore.SpeechKey}/{id}", Marshal.PtrToStringUni(raw.TargetName));
                Assert.Equal(0, raw.Comment); // no label or other text beside the key
            }
            finally
            {
                CredFree(pointer);
            }
        }
        finally
        {
            store.Delete(SecretStore.SpeechKey, id);
        }
    }

    [Theory]
    [InlineData("Dictum/OPENAI_API_KEY/0123456789ab", "OPENAI_API_KEY", "0123456789ab")]
    [InlineData("dictum/openai_api_key/0123456789AB", "OPENAI_API_KEY", "0123456789ab")] // target names are case-insensitive
    [InlineData("Dictum/NOT_A_KEY/0123456789ab", null, null)]
    [InlineData("Dictum/OPENAI_API_KEY/short", null, null)]
    [InlineData("Dictum/OPENAI_API_KEY/0123456789ab/extra", null, null)]
    [InlineData("DictumX/OPENAI_API_KEY/0123456789ab", null, null)]
    public void Only_target_names_Dictum_writes_are_read_back_as_its_keys(string target, string? variable, string? id) =>
        Assert.Equal(variable is null ? null : (variable, id!), CredentialManagerStore.Parse(target));

    [StructLayout(LayoutKind.Sequential)]
    private struct RawCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
