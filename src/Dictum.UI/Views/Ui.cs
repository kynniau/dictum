using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Dictum.UI.Views;

/// <summary>
/// The few building blocks every window is made of, so each looks the same (docs/design: the type scale, radius 8 controls,
/// radius 12 cards, label left and switch right). Styles come from Theme/Controls.axaml by class name.
/// </summary>
internal static class Ui
{
    public static TextBlock Text(string text, string cls = "body")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add(cls);
        return block;
    }

    public static TextBlock Heading(string text) => Text(text, "heading");

    public static TextBlock Overline(string text) => Text(text.ToUpperInvariant(), "overline");

    /// <summary>
    /// A button of class primary, secondary, danger, quiet or link. A failing action is reported, never fatal. A dialog the
    /// action opens is owned by this button's window (<see cref="Modal.Run"/>).
    /// </summary>
    public static Button Button(string label, string cls, Action click, Action<Exception>? failed = null, string? name = null)
    {
        var button = new Button { Content = label };
        button.Classes.Add(cls);
        if (name is not null) AutomationProperties.SetName(button, name);
        button.Click += (_, _) => Modal.From(TopLevel.GetTopLevel(button) as Window, () =>
        {
            try
            {
                click();
            }
            catch (Exception ex) when (failed is not null)
            {
                failed(ex);
            }
        });
        return button;
    }

    /// <summary>
    /// A state to read, not a control to press ("In use"): text in the link colour (the accent, legible in both themes) in an
    /// outlined pill, button-sized text, never focusable, named for a screen reader.
    /// </summary>
    public static Border Tag(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("LinkBrush");
        var tag = new Border { Child = label, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 5), Focusable = false };
        tag[!Border.BorderBrushProperty] = new DynamicResourceExtension("LinkBrush");
        // No name on the border: a Border has no automation peer. The label is a TextBlock, whose own peer (Text) says "In use".
        return tag;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        panel.Children.AddRange(children);
        return panel;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        panel.Children.AddRange(children);
        foreach (var child in children) child.VerticalAlignment = VerticalAlignment.Center;
        return panel;
    }

    /// <summary>A white (dark: navy) card with an optional small-caps title.</summary>
    public static Border Card(Control child, string? overline = null)
    {
        var card = new Border { Child = overline is null ? child : Stack(8, Overline(overline), child) };
        card.Classes.Add("card");
        return card;
    }

    /// <summary>Equal columns, children placed left to right then down.</summary>
    public static Grid Columns(int count, double gap, params Control[] children)
    {
        var grid = new Grid { ColumnSpacing = gap, RowSpacing = gap };
        for (var i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var i = 0; i < children.Length; i++)
        {
            if (i % count == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(children[i], i % count);
            Grid.SetRow(children[i], i / count);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    /// <summary>A label on the left and a control on the right, 36 px tall (the flyout's toggle rows).</summary>
    public static Grid Line(string label, Control control, string? hint = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 36, ColumnSpacing = 12 };
        var text = hint is null ? (Control)Text(label, "body") : Stack(2, Text(label, "body"), Text(hint, "hint"));
        text.VerticalAlignment = VerticalAlignment.Center;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        AutomationProperties.SetName(control, label);
        return grid;
    }

    /// <summary>A switch with no On/Off words (the row's label names it); <paramref name="changed"/> runs on a user change only.</summary>
    public static ToggleSwitch Switch(bool on, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsChecked = on, OnContent = null, OffContent = null, MinWidth = 0 };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.Tag is not "quiet") changed(toggle.IsChecked == true);
        };
        return toggle;
    }

    /// <summary>Sets a switch without running its change action (a refresh from the settings).</summary>
    public static void SetQuietly(ToggleSwitch toggle, bool on)
    {
        if (toggle.IsChecked == on) return;
        toggle.Tag = "quiet";
        toggle.IsChecked = on;
        toggle.Tag = null;
    }

    /// <summary>A path figure in a 24-unit box (the design's icons), stroked with a theme brush.</summary>
    public static Control Icon(string data, double size = 18, string brush = "MutedBrush")
    {
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        path[!Shape.StrokeProperty] = new DynamicResourceExtension(brush);
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    public const string MicIcon = "M9,6 A3,3 0 0 1 15,6 L15,11 A3,3 0 0 1 9,11 Z M5,11 A7,7 0 0 0 19,11 M12,18 L12,21";
    public const string LockIcon = "M6,10 L18,10 A2,2 0 0 1 20,12 L20,19 A2,2 0 0 1 18,21 L6,21 A2,2 0 0 1 4,19 L4,12 A2,2 0 0 1 6,10 Z M8,10 L8,7 A4,4 0 0 1 16,7 L16,10";
    public const string CloudIcon = "M7,18 A4,4 0 0 1 7,10 A5,5 0 0 1 17,9 A4.5,4.5 0 0 1 17,18 Z";
}

