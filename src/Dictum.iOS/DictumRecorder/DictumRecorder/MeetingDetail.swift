import AVFoundation
import DictumBundle
import Observation
import SwiftUI

// A kept meeting, opened after Face ID or the passcode: the transcript with times and speaker names, the summary, who
// does what (editable, saved back to the encrypted record), the confirmation time and the facts; and compose email,
// send to computer (a fresh six-word code each time), play the recording from memory, and delete. The content key and
// every plaintext value here live in memory only and are dropped when the screen closes.

@MainActor
@Observable
final class MeetingDetailModel {
    enum Work: Equatable {
        case idle
        case working
        case failed(String)
    }

    let url: URL
    let meetingId: MeetingId
    private var contentKey: Data
    /// The record as last saved.
    private(set) var record: MeetingRecord
    /// Names, actions and the summary generator, as on the review screen.
    let review = MeetingReview()
    var summaryText: String
    private(set) var saveFailed = false
    /// The newest six-word code made by Send to computer; memory only, gone when the screen closes.
    private(set) var newCode: String?
    private(set) var rewrap: Work = .idle
    private(set) var transcribe: Work = .idle
    /// The meeting's recorded notice said transcripts can be made and kept on this device.
    let consentCoversOnDevice: Bool
    let player = MeetingPlayer()
    private var saveTask: Task<Void, Never>?

    init(url: URL, meetingId: MeetingId, contentKey: Data, record: MeetingRecord, consentCoversOnDevice: Bool) {
        self.url = url
        self.meetingId = meetingId
        self.contentKey = contentKey
        self.record = record
        self.consentCoversOnDevice = consentCoversOnDevice
        summaryText = record.summary
        review.names = record.speakerNames
        review.actions = record.actions.map { MeetingReview.Action(owner: $0.owner, text: $0.text, byWhen: $0.byWhen) }
        review.confirmedAt = record.confirmedAt
        review.meetingDate = record.startedAt
    }

    /// The record with the edits on screen.
    var current: MeetingRecord {
        var r = record
        r.speakerNames = review.names
        r.actions = review.actions.map { MeetingRecord.Action(owner: $0.owner, text: $0.text, byWhen: $0.byWhen) }
        r.summary = summaryText
        return r
    }

    var email: SummaryEmail {
        SummaryEmail.make(summary: summaryText, actions: review.actions, date: record.startedAt, footer: PROPOSED_EmailKeptLine)
    }

    func label(_ speaker: Int) -> String { review.label(speaker) }

    /// "00:12 Name: words" per line, for the summary model.
    var promptLines: [String] {
        record.segments.map { s in
            (s.start.map { SpeakerSeparation.clock($0) + " " } ?? "") + (s.speaker.map { label($0) + ": " } ?? "") + s.text
        }
    }

