import Accelerate
import Foundation
import Observation
import os
#if canImport(SherpaOnnxC)
import SherpaOnnxC
#endif

// Experimental speaker identification for the on-device transcript beta (switch on screen 1, default off).
//
// The desktop's own separation rule, ported: each final transcript segment is cut into windows
// (src/Dictum.Core/Meetings/Speakers/SpeakerSeparator.cs, Windows), every window gets one CAM++ voice print through
// sherpa-onnx, all windows are clustered together with complete linkage on cosine distance (SpeakerClustering.cs), each
// segment takes the majority speaker of its windows, speakers are numbered by first appearance, and a segment with no
// window takes the speaker of the turn it overlaps most, else the nearest within a second (MeetingSpeakers.SpeakerOf).
// Owner decision: no pyannote and no sherpa OfflineSpeakerDiarization.
//
// Every tunable value is in `SpeakerParameters` (one struct, one place). `SpeakerParameters.desktop` is the desktop's
// rule exactly; `SpeakerParameters.current` is what the beta runs while a sweep picks final values (coordinator,
// 08/11/2026: on the Mac one real speaker split into 27 clusters with the desktop's rule; same-voice window distances had
// median 0.545 and max 1.08).
//
// What this never does: write the sound, the voice prints, the transcript or the labels anywhere (no file, no bundle
// entry, no defaults, no Keychain), or send them anywhere. The 16 kHz PCM copy lives in `PcmStore` in memory only; it is
// dropped as soon as the labels are made, and everything else is dropped on Done. `SealWriter` and the bundle are
// unchanged: the seal gets exactly the same audio it gets with the switch off.

// MARK: - Voice prints

/// One voice print of a stretch of 16 kHz mono speech (CAM++ gives 512 numbers). Called from one background task at a
/// time.
protocol VoiceEmbedder: AnyObject {
    /// The length of each voice print; 0 when no model is loaded.
    var dim: Int { get }
    /// The voice print, or nil when the audio is too short for the model or the result is not finite.
    func embed(_ samples16k: [Float]) -> [Float]?
}

/// Used only when the sherpa-onnx library is not in this build, so the app still builds and says so.
final class StubVoiceEmbedder: VoiceEmbedder {
    var dim: Int { 0 }
    func embed(_ samples16k: [Float]) -> [Float]? { nil }
}

enum VoiceEmbedders {
    /// The model file, bundled from `Models/` when it is present at build time.
    static let modelName = "3dspeaker_speech_campplus_sv_en_voxceleb_16k"

    /// The desktop's thread count: half the processors, 1 to 4 (src/Dictum.UI/Shell/AppShell.Meetings.cs).
    static var threads: Int { min(max(ProcessInfo.processInfo.processorCount / 2, 1), 4) }

    /// The real embedder when the library is linked and the model is in the app; otherwise the stub. Loads the model,
    /// so call it off the main thread.
    static func make() -> VoiceEmbedder {
        #if canImport(SherpaOnnxC)
        if let path = Bundle.main.path(forResource: modelName, ofType: "onnx"),
           let real = SherpaVoiceEmbedder(modelPath: path, threads: threads) {
            return real
        }
        #endif
        return StubVoiceEmbedder()
    }

    /// True when this build links sherpa-onnx (the model may still be missing).
    static var libraryLinked: Bool {
        #if canImport(SherpaOnnxC)
        return true
        #else
        return false
        #endif
    }
}

#if canImport(SherpaOnnxC)
/// CAM++ through the sherpa-onnx 2.0.1 C API (names from sherpa-onnx/c-api/c-api.h at tag v2.0.1), configured as the
/// desktop's SherpaVoiceEmbedder: model path, threads, provider "cpu", debug 0; CreateStream, AcceptWaveform(16000,
/// floats), InputFinished, IsReady, ComputeEmbedding.
///
/// `@unchecked Sendable`: it is created, used and destroyed by one background task; calls never overlap.
final class SherpaVoiceEmbedder: VoiceEmbedder, @unchecked Sendable {
    private let extractor: OpaquePointer
    let dim: Int

