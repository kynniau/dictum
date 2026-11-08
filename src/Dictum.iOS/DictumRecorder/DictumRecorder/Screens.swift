import DictumBundle
import SwiftUI

// The recording screens in Dictum's design language (Theme.swift). Every control has an accessibility label; text uses
// Dynamic Type styles; no meaning is carried by colour alone; targets are at least 44 pt.

/// A text field on a surface, with the site's control radius and border.
struct DictumFieldStyle: TextFieldStyle {
    func _body(configuration: TextField<Self._Label>) -> some View {
        configuration
            .font(.body)
            .foregroundStyle(DictumTheme.text)
            .padding(.horizontal, 12)
            .frame(minHeight: DictumTheme.minTarget)
            .background(DictumTheme.surface, in: RoundedRectangle(cornerRadius: DictumTheme.radiusControl))
            .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusControl).strokeBorder(DictumTheme.borderStrong, lineWidth: 1))
    }
}

/// A scrolling page on bg with the top bar, content at a readable width.
struct DictumScreen<Trailing: View, Content: View>: View {
    @ViewBuilder var trailing: Trailing
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                DictumTopBar { trailing }
                content
            }
            .padding(.vertical, 12)
            .dictumPage()
        }
        .background(DictumTheme.bg.ignoresSafeArea())
    }
}

extension DictumScreen where Trailing == EmptyView {
    init(@ViewBuilder content: () -> Content) {
        trailing = EmptyView()
        self.content = content()
    }
}

// MARK: - Screen 1: Before you record

struct ConsentView: View {
    @Bindable var controller: RecordingController
    @State private var starting = false

