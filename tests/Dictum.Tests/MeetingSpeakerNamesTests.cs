using Dictum.Audio;
using Dictum.Llm;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Transcribe;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// Owner decision (Kyle, 07/11/2026): typed speaker names never reach a summary engine outside this computer. Every engine other
/// than the in-process local model (own key, Ollama on any host) gets "Other n" labels; Dictum puts the names back in the summary it
/// stores. All names and words here are invented.
/// </summary>
public sealed class MeetingSpeakerNamesTests
{
    private const string First = "Michelle Example", Second = "Ana Sample";
    private const string Reply = "# Summary\n- Other 1 agreed the plan.\n- Other 2 said \"Other 2 options are fine\" [00:03 Others]\n";

    private static readonly Segment[] Transcript =
    [
        new(TimeSpan.Zero, TimeSpan.FromSeconds(2), Channel.Others, "Synthetic first guest"),
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), Channel.Others, "Other 2 options are fine"),
        new(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7), Channel.You, "Synthetic own words"),
    ];

    private static MeetingSpeakers Speakers(int count, Dictionary<int, string> names) =>
        new MeetingSpeakers(MeetingSpeakers.CurrentVersion, "synthetic@00000000", MeetingSpeakers.DefaultThreshold,
            [.. Enumerable.Range(1, count).Select(n => new SpeakerTurn((n - 1) * 3, (n - 1) * 3 + 2, n))], new Dictionary<string, string>())
            .WithNames(names).Validated();

    private sealed class Harness : IDisposable
    {
        private readonly MeetingFixture _files = new();
        public MeetingsSettings Settings;
        public FileMeetingStore Store { get; }
        public MeetingsHost Host { get; }
        public CapturingClient Cloud { get; } = new();
        public CapturingClient Local { get; } = new();
        public MeetingId Id { get; }

        public Harness(SummaryEngine engine)
        {
            Settings = new() { Enabled = true, SummaryEngine = engine };
            var paths = new AppPaths(Path.Combine(_files.Root, "config"), Path.Combine(_files.Root, "local"), Path.Combine(_files.Root, "exe"));
            Directory.CreateDirectory(paths.ConfigDir);
            Store = _files.Store();
            Id = Store.Create(_files.Meta());
            Store.AppendSegments(Id, Transcript);
            Store.WriteSpeakers(Id, Speakers(2, new() { [1] = First, [2] = Second }));
            Host = new MeetingsHost(paths, () => Settings, Store, (_, _, _) => throw new NotSupportedException(), new NoTranscriber(),
                () => throw new NotSupportedException(),
                (requested, _) => Task.FromResult<ILlmClient>(requested == SummaryEngine.Local ? Local : Cloud), _ => { }, (_, _) => { },
                _ => Task.FromResult<bool?>(null), () => false, () => { })
            {
                SummaryHost = () => "summary.example",
                LocalSummaryClient = _ => Task.FromResult<ILlmClient>(Local),
            };
        }

        public void Dispose() => _files.Dispose();
    }

    [Theory]
    [InlineData(SummaryEngine.OwnKey)]
    [InlineData(SummaryEngine.Ollama)]
    public async Task Cloud_summary_chunks_never_contain_typed_names(SummaryEngine engine)
    {
        using var harness = new Harness(engine);
        await harness.Host.Regenerate(harness.Id, "general", TestContext.Current.CancellationToken);

        Assert.Empty(harness.Local.Requests);
        Assert.NotEmpty(harness.Cloud.Requests);
        // The exact text handed to the engine: system prompt and user turn, every call (chunks and the combine).
        foreach (var (system, user) in harness.Cloud.Requests)
            foreach (var sent in new[] { system, user })
                foreach (var word in new[] { First, Second, "Michelle", "Sample" })
                    Assert.DoesNotContain(word, sent, StringComparison.OrdinalIgnoreCase);
        var chunk = harness.Cloud.Requests[0].User;
        Assert.Contains("[00:00 Others] Other 1: Synthetic first guest", chunk);
        Assert.Contains("[00:03 Others] Other 2: Other 2 options are fine", chunk);
        Assert.Contains("[00:06 You] Synthetic own words", chunk);
    }

    [Fact]
    public async Task Names_are_restored_in_the_stored_cloud_summary()
    {
        using var harness = new Harness(SummaryEngine.OwnKey);
        await harness.Host.Regenerate(harness.Id, "general", TestContext.Current.CancellationToken);

        var stored = harness.Store.ReadSummary(harness.Id)!;
        Assert.Equal("OwnKey:synthetic", stored.Engine);
        Assert.Contains($"- {First} agreed the plan.", stored.Markdown);
        // The label outside the quote is the name; the quote is verbatim transcript and keeps its citation for the validator.
        Assert.Contains($"- {Second} said \"Other 2 options are fine\" [00:03 Others]", stored.Markdown);
        Assert.DoesNotContain("Other 1", stored.Markdown);
        Assert.DoesNotContain(MeetingSummariser.NotWritten, stored.Markdown);
    }

    [Fact]
    public async Task Local_summary_chunks_use_names()
    {
        using var harness = new Harness(SummaryEngine.Local);
        await harness.Host.Regenerate(harness.Id, "general", TestContext.Current.CancellationToken);

        Assert.Empty(harness.Cloud.Requests);
        var chunk = harness.Local.Requests[0].User;
        Assert.Contains($"[00:00 Others] {First}: Synthetic first guest", chunk);
        Assert.Contains($"[00:03 Others] {Second}: Other 2 options are fine", chunk);
    }

    [Fact]
    public void Other_1_is_not_rewritten_inside_Other_10()
    {
        var first = Speakers(10, new() { [1] = First });
        Assert.Equal($"- Other 10 replied to {First}, then Others agreed [00:01 Others].",
            SpeakerNameSwap.Restore("- Other 10 replied to Other 1, then Others agreed [00:01 Others].", first));
        var tenth = Speakers(10, new() { [10] = Second });
        Assert.Equal($"- Other 1 replied to {Second}; AnOther 1 and Other 1x stay.",
            SpeakerNameSwap.Restore("- Other 1 replied to Other 10; AnOther 1 and Other 1x stay.", tenth));
    }

    [Fact]
    public void A_name_that_reads_like_a_label_is_not_replaced_again_and_an_unnamed_speaker_keeps_its_label()
    {
        var speakers = Speakers(3, new() { [1] = "Other 2", [2] = Second });
        Assert.Equal($"- Other 2 asked {Second}; Other 3 listened.", SpeakerNameSwap.Restore("- Other 1 asked Other 2; Other 3 listened.", speakers));
    }

    [Fact]
    public void A_name_with_regex_or_Markdown_characters_goes_in_literally()
    {
        const string name = @"$1 (a+)* [x](y) \d _z_ $$";
        var speakers = Speakers(2, new() { [2] = name });
        Assert.Equal($"- {name} and Other 1.", SpeakerNameSwap.Restore("- Other 2 and Other 1.", speakers));
    }

    [Fact]
    public void A_label_inside_a_quotation_is_never_rewritten()
    {
        var speakers = Speakers(2, new() { [1] = First });
        Assert.Equal($"- {First} said \"Other 1 is late\" [00:00 Others] and “Other 1 again” [00:01 Others]",
            SpeakerNameSwap.Restore("- Other 1 said \"Other 1 is late\" [00:00 Others] and “Other 1 again” [00:01 Others]", speakers));
        // An unpaired quote mark is not a quotation (the validator reads it the same way), and a pair split across lines shields nothing.
        Assert.Equal($"- \"unpaired {First}\n- {First}\" [00:00 Others]",
            SpeakerNameSwap.Restore("- \"unpaired Other 1\n- Other 1\" [00:00 Others]", speakers));
    }

    [Fact]
    public void Without_names_the_reply_is_unchanged_and_the_cloud_copy_has_no_names()
    {
        Assert.Equal(Reply, SpeakerNameSwap.Restore(Reply, null));
        Assert.Equal(Reply, SpeakerNameSwap.Restore(Reply, Speakers(2, new())));
        var named = Speakers(2, new() { [1] = First });
        Assert.Empty(SpeakerNameSwap.WithoutNames(named)!.Names);
        Assert.Equal(named.Turns, SpeakerNameSwap.WithoutNames(named)!.Turns);
        Assert.Equal("Other 1", SpeakerNameSwap.WithoutNames(named)!.Label(1));
    }

    // An unnamed speaker's "Other n" label is not a figure; every other number still is (figure rule unchanged).
    private const string FigureRejection = "A figure or date was not present in the transcript.";
    private static readonly SummaryTemplate General = SummaryTemplates.Get("general");

    private static (Summary? Ok, string? Rejection) Check(string bullet, MeetingSpeakers? speakers) =>
        SummaryValidator.Validate("# Summary\n- " + bullet, Transcript, General, "Local", speakers);

    [Fact]
    public void An_unnamed_speaker_label_does_not_trip_the_figure_rule()
    {
        var result = Check("Other 2 will send the agenda.", Speakers(3, new()));
        Assert.Null(result.Rejection);
        // Masking is for the check only: the summary keeps the label as written.
        Assert.Equal("# Summary\n- Other 2 will send the agenda.", Assert.IsType<Summary>(result.Ok).Markdown);
    }

    [Fact]
    public void A_label_beyond_the_speaker_count_is_still_a_figure()
    {
        Assert.Equal(FigureRejection, Check("Other 7 will send the agenda.", Speakers(3, new())).Rejection);
        Assert.Equal(FigureRejection, Check("Other 0 will send the agenda.", Speakers(3, new())).Rejection);
    }

    [Fact]
    public void A_real_figure_next_to_a_label_is_still_rejected() =>
        Assert.Equal(FigureRejection, Check("Other 2 promised 40 units.", Speakers(3, new())).Rejection);

    [Fact]
    public void Without_speakers_nothing_is_masked() =>
        Assert.Equal(FigureRejection, Check("Other 2 will send the agenda.", null).Rejection);

    [Fact]
    public void Other_1_inside_Other_10_is_not_masked_as_Other_1()
    {
        Assert.Equal(FigureRejection, Check("Other 10 will send the agenda.", Speakers(2, new())).Rejection);
        Assert.Null(Check("Other 10 will send the agenda.", Speakers(10, new())).Rejection);
    }

    [Theory]
    [InlineData("Others 2 will send the agenda.")]
    [InlineData("AnOther 2 will send the agenda.")]
    [InlineData("Other 2x will send the agenda.")]
    [InlineData("Other 02 will send the agenda.")]
    [InlineData("They said \"Other 2 will send it\" without a citation.")]
    // Removing a checked quote must not join "Other " and a stray digit into a label.
    [InlineData("Other \"Other 2 options are fine\" [00:03 Others]2 agreed.")]
    public void Only_a_whole_label_outside_a_quote_is_masked(string bullet) =>
        Assert.Equal(FigureRejection, Check(bullet, Speakers(3, new())).Rejection);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_summary_naming_unnamed_speakers_is_written(bool inProcessEngine)
    {
        var summary = await new MeetingSummariser().SummariseAsync(MeetingId.New(), Transcript, General, new CapturingClient(), "Local",
            TestContext.Current.CancellationToken, Speakers(2, new()), inProcessEngine);
        Assert.DoesNotContain(MeetingSummariser.NotWritten, summary.Markdown);
        Assert.Contains("- Other 1 agreed the plan.", summary.Markdown);
    }

    internal sealed class CapturingClient : ILlmClient
    {
        public List<(string System, string User)> Requests { get; } = [];
        public string Name => "synthetic";
        public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((systemPrompt, rawTranscript));
            return Task.FromResult(new LlmReply(Reply, "stop"));
        }
    }

    private sealed class NoTranscriber : IMeetingTranscriber
    {
        public Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
