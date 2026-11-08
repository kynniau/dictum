import Foundation

// The speaker rule's self-check, moved out of SpeakerLabels.swift to keep that file under 1,000 lines.

// MARK: - Self-check (no device needed)

#if DEBUG
/// Runs the ported rule on fixed inputs and returns one line per check, "PASS …" or "FAIL …". The cases are the brief's
/// and the desktop's own (tests/Dictum.Tests/MeetingSpeakersTests.cs).
enum SpeakerSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }
        func cluster(_ v: [[Float]], _ t: Double, _ c: Int = 0, linkage: SpeakerParameters.Linkage = .complete) -> [Int] {
            (try? SpeakerClustering.cluster(v, threshold: t, count: c, linkage: linkage)) ?? [-1]
        }
        func windows(_ s: Double, _ e: Double, _ n: Int) -> [[Int]] {
            SpeakerSeparation.windows(start: s, end: e, trackLength: n).map { [$0.from, $0.to] }
        }

        // The brief: two near-identical vectors and one orthogonal at 0.5.
        check("brief: near, near, orthogonal at 0.5", cluster([[1, 0, 0], [0.999, 0.001, 0], [0, 0, 1]], 0.5), [0, 0, 1])
        // The brief: Windows(0, 5.5): 2 s windows at 0, 1, 2, 3 s, then the tail 3.5-5.5 s.
        check("brief: Windows(0, 5.5)", windows(0, 5.5, 16_000 * 60),
              [[0, 32_000], [16_000, 48_000], [32_000, 64_000], [48_000, 80_000], [56_000, 88_000]])

        // Desktop: Clustering_follows_sherpa_cut_rules.
        let points: [[Float]] = [[1, 0], [0.9, 0.1], [0, 1], [0.1, 0.9]]
        check("desktop: cut at 0.5", cluster(points, 0.5), [0, 0, 1, 1])
        check("desktop: cut at 0.001", cluster(points, 0.001), [0, 1, 2, 3])
        check("desktop: cut at 1.01", cluster(points, 1.01), [0, 0, 0, 0])
        check("desktop: count 2", cluster(points, 0.001, 2), [0, 0, 1, 1])
        check("desktop: empty", cluster([], 0.5), [])
        // Desktop: A_merge_exactly_at_the_threshold_is_not_made.
        check("desktop: orthogonal at 1.0", cluster([[1, 0], [0, 1]], 1.0), [0, 1])
        check("desktop: orthogonal just above 1.0", cluster([[1, 0], [0, 1]], (1.0).nextUp), [0, 0])
        check("desktop: identical at 0.0", cluster([[1, 0], [1, 0]], 0.0), [0, 1])
        // Desktop: Clustering_embeddings_that_are_all_non_finite_does_not_throw.
        check("desktop: all NaN", cluster([[.nan, 1], [.nan, 2], [.nan, 3]], 0.5), [0, 1, 2])
        // Desktop: Each_segment_is_cut_into_overlapping_windows_and_a_short_one_into_none.
        let track = 16_000 * 60
        check("desktop: Windows(0, 1.49)", windows(0, 1.49, track), [])
        check("desktop: Windows(0, 1.5)", windows(0, 1.5, track), [[0, 24_000]])
        check("desktop: Windows(1, 3)", windows(1, 3, track), [[16_000, 48_000]])
        check("desktop: Windows(0, 4)", windows(0, 4, track), [[0, 32_000], [16_000, 48_000], [32_000, 64_000]])
        check("desktop: Windows(0, 2.5)", windows(0, 2.5, track), [[0, 32_000], [8_000, 40_000]])
        check("desktop: Windows(0, 5) in 30000", windows(0, 5, 30_000), [[0, 30_000]])
        check("desktop: Windows(0, 5) in 20000", windows(0, 5, 20_000), [])

        // Desktop: A_segment_takes_the_majority_speaker_of_its_windows_and_a_tie_goes_to_the_earliest_window.
        var samples = [Int16](repeating: 0, count: 16_000 * 21 / 2)
        func fill(_ from: Double, _ to: Double, _ voice: Int16) {
            for i in Int(from * 16_000)..<Int(to * 16_000) { samples[i] = voice * 1000 }
        }
        fill(0, 2, 1); fill(3, 4.2, 1); fill(4.2, 7, 2); fill(8, 9.25, 2); fill(9.25, 10.5, 1)
        let majority = try? SpeakerSeparation.separate(samples: samples, segments: [(0, 2), (3, 7), (8, 10.5)],
                                                       embedder: LevelEmbedder(), parameters: .desktop) { _ in }
        check("desktop: majority and earliest-window tie", majority?.turns ?? [],
              [SpeakerTurn(start: 0, end: 2, speaker: 1), SpeakerTurn(start: 3, end: 7, speaker: 2),
               SpeakerTurn(start: 8, end: 10.5, speaker: 2)])

        // Desktop: SpeakerOf, the nearest-turn rule for a segment too short to embed.
        let turns = [SpeakerTurn(start: 0, end: 2, speaker: 1), SpeakerTurn(start: 5, end: 8, speaker: 2)]
        check("SpeakerOf: overlap", SpeakerSeparation.speakerOf(start: 1.5, end: 3, turns: turns), 1)
        check("SpeakerOf: nearest within 1 s", SpeakerSeparation.speakerOf(start: 4.2, end: 4.8, turns: turns), 2)
        check("SpeakerOf: nearest 0.8 s away", SpeakerSeparation.speakerOf(start: 2.8, end: 3.2, turns: turns), 1)
        check("SpeakerOf: none within 1 s", SpeakerSeparation.speakerOf(start: 3.1, end: 3.9, turns: turns), nil)
        check("SpeakerOf: no length", SpeakerSeparation.speakerOf(start: 1, end: 1, turns: turns), nil)
        check("SpeakerOf, any distance: 1.1 s away", SpeakerSeparation.speakerOf(start: 3.1, end: 3.9, turns: turns, within: nil), 1)
        check("SpeakerOf, any distance: 20 s away", SpeakerSeparation.speakerOf(start: 28, end: 29, turns: turns, within: nil), 2)
        check("SpeakerOf, any distance: no length", SpeakerSeparation.speakerOf(start: 1, end: 1, turns: turns, within: nil), nil)
        check("clock", [0, 12.9, 59.99, 61, 3599, 3600, 4503.2].map(SpeakerSeparation.clock),
              ["00:00", "00:12", "00:59", "01:01", "59:59", "60:00", "75:03"])

        // Desktop: Clustering_matches_the_plain_complete_linkage_loop_on_random_voices (60 rounds; this generator is not
        // .NET's Random, so the vectors differ from the desktop's but the comparison is the same).
        var rng = SplitMix(seed: 20_261_107)
        var mismatches = 0
        for _ in 0..<60 {
            let size = 1 + Int(rng.next() % 39)
            let vectors = (0..<size).map { _ in (0..<6).map { _ in Float(rng.unit() * 2 - 1) } }
            let threshold = rng.unit() * 1.2
            let fixed = 1 + Int(rng.next() % UInt64(size))
            if cluster(vectors, threshold) != plainLinkage(vectors, threshold) { mismatches += 1 }
            if cluster(vectors, threshold, fixed) != plainLinkage(vectors, threshold, fixed) { mismatches += 1 }
        }
        check("desktop: matches plain complete linkage on 120 random cases (mismatches)", mismatches, 0)

        // Average linkage (phone beta only). Unit vectors at 0, 50 and 100 degrees: 0-1 and 1-2 are 1 - cos 50 = 0.357
        // apart, 0-2 is 1 - cos 100 = 1.174. After 0 and 1 merge, the distance to 2 is 1.174 complete, 0.765 average.
        func angle(_ degrees: Double) -> [Float] { [Float(cos(degrees * .pi / 180)), Float(sin(degrees * .pi / 180))] }
        let chain = [angle(0), angle(50), angle(100)]
        check("average: chain at 0.8", cluster(chain, 0.8, linkage: .average), [0, 0, 0])
        check("complete: chain at 0.8", cluster(chain, 0.8, linkage: .complete), [0, 0, 1])
        check("average: chain at 0.6", cluster(chain, 0.6, linkage: .average), [0, 0, 1])
        check("average: cut at 0.5", cluster(points, 0.5, linkage: .average), [0, 0, 1, 1])
        // Chain at 0.6 average: pair distances 0.357, 0.357, 1.174; one merge at 0.357; then (1.174 + 0.357) / 2 = 0.765
        // is left between the two clusters.
        let d = try? SpeakerClustering.clusterWithDiagnostics(chain, threshold: 0.6, linkage: .average).diagnostics
        func r3(_ x: Double?) -> String { x.map { String(format: "%.3f", $0) } ?? "nil" }
        check("diagnostics: p50, p90, max", [r3(d?.p50), r3(d?.p90), r3(d?.max)], ["0.357", "1.174", "1.174"])
        check("diagnostics: largest merge, smallest left", [r3(d?.largestMerge), r3(d?.smallestBetweenClusters)],
              ["0.357", "0.765"])
        var averageMismatches = 0
        for _ in 0..<60 {
            let size = 1 + Int(rng.next() % 39)
            let vectors = (0..<size).map { _ in (0..<6).map { _ in Float(rng.unit() * 2 - 1) } }
            let threshold = rng.unit() * 1.2
            let fixed = 1 + Int(rng.next() % UInt64(size))
            if cluster(vectors, threshold, linkage: .average) != plainLinkage(vectors, threshold, average: true) { averageMismatches += 1 }
            if cluster(vectors, threshold, fixed, linkage: .average) != plainLinkage(vectors, threshold, fixed, average: true) {
                averageMismatches += 1
            }
        }
        check("average: matches the plain mean-pair loop on 120 random cases (mismatches)", averageMismatches, 0)

        // Segment mean: segments 0-3 s and 4-7 s are voice 1 (two windows each), 8-10 s voice 2 (one window). One vector
        // per segment, so 3 clustered; the desktop rule clusters the 5 windows.
        var meanSamples = [Int16](repeating: 0, count: 16_000 * 11)
        for (from, to, voice) in [(0.0, 3.0, Int16(1)), (4, 7, 1), (8, 10, 2)] {
            for i in Int(from * 16_000)..<Int(to * 16_000) { meanSamples[i] = voice * 1000 }
        }
        let meanRun = try? SpeakerSeparation.separate(samples: meanSamples, segments: [(0, 3), (4, 7), (8, 10)],
                                                      embedder: LevelEmbedder(), parameters: .current) { _ in }
        check("segment mean: speakers", meanRun?.turns.map(\.speaker) ?? [], [1, 1, 2])
        check("segment mean: vectors clustered", meanRun?.clustered ?? -1, 3)
        check("desktop rule: vectors clustered", (try? SpeakerSeparation.separate(
            samples: meanSamples, segments: [(0, 3), (4, 7), (8, 10)], embedder: LevelEmbedder(), parameters: .desktop) { _ in })?
            .clustered ?? -1, 5)

        // Fewer / More speakers: the desktop's SpeakerSeparator.Step (0.1, clamped 0.1-0.9, two places).
        check("step: fewer from 0.7", SpeakerLabels.step(0.7, fewer: true), 0.8)
        check("step: more from 0.7", SpeakerLabels.step(0.7, fewer: false), 0.6)
        check("step: fewer clamps at 0.9", SpeakerLabels.step(0.85, fewer: true), 0.9)
        check("step: more clamps at 0.1", SpeakerLabels.step(0.15, fewer: false), 0.1)
        // Re-clustering the kept voice prints gives what a full run at that threshold gives, with no new embedding.
        let segs: [(start: Double, end: Double)] = [(0, 3), (4, 7), (8, 10)]
        let kept = try? SpeakerSeparation.embed(samples: meanSamples, segments: segs, embedder: LevelEmbedder(),
                                                parameters: .current) { _ in }
        var high = SpeakerParameters.current
        high.threshold = 1.5
        check("recluster: kept prints at 1.5 equal a full run at 1.5",
              kept.flatMap { try? SpeakerSeparation.assign($0, segments: segs, parameters: high) }?.turns ?? [],
              (try? SpeakerSeparation.separate(samples: meanSamples, segments: segs, embedder: LevelEmbedder(),
                                               parameters: high) { _ in })?.turns ?? [SpeakerTurn(start: -1, end: -1, speaker: -1)])
        check("recluster: kept prints at 1.5 give one speaker",
              kept.flatMap { try? SpeakerSeparation.assign($0, segments: segs, parameters: high) }?.turns.map(\.speaker) ?? [],
              [1, 1, 1])
        return out
    }

    /// The desktop test's LevelEmbedder with SyntheticVoices.Distinct: the window's most common non-zero level picks a
    /// fixed voice (levels here are multiples of 1000 in Int16, i.e. about voice / 32.768).
    private final class LevelEmbedder: VoiceEmbedder {
        var dim: Int { 4 }
        func embed(_ samples16k: [Float]) -> [Float]? {
            if samples16k.count < 1600 { return nil }
            var counts: [Int: Int] = [:]
            var order: [Int] = []
            for s in samples16k where s != 0 {
                let level = Int((s * 32768 / 1000).rounded())
                if counts[level] == nil { order.append(level) }
                counts[level, default: 0] += 1
            }
            guard var best = order.first else { return nil }
            for level in order.dropFirst() where counts[level]! > counts[best]! { best = level }
            var voice: [Float] = [0, 0, 0, 0]
            voice[best - 1] = 1
            return voice
        }
    }

    /// The desktop test's reference loop (PlainCompleteLinkage); with `average`, the link is the mean over member pairs.
    private static func plainLinkage(_ embeddings: [[Float]], _ threshold: Double, _ count: Int = 0,
                                     average: Bool = false) -> [Int] {
        let n = embeddings.count
        let unit = embeddings.map { v -> [Float] in
            let l = v.reduce(0.0) { $0 + Double($1) * Double($1) }.squareRoot()
            return v.map { Float(Double($0) / l) }
        }
        var distance = [[Float]](repeating: [Float](repeating: 0, count: n), count: n)
        for i in 0..<n {
            for j in (i + 1)..<max(i + 1, n) {
                var sum = 0.0
                for k in unit[i].indices { sum += Double(unit[i][k]) * Double(unit[j][k]) }
                distance[i][j] = Float(max(0, 1 - sum))
                distance[j][i] = distance[i][j]
            }
        }
        var clusters = (0..<n).map { [$0] }
        while clusters.count > 1 && (count <= 0 || clusters.count > count) {
            var (a, b, height) = (-1, -1, Double.infinity)
            for i in clusters.indices {
                for j in (i + 1)..<max(i + 1, clusters.count) {
                    let pairs = clusters[i].flatMap { x in clusters[j].map { y in distance[x][y] } }
                    let link = average ? Double(Float(pairs.reduce(0.0) { $0 + Double($1) } / Double(pairs.count)))
                        : Double(pairs.max()!)
                    if link < height { (a, b, height) = (i, j, link) }
                }
            }
            if count <= 0 && height >= threshold { break }
            clusters[a].append(contentsOf: clusters[b])
            clusters.remove(at: b)
        }
        var labels = [Int](repeating: 0, count: n)
        for (label, cluster) in clusters.sorted(by: { $0.min()! < $1.min()! }).enumerated() {
            for member in cluster { labels[member] = label }
        }
        return labels
    }

    private struct SplitMix {
        var state: UInt64
        init(seed: UInt64) { state = seed }
        mutating func next() -> UInt64 {
            state &+= 0x9E37_79B9_7F4A_7C15
            var z = state
            z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
            z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
            return z ^ (z >> 31)
        }
        mutating func unit() -> Double { Double(next() >> 11) / Double(1 << 53) }
    }
}
#endif
