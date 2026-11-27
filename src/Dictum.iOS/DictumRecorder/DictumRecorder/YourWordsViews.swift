import SwiftUI

// The two places "Your words" fixes are made (rules: YourWords.swift; the kept list: YourWordsStore.swift):
// Settings > Your words, a list of pairs typed by hand, and "Fix this line" in a kept meeting, where the user corrects
// a transcript line and may choose to have the same fix made from then on.

// MARK: - Settings > Your words

struct YourWordsView: View {
    private enum Field { case heard, meant, hints }

    @State private var fixes: [WordFix] = []
    @State private var heard = ""
    @State private var meant = ""
    @State private var ask = false
    @State private var hints = ""
    @State private var message: String?
    @FocusState private var focus: Field?

    private var draft: WordFix? {
        YourWords.tidy(WordFix(heard: heard, meant: meant, ask: ask, hints: ask ? YourWords.hints(from: hints) : nil))
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text(PROPOSED_YourWordsIntro)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
                if let message {
                    StatusPill(kind: .warning, text: message)
                }
                addCard
                listCard
                Text(PROPOSED_YourWordsPrivacy)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }
            .padding(.vertical, 12)
            .dictumPage()
        }
        .background(DictumTheme.bg.ignoresSafeArea())
        .navigationTitle(PROPOSED_YourWordsTitle)
        .navigationBarTitleDisplayMode(.inline)
        .task { fixes = await YourWordsStore.current() }
    }

    private var addCard: some View {
        DictumCard {
            field(PROPOSED_YourWordsHeardField, text: $heard, focused: .heard, submit: .next) { focus = .meant }
            field(PROPOSED_YourWordsMeantField, text: $meant, focused: .meant, submit: .done) { add() }
            Toggle(PROPOSED_YourWordsAsk, isOn: $ask)
                .tint(DictumTheme.accent)
                .frame(minHeight: DictumTheme.minTarget)
                .accessibilityHint(PROPOSED_YourWordsAskNote)
            Text(PROPOSED_YourWordsAskNote)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
            if ask {
                field(PROPOSED_YourWordsHintsField, text: $hints, focused: .hints, submit: .done) { add() }
                Text(PROPOSED_YourWordsHintsNote)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }
            PrimaryButton(title: PROPOSED_YourWordsAdd, systemImage: "plus") { add() }
                .disabled(draft == nil)
            Text(PROPOSED_YourWordsRisk)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)
        }
    }

    /// A labelled field. Autocorrection and automatic capitals are off: names and terms are typed exactly as wanted.
    private func field(_ label: String, text: Binding<String>, focused: Field, submit: SubmitLabel,
                       onSubmit: @escaping () -> Void) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(.headline).foregroundStyle(DictumTheme.text)
            TextField(label, text: text)
                .textFieldStyle(DictumFieldStyle())
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(submit)
                .focused($focus, equals: focused)
                .onSubmit(onSubmit)
                .accessibilityLabel(label)
        }
    }

    private var listCard: some View {
        DictumCard {
            SectionHeader(PROPOSED_YourWordsListHeading)
            if fixes.isEmpty {
                Text(PROPOSED_YourWordsEmpty)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.text)
            }
            ForEach(Array(fixes.enumerated()), id: \.offset) { index, fix in
                HStack(alignment: .center, spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(fix.heard).font(.subheadline).foregroundStyle(DictumTheme.muted)
                        Label(fix.meant, systemImage: "arrow.turn.down.right")
                            .font(.body.weight(.semibold))
                            .foregroundStyle(DictumTheme.text)
                        if let offered = Self.offered(fix) {
                            Text(offered).font(.footnote).foregroundStyle(DictumTheme.muted)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel([String(format: PROPOSED_YourWordsPair, fix.heard, fix.meant), Self.offered(fix)]
                        .compactMap { $0 }.joined(separator: ". "))
                    Button(role: .destructive) { remove(index) } label: {
                        Image(systemName: "trash")
                            .frame(minWidth: DictumTheme.minTarget, minHeight: DictumTheme.minTarget)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(DictumTheme.link)
                    .accessibilityLabel(String(format: PROPOSED_YourWordsRemove, fix.heard))
                }
                .frame(minHeight: DictumTheme.minTarget)
            }
        }
    }

    private func add() {
        guard let draft else { return }
        guard let next = YourWords.adding(draft, to: fixes) else {
            message = PROPOSED_YourWordsFull
            return
        }
        Task {
            guard await keep(next) else { return }
            heard = ""
            meant = ""
            hints = ""
            focus = .heard
        }
    }

    /// For a fix that is offered, not made: when it is offered. Nil for a fix made every time.
    private static func offered(_ fix: WordFix) -> String? {
        guard fix.ask == true else { return nil }
        let hints = fix.hints ?? []
        return hints.isEmpty ? PROPOSED_YourWordsAsksAlways : String(format: PROPOSED_YourWordsAsksWhen, hints.joined(separator: ", "))
    }

    private func remove(_ index: Int) {
        guard fixes.indices.contains(index) else { return }
        var next = fixes
        next.remove(at: index)
        Task { _ = await keep(next) }
    }

    /// Saves first; the list on screen changes only when the phone has kept it.
    private func keep(_ next: [WordFix]) async -> Bool {
        guard await YourWordsStore.replace(next) else {
            message = PROPOSED_YourWordsSaveFailed
            return false
        }
        fixes = next
        message = nil
        return true
    }
}

