import MessageUI
import SwiftUI

// Compose the summary email. The app sends nothing: it fills in an email and hands it to the user's own mail app
// (Apple's Mail compose sheet when Mail is set up, else a mailto: link opened in whichever mail app the system picks),
// and the user checks it and sends it themselves. Recipients are typed here, kept only in this sheet's memory, and
// gone when the sheet closes; nothing is stored.

/// Subject and body built from the review. No file, no attachment: the sealed recording is not attached.
struct SummaryEmail: Equatable {
    var subject: String
    var body: String

    @MainActor
    static func make(from review: MeetingReview) -> SummaryEmail {
        let day = DateFormatter()
        day.locale = Locale(identifier: "en_AU")
        day.dateFormat = "dd/MM/yyyy"
        let subject = String(format: PROPOSED_EmailSubject, day.string(from: review.meetingDate ?? Date()))

        var lines: [String] = [PROPOSED_EmailSummaryHeading]
        let summary = review.summary.state == .done ? review.summary.summary : ""
        lines.append(summary.isEmpty ? PROPOSED_EmailNoSummary : summary)
        lines.append("")
        lines.append(PROPOSED_EmailActionsHeading)

        // Grouped by owner, owners in the order they first appear; actions with no text are left out.
        let actions = review.actions.filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        if actions.isEmpty {
            lines.append(PROPOSED_EmailNoActions)
        } else {
            var order: [String] = []
            var grouped: [String: [MeetingReview.Action]] = [:]
            for action in actions {
                let owner = action.owner.trimmingCharacters(in: .whitespacesAndNewlines)
                let key = owner.isEmpty ? PROPOSED_EmailUnassigned : owner
                if grouped[key] == nil { order.append(key) }
                grouped[key, default: []].append(action)
            }
            for (index, owner) in order.enumerated() {
                if index > 0 { lines.append("") }
                lines.append(owner)
                for action in grouped[owner] ?? [] {
                    let by = action.byWhen.trimmingCharacters(in: .whitespacesAndNewlines)
                    lines.append("- " + action.text.trimmingCharacters(in: .whitespacesAndNewlines)
                                 + (by.isEmpty ? "" : String(format: PROPOSED_EmailByWhen, by)))
                }
            }
        }
        lines.append("")
        lines.append(PROPOSED_EmailSealedLine)
        return SummaryEmail(subject: subject, body: lines.joined(separator: "\n"))
    }

    /// The same email as a mailto: link, for when Mail's compose sheet is not available. Opened on this device only.
    func mailto(to recipients: [String]) -> URL? {
        var components = URLComponents()
        components.scheme = "mailto"
        components.path = recipients.joined(separator: ",")
        components.queryItems = [URLQueryItem(name: "subject", value: subject), URLQueryItem(name: "body", value: body)]
        return components.url
    }

    /// A loose shape check (something@something.something, no spaces), enough to catch typing slips.
    static func looksLikeAddress(_ text: String) -> Bool {
        text.range(of: #"^[^\s@,;<>]+@[^\s@,;<>]+\.[^\s@,;<>]+$"#, options: .regularExpression) != nil
    }
}

struct ComposeEmailSheet: View {
    let review: MeetingReview
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @State private var recipients: [String] = []
    @State private var draft = ""
    @State private var invalid = false
    @State private var showMail = false
    @State private var mailUnavailable = false
    @FocusState private var fieldFocused: Bool

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    Text(PROPOSED_ComposeIntro)
                        .font(.body)
                        .foregroundStyle(DictumTheme.text)

