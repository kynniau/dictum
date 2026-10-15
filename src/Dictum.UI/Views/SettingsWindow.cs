using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Dictum.UI.Shell;
using Dictum.UI.Views.Settings;

namespace Dictum.UI.Views;

/// <summary>
/// Settings (SPEC §21): General, Mode, Models, Storage, Microphone, Clean-up, Privacy, About. Every control writes
/// settings.json through the shell (the file stays the source of truth and hot-reloads); each page redraws when the
/// settings or the models change. Opened on purpose, so it takes focus.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppShell _shell;
    private readonly ListBox _nav = new() { Width = 180 };
    private readonly ContentControl _host = new();
    private readonly Dictionary<string, SettingsPage> _pages = [];
    private readonly string[] _names = ["General", "Mode", "Models", "Storage", "Microphone", "Clean-up", "Privacy", "About"];

    public SettingsWindow(AppShell shell)
    {
        _shell = shell;
        Title = "Dictum settings";
        Width = 860;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _nav.ItemsSource = _names;
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is string name) ShowPage(name);
        };
        var navBox = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(8, 16), Child = Ui.Stack(12, Ui.Row(8, new MarkIcon(22), Ui.Heading("Dictum")), _nav) };
        navBox[!Border.BorderBrushProperty] = new DynamicResourceExtension("HairlineBrush");
        navBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SurfaceBrush");
        var scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(28, 24), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Child = _host } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(scroll, 1);
        grid.Children.AddRange([navBox, scroll]);
        Content = grid;

        Opened += (_, _) => shell.Changed += Refresh;
        Closed += (_, _) =>
        {
            shell.Changed -= Refresh;
            foreach (var page in _pages.Values) page.Leave();
        };
        _nav.SelectedIndex = 0;
    }

    /// <summary>Selects a page by name ("Storage", "Clean-up"…); an unknown name keeps the current page.</summary>
    public void ShowPage(string name)
    {
        if (Array.IndexOf(_names, name) is var index and >= 0 && _nav.SelectedIndex != index)
        {
            _nav.SelectedIndex = index; // raises SelectionChanged, which lands here again
            return;
        }
        if (_host.Content is SettingsPage current && current.PageTitle == name) return;
        (_host.Content as SettingsPage)?.Leave();
        if (!_pages.TryGetValue(name, out var page))
        {
            page = Create(name);
            _pages[name] = page;
        }
        _host.Content = page;
        page.Refresh();
    }

    private SettingsPage Create(string name) => name switch
    {
        "Mode" => new ModePage(_shell),
        "Models" => new ModelsPage(_shell, this),
        "Storage" => new StoragePage(_shell),
        "Microphone" => new MicrophonePage(_shell),
        "Clean-up" => new CleanupPage(_shell),
        "Privacy" => new PrivacyPage(_shell),
        "About" => new AboutPage(_shell),
        _ => new GeneralPage(_shell),
    };

    private void Refresh() => (_host.Content as SettingsPage)?.Refresh();
}

/// <summary>One page of Settings: built once, redrawn from the settings on every change, told when it is left.</summary>
internal abstract class SettingsPage : UserControl
{
    private readonly TextBlock _status = Ui.Text("", "caption");
    private bool _refreshing;

    protected SettingsPage(AppShell shell, string title)
    {
        Shell = shell;
        PageTitle = title;
        _status.IsVisible = false;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_status, Avalonia.Automation.AutomationLiveSetting.Polite);
    }

    protected AppShell Shell { get; }

    public string PageTitle { get; }

    /// <summary>True while <see cref="Redraw"/> runs: a control set from the settings must not write them back.</summary>
    protected bool Refreshing => _refreshing;

    /// <summary>Builds the page: the title, its sections, then the status line where results and errors appear.</summary>
    protected void Compose(params Control[] sections) =>
        Content = Ui.Stack(18, [Ui.Text(PageTitle, "title"), .. sections, _status]);

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            Redraw();
        }
        finally
        {
            _refreshing = false;
        }
    }

    protected abstract void Redraw();

    /// <summary>The page is no longer shown (another page, or the window closed): stop what it runs.</summary>
    public virtual void Leave()
    {
    }

    /// <summary>Shows a result or a failure under the page.</summary>
    protected void Say(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    /// <summary>Writes settings.json keys (unless the page is redrawing); a failure is said, never thrown.</summary>
    protected void Write(params (string Key, JsonNode? Value)[] values)
    {
        if (_refreshing) return;
        Run(() => Shell.Write(values));
    }

    protected void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Shell.Log.Error($"Settings: {ex.GetType().Name}: {ex.Message}");
            Say($"That did not work: {ex.Message}");
        }
    }

    protected static JsonNode? Json(string? value) => value is null ? null : JsonValue.Create(value);

    protected static JsonNode Json(bool value) => JsonValue.Create(value);

    protected static JsonNode Json(int value) => JsonValue.Create(value);
}
