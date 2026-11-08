import DictumBundle
import Observation
import SwiftUI

// The end-of-meeting review, between Stop and the sealed screen: the transcript with speaker naming, an on-device
// summary, who does what, and the spoken agreement. Everything on it lives in memory only (this object, the transcript
// and the speaker labels) and is cleared on Done. Nothing here is written to disk, put in the bundle or sent: the
// recording was already sealed at Stop, exactly as before.

/// Review state for one meeting. Main thread only; memory only.
@MainActor
@Observable
final class MeetingReview {
    struct Action: Identifiable, Equatable {
        let id = UUID()
        var owner = ""
        var text = ""
        var byWhen = ""
    }

    /// Names given to speaker numbers for this meeting.
    var names: [Int: String] = [:]
    var actions: [Action] = []
    /// When "Everyone confirmed" was tapped.
    var confirmedAt: Date?
    /// When the recording started, for the email subject.
    var meetingDate: Date?
    let summary = OnDeviceSummary()

    func label(_ speaker: Int) -> String { names[speaker] ?? String(format: APPROVED_Other, speaker) }

    /// Fills the actions from the model's suggestions when none have been typed yet.
    func prefillActions() {
        guard actions.allSatisfy({ $0.owner.isEmpty && $0.text.isEmpty && $0.byWhen.isEmpty }) else { return }
        actions = summary.actions.map { Action(owner: $0.owner, text: $0.text, byWhen: $0.byWhen) }
    }

    func clear() {
        names = [:]
        actions = []
        confirmedAt = nil
        meetingDate = nil
        summary.clear()
    }

    /// The desktop's name rule (MeetingSpeakers.IsValidName): trimmed, 1 to 80 characters of letters, digits, white
    /// space, full stop, the two apostrophes and hyphen, no control character, and not shaped like "Other n".
    static func isValidName(_ name: String) -> Bool {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...80).contains(trimmed.count), !trimmed.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
        else { return false }
        guard trimmed.range(of: #"^[\p{L}\p{Nd}\s.'’\-]+$"#, options: .regularExpression) != nil else { return false }
        return trimmed.range(of: #"^Other [0-9]+$"#, options: .regularExpression) == nil
    }
}

struct ReviewView: View {
    @Bindable var controller: RecordingController
    let bundle: FinishedBundle
    @State private var composing = false

    private var review: MeetingReview { controller.review }
    private var transcript: OnDeviceTranscript { controller.transcript }

    var body: some View {
        DictumScreen {
            Text(PROPOSED_ReviewHeading)
                .dictumTitle()
                .accessibilityAddTraits(.isHeader)
            Text(PROPOSED_ReviewIntro)
                .font(.body)
                .foregroundStyle(DictumTheme.text)
            StatusPill(kind: .done, text: PROPOSED_ReviewSealedNote)

            ReviewTranscriptView(transcript: transcript, review: review)
            ReviewSummaryView(controller: controller)
            ReviewActionsView(review: review, owners: owners)
            ReviewAgreementView(review: review)

            SecondaryButton(title: PROPOSED_ComposeEmail, systemImage: "envelope") { composing = true }
            PrimaryButton(title: PROPOSED_ContinueToSealed, systemImage: "lock.fill") { controller.continueToSealed() }
                .accessibilityIdentifier("review.continue")
        }
        .sheet(isPresented: $composing) {
            ComposeEmailSheet(review: review)
        }
    }

    /// The speakers' names or labels, in number order, for the owner picker.
    private var owners: [String] {
        let speakers = Set(transcript.speakers.lines.compactMap(\.speaker)).sorted()
        return speakers.map(review.label)
    }
}

// MARK: - 1. Transcript and speaker naming

struct ReviewTranscriptView: View {
    let transcript: OnDeviceTranscript
    @Bindable var review: MeetingReview
    @State private var copied = false
    @State private var renaming: Int?
    @State private var draftName = ""
    @State private var invalidName = false
    @State private var showSeparated = false

