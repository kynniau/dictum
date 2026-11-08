import Foundation
import Observation
import SwiftUI

// Non-sensitive choices only, kept in UserDefaults: which engine, whether the experimental transcript and speaker labels
// are on, the separation threshold, the meeting type and the summary engine. Nothing said in a meeting, no name, no
// recipient and no summary is ever stored here or anywhere else.

/// A meeting type: the desktop's built-in summary types (src/Dictum.Core/Meetings/Summary/SummaryTemplate.cs,
/// `SummaryTemplates.Builtins`, ids and names) with the section headings of each built-in template file
/// (src/Dictum.Core/Meetings/Summary/Templates/<id>.md), which the on-device summary uses as its intent.
struct MeetingType: Identifiable, Hashable {
    let id: String
    let name: String
    /// The template's section headings, in order, as the desktop's template file lists them.
    let sections: [String]

    static let all: [MeetingType] = [
        MeetingType(id: "general", name: PROPOSED_TypeGeneral, sections: ["Summary", "Decisions", "Action items"]),
        MeetingType(id: "client-file-note", name: PROPOSED_TypeClientFileNote,
                    sections: ["Meeting details", "Discussion", "Decisions", "Action items"]),
        MeetingType(id: "lender-call", name: PROPOSED_TypeLenderCall,
                    sections: ["Call details", "Discussion", "Figures", "Action items"]),
        MeetingType(id: "stand-up", name: PROPOSED_TypeStandUp, sections: ["Updates", "Blockers", "Action items"]),
    ]

    static func with(id: String) -> MeetingType { all.first { $0.id == id } ?? all[0] }
}

@MainActor
@Observable
final class AppSettings {
    enum Key {
        static let transcribe = "settings.transcribeOnDevice"
        static let labels = "settings.speakerLabels"
        static let threshold = "settings.speakerThreshold"
        static let meetingType = "settings.meetingType"
        static let engine = "settings.transcriptionEngine"
        static let summaryEngine = "settings.summaryEngine"
        static let appearance = "settings.appearance"
    }

    /// Automatic follows the system; Light and Dark force one.
    enum Appearance: String, CaseIterable, Identifiable {
        case automatic, light, dark
        var id: String { rawValue }
        var name: String {
            switch self {
            case .automatic: return PROPOSED_AppearanceAutomatic
            case .light: return PROPOSED_AppearanceLight
            case .dark: return PROPOSED_AppearanceDark
            }
        }
        var colorScheme: ColorScheme? {
            switch self {
            case .automatic: return nil
            case .light: return .light
            case .dark: return .dark
            }
        }
    }

    /// The beta's speaker threshold (SpeakerParameters.current), and the slider's range and step.
    static let defaultThreshold = 0.70
    static let thresholdRange: ClosedRange<Double> = 0.5...0.9
    static let thresholdStep = 0.05

    private let defaults: UserDefaults

    /// The experimental switch on the consent screen; default off, as before.
    var transcribeOnDevice: Bool { didSet { defaults.set(transcribeOnDevice, forKey: Key.transcribe) } }
    /// Speaker labels after Stop, with the transcript switch on. Default on, as the beta ran before this setting.
    var speakerLabels: Bool { didSet { defaults.set(speakerLabels, forKey: Key.labels) } }
    var speakerThreshold: Double { didSet { defaults.set(speakerThreshold, forKey: Key.threshold) } }
    var meetingTypeId: String { didSet { defaults.set(meetingTypeId, forKey: Key.meetingType) } }
    /// Only "apple" is selectable in this build.
    var transcriptionEngine: String { didSet { defaults.set(transcriptionEngine, forKey: Key.engine) } }
    /// Only "apple" (the on-device model) is selectable in this build.
    var summaryEngine: String { didSet { defaults.set(summaryEngine, forKey: Key.summaryEngine) } }
    var appearance: Appearance { didSet { defaults.set(appearance.rawValue, forKey: Key.appearance) } }

    /// `-DictumAppearance light|dark|automatic` on the launch command line overrides the stored value for that launch
    /// only (screenshot tests); it is never stored.
    let launchAppearance: Appearance? = {
        let arguments = ProcessInfo.processInfo.arguments
        guard let at = arguments.firstIndex(of: "-DictumAppearance"), at + 1 < arguments.count else { return nil }
        return Appearance(rawValue: arguments[at + 1].lowercased())
    }()

    /// What the root applies with `.preferredColorScheme`: nil follows the system.
    var colorScheme: ColorScheme? { (launchAppearance ?? appearance).colorScheme }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        transcribeOnDevice = defaults.object(forKey: Key.transcribe) as? Bool ?? false
        speakerLabels = defaults.object(forKey: Key.labels) as? Bool ?? true
        let stored = defaults.object(forKey: Key.threshold) as? Double ?? Self.defaultThreshold
        speakerThreshold = min(max(stored, Self.thresholdRange.lowerBound), Self.thresholdRange.upperBound)
        meetingTypeId = MeetingType.with(id: defaults.string(forKey: Key.meetingType) ?? "general").id
        transcriptionEngine = "apple"
        summaryEngine = "apple"
        appearance = Appearance(rawValue: defaults.string(forKey: Key.appearance) ?? "") ?? .automatic
    }

    var meetingType: MeetingType { MeetingType.with(id: meetingTypeId) }
}

enum BuildInfo {
    /// The `DictumBuildSha` Info.plist key, filled from the DICTUM_BUILD_SHA build setting (DictumRecorder.xcconfig;
    /// pass `DICTUM_BUILD_SHA=$(git rev-parse --short HEAD)` to xcodebuild to stamp it).
    static var sha: String {
        let value = (Bundle.main.infoDictionary?["DictumBuildSha"] as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
        return value.isEmpty || value.hasPrefix("$(") ? PROPOSED_BuildShaUnknown : value
    }
}
