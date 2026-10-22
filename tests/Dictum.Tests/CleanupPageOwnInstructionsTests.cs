using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Settings > Clean-up with the 1.3 additions in place (headless, scratch profile): the box for your own instructions in the
/// Advanced view only, saved when the page is left, and "Create the key file…" beside "Open the settings folder".
/// </summary>
public class CleanupPageOwnInstructionsTests
{
    private const string Advanced = """{ "hotkey": "F24", "whisper": { "model": "base.en" }, "ui": { "view": "advanced" } }""";

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static TextBox Box(ILogical root) => All<TextBox>(root).Single(t => AutomationProperties.GetName(t) == OwnInstructionsCard.Label);

    private static Button KeyButton(ILogical root) => All<Button>(root).Single(b => b.Content as string == KeyFileButton.Label);

    private static List<string> Lines(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    private static SettingsWindow CleanupPage(AppShell shell)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Clean-up");
        return window;
    }

    [Fact]
    public Task The_Advanced_view_shows_the_box_and_saves_it_when_the_page_is_left() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Advanced);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = CleanupPage(shell);
        var box = Box(window);
        Assert.True(box.IsEffectivelyVisible);
        Assert.Contains(OwnInstructionsCard.Hint, Lines(window));
        // Between the Grammar card and the Cloud clean-up card.
        var overlines = Lines(window).Where(l => l == l.ToUpperInvariant() && l.Any(char.IsLetter)).ToList();
        Assert.True(overlines.IndexOf("GRAMMAR") < overlines.IndexOf(OwnInstructionsCard.Label.ToUpperInvariant()));
        Assert.True(overlines.IndexOf(OwnInstructionsCard.Label.ToUpperInvariant()) < overlines.IndexOf("CLOUD CLEAN-UP"));

        box.Text = "Prefer short sentences.\nWrite product names in capitals.";
        window.ShowPage("Privacy"); // leaving the page saves
        Assert.Equal("Prefer short sentences.\nWrite product names in capitals.", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Instructions);
        Assert.Equal("Prefer short sentences.\nWrite product names in capitals.", shell.Settings.Llm.Instructions);
        window.ShowPage("Clean-up");
        Assert.Equal("Prefer short sentences.\nWrite product names in capitals.", Box(window).Text); // shown again from the settings

        Box(window).Text = "Prefer short sentences.";
        window.Close(); // closing the window saves too
        Assert.Equal("Prefer short sentences.", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Llm.Instructions);
        shell.Dispose();
    });

    /// <summary>Mutation: OwnInstructionsCard.Redraw showing the card in both views makes this fail.</summary>
    [Fact]
    public Task The_Simple_view_hides_the_box() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "ui": { "view": "simple" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = CleanupPage(shell);
        Assert.False(Box(window).IsEffectivelyVisible);
        Assert.DoesNotContain(OwnInstructionsCard.Hint, Lines(window));
        Assert.True(shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create("advanced"))));
        Assert.True(Box(window).IsEffectivelyVisible); // the page redraws on the reload
        Assert.True(shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create("simple"))));
        Assert.False(Box(window).IsEffectivelyVisible);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// The Simple view hides the box, so it says when instructions apply (orchestrator decision after the lens on 5e9c227).
    /// Mutation: OwnInstructionsCard.Redraw never showing the line makes this fail.
    /// </summary>
    [Fact]
    public Task The_Simple_view_says_when_your_own_instructions_are_in_use() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "ui": { "view": "simple" }, "llm": { "instructions": "Prefer short sentences." } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = CleanupPage(shell);
        Assert.Contains(OwnInstructionsCard.SimpleLine, Lines(window));
        Assert.Equal("Your own clean-up instructions are in use (Advanced view).", OwnInstructionsCard.SimpleLine);
        Assert.True(shell.Write(("llm.instructions", null)));
        Assert.DoesNotContain(OwnInstructionsCard.SimpleLine, Lines(window)); // none set: no line
        Assert.True(shell.Write(("llm.instructions", System.Text.Json.Nodes.JsonValue.Create("Prefer short sentences."))));
        Assert.True(shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create("advanced"))));
        Assert.DoesNotContain(OwnInstructionsCard.SimpleLine, Lines(window)); // Advanced: the box itself shows them
        Assert.True(Box(window).IsEffectivelyVisible);
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_limit_line_is_a_polite_live_region() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Advanced);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = CleanupPage(shell);
        var limit = All<TextBlock>(window).Single(t => t.Text == OwnInstructionsCard.LimitLine);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(limit));
        Assert.False(limit.IsEffectivelyVisible);
        Box(window).Text = new string('a', Dictum.Settings.LlmSettings.InstructionsMaxLength);
        Assert.True(limit.IsEffectivelyVisible);
        Box(window).Text = "";
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_button_beside_the_folder_button_creates_the_key_file_and_says_so() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Advanced);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = CleanupPage(shell);
        var button = KeyButton(window);
        Assert.True(button.IsEffectivelyVisible);
        var row = Assert.IsType<StackPanel>(button.Parent);
        Assert.Contains(row.Children.OfType<Button>(), b => b.Content as string == "Open the settings folder");

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // the Service list starts on Anthropic (Claude)
        Assert.Equal(KeyFile.Contents(SecretStore.AnthropicKey), File.ReadAllText(scratch.Paths.EnvFile));
        Assert.Equal(scratch.Paths.EnvFile, Assert.Single(platform.Opened));
        Assert.Contains(AppShell.KeyFileCreated(SecretStore.AnthropicKey), Lines(window));
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task An_existing_key_file_is_opened_and_not_changed() => UiSession.Run(() =>
    {
        using var scratch = new Scratch(Advanced);
        byte[] before = [.. "GEMINI_API_KEY=k-test-not-real\r\n"u8.ToArray()];
        File.WriteAllBytes(scratch.Paths.EnvFile, before);
        var (shell, platform, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = CleanupPage(shell);
        KeyButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllBytes(scratch.Paths.EnvFile));
        Assert.Equal(scratch.Paths.EnvFile, Assert.Single(platform.Opened));
        Assert.Contains(AppShell.KeyFileExists, Lines(window));
        window.Close();
        shell.Dispose();
    });
}