    private var speakers: SpeakerLabels { transcript.speakers }

    /// The lines by speaker, once the speaker step is done; empty before then and when it did not run.
    private var labelled: [SpeakerLabels.Line] { speakers.state == .done ? speakers.lines : [] }
    private var numbers: [Int] { Set(labelled.compactMap(\.speaker)).sorted() }

    var body: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewTranscript)
            Text(PROPOSED_TranscriptHeading)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)

            if transcript.finishing {
                StatusPill(kind: .working, text: PROPOSED_TranscriptFinishing)
            }
            if speakers.reclustering {
                StatusPill(kind: .working, text: APPROVED_Separating)
            }
            SpeakerStatusView(speakers: speakers)

            if !numbers.isEmpty {
                speakerControls
            }

            ScrollView {
                Group {
                    if !labelled.isEmpty {
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(labelled) { line in
                                speakerLine(line)
                                    .accessibilityElement(children: .ignore)
                                    .accessibilityLabel(spoken(line))
                            }
                        }
                        .accessibilityElement(children: .contain)
                        .accessibilityLabel(PROPOSED_SpeakerLinesRegion)
                    } else if transcript.finalText.isEmpty && transcript.volatileText.isEmpty && !transcript.finishing {
                        Text(PROPOSED_TranscriptNone)
                    } else {
                        Text(transcript.finalText + transcript.volatileText)
                    }
                }
                .font(.body)
                .foregroundStyle(DictumTheme.text)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 320)

            if !labelled.isEmpty {
                Text(PROPOSED_SpeakersExperimentalNote)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }

            if !transcript.finalText.isEmpty {
                Button {
                    UIPasteboard.general.string = labelled.isEmpty ? transcript.finalText : labelledText
                    copied = true
                } label: {
                    Label(copied ? PROPOSED_TranscriptCopied : PROPOSED_CopyTranscript, systemImage: "doc.on.doc")
                }
                .buttonStyle(.dictumSecondary)
                .accessibilityLabel(PROPOSED_CopyTranscript)
                .accessibilityValue(copied ? PROPOSED_TranscriptCopied : "")
            }

            if let facts = transcript.facts {
                TranscriptFactsView(facts: facts, speakers: speakers)
            }
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

    private var speakerControls: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(PROPOSED_RenameHint)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
            FlowRow(spacing: 8) {
                ForEach(numbers, id: \.self) { number in
                    Button {
                        draftName = review.names[number] ?? ""
                        invalidName = false
                        renaming = number
                    } label: {
                        Label(review.label(number), systemImage: "person.fill")
                    }
                    .buttonStyle(.dictumCompact)
                    .accessibilityLabel(review.label(number))
                    .accessibilityHint(PROPOSED_RenameAction)
                }
            }
            HStack(spacing: 8) {
                Button(APPROVED_FewerSpeakers) { recluster(fewer: true) }
                    .buttonStyle(.dictumCompact)
                    .disabled(!speakers.canRecluster || speakers.threshold.map { $0 >= 0.9 } ?? true)
                    .accessibilityLabel(APPROVED_FewerSpeakers)
                Button(APPROVED_MoreSpeakers) { recluster(fewer: false) }
                    .buttonStyle(.dictumCompact)
                    .disabled(!speakers.canRecluster || speakers.threshold.map { $0 <= 0.1 } ?? true)
                    .accessibilityLabel(APPROVED_MoreSpeakers)
            }
            if showSeparated, !speakers.reclustering, let count = speakers.facts?.speakersFound {
                StatusPill(kind: .done, text: String(format: APPROVED_Separated, count))
            }
            Text(APPROVED_Limits)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
        }
    }

    private func recluster(fewer: Bool) {
        // simplification: re-clustering renumbers the speakers, so names given before it are dropped rather than
        // carried to the new numbers. Ceiling: names must be typed again after Fewer or More speakers. Retire with a
        // rule that maps old clusters to new ones (for example by shared segments).
        review.names = [:]
        showSeparated = true
        speakers.recluster(fewer: fewer)
    }

    private func saveName() {
        guard let number = renaming else { return }
        let trimmed = draftName.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            review.names[number] = nil
            renaming = nil
        } else if MeetingReview.isValidName(trimmed) {
            review.names[number] = trimmed
            renaming = nil
        } else {
            // The alert closes on any button; open it again with the desktop's approved message.
            invalidName = true
            let again = number
            renaming = nil
            Task { @MainActor in renaming = again }
        }
    }

    private func label(_ line: SpeakerLabels.Line) -> String? { line.speaker.map(review.label) }

    private func words(_ line: SpeakerLabels.Line) -> String {
        label(line).map { "\($0): \(line.text)" } ?? line.text
    }

    /// "00:12  Name: …" per line, for Copy.
    private var labelledText: String {
        labelled.map { line in
            line.start.map { "\(SpeakerSeparation.clock($0))  \(words(line))" } ?? words(line)
        }.joined(separator: "\n")
    }

    /// VoiceOver: "at 00:12, Name: …".
    private func spoken(_ line: SpeakerLabels.Line) -> String {
        line.start.map { String(format: PROPOSED_LineAt, SpeakerSeparation.clock($0)) + ", " + words(line) } ?? words(line)
    }

    /// The time in muted monospaced digits, the speaker in bold (neither told apart by colour alone), then the words.
    private func speakerLine(_ line: SpeakerLabels.Line) -> Text {
        var parts: [Text] = []
        if let start = line.start {
            parts.append(Text(SpeakerSeparation.clock(start) + "  ").monospacedDigit().foregroundStyle(DictumTheme.muted))
        }
        if let label = label(line) { parts.append(Text(label + ": ").bold()) }
        parts.append(Text(line.text))
        return parts.dropFirst().reduce(parts[0]) { Text("\($0)\($1)") }
    }
}

