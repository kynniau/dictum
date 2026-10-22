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