    var body: some View {
        DictumScreen {
            Button { controller.backToHomeTapped() } label: {
                Label(PROPOSED_Back, systemImage: "chevron.left")
            }
            .buttonStyle(.dictumCompact)
            .disabled(starting)
            .accessibilityLabel(PROPOSED_Back)
        } content: {
            Text(APPROVED_Before)
                .dictumTitle()
                .accessibilityAddTraits(.isHeader)

            DictumCard {
                Text(APPROVED_Agreement)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)

                VStack(alignment: .leading, spacing: 12) {
                    ForEach(Array(PROPOSED_Points.enumerated()), id: \.offset) { index, point in
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

            SectionHeader(APPROVED_How)
            // A segmented control truncates these approved labels on an iPhone, so the two choices are full-width
            // rows; each shows a filled or empty circle and announces itself as selected.
            VStack(spacing: 8) {
                choice(APPROVED_Spoken, value: "spoken")
                choice(APPROVED_Written, value: "written")
            }

            VStack(alignment: .leading, spacing: 6) {
                Text(PROPOSED_TitleLabel)
                    .font(.headline)
                    .foregroundStyle(DictumTheme.text)
                TextField(PROPOSED_TitleLabel, text: $controller.title)
                    .textFieldStyle(DictumFieldStyle())
                    .submitLabel(.done)
                    .accessibilityLabel(PROPOSED_TitleLabel)
            }

            if controller.transcript.supported {
                TranscriptSetupView(transcript: controller.transcript, settings: controller.settings)
            }

            if controller.micDenied {
                DictumCard {
                    Label(PROPOSED_MicDenied, systemImage: "mic.slash")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(DictumTheme.text)
                    SecondaryButton(title: PROPOSED_OpenSettings) { controller.openSettings() }
                }
            }

            Button {
                starting = true
                Task {
                    await controller.startTapped()
                    starting = false
                }
            } label: {
                Text(APPROVED_Start)
            }
            .buttonStyle(.dictumPrimary)
            .disabled(starting || !controller.transcript.canStart)
            .accessibilityLabel(APPROVED_Start)

            if !controller.transcript.canStart {
                Text(PROPOSED_StartNeedsModel)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.text)
            }
        }
        // The stored experimental switch (Settings) is applied once support is known; Done turns it off in memory,
        // so it is re-applied for each new meeting.
        .task(id: controller.transcript.supported) {
            let transcript = controller.transcript
            if transcript.supported, controller.settings.transcribeOnDevice, !transcript.enabled {
                await transcript.setEnabled(true)
            }
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

// MARK: - Screen 2: Recording

struct RecordingView: View {
    @Bindable var controller: RecordingController

    private var sealing: Bool {
        if case .sealing = controller.phase { return true }
        return false
    }

    var body: some View {
        VStack(spacing: 24) {
            DictumTopBar()
            Spacer(minLength: 8)
            RecordingIndicator()

            if let start = controller.startedAt {
                TimelineView(.periodic(from: start, by: 1)) { context in
                    let text = Self.elapsed(from: start, to: context.date)
                    Text(text)
                        .font(.system(.largeTitle, design: .rounded).weight(.semibold).monospacedDigit())
                        .foregroundStyle(DictumTheme.text)
                        .accessibilityLabel(PROPOSED_ElapsedLabel)
                        .accessibilityValue(text)
                }
            }

            // Bar length shows the input level; VoiceOver reads it as a percentage.
            ProgressView(value: controller.level)
                .progressViewStyle(.linear)
                .tint(DictumTheme.accent)
                .frame(maxWidth: 280)
                .accessibilityLabel(PROPOSED_InputLevel)
                .accessibilityValue(String(format: PROPOSED_InputLevelValue, Int((controller.level * 100).rounded())))

            if controller.noSoundDetected {
                StatusPill(kind: .warning, text: PROPOSED_NoSoundDetected)
            }

            if controller.transcript.active {
                LiveTranscriptView(transcript: controller.transcript)
            }

            Text(controller.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? PROPOSED_DefaultTitle : controller.title)
                .dictumTitle(.title2)
                .multilineTextAlignment(.center)

            if controller.captureStatus != .running {
                VStack(spacing: 12) {
                    StatusPill(kind: .warning, text: PROPOSED_Interrupted)
                    if controller.captureStatus == .needsUser {
                        SecondaryButton(title: PROPOSED_Resume) { controller.resumeTapped() }
                    }
                }
            }
            Spacer(minLength: 8)

            Button {
                Task { await controller.stopTapped() }
            } label: {
                Group {
                    if sealing {
                        HStack(spacing: 10) {
                            ProgressView().tint(DictumTheme.onAccent)
                            Text(PROPOSED_Sealing)
                        }
                    } else {
                        Label(PROPOSED_Stop, systemImage: "stop.fill")
                    }
                }
                .font(.title2.weight(.semibold))
                .frame(minHeight: 48)
            }
            .buttonStyle(.dictumPrimary)
            .disabled(sealing)
            .accessibilityLabel(sealing ? PROPOSED_Sealing : PROPOSED_Stop)
        }
        .padding(.vertical, 12)
        .dictumPage()
        .background(DictumTheme.bg.ignoresSafeArea())
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
                .fill(DictumTheme.recording)
                .frame(width: 24, height: 24)
                .scaleEffect(pulse && !reduceMotion ? 1.25 : 1)
                .opacity(pulse && !reduceMotion ? 0.55 : 1)
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.9).repeatForever(autoreverses: true), value: pulse)
            Text(PROPOSED_RecordingIndicator)
                .dictumTitle(.title)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
        .background(DictumTheme.recording.opacity(0.12), in: RoundedRectangle(cornerRadius: DictumTheme.radiusPill))
        .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusPill).strokeBorder(DictumTheme.recording.opacity(0.5), lineWidth: 1))
        .onAppear { pulse = true }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PROPOSED_RecordingIndicator)
    }
}

// MARK: - Screen 3: Sealed

struct SealedView: View {
    let bundle: FinishedBundle
    let captureFailed: Bool
    let review: MeetingReview
    let done: () -> Void
    @State private var composing = false

    /// Decimal megabytes (1 MB = 1 000 000 bytes, as Files shows), one decimal place, from the file's byte count.
    private var size: String { String(format: "%.1f MB", Double(bundle.bytes) / 1_000_000) }

