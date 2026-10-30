using System.Text.Json.Nodes;
using Dictum.App;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;

namespace Dictum.Tests;

/// <summary>A credential store in memory: what the Windows and macOS stores do, without touching either.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<(string Variable, string Id), string> _entries = [];

    public bool Available { get; set; } = true;
    public string? Name => Available ? "Test store" : null;

    /// <summary>The next write throws, as the system refusing.</summary>
    public bool RefuseWrites { get; set; }

    public IReadOnlyDictionary<(string Variable, string Id), string> Entries => _entries;

    public void Write(string variable, string id, string value)
    {
        if (!Available || RefuseWrites) throw new CredentialStoreException("CredWrite failed: refused (5).");
        _entries[(variable, id)] = value;
    }

    public string? Read(string variable, string id) => _entries.TryGetValue((variable, id), out var v) ? v : null;

    public bool Delete(string variable, string id) => _entries.Remove((variable, id));

    public IReadOnlyList<(string Variable, string Id)> List() => [.. _entries.Keys];
}

/// <summary>
/// API keys, Core (30/10/2026, .demo-preview/design-api-keys.md §3): several keys per service in the system's credential store,
/// labels in keys.json, the choice in settings.json's keys.active, and never a value in either file.
/// </summary>
public class SecretVaultTests
{
    private const string Value = "sk-test-0123456789abcdefXYZ";

    private static (Scratch Scratch, FakeCredentialStore Store, SecretVault Vault) Make(string? settingsJson = null)
    {
        var scratch = new Scratch(settingsJson);
        var store = new FakeCredentialStore();
        return (scratch, store, new SecretVault(scratch.Paths, store, () => new DateOnly(2026, 10, 30)));
    }

