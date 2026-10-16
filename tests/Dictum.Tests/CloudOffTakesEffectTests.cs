using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// Lens on 3e86363 (16/10/2026; Store Policies 10.5.2): with settings.json not valid JSON, "Switch cloud clean-up off" deleted
/// consent.json, the settings write was refused, nothing reloaded, and the page said "Cloud clean-up is off" while the clean-up in
/// use stayed openai-compatible/m at llm.example.com and both pages still said the text is sent. The switch now takes effect
/// either way and the page shows the write failure; "Allow" with a failed write records no consent; and a reload stops a clean-up
/// request still under way, so nothing goes out on settings the pages no longer show.
/// Lens on 31d01f3 (16/10/2026): the switch still held only while the files could be changed. A read-only or locked consent.json
/// stopped it before anything else ran; a reload that could read settings.json brought back a server on this PC, or a consent still
/// on file; and offline only switched on with settings.json unwritable was not on at all. Both now apply in memory first and hold
/// for the session, the page names the file that could not be changed, and a reload stops only a clean-up that sends text.
/// Lens on f69457d (16/10/2026): those holds lived only in memory, and nothing tried the files again, so clearing read-only and
/// reloading left consent.json and a settings.json naming the host for the next start; the hold also outlived a switch-off that
/// was fully saved, so a hand edit naming a server on this PC was ignored until a restart. Every reload, and the quit, now try
/// again and end a hold once its files keep it; the folder watcher reloads on consent.json and on clearing read-only.
/// </summary>
public partial class CloudOffTakesEffectTests
{
    // The write path's own failure text (ModelCoordinator.WriteFailed), pinned word for word, independently of the app's copy.
    private const string WriteFailed = "settings.json could not be updated (it is not valid JSON, holds a key twice, or is read-only). Fix it, then choose again.";

    // PROPOSED copy (16/10/2026, third draft), awaiting the product owner's review: pinned word for word as proposed,
    // independently of the app's copy (AppShell.OffNotSaved). Shown when consent.json could not be deleted but settings.json
    // keeps cloud clean-up off.
    private static string ConsentNotDeleted(string reason) =>
        $"Cloud clean-up is off, but consent.json could not be deleted ({reason}). Nothing is sent. Dictum tries again at the next reload and when it quits; until it is deleted, your consent stays on file.";

    // PROPOSED copy (16/10/2026, second draft, kept in the third), as above: settings.json could not be saved; consent.json is
    // gone or never was.
    private static string SettingsNotSaved(string reason) =>
        $"Cloud clean-up is off for this session, but settings.json could not be saved ({reason}). Nothing is sent. Dictum tries again at the next reload and when it quits.";

    // PROPOSED copy (16/10/2026, third draft), as above: neither file could be changed.
    private static string NeitherChanged(string consent, string settings) =>
        $"Cloud clean-up is off for this session, but consent.json could not be deleted ({consent}) and settings.json could not be saved ({settings}). Nothing is sent. Dictum tries again at the next reload and when it quits; if both are still unchanged then, the next start sends dictations again.";

    // PROPOSED copy (16/10/2026, third draft), awaiting the product owner's review: pinned word for word as proposed, independently of the app's copy.
    private static string OfflineOnlyNotSaved(string reason) =>
        $"Offline only is on for this session, but settings.json could not be saved ({reason}). Dictum tries again at the next reload and when it quits; if it is still not saved then, the next start runs without offline only.";

    private const string CloudSettings = """{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" } }""";

    /// <summary>A server on this PC (no consent step); port 9 is never contacted, since no dictation runs.</summary>
    private const string LocalServerSettings = """{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "base_url": "http://127.0.0.1:9/v1", "model": "m" } }""";

