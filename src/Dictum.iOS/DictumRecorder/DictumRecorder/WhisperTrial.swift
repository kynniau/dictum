import Foundation
#if DEBUG && canImport(SherpaOnnxC)
import SherpaOnnxC
#endif

// Test builds only: transcribe a kept meeting again with Whisper (the "turbo" model, int8, through sherpa-onnx), to measure
// its speed, memory use and words on this phone. The model is never downloaded and never in the app: the three files are
// copied by hand into <container>/Library/Application Support/WhisperTrial/. The PCM is handed over in memory; nothing is
// written and nothing goes over the network. A Release build contains none of this file.

#if DEBUG
enum WhisperTrial {
    static let rate = 16_000
    static let folderName = "WhisperTrial"
    static let encoderName = "turbo-encoder.int8.onnx"
    static let decoderName = "turbo-decoder.int8.onnx"
    static let tokensName = "turbo-tokens.txt"

    enum Failure: Error {
        /// One or more of the three model files is not in the folder.
        case modelMissing
        /// sherpa-onnx could not make a recogniser from the files.
        case recogniserNotCreated
        /// This build does not link sherpa-onnx.
        case libraryMissing
    }

    /// One transcript line, in seconds from the start of the recording.
    struct Line: Equatable {
        var start: Double
        var end: Double
        var text: String
    }

    /// One segment as the model returned it, in seconds from the start of its piece.
    struct PieceSegment {
        var start: Double
        var duration: Double
        var text: String
    }

    /// What the run measured.
    struct Report {
        /// The whole Whisper pass, model load included.
        var seconds: Double
        var loadSeconds: Double
        var audioSeconds: Double
        var pieces: Int
        /// Largest `phys_footprint` seen (before, after the load, after each piece); nil when it could not be read.
        var peakBytes: UInt64?
        var thermal: ProcessInfo.ThermalState
    }

    struct Run {
        var lines: [Line]
        var report: Report
    }

    static var folder: URL? {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first?
            .appendingPathComponent(folderName, isDirectory: true)
    }

    /// True when all three model files are in the folder. When the folder exists, it is also kept out of backups.
    static var isReady: Bool {
        guard let folder else { return false }
        let fm = FileManager.default
        guard fm.fileExists(atPath: folder.path) else { return false }
        excludeFromBackup(folder)
        return [encoderName, decoderName, tokensName].allSatisfy {
            fm.fileExists(atPath: folder.appendingPathComponent($0).path)
        }
    }

    /// Sets isExcludedFromBackup on the folder once (reading the flag first, so a screen redraw costs one read).
    static func excludeFromBackup(_ folder: URL) {
        var url = folder
        if (try? url.resourceValues(forKeys: [.isExcludedFromBackupKey]))?.isExcludedFromBackup == true { return }
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? url.setResourceValues(values)
    }

    // MARK: Pure part (Foundation only; checked by WhisperTrialSelfCheck)

    // simplification: Whisper takes at most 30 s, so the audio is cut into pieces of 15 to 25 s at the quietest 0.3 s in
    // that span (a port of the Mac tool's `cuts`). Ceiling: a cut can still land mid-word in continuous speech, and a
    // word cut in two is lost or doubled. Retire with a real voice-activity cut (sherpa-onnx's Silero VAD) if Whisper
    // is taken further than this test.
    /// Contiguous pieces covering every sample once. Every piece but the last is 15 to 25 s; the last is the remainder.
    static func cuts(_ samples: UnsafeBufferPointer<Int16>) -> [Range<Int>] {
        let n = samples.count
        guard n > 0 else { return [] }
        let window = 3 * rate / 10 // 0.3 s
        let step = rate / 10 // 0.1 s
        var out: [Range<Int>] = []
        var from = 0
        while n - from > 25 * rate {
            var best = from + 25 * rate
            var bestEnergy = Int64.max
            var at = from + 15 * rate
            while at + window <= from + 25 * rate {
                var energy: Int64 = 0
                for i in at..<(at + window) {
                    let s = Int64(samples[i])
                    energy += s * s
                }
                if energy < bestEnergy {
                    bestEnergy = energy
                    best = at + window / 2
                }
                at += step
            }
            out.append(from..<best)
            from = best
        }
        out.append(from..<n)
        return out
    }

