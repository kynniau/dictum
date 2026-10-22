using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dictum.Settings;

/// <summary>One agreement to send dictations to a cloud service, as a consent dialog recorded it.</summary>
/// <param name="Provider">The provider agreed to (`anthropic` or `openai-compatible`).</param>
/// <param name="Host">The host dictations go to; consent covers this host only.</param>
public sealed record CloudConsentRecord(string Provider, string Host, DateTimeOffset AgreedAt, string AppVersion);

/// <summary>What consent.json holds: the clean-up agreement (text) and the speech agreement (audio), each or neither.</summary>
public sealed record CloudConsents(CloudConsentRecord? Cleanup, CloudConsentRecord? Speech)
{
    public static readonly CloudConsents None = new(null, null);
}

/// <summary>
/// SPEC §22 (amended 16/10/2026): consent to cloud clean-up lives in consent.json in the settings folder, written only by
/// the in-app consent dialog (provider, host, timestamp, app version). settings.json cannot grant it: its
/// `llm.cloud_consent` key, which an earlier build of this lane wrote, is ignored with a warning.
/// Amended 22/10/2026 (cloud speech, Core): the file is an object with a `cleanup` entry and a `speech` entry, each one
/// record, so agreeing to send text never agrees to send audio. A file in the earlier shape (one record at the top level)
/// is a clean-up consent and never a speech consent. Every write uses the new shape; an earlier Dictum reading a new-shape
/// file finds no top-level record and so no consent (it fails closed).
/// </summary>
public static class CloudConsentFile
{
    private const string CleanupEntry = "cleanup";
    private const string SpeechEntry = "speech";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The clean-up consent on record, or null when there is none or the file cannot be read (no consent: fail closed).</summary>
    public static CloudConsentRecord? Read(string path) => ReadAll(path).Cleanup;

    /// <summary>Both consents on record. A file that cannot be read, or an entry that is not a whole record, is no consent.</summary>
    public static CloudConsents ReadAll(string path)
    {
        try
        {
            if (!File.Exists(path)) return CloudConsents.None;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return CloudConsents.None;
            // The earlier shape: one record at the top level, written before cloud speech existed, so it is a clean-up consent.
            if (root.ContainsKey("provider") || root.ContainsKey("host")) return new CloudConsents(Valid(root.Deserialize<CloudConsentRecord>(Options)), null);
            return new CloudConsents(Entry(root, CleanupEntry), Entry(root, SpeechEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return CloudConsents.None;
        }
    }

    private static CloudConsentRecord? Entry(JsonObject root, string name)
    {
        try
        {
            return root[name] is JsonObject entry ? Valid(entry.Deserialize<CloudConsentRecord>(Options)) : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null; // one damaged entry does not take the other with it
        }
    }

    private static CloudConsentRecord? Valid(CloudConsentRecord? record) => record is { Provider.Length: > 0, Host.Length: > 0 } ? record : null;

    /// <summary>
    /// Records the clean-up dialog's "Allow" (written beside, then moved over, so a half-written file is never read); a speech
    /// consent already on file is kept. When it cannot be written (a read-only consent.json) it throws, and no consent.json.tmp
    /// is left behind.
    /// </summary>
    public static void Write(string path, CloudConsentRecord record) => Save(path, ReadAll(path) with { Cleanup = record });

    /// <summary>Records the speech consent step's agreement; a clean-up consent already on file is kept. Throws as <see cref="Write"/> does.</summary>
    public static void WriteSpeech(string path, CloudConsentRecord record) => Save(path, ReadAll(path) with { Speech = record });

    /// <summary>
    /// Withdraws the speech consent only, keeping a clean-up consent on file; with nothing left the file is deleted
    /// (<see cref="Delete"/>). Throws as <see cref="Write"/> and <see cref="Delete"/> do, and the caller tries again later.
    /// </summary>
    public static void WithdrawSpeech(string path)
    {
        var consents = ReadAll(path);
        if (consents.Cleanup is null) Delete(path);
        else if (consents.Speech is not null) Save(path, consents with { Speech = null });
    }

    private static void Save(string path, CloudConsents consents)
    {
        var root = new JsonObject();
        if (consents.Cleanup is { } cleanup) root[CleanupEntry] = JsonSerializer.SerializeToNode(cleanup, Options);
        if (consents.Speech is { } speech) root[SpeechEntry] = JsonSerializer.SerializeToNode(speech, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SettingsFile.WriteAtomic(path, root.ToJsonString(Options));
    }

    /// <summary>
    /// Withdraws every consent on file (Settings > Clean-up > Switch cloud clean-up off). consent.json is Dictum's own record of
    /// the user's agreement, so a read-only attribute on it is cleared to honour the withdrawal. That is the only attribute
    /// written, and only when it is set: a file whose access list allows delete but not write-attributes is still deleted. A
    /// file another program holds open still throws, and the caller tries again later.
    /// </summary>
    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    /// <summary>
    /// The settings with the consents on record applied: the clean-up host only when it was given for llm.provider, the speech
    /// host only when it was given for speech.provider. A clean-up consent never fills the speech one.
    /// </summary>
    public static AppSettings Apply(AppSettings settings, CloudConsentRecord? cleanup, CloudConsentRecord? speech = null) =>
        settings with
        {
            Llm = settings.Llm with { CloudConsent = For(cleanup, settings.Llm.Provider) },
            Speech = settings.Speech with { Consent = For(speech, settings.Speech.Provider) },
        };

    private static string? For(CloudConsentRecord? consent, string provider) =>
        consent is not null && string.Equals(consent.Provider, provider, StringComparison.OrdinalIgnoreCase) ? consent.Host : null;
}
