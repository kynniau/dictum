import DictumBundle
import SwiftUI

// The recording screens in Dictum's design language (Theme.swift). Every control has an accessibility label; text uses
// Dynamic Type styles; no meaning is carried by colour alone; targets are at least 44 pt.

/// A text field on a surface, with the control radius and border. `reading` sets the serif reading face, for long text
/// such as a summary.
struct DictumFieldStyle: TextFieldStyle {
    var reading = false

    func _body(configuration: TextField<Self._Label>) -> some View {
        let shape = RoundedRectangle(cornerRadius: DictumTheme.radiusControl, style: .continuous)
        configuration
            .font(reading ? .dictumReading() : .body)
            .foregroundStyle(DictumTheme.text)
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .frame(minHeight: DictumTheme.minTarget)
            .background(DictumTheme.surface, in: shape)
            .overlay(shape.strokeBorder(DictumTheme.borderStrong, lineWidth: 1))
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
                    ForEach(Array(PROPOSED_PointsFor(keep: controller.settings.keepOnDevice).enumerated()), id: \.offset) { index, point in
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
            .accessibilityIdentifier("consent.start")

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
        AgreementChoiceRow(label: label, selected: controller.how == value) { controller.how = value }
    }
}

// MARK: - Screen 2: Recording

struct RecordingView: View {
    @Bindable var controller: RecordingController

    @ScaledMetric(relativeTo: .largeTitle) private var timerSize: CGFloat = 64

    private var sealing: Bool {
        if case .sealing = controller.phase { return true }
        return false
    }

    var body: some View {
        // This screen alone is always dark ("Studio"): the colour sets resolve to their dark values here. The status bar
        // follows from RootView's preferredColorScheme.
        VStack(spacing: 12) {
            topRow
            // Centred in all the height above Stop when it fits; scrolls when it does not (large text, a long transcript).
            GeometryReader { proxy in
                ScrollView {
                    middle
                        .frame(maxWidth: .infinity, minHeight: proxy.size.height, alignment: .center)
                }
                .scrollBounceBehavior(.basedOnSize)
            }
            stopControl
        }
        .padding(.vertical, 12)
        .frame(maxHeight: .infinity)
        .dictumPage()
        .background(DictumTheme.bg.ignoresSafeArea())
        .environment(\.colorScheme, .dark)
    }

