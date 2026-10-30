using System.Net;
using System.Text;
using Dictum.Cli;
using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// API keys, Core (30/10/2026, brief §3 "Resolution order"; SPEC §5 amended): which key SecretStore hands out, what the self-test
/// says about it, that every value is redacted, and that a stored key still goes only to its own service.
/// </summary>
public class KeyResolutionTests
{
    private const string Openai = SecretStore.OpenAiKey;
    private const string EnvValue = "k-ev-9f3a1c-1111";
    private const string ConfigValue = "k-cf-7d2e4b-2222";
    private const string ExeValue = "k-ex-5a8c1f-3333";
    private const string WorkValue = "k-wk-3e9b7d-4444";
    private const string HomeValue = "k-hm-1c6f2a-5555";
    private const string WorkId = "aaaaaaaaaaaa";
    private const string HomeId = "bbbbbbbbbbbb";

    private static (StoredKey Key, string Value) Stored(string variable, string id, string label, string value) => (new StoredKey(id, variable, label, null), value);

    private static SecretStore Store(bool env = true, bool config = true, bool exe = true, IReadOnlyDictionary<string, string>? active = null,
        params (StoredKey Key, string Value)[] stored) =>
        SecretStore.FromContents(config ? $"{Openai}={ConfigValue}\n" : null, exe ? $"{Openai}={ExeValue}\n" : null,
            name => env && name == Openai ? EnvValue : null, active, stored);

    private static readonly (StoredKey, string)[] Both = [Stored(Openai, WorkId, "Work", WorkValue), Stored(Openai, HomeId, "Home", HomeValue)];

    private static Dictionary<string, string> Choose(string id) => new() { [Openai] = id };

    [Fact]
    public void The_chosen_stored_key_wins_over_the_environment_and_both_env_files()
    {
        var secrets = Store(active: Choose(HomeId), stored: Both);
        Assert.Equal(HomeValue, secrets.Get(Openai));
        Assert.Equal("present (stored key \"Home\")", secrets.Describe(Openai));
    }

    [Fact]
    public void Choosing_the_key_file_or_the_environment_variable_picks_that_source()
    {
        Assert.Equal(ConfigValue, Store(active: Choose(StoredKeyId.File), stored: Both).Get(Openai)); // over the environment variable
        Assert.Equal("present (config-folder .env)", Store(active: Choose(StoredKeyId.File), stored: Both).Describe(Openai));
        Assert.Equal(ExeValue, Store(config: false, active: Choose(StoredKeyId.File)).Get(Openai));
        Assert.Equal(EnvValue, Store(active: Choose(StoredKeyId.Environment), stored: Both).Get(Openai));
    }

    [Fact]
    public void With_no_choice_the_order_is_environment_then_config_env_then_exe_env_then_the_only_stored_key()
    {
        (StoredKey, string)[] one = [Stored(Openai, WorkId, "Work", WorkValue)];
        Assert.Equal(EnvValue, Store(stored: one).Get(Openai));
        Assert.Equal(ConfigValue, Store(env: false, stored: one).Get(Openai));
        Assert.Equal(ExeValue, Store(env: false, config: false, stored: one).Get(Openai));
        Assert.Equal(WorkValue, Store(env: false, config: false, exe: false, stored: one).Get(Openai));
        Assert.Equal("present (stored key \"Work\")", Store(env: false, config: false, exe: false, stored: one).Describe(Openai));
        Assert.Null(Store(env: false, config: false, exe: false).Get(Openai));
    }

    [Fact]
    public void Two_stored_keys_and_no_choice_use_neither()
    {
        var secrets = Store(env: false, config: false, exe: false, stored: Both);
        Assert.Null(secrets.Get(Openai));
        Assert.Equal("absent (none chosen)", secrets.Describe(Openai));
        Assert.Equal(EnvValue, Store(stored: Both).Get(Openai)); // today's sources still work beside them
    }