    init?(modelPath: String, threads: Int) {
        let made: OpaquePointer? = modelPath.withCString { model in
            "cpu".withCString { provider in
                var config = SherpaOnnxSpeakerEmbeddingExtractorConfig(
                    model: model, num_threads: Int32(threads), debug: 0, provider: provider)
                return SherpaOnnxCreateSpeakerEmbeddingExtractor(&config)
            }
        }
        guard let made else { return nil }
        let dim = Int(SherpaOnnxSpeakerEmbeddingExtractorDim(made))
        guard dim > 0 else {
            SherpaOnnxDestroySpeakerEmbeddingExtractor(made)
            return nil
        }
        extractor = made
        self.dim = dim
    }

    deinit {
        SherpaOnnxDestroySpeakerEmbeddingExtractor(extractor)
    }

    func embed(_ samples16k: [Float]) -> [Float]? {
        guard let stream = SherpaOnnxSpeakerEmbeddingExtractorCreateStream(extractor) else { return nil }
        defer { SherpaOnnxDestroyOnlineStream(stream) }
        samples16k.withUnsafeBufferPointer { buffer in
            SherpaOnnxOnlineStreamAcceptWaveform(stream, 16_000, buffer.baseAddress, Int32(buffer.count))
        }
        SherpaOnnxOnlineStreamInputFinished(stream)
        guard SherpaOnnxSpeakerEmbeddingExtractorIsReady(extractor, stream) == 1,
              let values = SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(extractor, stream) else { return nil }
        defer { SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding(values) }
        let embedding = Array(UnsafeBufferPointer(start: values, count: dim))
        // A NaN or infinite value would poison every distance.
        return embedding.allSatisfy(\.isFinite) ? embedding : nil
    }
}
#endif

// MARK: - The sound, in memory only

/// The 16 kHz mono 16-bit PCM the seal writer also gets, copied into memory while the switch is on. Never written
/// anywhere.
///
/// `@unchecked Sendable`: all state is behind the lock. The audio tap appends; the main actor takes it after capture
/// has stopped.
final class PcmStore: @unchecked Sendable {
    // simplification: the copy is capped at 60 minutes (115,200,000 bytes), one contiguous array that grows by doubling
    // on the tap's thread. Ceiling: sound after 60 minutes gets no speaker label, and a regrowth copies the whole array
    // once on the tap's thread. Trigger to retire: a meeting over an hour, or a device test showing a dropped tap buffer;
    // then keep chunks, or read the windows back from the sealed session.
    static let capBytes = 115_200_000
    static let capSamples = capBytes / MemoryLayout<Int16>.size

    private struct State {
        var samples: [Int16] = []
        var lastAppend = 0
        var analyzerStart: Int?
        var capHit = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// Audio tap thread: the converted buffer that was just given to the seal writer.
    func append(_ pcm: Data) {
        state.withLock { s in
            let room = Self.capSamples - s.samples.count
            guard room > 0 else { s.capHit = true; s.lastAppend = 0; return }
            pcm.withUnsafeBytes { raw in
                let all = raw.bindMemory(to: Int16.self)
                let kept = min(all.count, room)
                s.samples.append(contentsOf: all.prefix(kept).lazy.map { Int16(littleEndian: $0) })
                s.lastAppend = kept
                if kept < all.count { s.capHit = true }
            }
        }
    }

    /// Audio tap thread, once: the speech analyzer has just been given its first buffer, the same tap buffer whose
    /// converted copy was appended last. The analyzer's time 0 is taken to be the start of that buffer.
    // simplification: if that tap buffer's 16 kHz conversion failed, the offset is one tap buffer (about 0.1 s) late.
    // Ceiling: labels a tenth of a second out. Trigger to retire: a device test that shows windows misaligned.
    func markAnalyzerStart() {
        state.withLock { s in
            if s.analyzerStart == nil { s.analyzerStart = s.samples.count - s.lastAppend }
        }
    }

    /// After capture has stopped: the samples (the store is emptied), the sample at the analyzer's time 0, and whether
    /// the cap was hit.
    func take() -> (samples: [Int16], analyzerStart: Int?, capHit: Bool) {
        state.withLock { s in
            let out = (s.samples, s.analyzerStart, s.capHit)
            s = State()
            return out
        }
    }

