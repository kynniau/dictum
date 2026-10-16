using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.Cli;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// Cloud clean-up stays an advanced option (decided 16/10/2026): the approved warning copy appears word for word, and the
/// Clean-up presets only fill in fields. No preset switches cloud clean-up on, skips the consent step or gets past offline_only.
/// Each key goes only to its own service (a service that needs one gets nothing without it), and a server on this PC is never
/// shown as "Nothing leaves this PC" (fixes after the lens on 977bee9, 16/10/2026).
/// </summary>
public class CloudWarningAndPresetsTests
{
    // Approved by the product owner on 16/10/2026 as written; pinned here word for word, independently of the app's copy.
    private static string ApprovedWarning(string host) =>
        $"WARNING: this is an advanced option and it is less private than local clean-up. Your text leaves this PC to be processed on {host}'s servers. Dictum encrypts the connection (HTTPS) but cannot see, validate or certify how that service stores, protects or uses your text, or who else can read it. Do not dictate confidential, personal or client information with cloud clean-up on unless you have checked that service's terms yourself.";

    private const string ApprovedStateLine = "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it.";

    // Approved by the product owner on 16/10/2026 as written; pinned word for word, independently of the app's copy. Settings > Clean-up
    // and Settings > Privacy say it, with the variable of the key they wait for, while a service that needs its key is on and the key is not set.
    private static string ApprovedWaitingLine(string variable) => $"On, but waiting for your {variable}: nothing is sent until you add it to .env.";

    private const string ApprovedPolicySentence = "This is an advanced option: Dictum does not validate or certify how that service stores, protects or uses the text it receives. Check its terms before dictating anything sensitive.";

    // Approved by the product owner on 16/10/2026 for the privacy policy's OpenAI-compatible section, pinned word for word. The label
    // ("OpenAI-compatible service (off by default)") is the <h3>, so the paragraph starts with a capital like its neighbours.
    private const string ApprovedOpenAiCompatibleParagraph = "Only if you choose a preset or enter an address yourself. The presets point at api.openai.com (OpenAI) or generativelanguage.googleapis.com (Google Gemini); a custom address goes wherever you enter. After each dictation Dictum sends that service the text of the dictation, its clean-up instructions and your dictionary terms, with your key. That service handles the data under its own terms. If the server runs on your PC, for example Ollama at localhost, nothing leaves your PC unless that server itself forwards to a cloud service, which Dictum cannot see: Ollama can serve its cloud models once you sign in to it.";

    // The landing page's card for a server you name ends with the policy's approved clause (the end of the paragraph above).
    private const string ApprovedLocalServerClause = "If the server runs on your PC, for example Ollama at localhost, nothing leaves your PC unless that server itself forwards to a cloud service, which Dictum cannot see: Ollama can serve its cloud models once you sign in to it.";

    // The tray panel's privacy line while a server on this PC, or a host off it, does the clean-up. Each line: Approved by the product
    // owner on 16/10/2026 as written: never reword it. Pinned word for word, independently of the app's copy.
    private const string ApprovedFlyoutLocalServer = "Clean-up via a server on this PC";
    private const string ApprovedFlyoutCloud = "Cloud clean-up on · text leaves this PC";

    // Settings > Privacy and the Settings > Clean-up state line for a server on this PC. Approved by the product owner on 16/10/2026 as
    // written: never reword it. Pinned word for word, independently of the app's copy.
    private static string ApprovedLocalServerLine(string host, string model) =>
        $"On: a server on this PC ({host}, model {model}) tidies each dictation. Dictum cannot see whether that server forwards your text elsewhere.";

    // Proposed by the orchestrator to the product owner on 16/10/2026 and awaiting confirmation: pinned word for word, so any change is
    // deliberate. Settings > Clean-up and Settings > Privacy say it while cloud clean-up is on and offline only refuses it.
    private const string ProposedOfflineOnlyLine = "On, but offline only is on: nothing is sent until you switch offline only off.";

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static T Named<T>(ILogical root, string name) where T : Control => All<T>(root).Single(c => AutomationProperties.GetName(c) == name);

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>What the Clean-up page sends to the consent step when the fields are left as the preset filled them.</summary>
    private static LlmSettings Proposed(CloudPreset preset) => new() { Provider = preset.Provider, BaseUrl = preset.BaseUrl, Model = preset.DefaultModel ?? "m" };

    private static IEnumerable<CloudPreset> OffThePc => CloudPresets.All.Where(p => p.BaseUrl is not null && CloudPolicy.CloudHost(Proposed(p)) is not null);

