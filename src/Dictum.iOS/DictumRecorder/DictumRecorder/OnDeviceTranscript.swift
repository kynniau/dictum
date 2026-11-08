import AVFoundation
import CoreMedia
import Network
import Observation
import os
import Speech

// Experimental on-device transcript, behind a switch on screen 1 that defaults to off.
//
// What this never does: write the transcript anywhere (no file, no bundle entry, no defaults, no Keychain), send it
// anywhere, or download anything while a recording runs. The speech model is downloaded only when the user taps the
// download button on screen 1, before Start. The text lives in this object's memory and is cleared on Done.
//
// With the switch off, `makeSink()` returns nil and `pcmSink` is nil, so `AudioCapture` gets no second sink, the seal
// writer's sink is not wrapped, and the recording path is the same as without this file.
//
// With it on, each final result also keeps its audio time range (min start, max end of its runs), and the 16 kHz PCM
// the seal writer gets is copied into memory for the experimental speaker step (SpeakerLabels.swift) after Stop.

/// Screen state for the experiment. Main thread only.
@MainActor
@Observable
final class OnDeviceTranscript {
    /// Setup on screen 1, after the switch is turned on.
    enum Setup: Equatable {
        case off
        case checking
        case needsAssets
        case downloading
        case ready
        case unavailable
        case failed(String)
    }

    /// The live status line on screen 2.
    enum Live: Equatable {
        case preparing
        case listening
        case unavailable
        case failed(String)
    }

    enum AssetOutcome: Equatable {
        /// The switch found the model already installed; no request was made.
        case preinstalled
        /// An installation request existed and completed in this many seconds (a network event to Apple).
        case installed(seconds: Double)
        /// The download was tapped but `assetInstallationRequest` returned nil; carries the status found before.
        case noRequest(statusBefore: String)
    }

    /// Measured values shown on screen 3.
    struct Facts: Equatable {
        var locale: String
        /// How the speech model came to be on the device for this recording.
        var assets: AssetOutcome
        /// The AssetInventory status the switch first found: supported, downloading, installed or unsupported.
        var assetStatusBefore: String
        /// Seconds from Stop to the last final result, or nil when no final result arrived after Stop.
        var stopToLastFinalSeconds: Double?
        var thermalAtStop: String
        /// True when no network path was ever satisfied between Start and Stop.
        var networkingOff: Bool
    }

    /// True once the device and OS are known to support it. The switch is not shown until then.
    private(set) var supported = false
    /// The switch on screen 1.
    private(set) var enabled = false
    private(set) var setup: Setup = .off
    /// Progress of the model download while `setup == .downloading`.
    private(set) var downloadProgress: Progress?

    /// True when the current or just-sealed recording ran with the switch on.
    private(set) var active = false
    private(set) var live: Live = .preparing
    private(set) var finalText = ""
    private(set) var volatileText = ""
    /// True between Stop and the analyzer finishing.
    private(set) var finishing = false
    private(set) var facts: Facts?
    /// Experimental speaker labels, made after Stop from `segments` and the in-memory PCM.
    let speakers = SpeakerLabels()
    /// The 16 kHz PCM copy for the speaker step: set by `makeSink()`, nil with the switch off.
    private(set) var pcmSink: ((Data) -> Void)?

    private var localeIdentifier: String?
    private var assetSeconds: Double?
    /// The status the switch first found, kept until Done so a later re-check after a download cannot overwrite it.
    private var assetStatusBefore: String?
    /// True only when `assetInstallationRequest` returned a request and `downloadAndInstall()` was called.
    private var assetRequestMade = false
    private var engine: AnyObject?
    private var resultsTask: Task<Void, Never>?
    private var lastFinalAt: ContinuousClock.Instant?
    /// Each final result's words with seconds on the analyzer's timeline. Memory only.
    private var segments: [SpeakerLabels.Segment] = []
    private var wordRangeSegments = 0
    private var resultRangeSegments = 0
    /// Watches the network path between Start and Stop, only with the switch on. It only observes the path; it makes
    /// no network requests.
    private var pathMonitor: NWPathMonitor?
    /// Set from the monitor's queue; read once at Stop.
    private var networkSeen = OSAllocatedUnfairLock(initialState: false)

    init() {
        Task { await checkSupport() }
    }

    /// Start is allowed with the switch off, or with it on once the model is installed.
    var canStart: Bool { !enabled || setup == .ready }

    // MARK: - Screen 1

    private func checkSupport() async {
        guard #available(iOS 26, *) else { return }
        guard SpeechTranscriber.isAvailable else { return }
        supported = await SpeechEngine.pickLocale() != nil
    }

