using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// "Create the key file…" on Settings > Clean-up (1.3): the config folder's .env, made with the chosen service's variable and the
/// placeholder, or an existing one opened untouched; then opened. Every test runs in a scratch profile, never the real %APPDATA%.
/// </summary>
public class KeyFileTests
{
    private static CloudPreset Preset(string name) => Assert.Single(CloudPresets.All, p => p.Name == name);

    [Theory]
    [InlineData(null, SecretStore.AnthropicKey)] // Anthropic (Claude), the list's first entry, is not a preset
    [InlineData("OpenAI", SecretStore.OpenAiKey)]
    [InlineData("Google Gemini", SecretStore.GeminiKey)]
    [InlineData("Custom server (OpenAI-compatible)", SecretStore.LlmKey)]
    [InlineData("Ollama on this PC", null)] // no key is ever sent there
    public void The_variable_is_the_one_the_card_names_for_each_service(string? preset, string? variable) =>
        Assert.Equal(variable, KeyFileButton.VariableFor(preset is null ? null : Preset(preset), preset is null ? null : Preset(preset).BaseUrl));

    [Fact]
    public void An_address_typed_over_a_preset_gets_the_custom_servers_variable_as_the_key_line_does() =>
        Assert.Equal(SecretStore.LlmKey, KeyFileButton.VariableFor(Preset("OpenAI"), "https://llm.example.net/v1"));

