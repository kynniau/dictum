using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Dictum.Audio;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

public class ParagraphingTests
{
    private static (string Separator, string Text) Join(string before, double? gap, double threshold = 2,
        bool trailingSpace = false, bool keepCase = false, string raw = "Next topic") =>
        SegmentJoin.Join(before, raw, "Next topic.", trailingSpace, [], keepCase,
            gap is { } seconds ? TimeSpan.FromSeconds(seconds) : null, threshold);

    [Fact]
    public void Gap_below_threshold_joins_with_a_space() =>
        Assert.Equal((" ", "Next topic."), Join("Finished.", 1.98));

    [Theory]
    [InlineData("Still speaking")]
    [InlineData("Still speaking,")]
    [InlineData("Still speaking:")]
    [InlineData("Still speaking;")]
    [InlineData("")]
    public void Long_gap_without_terminal_punctuation_keeps_the_existing_join(string before)
    {
        var expected = SegmentJoin.Join(before, "Next topic", "Next topic.", false, []);
        Assert.Equal(expected, Join(before, 3));
    }

    [Theory]
    [InlineData("Finished.", 2)]
    [InlineData("Finished!", 3)]
    [InlineData("Finished?", 2.02)]
    [InlineData("He said “finished.”", 2)]
    [InlineData("He said 'finished!'", 2)]
    [InlineData("(Finished.)", 2)]
    [InlineData("[Finished?]", 2)]
    [InlineData("Finished.   ", 2)]
    public void Long_gap_after_terminal_punctuation_inserts_the_spoken_paragraph_break(string before, double gap)
    {
        Assert.Equal((SegmentJoin.LeadingBreak("new paragraph next topic", "Next topic."), "Next topic."), Join(before, gap));
        Assert.Equal("\n\n", Join(before, gap).Separator);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(30)]
    public void Zero_setting_never_adds_an_automatic_break(double gap) =>
        Assert.Equal((" ", "Next topic."), Join("Finished.", gap, threshold: 0));

    [Fact]
    public void Unknown_gap_keeps_the_existing_rule() => Assert.Equal(" ", Join("Finished.", null).Separator);

    [Fact]
    public void An_ellipsis_ends_a_sentence_for_the_paragraph_rule_and_a_trailing_newline_does_not()
    {
        Assert.Equal("\n\n", Join("Finished…", 3).Separator);
        Assert.Equal("", Join("Finished.\n", 3).Separator);
        Assert.False(SentenceStart.EndsSentence("Finished.\n"));
        Assert.False(SentenceStart.EndsSentence(""));
        Assert.True(SentenceStart.EndsSentence("Finished…"));
    }

    [Theory]
    [InlineData(1, 3.0)]   // one voiced block (a click) at 1.0 s is ignored
    [InlineData(2, 3.0)]   // two blocks (40 ms) are still under ConfirmBlocks
    [InlineData(3, 1.0)]   // three blocks (60 ms) count as speech: the gap is 1.0 s
    public void A_click_inside_a_pause_does_not_decide_the_quiet_before_the_next_speech(int clickBlocks, double expectedSeconds)
    {
        var segmenter = new SpeechSegmenter(0.01);
        void Feed(float level, int blocks) { for (var i = 0; i < blocks; i++) segmenter.Add(level); }
        Feed(0.2f, 120);   // "Finished."
        Feed(0.001f, 50);  // the pause cuts the part at 0.7 s; quiet to 1.0 s
        Feed(0.2f, clickBlocks);          // a click, or a short burst, at 1.0 s
        Feed(0.001f, 100 - clickBlocks);  // quiet to 3.0 s in all
        Feed(0.2f, 120);   // real speech
        var quietBefore = segmenter.TailQuietBefore!.Value;
        Assert.Equal(expectedSeconds, quietBefore.TotalSeconds, 0.05);
        Assert.Equal(expectedSeconds >= 2 ? "\n\n" : " ",
            SegmentJoin.Join("Finished.", "Next topic", "Next topic.", false, [], false, quietBefore, 2).Separator);
    }

    [Fact]
    public void The_configured_threshold_is_used()
    {
        Assert.Equal(" ", Join("Finished.", 3, threshold: 4).Separator);
        Assert.Equal("\n\n", Join("Finished.", 4, threshold: 4).Separator);
        Assert.Equal("\n\n", Join("Finished.", 0.5, threshold: 0.5).Separator);
    }

    [Fact]
    public void Trailing_space_does_not_prevent_a_paragraph_and_plain_cleanup_keeps_case() =>
        Assert.Equal(("\n\n", "Next topic."), Join("Finished.", 2, trailingSpace: true, keepCase: true));

