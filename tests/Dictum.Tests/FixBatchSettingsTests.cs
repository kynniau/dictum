using System.Net;
using Dictum.Cli;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>Answers every request with 403 and counts them: a test using it can never reach a real host.</summary>
internal sealed class CountingHandler(HttpStatusCode status = HttpStatusCode.Forbidden) : HttpMessageHandler
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        return Task.FromResult(new HttpResponseMessage(status));
    }
}

/// <summary>Fix batch, item 2 (settings fail-open) and item 10 (SetValue): one tested loader, failing closed.</summary>
public sealed class FixBatchSettingsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-cfg-");

    private AppPaths Paths()
    {
        var paths = new AppPaths(Path.Combine(_dir.FullName, "cfg"), Path.Combine(_dir.FullName, "local"), Path.Combine(_dir.FullName, "exe"));
        Directory.CreateDirectory(paths.ConfigDir);
        Directory.CreateDirectory(paths.ExeDir);
        return paths;
    }

    private static ConfigLoadResult Load(AppPaths paths, AppSettings? previous = null, SecretRedactor? redactor = null, Func<string, string?>? env = null) =>
        ConfigLoader.Load(paths, redactor ?? new SecretRedactor(), env ?? (_ => null), _ => false, _ => { }, previous: previous);

    private const string Broken = """{ "enabled": false, "hotkey": "F9", "offline_only": true, "whisper": { "model": "base.en" }, "llm": { "provider": "none" }""";

    [Fact]
    public void A_file_that_does_not_parse_is_not_usable()
    {
        Assert.False(SettingsLoader.Parse(Broken).Usable);
        Assert.False(SettingsLoader.Parse("[1, 2]").Usable);
        Assert.True(SettingsLoader.Parse("{}").Usable);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_usable()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, "{}");
        using var held = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var load = SettingsLoader.LoadFile(paths.SettingsFile);
        Assert.True(load.FileExisted);
        Assert.False(load.Usable);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("\"no\"")]
    public void Offline_only_that_is_present_but_not_true_or_false_counts_as_true(string value)
    {
        var load = SettingsLoader.Parse($$"""{ "offline_only": {{value}} }""");
        Assert.True(load.Settings.OfflineOnly);
        Assert.Contains(load.Warnings, w => w.Contains("offline_only", StringComparison.Ordinal) && w.Contains("treated as true", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_given_twice_is_reported()
    {
        var load = SettingsLoader.Parse("""{ "llm": { "provider": "none" }, "llm": { "provider": "local" } }""");
        Assert.Contains(load.Warnings, w => w.Contains("\"llm\" appears more than once", StringComparison.Ordinal));
        Assert.Equal("local", load.Settings.Llm.Provider); // the warning says the last one is used: pin that it is
    }

    [Theory]
    [InlineData("""{ "whisper": { "model": "base.en" }, "llm": { "model": "qwen2.5-0.5b" } }""", true, true)]
    [InlineData("""{ "offline_only": true }""", false, false)]
    [InlineData("""{ "whisper": { "model": "base.en" } }""", true, false)]
    [InlineData("""{ "llm": { "provider": "none" } }""", false, true)]
    [InlineData("""{ "llm": { "provider": "anthropic" } }""", false, true)]
    [InlineData("""{ "llm": { "provider": "local" } }""", false, false)]
    public void The_reader_says_whether_the_file_names_its_models(string json, bool speech, bool cleanup)
    {
        var load = SettingsLoader.Parse(json);
        Assert.Equal((speech, cleanup), (load.NamesSpeechModel, load.NamesCleanupModel));
    }

    [Fact]
    public void A_broken_file_at_startup_runs_with_defaults_and_offline_only_forced_on()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, Broken);
        var config = Load(paths);
        Assert.True(config.Settings.OfflineOnly);
        Assert.True(config.ForcedOffline);
        Assert.Equal("settings.json could not be read; running offline with defaults until it is fixed", config.Problem);
        Assert.Equal(new AppSettings().Hotkey, config.Settings.Hotkey); // defaults otherwise: nothing else is switched off
    }

    [Fact]
    public void A_broken_file_on_reload_keeps_the_settings_in_use()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, Broken);
        var running = new AppSettings { Enabled = false, Hotkey = "F9", OfflineOnly = true, Llm = new LlmSettings { Provider = "none" } };
        var config = Load(paths, previous: running);
        Assert.Same(running, config.Settings);
        Assert.True(config.KeptPrevious);
        Assert.Contains("keeping the previous settings", config.Problem);
    }

    [Fact]
    public void The_config_folder_env_file_wins_over_the_one_beside_the_exe()
    {
        var paths = Paths();
        File.WriteAllText(paths.EnvFile, "ANTHROPIC_API_KEY=from-config-123\n");
        File.WriteAllText(paths.ExeEnvFile, "ANTHROPIC_API_KEY=from-exe-123\nLLM_API_KEY=only-in-exe-123\n");
        var config = Load(paths);
        Assert.Equal("from-config-123", config.Secrets.Get(SecretStore.AnthropicKey));
        Assert.Equal("only-in-exe-123", config.Secrets.Get(SecretStore.LlmKey));
    }

    [Fact]
    public void A_blank_key_in_the_config_folder_env_does_not_hide_the_exe_folder_key()
    {
        var paths = Paths();
        File.WriteAllText(paths.EnvFile, "ANTHROPIC_API_KEY=\nLLM_API_KEY=\n");
        File.WriteAllText(paths.ExeEnvFile, "ANTHROPIC_API_KEY=sk-ant-from-exe-folder\n");
        Assert.Equal("sk-ant-from-exe-folder", Load(paths).Secrets.Get(SecretStore.AnthropicKey));
    }

    [Fact]
    public void Loaded_keys_are_registered_with_the_log_redactor()
    {
        var paths = Paths();
        File.WriteAllText(paths.EnvFile, "ANTHROPIC_API_KEY=sk-ant-redact-me-123\nLLM_API_KEY=llm-redact-me-456\n");
        var redactor = new SecretRedactor();
        Load(paths, redactor: redactor);
        Assert.Equal("a [redacted] b [redacted]", redactor.Redact("a sk-ant-redact-me-123 b llm-redact-me-456"));
    }

    [Fact]
    public void The_cleanup_repair_is_told_whether_the_key_is_present()
    {
        var paths = Paths();
        List<string> warnings = [];
        string Provider()
        {
            warnings.Clear();
            return ConfigLoader.Load(paths, new SecretRedactor(), _ => null, _ => false, warnings.Add).Settings.Llm.Provider;
        }
        // Never agreed to (a first-build settings.json): the key's absence is the repair's reason, logged as such.
        File.WriteAllText(paths.SettingsFile, """{ "llm": { "provider": "anthropic" } }""");
        Assert.Equal("local", Provider());
        Assert.Contains(warnings, w => w.Contains("ANTHROPIC_API_KEY is not set", StringComparison.Ordinal));
        File.WriteAllText(paths.EnvFile, "ANTHROPIC_API_KEY=sk-ant-present-123\n");
        Assert.Equal("local", Provider()); // the key is there, so only the missing consent is why
        Assert.DoesNotContain(warnings, w => w.Contains("ANTHROPIC_API_KEY is not set", StringComparison.Ordinal));
        // Agreed to in the app (SPEC §22): Anthropic with its key, and without it too (22/10/2026), when the clean-up in use sends nothing
        // and waits for the key (TranscriptCleaner.MissingKey), so the pages can say so.
        CloudConsentFile.Write(paths.ConsentFile, new CloudConsentRecord("anthropic", "api.anthropic.com", DateTimeOffset.UtcNow, "test"));
        Assert.Equal("anthropic", Provider());
        File.Delete(paths.EnvFile);
        Assert.Equal("anthropic", Provider());
    }

    [Fact]
    public async Task A_download_is_refused_while_settings_json_is_broken_and_makes_no_request()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, Broken);
        var server = new CountingHandler();
        var options = CliOptions.Parse(["--download-model", "tiny.en"]).Options!;
        Assert.Equal(1, await DownloadCommand.RunAsync(options, paths, NullLog.Instance, new HttpClient(server)));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public void Writing_a_read_only_settings_file_returns_false_and_leaves_no_temporary_file()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, "{}");
        File.SetAttributes(paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            Assert.False(SettingsFile.SetValue(paths.SettingsFile, "enabled", System.Text.Json.Nodes.JsonValue.Create(false)));
            Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
        }
        finally
        {
            File.SetAttributes(paths.SettingsFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Writing_a_settings_file_with_a_duplicate_key_returns_false_and_leaves_no_temporary_file()
    {
        var paths = Paths();
        const string text = """{ "insert": { "method": "paste" }, "insert": { "method": "type" } }""";
        File.WriteAllText(paths.SettingsFile, text);
        Assert.False(SettingsFile.SetValue(paths.SettingsFile, "insert.method", System.Text.Json.Nodes.JsonValue.Create("paste")));
        Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
        Assert.Equal(text, File.ReadAllText(paths.SettingsFile));
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
