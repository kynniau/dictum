import AVFoundation
import Foundation
import Speech

// "Transcribe on this device" for a kept meeting that has audio but no transcript. The same pipeline as the live
// transcript: Apple's SpeechAnalyzer with SpeechTranscriber (SpeechEngine), fed from memory instead of the microphone,
// then the same speaker step (SpeakerLabels). File-less: the PCM is decrypted into memory and handed over in buffers;
// nothing is written. Never downloads: the speech model must already be on the device.

@available(iOS 26, *)
@MainActor
enum LibraryTranscriber {
    enum Failure: Error {
        case unavailable
        case needsModel
        case noWords
        case timedOut
    }

    struct Outcome {
        var segments: [MeetingRecord.Segment]
        var locale: String
        var speakersFound: Int?
        var threshold: Double?
        /// Each speaker's group centre, for "Your voice" (memory only; never saved).
        var centres: [Int: [Float]] = [:]
        /// How many "Your words" fixes were made in the lines.
        var wordFixes = 0
    }

    static func transcribe(pcm: Data, labels: Bool, threshold: Double) async throws -> Outcome {
        guard SpeechTranscriber.isAvailable, await OnDeviceTranscript.speechAuthorised(),
              let locale = await SpeechEngine.pickLocale() else { throw Failure.unavailable }
        guard await SpeechEngine.assetStatus(locale) == .installed else { throw Failure.needsModel }

        let engine = SpeechEngine(locale: locale)
        let collector = Task { () -> [(text: String, span: TranscriptSpan?)] in
            var finals: [(text: String, span: TranscriptSpan?)] = []
            for try await result in engine.transcriber.results where result.isFinal {
                let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
                if !text.isEmpty { finals.append((text, SpeechEngine.span(result))) }
            }
            return finals
        }
        let starter = Task { try await engine.start() }
        // start() learns the analyzer's format before it starts analysing; feed() drops buffers until then.
        var waited = 0
        while !engine.isReady {
            if waited >= 400 { // 20 s
                starter.cancel()
                collector.cancel()
                await engine.cancel()
                throw Failure.timedOut
            }
            try await Task.sleep(for: .milliseconds(50))
            waited += 1
        }

        // simplification: the whole recording (capped at 60 minutes by the caller) is handed to the analyzer as fast as
        // it is converted, and SpeechEngine buffers without a bound. Ceiling: the converted copy (up to about 230 MB of
        // 16 kHz float for an hour) sits in memory while the analyzer catches up. Retire with a bounded hand-over once a
        // device test measures an hour-long meeting.
        try await Task.detached(priority: .userInitiated) {
            let format = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!
            let frames = 16_000
            var offset = 0
            var fed = 0
            while offset < pcm.count {
                try Task.checkCancellation()
                let length = min(frames * 2, pcm.count - offset) & ~1
                guard length > 0 else { break }
                guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(length / 2)) else { break }
                buffer.frameLength = AVAudioFrameCount(length / 2)
                pcm.withUnsafeBytes { raw in
                    let source = raw.baseAddress!.advanced(by: offset)
                    buffer.int16ChannelData![0].withMemoryRebound(to: UInt8.self, capacity: length) { target in
                        target.update(from: source.assumingMemoryBound(to: UInt8.self), count: length)
                    }
                }
                engine.feed(buffer)
                offset += length
                fed += 1
                if fed % 30 == 0 { await Task.yield() }
            }
        }.value
        await engine.finish()
        _ = try? await starter.value
        var finals = try await collector.value
        guard !finals.isEmpty else { throw Failure.noWords }
        // "Your words": the kept fixes are made in every final line before anything else reads the words.
        let fixes = await YourWordsStore.current()
        var wordFixes = 0
        for at in finals.indices {
            let fixed = YourWords.apply(finals[at].text, fixes)
            finals[at].text = fixed.text
            wordFixes += fixed.changed
        }

        let wordSpans = finals.filter { $0.span?.source == .words }.count
        let resultSpans = finals.filter { $0.span?.source == .result }.count
        let source: SpeakerLabels.TimeSource = wordSpans > 0 ? .wordRanges : resultSpans > 0 ? .resultRanges : .wholeRecording
        let segments = finals.map { SpeakerLabels.Segment(text: $0.text, start: $0.span?.start, end: $0.span?.end) }

        guard labels else {
            return Outcome(segments: segments.map { .init(start: $0.start, end: $0.end, text: $0.text, speaker: nil) },
                           locale: locale.identifier, speakersFound: nil, threshold: nil, wordFixes: wordFixes)
        }
        // The speaker step, exactly as after a live recording: the PCM's first sample is the analyzer's time 0 here.
        let speakers = SpeakerLabels()
        let store = speakers.makeStore()
        store.append(pcm)
        store.markAnalyzerStart()
        speakers.run(segments: segments, timeSource: source, threshold: threshold)
        while true {
            switch speakers.state {
            case .working, .idle: try await Task.sleep(for: .milliseconds(200))
            default:
                let done = speakers.state == .done
                let lines = done ? speakers.lines.map {
                    MeetingRecord.Segment(start: $0.start, end: $0.end, text: $0.text, speaker: $0.speaker)
                } : segments.map { MeetingRecord.Segment(start: $0.start, end: $0.end, text: $0.text, speaker: nil) }
                let found = done ? speakers.facts?.speakersFound : nil
                let used = done ? speakers.threshold : nil
                let centres = done ? speakers.groupCentres : [:]
                speakers.clear()
                return Outcome(segments: lines, locale: locale.identifier, speakersFound: found, threshold: used,
                               centres: centres, wordFixes: wordFixes)
            }
        }
    }
}
