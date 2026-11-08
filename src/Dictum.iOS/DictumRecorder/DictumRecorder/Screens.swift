import DictumBundle
import SwiftUI

// Plain screens with large touch targets. Every control has an accessibility label; text uses Dynamic Type styles;
// no meaning is carried by colour alone.

private struct WideButton: ViewModifier {
    func body(content: Content) -> some View {
        content
            .frame(maxWidth: .infinity, minHeight: 52)
            .contentShape(Rectangle())
    }
}

private extension View {
    func wide() -> some View { modifier(WideButton()) }
}

// MARK: - Screen 1: Before you record

struct ConsentView: View {
    @Bindable var controller: RecordingController
    @State private var starting = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text(APPROVED_Before)
                    .font(.largeTitle.bold())
                    .accessibilityAddTraits(.isHeader)

                Text(APPROVED_Agreement)
                    .font(.body)

                VStack(alignment: .leading, spacing: 12) {
                    ForEach(PROPOSED_Points, id: \.self) { point in
                        HStack(alignment: .firstTextBaseline, spacing: 10) {
                            Text("•").accessibilityHidden(true)
                            Text(point)
                        }
                        .accessibilityElement(children: .combine)
                    }
                }

                Text(APPROVED_How)
                    .font(.headline)
                    .accessibilityAddTraits(.isHeader)
                // A segmented control truncates these approved labels on an iPhone, so the two choices are full-width
                // rows; each shows a filled or empty circle and announces itself as selected.
                VStack(spacing: 8) {
                    choice(APPROVED_Spoken, value: "spoken")
                    choice(APPROVED_Written, value: "written")
                }

                VStack(alignment: .leading, spacing: 6) {
                    Text(PROPOSED_TitleLabel).font(.headline)
                    TextField(PROPOSED_TitleLabel, text: $controller.title)
                        .textFieldStyle(.roundedBorder)
                        .font(.body)
                        .submitLabel(.done)
                        .accessibilityLabel(PROPOSED_TitleLabel)
                }

                if controller.micDenied {
                    VStack(alignment: .leading, spacing: 10) {
                        Label(PROPOSED_MicDenied, systemImage: "mic.slash")
                            .font(.body.weight(.semibold))
                        Button(PROPOSED_OpenSettings) { controller.openSettings() }
                            .buttonStyle(.bordered)
                            .controlSize(.large)
                            .accessibilityLabel(PROPOSED_OpenSettings)
                    }
                    .padding()
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
                }

                Button {
                    starting = true
                    Task {
                        await controller.startTapped()
                        starting = false
                    }
                } label: {
                    Text(APPROVED_Start)
                        .font(.headline)
                        .multilineTextAlignment(.center)
                        .wide()
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(starting)
                .accessibilityLabel(APPROVED_Start)
            }
            .padding(20)
        }
    }

    private func choice(_ label: String, value: String) -> some View {
        let selected = controller.how == value
        return Button {
            controller.how = value
        } label: {
            HStack(spacing: 12) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .font(.title2)
                    .accessibilityHidden(true)
                Text(label)
                    .font(.body)
                    .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 14)
            .wide()
            .background(RoundedRectangle(cornerRadius: 12).strokeBorder(selected ? Color.accentColor : Color.secondary,
                                                                        lineWidth: selected ? 3 : 1))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }
}

// MARK: - Screen 2: Recording

struct RecordingView: View {
    @Bindable var controller: RecordingController

    private var sealing: Bool {
        if case .sealing = controller.phase { return true }
        return false
    }

