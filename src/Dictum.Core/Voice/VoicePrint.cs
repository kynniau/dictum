using System.Text.Json.Serialization;
using Dictum.Meetings.Speakers;

namespace Dictum.Meetings.Voice;

/// <summary>How alike the enrolment windows were to the print: the mean cosine similarity and its 20th percentile.</summary>
public sealed record VoiceSelfSimilarity(
    [property: JsonPropertyName("mean")] double Mean,
    [property: JsonPropertyName("p20")] double P20);

/// <summary>
/// voiceprint.enc's plaintext (design 1.6 C.2): one unit-length embedding of the owner's voice, made by the CAM++ model named in
/// <see cref="Model"/> from <see cref="Windows"/> windows of speech. No audio, no transcript, no name.
/// </summary>
/// <param name="Seconds">
/// The seconds of speech the windows covered, which the card states (row V5). Not in design C.2's shape: added by the lane because V5
/// names the seconds and C.2 stores only the window count, from which the seconds cannot be recovered (reported for the owner).
/// </param>
public sealed record VoicePrint(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("embedding")] float[] Embedding,
    [property: JsonPropertyName("windows")] int Windows,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("enrolledAt")] DateTimeOffset EnrolledAt,
    [property: JsonPropertyName("selfSimilarity")] VoiceSelfSimilarity SelfSimilarity)
{
    public const int CurrentVersion = 1;

    /// <summary>Throws InvalidDataException for anything that is not a print this version can use (a file is data, not trusted).</summary>
    public VoicePrint Validated()
    {
        if (Version != CurrentVersion || string.IsNullOrWhiteSpace(Model) || Embedding is null || Dimension <= 0 || Embedding.Length != Dimension ||
            !Array.TrueForAll(Embedding, float.IsFinite) || Windows <= 0 || !double.IsFinite(Seconds) || Seconds <= 0 || SelfSimilarity is null ||
            !double.IsFinite(SelfSimilarity.Mean) || !double.IsFinite(SelfSimilarity.P20))
            throw new InvalidDataException("Invalid voice print.");
        return this;
    }
}

/// <summary>What one enrolment attempt came to.</summary>
public enum EnrolmentOutcome { Made, TooLittleSpeech, Inconsistent, ModelMissing }

/// <summary>The outcome, the print when one was made, and the numbers the gates read (for the log and the harness, never shown).</summary>
public sealed record EnrolmentResult(EnrolmentOutcome Outcome, VoicePrint? Print, double Seconds, int Windows, double ConsistentShare)
{
    /// <summary>The card's sentence for a refusal (rows V11, V12, V14), or null when a print was made.</summary>
    public string? Refusal => Outcome switch
    {
        EnrolmentOutcome.TooLittleSpeech => VoiceText.PROPOSED_TooShort,
        EnrolmentOutcome.Inconsistent => VoiceText.PROPOSED_Inconsistent,
        EnrolmentOutcome.ModelMissing => SpeakerText.APPROVED_ModelsMissing,
        _ => null,
    };
}

/// <summary>
/// Builds a voice print (design 1.6 C.1): each clip's speech parts (the pause segmenter that <c>--speaker-check</c> and imports use)
/// are cut into speaker separation's windows (<see cref="SpeakerSeparator.WindowSeconds"/>, <see cref="SpeakerSeparator.HopSeconds"/>,
/// <see cref="SpeakerSeparator.MinWindowSeconds"/>), every window is embedded, and the print is the unit-normalised mean.
/// </summary>
public static class VoicePrintBuilder
{
    /// <summary>
    /// The least speech the windows must cover, in seconds. Starting value from design C.1 (row V11 names it); not measured on a real
    /// voice: the owner measures it with <c>--speaker-check --print</c> before release (C.5).
    /// </summary>
    public const double MinSeconds = 20;

    /// <summary>The share of windows that must sit within <see cref="ConsistentDistance"/> of the mean (design C.1 starting value).</summary>
    public const double ConsistentShare = 0.8;

    /// <summary>The largest cosine distance (1 − cosine) from the mean at which a window counts as consistent (design C.1 starting value).</summary>
    public const double ConsistentDistance = 0.5;

    private const int Rate = 16000;