    /// Saves a second after the last edit.
    func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            self?.save()
        }
    }

    func save(force: Bool = false) {
        let next = current
        guard force || next != record else { return }
        do {
            try LibraryFiles.write(next, contentKey: contentKey, meetingId: meetingId)
            record = next
            saveFailed = false
        } catch {
            saveFailed = true
        }
    }

    /// A fresh code and salt for the same file (BundleFile.rewrap); the payload is unchanged.
    func sendToComputer() async {
        rewrap = .working
        newCode = nil
        let url = self.url, key = contentKey
        do {
            let code = try await offMain {
                var bytes = SystemByteSource()
                return try BundleFile.rewrap(url: url, contentKey: key, newCode: TestMode.sealingCode(), bytes: &bytes).code.text
            }
            SessionFiles.protect(url)
            newCode = code
            rewrap = .idle
        } catch {
            rewrap = .failed(PROPOSED_RewrapFailed)
        }
    }

    func play() async {
        await player.load(url: url, contentKey: contentKey)
        player.toggle()
    }

    /// Whether "Transcribe again" is offered: the notice read to the room covers it, and the whole meeting fits under
    /// the cap, so a new transcript can never be shorter than the one it replaces.
    var canTranscribeAgain: Bool { consentCoversOnDevice && record.duration <= Double(MeetingPlayer.capMinutes * 60) }

    /// Transcribes the decrypted audio in memory, then saves the transcript into the record. The transcript, and on a
    /// second run the names, change only when the new transcript is ready; a failure leaves the meeting as it was.
    func transcribeHere(labels: Bool, threshold: Double) async {
        guard #available(iOS 26, *) else {
            transcribe = .failed(PROPOSED_TranscribeUnavailable)
            return
        }
        transcribe = .working
        let url = self.url, key = contentKey
        do {
            guard var pcm = try await offMain({ try BundleFile.audio(url: url, contentKey: key, limit: MeetingPlayer.capBytes) }),
                  !pcm.isEmpty else {
                transcribe = .failed(PROPOSED_NoAudio)
                return
            }
            defer { pcm.resetBytes(in: 0..<pcm.count) }
            let outcome = try await LibraryTranscriber.transcribe(pcm: Data(pcm.prefix(MeetingPlayer.capBytes)), labels: labels,
                                                                  threshold: threshold)
            // Transcribe again numbers the speakers afresh, so names given to the old numbers are dropped, not carried.
            if !record.segments.isEmpty { review.forgetNames() }
            record.segments = outcome.segments
            // The name is placed a moment later (the voice-print is read off the main thread), so save again then;
            // otherwise it would reach the record only when the screen closes.
            if labels { review.applyHostVoice(centres: outcome.centres) { [weak self] in self?.save() } }
            record.facts = MeetingRecord.Facts(engine: "apple", locale: outcome.locale, speakersFound: outcome.speakersFound,
                                               threshold: outcome.threshold)
            save(force: true)
            transcribe = .idle
        } catch LibraryTranscriber.Failure.needsModel {
            transcribe = .failed(PROPOSED_TranscribeNeedsModel)
        } catch LibraryTranscriber.Failure.noWords {
            transcribe = .failed(PROPOSED_TranscribeNone)
        } catch LibraryTranscriber.Failure.unavailable {
            transcribe = .failed(PROPOSED_TranscribeUnavailable)
        } catch {
            transcribe = .failed(String(format: PROPOSED_TranscribeFailed, error.localizedDescription))
        }
    }

    /// Leaving the screen: save, stop playback, drop the summary state and the key.
    func close() {
        saveTask?.cancel()
        save()
        player.stop()
        review.clear()
        contentKey.resetBytes(in: 0..<contentKey.count)
    }

    func delete() -> Bool {
        saveTask?.cancel()
        player.stop()
        guard Library.delete(url: url, meetingHex: meetingId.hex) else { return false }
        review.clear()
        contentKey.resetBytes(in: 0..<contentKey.count)
        return true
    }
}

/// Plays the recording from memory: the audio entry is verified and decrypted to PCM, wrapped in a WAV header in memory
/// and given to AVAudioPlayer as data. No audio file is written.
@MainActor
@Observable
final class MeetingPlayer {
    enum State: Equatable {
        case idle
        case loading
        case ready
        case failed(String)
    }

    // simplification: only the first 60 minutes are decrypted and played (115,200,000 PCM bytes in memory, plus the
    // WAV copy). Ceiling: a longer meeting plays its first hour only, and says so. Retire with streamed playback
    // (AVAudioEngine fed record by record) once meetings over an hour are kept.
    static let capMinutes = 60
    static let capBytes = capMinutes * 60 * 32_000

    private(set) var state: State = .idle
    private(set) var playing = false
    private(set) var capped = false
    private(set) var duration: TimeInterval = 0
    private var player: AVAudioPlayer?
    private var wav = Data()
    private let finished = Finished()

    var currentTime: TimeInterval { player?.currentTime ?? 0 }

    func load(url: URL, contentKey: Data) async {
        guard player == nil else { return }
        state = .loading
        do {
            guard var pcm = try await offMain({ try BundleFile.audio(url: url, contentKey: contentKey, limit: Self.capBytes) }),
                  pcm.count >= 2 else {
                state = .failed(PROPOSED_NoAudio)
                return
            }
            defer { pcm.resetBytes(in: 0..<pcm.count) }
            capped = pcm.count >= Self.capBytes
            wav = Self.wav(pcm.prefix(Self.capBytes))
            let made = try AVAudioPlayer(data: wav)
            finished.onFinish = { [weak self] in Task { @MainActor in self?.playing = false } }
            made.delegate = finished
            made.prepareToPlay()
            player = made
            duration = made.duration
            state = .ready
        } catch {
            state = .failed(PROPOSED_PlayFailed)
        }
    }