    func clear() {
        state.withLock { $0 = State() }
    }
}

// MARK: - The desktop rule, ported

/// One speaker turn: seconds on the recording's timeline, and its speaker (1 = the first voice heard).
struct SpeakerTurn: Equatable, Sendable {
    var start: Double
    var end: Double
    var speaker: Int
}

/// Every value the speaker step can be tuned by. Change `current` only; `desktop` stays the desktop's rule.
struct SpeakerParameters: Equatable, Sendable {
    enum Linkage: Equatable, Sendable {
        /// The desktop's: a merged cluster's distance to another is the largest between their members.
        case complete
        /// UPGMA: the merge height is the mean distance over every pair of the two clusters' members.
        case average
    }

    /// Window length and the step between window starts (SpeakerSeparator.WindowSeconds, HopSeconds).
    var windowSeconds: Double
    var hopSeconds: Double
    /// A segment shorter than this gets no window (SpeakerSeparator.MinWindowSeconds).
    var minWindowSeconds: Double
    var linkage: Linkage
    /// Merges are made while the closest two clusters are nearer than this (cosine distance).
    var threshold: Double
    /// When true, each window's voice print is L2-normalised, a segment's windows are averaged and re-normalised, and
    /// one vector per segment is clustered; every window of a segment then carries that segment's cluster.
    var segmentMean: Bool
    /// A segment that overlaps no turn takes the nearest turn's speaker when it is at most this many seconds away; nil
    /// means at any distance. The desktop's MeetingSpeakers.SpeakerOf uses 1.0.
    var nearestTurnSeconds: Double?

    /// The desktop's rule: SpeakerSeparator's windows 2/1/1.5 s, SpeakerClustering's complete linkage, and
    /// MeetingSpeakers.DefaultThreshold 0.5, one vector per window.
    static let desktop = SpeakerParameters(windowSeconds: 2.0, hopSeconds: 1.0, minWindowSeconds: 1.5,
                                           linkage: .complete, threshold: 0.5, segmentMean: false,
                                           nearestTurnSeconds: 1.0)

    // simplification: values from a Mac sweep on 08/11/2026 over ONE solo recording (Apple transcript segments, 20 segments,
    // 49 windows) and ONE public two-speaker clip: average linkage at 0.7 gave the solo voice 2 clusters (1 of 20 segments
    // off its majority) and the clip 2; the desktop's complete linkage at 0.5 gave the solo voice 14 clusters. Ceiling: not
    // tuned on any real two-person meeting. Trigger to retire: measured meetings on the device; change them here only.
    static let current = SpeakerParameters(windowSeconds: 2.0, hopSeconds: 1.0, minWindowSeconds: 1.5,
                                           linkage: .average, threshold: 0.7, segmentMean: true,
                                           nearestTurnSeconds: nil)
    // nearestTurnSeconds nil is a phone deviation (coordinator, 08/11/2026, after the first iPad run left "Okay.", "How
    // does that sound?" and "Hmm." unlabelled): every timed line takes the nearest turn's speaker, however far.
}

/// src/Dictum.Core/Meetings/Speakers/SpeakerSeparator.cs, without the owner's voice print (the phone has none), with
/// the values from `SpeakerParameters`.
enum SpeakerSeparation {
    /// SpeakerSeparator.MaxWindows: above it the windows are sub-sampled evenly.
    static let maxWindows = 7200
    static let rate = 16_000

    /// The sample ranges [from, to) of one segment's windows, within the track (SpeakerSeparator.Windows).
    static func windows(start: Double, end: Double, trackLength: Int,
                        parameters: SpeakerParameters = .desktop) -> [(from: Int, to: Int)] {
        // Math.Round in C# rounds half to even; Math.Clamp then (int).
        let from = Int(min(max((start * Double(rate)).rounded(.toNearestOrEven), 0), Double(trackLength)))
        let to = Int(min(max((end * Double(rate)).rounded(.toNearestOrEven), Double(from)), Double(trackLength)))
        let window = Int(parameters.windowSeconds * Double(rate)), hop = Int(parameters.hopSeconds * Double(rate))
        let floor = Int(parameters.minWindowSeconds * Double(rate))
        if to - from < floor { return [] }
        if to - from <= window { return [(from, to)] }
        var out: [(from: Int, to: Int)] = []
        var last = from
        var at = from
        while at + window <= to {
            last = at
            out.append((at, at + window))
            at += hop
        }
        if last + window < to { out.append((to - window, to)) } // the tail, so the segment's last words are heard
        return out
    }

