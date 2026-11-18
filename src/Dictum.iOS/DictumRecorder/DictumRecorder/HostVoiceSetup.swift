import Foundation
import Observation
import os
import SwiftUI

// "Your voice" set-up, reached from Settings (never during a recording). The microphone is captured with the app's own
// AudioCapture (16 kHz mono PCM16) into memory only; after Stop the sound is cut into the speaker step's windows
// (SpeakerParameters.current: 2 s long, 1 s apart), quiet windows are skipped, each window gets one voice print from
// the app's embedder, and their mean is kept with the typed name in the Keychain (HostVoiceStore). The sound is dropped
// as soon as the voice-print is made, or at once on Cancel, an interruption or leaving the screen. Nothing is written
// to disk.

/// The set-up sound in memory, capped at `capSamples`. The audio tap appends; the main actor reads the count and takes it.
final class SetupSamples: @unchecked Sendable {
    static let capSamples = Int(HostVoiceSetup.maxSeconds) * SpeakerSeparation.rate
    private let state = OSAllocatedUnfairLock(initialState: [Int16]())

    /// Audio tap thread: one converted buffer.
    func append(_ pcm: Data) {
        state.withLock { s in
            let room = Self.capSamples - s.count
            guard room > 0 else { return }
            pcm.withUnsafeBytes { raw in
                s.append(contentsOf: raw.bindMemory(to: Int16.self).prefix(room).lazy.map { Int16(littleEndian: $0) })
            }
        }
    }

    var count: Int { state.withLock { $0.count } }

    /// The samples; the store is emptied.
    func take() -> [Int16] {
        state.withLock { s in
            let out = s
            s = []
            return out
        }
    }
}

@MainActor
@Observable
final class HostVoiceSetup {
    enum Phase: Equatable {
        case loading
        case notSetUp
        case recording
        case working
        case setUp
    }

    nonisolated static let minSeconds = 20.0
    nonisolated static let maxSeconds = 45.0

    private(set) var phase: Phase = .loading
    /// The name kept with the voice-print; nil when none is kept.
    private(set) var savedName: String?
    var name = ""
    /// Seconds of sound captured so far.
    private(set) var seconds = 0.0
    private(set) var level = 0.0
    private(set) var message: String?
    private(set) var micDenied = false
    /// The voice model is in this build (checked without loading it).
    let modelAvailable = VoiceEmbedders.libraryLinked
        && Bundle.main.path(forResource: VoiceEmbedders.modelName, ofType: "onnx") != nil

    private var capture: AudioCapture?
    private var samples: SetupSamples?
    private var ticker: Task<Void, Never>?
    private var work: Task<Void, Never>?

    var trimmedName: String { name.trimmingCharacters(in: .whitespacesAndNewlines) }
    var nameValid: Bool { MeetingReview.isValidName(trimmedName) }
    var canStart: Bool { modelAvailable && nameValid && phase == .notSetUp }
    var canStop: Bool { phase == .recording && seconds >= Self.minSeconds }

    /// Reads whether a voice-print is kept, off the main thread; only the name comes back.
    func load() async {
        guard phase == .loading else { return }
        let stored = await Task.detached(priority: .userInitiated) { HostVoiceStore.load()?.name }.value
        savedName = stored
        if name.isEmpty { name = stored ?? "" }
        phase = stored == nil ? .notSetUp : .setUp
    }

    func doItAgain() {
        message = nil
        phase = .notSetUp
    }

    /// Back to the kept voice-print without recording again.
    func keepCurrent() {
        guard savedName != nil, phase == .notSetUp else { return }
        name = savedName ?? ""
        message = nil
        phase = .setUp
    }

