import AVFoundation
import DictumBundle
import LocalAuthentication
import Observation
import SwiftUI

/// UI state for the whole app: home → consent → recording → sealing → sealed, plus recovery of a cut-short session.
@MainActor
@Observable
final class RecordingController {
    enum Phase {
        /// The sealed files and New meeting.
        case home
        case consent
        case recording
        case sealing
        /// After Stop with the on-device transcript: the end-of-meeting review. The bundle is already sealed.
        case review(FinishedBundle)
        case sealed(FinishedBundle)
        case recover(url: URL, hex: String)
        case failed(String)
        /// A kept meeting, opened after Face ID or the passcode (`detail` holds it).
        case detail
    }

    var phase: Phase = .home
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
    /// Non-sensitive choices in UserDefaults (engine, switches, threshold, meeting type). Never meeting content.
    let settings = AppSettings()
    /// The end-of-meeting review: names, summary, actions, agreement time. Memory only; cleared on Done.
    let review = MeetingReview()

    private var capture: AudioCapture?
    private var writer: SealWriter?
    private var meetingHex: String?

    /// Whether the meeting being recorded is kept on this device: the setting when Start was tapped, which also chose
    /// the notice shown and recorded.
    private(set) var keepThisMeeting = false
    /// The meeting's content key, in memory from Start until Done when it is kept, so the record is written at Done
    /// without a Face ID prompt. Zeroed on Done, on a failed start and on a failed seal.
    private var contentKey: Data?
    /// The sealed meeting waiting for its record at Done.
    private var keptMeeting: (id: MeetingId, url: URL)?
    /// True when a kept meeting's key or record could not be saved (the sealed file and its code are unaffected).
    var keepFailed = false

    /// One Face ID / passcode check for opening kept meetings.
    let auth = LibraryAuth()
    /// The kept meeting on screen, while `phase == .detail`.
    private(set) var detail: MeetingDetailModel?
    /// Titles of meetings opened since the app came to the foreground, for the home list. Memory only.
    private(set) var openedTitles: [String: String] = [:]
    /// A message for the home screen after a failed open or save.
    var homeMessage: String?

    init() {
        review.follow(transcript.speakers)
        checkForUnfinished()
        if settings.keepOnDevice { adoptLeftoverKeys() }
    }

    // MARK: - Home

    func newMeetingTapped() {
        micDenied = false
        phase = .consent
    }

    /// Back from the consent screen: nothing has started, so only the typed title is dropped.
    func backToHomeTapped() {
        guard case .consent = phase else { return }
        title = ""
        how = "spoken"
        micDenied = false
        phase = .home
    }

    // MARK: - Screen 1

    /// The microphone permission, asked for when not yet decided. Also used by "Your voice" set-up.
    static func microphoneAllowed() async -> Bool {
        switch AVAudioApplication.shared.recordPermission {
        case .granted: return true
        case .undetermined: return await AVAudioApplication.requestRecordPermission()
        case .denied: return false
        @unknown default: return false
        }
    }

    func startTapped() async {
        micDenied = false
        guard await Self.microphoneAllowed() else { micDenied = true; return }
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
        keepThisMeeting = settings.keepOnDevice
        keepFailed = false
        contentKey = keepThisMeeting ? key : nil

        let directory = try SessionFiles.makeSessionDirectory(hex: id.hex)
        // The notice recorded is the one shown on screen 1 for the keep setting in force now.
        let version = PROPOSED_ScriptVersionFor(keep: keepThisMeeting)
        let agreement = Agreement(acknowledgedAt: now, how: how, scriptVersion: version, kind: 0)
        let confirmation = Confirmation(answer: how, at: now, kind: 0, scriptVersion: version,
                                        script: PROPOSED_ConsentScriptFor(keep: keepThisMeeting), importerIdentity: nil, route: nil)
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
        // Switch off: rawSink and keep are nil, and the seal writer's sink is passed exactly as before. Switch on: the
        // seal writer gets each buffer first, then the in-memory copy for the experimental speaker step.
        transcript.labelsEnabled = settings.speakerLabels
        transcript.threshold = settings.speakerThreshold
        let rawSink = transcript.makeSink()
        let capture: AudioCapture
        if let keep = transcript.pcmSink {
            capture = AudioCapture(sink: { pcm in
                writer.append(pcm)
                keep(pcm)
            })
        } else {
            capture = AudioCapture(sink: writer.append)
        }
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
        capture.rawSink = rawSink
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
        dropContentKey()
    }