    var body: some View {
        DictumScreen {
            HStack(spacing: 12) {
                Image(systemName: "lock.fill")
                    .font(.title)
                    .foregroundStyle(DictumTheme.done)
                    .accessibilityHidden(true)
                Text(PROPOSED_SealedHeading)
                    .dictumTitle()
            }
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isHeader)

            if bundle.incomplete {
                StatusPill(kind: .warning, text: PROPOSED_IncompleteNote)
            }
            if captureFailed {
                StatusPill(kind: .warning, text: PROPOSED_CaptureFailed)
            }

            DictumCard {
                FactRow(PROPOSED_FileLabel, value: bundle.url.lastPathComponent)
                FactRow(PROPOSED_SizeLabel, value: size)
            }

            DictumCard {
                SectionHeader(PROPOSED_CodeLabel)
                Text(bundle.transferCode)
                    .font(.system(.title2, design: .monospaced).weight(.semibold))
                    .foregroundStyle(DictumTheme.text)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
                    .accessibilityLabel(PROPOSED_CodeLabel)
                    .accessibilityValue(bundle.transferCode)
                Text(PROPOSED_CodeHint)
                    .font(.callout)
                    .foregroundStyle(DictumTheme.muted)
            }

            ShareLink(item: bundle.url) {
                Label(PROPOSED_Share, systemImage: "square.and.arrow.up")
            }
            .buttonStyle(.dictumPrimary)
            .accessibilityLabel(PROPOSED_Share)

            Text(PROPOSED_FilesHint)
                .font(.footnote)
                .foregroundStyle(DictumTheme.text)

            SecondaryButton(title: PROPOSED_ComposeEmail, systemImage: "envelope") { composing = true }

            SecondaryButton(title: PROPOSED_Done, action: done)
        }
        .sheet(isPresented: $composing) {
            ComposeEmailSheet(review: review)
        }
    }
}

// MARK: - Screen 4: Recovered

struct RecoverView: View {
    let seal: () -> Void

    var body: some View {
        DictumScreen {
            StatusPill(kind: .warning, text: PROPOSED_RecoverHeading)
                .accessibilityAddTraits(.isHeader)
            DictumCard {
                Text(PROPOSED_RecoverBody)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
            }
            PrimaryButton(title: PROPOSED_RecoverSeal, action: seal)
        }
    }
}

// MARK: - Errors

struct FailedView: View {
    let message: String
    let back: () -> Void

    var body: some View {
        DictumScreen {
            HStack(spacing: 12) {
                Image(systemName: "xmark.octagon")
                    .font(.title)
                    .foregroundStyle(DictumTheme.recording)
                    .accessibilityHidden(true)
                Text(PROPOSED_FailedHeading)
                    .dictumTitle(.title)
            }
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isHeader)
            DictumCard {
                Text(message)
                    .font(.body)
                    .foregroundStyle(DictumTheme.text)
            }
            SecondaryButton(title: PROPOSED_BackToStart, action: back)
        }
    }
}

/// While a recovered session is sealed: nothing is being recorded, so no recording indicator.
struct SealingRecoveredView: View {
    var body: some View {
        VStack(spacing: 20) {
            DictumTopBar()
            Spacer()
            StatusPill(kind: .working, text: PROPOSED_Sealing)
            ProgressView().accessibilityHidden(true)
            Spacer()
        }
        .padding(.vertical, 12)
        .dictumPage()
        .background(DictumTheme.bg.ignoresSafeArea())
    }
}

// MARK: - Experimental on-device transcript

/// Screen 1: the switch, its note, and the one place the speech model can be downloaded. The switch also sets the
/// stored setting, so Settings and this screen show the same value.
struct TranscriptSetupView: View {
    let transcript: OnDeviceTranscript
    let settings: AppSettings

