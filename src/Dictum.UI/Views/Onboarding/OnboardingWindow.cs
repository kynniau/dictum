using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// "Welcome to Dictum" (Dictum 1.2): the first run in one window, replacing the chain of first-run dialogs. A step line and
/// dots, the step's body (scrolling when it does not fit), and Back with the step's own buttons. Steps 1-5 run before the
/// settings are first loaded (<see cref="RunFirstSteps"/> returns when they are done or the window closes); Try it follows once
/// the shell has loaded them and the hotkey listens (<see cref="OnboardingFlow.BeginTryIt"/>). Esc or closing the window skips
/// whatever is left with today's defaults (<see cref="OnboardingFlow.SkipRest"/>). The text is larger than Dictum's other
/// windows (17 px body). Its client area is 420 DIP high, so with the caption it fits a 1366 × 768 screen at 150 % (911 × 512
/// DIP, less the taskbar), and on opening it is clamped to the screen's work area, the body scrolling (<see cref="Fit"/>).
/// </summary>
internal sealed class OnboardingWindow : Window
{
    public const double WindowWidth = 640;
    public const double WindowHeight = 420;

    private readonly OnboardingFlow _flow;
    private readonly List<OnboardingStep> _steps = [];
    private readonly TextBlock _stepLine = Ui.Text("", "caption");
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _heading = Ui.Heading("");
    private readonly ContentControl _body = new();
    private readonly ScrollViewer _scroll;
    private readonly Button _back;
    private readonly Button _secondary;
    private readonly Button _primary;
    private int _index = -1;