    struct Outcome: Sendable {
        var turns: [SpeakerTurn]
        var windowsCut: Int
        var windowsEmbedded: Int
        var embedSeconds: Double
        var clusterSeconds: Double
        /// Vectors clustered: windows, or segments in segment-mean mode.
        var clustered: Int
        var diagnostics: SpeakerClustering.Diagnostics
    }

    /// SpeakerSeparator.Separate with no owner print: windows of each segment, one voice print per window, clustering of
    /// all windows, the majority speaker of each segment's windows (a tie goes to the speaker of the earliest of the tied
    /// windows), speakers numbered by first appearance. `segments` are seconds on the PCM's timeline, in time order.
    /// With `parameters.segmentMean`, the clustered vectors are the segments' mean voice prints instead of the windows.
    static func separate(samples: [Int16], segments: [(start: Double, end: Double)], embedder: VoiceEmbedder,
                         parameters: SpeakerParameters, progress: (Double) -> Void) throws -> Outcome {
        var windows: [(segment: Int, from: Int, to: Int)] = []
        for (i, s) in segments.enumerated() {
            for w in Self.windows(start: s.start, end: s.end, trackLength: samples.count, parameters: parameters) {
                windows.append((i, w.from, w.to))
            }
        }
        let windowsCut = windows.count
        var skipped = Set<Int>() // segments with a window the cap left out
        if windows.count > maxWindows { // even sub-sampling, in time order, before the costly embedding and clustering
            let all = windows
            let keptIndices = (0..<maxWindows).map { Int(Int64($0) * Int64(all.count) / Int64(maxWindows)) }
            let kept = Set(keptIndices)
            for k in 0..<all.count where !kept.contains(k) { skipped.insert(all[k].segment) }
            windows = keptIndices.map { all[$0] }
        }

        let clock = ContinuousClock()
        let embedBegan = clock.now
        var owners: [Int] = []
        var centres: [Double] = []
        var embeddings: [[Float]] = []
        for (k, w) in windows.enumerated() {
            try Task.checkCancellation()
            let slice = samples[w.from..<w.to].map { Float($0) / 32768 } // StoreWav.ToSamples
            // A non-finite value would poison every distance, so that window is dropped like a too-short one.
            if let embedding = embedder.embed(slice), embedding.allSatisfy(\.isFinite) {
                owners.append(w.segment)
                centres.append(Double(w.from + w.to) / 2.0 / Double(rate))
                embeddings.append(embedding)
            }
            progress(Double(k + 1) / Double(windows.count))
        }
        let embedSeconds = seconds(clock.now - embedBegan)

        let clusterBegan = clock.now
        let labels: [Int]
        let clustered: Int
        var diagnostics = SpeakerClustering.Diagnostics()
        if parameters.segmentMean {
            // One vector per segment that has a voice print: the mean of its unit windows, made unit again.
            var sums: [Int: [Double]] = [:]
            var order: [Int] = []
            for (k, segment) in owners.enumerated() {
                let unit = unitVector(embeddings[k].map(Double.init))
                if let sum = sums[segment] {
                    sums[segment] = zip(sum, unit).map { $0 + $1 }
                } else {
                    sums[segment] = unit
                    order.append(segment)
                }
            }
            let means = order.map { unitVector(sums[$0]!).map { Float($0) } }
            let result = try SpeakerClustering.clusterWithDiagnostics(means, threshold: parameters.threshold,
                                                                      linkage: parameters.linkage)
            let segmentLabels = result.labels
            diagnostics = result.diagnostics
            var labelOf: [Int: Int] = [:]
            for (n, segment) in order.enumerated() { labelOf[segment] = segmentLabels[n] }
            labels = owners.map { labelOf[$0]! }
            clustered = means.count
        } else {
            let result = try SpeakerClustering.clusterWithDiagnostics(embeddings, threshold: parameters.threshold,
                                                                      linkage: parameters.linkage)
            labels = result.labels
            diagnostics = result.diagnostics
            clustered = embeddings.count
        }
        let clusterSeconds = seconds(clock.now - clusterBegan)

        var turns: [(start: Double, end: Double, label: Int)] = []
        for i in segments.indices {
            var heard = owners.indices.filter { owners[$0] == i }.map { labels[$0] }
            if heard.isEmpty, skipped.contains(i), !centres.isEmpty {
                // Its windows were not kept: the speaker of the kept window nearest in time (MinBy: the first of equals).
                let middle = (segments[i].start + segments[i].end) / 2
                var best = 0
                for k in centres.indices where abs(centres[k] - middle) < abs(centres[best] - middle) { best = k }
                heard.append(labels[best])
            }
            if heard.isEmpty { continue }
            // GroupBy keeps first-appearance order, and only a strictly larger count replaces the best, so a tie goes to
            // the label of the earliest tied window.
            var order: [Int] = []
            var counts: [Int: Int] = [:]
            for label in heard {
                if counts[label] == nil { order.append(label) }
                counts[label, default: 0] += 1
            }
            var majority = order[0]
            for label in order.dropFirst() where counts[label]! > counts[majority]! { majority = label }
            turns.append((segments[i].start, segments[i].end, majority))
        }
        var numbers: [Int: Int] = [:]
        for turn in turns where numbers[turn.label] == nil { numbers[turn.label] = numbers.count + 1 }
        return Outcome(turns: turns.map { SpeakerTurn(start: round3($0.start), end: round3($0.end), speaker: numbers[$0.label]!) },
                       windowsCut: windowsCut, windowsEmbedded: embeddings.count,
                       embedSeconds: embedSeconds, clusterSeconds: clusterSeconds, clustered: clustered,
                       diagnostics: diagnostics)
    }