    func setEnabled(_ on: Bool) async {
        enabled = on
        guard on else {
            setup = .off
            downloadProgress = nil
            return
        }
        guard #available(iOS 26, *) else { setup = .unavailable; return }
        setup = .checking
        guard await Self.speechAuthorised() else {
            if enabled { setup = .unavailable }
            return
        }
        guard let locale = await SpeechEngine.pickLocale() else {
            if enabled { setup = .unavailable }
            return
        }
        localeIdentifier = locale.identifier
        let status = await SpeechEngine.assetStatus(locale)
        if assetStatusBefore == nil { assetStatusBefore = Self.statusText(status) }
        guard enabled, setup == .checking else { return }
        switch status {
        case .installed: setup = .ready
        case .unsupported: setup = .unavailable
        default: setup = .needsAssets
        }
    }

    /// The user's explicit act on screen 1: the only place the speech model is ever downloaded.
    func downloadTapped() async {
        guard #available(iOS 26, *), setup == .needsAssets,
              let id = localeIdentifier else { return }
        setup = .downloading
        let clock = ContinuousClock()
        let began = clock.now
        do {
            if let request = try await SpeechEngine.installationRequest(Locale(identifier: id)) {
                downloadProgress = request.progress
                assetRequestMade = true
                try await request.downloadAndInstall()
                assetSeconds = Self.seconds(clock.now - began)
            }
            downloadProgress = nil
            guard enabled else { return }
            setup = await SpeechEngine.assetStatus(Locale(identifier: id)) == .installed
                ? .ready : .failed(PROPOSED_TranscriptNotInstalled)
        } catch {
            downloadProgress = nil
            if enabled { setup = .failed(error.localizedDescription) }
        }
    }

    /// After a failed download: try once more, still only on the user's tap.
    func retryDownload() async {
        guard case .failed = setup, enabled else { return }
        setup = .needsAssets
        await downloadTapped()
    }