    [Fact]
    public void A_key_added_is_listed_by_its_label_and_read_back_by_its_id()
    {
        var (scratch, store, vault) = Make();
        using var owned = scratch;
        var id = vault.Add(SecretStore.OpenAiKey, "  Work  ", Value);
        Assert.True(StoredKeyId.IsValid(id));
        Assert.Equal(Value, store.Read(SecretStore.OpenAiKey, id));
        var listed = Assert.Single(vault.List(SecretStore.OpenAiKey));
        Assert.Equal(new StoredKey(id, SecretStore.OpenAiKey, "Work", new DateOnly(2026, 10, 30)), listed);
        Assert.Empty(vault.List(SecretStore.GeminiKey));
        Assert.Equal(Value, vault.Get(id));
        Assert.True(vault.Remove(id));
        Assert.Empty(vault.List(SecretStore.OpenAiKey));
        Assert.Null(vault.Get(id));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Neither_keys_json_nor_settings_json_ever_holds_a_value()
    {
        // Brief §3: "Neither ever holds a value." Every write path the vault has runs here, then both files are searched for the
        // key, and for every eight-character piece of it.
        var (scratch, _, vault) = Make("""{ "hotkey": "F24" }""");
        using var owned = scratch;
        var first = vault.Add(SecretStore.OpenAiKey, "Work", Value);
        var second = vault.Add(SecretStore.OpenAiKey, "Home", Value + "-2");
        Assert.True(SecretVault.SetActive(scratch.Paths, SecretStore.OpenAiKey, second, out _));
        vault.Remove(first);
        vault.Add(SecretStore.GroqKey, "Groq", Value + "-groq");
        foreach (var file in new[] { scratch.Paths.KeysFile, scratch.Paths.SettingsFile })
        {
            var text = File.ReadAllText(file);
            for (var i = 0; i + 8 <= Value.Length; i++) Assert.DoesNotContain(Value.Substring(i, 8), text, StringComparison.Ordinal);
        }
        Assert.Contains(second, File.ReadAllText(scratch.Paths.SettingsFile), StringComparison.Ordinal); // the choice is there, as an id
        Assert.Contains("\"Home\"", File.ReadAllText(scratch.Paths.KeysFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_are_trimmed_cut_to_60_characters_and_lose_their_control_characters()
    {
        Assert.Equal("Work key", StoredKey.CleanLabel("\tWork\u0007 key\r\n "));
        Assert.Equal(new string('a', 60), StoredKey.CleanLabel(new string('a', 75)));
        Assert.Equal(new string('a', 59), StoredKey.CleanLabel(new string('a', 59) + "\U0001F600" + "b")); // never through a surrogate pair
        var (scratch, _, vault) = Make();
        using var owned = scratch;
        var id = vault.Add(SecretStore.OpenAiKey, new string('x', 80), Value);
        Assert.Equal(60, Assert.Single(vault.List(SecretStore.OpenAiKey), k => k.Id == id).Label.Length);
    }

    [Theory]
    [InlineData("NOT_A_KEY", "Work", Value)] // unknown variable
    [InlineData("OPENAI_API_KEY", "  \u0001 ", Value)] // empty label
    [InlineData("OPENAI_API_KEY", "Work", "  ")] // blank key
    [InlineData("OPENAI_API_KEY", "Work", "your-key-here")] // the placeholder
    [InlineData("OPENAI_API_KEY", "Work", "sk-abc\ndef-123456")] // a line break inside
    [InlineData("OPENAI_API_KEY", "my sk-test-0123 key", Value)] // the label holds part of the key
    public void An_add_that_could_not_work_or_would_show_the_key_is_refused_and_stores_nothing(string variable, string label, string value)
    {
        var (scratch, store, vault) = Make();
        using var owned = scratch;
        var ex = Assert.Throws<ArgumentException>(() => vault.Add(variable, label, value));
        if (value.Trim().Length >= 6) Assert.DoesNotContain(value.Trim(), ex.Message, StringComparison.Ordinal);
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(scratch.Paths.KeysFile));
    }

    [Fact]
    public void A_key_longer_than_the_store_holds_is_refused()
    {
        var (scratch, store, vault) = Make();
        using var owned = scratch;
        Assert.Throws<ArgumentException>(() => vault.Add(SecretStore.OpenAiKey, "Long", new string('k', SecretVault.ValueMaxBytes + 1)));
        vault.Add(SecretStore.OpenAiKey, "Longest", new string('k', SecretVault.ValueMaxBytes));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void A_refused_write_leaves_no_label_and_a_failed_label_write_leaves_no_key()
    {
        var (scratch, store, vault) = Make();
        using var owned = scratch;
        store.RefuseWrites = true;
        var refused = Assert.Throws<CredentialStoreException>(() => vault.Add(SecretStore.OpenAiKey, "Work", Value));
        Assert.DoesNotContain(Value, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(scratch.Paths.KeysFile));
        store.RefuseWrites = false;
        Directory.CreateDirectory(scratch.Paths.KeysFile); // keys.json cannot be written: a folder is in its place
        Assert.True(Record.Exception(() => vault.Add(SecretStore.OpenAiKey, "Work", Value)) is IOException or UnauthorizedAccessException);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void A_keys_json_that_cannot_be_read_is_never_written_over()
    {
        var (scratch, store, vault) = Make();
        using var owned = scratch;
        File.WriteAllText(scratch.Paths.KeysFile, "{ not json");
        var ex = Assert.Throws<IOException>(() => vault.Add(SecretStore.OpenAiKey, "Work", Value));
        Assert.Equal(KeysFile.Unreadable, ex.Message);
        Assert.Equal("{ not json", File.ReadAllText(scratch.Paths.KeysFile));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void A_key_the_store_holds_but_keys_json_does_not_list_is_still_listed_without_a_label()
    {
        var (scratch, store, vault) = Make();
        using var owned = scratch;
        var id = vault.Add(SecretStore.GeminiKey, "Gemini", Value);
        File.Delete(scratch.Paths.KeysFile);
        Assert.Equal(new StoredKey(id, SecretStore.GeminiKey, "", null), Assert.Single(vault.List(SecretStore.GeminiKey)));
        File.WriteAllText(scratch.Paths.KeysFile, """{"keys":[{"id":"0123456789ab","variable":"GEMINI_API_KEY","label":"Gone","added":"2026-10-01"}]}""");
        Assert.DoesNotContain(vault.List(SecretStore.GeminiKey), k => k.Id == "0123456789ab"); // listed but not stored: not a key
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Removing_a_key_clears_the_choice_that_named_it_and_remove_all_clears_every_stored_choice()
    {
        var (scratch, store, vault) = Make("""{ "hotkey": "F24" }""");
        using var owned = scratch;
        var a = vault.Add(SecretStore.OpenAiKey, "A", Value);
        var b = vault.Add(SecretStore.GeminiKey, "B", Value + "b");
        SecretVault.SetActive(scratch.Paths, SecretStore.OpenAiKey, a, out _);
        SecretVault.SetActive(scratch.Paths, SecretStore.GeminiKey, b, out _);
        SecretVault.SetActive(scratch.Paths, SecretStore.GroqKey, StoredKeyId.Environment, out _);
        vault.Remove(a);
        var active = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Keys.Active;
        Assert.False(active.ContainsKey(SecretStore.OpenAiKey));
        Assert.Equal(b, active[SecretStore.GeminiKey]);
        Assert.Equal(1, vault.RemoveAll());
        active = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Keys.Active;
        Assert.Equal(new Dictionary<string, string> { [SecretStore.GroqKey] = StoredKeyId.Environment }, active); // "env" is not a stored key: kept
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(scratch.Paths.KeysFile));
    }

    [Fact]
    public void An_unavailable_store_refuses_an_add_and_lists_nothing()
    {
        using var scratch = new Scratch();
        var vault = SecretVault.Unavailable(scratch.Paths);
        Assert.False(vault.Available);
        Assert.Null(vault.StoreName);
        Assert.Throws<CredentialStoreException>(() => vault.Add(SecretStore.OpenAiKey, "Work", Value));
        Assert.Empty(vault.List(SecretStore.OpenAiKey));
        Assert.Empty(vault.Snapshot(out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Keys_active_names_only_keys_Dictum_reads_and_a_bad_value_is_never_echoed_into_the_log()
    {
        var load = SettingsLoader.Parse($$"""
            { "keys": { "active": {
                "OPENAI_API_KEY": "0123456789ab",
                "GEMINI_API_KEY": "env",
                "GROQ_API_KEY": "file",
                "ANTHROPIC_API_KEY": "{{Value}}",
                "NOT_A_KEY": "env",
                "SPEECH_API_KEY": null } } }
            """);
        Assert.True(load.Usable);
        Assert.Equal(new Dictionary<string, string> { ["OPENAI_API_KEY"] = "0123456789ab", ["GEMINI_API_KEY"] = "env", ["GROQ_API_KEY"] = "file" }, load.Settings.Keys.Active);
        Assert.Equal(2, load.Warnings.Count);
        Assert.All(load.Warnings, w => Assert.DoesNotContain("sk-test", w, StringComparison.Ordinal)); // a key pasted there by mistake stays out of the log
        Assert.Contains(load.Warnings, w => w.Contains("keys.active.ANTHROPIC_API_KEY", StringComparison.Ordinal));
        Assert.Contains(load.Warnings, w => w.Contains("keys.active.NOT_A_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_change_to_keys_json_reloads_as_settings_json_does()
    {
        Assert.True(AppRules.ReloadsOn(WatcherChangeTypes.Changed, "keys.json", null));
        Assert.True(AppRules.ReloadsOn(WatcherChangeTypes.Created, "keys.json", null));
        Assert.True(AppRules.ReloadsOn(WatcherChangeTypes.Deleted, "keys.json", null));
        Assert.True(AppRules.ReloadsOn(WatcherChangeTypes.Renamed, "keys.json.bak", "keys.json"));
        Assert.False(AppRules.ReloadsOn(WatcherChangeTypes.Changed, "keys.json.tmp", null));
    }

    [Fact]
    public void Keys_json_is_in_the_path_registry_with_the_keys_so_only_full_removal_deletes_it()
    {
        using var scratch = new Scratch();
        var registry = PathRegistry.Locations(scratch.Paths, new AppSettings());
        Assert.Contains(registry, l => l.Path == scratch.Paths.KeysFile && l.Category == DataCategory.Secrets);
        Assert.Contains(registry, l => l.Path == scratch.Paths.KeysFile + ".tmp" && l.Category == DataCategory.Secrets);
        new SecretVault(scratch.Paths, new FakeCredentialStore()).Add(SecretStore.OpenAiKey, "Work", Value);
        Assert.DoesNotContain(RemovalPlan.Build(scratch.Paths, new AppSettings(), RemovalScope.AllDataIncludingSettings).Items, i => i.Path == scratch.Paths.KeysFile);
        Assert.Contains(RemovalPlan.Build(scratch.Paths, new AppSettings(), RemovalScope.Everything).Items, i => i.Path == scratch.Paths.KeysFile);
    }

    [Fact]
    public Task Remove_Dictum_completely_also_removes_every_key_stored_in_the_credential_store() => UiSession.Run(() =>
    {
        // SPEC §17: afterwards the only trace is the temp folder. The keys are outside every folder the plan lists.
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, dialogs) = scratch.Shell();
        shell.Vault.Add(SecretStore.OpenAiKey, "Work", Value);
        shell.Vault.Add(SecretStore.GroqKey, "Groq", Value + "-groq");
        dialogs.Answers.Enqueue(0);
        dialogs.ConfirmWord = true;
        Assert.True(shell.RemoveEverything());
        Assert.Empty(platform.FakeCredentials.Entries);
        Assert.False(File.Exists(scratch.Paths.KeysFile));
        Assert.False(File.Exists(scratch.Paths.SettingsFile));
    });

    [Fact]
    public void The_active_choice_writer_refuses_anything_but_an_id_or_a_source()
    {
        using var scratch = new Scratch("{}");
        Assert.Throws<ArgumentException>(() => SecretVault.SetActive(scratch.Paths, SecretStore.OpenAiKey, Value, out _));
        Assert.Throws<ArgumentException>(() => SecretVault.SetActive(scratch.Paths, "NOT_A_KEY", "env", out _));
        Assert.Equal("{}", File.ReadAllText(scratch.Paths.SettingsFile));
        Assert.True(SecretVault.SetActive(scratch.Paths, SecretStore.OpenAiKey, StoredKeyId.File, out _));
        Assert.Equal("file", JsonNode.Parse(File.ReadAllText(scratch.Paths.SettingsFile))!["keys"]!["active"]![SecretStore.OpenAiKey]!.GetValue<string>());
    }
}