    /// v / |v|, or v unchanged when it has no length.
    static func unitVector(_ v: [Double]) -> [Double] {
        let length = v.reduce(0) { $0 + $1 * $1 }.squareRoot()
        return length == 0 ? v : v.map { $0 / length }
    }

    /// MeetingSpeakers.SpeakerOf: the turn a segment overlaps most, else the nearest turn within `within` seconds (the
    /// desktop's 1.0; nil for any distance); nil when the segment has no length or no turn is near. MaxBy and MinBy keep
    /// the first of equals.
    static func speakerOf(start: Double, end: Double, turns: [SpeakerTurn], within: Double? = 1.0) -> Int? {
        if end <= start || turns.isEmpty { return nil }
        let end = max(end, start)
        var best = (speaker: turns[0].speaker, overlap: min(end, turns[0].end) - max(start, turns[0].start))
        for t in turns.dropFirst() {
            let overlap = min(end, t.end) - max(start, t.start)
            if overlap > best.overlap { best = (t.speaker, overlap) }
        }
        if best.overlap > 0 { return best.speaker }
        func gap(_ t: SpeakerTurn) -> Double { t.end < start ? start - t.end : t.start - end }
        var nearest = (speaker: turns[0].speaker, gap: gap(turns[0]))
        for t in turns.dropFirst() where gap(t) < nearest.gap { nearest = (t.speaker, gap(t)) }
        return within.map { nearest.gap <= $0 } ?? true ? nearest.speaker : nil
    }

    /// mm:ss from the recording start, whole seconds rounded down; minutes go past 59 rather than adding hours.
    static func clock(_ seconds: Double) -> String {
        let whole = Swift.max(0, Int(seconds.rounded(.down)))
        return String(format: "%02d:%02d", whole / 60, whole % 60)
    }

    /// Math.Round(x, 3) (half to even).
    static func round3(_ x: Double) -> Double { (x * 1000).rounded(.toNearestOrEven) / 1000 }

    static func seconds(_ duration: Duration) -> Double {
        let parts = duration.components
        return Double(parts.seconds) + Double(parts.attoseconds) / 1e18
    }
}

/// src/Dictum.Core/Meetings/Speakers/SpeakerClustering.cs, ported line for line: agglomerative complete-linkage
/// clustering on cosine distance (rows normalised, distance 1 − cosine floored at 0), applying every merge whose height
/// is below the threshold, or merging down to a fixed count. `.average` (UPGMA) is an addition for the phone beta, not
/// in the desktop: the merged cluster's distance to another is the size-weighted mean of its halves' distances.
enum SpeakerClustering {
    /// A cluster index per embedding, numbered in order of first appearance (the first embedding's cluster is 0). Each
    /// step merges the closest two clusters; of equally close pairs, the one whose first cluster, then second, starts
    /// earliest.
    // simplification: as on the desktop, a row whose nearest cluster merged is searched in full again. Ceiling: the worst
    // case is n cubed, and the triangle grows with the square (an hour of speech, about 3,600 windows, is about 26 MB).
    // Trigger to retire: a clustering step that takes more than a few seconds on a device; then port hclust-cpp's
    // nearest-neighbour chain (on both platforms).
    static func cluster(_ embeddings: [[Float]], threshold: Double, count: Int = 0,
                        linkage: SpeakerParameters.Linkage = .complete) throws -> [Int] {
        try clusterWithDiagnostics(embeddings, threshold: threshold, count: count, linkage: linkage).labels
    }

