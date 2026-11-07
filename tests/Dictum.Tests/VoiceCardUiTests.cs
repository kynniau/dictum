using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Voice;
using Dictum.Settings;
using Dictum.UI.Views;
using static Dictum.Meetings.Voice.VoiceText;

namespace Dictum.Tests;

/// <summary>
/// Settings > General > "Your voice" (design 1.6 C and D), headless: the card's states (model missing, no print, a print, a print from
/// another model), every enabled control reachable by keyboard with an accessible name, the uses greyed with a reason without a print,
/// and Delete returning the card to its un-enrolled state with both uses off.
/// </summary>
public sealed class VoiceCardUiTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();
    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
    private static T Named<T>(ILogical root, string name) where T : Control => All<T>(root).Single(c => AutomationProperties.GetName(c) == name);

    /// <summary>A file of the speaker model's pinned size where the model folder lookup finds it (its content is never read here).</summary>
    private static void PlaceModel(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DefaultModelsDir);
        using var file = File.Create(Path.Combine(paths.DefaultModelsDir, SpeakerModels.Embedding.FileName));
        file.SetLength(SpeakerModels.Embedding.Bytes!.Value);
    }

    private static VoicePrint Print(string model) =>
        new(1, model, 4, [1, 0, 0, 0], 24, 24, new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(11)), new(1, 1));

    private static (SettingsWindow Window, ILogical Card) General(Dictum.UI.Shell.AppShell shell)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("General");
        var card = All<Dictum.UI.Views.Settings.VoiceCard>(window).Single();
        return (window, card);
    }

    [Fact]
    public Task Without_the_speaker_model_the_card_offers_its_download_and_greys_the_uses() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var (window, card) = General(shell);

        var texts = Texts(card);
        Assert.Contains(PROPOSED_CardNoPrint, texts);
        Assert.Contains(SpeakerText.APPROVED_ModelsMissing, texts); // row V14, reused as it stands
        Assert.True(Named<Button>(card, SpeakerText.APPROVED_DownloadModels).IsTabStop);
        Assert.False(Named<Button>(card, PROPOSED_RecordNow).IsEffectivelyVisible);
        foreach (var name in new[] { PROPOSED_UseInMeetings, PROPOSED_YourVoiceOnly })
        {
            var toggle = Named<ToggleSwitch>(card, name);
            Assert.False(toggle.IsEnabled);
            Assert.Equal(PROPOSED_CardNoPrint, AutomationProperties.GetHelpText(toggle)); // greyed, with the reason
        }
        Assert.False(Named<Button>(card, PROPOSED_Delete).IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task With_a_print_every_control_is_named_and_reachable_by_keyboard_and_delete_turns_both_uses_off() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "audio": { "your_voice_only": true } }""");
        PlaceModel(scratch.Paths);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        VoicePrintStore.Write(VoicePrintStore.PathFor(scratch.Paths), Print(VoicePrintStore.CurrentModel), shell.VoiceKeyring);
        var (window, card) = General(shell);

        Assert.Contains("Your voice print was made on 08/11/2026 from 24 seconds of recording.", Texts(card));
        Assert.False(Named<Button>(card, PROPOSED_UseCalibration).IsEffectivelyVisible); // no calibration recordings here
        foreach (var name in new[] { PROPOSED_RecordNow, PROPOSED_Delete })
        {
            var button = Named<Button>(card, name);
            Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.IsTabStop && button.Focusable, name);
        }
        foreach (var name in new[] { PROPOSED_UseInMeetings, PROPOSED_YourVoiceOnly })
        {
            var toggle = Named<ToggleSwitch>(card, name);
            Assert.True(toggle.IsEffectivelyEnabled && toggle.IsTabStop && toggle.Focusable, name);
            Assert.True(toggle.IsChecked); // use 1 on by default once a print exists; use 2 as settings.json says
            Assert.True(toggle.Focus(Avalonia.Input.NavigationMethod.Tab), name);
        }

        ((Dictum.UI.Views.Settings.VoiceCard)card).Delete();

        Assert.False(File.Exists(VoicePrintStore.PathFor(scratch.Paths)));
        var settings = SettingsLoader.LoadFile(scratch.Paths.SettingsFile);
        Assert.False(settings.Settings.Audio.YourVoiceOnly);
        Assert.False(settings.Meetings.UseVoicePrint);
        Assert.Contains(PROPOSED_CardNoPrint, Texts(card)); // back to un-enrolled
        Assert.False(Named<Button>(card, PROPOSED_Delete).IsEffectivelyVisible);
        Assert.All(new[] { PROPOSED_UseInMeetings, PROPOSED_YourVoiceOnly }, name => Assert.False(Named<ToggleSwitch>(card, name).IsEnabled));
        window.Close();
    });

    [Fact]
    public Task A_print_from_another_model_is_said_and_not_used() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        PlaceModel(scratch.Paths);
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        VoicePrintStore.Write(VoicePrintStore.PathFor(scratch.Paths), Print("older-model.onnx@00000000"), shell.VoiceKeyring);
        var (window, card) = General(shell);

        Assert.Contains(PROPOSED_OtherModel, Texts(card));
        Assert.False(Named<ToggleSwitch>(card, PROPOSED_UseInMeetings).IsEnabled);
        Assert.True(Named<Button>(card, PROPOSED_Delete).IsTabStop); // it can still be deleted
        window.Close();
    });
}