    /// One piece's result as transcript lines. With segments (count > 0, every time finite), one line per non-empty
    /// segment, placed from the piece's start and kept inside the piece; otherwise, or when no segment has words, one
    /// line for the whole piece. Lines with no words are dropped.
    static func lines(pieceStart: Double, pieceEnd: Double, text: String, segments: [PieceSegment]) -> [Line] {
        let usable = !segments.isEmpty && segments.allSatisfy { $0.start.isFinite && $0.duration.isFinite }
        if usable {
            var out: [Line] = []
            for segment in segments {
                let words = segment.text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !words.isEmpty else { continue }
                let start = min(max(pieceStart + segment.start, pieceStart), pieceEnd)
                let end = min(max(start + segment.duration, start), pieceEnd)
                out.append(Line(start: start, end: end, text: words))
            }
            if !out.isEmpty { return out }
        }
        let words = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return words.isEmpty ? [] : [Line(start: pieceStart, end: pieceEnd, text: words)]
    }
}

/// One line per check, "PASS …" or "FAIL …". No device or model needed.
enum WhisperTrialSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }
        let rate = WhisperTrial.rate
        func cuts(_ samples: [Int16]) -> [Range<Int>] { samples.withUnsafeBufferPointer { WhisperTrial.cuts($0) } }
        /// Loud everywhere (a square wave) except a quiet stretch.
        func loud(seconds: Double, quiet: [Range<Int>] = []) -> [Int16] {
            var s = (0..<Int(seconds * Double(rate))).map { Int16($0 % 2 == 0 ? 8_000 : -8_000) }
            for range in quiet { for i in range where i < s.count { s[i] = 0 } }
            return s
        }
        func covers(_ pieces: [Range<Int>], _ n: Int) -> Bool {
            guard let first = pieces.first, let last = pieces.last, first.lowerBound == 0, last.upperBound == n else { return false }
            return zip(pieces, pieces.dropFirst()).allSatisfy { $0.upperBound == $1.lowerBound } && pieces.allSatisfy { !$0.isEmpty }
        }

        // Cuts.
        check("cuts: empty audio has no pieces", cuts([]), [])
        check("cuts: short audio is one piece", cuts(loud(seconds: 12)), [0..<(12 * rate)])
        check("cuts: exactly 25 s is one piece", cuts(loud(seconds: 25)), [0..<(25 * rate)])
        check("cuts: one sample is one piece", cuts([5]), [0..<1])
        for seconds in [25.5, 61.0, 187.3, 600.0] {
            let n = Int(seconds * Double(rate))
            let pieces = cuts(loud(seconds: seconds))
            check("cuts: \(seconds) s covered once, contiguous", covers(pieces, n), true)
            check("cuts: \(seconds) s pieces before the last are 15 to 25 s",
                  pieces.dropLast().allSatisfy { $0.count >= 15 * rate && $0.count <= 25 * rate }, true)
            check("cuts: \(seconds) s last piece at most 25 s", (pieces.last?.count ?? 0) <= 25 * rate, true)
            check("cuts: \(seconds) s sample total", pieces.reduce(0) { $0 + $1.count }, n)
        }
        // A planted quiet 0.3 s from 18.0 s (on the 0.1 s grid from 15 s): the cut is its middle, 18.15 s.
        let window = 3 * rate / 10
        let firstQuiet = 18 * rate
        let planted = cuts(loud(seconds: 40, quiet: [firstQuiet..<(firstQuiet + window)]))
        check("cuts: the cut lands in the planted quiet spot", planted.first?.upperBound, firstQuiet + window / 2)
        check("cuts: planted, two pieces", planted.count, 2)
        // A second quiet spot 20 s after the first cut (on the next piece's grid): the second cut is its middle.
        let secondQuiet = firstQuiet + window / 2 + 20 * rate
        let two = cuts(loud(seconds: 70, quiet: [firstQuiet..<(firstQuiet + window), secondQuiet..<(secondQuiet + window)]))
        check("cuts: the second cut lands in the second quiet spot", two.count > 1 ? two[1].upperBound : -1, secondQuiet + window / 2)
        // No quiet anywhere: every window is as loud, so the first window wins (15.15 s), as the Mac tool.
        check("cuts: even loudness cuts at the first window", cuts(loud(seconds: 40)).first?.upperBound, 15 * rate + 3 * rate / 20)

        // Lines.
        typealias Seg = WhisperTrial.PieceSegment
        typealias Line = WhisperTrial.Line
        check("lines: segments placed from the piece start",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "a b",
                                 segments: [Seg(start: 0.5, duration: 2, text: " a "), Seg(start: 4, duration: 3, text: "b")]),
              [Line(start: 30.5, end: 32.5, text: "a"), Line(start: 34, end: 37, text: "b")])
        check("lines: a segment running past the piece end is clamped",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "x", segments: [Seg(start: 18, duration: 9, text: "x")]),
              [Line(start: 48, end: 50, text: "x")])
        check("lines: a segment starting past the piece end is clamped to the end",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "x", segments: [Seg(start: 25, duration: 1, text: "x")]),
              [Line(start: 50, end: 50, text: "x")])
        check("lines: a negative start is clamped to the piece start",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "x", segments: [Seg(start: -2, duration: 1, text: "x")]),
              [Line(start: 30, end: 31, text: "x")])
        check("lines: a negative duration gives end = start",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "x", segments: [Seg(start: 5, duration: -3, text: "x")]),
              [Line(start: 35, end: 35, text: "x")])
        check("lines: no segments, one line for the whole piece",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: " whole piece ", segments: []),
              [Line(start: 30, end: 50, text: "whole piece")])
        check("lines: a non-finite time, one line for the whole piece",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "whole",
                                 segments: [Seg(start: 1, duration: 1, text: "a"), Seg(start: .nan, duration: 1, text: "b")]),
              [Line(start: 30, end: 50, text: "whole")])
        check("lines: an infinite duration, one line for the whole piece",
              WhisperTrial.lines(pieceStart: 30, pieceEnd: 50, text: "whole", segments: [Seg(start: 1, duration: .infinity, text: "a")]),
              [Line(start: 30, end: 50, text: "whole")])
        check("lines: empty segment text is dropped",
              WhisperTrial.lines(pieceStart: 0, pieceEnd: 20, text: "a",
                                 segments: [Seg(start: 1, duration: 1, text: "  \n"), Seg(start: 3, duration: 1, text: "a")]),
              [Line(start: 3, end: 4, text: "a")])
        check("lines: every segment empty falls back to the whole piece",
              WhisperTrial.lines(pieceStart: 0, pieceEnd: 20, text: "a", segments: [Seg(start: 1, duration: 1, text: " ")]),
              [Line(start: 0, end: 20, text: "a")])
        check("lines: whitespace-only whole text is dropped",
              WhisperTrial.lines(pieceStart: 0, pieceEnd: 20, text: " \t ", segments: []), [])
        check("lines: nothing at all is dropped",
              WhisperTrial.lines(pieceStart: 0, pieceEnd: 20, text: "", segments: [Seg(start: 1, duration: 1, text: "")]), [])
        return out
    }
}
#endif

