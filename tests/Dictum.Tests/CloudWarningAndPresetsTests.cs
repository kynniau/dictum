using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// Cloud clean-up stays an advanced option (decided 16/10/2026): the approved warning copy appears word for word, and the
/// Clean-up presets only fill in fields. No preset switches cloud clean-up on, skips the consent step or gets past offline_only.
/// </summary>
public class CloudWarningAndPresetsTests
{
    // Approved by the product owner on 16/10/2026 as written; pinned here word for word, independently of the app's copy.
    private static string ApprovedWarning(string host) =>
        $"WARNING: this is an advanced option and it is less private than local clean-up. Your text leaves this PC to be processed on {host}'s servers. Dictum encrypts the connection (HTTPS) but cannot see, validate or certify how that service stores, protects or uses your text, or who else can read it. Do not dictate confidential, personal or client information with cloud clean-up on unless you have checked that service's terms yourself.";

    private const string ApprovedStateLine = "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it.";

    private const string ApprovedPolicySentence = "This is an advanced option: Dictum does not validate or certify how that service stores, protects or uses the text it receives. Check its terms before dictating anything sensitive.";

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static T Named<T>(ILogical root, string name) where T : Control => All<T>(root).Single(c => AutomationProperties.GetName(c) == name);

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>What the Clean-up page sends to the consent step when the fields are left as the preset filled them.</summary>
    private static LlmSettings Proposed(CloudPreset preset) => new() { Provider = preset.Provider, BaseUrl = preset.BaseUrl, Model = preset.DefaultModel ?? "m" };

    private static IEnumerable<CloudPreset> OffThePc => CloudPresets.All.Where(p => p.BaseUrl is not null && CloudPolicy.CloudHost(Proposed(p)) is not null);

    [Fact]
    public void The_consent_text_carries_the_approved_warning_for_Anthropic_and_for_an_OpenAI_compatible_host()
    {
        var cases = new List<LlmSettings> { new() { Provider = LlmSettings.Anthropic, Model = LlmSettings.AnthropicDefaultModel } };
        cases.Add(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" });
        cases.AddRange(OffThePc.Select(Proposed));
        Assert.True(cases.Count >= 3);
        foreach (var llm in cases)
        {
            var host = CloudPolicy.CloudHost(llm)!;
            var text = CloudPolicy.ConsentText(llm, host);
            // Its own paragraph, straight before "Nothing is sent until you agree."
            Assert.Contains("\n\n" + ApprovedWarning(host) + "\n\nNothing is sent until you agree.", text);
        }
    }

    [Fact]
    public Task The_clean_up_page_adds_the_approved_sentence_while_cloud_clean_up_is_on() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" } }""");
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        var state = All<TextBlock>(window).Single(t => t.IsEffectivelyVisible && t.Text?.StartsWith("On: ", StringComparison.Ordinal) == true).Text!;
        Assert.EndsWith("You agreed to this host in the consent step. " + ApprovedStateLine, state);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Choosing_a_preset_only_fills_the_fields_and_switching_on_still_asks_with_the_warning() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        var service = Named<ComboBox>(window, "Service");
        var address = Named<TextBox>(window, "Server address");
        var model = Named<TextBox>(window, "Model");
        var before = File.ReadAllText(scratch.Paths.SettingsFile);

