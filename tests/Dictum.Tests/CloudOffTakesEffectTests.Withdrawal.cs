using System.Security.AccessControl;
using System.Security.Principal;
using Dictum.App;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Shell;

namespace Dictum.Tests;

// Lens on eaa3a04 (16/10/2026): a withdrawal could still be lost. Read-only kept consent.json, switching on a server on this PC
// ended the tries to delete it, and the withdrawal lived only in memory, so a stale consent.json was honoured later with no consent
// dialog. consent.json is Dictum's own record (CloudConsentFile.Delete clears read-only on it), and the consent withdrawal holds on
// its own until consent.json is gone or "Allow" records a new consent. The pages say what the files mean if the last try fails.
public partial class CloudOffTakesEffectTests
{
    /// <summary>A server on this PC, which needs no consent step; port 9 is never contacted, since no dictation runs.</summary>
    private static readonly LlmSettings LocalServer = new() { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://127.0.0.1:9/v1", Model = "m" };

    /// <summary>A file held open by another program: readable, not deletable while held.</summary>
    private static FileStream HoldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>The next start on the same files: a new shell's first load, then <paramref name="check"/>; it asks no consent.</summary>
    private static void NextStart(Scratch scratch, Action<AppShell> check)
    {
        var (next, _, dialogs) = scratch.Shell();
        try
        {
            next.Reload(userRequested: false);
            check(next);
            Assert.Empty(dialogs.Asked);
        }
        finally
        {
            next.Dispose();
        }
    }

    /// <summary>
    /// The next start as Dictum starts (<see cref="AppShell.Start"/>): a reload would keep the settings in use when settings.json
    /// cannot be read, a start runs offline with the defaults. Then <paramref name="check"/>, with that start's log.
    /// </summary>
    private static void NextRealStart(Scratch scratch, Action<AppShell, RecordingLog> check)
    {
        var log = new RecordingLog();
        var next = StartedShell(scratch, log);
        try
        {
            check(next, log);
        }
        finally
        {
            next.Dispose();
        }
    }

    /// <summary>A start that runs offline with the local model, saying why: settings.json could not be read.</summary>
    private static void AssertStartedOffline(AppShell next, RecordingLog log)
    {
        Assert.True(next.Settings.OfflineOnly);
        AssertLocal(next);
        Assert.Contains(log.Lines, l => l.Message.Contains(ConfigLoader.BrokenAtStartup, StringComparison.Ordinal));
    }

    [Fact]
    public Task A_read_only_consent_json_is_deleted_by_the_switch_off_so_a_later_hand_edit_back_to_the_host_sends_nothing() => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P2): read-only stopped the delete, and switching on a server on this PC then ended the tries, so with
        // read-only cleared and Settings > About > Reload settings, consent.json was still there; a hand edit back to the host was
        // then used with no consent dialog, at the next start too. The switch-off now clears read-only and deletes consent.json.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, dialogs) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.ConsentFile, FileAttributes.ReadOnly);
            Assert.Null(shell.DisableCloud()); // both files keep the switch
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            Assert.Null(shell.EnableCloud(LocalServer));
            Assert.StartsWith("openai-compatible/m at 127.0.0.1", shell.CleanupStatus);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings); // a later hand edit back to the host
            shell.Reload(userRequested: false);
            AssertLocal(shell); // no consent on file: refused at load
            Assert.Empty(dialogs.Asked);
            NextStart(scratch, AssertLocal);
        }
        finally
        {
            Normal(scratch.Paths.ConsentFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task Switching_on_a_server_on_this_PC_leaves_the_consent_withdrawn_until_consent_json_is_deleted() => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P2L): with consent.json held open by another program, switching on a server on this PC ended the tries,
        // so once the program let go and Settings > About > Reload settings ran, consent.json was still there, and a hand edit back to
        // the host was used with no consent dialog. Only "Allow" ends the withdrawal: until consent.json is gone, the consent on file
        // is not used, and deleting it is tried again at every reload.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, dialogs) = scratch.Shell();
        FileStream? held = null;
        try
        {
            shell.Reload(userRequested: false);
            held = HoldOpen(scratch.Paths.ConsentFile);
            Reason([shell.DisableCloud()!], ConsentNotDeleted);
            Assert.Null(shell.EnableCloud(LocalServer));
            Assert.StartsWith("openai-compatible/m at 127.0.0.1", shell.CleanupStatus);

            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings); // still held open: a hand edit back to the host
            shell.Reload(userRequested: false);
            AssertLocal(shell); // the consent on file is not used
            Assert.True(File.Exists(scratch.Paths.ConsentFile));

            held.Dispose(); // let go
            held = null;
            shell.Reload(userRequested: true); // Settings > About > Reload settings
            Assert.False(File.Exists(scratch.Paths.ConsentFile)); // the withdrawal is saved
            AssertLocal(shell); // the host, with no consent on file: refused at load
            Assert.Empty(dialogs.Asked);
            NextStart(scratch, AssertLocal);
        }
        finally
        {
            held?.Dispose();
            shell.Dispose();
        }
    });

    [Fact]
    public Task Allowing_cloud_clean_up_again_ends_a_consent_withdrawal_not_saved_yet() => UiSession.Run(() =>
    {
        // "Allow" records a new consent, which ends the withdrawal: otherwise the switch-on's own reload would drop the consent just
        // given, and the next try would delete it.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, dialogs) = scratch.Shell();
        FileStream? held = null;
        try
        {
            shell.Reload(userRequested: false);
            held = HoldOpen(scratch.Paths.ConsentFile);
            Reason([shell.DisableCloud()!], ConsentNotDeleted);
            held.Dispose(); // let go, with no reload since: the withdrawal is not saved yet
            held = null;
            dialogs.Answers.Enqueue(0); // Allow
            Assert.Null(shell.EnableCloud(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" }));
            Assert.Equal("Send dictations to a cloud service?", Assert.Single(dialogs.Asked).Title);
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus);
            shell.Reload(userRequested: false);
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus);
            Assert.Equal("llm.example.com", CloudConsentFile.Read(scratch.Paths.ConsentFile)?.Host); // the new consent stays on file
        }
        finally
        {
            held?.Dispose();
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // both read-only
    [InlineData(true)] // consent.json held open by another program until after the quit, settings.json read-only
    public Task After_a_quit_that_could_not_save_the_switch_off_the_next_start_does_what_the_files_say(bool heldOpen) => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P3a, P3aL): with nothing fixed, the quit's try failed too, and the next start sent again with no notice.
        // The switch-off lives in this session; a later start can only read the files. Read-only no longer keeps consent.json, so with
        // no consent on file nothing is sent. Held open past the quit, both files are as they were and the next start sends again:
        // the limit, which the page now says ("if both are still unchanged then, the next start turns cloud clean-up back on").
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        FileStream? held = null;
        try
        {
            shell.Reload(userRequested: false);
            if (heldOpen) held = HoldOpen(scratch.Paths.ConsentFile);
            else File.SetAttributes(scratch.Paths.ConsentFile, FileAttributes.ReadOnly);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            var said = shell.DisableCloud()!;
            if (heldOpen) Reasons([said], NeitherChanged);
            else Reason([said], SettingsNotSaved);
            shell.Quit(); // the last try: settings.json is still read-only
            held?.Dispose();
            held = null;
            Assert.Equal(heldOpen, File.Exists(scratch.Paths.ConsentFile));
            Assert.Equal(LlmSettings.OpenAiCompatible, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider); // still names the host
            NextStart(scratch, next =>
            {
                if (heldOpen) Assert.StartsWith("openai-compatible/m at llm.example.com", next.CleanupStatus);
                else AssertLocal(next);
            });
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
    public Task With_both_files_unchanged_and_settings_json_unreadable_the_next_start_is_offline_as_the_page_says() => UiSession.Run(() =>
    {
        // The page's sentence for both files ends "unless settings.json cannot be read, in which case Dictum starts offline" (lens on
        // eaa3a04, finding 2). consent.json held open until after the quit, settings.json not valid JSON through it.
        using var scratch = new Scratch(StartingCloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        FileStream? held = null;
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly: false); // not valid JSON
            held = HoldOpen(scratch.Paths.ConsentFile);
            Reasons([shell.DisableCloud()!], NeitherChanged);
            shell.Quit(); // the last try: neither file can be changed
            held.Dispose();
            held = null;
            Assert.True(File.Exists(scratch.Paths.ConsentFile)); // the consent is still on file
            NextRealStart(scratch, AssertStartedOffline);
        }
        finally
        {
            held?.Dispose();
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // consent.json read-only
    [InlineData(true)] // consent.json held open by another program until after the quit
    public Task After_the_switch_off_and_a_restart_a_hand_edit_back_to_the_host_is_used_only_while_consent_json_is_on_file(bool heldOpen) => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P3b): consent.json alone read-only, through the quit: the next start used the local model, but a hand
        // edit back to the host was then used with no consent dialog. Read-only no longer keeps consent.json. Held open past the quit,
        // it is still on file, and that hand edit is used: the limit, which the page now says ("until it is deleted, your consent
        // stays on file").
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        FileStream? held = null;
        try
        {
            shell.Reload(userRequested: false);
            if (heldOpen) held = HoldOpen(scratch.Paths.ConsentFile);
            else File.SetAttributes(scratch.Paths.ConsentFile, FileAttributes.ReadOnly);
            var said = shell.DisableCloud();
            if (heldOpen) Reason([said!], ConsentNotDeleted);
            else Assert.Null(said);
            shell.Quit();
            held?.Dispose();
            held = null;
            Assert.Equal(heldOpen, File.Exists(scratch.Paths.ConsentFile));
            NextStart(scratch, next =>
            {
                AssertLocal(next); // settings.json was saved either way
                File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings); // a hand edit back to the host
                next.Reload(userRequested: false);
                if (heldOpen) Assert.StartsWith("openai-compatible/m at llm.example.com", next.CleanupStatus);
                else AssertLocal(next);
            });
        }
        finally
        {
            held?.Dispose();
            Normal(scratch.Paths.ConsentFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task Offline_only_the_quit_could_not_save_is_off_at_the_next_start_as_the_page_says() => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (finding 2): the page says what the next start does if the last try fails ("if it is still not saved then,
        // the next start runs without offline only"). settings.json read-only through the quit.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Reason([shell.SetOfflineOnly(true)!], OfflineOnlyNotSaved);
            shell.Quit(); // the last try: still read-only
            NextStart(scratch, next =>
            {
                Assert.False(next.Settings.OfflineOnly);
                Assert.StartsWith("openai-compatible/m at llm.example.com", next.CleanupStatus); // the consent on file applies again
            });
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task Offline_only_the_quit_could_not_save_with_settings_json_unreadable_the_next_start_is_offline_as_the_page_says() => UiSession.Run(() =>
    {
        // The offline-only sentence ends "unless settings.json cannot be read, in which case Dictum starts offline" (lens on eaa3a04,
        // finding 2). settings.json not valid JSON through the quit.
        using var scratch = new Scratch(StartingCloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly: false); // not valid JSON
            Reason([shell.SetOfflineOnly(true)!], OfflineOnlyNotSaved);
            shell.Quit(); // the last try: still not valid JSON
            NextRealStart(scratch, AssertStartedOffline);
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task Delete_all_data_including_settings_leaves_no_settings_json_for_offline_only_held_this_session() => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P1): the reload after the deletion saved the offline-only hold, so settings.json came back as
        // {"offline_only": true}. Deleted on request, the settings stay deleted, as when offline only was saved and then deleted.
        using var scratch = new Scratch("""{ "hotkey": "F24" """); // not valid JSON: offline only cannot be saved
        var (shell, _, dialogs) = scratch.Shell();
        try
        {
            Assert.NotNull(shell.SetOfflineOnly(true));
            Assert.True(shell.Settings.OfflineOnly);
            dialogs.Answers.Enqueue(0); // Delete
            Assert.StartsWith("Deleted ", shell.DeleteData(RemovalScope.AllDataIncludingSettings));
            Assert.False(File.Exists(scratch.Paths.SettingsFile));
            Assert.False(shell.Settings.OfflineOnly);
            shell.Quit(); // nor at quit
            Assert.False(File.Exists(scratch.Paths.SettingsFile));
            Assert.Equal((true, true), AppRules.FirstRunQuestions(SettingsLoader.LoadFile(scratch.Paths.SettingsFile))); // the next start asks again
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task Switching_cloud_clean_up_off_reloads_before_it_changes_either_file() => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P10, a pin): Reload saves what the session holds only after the load, so the switch-off is in effect (the
        // clean-up in use is the local model) before consent.json or settings.json is changed.
        var log = new RecordingLog();
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell(log: log);
        try
        {
            shell.Reload(userRequested: false);
            Assert.Null(shell.DisableCloud());
            var lines = log.Lines.Select(l => l.Message).ToList();
            var off = lines.IndexOf("Cloud clean-up switched off; consent withdrawn.");
            Assert.True(off >= 0, "no switch-off line");
            var reloaded = lines.FindIndex(off, l => l.StartsWith("Settings reloaded: ", StringComparison.Ordinal) && l.Contains("cleanup local/", StringComparison.Ordinal));
            var written = lines.FindIndex(off, l => l.StartsWith("Set from the app: llm.provider = \"local\"", StringComparison.Ordinal));
            Assert.True(reloaded > off && written > reloaded, $"off {off}, reloaded {reloaded}, written {written}");
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task Switching_offline_only_off_when_settings_json_cannot_be_written_keeps_it_on_at_the_next_reload() => UiSession.Run(() =>
    {
        // Lens on eaa3a04 (P11, a pin): the offline-only hold ends only once settings.json has it off, so a failed switch-off leaves it
        // in force at the next reload too.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Assert.NotNull(shell.SetOfflineOnly(true));
            Assert.Equal(WriteFailed, shell.SetOfflineOnly(false));
            shell.Reload(userRequested: false); // the folder watcher, any other choice
            Assert.True(shell.Settings.OfflineOnly);
            Assert.True(shell.NothingLeaves);
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    // Lens on cb2290c (16/10/2026): CloudConsentFile.Delete wrote attributes it need not; "Allow" with settings.json unwritable
    // let the exception of a consent that could not be deleted again reach the page; the sentences are the fifth draft.

    /// <summary>The server on this PC of <see cref="LocalServerSettings"/>, for a shell that starts (see <see cref="StartingCloudSettings"/>).</summary>
    private const string StartingLocalServerSettings = """
        { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false },
          "llm": { "provider": "openai-compatible", "base_url": "http://127.0.0.1:9/v1", "model": "m" } }
        """;

    /// <summary>
    /// Denies the current user write-attributes on a file made for the test, its access list otherwise as it was: SetAttributes
    /// throws, delete still works. Disposing takes the entry off again, so the scratch folder can be deleted.
    /// </summary>
    private sealed class DeniedWriteAttributes : IDisposable
    {
        private readonly string _path;
        private readonly FileSystemAccessRule _rule;

        public DeniedWriteAttributes(string path)
        {
            using var user = WindowsIdentity.GetCurrent();
            _path = path;
            _rule = new FileSystemAccessRule(user.User!, FileSystemRights.WriteAttributes, AccessControlType.Deny);
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(_rule);
            info.SetAccessControl(security);
        }

        public void Dispose()
        {
            if (!File.Exists(_path)) return;
            var info = new FileInfo(_path);
            var security = info.GetAccessControl();
            security.RemoveAccessRuleSpecific(_rule);
            info.SetAccessControl(security);
        }
    }

    /// <summary>
    /// A log that also runs <paramref name="onLine"/> on the writing thread as each line is written: a fixed point inside an
    /// action, such as "Allow" between the consent being written and being deleted again.
    /// </summary>
    private sealed class HookedLog(Action<string> onLine) : ILog
    {
        public RecordingLog Recorded { get; } = new();

        public void Write(LogLevel level, string message)
        {
            Recorded.Write(level, message);
            onLine(message);
        }
    }

    [Fact]
    public Task A_consent_json_whose_attributes_cannot_be_written_is_still_deleted_by_the_switch_off() => UiSession.Run(() =>
    {
        // Lens on cb2290c (R1): CloudConsentFile.Delete set the attributes to Normal whatever they were, so a consent.json whose access
        // list denies write-attributes but allows delete was kept (the call threw before the delete), and Normal also cleared hidden,
        // system and archive. It now clears read-only alone, and only when it is set. The deny is an access-list entry on a file made
        // for this test, taken off again at the end: with no deny the file is deleted either way, so this is what tells them apart.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        DeniedWriteAttributes? denied = null;
        try
        {
            shell.Reload(userRequested: false);
            var attributes = File.GetAttributes(scratch.Paths.ConsentFile);
            Assert.True(attributes.HasFlag(FileAttributes.Archive) && !attributes.HasFlag(FileAttributes.ReadOnly), $"archive and not read-only: {attributes}");
            denied = new DeniedWriteAttributes(scratch.Paths.ConsentFile);
            Assert.Throws<UnauthorizedAccessException>(() => File.SetAttributes(scratch.Paths.ConsentFile, FileAttributes.Normal)); // the deny holds

            Assert.Null(shell.DisableCloud()); // both files keep the switch
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            AssertLocal(shell);
        }
        finally
        {
            denied?.Dispose();
            shell.Dispose();
        }
    });

    [Fact]
    public Task Allowing_cloud_clean_up_when_settings_json_cannot_be_written_and_the_consent_cannot_be_deleted_again_keeps_it_withdrawn() => UiSession.Run(() =>
    {
        // Lens on cb2290c (contract): the consent just recorded is deleted again when settings.json cannot be written, and when that
        // delete threw (another program had consent.json open) the page said "That did not work: Access to the path ... is denied."
        // with the full path, while the consent stayed on file and the doc said it was withdrawn again. The write failure is returned
        // as for any unwritable settings.json; the consent on file is not used this session and is deleted at the next reload. The
        // log line for "Allow" is the point between the consent being written and being deleted again, where the other program
        // takes the file.
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        FileStream? held = null;
        var log = new HookedLog(line =>
        {
            if (held is null && line.StartsWith("Cloud clean-up agreed to in the consent dialog", StringComparison.Ordinal)) held = HoldOpen(scratch.Paths.ConsentFile);
        });
        var (shell, _, dialogs) = scratch.Shell(log: log);
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            dialogs.Answers.Enqueue(0); // Allow
            var proposed = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" };
            Assert.Equal(WriteFailed, shell.EnableCloud(proposed)); // returned, not thrown: the page shows a thrown one as "That did not work: ..."
            Assert.NotNull(held); // the other program took consent.json after it was written
            Assert.True(File.Exists(scratch.Paths.ConsentFile)); // so the delete failed
            AssertLocal(shell);
            var warning = Assert.Single(log.Recorded.Lines, l => l.Message.StartsWith("Consent to llm.example.com could not be withdrawn again (", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.EndsWith("); it is not used this session, and deleting it is tried again at the next reload and at quit.", warning.Message);
            PathFree(warning.Message);
            Assert.DoesNotContain(log.Recorded.Lines, l => l.Message.StartsWith("Consent to llm.example.com withdrawn again", StringComparison.Ordinal));

            // settings.json fixed by hand to name that host, with consent.json still there and still held: it is not used.
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            shell.Reload(userRequested: false);
            AssertLocal(shell);
            Assert.True(File.Exists(scratch.Paths.ConsentFile));

            held!.Dispose(); // let go: the next reload deletes it
            held = null;
            shell.Reload(userRequested: false);
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            AssertLocal(shell); // the host, with no consent on file: refused at load
            NextStart(scratch, AssertLocal);
        }
        finally
        {
            held?.Dispose();
            Normal(scratch.Paths.SettingsFile);
            Normal(scratch.Paths.ConsentFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task With_settings_json_unsaved_through_the_quit_the_next_start_uses_the_server_on_this_PC_as_the_page_says() => UiSession.Run(() =>
    {
        // Lens on cb2290c (disclosure, probe J): settings.json read-only through the quit, naming a server on this PC. A server on
        // this PC needs no consent, so unlike a service off this PC (refused at the next start without a consent on file) the next
        // start uses it again, which the fifth draft's variant says ("the next start uses that server again").
        using var scratch = new Scratch(StartingLocalServerSettings);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Reason([shell.DisableCloud()!], SettingsNotSavedServerOnThisPc);
            AssertLocal(shell);
            shell.Quit(); // the last try: still read-only
            NextRealStart(scratch, (next, _) =>
            {
                Assert.StartsWith("openai-compatible/m at 127.0.0.1", next.CleanupStatus);
                Assert.False(next.NothingLeaves);
            });
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });
}