    /// Calibration numbers for the facts box, all cosine distances (1 − cosine, floored at 0).
    struct Diagnostics: Equatable, Sendable {
        /// Percentiles of the distances between every pair of input vectors (nearest rank), before any merge.
        var p50: Double?
        var p90: Double?
        var max: Double?
        /// The linkage distance between the closest two clusters left when merging stopped; nil with one cluster.
        var smallestBetweenClusters: Double?
        /// The highest merge that was applied; nil when nothing merged.
        var largestMerge: Double?
    }

    static func clusterWithDiagnostics(_ embeddings: [[Float]], threshold: Double, count: Int = 0,
                                       linkage: SpeakerParameters.Linkage = .complete) throws
        -> (labels: [Int], diagnostics: Diagnostics) {
        let n = embeddings.count
        if n == 0 { return ([], Diagnostics()) }
        // The unit rows as doubles (exact), so each dot product is summed in double precision as on the desktop.
        let unit = embeddings.map { normalised($0).map(Double.init) }
        var distance = [Float](repeating: 0, count: n * (n - 1) / 2)
        func at(_ i: Int, _ j: Int) -> Int { i < j ? i * (2 * n - i - 1) / 2 + (j - i - 1) : at(j, i) }
        for i in 0..<n {
            try Task.checkCancellation()
            for j in (i + 1)..<max(i + 1, n) {
                distance[at(i, j)] = Float(netMax(0, 1 - dot(unit[i], unit[j])))
            }
        }

        var diagnostics = Diagnostics()
        if !distance.isEmpty {
            // simplification: the whole triangle is copied and sorted once (window mode at an hour: about 26 MB and a
            // few seconds). Ceiling: memory and time with many windows. Trigger to retire: segment mean made the
            // default for good; then compute these only for segment vectors.
            let sorted = distance.sorted()
            func rank(_ p: Double) -> Double { Double(sorted[Swift.max(Int((p * Double(sorted.count)).rounded(.up)) - 1, 0)]) }
            (diagnostics.p50, diagnostics.p90, diagnostics.max) = (rank(0.5), rank(0.9), Double(sorted[sorted.count - 1]))
        }

        var active = [Bool](repeating: true, count: n)
        var members = (0..<n).map { [$0] }
        var nearest = [Int](repeating: -1, count: n)
        var nearestDistance = [Float](repeating: .infinity, count: n)
        func search(_ i: Int) { // the nearest active cluster after i, the earliest of equals
            nearest[i] = -1
            nearestDistance[i] = .infinity
            for j in (i + 1)..<max(i + 1, n) where active[j] && distance[at(i, j)] < nearestDistance[i] {
                nearest[i] = j
                nearestDistance[i] = distance[at(i, j)]
            }
        }
        for i in 0..<n { search(i) }

        var left = n
        while left > 1 && (count <= 0 || left > count) {
            try Task.checkCancellation()
            var a = -1
            for i in 0..<n where active[i] && nearest[i] >= 0 && (a < 0 || nearestDistance[i] < nearestDistance[a]) { a = i }
            if a < 0 { break } // no finite distance left (a non-finite embedding): stop merging rather than index -1
            let b = nearest[a]
            let height = Double(nearestDistance[a])
            if count <= 0 && height >= threshold { break } // cutree_cdist: only merges below the threshold
            diagnostics.largestMerge = Swift.max(diagnostics.largestMerge ?? height, height)
            active[b] = false
            let (sizeA, sizeB) = (Double(members[a].count), Double(members[b].count))
            members[a].append(contentsOf: members[b])
            for k in 0..<n where active[k] && k != a {
                switch linkage {
                case .complete:
                    distance[at(a, k)] = netMax(distance[at(a, k)], distance[at(b, k)]) // complete linkage
                case .average:
                    // UPGMA: the mean over every member pair, from the two halves' means weighted by their sizes.
                    distance[at(a, k)] = Float((sizeA * Double(distance[at(a, k)]) + sizeB * Double(distance[at(b, k)]))
                                               / (sizeA + sizeB))
                }
            }
            for i in 0..<a where active[i] {
                if nearest[i] == a || nearest[i] == b {
                    search(i)
                } else if distance[at(i, a)] == nearestDistance[i] && a < nearest[i] {
                    nearest[i] = a // now an equal, earlier pair
                }
                // No shorter-distance case: both linkages are reducible (a merged distance is never below the smaller of
                // its halves' distances, and both were at least i's nearest), so a row's nearest never gets closer.
            }
            for i in a..<b where active[i] && (i == a || nearest[i] == b) { search(i) }
            left -= 1
        }

        for i in 0..<n where active[i] && nearest[i] >= 0 {
            let d = Double(nearestDistance[i])
            diagnostics.smallestBetweenClusters = Swift.min(diagnostics.smallestBetweenClusters ?? d, d)
        }

        var labels = [Int](repeating: 0, count: n)
        var label = 0
        for i in 0..<n where active[i] { // a cluster is kept at its earliest member, so this is first-appearance order
            for member in members[i] { labels[member] = label }
            label += 1
        }
        return (labels, diagnostics)
    }