    var body: some View {
        DictumCard {
            Toggle(isOn: Binding(get: { transcript.enabled },
                                 set: { on in
                                     settings.transcribeOnDevice = on
                                     Task { await transcript.setEnabled(on) }
                                 })) {
                Text(PROPOSED_OnDeviceTranscriptToggle)
                    .font(.headline)
                    .foregroundStyle(DictumTheme.text)
            }
            .tint(DictumTheme.accent)
            .frame(minHeight: DictumTheme.minTarget)
            .accessibilityLabel(PROPOSED_OnDeviceTranscriptToggle)

            Text(PROPOSED_OnDeviceTranscriptNote)
                .font(.footnote)
                .foregroundStyle(DictumTheme.muted)

            switch transcript.setup {
            case .off:
                EmptyView()
            case .checking:
                HStack(spacing: 10) {
                    ProgressView()
                    Text(PROPOSED_TranscriptChecking).foregroundStyle(DictumTheme.text)
                }
                .accessibilityElement(children: .combine)
            case .needsAssets:
                Text(PROPOSED_TranscriptAssetsNeeded).foregroundStyle(DictumTheme.text)
                SecondaryButton(title: PROPOSED_TranscriptDownload, systemImage: "arrow.down.circle") {
                    Task { await transcript.downloadTapped() }
                }
            case .downloading:
                if let progress = transcript.downloadProgress {
                    ProgressView(progress)
                        .tint(DictumTheme.accent)
                        .accessibilityLabel(PROPOSED_TranscriptDownloading)
                } else {
                    ProgressView()
                        .accessibilityHidden(true)
                }
                StatusPill(kind: .working, text: PROPOSED_TranscriptDownloading)
            case .ready:
                StatusPill(kind: .done, text: PROPOSED_TranscriptReady)
            case .unavailable:
                StatusPill(kind: .warning, text: PROPOSED_TranscriptUnavailable)
            case let .failed(reason):
                StatusPill(kind: .warning, text: String(format: PROPOSED_TranscriptDownloadFailed, reason))
                SecondaryButton(title: PROPOSED_TranscriptDownload, systemImage: "arrow.down.circle") {
                    Task { await transcript.retryDownload() }
                }
            }
        }
    }
}

/// Finalised words in the primary colour; the words still being recognised in the secondary colour and italic, so
/// the difference is not shown by colour alone.
private func transcriptText(final: String, volatile: String) -> Text {
    Text("\(Text(final))\(Text(volatile).italic().foregroundStyle(.secondary))")
}

/// Screen 2: status line and the live words. VoiceOver reads the region on demand; nothing is announced by itself.
struct LiveTranscriptView: View {
    let transcript: OnDeviceTranscript

