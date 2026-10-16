using System.Text.Json.Nodes;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;

namespace Dictum.Tests;

// Lens on f69457d (16/10/2026): what the session holds is saved to the files after every reload and once at quit, and each
// hold ends once its files keep it (AppShell.SaveSessionChoices).
public partial class CloudOffTakesEffectTests
{
    /// <summary>The host of <see cref="CloudSettings"/>, for a shell that starts: whisper.model named, so no first-run picker asks.</summary>
    private const string StartingCloudSettings = """
        { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false },
          "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" } }
        """;

    /// <summary>
    /// Waits, letting the UI thread's loop run (posts and timers, such as the reload debounce), until <paramref name="condition"/>
    /// holds or 5 s have passed.
    /// </summary>
    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>
    /// Waits, letting the UI thread's loop run, until <paramref name="count"/> has stood still for 1.5 s (three reload debounces),
    /// or 10 s have passed: true when it stood still.
    /// </summary>
    private static async Task<bool> QuietAsync(Func<int> count)
    {
        var until = Environment.TickCount64 + 10_000;
        var (last, since) = (count(), Environment.TickCount64);
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(20);
            var now = count();
            if (now != last) (last, since) = (now, Environment.TickCount64);
            else if (Environment.TickCount64 - since >= 1_500) return true;
        }
        return false;
    }

    /// <summary>
    /// A started shell (the folder watcher on) whose fake speech model loads at once, so the start asks nothing. The settings
    /// must name whisper.model, or the first-run picker would ask.
    /// </summary>
    private static AppShell StartedShell(Scratch scratch, ILog log)
    {
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, _, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model), log: log);
        Assert.True(shell.Start(lifetime: null));
        return shell;
    }

    [Theory]
    [InlineData("consent.json held open, settings.json read-only", false)] // then Settings > About > Reload settings
    [InlineData("consent.json held open, settings.json read-only", true)] // then Quit
    [InlineData("consent.json held open", false)]
    [InlineData("consent.json held open", true)]
    public Task Saving_the_switch_off_is_tried_again_at_a_reload_and_at_quit_until_both_files_keep_it(string mode, bool atQuit) => UiSession.Run(() =>
    {
        // Lens on f69457d (finding 1): the switch-off was kept in memory only, and nothing tried the files again. Both read-only,
        // read-only cleared, About > Reload: consent.json was still there and settings.json still named the host, so the next start
        // sent again. consent.json alone read-only: settings.json was saved, but the consent stayed on file, so a later hand edit
        // back to the host was honoured at the next start with no consent dialog. Since the lens on eaa3a04 a read-only consent.json
        // is deleted at once (CloudConsentFile.Delete), so another program holding it open is what keeps it here.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        FileStream? held = null;
        try
        {
            shell.Reload(userRequested: false);
            held = HoldOpen(scratch.Paths.ConsentFile);
            if (mode != "consent.json held open") File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Assert.NotNull(shell.DisableCloud());
            shell.Reload(userRequested: true); // tried again with nothing changed yet: not saved, still held
            Assert.True(File.Exists(scratch.Paths.ConsentFile));
            AssertLocal(shell);

            held.Dispose(); // let go
            held = null;
            Normal(scratch.Paths.SettingsFile);
            if (atQuit) shell.Quit();
            else shell.Reload(userRequested: true);
            Assert.False(File.Exists(scratch.Paths.ConsentFile)); // the consent is withdrawn on disk too
            var saved = SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm;
            Assert.Equal((LlmSettings.Local, CleanupCatalog.Recommend(false).Model), (saved.Provider, saved.Model));

            // The next start, after a hand edit back to the host: no consent is on file, so nothing is sent there.
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            var (next, _, _) = scratch.Shell();
            try
            {
                next.Reload(userRequested: false);
                AssertLocal(next);
            }
            finally
            {
                next.Dispose();
            }
        }
        finally
        {
            held?.Dispose();
            Normal(scratch.Paths.ConsentFile);
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task Clearing_read_only_on_settings_json_is_enough_and_the_reloads_then_stop_while_consent_json_is_held_open() => UiSession.RunAsync(async () =>
    {
        // The folder watcher reloads on consent.json and on a change of attributes (AppRules.ReloadsOn, NotifyFilters.Attributes),
        // so clearing read-only on settings.json alone lets a reload save the switch-off there. A save fires one more reload, which
        // finds the files saved and writes nothing: the reloads stop, also while another program holds consent.json open (since the
        // lens on eaa3a04, read-only no longer stops its delete: CloudConsentFile.Delete clears it). Letting go of consent.json is no
        // change the watcher sees: the next reload of any kind deletes it. Asynchronous, so the UI thread's own loop runs the
        // watcher's posts and the reload debounce timer while it waits.
        var log = new RecordingLog();
        using var scratch = new Scratch(StartingCloudSettings);
        Consent(scratch);
        var shell = StartedShell(scratch, log);
        int Reloads() => log.Lines.Count(l => l.Message.StartsWith("Settings reloaded", StringComparison.Ordinal));
        FileStream? held = null;
        try
        {
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly); // a change of attributes: the watcher reloads
            Assert.True(await QuietAsync(Reloads), "the reloads did not stop after settings.json was made read-only");
            held = HoldOpen(scratch.Paths.ConsentFile);
            Reasons([shell.DisableCloud()!], NeitherChanged);
            Assert.True(await QuietAsync(Reloads), "the reloads did not stop while neither file could be changed");
            AssertLocal(shell);

            var before = Reloads();
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal); // nothing else: no save, no Reload settings
            Assert.True(await EventuallyAsync(() => SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider == LlmSettings.Local),
                "clearing read-only did not let a reload save settings.json");
            Assert.True(await QuietAsync(Reloads), "the reloads did not stop while consent.json stayed held open");
            Assert.True(Reloads() - before >= 2, "the watcher did not reload after settings.json was saved"); // the attributes', then the save's
            Assert.True(File.Exists(scratch.Paths.ConsentFile));
            AssertLocal(shell);

            held.Dispose(); // let go
            held = null;
            shell.Reload(userRequested: true); // Settings > About > Reload settings
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            Assert.True(await QuietAsync(Reloads), "the reloads did not stop once the switch-off was saved");
            AssertLocal(shell);
            return true;
        }
        finally
        {
            held?.Dispose();
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // settings.json deleted
    [InlineData(true)] // the whole settings folder deleted
    public Task Switching_cloud_clean_up_off_with_no_settings_json_is_saved_and_creates_none(bool folder) => UiSession.Run(() =>
    {
        // With no settings.json the next start runs the first-run choice, never a server, so there is nothing to save there: the
        // switch-off writes no new settings.json (which would skip the first-run clean-up question), and with the folder gone it
        // does not report a write that could not find the folder (lens mode "settings folder missing").
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            if (folder) Directory.Delete(scratch.Paths.ConfigDir, recursive: true);
            else File.Delete(scratch.Paths.SettingsFile);
            Assert.Null(shell.DisableCloud());
            AssertLocal(shell);
            Assert.False(File.Exists(scratch.Paths.SettingsFile));
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task A_saved_switch_off_ends_the_hold_so_a_hand_edit_naming_a_server_on_this_PC_is_picked_up() => UiSession.Run(() =>
    {
        // Lens on f69457d (finding 3): the hold outlived a switch-off with both files saved, so the README's snippet for a server on
        // this PC, saved by hand, was ignored until a restart, although Settings > About says an edit there is picked up in a second.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            Assert.Null(shell.DisableCloud()); // both files saved
            AssertLocal(shell);
            File.WriteAllText(scratch.Paths.SettingsFile,
                """{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "model": "llama3.2", "base_url": "http://localhost:11434/v1" } }""");
            shell.Reload(userRequested: false); // what the folder watcher does after the save
            Assert.StartsWith("openai-compatible/llama3.2 at localhost", shell.CleanupStatus);
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task The_log_has_a_line_for_each_try_and_each_override_and_neither_switch_shows_a_balloon() => UiSession.Run(() =>
    {
        // Lens on f69457d: one log line whenever the session's choices override the files (finding 3) and one per try to save them
        // (finding 1). The switches say on their own page why a file could not be changed, so the write they share with the
        // tray's (ModelCoordinator.TryWrite, finding 4) shows no balloon; the tray's own write still does.
        var log = new RecordingLog();
        using var scratch = new Scratch(LocalServerSettings); // read each time as a server on this PC, so each load overrides it
        var (shell, _, _) = scratch.Shell(log: log);
        int Lines(string start) => log.Lines.Count(l => l.Message.StartsWith(start, StringComparison.Ordinal));
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Assert.NotNull(shell.DisableCloud());
            shell.Reload(userRequested: false);
            Assert.Equal(2, Lines("Cloud clean-up is off for this session: the settings loaded name openai-compatible at 127.0.0.1, which is not used"));
            Assert.Equal(2, Lines("Cloud clean-up off is not saved yet (settings.json could not be saved: "));

            Assert.NotNull(shell.SetOfflineOnly(true));
            Assert.Equal(1, Lines("Offline only is on for this session: the settings loaded have it off"));
            Assert.Equal(1, Lines("Offline only is not saved yet (settings.json could not be saved: "));
            Assert.Equal(0, Lines($"Notice: {WriteFailed}"));

            Assert.False(shell.Write(("hotkey", JsonValue.Create("F23"))));
            Assert.Equal(1, Lines($"Notice: {WriteFailed}"));
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // then Settings > About > Reload settings
    [InlineData(true)] // then Quit
    public Task Saving_offline_only_is_tried_again_at_a_reload_and_at_quit(bool atQuit) => UiSession.Run(() =>
    {
        // The same lens: offline only switched on with settings.json read-only lived in memory only, so the next start ran without it.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Assert.NotNull(shell.SetOfflineOnly(true));
            shell.Reload(userRequested: true); // tried again, still read-only: not saved, still held
            Assert.True(shell.Settings.OfflineOnly);
            Assert.False(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.OfflineOnly);

            Normal(scratch.Paths.SettingsFile);
            if (atQuit) shell.Quit();
            else shell.Reload(userRequested: true);
            Assert.True(SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.OfflineOnly); // the next start has it on
            if (atQuit) return;

            // Saved, the hold is over: a hand edit that drops it is picked up like any other.
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            shell.Reload(userRequested: false);
            Assert.False(shell.Settings.OfflineOnly);
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task Remove_Dictum_completely_leaves_no_settings_json_for_offline_only_held_this_session() => UiSession.Run(() =>
    {
        // The quit tries to save what the session holds, but after Remove Dictum completely nothing is kept. dictionary.txt held
        // open keeps the settings folder, where a settings.json written at quit would be left behind.
        using var scratch = new Scratch("""{ "hotkey": "F24" """); // not valid JSON: offline only cannot be saved
        File.WriteAllText(scratch.Paths.DictionaryFile, "Kynni\n");
        var (shell, _, dialogs) = scratch.Shell();
        using var held = new FileStream(scratch.Paths.DictionaryFile, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Assert.NotNull(shell.SetOfflineOnly(true));
            dialogs.Answers.Enqueue(0); // Continue
            dialogs.ConfirmWord = true; // DELETE typed
            Assert.True(shell.RemoveEverything()); // deletes, then quits
            Assert.True(Directory.Exists(scratch.Paths.ConfigDir)); // kept by the file held open
            Assert.False(File.Exists(scratch.Paths.SettingsFile));
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task Allowing_cloud_clean_up_with_consent_json_read_only_leaves_no_tmp_file() => UiSession.Run(() =>
    {
        // Lens on f69457d (item 5, pre-existing): the move over a read-only consent.json threw, the page said "That did not work:
        // Access to the path is denied.", and consent.json.tmp was left beside it.
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        Consent(scratch); // an earlier consent, now read-only
        File.SetAttributes(scratch.Paths.ConsentFile, FileAttributes.ReadOnly);
        var (shell, _, dialogs) = scratch.Shell();
        try
        {
            dialogs.Answers.Enqueue(0); // Allow
            Assert.Throws<UnauthorizedAccessException>(() =>
                shell.EnableCloud(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://other.example.com/v1", Model = "m" }));
            Assert.False(File.Exists(scratch.Paths.ConsentFile + ".tmp"));
            Assert.Equal("llm.example.com", CloudConsentFile.Read(scratch.Paths.ConsentFile)?.Host); // the earlier consent, unchanged
            Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider); // not switched on
        }
        finally
        {
            Normal(scratch.Paths.ConsentFile);
            shell.Dispose();
        }
    });
}
