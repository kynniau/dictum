using Avalonia.Controls;
using Avalonia.LogicalTree;
using Dictum.Help;
using Dictum.Meetings;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Meetings;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Mac 1.6.0 dictation-only (owner's decision 09/11/2026 00:24 AEDT): where the platform has no credential store the Meetings
/// key cannot exist, so Meetings and Learn my voice are hidden (AppShell.MeetingsAvailable). Windows, where the store is there,
/// keeps every one of them.
/// </summary>
public sealed class MeetingsHiddenWithoutCredentialStoreTests
{
    private static (AppShell Shell, Scratch Scratch) Make(bool storeAvailable, bool meetingsEnabled = false)
    {
        var scratch = new Scratch();
        if (meetingsEnabled)
            Assert.True(SettingsFile.SetValue(scratch.Paths.SettingsFile, "meetings", SettingsFile.MeetingsJson(new MeetingsSettings { Enabled = true })));
        var (shell, platform, _) = scratch.Shell();
        platform.FakeCredentials.Available = storeAvailable;
        return (shell, scratch);
    }

    [Fact]
    public Task Settings_navigation_has_no_Meetings_entry_without_a_store_and_keeps_its_place_with_one() => UiSession.Run(() =>
    {
        var (without, s1) = Make(storeAvailable: false);
        var (with, s2) = Make(storeAvailable: true);
        using (s1) using (s2) using (without) using (with)
        {
            Assert.DoesNotContain(MeetingsText.PROPOSED_Meetings, new SettingsWindow(without).PageNames);
            var names = new SettingsWindow(with).PageNames;
            Assert.Equal(["General", "Mode", "Models", "Storage", "Microphone", "Clean-up", MeetingsText.PROPOSED_Meetings, "Privacy", "About",
                Dictum.Core.HowItWorksText.PageTitle, HelpText.PageTitle], names);
        }
    });

    [Fact]
    public Task Learn_my_voice_card_is_hidden_without_a_store_and_shown_with_one() => UiSession.Run(() =>
    {
        var (without, s1) = Make(storeAvailable: false);
        var (with, s2) = Make(storeAvailable: true);
        using (s1) using (s2) using (without) using (with)
        {
            var hidden = new VoiceCard(without, _ => true); hidden.Refresh();
            Assert.False(hidden.IsVisible);
            var shown = new VoiceCard(with, _ => true); shown.Refresh();
            Assert.True(shown.IsVisible);
        }
    });

    [Fact]
    public Task Flyout_shows_neither_Meetings_entry_without_a_store_even_when_meetings_are_enabled() => UiSession.Run(() =>
    {
        var (without, s1) = Make(storeAvailable: false, meetingsEnabled: true);
        var (with, s2) = Make(storeAvailable: true, meetingsEnabled: true);
        using (s1) using (s2) using (without) using (with)
        {
            var hidden = new FlyoutWindow(without);
            Assert.False(Assert.Single(hidden.GetLogicalDescendants().OfType<MeetingsEntries>()).IsVisible);
            var shown = new FlyoutWindow(with);
            Assert.True(Assert.Single(shown.GetLogicalDescendants().OfType<MeetingsEntries>()).IsVisible);
        }
    });

    [Fact]
    public Task OpenMeetings_opens_no_window_without_a_store() => UiSession.Run(() =>
    {
        var (without, s1) = Make(storeAvailable: false, meetingsEnabled: true);
        using (s1) using (without)
        {
            without.OpenMeetings();
            without.OpenMeetings(start: true);
            Assert.False(without.MeetingsWindowOpen);
        }
    });
}
