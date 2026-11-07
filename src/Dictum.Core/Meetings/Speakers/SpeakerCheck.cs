using System.Globalization;

namespace Dictum.Meetings.Speakers;

/// <summary>
/// Dictum.exe --speaker-check &lt;folder&gt; (design 1.6 B): the measurement the owner runs on his own recordings before speaker
/// separation is called working. The lane that built it never read a real recording; its tests use synthetic numbers only.
/// <list type="bullet">
/// <item>Every &lt;name&gt;.wav (16 kHz mono PCM16) with a reference &lt;name&gt;.rttm beside it is separated as a meeting would be:
/// its turns are the parts the meetings pause segmenter cuts it into (FileSource, as an import is cut, silence_threshold 0.01),
/// less any part whose loudest block is below that threshold. Not applied: the voice gate and whisper (a part they would drop
/// still counts here). Then it is scored: diarization error rate = (missed + false alarm + speaker confusion) / scored reference
/// speech, on 10 ms frames, with a 0.25 s no-score collar either side of every reference boundary, overlapping speech scored (every active reference
/// speaker counts), and the one-to-one speaker mapping that maximises agreement (Hungarian method).</item>
/// <item>A trials.txt of lines "&lt;1|0&gt; &lt;enrolment.wav&gt; &lt;test.wav&gt;" (1 = same speaker; paths relative to the folder)
/// gives the equal error rate of the cosine score between whole-file embeddings.</item>
/// </list>
/// </summary>
public static class SpeakerCheck
{
    public const double Collar = 0.25;
    public const double Frame = 0.01;

    public sealed record DerScore(double Scored, double Missed, double FalseAlarm, double Confusion)
    {
        public double Rate => Scored == 0 ? 0 : (Missed + FalseAlarm + Confusion) / Scored;
        public static DerScore operator +(DerScore a, DerScore b) => new(a.Scored + b.Scored, a.Missed + b.Missed, a.FalseAlarm + b.FalseAlarm, a.Confusion + b.Confusion);
    }