    /// The speech-recognition permission prompt. Whether SpeechAnalyzer needs it is not stated in the iOS 27.2 SDK
    /// interface; it is asked as a precaution, only after the user turns the switch on.
    private static func speechAuthorised() async -> Bool {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized: return true
        case .denied, .restricted: return false
        case .notDetermined:
            let status = await withCheckedContinuation { continuation in
                SFSpeechRecognizer.requestAuthorization { continuation.resume(returning: $0) }
            }
            return status == .authorized
        @unknown default: return false
        }
    }

    // MARK: - Screen 2

    /// The second capture sink, or nil when the switch is off or the model is not ready (the recording path is then
    /// unchanged). Call before `AudioCapture.start()`.
    func makeSink() -> ((AVAudioPCMBuffer) -> Void)? {
        pcmSink = nil
        guard enabled, setup == .ready, #available(iOS 26, *), let id = localeIdentifier else { return nil }
        let store = speakers.makeStore()
        let engine = SpeechEngine(locale: Locale(identifier: id), onFirstInput: store.markAnalyzerStart)
        self.engine = engine
        pcmSink = store.append
        active = true
        live = .preparing
        finalText = ""
        volatileText = ""
        facts = nil
        lastFinalAt = nil
        segments = []
        wordRangeSegments = 0
        resultRangeSegments = 0
        return engine.feed
    }

    /// Starts analysis after capture has started. Never downloads: a missing model shows the unavailable line.
    func recordingStarted() {
        guard #available(iOS 26, *), let engine = engine as? SpeechEngine else { return }
        let seen = OSAllocatedUnfairLock(initialState: false)
        networkSeen = seen
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { path in
            if path.status == .satisfied { seen.withLock { $0 = true } }
        }
        monitor.start(queue: DispatchQueue(label: "dictum.transcript.path"))
        pathMonitor = monitor
        resultsTask = Task { [weak self] in
            do {
                for try await result in engine.transcriber.results {
                    self?.received(String(result.text.characters), isFinal: result.isFinal,
                                   span: result.isFinal ? SpeechEngine.span(result) : nil)
                }
            } catch is CancellationError {
            } catch {
                self?.live = .failed(error.localizedDescription)
            }
        }
        Task { [weak self] in
            do {
                try await engine.start()
                if self?.live == .preparing { self?.live = .listening }
            } catch SpeechEngine.EngineError.assetsMissing {
                self?.live = .unavailable
            } catch {
                self?.live = .failed(error.localizedDescription)
            }
        }
    }

    private func received(_ text: String, isFinal: Bool, span: TranscriptSpan?) {
        if live == .preparing { live = .listening }
        if isFinal {
            let words = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !words.isEmpty {
                switch span?.source {
                case .words: wordRangeSegments += 1
                case .result: resultRangeSegments += 1
                case nil: break
                }
                segments.append(SpeakerLabels.Segment(text: words, start: span?.start, end: span?.end))
            }
            finalText += text
            volatileText = ""
            lastFinalAt = ContinuousClock.now
        } else {
            volatileText = text
        }
    }

    /// Called right after capture stops. Finishing runs beside sealing; it never delays the seal.
    func recordingStopped() {
        guard #available(iOS 26, *), let engine = engine as? SpeechEngine else { return }
        let stoppedAt = ContinuousClock.now
        let thermal = Self.thermalText(ProcessInfo.processInfo.thermalState)
        if pathMonitor?.currentPath.status == .satisfied { networkSeen.withLock { $0 = true } }
        pathMonitor?.cancel()
        pathMonitor = nil
        let networkingOff = !networkSeen.withLock { $0 }
        finishing = true
        Task { [weak self] in
            await engine.finish()
            await self?.resultsTask?.value
            guard let self, self.engine === engine else { return }
            self.volatileText = ""
            let toFinal = self.lastFinalAt.flatMap { $0 >= stoppedAt ? Self.seconds($0 - stoppedAt) : nil }
            let before = self.assetStatusBefore ?? ""
            let assets: AssetOutcome
            if self.assetRequestMade, let seconds = self.assetSeconds {
                assets = .installed(seconds: seconds)
            } else if before == "installed" {
                assets = .preinstalled
            } else {
                // Start needs the model installed, so a non-installed status with no request means the request was nil.
                assets = .noRequest(statusBefore: before)
            }
            self.facts = Facts(locale: self.localeIdentifier ?? "", assets: assets, assetStatusBefore: before,
                               stopToLastFinalSeconds: toFinal, thermalAtStop: thermal, networkingOff: networkingOff)
            self.finishing = false
            // After the analyzer has finished, so every final segment is in. A segment without a time range keeps no
            // time (it is shown without a label); with no range at all, one segment covers the whole recording.
            let source: SpeakerLabels.TimeSource = self.wordRangeSegments > 0 ? .wordRanges
                : self.resultRangeSegments > 0 ? .resultRanges : .wholeRecording
            self.speakers.run(segments: self.segments, timeSource: source)
        }
    }

    /// Start failed before any audio was captured.
    func recordingAbandoned() {
        cancelEngine()
        speakers.clear()
        pcmSink = nil
        segments = []
        active = false
    }

    // MARK: - Screen 3

    /// Done: drop the transcript, the facts, the PCM copy, the voice prints and the speaker labels from memory, and put
    /// the switch back to its default, off.
    func clear() {
        cancelEngine()
        speakers.clear()
        pcmSink = nil
        segments = []
        wordRangeSegments = 0
        resultRangeSegments = 0
        active = false
        finishing = false
        finalText = ""
        volatileText = ""
        facts = nil
        lastFinalAt = nil
        assetSeconds = nil
        assetStatusBefore = nil
        assetRequestMade = false
        enabled = false
        setup = .off
        downloadProgress = nil
    }

    private func cancelEngine() {
        pathMonitor?.cancel()
        pathMonitor = nil
        resultsTask?.cancel()
        resultsTask = nil
        if #available(iOS 26, *), let engine = engine as? SpeechEngine {
            Task { await engine.cancel() }
        }
        engine = nil
    }

    // MARK: - Helpers

    private static func seconds(_ duration: Duration) -> Double {
        let parts = duration.components
        return Double(parts.seconds) + Double(parts.attoseconds) / 1e18
    }

    @available(iOS 26, *)
    private static func statusText(_ status: AssetInventory.Status) -> String {
        switch status {
        case .supported: return "supported"
        case .downloading: return "downloading"
        case .installed: return "installed"
        case .unsupported: return "unsupported"
        @unknown default: return "unknown"
        }
    }

    private static func thermalText(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .nominal: return "nominal"
        case .fair: return "fair"
        case .serious: return "serious"
        case .critical: return "critical"
        @unknown default: return "unknown"
        }
    }
}

/// A final result's seconds on the analyzer's timeline, and where they came from.
struct TranscriptSpan {
    enum Source { case words, result }
    var start: Double
    var end: Double
    var source: Source
}

/// SpeechAnalyzer + SpeechTranscriber fed from the capture tap.
///
/// `@unchecked Sendable`: `converter`, `converterInput` and `fedAny` are touched only on the audio tap's thread (tap
/// callbacks arrive one at a time); `target` is behind a lock the tap only tries, never waits on; everything else is
/// immutable or Sendable.
@available(iOS 26, *)
final class SpeechEngine: @unchecked Sendable {
    enum EngineError: Error { case assetsMissing, noFormat }

