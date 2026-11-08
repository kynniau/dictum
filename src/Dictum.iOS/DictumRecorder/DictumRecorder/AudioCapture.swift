import AVFoundation

/// Microphone capture: AVAudioEngine input tap → AVAudioConverter → 16 kHz mono signed 16-bit PCM.
///
/// Conversion runs on the tap's thread; the converted `Data` goes to `sink`, which must not block (the seal writer
/// only enqueues it on its own serial queue). Interruptions and route changes restart the engine and keep feeding the
/// same sink, so audio after a phone call or a headset change lands in the same session.
final class AudioCapture {
    enum CaptureError: Error { case noInput, noConverter }

    /// What the capture is doing, reported on the main thread.
    enum Status { case running, interrupted, needsUser }

    static let outputFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!

    private var engine = AVAudioEngine()
    private let sink: (Data) -> Void
    private var observers: [NSObjectProtocol] = []
    private var interrupted = false
    private var stopped = false

    /// Called on the main thread whenever the capture status changes.
    var onStatus: ((Status) -> Void)?
    /// Called on the main thread about ten times a second with the peak |sample| of the converted audio since the
    /// last report, 0...1 (0 means every converted sample was exactly zero). Set before `start()`.
    var onLevel: ((Double) -> Void)?

    init(sink: @escaping (Data) -> Void) {
        self.sink = sink
    }

    deinit {
        observers.forEach(NotificationCenter.default.removeObserver)
    }

    func start() throws {
        let session = AVAudioSession.sharedInstance()
        // Mode .default: no voice processing, so quiet passages are kept rather than gated (not measured on a device).
        try session.setCategory(.record, mode: .default, options: [])
        try session.setActive(true)
        observe(session)
        try startEngine()
    }

    func stop() {
        stopped = true
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    /// Restarts capture after an interruption the system did not resume, or after a failed restart.
    func resume() {
        guard !stopped else { return }
        do {
            try AVAudioSession.sharedInstance().setActive(true)
            try startEngine()
            interrupted = false
            onStatus?(.running)
        } catch {
            onStatus?(.needsUser)
        }
    }

    // MARK: - Engine

    private func startEngine() throws {
        let input = engine.inputNode
        input.removeTap(onBus: 0)
        if engine.isRunning { engine.stop() }

        // Read the format every time: a route change can change the hardware sample rate or channel count.
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { throw CaptureError.noInput }
        guard let converter = AVAudioConverter(from: inputFormat, to: Self.outputFormat) else { throw CaptureError.noConverter }
        converter.downmix = true

        let sink = self.sink
        let meter = LevelMeter(report: onLevel)
        input.installTap(onBus: 0, bufferSize: 4096, format: inputFormat) { buffer, _ in
            if let data = Self.convert(buffer, with: converter) {
                meter.add(data)
                sink(data)
            }
        }
        engine.prepare()
        try engine.start()
    }

    /// Converts one tap buffer. Runs on the audio tap's thread; allocates one small output buffer and never blocks.
    private static func convert(_ buffer: AVAudioPCMBuffer, with converter: AVAudioConverter) -> Data? {
        let ratio = outputFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount((Double(buffer.frameLength) * ratio).rounded(.up)) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else { return nil }

        var supplied = false
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, inputStatus in
            if supplied {
                // .noDataNow (not .endOfStream) keeps the resampler's state primed for the next buffer.
                inputStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            inputStatus.pointee = .haveData
            return buffer
        }
        guard status != .error, out.frameLength > 0, let channels = out.int16ChannelData else { return nil }
        return Data(bytes: channels[0], count: Int(out.frameLength) * MemoryLayout<Int16>.size)
    }

    // MARK: - Interruptions and route changes

    private func observe(_ session: AVAudioSession) {
        let centre = NotificationCenter.default
        observers.append(centre.addObserver(forName: AVAudioSession.interruptionNotification, object: session, queue: .main) { [weak self] note in
            self?.handleInterruption(note)
        })
        observers.append(centre.addObserver(forName: AVAudioSession.routeChangeNotification, object: session, queue: .main) { [weak self] note in
            self?.handleRouteChange(note)
        })
        // The engine stops itself when its hardware configuration changes (for example a new input's sample rate).
        observers.append(centre.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            self?.restartAfterChange()
        })
        // simplification: after a media-services reset the engine is rebuilt and the user is asked to resume.
        // Ceiling: no automatic recovery from a reset. Retire when a device test shows resets during meetings.
        observers.append(centre.addObserver(forName: AVAudioSession.mediaServicesWereResetNotification, object: session, queue: .main) { [weak self] _ in
            guard let self, !self.stopped else { return }
            self.rebuildEngine()
            self.interrupted = true
            self.onStatus?(.needsUser)
        })
    }

    private func handleInterruption(_ note: Notification) {
        guard !stopped,
              let raw = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: raw) else { return }
        switch type {
        case .began:
            interrupted = true
            engine.stop()
            onStatus?(.interrupted)
        case .ended:
            let optionsRaw = note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt ?? 0
            if AVAudioSession.InterruptionOptions(rawValue: optionsRaw).contains(.shouldResume) {
                resume()
            } else {
                onStatus?(.needsUser)
            }
        @unknown default:
            break
        }
    }

    private func handleRouteChange(_ note: Notification) {
        guard !stopped, !interrupted,
              let raw = note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
              let reason = AVAudioSession.RouteChangeReason(rawValue: raw),
              reason == .oldDeviceUnavailable else { return }
        restartAfterChange()
    }

    private func restartAfterChange() {
        guard !stopped, !interrupted else { return }
        do {
            try startEngine()
            onStatus?(.running)
        } catch {
            interrupted = true
            onStatus?(.needsUser)
        }
    }

    private func rebuildEngine() {
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
        engine = AVAudioEngine()
        observe(AVAudioSession.sharedInstance())
    }
}

/// Peak meter for the converted PCM. Used only from the audio tap's thread (tap callbacks arrive one at a time);
/// it reports to the main thread at most every 100 ms.
private final class LevelMeter {
    private let report: ((Double) -> Void)?
    private var peak: Int32 = 0
    private var lastReport: UInt64 = 0
    private static let intervalNanos: UInt64 = 100_000_000

    init(report: ((Double) -> Void)?) { self.report = report }

    func add(_ pcm: Data) {
        guard let report else { return }
        pcm.withUnsafeBytes { raw in
            for sample in raw.bindMemory(to: Int16.self) {
                let magnitude = abs(Int32(Int16(littleEndian: sample)))
                if magnitude > peak { peak = magnitude }
            }
        }
        let now = DispatchTime.now().uptimeNanoseconds
        guard now &- lastReport >= Self.intervalNanos else { return }
        lastReport = now
        let level = min(1, Double(peak) / Double(Int16.max))
        peak = 0
        DispatchQueue.main.async { report(level) }
    }
}