    [Fact]
    public void Existing_and_spoken_breaks_keep_precedence()
    {
        Assert.Equal("", Join("Finished.\n", 3).Separator);
        Assert.Equal("\n", Join("Finished.", 3, raw: "new line next topic").Separator);
        Assert.Equal("\n\n", Join("Finished.", 0, threshold: 0, raw: "new paragraph next topic").Separator);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(1800)] // exceeds both the look-back and the silent-part limit
    public void Capture_measures_the_full_quiet_run_across_cuts_and_on_the_stop_tail(int quietBlocks)
    {
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, 16000);
        var parts = new List<CapturedAudio>();
        buffer.SegmentAtPauses(0.01, (audio, _) => parts.Add(audio));
        void Feed(float level, int blocks) => buffer.AppendResampled(Enumerable.Repeat(level, blocks * CaptureBuffer.BlockSamples).ToArray());
        Feed(0.2f, 120);
        Feed(0.001f, Math.Max(35, quietBlocks));
        Feed(0.2f, 120);
        Feed(0.001f, 35);
        Assert.Null(parts[0].QuietBefore);
        Assert.Equal(TimeSpan.FromMilliseconds(Math.Max(35, quietBlocks) * 20), parts.Last(p => p.PeakLevel > 0.01f).QuietBefore);
        Feed(0.001f, 100);
        Feed(0.2f, 10);
        Assert.Equal(TimeSpan.FromMilliseconds(135 * 20), buffer.Seal().QuietBefore);
    }

    [Fact]
    public void Limit_cut_with_speech_already_in_its_tail_preserves_the_quiet_measurement()
    {
        var segmenter = new SpeechSegmenter(0.01);
        SegmentCut? cut = null;
        for (var block = 0; block < 1500; block++)
        {
            // A short pause, whose quietest block is its first, forces a retrospective limit cut.
            var level = block == 1400 ? 0.001f : block is > 1400 and < 1410 ? 0.002f : 0.2f;
            cut = segmenter.Add(level) ?? cut;
        }
        Assert.Equal(CutReason.Limit, cut!.Value.Reason);
        Assert.Equal(1400, cut.Value.Block);
        Assert.Equal(TimeSpan.FromMilliseconds(200), segmenter.TailQuietBefore);
    }

    [Fact]
    public void Continuous_speech_limit_has_zero_quiet_and_unsegmented_audio_has_none()
    {
        var segmenter = new SpeechSegmenter(0.01);
        for (var i = 0; i < 1500; i++) segmenter.Add(0.2f);
        Assert.Equal(TimeSpan.Zero, segmenter.TailQuietBefore);
        var buffer = new CaptureBuffer(SampleEncoding.Float32, 1, 16000);
        buffer.AppendResampled(new float[32000]);
        Assert.Null(buffer.Seal().QuietBefore);
    }

    [Fact]
    public void Setting_defaults_and_generated_file_round_trip()
    {
        Assert.Equal(2, new AppSettings().Cleanup.ParagraphPauseSeconds);
        Assert.Equal(2, SettingsLoader.Parse("{}").Settings.Cleanup.ParagraphPauseSeconds);
        var json = SettingsFile.DefaultJson("base.en");
        Assert.Equal(2, JsonNode.Parse(json)!["cleanup"]!["paragraph_pause_seconds"]!.GetValue<double>());
        var loaded = SettingsLoader.Parse(json);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(2, loaded.Settings.Cleanup.ParagraphPauseSeconds);
    }

    [Theory]
    [InlineData(-1, 0, true)]
    [InlineData(0, 0, false)]
    [InlineData(0.1, 0.5, true)]
    [InlineData(0.5, 0.5, false)]
    [InlineData(3.25, 3.25, false)]
    [InlineData(30, 30, false)]
    [InlineData(31, 30, true)]
    public void Setting_loads_and_clamps_with_a_warning(double value, double expected, bool warned)
    {
        var loaded = SettingsLoader.Parse("{\"cleanup\":{\"paragraph_pause_seconds\":" + value.ToString(CultureInfo.InvariantCulture) + "}}");
        Assert.Equal(expected, loaded.Settings.Cleanup.ParagraphPauseSeconds);
        Assert.Equal(warned ? 1 : 0, loaded.Warnings.Count);
        if (warned) Assert.Contains("cleanup.paragraph_pause_seconds", Assert.Single(loaded.Warnings));
    }

    [Theory]
    [InlineData("\"two\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1e999")]
    public void Malformed_setting_uses_the_default_with_a_warning(string value)
    {
        var loaded = SettingsLoader.Parse("{\"cleanup\":{\"paragraph_pause_seconds\":" + value + "}}");
        Assert.Equal(2, loaded.Settings.Cleanup.ParagraphPauseSeconds);
        Assert.Contains("cleanup.paragraph_pause_seconds", Assert.Single(loaded.Warnings));
    }


    [Fact]
    public void Malformed_setting_warning_states_the_accepted_range_not_the_double_limits()
    {
        var warning = Assert.Single(SettingsLoader.Parse("{\"cleanup\":{\"paragraph_pause_seconds\":\"two\"}}").Warnings);
        Assert.Contains("expected 0, or a number from 0.5 to 30", warning);
        Assert.DoesNotContain("E+308", warning);
    }
    [Fact]
    public Task Cleanup_row_is_labelled_and_saves_a_fraction_or_zero() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        foreach (var value in new[] { 3.25m, 0m, 0.1m })
        {
            window.ShowPage("Clean-up");
            var box = window.GetLogicalDescendants().OfType<NumericUpDown>().Single();
            Assert.Equal(CleanupPage.PROPOSED_ParagraphPauseLabel, AutomationProperties.GetName(box));
            Assert.Equal(CleanupPage.PROPOSED_ParagraphPauseHelp, AutomationProperties.GetHelpText(box));
            box.Value = value;
            window.ShowPage("General");
            var expected = value == 0.1m ? 0.5 : (double)value;
            Assert.Equal(expected, shell.Settings.Cleanup.ParagraphPauseSeconds);
            Assert.Equal(expected, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Cleanup.ParagraphPauseSeconds);
        }
        window.Close();
        shell.Dispose();
    });
}
