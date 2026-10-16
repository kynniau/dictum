using Dictum.App;

namespace Dictum.UI.Shell;

/// <summary>A button in a question: its label and whether it is the one Enter presses.</summary>
public sealed record DialogChoice(string Label, bool IsDefault = false, bool IsDanger = false);

/// <summary>
/// Every question the shell asks: the model prompts (first run, missing model, download, choose file) and the rest
/// (confirmations, the cloud consent, the two warnings of "Remove Dictum completely"). Behind an interface so the shell's
/// logic runs in tests with scripted answers; <see cref="ShellDialogs"/> shows them as windows.
/// </summary>
public interface IShellDialogs : IModelPrompts
{
    /// <summary>A question with buttons; returns the index of the one chosen, or -1 if the window was closed.</summary>
    int Ask(string title, string text, params DialogChoice[] choices);

    /// <summary>A notice with an OK button.</summary>
    void Tell(string title, string text);

    /// <summary>The second warning of "Remove Dictum completely": true only when the user typed <paramref name="word"/> and confirmed.</summary>
    bool TypeToConfirm(string title, string text, string word);

    /// <summary>The first-run hotkey check (SPEC §20): the capture box, defaulting to the current hotkey. Returns the hotkey to keep.</summary>
    string HotkeyCheck(AppShell shell, string current);

    /// <summary>Settings > Models > Model folder > Browse…: the system's folder chooser. Returns the folder picked, or null.</summary>
    string? ChooseFolder(string initialDir);
}
