using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Platform;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// The input-access grant (IPlatform.InputAccess; docs/DECISIONS.md "Request access", 27/10/2026): Windows has none to ask for
/// and its surfaces are unchanged; where the grant is missing, every surface that fails for want of it shows the state and one
/// Request access button, and the grant arriving restarts the listeners without reopening Dictum. The window tests run on the
/// fake platform with a scripted grant; the two real-platform pins run each on its own system.
/// </summary>
public sealed class InputAccessTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string> VisibleLines(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    private static Button? RequestButton(ILogical root) =>
        All<Button>(root).SingleOrDefault(b => AutomationProperties.GetName(b) == InputAccessText.RequestLabel && b.IsEffectivelyVisible);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static (SettingsWindow Window, GeneralPage Page) General(Dictum.UI.Shell.AppShell shell)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        Dispatcher.UIThread.RunJobs();
        return (window, All<GeneralPage>(window).Single());
    }

    // ---- The seam ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_platform_with_no_grant_answers_not_applicable_and_a_request_does_nothing()
    {
        Assert.Equal(InputAccessState.NotApplicable, InputAccessNotNeeded.Instance.Check());
        InputAccessNotNeeded.Instance.Request();
        Assert.Equal(InputAccessState.NotApplicable, InputAccessNotNeeded.Instance.Check());
    }

    /// <summary>The Windows pin: the real platform has no grant to ask for, so nothing on Windows ever shows the line. Windows only.</summary>
    [Fact]
    public void The_windows_platform_has_no_grant_to_ask_for()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsPlatform is built only on Windows (it calls kernel32 when constructed).");
        WindowsPlatformCheck();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WindowsPlatformCheck()
    {
        using var windows = new Dictum.Windows.WindowsPlatform(NullLog.Instance, AppContext.BaseDirectory);
        Assert.Same(InputAccessNotNeeded.Instance, windows.InputAccess);
    }

    /// <summary>The real Mac platform answers Granted or Denied, never NotApplicable, and its Settings link is the verified one. macOS only.</summary>
    [Fact]
    public void The_mac_platform_checks_the_accessibility_grant()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "MacPlatform is built only on macOS.");
        MacPlatformCheck();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MacPlatformCheck()
    {
        using var mac = new Dictum.Mac.MacPlatform(NullLog.Instance, Path.GetTempPath());
        var expected = Dictum.Mac.Interop.AX.IsProcessTrusted() ? InputAccessState.Granted : InputAccessState.Denied;
        Assert.Equal(expected, mac.InputAccess.Check());
        Assert.Equal("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility", Dictum.Mac.Input.MacInputAccess.SettingsUrl);
    }

    // ---- Windows surfaces unchanged -------------------------------------------------------------------------------

    /// <summary>With no grant to ask for, a refused listener shows no access line, no button, and the box keeps its log pointer.</summary>
    [Fact]
    public Task With_no_grant_to_ask_for_the_surfaces_show_nothing_new() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        Assert.Equal(InputAccessState.NotApplicable, shell.Access.State);

        var (window, _) = General(shell);
        var lines = VisibleLines(window);
        Assert.DoesNotContain(lines, l => l.Contains("Request access") || l.Contains("allowed to control"));
        Assert.Null(RequestButton(window));

        var box = All<HotkeyCaptureBox>(window).First();
        box.Start();
        Assert.False(box.Listening);
        Assert.Contains("The system refused to report keys, so the hotkey cannot be checked here. The log has details.", VisibleLines(box));
        Assert.Null(RequestButton(box));
        window.Close();
        shell.Dispose();
    });

    // ---- The grant missing ----------------------------------------------------------------------------------------

    [Fact]
    public Task A_listener_refused_for_want_of_the_grant_shows_the_line_and_the_button_requests_it() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        Assert.Equal(InputAccessState.Denied, shell.Access.State);

        var (window, _) = General(shell);
        var lines = VisibleLines(window);
        Assert.Contains(InputAccessText.Denied(PlatformWords.Mac), lines);
        Assert.Contains(InputAccessText.StaleEntryNote, lines);
        Assert.Contains("this Mac", InputAccessText.Denied(PlatformWords.Mac));

        var button = RequestButton(window);
        Assert.NotNull(button);
        Click(button);
        Assert.Equal(1, platform.FakeAccess.Requests);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_grant_arriving_restarts_the_listeners_without_reopening_dictum() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var (window, _) = General(shell);
        var refused = platform.Listeners.Count;
        Assert.True(refused >= 2);

        platform.FakeAccess.State = InputAccessState.Granted;
        platform.RefuseListeners = false;
        shell.Access.Tick();

        Assert.Equal(InputAccessState.Granted, shell.Access.State);
        Assert.False(shell.Access.RestartFailed);
        Assert.Equal(refused + 2, platform.Listeners.Count); // the dictation listener and the fix-last one, both replaced
        Assert.All(platform.Listeners.Skip(refused), l => Assert.False(l.Refused));
        Assert.All(platform.Listeners.Take(refused), l => Assert.True(l.Disposed));
        var lines = VisibleLines(window);
        Assert.Contains(InputAccessText.Granted, lines);
        Assert.DoesNotContain(InputAccessText.Denied(PlatformWords.Mac), lines);
        Assert.Null(RequestButton(window));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task A_grant_that_still_leaves_the_listener_refused_says_so() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var (window, _) = General(shell);

        platform.FakeAccess.State = InputAccessState.Granted; // the system says yes, the listener still fails
        shell.Access.Tick();

        Assert.True(shell.Access.RestartFailed);
        Assert.Contains(InputAccessText.RestartFailed, VisibleLines(window));
        Assert.Null(RequestButton(window));
        window.Close();
        shell.Dispose();
    });

    /// <summary>A box on its own (the Welcome hotkey step) carries the line itself; on Settings &gt; General the card's line serves both boxes.</summary>
    [Fact]
    public Task The_capture_box_refused_for_want_of_the_grant_shows_the_button_and_listens_once_granted() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var box = new HotkeyCaptureBox(shell, "F24", listenAtOnce: false);
        var window = new Window { Content = box };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        box.Start();
        Assert.False(box.Listening);
        Assert.Contains(InputAccessText.BoxDenied(PlatformWords.Mac), VisibleLines(box));
        var button = RequestButton(box);
        Assert.NotNull(button);
        Click(button);
        Assert.Equal(1, platform.FakeAccess.Requests);

        platform.FakeAccess.State = InputAccessState.Granted;
        platform.RefuseListeners = false;
        shell.Access.Tick();
        Assert.True(box.Listening); // the box picks up where the refusal stopped it
        Assert.Null(RequestButton(box));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Settings &gt; General: one line and one button for the card, and a refused box there says why without a second button.</summary>
    [Fact]
    public Task The_general_page_shows_one_request_button_even_with_a_refused_box() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var (window, _) = General(shell);
        var box = All<HotkeyCaptureBox>(window).First();
        box.Start();
        Assert.Contains(InputAccessText.BoxDenied(PlatformWords.Mac), VisibleLines(box));
        Assert.Single(All<Button>(window), b => AutomationProperties.GetName(b) == InputAccessText.RequestLabel && b.IsEffectivelyVisible);
        window.Close();
        shell.Dispose();
    });

    /// <summary>The menu-bar panel: its "!" explains itself with the same line and a Request access link; with no grant to ask for, no line and no link.</summary>
    [Fact]
    public Task The_panel_says_why_the_icon_shows_the_error_and_offers_the_request() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        Assert.Null(shell.Status.Problem); // Windows: a refused hook has no grant to blame
        Assert.False(shell.Status.RequestAccess);

        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        shell.Reload(userRequested: true);
        Assert.Equal(InputAccessText.Denied(PlatformWords.Mac), shell.Status.Problem);
        Assert.True(shell.Status.RequestAccess);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(InputAccessText.Denied(PlatformWords.Mac), VisibleLines(flyout));
        var link = RequestButton(flyout);
        Assert.NotNull(link);
        Click(link);
        Assert.Equal(1, platform.FakeAccess.Requests);

        platform.FakeAccess.State = InputAccessState.Granted;
        platform.RefuseListeners = false;
        shell.Access.Tick();
        Assert.Null(shell.Status.Problem);
        Assert.Null(RequestButton(flyout));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>The names a screen reader hears: the button is named, and the status is a polite live region.</summary>
    [Fact]
    public Task The_line_is_named_for_assistive_technology() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var (window, _) = General(shell);
        var line = All<InputAccessLine>(window).First(l => l.IsEffectivelyVisible);
        var status = All<TextBlock>(line).First(t => t.Text == InputAccessText.Denied(PlatformWords.Mac));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.Equal(InputAccessText.RequestLabel, AutomationProperties.GetName(RequestButton(line)!));
        Assert.True(RequestButton(line)!.Focusable);
        window.Close();
        shell.Dispose();
    });
}