/// The transcript's measured facts, and the speaker step's beneath.
struct TranscriptFactsView: View {
    let facts: OnDeviceTranscript.Facts
    let speakers: SpeakerLabels

    var body: some View {
        DisclosureGroup {
            VStack(alignment: .leading, spacing: 8) {
                FactRow(PROPOSED_FactLocale, value: facts.locale)
                FactRow(PROPOSED_FactAssets, value: assetText(facts.assets))
                FactRow(PROPOSED_FactAssetStatusBefore, value: facts.assetStatusBefore)
                FactRow(PROPOSED_FactStopToFinal, value: facts.stopToLastFinalSeconds.map { String(format: PROPOSED_FactSeconds, $0) }
                        ?? PROPOSED_FactNoFinalAfterStop)
                FactRow(PROPOSED_FactThermal, value: facts.thermalAtStop)
                FactRow(PROPOSED_FactOffline, value: facts.networkingOff ? PROPOSED_FactYes : PROPOSED_FactNo)
                if let speakerFacts = speakers.facts {
                    SpeakerFactsView(facts: speakerFacts)
                }
            }
            .padding(.top, 8)
        } label: {
            Text(PROPOSED_FactsHeading).font(.headline).foregroundStyle(DictumTheme.text)
        }
        .tint(DictumTheme.link)
        .frame(minHeight: DictumTheme.minTarget)
    }

    private func assetText(_ outcome: OnDeviceTranscript.AssetOutcome) -> String {
        switch outcome {
        case .preinstalled: return PROPOSED_FactAssetsPreinstalled
        case let .installed(seconds): return String(format: PROPOSED_FactAssetsInstalled, seconds)
        case let .noRequest(before): return String(format: PROPOSED_FactAssetsNoRequest, before)
        }
    }
}

