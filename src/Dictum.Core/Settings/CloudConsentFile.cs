using System.Text.Json;

namespace Dictum.Settings;

/// <summary>One agreement to send dictations to a cloud clean-up service, as the consent dialog recorded it.</summary>
/// <param name="Provider">The provider agreed to (`anthropic` or `openai-compatible`).</param>
/// <param name="Host">The host dictations go to; consent covers this host only.</param>
public sealed record CloudConsentRecord(string Provider, string Host, DateTimeOffset AgreedAt, string AppVersion);

/// <summary>
/// SPEC §22 (amended 16/10/2026): consent to cloud clean-up lives in consent.json in the settings folder, written only by
/// the in-app consent dialog (provider, host, timestamp, app version). settings.json cannot grant it: its
/// `llm.cloud_consent` key, which an earlier build of this lane wrote, is ignored with a warning.
/// </summary>
public static class CloudConsentFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The consent on record, or null when there is none or the file cannot be read (no consent: fail closed).</summary>
    public static CloudConsentRecord? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<CloudConsentRecord>(File.ReadAllText(path), Options);
            return record is { Provider.Length: > 0, Host.Length: > 0 } ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the dialog's "Allow" (written beside, then moved over, so a half-written file is never read). When it cannot be
    /// written (a read-only consent.json) it throws, and no consent.json.tmp is left behind.
    /// </summary>
    public static void Write(string path, CloudConsentRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SettingsFile.WriteAtomic(path, JsonSerializer.Serialize(record, Options));
    }

    /// <summary>
    /// Withdraws consent (Settings > Clean-up > Switch cloud clean-up off). consent.json is Dictum's own record of the user's
    /// agreement, so a read-only attribute on it is cleared to honour the withdrawal; a file another program holds open still
    /// throws, and the caller tries again later.
    /// </summary>
    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <summary>The settings with the consent on record applied: the host, only when it was given for this provider.</summary>
    public static AppSettings Apply(AppSettings settings, CloudConsentRecord? consent) =>
        settings with
        {
            Llm = settings.Llm with
            {
                CloudConsent = consent is not null && string.Equals(consent.Provider, settings.Llm.Provider, StringComparison.OrdinalIgnoreCase) ? consent.Host : null,
            },
        };
}
