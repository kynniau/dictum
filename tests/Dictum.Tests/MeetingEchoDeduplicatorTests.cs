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
    [InlineData("Please send the budget on Friday and I will call everyone afterwards", "Please send the budget on Friday", 0, false)]
    [InlineData("um uh", "erm hmm", 0, false)]
    public void Echo_rule_respects_slack_text_similarity_and_partial_speech(string you, string others, double start, bool echo)
    {
        var microphone = Part(Channel.You, you, start, start + 1);
        var remote = Part(Channel.Others, others);
        Assert.Equal(echo, EchoDeduplicator.IsEcho(microphone, remote));
        Assert.Equal(echo, EchoDeduplicator.IsEcho(microphone with { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) },
            remote with { Start = microphone.Start, End = microphone.End }));
        Assert.False(EchoDeduplicator.IsEcho(remote, microphone));
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
