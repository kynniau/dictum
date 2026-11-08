import SwiftUI

/// Settings: non-sensitive choices only (AppSettings). Shown as a sheet from the home screen, never during a recording.
struct SettingsView: View {
    @Bindable var settings: AppSettings
    let transcript: OnDeviceTranscript
    @Environment(\.dismiss) private var dismiss

    private let summary = SummaryModelStatus.current()

    var body: some View {
        NavigationStack {
            Form {
                engineSection
                speakerSection
                meetingTypeSection
                summarySection
                keepSection
                aboutSection
            }
            .scrollContentBackground(.hidden)
            .background(DictumTheme.bg.ignoresSafeArea())
            .tint(DictumTheme.link)
            .navigationTitle(PROPOSED_SettingsTitle)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(PROPOSED_SettingsDone) { dismiss() }
                        .accessibilityLabel(PROPOSED_SettingsDone)
                }
            }
        }
    }

    // MARK: - Sections

    private var engineSection: some View {
        Section {
            engineRow(PROPOSED_EngineApple,
                      detail: transcript.supported ? PROPOSED_EngineAppleAvailable : PROPOSED_EngineAppleUnavailable,
                      selected: settings.transcriptionEngine == "apple", enabled: transcript.supported) {
                settings.transcriptionEngine = "apple"
            }
            engineRow(PROPOSED_EngineWhisperBase, detail: PROPOSED_NotYetMeasured, selected: false, enabled: false) {}
            engineRow(PROPOSED_EngineWhisperSmall, detail: PROPOSED_NotYetMeasured, selected: false, enabled: false) {}
            engineRow(PROPOSED_EngineWhisperTurbo, detail: PROPOSED_NotYetMeasured, selected: false, enabled: false) {}
            if transcript.supported {
                VStack(alignment: .leading, spacing: 6) {
                    Toggle(PROPOSED_OnDeviceTranscriptToggle, isOn: $settings.transcribeOnDevice)
                        .tint(DictumTheme.accent)
                        .accessibilityLabel(PROPOSED_OnDeviceTranscriptToggle)
                    Text(PROPOSED_OnDeviceTranscriptNote)
                        .font(.footnote)
                        .foregroundStyle(DictumTheme.muted)
                }
                .frame(minHeight: DictumTheme.minTarget)
                .listRowBackground(DictumTheme.surface)
            }
        } header: {
            Text(PROPOSED_SectionEngine).accessibilityAddTraits(.isHeader)
        }
    }

    private var speakerSection: some View {
        Section {
            Toggle(PROPOSED_SpeakerLabelsToggle, isOn: $settings.speakerLabels)
                .tint(DictumTheme.accent)
                .frame(minHeight: DictumTheme.minTarget)
                .accessibilityLabel(PROPOSED_SpeakerLabelsToggle)
                .listRowBackground(DictumTheme.surface)
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(PROPOSED_ThresholdLabel).foregroundStyle(DictumTheme.text)
                    Spacer()
                    Text(String(format: PROPOSED_FactThresholdValue, settings.speakerThreshold))
                        .font(.body.monospacedDigit())
                        .foregroundStyle(DictumTheme.text)
                        .accessibilityHidden(true)
                }
                Slider(value: $settings.speakerThreshold, in: AppSettings.thresholdRange, step: AppSettings.thresholdStep)
                    .tint(DictumTheme.accent)
                    .accessibilityLabel(PROPOSED_ThresholdLabel)
                    .accessibilityValue(String(format: PROPOSED_FactThresholdValue, settings.speakerThreshold))
                Text(PROPOSED_ThresholdHint)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
                Text(APPROVED_Limits)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }
            .disabled(!settings.speakerLabels)
            .padding(.vertical, 4)
            .listRowBackground(DictumTheme.surface)
        } header: {
            Text(PROPOSED_SectionSpeakers).accessibilityAddTraits(.isHeader)
        }
    }

    private var meetingTypeSection: some View {
        Section {
            Picker(PROPOSED_MeetingTypePicker, selection: $settings.meetingTypeId) {
                ForEach(MeetingType.all) { type in
                    Text(type.name).tag(type.id)
                }
            }
            .pickerStyle(.inline)
            .labelsHidden()
            .accessibilityLabel(PROPOSED_MeetingTypePicker)
            .listRowBackground(DictumTheme.surface)
        } header: {
            Text(PROPOSED_SectionMeetingType).accessibilityAddTraits(.isHeader)
        }
    }

    private var summarySection: some View {
        Section {
            engineRow(PROPOSED_SummaryApple,
                      detail: summary.available ? PROPOSED_SummaryAppleAvailable : PROPOSED_SummaryAppleNotAvailable,
                      selected: summary.available && settings.summaryEngine == "apple", enabled: summary.available) {
                settings.summaryEngine = "apple"
            }
            engineRow(PROPOSED_SummaryOwnKey, detail: PROPOSED_NotInThisBuild, selected: false, enabled: false) {}
        } header: {
            Text(PROPOSED_SectionSummary).accessibilityAddTraits(.isHeader)
        }
    }

    private var keepSection: some View {
        Section {
            VStack(alignment: .leading, spacing: 6) {
                Toggle(PROPOSED_KeepToggle, isOn: .constant(false))
                    .disabled(true)
                    .accessibilityLabel(PROPOSED_KeepToggle)
                    .accessibilityHint(PROPOSED_KeepNote)
                Text(PROPOSED_KeepNote)
                    .font(.footnote)
                    .foregroundStyle(DictumTheme.muted)
            }
            .frame(minHeight: DictumTheme.minTarget)
            .listRowBackground(DictumTheme.surface)
        } header: {
            Text(PROPOSED_SectionKeep).accessibilityAddTraits(.isHeader)
        }
    }

    private var aboutSection: some View {
        Section {
            FactRow(PROPOSED_AboutVersion, value: AppInfo.version).listRowBackground(DictumTheme.surface)
            FactRow(PROPOSED_AboutBuild, value: BuildInfo.sha).listRowBackground(DictumTheme.surface)
            VStack(alignment: .leading, spacing: 6) {
                Text(PROPOSED_AboutLicences).font(.footnote).foregroundStyle(DictumTheme.muted)
                ForEach([PROPOSED_LicenceDictum, PROPOSED_LicenceSherpa, PROPOSED_LicenceCampPlus, PROPOSED_LicenceBip39],
                        id: \.self) { line in
                    Text(line).font(.subheadline).foregroundStyle(DictumTheme.text)
                }
            }
            .accessibilityElement(children: .combine)
            .listRowBackground(DictumTheme.surface)
        } header: {
            Text(PROPOSED_SectionAbout).accessibilityAddTraits(.isHeader)
        }
    }

    /// A selectable choice: a filled or empty circle (not colour alone) and the detail line, announced as selected.
    private func engineRow(_ title: String, detail: String, selected: Bool, enabled: Bool,
                           action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .font(.title3)
                    .foregroundStyle(selected ? DictumTheme.link : DictumTheme.muted)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).foregroundStyle(DictumTheme.text)
                    Text(detail).font(.footnote).foregroundStyle(DictumTheme.muted)
                }
                Spacer(minLength: 0)
            }
            .frame(minHeight: DictumTheme.minTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.6)
        .listRowBackground(DictumTheme.surface)
        .accessibilityLabel(title)
        .accessibilityValue(detail)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }
}
