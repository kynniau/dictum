import Foundation
import Observation
#if canImport(FoundationModels)
import FoundationModels
#endif

// On-device meeting summary with Apple's FoundationModels framework (iOS 26 or later).
//
// Only `SystemLanguageModel.default`, the model that runs on this device, is used. The SDK also offers
// `PrivateCloudComputeLanguageModel`, which runs off the device; this app never names it. The app makes no network call
// and never asks for the model to be downloaded: the system manages it.

/// Whether the on-device model can be used now, and why not.
struct SummaryModelStatus: Equatable {
    var available: Bool
    /// A plain reason when it is not available; nil when available.
    var reason: String?
    /// "available", "unavailable", or "not checked" before iOS 26, for the facts box.
    var statusText: String

    @MainActor
    static func current() -> SummaryModelStatus {
        #if canImport(FoundationModels)
        if #available(iOS 26, *) {
            switch SystemLanguageModel.default.availability {
            case .available:
                return SummaryModelStatus(available: true, reason: nil, statusText: PROPOSED_FactModelStatusAvailable)
            case let .unavailable(why):
                let reason: String
                switch why {
                case .deviceNotEligible: reason = PROPOSED_ReasonDeviceNotEligible
                case .appleIntelligenceNotEnabled: reason = PROPOSED_ReasonNotEnabled
                case .modelNotReady: reason = PROPOSED_ReasonNotReady
                @unknown default: reason = PROPOSED_ReasonOther
                }
                return SummaryModelStatus(available: false, reason: reason, statusText: PROPOSED_FactModelStatusUnavailable)
            }
        }
        #endif
        return SummaryModelStatus(available: false, reason: PROPOSED_ReasonDeviceNotEligible,
                                  statusText: PROPOSED_FactModelStatusNotChecked)
    }
}

/// The summary step on the review screen. Main thread only. Everything here is memory only and cleared on Done.
@MainActor
@Observable
final class OnDeviceSummary {
    enum State: Equatable {
        case idle
        /// Writing part `part` of `of` (1-based); with more than one part, a last combining pass follows.
        case working(part: Int, of: Int)
        case done
        case unavailable(String)
        case failed(String)
    }

    struct Action: Equatable {
        var owner: String
        var text: String
        var byWhen: String
    }

    /// Measured values for the facts box.
    struct Facts: Equatable {
        var available: Bool
        var reason: String?
        /// The model's availability when the summary was asked for.
        var statusBefore: String
        /// Always false: the app has no way to request the model; the system manages it.
        var requestMade: Bool
        var seconds: Double?
        var parts: Int?
    }

    private(set) var state: State = .idle
    private(set) var summary = ""
    private(set) var actions: [Action] = []
    private(set) var facts: Facts?
    private var task: Task<Void, Never>?
    private var generation = 0

    var working: Bool { if case .working = state { return true } else { return false } }

    /// Writes the summary and suggested actions from the labelled transcript lines ("00:12 Name: words").
    func generate(lines: [String], type: MeetingType) {
        task?.cancel()
        generation += 1
        let mine = generation
        summary = ""
        actions = []
        let status = SummaryModelStatus.current()
        var facts = Facts(available: status.available, reason: status.reason, statusBefore: status.statusText,
                          requestMade: false, seconds: nil, parts: nil)
        self.facts = facts
        guard status.available else {
            state = .unavailable(PROPOSED_SummaryUnavailable)
            return
        }
        #if canImport(FoundationModels)
        if #available(iOS 26, *) {
            let parts = SummaryWriter.parts(lines, contextTokens: SystemLanguageModel.default.contextSize)
            facts.parts = parts.count
            self.facts = facts
            state = .working(part: 1, of: parts.count)
            let started = facts
            task = Task { [weak self] in
                let clock = ContinuousClock()
                let began = clock.now
                do {
                    let notes = try await SummaryWriter.write(parts: parts, type: type) { part in
                        guard let self, self.generation == mine else { return }
                        self.state = .working(part: part, of: parts.count)
                    }
                    guard let self, self.generation == mine else { return }
                    let elapsed = (clock.now - began).components
                    var finished = started
                    finished.seconds = Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
                    self.facts = finished
                    self.summary = notes.summary
                    self.actions = notes.actions
                    self.state = .done
                } catch is CancellationError {
                } catch let error as LanguageModelSession.GenerationError {
                    guard let self, self.generation == mine else { return }
                    if case .exceededContextWindowSize = error {
                        self.state = .failed(PROPOSED_SummaryTooLong)
                    } else {
                        self.state = .failed(String(format: PROPOSED_SummaryFailed, error.localizedDescription))
                    }
                } catch {
                    guard let self, self.generation == mine else { return }
                    self.state = .failed(String(format: PROPOSED_SummaryFailed, error.localizedDescription))
                }
            }
            return
        }
        #endif
        state = .unavailable(PROPOSED_SummaryUnavailable)
    }

    /// Done: drop the summary, the suggested actions and the facts.
    func clear() {
        generation += 1
        task?.cancel()
        task = nil
        state = .idle
        summary = ""
        actions = []
        facts = nil
    }
}