    /// <summary>Reads RTTM "SPEAKER file channel start duration NA NA speaker NA NA" lines; other lines are skipped.</summary>
    public static IReadOnlyList<(double Start, double End, string Speaker)> ReadRttm(IEnumerable<string> lines) =>
    [
        .. lines.Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 8 && f[0] == "SPEAKER")
            .Select(f => (Start: double.Parse(f[3], CultureInfo.InvariantCulture), Duration: double.Parse(f[4], CultureInfo.InvariantCulture), Speaker: f[7]))
            .Select(f => (f.Start, f.Start + f.Duration, f.Speaker)),
    ];

    /// <summary>The diarization error of <paramref name="hypothesis"/> against <paramref name="reference"/>, in seconds (see the class).</summary>
    public static DerScore Score(IReadOnlyList<(double Start, double End, string Speaker)> reference,
        IReadOnlyList<(double Start, double End, string Speaker)> hypothesis, double collar = Collar)
    {
        var end = reference.Concat(hypothesis).Select(s => s.End).DefaultIfEmpty(0).Max();
        var frames = (int)Math.Ceiling(end / Frame);
        var refNames = reference.Select(s => s.Speaker).Distinct().ToArray();
        var hypNames = hypothesis.Select(s => s.Speaker).Distinct().ToArray();
        bool[][] Active(IReadOnlyList<(double Start, double End, string Speaker)> segments, string[] names) =>
        [
            .. names.Select(name =>
            {
                var on = new bool[frames];
                foreach (var s in segments.Where(s => s.Speaker == name))
                    for (var f = (int)Math.Floor(s.Start / Frame); f < Math.Min(frames, (int)Math.Ceiling(s.End / Frame)); f++)
                        if ((f + 0.5) * Frame >= s.Start && (f + 0.5) * Frame < s.End) on[f] = true;
                return on;
            }),
        ];
        var refOn = Active(reference, refNames);
        var hypOn = Active(hypothesis, hypNames);
        var excluded = new bool[frames];
        foreach (var boundary in reference.SelectMany(s => new[] { s.Start, s.End }))
            for (var f = Math.Max(0, (int)Math.Floor((boundary - collar) / Frame)); f < Math.Min(frames, (int)Math.Ceiling((boundary + collar) / Frame)); f++)
                if (Math.Abs((f + 0.5) * Frame - boundary) < collar) excluded[f] = true;
        var together = new double[refNames.Length, hypNames.Length];
        for (var f = 0; f < frames; f++)
        {
            if (excluded[f]) continue;
            for (var r = 0; r < refNames.Length; r++)
                if (refOn[r][f])
                    for (var h = 0; h < hypNames.Length; h++)
                        if (hypOn[h][f]) together[r, h]++;
        }
        var mapping = BestMapping(together);
        double scored = 0, missed = 0, falseAlarm = 0, confusion = 0;
        for (var f = 0; f < frames; f++)
        {
            if (excluded[f]) continue;
            var refCount = refOn.Count(on => on[f]);
            var hypCount = hypOn.Count(on => on[f]);
            var correct = Enumerable.Range(0, refNames.Length).Count(r => refOn[r][f] && mapping[r] is int h && hypOn[h][f]);
            scored += refCount;
            missed += Math.Max(0, refCount - hypCount);
            falseAlarm += Math.Max(0, hypCount - refCount);
            confusion += Math.Min(refCount, hypCount) - correct;
        }
        return new(scored * Frame, missed * Frame, falseAlarm * Frame, confusion * Frame);
    }

    /// <summary>
    /// The equal error rate of same-speaker (true) and different-speaker (false) scores: the point where the share of different-speaker
    /// trials accepted equals the share of same-speaker trials rejected (the mean of the two at the closest threshold).
    /// </summary>
    public static double EqualErrorRate(IReadOnlyList<(bool Same, double Score)> trials) => EqualErrorPoint(trials).Rate;

    /// <summary><see cref="EqualErrorRate"/> and the threshold it is reached at (a score at or above it is accepted as the same speaker).</summary>
    public static (double Rate, double Threshold) EqualErrorPoint(IReadOnlyList<(bool Same, double Score)> trials)
    {
        var targets = trials.Where(t => t.Same).Select(t => t.Score).ToArray();
        var others = trials.Where(t => !t.Same).Select(t => t.Score).ToArray();
        if (targets.Length == 0 || others.Length == 0) throw new ArgumentException("Trials need both same-speaker and different-speaker pairs.");
        var best = (Gap: double.PositiveInfinity, Rate: 1.0, Threshold: double.PositiveInfinity);
        foreach (var threshold in trials.Select(t => t.Score).Append(double.PositiveInfinity).Distinct().Order())
        {
            var falseReject = targets.Count(s => s < threshold) / (double)targets.Length;
            var falseAccept = others.Count(s => s >= threshold) / (double)others.Length;
            if (Math.Abs(falseAccept - falseReject) < best.Gap) best = (Math.Abs(falseAccept - falseReject), (falseAccept + falseReject) / 2, threshold);
        }
        return (best.Rate, best.Threshold);
    }

    /// <summary>
    /// --speaker-check --print (design 1.6 C.5): a print from every WAV in <paramref name="enrolment"/> (as "Record my voice now" makes
    /// one: the gates of <see cref="Voice.VoicePrintBuilder"/> apply and their numbers are printed), then every window of the speech in
    /// the WAVs of <paramref name="own"/> (same speaker) and <paramref name="other"/> (other speakers) is scored against it: the EER over
    /// those windows and the threshold at the EER, and the time the dictation filter would add per part (each WAV scored as one part).
    /// Exit 0, or 1 when there is no print or nothing to score.
    /// </summary>
    public static int RunPrint(string enrolment, string own, string other, IVoiceEmbedder embedder, string modelTag, TextWriter output)
    {
        foreach (var folder in new[] { enrolment, own, other })
            if (!Directory.Exists(folder)) { output.WriteLine($"error: {folder} is not a folder."); return 1; }
        float[] Read(string path) { using var input = File.OpenRead(path); return SpeakerSeparator.ReadWav(input, CancellationToken.None); }
        string[] Wavs(string folder) => [.. Directory.GetFiles(folder, "*.wav").Order(StringComparer.OrdinalIgnoreCase)];
        var made = Voice.VoicePrintBuilder.Build(Wavs(enrolment).Select(Read).ToList(), embedder, modelTag, DateTimeOffset.Now);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Enrolment: {made.Outcome}; {made.Seconds:0.0} s of speech in {made.Windows} windows (gate {Voice.VoicePrintBuilder.MinSeconds:0} s); " +
            $"{made.ConsistentShare * 100:0.0} % of windows within cosine distance {Voice.VoicePrintBuilder.ConsistentDistance:0.00} of the mean (gate {Voice.VoicePrintBuilder.ConsistentShare * 100:0} %)."));
        if (made.Print is not { } print) return 1;
        var scores = new List<(bool Same, double Score)>();
        var timing = (Parts: 0, Seconds: 0.0, Milliseconds: 0.0);
        foreach (var (folder, same) in new[] { (own, true), (other, false) })
            foreach (var wav in Wavs(folder))
            {
                var samples = Read(wav);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                scores.AddRange(Voice.VoiceScore.SpeechScores(samples, print.Embedding, embedder).Select(score => (same, score)));
                clock.Stop();
                timing = (timing.Parts + 1, timing.Seconds + samples.Length / 16000.0, timing.Milliseconds + clock.Elapsed.TotalMilliseconds);
            }
        if (scores.Count(s => s.Same) == 0 || scores.Count(s => !s.Same) == 0) { output.WriteLine("Nothing to score: own and other need at least one window of speech each."); return 1; }
        var (rate, threshold) = EqualErrorPoint(scores);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"EER over {scores.Count} windows ({scores.Count(s => s.Same)} own, {scores.Count(s => !s.Same)} other): {rate * 100:0.00} % at threshold {threshold:0.000} (voice.threshold now {Settings.VoiceSettings.DefaultThreshold:0.00} by default)."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Scoring time: {timing.Milliseconds / timing.Parts:0} ms per file on average over {timing.Parts} files ({timing.Milliseconds / timing.Seconds:0.0} ms per second of audio), on the CPU."));
        return 0;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; na += (double)a[i] * a[i]; nb += (double)b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>The reference-to-hypothesis assignment with the most agreeing frames; null for a reference speaker left unmapped.</summary>
    internal static int?[] BestMapping(double[,] together)
    {
        int rows = together.GetLength(0), columns = together.GetLength(1), n = Math.Max(rows, columns);
        if (n == 0) return new int?[rows];
        var max = 0.0;
        foreach (var value in together) max = Math.Max(max, value);
        var cost = new double[n + 1, n + 1]; // 1-based, padded square, minimising (max - agreement)
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= n; j++)
                cost[i, j] = i <= rows && j <= columns ? max - together[i - 1, j - 1] : max;
        // Hungarian method, O(n³) (the potentials form).
        var u = new double[n + 1]; var v = new double[n + 1]; var p = new int[n + 1]; var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                var delta = double.PositiveInfinity;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j]) continue;
                    var current = cost[i0, j] - u[i0] - v[j];
                    if (current < minv[j]) { minv[j] = current; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (var j = 0; j <= n; j++)
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                j0 = j1;
            } while (p[j0] != 0);
            do { var j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
        }
        var mapping = new int?[rows];
        for (var j = 1; j <= n; j++)
            if (p[j] >= 1 && p[j] <= rows && j <= columns) mapping[p[j] - 1] = j - 1;
        return mapping;
    }

    /// <summary>
    /// The turns a meeting of this audio would have, in seconds: the parts FileSource's pause segmenter cuts it into (its default
    /// silence threshold, as imports use), less any part whose loudest block is below that threshold (no speech in it).
    /// </summary>
    internal static IReadOnlyList<(double Start, double End)> PauseSegments(float[] samples)
    {
        const float silence = 0.01f; // FileSource's default, which MeetingImporter and BundleAudioSource use
        return
        [
            .. new Transcribe.FileSource(new Audio.CapturedAudio(samples, 0), silenceThreshold: silence).ReadAsync(CancellationToken.None)
                .ToBlockingEnumerable().Where(p => p.Part.PeakLevel >= silence)
                .Select(p => (p.Offset.TotalSeconds, (p.Offset + p.Part.Duration).TotalSeconds)),
        ];
    }

    /// <summary>Runs the check over <paramref name="folder"/> and prints the results; exit code 0, or 1 when there was nothing to score.</summary>
    public static int Run(string folder, IVoiceEmbedder embedder, TextWriter output, double threshold = MeetingSpeakers.DefaultThreshold)
    {
        if (!Directory.Exists(folder)) { output.WriteLine($"error: {folder} is not a folder."); return 1; }
        output.WriteLine($"Speaker check of {folder}: threshold {threshold.ToString("0.00", CultureInfo.InvariantCulture)}; DER on {Frame * 1000:0} ms frames, " +
                         $"collar {Collar.ToString("0.00", CultureInfo.InvariantCulture)} s either side of each reference boundary, overlapping speech scored; turns from the pause segmenter.");
        var total = new DerScore(0, 0, 0, 0);
        var files = 0;
        foreach (var wav in Directory.GetFiles(folder, "*.wav").Order(StringComparer.OrdinalIgnoreCase))
        {
            var rttm = Path.ChangeExtension(wav, ".rttm");
            if (!File.Exists(rttm)) continue;
            float[] samples;
            using (var input = File.OpenRead(wav)) samples = SpeakerSeparator.ReadWav(input, CancellationToken.None);
            var hypothesis = SpeakerSeparator.Diarize(samples, PauseSegments(samples), embedder, threshold, null, CancellationToken.None)
                .Select(t => (t.Start, t.End, t.Speaker.ToString(CultureInfo.InvariantCulture))).ToArray();
            var score = Score(ReadRttm(File.ReadLines(rttm)), hypothesis);
            total += score;
            files++;
            output.WriteLine(Line(Path.GetFileName(wav), score) + $", {hypothesis.Select(h => h.Item3).Distinct().Count()} voices found");
        }
        if (files > 0) output.WriteLine(Line($"All {files} files", total));
        var trials = Path.Combine(folder, "trials.txt");
        var pairs = 0;
        if (File.Exists(trials))
        {
            var cache = new Dictionary<string, float[]?>(StringComparer.OrdinalIgnoreCase);
            float[]? Embedding(string relative)
            {
                var path = Path.GetFullPath(Path.Combine(folder, relative));
                if (cache.TryGetValue(path, out var known)) return known;
                using var input = File.OpenRead(path);
                return cache[path] = embedder.Embed(SpeakerSeparator.ReadWav(input, CancellationToken.None));
            }
            var scored = new List<(bool, double)>();
            foreach (var fields in File.ReadLines(trials).Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(f => f.Length == 3))
                if (Embedding(fields[1]) is { } a && Embedding(fields[2]) is { } b) scored.Add((fields[0] == "1", Cosine(a, b)));
            pairs = scored.Count;
            if (pairs > 0) output.WriteLine($"EER over {pairs} trials ({scored.Count(s => s.Item1)} same-speaker): {EqualErrorRate(scored) * 100:0.00} %");
        }
        if (files == 0 && pairs == 0) { output.WriteLine("Nothing to score: no <name>.wav with a <name>.rttm beside it, and no trials.txt."); return 1; }
        return 0;
    }

    private static string Line(string what, DerScore s) =>
        string.Create(CultureInfo.InvariantCulture, $"{what}: DER {s.Rate * 100:0.00} % (missed {s.Missed:0.0} s, false alarm {s.FalseAlarm:0.0} s, confusion {s.Confusion:0.0} s, of {s.Scored:0.0} s scored)");
}