/// Wraps its children onto new lines, so speaker chips fit at any width and text size.
struct FlowRow: Layout {
    var spacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, row: CGFloat = 0, widest: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width {
                y += row + spacing
                x = 0
                row = 0
            }
            x += size.width + spacing
            row = max(row, size.height)
            widest = max(widest, x - spacing)
        }
        return CGSize(width: proposal.width ?? widest, height: y + row)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, row: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX {
                y += row + spacing
                x = bounds.minX
                row = 0
            }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            row = max(row, size.height)
        }
    }
}

// MARK: - 2. Summary

struct ReviewSummaryView: View {
    @Bindable var controller: RecordingController

    private var review: MeetingReview { controller.review }
    private var summary: OnDeviceSummary { review.summary }
    private var transcript: OnDeviceTranscript { controller.transcript }

    /// The speaker step is still going, so the labels (and names) are not final yet.
    private var waitingForSpeakers: Bool {
        if transcript.finishing || transcript.speakers.reclustering { return true }
        if case .working = transcript.speakers.state { return true }
        return false
    }

    private var hasWords: Bool { !transcript.finalText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    var body: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewSummary)

            switch summary.state {
            case .idle:
                EmptyView()
            case let .working(part, of):
                StatusPill(kind: .working, text: of > 1 ? "\(PROPOSED_SummaryWorking) \(part)/\(of)" : PROPOSED_SummaryWorking)
            case .done:
                StatusPill(kind: .done, text: PROPOSED_SummaryDone)
                Text(summary.summary)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
                    .textSelection(.enabled)
            case let .unavailable(message):
                StatusPill(kind: .warning, text: message)
            case let .failed(message):
                StatusPill(kind: .warning, text: message)
            }

            if !hasWords && !transcript.finishing {
                Text(PROPOSED_SummaryNeedsTranscript)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.muted)
            } else if waitingForSpeakers {
                Text(PROPOSED_SummaryWaitSpeakers)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.muted)
            }

            Button {
                summary.generate(lines: promptLines, type: controller.settings.meetingType)
            } label: {
                Label(summary.state == .done ? PROPOSED_MakeSummaryAgain : PROPOSED_MakeSummary, systemImage: "text.badge.star")
            }
            .buttonStyle(.dictumSecondary)
            .disabled(!hasWords || waitingForSpeakers || summary.working)
            .accessibilityLabel(summary.state == .done ? PROPOSED_MakeSummaryAgain : PROPOSED_MakeSummary)

            Text(PROPOSED_SummaryExperimentalNote)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)

            if let facts = summary.facts {
                DisclosureGroup {
                    VStack(alignment: .leading, spacing: 8) {
                        FactRow(PROPOSED_FactModelAvailable, value: facts.available ? PROPOSED_FactYes : PROPOSED_FactCapNo)
                        if let reason = facts.reason {
                            FactRow(PROPOSED_FactModelReason, value: reason)
                        }
                        FactRow(PROPOSED_FactModelStatusBefore, value: facts.statusBefore)
                        FactRow(PROPOSED_FactModelRequest, value: facts.requestMade ? PROPOSED_FactYes : PROPOSED_FactModelRequestNo)
                        FactRow(PROPOSED_FactSummarySeconds, value: facts.seconds.map { String(format: PROPOSED_FactSeconds, $0) }
                                ?? PROPOSED_FactNotRun)
                        FactRow(PROPOSED_FactSummaryParts, value: facts.parts.map(String.init) ?? PROPOSED_FactNotRun)
                        FactRow(PROPOSED_MeetingTypePicker, value: controller.settings.meetingType.name)
                    }
                    .padding(.top, 8)
                } label: {
                    Text(PROPOSED_SummaryFactsHeading).font(.headline).foregroundStyle(DictumTheme.text)
                }
                .tint(DictumTheme.link)
                .frame(minHeight: DictumTheme.minTarget)
            }
        }
        .onChange(of: summary.state) { _, state in
            if state == .done { review.prefillActions() }
        }
    }

    /// "00:12 Name: words" per labelled line (names given on this screen), else the plain final text.
    private var promptLines: [String] {
        let speakers = transcript.speakers
        guard speakers.state == .done, !speakers.lines.isEmpty else { return [transcript.finalText] }
        return speakers.lines.map { line in
            let who = line.speaker.map { review.label($0) + ": " } ?? ""
            return (line.start.map { SpeakerSeparation.clock($0) + " " } ?? "") + who + line.text
        }
    }
}

