using Dictum.Meetings.Store;

namespace Dictum.Meetings.Speakers;

/// <summary>One voice print of a stretch of 16 kHz mono speech: a speaker embedding (CAM++ gives 512 numbers).</summary>
public interface IVoiceEmbedder
{
    /// <summary>The embedding, or null when the audio is too short for the model to judge.</summary>
    float[]? Embed(float[] samples16kMono);
}

/// <summary>
/// Per-meeting speaker separation (design 1.6 B), with no model of its own for turns (owner decision, 07/11/2026: pyannote's
/// segmentation is not used). The meeting's own transcript segments on the separated track are the turns: each is cut into
/// fixed windows, every window gets one CAM++ embedding, all windows are clustered together, and each segment takes the
/// majority speaker of its windows. Speakers are numbered in order of first appearance, then speakers.json.enc is written in the
/// meeting folder. It runs after the meeting, on the Others track, or on Mixed when there is no Others track (imports and phone
/// bundles). The You track and its segments are never read here.
/// </summary>
// simplification: post-meeting only; live labels need a streaming clusterer; retire when a user asks for names during the meeting.
// simplification: one speaker per transcript segment. Ceiling: two people speaking in one segment, with no pause between them for
// the pause segmenter to cut at, are labelled as one (the majority of its windows). Trigger to retire: a report of a mixed line;
// then split a segment where its windows' speakers change.
public sealed class SpeakerSeparator(IMeetingStore store, IVoiceEmbedder embedder, string modelTag) : IDisposable
{
    /// <summary>
    /// Each window is 2 s, the next starting 1 s later (half overlapping); a segment shorter than a window is one window. The
    /// extractor's own minimum says what it will run on, not what it judges well: on 07/11/2026 CAM++ through sherpa-onnx embedded
    /// 80 samples (5 ms). On two synthetic voices (Windows TTS, 54 s of speech; not a quality measure) windows of 0.25 to 1 s did
    /// not tell the voices apart (mean cosine distance between different voices 0.21 to 0.27, within the same voice 0.18 to 0.20),
    /// 1.5 s partly (37.5 % of different-voice pairs closer than 0.5), and 2 s did (1.0 %, and no same-voice pair at 0.5 or more).
    /// Meeting segments are cut at pauses after at least 2 s of speech (SpeechSegmenter.MinSpeechBlocks), so most hold a window.
    /// </summary>
    public const double WindowSeconds = 2.0, HopSeconds = 1.0;

    /// <summary>
    /// A segment under this length gets no window (its words take the nearest turn's speaker, MeetingSpeakers.SpeakerOf): shorter
    /// windows did not separate the two synthetic voices above at all.
    /// </summary>
    public const double MinWindowSeconds = 1.5;

    private const int Rate = 16000;

    /// <summary>The most windows embedded and clustered; above it the windows are sub-sampled evenly (two hours of others' speech at a 1 s hop).</summary>
    // simplification: above 7200 windows only an even 7200 of them are embedded and clustered, because the distance triangle grows
    // with the square (7200 windows are about 100 MB of distances) and the worst case is cubic. Ceiling: a meeting with more than two
    // hours of other people's speech is labelled from every nth window, so a short turn between kept windows can be missed (it takes
    // the speaker of the nearest kept window in time). Trigger to retire: a user with a meeting longer than two hours of others'
    // speech reporting wrong labels; then port hclust-cpp's nearest-neighbour chain and cluster every window.
    internal const int MaxWindows = 7200;

    /// <summary>Frees the native model behind the embedder, if it holds one.</summary>
    public void Dispose() => (embedder as IDisposable)?.Dispose();