    private static readonly FieldInfo CleanerField = typeof(AppShell).GetField("_cleaner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static T Named<T>(ILogical root, string name) where T : Control => All<T>(root).Single(c => AutomationProperties.GetName(c) == name);

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static List<string> VisibleLines(SettingsWindow window) => All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();

    /// <summary>Every visible line of a page, drawn afresh from the settings and the clean-up in use as they are now.</summary>
    private static List<string> PageLines(SettingsWindow window, string page)
    {
        window.ShowPage(page == "Privacy" ? "Clean-up" : "Privacy");
        window.ShowPage(page);
        return VisibleLines(window);
    }

    private static bool Shown(SettingsWindow window, string button) => All<Button>(window).Any(b => b.IsEffectivelyVisible && b.Content as string == button);

    private static void Consent(Scratch scratch) =>
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));

    /// <summary>So the scratch folder can be deleted.</summary>
    private static void Normal(string path)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
    }

    /// <summary>The clean-up in use is the local model: nothing leaves this PC.</summary>
    private static void AssertLocal(AppShell shell)
    {
        Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider);
        Assert.StartsWith("local/", shell.CleanupStatus);
        Assert.EndsWith(" on this PC (nothing leaves it)", shell.CleanupStatus);
        Assert.True(shell.NothingLeaves);
    }

    /// <summary>
    /// The one line shown that is <paramref name="sentence"/> word for word around some reason; returns that reason, checked to
    /// name no path (the page names the settings folder elsewhere).
    /// </summary>
    private static string Reason(IEnumerable<string> lines, Func<string, string> sentence)
    {
        var marked = sentence("\u0001");
        var at = marked.IndexOf('\u0001');
        var (head, tail) = (marked[..at], marked[(at + 1)..]);
        var line = Assert.Single(lines, l => l.StartsWith(head, StringComparison.Ordinal) && l.EndsWith(tail, StringComparison.Ordinal));
        return PathFree(line[head.Length..^tail.Length]);
    }

    /// <summary>As <see cref="Reason"/>, for a sentence with two reasons; returns both, in order.</summary>
    private static (string First, string Second) Reasons(IEnumerable<string> lines, Func<string, string, string> sentence)
    {
        var marked = sentence("\u0001", "\u0002");
        var (first, second) = (marked.IndexOf('\u0001'), marked.IndexOf('\u0002'));
        var (head, middle, tail) = (marked[..first], marked[(first + 1)..second], marked[(second + 1)..]);
        var line = Assert.Single(lines, l => l.StartsWith(head, StringComparison.Ordinal) && l.EndsWith(tail, StringComparison.Ordinal)
                                             && l.Contains(middle, StringComparison.Ordinal));
        var body = line[head.Length..^tail.Length];
        var at = body.IndexOf(middle, StringComparison.Ordinal);
        return (PathFree(body[..at]), PathFree(body[(at + middle.Length)..]));
    }

    /// <summary>A reason, checked to be there and to name no path (the page names the settings folder elsewhere).</summary>
    private static string PathFree(string reason)
    {
        Assert.NotEmpty(reason);
        Assert.DoesNotContain(":\\", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), reason, StringComparison.OrdinalIgnoreCase);
        return reason;
    }

    /// <summary>
    /// settings.json made unwritable: not valid JSON (a closing brace lost; the reload the file watcher makes keeps the settings in
    /// use), or read-only (still readable).
    /// </summary>
    private static void MakeUnwritable(Scratch scratch, AppShell shell, bool readOnly)
    {
        if (readOnly)
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            return;
        }
        File.WriteAllText(scratch.Paths.SettingsFile, File.ReadAllText(scratch.Paths.SettingsFile).TrimEnd()[..^1]);
        shell.Reload(userRequested: false);
    }

    /// <summary>Runs the UI thread's queued work until <paramref name="condition"/> holds.</summary>
    private static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [Theory]
    [InlineData(false)] // not valid JSON
    [InlineData(true)] // read-only
    public Task Switching_cloud_clean_up_off_takes_effect_when_settings_json_cannot_be_written(bool readOnly) => UiSession.Run(() =>
    {
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly);
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus); // on: each dictation goes to the host
            Assert.False(shell.NothingLeaves);
            var before = File.ReadAllText(scratch.Paths.SettingsFile);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            Click(window, "Switch cloud clean-up off");

            // The page says the switch took effect for this session and names the file that could not be changed, with the reason;
            // never that it failed and must be chosen again, never plain "off" (the lens on 31d01f3: "Fix it, then choose again.").
            var said = VisibleLines(window);
            Reason(said, SettingsNotSaved);
            Assert.DoesNotContain(WriteFailed, said);
            Assert.DoesNotContain("Cloud clean-up is off; the local model tidies your dictations again.", said);
            Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile)); // the write really was refused
            // ...and the switch took effect anyway: the consent is withdrawn and the clean-up in use runs on this PC.
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            AssertLocal(shell);

            // Both pages say so: the Clean-up card is in its off state and shows the local clean-up; Privacy says nothing leaves.
            var cleanUp = PageLines(window, "Clean-up");
            Assert.Contains(shell.CleanupStatus, cleanUp);
            Assert.DoesNotContain(cleanUp, l => l.StartsWith("On: ", StringComparison.Ordinal) || l.StartsWith("On, ", StringComparison.Ordinal));
            Assert.True(Shown(window, "Use cloud clean-up…"));
            Assert.False(Shown(window, "Switch cloud clean-up off"));
            var privacy = PageLines(window, "Privacy");
            Assert.Contains("Nothing leaves this PC: speech recognition and clean-up both run here.", privacy);
            Assert.DoesNotContain(privacy, l => l.Contains("llm.example.com", StringComparison.Ordinal));

            shell.Reload(userRequested: false); // a later reload (the file watcher, any other choice): the switch holds
            AssertLocal(shell);

            // settings.json fixed by hand, still naming the host: the consent stays withdrawn, so nothing is sent there, and that
            // reload saves the switch-off over the host (SaveCloudOff), so the next start reads the local model too.
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            shell.Reload(userRequested: false);
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider);
            Assert.True(shell.NothingLeaves);
            Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
            window.Close();
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // not valid JSON
    [InlineData(true)] // read-only
    public Task Allowing_cloud_clean_up_when_settings_json_cannot_be_written_records_no_consent_and_sends_nothing(bool readOnly) => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, dialogs) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            Named<ComboBox>(window, "Service").SelectedIndex = CloudPresets.All.ToList().FindIndex(p => p.BaseUrl is null) + 1; // the custom server
            Named<TextBox>(window, "Server address").Text = "https://llm.example.com/v1";
            Named<TextBox>(window, "Model").Text = "m";
            Dispatcher.UIThread.RunJobs(); // TextBox raises TextChanged through the dispatcher
            dialogs.Answers.Enqueue(0); // Allow
            Click(window, "Use cloud clean-up…");
            Assert.Equal("Send dictations to a cloud service?", Assert.Single(dialogs.Asked).Title); // asked, and agreed to

            var said = VisibleLines(window);
            Assert.Contains(WriteFailed, said);
            Assert.DoesNotContain("Cloud clean-up is on.", said);
            Assert.False(File.Exists(scratch.Paths.ConsentFile)); // no consent stands for a host the settings could not be switched to
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider);
            Assert.StartsWith("local/", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);

            // settings.json fixed by hand to name that host: no consent is on record, so nothing is sent there.
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            shell.Reload(userRequested: false);
            Assert.Equal(LlmSettings.Local, shell.Settings.Llm.Provider); // repaired at load: llm.example.com was not agreed to
            Assert.True(shell.NothingLeaves);
            window.Close();
        }
        finally
        {
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData("consent.json held open")] // readable, not deletable
    [InlineData("consent.json locked")] // not even readable
    [InlineData("consent.json held open, settings.json read-only")]
    public Task Switching_cloud_clean_up_off_stops_sending_when_consent_json_cannot_be_deleted(string mode) => UiSession.Run(() =>
    {
        // Lens P1 on 31d01f3: the delete ran first and threw, so nothing else did. The clean-up in use stayed openai-compatible/m at
        // llm.example.com, the page said "On: the text of each dictation is sent to…" over "That did not work: …", and a later
        // reload re-applied the consent still on file. Since the lens on eaa3a04, read-only no longer stops the delete
        // (CloudConsentFile.Delete clears it): another program holding consent.json open does.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        FileStream? held = null;
        var both = mode.EndsWith("settings.json read-only", StringComparison.Ordinal);
        try
        {
            shell.Reload(userRequested: false);
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus);
            Assert.False(shell.NothingLeaves);
            held = new FileStream(scratch.Paths.ConsentFile, FileMode.Open, FileAccess.Read, mode == "consent.json locked" ? FileShare.None : FileShare.Read);
            if (both) File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            var before = File.ReadAllText(scratch.Paths.SettingsFile);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            Click(window, "Switch cloud clean-up off");

            var said = VisibleLines(window);
            if (both) Reasons(said, NeitherChanged);
            else Reason(said, ConsentNotDeleted);
            Assert.DoesNotContain(said, l => l.StartsWith("That did not work", StringComparison.Ordinal));
            Assert.True(File.Exists(scratch.Paths.ConsentFile)); // the delete really failed
            AssertLocal(shell);
            var cleanUp = PageLines(window, "Clean-up");
            Assert.DoesNotContain(cleanUp, l => l.StartsWith("On: ", StringComparison.Ordinal) || l.StartsWith("On, ", StringComparison.Ordinal));
            Assert.True(Shown(window, "Use cloud clean-up…"));
            Assert.Contains("Nothing leaves this PC: speech recognition and clean-up both run here.", PageLines(window, "Privacy"));
            if (both) Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile)); // still names the host
            else Assert.Equal(LlmSettings.Local, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider); // saved

            // A later reload, with the consent to llm.example.com on file and readable: the switch holds. Still held open, consent.json
            // is tried again later; let go, the next reload deletes it.
            if (mode == "consent.json locked")
            {
                held.Dispose();
                held = null;
            }
            Assert.Equal("llm.example.com", CloudConsentFile.Read(scratch.Paths.ConsentFile)?.Host);
            shell.Reload(userRequested: false);
            AssertLocal(shell);
            Assert.Equal(held is not null, File.Exists(scratch.Paths.ConsentFile));
            held?.Dispose();
            held = null;
            shell.Reload(userRequested: false);
            AssertLocal(shell);
            Assert.False(File.Exists(scratch.Paths.ConsentFile));
            window.Close();
        }
        finally
        {
            held?.Dispose();
            Normal(scratch.Paths.ConsentFile);
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // not valid JSON
    [InlineData(true)] // read-only
    public Task Switching_cloud_clean_up_off_stops_a_server_on_this_PC_when_settings_json_cannot_be_written(bool readOnly) => UiSession.Run(() =>
    {
        // Lens P5 on 31d01f3: with settings.json read-only, the reload after the in-memory switch read the server back, so the
        // clean-up in use was still openai-compatible at 127.0.0.1 after the click. The switch means: stop using any server.
        using var scratch = new Scratch(LocalServerSettings);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly);
            Assert.StartsWith("openai-compatible/m at 127.0.0.1", shell.CleanupStatus); // a server on this PC tidies each dictation
            Assert.False(shell.NothingLeaves);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Clean-up");
            Click(window, "Switch cloud clean-up off");

            Reason(VisibleLines(window), SettingsNotSaved);
            AssertLocal(shell);
            Assert.DoesNotContain(PageLines(window, "Privacy"), l => l.Contains("127.0.0.1", StringComparison.Ordinal));
            shell.Reload(userRequested: false); // a later reload that reads the server from the file again: the switch holds
            AssertLocal(shell);
            window.Close();
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Fact]
    public Task A_failed_switch_on_leaves_the_off_switch_in_force_and_a_saved_one_ends_it() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(LocalServerSettings);
        var (shell, _, _) = scratch.Shell();
        var server = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://127.0.0.1:9/v1", Model = "m" };
        try
        {
            shell.Reload(userRequested: false);
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
            Assert.NotNull(shell.DisableCloud()); // off for this session: settings.json still names the server
            AssertLocal(shell);

            // Switching on again with the file still read-only fails: the off switch holds, now and at the next reload.
            Assert.Equal(WriteFailed, shell.EnableCloud(server));
            AssertLocal(shell);
            shell.Reload(userRequested: false);
            AssertLocal(shell);

            // Once the file can be written, switching on ends it.
            File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.Normal);
            Assert.Null(shell.EnableCloud(server));
            Assert.StartsWith("openai-compatible/m at 127.0.0.1", shell.CleanupStatus);
            shell.Reload(userRequested: false);
            Assert.StartsWith("openai-compatible/m at 127.0.0.1", shell.CleanupStatus);
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Theory]
    [InlineData(false)] // not valid JSON
    [InlineData(true)] // read-only
    public Task Switching_offline_only_on_takes_effect_when_settings_json_cannot_be_written(bool readOnly) => UiSession.Run(() =>
    {
        // Lens P6 on 31d01f3: the switch showed on while offline only was off and each dictation still went to llm.example.com,
        // although the consent dialog promises that offline only mode refuses cloud clean-up.
        using var scratch = new Scratch(CloudSettings);
        Consent(scratch);
        var (shell, _, _) = scratch.Shell();
        try
        {
            shell.Reload(userRequested: false);
            MakeUnwritable(scratch, shell, readOnly);
            Assert.False(shell.NothingLeaves);
            var before = File.ReadAllText(scratch.Paths.SettingsFile);
            var window = new SettingsWindow(shell);
            window.Show();
            window.ShowPage("Privacy");
            var offline = Named<ToggleSwitch>(window, "Offline only");
            offline.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Reason(VisibleLines(window), OfflineOnlyNotSaved);
            Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile)); // the write really was refused
            // On in memory: the switch says so truthfully, cloud clean-up is refused and nothing leaves this PC.
            Assert.True(offline.IsChecked);
            Assert.True(shell.Settings.OfflineOnly);
            Assert.StartsWith("offline_only is on: the openai-compatible provider at llm.example.com is refused", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);
            Assert.Contains(CloudPolicy.OfflineOnlyLine, PageLines(window, "Clean-up"));
            shell.Reload(userRequested: false); // a later reload: it holds
            Assert.True(shell.Settings.OfflineOnly);
            Assert.StartsWith("offline_only is on", shell.CleanupStatus);
            Assert.True(shell.NothingLeaves);

            // Switching it off needs the file: nothing changes, the page says so and the switch shows on again.
            window.ShowPage("Privacy");
            offline.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(WriteFailed, VisibleLines(window));
            Assert.True(offline.IsChecked);
            Assert.True(shell.Settings.OfflineOnly);
            Assert.True(shell.NothingLeaves);

            // The file fixed: switching off is saved, and the consent on file applies again.
            Normal(scratch.Paths.SettingsFile);
            File.WriteAllText(scratch.Paths.SettingsFile, CloudSettings);
            offline.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(offline.IsChecked);
            Assert.False(shell.Settings.OfflineOnly);
            Assert.DoesNotContain(WriteFailed, VisibleLines(window));
            Assert.StartsWith("openai-compatible/m at llm.example.com", shell.CleanupStatus);
            window.Close();
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
            shell.Dispose();
        }
    });

    [Fact]
    public void The_reason_in_those_sentences_is_the_message_short_and_without_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "dictum-reason", "config", "consent.json");
        Assert.Equal("access to the path is denied", SettingsFile.ShortReason(new UnauthorizedAccessException($"Access to the path '{path}' is denied."), path));
        Assert.Equal("the process cannot access the file because it is being used by another process",
            SettingsFile.ShortReason(new IOException($"The process cannot access the file '{path}' because it is being used by another process."), path));
        Assert.Equal("could not find a part of the path", SettingsFile.ShortReason(new DirectoryNotFoundException($"Could not find a part of the path '{path}.tmp'."), path));
        Assert.Equal("expected depth to be zero at the end of the JSON payload", SettingsFile.ShortReason(new JsonException(
            "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed. LineNumber: 0 | BytePositionInLine: 9."), path));
        Assert.Equal("JSON text ended early", SettingsFile.ShortReason(new JsonException("JSON text ended early."), path)); // an acronym keeps its capitals
        Assert.Equal(nameof(IOException), SettingsFile.ShortReason(new IOException($"'{path}'"), path)); // nothing else said: the kind of failure

        using var scratch = new Scratch("[]");
        Assert.False(SettingsFile.SetValue(scratch.Paths.SettingsFile, "offline_only", JsonValue.Create(true), out var failure));
        Assert.Equal("settings.json must contain a JSON object", failure);
        File.WriteAllText(scratch.Paths.SettingsFile, "{}");
        File.SetAttributes(scratch.Paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            Assert.False(SettingsFile.SetValue(scratch.Paths.SettingsFile, "offline_only", JsonValue.Create(true), out failure));
            Assert.NotEmpty(failure!);
            Assert.DoesNotContain(scratch.Root.FullName, failure, StringComparison.OrdinalIgnoreCase);
            Assert.True(SettingsFile.SetValue(Path.Combine(scratch.Root.FullName, "other.json"), "offline_only", JsonValue.Create(true), out failure));
            Assert.Null(failure);
        }
        finally
        {
            Normal(scratch.Paths.SettingsFile);
        }
    }

    [Fact]
    public void Only_a_clean_up_that_sends_text_is_retired_by_a_reload()
    {
        using var http = new HttpClient(new CountingHandler()); // never a real network stack in a unit test
        var secrets = SecretStore.FromContents("ANTHROPIC_API_KEY=k-test-123456\n", null, _ => null);
        TranscriptCleaner Make(LlmSettings settings) => TranscriptCleaner.Create(settings, offlineOnly: false, secrets, http, NullLog.Instance);
        Assert.False(Make(new LlmSettings { Provider = LlmSettings.Local }).SendsText);
        Assert.False(Make(new LlmSettings { Provider = LlmSettings.NoProvider }).SendsText);
        Assert.True(Make(new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "http://127.0.0.1:9/v1", Model = "m" }).SendsText); // a server on this PC
        Assert.True(Make(new LlmSettings { Provider = LlmSettings.Anthropic, Model = LlmSettings.AnthropicDefaultModel, CloudConsent = "api.anthropic.com" }).SendsText);
    }

    [Fact]
    public async Task A_retired_cleaner_cancels_the_request_under_way_and_starts_no_other()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new HeldClient();
        try
        {
            var cleaner = new TranscriptCleaner(client, CleanupPrompt.Default, TimeSpan.FromSeconds(60), NullLog.Instance, "test");
            const string raw = "um the invoice went out today";
            var cleaning = cleaner.CleanAsync(raw, ct);
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            cleaner.Retire(); // what a settings reload does to the cleaner it replaces
            var result = await cleaning.WaitAsync(TimeSpan.FromSeconds(5), ct); // at once, not at the 60 s deadline, though the provider has not stopped
            Assert.Equal(new CleanupResult(DeterministicCleaner.Clean(raw), false, TranscriptCleaner.Reloaded), result);
            Assert.True(client.Token.IsCancellationRequested); // the provider's own token: it is told to stop, so an SDK retry is not made

            var later = await cleaner.CleanAsync(raw, ct); // a dictation that picked this cleaner up just before the reload
            Assert.Equal(1, client.Calls); // starts no request
            Assert.Equal(new CleanupResult(DeterministicCleaner.Clean(raw), false, TranscriptCleaner.Reloaded), later);
        }
        finally
        {
            client.Release();
        }
    }

    [Theory]
    [InlineData(false)] // a reload that changes nothing about the clean-up (a dictionary word added, a download finished, any save)
    [InlineData(true)] // Switch cloud clean-up off
    public Task A_reload_during_a_clean_up_request_cancels_it_and_the_dictation_gets_the_rule_based_tidy(bool switchOff) => UiSession.Run(() =>
    {
        // The lens (16/10/2026): the pipeline reads the cleaner once, so a request already sent ran on (with Anthropic's one retry)
        // after the pages changed. A server on this PC that never answers stands in for the cloud host: the request takes the same
        // path (OpenAiCompatibleLlmClient over the shell's HttpClient) with no consent step and no network.
        using var server = new SilentServer();
        using var scratch = new Scratch($$"""
            { "hotkey": "F24", "whisper": { "model": "base.en" }, "pill": { "enabled": false },
              "llm": { "provider": "openai-compatible", "base_url": "http://127.0.0.1:{{server.Port}}/v1", "model": "m" } }
            """);
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin"); // the fake engine transcribes every recording as this name
        File.WriteAllText(model, "");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), warmUp: () => false);
        var (shell, platform, _) = scratch.Shell(models, new ShellOptions(ModelOverride: model));
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            Assert.False(shell.NothingLeaves); // the server tidies each dictation
            var key = platform.LastListener!;

            // A one-second hold. Event times a minute ahead of the controller's own clock, so its tick can expire nothing meanwhile.
            var t0 = Environment.TickCount64 + 60_000;
            key.Post(new HookEvent(HotkeySignal.Down, t0));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            key.Post(new HookEvent(HotkeySignal.Up, t0 + 1_000));
            Assert.True(Eventually(() => server.Received == 1), "the clean-up request did not reach the server");

            if (switchOff) shell.DisableCloud(); // the settings reload with the local model, with the request under way
            else shell.Reload(userRequested: false); // the clean-up still sends text: its request is stopped all the same
            Assert.True(Eventually(() => server.Abandoned == 1), "the request under way ran on after the reload (it would have waited for the 15 s deadline)");
            var tidy = DeterministicCleaner.Clean("fake-model.bin");
            Assert.True(Eventually(() => platform.Desktop.Ops.Contains($"set-text:{tidy} ")), "the dictation was not pasted with the rule-based tidy");
            Assert.Equal(1, server.Received); // nothing was sent again
            Assert.Equal(switchOff, shell.NothingLeaves);
        }
        finally
        {
            shell.Dispose();
        }
    });

    [Fact]
    public Task A_reload_during_a_local_clean_up_lets_it_finish() => UiSession.Run(() =>
    {
        // Lens P10 on 31d01f3: every reload retired the clean-up in use, so a dictionary word added, a model download finishing or
        // any settings save during a local clean-up abandoned it for the rule-based tidy. The local model sends nothing: it finishes.
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var engine = new HeldLocalEngine("The invoice went out today.");
        try
        {
            shell.Reload(userRequested: false);
            var local = new TranscriptCleaner(new LocalLlmClient(() => engine, "test"), CleanupPrompt.Default, TimeSpan.FromSeconds(60), NullLog.Instance,
                "local/test on this PC (nothing leaves it)");
            CleanerField.SetValue(shell, local); // the clean-up in use: the local model, with a request under way
            var cleaning = local.CleanAsync("the invoice went out today", CancellationToken.None);
            Assert.True(engine.Started.Wait(TimeSpan.FromSeconds(5)), "the local model was not asked");

            shell.Reload(userRequested: false); // nothing about the clean-up changed
            Assert.NotSame(local, CleanerField.GetValue(shell)); // the reload did replace the cleaner
            engine.Release();
            Assert.True(cleaning.Wait(TimeSpan.FromSeconds(5)), "the clean-up did not finish");
            Assert.Equal(new CleanupResult("The invoice went out today.", true, null), cleaning.Result); // the model's reply, not the tidy
        }
        finally
        {
            engine.Release();
            shell.Dispose();
        }
    });

    /// <summary>
    /// A provider whose request stays under way until the test releases it, even once its token is cancelled (as llama.cpp
    /// mid-prompt, or an SDK that does not stop at once); keeps the token of the first request.
    /// </summary>
    private sealed class HeldClient : ILlmClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public int Calls => Volatile.Read(ref _calls);
        public string Name => "held";

        public void Release() => _release.TrySetResult();

        public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1) Token = cancellationToken;
            Started.TrySetResult();
            await _release.Task;
            return new LlmReply("never", "end_turn");
        }
    }

    /// <summary>A loaded local model whose reply waits until the test releases it, then gives <paramref name="reply"/>.</summary>
    private sealed class HeldLocalEngine(string reply) : ILocalLlmEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Started { get; } = new();
        public string ModelPath => "held.gguf";
        public (double Prompt, double Generation) Rates => (0, 0);

        public void Release() => _release.TrySetResult();

        public Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default) => Task.FromResult(TimeSpan.Zero);

        public int CountTokens(string text) => text.Length / 4;

        public TimeSpan? Predict(int dictationTokens) => null;

        public async Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
            CancellationToken cancellationToken)
        {
            Started.Set();
            await _release.Task.ConfigureAwait(false); // as llama.cpp mid-prompt: the token does not stop it
            return new GenerationResult(reply, true, 0, TimeSpan.Zero, 0, TimeSpan.Zero);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A server on this PC that reads each request and never answers it; counts the requests the client gave up on.</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _received;
        private int _abandoned;

        public SilentServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Received => Volatile.Read(ref _received);
        public int Abandoned => Volatile.Read(ref _abandoned);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HoldAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task HoldAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[64 * 1024];
                try
                {
                    if (await stream.ReadAsync(buffer, _stop.Token) == 0) return;
                    Interlocked.Increment(ref _received);
                    while (await stream.ReadAsync(buffer, _stop.Token) > 0)
                    {
                        // the rest of the request; no answer is ever written
                    }
                    Interlocked.Increment(ref _abandoned); // the client closed the connection
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref _abandoned); // the client reset it
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