    let transcriber: SpeechTranscriber
    private let analyzer: SpeechAnalyzer
    private let input: AsyncStream<AnalyzerInput>
    private let continuation: AsyncStream<AnalyzerInput>.Continuation
    private let target = OSAllocatedUnfairLock<AVAudioFormat?>(uncheckedState: nil)
    // Audio tap thread only.
    private var converter: AVAudioConverter?
    private var converterInput: AVAudioFormat?
    private var fedAny = false
    /// Called on the audio tap's thread once, when the analyzer is given its first buffer.
    private let onFirstInput: () -> Void

    init(locale: Locale, onFirstInput: @escaping () -> Void = {}) {
        self.onFirstInput = onFirstInput
        transcriber = Self.makeTranscriber(locale)
        analyzer = SpeechAnalyzer(modules: [transcriber])
        // simplification: unbounded buffering between the tap and the analyzer. Ceiling: if the analyzer falls far
        // behind, memory grows for the rest of the recording. Retire when a device test measures the backlog.
        (input, continuation) = AsyncStream.makeStream(of: AnalyzerInput.self, bufferingPolicy: .unbounded)
    }

    private static func makeTranscriber(_ locale: Locale) -> SpeechTranscriber {
        SpeechTranscriber(locale: locale, transcriptionOptions: [], reportingOptions: [.volatileResults],
                          attributeOptions: [.audioTimeRange])
    }

    /// The min start and max end of the runs' `audioTimeRange`; else the result's own `range`; else nil.
    static func span(_ result: SpeechTranscriber.Result) -> TranscriptSpan? {
        var start = Double.infinity, end = -Double.infinity
        for run in result.text.runs {
            guard let range = run.audioTimeRange, range.start.isNumeric, range.duration.isNumeric else { continue }
            start = min(start, range.start.seconds)
            end = max(end, range.end.seconds)
        }
        if start.isFinite, end.isFinite, end >= start { return TranscriptSpan(start: start, end: end, source: .words) }
        let range = result.range
        if range.start.isNumeric, range.duration.isNumeric, range.duration.seconds > 0 {
            return TranscriptSpan(start: range.start.seconds, end: range.end.seconds, source: .result)
        }
        return nil
    }

    /// en_AU, then en_US, then the first supported English locale.
    static func pickLocale() async -> Locale? {
        for wanted in ["en_AU", "en_US"] {
            if let match = await SpeechTranscriber.supportedLocale(equivalentTo: Locale(identifier: wanted)) {
                return match
            }
        }
        return await SpeechTranscriber.supportedLocales.first { $0.language.languageCode == .english }
    }

    static func assetStatus(_ locale: Locale) async -> AssetInventory.Status {
        await AssetInventory.status(forModules: [makeTranscriber(locale)])
    }

    /// nil when nothing needs installing.
    static func installationRequest(_ locale: Locale) async throws -> AssetInstallationRequest? {
        try await AssetInventory.assetInstallationRequest(supporting: [makeTranscriber(locale)])
    }

    func start() async throws {
        // Never download here: the recording screen only uses a model that is already installed.
        guard await AssetInventory.status(forModules: [transcriber]) == .installed else { throw EngineError.assetsMissing }
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
            throw EngineError.noFormat
        }
        // simplification: audio that arrives before this line is dropped, so the first moment of speech may be
        // missing from the transcript. Ceiling: a short gap at the start. Retire if a device test shows words lost.
        target.withLockUnchecked { $0 = format }
        try await analyzer.start(inputSequence: input)
    }

    /// Called on the audio tap's thread with the tap's own buffer. Converts into a new buffer and hands it on; never
    /// waits. Buffers are dropped while the analyzer's format is not yet known.
    func feed(_ buffer: AVAudioPCMBuffer) {
        guard let format = target.withLockIfAvailableUnchecked({ $0 }) ?? nil else { return }
        if converterInput != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: format)
            converterInput = buffer.format
        }
        guard let converter else { return }
        let ratio = format.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount((Double(buffer.frameLength) * ratio).rounded(.up)) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return }
        var supplied = false
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, inputStatus in
            if supplied {
                inputStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            inputStatus.pointee = .haveData
            return buffer
        }
        guard status != .error, out.frameLength > 0 else { return }
        if !fedAny {
            fedAny = true
            onFirstInput()
        }
        continuation.yield(AnalyzerInput(buffer: out))
    }

    /// Ends input and waits for the last final results.
    func finish() async {
        continuation.finish()
        do {
            try await analyzer.finalizeAndFinishThroughEndOfInput()
        } catch {
            await analyzer.cancelAndFinishNow()
        }
    }

    func cancel() async {
        continuation.finish()
        await analyzer.cancelAndFinishNow()
    }
}