    /// The recording indicator on the left; the lock and "Encrypted" on the right.
    private var topRow: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) {
                RecordingIndicator()
                Spacer(minLength: 8)
                encrypted
            }
            VStack(alignment: .leading, spacing: 6) {
                RecordingIndicator()
                encrypted
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var encrypted: some View {
        Label(PROPOSED_Encrypted, systemImage: "lock.fill")
            .font(.subheadline.weight(.medium))
            .foregroundStyle(DictumTheme.mutedStrong)
            .accessibilityElement(children: .combine)
    }

    private var middle: some View {
        VStack(spacing: 20) {
            VStack(spacing: 4) {
                Text(controller.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? PROPOSED_DefaultTitle : controller.title)
                    .font(.headline)
                    .foregroundStyle(DictumTheme.mutedStrong)
                    .multilineTextAlignment(.center)
                // The meeting type's own name, when it is not the default.
                if controller.settings.meetingTypeId != "general" {
                    Text(controller.settings.meetingType.name)
                        .font(.subheadline)
                        .foregroundStyle(DictumTheme.mutedStrong)
                        .multilineTextAlignment(.center)
                }
            }
            .accessibilityElement(children: .combine)

            if let start = controller.startedAt {
                TimelineView(.periodic(from: start, by: 1)) { context in
                    let text = Self.elapsed(from: start, to: context.date)
                    Text(text)
                        .font(.system(size: timerSize, weight: .medium).monospacedDigit())
                        .tracking(-1)
                        .foregroundStyle(.white)
                        .lineLimit(1)
                        .minimumScaleFactor(0.4)
                        .accessibilityLabel(PROPOSED_ElapsedLabel)
                        .accessibilityValue(text)
                }
            }

            // Bar heights show the recent input level; VoiceOver reads the current level as a percentage.
            LevelHistoryWaveform(level: controller.level, active: controller.captureStatus == .running)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(PROPOSED_InputLevel)
                .accessibilityValue(String(format: PROPOSED_InputLevelValue, Int((controller.level * 100).rounded())))

            if controller.noSoundDetected {
                StatusPill(kind: .warning, text: PROPOSED_NoSoundDetected)
            }

            if controller.transcript.active {
                LiveTranscriptView(transcript: controller.transcript)
            }

            if controller.captureStatus != .running {
                VStack(spacing: 12) {
                    StatusPill(kind: .warning, text: PROPOSED_Interrupted)
                    if controller.captureStatus == .needsUser {
                        SecondaryButton(title: PROPOSED_Resume) { controller.resumeTapped() }
                    }
                }
            }
        }
        .padding(.vertical, 8)
    }

    /// A round stop button (a ring in the text colour around a red rounded square), its words beneath. The whole stack
    /// is the button.
    private var stopControl: some View {
        Button {
            Task { await controller.stopTapped() }
        } label: {
            VStack(spacing: 8) {
                StopGlyph(sealing: sealing)
                Text(sealing ? PROPOSED_Sealing : PROPOSED_Stop)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(DictumTheme.text)
                    .multilineTextAlignment(.center)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(StopButtonStyle())
        .disabled(sealing)
        .accessibilityLabel(sealing ? PROPOSED_Sealing : PROPOSED_Stop)
        .accessibilityIdentifier("recording.stop")
    }

    static func elapsed(from start: Date, to now: Date) -> String {
        elapsed(seconds: now.timeIntervalSince(start))
    }

    /// h:mm:ss, whole seconds rounded down.
    static func elapsed(seconds: TimeInterval) -> String {
        let total = max(0, Int(seconds.isFinite ? seconds : 0))
        return String(format: "%d:%02d:%02d", total / 3600, (total / 60) % 60, total % 60)
    }
}

/// The stop button's mark: a 76 pt ring with a red rounded square, or a spinner while sealing. Decorative.
private struct StopGlyph: View {
    let sealing: Bool
    @ScaledMetric(relativeTo: .title) private var size: CGFloat = 76

    var body: some View {
        ZStack {
            Circle().strokeBorder(DictumTheme.text, lineWidth: 3)
            if sealing {
                ProgressView().tint(DictumTheme.text)
            } else {
                RoundedRectangle(cornerRadius: size * 0.1, style: .continuous)
                    .fill(DictumTheme.recording)
                    .frame(width: size * 0.37, height: size * 0.37)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

/// Pressed: dimmed a little. Disabled (sealing): as the other buttons.
private struct StopButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.7 : (enabled ? 1 : 0.6))
    }
}

/// Red pulsing dot plus the word "Recording", so the state is never shown by colour alone.
struct RecordingIndicator: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @ScaledMetric(relativeTo: .subheadline) private var dot: CGFloat = 10
    @State private var pulse = false

    var body: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(DictumTheme.recording)
                .frame(width: dot, height: dot)
                .scaleEffect(pulse && !reduceMotion ? 1.3 : 1)
                .opacity(pulse && !reduceMotion ? 0.55 : 1)
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.9).repeatForever(autoreverses: true), value: pulse)
            Text(PROPOSED_RecordingIndicator)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(DictumTheme.text)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(DictumTheme.recording.opacity(0.16), in: RoundedRectangle(cornerRadius: DictumTheme.radiusChip, style: .continuous))
        .onAppear { pulse = true }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PROPOSED_RecordingIndicator)
    }
}

// MARK: - Screen 3: Sealed

struct SealedView: View {
    let bundle: FinishedBundle
    let captureFailed: Bool
    var keepFailed = false
    /// Built from the review when this screen is shown.
    let email: SummaryEmail
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
            if keepFailed {
                StatusPill(kind: .warning, text: PROPOSED_KeepFailed)
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

            Text(UIDevice.current.userInterfaceIdiom == .pad ? PROPOSED_FilesHintPad : PROPOSED_FilesHint)
                .font(.footnote)
                .foregroundStyle(DictumTheme.text)

            SecondaryButton(title: PROPOSED_ComposeEmail, systemImage: "envelope") { composing = true }

            SecondaryButton(title: PROPOSED_Done, action: done)
                .accessibilityIdentifier("sealed.done")
        }
        .sheet(isPresented: $composing) {
            ComposeEmailSheet(email: email)
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

            Text(settings.keepOnDevice ? PROPOSED_OnDeviceTranscriptNoteKept : PROPOSED_OnDeviceTranscriptNote)
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
        DictumCard(padding: 16, spacing: 8) {
            Text(status)
                .font(.caption.weight(.medium))
                .tracking(1)
                .textCase(.uppercase)
                .foregroundStyle(DictumTheme.muted)
            ScrollView {
                transcriptText(final: transcript.finalText, volatile: transcript.volatileText)
                    .font(.body)
                    .lineSpacing(2)
                    .foregroundStyle(DictumTheme.text)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .defaultScrollAnchor(.bottom)
            .frame(maxHeight: 180)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(PROPOSED_TranscriptRegion)
            .accessibilityValue(liveValue)
        }
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
