using System.Text;
using Dictum.Mac.Interop;
using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.Mac.Shell;

/// <summary>
/// API keys and the Meetings owner key in the macOS login keychain (decision D1, 30/10/2026; design D2 and the orchestrator's
/// rulings of 08/11/2026): one generic password per key, service <c>Dictum/{VARIABLE}</c>, account <c>{id}</c>, label
/// <c>Dictum/{VARIABLE}/{id}</c> (the Windows target name, so Keychain Access and Credential Manager show the same name),
/// not synchronisable, the key as UTF-8 bytes. The labels the user typed live in keys.json (<see cref="SecretVault"/>), never here.
/// <para>
/// Deliberately absent from every query: kSecAttrAccessible ("not supported for macOS keychain items, unless the
/// kSecAttrSynchronizable attribute is also present", SecItem.h:185-186), kSecUseDataProtectionKeychain (needs an access-group
/// entitlement Dictum.entitlements does not carry; recalled, measurement M4), access-control keys, and kSecUseAuthenticationUI.
/// The last matters most: its Skip value makes items that need UI "silently skipped" (SecItem.h:1072-1074), so a refused read
/// of the Meetings owner key would look like no key, <see cref="Meetings.Store.MeetingKeyring"/> would mint a new one and
/// overwrite the real one, and every meeting would become unreadable. So <see cref="Read"/> returns null only for
/// errSecItemNotFound and throws for every other failure.
/// </para>
/// <para>
/// Only built by <see cref="MacPlatform"/> when the running binary is signed with a team identity (<see cref="CodeSignature"/>):
/// a legacy keychain item trusts the code that created it, and an ad-hoc signature changes with every build (recalled, to be
/// measured), which would prompt at launch, and D2 allows no prompt.
/// </para>
/// </summary>
internal sealed class KeychainStore : ICredentialStore
{
    /// <summary>The store's name in the self-test, where Windows shows "Windows Credential Manager". Owner to approve the wording.</summary>
    public const string PROPOSED_Name = "macOS Keychain";

    private readonly ILog _log;
    private readonly string _prefix;
    private readonly Lazy<bool> _probe;
    private volatile bool _failed;

    /// <param name="prefix">The service prefix: "Dictum/" in the app; a scratch prefix in tests so they never touch a real key.</param>
    public KeychainStore(ILog log, string prefix = "Dictum/")
    {
        _log = log;
        _prefix = prefix;
        _probe = new(Probe);
    }

    /// <summary>The keychain answered an attribute-only lookup of an item Dictum never writes, and no call since has failed unexpectedly.</summary>
    public bool Available => !_failed && _probe.Value;

    public string? Name => Available ? PROPOSED_Name : null;

    /// <summary>The service an entry is stored under.</summary>
    internal static string Service(string prefix, string variable) => prefix + variable;

    /// <summary>The label Keychain Access shows: the Windows target name.</summary>
    internal static string Label(string prefix, string variable, string id) => $"{prefix}{variable}/{id}";

    public void Write(string variable, string id, string value)
    {
        if (_failed) throw new CredentialStoreException("The macOS Keychain refused an earlier call this session; nothing was written.");
        var bytes = Encoding.UTF8.GetBytes(value);
        using var cf = new Owned();
        nint data = 0;
        try
        {
            data = cf.Add(CF.DataCreateMutableCopy(bytes));
            var call = "SecItemAdd";
            var add = cf.Add(Query(cf, variable, id, (Sec.K.AttrLabel, cf.Add(CF.String(Label(_prefix, variable, id)))), (Sec.K.ValueData, data)));
            var status = Sec.ItemAdd(add, 0);
            if (status == Sec.DuplicateItem)
            {
                call = "SecItemUpdate";
                var update = cf.Add(CF.DictionaryCreate([Sec.K.ValueData], [data]));
                status = Sec.ItemUpdate(cf.Add(Query(cf, variable, id)), update);
                if (status == Sec.ItemNotFound)
                {
                    // Deleted between the Add and the Update (another Dictum process, or Keychain Access): add once more.
                    call = "SecItemAdd";
                    status = Sec.ItemAdd(add, 0);
                }
            }
            if (status != Sec.Success) throw Refused(call, variable, status);
        }
        finally
        {
            CF.DataZero(data); // the key does not linger in CF's freed memory
            Array.Clear(bytes);
        }
    }

