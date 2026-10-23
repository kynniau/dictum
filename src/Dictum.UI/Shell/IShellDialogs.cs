using Dictum.App;
using Dictum.UI.Views.Onboarding;

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

    /// <summary>
    /// The first run's "Welcome to Dictum" window (1.2; it replaces the model pickers and the hotkey check, SPEC §20): steps 1-5
    /// ask through <paramref name="flow"/>, which writes each answer; returns when they are done or the window was closed. Try
    /// it follows in the same window once the shell calls <see cref="OnboardingFlow.BeginTryIt"/>. Step 1's Continue accepts the
    /// Terms of Use (<see cref="OnboardingFlow.AcceptTerms"/>); with <see cref="OnboardingFlow.TermsOnly"/> the window is that
    /// step alone.
    /// </summary>
    void Onboard(OnboardingFlow flow);

    /// <summary>Settings > Models > Model folder > Browse…: the system's folder chooser. Returns the folder picked, or null.</summary>
    string? ChooseFolder(string initialDir);
}
