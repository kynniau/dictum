using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using Dictum.UI.Views.Meetings;
using static Dictum.Meetings.Speakers.SpeakerText;

namespace Dictum.Tests;

public sealed partial class MeetingsWorkflowUiTests
{
    [Fact]
    public Task Identify_speakers_rename_and_fewer_or_more_are_named_and_reachable_by_keyboard() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2), (1, 1));
        var id = fixture.Store.Create(fixture.Files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(samples))) fixture.Store.WriteAudio(id, Channel.Others, audio);
        fixture.Store.AppendSegments(id, [.. turns.Select((t, i) => new Segment(TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), Channel.Others, $"Synthetic line {i}"))]);
        var host = fixture.Host with
        {
            SpeakerModelsReady = () => true,
            SpeakerSeparator = store => new SpeakerSeparator(store, new LevelTurnFinder(), new LevelEmbedder(SyntheticVoices.Distinct()), "synthetic@00000000"),
        };
        var window = new MeetingsWindow(host); window.Show(); window.ShowMeeting(id);
        KeyboardReach(window);
        Assert.Contains(PROPOSED_Limits, Texts(window));
        Assert.Contains("Others: Synthetic line 0", Texts(window));

        Click(window, PROPOSED_SeparateSpeakers);
        await Until(() => All<TextBox>(window).Any(b => AutomationProperties.GetName(b) == string.Format(PROPOSED_RenameSpeaker, "Other 2")));
        Dispatcher.UIThread.RunJobs();
        KeyboardReach(window); // the name boxes, Save names, Fewer, More and Identify speakers again, all named and in the tab order
        foreach (var name in new[] { PROPOSED_SeparateAgain, PROPOSED_SaveNames, PROPOSED_FewerSpeakers, PROPOSED_MoreSpeakers }) Assert.True(Find(window, name).IsTabStop);
        Assert.Contains("Other 1: Synthetic line 0", Texts(window));
        Assert.Contains("Other 2: Synthetic line 1", Texts(window));

        All<TextBox>(window).Single(b => AutomationProperties.GetName(b) == string.Format(PROPOSED_RenameSpeaker, "Other 1")).Text = "Michelle Example";
        Click(window, PROPOSED_SaveNames);
        Assert.Equal("Michelle Example", fixture.Store.ReadSpeakers(id)!.Label(1));
        Assert.Contains("Michelle Example: Synthetic line 2", Texts(window));

        Click(window, PROPOSED_FewerSpeakers);
        await Until(() => fixture.Store.ReadSpeakers(id)!.Threshold == 0.6);
        window.Close(); return true;
    });

    [Fact]
    public Task Settings_Meetings_shows_the_speaker_models_state_and_a_named_download_button() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        using var shell = fixture.Shell();
        foreach (var ready in new[] { false, true })
        {
            var page = new Dictum.UI.Views.Settings.MeetingsPage(shell, fixture.Host with { SpeakerModelsReady = () => ready });
            var window = new Window { Content = page, Width = 900, Height = 1000 }; window.Show(); page.Refresh();
            Assert.Contains(PROPOSED_SettingsNote, Texts(page));
            Assert.Contains(ready ? PROPOSED_ModelsReady : PROPOSED_ModelsNotReady, Texts(page));
            Assert.True(Find(page, PROPOSED_DownloadModels).IsTabStop);
            window.Close();
        }
    });

    [Fact]
    public Task Identify_speakers_without_the_models_says_where_to_get_them() => UiSession.RunAsync(() =>
    {
        using var fixture = new Fixture();
        var id = fixture.Store.Create(fixture.Files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(SyntheticVoices.Track((1, 1)).Samples))) fixture.Store.WriteAudio(id, Channel.Mixed, audio);
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        Click(window, PROPOSED_SeparateSpeakers);
        Assert.Contains(PROPOSED_ModelsMissing, Texts(window));
        Assert.Null(fixture.Store.ReadSpeakers(id));
        window.Close();
        return Task.FromResult(true);
    });
}