/// <summary>
/// A read-only folder path in a box like a text box (1.2 polish, 22/10/2026: a read-only text box cut a long path off at its
/// end, "…\mod"). The path is trimmed from the left, so its end, the folder's own name, stays in view; the whole path is the
/// tooltip and the automation value (a read-only Edit named for what it is). It is a tab stop with a focus ring, and keyboard focus opens the tooltip, so the
/// whole path is readable without a mouse, as the text box's was.
/// </summary>
internal sealed class PathBox : Border
{
    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.LeadingCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <param name="name">What the path is ("Model folder"): the accessible name; the path is the accessible value.</param>
    public PathBox(string name)
    {
        AutomationProperties.SetName(this, name); // the peer's name; its value is the path
        Child = _text;
        MinHeight = 32;
        Padding = new Thickness(10, 5);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Focusable = true;
        IsTabStop = true;
        this[!BackgroundProperty] = new DynamicResourceExtension("ControlBrush");
        Ring(false);
        GotFocus += (_, e) =>
        {
            var keyboard = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
            Ring(keyboard);
            if (keyboard) ToolTip.SetIsOpen(this, true);
        };
        LostFocus += (_, _) =>
        {
            Ring(false);
            ToolTip.SetIsOpen(this, false);
        };
    }

    /// <summary>The path shown; the tooltip and the automation peer's value follow it.</summary>
    public string Path
    {
        get => _text.Text ?? "";
        set
        {
            _text.Text = value;
            ToolTip.SetTip(this, value); // the automation peer reads Path as its value (PathBoxPeer)
        }
    }

    internal TextBlock Text => _text;

    /// <summary>
    /// A Border has no automation peer of its own, so a screen reader would skip this tab stop (lens on 4abba54). Its peer is a
    /// read-only Edit, as the text box it replaced was: named "Model folder", its value the whole path.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new PathBoxPeer(this);

    private sealed class PathBoxPeer(PathBox owner) : ControlAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        public bool IsReadOnly => true;

        public string? Value => owner.Path;

        public void SetValue(string? value) => throw new InvalidOperationException("The model folder is changed with Browse…, not typed.");
    }

    private void Ring(bool on) => this[!BorderBrushProperty] = new DynamicResourceExtension(on ? "LinkBrush" : "ControlBorderBrush");
}

/// <summary>The segmented control (Light | Full, Off | Orb | Edge | Both): exactly one is checked, or none for "Custom".</summary>
internal sealed class Segments : Border
{
    private readonly ToggleButton[] _buttons;
    private bool _quiet;

    public Segments(string[] labels, int selected, Action<int> changed, string name)
    {
        Classes.Add("segments");
        var grid = new UniformGrid { Rows = 1 };
        _buttons = new ToggleButton[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new ToggleButton { Content = labels[i], IsChecked = i == selected };
            button.Classes.Add("segment");
            AutomationProperties.SetName(button, $"{name}: {labels[i]}");
            button.IsCheckedChanged += (_, _) =>
            {
                if (_quiet) return;
                var pressedOn = button.IsChecked == true; // false: the chosen one was pressed again
                Select(index); // pressing the chosen one again keeps it chosen
                if (pressedOn) changed(index); // and does nothing else: re-running a Mode preset once cancelled a download (Store 10.1.2.10, 19/10/2026)
            };
            _buttons[i] = button;
            grid.Children.Add(button);
        }
        Child = grid;
    }

    public int Selected => Array.FindIndex(_buttons, b => b.IsChecked == true);

    /// <summary>Checks one segment (or none: -1) without raising a change.</summary>
    public void Select(int index)
    {
        _quiet = true;
        for (var i = 0; i < _buttons.Length; i++) _buttons[i].IsChecked = i == index;
        _quiet = false;
    }
}

/// <summary>Five bars that follow a 0..1 level (the design's microphone meter); grey when not listening.</summary>
internal sealed class Meter : StackPanel
{
    private static readonly double[] Shape = [0.45, 0.75, 1.0, 0.6, 0.35];
    private readonly Rectangle[] _bars;

    public Meter(double height = 18)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 3;
        Height = height;
        _bars = [.. Shape.Select(s => new Rectangle { Width = 4, Height = height * s, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Bottom })];
        Children.AddRange(_bars);
        Show(null);
    }

    /// <summary>The input RMS now, or null when not listening (all bars grey).</summary>
    public void Show(float? rms)
    {
        var norm = rms is { } r ? Math.Clamp((20 * Math.Log10(Math.Max(r, 1e-6f)) + 50) / 40, 0, 1) : 0;
        for (var i = 0; i < _bars.Length; i++)
        {
            var lit = rms is not null && norm >= (i + 0.5) / _bars.Length;
            _bars[i][!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension(lit ? "DoneBrush" : "MeterOffBrush");
        }
    }
}
