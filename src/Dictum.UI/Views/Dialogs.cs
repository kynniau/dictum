using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Dictum.UI.Shell;

namespace Dictum.UI.Views;

/// <summary>The frame every Dictum dialog shares: the mark and a heading, the body, and the buttons on the right.</summary>
internal abstract class DialogWindow : Window
{
    protected DialogWindow(string heading, double width = 520)
    {
        Title = $"Dictum: {heading}";
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = true;
        Heading = heading;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    protected string Heading { get; }

    /// <summary>
    /// Lays out the window: header, <paramref name="body"/>, then the buttons right-aligned. The buttons wrap onto another line
    /// when they do not fit the window's width, so a long label never pushes a button off the window (lens, 19/10/2026: the
    /// download question's Cancel ended past the edge). Tab order stays the order given.
    /// </summary>
    protected void Compose(Control body, params Button[] buttons)
    {
        var header = Ui.Row(10, new MarkIcon(24), Ui.Heading(Heading));
        var row = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, ItemsAlignment = WrapPanelItemsAlignment.End };
        row.Children.AddRange(buttons);
        Content = new Border { Padding = new Thickness(20), Child = Ui.Stack(16, header, body, row) };
    }

    /// <summary>Long text the user may want to copy (the removal list, an error), scrolling past 420 px.</summary>
    protected static Control Body(string text) => new ScrollViewer
    {
        MaxHeight = 420,
        Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 },
    };
}

/// <summary>A question with buttons (<see cref="IShellDialogs.Ask"/>): <see cref="Choice"/> is the index chosen, -1 if closed.</summary>
internal sealed class QuestionWindow : DialogWindow
{
    public QuestionWindow(string title, string text, IReadOnlyList<DialogChoice> choices) : base(title)
    {
        var buttons = choices.Select((c, i) =>
        {
            var button = Ui.Button(c.Label, c.IsDanger ? "danger" : c.IsDefault ? "primary" : "secondary", () =>
            {
                Choice = i;
                Close();
            });
            button.IsDefault = c.IsDefault;
            return button;
        }).ToArray();
        Compose(Body(text), buttons);
        Opened += (_, _) => (buttons.FirstOrDefault(b => b.IsDefault) ?? buttons.FirstOrDefault())?.Focus();
    }

    public int Choice { get; private set; } = -1;
}

/// <summary>The second warning of "Remove Dictum completely": the confirm button works only once the word is typed exactly.</summary>
internal sealed class ConfirmWordWindow : DialogWindow
{
    public ConfirmWordWindow(string title, string text, string word) : base(title)
    {
        var box = new TextBox { Watermark = $"Type {word}" };
        AutomationProperties.SetName(box, $"Type {word} to confirm");
        var confirm = Ui.Button("Remove everything", "danger", () =>
        {
            Confirmed = box.Text == word;
            if (Confirmed) Close();
        });
        confirm.IsEnabled = false;
        box.TextChanged += (_, _) => confirm.IsEnabled = box.Text == word;
        var cancel = Ui.Button("Cancel", "secondary", Close);
        cancel.IsDefault = true;
        Compose(Ui.Stack(12, Body(text), box), cancel, confirm);
        Opened += (_, _) => box.Focus();
    }

    public bool Confirmed { get; private set; }
}

/// <summary>One option of a model picker: the catalogue name and the two lines that describe it.</summary>
internal sealed record PickerOption(string Name, string Label, string Detail);

/// <summary>
/// The first-run model choice (SPEC §10, §5): every catalogue entry with its size and download state, the recommendation
/// chosen, and buttons to use it, choose a file instead, or quit. Nothing is downloaded until the user confirms here.
/// </summary>
internal sealed class ModelPickerWindow : DialogWindow
{
    public const string RulesOnlyName = "";

    public ModelPickerWindow(string heading, string text, IReadOnlyList<PickerOption> options, string recommended, string useLabel, string chooseLabel) : base(heading, 600)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var option in options)
        {
            var radio = new RadioButton
            {
                GroupName = "model",
                IsChecked = option.Name == recommended,
                Tag = option.Name,
                Content = Ui.Stack(1, Ui.Text(option.Label, "body"), Ui.Text(option.Detail, "hint")),
            };
            AutomationProperties.SetName(radio, option.Label);
            list.Children.Add(radio);
        }
        var use = Ui.Button(useLabel, "primary", () =>
        {
            Picked = list.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? recommended;
            Close();
        });
        use.IsDefault = true;
        var choose = Ui.Button(chooseLabel, "secondary", () =>
        {
            ChooseFile = true;
            Close();
        });
        var quit = Ui.Button("Quit", "quiet", Close);
        Compose(Ui.Stack(12, Ui.Text(text), new ScrollViewer { MaxHeight = 380, Content = list }), quit, choose, use);
        Opened += (_, _) => use.Focus();
    }

    /// <summary>The catalogue name chosen (<see cref="RulesOnlyName"/> for rule-based tidy only), or null.</summary>
    public string? Picked { get; private set; }

    public bool ChooseFile { get; private set; }
}

/// <summary>
/// SPEC §20 at first run: the hotkey check's words, now the "Your hotkey" step of the Welcome to Dictum window
/// (Onboarding.HotkeyStep, 1.2): the capture box defaulting to the configured hotkey, the note for Mac keyboards over Remote
/// Desktop, and the two buttons. Moved from the separate first-run window unchanged.
/// </summary>
internal static class HotkeyCheck
{
    public const string Heading = "Check your hotkey";

    public const string Intro =
        "Dictum records while you hold this key and pastes when you let go. Press it now: the box shows exactly what Windows " +
        "receives, and the light comes on while the key is held. Press a different key, or a mouse button other than the left " +
        "one, to choose it instead.";

    public const string MacNote = "On a Mac over Remote Desktop use the right Option key (RightAlt) or right Command (RightWin).";

    public const string UseLabel = "Use this hotkey";

    public const string SkipLabel = "Skip";
}
