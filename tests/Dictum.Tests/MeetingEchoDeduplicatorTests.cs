using Dictum.Meetings;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Transcribe;

namespace Dictum.Tests;

public class MeetingEchoDeduplicatorTests
{
    private static Segment Part(Channel channel, string text, double start = 0, double end = 1) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), channel, text);

    [Theory]
    [InlineData("Please send the budget on Friday", "Please send the budget on Friday", 0, true)]
    [InlineData("Please send the budget on Friday", "Please send the budget on Friday", 2.5, true)]
    [InlineData("Please send the budget on Friday", "Please send the budget on Friday", 3, true)]
    [InlineData("Please send the budget on Friday", "Please send the budget on Friday", 3.01, false)]
    [InlineData("Um, PLEASE send the budget on Friday!", "Please send the budget on Friday", 0, true)]
    [InlineData("Please send our budget on Friday", "Please send the budget on Friday", 0, true)]
    [InlineData("send the budget on Friday", "Please send the budget on Friday", 0, true)]
    [InlineData("I will review our staffing plan", "Please send the budget on Friday", 0, false)]
    [InlineData("yes", "Please say yes to the budget on Friday", 0, false)]
    [InlineData("yes", "yes", 0, true)]
    [InlineData("yes", "yes", 2, false)]
    [InlineData("Please send the budget on Friday and I will call everyone afterwards", "Please send the budget on Friday", 0, false)]
    [InlineData("um uh", "erm hmm", 0, false)]
    public void Echo_rule_respects_slack_text_similarity_and_partial_speech(string you, string others, double start, bool echo)
    {
        var microphone = Part(Channel.You, you, start, start + 1);
        var remote = Part(Channel.Others, others);
        Assert.Equal(echo, EchoDeduplicator.IsEcho(microphone, remote));
        Assert.Equal(echo, EchoDeduplicator.IsEcho(microphone, new[] { remote }));
        Assert.False(EchoDeduplicator.IsEcho(remote, microphone));
    }

    [Theory]
    [InlineData(.5, true)]
    [InlineData(.501, false)]
    public void Onset_threshold_is_inclusive_and_allows_half_similarity(double start, bool expected)
    {
        var remote = Part(Channel.Others, "one two three four five six seven eight nine ten", 0, 5);
        var you = Part(Channel.You, "red two blue four green six gold eight pink ten", start, 5);
        Assert.Equal(expected, EchoDeduplicator.IsEcho(you, remote));
    }

    [Fact]
    public void Reference_cap_keeps_400_words_nearest_in_time_in_transcript_order()
    {
        var distant = Part(Channel.Others, string.Join(" ", Enumerable.Range(0, 400).Select(i => $"distant{i}")), -2, -1);
        var nearby = Part(Channel.Others, string.Join(" ", Enumerable.Range(0, 400).Select(i => $"nearby{i}")), 0, 1);
        Assert.True(EchoDeduplicator.IsEcho(Part(Channel.You, "nearby100 nearby101 nearby102"), new[] { nearby, distant }));
        Assert.False(EchoDeduplicator.IsEcho(Part(Channel.You, "distant100 distant101 distant102"), new[] { distant, nearby }));
    }

    [Fact]
    public void Window_alignment_spans_two_Others_segments_and_fails_with_pairwise_comparison()
    {
        var first = Part(Channel.Others, "Please review the revised budget before lunch", 0, 4);
        var second = Part(Channel.Others, "Then send the final agenda to everyone", 4, 8);
        var you = Part(Channel.You, first.Text + " " + second.Text, 1, 8);
        Assert.False(EchoDeduplicator.IsEcho(you, first));
        Assert.False(EchoDeduplicator.IsEcho(you, second));
        Assert.Equal(new[] { first, second }, EchoDeduplicator.Filter([you, second, first]).OrderBy(s => s.Start));
    }

    [Fact]
    public void Synthetic_misaligned_meeting_drops_seven_echoes_and_keeps_own_sentence()
    {
        static string[] Sentences(int count, int offset) => Enumerable.Range(offset, (count + 4) / 5)
            .SelectMany(i => new[] { "Review", $"agenda{i}", "with", "the", "team" }).Take(count).ToArray();
        static string Changed(IEnumerable<string> words, int every) => string.Join(" ", words.Select((word, i) =>
            i % every == 0 ? $"revision{i}" : word));
        var a = Sentences(102, 0);
        var b = Sentences(105, 100);
        var c = Sentences(108, 200);
        var d = Sentences(20, 300);
        var e = Sentences(31, 400);
        var f = Sentences(14, 500);
        Segment[] others = [
            Part(Channel.Others, string.Join(" ", a), 2, 28),
            Part(Channel.Others, string.Join(" ", b), 28.6, 53.6),
            Part(Channel.Others, string.Join(" ", c), 54, 81),
            Part(Channel.Others, string.Join(" ", d), 81.9, 86.8),
            Part(Channel.Others, string.Join(" ", e), 87, 95.5),
            Part(Channel.Others, string.Join(" ", f), 96.9, 101),
            Part(Channel.Others, "Please prepare fresh materials for our workshop", 104.6, 107)];
        Segment[] echoes = [
            Part(Channel.You, Changed(a, 4), 1.8, 28),
            Part(Channel.You, Changed(b.Take(29), 10), 28.6, 35.5),
            Part(Channel.You, Changed(b.Skip(25), 10), 35.8, 54),
            Part(Channel.You, Changed(c.Take(99), 4), 53.9, 81),
            Part(Channel.You, Changed(d.Append("soon"), 5), 81.9, 86.8),
            Part(Channel.You, Changed(e, 4), 87, 95.5),
            Part(Channel.You, Changed(f.Take(11), 6), 96, 100)];
        var own = Part(Channel.You, "My separate suggestion concerns access during future sessions", 101.9, 104);
        var transcript = others.Concat(echoes).Append(own).OrderBy(s => s.Start).ToArray();
        var filtered = EchoDeduplicator.Filter(transcript);
        Assert.Equal(others.Append(own).OrderBy(s => s.Start), filtered);
        Assert.Equal(7, transcript.Length - filtered.Count);
        Assert.All(echoes, echo => Assert.DoesNotContain(echo, filtered));
        Assert.Equal(101.9, Assert.Single(filtered.Where(s => s.Channel == Channel.You)).Start.TotalSeconds);
    }

    [Fact]
    public void Exact_echo_keeps_Others_in_either_transcript_order()
    {
        var you = Part(Channel.You, "Review the budget on Friday");
        var others = you with { Channel = Channel.Others };
        Assert.Equal([others], EchoDeduplicator.Filter([you, others]));
        Assert.Equal([others], EchoDeduplicator.Filter([others, you]));
    }

    [Fact]
    public void Read_time_filter_cleans_existing_recordings_without_rewriting_them()
    {
        using var fixture = new MeetingFixture();
        var store = fixture.Store();
        var id = store.Create(fixture.Meta());
        var you = Part(Channel.You, "Review the budget on Friday");
        var others = you with { Channel = Channel.Others };
        store.AppendSegments(id, [you, others]);
        var before = Directory.GetFiles(Path.Combine(fixture.Root, id.Value), "transcript*.enc")
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal([others], fixture.Store().ReadTranscript(id));
        Assert.All(before, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
        Assert.Single(TranscriptChunker.Chunk([you, others], 1000, 0));
        Assert.Equal(others.Text, TranscriptChunker.Chunk([you, others], 1000, 0)[0]);
    }

    [Fact]
    public void Microphone_only_and_Mixed_transcripts_are_untouched()
    {
        var you = Part(Channel.You, "Review the budget on Friday");
        Segment[] transcript = [you, you, you with { Channel = Channel.Mixed }];
        Assert.Equal(transcript, EchoDeduplicator.Filter(transcript));
    }
}