// MARK: - Fix this line (a kept meeting)

/// The transcript line being fixed.
struct FixTarget: Identifiable {
    let index: Int
    let segment: MeetingRecord.Segment
    var id: Int { index }
}

/// One transcript line, editable. When the edit swaps some words for others, "Always fix this" offers to keep that swap
/// in Your words; it is off until the user turns it on, and says exactly what will be changed from then on.
struct FixLineSheet: View {
    let heading: String
    let original: String
    /// The line's new words, and the fix to keep when "Always fix this" is on.
    let save: (_ text: String, _ always: WordFix?) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var text: String
    @State private var always = false
    @FocusState private var focused: Bool

    init(heading: String, original: String, save: @escaping (_ text: String, _ always: WordFix?) -> Void) {
        self.heading = heading
        self.original = original
        self.save = save
        _text = State(initialValue: original)
    }

    private static func tidy(_ s: String) -> String { s.split(whereSeparator: \.isWhitespace).joined(separator: " ") }
    private var fix: WordFix? { YourWords.changed(from: original, to: text) }
    private var canSave: Bool { !Self.tidy(text).isEmpty && Self.tidy(text) != Self.tidy(original) }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    DictumCard {
                        if !heading.isEmpty {
                            Text(heading)
                                .font(.subheadline.monospacedDigit())
                                .foregroundStyle(DictumTheme.muted)
                        }
                        Text(PROPOSED_FixLineField).font(.headline).foregroundStyle(DictumTheme.text)
                        TextField(PROPOSED_FixLineField, text: $text, axis: .vertical)
                            .lineLimit(2...12)
                            .textFieldStyle(DictumFieldStyle())
                            .autocorrectionDisabled()
                            .focused($focused)
                            .accessibilityLabel(PROPOSED_FixLineField)
                        Text(PROPOSED_FixLineNote)
                            .font(.footnote)
                            .foregroundStyle(DictumTheme.muted)
                    }
                    if let fix {
                        DictumCard {
                            Toggle(PROPOSED_FixAlways, isOn: $always)
                                .tint(DictumTheme.accent)
                                .frame(minHeight: DictumTheme.minTarget)
                                .accessibilityLabel(PROPOSED_FixAlways)
                                .accessibilityHint(String(format: PROPOSED_FixAlwaysNote, fix.heard, fix.meant))
                            Text(String(format: PROPOSED_FixAlwaysNote, fix.heard, fix.meant))
                                .font(.footnote)
                                .foregroundStyle(DictumTheme.muted)
                        }
                    }
                }
                .padding(.vertical, 12)
                .dictumPage()
            }
            .background(DictumTheme.bg.ignoresSafeArea())
            .tint(DictumTheme.link)
            .navigationTitle(PROPOSED_FixLineTitle)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(PROPOSED_Cancel) { dismiss() }
                        .accessibilityLabel(PROPOSED_Cancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(PROPOSED_FixLineSave) {
                        save(text, always ? fix : nil)
                        dismiss()
                    }
                    .disabled(!canSave)
                    .accessibilityLabel(PROPOSED_FixLineSave)
                }
            }
            .onAppear { focused = true }
        }
    }
}