    public string? Read(string variable, string id)
    {
        using var cf = new Owned();
        var query = cf.Add(Query(cf, variable, id, (Sec.K.ReturnData, CF.BooleanTrue), (Sec.K.MatchLimit, Sec.K.MatchLimitOne)));
        var status = Sec.ItemCopyMatching(query, out var result);
        cf.Add(result);
        if (!ReadFound(status, variable)) return null;
        if (result == 0 || CF.GetTypeID(result) != CF.DataGetTypeID()) throw Unexpected("SecItemCopyMatching", variable, "something other than data");
        // simplification: the CFData Security returns is immutable, so its copy of the key is freed without zeroing; the managed copy is cleared. Retire if CF gains a supported way to clear it.
        var bytes = CF.DataBytes(result);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public bool Delete(string variable, string id)
    {
        using var cf = new Owned();
        var status = Sec.ItemDelete(cf.Add(Query(cf, variable, id)));
        return Classify(status) switch
        {
            Outcome.Success => true,
            Outcome.NotFound => false,
            _ => throw Refused("SecItemDelete", variable, status),
        };
    }

    /// <summary>
    /// Every entry under a variable Dictum reads (<see cref="SecretStore.KeyNames"/>) with an id <see cref="SecretVault"/> makes;
    /// attributes only (no kSecReturnData), so no value is decrypted. SecItem has no "service begins with" match (recalled), hence
    /// one query per variable. The Meetings owner key is under a variable outside that list and its id is not a stored-key id, so
    /// it is never listed, as on Windows.
    /// </summary>
    public IReadOnlyList<(string Variable, string Id)> List()
    {
        var found = new List<(string, string)>();
        foreach (var variable in SecretStore.KeyNames)
        {
            using var cf = new Owned();
            var query = cf.Add(CF.DictionaryCreate(
                [Sec.K.Class, Sec.K.AttrService, Sec.K.AttrSynchronizable, Sec.K.ReturnAttributes, Sec.K.MatchLimit],
                [Sec.K.ClassGenericPassword, cf.Add(CF.String(Service(_prefix, variable))), CF.BooleanFalse, CF.BooleanTrue, Sec.K.MatchLimitAll]));
            var status = Sec.ItemCopyMatching(query, out var result);
            cf.Add(result);
            var outcome = Classify(status);
            if (outcome == Outcome.NotFound) continue;
            if (outcome == Outcome.Refused) throw Refused("SecItemCopyMatching", variable, status);
            if (result == 0 || CF.GetTypeID(result) != CF.ArrayGetTypeID()) throw Unexpected("SecItemCopyMatching", variable, "something other than a list");
            var count = CF.ArrayGetCount(result);
            for (nint i = 0; i < count; i++)
            {
                var item = CF.ArrayGetValueAtIndex(result, i);
                if (item == 0 || CF.GetTypeID(item) != CF.DictionaryGetTypeID()) continue;
                var account = CF.DictionaryGetValue(item, Sec.K.AttrAccount); // a CFString (recalled; type-checked here)
                if (account == 0 || CF.GetTypeID(account) != CF.StringGetTypeID()) continue;
                if (CF.ToString(account) is { } id && StoredKeyId.IsValid(id)) found.Add((variable, id));
            }
        }
        return found;
    }

    /// <summary>Not-found or found both mean the keychain answers; anything else is logged and the store is unavailable for this process.</summary>
    private bool Probe()
    {
        try
        {
            using var cf = new Owned();
            var query = cf.Add(CF.DictionaryCreate(
                [Sec.K.Class, Sec.K.AttrService, Sec.K.AttrAccount, Sec.K.AttrSynchronizable, Sec.K.ReturnAttributes, Sec.K.MatchLimit],
                [Sec.K.ClassGenericPassword, cf.Add(CF.String(_prefix + ".probe")), cf.Add(CF.String("available")), CF.BooleanFalse, CF.BooleanTrue, Sec.K.MatchLimitOne]));
            var status = Sec.ItemCopyMatching(query, out var result);
            cf.Add(result);
            if (Classify(status) != Outcome.Refused) return true;
            _log.Warn($"Keychain: the availability check returned {Sec.Describe(status)}; the credential store is unavailable.");
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.Warn($"Keychain: Security.framework could not be loaded ({e.GetType().Name}); the credential store is unavailable.");
            return false;
        }
    }

    /// <summary>The base query every per-entry call uses, plus <paramref name="extra"/>: class, service, account, not synchronisable.</summary>
    private nint Query(Owned cf, string variable, string id, params (nint Key, nint Value)[] extra)
    {
        nint[] keys = [Sec.K.Class, Sec.K.AttrService, Sec.K.AttrAccount, Sec.K.AttrSynchronizable, .. extra.Select(e => e.Key)];
        nint[] values = [Sec.K.ClassGenericPassword, cf.Add(CF.String(Service(_prefix, variable))), cf.Add(CF.String(id)), CF.BooleanFalse, .. extra.Select(e => e.Value)];
        return CF.DictionaryCreate(keys, values);
    }

    /// <summary>What a SecItem status means to the store. Only errSecItemNotFound is "no entry"; every other failure, including
    /// errSecInteractionNotAllowed, errSecAuthFailed and errSecUserCanceled, is a refusal, never "no entry" (design D2: a refused
    /// owner-key read that looked like no key would re-key Meetings). No native call.</summary>
    internal enum Outcome { Success, NotFound, Refused }

    internal static Outcome Classify(int status) => status switch
    {
        Sec.Success => Outcome.Success,
        Sec.ItemNotFound => Outcome.NotFound,
        _ => Outcome.Refused,
    };

    /// <summary>Read's status step: true for an entry, false for none, and a thrown refusal for anything else. No native call.</summary>
    internal bool ReadFound(int status, string variable) => Classify(status) switch
    {
        Outcome.Success => true,
        Outcome.NotFound => false,
        _ => throw Refused("SecItemCopyMatching", variable, status),
    };

    /// <summary>
    /// Logs the refusal (the call, the service and the status name; never a value, label or id), marks the store unavailable
    /// for the rest of the process, and returns the exception to throw. Status names only, no native call (the system's message
    /// text is left out so this stays pure).
    /// </summary>
    internal CredentialStoreException Refused(string call, string variable, int status)
    {
        _failed = true;
        var described = $"{Sec.StatusName(status)} ({status})";
        _log.Warn($"Keychain: {call} on {Service(_prefix, variable)} returned {described}; the credential store is unavailable.");
        return new CredentialStoreException($"{call} failed: {described}.", status);
    }

    private CredentialStoreException Unexpected(string call, string variable, string what)
    {
        _failed = true;
        _log.Warn($"Keychain: {call} on {Service(_prefix, variable)} returned {what}; the credential store is unavailable.");
        return new CredentialStoreException($"{call} returned {what}.");
    }

    /// <summary>The CF objects one call created or copied, released together (0 is skipped). kSec* and kCFBoolean* globals are never added.</summary>
    private sealed class Owned : IDisposable
    {
        private readonly List<nint> _objects = [];

        public nint Add(nint cf)
        {
            if (cf != 0) _objects.Add(cf);
            return cf;
        }

        public void Dispose()
        {
            foreach (var cf in _objects) CF.Release(cf);
            _objects.Clear();
        }
    }
}