// MARK: - 3. Who does what

struct ReviewActionsView: View {
    @Bindable var review: MeetingReview
    let owners: [String]

    var body: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewActions)
            if review.actions.isEmpty {
                Text(PROPOSED_NoActions)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.muted)
            }
            ForEach(Array($review.actions.enumerated()), id: \.element.id) { index, $action in
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text(String(format: PROPOSED_ActionRow, index + 1))
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(DictumTheme.text)
                            .accessibilityAddTraits(.isHeader)
                        Spacer()
                        Button {
                            review.actions.removeAll { $0.id == action.id }
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.dictumCompact)
                        .accessibilityLabel(PROPOSED_RemoveAction)
                        .accessibilityValue(String(format: PROPOSED_ActionRow, index + 1))
                    }
                    HStack(spacing: 8) {
                        TextField(PROPOSED_ActionOwner, text: $action.owner)
                            .textFieldStyle(DictumFieldStyle())
                            .textInputAutocapitalization(.words)
                            .accessibilityLabel(PROPOSED_ActionOwner)
                        if !owners.isEmpty {
                            Menu {
                                ForEach(owners, id: \.self) { owner in
                                    Button(owner) { action.owner = owner }
                                }
                            } label: {
                                Image(systemName: "person.crop.circle.badge.checkmark")
                                    .font(.title3)
                                    .frame(minWidth: DictumTheme.minTarget, minHeight: DictumTheme.minTarget)
                                    .background(DictumTheme.accentSoft, in: RoundedRectangle(cornerRadius: DictumTheme.radiusControl))
                            }
                            .foregroundStyle(DictumTheme.text)
                            .accessibilityLabel(PROPOSED_ActionOwnerPick)
                        }
                    }
                    TextField(PROPOSED_ActionText, text: $action.text, axis: .vertical)
                        .lineLimit(1...4)
                        .textFieldStyle(DictumFieldStyle())
                        .accessibilityLabel(PROPOSED_ActionText)
                    TextField(PROPOSED_ActionByWhen, text: $action.byWhen)
                        .textFieldStyle(DictumFieldStyle())
                        .accessibilityLabel(PROPOSED_ActionByWhen)
                }
                .padding(.vertical, 6)
                if index < review.actions.count - 1 {
                    Divider().overlay(DictumTheme.border)
                }
            }
            Button {
                review.actions.append(MeetingReview.Action())
            } label: {
                Label(PROPOSED_AddAction, systemImage: "plus")
            }
            .buttonStyle(.dictumSecondary)
            .accessibilityLabel(PROPOSED_AddAction)
        }
    }
}

// MARK: - 4. Agreement

struct ReviewAgreementView: View {
    @Bindable var review: MeetingReview

    private static let time: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_AU")
        f.dateStyle = .none
        f.timeStyle = .short
        return f
    }()

    var body: some View {
        DictumCard {
            SectionHeader(PROPOSED_ReviewAgreement)
            Text(PROPOSED_AgreementText)
                .font(.body)
                .foregroundStyle(DictumTheme.text)
            if let at = review.confirmedAt {
                StatusPill(kind: .done, text: String(format: PROPOSED_ConfirmedAt, Self.time.string(from: at)))
            } else {
                Button {
                    review.confirmedAt = Date()
                } label: {
                    Label(PROPOSED_EveryoneConfirmed, systemImage: "checkmark.seal")
                }
                .buttonStyle(.dictumSecondary)
                .accessibilityLabel(PROPOSED_EveryoneConfirmed)
            }
            Text(PROPOSED_ConfirmedNote)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
        }
    }
}
