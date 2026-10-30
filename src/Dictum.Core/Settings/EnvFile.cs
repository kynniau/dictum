using System.Text;

namespace Dictum.Settings;

/// <summary>
/// Minimal .env parser: KEY=VALUE lines, optional "export " prefix, '#' comments, blank lines,
/// single quotes (literal), double quotes (with \n \r \t \" \\ escapes), CRLF or LF, UTF-8 BOM.
/// Unquoted values end at " #" (inline comment) and are trimmed.
/// </summary>
public static class EnvFile
{
    public static Dictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) continue;

            result[key] = ParseValue(line[(eq + 1)..].Trim());
        }
        return result;
    }

    private static string ParseValue(string v)
    {
        if (v.Length >= 2 && v[0] == '\'' )
        {
            var end = v.IndexOf('\'', 1);
            return end > 0 ? v[1..end] : v[1..];
        }
        if (v.Length >= 1 && v[0] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < v.Length; i++)
            {
                var c = v[i];
                if (c == '"') break;
                if (c == '\\' && i + 1 < v.Length)
                {
                    i++;
                    sb.Append(v[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => v[i] });
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? v[..hash] : v).Trim();
    }
}

/// <summary>
/// Secret lookup (SPEC §5, standard dotenv behaviour): a real process/user/machine environment variable wins;
/// .env only fills gaps, the config-folder .env before the one beside the exe (SPEC §9). A staff machine or
/// Azure VM can therefore carry a machine-level ANTHROPIC_API_KEY and no .env at all. Values are never logged.
/// API keys (SPEC §5 amended 30/10/2026): keys added in Dictum live in the system's credential store (<see cref="SecretVault"/>),
/// several per variable; settings.json's keys.active chooses one source per variable, and <see cref="Get"/> gives the order.
/// </summary>
public sealed class SecretStore
{
    public const string AnthropicKey = "ANTHROPIC_API_KEY";

    /// <summary>Sent only to OpenAI's server (the OpenAI preset's address); see CloudPresets.</summary>
    public const string OpenAiKey = "OPENAI_API_KEY";

    /// <summary>Sent only to Google's Gemini server (the Google Gemini preset's address); see CloudPresets.</summary>
    public const string GeminiKey = "GEMINI_API_KEY";

    /// <summary>Sent only to an OpenAI-compatible server at an address that is not a preset's own (a custom server).</summary>
    public const string LlmKey = "LLM_API_KEY";

    /// <summary>Cloud speech: sent only to an Azure OpenAI resource (a host ending .openai.azure.com); see SpeechPresets.</summary>
    public const string AzureOpenAiKey = "AZURE_OPENAI_API_KEY";

    /// <summary>Cloud speech: sent only to Groq's server (api.groq.com); see SpeechPresets.</summary>
    public const string GroqKey = "GROQ_API_KEY";

    /// <summary>Cloud speech: sent only to a speech service at an address that is not a speech preset's own (a custom server).</summary>
    public const string SpeechKey = "SPEECH_API_KEY";

    /// <summary>Every key Dictum reads: each is registered with the log redactor and listed (present or absent) by the self-test.</summary>
    public static IReadOnlyList<string> KeyNames { get; } = [AnthropicKey, OpenAiKey, GeminiKey, LlmKey, AzureOpenAiKey, GroqKey, SpeechKey];

    private readonly Dictionary<string, (string Value, string Source)> _values = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyDictionary<string, string> _active;
    private readonly IReadOnlyList<(StoredKey Key, string Value)> _stored;

    private SecretStore(Func<string, string?> environment, IReadOnlyDictionary<string, string>? active, IReadOnlyList<(StoredKey Key, string Value)>? stored)
    {
        _environment = environment;
        _active = active ?? new Dictionary<string, string>();
        // A blank or placeholder value is "not set" in the credential store too, as in a .env line.
        _stored = (stored ?? []).Where(s => s.Value.Length > 0 && s.Value != Placeholder).ToList();
    }

    public static SecretStore Empty { get; } = new(_ => null, null, null);

    /// <param name="active">settings.json's keys.active (<see cref="KeysSettings.Active"/>): variable to a stored key's id, "env" or "file".</param>
    /// <param name="stored">Every stored key with its value (<see cref="SecretVault.Snapshot"/>), read once per load.</param>
    public static SecretStore FromContents(string? appDataEnv, string? exeDirEnv, Func<string, string?> environment,
        IReadOnlyDictionary<string, string>? active = null, IReadOnlyList<(StoredKey Key, string Value)>? stored = null)
    {
        var store = new SecretStore(environment, active, stored);
        // Lowest precedence first; later sources overwrite.
        if (exeDirEnv is not null) store.Merge(EnvFile.Parse(exeDirEnv), "exe-folder .env");
        if (appDataEnv is not null) store.Merge(EnvFile.Parse(appDataEnv), "config-folder .env");
        return store;
    }