    /// <summary>The track speaker separation reads for this meeting (Others, else Mixed), or null when it has neither.</summary>
    public static Channel? TrackFor(IMeetingStore store, MeetingId id)
    {
        foreach (var channel in new[] { Channel.Others, Channel.Mixed })
        {
            try { using var audio = store.OpenAudio(id, channel); return channel; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return null;
    }

    /// <summary>The threshold one step towards fewer (true) or more (false) speakers, kept within 0.1 to 0.9.</summary>
    public static double Step(double threshold, bool fewer) =>
        Math.Round(Math.Clamp(threshold + (fewer ? MeetingSpeakers.ThresholdStep : -MeetingSpeakers.ThresholdStep), 0.1, 0.9), 2);

    /// <summary>
    /// The transcript segments heard on the separated track, in seconds and time order: every segment but You's, and not typed notes
    /// (zero-length Mixed segments). A bundle with one track labels its segments Mixed even when that track is Others.
    /// </summary>
    public static (double Start, double End)[] Turns(IEnumerable<Segment> transcript) =>
    [
        .. transcript.Where(s => s.Channel != Channel.You && s.End > s.Start)
            .Select(s => (s.Start.TotalSeconds, s.End.TotalSeconds)).OrderBy(t => t.Item1),
    ];

    /// <summary>The speakers written, or null (nothing written) when no window survives: an empty transcript, or every segment too short.</summary>
    public async Task<MeetingSpeakers?> SeparateAsync(MeetingId id, double threshold, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var track = TrackFor(store, id) ?? throw new InvalidOperationException(SpeakerText.PROPOSED_NothingToSeparate);
        if (track == Channel.You) throw new InvalidOperationException("The You track is never separated.");
        var previous = store.ReadSpeakers(id);
        var turns = Turns(store.ReadTranscript(id));
        var result = await Task.Run(() =>
        {
            var samples = ReadSamples(store, id, track, cancellationToken);
            try
            {
                var speakerTurns = Diarize(samples, turns, embedder, threshold, progress, cancellationToken);
                if (speakerTurns.Length == 0) return null;
                return new MeetingSpeakers(MeetingSpeakers.CurrentVersion, modelTag, threshold, speakerTurns, new Dictionary<string, string>());
            }
            finally { Array.Clear(samples); }
        }, cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        result = CarryNames(previous, result);
        MeetingProcessingConsent.Check(cancellationToken);
        store.WriteSpeakers(id, result);
        return result;
    }

    /// <summary>The sample ranges [From, To) of one segment's windows (see <see cref="WindowSeconds"/>), within the track.</summary>
    internal static IEnumerable<(int From, int To)> Windows(double start, double end, int trackLength)
    {
        var from = (int)Math.Clamp(Math.Round(start * Rate), 0, trackLength);
        var to = (int)Math.Clamp(Math.Round(end * Rate), from, trackLength);
        int window = (int)(WindowSeconds * Rate), hop = (int)(HopSeconds * Rate), floor = (int)(MinWindowSeconds * Rate);
        if (to - from < floor) yield break;
        if (to - from <= window) { yield return (from, to); yield break; }
        var last = from;
        for (var at = from; at + window <= to; at += hop) yield return (last = at, at + window);
        if (last + window < to) yield return (to - window, to); // the tail, so the segment's last words are heard
    }

    /// <summary>
    /// The pipeline on samples: windows of each segment, one embedding per window, clustering of all windows, the majority speaker
    /// of each segment's windows (a tie goes to the speaker of the earliest of the tied windows), speakers numbered by first appearance.
    /// </summary>
    internal static SpeakerTurn[] Diarize(float[] samples, IReadOnlyList<(double Start, double End)> segments, IVoiceEmbedder embedder,
        double threshold, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var windows = segments.SelectMany((s, i) => Windows(s.Start, s.End, samples.Length).Select(w => (Segment: i, w.From, w.To))).ToArray();
        var skipped = new HashSet<int>(); // segments with a window the cap left out
        if (windows.Length > MaxWindows) // even sub-sampling, in time order, before the costly embedding and clustering
        {
            var all = windows; var kept = new HashSet<int>(Enumerable.Range(0, MaxWindows).Select(k => (int)((long)k * all.Length / MaxWindows)));
            for (var k = 0; k < all.Length; k++) if (!kept.Contains(k)) skipped.Add(all[k].Segment);
            windows = [.. Enumerable.Range(0, MaxWindows).Select(k => all[(int)((long)k * all.Length / MaxWindows)])];
        }
        var owners = new List<int>();
        var centres = new List<double>();
        var embeddings = new List<float[]>();
        for (var k = 0; k < windows.Length; k++)
        {
            MeetingProcessingConsent.Check(cancellationToken);
            var (segment, from, to) = windows[k];
            // A non-finite value would poison every distance, so that window is dropped like a too-short one.
            if (embedder.Embed(samples[from..to]) is { } embedding && Array.TrueForAll(embedding, float.IsFinite))
            { owners.Add(segment); centres.Add((from + to) / 2.0 / Rate); embeddings.Add(embedding); }
            progress?.Report((k + 1) / (double)windows.Length);
        }
        var labels = SpeakerClustering.Cluster(embeddings, threshold, 0, cancellationToken);
        var turns = new List<(double Start, double End, int Label)>();
        for (var i = 0; i < segments.Count; i++)
        {
            var heard = Enumerable.Range(0, owners.Count).Where(k => owners[k] == i).Select(k => labels[k]).ToList();
            if (heard.Count == 0 && skipped.Contains(i) && centres.Count > 0)
            {
                // Its windows were not kept: the speaker of the kept window nearest in time.
                var middle = (segments[i].Start + segments[i].End) / 2;
                heard.Add(labels[Enumerable.Range(0, centres.Count).MinBy(k => Math.Abs(centres[k] - middle))]);
            }
            if (heard.Count == 0) continue;
            // GroupBy keeps first-appearance order, so the first group with the top count holds the earliest tied window.
            var majority = heard.GroupBy(l => l).Select(g => (Label: g.Key, Count: g.Count())).Aggregate((best, next) => next.Count > best.Count ? next : best);
            turns.Add((segments[i].Start, segments[i].End, majority.Label));
        }
        var numbers = new Dictionary<int, int>();
        foreach (var turn in turns) numbers.TryAdd(turn.Label, numbers.Count + 1);
        return [.. turns.Select(t => new SpeakerTurn(Math.Round(t.Start, 3), Math.Round(t.End, 3), numbers[t.Label]))];
    }

    /// <summary>
    /// Names given before a "separate again" move to the new speaker that overlaps the named one most, but only where exactly one
    /// named speaker lands on that new speaker: two names merged into one voice are both dropped rather than one silently chosen.
    /// </summary>
    internal static MeetingSpeakers CarryNames(MeetingSpeakers? previous, MeetingSpeakers next)
    {
        if (previous is null || previous.Names.Count == 0) return next;
        var moved = new Dictionary<int, List<string>>();
        foreach (var (key, name) in previous.Names)
        {
            var old = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
            var target = next.Turns.GroupBy(t => t.Speaker).Select(g => (Speaker: g.Key, Overlap: g.Sum(t => previous.Turns.Where(p => p.Speaker == old)
                .Sum(p => Math.Max(0, Math.Min(p.End, t.End) - Math.Max(p.Start, t.Start)))))).Where(x => x.Overlap > 0).OrderByDescending(x => x.Overlap).FirstOrDefault();
            if (target.Overlap <= 0) continue;
            (moved.TryGetValue(target.Speaker, out var list) ? list : moved[target.Speaker] = []).Add(name);
        }
        return next.WithNames(moved.Where(m => m.Value.Count == 1).ToDictionary(m => m.Key, m => m.Value[0]));
    }

    /// <summary>The whole track as 16 kHz mono samples (the store holds PCM16 mono 16 kHz WAV, sealed).</summary>
    // simplification: the whole track is held in memory (an hour is about 230 MB of floats), though only the segments' stretches
    // are embedded. Ceiling: very long meetings on small machines. Trigger to retire: a meeting over two hours, or an out-of-memory
    // report; then read only each segment's stretch.
    internal static float[] ReadSamples(IMeetingStore store, MeetingId id, Channel channel, CancellationToken cancellationToken)
    {
        using var audio = store.OpenAudio(id, channel);
        return ReadWav(audio, cancellationToken);
    }

    /// <summary>A 16 kHz mono PCM16 WAV as samples; anything else is refused (<see cref="StoreWav"/>).</summary>
    internal static float[] ReadWav(Stream audio, CancellationToken cancellationToken)
    {
        var length = StoreWav.SeekData(audio, () => new InvalidDataException("The track is not a 16 kHz mono PCM16 WAV."), cancellationToken);
        var samples = new float[length / 2];
        var buffer = new byte[1 << 16];
        try
        {
            for (long done = 0; done < samples.Length;)
            {
                MeetingProcessingConsent.Check(cancellationToken);
                var count = (int)Math.Min(buffer.Length, (samples.Length - done) * 2);
                audio.ReadExactly(buffer.AsSpan(0, count));
                StoreWav.ToSamples(buffer.AsSpan(0, count), samples.AsSpan((int)done, count / 2));
                done += count / 2;
            }
        }
        finally { Array.Clear(buffer); }
        return samples;
    }
}