    func toggle() {
        guard let player else { return }
        if player.isPlaying {
            player.pause()
            playing = false
        } else {
            try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio)
            try? AVAudioSession.sharedInstance().setActive(true)
            playing = player.play()
        }
    }

    func seek(_ time: TimeInterval) {
        player?.currentTime = min(max(time, 0), duration)
    }

    /// Stops and drops the decrypted sound.
    func stop() {
        player?.stop()
        player = nil
        playing = false
        wav.resetBytes(in: 0..<wav.count)
        wav = Data()
        duration = 0
        if state != .idle { try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation) }
        state = .idle
    }

    /// RIFF/WAVE header for PCM16 mono 16 kHz, then the samples.
    static func wav(_ pcm: Data) -> Data {
        var out = Data(capacity: 44 + pcm.count)
        func le32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { out.append(contentsOf: $0) } }
        func le16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { out.append(contentsOf: $0) } }
        out.append(contentsOf: Array("RIFF".utf8)); le32(UInt32(36 + pcm.count))
        out.append(contentsOf: Array("WAVE".utf8))
        out.append(contentsOf: Array("fmt ".utf8)); le32(16); le16(1); le16(1); le32(16_000); le32(32_000); le16(2); le16(16)
        out.append(contentsOf: Array("data".utf8)); le32(UInt32(pcm.count))
        out.append(pcm)
        return out
    }

    private final class Finished: NSObject, AVAudioPlayerDelegate {
        var onFinish: (() -> Void)?
        func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) { onFinish?() }
    }
}

// MARK: - Screen

struct MeetingDetailView: View {
    @Bindable var model: MeetingDetailModel
    let settings: AppSettings
    let close: () -> Void
    let deleted: () -> Void
    @State private var composing = false
    @State private var confirmDelete = false
    @State private var deleteFailed = false
    @State private var renaming: Int?
    @State private var draftName = ""
    @State private var invalidName = false
    @State private var copied = false
    @State private var confirmAgain = false

