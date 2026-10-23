using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Dictum.Help;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Onboarding;

/// <summary>
/// "Welcome to Dictum" (Dictum 1.2): the first run in one window, replacing the chain of first-run dialogs. A step line and
/// dots, the step's body (scrolling when it does not fit), and Back with the step's own buttons. Steps 1-5 run before the
/// settings are first loaded (<see cref="RunFirstSteps"/> returns when they are done or the window closes); Try it follows once
/// the shell has loaded them and the hotkey listens (<see cref="OnboardingFlow.BeginTryIt"/>). Esc or closing the window skips
/// whatever is left with today's defaults (<see cref="OnboardingFlow.SkipRest"/>); before the first step's Continue, Dictum quits
/// instead (the Terms of Use not accepted). The text is larger than Dictum's other windows (17 px body). Its client area (720 ×
/// 560 DIP since the legal block, 23/10/2026; 720 × 500 since 1.2 polish, 22/10/2026; 640 × 420 before) holds every step's
/// body as built without scrolling (the hotkey step's "Hold to test" and "Stop listening" sat under the buttons at 420; at 500
/// the first step, with the Terms of Use notice and the longer approved short version, measured 315 of 278); on opening it is
/// clamped to the screen's work area (<see cref="Fit"/>), and on a screen too small for it (1366 × 768 at 150 %, and since 560
/// probably at 125 % too: inferred from the recalled 48 DIP taskbar and 32 DIP caption, not measured) the body scrolls under a scroll bar that stays visible, with Back and the step's buttons always in view. On the
/// first step the Terms of Use notice sits beside Continue in that same row, so it never scrolls away from the button; with
/// <see cref="OnboardingFlow.TermsOnly"/> the window is that step alone, without the step line.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    public const double WindowWidth = 720;
    public const double WindowHeight = 560;

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
    private readonly StackPanel _terms;
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
        if (!flow.TermsOnly)
        {
            if (flow.AsksFolder) _steps.Add(new FolderStep(flow));
            _steps.Add(new SpeechStep(flow));
            _steps.Add(new HotkeyStep(flow));
            _steps.Add(new MicrophoneStep(flow));
            _steps.Add(new TryItStep(flow));
        }
        foreach (var step in _steps) step.ButtonsChanged += ShowButtons;

        _back = Ui.Button(OnboardingText.Back, "secondary", Back);
        _secondary = Ui.Button("", "secondary", PressSecondary);
        _primary = Ui.Button("", "primary", PressPrimary);
        _primary.IsDefault = true;
        AutomationProperties.SetLiveSetting(_stepLine, AutomationLiveSetting.Polite);
        // The Terms of Use notice (APPROVED by the owner 23/10/2026 09:50 AEDT, legal block: TermsText): the acceptance sentence
        // beside Continue and the privacy sentence below it, each with its link opened through the platform, as Settings opens pages.
        _terms = Ui.Stack(2,
            TermsLine(TermsText.Acceptance, TermsText.AcceptanceLink, () => flow.Shell.Platform.Open(TermsText.Url)),
            TermsLine(TermsText.PrivacyLine, TermsText.PrivacyLink, () => flow.Shell.Platform.Open(AppShell.PrivacyUrl)));
        _terms.VerticalAlignment = VerticalAlignment.Center;
        _terms.Margin = new Thickness(0, 0, 12, 0);

        // The bar stays visible whenever the body scrolls (only on a small screen): an auto-hiding one hid that there was more.
        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, AllowAutoHide = false };
        var header = Ui.Row(12, new MarkIcon(30), Ui.Text(OnboardingText.Title, "title"));
        var progress = Ui.Row(12, _dots, _stepLine);
        progress.IsVisible = !flow.TermsOnly; // one step: no "Step 1 of 1"
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.AddRange([_secondary, _primary]);
        var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_terms, 1);
        Grid.SetColumn(right, 2);
        nav.Children.AddRange([_back, _terms, right]);
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
        if (_index == _steps.Count - 1)
        {
            _flow.FinishFirstSteps(); // the Terms of Use step alone (OnboardingFlow.TermsOnly): its Continue ends the window
            Close();
            return;
        }
        Advance();
    }

    /// <summary>The Terms of Use notice beside Continue: its two sentences, each with its link (tests).</summary>
    internal StackPanel TermsNotice => _terms;

    /// <summary>
    /// One sentence of the notice: the text before <paramref name="link"/>, the link (a link button that opens a page through
    /// the platform), and the text after it, side by side, each centred on the row. Not a control inside the text's own run
    /// (InlineUIContainer): rendered headless on 23/10/2026, that sat the link about 5 px above the line and, with the body's
    /// fixed line height, clipped the text under it. The link takes the body text size, so it reads as part of the sentence;
    /// on a window too narrow for the sentence the link moves to the next row. The whole sentence is the row's accessible name
    /// (lens on PR #76): its parts alone read as fragments. The row is exposed to UI Automation as a group in the content view;
    /// a panel is otherwise left out of the automation tree, name and all.
    /// </summary>
    private static WrapPanel TermsLine(string sentence, string link, Action open)
    {
        var at = sentence.IndexOf(link, StringComparison.Ordinal);
        var button = Ui.Button(link, "link", open);
        button.FontSize = BodySize;
        var line = new WrapPanel();
        foreach (var part in new Control[] { Ui.Text(sentence[..at]), button, Ui.Text(sentence[(at + link.Length)..]) })
        {
            part.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(part);
        }
        AutomationProperties.SetName(line, sentence);
        // A WrapPanel gets Avalonia's NoneAutomationPeer, so without these two the name never reaches UI Automation (re-lens on 5f878a6).
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Content);
        AutomationProperties.SetControlTypeOverride(line, AutomationControlType.Group);
        return line;
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
        _terms.IsVisible = step is WelcomeStep;
        // A screen reader says the acceptance sentence with the Continue button (lens on PR #76), as About's Check for updates says its line.
        AutomationProperties.SetHelpText(_primary, step is WelcomeStep ? TermsText.Acceptance : null);
    }

    /// <summary>This window's body text size (the theme's is <see cref="ThemeBodySize"/>).</summary>
    internal const double BodySize = 17;

    /// <summary>The theme's body text, Theme/Controls.axaml's TextBlock.body: 13 px on a 19 px line (a pin reads the theme against these).</summary>
    internal const double ThemeBodySize = 13;
    internal const double ThemeBodyLineHeight = 19;

    /// <summary>The line height for body text of <paramref name="size"/>, in the theme's proportion, rounded up: 25 for 17 px.</summary>
    internal static double LineHeightFor(double size) => Math.Ceiling(size * ThemeBodyLineHeight / ThemeBodySize);

    /// <summary>
    /// Larger text than the other windows: these styles, on this window, take precedence over the theme's (Theme/Controls.axaml).
    /// </summary>
    private void LargeText()
    {
        static Style Size(Func<Selector?, Selector> selector, double size) => new(selector) { Setters = { new Setter(TextBlock.FontSizeProperty, size) } };
        // The theme gives body text a fixed line height (19 px for 13 px text). Keeping it under 17 px text clipped the bottom of
        // each paragraph's last line ("gigabytes", "let go"): the style sets both, the line height in the theme's proportion.
        Styles.Add(new Style(s => s.OfType<TextBlock>().Class("body"))
        {
            Setters = { new Setter(TextBlock.FontSizeProperty, BodySize), new Setter(TextBlock.LineHeightProperty, LineHeightFor(BodySize)) },
        });
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