#if canImport(FoundationModels)
/// What the on-device model is asked to produce.
@available(iOS 26, *)
@Generable
struct GeneratedNotes {
    @Guide(description: "A brief summary of the meeting in three to five sentences, using only what was said.")
    var summary: String
    @Guide(description: "Every action someone agreed to do in the meeting. Empty if none.")
    var actions: [GeneratedAction]
}

@available(iOS 26, *)
@Generable
struct GeneratedAction {
    @Guide(description: "Who will do it: exactly one of the speaker names or labels used in the transcript, or an empty string if nobody was named.")
    var owner: String
    @Guide(description: "What they will do, as one short instruction.")
    var action: String
    @Guide(description: "When it is due, in the words used in the meeting, or an empty string if no time was given.")
    var byWhen: String
}

/// Calls to the on-device model. A fresh session per call, so no earlier part fills the context.
@available(iOS 26, *)
enum SummaryWriter {
    // Model instructions, not shown on screen.
    static func instructions(_ type: MeetingType) -> String {
        "You write meeting notes for a meeting of the type \"\(type.name)\". Think about it under these headings: "
            + type.sections.joined(separator: ", ")
            + ". Use only what was said in the transcript. Never invent names, dates, figures or actions. Each line of "
            + "the transcript starts with the time and, where known, who spoke (a name or a label such as \"Other 1\")."
    }

    // simplification: the transcript is split by characters at about 3 characters per token, with a fixed reserve for
    // the instructions, the schema and the answer. Ceiling: a very dense part can still overflow the context (shown as
    // PROPOSED_SummaryTooLong), and the combining pass sees only the parts' notes, not the words. Retire with the
    // tokenCount API (iOS 26.4+) once a device test measures real meetings.
    static func parts(_ lines: [String], contextTokens: Int) -> [String] {
        let budget = max(2_000, (contextTokens - 1_800) * 3)
        var parts: [String] = []
        var current = ""
        for line in lines {
            if !current.isEmpty, current.count + line.count + 1 > budget {
                parts.append(current)
                current = ""
            }
            current += (current.isEmpty ? "" : "\n") + String(line.prefix(budget))
        }
        if !current.isEmpty { parts.append(current) }
        return parts
    }

    static func write(parts: [String], type: MeetingType,
                      progress: @MainActor (Int) -> Void) async throws -> (summary: String, actions: [OnDeviceSummary.Action]) {
        var notes: [GeneratedNotes] = []
        for (index, part) in parts.enumerated() {
            try Task.checkCancellation()
            await progress(index + 1)
            let session = LanguageModelSession(model: SystemLanguageModel.default, instructions: instructions(type))
            let prompt = (parts.count > 1 ? "This is part \(index + 1) of \(parts.count) of the transcript.\n\n" : "")
                + "Transcript:\n" + part
            notes.append(try await session.respond(to: prompt, generating: GeneratedNotes.self).content)
        }
        var final = notes.first ?? GeneratedNotes(summary: "", actions: [])
        if notes.count > 1 {
            try Task.checkCancellation()
            let session = LanguageModelSession(model: SystemLanguageModel.default, instructions: instructions(type))
            let combined = notes.enumerated().map { i, n in
                "Part \(i + 1) summary: \(n.summary)\nPart \(i + 1) actions:\n"
                    + n.actions.map { "- \($0.owner): \($0.action)\($0.byWhen.isEmpty ? "" : " (by \($0.byWhen))")" }
                    .joined(separator: "\n")
            }.joined(separator: "\n\n")
            let prompt = "These are notes from consecutive parts of one meeting. Combine them into one brief summary of "
                + "three to five sentences and one list of every action, without repeating any.\n\n" + combined
            final = try await session.respond(to: prompt, generating: GeneratedNotes.self).content
        }
        let actions = final.actions.map {
            OnDeviceSummary.Action(owner: $0.owner.trimmingCharacters(in: .whitespacesAndNewlines),
                                   text: $0.action.trimmingCharacters(in: .whitespacesAndNewlines),
                                   byWhen: $0.byWhen.trimmingCharacters(in: .whitespacesAndNewlines))
        }.filter { !$0.text.isEmpty }
        return (final.summary.trimmingCharacters(in: .whitespacesAndNewlines), actions)
    }
}
#endif
