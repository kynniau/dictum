using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictum.Platform;

namespace Dictum.Settings;

/// <summary>
/// A stored key's id (API keys, 30/10/2026): twelve lower-case hex characters made by <see cref="New"/>. settings.json's
/// keys.active may also name the two sources that are not stored keys: <see cref="Environment"/> (a real environment variable) and
/// <see cref="File"/> (a line in a .env file).
/// </summary>
public static class StoredKeyId
{
    /// <summary>keys.active: the environment variable of the same name.</summary>
    public const string Environment = "env";

    /// <summary>keys.active: the .env line (the config folder's, else the one beside the program).</summary>
    public const string File = "file";

    public static bool IsValid(string? id) => id is { Length: 12 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>What keys.active may hold for a variable: a stored key's id, or one of the two sources.</summary>
    public static bool IsChoice(string? id) => id is Environment or File || IsValid(id);

    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}

/// <summary>One stored key as keys.json and the credential store describe it together. Never holds the value.</summary>
/// <param name="Label">The user's name for it; empty for a key the store holds that keys.json does not list.</param>
/// <param name="Added">The day it was added; null when keys.json does not list it.</param>
public sealed record StoredKey(string Id, string Variable, string Label, DateOnly? Added)
{
    /// <summary>The longest label kept, in UTF-16 characters (the brief, 30/10/2026: trimmed, at most 60, no control characters).</summary>
    public const int LabelMaxLength = 60;

