using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using Dictum.UI.Views.Meetings;
using static Dictum.Meetings.Speakers.SpeakerText;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.Tests;

public sealed partial class MeetingsWorkflowUiTests
{
    [Fact]
    public Task Identify_speakers_rename_and_fewer_or_more_are_named_and_reachable_by_keyboard() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var (samples, turns) = SyntheticVoices.Track((1, 2), (2, 2), (1, 1.5));
        var id = fixture.Store.Create(fixture.Files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(samples))) fixture.Store.WriteAudio(id, Channel.Others, audio);
        fixture.Store.AppendSegments(id, [.. turns.Select((t, i) => new Segment(TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), Channel.Others, $"Synthetic line {i}"))]);
        var host = fixture.Host with
        {
            SpeakerModelsReady = () => true,
            SpeakerSeparator = store => new SpeakerSeparator(store, new LevelEmbedder(SyntheticVoices.Distinct()), "synthetic@00000000"),
        };
        var window = new MeetingsWindow(host); window.Show(); window.ShowMeeting(id);
        KeyboardReach(window);
        Assert.Contains(APPROVED_Limits, Texts(window));
        Assert.Contains("Others: Synthetic line 0", Texts(window));

        Click(window, APPROVED_SeparateSpeakers);
        await Until(() => All<TextBox>(window).Any(b => AutomationProperties.GetName(b) == string.Format(APPROVED_RenameSpeaker, "Other 2")));
        Dispatcher.UIThread.RunJobs();
        KeyboardReach(window); // the name boxes, Save names, Fewer, More and Identify speakers again, all named and in the tab order
        foreach (var name in new[] { APPROVED_SeparateAgain, APPROVED_SaveNames, APPROVED_FewerSpeakers, APPROVED_MoreSpeakers }) Assert.True(Find(window, name).IsTabStop);
        Assert.Contains("Other 1: Synthetic line 0", Texts(window));
        Assert.Contains("Other 2: Synthetic line 1", Texts(window));

        All<TextBox>(window).Single(b => AutomationProperties.GetName(b) == string.Format(APPROVED_RenameSpeaker, "Other 1")).Text = "Michelle Example";
        Click(window, APPROVED_SaveNames);
        Assert.Equal("Michelle Example", fixture.Store.ReadSpeakers(id)!.Label(1));
        Assert.Contains("Michelle Example: Synthetic line 2", Texts(window));

        // A name the summary's owner rule could not accept is refused at Save, with the reason, and nothing is saved.
        All<TextBox>(window).Single(b => AutomationProperties.GetName(b) == string.Format(APPROVED_RenameSpeaker, "Other 2")).Text = "Ana (HR)";
        Click(window, APPROVED_SaveNames);
        Assert.Contains(APPROVED_InvalidName, Texts(window));
        Assert.Equal("Other 2", fixture.Store.ReadSpeakers(id)!.Label(2));
        Assert.Equal("Michelle Example", fixture.Store.ReadSpeakers(id)!.Label(1));

        Click(window, APPROVED_FewerSpeakers);
        await Until(() => fixture.Store.ReadSpeakers(id)!.Threshold == 0.6);
        window.Close(); return true;
    });

    [Fact]
    public Task Identify_speakers_with_too_little_speech_says_so_and_writes_nothing() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var (samples, turns) = SyntheticVoices.Track((1, 1.0)); // under the 1.5 s window minimum
        var id = fixture.Store.Create(fixture.Files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(samples))) fixture.Store.WriteAudio(id, Channel.Others, audio);
        fixture.Store.AppendSegments(id, [new Segment(TimeSpan.FromSeconds(turns[0].Start), TimeSpan.FromSeconds(turns[0].End), Channel.Others, "Synthetic line 0")]);
        var host = fixture.Host with
        {
            SpeakerModelsReady = () => true,
            SpeakerSeparator = store => new SpeakerSeparator(store, new LevelEmbedder(SyntheticVoices.Distinct()), "synthetic@00000000"),
        };
        var window = new MeetingsWindow(host); window.Show(); window.ShowMeeting(id);
        Click(window, APPROVED_SeparateSpeakers);
        await Until(() => Texts(window).Contains(APPROVED_TooLittleSpeech));
        Assert.DoesNotContain(PROPOSED_Error, Texts(window));
        Assert.Null(fixture.Store.ReadSpeakers(id));
        Assert.False(fixture.Store.HasSpeakers(id));
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
            Assert.Contains(APPROVED_SettingsNote, Texts(page));
            Assert.Contains(ready ? APPROVED_ModelsReady : APPROVED_ModelsNotReady, Texts(page));
            Assert.True(Find(page, APPROVED_DownloadModels).IsTabStop);
            window.Close();
        }
    });

    [Fact]
    public Task Delete_confirmation_says_speaker_names_go_too_only_when_the_meeting_has_a_speakers_file() => UiSession.Run(() =>
    {
        using var fixture = new Fixture();
        var plain = fixture.Store.Create(fixture.Files.Meta());
        var named = fixture.Store.Create(fixture.Files.Meta());
        var later = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.WriteSpeakers(named, new MeetingSpeakers(MeetingSpeakers.CurrentVersion, "synthetic@00000000", MeetingSpeakers.DefaultThreshold,
            [new SpeakerTurn(0, 2, 1)], new Dictionary<string, string> { ["1"] = "Michelle Example" }));
        SyntheticVoices.WriteNewerSpeakersFile(fixture.Files, fixture.Store, later, Path.Combine(fixture.Files.Root, "meetings")); // a file this version cannot read still counts
        foreach (var (id, hasNames) in new[] { (plain, false), (named, true), (later, true) })
        {
            var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
            Click(window, PROPOSED_Delete);
            var confirm = Assert.Single(window.OwnedWindows);
            // The delete question is unchanged; the names sentence is a separate line beside it.
            Assert.Contains(PROPOSED_DeleteQuestion, Texts(confirm));
            Assert.Equal(hasNames, Texts(confirm).Contains(APPROVED_NamesDeleted));
            Click(confirm, PROPOSED_Delete);
            Assert.DoesNotContain(fixture.Store.List(), m => m.Id == id);
            window.Close();
        }
        Assert.Empty(Directory.GetFiles(fixture.Files.Root, "speakers*", SearchOption.AllDirectories));
    });

    [Fact]
    public Task Identify_speakers_without_the_models_says_where_to_get_them() => UiSession.RunAsync(() =>
    {
        using var fixture = new Fixture();
        var id = fixture.Store.Create(fixture.Files.Meta());
        using (var audio = new MemoryStream(SyntheticVoices.Wav(SyntheticVoices.Track((1, 1)).Samples))) fixture.Store.WriteAudio(id, Channel.Mixed, audio);
        var window = new MeetingsWindow(fixture.Host); window.Show(); window.ShowMeeting(id);
        Click(window, APPROVED_SeparateSpeakers);
        Assert.Contains(APPROVED_ModelsMissing, Texts(window));
        Assert.Null(fixture.Store.ReadSpeakers(id));
        window.Close();
        return Task.FromResult(true);
    });
}
