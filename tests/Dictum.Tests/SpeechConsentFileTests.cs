using System.Text.Json.Nodes;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// consent.json with cloud speech (spec "Cloud speech" §3, 22/10/2026): a cleanup entry and a speech entry, so agreeing to send
/// text never agrees to send audio; the earlier single-record file is a clean-up consent and never a speech consent. And the
/// speech.* settings keys: loaded with validation, documented in the default settings.json, never able to grant consent.
/// </summary>
public sealed class SpeechConsentFileTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private string ConsentFile => _scratch.Paths.ConsentFile;

    private static CloudConsentRecord Record(string host) => new(SpeechSettings.OpenAiCompatible, host, DateTimeOffset.UtcNow, "test");

    private const string OldShape = """{ "provider": "openai-compatible", "host": "llm.example.com", "agreed_at": "2026-10-16T01:00:00+00:00", "app_version": "1.1.0" }""";

    /// <summary>Pin (mutation-checked): the single-record file written before cloud speech is a clean-up consent only.</summary>
    [Fact]
    public void The_old_single_record_file_is_a_clean_up_consent_only()
    {
        File.WriteAllText(ConsentFile, OldShape);
        var consents = CloudConsentFile.ReadAll(ConsentFile);
        Assert.Equal("llm.example.com", consents.Cleanup?.Host);
        Assert.Null(consents.Speech);
        Assert.Equal("llm.example.com", CloudConsentFile.Read(ConsentFile)?.Host);

        // Both services at the same host: the old record still never switches cloud speech on.
        var settings = new AppSettings
        {
            Llm = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1" },
            Speech = new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" },
        };
        var applied = CloudConsentFile.Apply(settings, consents.Cleanup, consents.Speech);
        Assert.Equal("llm.example.com", applied.Llm.CloudConsent);
        Assert.Null(applied.Speech.Consent);
    }

    /// <summary>Through the one loader: the old file gives clean-up consent and no speech consent; a speech entry gives speech consent.</summary>
    [Fact]
    public void The_loader_reads_each_consent_into_its_own_settings()
    {
        File.WriteAllText(_scratch.Paths.SettingsFile, """
            { "hotkey": "F24",
              "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" },
              "speech": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "s" } }
            """);
        File.WriteAllText(ConsentFile, OldShape);
        var old = ConfigLoader.Load(_scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }).Settings;
        Assert.Equal("llm.example.com", old.Llm.CloudConsent);
        Assert.Null(old.Speech.Consent);

        CloudConsentFile.WriteSpeech(ConsentFile, Record("llm.example.com"));
        var both = ConfigLoader.Load(_scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }).Settings;
        Assert.Equal("llm.example.com", both.Llm.CloudConsent);
        Assert.Equal("llm.example.com", both.Speech.Consent);
    }

    /// <summary>Writing one consent keeps the other; withdrawing speech keeps clean-up, and with nothing left the file goes.</summary>
    [Fact]
    public void Each_consent_is_written_and_withdrawn_on_its_own()
    {
        CloudConsentFile.Write(ConsentFile, Record("llm.example.com"));
        Assert.Null(CloudConsentFile.ReadAll(ConsentFile).Speech); // a clean-up agreement is not a speech one
        CloudConsentFile.WriteSpeech(ConsentFile, Record("api.groq.com"));
        Assert.Equal(("llm.example.com", "api.groq.com"), Hosts());

        CloudConsentFile.Write(ConsentFile, Record("llm2.example.com")); // the clean-up dialog again: speech consent untouched
        Assert.Equal(("llm2.example.com", "api.groq.com"), Hosts());

        var root = JsonNode.Parse(File.ReadAllText(ConsentFile))!.AsObject();
        Assert.Equal(["cleanup", "speech"], root.Select(p => p.Key));
        Assert.Equal("api.groq.com", root["speech"]!["host"]!.GetValue<string>()); // snake_case, as before

        CloudConsentFile.WithdrawSpeech(ConsentFile);
        Assert.Equal(("llm2.example.com", null), Hosts());
        CloudConsentFile.WriteSpeech(ConsentFile, Record("api.groq.com"));
        CloudConsentFile.Delete(ConsentFile); // Switch cloud clean-up off still withdraws everything (the UI lane narrows it)
        Assert.False(File.Exists(ConsentFile));

        CloudConsentFile.WriteSpeech(ConsentFile, Record("api.groq.com"));
        CloudConsentFile.WithdrawSpeech(ConsentFile);
        Assert.False(File.Exists(ConsentFile)); // nothing left on file
    }

    /// <summary>A damaged entry is no consent and does not take the other entry with it; a damaged file is no consent at all.</summary>
    [Fact]
    public void A_damaged_entry_or_file_is_no_consent()
    {
        File.WriteAllText(ConsentFile, """{ "cleanup": { "provider": "openai-compatible", "host": "llm.example.com", "agreed_at": "2026-10-22T00:00:00+00:00", "app_version": "t" }, "speech": { "host": "" } }""");
        Assert.Equal(("llm.example.com", null), Hosts());
        File.WriteAllText(ConsentFile, """{ "cleanup": 7, "speech": { "provider": "openai-compatible", "host": "api.groq.com", "agreed_at": "not a date", "app_version": "t" } }""");
        Assert.Equal((null, null), Hosts());
        File.WriteAllText(ConsentFile, "{ not json");
        Assert.Equal((null, null), Hosts());
        File.WriteAllText(ConsentFile, "[]");
        Assert.Equal((null, null), Hosts());
    }

    /// <summary>A speech consent given for another provider value is not applied (as clean-up's rule).</summary>
    [Fact]
    public void A_speech_consent_applies_only_to_its_provider()
    {
        var local = CloudConsentFile.Apply(new AppSettings(), null, Record("api.groq.com"));
        Assert.Null(local.Speech.Consent); // speech.provider = local
        var cloud = CloudConsentFile.Apply(new AppSettings { Speech = new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible } }, null, Record("api.groq.com"));
        Assert.Equal("api.groq.com", cloud.Speech.Consent);
        Assert.Null(cloud.Llm.CloudConsent);
    }

    private (string? Cleanup, string? Speech) Hosts()
    {
        var consents = CloudConsentFile.ReadAll(ConsentFile);
        return (consents.Cleanup?.Host, consents.Speech?.Host);
    }

    /// <summary>speech.* loads with validation: defaults, every key, bad values warn and use the default, and no key can grant consent.</summary>
    [Fact]
    public void The_speech_keys_load_with_validation()
    {
        var defaults = SettingsLoader.Parse("{}");
        Assert.Equal(SpeechSettings.Local, defaults.Settings.Speech.Provider);
        Assert.Null(defaults.Settings.Speech.BaseUrl);
        Assert.Null(defaults.Settings.Speech.Model);
        Assert.Equal(SpeechSettings.DefaultTimeoutMs, defaults.Settings.Speech.TimeoutMs);

        var set = SettingsLoader.Parse("""{ "speech": { "provider": "OpenAI-Compatible", "base_url": "https://my-resource.openai.azure.com/openai/v1/", "model": "my-deployment", "timeout_ms": 20000 } }""");
        Assert.Empty(set.Warnings);
        Assert.Equal(new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = "https://my-resource.openai.azure.com/openai/v1/", Model = "my-deployment", TimeoutMs = 20000 },
            set.Settings.Speech);

        var bad = SettingsLoader.Parse("""{ "speech": { "provider": "deepgram", "base_url": "not a url", "timeout_ms": 5, "consent": "api.openai.com" } }""");
        Assert.Equal(4, bad.Warnings.Count);
        Assert.Contains(bad.Warnings, w => w.StartsWith("Setting \"speech.provider\" has invalid value", StringComparison.Ordinal));
        Assert.Contains(bad.Warnings, w => w.StartsWith("Setting \"speech.base_url\" has invalid value", StringComparison.Ordinal));
        Assert.Contains(bad.Warnings, w => w.StartsWith("Setting \"speech.timeout_ms\" has invalid value", StringComparison.Ordinal));
        Assert.Contains("Unknown setting \"speech.consent\" ignored.", bad.Warnings);
        Assert.Equal(new SpeechSettings(), bad.Settings.Speech); // settings.json can never fill Consent
    }

    /// <summary>The default settings.json documents speech.* (README-style help) and still reads back without a warning.</summary>
    [Fact]
    public void The_default_file_documents_the_speech_keys()
    {
        var json = SettingsFile.DefaultJson("base.en");
        var doc = JsonNode.Parse(json)!["_doc"]!["speech"]!.GetValue<string>();
        foreach (var part in new[] { "provider: local", "openai-compatible", "/audio/transcriptions", "deployment name", "timeout_ms", "consent step", "offline_only refuses it", "whisper.translate" })
            Assert.Contains(part, doc);
        Assert.Empty(SettingsLoader.Parse(json).Warnings);
    }
}