    private static CloudPreset Preset(string name) => Assert.Single(CloudPresets.All, p => p.Name == name);

    /// <summary>Settings as the consent step leaves them: agreed to the host they send to (none is needed on this PC).</summary>
    private static LlmSettings Agreed(LlmSettings s) => s with { CloudConsent = CloudPolicy.CloudHost(s) };

    /// <summary>Where the Service list puts a preset (Anthropic comes first).</summary>
    private static int ServiceIndex(string name) => CloudPresets.All.ToList().FindIndex(p => p.Name == name) + 1;

    /// <summary>The icon in the same row as the tray panel's privacy line.</summary>
    private static Control IconBeside(ILogical root, string line) =>
        ((Panel)All<TextBlock>(root).Single(t => t.Text == line).Parent!).Children.OfType<Viewbox>().Single();

    /// <summary>
    /// The Clean-up card's state line ("On: …" or "On, but …"). Every read follows a move from another page, so the page is drawn
    /// from the settings and the clean-up in use as they are now.
    /// </summary>
    private static string CleanUpStateLine(SettingsWindow window)
    {
        window.ShowPage("Privacy");
        window.ShowPage("Clean-up");
        return All<TextBlock>(window).Single(t => t.IsEffectivelyVisible && t.Text is { } x && (x.StartsWith("On: ", StringComparison.Ordinal) || x.StartsWith("On, ", StringComparison.Ordinal))).Text!;
    }

    /// <summary>Every visible line of Settings > Privacy, drawn afresh in the same way.</summary>
    private static IReadOnlyList<string> PrivacyPageLines(SettingsWindow window)
    {
        window.ShowPage("Clean-up");
        window.ShowPage("Privacy");
        return All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
    }

