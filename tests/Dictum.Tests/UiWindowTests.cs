using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Settings;
using Dictum.UI.Overlays;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// The windows on Avalonia's headless platform: they build, show the right words and wire their buttons. Nothing reaches
/// the desktop, so these need no gate. Whether a real window takes focus is checked out of process (PillFocusTests).
/// </summary>
public class UiWindowTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Select(t => t.Text));

    [Fact]
    public Task The_pill_never_takes_focus_and_lets_clicks_through() => UiSession.Run(() =>
    {
        var pill = new PillWindow(null);
        Assert.False(pill.ShowActivated);
        Assert.False(pill.Focusable);
        Assert.False(pill.IsHitTestVisible);
        Assert.False(pill.ShowInTaskbar);
        Assert.True(pill.Topmost);
        Assert.Equal(SystemDecorations.None, pill.SystemDecorations);
        Assert.False(pill.IsVisible); // built without showing: nothing can take focus
        pill.Close();
    });

    [Fact]
    public Task The_pill_shows_each_state_in_the_designed_words() => UiSession.Run(() =>
    {
        var pill = new PillWindow(null);
        var start = Environment.TickCount64;
        pill.Present(new PillState(PillView.RecordingHold, start));
        Assert.True(pill.IsVisible);
        Assert.Equal("Recording · hold", pill.Text);
        pill.Present(new PillState(PillView.RecordingHandsFree, start));
        Assert.StartsWith("Hands-free 0:0", pill.Text);
        Assert.Contains(PillText.HandsFreeHint, Texts(pill));
        pill.Present(new PillState(PillView.Transcribing, start));
        Assert.Equal("Transcribing…", pill.Text);
        pill.Present(new PillState(PillView.Polishing, start));
        Assert.Equal("Polishing…", pill.Text);
        pill.Present(new PillState(PillView.Done, start, Words: 12));
        Assert.Equal("Pasted 12 words", pill.Text);
        pill.Present(new PillState(PillView.Cancelled, start));
        Assert.Equal("Cancelled", pill.Text);
        pill.Close();
    });

    [Fact]
    public Task Reduced_motion_turns_the_fades_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var platform = new FakePlatform(scratch.Paths);
        var calm = new PillWindow(platform);
        platform.Flags.Reduced = true;
        calm.Present(new PillState(PillView.RecordingHold, Environment.TickCount64));
        Assert.Empty(calm.FindControl<Border>("Pill")!.Transitions ?? []);
        calm.Close();
        platform.Flags.Reduced = false;
        var moving = new PillWindow(platform);
        moving.Present(new PillState(PillView.RecordingHold, Environment.TickCount64));
        Assert.Single(moving.FindControl<Border>("Pill")!.Transitions!);
        moving.Close();
    });

    [Fact]
    public Task The_orb_and_the_edge_follow_the_indicator_setting() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "indicator": { "mode": "both" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var overlays = new OverlayPresenter(shell);
        var start = Environment.TickCount64;
        overlays.Show(new PillState(PillView.RecordingHold, start));
        Assert.True(overlays.Pill!.IsVisible);
        Assert.True(overlays.Orb!.IsVisible);
        Assert.True(overlays.EdgesShown);
        overlays.Show(new PillState(PillView.Polishing, start));
        Assert.True(overlays.Orb.IsVisible); // the orb stays up, amber, while polishing
        Assert.Equal(PillView.Polishing, overlays.Orb.View);
        Assert.False(overlays.EdgesShown); // the edge glows only while recording
        overlays.Show(new PillState(PillView.Done, start, 3));
        Assert.False(overlays.Orb.IsVisible);
        overlays.Dispose();
        shell.Dispose();

        using var off = new Scratch("""{ "hotkey": "F24", "pill": { "enabled": false } }""");
        var (quiet, _, _) = off.Shell();
        quiet.Reload(userRequested: false);
        var none = new OverlayPresenter(quiet);
        none.Show(new PillState(PillView.RecordingHold, start));
        Assert.Null(none.Pill);
        Assert.Null(none.Orb);
        Assert.False(none.EdgesShown);
        quiet.Dispose();
    });

    [Fact]
    public Task Fix_last_offers_unticked_corrections_and_saves_only_the_ticked_ones() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new FixLastWindow(shell, new LastDictation("Ask kinney about the cafe.", 42, "Outlook", DateTime.UtcNow));
        window.Show();
        Assert.True(window.Topmost);
        Assert.Contains("into Outlook", Texts(window));
        var text = All<TextBox>(window).Single();
        Assert.Equal("Ask kinney about the cafe.", text.Text);
        text.Text = "Ask Kynni about the café.";
        Click(window, "Save corrections"); // lists the changes first; nothing is ticked, so nothing is written
        var boxes = All<CheckBox>(window).ToList();
        Assert.Equal(["Add correction: kinney to Kynni", "Add correction: cafe to café"], boxes.Select(AutomationProperties.GetName));
        Assert.All(boxes, b => Assert.False(b.IsChecked)); // unticked by default
        Assert.False(File.Exists(scratch.Paths.CorrectionsFile));
        boxes[0].IsChecked = true;
        Click(window, "Save corrections");
        var written = File.ReadAllLines(scratch.Paths.CorrectionsFile).Where(l => !l.StartsWith('#') && l.Length > 0).ToList();
        Assert.Equal(["kinney => Kynni"], written);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_calibration_window_starts_at_the_first_sentence_and_lists_missing_models() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"), scratch.Paths.CalibrationScriptFile);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new CalibrationWindow(shell);
        window.Show();
        var texts = Texts(window);
        Assert.Contains(CalibrationWindow.Framing, texts);
        Assert.Contains("Sentence 1 of 10", texts);
        Assert.Contains("or hold F24", texts);
        Assert.Equal(ModelCatalog.Entries.Count, All<Button>(window).Count(b => b.Content as string == "Download and include")); // an empty models folder
        Click(window, "Run test");
        Assert.Contains("Record at least one sentence first", Texts(window));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Settings_opens_every_page_with_its_controls() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        foreach (var page in new[] { "General", "Mode", "Models", "Storage", "Microphone", "Clean-up", "Privacy", "About" })
        {
            window.ShowPage(page);
            var shown = All<SettingsPage>(window).Single(p => p.IsVisible && p.Parent is ContentControl);
            Assert.Equal(page, shown.PageTitle);
        }
        window.ShowPage("Storage");
        Assert.Contains(All<Button>(window), b => b.Content as string == "Remove Dictum completely…");
        window.ShowPage("About");
        Assert.Contains(All<Button>(window), b => b.Content as string == "Report a problem");
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_tray_panel_shows_the_status_and_the_privacy_line() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var texts = Texts(flyout);
        Assert.Contains("Nothing leaves this PC", texts);
        Assert.Contains("Full: ", texts);
        foreach (var label in new[] { "Calibrate my voice", "Dictionary", "Fix last dictation", "Settings", "Quit" })
            Assert.Contains(All<Button>(flyout), b => b.Content as string == label);
        flyout.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_hotkey_box_shows_what_arrived_lights_while_held_and_pauses_dictation() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: true); // installs the (fake) dictation hook
        var hook = platform.LastListener!;
        var box = new HotkeyCaptureBox(shell, "F24", listenAtOnce: false);
        var window = new Window { Content = box };
        window.Show();
        box.Start();
        Assert.False(hook.Enabled); // holding a key here must not start a dictation
        var monitor = platform.LastMonitor!;
        monitor.Press(Vk.RMenu, down: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Windows received: Right Alt down", Texts(box));
        Assert.Contains("Hold to test", Texts(box)); // F24 is the chosen key, and it is not held
        monitor.Press(Vk.RMenu, down: false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("RightAlt", box.Hotkey);
        monitor.Press(Vk.RMenu, down: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Held: Dictum sees this key", Texts(box));
        box.Stop();
        Assert.True(monitor.Disposed);
        Assert.True(hook.Enabled);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Light_and_dark_follow_the_system_and_every_token_has_both() => UiSession.Run(() =>
    {
        var app = Avalonia.Application.Current!;
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant); // follows the system setting
        foreach (var key in new[] { "WindowBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "HairlineBrush", "ControlBorderBrush", "MeterOffBrush", "LinkBrush" })
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var light), $"{key} (light)");
            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var dark), $"{key} (dark)");
            Assert.NotEqual(light?.ToString(), dark?.ToString());
        }
    });
}