    func start() async {
        guard canStart else { return }
        message = nil
        micDenied = false
        guard await RecordingController.microphoneAllowed() else { micDenied = true; return }
        guard phase == .notSetUp else { return }
        let buffer = SetupSamples()
        let made = AudioCapture(sink: { buffer.append($0) })
        made.onLevel = { [weak self] level in self?.level = level }
        made.onStatus = { [weak self] status in
            if status != .running { self?.cancel(PROPOSED_HostVoiceInterrupted) }
        }
        do {
            try made.start()
        } catch AudioCapture.CaptureError.noInput {
            made.stop()
            message = PROPOSED_NoMicrophone
            return
        } catch {
            made.stop()
            message = PROPOSED_StartFailed
            return
        }
        capture = made
        samples = buffer
        seconds = 0
        phase = .recording
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(200))
                guard let self, self.phase == .recording, let samples = self.samples else { return }
                self.seconds = Double(samples.count) / Double(SpeakerSeparation.rate)
                if self.seconds >= Self.maxSeconds { self.stop() }
            }
        }
    }

    /// Stop and save: the sound becomes a voice-print off the main thread and is then dropped.
    func stop() {
        guard canStop, let samples else { return }
        endCapture()
        let taken = samples.take()
        self.samples = nil
        let name = trimmedName
        phase = .working
        work = Task { [weak self] in
            let made = await Task.detached(priority: .userInitiated) { HostVoiceSetup.makePrint(from: taken) }.value
            guard !Task.isCancelled else { return }
            var saved = false
            if case let .print(print) = made {
                saved = await Task.detached(priority: .userInitiated) { HostVoiceStore.save(print: print, name: name) }.value
            }
            guard let self, !Task.isCancelled else { return }
            switch made {
            case .print where saved:
                self.savedName = name
                self.message = nil
                self.phase = .setUp
            case .print:
                self.fail(PROPOSED_HostVoiceSaveFailed)
            case .unavailable:
                self.fail(PROPOSED_HostVoiceUnavailable)
            case .tooLittle:
                self.fail(PROPOSED_HostVoiceNotEnough)
            }
        }
    }

    /// Cancel, an interruption or leaving the screen: the sound is dropped and nothing is kept.
    func cancel(_ reason: String? = nil) {
        work?.cancel()
        work = nil
        endCapture()
        _ = samples?.take()
        samples = nil
        seconds = 0
        if phase == .recording || phase == .working { fail(reason) }
    }

    func remove() async -> Bool {
        let removed = await Task.detached(priority: .userInitiated) { HostVoiceStore.remove() }.value
        if removed {
            savedName = nil
            name = ""
            message = nil
            phase = .notSetUp
        } else {
            message = PROPOSED_HostVoiceRemoveFailed
        }
        return removed
    }

    private func fail(_ reason: String?) {
        message = reason
        phase = .notSetUp
    }

    private func endCapture() {
        ticker?.cancel()
        ticker = nil
        capture?.onStatus = nil
        capture?.stop()
        capture = nil
        level = 0
    }

    enum Made: Sendable {
        case print([Float])
        case unavailable
        case tooLittle
    }

    /// Off the main thread: the speaker step's windows over the whole sample, quiet ones skipped, one voice print each,
    /// then their mean. Loads the model, so it is slow the first time.
    nonisolated static func makePrint(from samples: [Int16]) -> Made {
        let embedder = VoiceEmbedders.make()
        guard embedder.dim > 0 else { return .unavailable }
        let floats = samples.map { Float($0) / 32768 } // as the speaker step reads the PCM
        let windows = SpeakerSeparation.windows(start: 0, end: Double(samples.count) / Double(SpeakerSeparation.rate),
                                                trackLength: samples.count, parameters: .current)
        var prints: [[Float]] = []
        for w in windows {
            let slice = floats[w.from..<w.to]
            guard HostVoice.rms(slice) >= HostVoice.silenceRms, let print = embedder.embed(Array(slice)) else { continue }
            prints.append(print)
        }
        guard prints.count >= HostVoice.minWindows, let print = HostVoice.print(from: prints) else { return .tooLittle }
        return .print(print)
    }
}

// MARK: - Screen

