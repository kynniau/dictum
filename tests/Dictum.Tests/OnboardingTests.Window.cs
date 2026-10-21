using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>The onboarding window's Try it step, its size and placement on the screen, and its keyboard focus.</summary>
public sealed partial class OnboardingTests
{
    // ---- 6. Try it, and the shell's own path ----

    /// <summary>
    /// Pin: through the shell's own first run (Start, with the window driven as a person would), steps 1-5 write their answers,
    /// the settings load, Try it waits for the speech model, and a dictation through the real hotkey path shows the accent
    /// explainer and Calibrate my voice; Finish closes the window.
    /// </summary>
    [Fact]
    public Task Try_it_after_the_first_dictation_shows_the_accent_explainer() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, dialogs) = scratch.Shell(FakeModels());
        OnboardingWindow? window = null;
        dialogs.Onboarding = flow =>
        {
            window = new OnboardingWindow(flow);
            window.Show();
            while (window.Current is not TryItStep) Next(window);
        };
        try
        {
            Assert.True(shell.Start(lifetime: null));
            Assert.Equal((0, 0, 1), dialogs.FirstRunSteps); // the window, once; no picker
            var tryIt = Assert.IsType<TryItStep>(window!.Current);
            Assert.True(dialogs.LastOnboarding!.TryItLive);
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            tryIt.Tick();
            Assert.Equal(OnboardingText.TryInstruction("RightCtrl"), tryIt.Instruction);
            Assert.False(window.SecondaryButton.IsVisible); // Calibrate my voice comes after the first dictation

            var listener = platform.LastListener!;
            var t0 = Environment.TickCount64 + 60_000;
            listener.Post(new HookEvent(HotkeySignal.Down, t0));
            Assert.True(Eventually(() => !shell.DictationIdle), "the dictation did not start");
            listener.Post(new HookEvent(HotkeySignal.Up, t0 + 1_500));
            Assert.True(Eventually(() => shell.Personal.Last is not null), "nothing was pasted");
            tryIt.Tick();
            Assert.True(tryIt.Succeeded);
            Assert.Contains(OnboardingText.AccentExplainer, Visible(window));
            Assert.True(window.SecondaryButton.IsVisible);
            Assert.Equal(ModelsPage.CalibrateLabel, window.SecondaryButton.Content);
            Click(window.PrimaryButton); // Finish
            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window?.IsVisible == true) window.Close();
            shell.Dispose();
        }
    });

    /// <summary>Pin: with nothing pasted 30 seconds after the model is ready, the try-later line; Finish still closes.</summary>
    [Fact]
    public Task Try_it_says_nothing_came_through_after_thirty_seconds() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, dialogs) = scratch.Shell(FakeModels());
        var clock = new long[] { 5_000_000 };
        OnboardingWindow? window = null;
        dialogs.Onboarding = flow =>
        {
            flow.Clock = () => clock[0];
            window = new OnboardingWindow(flow);
            window.Show();
            while (window.Current is not TryItStep) Next(window);
        };
        try
        {
            Assert.True(shell.Start(lifetime: null));
            var tryIt = Assert.IsType<TryItStep>(window!.Current);
            Assert.True(Eventually(() => shell.SpeechReady), "the speech model did not load");
            tryIt.Tick(); // ready from now
            clock[0] += TryItStep.NothingAfterMs - 1;
            tryIt.Tick();
            Assert.False(tryIt.NothingShown);
            clock[0] += 1;
            tryIt.Tick();
            Assert.True(tryIt.NothingShown);
            Assert.Contains(OnboardingText.NothingCame, Visible(window));
            Assert.True(window.PrimaryButton.IsEnabled);
            Click(window.PrimaryButton);
            Assert.False(window.IsVisible);
        }
        finally
        {
            if (window?.IsVisible == true) window.Close();
            shell.Dispose();
        }
    });

    /// <summary>Pin: before the shell has loaded (steps 1-5 just ended), Try it says the model is getting ready, not the instruction.</summary>
    [Fact]
    public Task Try_it_waits_for_the_shell() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var tryIt = GoTo<TryItStep>(window);
        Assert.False(flow.TryItLive);
        Assert.Equal(OnboardingText.TryLoading, tryIt.Instruction);
        window.Close();
        shell.Dispose();
    });

    // ---- Accessibility and size ----

    private const double WorkAreaDip = 512 - 48;
    private const double CaptionDip = 32;

    /// <summary>
    /// Pin: every step fits a 1366 × 768 screen at 150 % (911 × 512 DIP, less a 48 px taskbar: 480 DIP), keeps its buttons inside
    /// the window (as the busy question's layout pin), names every control a keyboard reaches, and uses the larger text.
    /// </summary>
    [Fact]
    public Task Every_step_fits_at_150_percent_and_names_its_controls() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var (shell, _, _) = scratch.Shell(); // without base.en: the speech step's longest form
        var (_, window) = Open(shell, scratch);
        // 1366 × 768 at 150 % is 911 × 512 DIP; less the Windows 11 taskbar (recalled: 48 DIP) the work area is 464 DIP, and
        // the whole window is the client plus a caption (recalled: about 32 DIP). Both figures are recalled, not measured; a check
        // on a real 150 % desktop is still outstanding.
        Assert.True(OnboardingWindow.WindowWidth <= 911 && OnboardingWindow.WindowHeight + CaptionDip <= WorkAreaDip, $"{OnboardingWindow.WindowHeight} + {CaptionDip} > {WorkAreaDip}");
        while (true)
        {
            var step = window.Current!;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(OnboardingWindow.WindowHeight, window.ClientSize.Height);
            foreach (var button in new[] { window.PrimaryButton, window.SecondaryButton, window.BackButton }.Where(b => b.IsEffectivelyVisible))
            {
                var right = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width, 0), window)!.Value.X;
                var bottom = button.TranslatePoint(new Avalonia.Point(0, button.Bounds.Height), window)!.Value.Y;
                Assert.True(right <= window.ClientSize.Width - 24 + 0.5, $"{step.StepName}: \"{button.Content}\" ends at {right:0} of {window.ClientSize.Width:0}");
                Assert.True(bottom <= window.ClientSize.Height + 0.5, $"{step.StepName}: \"{button.Content}\" is below the window");
            }
            foreach (var control in All<Control>(step).Where(c => c is Button or RadioButton or ComboBox or TextBox && c.IsEffectivelyVisible))
            {
                var name = AutomationProperties.GetName(control);
                var label = control is ContentControl { Content: string s } ? s : null;
                Assert.False(string.IsNullOrWhiteSpace(name ?? label), $"{step.StepName}: a {control.GetType().Name} has no name");
                Assert.True(control.Focusable && control.IsTabStop, $"{step.StepName}: {name ?? label} is not reached by Tab");
            }
            foreach (var body in All<TextBlock>(step).Where(t => t.Classes.Contains("body") && t.IsEffectivelyVisible))
                Assert.True(body.FontSize >= 17, $"{step.StepName}: body text at {body.FontSize}");
            if (step is TryItStep) break;
            Next(window);
        }
        window.Close();
        shell.Dispose();
    });

    // ---- Lens fixes (21/10/2026), lens on 6bacce9 ----

    /// <summary>
    /// Pin (lens 3, then lens on 36fa43e): the whole placement is one pure function. At 150 % on 1366 × 768 with a 48 DIP taskbar
    /// (recalled, not measured: 72 px) the 420 DIP client and a 32 DIP caption (recalled) fit and the window is moved up into the
    /// work area; a client too tall is cut to the work area less the frame; on a second screen whose work area starts at a
    /// non-zero Y (above or below the main one) the window is kept on it or moved into it.
    /// </summary>
    [Fact]
    public void The_window_is_fitted_to_the_work_area_of_its_screen()
    {
        var laptop = new PixelRect(0, 0, 1366, 768 - 72);
        Assert.Equal((OnboardingWindow.WindowHeight, new PixelPoint(363, 9)), OnboardingWindow.Fit(OnboardingWindow.WindowHeight, CaptionDip, laptop, 1.5, new PixelPoint(363, 400)));
        Assert.Equal((432d, new PixelPoint(363, 0)), OnboardingWindow.Fit(470, CaptionDip, laptop, 1.5, new PixelPoint(363, 400)));
        Assert.Equal((OnboardingWindow.WindowHeight, new PixelPoint(10, 20)), OnboardingWindow.Fit(OnboardingWindow.WindowHeight, CaptionDip, new PixelRect(0, 0, 1920, 1032), 1, new PixelPoint(10, 20)));

        var above = new PixelRect(1920, -1080, 1920, 1032);
        Assert.Equal((OnboardingWindow.WindowHeight, new PixelPoint(2000, -1000)), OnboardingWindow.Fit(OnboardingWindow.WindowHeight, CaptionDip, above, 1, new PixelPoint(2000, -1000)));
        Assert.Equal((OnboardingWindow.WindowHeight, new PixelPoint(2000, -790)), OnboardingWindow.Fit(OnboardingWindow.WindowHeight, CaptionDip, above, 1, new PixelPoint(2000, 100)));
        var below = new PixelRect(0, 1080, 1920, 1032);
        Assert.Equal((OnboardingWindow.WindowHeight, new PixelPoint(100, 1370)), OnboardingWindow.Fit(OnboardingWindow.WindowHeight, CaptionDip, below, 1, new PixelPoint(100, 50)));
        Assert.Equal(300, OnboardingWindow.Fit(OnboardingWindow.WindowHeight, 40, new PixelRect(0, 0, 800, 340), 1, default).Height); // under the minimum: fits all the same
    }

    /// <summary>
    /// Pin (lens on 36fa43e): opening the window runs the placement on its screen (the Opened hook is wired). Lens on 86132af:
    /// the window then has the height and position <see cref="OnboardingWindow.Fit"/> worked out, not only worked them out.
    /// </summary>
    [Fact]
    public Task Opening_the_window_fits_it_to_its_screen() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var flow = new OnboardingFlow(shell, SettingsLoader.LoadFile(scratch.Paths.SettingsFile), askCleanup: true);
        var window = new OnboardingWindow(flow) { Height = 20_000, Position = new PixelPoint(0, 1_000_000) }; // taller than the screen, and below it
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, window.FitsOnOpen);
        var fit = Assert.NotNull(window.LastFit);
        Assert.True(fit.Height < 20_000 && fit.Position.Y < 1_000_000, $"Fit changed nothing: {fit}");
        Assert.Equal(fit, (window.Height, window.Position)); // applied on opening

        var laptop = OnboardingWindow.Fit(window.Height, CaptionDip, new PixelRect(0, 0, 1366, 684), 1.75, new PixelPoint(100, 600)); // 1366 x 768 at 175 %
        Assert.True(laptop.Height < window.Height && laptop.Position != window.Position, $"{laptop} against {(window.Height, window.Position)}");
        window.Apply(laptop);
        Assert.Equal(laptop, (window.Height, window.Position));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 4): Try it puts the keyboard focus in the practice box, not on Finish.</summary>
    [Fact]
    public Task Try_it_focuses_the_practice_box() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        var tryIt = GoTo<TryItStep>(window);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(tryIt.Box, window.FocusManager!.GetFocusedElement());
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 5): the hotkey step gives focus to nothing, as the old hotkey window did, so Space cannot press a button.</summary>
    [Fact]
    public Task The_hotkey_step_focuses_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        GoTo<HotkeyStep>(window);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.FocusManager!.GetFocusedElement() is Button, $"focus is on {window.FocusManager.GetFocusedElement()}");
        window.Close();
        shell.Dispose();
    });

    // ---- Final lens (21/10/2026), lens on 4b5ee4f ----

    /// <summary>
    /// Pin (M2): the accent explainer names "More accurate", a Simple view name, so it says where that is: a seeded
    /// settings.json may open Settings > Models in its Advanced view, where the models go by their file names.
    /// </summary>
    [Fact]
    public void The_accent_explainer_says_where_More_accurate_is()
    {
        Assert.Contains($"choose {SimpleView.MoreAccurateLabel} in the {SimpleView.SimpleLabel} view of Settings > Models", OnboardingText.AccentExplainer);
        Assert.Contains(SimpleView.MoreAccurateLabel, SimpleView.Tiers(gpuMachine: false).Select(t => t.Label));
        Assert.Contains(SimpleView.MoreAccurateLabel, SimpleView.Tiers(gpuMachine: false, "es").Select(t => t.Label)); // every language has it
    }
}