// MARK: - The sherpa-onnx pass

#if DEBUG && canImport(SherpaOnnxC)
extension WhisperTrial {
    /// Transcribes 16 kHz mono Int16 PCM off the main thread. `progress(piece, of)` is called before each piece, on the
    /// background thread. Cancelling the calling task stops the run between pieces.
    static func transcribe(pcm: Data, progress: @escaping @Sendable (Int, Int) -> Void) async throws -> Run {
        guard isReady, let folder else { throw Failure.modelMissing }
        let worker = Task.detached(priority: .userInitiated) { () throws -> Run in
            try Self.run(pcm: pcm, folder: folder, progress: progress)
        }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
        }
    }

    // simplification: the whole model is loaded for each run and freed at the end, and the decode is greedy, 4 threads,
    // CPU only. Ceiling: each run pays the load (several seconds and the model's memory again). Retire by keeping one
    // recogniser for the app's life if Whisper is ever offered outside this test.
    private static func run(pcm: Data, folder: URL, progress: @Sendable (Int, Int) -> Void) throws -> Run {
        let began = Date()
        var peak = footprint()
        func sample() { if let now = footprint() { peak = max(peak ?? 0, now) } }

        // The config keeps C strings; sherpa-onnx copies them when it makes the recogniser. Every strdup is freed here.
        var owned: [UnsafeMutablePointer<CChar>] = []
        var allMade = true
        defer { owned.forEach { free($0) } }
        func c(_ s: String) -> UnsafePointer<CChar>? {
            guard let p = strdup(s) else {
                allMade = false
                return nil
            }
            owned.append(p)
            return UnsafePointer(p)
        }
        var config = SherpaOnnxOfflineRecognizerConfig() // zero-filled, as the Mac tool's memset
        config.feat_config.sample_rate = Int32(rate)
        config.feat_config.feature_dim = 80
        config.model_config.whisper.encoder = c(folder.appendingPathComponent(encoderName).path)
        config.model_config.whisper.decoder = c(folder.appendingPathComponent(decoderName).path)
        config.model_config.whisper.language = c("en")
        config.model_config.whisper.task = c("transcribe")
        config.model_config.whisper.tail_paddings = -1
        config.model_config.whisper.enable_segment_timestamps = 1
        config.model_config.tokens = c(folder.appendingPathComponent(tokensName).path)
        config.model_config.num_threads = 4
        config.model_config.provider = c("cpu")
        config.model_config.debug = 0
        config.decoding_method = c("greedy_search")
        guard allMade else { throw Failure.recogniserNotCreated }
        guard let recogniser = SherpaOnnxCreateOfflineRecognizer(&config) else { throw Failure.recogniserNotCreated }
        defer { SherpaOnnxDestroyOfflineRecognizer(recogniser) }
        let loadSeconds = Date().timeIntervalSince(began)
        sample()

        var lines: [Line] = []
        var pieceCount = 0
        try pcm.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let samples = raw.bindMemory(to: Int16.self)
            let pieces = cuts(samples)
            pieceCount = pieces.count
            var floats: [Float] = []
            floats.reserveCapacity(25 * rate)
            for (index, piece) in pieces.enumerated() {
                try Task.checkCancellation()
                progress(index + 1, pieces.count)
                floats.removeAll(keepingCapacity: true)
                for i in piece { floats.append(Float(samples[i]) / 32768) }
                guard let stream = SherpaOnnxCreateOfflineStream(recogniser) else { continue }
                defer { SherpaOnnxDestroyOfflineStream(stream) }
                floats.withUnsafeBufferPointer {
                    SherpaOnnxAcceptWaveformOffline(stream, Int32(rate), $0.baseAddress, Int32($0.count))
                }
                SherpaOnnxDecodeOfflineStream(recogniser, stream)
                if let result = SherpaOnnxGetOfflineStreamResult(stream) {
                    defer { SherpaOnnxDestroyOfflineRecognizerResult(result) }
                    lines += Self.lines(pieceStart: Double(piece.lowerBound) / Double(rate),
                                        pieceEnd: Double(piece.upperBound) / Double(rate),
                                        text: result.pointee.text.map { String(cString: $0) } ?? "",
                                        segments: segments(result.pointee))
                }
                sample()
            }
            floats.removeAll()
        }
        return Run(lines: lines, report: Report(seconds: Date().timeIntervalSince(began), loadSeconds: loadSeconds,
                                                audioSeconds: Double(pcm.count / 2) / Double(rate), pieces: pieceCount,
                                                peakBytes: peak, thermal: ProcessInfo.processInfo.thermalState))
    }

    /// The segment arrays, read while the result is alive; empty when the model returned none.
    private static func segments(_ r: SherpaOnnxOfflineRecognizerResult) -> [PieceSegment] {
        let count = Int(r.segment_count)
        guard count > 0, let starts = r.segment_timestamps, let durations = r.segment_durations,
              let texts = r.segment_texts_arr else { return [] }
        return (0..<count).map { i in
            PieceSegment(start: Double(starts[i]), duration: Double(durations[i]), text: texts[i].map { String(cString: $0) } ?? "")
        }
    }

    /// The process's memory footprint as the system counts it against the app (task_vm_info phys_footprint).
    static func footprint() -> UInt64? {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let status = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return status == KERN_SUCCESS ? info.phys_footprint : nil
    }
}
#elseif DEBUG
extension WhisperTrial {
    /// This build does not link sherpa-onnx.
    static func transcribe(pcm: Data, progress: @escaping @Sendable (Int, Int) -> Void) async throws -> Run {
        throw Failure.libraryMissing
    }
}
#endif