    var body: some View {
        VStack(spacing: 28) {
            Spacer()
            RecordingIndicator()

            if let start = controller.startedAt {
                TimelineView(.periodic(from: start, by: 1)) { context in
                    let text = Self.elapsed(from: start, to: context.date)
                    Text(text)
                        .font(.system(.largeTitle, design: .rounded).monospacedDigit())
                        .accessibilityLabel(PROPOSED_ElapsedLabel)
                        .accessibilityValue(text)
                }
            }

            Text(controller.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? PROPOSED_DefaultTitle : controller.title)
                .font(.title2)
                .multilineTextAlignment(.center)

            if controller.captureStatus != .running {
                VStack(spacing: 12) {
                    Label(PROPOSED_Interrupted, systemImage: "pause.circle")
                        .font(.body.weight(.semibold))
                        .multilineTextAlignment(.center)
                    if controller.captureStatus == .needsUser {
                        Button { controller.resumeTapped() } label: { Text(PROPOSED_Resume).font(.headline).wide() }
                        .buttonStyle(.bordered)
                        .controlSize(.large)
                        .accessibilityLabel(PROPOSED_Resume)
                    }
                }
            }
            Spacer()

            Button {
                Task { await controller.stopTapped() }
            } label: {
                Group {
                    if sealing {
                        HStack(spacing: 10) {
                            ProgressView()
                            Text(PROPOSED_Sealing)
                        }
                    } else {
                        Label(PROPOSED_Stop, systemImage: "stop.fill")
                    }
                }
                .font(.title2.bold())
                .frame(maxWidth: .infinity, minHeight: 72)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(sealing)
            .accessibilityLabel(sealing ? PROPOSED_Sealing : PROPOSED_Stop)
        }
        .padding(20)
    }

    static func elapsed(from start: Date, to now: Date) -> String {
        let total = max(0, Int(now.timeIntervalSince(start)))
        return String(format: "%d:%02d:%02d", total / 3600, (total / 60) % 60, total % 60)
    }
}

/// Red pulsing dot plus the word "Recording", so the state is never shown by colour alone.
struct RecordingIndicator: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var pulse = false

    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(Color.red)
                .frame(width: 28, height: 28)
                .scaleEffect(pulse && !reduceMotion ? 1.25 : 1)
                .opacity(pulse && !reduceMotion ? 0.55 : 1)
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.9).repeatForever(autoreverses: true), value: pulse)
            Text(PROPOSED_RecordingIndicator)
                .font(.title.bold())
        }
        .onAppear { pulse = true }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PROPOSED_RecordingIndicator)
    }
}

// MARK: - Screen 3: Sealed

struct SealedView: View {
    let bundle: FinishedBundle
    let captureFailed: Bool
    let done: () -> Void

    /// Decimal megabytes (1 MB = 1 000 000 bytes, as Files shows), one decimal place, from the file's byte count.
    private var size: String { String(format: "%.1f MB", Double(bundle.bytes) / 1_000_000) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Label(PROPOSED_SealedHeading, systemImage: "lock.fill")
                    .font(.largeTitle.bold())
                    .accessibilityAddTraits(.isHeader)

                if bundle.incomplete {
                    Label(PROPOSED_IncompleteNote, systemImage: "exclamationmark.triangle")
                        .font(.body.weight(.semibold))
                }
                if captureFailed {
                    Label(PROPOSED_CaptureFailed, systemImage: "exclamationmark.triangle")
                        .font(.body.weight(.semibold))
                }

                LabeledContent(PROPOSED_FileLabel, value: bundle.url.lastPathComponent)
                LabeledContent(PROPOSED_SizeLabel, value: size)

                VStack(alignment: .leading, spacing: 8) {
                    Text(PROPOSED_CodeLabel).font(.headline)
                    Text(bundle.transferCode)
                        .font(.system(.title, design: .monospaced).weight(.semibold))
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityLabel(PROPOSED_CodeLabel)
                        .accessibilityValue(bundle.transferCode)
                    Text(PROPOSED_CodeHint)
                }

                ShareLink(item: bundle.url) {
                    Label(PROPOSED_Share, systemImage: "square.and.arrow.up")
                        .font(.headline)
                        .wide()
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .accessibilityLabel(PROPOSED_Share)

                Text(PROPOSED_FilesHint)
                    .font(.footnote)

                Button(action: done) { Text(PROPOSED_Done).font(.headline).wide() }
                    .buttonStyle(.bordered)
                    .controlSize(.large)
                    .accessibilityLabel(PROPOSED_Done)
            }
            .padding(20)
        }
    }
}

// MARK: - Screen 4: Recovered

struct RecoverView: View {
    let seal: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Label(PROPOSED_RecoverHeading, systemImage: "exclamationmark.triangle")
                    .font(.title.bold())
                    .accessibilityAddTraits(.isHeader)
                Text(PROPOSED_RecoverBody)
                Button(action: seal) { Text(PROPOSED_RecoverSeal).font(.headline).wide() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .accessibilityLabel(PROPOSED_RecoverSeal)
            }
            .padding(20)
        }
    }
}

// MARK: - Errors

struct FailedView: View {
    let message: String
    let back: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Label(PROPOSED_FailedHeading, systemImage: "xmark.octagon")
                    .font(.title.bold())
                    .accessibilityAddTraits(.isHeader)
                Text(message)
                Button(action: back) { Text(PROPOSED_BackToStart).font(.headline).wide() }
                    .buttonStyle(.bordered)
                    .controlSize(.large)
                    .accessibilityLabel(PROPOSED_BackToStart)
            }
            .padding(20)
        }
    }
}