    public static SecretStore Load(AppPaths paths, Func<string, string?>? environment = null, IReadOnlyDictionary<string, string>? active = null,
        IReadOnlyList<(StoredKey Key, string Value)>? stored = null) =>
        FromContents(ReadOrNull(paths.EnvFile), ReadOrNull(paths.ExeEnvFile), environment ?? Environment.GetEnvironmentVariable, active, stored);

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value "Create the key file…" writes after each variable (<see cref="KeyFile"/>) until the user pastes a key over it. It is
    /// "not set", as a blank value is: the service is not asked with it, so no dictation goes out with a key that cannot work, and the
    /// pages keep saying they wait for the key.
    /// </summary>
    public const string Placeholder = "your-key-here";

    /// <summary>
    /// A blank KEY= line, or one still holding <see cref="Placeholder"/>, is "not set" (as an empty environment variable is), so it
    /// never hides a lower-precedence value.
    /// </summary>
    private void Merge(Dictionary<string, string> values, string source)
    {
        foreach (var (k, v) in values)
        {
            if (v.Length > 0 && v != Placeholder) _values[k] = (v, source);
        }
    }

    /// <summary>
    /// The key for <paramref name="key"/> (API keys, SPEC §5 amended 30/10/2026): the source keys.active chose for it, when that
    /// source has a value (a stored key of this same variable, the environment variable, or the .env line); otherwise today's order,
    /// a real environment variable, then the config-folder .env, then the one beside the program; otherwise the stored key, when
    /// exactly one is stored for this variable. Two or more stored and none chosen: none is used, as Dictum cannot tell which.
    /// </summary>
    public string? Get(string key) => Resolve(key).Value;

    /// <summary>Where a key came from, for the self-test. Never returns the value, nor any part of it (decision D2).</summary>
    public string Describe(string key)
    {
        var (value, source, stored) = Resolve(key);
        if (value is null) return StoredCount(key) > 1 ? "absent (none chosen)" : "absent"; // the self-test adds the count (KeyLines)
        if (stored is null) return $"present ({source})";
        return StoredKey.LabelIsSafe(stored.Label, value) ? $"present (stored key \"{stored.Label}\")" : "present (stored key)";
    }

    /// <summary>How many keys are stored for <paramref name="key"/> in the credential store (the self-test's count; decision D2: a count, never a value).</summary>
    public int StoredCount(string key) => _stored.Count(s => s.Key.Variable == key);

    /// <summary>
    /// Every key value this store holds, chosen or not: each variable's environment value, the .env values of those variables, every stored key.
    /// ConfigLoader registers them all with the log redactor, so a key that is not in use still never reaches the log.
    /// </summary>
    public IEnumerable<string> AllValues() =>
        KeyNames.Select(_environment).OfType<string>()
            .Concat(_values.Where(kv => KeyNames.Contains(kv.Key)).Select(kv => kv.Value.Value)) // keys only: a MODEL= line in .env is not a secret (lens minor 4)
            .Concat(_stored.Select(s => s.Value));

    private (string? Value, string? Source, StoredKey? Stored) Resolve(string key)
    {
        if (_active.TryGetValue(key, out var chosen))
        {
            var picked = chosen switch
            {
                StoredKeyId.Environment => FromEnvironment(key),
                StoredKeyId.File => FromFile(key),
                _ => FromStored(key, chosen),
            };
            if (picked.Value is not null) return picked; // a choice that names nothing (removed, emptied) falls back to the order below
        }
        if (FromEnvironment(key) is { Value: not null } env) return env;
        if (FromFile(key) is { Value: not null } file) return file;
        var mine = _stored.Where(s => s.Key.Variable == key).ToList();
        return mine.Count == 1 ? (mine[0].Value, null, mine[0].Key) : (null, null, null);
    }

    private (string? Value, string? Source, StoredKey? Stored) FromEnvironment(string key) =>
        _environment(key) is { Length: > 0 } env ? (env, "environment variable", null) : (null, null, null);

    private (string? Value, string? Source, StoredKey? Stored) FromFile(string key) =>
        _values.TryGetValue(key, out var entry) && entry.Value.Length > 0 ? (entry.Value, entry.Source, null) : (null, null, null);

    /// <summary>The stored key <paramref name="id"/>, only when it is a key for this same variable: another service's key is never sent here.</summary>
    private (string? Value, string? Source, StoredKey? Stored) FromStored(string key, string id) =>
        _stored.FirstOrDefault(s => s.Key.Id == id && s.Key.Variable == key) is { Key: not null } s ? (s.Value, null, s.Key) : (null, null, null);
}
