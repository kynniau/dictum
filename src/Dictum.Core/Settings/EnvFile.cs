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

    /// <summary>Every key Dictum reads: each is registered with the log redactor and listed (present or absent) by the self-test.</summary>
    public static IReadOnlyList<string> KeyNames { get; } = [AnthropicKey, OpenAiKey, GeminiKey, LlmKey];

    private readonly Dictionary<string, (string Value, string Source)> _values = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _environment;

    private SecretStore(Func<string, string?> environment) => _environment = environment;

    public static SecretStore Empty { get; } = new(_ => null);

    public static SecretStore FromContents(string? appDataEnv, string? exeDirEnv, Func<string, string?> environment)
    {
        var store = new SecretStore(environment);
        // Lowest precedence first; later sources overwrite.
        if (exeDirEnv is not null) store.Merge(EnvFile.Parse(exeDirEnv), "exe-folder .env");
        if (appDataEnv is not null) store.Merge(EnvFile.Parse(appDataEnv), "config-folder .env");
        return store;
    }

    public static SecretStore Load(AppPaths paths, Func<string, string?>? environment = null) =>
        FromContents(ReadOrNull(paths.EnvFile), ReadOrNull(paths.ExeEnvFile), environment ?? Environment.GetEnvironmentVariable);

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

    /// <summary>A blank KEY= line is "not set" (as an empty environment variable is), so it never hides a lower-precedence value.</summary>
    private void Merge(Dictionary<string, string> values, string source)
    {
        foreach (var (k, v) in values)
        {
            if (v.Length > 0) _values[k] = (v, source);
        }
    }

    public string? Get(string key)
    {
        var env = _environment(key);
        if (!string.IsNullOrEmpty(env)) return env;
        return _values.TryGetValue(key, out var entry) && entry.Value.Length > 0 ? entry.Value : null;
    }

    /// <summary>Where a key came from, for the self-test. Never returns the value.</summary>
    public string Describe(string key)
    {
        if (!string.IsNullOrEmpty(_environment(key))) return "present (environment variable)";
        return _values.TryGetValue(key, out var entry) && entry.Value.Length > 0 ? $"present ({entry.Source})" : "absent";
    }
}
