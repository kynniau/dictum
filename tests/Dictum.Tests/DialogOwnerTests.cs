using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// A question opened by a click on a window's button is that window's modal dialog (fix lane, 23/10/2026). Avalonia raises
/// Click inside the button's pointer-released handler, before the window releases the mouse capture, and the dialog's frame
/// runs inside that Click: a dialog shown on its own got no mouse input until it closed (GetCapture() was still the opener).
/// These run the real <see cref="ShellDialogs"/> and its nested frame on the headless platform; the capture itself is
/// checked on real Win32 by <see cref="Dialog_opened_by_a_click_gets_the_mouse_on_Win32"/>.
/// </summary>
public class DialogOwnerTests(ITestOutputHelper output)
{
    private const string ConsentTitle = "Dictum: Send dictations to a cloud service?";

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.IsEffectivelyVisible && b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Every question window opened while it is alive, owned or not (the headless platform has no window list).</summary>
    private sealed class Opened : IDisposable
    {
        private readonly IDisposable _handler;

        public Opened() => _handler = Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            if (sender is QuestionWindow q) Windows.Add(q);
        });

        public List<QuestionWindow> Windows { get; } = [];

        public void Dispose() => _handler.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="inside"/> inside the next question's frame, then closes the question if it is still open, so a
    /// failing check never leaves the frame running.
    /// </summary>
    private static void Inside(Opened opened, Action<QuestionWindow> inside) => Dispatcher.UIThread.Post(() =>
    {
        var question = opened.Windows.LastOrDefault(w => w.IsVisible);
        if (question is null) return;
        try
        {
            inside(question);
        }
        finally
        {
            if (question.IsVisible) question.Close();
        }
    }, DispatcherPriority.Background);

    private static (AppShell Shell, SettingsWindow Window, Scratch Scratch) CleanUpPage()
    {
        var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var shell = new AppShell(new FakePlatform(scratch.Paths), scratch.Paths, NullLog.Instance, new SecretRedactor(), new ShellOptions(), new ShellDialogs(), gpuMachine: () => false);
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        All<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "Service").SelectedIndex =
            CloudPresets.All.ToList().FindIndex(p => p.Name == "OpenAI") + 1;
        Dispatcher.UIThread.RunJobs();
        return (shell, window, scratch);
    }

    [Fact]
    public Task The_cloud_consent_opened_from_Settings_is_its_modal_dialog_and_one_at_a_time() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var (shell, window, scratch) = CleanUpPage();
        using var _ = scratch;
        var before = File.ReadAllText(scratch.Paths.SettingsFile);

        (WindowBase? Owner, WindowStartupLocation Where, bool Owns)? seen = null;
        Inside(opened, q =>
        {
            seen = (q.Owner, q.WindowStartupLocation, window.OwnedWindows.Contains(q));
            Inside(opened, _ => { }); // closes a second dialog, if one opened, so its frame ends
            Click(window, "Use cloud clean-up…"); // a second click on Settings while the consent is up
            q.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        });
        Click(window, "Use cloud clean-up…");

        Assert.Equal(ConsentTitle, Assert.Single(opened.Windows).Title); // the second click opened no second dialog
        Assert.NotNull(seen);
        Assert.Same(window, seen.Value.Owner);
        Assert.True(seen.Value.Owns);
        Assert.Equal(WindowStartupLocation.CenterOwner, seen.Value.Where);
        Assert.Empty(window.OwnedWindows); // closed: Settings is no longer its owner
        Assert.Equal(-1, opened.Windows[0].Choice); // Escape closed it unanswered
        Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile));
        Assert.False(File.Exists(scratch.Paths.ConsentFile));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Enter_and_the_buttons_answer_the_consent_as_before() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var (shell, window, scratch) = CleanUpPage();
        using var _ = scratch;
        var before = File.ReadAllText(scratch.Paths.SettingsFile);

        Inside(opened, q => q.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null)); // Enter: Cancel, the default
        Click(window, "Use cloud clean-up…");
        Assert.Equal(1, Assert.Single(opened.Windows).Choice);
        Assert.Equal(before, File.ReadAllText(scratch.Paths.SettingsFile));
        Assert.False(File.Exists(scratch.Paths.ConsentFile));

        Inside(opened, q => Click(q, "Allow cloud clean-up"));
        Click(window, "Use cloud clean-up…");
        Assert.Equal(2, opened.Windows.Count);
        Assert.Equal(0, opened.Windows[1].Choice);
        Assert.Equal("api.openai.com", CloudConsentFile.Read(scratch.Paths.ConsentFile)!.Host); // recorded by Allow only
        Assert.Equal(LlmSettings.OpenAiCompatible, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Provider);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_question_with_no_click_behind_it_or_from_the_tray_panel_is_shown_on_its_own() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var dialogs = new ShellDialogs();

        // First run, or a question from the tray or another thread: no window's button is being clicked.
        (WindowBase? Owner, WindowStartupLocation Where)? seen = null;
        Inside(opened, q => seen = (q.Owner, q.WindowStartupLocation));
        Assert.Equal(-1, dialogs.Ask("Download a model", "text", new DialogChoice("Download", IsDefault: true), new DialogChoice("Cancel")));
        Assert.Equal((null, WindowStartupLocation.CenterScreen), seen);

        // The tray panel closes itself when it loses focus, which would close a dialog it owned: its questions stay unowned.
        var flyout = new FlyoutWindow(shell);
        flyout.Show();
        seen = null;
        Inside(opened, q => seen = (q.Owner, q.WindowStartupLocation));
        Modal.From(flyout, () => dialogs.Ask("Download a model", "text", new DialogChoice("Download", IsDefault: true), new DialogChoice("Cancel")));
        Assert.Equal((null, WindowStartupLocation.CenterScreen), seen);
        Assert.Equal(2, opened.Windows.Count);
        if (flyout.IsVisible) flyout.Close();
        shell.Dispose();
    });

    // ---- Calibration's "Download and include" (Mac beta, 23/10/2026: the question was seen behind the calibration window,
    // which already said "Downloading…" while nothing had started) ----

    private const string IncludeSmallEn = "Download small.en and include it in the test";
    private const string DownloadingSmallEn = "Downloading small.en: progress shows in the tray panel. It joins the test when it arrives.";
    private const string SmallEnNotDownloaded = "small.en was not downloaded, so it was not added to the test.";

    private static Button Named(ILogical root, string name) => All<Button>(root).Single(b => AutomationProperties.GetName(b) == name);

    private static List<string?> Texts(ILogical root) => [.. All<TextBlock>(root).Select(t => t.Text)];

    /// <summary>Runs the UI thread's queued work until <paramref name="condition"/> holds.</summary>
    private static bool Eventually(Func<bool> condition, int timeoutMs = 10_000)
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

    /// <summary>
    /// The calibration window over a shell with the real <see cref="ShellDialogs"/>, no speech model on this PC (every catalogue
    /// model is offered as "Download and include"), and <paramref name="server"/> standing in for the download host.
    /// </summary>
    private static (AppShell Shell, CalibrationWindow Window, Scratch Scratch) Calibration(HttpMessageHandler server,
        string settings = """{ "hotkey": "F24" }""")
    {
        var scratch = new Scratch(settings);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var shell = new AppShell(new FakePlatform(scratch.Paths), scratch.Paths, NullLog.Instance, new SecretRedactor(), new ShellOptions(), new ShellDialogs(),
            gpuMachine: () => false, downloadHandler: server);
        shell.Reload(userRequested: false);
        var window = new CalibrationWindow(shell);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (shell, window, scratch);
    }

    /// <summary>
    /// The download question that "Download and include" opens is the calibration window's modal dialog, centred on it: the
    /// button is a <see cref="Ui.Button"/>, whose click names its window as the opener (<see cref="Modal.From"/>).
    /// </summary>
    [Fact]
    public Task The_calibration_download_question_is_the_calibration_windows_modal_dialog() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var server = new CountingHandler();
        var (shell, window, scratch) = Calibration(server);
        using var _ = scratch;

        (WindowBase? Owner, WindowStartupLocation Where, bool Owns)? seen = null;
        Inside(opened, q =>
        {
            seen = (q.Owner, q.WindowStartupLocation, window.OwnedWindows.Contains(q));
            q.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        });
        Named(window, IncludeSmallEn).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Dictum: Download a model", Assert.Single(opened.Windows).Title);
        Assert.NotNull(seen);
        Assert.Same(window, seen.Value.Owner);
        Assert.True(seen.Value.Owns);
        Assert.Equal(WindowStartupLocation.CenterOwner, seen.Value.Where);
        Assert.Empty(window.OwnedWindows); // closed: the calibration window is no longer its owner
        Assert.Equal(0, server.Requests);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// While the download question is open the calibration window still offers "Download and include" and says nothing new, and
    /// Cancel leaves it so: no "Downloading…", no "was not downloaded", no request to the download host.
    /// </summary>
    [Fact]
    public Task Declining_the_calibration_download_leaves_the_window_as_it_was_and_downloads_nothing() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var server = new CountingHandler();
        var (shell, window, scratch) = Calibration(server);
        using var _ = scratch;
        var before = Texts(window);

        (object? Label, bool Enabled, List<string?> Texts)? whileAsked = null;
        Inside(opened, q =>
        {
            var b = Named(window, IncludeSmallEn);
            whileAsked = (b.Content, b.IsEnabled, Texts(window));
            Click(q, "Cancel");
        });
        Named(window, IncludeSmallEn).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); // what the declined download posts back to the window

        Assert.Equal(1, Assert.Single(opened.Windows).Choice); // Cancel
        Assert.NotNull(whileAsked);
        Assert.Equal("Download and include", whileAsked.Value.Label); // nothing has started while the question is up
        Assert.True(whileAsked.Value.Enabled);
        Assert.Equal(before, whileAsked.Value.Texts);
        var button = Named(window, IncludeSmallEn);
        Assert.Equal("Download and include", button.Content);
        Assert.True(button.IsEnabled);
        Assert.Equal(before, Texts(window)); // no "Downloading small.en…", no "was not downloaded"
        Assert.Equal(0, server.Requests);
        Assert.False(shell.Models.IsDownloading);
        Assert.Equal(0, shell.Models.DownloadsEnded);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Download answers the question: the button then reads "Downloading…", disabled, the line under the results says where the
    /// progress shows, and the download really starts (the fake host refuses it, and the window then says so, as before).
    /// </summary>
    [Fact]
    public Task Agreeing_to_the_calibration_download_starts_it_and_says_so() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var server = new CountingHandler(); // answers 403: the download starts and then fails, without the network
        var (shell, window, scratch) = Calibration(server);
        using var _ = scratch;

        Inside(opened, q => Click(q, "Download"));
        Named(window, IncludeSmallEn).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(0, Assert.Single(opened.Windows).Choice);
        var button = Named(window, IncludeSmallEn);
        Assert.Equal("Downloading…", button.Content);
        Assert.False(button.IsEnabled);
        Assert.Contains(DownloadingSmallEn, Texts(window));

        // The download really started: the server refused it, and the window then says so and offers the button again.
        Assert.True(Eventually(() => Texts(window).Contains(SmallEnNotDownloaded)), "the failed download was never reported");
        Assert.Equal(1, server.Requests);
        button = Named(window, IncludeSmallEn);
        Assert.Equal("Download and include", button.Content);
        Assert.True(button.IsEnabled);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// A list redrawn while the question is open (a test run that ends then redraws it) still shows the download once it is
    /// agreed: the window redraws from the session instead of changing the button that was clicked, which is no longer shown.
    /// </summary>
    [Fact]
    public Task A_list_redrawn_while_the_calibration_question_is_open_still_shows_the_download() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var server = new CountingHandler();
        var (shell, window, scratch) = Calibration(server);
        using var _ = scratch;
        var redraw = typeof(CalibrationWindow).GetMethod("RenderMissing", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Inside(opened, q =>
        {
            redraw.Invoke(window, null); // what the end of a test run does (RunAsync's finally)
            Click(q, "Download");
        });
        Named(window, IncludeSmallEn).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var button = Named(window, IncludeSmallEn);
        Assert.Equal("Downloading…", button.Content);
        Assert.False(button.IsEnabled);
        Assert.Contains(DownloadingSmallEn, Texts(window));
        Assert.True(Eventually(() => Texts(window).Contains(SmallEnNotDownloaded)), "the failed download was never reported");
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// A download refused before the question (offline_only here; the coordinator's balloon says why) asks nothing and, as
    /// before, says under the results that the model was not downloaded: only a declined question leaves the window as it was.
    /// </summary>
    [Fact]
    public Task A_refused_calibration_download_asks_nothing_and_says_it_was_not_downloaded() => UiSession.Run(() =>
    {
        using var opened = new Opened();
        var server = new CountingHandler();
        var (shell, window, scratch) = Calibration(server, """{ "hotkey": "F24", "offline_only": true }""");
        using var _ = scratch;

        Named(window, IncludeSmallEn).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); // what the refused download posts back to the window

        Assert.Empty(opened.Windows);
        Assert.Contains(SmallEnNotDownloaded, Texts(window));
        var button = Named(window, IncludeSmallEn);
        Assert.Equal("Download and include", button.Content);
        Assert.True(button.IsEnabled);
        Assert.Equal(0, server.Requests);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// The real Win32 check (Dictum.UiProbe --dialog-capture, in a child process: this one hosts the headless platform): a
    /// Ui.Button clicked with WM_LBUTTONDOWN and WM_LBUTTONUP opens a question through ShellDialogs, and inside the dialog's
    /// frame GetCapture() is not the opener, the opener is disabled and owns the dialog, the dialog is on top even of a Topmost
    /// opener, a second click opens nothing, OK clicked with mouse messages answers 0, and focus returns to the button.
    /// </summary>
    [Fact]
    public void Dialog_opened_by_a_click_gets_the_mouse_on_Win32()
    {
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 on an interactive desktop (shows windows for a few seconds).");
        var probe = PillFocusTests.ProbePath();
        Assert.True(File.Exists(probe), $"{probe} is missing: build tests\\Dictum.UiProbe");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(probe, "--dialog-capture") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line) lines.Add(line);
        Assert.True(process.WaitForExit(60_000), "the probe did not finish");
        foreach (var line in lines) output.WriteLine(line);
        Assert.DoesNotContain(lines, l => l.StartsWith("FAIL", StringComparison.Ordinal));
        Assert.Equal(0, process.ExitCode);
        foreach (var opener in new[] { "ordinary opener", "Topmost opener", "opener closed while its dialog is up" })
            Assert.Contains(lines, l => l.StartsWith($"PASS {opener}: inside the dialog's frame the opener no longer holds the mouse capture", StringComparison.Ordinal));
    }
}