    private static let dateTime: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_AU")
        f.dateFormat = "dd/MM/yyyy h:mm a"
        return f
    }()

    private var record: MeetingRecord { model.record }
    private var numbers: [Int] { Set(record.segments.compactMap(\.speaker)).sorted() }

    var body: some View {
        DictumScreen {
            Button(action: close) {
                Label(PROPOSED_MeetingBack, systemImage: "chevron.left")
            }
            .buttonStyle(.dictumCompact)
            .accessibilityLabel(PROPOSED_MeetingBack)
            .accessibilityIdentifier("detail.back")
        } content: {
            VStack(alignment: .leading, spacing: 6) {
                Text(record.title)
                    .dictumTitle()
                    .accessibilityAddTraits(.isHeader)
                Text(Self.dateTime.string(from: record.startedAt) + " · " + RecordingView.elapsed(seconds: record.duration))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(DictumTheme.text)
                StatusPill(kind: .done, text: PROPOSED_Saved)
            }
            if model.saveFailed {
                StatusPill(kind: .warning, text: PROPOSED_SaveFailed)
            }

            transcriptCard
            summaryCard
            ReviewActionsView(review: model.review, owners: numbers.map(model.label))
            agreementCard
            factsCard

            SecondaryButton(title: PROPOSED_ComposeEmail, systemImage: "envelope") { composing = true }
            sendCard
            playCard

            if deleteFailed {
                StatusPill(kind: .warning, text: PROPOSED_DeleteFailed)
            }
            Button(role: .destructive) { confirmDelete = true } label: {
                Label(PROPOSED_DeleteMeeting, systemImage: "trash")
            }
            .buttonStyle(.dictumSecondary)
            .accessibilityLabel(PROPOSED_DeleteMeeting)
        }
        .onChange(of: model.current) { _, _ in model.scheduleSave() }
        .onChange(of: model.review.summary.state) { _, state in
            if state == .done {
                model.summaryText = model.review.summary.summary
                model.review.prefillActions()
            }
        }
        .sheet(isPresented: $composing) {
            ComposeEmailSheet(email: model.email)
        }
        .confirmationDialog(PROPOSED_DeleteMeetingTitle, isPresented: $confirmDelete, titleVisibility: .visible) {
            Button(PROPOSED_DeleteMeetingConfirm, role: .destructive) {
                if model.delete() { deleted() } else { deleteFailed = true }
            }
            Button(PROPOSED_Cancel, role: .cancel) {}
        } message: {
            Text(PROPOSED_DeleteMeetingMessage)
        }
        .confirmationDialog(PROPOSED_TranscribeAgainTitle, isPresented: $confirmAgain, titleVisibility: .visible) {
            Button(PROPOSED_TranscribeAgainConfirm, role: .destructive) {
                Task { await model.transcribeHere(labels: settings.speakerLabels, threshold: settings.speakerThreshold) }
            }
            Button(PROPOSED_Cancel, role: .cancel) {}
        } message: {
            Text(PROPOSED_TranscribeAgainMessage)
        }
        .alert(renaming.map { String(format: APPROVED_RenameSpeaker, String(format: APPROVED_Other, $0)) } ?? "",
               isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField(PROPOSED_NameField, text: $draftName)
                .textInputAutocapitalization(.words)
                .accessibilityLabel(PROPOSED_NameField)
            Button(APPROVED_SaveNames) { saveName() }
            Button(PROPOSED_Cancel, role: .cancel) { renaming = nil }
        } message: {
            if invalidName { Text(APPROVED_InvalidName) }
        }
    }

    // MARK: Transcript

    private var transcriptCard: some View {
        DictumCard {
            SectionHeader(PROPOSED_DetailTranscript)
            if record.segments.isEmpty {
                Text(PROPOSED_DetailNoTranscript)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.text)
                transcribeControls
            } else {
                if !numbers.isEmpty {
                    Text(PROPOSED_RenameHint)
                        .font(.footnote)
                        .foregroundStyle(DictumTheme.muted)
                    FlowRow(spacing: 8) {
                        ForEach(numbers, id: \.self) { number in
                            Button {
                                draftName = model.review.names[number] ?? ""
                                invalidName = false
                                renaming = number
                            } label: {
                                Label(model.label(number), systemImage: "person.fill")
                            }
                            .buttonStyle(.dictumCompact)
                            .accessibilityLabel(model.label(number))
                            .accessibilityHint(PROPOSED_RenameAction)
                        }
                    }
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(Array(record.segments.enumerated()), id: \.offset) { _, segment in
                            line(segment)
                                .accessibilityElement(children: .ignore)
                                .accessibilityLabel(spoken(segment))
                        }
                    }
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 360)
                .accessibilityElement(children: .contain)
                .accessibilityLabel(PROPOSED_SpeakerLinesRegion)
                if !numbers.isEmpty {
                    Text(APPROVED_Limits)
                        .font(.footnote)
                        .foregroundStyle(DictumTheme.muted)
                }
                Button {
                    UIPasteboard.general.string = record.segments.map(copyLine).joined(separator: "\n")
                    copied = true
                } label: {
                    Label(copied ? PROPOSED_TranscriptCopied : PROPOSED_CopyTranscript, systemImage: "doc.on.doc")
                }
                .buttonStyle(.dictumSecondary)
                .accessibilityLabel(PROPOSED_CopyTranscript)
                .accessibilityValue(copied ? PROPOSED_TranscriptCopied : "")
                if model.canTranscribeAgain { transcribeAgainControls }
            }
        }
    }

    @ViewBuilder private var transcribeAgainControls: some View {
        switch model.transcribe {
        case .working:
            StatusPill(kind: .working, text: PROPOSED_Transcribing)
        case let .failed(message):
            StatusPill(kind: .warning, text: message)
        case .idle:
            EmptyView()
        }
        SecondaryButton(title: PROPOSED_TranscribeAgain, systemImage: "arrow.clockwise") { confirmAgain = true }
            .disabled(model.transcribe == .working)
    }

    @ViewBuilder private var transcribeControls: some View {
        if !model.consentCoversOnDevice {
            Text(PROPOSED_TranscribeEarlierNotice)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
        } else {
            switch model.transcribe {
            case .working:
                StatusPill(kind: .working, text: PROPOSED_Transcribing)
            case let .failed(message):
                StatusPill(kind: .warning, text: message)
            case .idle:
                EmptyView()
            }
            SecondaryButton(title: PROPOSED_TranscribeHere, systemImage: "waveform") {
                Task { await model.transcribeHere(labels: settings.speakerLabels, threshold: settings.speakerThreshold) }
            }
            .disabled(model.transcribe == .working)
        }
    }

    private func words(_ s: MeetingRecord.Segment) -> String {
        s.speaker.map { "\(model.label($0)): \(s.text)" } ?? s.text
    }

    private func copyLine(_ s: MeetingRecord.Segment) -> String {
        s.start.map { "\(SpeakerSeparation.clock($0))  \(words(s))" } ?? words(s)
    }

    private func spoken(_ s: MeetingRecord.Segment) -> String {
        s.start.map { String(format: PROPOSED_LineAt, SpeakerSeparation.clock($0)) + ", " + words(s) } ?? words(s)
    }

    /// Time in muted monospaced digits, the speaker in bold, then the words (not told apart by colour alone).
    private func line(_ s: MeetingRecord.Segment) -> Text {
        var parts: [Text] = []
        if let start = s.start {
            parts.append(Text(SpeakerSeparation.clock(start) + "  ").monospacedDigit().foregroundStyle(DictumTheme.muted))
        }
        if let speaker = s.speaker { parts.append(Text(model.label(speaker) + ": ").bold()) }
        parts.append(Text(s.text))
        return parts.dropFirst().reduce(parts[0]) { Text("\($0)\($1)") }
    }

    private func saveName() {
        guard let number = renaming else { return }
        let trimmed = draftName.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            model.review.setName(nil, for: number)
            renaming = nil
        } else if MeetingReview.isValidName(trimmed) {
            model.review.setName(trimmed, for: number)
            renaming = nil
        } else {
            invalidName = true
            let again = number
            renaming = nil
            Task { @MainActor in renaming = again }
        }
    }

    // MARK: Summary

    private var summaryCard: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewSummary)
            TextField(PROPOSED_DetailSummaryField, text: $model.summaryText, prompt: Text(PROPOSED_DetailNoSummary), axis: .vertical)
                .lineLimit(3...12)
                .textFieldStyle(DictumFieldStyle())
                .accessibilityLabel(PROPOSED_DetailSummaryField)
            switch model.review.summary.state {
            case .idle, .done:
                EmptyView()
            case let .working(part, of):
                StatusPill(kind: .working, text: of > 1 ? "\(PROPOSED_SummaryWorking) \(part)/\(of)" : PROPOSED_SummaryWorking)
            case let .unavailable(message), let .failed(message):
                StatusPill(kind: .warning, text: message)
            }
            if !record.segments.isEmpty {
                Button {
                    model.review.summary.generate(lines: model.promptLines,
                                                  type: MeetingType.with(id: record.meetingTypeId ?? settings.meetingTypeId))
                } label: {
                    Label(model.summaryText.isEmpty ? PROPOSED_MakeSummary : PROPOSED_MakeSummaryAgain, systemImage: "text.badge.star")
                }
                .buttonStyle(.dictumSecondary)
                .disabled(model.review.summary.working)
                .accessibilityLabel(model.summaryText.isEmpty ? PROPOSED_MakeSummary : PROPOSED_MakeSummaryAgain)
                Text(PROPOSED_SummaryExperimentalNote)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }
        }
    }

    // MARK: Agreement and facts

    private var agreementCard: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewAgreement)
            if let at = record.confirmedAt {
                StatusPill(kind: .done, text: String(format: PROPOSED_ConfirmedAt, Self.dateTime.string(from: at)))
            } else {
                Text(PROPOSED_DetailNotConfirmed)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.text)
            }
        }
    }

    private var factsCard: some View {
        DictumCard {
            DisclosureGroup {
                VStack(alignment: .leading, spacing: 8) {
                    FactRow(PROPOSED_FactStarted, value: Self.dateTime.string(from: record.startedAt))
                    FactRow(PROPOSED_FactDuration, value: RecordingView.elapsed(seconds: record.duration))
                    FactRow(PROPOSED_FactMeetingType, value: MeetingType.with(id: record.meetingTypeId ?? "general").name)
                    FactRow(PROPOSED_FactEngine, value: record.facts.engine ?? PROPOSED_FactNotRun)
                    FactRow(PROPOSED_FactLocale, value: record.facts.locale ?? PROPOSED_FactNotRun)
                    FactRow(PROPOSED_FactSpeakers, value: record.facts.speakersFound.map(String.init) ?? PROPOSED_FactNotRun)
                    FactRow(PROPOSED_FactThreshold, value: record.facts.threshold.map { String(format: PROPOSED_FactThresholdValue, $0) }
                            ?? PROPOSED_FactNotRun)
                    if let host = model.review.hostVoice {
                        FactRow(PROPOSED_FactHostVoice, value: host.text)
                    }
                }
                .padding(.top, 8)
            } label: {
                Text(PROPOSED_DetailFacts).font(.headline).foregroundStyle(DictumTheme.text)
            }
            .tint(DictumTheme.link)
            .frame(minHeight: DictumTheme.minTarget)
        }
    }

    // MARK: Send and play

    private var sendCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            PrimaryButton(title: PROPOSED_SendToComputer, systemImage: "laptopcomputer.and.arrow.down") {
                Task { await model.sendToComputer() }
            }
            .disabled(model.rewrap == .working)
            .accessibilityIdentifier("detail.send")
            switch model.rewrap {
            case .working: StatusPill(kind: .working, text: PROPOSED_Rewrapping)
            case let .failed(message): StatusPill(kind: .warning, text: message)
            case .idle: EmptyView()
            }
            if let code = model.newCode {
                DictumCard {
                    SectionHeader(PROPOSED_CodeLabel)
                    Text(code)
                        .font(.system(.title2, design: .monospaced).weight(.semibold))
                        .foregroundStyle(DictumTheme.text)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                        .accessibilityLabel(PROPOSED_CodeLabel)
                        .accessibilityValue(code)
                    Text(PROPOSED_NewCodeNote)
                        .font(.callout.weight(.semibold))
                        .foregroundStyle(DictumTheme.text)
                    Text(PROPOSED_CodeHint)
                        .font(.callout)
                        .foregroundStyle(DictumTheme.muted)
                    ShareLink(item: model.url) {
                        Label(PROPOSED_Share, systemImage: "square.and.arrow.up")
                    }
                    .buttonStyle(.dictumSecondary)
                    .accessibilityLabel(PROPOSED_Share)
                }
            }
        }
    }

    private var playCard: some View {
        DictumCard {
            switch model.player.state {
            case .idle:
                SecondaryButton(title: PROPOSED_PlayRecording, systemImage: "play.fill") {
                    Task { await model.play() }
                }
            case .loading:
                StatusPill(kind: .working, text: PROPOSED_Decrypting)
            case let .failed(message):
                StatusPill(kind: .warning, text: message)
            case .ready:
                TimelineView(.periodic(from: .now, by: 0.5)) { _ in
                    let now = model.player.currentTime
                    VStack(alignment: .leading, spacing: 8) {
                        ProgressView(value: model.player.duration > 0 ? now / model.player.duration : 0)
                            .tint(DictumTheme.accent)
                            .accessibilityLabel(PROPOSED_PlaybackPosition)
                            .accessibilityValue(RecordingView.elapsed(seconds: now))
                        Text(RecordingView.elapsed(seconds: now) + " / " + RecordingView.elapsed(seconds: model.player.duration))
                            .font(.subheadline.monospacedDigit())
                            .foregroundStyle(DictumTheme.text)
                            .accessibilityHidden(true)
                    }
                }
                HStack(spacing: 8) {
                    Button { model.player.toggle() } label: {
                        Label(model.player.playing ? PROPOSED_PauseRecording : PROPOSED_PlayRecording,
                              systemImage: model.player.playing ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(.dictumCompact)
                    .accessibilityLabel(model.player.playing ? PROPOSED_PauseRecording : PROPOSED_PlayRecording)
                    Button { model.player.stop() } label: {
                        Label(PROPOSED_StopPlayback, systemImage: "stop.fill")
                    }
                    .buttonStyle(.dictumCompact)
                    .accessibilityLabel(PROPOSED_StopPlayback)
                }
                if model.player.capped {
                    Text(String(format: PROPOSED_PlayCapNote, MeetingPlayer.capMinutes))
                        .font(.footnote)
                        .foregroundStyle(DictumTheme.muted)
                }
            }
        }
    }
}
