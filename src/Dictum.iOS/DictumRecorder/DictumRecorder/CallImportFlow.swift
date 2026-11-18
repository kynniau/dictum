import DictumBundle
import SwiftUI

// Bringing in a recording, the part with the bundle and the screen (the rules and the sound are in CallImport.swift).
// The file becomes an ordinary sealed meeting: the same session, the same encrypted file, the same six words. Only its
// recorded agreement differs: Import kind, with the notice confirmed on the screen below.

/// A sound file waiting on the "Before you bring in a recording" screen.
struct ImportDraft {
    let url: URL
    /// True when the system gave access to a file outside the app (picked in Files); released by `release()`.
    let scoped: Bool
    let seconds: TimeInterval

    /// Ends access to the file and removes it when it is the copy the share menu handed over. The user's own file is
    /// never removed (ImportInbox.remove).
    func release() {
        if scoped { url.stopAccessingSecurityScopedResource() }
        ImportInbox.remove(url, documents: SessionFiles.documents)
    }
}

enum CallImporter {
    /// Seals the sound file at `url` as a meeting. Blocking: call off the main thread. The key must already be in
    /// ContentKeyStore, as for a live recording, so an import cut short is offered for sealing at the next launch.
    /// On a throw the caller removes the session folder and the key.
    static func seal(_ url: URL, id: MeetingId, key: Data, title: String, how: String, keep: Bool,
                     appVersion: String) throws -> FinishedBundle {
        guard ImportNotice.hows.contains(how) else { throw ImportAudio.Failure.unreadable }
        let now = Date()
        let version = ImportNotice.version(keep: keep)
        let agreement = Agreement(acknowledgedAt: now, how: how, scriptVersion: version, kind: ImportNotice.kind)
        let confirmation = Confirmation(answer: how, at: now, kind: ImportNotice.kind, scriptVersion: version,
                                        script: ImportNotice.script(keep: keep), importerIdentity: nil, route: nil)
        let directory = try SessionFiles.makeSessionDirectory(hex: id.hex)
        let session = try RecordingSession(directory: directory, meetingId: id, contentKey: key,
                                           startedAt: ImportAudio.created(url) ?? now, title: title,
                                           agreement: agreement, confirmations: [confirmation], appVersion: appVersion)
        SessionFiles.protect(directory)
        let samples = try ImportAudio.decode(url: url) { try session.append(pcm16Mono16k: $0) }
        guard samples > 0 else { throw ImportAudio.Failure.noSound }
        return try session.finish(duration: TimeInterval(samples) / 16_000, at: now, transferCode: TestMode.sealingCode())
    }
}

/// One full-width choice with a filled or empty circle; announces itself as selected. Used for "How did everyone
/// agree?" on the consent screen and on the bring-in screen (a segmented control truncates the approved labels).
struct AgreementChoiceRow: View {
    let label: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .font(.title2)
                    .foregroundStyle(selected ? DictumTheme.link : DictumTheme.text)
                    .accessibilityHidden(true)
                Text(label)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
                    .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 14)
            .frame(maxWidth: .infinity, minHeight: 52)
            .background(selected ? DictumTheme.accentSoft : DictumTheme.surface,
                        in: RoundedRectangle(cornerRadius: DictumTheme.radiusCard))
            .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusCard)
                .strokeBorder(selected ? DictumTheme.accent : DictumTheme.borderStrong, lineWidth: selected ? 2 : 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }
}

/// "Before you bring in a recording": the notice, how everyone agreed (nothing is chosen for the user), a title, and
/// the confirming button. Back leaves the file as it was.
struct ImportView: View {
    @Bindable var controller: RecordingController
    @State private var how: String?

    private var keep: Bool { controller.settings.keepOnDevice }

    var body: some View {
        DictumScreen {
            Button { controller.importBackTapped() } label: {
                Label(PROPOSED_Back, systemImage: "chevron.left")
            }
            .buttonStyle(.dictumCompact)
            .accessibilityLabel(PROPOSED_Back)
        } content: {
            Text(PROPOSED_ImportHeading)
                .dictumTitle()
                .accessibilityAddTraits(.isHeader)

            if let draft = controller.importDraft {
                Text(String(format: PROPOSED_ImportLength, ImportAudio.lengthText(draft.seconds)))
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
            }

            DictumCard {
                Text(PROPOSED_ImportAgreement)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)

                VStack(alignment: .leading, spacing: 12) {
                    ForEach(Array(ImportNotice.points(keep: keep).enumerated()), id: \.offset) { index, point in
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text("\(index + 1)")
                                .font(.subheadline.weight(.semibold).monospacedDigit())
                                .foregroundStyle(DictumTheme.text)
                                .frame(minWidth: 26, minHeight: 26)
                                .background(DictumTheme.accentSoft, in: Circle())
                                .accessibilityHidden(true)
                            Text(point)
                                .font(.body)
                                .foregroundStyle(DictumTheme.text)
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
            }

            SectionHeader(APPROVED_How, style: .heading)
            VStack(spacing: 8) {
                ForEach(ImportNotice.hows, id: \.self) { value in
                    AgreementChoiceRow(label: ImportNotice.label(how: value), selected: how == value) { how = value }
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text(PROPOSED_ImportTitleLabel)
                    .font(.headline)
                    .foregroundStyle(DictumTheme.text)
                TextField(PROPOSED_ImportTitleLabel, text: $controller.title)
                    .textFieldStyle(DictumFieldStyle())
                    .submitLabel(.done)
                    .accessibilityLabel(PROPOSED_ImportTitleLabel)
            }

            Button {
                if let how { Task { await controller.importTapped(how: how) } }
            } label: {
                Text(PROPOSED_ImportConfirm)
            }
            .buttonStyle(.dictumPrimary)
            .disabled(how == nil)
            .accessibilityLabel(PROPOSED_ImportConfirm)
            .accessibilityIdentifier("import.confirm")

            if how == nil {
                Text(PROPOSED_ImportChooseHow)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.text)
            }
        }
    }
}
