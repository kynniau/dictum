using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// The dialog windows the shell's questions open (headless): their buttons give the answers the shell expects. The
/// nested modal frame itself (Modal.Run) needs a real desktop and is not exercised here.
/// </summary>
public class DialogTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static void Click(ILogical root, string label) =>
        All<Button>(root).First(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public Task A_question_returns_the_button_chosen_and_minus_one_when_closed() => UiSession.Run(() =>
    {
        var window = new QuestionWindow("Delete a model", "Delete ggml-small.en.bin (487 MB)?",
            [new DialogChoice("Delete", IsDefault: true, IsDanger: true), new DialogChoice("Cancel")]);
        window.Show();
        Assert.Contains(All<Button>(window), b => b.Content as string == "Delete" && b.Classes.Contains("danger") && b.IsDefault);
        Click(window, "Cancel");
        Assert.Equal(1, window.Choice);
        Assert.False(window.IsVisible);

        var closed = new QuestionWindow("Notice", "Text", [new DialogChoice("OK", IsDefault: true)]);
        closed.Show();
        closed.Close();
        Assert.Equal(-1, closed.Choice);
    });

    [Fact]
    public Task The_second_removal_warning_needs_the_word_typed_exactly() => UiSession.Run(() =>
    {
        var window = new ConfirmWordWindow("Remove Dictum completely", "There is no undo. Type DELETE to confirm.", "DELETE");
        window.Show();
        var confirm = All<Button>(window).Single(b => b.Content as string == "Remove everything");
        var box = All<TextBox>(window).Single();
        Assert.False(confirm.IsEnabled);
        box.Text = "delete";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // TextBox raises TextChanged through the dispatcher
        Assert.False(confirm.IsEnabled); // case matters
        box.Text = "DELETE";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(confirm.IsEnabled);
        Assert.True(All<Button>(window).Single(b => b.Content as string == "Cancel").IsDefault); // Enter does not delete
        Click(window, "Remove everything");
        Assert.True(window.Confirmed);
    });

    [Fact]
    public Task The_model_picker_lists_the_catalogue_with_the_recommendation_chosen() => UiSession.Run(() =>
    {
        var options = ModelCatalog.Entries.Select(e => new PickerOption(e.Name, e.ItemText(false), e.TradeOff)).ToList();
        var window = new ModelPickerWindow("Choose a speech model", "text", options, "small.en", "Use selected model", "Choose model file…");
        window.Show();
        var radios = All<RadioButton>(window).ToList();
        Assert.Equal(ModelCatalog.Entries.Count, radios.Count);
        Assert.Equal("small.en", radios.Single(r => r.IsChecked == true).Tag);
        radios.Single(r => (string?)r.Tag == "base.en").IsChecked = true;
        Click(window, "Use selected model");
        Assert.Equal("base.en", window.Picked);

        var quit = new ModelPickerWindow("Choose a speech model", "text", options, "small.en", "Use selected model", "Choose model file…");
        quit.Show();
        Click(quit, "Quit");
        Assert.Null(quit.Picked);
        Assert.False(quit.ChooseFile);
    });

    [Fact]
    public Task Add_word_writes_the_dictionary_and_refuses_a_comment() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        var window = new AddWordWindow(shell);
        window.Show();
        var box = All<TextBox>(window).Single();
        box.Text = "#note";
        Click(window, "Add");
        Assert.False(File.Exists(scratch.Paths.DictionaryFile));
        box.Text = "Kynni";
        Click(window, "Add");
        Assert.Contains("Kynni", File.ReadAllLines(scratch.Paths.DictionaryFile));
        window.Close();
        shell.Dispose();
    });
}