struct HostVoiceView: View {
    @State private var model = HostVoiceSetup()
    @State private var confirmRemove = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text(PROPOSED_HostVoiceIntro)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
                if let message = model.message {
                    StatusPill(kind: .warning, text: message)
                }
                switch model.phase {
                case .loading:
                    ProgressView()
                        .frame(maxWidth: .infinity)
                        .accessibilityLabel(PROPOSED_HostVoiceTitle)
                case .setUp:
                    savedCard
                case .notSetUp, .recording, .working:
                    setUpCard
                }
                Text(PROPOSED_HostVoicePrivacy)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }
            .padding(.vertical, 12)
            .dictumPage()
        }
        .background(DictumTheme.bg.ignoresSafeArea())
        .navigationTitle(PROPOSED_HostVoiceTitle)
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.load() }
        .onDisappear { model.cancel() }
        .confirmationDialog(PROPOSED_HostVoiceRemoveTitle, isPresented: $confirmRemove, titleVisibility: .visible) {
            Button(PROPOSED_HostVoiceRemove, role: .destructive) {
                Task { _ = await model.remove() }
            }
            Button(PROPOSED_Cancel, role: .cancel) {}
        } message: {
            Text(PROPOSED_HostVoiceRemoveMessage)
        }
    }

    private var savedCard: some View {
        DictumCard {
            StatusPill(kind: .done, text: String(format: PROPOSED_HostVoiceSavedAs, model.savedName ?? ""))
            SecondaryButton(title: PROPOSED_HostVoiceAgain, systemImage: "arrow.counterclockwise") { model.doItAgain() }
            Button(role: .destructive) { confirmRemove = true } label: {
                Label(PROPOSED_HostVoiceRemove, systemImage: "trash")
            }
            .buttonStyle(.dictumSecondary)
            .accessibilityLabel(PROPOSED_HostVoiceRemove)
        }
    }

    private var setUpCard: some View {
        DictumCard {
            TextField(PROPOSED_HostVoiceNameField, text: $model.name)
                .textFieldStyle(DictumFieldStyle())
                .textInputAutocapitalization(.words)
                .disabled(model.phase != .notSetUp)
                .accessibilityLabel(PROPOSED_HostVoiceNameField)
            if !model.trimmedName.isEmpty && !model.nameValid {
                Text(APPROVED_InvalidName)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.text)
            }

            SectionHeader(PROPOSED_HostVoicePassageHeading)
            Text(PROPOSED_HostVoicePassage)
                .font(.title3)
                .foregroundStyle(DictumTheme.text)
                .fixedSize(horizontal: false, vertical: true)

            if !model.modelAvailable {
                StatusPill(kind: .warning, text: PROPOSED_HostVoiceUnavailable)
            }
            if model.micDenied {
                StatusPill(kind: .warning, text: PROPOSED_MicDenied)
            }

            switch model.phase {
            case .recording:
                recordingControls
            case .working:
                StatusPill(kind: .working, text: PROPOSED_HostVoiceWorking)
            default:
                PrimaryButton(title: PROPOSED_HostVoiceStart, systemImage: "mic.fill") {
                    Task { await model.start() }
                }
                .disabled(!model.canStart)
                if model.savedName != nil {
                    SecondaryButton(title: PROPOSED_Cancel) { model.keepCurrent() }
                }
            }
        }
    }

    private var recordingControls: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 16) {
                DictumLevelMeter(level: model.level, active: true, height: 48)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(PROPOSED_InputLevel)
                    .accessibilityValue(String(format: PROPOSED_InputLevelValue, Int((model.level * 100).rounded())))
                Text(String(format: PROPOSED_HostVoiceSeconds, Int(model.seconds)))
                    .font(.title2.monospacedDigit().weight(.semibold))
                    .foregroundStyle(DictumTheme.text)
                    .accessibilityLabel(PROPOSED_ElapsedLabel)
                    .accessibilityValue(String(format: PROPOSED_HostVoiceSeconds, Int(model.seconds)))
            }
            Text(String(format: PROPOSED_HostVoiceRecordingHint, Int(HostVoiceSetup.minSeconds), Int(HostVoiceSetup.maxSeconds)))
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
            PrimaryButton(title: PROPOSED_HostVoiceStop, systemImage: "stop.fill") { model.stop() }
                .disabled(!model.canStop)
            SecondaryButton(title: PROPOSED_Cancel) { model.cancel() }
        }
    }
}
