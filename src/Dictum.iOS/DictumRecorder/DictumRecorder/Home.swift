import SwiftUI
import UniformTypeIdentifiers

// The home screen: New meeting, and the meetings: each sealed `.dictum` in Documents. The list is built without any
// prompt or decryption, from the files' names, dates and sizes and their plaintext manifests (meeting id, audio length),
// plus which meeting ids have a kept key. A meeting's title is encrypted, so a row shows it only after that meeting has
// been opened with Face ID or the passcode (kept in memory until the app leaves the foreground). A saved meeting opens
// after Face ID or the passcode; a sealed file with no kept key can be unlocked with its six words. Delete removes the
// file, and for a saved meeting its record and key, after a confirmation.

/// One sealed file in Documents.
struct SealedFile: Identifiable, Equatable {
    let url: URL
    let bytes: Int64
    let modified: Date?

    var id: URL { url }
    var name: String { url.lastPathComponent }

    /// The local start date the package writes into the name (`meeting-yyyy-MM-dd-<hex>.dictum`), else the file date.
    var date: Date? {
        let parts = url.deletingPathExtension().lastPathComponent.split(separator: "-")
        if parts.count >= 4, parts[0] == "meeting" {
            let f = DateFormatter()
            f.calendar = Calendar(identifier: .gregorian)
            f.locale = Locale(identifier: "en_US_POSIX")
            f.timeZone = .current
            f.dateFormat = "yyyy-MM-dd"
            if let day = f.date(from: parts[1...3].joined(separator: "-")) { return day }
        }
        return modified
    }

    /// DD/MM/YYYY, then the file's time when it has one.
    var dateText: String {
        guard let date else { return "" }
        let day = DateFormatter()
        day.locale = Locale(identifier: "en_AU")
        day.dateFormat = "dd/MM/yyyy"
        var text = day.string(from: date)
        if let modified {
            let time = DateFormatter()
            time.locale = Locale(identifier: "en_AU")
            time.timeStyle = .short
            time.dateStyle = .none
            text += " · " + time.string(from: modified)
        }
        return text
    }

    /// Decimal megabytes, one decimal place, as the sealed screen shows.
    var sizeText: String { String(format: "%.1f MB", Double(bytes) / 1_000_000) }
}

enum SealedFiles {
    /// Every regular `.dictum` file directly in Documents, newest first. Session folders (`.session-…`) are not files.
    static func list() -> [SealedFile] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        let items = (try? FileManager.default.contentsOfDirectory(at: SessionFiles.documents, includingPropertiesForKeys: keys,
                                                                  options: [.skipsHiddenFiles])) ?? []
        return items.compactMap { url -> SealedFile? in
            guard url.pathExtension == "dictum",
                  let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else { return nil }
            return SealedFile(url: url, bytes: Int64(values.fileSize ?? 0), modified: values.contentModificationDate)
        }
        .sorted { ($0.modified ?? .distantPast) > ($1.modified ?? .distantPast) }
    }

    static func delete(_ file: SealedFile) -> Bool {
        do {
            try FileManager.default.removeItem(at: file.url)
            return true
        } catch {
            return false
        }
    }
}

struct HomeView: View {
    @Bindable var controller: RecordingController
    @State private var entries: [LibraryEntry] = []
    @State private var pendingDelete: LibraryEntry?
    @State private var deleteFailed = false
    @State private var showSettings = false
    @State private var unlocking: LibraryEntry?
    @State private var opening = false
    @State private var choosingFile = false

    private var keep: Bool { controller.settings.keepOnDevice }