    /// <summary>A page of the site without its HTML comments: a host named only inside a comment is not named to the reader.</summary>
    private static string VisibleHtml(string page) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page)), "<!--.*?-->", "", RegexOptions.Singleline);

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
    }

    [Fact]
    public void Each_hosted_preset_names_its_own_key_and_Ollama_names_none()
    {
        // Lens finding (16/10/2026): every preset named LLM_API_KEY, so one key went to whichever host was chosen.
        Assert.Equal(
            new (string, string?, bool)[]
            {
                ("OpenAI", SecretStore.OpenAiKey, true),
                ("Google Gemini", SecretStore.GeminiKey, true),
                ("Ollama on this PC", null, false),
                ("Custom server (OpenAI-compatible)", SecretStore.LlmKey, false),
            },
            CloudPresets.All.Select(p => (p.Name, p.KeyVariable, p.KeyRequired)));
        Assert.Equal(("OPENAI_API_KEY", "GEMINI_API_KEY", "LLM_API_KEY"), (SecretStore.OpenAiKey, SecretStore.GeminiKey, SecretStore.LlmKey));
        // Each one is redacted from the log and listed by the self-test.
        Assert.All(CloudPresets.All.Where(p => p.KeyVariable is not null), p => Assert.Contains(p.KeyVariable!, SecretStore.KeyNames));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "OpenAI")]
    [InlineData(" https://API.OpenAI.com/v1/ ", "OpenAI")]
    [InlineData("https://api.openai.com:443/v2", "OpenAI")] // the same server at another path: still OpenAI's key, and only there
    [InlineData("https://api.openai.com:8443/v1", "Custom server (OpenAI-compatible)")] // the same host at another port is another server, off this PC too
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "Google Gemini")]
    [InlineData("http://localhost:11434/v1", "Ollama on this PC")]
    [InlineData("http://127.0.0.1:11434/v1", "Ollama on this PC")] // the same server written another way: still no key
    [InlineData("http://[::1]:11434/v1", "Ollama on this PC")]
    [InlineData("http://localhost:1234/v1", "Custom server (OpenAI-compatible)")] // another server on this PC (LM Studio's usual port)
    [InlineData("https://api.openai.com.example.net/v1", "Custom server (OpenAI-compatible)")]
    [InlineData("https://llm.example.net/api.openai.com/v1", "Custom server (OpenAI-compatible)")]
    [InlineData("https://llm.example.net/v1", "Custom server (OpenAI-compatible)")]
    [InlineData(null, "Custom server (OpenAI-compatible)")]
    [InlineData("not an address", "Custom server (OpenAI-compatible)")]
    public void The_server_an_address_names_decides_which_key_goes(string? baseUrl, string service) =>
        Assert.Equal(service, CloudPresets.ForAddress(baseUrl).Name);

    [Theory]
    // Every key variable holds its own value: each server must get exactly its own key. Ollama names none, so it gets none
    // even with every key set; any other address, on this PC or off it, gets LLM_API_KEY.
    [InlineData("https://api.openai.com/v1", "Bearer k-openai-test")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "Bearer k-gemini-test")]
    [InlineData("http://localhost:11434/v1/", null)]
    [InlineData("http://127.0.0.1:11434/v1", null)]
    [InlineData("https://llm.example.net/v1", "Bearer k-custom-test")]
    [InlineData("https://api.openai.com:8443/v1", "Bearer k-custom-test")] // OpenAI's host at another port: not OpenAI's server, so not its key
    [InlineData("http://localhost:1234/v1", "Bearer k-custom-test")]
    public async Task Each_key_goes_only_to_its_own_service(string baseUrl, string? authorization)
    {
        var secrets = SecretStore.FromContents("ANTHROPIC_API_KEY=k-anthropic-test\nOPENAI_API_KEY=k-openai-test\nGEMINI_API_KEY=k-gemini-test\nLLM_API_KEY=k-custom-test\n", null, _ => null);
        var handler = new Recorder();
        var cleaner = TranscriptCleaner.Create(Agreed(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = baseUrl, Model = "m" }), offlineOnly: false, secrets,
            new HttpClient(handler), NullLog.Instance);
        await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count);
        Assert.Equal(authorization, handler.Authorization);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Google Gemini")]
    public async Task A_preset_that_needs_its_key_sends_nothing_without_it(string name)
    {
        // Lens finding (16/10/2026): with the OpenAI preset agreed to and no key, every dictation went to api.openai.com with no
        // Authorization header. Every other key is set here, so only this preset's own missing key can stop the request.
        var preset = Preset(name);
        var others = string.Concat(SecretStore.KeyNames.Where(k => k != preset.KeyVariable).Select(k => $"{k}=k-{k.ToLowerInvariant()}-test\n"));
        var handler = new Recorder();
        var cleaner = TranscriptCleaner.Create(Agreed(Proposed(preset)), offlineOnly: false, SecretStore.FromContents(others, null, _ => null), new HttpClient(handler), NullLog.Instance);
        Assert.False(cleaner.UsesModel);
        Assert.Equal($"rule-based tidy only: {preset.KeyVariable} is not set (environment or .env)", cleaner.Status);
        var result = await cleaner.CleanAsync("um the client's account is overdrawn", TestContext.Current.CancellationToken);
        Assert.False(result.Cleaned);
        Assert.Equal(0, handler.Count); // no request at all
    }

    [Fact]
    public Task Switching_on_the_OpenAI_preset_without_its_key_sends_nothing_and_says_which_key_is_missing()
    {
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SecretStore.OpenAiKey)), "OPENAI_API_KEY is set in this machine's environment, which wins over .env.");
        return UiSession.Run(() =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24" }""");
            File.WriteAllText(scratch.Paths.EnvFile, "LLM_API_KEY=k-custom-test\n"); // the custom-server key must not stand in for OpenAI's
            var (shell, _, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);
            dialogs.Answers.Enqueue(0); // Allow
            Assert.Null(shell.EnableCloud(Proposed(Preset("OpenAI"))));
            Assert.Equal("rule-based tidy only: OPENAI_API_KEY is not set (environment or .env)", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves); // nothing is sent, so the tray panel may say so
            shell.Dispose();
        });
    }

    [Theory]
    [InlineData("OpenAI", "OPENAI_API_KEY")]
    [InlineData("Google Gemini", "GEMINI_API_KEY")]
    public Task A_preset_that_is_on_without_its_key_says_it_is_waiting_for_that_key_and_the_warning_returns_once_it_is_set(string name, string variable)
    {
        // Before the approved wording (16/10/2026) the Clean-up card still said the text of each dictation is sent to the host while the key
        // was missing and nothing was. Each service names its own variable, taken from the preset.
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)), $"{variable} is set in this machine's environment, which wins over .env.");
        return UiSession.Run(() =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24" }""");
            var (shell, _, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);
            dialogs.Answers.Enqueue(0); // Allow
            Assert.Null(shell.EnableCloud(Proposed(Preset(name))));
            var window = new SettingsWindow(shell);
            window.Show();

            // No key: nothing is sent, and both pages say which key they wait for, in the approved words and no others.
            Assert.Equal($"rule-based tidy only: {variable} is not set (environment or .env)", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);
            Assert.Equal(ApprovedWaitingLine(variable), CleanUpStateLine(window));
            var waiting = PrivacyPageLines(window);
            Assert.Contains(ApprovedWaitingLine(variable), waiting);
            Assert.DoesNotContain(waiting, l => l.StartsWith("Nothing leaves this PC: speech", StringComparison.Ordinal) || l.Contains(ApprovedStateLine, StringComparison.Ordinal));

            // The key is set: the text now goes to the host, so both pages say so again, with the approved warning, and no longer say "waiting".
            File.WriteAllText(scratch.Paths.EnvFile, $"{variable}=k-test\n");
            shell.Reload(userRequested: false);
            Assert.EndsWith($"({variable} present)", shell.CleanupStatus);
            Assert.False(shell.NothingLeaves);
            var cleanUp = CleanUpStateLine(window);
            Assert.StartsWith("On: the text of each dictation is sent to ", cleanUp);
            Assert.EndsWith("You agreed to this host in the consent step. " + ApprovedStateLine, cleanUp);
            Assert.DoesNotContain("waiting", cleanUp);
            var privacy = Assert.Single(PrivacyPageLines(window), l => l.StartsWith("The text of each dictation goes to ", StringComparison.Ordinal));
            Assert.EndsWith("Audio never leaves this PC. " + ApprovedStateLine, privacy);
            Assert.DoesNotContain("waiting", privacy);

            // Offline only refuses the clean-up before the key is looked at, so the key is not what stops it: neither page says it is
            // waiting, and both say offline only stops it.
            File.Delete(scratch.Paths.EnvFile);
            shell.Write(("offline_only", System.Text.Json.Nodes.JsonValue.Create(true)));
            Assert.Contains("offline_only", shell.CleanupStatus);
            Assert.Equal(ProposedOfflineOnlyLine, CleanUpStateLine(window));
            var offline = PrivacyPageLines(window);
            Assert.Contains(ProposedOfflineOnlyLine, offline);
            Assert.DoesNotContain(offline, l => l.Contains("waiting", StringComparison.Ordinal));
            window.Close();
            shell.Dispose();
        });
    }

    [Theory]
    [InlineData(true)] // the key leaves the .env beside the exe after the load
    [InlineData(false)] // the key arrives in the .env beside the exe after the load
    public Task The_pages_follow_the_clean_up_in_use_not_the_env_files_on_disk(bool keyAtLoad)
    {
        // Lens probe on a56b436 (16/10/2026): the pages re-read .env at every redraw while the clean-up in use kept the keys read at the
        // last load. With the key taken out of the .env beside the exe and no reload, every dictation still went to api.openai.com while
        // both pages said nothing is sent; with the key put in and no reload, the card said the text is sent while nothing was.
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SecretStore.OpenAiKey)), "OPENAI_API_KEY is set in this machine's environment, which wins over .env.");
        return UiSession.Run(() =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24" }""");
            if (keyAtLoad) File.WriteAllText(scratch.Paths.ExeEnvFile, "OPENAI_API_KEY=k-test\n");
            var (shell, _, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);
            dialogs.Answers.Enqueue(0); // Allow
            Assert.Null(shell.EnableCloud(Proposed(Preset("OpenAI"))));
            var status = shell.CleanupStatus;
            Assert.Equal(keyAtLoad, status.EndsWith("(OPENAI_API_KEY present)", StringComparison.Ordinal));
            var window = new SettingsWindow(shell);
            window.Show();

            // The .env beside the exe changes and nothing reloads: the clean-up in use is the one built at the load.
            if (keyAtLoad) File.Delete(scratch.Paths.ExeEnvFile);
            else File.WriteAllText(scratch.Paths.ExeEnvFile, "OPENAI_API_KEY=k-test\n");
            Assert.Equal(status, shell.CleanupStatus);
            Assert.Equal(!keyAtLoad, shell.NothingLeaves);
            var cleanUp = CleanUpStateLine(window);
            var privacy = PrivacyPageLines(window);
            if (keyAtLoad)
            {
                // It still sends the text with the key it read, so neither page may say it is waiting.
                Assert.StartsWith("On: the text of each dictation is sent to api.openai.com (", cleanUp);
                Assert.DoesNotContain(privacy, l => l.Contains("waiting", StringComparison.Ordinal));
                Assert.Contains(privacy, l => l.StartsWith("The text of each dictation goes to api.openai.com for clean-up", StringComparison.Ordinal));
            }
            else
            {
                // It has no key and sends nothing, so both pages still wait for it.
                Assert.Equal(ApprovedWaitingLine(SecretStore.OpenAiKey), cleanUp);
                Assert.Contains(ApprovedWaitingLine(SecretStore.OpenAiKey), privacy);
                Assert.DoesNotContain(privacy, l => l.StartsWith("Nothing leaves this PC: speech", StringComparison.Ordinal));
            }
            window.Close();
            shell.Dispose();
        });
    }

    [Theory]
    [InlineData("OpenAI", "OPENAI_API_KEY")]
    [InlineData("Google Gemini", "GEMINI_API_KEY")]
    [InlineData("Anthropic", "ANTHROPIC_API_KEY")]
    public Task With_offline_only_on_cloud_clean_up_that_is_on_says_on_both_pages_that_nothing_is_sent(string service, string variable)
    {
        // Lens finding on a56b436 (16/10/2026): with a cloud preset agreed to, its key set and offline only on, the status said offline_only
        // refused it and nothing was sent, while the Clean-up card still said the text of each dictation is sent to the host. Anthropic,
        // agreed to with its key, took the same path.
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)), $"{variable} is set in this machine's environment, which wins over .env.");
        return UiSession.Run(() =>
        {
            using var scratch = new Scratch("""{ "hotkey": "F24" }""");
            File.WriteAllText(scratch.Paths.EnvFile, $"{variable}=k-test\n");
            var (shell, _, dialogs) = scratch.Shell();
            shell.Reload(userRequested: false);
            dialogs.Answers.Enqueue(0); // Allow
            var proposed = service == "Anthropic" ? new LlmSettings { Provider = LlmSettings.Anthropic, Model = LlmSettings.AnthropicDefaultModel } : Proposed(Preset(service));
            Assert.Null(shell.EnableCloud(proposed));
            Assert.EndsWith($"({variable} present)", shell.CleanupStatus);

            shell.Write(("offline_only", System.Text.Json.Nodes.JsonValue.Create(true)));
            Assert.StartsWith("offline_only is on: ", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);
            var window = new SettingsWindow(shell);
            window.Show();
            Assert.Equal(ProposedOfflineOnlyLine, CleanUpStateLine(window));
            var privacy = PrivacyPageLines(window);
            Assert.Contains(ProposedOfflineOnlyLine, privacy);
            Assert.DoesNotContain(privacy, l => l.StartsWith("Nothing leaves this PC: speech", StringComparison.Ordinal) || l.Contains(ApprovedStateLine, StringComparison.Ordinal));
            window.Close();
            shell.Dispose();
        });
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "It uses your own OpenAI API key (OPENAI_API_KEY).")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "It uses your own Google Gemini API key (GEMINI_API_KEY).")]
    [InlineData("https://llm.example.net/v1", "It uses your own API key (LLM_API_KEY) if you have set one.")]
    public void The_consent_text_names_the_key_that_goes_with_the_text(string baseUrl, string sentence)
    {
        var llm = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = baseUrl, Model = "m" };
        var host = CloudPolicy.CloudHost(llm)!;
        var text = CloudPolicy.ConsentText(llm, host);
        // A factual sentence beside the others; the approved warning paragraph follows, untouched.
        Assert.Contains("personal dictionary. " + sentence + " Whoever runs that server receives the text; its own terms apply.\n\n" + ApprovedWarning(host) + "\n\nNothing is sent until you agree.", text);
        Assert.Single(SecretStore.KeyNames, k => text.Contains(k, StringComparison.Ordinal)); // it names one key: the one that goes
    }

    [Fact]
    public Task The_key_line_names_the_variable_that_goes_to_the_address_in_the_field() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        var service = Named<ComboBox>(window, "Service");
        string KeyLine() => All<TextBlock>(window).Single(t => t.IsEffectivelyVisible && t.Text is { } x && (x.Contains("_API_KEY=", StringComparison.Ordinal) || x.EndsWith("needs no key.", StringComparison.Ordinal))).Text!;

        Assert.Equal("The key goes in .env in the settings folder: ANTHROPIC_API_KEY=your Anthropic API key.", KeyLine());
        service.SelectedIndex = ServiceIndex("OpenAI");
        Assert.Equal("The key goes in .env in the settings folder: OPENAI_API_KEY=your OpenAI API key. Without it nothing is sent.", KeyLine());
        service.SelectedIndex = ServiceIndex("Google Gemini");
        Assert.Equal("The key goes in .env in the settings folder: GEMINI_API_KEY=your Google Gemini API key. Without it nothing is sent.", KeyLine());
        service.SelectedIndex = ServiceIndex("Ollama on this PC");
        Assert.Equal("Ollama on this PC needs no key.", KeyLine());
        service.SelectedIndex = ServiceIndex("Custom server (OpenAI-compatible)");
        Assert.Equal("If the server needs a key, it goes in .env in the settings folder: LLM_API_KEY=your key.", KeyLine());
        service.SelectedIndex = ServiceIndex("OpenAI");
        Named<TextBox>(window, "Server address").Text = "https://llm.example.net/v1"; // another server: OpenAI's key does not go there
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // TextBox raises TextChanged through the dispatcher
        Assert.Equal("If the server needs a key, it goes in .env in the settings folder: LLM_API_KEY=your key.", KeyLine());
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public void Every_key_Dictum_reads_is_registered_with_the_log_redactor()
    {
        using var scratch = new Scratch();
        string[] keys = [SecretStore.AnthropicKey, SecretStore.OpenAiKey, SecretStore.GeminiKey, SecretStore.LlmKey];
        File.WriteAllText(scratch.Paths.EnvFile, string.Concat(keys.Select(k => $"{k}=secret-{k}-123\n")));
        var redactor = new SecretRedactor();
        ConfigLoader.Load(scratch.Paths, redactor, _ => null, _ => false, _ => { });
        foreach (var k in keys) Assert.Equal("a [redacted] b", redactor.Redact($"a secret-{k}-123 b"));
    }

    [Fact]
    public void The_self_test_lists_every_key_Dictum_reads_and_never_a_value()
    {
        // Lens note (16/10/2026): the self-test listed only ANTHROPIC_API_KEY and LLM_API_KEY before the preset keys; nothing pinned the four.
        var secrets = SecretStore.FromContents("OPENAI_API_KEY=k-openai-test\n", "GEMINI_API_KEY=k-gemini-test\n", _ => null);
        var lines = new List<(string Label, string Value, bool? Pass)>();
        SelfTestCommand.KeyLines(secrets, (label, value, pass) => lines.Add((label, value, pass)));
        Assert.Equal(
            new (string, string, bool?)[]
            {
                ("ANTHROPIC_API_KEY", "absent", null),
                ("OPENAI_API_KEY", "present (config-folder .env)", null),
                ("GEMINI_API_KEY", "present (exe-folder .env)", null),
                ("LLM_API_KEY", "absent", null),
            },
            lines);
    }

    [Fact]
    public void A_server_on_this_PC_never_counts_as_nothing_leaving()
    {
        // Lens probe P5 (16/10/2026): Ollama with a cloud model name, and the app said nothing leaves this PC. Ollama can serve its
        // cloud models once you sign in to it, which Dictum cannot see.
        var ollama = Proposed(Preset("Ollama on this PC")) with { Model = "gemma4:cloud" };
        Assert.Equal("localhost", CloudPolicy.LocalServerHost(ollama));
        Assert.False(CloudPolicy.NothingLeaves(ollama, usesModel: true));
        var offPc = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.net/v1", Model = "m" };
        Assert.Null(CloudPolicy.LocalServerHost(offPc));
        Assert.False(CloudPolicy.NothingLeaves(offPc, usesModel: true));
        Assert.False(CloudPolicy.NothingLeaves(new LlmSettings { Provider = LlmSettings.Anthropic }, usesModel: true));
        // Nothing goes anywhere when the service is not in use (offline_only, a missing key) or clean-up runs in Dictum.
        Assert.True(CloudPolicy.NothingLeaves(ollama, usesModel: false));
        Assert.True(CloudPolicy.NothingLeaves(offPc, usesModel: false));
        Assert.True(CloudPolicy.NothingLeaves(new LlmSettings(), usesModel: true)); // the local model
        Assert.True(CloudPolicy.NothingLeaves(new LlmSettings { Provider = LlmSettings.NoProvider }, usesModel: false));
    }

    [Fact]
    public Task The_Ollama_preset_shows_a_server_on_this_PC_never_nothing_leaves() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        var before = new FlyoutWindow(shell);
        before.ShowNear(null);
        Assert.Contains(All<TextBlock>(before), t => t.Text == "Nothing leaves this PC");
        Assert.True(IconBeside(before, "Nothing leaves this PC").IsVisible); // the local model: the lock stays
        before.Close();

        Assert.Null(shell.EnableCloud(Proposed(Preset("Ollama on this PC")) with { Model = "gemma4:cloud" }));
        Assert.Empty(dialogs.Asked); // Dictum's own request stays on this PC: no consent step
        Assert.False(shell.NothingLeaves);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(All<TextBlock>(flyout), t => t.Text == ApprovedFlyoutLocalServer);
        Assert.False(IconBeside(flyout, ApprovedFlyoutLocalServer).IsVisible); // no lock beside it
        Assert.DoesNotContain(All<TextBlock>(flyout), t => t.Text == "Nothing leaves this PC");
        flyout.Close();

        var window = new SettingsWindow(shell);
        window.Show();
        foreach (var page in new[] { "Privacy", "Clean-up" })
        {
            window.ShowPage(page);
            Assert.Contains(All<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text == ApprovedLocalServerLine("localhost", "gemma4:cloud"));
        }
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task While_text_goes_off_this_PC_the_tray_panel_says_so_and_the_privacy_page_carries_the_approved_sentence() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" } }""");
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.False(shell.NothingLeaves);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(All<TextBlock>(flyout), t => t.Text == ApprovedFlyoutCloud);
        Assert.False(IconBeside(flyout, ApprovedFlyoutCloud).IsVisible); // no lock beside it
        flyout.Close();

        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Privacy");
        var summary = All<TextBlock>(window).Single(t => t.IsEffectivelyVisible && t.Text?.StartsWith("The text of each dictation goes to llm.example.com", StringComparison.Ordinal) == true).Text!;
        Assert.EndsWith("Audio never leaves this PC. " + ApprovedStateLine, summary);
        window.Close();
        shell.Dispose();
    });

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080/v1")]
    public async Task Anthropic_at_a_plain_http_address_on_this_PC_is_refused(string url)
    {
        // Lens probe P3 (16/10/2026): allowed, while its consent dialog said "Dictum encrypts the connection (HTTPS)".
        var llm = new LlmSettings { Provider = LlmSettings.Anthropic, BaseUrl = url, Model = LlmSettings.AnthropicDefaultModel };
        Assert.Contains("https://", CloudPolicy.InsecureEndpoint(llm));
        Assert.Null(CloudPolicy.InsecureEndpoint(llm with { Provider = LlmSettings.OpenAiCompatible })); // a local OpenAI-compatible server still may
        var handler = new Recorder();
        var secrets = SecretStore.FromContents("ANTHROPIC_API_KEY=k-anthropic-test\n", null, _ => null);
        var cleaner = TranscriptCleaner.Create(Agreed(llm), offlineOnly: false, secrets, new HttpClient(handler), NullLog.Instance);
        Assert.False(cleaner.UsesModel); // a settings.json like this, agreed to before, now gets the rule-based tidy
        await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public Task Switching_on_Anthropic_at_a_plain_http_address_on_this_PC_is_refused_before_the_consent_step() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Contains("https://", shell.EnableCloud(new LlmSettings { Provider = LlmSettings.Anthropic, BaseUrl = "http://localhost:8080", Model = LlmSettings.AnthropicDefaultModel }));
        Assert.Empty(dialogs.Asked); // refused before the dialog that says the connection is encrypted
        Assert.False(File.Exists(scratch.Paths.ConsentFile));
        Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
        shell.Dispose();
    });

    [Fact]
    public void A_preset_default_model_applies_only_at_the_preset_address()
    {
        var openai = Preset("OpenAI");
        var gemini = Preset("Google Gemini");
        Assert.Equal("gpt-5.4-nano", CloudPresets.DefaultModelFor(openai, openai.BaseUrl));
        Assert.Equal("gpt-5.4-nano", CloudPresets.DefaultModelFor(openai, " https://api.openai.com/v1/ ")); // spaces and a final slash: the same address
        Assert.Equal("gemini-3.8-flash", CloudPresets.DefaultModelFor(gemini, "https://generativelanguage.googleapis.com/v1beta/openai"));
        Assert.Null(CloudPresets.DefaultModelFor(openai, "https://llm.example.net/v1")); // lens probe P4: another server, which may not have that model
        Assert.Null(CloudPresets.DefaultModelFor(openai, gemini.BaseUrl));
        Assert.Null(CloudPresets.DefaultModelFor(Preset("Ollama on this PC"), "http://localhost:11434/v1/")); // no default: whichever model was pulled
    }

    [Fact]
    public Task An_address_typed_over_a_preset_does_not_get_its_default_model() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        var before = File.ReadAllText(scratch.Paths.SettingsFile);
        Named<ComboBox>(window, "Service").SelectedIndex = ServiceIndex("OpenAI");
        Named<TextBox>(window, "Server address").Text = "https://llm.example.net/v1";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // TextBox raises TextChanged through the dispatcher
        Assert.Equal("the model name the server uses", Named<TextBox>(window, "Model").Watermark); // not gpt-5.4-nano
        dialogs.Answers.Enqueue(0); // Allow, if the consent step were reached
        Click(window, "Use cloud clean-up…");
        Assert.Empty(dialogs.Asked);
        Assert.Contains(All<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text == "Type the model name the server uses.");
        Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile));
        Assert.False(File.Exists(scratch.Paths.ConsentFile));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Switching_on_Ollama_shows_the_on_state_with_its_off_switch() => UiSession.Run(() =>
    {
        // Lens probe P6 (16/10/2026): the page said "Cloud clean-up is on." but the card stayed in its off state, with no off switch.
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        Named<ComboBox>(window, "Service").SelectedIndex = ServiceIndex("Ollama on this PC");
        Named<TextBox>(window, "Model").Text = "llama3.2";
        Click(window, "Use cloud clean-up…");
        Assert.Empty(dialogs.Asked); // a server on this PC: no consent step
        window.ShowPage("Clean-up"); // redraw from the saved settings
        Assert.Contains(All<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text == ApprovedLocalServerLine("localhost", "llama3.2"));
        Assert.DoesNotContain(All<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text?.Contains(ApprovedStateLine, StringComparison.Ordinal) == true); // for hosts off this PC
        Assert.DoesNotContain(All<Button>(window), b => b.IsEffectivelyVisible && b.Content as string == "Use cloud clean-up…");
        Assert.Contains(All<Button>(window), b => b.IsEffectivelyVisible && b.Content as string == "Switch cloud clean-up off");
        Click(window, "Switch cloud clean-up off");
        Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public void The_landing_page_says_a_server_on_this_PC_may_forward_the_text()
    {
        Assert.EndsWith(ApprovedLocalServerClause, ApprovedOpenAiCompatibleParagraph); // the policy's approved clause, not new wording
        var html = VisibleHtml("index.html");
        Assert.Contains("<p>Only if you point clean-up at a server of your own, such as Ollama. " + ApprovedLocalServerClause + "</p>", html);
        Assert.DoesNotContain("If that server runs on your PC, nothing leaves it.", html);
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
        foreach (var heading in new[] { "<h3>Cloud clean-up: <code>api.anthropic.com</code> (off by default)</h3>", "<h3>OpenAI-compatible service (off by default)</h3>" })
        {
            var start = html.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(start >= 0, $"privacy.html no longer has {heading}");
            Assert.Contains(ApprovedPolicySentence, html[start..html.IndexOf("</p>", start, StringComparison.Ordinal)]);
        }
    }

    [Fact]
    public void The_privacy_policy_carries_the_approved_OpenAI_compatible_wording_word_for_word()
    {
        var html = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "privacy.html"));
        const string heading = "<h3>OpenAI-compatible service (off by default)</h3>";
        var start = html.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"privacy.html no longer has {heading}");
        var rest = html[(start + heading.Length)..].TrimStart();
        Assert.StartsWith("<p>", rest);
        // One paragraph with no markup inside: the approved text as written, then the advanced-option sentence.
        Assert.Equal(ApprovedOpenAiCompatibleParagraph + " " + ApprovedPolicySentence, rest["<p>".Length..rest.IndexOf("</p>", StringComparison.Ordinal)]);
    }

    [Fact]
    public void The_privacy_policy_and_the_landing_page_name_every_host_cloud_clean_up_can_send_text_to()
    {
        var anthropic = new LlmSettings { Provider = LlmSettings.Anthropic, Model = LlmSettings.AnthropicDefaultModel };
        var hosts = OffThePc.Select(p => new Uri(p.BaseUrl!).Host).Prepend(CloudPolicy.CloudHost(anthropic)!).ToList();
        Assert.True(hosts.Count >= 3); // Anthropic and every preset off this PC; a new preset fails this test until both pages name its host
        foreach (var page in new[] { "privacy.html", "index.html" })
        {
            var html = VisibleHtml(page); // a host only inside an HTML comment does not count
            foreach (var host in hosts) Assert.Contains(host, html);
        }
    }

    /// <summary>Answers every request with one chat completion, counts them, and remembers where the last went and with which key.</summary>
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