    private func dropContentKey() {
        contentKey?.resetBytes(in: 0..<(contentKey?.count ?? 0))
        contentKey = nil
        keptMeeting = nil
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
            if let hex = meetingHex {
                if keepThisMeeting, let key = contentKey, let id = MeetingId(hex: hex) {
                    keepSealed(id: id, url: bundle.url, key: key)
                } else {
                    dropContentKey()
                }
                // The crash-recovery copy goes either way: a kept key now lives only behind Face ID / the passcode.
                ContentKeyStore.delete(meetingHex: hex)
            }
            review.meetingDate = startedAt
            phase = transcript.active ? .review(bundle) : .sealed(bundle)
        } catch {
            // The session folder and its key stay, so the recovery screen offers it on the next launch.
            dropContentKey()
            phase = .failed(PROPOSED_SealFailed)
        }
        self.writer = nil
        meetingHex = nil
        startedAt = nil
        captureStatus = .running
        resetLevel()
    }

    // MARK: - Review, screen 3 and errors

    func continueToSealed() {
        guard case let .review(bundle) = phase else { return }
        phase = .sealed(bundle)
    }

    func doneTapped() {
        if let kept = keptMeeting, let key = contentKey {
            writeRecordAtDone(id: kept.id, url: kept.url, key: key)
        }
        dropContentKey()
        keepThisMeeting = false
        title = ""
        how = "spoken"
        micDenied = false
        captureFailed = false
        transcript.clear()
        review.clear()
        phase = .home
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
                // Kept only when the meeting was recorded under the keep-on notice and keeping is still on.
                if settings.keepOnDevice, let id = MeetingId(hex: hex),
                   Library.consentCoversOnDevice(Library.scriptVersions(url: bundle.url, contentKey: key, meetingId: id)) {
                    keepSealed(id: id, url: bundle.url, key: key)
                    keptMeeting = nil
                }
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

// MARK: - Meetings kept on this device

extension RecordingController {
    /// At seal: the key goes behind Face ID / the passcode, and a minimal record (title, start, duration) is written at
    /// once, so the meeting opens even if the app is closed before Done.
    fileprivate func keepSealed(id: MeetingId, url: URL, key: Data) {
        do {
            try LibraryKeyStore.save(key, meetingHex: id.hex)
            let meta = try BundleFile.meta(url: url, contentKey: key)
            var record = MeetingRecord.minimal(meta)
            record.meetingTypeId = settings.meetingTypeId
            try LibraryFiles.write(record, contentKey: key, meetingId: id)
            keptMeeting = (id, url)
        } catch {
            keepFailed = true
            keptMeeting = nil
        }
    }

    /// At Done: the review's transcript, names, summary, actions, confirmation time and facts, sealed into the record.
    fileprivate func writeRecordAtDone(id: MeetingId, url: URL, key: Data) {
        do {
            let meta = try BundleFile.meta(url: url, contentKey: key)
            var record = (try LibraryFiles.read(contentKey: key, meetingId: id)) ?? .minimal(meta)
            record.meetingTypeId = settings.meetingTypeId
            if transcript.active {
                record.segments = transcript.recordSegments
                let speakerFacts = transcript.speakers.facts
                record.facts = MeetingRecord.Facts(engine: settings.transcriptionEngine, locale: transcript.recordLocale,
                                                   speakersFound: speakerFacts?.speakersFound,
                                                   threshold: speakerFacts == nil ? nil : transcript.speakers.threshold,
                                                   wordFixes: transcript.wordFixes)
            }
            record.speakerNames = review.names
            record.summary = review.summary.state == .done ? review.summary.summary : ""
            record.actions = review.actions
                .filter { !($0.owner.isEmpty && $0.text.isEmpty && $0.byWhen.isEmpty) }
                .map { MeetingRecord.Action(owner: $0.owner, text: $0.text, byWhen: $0.byWhen) }
            record.confirmedAt = review.confirmedAt
            try LibraryFiles.write(record, contentKey: key, meetingId: id)
        } catch {
            homeMessage = PROPOSED_SaveFailed
        }
    }

    /// Launch, with keeping on: adopt a key an earlier build left under the crash-recovery service (expected: none).
    fileprivate func adoptLeftoverKeys() {
        Task {
            let entries = Library.entries()
            _ = try? await offMain { Library.adoptLeftoverKeys(entries) }
        }
    }

    /// Home → Face ID / passcode → the meeting.
    func openMeeting(_ entry: LibraryEntry) async {
        homeMessage = nil
        guard let hex = entry.meetingHex, let id = MeetingId(hex: hex) else {
            homeMessage = PROPOSED_OpenFailed
            return
        }
        let context: LAContext
        switch await auth.authenticate() {
        case let .ok(c): context = c
        case .denied: homeMessage = PROPOSED_AuthFailed; return
        case .unavailable: homeMessage = PROPOSED_AuthUnavailable; return
        }
        let outcome = (try? await offMain { LibraryKeyStore.load(meetingHex: hex, context: context) }) ?? .failed(0)
        guard case var .key(key) = outcome else {
            if case .missing = outcome { homeMessage = PROPOSED_KeyMissing } else { homeMessage = PROPOSED_AuthFailed }
            return
        }
        defer { key.resetBytes(in: 0..<key.count) }
        let url = entry.file.url
        do {
            let opened = try await offMain { () -> (MeetingRecord, [String]) in
                let record = try LibraryFiles.read(contentKey: key, meetingId: id)
                    ?? MeetingRecord.minimal(try BundleFile.meta(url: url, contentKey: key))
                return (record, Library.scriptVersions(url: url, contentKey: key, meetingId: id))
            }
            if !LibraryFiles.hasRecord(hex) { try? LibraryFiles.write(opened.0, contentKey: key, meetingId: id) }
            openedTitles[hex] = opened.0.title
            detail = MeetingDetailModel(url: url, meetingId: id, contentKey: Data(key), record: opened.0,
                                        consentCoversOnDevice: Library.consentCoversOnDevice(opened.1))
            phase = .detail
        } catch {
            homeMessage = PROPOSED_OpenFailed
        }
    }

    func closeDetail() {
        detail?.close()
        detail = nil
        phase = .home
    }

    func detailDeleted() {
        if let hex = detail?.meetingId.hex { openedTitles[hex] = nil }
        detail = nil
        phase = .home
    }

    /// A sealed file with no kept key: the six words unlock it, and the key is then kept behind Face ID / the passcode
    /// with a minimal record from its sealed metadata. Returns the message to show, or nil on success.
    func unlockWithWords(_ entry: LibraryEntry, code: String) async -> String? {
        guard settings.keepOnDevice else { return PROPOSED_KeepOffUnlock }
        guard let hex = entry.meetingHex, let id = MeetingId(hex: hex) else { return PROPOSED_UnlockFailed }
        let url = entry.file.url
        do {
            try await offMain {
                var key = try BundleFile.unlock(url: url, code: code)
                defer { key.resetBytes(in: 0..<key.count) }
                let meta = try BundleFile.meta(url: url, contentKey: key)
                try LibraryKeyStore.save(key, meetingHex: hex)
                if !LibraryFiles.hasRecord(hex) { try LibraryFiles.write(.minimal(meta), contentKey: key, meetingId: id) }
            }
            return nil
        } catch DictumBundleError.authenticationFailed {
            return PROPOSED_UnlockWrong
        } catch DictumBundleError.codeRequired {
            return PROPOSED_UnlockNeedsSix
        } catch {
            return PROPOSED_UnlockFailed
        }
    }

    /// The app left the foreground: the Face ID session ends, an open meeting is saved and closed, and remembered
    /// titles are dropped.
    func enteredBackground() {
        auth.invalidate()
        openedTitles = [:]
        if case .detail = phase { closeDetail() }
    }
}