    private var status: String {
        switch transcript.live {
        case .preparing: return PROPOSED_TranscriptPreparing
        case .listening: return PROPOSED_TranscriptListening
        case .unavailable: return PROPOSED_TranscriptUnavailable
        case let .failed(reason): return String(format: PROPOSED_TranscriptFailed, reason)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(status)
                .font(.footnote.weight(.semibold))
                .foregroundStyle(DictumTheme.text)
            ScrollView {
                transcriptText(final: transcript.finalText, volatile: transcript.volatileText)
                    .font(.body)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .defaultScrollAnchor(.bottom)
            .frame(maxHeight: 180)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(PROPOSED_TranscriptRegion)
            .accessibilityValue(liveValue)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(DictumTheme.surface, in: RoundedRectangle(cornerRadius: DictumTheme.radiusCard))
        .overlay(RoundedRectangle(cornerRadius: DictumTheme.radiusCard).strokeBorder(DictumTheme.border, lineWidth: 1))
    }

    private var liveValue: String {
        let text = transcript.finalText + transcript.volatileText
        return text.isEmpty ? PROPOSED_TranscriptEmpty : text
    }
}

/// Screen 3: where the experimental speaker step is up to.
struct SpeakerStatusView: View {
    let speakers: SpeakerLabels

    var body: some View {
        switch speakers.state {
        case .idle, .done:
            EmptyView()
        case let .working(fraction):
            VStack(alignment: .leading, spacing: 6) {
                Text(PROPOSED_SpeakersWorking)
                ProgressView(value: fraction)
                    .accessibilityLabel(PROPOSED_SpeakersWorking)
                    .accessibilityValue(String(format: PROPOSED_SpeakersProgressValue, Int(fraction * 100)))
            }
        case .unavailable:
            Label(PROPOSED_SpeakersUnavailable, systemImage: "person.2.slash")
                .accessibilityLabel(PROPOSED_SpeakersUnavailable)
        case .tooLittleSpeech:
            Label(PROPOSED_SpeakersTooLittle, systemImage: "person.2")
                .accessibilityLabel(PROPOSED_SpeakersTooLittle)
        case let .failed(reason):
            Label(String(format: PROPOSED_SpeakersFailed, reason), systemImage: "exclamationmark.triangle")
                .accessibilityLabel(String(format: PROPOSED_SpeakersFailed, reason))
        }
    }
}

/// The speaker step's measured values, inside the experiment facts box.
struct SpeakerFactsView: View {
    let facts: SpeakerLabels.Facts

    var body: some View {
        LabeledContent(PROPOSED_FactSegments, value: String(facts.segments))
        LabeledContent(PROPOSED_FactSegmentTimes, value: timesText)
        LabeledContent(PROPOSED_FactWindows, value: facts.windowsCut.map { cut in
            String(format: PROPOSED_FactWindowsValue, cut, facts.windowsEmbedded ?? 0)
        } ?? PROPOSED_FactNotRun)
        LabeledContent(PROPOSED_FactEmbedding, value: facts.embedSeconds.map { seconds in
            String(format: PROPOSED_FactEmbeddingValue, seconds, facts.embedSecondsPerAudioMinute ?? 0)
        } ?? PROPOSED_FactNotRun)
        LabeledContent(PROPOSED_FactClustering, value: facts.clusterSeconds.map { String(format: PROPOSED_FactSeconds, $0) }
                       ?? PROPOSED_FactNotRun)
        LabeledContent(PROPOSED_FactClustered, value: facts.clustered.map(String.init) ?? PROPOSED_FactNotRun)
        if facts.parameters.segmentMean {
            LabeledContent(PROPOSED_FactSegmentVectors, value: facts.clustered.map(String.init) ?? PROPOSED_FactNotRun)
        }
        LabeledContent(PROPOSED_FactDistanceP50, value: distance(facts.diagnostics?.p50))
        LabeledContent(PROPOSED_FactDistanceP90, value: distance(facts.diagnostics?.p90))
        LabeledContent(PROPOSED_FactDistanceMax, value: distance(facts.diagnostics?.max))
        LabeledContent(PROPOSED_FactSmallestBetween, value: distance(facts.diagnostics?.smallestBetweenClusters))
        LabeledContent(PROPOSED_FactLargestMerge, value: distance(facts.diagnostics?.largestMerge))
        LabeledContent(PROPOSED_FactSpeakers, value: facts.speakersFound.map(String.init) ?? PROPOSED_FactNotRun)
        let p = facts.parameters
        LabeledContent(PROPOSED_FactWindowRule, value: String(format: PROPOSED_FactWindowRuleValue, p.windowSeconds,
                                                              p.hopSeconds, p.minWindowSeconds))
        LabeledContent(PROPOSED_FactLinkage, value: p.linkage == .complete ? PROPOSED_FactLinkageComplete
                       : PROPOSED_FactLinkageAverage)
        LabeledContent(PROPOSED_FactThreshold, value: String(format: PROPOSED_FactThresholdValue, p.threshold))
        LabeledContent(PROPOSED_FactSegmentMean, value: p.segmentMean ? PROPOSED_FactYes : PROPOSED_FactCapNo)
        LabeledContent(PROPOSED_FactDesktopRule, value: p == .desktop ? PROPOSED_FactYes : PROPOSED_FactCapNo)
        LabeledContent(PROPOSED_FactPcm, value: String(format: PROPOSED_FactMegabytes, Double(facts.pcmBytes) / 1_000_000))
        LabeledContent(PROPOSED_FactCap, value: facts.capHit ? PROPOSED_FactYes : PROPOSED_FactCapNo)
    }

    /// Three decimals, or "Not run" when there was no such distance.
    private func distance(_ value: Double?) -> String {
        value.map { String(format: PROPOSED_FactDistanceValue, $0) } ?? PROPOSED_FactNotRun
    }

    private var timesText: String {
        switch facts.timeSource {
        case .wordRanges: return PROPOSED_FactTimesWords
        case .resultRanges: return PROPOSED_FactTimesResults
        case .wholeRecording: return PROPOSED_FactTimesWhole
        }
    }
}
