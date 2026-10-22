using Dictum.Llm;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Shell;

namespace Dictum.Tests;

/// <summary>The shell's own decisions with scripted answers: cloud consent (SPEC §22), the removal flow (§17), Report a problem.</summary>
public class ShellTests
{
    [Fact]
    public Task Cloud_clean_up_is_switched_on_only_through_the_consent_dialog() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        var anthropic = new LlmSettings { Provider = LlmSettings.Anthropic, Model = LlmSettings.AnthropicDefaultModel };

        dialogs.Answers.Enqueue(1); // Cancel
        Assert.Contains("not switched on", shell.EnableCloud(anthropic));
        Assert.Equal("Send dictations to a cloud service?", dialogs.Asked.Single().Title);
        Assert.Contains("api.anthropic.com", dialogs.Asked.Single().Text);
        Assert.Contains("(the audio only if you also turn on cloud speech)", dialogs.Asked.Single().Text); // owner, 22/10/2026 18:41 AEDT
        Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);

        dialogs.Answers.Enqueue(0); // Allow
        Assert.Null(shell.EnableCloud(anthropic));
        var saved = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm;
        Assert.Equal(LlmSettings.Anthropic, saved.Provider);
        var consent = CloudConsentFile.Read(scratch.Paths.ConsentFile)!; // recorded by the dialog, not in settings.json
        Assert.Equal(("anthropic", "api.anthropic.com"), (consent.Provider, consent.Host));
        Assert.DoesNotContain("cloud_consent", File.ReadAllText(scratch.Paths.SettingsFile));

        shell.DisableCloud();
        saved = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm;
        Assert.Equal(LlmSettings.Local, saved.Provider);
        Assert.False(File.Exists(scratch.Paths.ConsentFile)); // the consent goes with it
        shell.Dispose();
    });

    [Fact]
    public Task Plain_http_off_the_PC_is_refused_and_a_local_server_needs_no_consent() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        var refused = shell.EnableCloud(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://example.com/v1", Model = "m" });
        Assert.Contains("http://", refused);
        Assert.Empty(dialogs.Asked); // refused before any question
        Assert.Null(shell.EnableCloud(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://localhost:11434/v1", Model = "m" }));
        Assert.Empty(dialogs.Asked); // nothing leaves the PC: no consent needed
        Assert.Equal(LlmSettings.OpenAiCompatible, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
        shell.Dispose();
    });

    [Fact]
    public Task Offline_only_refuses_cloud_clean_up() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "offline_only": true }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Contains("offline_only", shell.EnableCloud(new LlmSettings { Provider = LlmSettings.Anthropic, Model = "m" }));
        Assert.Empty(dialogs.Asked);
        shell.Dispose();
    });

    private static void Populate(AppPaths paths)
    {
        File.WriteAllText(paths.DictionaryFile, "Kynni\n");
        File.WriteAllText(paths.EnvFile, "LLM_API_KEY=x\n");
        Directory.CreateDirectory(paths.DefaultModelsDir);
        File.WriteAllBytes(Path.Combine(paths.DefaultModelsDir, "ggml-tiny.en.bin"), new byte[2048]);
        Directory.CreateDirectory(paths.LogsDir);
        File.WriteAllText(Path.Combine(paths.LogsDir, "dictum-20261016.log"), "log");
    }

    [Fact]
    public Task Remove_everything_warns_twice_lists_every_path_and_needs_DELETE() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        Populate(scratch.Paths);
        var (shell, platform, dialogs) = scratch.Shell();
        var leftover = Path.GetFullPath(Path.Combine(platform.LegacyFolders().ConfigDir, ".env")); // a key the migration skipped (SPEC §21, amended)
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "ANTHROPIC_API_KEY=sk-ant-left-behind\n");

        dialogs.Answers.Enqueue(1); // Cancel at the first warning
        Assert.False(shell.RemoveEverything());
        var first = dialogs.Asked.Single().Text;
        foreach (var path in new[] { scratch.Paths.SettingsFile, scratch.Paths.DictionaryFile, scratch.Paths.EnvFile, Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin") })
            Assert.Contains(path, first);
        Assert.Contains("Total:", first);
        Assert.Contains(leftover, first); // Dictate's leftover key is listed with the rest
        Assert.True(File.Exists(scratch.Paths.EnvFile));

        dialogs.Answers.Enqueue(0); // Continue, then the word is not typed
        dialogs.ConfirmWord = false;
        Assert.False(shell.RemoveEverything());
        Assert.Contains("no undo", dialogs.Asked.Last().Text);
        Assert.True(File.Exists(scratch.Paths.SettingsFile));
        Assert.Empty(platform.FakeRemoval.Steps);

        dialogs.Answers.Enqueue(0);
        dialogs.ConfirmWord = true;
        Assert.True(shell.RemoveEverything());
        Assert.False(Directory.Exists(scratch.Paths.ConfigDir));
        Assert.False(Directory.Exists(scratch.Paths.LocalDir));
        Assert.Equal(["shortcut and startup", $"program folder {scratch.Paths.ExeDir}"], platform.FakeRemoval.Steps);
        Assert.False(File.Exists(leftover)); // and deleted with it
    });

    [Fact]
    public Task A_packaged_build_hands_its_own_removal_to_Windows() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        Populate(scratch.Paths);
        var (shell, platform, dialogs) = scratch.Shell();
        platform.FakePackage.IsPackaged = true;
        dialogs.Answers.Enqueue(0);
        dialogs.ConfirmWord = true;
        Assert.True(shell.RemoveEverything());
        Assert.Contains("Settings > Apps", dialogs.Asked.First().Text);
        Assert.Equal(["shortcut and startup", "uninstall settings"], platform.FakeRemoval.Steps);
        Assert.False(File.Exists(scratch.Paths.DictionaryFile));
    });

    [Fact]
    public Task Delete_logs_asks_once_and_says_what_it_freed() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        Populate(scratch.Paths);
        var (shell, _, dialogs) = scratch.Shell();
        dialogs.Answers.Enqueue(1);
        Assert.Equal("Nothing was deleted.", shell.DeleteData(RemovalScope.Logs));
        dialogs.Answers.Enqueue(0);
        Assert.StartsWith("Deleted ", shell.DeleteData(RemovalScope.Logs));
        Assert.Empty(Directory.GetFiles(scratch.Paths.LogsDir));
        Assert.True(File.Exists(Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-tiny.en.bin")));
        Assert.Equal("There is nothing to delete.", shell.DeleteData(RemovalScope.Logs));
        shell.Dispose();
    });

    [Fact]
    public Task Report_a_problem_opens_a_prefilled_email_without_any_dictation() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Personal.Remember("the secret merger figures", 42, "Outlook");
        shell.ReportProblem();
        var uri = Assert.Single(platform.Opened);
        Assert.StartsWith("mailto:hello@kynni.com.au?subject=", uri);
        Assert.Contains("problem%20report", uri);
        Assert.Contains("Test", Uri.UnescapeDataString(uri)); // the platform, so the report says where it happened
        Assert.DoesNotContain("secret", Uri.UnescapeDataString(uri));
        Assert.DoesNotContain(shell.Status.Models.Split(" · ")[^1], Uri.UnescapeDataString(uri)); // the hotkey (as the header shows it) is not part of "version and models"
        shell.Dispose();
    });
}
