using System.Buffers.Binary;
using Dictum.Meetings.Store;

namespace Dictum.Meetings.Speakers;

/// <summary>One voice print of a stretch of 16 kHz mono speech: a speaker embedding (CAM++ gives 512 numbers).</summary>
public interface IVoiceEmbedder
{
    /// <summary>The embedding, or null when the audio is too short for the model to judge.</summary>
    float[]? Embed(float[] samples16kMono);
}

/// <summary>Where one voice hands over to another: speech turns, in seconds, in time order. Each turn holds one voice.</summary>
public interface ISpeakerTurnFinder
{
    IReadOnlyList<(double Start, double End)> FindTurns(float[] samples16kMono, double threshold, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Per-meeting speaker separation (design 1.6 B): turns, one embedding per turn, clustering, speaker numbers in order of first
/// appearance, then speakers.json.enc in the meeting folder. It runs after the meeting, on the Others track, or on Mixed when
/// there is no Others track (imports and phone bundles). The You track is never read here.
/// </summary>
// simplification: post-meeting only; live labels need a streaming clusterer; retire when a user asks for names during the meeting.
public sealed class SpeakerSeparator(IMeetingStore store, ISpeakerTurnFinder turns, IVoiceEmbedder embedder, string modelTag) : IDisposable
{
    /// <summary>Frees the native models behind the turn finder and the embedder, if they hold any.</summary>
    public void Dispose()
    {
        (turns as IDisposable)?.Dispose();
        (embedder as IDisposable)?.Dispose();
    }

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

    public async Task<MeetingSpeakers> SeparateAsync(MeetingId id, double threshold, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var track = TrackFor(store, id) ?? throw new InvalidOperationException(SpeakerText.PROPOSED_NothingToSeparate);
        if (track == Channel.You) throw new InvalidOperationException("The You track is never separated.");
        var previous = store.ReadSpeakers(id);
        var result = await Task.Run(() =>
        {
            var samples = ReadSamples(store, id, track, cancellationToken);
            try
            {
                var speakerTurns = Diarize(samples, turns, embedder, threshold, progress, cancellationToken);
                return new MeetingSpeakers(MeetingSpeakers.CurrentVersion, modelTag, threshold, speakerTurns, new Dictionary<string, string>());
            }
            finally { Array.Clear(samples); }
        }, cancellationToken).ConfigureAwait(false);
        result = CarryNames(previous, result);
        MeetingProcessingConsent.Check(cancellationToken);
        store.WriteSpeakers(id, result);
        return result;
    }

    /// <summary>The pipeline on samples: turns, one embedding per turn, clustering, speakers numbered by first appearance.</summary>
    internal static SpeakerTurn[] Diarize(float[] samples, ISpeakerTurnFinder turns, IVoiceEmbedder embedder, double threshold,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var found = turns.FindTurns(samples, threshold, progress, cancellationToken);
        var kept = new List<(double Start, double End)>();
        var embeddings = new List<float[]>();
        foreach (var (start, end) in found.OrderBy(t => t.Start))
        {
            MeetingProcessingConsent.Check(cancellationToken);
            var from = (int)Math.Clamp(start * 16000, 0, samples.Length);
            var to = (int)Math.Clamp(end * 16000, from, samples.Length);
            // simplification: a turn too short for the model to embed is dropped, so its words take the nearest turn's speaker
            // (MeetingSpeakers.SpeakerOf). Ceiling: a one-word interjection by a third voice is not counted as a voice. Trigger to
            // retire: a report of a missed short speaker; then embed short turns with their neighbours.
            if (embedder.Embed(samples[from..to]) is not { } embedding) continue;
            kept.Add((start, end));
            embeddings.Add(embedding);
        }
        var labels = SpeakerClustering.Cluster(embeddings, threshold);
        return [.. kept.Select((t, i) => new SpeakerTurn(Math.Round(t.Start, 3), Math.Round(t.End, 3), labels[i] + 1))];
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
    // simplification: the whole track is held in memory (an hour is about 230 MB of floats), because the segmentation needs it in
    // one array. Ceiling: very long meetings on small machines. Trigger to retire: a meeting over two hours, or an out-of-memory
    // report; then run the segmentation over overlapping windows.
    internal static float[] ReadSamples(IMeetingStore store, MeetingId id, Channel channel, CancellationToken cancellationToken)
    {
        using var audio = store.OpenAudio(id, channel);
        return ReadWav(audio, cancellationToken);
    }

    /// <summary>A 16 kHz mono PCM16 WAV as samples; anything else is refused.</summary>
    internal static float[] ReadWav(Stream audio, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        audio.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException("Not a WAV track.");
        var chunk = new byte[8];
        var formatSeen = false;
        while (true)
        {
            audio.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (length is < 16 or > 4096) throw new InvalidDataException("Invalid WAV format.");
                var format = new byte[length];
                audio.ReadExactly(format);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) != 16000 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) != 16)
                    throw new InvalidDataException("The track is not 16 kHz mono PCM16.");
                formatSeen = true;
                continue;
            }
            if (!chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                audio.ReadExactly(new byte[length + (length & 1)]);
                continue;
            }
            if (!formatSeen || length % 2 != 0) throw new InvalidDataException("Invalid WAV data.");
            var samples = new float[length / 2];
            var buffer = new byte[1 << 16];
            for (long done = 0; done < samples.Length;)
            {
                MeetingProcessingConsent.Check(cancellationToken);
                var count = (int)Math.Min(buffer.Length, (samples.Length - done) * 2);
                audio.ReadExactly(buffer.AsSpan(0, count));
                for (var i = 0; i < count / 2; i++) samples[done + i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2)) / 32768f;
                done += count / 2;
            }
            Array.Clear(buffer);
            return samples;
        }
    }
}