    [Theory]
    [InlineData(SecretStore.AnthropicKey)]
    [InlineData(SecretStore.OpenAiKey)]
    [InlineData(SecretStore.GeminiKey)]
    [InlineData(SecretStore.LlmKey)]
    public Task The_file_is_created_with_that_variable_and_the_placeholder_then_opened(string variable) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" } }""");
        var (shell, platform, _) = scratch.Shell();
        Assert.False(File.Exists(scratch.Paths.EnvFile));
        Assert.Equal(AppShell.KeyFileCreated(variable), shell.CreateKeyFile(variable));
        var text = File.ReadAllText(scratch.Paths.EnvFile);
        Assert.Equal(KeyFile.Header + "\n" + variable + "=your-key-here\n", text);
        Assert.StartsWith("# ", text); // .env.example's comment style
        Assert.Equal([variable], EnvFile.Parse(text).Keys);
        Assert.Equal(scratch.Paths.EnvFile, Assert.Single(platform.Opened)); // opened, at its real path (the fake package's is the same)
        Assert.Equal($"Your key file has been created and opened: put your key in place of your-key-here after {variable}=, then save it.", AppShell.KeyFileCreated(variable));
        shell.Dispose();
    });

    /// <summary>Mutation: KeyFile.CreateIfMissing without its exists check and with FileMode.Create makes this fail.</summary>
    [Fact]
    public Task An_existing_file_is_left_byte_for_byte_and_opened() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" } }""");
        byte[] before = [0xEF, 0xBB, 0xBF, .. "OPENAI_API_KEY=k-test-not-real\r\n# mine\r\n"u8.ToArray()];
        File.WriteAllBytes(scratch.Paths.EnvFile, before);
        var written = File.GetLastWriteTimeUtc(scratch.Paths.EnvFile);
        var (shell, platform, _) = scratch.Shell();
        var first = shell.CreateKeyFile(SecretStore.AnthropicKey);
        Assert.Equal(before, File.ReadAllBytes(scratch.Paths.EnvFile));
        Assert.Equal("Your key file already exists; it has been opened.", first);
        Assert.Equal(AppShell.KeyFileExists, shell.CreateKeyFile(SecretStore.AnthropicKey));
        Assert.Equal(before, File.ReadAllBytes(scratch.Paths.EnvFile));
        Assert.Equal(written, File.GetLastWriteTimeUtc(scratch.Paths.EnvFile));
        Assert.Equal([scratch.Paths.EnvFile, scratch.Paths.EnvFile], platform.Opened);
        shell.Dispose();
    });

    [Fact]
    public void Only_a_key_Dictum_reads_can_be_written()
    {
        var dir = Directory.CreateTempSubdirectory("dictum-keyfile-");
        try
        {
            var paths = new AppPaths(Path.Combine(dir.FullName, "config"), Path.Combine(dir.FullName, "local"), Path.Combine(dir.FullName, "exe"));
            Assert.Throws<ArgumentException>(() => KeyFile.CreateIfMissing(paths, "PATH"));
            Assert.False(File.Exists(paths.EnvFile));
            Assert.True(KeyFile.CreateIfMissing(paths, SecretStore.GeminiKey)); // the config folder is made when missing
            Assert.False(KeyFile.CreateIfMissing(paths, SecretStore.OpenAiKey));
            Assert.Equal(KeyFile.Contents(SecretStore.GeminiKey), File.ReadAllText(paths.EnvFile));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The placeholder is not a key: a service that needs one sends nothing while the file still says your-key-here, and the card
    /// waits for the key, as it does with no file. A key pasted over it is read.
    /// </summary>
    [Fact]
    public async Task The_placeholder_counts_as_not_set_so_nothing_is_sent_with_it()
    {
        var placeholder = SecretStore.FromContents(KeyFile.Contents(SecretStore.OpenAiKey), null, _ => null);
        Assert.Null(placeholder.Get(SecretStore.OpenAiKey));
        Assert.Equal("absent", placeholder.Describe(SecretStore.OpenAiKey));
        // It never hides a key beside the program either.
        Assert.Equal("k-exe", SecretStore.FromContents(KeyFile.Contents(SecretStore.OpenAiKey), "OPENAI_API_KEY=k-exe\n", _ => null).Get(SecretStore.OpenAiKey));

        var openAi = Preset("OpenAI");
        var llm = new LlmSettings { Provider = openAi.Provider, BaseUrl = openAi.BaseUrl, Model = openAi.DefaultModel! };
        var handler = new CountingHandler(HttpStatusCode.OK);
        var cleaner = TranscriptCleaner.Create(llm with { CloudConsent = CloudPolicy.CloudHost(llm) }, offlineOnly: false, placeholder, new HttpClient(handler), NullLog.Instance);
        Assert.Equal(SecretStore.OpenAiKey, cleaner.MissingKey);
        await cleaner.CleanAsync("um the account is overdrawn", TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Requests);

        var pasted = SecretStore.FromContents(KeyFile.Contents(SecretStore.OpenAiKey).Replace(SecretStore.Placeholder, "k-real-looking-test"), null, _ => null);
        Assert.Equal("k-real-looking-test", pasted.Get(SecretStore.OpenAiKey));
    }

    [Fact]
    public Task The_button_writes_the_chosen_services_file_and_says_so_and_says_the_no_key_line_for_a_service_without_one() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" } }""");
        var (shell, platform, _) = scratch.Shell();
        var said = new List<string>();
        string? variable = null;
        var button = KeyFileButton.Create(shell, () => variable, () => "Ollama on this PC needs no key.", said.Add);
        Assert.Equal("Create the key file…", button.Content);
        Assert.Equal(KeyFileButton.Label, button.Content);
        var window = new Window { Content = button };
        window.Show();

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["Ollama on this PC needs no key."], said);
        Assert.False(File.Exists(scratch.Paths.EnvFile)); // nothing written for a service that takes no key
        Assert.Empty(platform.Opened);

        variable = SecretStore.GeminiKey;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(AppShell.KeyFileCreated(SecretStore.GeminiKey), said[^1]);
        Assert.Equal(KeyFile.Contents(SecretStore.GeminiKey), File.ReadAllText(scratch.Paths.EnvFile));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(AppShell.KeyFileExists, said[^1]);
        Assert.Equal([scratch.Paths.EnvFile, scratch.Paths.EnvFile], platform.Opened);
        window.Close();
        shell.Dispose();
    });
}
