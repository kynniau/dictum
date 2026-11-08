import SwiftUI

// The home screen: New meeting, and the sealed `.dictum` files already in Documents. The list is built only from those
// files' names, dates and sizes; the files are sealed, so no meeting content is read or shown here. Delete removes the
// one file after a confirmation; nothing else on disk is touched.

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
    @State private var files: [SealedFile] = []
    @State private var pendingDelete: SealedFile?
    @State private var deleteFailed = false
    @State private var showSettings = false

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
            Text(PROPOSED_ProofOfConcept)
                .font(.footnote.weight(.medium))
                .foregroundStyle(DictumTheme.text)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusControl).strokeBorder(DictumTheme.borderStrong, lineWidth: 1))

            PrimaryButton(title: PROPOSED_NewMeeting, systemImage: "mic.fill") { controller.newMeetingTapped() }
                .font(.title3)
                .accessibilityIdentifier("home.newMeeting")

            SectionHeader(PROPOSED_SealedRecordings)

            if files.isEmpty {
                DictumCard {
                    HStack(alignment: .center, spacing: 14) {
                        DictumLevelMeter(height: 56)
                            .accessibilityHidden(true)
                        Text(PROPOSED_NoRecordings)
                            .font(.callout)
                            .foregroundStyle(DictumTheme.muted)
                    }
                }
            } else {
                VStack(spacing: 10) {
                    ForEach(files) { file in row(file) }
                }
            }

            if deleteFailed {
                StatusPill(kind: .warning, text: PROPOSED_DeleteFailed)
            }
        }
        .onAppear(perform: refresh)
        .confirmationDialog(pendingDelete.map { String(format: PROPOSED_DeleteConfirmTitle, $0.name) } ?? "",
                            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
                            titleVisibility: .visible, presenting: pendingDelete) { file in
            Button(PROPOSED_DeleteConfirm, role: .destructive) {
                deleteFailed = !SealedFiles.delete(file)
                pendingDelete = nil
                refresh()
            }
            Button(PROPOSED_Cancel, role: .cancel) { pendingDelete = nil }
        } message: { _ in
            Text(PROPOSED_DeleteConfirmMessage)
        }
        .sheet(isPresented: $showSettings) {
            SettingsView(settings: controller.settings, transcript: controller.transcript)
        }
    }

    private func refresh() { files = SealedFiles.list() }

    private func row(_ file: SealedFile) -> some View {
        DictumCard(padding: 14) {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 12) {
                    details(file)
                    Spacer(minLength: 8)
                    actions(file)
                }
                VStack(alignment: .leading, spacing: 10) {
                    details(file)
                    actions(file)
                }
            }
        }
    }

    private func details(_ file: SealedFile) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "lock.doc")
                .font(.title2)
                .foregroundStyle(DictumTheme.link)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(file.name)
                    .font(.subheadline.weight(.semibold).monospacedDigit())
                    .foregroundStyle(DictumTheme.text)
                    .lineLimit(2)
                    .truncationMode(.middle)
                Text("\(file.dateText) · \(file.sizeText)")
                    .font(.footnote.monospacedDigit())
                    .foregroundStyle(DictumTheme.muted)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(String(format: PROPOSED_RecordingRow, file.name, file.dateText, file.sizeText))
    }

    private func actions(_ file: SealedFile) -> some View {
        HStack(spacing: 8) {
            ShareLink(item: file.url) {
                Image(systemName: "square.and.arrow.up")
            }
            .buttonStyle(.dictumCompact)
            .accessibilityLabel(PROPOSED_ShareRecording)
            Button { pendingDelete = file } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.dictumCompact)
            .accessibilityLabel(PROPOSED_DeleteRecording)
        }
    }
}