    [Fact]
    public void A_choice_that_names_nothing_falls_back_to_the_order()
    {
        Assert.Equal(EnvValue, Store(active: Choose("cccccccccccc"), stored: Both).Get(Openai)); // the chosen key was removed
        Assert.Equal(ConfigValue, Store(env: false, active: Choose(StoredKeyId.Environment)).Get(Openai)); // the variable was unset
        Assert.Equal(WorkValue, Store(env: false, config: false, exe: false, active: Choose(StoredKeyId.File), stored: [Stored(Openai, WorkId, "Work", WorkValue)]).Get(Openai));
    }

    [Fact]
    public void A_choice_never_hands_out_another_services_key()
    {
        var secrets = SecretStore.FromContents(null, null, _ => null, new Dictionary<string, string> { [SecretStore.GeminiKey] = WorkId },
            [Stored(Openai, WorkId, "Work", WorkValue)]);
        Assert.Null(secrets.Get(SecretStore.GeminiKey));
        Assert.Equal("absent", secrets.Describe(SecretStore.GeminiKey));
        Assert.Equal(WorkValue, secrets.Get(Openai));
    }

    [Fact]
    public void A_blank_or_placeholder_key_is_still_not_set_wherever_it_is()
    {
        var secrets = SecretStore.FromContents($"{Openai}={SecretStore.Placeholder}\n{SecretStore.GeminiKey}=\n", $"{Openai}=\n", name => name == SecretStore.GroqKey ? "" : null,
            new Dictionary<string, string> { [Openai] = StoredKeyId.File, [SecretStore.GroqKey] = StoredKeyId.Environment },
            [Stored(SecretStore.AnthropicKey, WorkId, "Placeholder", SecretStore.Placeholder), Stored(SecretStore.AnthropicKey, HomeId, "Blank", "")]);
        foreach (var name in new[] { Openai, SecretStore.GeminiKey, SecretStore.GroqKey, SecretStore.AnthropicKey })
        {
            Assert.Null(secrets.Get(name));
            Assert.Equal("absent", secrets.Describe(name));
        }
        Assert.Equal(0, secrets.StoredCount(SecretStore.AnthropicKey)); // a placeholder in the store is not a stored key either
    }

    [Fact]
    public void Describe_never_shows_a_key_or_any_part_of_one_even_through_its_label()
    {
        string[] values = [EnvValue, ConfigValue, ExeValue, WorkValue, HomeValue];
        var stores = new[]
        {
            Store(active: Choose(HomeId), stored: Both), Store(stored: Both), Store(env: false, stored: Both), Store(env: false, config: false, stored: Both),
            Store(env: false, config: false, exe: false, stored: Both), Store(env: false, config: false, exe: false, stored: [Both[0]]),
            // labels that hold the key, or a piece of it, as a key pasted into the Label field or keys.json edited by hand would
            Store(env: false, config: false, exe: false, stored: [Stored(Openai, WorkId, WorkValue, WorkValue)]),
            Store(env: false, config: false, exe: false, stored: [Stored(Openai, WorkId, "mine: " + WorkValue[..10], WorkValue)]),
        };
        foreach (var describe in stores.Select(s => s.Describe(Openai)))
        {
            foreach (var value in values)
            {
                for (var i = 0; i + 8 <= value.Length; i++) Assert.DoesNotContain(value.Substring(i, 8), describe, StringComparison.Ordinal);
            }
        }
        Assert.Equal("present (stored key)", stores[^1].Describe(Openai));
    }

