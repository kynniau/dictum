using Dictum.Meetings.Store;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>
/// The owner key under two Dictum processes (08/11/2026): the keyring's lock is per process, so the key is stored add-only
/// (ICredentialStore.TryCreate). The process that loses must use the winner's key: a replaced owner key leaves every meeting
/// sealed with the first one unreadable for ever (decision D2).
/// </summary>
[Collection("Windows Credential Manager")]
public class MeetingKeyringRaceTests
{
    /// <summary>Another process stores its key between this process's first Read and its TryCreate.</summary>
    private sealed class LosingRaceStore(string winner) : ICredentialStore
    {
        public int Reads { get; private set; }
        public int TryCreates { get; private set; }
        public int Writes { get; private set; }
        private bool _stored;

        public bool Available => true;
        public string? Name => "Race store";

        public void Write(string variable, string id, string value)
        {
            Writes++;
            _stored = true; // the old create-or-replace path: this overwrites the other process's key
        }

        public bool TryCreate(string variable, string id, string value)
        {
            TryCreates++;
            _stored = true; // the other process won: its key is there now, this value is not stored
            return false;
        }

        public string? Read(string variable, string id)
        {
            Reads++;
            return _stored ? winner : null;
        }

        public bool Delete(string variable, string id) => false;
        public IReadOnlyList<(string Variable, string Id)> List() => [];
    }

    [Fact]
    public void The_process_that_loses_the_race_uses_the_key_the_winner_stored()
    {
        var winner = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var store = new LosingRaceStore(Convert.ToBase64String(winner));
        var key = new MeetingKeyring(store).OwnerKey();
        Assert.Equal(winner, key);
        Assert.Equal(1, store.TryCreates);
        Assert.Equal(2, store.Reads);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void A_key_minted_and_created_now_is_returned_and_read_back_the_same_next_time()
    {
        var store = new FakeCredentialStore();
        var first = new MeetingKeyring(store).OwnerKey();
        Assert.Equal(32, first.Length);
        Assert.Equal(Convert.ToBase64String(first), store.Read(MeetingKeyring.Variable, "owner"));
        var second = new MeetingKeyring(store).OwnerKey();
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_platform_with_no_credential_store_refuses_TryCreate_as_it_refuses_Write()
    {
        var ex = Assert.Throws<CredentialStoreException>(() => NoCredentialStore.Instance.TryCreate(SecretStore.SpeechKey, "0123456789ab", "v"));
        Assert.Equal("This platform has no credential store Dictum can use.", ex.Message);
    }

    [Fact]
    public void TryCreate_in_Credential_Manager_writes_once_and_leaves_an_existing_entry_unchanged()
    {
        TestEnv.WindowsOnly("Calls Windows Credential Manager (advapi32 CredWrite, CredRead, CredDelete) under a named mutex.");
        var store = new CredentialManagerStore();
        SkipWithoutCredentialSet(store);
        var id = StoredKeyId.New();
        try
        {
            Assert.True(store.TryCreate(SecretStore.SpeechKey, id, "k-dictum-test-first"));
            Assert.False(store.TryCreate(SecretStore.SpeechKey, id, "k-dictum-test-second"));
            Assert.Equal("k-dictum-test-first", store.Read(SecretStore.SpeechKey, id));
        }
        finally
        {
            store.Delete(SecretStore.SpeechKey, id);
        }
        Assert.Null(store.Read(SecretStore.SpeechKey, id));
    }

    [Fact]
    public void Two_parallel_TryCreate_calls_on_one_fresh_entry_create_it_exactly_once()
    {
        TestEnv.WindowsOnly("Calls Windows Credential Manager (advapi32 CredWrite, CredRead, CredDelete) under a named mutex.");
        var store = new CredentialManagerStore();
        SkipWithoutCredentialSet(store);
        for (var round = 0; round < 10; round++)
        {
            var id = StoredKeyId.New();
            try
            {
                using var start = new Barrier(2);
                var tasks = new[] { "k-dictum-test-a", "k-dictum-test-b" }
                    .Select(value => Task.Run(() => { start.SignalAndWait(TestContext.Current.CancellationToken); return (value, created: store.TryCreate(SecretStore.SpeechKey, id, value)); }))
                    .ToArray();
                var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
                var won = Assert.Single(results, r => r.created);
                Assert.Equal(won.value, store.Read(SecretStore.SpeechKey, id));
            }
            finally
            {
                store.Delete(SecretStore.SpeechKey, id);
            }
        }
    }

    private static void SkipWithoutCredentialSet(ICredentialStore store)
    {
        try
        {
            store.List();
        }
        catch (CredentialStoreException ex) when (ex.Message.Contains("(1312)", StringComparison.Ordinal))
        {
            Assert.Skip("This logon session has no credential set (ERROR_NO_SUCH_LOGON_SESSION).");
        }
    }
}