                    DictumCard {
                        Text(PROPOSED_RecipientField)
                            .font(.headline)
                            .foregroundStyle(DictumTheme.text)
                        HStack(spacing: 8) {
                            TextField(PROPOSED_RecipientField, text: $draft)
                                .textFieldStyle(DictumFieldStyle())
                                .keyboardType(.emailAddress)
                                .textContentType(.emailAddress)
                                .textInputAutocapitalization(.never)
                                .autocorrectionDisabled()
                                .submitLabel(.done)
                                .focused($fieldFocused)
                                .onSubmit(add)
                                .accessibilityLabel(PROPOSED_RecipientField)
                            Button(PROPOSED_AddRecipient, action: add)
                                .buttonStyle(.dictumCompact)
                                .disabled(draft.trimmingCharacters(in: .whitespaces).isEmpty)
                                .accessibilityLabel(PROPOSED_AddRecipient)
                        }
                        if invalid {
                            StatusPill(kind: .warning, text: PROPOSED_RecipientInvalid)
                        }
                        if !recipients.isEmpty {
                            FlowRow(spacing: 8) {
                                ForEach(recipients, id: \.self) { address in
                                    Button {
                                        recipients.removeAll { $0 == address }
                                    } label: {
                                        HStack(spacing: 6) {
                                            Text(address).lineLimit(1)
                                            Image(systemName: "xmark.circle.fill").accessibilityHidden(true)
                                        }
                                    }
                                    .buttonStyle(.dictumCompact)
                                    .accessibilityLabel(String(format: PROPOSED_RemoveRecipient, address))
                                }
                            }
                        }
                    }

                    DictumCard {
                        SectionHeader(email.subject)
                        Text(email.body)
                            .font(.callout)
                            .foregroundStyle(DictumTheme.text)
                            .textSelection(.enabled)
                    }

                    if mailUnavailable {
                        StatusPill(kind: .warning, text: PROPOSED_MailUnavailable)
                    }

                    PrimaryButton(title: PROPOSED_OpenInMail, systemImage: "envelope") { openInMail() }
                }
                .padding(.vertical, 16)
                .dictumPage()
            }
            .background(DictumTheme.bg.ignoresSafeArea())
            .navigationTitle(PROPOSED_ComposeTitle)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(PROPOSED_Cancel) { close() }
                        .accessibilityLabel(PROPOSED_Cancel)
                }
            }
            .sheet(isPresented: $showMail, onDismiss: close) {
                MailComposer(email: email, recipients: recipients)
                    .ignoresSafeArea()
            }
        }
    }

    private var email: SummaryEmail { SummaryEmail.make(from: review) }

    private func add() {
        let address = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !address.isEmpty else { return }
        guard SummaryEmail.looksLikeAddress(address) else {
            invalid = true
            return
        }
        invalid = false
        if !recipients.contains(address) { recipients.append(address) }
        draft = ""
        fieldFocused = true
    }

    private func openInMail() {
        if !draft.trimmingCharacters(in: .whitespaces).isEmpty { add() }
        mailUnavailable = false
        if MFMailComposeViewController.canSendMail() {
            showMail = true
        } else if let url = email.mailto(to: recipients) {
            openURL(url) { accepted in
                if accepted { close() } else { mailUnavailable = true }
            }
        } else {
            mailUnavailable = true
        }
    }

    /// Recipients live only in this sheet; they are dropped here and when the sheet goes.
    private func close() {
        recipients = []
        draft = ""
        dismiss()
    }
}

/// Apple's Mail compose sheet, filled in. The user sends (or cancels) from it; the app is only told it closed.
struct MailComposer: UIViewControllerRepresentable {
    let email: SummaryEmail
    let recipients: [String]
    @Environment(\.dismiss) private var dismiss

    func makeCoordinator() -> Coordinator { Coordinator(dismiss: { dismiss() }) }

    func makeUIViewController(context: Context) -> MFMailComposeViewController {
        let mail = MFMailComposeViewController()
        mail.mailComposeDelegate = context.coordinator
        mail.setToRecipients(recipients)
        mail.setSubject(email.subject)
        mail.setMessageBody(email.body, isHTML: false)
        return mail
    }

    func updateUIViewController(_ controller: MFMailComposeViewController, context: Context) {}

    final class Coordinator: NSObject, MFMailComposeViewControllerDelegate {
        let dismiss: () -> Void

        init(dismiss: @escaping () -> Void) { self.dismiss = dismiss }

        func mailComposeController(_ controller: MFMailComposeViewController, didFinishWith result: MFMailComposeResult,
                                   error: Error?) {
            dismiss()
        }
    }
}