    [Fact]
    public void Every_stored_key_is_registered_with_the_log_redactor_at_load_the_unchosen_ones_too()
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var store = new FakeCredentialStore();
        var vault = new SecretVault(scratch.Paths, store);
        var work = vault.Add(Openai, "Work", WorkValue);
        vault.Add(Openai, "Home", HomeValue);
        vault.Add(SecretStore.GroqKey, "Groq", "k-stored-groq-6666");
        SecretVault.SetActive(scratch.Paths, Openai, work, out _);
        File.WriteAllText(scratch.Paths.EnvFile, $"{Openai}={ConfigValue}\n"); // shadowed by the chosen key: registered all the same
        var redactor = new SecretRedactor();
        var config = ConfigLoader.Load(scratch.Paths, redactor, name => name == Openai ? EnvValue : null, _ => false, _ => { }, vault: vault);
        Assert.Equal(WorkValue, config.Secrets.Get(Openai));
        foreach (var value in new[] { WorkValue, HomeValue, "k-stored-groq-6666", ConfigValue, EnvValue })
            Assert.Equal("a [redacted] b", redactor.Redact($"a {value} b"));
    }

    [Fact]
    public void The_loader_reads_the_choice_from_settings_json_and_keeps_the_one_in_use_while_the_file_is_broken()
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var vault = new SecretVault(scratch.Paths, new FakeCredentialStore());
        var work = vault.Add(Openai, "Work", WorkValue);
        var home = vault.Add(Openai, "Home", HomeValue);
        Assert.Null(ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }, vault: vault).Secrets.Get(Openai)); // two, none chosen
        SecretVault.SetActive(scratch.Paths, Openai, home, out _);
        var loaded = ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }, vault: vault);
        Assert.Equal(HomeValue, loaded.Secrets.Get(Openai));
        File.WriteAllText(scratch.Paths.SettingsFile, """{ "keys": { "active": { "OPENAI_API_KEY": """ + $"\"{work}\"" + " } }, oops");
        var broken = ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }, previous: loaded.Settings, vault: vault);
        Assert.True(broken.KeptPrevious);
        Assert.Equal(HomeValue, broken.Secrets.Get(Openai));
    }

    [Fact]
    public void A_store_that_refuses_at_load_leaves_todays_keys_working_and_says_why_without_a_value()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.EnvFile, $"{Openai}={ConfigValue}\n");
        var warnings = new List<string>();
        var config = ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, warnings.Add, vault: new SecretVault(scratch.Paths, new ThrowingStore()));
        Assert.Equal(ConfigValue, config.Secrets.Get(Openai));
        Assert.Contains(warnings, w => w.StartsWith("Stored keys could not be read from Throwing store", StringComparison.Ordinal));
    }

    [Fact]
    public void The_self_test_adds_the_stored_key_count_and_names_the_store()
    {
        var secrets = Store(env: false, config: false, exe: false, stored: [.. Both, Stored(SecretStore.GroqKey, "cccccccccccc", "Groq", "k-stored-groq-6666")]);
        var lines = new List<(string Label, string Value)>();
        SelfTestCommand.KeyLines(secrets, (label, value, _) => lines.Add((label, value)));
        Assert.Contains((Openai, "absent (none chosen); 2 stored keys"), lines);
        Assert.Contains((SecretStore.GroqKey, "present (stored key \"Groq\"); 1 stored key"), lines);
        Assert.Contains((SecretStore.AnthropicKey, "absent"), lines);
        using var scratch = new Scratch();
        var store = new List<string>();
        SelfTestCommand.KeyStoreLine(new SecretVault(scratch.Paths, new FakeCredentialStore()), (label, value, _) => store.Add($"{label}: {value}"));
        SelfTestCommand.KeyStoreLine(SecretVault.Unavailable(scratch.Paths), (label, value, _) => store.Add($"{label}: {value}"));
        Assert.Equal(["key store: Test store", "key store: none on this platform"], store);
    }

    [Theory]
    [InlineData(LlmSettings.Anthropic, null)]
    [InlineData(LlmSettings.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai/")]
    public async Task A_stored_OpenAI_key_never_goes_to_Anthropic_or_Gemini_even_when_chosen_for_them(string provider, string? baseUrl)
    {
        // Brief F (30/10/2026): a key still goes only to its own host. The stored OpenAI key is the only key there is, and keys.active
        // names it for Anthropic's and Gemini's variables too: neither service may get it.
        var active = new Dictionary<string, string> { [Openai] = WorkId, [SecretStore.AnthropicKey] = WorkId, [SecretStore.GeminiKey] = WorkId };
        var secrets = SecretStore.FromContents(null, null, _ => null, active, [Stored(Openai, WorkId, "Work", WorkValue)]);
        var settings = new LlmSettings { Provider = provider, BaseUrl = baseUrl, Model = "m" };
        var handler = new Recorder();
        var cleaner = TranscriptCleaner.Create(settings with { CloudConsent = CloudPolicy.CloudHost(settings) }, offlineOnly: false, secrets, new HttpClient(handler), NullLog.Instance);
        await cleaner.CleanAsync("um the client's account is overdrawn", TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Count);
        Assert.NotNull(cleaner.MissingKey);
    }

    [Theory]
    [InlineData("https://my-resource.openai.azure.com/openai/v1/", SecretStore.AzureOpenAiKey)]
    [InlineData("https://api.groq.com/openai/v1", SecretStore.GroqKey)]
    public async Task A_stored_OpenAI_key_never_goes_to_Azure_or_Groq_speech_even_when_chosen_for_them(string baseUrl, string variable)
    {
        // Lens note 6 (30/10/2026): the same isolation for the cloud speech hosts.
        var active = new Dictionary<string, string> { [Openai] = WorkId, [SecretStore.AzureOpenAiKey] = WorkId, [SecretStore.GroqKey] = WorkId };
        var secrets = SecretStore.FromContents(null, null, _ => null, active, [Stored(Openai, WorkId, "Work", WorkValue)]);
        var settings = new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = baseUrl, Model = "speech-model", Consent = new Uri(baseUrl).Host };
        var handler = new Recorder();
        var local = new FakeTranscriber("heard here");
        var speech = Dictum.Transcribe.CloudSpeech.Create(settings, new Dictum.Transcribe.DecodeOptions(null, 0, "en", false), offlineOnly: false, secrets,
            new HttpClient(handler), NullLog.Instance, _ => Task.FromResult<Dictum.Transcribe.ITranscriber>(local));
        var engine = await speech.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal("heard here", await engine.TranscribeAsync(new float[16000], TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Count);
        Assert.False(speech.SendsAudio);
        Assert.Equal(variable, speech.MissingKey);
    }

    [Fact]
    public void Only_key_variables_from_a_env_file_are_registered_with_the_redactor()
    {
        // Lens minor 4 (30/10/2026): every .env line's value was registered, so MODEL=whisper-large was redacted from the log.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.EnvFile, $"MODEL=whisper-large\n{Openai}={ConfigValue}\n");
        var redactor = new SecretRedactor();
        ConfigLoader.Load(scratch.Paths, redactor, _ => null, _ => false, _ => { });
        Assert.Equal("using whisper-large", redactor.Redact("using whisper-large"));
        Assert.Equal("a [redacted] b", redactor.Redact($"a {ConfigValue} b"));
    }

    [Fact]
    public async Task A_stored_OpenAI_key_goes_to_OpenAI()
    {
        var secrets = SecretStore.FromContents(null, null, _ => null, null, [Stored(Openai, WorkId, "Work", WorkValue)]);
        var settings = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://api.openai.com/v1", Model = "m" };
        var handler = new Recorder();
        var cleaner = TranscriptCleaner.Create(settings with { CloudConsent = CloudPolicy.CloudHost(settings) }, offlineOnly: false, secrets, new HttpClient(handler), NullLog.Instance);
        await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count);
        Assert.Equal("Bearer " + WorkValue, handler.Authorization);
        Assert.Equal("api.openai.com", handler.Uri?.Host);
    }

    private sealed class ThrowingStore : Dictum.Platform.ICredentialStore
    {
        public bool Available => true;
        public string? Name => "Throwing store";
        public void Write(string variable, string id, string value) => throw new Dictum.Platform.CredentialStoreException("CredWrite failed (5).");
        public string? Read(string variable, string id) => throw new Dictum.Platform.CredentialStoreException("CredRead failed (5).");
        public bool Delete(string variable, string id) => false;
        public IReadOnlyList<(string Variable, string Id)> List() => throw new Dictum.Platform.CredentialStoreException("CredEnumerate failed (5).");
    }

    /// <summary>Answers every request with one chat completion and remembers where it went and with which key.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Hello there."},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