    public OnboardingWindow(OnboardingFlow flow)
    {
        _flow = flow;
        Title = OnboardingText.Title;
        Width = WindowWidth;
        Height = WindowHeight;
        MinWidth = 480;
        MinHeight = 360;
        ShowInTaskbar = true;
        LargeText();

        _steps.Add(new WelcomeStep(flow));
        if (flow.AsksFolder) _steps.Add(new FolderStep(flow));
        _steps.Add(new SpeechStep(flow));
        _steps.Add(new HotkeyStep(flow));
        _steps.Add(new MicrophoneStep(flow));
        _steps.Add(new TryItStep(flow));
        foreach (var step in _steps) step.ButtonsChanged += ShowButtons;

        _back = Ui.Button(OnboardingText.Back, "secondary", Back);
        _secondary = Ui.Button("", "secondary", PressSecondary);
        _primary = Ui.Button("", "primary", PressPrimary);
        _primary.IsDefault = true;
        AutomationProperties.SetLiveSetting(_stepLine, AutomationLiveSetting.Polite);

        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var header = Ui.Row(12, new MarkIcon(30), Ui.Text(OnboardingText.Title, "title"));
        var progress = Ui.Row(12, _dots, _stepLine);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.AddRange([_secondary, _primary]);
        var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(right, 1);
        nav.Children.AddRange([_back, right]);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 12 };
        Control[] rows = [header, progress, _heading, _scroll, nav];
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            layout.Children.Add(rows[i]);
        }
        Content = new Border { Padding = new Thickness(24, 20), Child = layout };

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(); // Esc skips the rest (Closed)
        };
        Closed += (_, _) =>
        {
            Current?.Leave(); // the microphone test's capture, the hotkey box's listener
            if (!_flow.FirstStepsDone) _flow.SkipRest();
            FirstStepsEnded?.Invoke();
        };
        flow.TryItReady += () => Dispatcher.UIThread.Post(() => (Current as TryItStep)?.Tick());
        Opened += (_, _) => FitToWorkArea();
        Show(0);
    }

    /// <summary>
    /// Where the window goes on a screen whose work area (the taskbar excluded) is <paramref name="work"/>, in physical pixels at
    /// <paramref name="scaling"/>: the client <paramref name="height"/> in DIP, or less so that it and the <paramref name="frame"/>
    /// (caption and borders, DIP) fit (the body scrolls; lens on 6bacce9: Back and Next sat under the taskbar), and the position,
    /// moved down or up into the work area when the window would start above it or end below it. Pure, so it is pinned headless
    /// (lens on 36fa43e).
    /// </summary>
    internal static (double Height, PixelPoint Position) Fit(double height, double frame, PixelRect work, double scaling, PixelPoint position)
    {
        if (scaling <= 0) scaling = 1;
        // The floor of 1 DIP only keeps the height positive for a degenerate work area (a test's, or a screen that reports
        // none); no real screen comes near it. 1366 x 768 at 175 % leaves about 359 DIP (inferred from the recalled 48 DIP
        // taskbar and 32 DIP caption, not measured): 768 px less 84 px of taskbar is 684 px, 391 DIP, less the caption. The nav
        // row's height is not a constant (it follows the 17 px text and the theme), so it is not used as the floor (lens on 86132af).
        var fitted = Math.Max(1, Math.Min(height, work.Height / scaling - frame));
        var outer = (int)Math.Ceiling((fitted + frame) * scaling);
        var y = position.Y < work.Y || position.Y + outer > work.Bottom ? Math.Max(work.Y, work.Y + (work.Height - outer) / 2) : position.Y;
        return (fitted, new PixelPoint(position.X, y));
    }

    /// <summary>How many times opening the window ran <see cref="Fit"/> on its screen (tests: the Opened hook is wired).</summary>
    internal int FitsOnOpen { get; private set; }

    /// <summary>What <see cref="Fit"/> said on opening, or null before it ran (tests compare the window with it).</summary>
    internal (double Height, PixelPoint Position)? LastFit { get; private set; }

    /// <summary>Clamps the window to the work area of its screen and moves it inside it (<see cref="Fit"/>, then <see cref="Apply"/>).</summary>
    private void FitToWorkArea()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
        var frame = Math.Max(0, (FrameSize?.Height ?? ClientSize.Height) - ClientSize.Height);
        var fit = Fit(Height, frame, screen.WorkingArea, screen.Scaling, Position);
        FitsOnOpen++;
        LastFit = fit;
        Apply(fit);
    }

    /// <summary>
    /// Puts the window where <see cref="Fit"/> said: a smaller height (the minimum lowered with it, so the clamp holds) and the
    /// position. Its own member so a test sees the result applied, not only worked out (lens on 86132af).
    /// </summary>
    internal void Apply((double Height, PixelPoint Position) fit)
    {
        if (fit.Height < Height)
        {
            MinHeight = Math.Min(MinHeight, fit.Height);
            Height = fit.Height;
        }
        if (fit.Position != Position) Position = fit.Position;
    }

    /// <summary>Raised once steps 1-5 are over: finished, or the window closed.</summary>
    public event Action? FirstStepsEnded;

    internal IReadOnlyList<OnboardingStep> Steps => _steps;

    internal OnboardingStep? Current => _index >= 0 && _index < _steps.Count ? _steps[_index] : null;

    internal Button PrimaryButton => _primary;

    internal Button SecondaryButton => _secondary;

    internal Button BackButton => _back;

    /// <summary>Shows the window and waits, with the UI running, until steps 1-5 are over (the shell then loads the settings).</summary>
    public void RunFirstSteps()
    {
        var frame = new DispatcherFrame();
        FirstStepsEnded += () => frame.Continue = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Show();
        Activate(); // the first run: it takes focus
        if (!_flow.FirstStepsDone) Dispatcher.UIThread.PushFrame(frame);
    }

    internal void PressPrimary()
    {
        if (Current is not { } step || !step.Primary()) return;
        if (step is TryItStep)
        {
            Close();
            return;
        }
        Advance();
    }

    internal void PressSecondary()
    {
        if (Current is not { } step || !step.Secondary()) return;
        if (step is TryItStep)
        {
            Close();
            return;
        }
        Advance();
    }

    private void Advance()
    {
        var next = _index + 1;
        if (_steps[next] is TryItStep && !_flow.FirstStepsDone)
        {
            _flow.FinishFirstSteps();
            Show(next);
            FirstStepsEnded?.Invoke();
            return;
        }
        Show(next);
    }

    private void Back()
    {
        if (Current is { AllowsBack: true } && _index > 0) Show(_index - 1);
    }

    private void Show(int index)
    {
        Current?.Leave();
        _index = index;
        var step = _steps[index];
        _body.Content = step;
        _heading.Text = step.Heading;
        _stepLine.Text = OnboardingText.StepLine(index + 1, _steps.Count, step.StepName);
        _dots.Children.Clear();
        for (var i = 0; i < _steps.Count; i++)
        {
            var dot = new Ellipse { Width = 10, Height = 10 };
            dot[!Shape.FillProperty] = new DynamicResourceExtension(i <= index ? "AccentBrush" : "MeterOffBrush");
            _dots.Children.Add(dot);
        }
        step.Enter();
        ShowButtons();
        _scroll.Offset = default;
        // Enter or Space moves on from the primary button; Tab reaches everything else. Try it focuses its box, the hotkey step
        // nothing (OnboardingStep.InitialFocus).
        var focus = step.InitialFocus(_primary);
        if (focus is null) FocusManager?.ClearFocus();
        Dispatcher.UIThread.Post(() =>
        {
            if (Current == step && focus is not null) focus.Focus();
        });
    }

    private void ShowButtons()
    {
        if (Current is not { } step) return;
        _primary.Content = step.PrimaryLabel;
        _secondary.Content = step.SecondaryLabel ?? "";
        _secondary.IsVisible = step.SecondaryVisible;
        _back.IsVisible = step.AllowsBack && _index > 0;
    }

    /// <summary>
    /// Larger text than the other windows: these styles, on this window, take precedence over the theme's (Theme/Controls.axaml).
    /// </summary>
    private void LargeText()
    {
        static Style Size(Func<Selector?, Selector> selector, double size) => new(selector) { Setters = { new Setter(TextBlock.FontSizeProperty, size) } };
        Styles.Add(Size(s => s.OfType<TextBlock>().Class("body"), 17));
        Styles.Add(Size(s => s.OfType<TextBlock>().Class("caption"), 15));
        Styles.Add(Size(s => s.OfType<TextBlock>().Class("hint"), 14));
        Styles.Add(Size(s => s.OfType<TextBlock>().Class("error"), 15));
        Styles.Add(Size(s => s.OfType<TextBlock>().Class("heading"), 22));
        Styles.Add(Size(s => s.OfType<TextBlock>().Class("title"), 24));
        Styles.Add(new Style(s => s.OfType<Button>()) { Setters = { new Setter(TemplatedControl.FontSizeProperty, 16.0) } });
        Styles.Add(new Style(s => s.OfType<ComboBox>()) { Setters = { new Setter(TemplatedControl.FontSizeProperty, 16.0) } });
        Styles.Add(new Style(s => s.OfType<TextBox>()) { Setters = { new Setter(TemplatedControl.FontSizeProperty, 16.0) } });
    }
}