    var body: some View {
        DictumScreen {
            Button { showSettings = true } label: {
                Image(systemName: "gearshape")
                    .font(.title3)
            }
            .buttonStyle(.dictumCompact)
            .accessibilityLabel(PROPOSED_SettingsButton)
            .accessibilityIdentifier("home.settings")
        } content: {
            // The heading, today's date, then the proof-of-concept notice: it stays on the first screenful.
            VStack(alignment: .leading, spacing: 6) {
                Text(keep ? PROPOSED_Meetings : PROPOSED_SealedRecordings)
                    .dictumTitle()
                    .accessibilityAddTraits(.isHeader)
                Text(Date.now, format: .dateTime.weekday(.wide).day().month(.wide))
                    .font(.subheadline)
                    .foregroundStyle(DictumTheme.mutedStrong)
                Label(PROPOSED_ProofOfConcept, systemImage: "info.circle")
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.mutedStrong)
                    .padding(.top, 4)
            }

            VStack(alignment: .leading, spacing: 6) {
                PrimaryButton(title: PROPOSED_NewMeeting, systemImage: "mic.fill") { controller.newMeetingTapped() }
                    .accessibilityIdentifier("home.newMeeting")
                ViewThatFits(in: .horizontal) {
                    // Side by side only when both fit on one line each; otherwise stacked, never squeezed.
                    HStack(spacing: 12) {
                        homeCaption.fixedSize()
                        Spacer(minLength: 8)
                        importButton.fixedSize()
                    }
                    VStack(alignment: .leading, spacing: 0) {
                        homeCaption
                        importButton
                    }
                }
            }

            if let message = controller.homeMessage {
                StatusPill(kind: .warning, text: message)
            }

            if entries.isEmpty {
                DictumCard {
                    HStack(alignment: .center, spacing: 14) {
                        DictumLevelMeter(height: 56)
                            .accessibilityHidden(true)
                        Text(keep ? PROPOSED_NoMeetings : PROPOSED_NoRecordings)
                            .font(.callout)
                            .foregroundStyle(DictumTheme.muted)
                    }
                }
            } else {
                // One card of rows separated by hairlines.
                DictumCard(padding: 0, spacing: 0) {
                    ForEach(Array(entries.enumerated()), id: \.element.id) { index, entry in
                        row(entry)
                        if index < entries.count - 1 {
                            Divider().overlay(DictumTheme.border).padding(.leading, 18)
                        }
                    }
                }
            }

            if deleteFailed {
                StatusPill(kind: .warning, text: PROPOSED_DeleteFailed)
            }
        }
        .onAppear(perform: refresh)
        // A sound file picked in Files. It is read where it is; nothing is copied until it is sealed.
        .fileImporter(isPresented: $choosingFile, allowedContentTypes: [.audio]) { result in
            if case let .success(url) = result { Task { await controller.openedFile(url) } }
        }
        .confirmationDialog(pendingDelete.map(deleteTitle) ?? "",
                            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
                            titleVisibility: .visible, presenting: pendingDelete) { entry in
            Button(entry.saved ? PROPOSED_DeleteMeetingConfirm : PROPOSED_DeleteConfirm, role: .destructive) {
                deleteFailed = !Library.delete(url: entry.file.url, meetingHex: entry.saved ? entry.meetingHex : nil)
                pendingDelete = nil
                refresh()
            }
            Button(PROPOSED_Cancel, role: .cancel) { pendingDelete = nil }
        } message: { entry in
            Text(entry.saved ? PROPOSED_DeleteMeetingMessage : PROPOSED_DeleteConfirmMessage)
        }
        .sheet(isPresented: $showSettings, onDismiss: refresh) {
            SettingsView(settings: controller.settings, transcript: controller.transcript)
        }
        .sheet(item: $unlocking, onDismiss: refresh) { entry in
            UnlockSheet(entry: entry, controller: controller)
        }
    }

    private func refresh() { entries = Library.entries() }

    private func deleteTitle(_ entry: LibraryEntry) -> String {
        entry.saved ? PROPOSED_DeleteMeetingTitle : String(format: PROPOSED_DeleteConfirmTitle, entry.file.name)
    }

    private func title(_ entry: LibraryEntry) -> String {
        entry.meetingHex.flatMap { controller.openedTitles[$0] } ?? String(format: PROPOSED_MeetingOn, entry.file.dateText)
    }

    private func durationText(_ entry: LibraryEntry) -> String? {
        entry.durationEstimate.map { String(format: PROPOSED_DurationAbout, RecordingView.elapsed(seconds: $0)) }
    }

    /// The date only when the title is a real one (the placeholder title already carries it).
    private func subtitle(_ entry: LibraryEntry) -> String {
        let titled = entry.meetingHex.flatMap { controller.openedTitles[$0] } != nil
        return ((titled ? [entry.file.dateText] : []) + [durationText(entry)].compactMap { $0 } + [entry.file.sizeText])
            .joined(separator: " · ")
    }

    private var homeCaption: some View {
        Text(PROPOSED_HomeCaption)
            .font(.footnote)
            .foregroundStyle(DictumTheme.mutedStrong)
    }

    private var importButton: some View {
        Button { choosingFile = true } label: {
            Label(PROPOSED_ImportButton, systemImage: "square.and.arrow.down")
        }
        .buttonStyle(.dictumText)
        .accessibilityLabel(PROPOSED_ImportButton)
        .accessibilityIdentifier("home.import")
    }

    /// The row's own action: open a saved meeting, or unlock a sealed one with its six words.
    private enum RowAction { case open, unlock }

    private func rowAction(_ entry: LibraryEntry) -> RowAction? {
        if entry.saved { return .open }
        if keep, entry.meetingHex != nil { return .unlock }
        return nil
    }

    /// The whole row is the button for its own action; share and delete are in the trailing "more" menu.
    private func row(_ entry: LibraryEntry) -> some View {
        HStack(alignment: .center, spacing: 4) {
            primary(entry)
            moreMenu(entry)
        }
        .padding(.leading, 18)
        .padding(.trailing, 6)
        .padding(.vertical, 12)
    }

    private func details(_ entry: LibraryEntry, action: RowAction?) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title(entry))
                .font(.dictumDisplay(.title3).monospacedDigit())
                .foregroundStyle(DictumTheme.text)
                .multilineTextAlignment(.leading)
            Text(subtitle(entry))
                .font(.footnote.monospacedDigit())
                .foregroundStyle(DictumTheme.muted)
                .multilineTextAlignment(.leading)
            StatusPill(kind: entry.saved ? .done : .warning, text: entry.saved ? PROPOSED_StateSaved : PROPOSED_StateCodeNeeded)
            if action == .unlock {
                Label(PROPOSED_UnlockWithWords, systemImage: "key")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(DictumTheme.link)
                    .multilineTextAlignment(.leading)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
    }

    @ViewBuilder private func primary(_ entry: LibraryEntry) -> some View {
        let label = String(format: PROPOSED_MeetingRow, title(entry), entry.file.dateText, durationText(entry) ?? "",
                           entry.file.sizeText, entry.saved ? PROPOSED_StateSaved : PROPOSED_StateCodeNeeded)
        switch rowAction(entry) {
        case .open:
            Button {
                guard !opening else { return }
                opening = true
                Task {
                    await controller.openMeeting(entry)
                    opening = false
                }
            } label: {
                details(entry, action: .open)
            }
            .buttonStyle(RowButtonStyle())
            .disabled(opening)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(label)
            .accessibilityHint(PROPOSED_OpenMeeting)
            .accessibilityAddTraits(.isButton)
            .accessibilityIdentifier("home.meeting")
        case .unlock:
            Button { unlocking = entry } label: {
                details(entry, action: .unlock)
            }
            .buttonStyle(RowButtonStyle())
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(PROPOSED_UnlockWithWords)
            .accessibilityValue(label)
            .accessibilityAddTraits(.isButton)
        case nil:
            details(entry, action: nil)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(label)
        }
    }

    /// Share and delete, with the same labels and the same delete confirmation as before.
    private func moreMenu(_ entry: LibraryEntry) -> some View {
        Menu {
            ShareLink(item: entry.file.url) {
                Label(PROPOSED_ShareRecording, systemImage: "square.and.arrow.up")
            }
            .accessibilityLabel(PROPOSED_ShareRecording)
            Button(role: .destructive) { pendingDelete = entry } label: {
                Label(entry.saved ? PROPOSED_DeleteMeeting : PROPOSED_DeleteRecording, systemImage: "trash")
            }
            .accessibilityLabel(entry.saved ? PROPOSED_DeleteMeeting : PROPOSED_DeleteRecording)
        } label: {
            Image(systemName: "ellipsis")
                .font(.body.weight(.semibold))
                .foregroundStyle(DictumTheme.text)
                .frame(minWidth: DictumTheme.minTarget, minHeight: DictumTheme.minTarget)
                .contentShape(Rectangle())
        }
        .accessibilityLabel(PROPOSED_RowMoreActions)
        .accessibilityIdentifier("home.meetingMore")
    }
}