    /// <summary>A print from these 16 kHz mono clips, or why not. Nothing is written here.</summary>
    public static EnrolmentResult Build(IEnumerable<float[]> clips, IVoiceEmbedder embedder, string modelTag, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var embeddings = new List<float[]>();
        double seconds = 0;
        foreach (var clip in clips)
        {
            foreach (var (start, end) in SpeakerCheck.PauseSegments(clip))
            {
                var covered = new List<(int From, int To)>();
                foreach (var (from, to) in SpeakerSeparator.Windows(start, end, clip.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (embedder.Embed(clip[from..to]) is not { } embedding || !Array.TrueForAll(embedding, float.IsFinite)) continue;
                    embeddings.Add(embedding);
                    covered.Add((from, to));
                }
                seconds += Covered(covered) / (double)Rate;
            }
        }
        seconds = Math.Round(seconds, 1);
        if (seconds < MinSeconds || embeddings.Count == 0) return new(EnrolmentOutcome.TooLittleSpeech, null, seconds, embeddings.Count, 0);
        var mean = UnitMean(embeddings);
        var similarities = embeddings.Select(e => SpeakerCheck.Cosine(e, mean)).Order().ToArray();
        var share = similarities.Count(s => 1 - s <= ConsistentDistance) / (double)similarities.Length;
        if (share < ConsistentShare) return new(EnrolmentOutcome.Inconsistent, null, seconds, embeddings.Count, share);
        var p20 = similarities[(int)Math.Floor(0.2 * (similarities.Length - 1))];
        var print = new VoicePrint(VoicePrint.CurrentVersion, modelTag, mean.Length, mean, embeddings.Count, seconds, now,
            new VoiceSelfSimilarity(Math.Round(similarities.Average(), 4), Math.Round(p20, 4)));
        return new(EnrolmentOutcome.Made, print.Validated(), seconds, embeddings.Count, share);
    }

    /// <summary>The unit-length mean of the embeddings (all of one dimension).</summary>
    internal static float[] UnitMean(IReadOnlyList<float[]> embeddings)
    {
        var dimension = embeddings[0].Length;
        if (embeddings.Any(e => e.Length != dimension)) throw new InvalidDataException("The embeddings differ in length.");
        var sum = new double[dimension];
        foreach (var embedding in embeddings)
            for (var i = 0; i < dimension; i++) sum[i] += embedding[i];
        var norm = Math.Sqrt(sum.Sum(v => v * v));
        if (norm == 0 || !double.IsFinite(norm)) throw new InvalidDataException("The embeddings cancel out.");
        return [.. sum.Select(v => (float)(v / norm))];
    }

    /// <summary>Samples covered by the union of these ranges (windows overlap by design).</summary>
    private static long Covered(List<(int From, int To)> ranges)
    {
        long total = 0;
        var end = int.MinValue;
        foreach (var (from, to) in ranges.OrderBy(r => r.From))
        {
            var start = Math.Max(from, end);
            if (to > start) total += to - start;
            end = Math.Max(end, to);
        }
        return total;
    }
}

/// <summary>
/// Scores audio against a print (design 1.6 C.4 and D): the same windows as speaker separation, each embedded and compared by cosine
/// (<see cref="SpeakerCheck.Cosine"/>) with the print; a window at or above the threshold sounds like the owner.
/// </summary>
public static class VoiceScore
{
    /// <summary>How many of the windows of samples[from..to) embedded, and how many of those scored at or above the threshold.</summary>
    public static (int Scored, int Own) Score(float[] samples, int from, int to, float[] print, IVoiceEmbedder embedder, double threshold)
    {
        var (scored, own) = (0, 0);
        foreach (var (a, b) in SpeakerSeparator.Windows(from / 16000.0, to / 16000.0, samples.Length))
        {
            if (embedder.Embed(samples[a..b]) is not { } embedding || !Array.TrueForAll(embedding, float.IsFinite)) continue;
            scored++;
            if (SpeakerCheck.Cosine(embedding, print) >= threshold) own++;
        }
        return (scored, own);
    }

    /// <summary>More than half of the scored windows sound like the owner (a tie is not a majority).</summary>
    public static bool MostlyOwn(int scored, int own) => scored > 0 && own * 2 > scored;

    /// <summary>More than half of the scored windows do not sound like the owner (a tie is not a majority).</summary>
    public static bool MostlyOther(int scored, int own) => scored > 0 && (scored - own) * 2 > scored;
}
