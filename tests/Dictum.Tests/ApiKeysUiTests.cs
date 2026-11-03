using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.Llm;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// API keys, the UI lane (K-UI, 04/11/2026; brief .demo-preview/design-api-keys.md §2 items 1, 2, 3 and 5): the Add a key dialog, the
/// key list with Active and Remove, Check this key behind the consent on record, and the full-template key file. Every test runs on a
/// scratch profile with a fake credential store or a fake vault, and a fake server stands in for the network.
/// </summary>
public sealed class ApiKeysUiTests
{
    private const string Key = "Zq81Lw4Rv93Kd7Pm2Xa6Yb5N"; // random letters and digits: no four in a row spell a word the app or its log uses

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static Button ButtonNamed(ILogical root, string text) =>
        Assert.Single(All<Button>(root), b => b.IsEffectivelyVisible && (b.Content as string == text || AutomationProperties.GetName(b) == text));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Every string a window shows or names: text, button labels, accessible names and what a text box holds.</summary>
    private static List<string> Exposed(ILogical root)
    {
        var seen = new List<string>();
        foreach (var t in All<TextBlock>(root)) if (t.Text is { } s) seen.Add(s);
        foreach (var b in All<Button>(root)) if (b.Content is string s) seen.Add(s);
        foreach (var c in All<Control>(root)) if (AutomationProperties.GetName(c) is { Length: > 0 } s) seen.Add(s);
        foreach (var x in All<TextBox>(root)) if (x.Text is { Length: > 0 } s) seen.Add(s);
        return seen;
    }

    /// <summary>True when <paramref name="text"/> holds any <paramref name="run"/> characters of <see cref="Key"/> in a row.</summary>
    private static bool HoldsPartOfKey(string text, int run = 4)
    {
        for (var i = 0; i + run <= Key.Length; i++) if (text.Contains(Key.Substring(i, run), StringComparison.Ordinal)) return true;
        return false;
    }

    private const string Base = """ "hotkey": "F24", "whisper": { "model": "base.en" } """;

    private const string OpenAiCleanup = """, "llm": { "provider": "openai-compatible", "base_url": "https://api.openai.com/v1", "model": "gpt-5.4-nano" }""";

    private const string GeminiCleanup = """, "llm": { "provider": "openai-compatible", "base_url": "https://generativelanguage.googleapis.com/v1beta/openai/", "model": "gemini-3.8-flash" }""";