/// A list row pressed: a faint wash, no other change.
private struct RowButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(configuration.isPressed ? DictumTheme.accentSoft.opacity(0.6) : .clear,
                        in: RoundedRectangle(cornerRadius: DictumTheme.radiusControl, style: .continuous))
            .opacity(enabled ? 1 : 0.6)
    }
}

/// The six words for a sealed file with no kept key. The words live only in this sheet and are dropped when it closes.
struct UnlockSheet: View {
    let entry: LibraryEntry
    let controller: RecordingController
    @Environment(\.dismiss) private var dismiss
    @State private var code = ""
    @State private var working = false
    @State private var message: String?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    Text(PROPOSED_UnlockIntro)
                        .font(.body)
                        .foregroundStyle(DictumTheme.text)
                    Text(entry.file.name)
                        .font(.footnote.monospacedDigit())
                        .foregroundStyle(DictumTheme.text)
                    TextField(PROPOSED_UnlockField, text: $code, axis: .vertical)
                        .lineLimit(1...3)
                        .textFieldStyle(DictumFieldStyle())
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .accessibilityLabel(PROPOSED_UnlockField)
                    if working {
                        StatusPill(kind: .working, text: PROPOSED_Unlocking)
                    } else if let message {
                        StatusPill(kind: .warning, text: message)
                    }
                    PrimaryButton(title: PROPOSED_UnlockButton, systemImage: "key") { unlock() }
                        .disabled(working || code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                .padding(.vertical, 16)
                .dictumPage()
            }
            .background(DictumTheme.bg.ignoresSafeArea())
            .navigationTitle(PROPOSED_UnlockTitle)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(PROPOSED_Cancel) { close() }
                        .accessibilityLabel(PROPOSED_Cancel)
                }
            }
        }
    }

    private func unlock() {
        working = true
        message = nil
        let typed = code
        Task {
            let outcome = await controller.unlockWithWords(entry, code: typed)
            working = false
            if let outcome { message = outcome } else { close() }
        }
    }

    private func close() {
        code = ""
        dismiss()
    }
}