    /// <summary>
    /// The label with every control character removed, trimmed, then cut to <see cref="LabelMaxLength"/> characters (never through a
    /// surrogate pair) and trimmed again.
    /// </summary>
    public static string CleanLabel(string? label)
    {
        var text = new string((label ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length <= LabelMaxLength) return text;
        return text[..(char.IsHighSurrogate(text[LabelMaxLength - 1]) ? LabelMaxLength - 1 : LabelMaxLength)].TrimEnd();
    }

    /// <summary>
    /// True when the label may be shown beside a key with this value: it is not empty, holds no part of the value at least eight
    /// characters long, and is not held inside the value (decision D2: never any part of a key on screen; a key pasted into the
    /// Label field, or a keys.json edited by hand, must not reach a page or the self-test through the label).
    /// </summary>
    public static bool LabelIsSafe(string label, string value) =>
        label.Length > 0 && !label.Contains(value, StringComparison.Ordinal) && !SharesRun(label, value, 8);

    private static bool SharesRun(string label, string value, int run)
    {
        for (var i = 0; i + run <= label.Length; i++)
        {
            if (value.Contains(label.AsSpan(i, run), StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// Several API keys per service, kept in the operating system's credential store (decision D1, 30/10/2026; SPEC §5, §9, §17
/// amended 30/10/2026). The values live only in <see cref="IPlatform.Credentials"/>; the id, variable, label and date of each
/// live in keys.json in the config folder, and which one a service uses in settings.json's keys.active. Neither file ever
/// holds a value. Shared by every platform; only the store differs.
/// </summary>
public interface ISecretVault
{
    /// <summary>False where the platform has no credential store Dictum can use: <see cref="Add"/> is refused and the lists are empty.</summary>
    bool Available { get; }

    /// <summary>The store's name ("Windows Credential Manager"), or null when unavailable.</summary>
    string? StoreName { get; }

    /// <summary>
    /// Stores <paramref name="value"/> for <paramref name="variable"/> (one of <see cref="SecretStore.KeyNames"/>) under
    /// <paramref name="label"/> and returns its new id. Throws <see cref="ArgumentException"/> for an unknown variable, an empty
    /// label, a value that is blank, the placeholder, too long or holds a control character, or a label that holds part of the
    /// value; <see cref="CredentialStoreException"/> when the store refuses; <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when keys.json cannot be
    /// written (the stored value is removed again). No message ever holds the value.
    /// </summary>
    string Add(string variable, string label, string value);

    /// <summary>The stored keys for <paramref name="variable"/>: ids, labels and dates, never values.</summary>
    IReadOnlyList<StoredKey> List(string variable);

    /// <summary>The value of the stored key <paramref name="id"/>, or null when there is none.</summary>
    string? Get(string id);

    /// <summary>Removes the stored key and its keys.json entry, and clears keys.active where it named it. False when there was none.</summary>
    bool Remove(string id);

    /// <summary>
    /// Removes every key Dictum stored, keys.json's entries, and every keys.active choice of a stored key. Returns how many were
    /// removed. Each entry is tried; when any is refused, the rest are still removed and one <see cref="CredentialStoreException"/>
    /// at the end gives how many were refused and the system's codes (no value), and their labels and choices are kept.
    /// </summary>
    int RemoveAll();

    /// <summary>
    /// Raised after every <see cref="Add"/>, <see cref="Remove"/> and <see cref="RemoveAll"/> that changed something (lens minor 1,
    /// 30/10/2026): a change in the credential store writes no watched file when keys.json is absent, so the app reloads on this
    /// instead, and a removed key is not sent again. Raised on the caller's thread.
    /// </summary>
    event Action? Changed;
}

/// <inheritdoc cref="ISecretVault"/>
public sealed class SecretVault(AppPaths paths, ICredentialStore store, Func<DateOnly>? today = null) : ISecretVault
{
    /// <summary>The most a key may be: Credential Manager's CRED_MAX_CREDENTIAL_BLOB_SIZE, 5*512 bytes (CREDENTIALW, Microsoft Learn, read 30/10/2026), as UTF-8.</summary>
    public const int ValueMaxBytes = 5 * 512;

    private readonly object _gate = new();

    public bool Available => store.Available;

    public string? StoreName => store.Available ? store.Name : null;

    /// <summary>A vault over a store that has nothing: for tests and code paths with no platform.</summary>
    public static SecretVault Unavailable(AppPaths paths) => new(paths, NoCredentialStore.Instance);

    public event Action? Changed;

    public string Add(string variable, string label, string value)
    {
        var id = AddLocked(variable, label, value);
        Changed?.Invoke();
        return id;
    }

    private string AddLocked(string variable, string label, string value)
    {
        CheckVariable(variable);
        var cleanLabel = StoredKey.CleanLabel(label);
        if (cleanLabel.Length == 0) throw new ArgumentException("The label is empty.", nameof(label));
        var cleanValue = (value ?? "").Trim();
        if (cleanValue.Length == 0 || cleanValue == SecretStore.Placeholder) throw new ArgumentException("The key is empty.", nameof(value));
        if (cleanValue.Any(char.IsControl)) throw new ArgumentException("The key holds a line break or another control character.", nameof(value));
        if (Encoding.UTF8.GetByteCount(cleanValue) > ValueMaxBytes) throw new ArgumentException($"The key is longer than {ValueMaxBytes} bytes.", nameof(value));
        if (!StoredKey.LabelIsSafe(cleanLabel, cleanValue)) throw new ArgumentException("The label holds part of the key.", nameof(label));
        if (!store.Available) throw new CredentialStoreException("This platform has no credential store Dictum can use.");
        lock (_gate)
        {
            var file = KeysFile.Read(paths.KeysFile);
            if (!file.Usable) throw new IOException(KeysFile.Unreadable);
            var taken = store.List().Select(e => e.Id).Concat(file.Entries.Select(e => e.Id)).ToHashSet(StringComparer.Ordinal);
            string id;
            do id = StoredKeyId.New(); while (taken.Contains(id));
            store.Write(variable, id, cleanValue);
            try
            {
                KeysFile.Write(paths.KeysFile, [.. file.Entries, new StoredKey(id, variable, cleanLabel, (today ?? Today)())]);
            }
            catch
            {
                store.Delete(variable, id); // no value without its label: the store is put back as it was
                throw;
            }
            return id;
        }
    }

    /// <summary>
    /// As <see cref="ISecretVault.List"/>, with the label blanked where it holds any eight characters of its key (lens, 30/10/2026: the
    /// rule <see cref="SecretStore.Describe"/> applies, here too, so no caller can show a key through a hand-edited keys.json).
    /// </summary>
    public IReadOnlyList<StoredKey> List(string variable)
    {
        CheckVariable(variable);
        return All().Where(k => k.Variable == variable)
            .Select(k => k.Label.Length > 0 && store.Read(k.Variable, k.Id) is { } value && !StoredKey.LabelIsSafe(k.Label, value) ? k with { Label = "" } : k)
            .ToList();
    }

    public string? Get(string id)
    {
        if (!StoredKeyId.IsValid(id)) return null;
        var entry = store.List().FirstOrDefault(e => e.Id == id);
        return entry.Variable is null ? null : store.Read(entry.Variable, id);
    }

    public bool Remove(string id)
    {
        if (!StoredKeyId.IsValid(id)) return false;
        bool removed;
        lock (_gate)
        {
            var stored = store.List().Where(e => e.Id == id).ToList();
            removed = false;
            foreach (var (variable, _) in stored) removed |= store.Delete(variable, id);
            var file = KeysFile.Read(paths.KeysFile);
            if (file.Usable && file.Entries.Any(e => e.Id == id))
            {
                KeysFile.Write(paths.KeysFile, file.Entries.Where(e => e.Id != id).ToList());
                removed = true;
            }
            ClearActive(chosen => chosen == id);
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public int RemoveAll()
    {
        var count = 0;
        var refused = new List<(string Variable, string Id, int? Code)>();
        lock (_gate)
        {
            foreach (var (variable, id) in store.List())
            {
                try
                {
                    if (store.Delete(variable, id)) count++;
                }
                catch (CredentialStoreException ex)
                {
                    refused.Add((variable, id, ex.Code)); // the others are still removed
                }
            }
            var kept = refused.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            if (kept.Count == 0)
            {
                if (File.Exists(paths.KeysFile)) File.Delete(paths.KeysFile);
            }
            else if (KeysFile.Read(paths.KeysFile) is { Usable: true } file)
            {
                KeysFile.Write(paths.KeysFile, file.Entries.Where(e => kept.Contains(e.Id)).ToList());
            }
            // else: keys remain and keys.json cannot be read now (a parse error, or a passing sharing violation): left as it is (re-lens minor 2)
            ClearActive(chosen => StoredKeyId.IsValid(chosen) && !kept.Contains(chosen));
        }
        if (count > 0) Changed?.Invoke();
        if (refused.Count > 0)
        {
            var codes = string.Join(", ", refused.Select(r => r.Code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"));
            throw new CredentialStoreException($"{refused.Count} of {refused.Count + count} stored keys could not be removed (system error {codes}).");
        }
        return count;
    }

    /// <summary>
    /// Every stored key with its value, read once per configuration load for <see cref="SecretStore"/>. Empty when the store is
    /// unavailable; a store that refuses gives what it could read and the reason in <paramref name="problem"/> (never a value).
    /// </summary>
    public IReadOnlyList<(StoredKey Key, string Value)> Snapshot(out string? problem)
    {
        problem = null;
        if (!store.Available) return [];
        try
        {
            var result = new List<(StoredKey, string)>();
            foreach (var key in All())
            {
                if (store.Read(key.Variable, key.Id) is { Length: > 0 } value) result.Add((key, value));
            }
            return result;
        }
        catch (CredentialStoreException ex)
        {
            problem = $"Stored keys could not be read from {store.Name}: {ex.Message}";
            return [];
        }
    }

    /// <summary>
    /// Writes settings.json's keys.active.{variable}: a stored key's id, <see cref="StoredKeyId.Environment"/>,
    /// <see cref="StoredKeyId.File"/>, or null for no choice (Dictum's order decides). False, with the reason, when it cannot be written.
    /// </summary>
    public static bool SetActive(AppPaths paths, string variable, string? id, out string? failure)
    {
        CheckVariable(variable);
        if (id is not null && !StoredKeyId.IsChoice(id)) throw new ArgumentException("Not a stored key's id, \"env\" or \"file\".", nameof(id));
        return SettingsFile.SetValue(paths.SettingsFile, $"keys.active.{variable}", id is null ? null : JsonValue.Create(id), out failure);
    }

    /// <summary>keys.json's entries for keys the store holds, in keys.json's order, then any the store holds that keys.json does not list (no label).</summary>
    private List<StoredKey> All()
    {
        if (!store.Available) return [];
        var stored = store.List();
        var listed = KeysFile.Read(paths.KeysFile).Entries;
        var result = listed.Where(k => stored.Contains((k.Variable, k.Id))).ToList();
        result.AddRange(stored.Where(s => !listed.Any(k => k.Id == s.Id && k.Variable == s.Variable)).Select(s => new StoredKey(s.Id, s.Variable, "", null)));
        return result;
    }

    /// <summary>Clears the keys.active entries whose choice <paramref name="clears"/> matches. Best effort: a choice left behind names nothing and is passed over.</summary>
    private void ClearActive(Func<string, bool> clears)
    {
        var active = SettingsLoader.LoadFile(paths.SettingsFile) is { Usable: true } file ? file.Settings.Keys.Active : new Dictionary<string, string>();
        foreach (var (variable, chosen) in active)
        {
            if (clears(chosen)) SettingsFile.SetValue(paths.SettingsFile, $"keys.active.{variable}", null);
        }
    }

    private static void CheckVariable(string variable)
    {
        if (!SecretStore.KeyNames.Contains(variable)) throw new ArgumentException($"{variable} is not a key Dictum reads.", nameof(variable));
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);
}

/// <summary>
/// keys.json in the config folder: <c>{"keys": [{"id", "variable", "label", "added"}]}</c>, the date as yyyy-MM-dd. Labels and
/// dates only, never a value; written atomically like settings.json and watched like it (<see cref="App.AppRules.ReloadsOn"/>).
/// </summary>
public static class KeysFile
{
    /// <summary>Why a change was refused: keys.json exists but cannot be read, so it is never written over. Names no value.</summary>
    public const string Unreadable = "keys.json could not be read, so it was left as it is; fix or delete it, then try again.";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="Usable">False when the file exists but could not be read or parsed (its entries are unknown).</param>
    public sealed record Contents(IReadOnlyList<StoredKey> Entries, bool Usable);

    /// <summary>The entries, each checked on its own: an entry with an unknown variable or a bad id is passed over; labels are cleaned again.</summary>
    public static Contents Read(string path)
    {
        if (!File.Exists(path)) return new([], true);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                is not JsonObject { } root || root["keys"] is not JsonArray array) return new([], false);
            var entries = new List<StoredKey>();
            foreach (var node in array)
            {
                if (node is not JsonObject o) continue;
                var id = Text(o, "id");
                var variable = Text(o, "variable");
                if (!StoredKeyId.IsValid(id) || variable is null || !SecretStore.KeyNames.Contains(variable) || entries.Any(e => e.Id == id)) continue;
                DateOnly? added = DateOnly.TryParseExact(Text(o, "added"), "yyyy-MM-dd", out var day) ? day : null;
                entries.Add(new StoredKey(id!, variable, StoredKey.CleanLabel(Text(o, "label")), added));
            }
            return new(entries, true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new([], false);
        }
    }

    public static void Write(string path, IReadOnlyList<StoredKey> entries)
    {
        var array = new JsonArray();
        foreach (var e in entries)
        {
            array.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["variable"] = e.Variable,
                ["label"] = e.Label,
                ["added"] = e.Added?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SettingsFile.WriteAtomic(path, new JsonObject { ["keys"] = array }.ToJsonString(WriteOptions));
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