    private static void CleanupConsent(Scratch scratch, string host) =>
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, host, DateTimeOffset.UtcNow, "test"));

    // ---- 1. Add a key: the vault's refusals, verbatim -------------------------------------------------------------------------

    /// <summary>
    /// Pin (mutation-checked): every refusal Add raises for what was typed is the dialog's error line, byte for byte as approve-api-keys.md
    /// row 14 has it (the strings are written out here, not read from the app), with nothing stored and the Key field emptied.
    /// </summary>
    [Theory]
    [InlineData("", "k-valid-0123456789", "The label is empty.")]
    [InlineData("Work", "   ", "The key is empty.")]
    [InlineData("Work", "k-valid-01234\u000756789", "The key holds a line break or another control character.")]
    [InlineData("Work", null, "The key is longer than 2560 bytes.")]
    [InlineData("sk-test-0123456789", "sk-test-0123456789abcdef", "The label holds part of the key.")]
    [InlineData("Work", "k-valid-0123456789", "This platform has no credential store Dictum can use.")]
    public Task Add_refusals_are_shown_verbatim(string label, string? key, string expected) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var store = new FakeCredentialStore { Available = expected != "This platform has no credential store Dictum can use." };
        var vault = new SecretVault(scratch.Paths, store);
        var window = new AddKeyWindow(vault, SecretStore.OpenAiKey, "OpenAI", _ => { });
        window.Show();
        window.LabelBox.Text = label;
        window.KeyBox.Text = key ?? new string('a', 2561);
        Click(ButtonNamed(window, "Save"));
        Assert.Equal(expected, window.Error);
        Assert.True(window.IsVisible); // still open, to try again
        Assert.Null(window.AddedId);
        Assert.Empty(store.Entries);
        Assert.Equal("", window.KeyText); // Add returned: nothing of the key stays in the field
        window.Close();
    });

    // ---- 2. Nothing of the key survives in what the dialog or the list exposes -------------------------------------------------

    /// <summary>
    /// Pin (mutation-checked): after Save, the vault has the key and nothing else does: the field is empty, and no text, button, accessible
    /// name or field of the dialog, of the list drawn afterwards, of the page's status lines or of the log holds any four characters of it.
    /// </summary>
    [Fact]
    public Task Nothing_of_the_key_survives_in_what_the_dialog_and_the_list_expose() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}}{{OpenAiCleanup}} }""");
        var log = new RecordingLog();
        var (shell, _, _) = scratch.Shell(log: log);
        try
        {
            shell.Reload(userRequested: false);
            var vault = new FakeVault();
            var dialog = new AddKeyWindow(vault, SecretStore.OpenAiKey, "OpenAI", _ => { });
            dialog.Show();
            Assert.Equal("OpenAI", dialog.LabelBox.Text); // prefilled with the service's name
            Assert.Equal('•', dialog.KeyBox.PasswordChar);
            Assert.False(dialog.KeyBox.IsUndoEnabled); // Ctrl+Z cannot bring a cleared key back
            dialog.KeyBox.Text = Key;
            Click(ButtonNamed(dialog, "Save"));
            Assert.False(dialog.IsVisible); // saved and closed
            Assert.Equal((SecretStore.OpenAiKey, "OpenAI", Key), Assert.Single(vault.Added));
            Assert.Equal("", dialog.KeyText);
            Assert.DoesNotContain(Exposed(dialog), s => HoldsPartOfKey(s));

            var said = new List<string>();
            var panel = new KeysPanel(shell, vault, said.Add, _ => null);
            var host = new Window { Content = panel.View };
            host.Show();
            panel.Show(SecretStore.OpenAiKey, "OpenAI");
            panel.Added(SecretStore.OpenAiKey, dialog.AddedId);
            Assert.Equal("Key \"OpenAI\" added. Dictum uses it from the next dictation.", said[^1]);
            Assert.Contains("OpenAI", Exposed(host)); // the label is shown
            Assert.DoesNotContain(Exposed(host), s => HoldsPartOfKey(s));
            Assert.DoesNotContain(said, s => HoldsPartOfKey(s));
            Assert.False(HoldsPartOfKey(log.All));
            Assert.False(HoldsPartOfKey(File.ReadAllText(scratch.Paths.SettingsFile)));
            host.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>A label in keys.json that holds part of its key (edited by hand) is never shown: the row says "Unnamed key".</summary>
    [Fact]
    public Task A_label_holding_part_of_its_key_is_shown_as_an_unnamed_key() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}} }""");
        var (shell, platform, _) = scratch.Shell();
        try
        {
            var id = shell.Vault.Add(SecretStore.GroqKey, "Work", Key);
            KeysFile.Write(scratch.Paths.KeysFile, [new StoredKey(id, SecretStore.GroqKey, "mine " + Key[..12], new DateOnly(2026, 11, 4))]);
            shell.Reload(userRequested: false);
            var panel = new KeysPanel(shell, shell.Vault, _ => { }, _ => null);
            var host = new Window { Content = panel.View };
            host.Show();
            panel.Show(SecretStore.GroqKey, "Groq");
            Assert.Contains("Unnamed key", Exposed(host));
            Assert.Contains("Added here on 04/11/2026", Exposed(host));
            Assert.DoesNotContain(Exposed(host), s => HoldsPartOfKey(s));
            Assert.Single(platform.FakeCredentials.Entries);
            host.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    // ---- 3. Use this key writes keys.active through SetActive -------------------------------------------------------------------

    /// <summary>
    /// Pin (mutation-checked): with two stored keys and none chosen the list says none is used; "Use Personal" writes keys.active for
    /// that variable to that key's id (SecretVault.SetActive), the settings in use follow, and the Active mark moves to it.
    /// </summary>
    [Fact]
    public Task Use_this_key_writes_keys_active_through_SetActive() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}} }""");
        var (shell, _, _) = scratch.Shell();
        try
        {
            var work = shell.Vault.Add(SecretStore.OpenAiKey, "Work", Key);
            var personal = shell.Vault.Add(SecretStore.OpenAiKey, "Personal", Key + "B");
            shell.Reload(userRequested: false);
            var said = new List<string>();
            var panel = new KeysPanel(shell, shell.Vault, said.Add, _ => null);
            var host = new Window { Content = panel.View };
            host.Show();
            panel.Show(SecretStore.OpenAiKey, "OpenAI");
            Assert.Contains(KeysPanel.NoneChosen, Exposed(host));
            Assert.Empty(All<TextBlock>(host).Where(t => t.IsEffectivelyVisible && t.Text == "Active"));

            Click(ButtonNamed(host, "Use Personal"));
            var written = JsonNode.Parse(File.ReadAllText(scratch.Paths.SettingsFile))!["keys"]!["active"]![SecretStore.OpenAiKey]!.GetValue<string>();
            Assert.Equal(personal, written);
            Assert.Equal(personal, shell.Settings.Keys.Active[SecretStore.OpenAiKey]);
            Assert.Equal("Dictum now uses \"Personal\" from the next dictation.", said[^1]);
            Assert.Single(All<TextBlock>(host), t => t.IsEffectivelyVisible && t.Text == "Active");
            ButtonNamed(host, "Use Work"); // the other one is offered
            Assert.DoesNotContain(All<TextBlock>(host), t => t.IsEffectivelyVisible && t.Text == KeysPanel.NoneChosen);
            Assert.NotEqual(work, personal);
            host.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// The Active mark is the entry SecretStore.Get uses (SPEC §5), for every mix of an environment variable, a .env line, none, one or
    /// two stored keys, and each choice keys.active can hold: the list's own rule (KeyListing.Active) never disagrees with what is sent.
    /// </summary>
    [Fact]
    public void The_active_mark_is_the_key_SecretStore_uses()
    {
        const string variable = SecretStore.OpenAiKey;
        StoredKey[] keys = [new("aaaaaaaaaaaa", variable, "First", null), new("bbbbbbbbbbbb", variable, "Second", null)];
        foreach (var env in new[] { false, true })
        foreach (var file in new[] { false, true })
        foreach (var storedCount in new[] { 0, 1, 2 })
        foreach (var choice in new string?[] { null, StoredKeyId.Environment, StoredKeyId.File, "aaaaaaaaaaaa", "bbbbbbbbbbbb" })
        {
            var stored = keys.Take(storedCount).Select((k, i) => (k, $"value-{i}")).ToList();
            var entries = stored.Select(s => new KeyEntry(KeySource.Stored, s.k.Id, s.k.Label, null)).ToList();
            if (env) entries.Add(new KeyEntry(KeySource.Environment, null, variable, null));
            if (file) entries.Add(new KeyEntry(KeySource.File, null, variable, null));
            var active = choice is null ? null : new Dictionary<string, string> { [variable] = choice };
            var secrets = SecretStore.FromContents(file ? $"{variable}=from-file\n" : null, null, v => env && v == variable ? "from-env" : null, active, stored);
            var expected = secrets.Describe(variable) switch
            {
                "present (environment variable)" => KeySource.Environment.ToString(),
                "present (config-folder .env)" => KeySource.File.ToString(),
                var d when d.StartsWith("present (stored key \"", StringComparison.Ordinal) => d["present (stored key \"".Length..^2],
                var d when d.StartsWith("absent", StringComparison.Ordinal) => "none",
                var d => throw new InvalidOperationException(d),
            };
            var mark = new KeyListing(entries, null).Active(choice) switch
            {
                null => "none",
                { Source: KeySource.Stored } e => e.Label,
                var e => e.Source.ToString(),
            };
            Assert.True(expected == mark, $"env {env}, file {file}, stored {storedCount}, choice {choice ?? "none"}: SecretStore uses {expected}, the list marks {mark}");
        }
    }

    // ---- 4. A store that cannot be read leaves the page usable -------------------------------------------------------------------

    /// <summary>
    /// Pin (mutation-checked): when List throws CredentialStoreException the panel says the PROPOSED "could not be read" line with the
    /// store's reason, still lists the environment variable as the key in use, and still offers Add a key….
    /// </summary>
    [Fact]
    public Task A_store_that_cannot_be_read_leaves_the_list_usable() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}} }""");
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            var vault = new FakeVault { ListFails = new CredentialStoreException("CredEnumerate failed: refused (5).", 5) };
            var panel = new KeysPanel(shell, vault, _ => { }, v => v == SecretStore.GroqKey ? "from-the-environment" : null);
            var host = new Window { Content = panel.View };
            host.Show();
            panel.Show(SecretStore.GroqKey, "Groq");
            Assert.Contains("Your stored keys could not be read from Test store (CredEnumerate failed: refused (5)). Keys in the key file and environment variables still work.", Exposed(host));
            Assert.Contains("Environment variable", Exposed(host));
            Assert.Single(All<TextBlock>(host), t => t.IsEffectivelyVisible && t.Text == "Active");
            ButtonNamed(host, "Add a key…");
            host.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>The same on the real page: Settings > Clean-up draws, its cloud card works, and the line says why the keys are missing.</summary>
    [Fact]
    public Task The_clean_up_page_still_draws_when_the_store_cannot_be_read() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}} }""");
        var (shell, platform, _) = scratch.Shell();
        try
        {
            platform.FakeCredentials.ListFails = new CredentialStoreException("CredEnumerate failed: refused (5).", 5);
            shell.Reload(userRequested: false);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            var lines = Exposed(window);
            Assert.Contains(KeysPanel.CouldNotRead("Test store", "CredEnumerate failed: refused (5)."), lines);
            ButtonNamed(window, "Use cloud clean-up…");
            ButtonNamed(window, "Add a key…");
            window.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Where the platform has no credential store, Add a key… is not offered and the PROPOSED line sits in its place.</summary>
    [Fact]
    public Task Without_a_credential_store_Add_a_key_is_not_offered() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}} }""");
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            var panel = new KeysPanel(shell, new FakeVault { Available = false }, _ => { }, _ => null);
            var host = new Window { Content = panel.View };
            host.Show();
            panel.Show(SecretStore.AnthropicKey, "Anthropic");
            Assert.DoesNotContain(All<Button>(host), b => b.IsEffectivelyVisible && b.Content as string == "Add a key…");
            Assert.Contains(All<TextBlock>(host), t => t.IsEffectivelyVisible && t.Text == KeysPanel.NoStoreLine);
            host.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    // ---- 5. Check this key ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pin (mutation-checked): without the consent step for the host on record there is no Check this key; with it, the button is there.
    /// A consent for one host never offers the check for another service's key.
    /// </summary>
    [Fact]
    public Task Check_this_key_is_absent_without_the_consent_on_record() => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ {{Base}}{{OpenAiCleanup}} }""");
        var (shell, _, _) = scratch.Shell(providerHandler: new RecordingHandler());
        try
        {
            shell.Vault.Add(SecretStore.OpenAiKey, "Work", Key);
            shell.Reload(userRequested: false);
            var panel = new KeysPanel(shell, shell.Vault, _ => { }, _ => null);
            var host = new Window { Content = panel.View };
            host.Show();
            panel.Show(SecretStore.OpenAiKey, "OpenAI");
            Assert.DoesNotContain(All<Button>(host), b => b.Content as string == "Check this key");
            Assert.Null(KeyCheck.Target(SecretStore.OpenAiKey, shell.Settings));

            CleanupConsent(scratch, "api.openai.com");
            shell.Reload(userRequested: false);
            panel.Show(SecretStore.OpenAiKey, "OpenAI");
            ButtonNamed(host, "Check Work");
            Assert.Null(KeyCheck.Target(SecretStore.GeminiKey, shell.Settings)); // that consent names api.openai.com only
            Assert.Null(KeyCheck.Target(SecretStore.AnthropicKey, shell.Settings));
            host.Close();
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// Pin (mutation-checked): one click sends one GET with no body to the consented preset's own host and path, with the key in the
    /// preset's header and nothing the user wrote (a dictionary word, own instructions) anywhere in it; a refusal is reported with its
    /// status code and the service's message, key-like tokens hidden; offline only sends nothing.
    /// </summary>
    [Fact]
    public Task Check_this_key_sends_only_to_the_presets_host_and_carries_no_user_text() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch($$"""{ {{Base}}{{GeminiCleanup}} }""");
        File.WriteAllText(Path.Combine(scratch.Paths.ConfigDir, "dictionary.txt"), "Parramatta\n");
        CleanupConsent(scratch, "generativelanguage.googleapis.com");
        var handler = new RecordingHandler();
        var (shell, _, _) = scratch.Shell(providerHandler: handler);
        try
        {
            var id = shell.Vault.Add(SecretStore.GeminiKey, "Work", Key);
            shell.Vault.Add(SecretStore.OpenAiKey, "Other", "sk-other-0000111122223333"); // another service's key: never sent here
            shell.Reload(userRequested: false);

            Assert.Equal("The key works.", await shell.CheckKeyAsync(SecretStore.GeminiKey, id, TestContext.Current.CancellationToken));
            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/models", request.Uri);
            Assert.False(request.HadBody);
            Assert.Equal("Bearer " + Key, request.Authorization);
            Assert.DoesNotContain("Parramatta", request.Everything);
            Assert.DoesNotContain("sk-other", request.Everything);

            handler.Next = (HttpStatusCode.BadRequest, """[{"error": {"code": 400, "message": "API key not valid. Please pass a valid API key. Got Zq81****Yb5N instead.", "status": "INVALID_ARGUMENT"}}]""");
            var refused = await shell.CheckKeyAsync(SecretStore.GeminiKey, id, TestContext.Current.CancellationToken);
            Assert.StartsWith("The key did not work: generativelanguage.googleapis.com answered 400 Bad Request. generativelanguage.googleapis.com says: API key not valid.", refused);
            Assert.Contains("[hidden]", refused);
            Assert.False(HoldsPartOfKey(refused), refused);

            File.WriteAllText(scratch.Paths.SettingsFile, $$"""{ {{Base}}{{GeminiCleanup}}, "offline_only": true }""");
            shell.Reload(userRequested: false);
            Assert.Equal("Offline only is on, so the key was not checked.", await shell.CheckKeyAsync(SecretStore.GeminiKey, id, TestContext.Current.CancellationToken));
            Assert.Equal(2, handler.Requests.Count); // nothing more was sent
            return true;
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Each service's check, by the consent on record: its own host and path, and its own header (Azure's api-key, Anthropic's version).</summary>
    [Fact]
    public void Each_service_is_checked_at_its_own_address_with_its_own_header()
    {
        var anthropic = new AppSettings { Llm = new LlmSettings { Provider = LlmSettings.Anthropic, CloudConsent = "api.anthropic.com" } };
        var a = KeyCheck.Target(SecretStore.AnthropicKey, anthropic)!;
        Assert.Equal(("https://api.anthropic.com/v1/models", KeyCheckAuth.Anthropic), (a.Url.AbsoluteUri, a.Auth));

        var azure = new AppSettings { Speech = new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = "https://contoso.openai.azure.com/openai/v1", Model = "whisper", Consent = "contoso.openai.azure.com" } };
        var z = KeyCheck.Target(SecretStore.AzureOpenAiKey, azure)!;
        Assert.Equal(("https://contoso.openai.azure.com/openai/v1/models", KeyCheckAuth.ApiKeyHeader), (z.Url.AbsoluteUri, z.Auth));
        Assert.Null(KeyCheck.Target(SecretStore.SpeechKey, azure)); // the custom server's key never goes to Azure

        var groq = new AppSettings { Speech = new SpeechSettings { Provider = SpeechSettings.OpenAiCompatible, BaseUrl = "https://api.groq.com/openai/v1", Model = "whisper", Consent = "api.groq.com" } };
        Assert.Equal("https://api.groq.com/openai/v1/models", KeyCheck.Target(SecretStore.GroqKey, groq)!.Url.AbsoluteUri);

        var plain = new AppSettings { Llm = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://llm.example.net/v1", Model = "m", CloudConsent = "llm.example.net" } };
        Assert.Null(KeyCheck.Target(SecretStore.LlmKey, plain)); // a plain http:// address off this PC is never offered
        var noConsent = groq with { Speech = groq.Speech with { Consent = null } };
        Assert.Null(KeyCheck.Target(SecretStore.GroqKey, noConsent));
    }

    /// <summary>A service's message loses every key-like token; ordinary words stay.</summary>
    [Fact]
    public void A_services_message_is_shown_with_key_like_tokens_hidden()
    {
        Assert.Equal("Incorrect API key provided: [hidden] You can find your API key at https://platform.openai.com/account/api-keys.",
            KeyCheck.Redact("Incorrect API key provided: Zq81****************Yb5N. You can find your API key at https://platform.openai.com/account/api-keys.", Key));
        Assert.Equal("Request [hidden] failed", KeyCheck.Redact("Request req_011CZkZaBF1tNoB5wlCeusgy failed", Key)); // a long token of any kind
        Assert.Equal("Bad [hidden] here", KeyCheck.Redact("Bad " + Key + " here", Key));
        Assert.Equal("Invalid bearer token", KeyCheck.Redact("Invalid bearer token", Key));
        Assert.Null(KeyCheck.ProviderMessage("<html>502 Bad Gateway</html>"));
        Assert.Equal("invalid x-api-key", KeyCheck.ProviderMessage("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
    }

    // ---- 6. The full-template key file -------------------------------------------------------------------------------------------

    /// <summary>
    /// Pin (mutation-checked): "Create the key file…" writes the APPROVED header first, byte for byte (written out here, not read from the
    /// app), then the repository's .env.example in full, every empty key line holding the placeholder; every variable is in it once, and
    /// none counts as set.
    /// </summary>
    [Fact]
    public void The_key_file_is_the_full_template_with_the_approved_header_first()
    {
        const string approvedHeader =
            "# Dictum keys, created by Settings > Clean-up. Replace your-key-here with your key and save this file: Dictum reads it again by itself.\n" +
            "# A real environment variable of the same name wins over this file. Keys are never written to settings.json or the log.\n";
        var example = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), ".env.example")).Replace("\r\n", "\n");
        var expected = approvedHeader + "\n" + Regex.Replace(example, @"^([A-Z_]+)=$", "$1=your-key-here", RegexOptions.Multiline);
        var text = KeyFile.Contents();
        Assert.Equal(Encoding.UTF8.GetBytes(approvedHeader), Encoding.UTF8.GetBytes(text)[..Encoding.UTF8.GetByteCount(approvedHeader)]);
        Assert.Equal(expected, text);
        foreach (var variable in SecretStore.KeyNames)
        {
            Assert.Single(text.Split('\n'), l => l == variable + "=your-key-here");
            Assert.Null(SecretStore.FromContents(text, null, _ => null).Get(variable));
        }
        Assert.Contains("# The OpenAI preset (Settings > Clean-up). Sent to api.openai.com only.", text); // its comments, in its words
    }

    // ---- Fakes --------------------------------------------------------------------------------------------------------------------

    /// <summary>A vault that records what it is given; List can be made to throw as a store that cannot be read.</summary>
    private sealed class FakeVault : ISecretVault
    {
        private readonly List<StoredKey> _keys = [];

        public bool Available { get; set; } = true;

        public string? StoreName => Available ? "Test store" : null;

        public CredentialStoreException? ListFails { get; set; }

        public List<(string Variable, string Label, string Value)> Added { get; } = [];

        public string Add(string variable, string label, string value)
        {
            Added.Add((variable, label, value));
            var id = StoredKeyId.New();
            _keys.Add(new StoredKey(id, variable, StoredKey.CleanLabel(label), new DateOnly(2026, 11, 4)));
            Changed?.Invoke();
            return id;
        }

        public IReadOnlyList<StoredKey> List(string variable) => ListFails is { } fails ? throw fails : _keys.Where(k => k.Variable == variable).ToList();

        public string? Get(string id) => null;

        public bool Remove(string id) => _keys.RemoveAll(k => k.Id == id) > 0;

        public int RemoveAll()
        {
            var n = _keys.Count;
            _keys.Clear();
            return n;
        }

        public event Action? Changed;
    }

    private sealed record SeenRequest(HttpMethod Method, string Uri, bool HadBody, string? Authorization, string Everything);

    /// <summary>The network for the provider client: records each request, answers 200 or what <see cref="Next"/> says.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        public (HttpStatusCode Status, string Body)? Next { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = string.Join("\n", request.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
            Requests.Add(new SeenRequest(request.Method, request.RequestUri!.AbsoluteUri, request.Content is not null,
                request.Headers.Authorization?.ToString(), request.RequestUri.AbsoluteUri + "\n" + headers));
            var (status, body) = Next ?? (HttpStatusCode.OK, """{"object":"list","data":[]}""");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