        for (var i = 0; i < CloudPresets.All.Count; i++)
        {
            var preset = CloudPresets.All[i];
            service.SelectedIndex = i + 1; // what a click or the arrow keys in the list do
            Assert.Equal(preset.Name, service.SelectedItem as string);
            Assert.Equal(preset.BaseUrl ?? "", address.Text ?? "");
            Assert.Equal(preset.DefaultModel ?? preset.ModelHint, model.Watermark);
        }
        Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile)); // choosing writes nothing
        Assert.False(File.Exists(scratch.Paths.ConsentFile));
        Assert.Empty(dialogs.Asked);

        foreach (var preset in OffThePc)
        {
            service.SelectedIndex = CloudPresets.All.ToList().IndexOf(preset) + 1;
            var asked = dialogs.Asked.Count;
            dialogs.Answers.Enqueue(1); // Cancel
            Click(window, "Use cloud clean-up…");
            Assert.Equal(asked + 1, dialogs.Asked.Count); // the consent step came up
            var (title, text) = dialogs.Asked[^1];
            Assert.Equal("Send dictations to a cloud service?", title);
            Assert.Contains(ApprovedWarning(new Uri(preset.BaseUrl!).Host), text);
            Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile));
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
        }
        Assert.Equal(OffThePc.Count(), dialogs.Asked.Count);

        var first = OffThePc.First();
        service.SelectedIndex = CloudPresets.All.ToList().IndexOf(first) + 1;
        dialogs.Answers.Enqueue(0); // Allow
        Click(window, "Use cloud clean-up…");
        var consent = CloudConsentFile.Read(scratch.Paths.ConsentFile)!; // recorded by the consent step only
        Assert.Equal((LlmSettings.OpenAiCompatible, new Uri(first.BaseUrl!).Host), (consent.Provider, consent.Host));
        var saved = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm;
        Assert.Equal((LlmSettings.OpenAiCompatible, first.BaseUrl, first.DefaultModel), (saved.Provider, saved.BaseUrl, saved.Model));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public void No_preset_carries_consent_for_a_host_off_this_PC()
    {
        Assert.NotEmpty(OffThePc);
        foreach (var preset in OffThePc)
        {
            var proposed = Proposed(preset);
            var host = CloudPolicy.CloudHost(proposed)!;
            Assert.False(CloudPolicy.HasConsent(proposed));
            Assert.False(TranscriptCleaner.Create(proposed, false, SecretStore.Empty, new HttpClient(new CountingHandler()), NullLog.Instance).UsesModel);

            var agreed = CloudConsentFile.Apply(new AppSettings { Llm = proposed }, new CloudConsentRecord(preset.Provider, host, DateTimeOffset.UtcNow, "test")).Llm;
            Assert.True(CloudPolicy.HasConsent(agreed)); // only once the consent step has recorded this host
        }
    }

    [Fact]
    public void Ollama_stays_on_this_PC_and_every_other_preset_address_is_https_off_it()
    {
        var ollama = Assert.Single(CloudPresets.All, p => p.Name == "Ollama on this PC");
        Assert.True(new Uri(ollama.BaseUrl!).IsLoopback);
        Assert.Null(CloudPolicy.CloudHost(Proposed(ollama)));
        Assert.True(CloudPolicy.HasConsent(Proposed(ollama))); // nothing to agree to: no consent step
        Assert.Null(ollama.KeyVariable);

        var custom = Assert.Single(CloudPresets.All, p => p.BaseUrl is null); // the user types it; every check applies as typed
        var others = CloudPresets.All.Where(p => p != ollama && p != custom).ToList();
        Assert.NotEmpty(others);
        foreach (var preset in others)
        {
            var uri = new Uri(preset.BaseUrl!);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.False(uri.IsLoopback);
            Assert.Null(CloudPolicy.InsecureEndpoint(Proposed(preset)));
        }
        Assert.All(CloudPresets.All, p => Assert.Equal(LlmSettings.OpenAiCompatible, p.Provider));
        Assert.All(CloudPresets.All, p => Assert.Contains(p.KeyVariable, new[] { null, SecretStore.LlmKey })); // the key the client already reads
    }

    [Fact]
    public void Offline_only_still_refuses_every_preset_that_leaves_this_PC()
    {
        foreach (var preset in CloudPresets.All.Where(p => p.BaseUrl is not null))
        {
            var settings = Proposed(preset);
            var leaves = CloudPolicy.CloudHost(settings) is not null;
            Assert.Equal(leaves, TranscriptCleaner.OfflineRefusal(settings, offlineOnly: true) is not null); // a server on this PC stays allowed (SPEC §5)
            Assert.Null(TranscriptCleaner.OfflineRefusal(settings, offlineOnly: false));
        }
    }

    [Fact]
    public Task Offline_only_refuses_switching_on_any_preset() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "offline_only": true }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        foreach (var preset in CloudPresets.All.Where(p => p.BaseUrl is not null))
            Assert.Contains("offline_only", shell.EnableCloud(Proposed(preset)));
        Assert.Empty(dialogs.Asked);
        Assert.False(File.Exists(scratch.Paths.ConsentFile));
        Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
        shell.Dispose();
    });

    [Theory]
    // Each endpoint as the provider's own page gives it, read 16/10/2026: developers.openai.com/api/reference/resources/chat,
    // ai.google.dev/gemini-api/docs/openai and docs.ollama.com/api/openai-compatibility.
    [InlineData("OpenAI", "https://api.openai.com/v1/chat/completions")]
    [InlineData("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("Ollama on this PC", "http://localhost:11434/v1/chat/completions")]
    public async Task A_preset_calls_its_documented_endpoint_with_the_key_it_names(string name, string endpoint)
    {
        var preset = Assert.Single(CloudPresets.All, p => p.Name == name);
        var settings = Proposed(preset);
        settings = settings with { CloudConsent = CloudPolicy.CloudHost(settings) }; // agreed in the consent step (none needed on this PC)
        var secrets = preset.KeyVariable is { } key ? SecretStore.FromContents($"{key}=k-test-only-123\n", null, _ => null) : SecretStore.Empty;
        var handler = new Recorder();
        var cleaner = TranscriptCleaner.Create(settings, offlineOnly: false, secrets, new HttpClient(handler), NullLog.Instance);
        await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Equal(endpoint, handler.Uri?.ToString());
        Assert.Equal(preset.KeyVariable is null ? null : "Bearer k-test-only-123", handler.Authorization);
    }

    [Fact]
    public void The_privacy_policy_carries_the_approved_sentence_in_both_cloud_paragraphs()
    {
        var html = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "privacy.html"));
        var count = 0;
        for (var at = html.IndexOf(ApprovedPolicySentence, StringComparison.Ordinal); at >= 0; at = html.IndexOf(ApprovedPolicySentence, at + 1, StringComparison.Ordinal)) count++;
        Assert.Equal(2, count);
        foreach (var heading in new[] { "<h3>Cloud clean-up: <code>api.anthropic.com</code> (off by default)</h3>", "<h3>Your own server (off by default)</h3>" })
        {
            var start = html.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(start >= 0, $"privacy.html no longer has {heading}");
            Assert.Contains(ApprovedPolicySentence, html[start..html.IndexOf("</p>", start, StringComparison.Ordinal)]);
        }
    }

    /// <summary>Answers every request with one chat completion and remembers where it went and with which key.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Hello there."},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