    private static func normalised(_ v: [Float]) -> [Float] {
        let length = v.reduce(0.0) { $0 + Double($1) * Double($1) }.squareRoot()
        return length == 0 ? v : v.map { Float(Double($0) / length) }
    }

    // simplification: vDSP sums the products in its own order, not strictly left to right as the desktop loop does, so
    // a distance can differ from the desktop's in the last bits before it is rounded to a float. Ceiling: two windows
    // exactly tied on one platform may not tie on the other. Trigger to retire: a cross-platform comparison on the same
    // voice prints that disagrees; then sum in order (the plain loop is about 140 times slower in a Debug build).
    private static func dot(_ a: [Double], _ b: [Double]) -> Double {
        precondition(a.count == b.count, "Embeddings differ in length.")
        var sum = 0.0
        vDSP_dotprD(a, 1, b, 1, &sum, vDSP_Length(a.count))
        return sum
    }

    /// .NET's Math.Max: NaN if either is NaN (Swift's max would return the other value).
    private static func netMax<T: BinaryFloatingPoint>(_ a: T, _ b: T) -> T {
        a.isNaN || b.isNaN ? .nan : Swift.max(a, b)
    }
}

// MARK: - Screen state

/// The speaker step after Stop. Main thread only.
@MainActor
@Observable
final class SpeakerLabels {
    enum State: Equatable {
        case idle
        /// Embedding and clustering on a background task; the fraction of windows done.
        case working(Double)
        /// The sherpa-onnx library or the speaker model is not in this build.
        case unavailable
        /// No window survived: every segment too short, or no words.
        case tooLittleSpeech
        case done
        case failed(String)
    }

    /// Where the segments' times came from.
    enum TimeSource: Equatable {
        case wordRanges
        case resultRanges
        /// No audio time range at all: one segment for the whole recording.
        case wholeRecording
    }

    /// One line on screen 3: "00:12  Other n: …"; no label when no turn is near or the segment has no time, and no
    /// time when it has no time range.
    struct Line: Equatable, Identifiable {
        let id: Int
        /// Seconds from the recording start (the PCM copy's first sample).
        let start: Double?
        let speaker: Int?
        let text: String
    }

    struct Facts: Equatable {
        var segments: Int
        var timeSource: TimeSource
        var pcmBytes: Int
        var capHit: Bool
        var parameters: SpeakerParameters
        var windowsCut: Int?
        var windowsEmbedded: Int?
        var embedSeconds: Double?
        /// Embedding seconds per minute of audio kept in memory.
        var embedSecondsPerAudioMinute: Double?
        var clusterSeconds: Double?
        /// Vectors clustered: windows, or segments in segment-mean mode.
        var clustered: Int?
        var diagnostics: SpeakerClustering.Diagnostics?
        var speakersFound: Int?
    }

    /// A segment of final words on the PCM's timeline, or with no time.
    struct Segment: Sendable {
        var text: String
        var start: Double?
        var end: Double?
    }

    private(set) var state: State = .idle
    private(set) var lines: [Line] = []
    private(set) var facts: Facts?

    private var store: PcmStore?
    private var task: Task<Void, Never>?
    private var generation = 0

    /// Before capture starts, with the switch on: a fresh in-memory store.
    func makeStore() -> PcmStore {
        clear()
        let store = PcmStore()
        self.store = store
        return store
    }

