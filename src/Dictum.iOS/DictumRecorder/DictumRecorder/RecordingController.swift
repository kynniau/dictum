import AVFoundation
import DictumBundle
import Observation
import SwiftUI

/// UI state for the whole app: consent → recording → sealing → sealed, plus recovery of a cut-short session.
@MainActor
@Observable
final class RecordingController {
    enum Phase {
        case consent
        case recording
        case sealing
        case sealed(FinishedBundle)
        case recover(url: URL, hex: String)
        case failed(String)
    }

    var phase: Phase = .consent
    var title = ""
    /// "spoken" or "written", the values the desktop importer accepts. Defaults to spoken, as the desktop picker does.
    var how = "spoken"
    var micDenied = false
    var startedAt: Date?
    /// Capture status while recording. Anything but .running means no sound is being captured right now.
    var captureStatus: AudioCapture.Status = .running
    /// Peak input level of the converted audio over the last ~100 ms, 0...1.
    var level: Double = 0
    /// How long every converted sample has been exactly zero while capture was running, in seconds.
    var silentSeconds: TimeInterval = 0
    private var silentSince: Date?
    /// True when capture stopped early because the encrypted file could not be written.
    var captureFailed = false

    /// Experimental on-device transcript (switch on screen 1, default off). Memory only; never written to disk.
    let transcript = OnDeviceTranscript()

    private var capture: AudioCapture?
    private var writer: SealWriter?
    private var meetingHex: String?

    init() {
        checkForUnfinished()
    }

    // MARK: - Screen 1

    func startTapped() async {
        micDenied = false
        switch AVAudioApplication.shared.recordPermission {
        case .granted:
            break
        case .undetermined:
            guard await AVAudioApplication.requestRecordPermission() else { micDenied = true; return }
        case .denied:
            micDenied = true
            return
        @unknown default:
            micDenied = true
            return
        }
        do {
            try begin()
        } catch AudioCapture.CaptureError.noInput {
            abandonStart()
            phase = .failed(PROPOSED_NoMicrophone)
        } catch {
            abandonStart()
            phase = .failed(PROPOSED_StartFailed)
        }
    }

    private func begin() throws {
        let now = Date()
        let id = MeetingId.random()
        meetingHex = id.hex

        // The key is in the Keychain before the session exists, so a crash at any later point can still be sealed.
        let key = try ContentKeyStore.newKey()
        try ContentKeyStore.save(key, meetingHex: id.hex)

        let directory = try SessionFiles.makeSessionDirectory(hex: id.hex)
        let agreement = Agreement(acknowledgedAt: now, how: how, scriptVersion: PROPOSED_ScriptVersion, kind: 0)
        let confirmation = Confirmation(answer: how, at: now, kind: 0, scriptVersion: PROPOSED_ScriptVersion,
                                        script: PROPOSED_ConsentScript, importerIdentity: nil, route: nil)
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let session = try RecordingSession(directory: directory, meetingId: id, contentKey: key, startedAt: now,
                                           title: trimmed.isEmpty ? PROPOSED_DefaultTitle : trimmed,
                                           agreement: agreement, confirmations: [confirmation],
                                           appVersion: AppInfo.version)
        SessionFiles.protect(directory)

        let writer = SealWriter(session: session)
        writer.onFailure = { [weak self] in
            guard let self, case .recording = self.phase else { return }
            self.captureFailed = true
            Task { await self.stopTapped() }
        }
        let capture = AudioCapture(sink: writer.append)
        capture.onStatus = { [weak self] status in
            guard let self else { return }
            self.captureStatus = status
            // Silence is only counted while capture runs; a pause restarts the count.
            self.silentSince = nil
            self.silentSeconds = 0
        }
        capture.onLevel = { [weak self] level in
            self?.levelReported(level)
        }
        capture.rawSink = transcript.makeSink()
        self.writer = writer
        self.capture = capture

        try capture.start()
        transcript.recordingStarted()
        startedAt = now
        resetLevel()
        captureFailed = false
        captureStatus = .running
        phase = .recording
    }

    /// Start failed before any audio was captured: remove the empty session and its key.
    private func abandonStart() {
        transcript.recordingAbandoned()
        capture?.stop()
        capture = nil
        writer = nil
        if let hex = meetingHex {
            SessionFiles.remove(SessionFiles.sessionDirectory(hex: hex))
            ContentKeyStore.delete(meetingHex: hex)
        }
        meetingHex = nil
    }

    func openSettings() {
        if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
    }

    // MARK: - Screen 2

    private func levelReported(_ value: Double) {
        guard case .recording = phase, captureStatus == .running else { return }
        level = value
        if value > 0 {
            silentSince = nil
            silentSeconds = 0
        } else {
            let now = Date()
            if silentSince == nil { silentSince = now }
            silentSeconds = now.timeIntervalSince(silentSince ?? now)
        }
    }

    private func resetLevel() {
        level = 0
        silentSeconds = 0
        silentSince = nil
    }

    /// True after 3 s of continuous all-zero input while capture is running.
    var noSoundDetected: Bool {
        captureStatus == .running && silentSeconds >= 3
    }

    func resumeTapped() {
        capture?.resume()
    }

    func stopTapped() async {
        guard case .recording = phase, let writer else { return }
        phase = .sealing
        capture?.stop()
        capture = nil
        transcript.recordingStopped()
        do {
            let bundle = try await writer.finish(at: Date())
            SessionFiles.protect(bundle.url)
            if let hex = meetingHex { ContentKeyStore.delete(meetingHex: hex) }
            phase = .sealed(bundle)
        } catch {
            // The session folder and its key stay, so the recovery screen offers it on the next launch.
            phase = .failed(PROPOSED_SealFailed)
        }
        self.writer = nil
        meetingHex = nil
        startedAt = nil
        captureStatus = .running
        resetLevel()
    }

    // MARK: - Screen 3 and errors

    func doneTapped() {
        title = ""
        how = "spoken"
        micDenied = false
        captureFailed = false
        transcript.clear()
        phase = .consent
        checkForUnfinished()
    }

    // MARK: - Screen 4

    private func checkForUnfinished() {
        if let first = SessionFiles.pending().first {
            phase = .recover(url: first.url, hex: first.hex)
        }
    }

    func recoverTapped() async {
        guard case let .recover(url, hex) = phase else { return }
        guard let key = ContentKeyStore.load(meetingHex: hex) else {
            // simplification: a session whose key is gone is left on disk, untouched, and offered again next launch.
            // Ceiling: it can never be sealed. Retire with a "Delete this unfinished recording" action once its
            // wording is approved.
            phase = .failed(PROPOSED_RecoverNoKey)
            return
        }
        phase = .sealing
        do {
            let version = AppInfo.version
            if let bundle = try await offMain({ try RecordingSession.recover(directory: url, appVersion: version, contentKey: key) }) {
                SessionFiles.protect(bundle.url)
                ContentKeyStore.delete(meetingHex: hex)
                phase = .sealed(bundle)
            } else {
                // The package returns nil only when the folder holds no audio file (the app died while the session
                // was being created), so the folder holds no sound; remove it so it is not offered again.
                SessionFiles.remove(url)
                ContentKeyStore.delete(meetingHex: hex)
                phase = .failed(PROPOSED_RecoverNothing)
            }
        } catch {
            phase = .failed(PROPOSED_SealFailed)
        }
    }
}
