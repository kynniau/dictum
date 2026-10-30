namespace Dictum.Platform;

/// <summary>
/// The operating system's own credential store, reached through <see cref="IPlatform.Credentials"/> (API keys, decision D1,
/// 30/10/2026): Windows Credential Manager, the macOS login Keychain (K-Mac lane). It holds the key values and nothing else;
/// the labels, dates and the active choice are not secrets and live in keys.json and settings.json
/// (<see cref="Settings.SecretVault"/>, which every platform shares). Every entry is named by the variable it is a key for
/// (one of <see cref="Settings.SecretStore.KeyNames"/>) and an id <see cref="Settings.SecretVault"/> made.
/// </summary>
public interface ICredentialStore
{
    /// <summary>False where the platform has no store Dictum can use (tests, a platform not done yet): nothing can be added.</summary>
    bool Available { get; }

    /// <summary>The store's name as the system calls it, for the self-test ("Windows Credential Manager"); null when unavailable.</summary>
    string? Name { get; }

    /// <summary>Creates or replaces the entry. Throws <see cref="CredentialStoreException"/> when the system refuses (the message never holds the value).</summary>
    void Write(string variable, string id, string value);

    /// <summary>The entry's value, or null when there is none.</summary>
    string? Read(string variable, string id);

    /// <summary>Removes the entry; false when there was none. Throws <see cref="CredentialStoreException"/> when the system refuses.</summary>
    bool Delete(string variable, string id);

    /// <summary>Every entry Dictum made (its variable and id), whatever keys.json says; never a value.</summary>
    IReadOnlyList<(string Variable, string Id)> List();
}

/// <summary>The system refused a credential-store call. The message names the call and the system's code, never a value.</summary>
/// <param name="code">The system's error code (Windows: GetLastError), when there is one.</param>
public sealed class CredentialStoreException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>A platform with no credential store Dictum uses: unavailable, empty, and a write is refused.</summary>
public sealed class NoCredentialStore : ICredentialStore
{
    public static readonly NoCredentialStore Instance = new();

    private NoCredentialStore()
    {
    }

    public bool Available => false;

    public string? Name => null;

    public void Write(string variable, string id, string value) => throw new CredentialStoreException("This platform has no credential store Dictum can use.");

    public string? Read(string variable, string id) => null;

    public bool Delete(string variable, string id) => false;

    public IReadOnlyList<(string Variable, string Id)> List() => [];
}
