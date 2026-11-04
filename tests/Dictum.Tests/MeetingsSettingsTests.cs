using Dictum.Meetings;
using Dictum.Settings;

namespace Dictum.Tests;

public sealed class MeetingsSettingsTests
{
    [Fact]
    public void All_meetings_settings_round_trip_through_settings_json()
    {
        using var fixture = new MeetingFixture();
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "settings.json");
        var expected = new MeetingsSettings
        {
            Enabled = true, Folder = Path.Combine(fixture.Root, "recordings"), KeepAudio = KeepAudio.Days90,
            ZoomImport = ZoomImport.Always, ZoomFolder = Path.Combine(fixture.Root, "zoom"),
            SummaryEngine = SummaryEngine.OwnKey, SummaryTemplate = "client-file-note", ShareDefault = ShareDefault.Ask,
        };
        Assert.True(SettingsFile.SetValue(path, "meetings", SettingsFile.MeetingsJson(expected)));
        var loaded = SettingsLoader.LoadFile(path);
        Assert.True(loaded.Usable);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(expected, loaded.Meetings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"meetings\":{}}")]
    [InlineData("{\"meetings\":{\"future_option\":true}}")]
    public void Missing_fields_default_and_unknown_fields_are_ignored(string json)
    {
        var loaded = SettingsLoader.Parse(json);
        Assert.Equal(new MeetingsSettings(), loaded.Meetings);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void Invalid_values_take_defaults_without_disabling_valid_fields()
    {
        var loaded = SettingsLoader.Parse("""{"meetings":{"enabled":true,"summary_engine":"internet","keep_audio":3,"summary_template":"unknown"}}""");
        Assert.True(loaded.Meetings.Enabled);
        Assert.Equal(SummaryEngine.Local, loaded.Meetings.SummaryEngine);
        Assert.Equal(KeepAudio.Forever, loaded.Meetings.KeepAudio);
        Assert.Equal("general", loaded.Meetings.SummaryTemplate);
        Assert.Equal(3, loaded.Warnings.Count);
    }

    [Fact]
    public void Default_file_contains_the_disabled_meetings_section()
    {
        Assert.Equal(new MeetingsSettings(), SettingsLoader.Parse(SettingsFile.DefaultJson("tiny.en")).Meetings);
        Assert.Contains("\"meetings\"", SettingsFile.DefaultJson("tiny.en"));
    }
}
