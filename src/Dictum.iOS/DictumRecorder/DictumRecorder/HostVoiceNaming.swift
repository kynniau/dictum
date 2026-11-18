import Foundation

// "Your voice" in meetings: after the speaker step, the speaker group nearest the kept voice-print (under
// HostVoice.matchCutOff) is given the kept name, unless that speaker already has a name. The one place both flows call:
// the review after a recording (RecordingController wires `follow`) and "Transcribe on this device" for a kept meeting
// (MeetingDetailModel.transcribeHere calls `applyHostVoice`).
//
// A name put there by this rule is remembered as auto-applied (`MeetingReview.autoNamed`), so after Fewer or More
// speakers it moves to the newly matched group; a name the user types for that speaker (`setName`) clears the mark, and
// a typed name is never overwritten. Loading the voice-print happens off the main thread and fails soft: no voice-print,
// a locked Keychain or no match all leave the names as they are and show no error.

/// What "Your voice" found, for the facts box. Never the voice-print itself.
struct HostVoiceFacts: Equatable {
    var stored: Bool
    /// The nearest group at any distance; nil when there was no group to compare.
    var nearest: HostVoice.Match?
    /// The speaker matched under the cut-off.
    var matched: Int?

    var text: String {
        guard stored else { return PROPOSED_FactHostVoiceNone }
        guard let nearest else { return PROPOSED_FactHostVoiceNoGroups }
        if let matched { return String(format: PROPOSED_FactHostVoiceMatched, matched, nearest.distance) }
        return String(format: PROPOSED_FactHostVoiceNoMatch, nearest.distance)
    }
}

extension MeetingReview {
    /// The live review: re-match every time the speaker step makes or re-makes its labels.
    func follow(_ speakers: SpeakerLabels) {
        speakers.onLabelled = { [weak self, weak speakers] in
            guard let self, let speakers else { return }
            self.applyHostVoice(centres: speakers.groupCentres)
        }
    }

    /// Loads the kept voice-print off the main thread, matches it against `centres`, then places the name.
    /// `placed` runs on the main actor once the name has been placed or left alone, so a kept meeting can save it.
    func applyHostVoice(centres: [Int: [Float]], placed: (@MainActor () -> Void)? = nil) {
        hostRun += 1
        let run = hostRun
        Task { [weak self] in
            let found = await Task.detached(priority: .utility) { () -> (facts: HostVoiceFacts, name: String?) in
                guard let stored = HostVoiceStore.load() else { return (HostVoiceFacts(stored: false), nil) }
                let nearest = HostVoice.nearest(print: stored.print, groupCentres: centres)
                let matched = HostVoice.match(print: stored.print, groupCentres: centres)
                return (HostVoiceFacts(stored: true, nearest: nearest, matched: matched), stored.name)
            }.value
            guard let self, self.hostRun == run else { return } // a newer run, or Done, came first
            self.hostVoice = found.facts
            self.placeHostName(found.name, on: found.facts.matched)
            placed?()
        }
    }

    /// Moves the auto-applied name to `speaker`; never over a name the user typed.
    func placeHostName(_ name: String?, on speaker: Int?) {
        var rules = SpeakerNames(names: names, autoNamed: autoNamed)
        rules.place(name, on: speaker)
        (names, autoNamed) = (rules.names, rules.autoNamed)
    }

    /// A name typed by the user (nil clears it). It is the user's from now on, so re-matching leaves it alone.
    func setName(_ name: String?, for speaker: Int) {
        var rules = SpeakerNames(names: names, autoNamed: autoNamed)
        rules.type(name, for: speaker)
        (names, autoNamed) = (rules.names, rules.autoNamed)
    }

    /// The speakers were numbered afresh: drop every name (SpeakerNames.forget).
    func forgetNames() {
        var rules = SpeakerNames(names: names, autoNamed: autoNamed)
        rules.forget()
        (names, autoNamed) = (rules.names, rules.autoNamed)
    }
}
