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

    // ---- The approved words, byte for byte ------------------------------------------------------------------------

    // pin: APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, rows 1 to 6)
    [Fact]
    public void The_six_screen_strings_are_the_approved_words()
    {
        Assert.Equal("Dictum is not allowed to control this Mac yet, so it cannot see the hotkey or paste. Press Request access, then turn Dictum on in the System Settings page that opens.", InputAccessText.Denied(PlatformWords.Mac)); // row 1
        Assert.Equal("If Dictum is already listed there and on but still refused, remove it from that list with the \u2212 button, then press Request access again.", InputAccessText.StaleEntryNote); // row 2
        Assert.Equal("Request access", InputAccessText.RequestLabel); // row 3
        Assert.Equal("Dictum can now see the hotkey.", InputAccessText.Granted); // row 4
        Assert.Equal("Dictum cannot listen for keys until it is allowed to control this Mac.", InputAccessText.BoxDenied(PlatformWords.Mac)); // row 5
        Assert.Equal("Access was granted, but the hotkey still could not be set up. Quit and reopen Dictum.", InputAccessText.RestartFailed); // row 6
    }

    // pin: APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 7): the log line when a tap cannot install
    [Fact]
    public void The_tap_log_line_is_the_approved_words()
    {
        var log = new RecordingLog();
        using (var hotkey = new Dictum.Mac.Input.EventTapHotkey(new Dictum.Hotkey.HotkeyMatcher(Dictum.Hotkey.HotkeyParser.Parse("RightAlt"), suppress: true), _ => { }, log, trusted: () => false))
            Assert.False(hotkey.Start());
        var errors = log.Lines.Where(l => l.Level == LogLevel.Error).Select(l => l.Message).ToList();
        var line = Assert.Single(errors);
        const string tail = ") not installed: Dictum is not allowed to control this Mac yet. Press Request access in Settings > General (or the menu-bar panel), or turn Dictum on in System Settings > Privacy & Security > Accessibility; the hotkey starts working as soon as it is on.";
        Assert.StartsWith("Event tap (", line, StringComparison.Ordinal);
        Assert.EndsWith(tail, line, StringComparison.Ordinal);
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
        Assert.DoesNotContain(lines, l => l.Contains("Request access") || l.Contains("allowed to control") || l == InputAccessText.Granted || l == InputAccessText.RestartFailed);
        Assert.DoesNotContain(All<InputAccessLine>(window), l => l.IsEffectivelyVisible); // the page is laid out as before the line existed
        Assert.Null(RequestButton(window));
        Assert.Null(shell.Status.Problem);
        Assert.False(shell.Status.RequestAccess);

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

    // ---- Merge lens findings, PR #82 (27/10/2026) ------------------------------------------------------------------

    /// <summary>
    /// MAJOR 1: on a fresh install the Welcome hotkey step's box runs before the shell installs its own listener, so nothing has
    /// asked the system yet. The box asks itself: the approved line and the button, not the old dead end, with no shell reload.
    /// </summary>
    [Fact]
    public Task The_welcome_box_asks_for_itself_before_the_shell_has_installed_a_listener() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true; // no shell.Reload: the shell's own listener has never been tried
        Assert.Equal(InputAccessState.NotApplicable, shell.Access.State);
        var box = new HotkeyCaptureBox(shell, "F24", listenAtOnce: false);
        var window = new Window { Content = box };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        box.Start();
        Assert.False(box.Listening);
        Assert.Equal(InputAccessState.Denied, shell.Access.State);
        Assert.True(shell.Access.Polling);
        var lines = VisibleLines(box);
        Assert.Contains(InputAccessText.BoxDenied(PlatformWords.Mac), lines);
        Assert.Contains(InputAccessText.Denied(PlatformWords.Mac), lines);
        Assert.DoesNotContain("The system refused to report keys, so the hotkey cannot be checked here. The log has details.", lines);
        Assert.NotNull(RequestButton(box));
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// MAJOR 1: a grant that arrives during the Welcome window (before Start installs the listeners) installs nothing there, so
    /// Start's own install is the only one: one live dictation listener after Start, none left undisposed beside it.
    /// </summary>
    [Fact]
    public Task A_grant_during_the_welcome_window_leaves_one_listener_after_start() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        OnboardingTests.Bundle(scratch);
        var (shell, platform, dialogs) = scratch.Shell(OnboardingTests.FakeModels());
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        var listenedInWelcome = false;
        dialogs.Onboarding = flow =>
        {
            flow.AcceptTerms();
            var box = new HotkeyCaptureBox(shell, flow.Hotkey, listenAtOnce: false); // the hotkey step's box
            var window = new Window { Content = box };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            box.Start();
            Assert.NotNull(RequestButton(box));
            platform.FakeAccess.State = InputAccessState.Granted;
            platform.RefuseListeners = false;
            shell.Access.Tick();
            listenedInWelcome = box.Listening;
            Assert.Empty(platform.Listeners); // nothing installed before Start
            window.Close();
        };
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.True(listenedInWelcome); // the box picked up again once granted
            var live = platform.Listeners.Where(l => !l.Disposed && !l.Refused).ToList();
            Assert.Equal(shell.Settings.FixHotkey is null ? 1 : 2, live.Count);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>
    /// MAJOR 2: polling stopped, the user turned Dictum on by hand, then pressed Request access: the grant already in place
    /// installs the listeners, not only the words.
    /// </summary>
    [Fact]
    public Task Request_access_with_the_grant_already_in_place_installs_the_listeners() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var (window, _) = General(shell);
        var before = platform.Listeners.Count;

        platform.FakeAccess.State = InputAccessState.Granted; // switched on in System Settings; no tick saw it
        platform.RefuseListeners = false;
        Click(RequestButton(window)!);

        Assert.Equal(1, platform.FakeAccess.Requests);
        Assert.Equal(InputAccessState.Granted, shell.Access.State);
        Assert.False(shell.Access.RestartFailed);
        Assert.Equal(before + 2, platform.Listeners.Count);
        Assert.All(platform.Listeners.Skip(before), l => Assert.False(l.Refused));
        Assert.False(shell.Access.Polling);
        Assert.Null(shell.Status.Problem);
        window.Close();
        shell.Dispose();
    });

    /// <summary>MINOR 3: a restart that throws from the timer is caught: logged, shown as a grant the listener could not use, polling stopped.</summary>
    [Fact]
    public Task A_restart_that_throws_is_caught_and_shown_as_a_failed_restart() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var log = new RecordingLog();
        var (shell, platform, _) = scratch.Shell(log: log);
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        Assert.True(shell.Access.Polling);

        platform.FakeAccess.State = InputAccessState.Granted;
        platform.RefuseListeners = false;
        platform.ThrowNextListener = true;
        Assert.Null(Record.Exception(() => shell.Access.Tick()));

        Assert.Equal(InputAccessState.Granted, shell.Access.State);
        Assert.True(shell.Access.RestartFailed);
        Assert.False(shell.Access.Polling);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.StartsWith("Input access restart failed: ", StringComparison.Ordinal));
        shell.Dispose();
    });

    /// <summary>
    /// MINOR 4: "Dictum can now see the hotkey." is said for a grant that arrives while the line is on screen, never for one
    /// already in place when the page opens (it read as a claim on every later visit, even after the grant was revoked).
    /// </summary>
    [Fact]
    public Task The_granted_line_shows_only_for_a_grant_seen_while_attached() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);

        var (first, _) = General(shell);
        platform.FakeAccess.State = InputAccessState.Granted;
        platform.RefuseListeners = false;
        shell.Access.Tick();
        Assert.Contains(InputAccessText.Granted, VisibleLines(first)); // the live Denied-to-Granted change
        first.Close();

        Assert.Equal(InputAccessState.Granted, shell.Access.State);
        var (later, _) = General(shell);
        Assert.DoesNotContain(InputAccessText.Granted, VisibleLines(later));
        Assert.DoesNotContain(All<InputAccessLine>(later), l => l.IsEffectivelyVisible);
        later.Close();

        var detached = new InputAccessLine(shell); // built while Granted, then attached: still nothing to say
        var window = new Window { Content = detached };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(detached.IsVisible);
        Assert.Null(detached.StatusText);
        window.Close();
        shell.Dispose();
    });

    /// <summary>MINOR 5: on Settings &gt; General two refused boxes do not both resume on the grant; the card's line is the cue and Change… starts one.</summary>
    [Fact]
    public Task Two_refused_general_boxes_do_not_resume_on_the_grant() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "fix_hotkey": "F23" }""");
        var (shell, platform, _) = scratch.Shell();
        platform.Words = PlatformWords.Mac;
        platform.FakeAccess.State = InputAccessState.Denied;
        platform.RefuseListeners = true;
        shell.Reload(userRequested: true);
        var (window, _) = General(shell);
        var boxes = All<HotkeyCaptureBox>(window).ToList();
        Assert.Equal(2, boxes.Count);
        foreach (var box in boxes) box.Start();
        Assert.All(boxes, b => Assert.False(b.Listening));

        platform.FakeAccess.State = InputAccessState.Granted;
        platform.RefuseListeners = false;
        shell.Access.Tick();
        Assert.All(boxes, b => Assert.False(b.Listening));
        Assert.Contains(InputAccessText.Granted, VisibleLines(window));
        window.Close();
        shell.Dispose();
    });

    /// <summary>MINOR 6: polling stops once <see cref="Dictum.UI.Shell.InputAccessWatch.PollFor"/> has passed on the injected clock, and not before.</summary>
    [Fact]
    public Task Polling_stops_after_the_cut_off_on_the_injected_clock() => UiSession.Run(() =>
    {
        var access = new FakeInputAccess { State = InputAccessState.Denied };
        var clock = new FakeTimeProvider();
        var watch = new Dictum.UI.Shell.InputAccessWatch(access, NullLog.Instance, () => true, clock);
        try
        {
            watch.ListenerRefused();
            Assert.True(watch.Polling);
            clock.Advance(Dictum.UI.Shell.InputAccessWatch.PollFor);
            watch.Tick();
            Assert.True(watch.Polling); // at the cut-off, still watching
            clock.Advance(TimeSpan.FromSeconds(1));
            watch.Tick();
            Assert.False(watch.Polling);
            Assert.Equal(InputAccessState.Denied, watch.State);
        }
        finally
        {
            watch.Dispose();
        }
    });
}
