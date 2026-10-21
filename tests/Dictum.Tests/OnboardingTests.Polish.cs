using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;

namespace Dictum.Tests;

/// <summary>1.2 polish (22/10/2026): the welcome window's text, its size and Try it's waiting state, from the demo lane's Sandbox run.</summary>
public sealed partial class OnboardingTests
{
    /// <summary>The visible TextBlocks of a step, each with the height it would need on its own lines (no LineHeight: the font's own).</summary>
    private static IEnumerable<(TextBlock Text, double Natural)> NaturalHeights(Control step)
    {
        foreach (var text in All<TextBlock>(step).Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 0))
        {
            var natural = new TextBlock
            {
                Text = text.Text,
                FontSize = text.FontSize,
                FontFamily = text.FontFamily,
                FontWeight = text.FontWeight,
                TextWrapping = text.TextWrapping,
            };
            natural.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
            yield return (text, natural.DesiredSize.Height);
        }
    }

    /// <summary>
    /// Pin (item 1): no line of text in any step is cut off. Every text block is at least as tall as the same text set in its
    /// font's own line height; the theme's 19 px body line under the window's 17 px body text clipped the last line's
    /// descenders ("gigabytes", "let go", "begin"). Both forms of the window: with base.en on this PC and without it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task No_step_cuts_off_its_text(bool bundled) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        if (bundled) Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        while (true)
        {
            var step = window.Current!;
            Dispatcher.UIThread.RunJobs();
            var checkedBodies = 0;
            foreach (var (text, natural) in NaturalHeights(step))
            {
                Assert.True(text.DesiredSize.Height >= natural - 0.5,
                    $"{step.StepName}: \"{text.Text}\" is {text.DesiredSize.Height:0.0} px tall, its lines need {natural:0.0} (font {text.FontSize}, line {text.LineHeight})");
                if (text.Classes.Contains("body")) checkedBodies++;
            }
            Assert.True(checkedBodies > 0, $"{step.StepName}: no body text was checked");
            if (step is TryItStep) break;
            Next(window);
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (item 1): the window's body style sets its line height with its size, in the theme's proportion; the theme is read, not assumed.</summary>
    [Fact]
    public Task The_body_style_sets_size_and_line_height_together() => UiSession.Run(() =>
    {
        var plain = Ui.Text("theme");
        var window = new Window { Content = plain };
        window.Show();
        Assert.Equal(OnboardingWindow.ThemeBodySize, plain.FontSize); // Theme/Controls.axaml as the constants say
        Assert.Equal(OnboardingWindow.ThemeBodyLineHeight, plain.LineHeight);
        window.Close();

        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, onboarding) = Open(shell, scratch);
        var body = All<TextBlock>(onboarding.Current!).First(t => t.Classes.Contains("body"));
        Assert.Equal(OnboardingWindow.BodySize, body.FontSize);
        Assert.Equal(25, body.LineHeight); // 17 x 19 / 13 = 24.8, rounded up
        Assert.Equal(OnboardingWindow.LineHeightFor(OnboardingWindow.BodySize), body.LineHeight);
        onboarding.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (item 2): at the window's default size every step's body fits its area without scrolling, the longest forms included:
    /// the speech choice without base.en, the hotkey step listening (its "Hold to test" and "Stop listening" sat under the buttons
    /// at 640 × 420), and Try it with both its later lines shown.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Every_step_fits_the_default_window_without_scrolling(bool bundled) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        if (bundled) Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (_, window) = Open(shell, scratch);
        var scroll = All<ScrollViewer>(window).Single();
        Assert.False(scroll.AllowAutoHide); // on a screen too small for it, the bar that says there is more stays visible
        void Fits(OnboardingStep step, string what)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Size(OnboardingWindow.WindowWidth, OnboardingWindow.WindowHeight), window.ClientSize);
            Assert.True(step.DesiredSize.Height <= scroll.Viewport.Height + 0.5, $"{what}: {step.DesiredSize.Height:0} of {scroll.Viewport.Height:0}");
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"{what}: the body scrolls");
        }
        while (true)
        {
            var step = window.Current!;
            Fits(step, step.StepName);
            if (step is HotkeyStep hotkey)
            {
                Assert.True(hotkey.Box.Listening);
                var stop = All<Button>(hotkey).Single(b => b.Content as string == "Stop listening");
                var bottom = stop.TranslatePoint(new Point(0, stop.Bounds.Height), scroll)!.Value.Y;
                Assert.True(bottom <= scroll.Viewport.Height + 0.5, $"Stop listening ends at {bottom:0} of {scroll.Viewport.Height:0}");
            }
            if (step is TryItStep tryIt)
            {
                foreach (var line in All<TextBlock>(tryIt).Where(t => t.Text == OnboardingText.AccentExplainer || t.Text == OnboardingText.NothingCame))
                    line.IsVisible = true; // never both at once in use; both here, the longest it can be
                Fits(tryIt, "Try it with the explainer and the try-later line");
                break;
            }
            Next(window);
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (item 3): while the model gets ready its status is inside the practice box, in body text, and the instruction already
    /// names the key by its display name; typing in the box takes the status away, and an empty box shows it again.
    /// </summary>
    [Fact]
    public Task Try_it_shows_the_wait_inside_the_box() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, _, _) = scratch.Shell();
        var (flow, window) = Open(shell, scratch);
        var tryIt = GoTo<TryItStep>(window);
        Assert.False(flow.TryItLive);
        Assert.Equal(OnboardingText.TryLoading, tryIt.Placeholder);
        Assert.Equal(OnboardingText.TryInstruction("Right Ctrl"), tryIt.Instruction);
        Assert.DoesNotContain(All<TextBlock>(tryIt), t => t.Text == OnboardingText.TryLoading && t != tryIt.StatusText);
        var holder = Assert.IsType<Grid>(tryIt.StatusText.Parent);
        Assert.Contains(tryIt.Box, holder.Children); // over the box, not a line above it
        Assert.True(tryIt.StatusText.Classes.Contains("body"));
        Assert.False(tryIt.StatusText.IsHitTestVisible); // a click goes to the box
        Dispatcher.UIThread.RunJobs();
        var boxTop = tryIt.Box.TranslatePoint(default, window)!.Value.Y;
        var statusTop = tryIt.StatusText.TranslatePoint(default, window)!.Value.Y;
        Assert.InRange(statusTop - boxTop, 0, tryIt.Box.Bounds.Height - tryIt.StatusText.Bounds.Height);

        tryIt.Box.Text = "typed";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(tryIt.Placeholder);
        tryIt.Box.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(OnboardingText.TryLoading, tryIt.Placeholder);
        window.Close();
        shell.Dispose();
    });
}
