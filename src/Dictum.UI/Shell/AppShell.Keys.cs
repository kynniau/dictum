using Dictum.App;
using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.UI.Shell;

/// <summary>
/// API keys, the shell's half of the Settings key lists (K-UI lane, 04/11/2026; brief .demo-preview/design-api-keys.md §2): which key a
/// list entry names, choosing the one a service uses (<see cref="SecretVault.SetActive"/>), Check this key (<see cref="KeyCheck"/>, sent
/// through the provider client and only where the consent on record allows), and Remove all keys. A key value is read at the click that
/// needs it and handed straight on; nothing here keeps, logs or returns one.
/// </summary>
public sealed partial class AppShell
{
    // PROPOSED 04/11/2026 (approve-api-keys.md rows 37 and 38): the shell's lines for the key lists and Settings > Privacy.

    /// <summary>PROPOSED 04/11/2026 (row 37): Check this key was asked without the consent step for that host (the button is not shown then).</summary>
    internal const string CheckNeedsConsent = "Switch the service on first: the key is checked only with a service you agreed to in its consent step.";

    /// <summary>PROPOSED 04/11/2026 (row 37): the entry chosen no longer has a key (removed, or the file changed meanwhile).</summary>
    internal const string KeyGone = "That key is no longer there.";

    /// <summary>PROPOSED 04/11/2026 (row 38): Remove all keys' question title.</summary>
    internal const string RemoveAllTitle = "Remove all keys?";

    /// <summary>PROPOSED 04/11/2026 (row 38): Remove all keys' question.</summary>
    internal static string RemoveAllText(string store) =>
        $"Dictum removes every API key you added in Dictum from {store}, and their names. Keys in the key file (.env) and in environment variables are not touched. There is no undo: to use a key again, add it again.";

    /// <summary>PROPOSED 04/11/2026 (row 38): the question's buttons.</summary>
    internal const string RemoveAllButton = "Remove all keys";

    /// <summary>PROPOSED 04/11/2026 (row 38): done.</summary>
    internal static string RemovedKeys(int count) => count switch
    {
        0 => "There were no stored keys to remove.",
        1 => "1 stored key removed.",
        _ => $"{count} stored keys removed.",
    };

    /// <summary>
    /// The value of the key a list entry names, read now: a stored key's id (only one stored for <paramref name="variable"/>, never
    /// another service's), <see cref="StoredKeyId.Environment"/> or <see cref="StoredKeyId.File"/>. Null when it has none.
    /// Throws <see cref="CredentialStoreException"/> when the store cannot be read.
    /// </summary>
    internal string? KeyValue(string variable, string choice) => choice switch
    {
        StoredKeyId.Environment => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null,
        StoredKeyId.File => FileKey(Paths, variable),
        _ => Vault.List(variable).Any(k => k.Id == choice) ? Vault.Get(choice) : null,
    };

    /// <summary>
    /// The .env line for <paramref name="variable"/>, the config folder's before the one beside the program, as <see cref="SecretStore"/>
    /// reads them (a blank or placeholder value is none). The environment is left out: this is the file's own line.
    /// </summary>
    internal static string? FileKey(AppPaths paths, string variable) => SecretStore.Load(paths, environment: _ => null).Get(variable);

    /// <summary>
    /// Settings > a key list > "Use this key": writes keys.active.{variable} through <see cref="SecretVault.SetActive"/>, then reloads so
    /// the next dictation uses it. Null when saved; else why it could not be.
    /// </summary>
    public string? ChooseKey(string variable, string choice)
    {
        if (!SecretVault.SetActive(Paths, variable, choice, out var failure)) return failure ?? ModelCoordinator.WriteFailed;
        _log.Info($"Key choice saved for {variable}."); // PROPOSED 04/11/2026 (row 39): no id, no label, no value
        Reload(userRequested: false);
        return null;
    }

    /// <summary>
    /// Check this key (decision D3): one request with the key the entry names, to the host the consent on record names for the service
    /// that takes <paramref name="variable"/>, and nowhere else (<see cref="KeyCheck.Target"/>); through the provider client, so offline
    /// only refuses it. Returns what to say. The log names the variable and the host, never the key or the service's message.
    /// </summary>
    public async Task<string> CheckKeyAsync(string variable, string choice, CancellationToken cancel = default)
    {
        if (KeyCheck.Target(variable, _settings) is not { } target) return CheckNeedsConsent;
        var key = KeyValue(variable, choice);
        if (key is null) return KeyGone;
        _log.Info($"Check this key: one request for {variable} to {target.Host}."); // PROPOSED 04/11/2026 (row 39)
        var said = await KeyCheck.RunAsync(_http, target, key, _settings.OfflineOnly, cancel);
        _log.Info($"Check this key: {target.Host} {(said == KeyCheck.Works ? "accepted the key" : "did not accept it, or was not reached")}."); // PROPOSED (row 39)
        return said;
    }

    /// <summary>
    /// Settings > Privacy > "Remove all keys…": asks first, then <see cref="ISecretVault.RemoveAll"/> (every key Dictum stored, keys.json's
    /// entries and the choices that named them; the reload follows from <see cref="ISecretVault.Changed"/>). Null when not asked to go on;
    /// else what to say, with "Remove Dictum completely"'s own line (row 8) when the store refused some.
    /// </summary>
    public string? RemoveAllKeys()
    {
        var store = Vault.StoreName ?? "the credential store";
        if (Dialogs.Ask(RemoveAllTitle, RemoveAllText(store), new DialogChoice(RemoveAllButton, IsDanger: true), new DialogChoice("Cancel", IsDefault: true)) != 0) return null;
        try
        {
            var removed = Vault.RemoveAll();
            _log.Info($"Remove all keys: {removed} removed."); // PROPOSED 04/11/2026 (row 39)
            return RemovedKeys(removed);
        }
        catch (Exception ex) when (ex is CredentialStoreException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Remove all keys: {ex.Message}");
            return $"Done, except: the API keys stored in {store} ({ex.Message})"; // "Done, except:" and row 8, as Remove Dictum completely says them
        }
    }
}