    /// After the analyzer has finished: label the segments. `timeSource` says how their times were found; with
    /// `.wholeRecording` the one segment's times are filled in here from the PCM length.
    func run(segments: [Segment], timeSource: TimeSource) {
        guard let store else { return }
        self.store = nil
        let (samples, analyzerStart, capHit) = store.take()
        let parameters = SpeakerParameters.current
        let audioMinutes = Double(samples.count) / Double(SpeakerSeparation.rate) / 60
        // The analyzer's timeline starts at `analyzerStart` samples into the PCM.
        let offset = Double(analyzerStart ?? 0) / Double(SpeakerSeparation.rate)
        var placed = segments.map { s -> Segment in
            var s = s
            s.start = s.start.map { $0 + offset }
            s.end = s.end.map { $0 + offset }
            return s
        }
        if timeSource == .wholeRecording, !placed.isEmpty {
            let text = placed.map(\.text).joined(separator: " ")
            placed = [Segment(text: text, start: 0, end: Double(samples.count) / Double(SpeakerSeparation.rate))]
        }
        let known = Facts(segments: placed.count, timeSource: timeSource, pcmBytes: samples.count * MemoryLayout<Int16>.size,
                          capHit: capHit, parameters: parameters)
        self.facts = known
        let segmentsShown = placed
        // SpeakerSeparator.Turns: segments with a length, in time order (a stable sort, as LINQ's OrderBy is).
        let turnsIn = segmentsShown.enumerated()
            .compactMap { i, s -> (index: Int, start: Double, end: Double)? in
                guard let start = s.start, let end = s.end, end > start else { return nil }
                return (i, start, end)
            }
            .sorted { $0.start != $1.start ? $0.start < $1.start : $0.index < $1.index }
            .map { (start: $0.start, end: $0.end) }

        generation += 1
        let mine = generation
        state = .working(0)
        task = Task.detached(priority: .userInitiated) { [weak self] in
            let embedder = VoiceEmbedders.make()
            guard embedder.dim > 0 else {
                await self?.finish(mine) { $0.state = .unavailable }
                return
            }
            var facts = known
            do {
                var lastReported = -1
                let outcome = try SpeakerSeparation.separate(samples: samples, segments: turnsIn, embedder: embedder,
                                                             parameters: parameters) { fraction in
                    let percent = Int(fraction * 100)
                    guard percent != lastReported else { return }
                    lastReported = percent
                    Task { @MainActor in
                        guard let self, self.generation == mine, case .working = self.state else { return }
                        self.state = .working(fraction)
                    }
                }
                let lines = segmentsShown.enumerated().map { i, s in
                    Line(id: i, start: s.start, speaker: s.start.flatMap { start in
                        s.end.flatMap {
                            SpeakerSeparation.speakerOf(start: start, end: $0, turns: outcome.turns,
                                                        within: parameters.nearestTurnSeconds)
                        }
                    }, text: s.text)
                }
                facts.windowsCut = outcome.windowsCut
                facts.windowsEmbedded = outcome.windowsEmbedded
                facts.embedSeconds = outcome.embedSeconds
                facts.embedSecondsPerAudioMinute = audioMinutes > 0 ? outcome.embedSeconds / audioMinutes : nil
                facts.clusterSeconds = outcome.clusterSeconds
                facts.clustered = outcome.clustered
                facts.diagnostics = outcome.diagnostics
                facts.speakersFound = outcome.turns.map(\.speaker).max() ?? 0
                let final = facts
                await self?.finish(mine) {
                    $0.facts = final
                    $0.lines = lines
                    $0.state = outcome.turns.isEmpty ? .tooLittleSpeech : .done
                }
            } catch is CancellationError {
            } catch {
                let reason = error.localizedDescription
                await self?.finish(mine) { $0.state = .failed(reason) }
            }
        }
    }

    private func finish(_ run: Int, _ apply: @MainActor (SpeakerLabels) -> Void) {
        guard generation == run else { return }
        apply(self)
        task = nil
    }

    /// Done, or a start that failed: drop the sound, the voice prints (they live only inside the task) and the labels.
    func clear() {
        generation += 1
        task?.cancel()
        task = nil
        store?.clear()
        store = nil
        state = .idle
        lines = []
        facts = nil
    }
}

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
