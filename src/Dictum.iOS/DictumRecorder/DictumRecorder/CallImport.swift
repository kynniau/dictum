import AVFAudio
import Foundation

// Bringing in a recording made outside Dictum Recorder: a call recorded by the phone's own call recorder, a dictaphone
// file, any sound file handed over by the share menu or picked in Files. The app cannot record a phone call itself (the
// system gives no app the call's sound), so this is the way a call gets in.
//
// This file is the part with no screen and no bundle: the wording, the notice that is recorded, which notices allow a
// transcript on this device, reading a sound file as 16 kHz mono PCM16 in memory, and the rule for the copy the system
// hands over. CallImportFlow.swift seals it and shows the screen; RecordingController.swift moves between screens.
//
// The strings sit here, not in Text.swift, only because another live session owned Text.swift when this was written
// (18/11/2026). Move them there when the two branches are joined.

// MARK: - Wording (all proposed; consent wording is the owner's and counsel's to approve)

let PROPOSED_ImportButton = "Bring in a recording"
let PROPOSED_ImportHeading = "Before you bring in a recording"
let PROPOSED_ImportAgreement = "Only bring in a recording when everyone on it was told it was being recorded and agreed. If you are not sure, do not bring it in."

let PROPOSED_ImportPointCopy = "Dictum Recorder makes its own encrypted copy of the sound on this phone. No sound is stored in the clear by this app."
let PROPOSED_ImportPointOriginal = "The recording you chose stays where it is, for example in Notes or Files, and is not encrypted by this app. Delete it there yourself if you do not want it kept."
let PROPOSED_ImportPoints = [PROPOSED_ImportPointCopy, PROPOSED_PointTransfer, PROPOSED_PointProcessing, PROPOSED_ImportPointOriginal]
let PROPOSED_ImportPointsKept = [PROPOSED_ImportPointCopy, PROPOSED_PointTransfer, PROPOSED_PointProcessingKept, PROPOSED_ImportPointOriginal]

let PROPOSED_ImportAnnounced = "The phone announced the recording to everyone"
let PROPOSED_ImportChooseHow = "Choose how everyone agreed before you bring it in."
let PROPOSED_ImportTitleLabel = "Title (optional)"
/// Stored as the title when the title field is left empty.
let PROPOSED_ImportDefaultTitle = "Recording brought in"
/// %@ is the length, for example "12 min 03 s".
let PROPOSED_ImportLength = "Length: %@"
let PROPOSED_ImportConfirm = "I confirm that everyone on this recording agreed. Bring it in"
let PROPOSED_ImportUnreadable = "That file could not be read as a sound recording."
let PROPOSED_ImportFailed = "The recording could not be brought in. The file you chose has not been changed."
let PROPOSED_ImportBusy = "That recording was not brought in because the app was busy. Share it again from this screen."

let PROPOSED_ImportScriptVersion = "PROPOSED-ios-import-2026-11-18-v1"
let PROPOSED_ImportScriptVersionKept = "PROPOSED-ios-import-kept-2026-11-18-v1"

// MARK: - The notice recorded with a recording brought in

/// What the bundle records about agreement for a recording brought in: an Import-kind agreement (MEETINGS-CONTRACTS
/// 14.A.1: `Kind` Live = 0, Import = 1), one of the three `How` values the desktop reader accepts, and the exact text
/// shown on the screen with its version.
enum ImportNotice {
    /// `AgreementKind.Import`.
    static let kind = 1
    /// The choices offered, in the order shown. The desktop refuses a bundle whose `How` is anything else.
    static let hows = ["platform-announced", "spoken", "written"]

    static func points(keep: Bool) -> [String] { keep ? PROPOSED_ImportPointsKept : PROPOSED_ImportPoints }
    static func version(keep: Bool) -> String { keep ? PROPOSED_ImportScriptVersionKept : PROPOSED_ImportScriptVersion }
    /// The exact text shown, saved in the bundle's confirmation as the script that was shown.
    static func script(keep: Bool) -> String {
        PROPOSED_ImportAgreement + "\n\n" + points(keep: keep).joined(separator: "\n")
    }

    static func label(how: String) -> String {
        switch how {
        case "platform-announced": return PROPOSED_ImportAnnounced
        case "written": return APPROVED_Written
        default: return APPROVED_Spoken
        }
    }
}

/// Which notices allow a transcript to be made on this device.
///
/// The keep-on notice read to a room says transcripts and summaries can be made and kept on this device, so it covers
/// it. The notice confirmed for a recording brought in does not: the people on that recording were told only that it
/// was being recorded (the phone's own announcement is one of the answers), not that a transcript would be made on
/// this phone. So a recording brought in is transcribed here only in the owner's test mode (TestMode, DEBUG builds),
/// until an import notice that tells people so is approved (owner's decision, 18/11/2026). Every other notice (the
/// keep-off notices say the computer makes them) is never transcribed on the phone.
enum OnDeviceNotice {
    /// Notices that tell the people in the meeting about transcripts on this device.
    static let told: Set<String> = [PROPOSED_ScriptVersionKept]
    /// Notices that cover it in the owner's test mode only.
    static let testOnly: Set<String> = [PROPOSED_ImportScriptVersionKept]

    /// `history` is the bundle's recorded script versions, oldest first; the first is the notice it was made under.
    static func covers(_ history: [String], testMode: Bool) -> Bool {
        guard let first = history.first else { return false }
        return told.contains(first) || (testMode && testOnly.contains(first))
    }
}

// MARK: - Reading the sound

enum ImportAudio {
    enum Failure: Error { case unreadable, noSound }

    /// The bundle's audio format: 16 kHz mono PCM16, as AudioCapture.outputFormat (repeated so this file stands alone
    /// in the self-check, which cannot compile the iOS audio session code).
    static let output = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!

    /// The length the file says it has, in seconds. Throws when the file is not a sound file the system can read.
    static func duration(url: URL) throws -> TimeInterval {
        let file = try AVAudioFile(forReading: url)
        let rate = file.fileFormat.sampleRate
        guard rate > 0 else { throw Failure.unreadable }
        return TimeInterval(file.length) / rate
    }

    /// When the file was made, for the meeting's start time.
    /// simplification: the file's creation date, which for a copy handed over by the share menu is when it was handed
    /// over, not when the call happened. Ceiling: the start time can be later than the real one. Retire by reading the
    /// recording's own date from its metadata once a real call recording has been inspected.
    static func created(_ url: URL) -> Date? {
        (try? url.resourceValues(forKeys: [.creationDateKey]))?.creationDate
    }

    /// Reads the whole file a piece at a time and hands `append` 16 kHz mono PCM16 (little-endian), never more than a
    /// second per call. Every channel is mixed in (`downmix`), so a call kept as one voice per channel loses neither.
    /// The decoded sound exists only in memory. Returns the number of samples handed over.
    static func decode(url: URL, append: (Data) throws -> Void) throws -> Int64 {
        let file = try AVAudioFile(forReading: url)
        let source = file.processingFormat
        guard source.sampleRate > 0, source.channelCount > 0,
              let converter = AVAudioConverter(from: source, to: output),
              let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: 32_768),
              let out = AVAudioPCMBuffer(pcmFormat: output, frameCapacity: 16_000) else { throw Failure.unreadable }
        converter.downmix = true

        var samples: Int64 = 0
        var readError: Error?
        while true {
            out.frameLength = 0
            var convertError: NSError?
            let status = converter.convert(to: out, error: &convertError) { _, state in
                guard readError == nil, file.framePosition < file.length else {
                    state.pointee = .endOfStream
                    return nil
                }
                do {
                    try file.read(into: input)
                } catch {
                    readError = error
                    state.pointee = .endOfStream
                    return nil
                }
                guard input.frameLength > 0 else {
                    state.pointee = .endOfStream
                    return nil
                }
                state.pointee = .haveData
                return input
            }
            if let readError { throw readError }
            if status == .error { throw convertError ?? Failure.unreadable }
            if out.frameLength > 0, let channel = out.int16ChannelData {
                try append(Data(bytes: channel[0], count: Int(out.frameLength) * 2))
                samples += Int64(out.frameLength)
            }
            if status == .endOfStream || out.frameLength == 0 { break }
        }
        return samples
    }

    /// "12 min 03 s", or "45 s" under a minute.
    static func lengthText(_ seconds: TimeInterval) -> String {
        let whole = max(0, Int(seconds.rounded()))
        return whole < 60 ? "\(whole) s" : String(format: "%d min %02d s", whole / 60, whole % 60)
    }
}

// MARK: - The copy the system hands over

/// The share menu hands the app its own copy of the file, in `Documents/Inbox`, not encrypted by this app. Documents
/// shows in the Files app and is backed up, so that copy is moved at once into a holding folder in the app's temporary
/// folder (not shown in Files, not backed up), and removed as soon as it has been sealed, turned away, or the app
/// leaves the screen. A file opened where it lives (picked in Files) is the user's original and is never moved or
/// removed.
enum ImportInbox {
    static let folder = "Inbox"
    static let holdingFolder = "Import"
    /// A handed-over copy still in Inbox this long after it arrived was left by a hand-over the app never received.
    static let staleAfter: TimeInterval = 3600

    /// Where a handed-over copy waits while the bring-in screen is open.
    static var holding: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent(holdingFolder, isDirectory: true)
    }

    /// True only for a file directly inside `folder`. Paths are compared as given: resolve links first.
    static func isDirectlyInside(_ path: String, folder: String) -> Bool {
        let root = folder.hasSuffix("/") ? folder : folder + "/"
        guard path.hasPrefix(root) else { return false }
        let name = path.dropFirst(root.count)
        return !name.isEmpty && !name.contains("/")
    }

    /// True only for a file directly inside `<documents>/Inbox/`.
    static func isHandedOver(_ path: String, documents: String) -> Bool {
        isDirectlyInside(path, folder: (documents.hasSuffix("/") ? documents : documents + "/") + folder)
    }

    static func isStale(modified: Date, now: Date) -> Bool {
        now.timeIntervalSince(modified) > staleAfter
    }

    /// Moves a handed-over copy out of `Documents/Inbox` into the holding folder and returns where it now is. Any other
    /// file, and a copy that could not be moved, is returned as it came.
    static func takeIn(_ url: URL, documents: URL, holding: URL = holding) -> URL {
        guard isHandedOver(url.resolvingSymlinksInPath().path, documents: documents.resolvingSymlinksInPath().path) else { return url }
        let fm = FileManager.default
        do {
            try fm.createDirectory(at: holding, withIntermediateDirectories: true)
            var folder = holding
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            try? folder.setResourceValues(values)
            // The file keeps its own name after the prefix, so the sound decoder still sees its type.
            let held = holding.appendingPathComponent(UUID().uuidString + "-" + url.lastPathComponent)
            try fm.moveItem(at: url, to: held)
            try? fm.setAttributes([.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication], ofItemAtPath: held.path)
            return held
        } catch {
            return url
        }
    }

    /// Removes `url` when it is a handed-over copy, in Inbox or in the holding folder; leaves every other file alone.
    static func remove(_ url: URL, documents: URL, holding: URL = holding) {
        let path = url.resolvingSymlinksInPath().path
        guard isHandedOver(path, documents: documents.resolvingSymlinksInPath().path)
            || isDirectlyInside(path, folder: holding.resolvingSymlinksInPath().path) else { return }
        try? FileManager.default.removeItem(at: url)
    }

    /// At launch: empties the holding folder (nothing can be waiting there before a file has been opened), and removes
    /// copies left in Inbox by a hand-over the app never received.
    /// simplification: an Inbox copy goes only once it has been there longer than `staleAfter`, because the app can be
    /// launched to receive a copy that arrived a moment before. Ceiling: a copy the app never received stays in Inbox,
    /// unencrypted by this app, for up to an hour and until the next launch. Retire if the system offers a way to tell
    /// the copy being delivered from the rest.
    static func sweep(documents: URL, holding: URL = holding, now: Date = Date()) {
        let fm = FileManager.default
        for item in (try? fm.contentsOfDirectory(at: holding, includingPropertiesForKeys: nil)) ?? [] {
            try? fm.removeItem(at: item)
        }
        let inbox = documents.appendingPathComponent(folder, isDirectory: true)
        // When the copy was put in Inbox, not when the sound was last changed: a copy keeps its original's dates, so
        // an old recording shared in a moment ago would otherwise look stale.
        let keys: [URLResourceKey] = [.addedToDirectoryDateKey, .creationDateKey]
        for item in (try? fm.contentsOfDirectory(at: inbox, includingPropertiesForKeys: keys)) ?? [] {
            let values = try? item.resourceValues(forKeys: Set(keys))
            // A copy with no date it arrived by is left: better an hour's leftover than a file removed as it arrives.
            guard let arrived = values?.addedToDirectoryDate else { continue }
            if isStale(modified: arrived, now: now) { remove(item, documents: documents, holding: holding) }
        }
    }
}

#if DEBUG
/// One line per check, "PASS …" or "FAIL …". Needs no device: the sound checks write a made-up tone to the temporary
/// folder, read it back through `ImportAudio.decode`, and delete it.
enum CallImportSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }

        // The notice: what the desktop reader accepts, and never the same version as a notice read to a room.
        check("notice: an Import-kind agreement", ImportNotice.kind, 1)
        check("notice: only the three accepted answers", ImportNotice.hows, ["platform-announced", "spoken", "written"])
        check("notice: four points either way", [ImportNotice.points(keep: false).count, ImportNotice.points(keep: true).count], [4, 4])
        check("notice: four different versions",
              Set([PROPOSED_ScriptVersion, PROPOSED_ScriptVersionKept, ImportNotice.version(keep: false), ImportNotice.version(keep: true)]).count, 4)
        check("notice: the script recorded is the text shown",
              ImportNotice.script(keep: true), PROPOSED_ImportAgreement + "\n\n" + PROPOSED_ImportPointsKept.joined(separator: "\n"))
        check("notice: keep-on says the transcript is made here", ImportNotice.points(keep: true).contains(PROPOSED_PointProcessingKept), true)
        check("notice: keep-off says the computer makes it", ImportNotice.points(keep: false).contains(PROPOSED_PointProcessing), true)

        // Which notices allow a transcript on this device.
        check("on-device: the keep-on room notice", OnDeviceNotice.covers([PROPOSED_ScriptVersionKept], testMode: false), true)
        check("on-device: the keep-on room notice, test mode on", OnDeviceNotice.covers([PROPOSED_ScriptVersionKept], testMode: true), true)
        check("on-device: a recording brought in is not covered outside test mode",
              OnDeviceNotice.covers([ImportNotice.version(keep: true)], testMode: false), false)
        check("on-device: a recording brought in is covered in test mode",
              OnDeviceNotice.covers([ImportNotice.version(keep: true)], testMode: true), true)
        check("on-device: not the keep-off room notice", OnDeviceNotice.covers([PROPOSED_ScriptVersion], testMode: false), false)
        check("on-device: not the keep-off room notice, even in test mode", OnDeviceNotice.covers([PROPOSED_ScriptVersion], testMode: true), false)
        check("on-device: not the keep-off brought-in notice, even in test mode",
              OnDeviceNotice.covers([ImportNotice.version(keep: false)], testMode: true), false)
        check("on-device: no notice, no transcript", OnDeviceNotice.covers([], testMode: true), false)
        check("on-device: the first notice decides",
              OnDeviceNotice.covers([ImportNotice.version(keep: false), ImportNotice.version(keep: true)], testMode: true), false)
        check("on-device: a later room notice does not cover a recording brought in",
              OnDeviceNotice.covers([ImportNotice.version(keep: true), PROPOSED_ScriptVersionKept], testMode: false), false)

        // The handed-over copy: only a file directly in Documents/Inbox is ever removed.
        check("inbox: a file in Inbox", ImportInbox.isHandedOver("/d/Documents/Inbox/call.m4a", documents: "/d/Documents"), true)
        check("inbox: trailing slash on Documents", ImportInbox.isHandedOver("/d/Documents/Inbox/call.m4a", documents: "/d/Documents/"), true)
        check("inbox: not a file beside the meetings", ImportInbox.isHandedOver("/d/Documents/call.m4a", documents: "/d/Documents"), false)
        check("inbox: not a sealed meeting", ImportInbox.isHandedOver("/d/Documents/meeting-2026-11-18-00000000.dictum", documents: "/d/Documents"), false)
        check("inbox: not the folder itself", ImportInbox.isHandedOver("/d/Documents/Inbox/", documents: "/d/Documents"), false)
        check("inbox: not a folder with a longer name", ImportInbox.isHandedOver("/d/Documents/Inboxes/call.m4a", documents: "/d/Documents"), false)
        check("inbox: not deeper than Inbox", ImportInbox.isHandedOver("/d/Documents/Inbox/sub/call.m4a", documents: "/d/Documents"), false)
        check("inbox: not another app's Inbox", ImportInbox.isHandedOver("/other/Documents/Inbox/call.m4a", documents: "/d/Documents"), false)
        check("holding: a file in the holding folder", ImportInbox.isDirectlyInside("/t/tmp/Import/abc-call.m4a", folder: "/t/tmp/Import"), true)
        check("holding: not the holding folder itself", ImportInbox.isDirectlyInside("/t/tmp/Import/", folder: "/t/tmp/Import"), false)
        check("holding: not deeper than the holding folder", ImportInbox.isDirectlyInside("/t/tmp/Import/x/call.m4a", folder: "/t/tmp/Import"), false)
        check("holding: not a file beside the holding folder", ImportInbox.isDirectlyInside("/t/tmp/call.m4a", folder: "/t/tmp/Import"), false)
        // Real files, in a made-up Documents and holding folder under the temporary folder.
        let fm = FileManager.default
        let base = fm.temporaryDirectory.appendingPathComponent("import-check-" + UUID().uuidString, isDirectory: true)
        let docs = base.appendingPathComponent("Documents", isDirectory: true)
        let inbox = docs.appendingPathComponent(ImportInbox.folder, isDirectory: true)
        let hold = base.appendingPathComponent("tmp/Import", isDirectory: true)
        try? fm.createDirectory(at: inbox, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: base) }
        func put(_ url: URL) -> URL { fm.createFile(atPath: url.path, contents: Data([1, 2, 3])); return url }
        func there(_ url: URL) -> Bool { fm.fileExists(atPath: url.path) }
        let shared = put(inbox.appendingPathComponent("call.m4a"))
        let held = ImportInbox.takeIn(shared, documents: docs, holding: hold)
        check("take in: the copy leaves Inbox", there(shared), false)
        check("take in: it is now in the holding folder",
              ImportInbox.isDirectlyInside(held.resolvingSymlinksInPath().path, folder: hold.resolvingSymlinksInPath().path) && there(held), true)
        check("take in: it keeps its type", held.pathExtension, "m4a")
        check("take in: the sound is the same", (try? Data(contentsOf: held)) ?? Data(), Data([1, 2, 3]))
        let own = put(docs.appendingPathComponent("picked.m4a"))
        check("take in: a file that is not a handed-over copy is not moved", ImportInbox.takeIn(own, documents: docs, holding: hold) == own && there(own), true)
        ImportInbox.remove(own, documents: docs, holding: hold)
        check("remove: a file that is not a handed-over copy is never removed", there(own), true)
        ImportInbox.remove(held, documents: docs, holding: hold)
        check("remove: the held copy goes", there(held), false)
        let waiting = put(inbox.appendingPathComponent("just-arrived.m4a"))
        try? fm.createDirectory(at: hold, withIntermediateDirectories: true)
        let left = put(hold.appendingPathComponent("left-behind.m4a"))
        ImportInbox.sweep(documents: docs, holding: hold)
        check("sweep: the holding folder is emptied", there(left), false)
        check("sweep: a copy that just arrived in Inbox stays", there(waiting), true)
        check("sweep: the user's own file stays", there(own), true)
        ImportInbox.sweep(documents: docs, holding: hold, now: Date().addingTimeInterval(ImportInbox.staleAfter + 60))
        check("sweep: an Inbox copy over an hour old goes", there(waiting), false)
        let now = Date(timeIntervalSince1970: 1_000_000)
        check("inbox: a copy that just arrived stays", ImportInbox.isStale(modified: now.addingTimeInterval(-5), now: now), false)
        check("inbox: a copy an hour old stays", ImportInbox.isStale(modified: now.addingTimeInterval(-3600), now: now), false)
        check("inbox: an older copy goes", ImportInbox.isStale(modified: now.addingTimeInterval(-3601), now: now), true)

        check("length: under a minute", ImportAudio.lengthText(45.4), "45 s")
        check("length: minutes and seconds", ImportAudio.lengthText(723), "12 min 03 s")

        // Sound. Two seconds of stereo at 44.1 kHz: a tone on the left only in the first second, a tone on the right
        // only in the second. Read back as mono, both seconds must hold sound: a reader that kept one channel would
        // leave one of them silent, which on a call kept one voice per channel would lose a whole speaker.
        let stereo = tone(rate: 44_100, channels: 2, seconds: 2) { channel, second in channel == second }
        check("sound: stereo 44.1 kHz gives two seconds at 16 kHz", near(stereo.samples, 32_000), true)
        check("sound: every piece is whole samples, a second at most", stereo.pieces, true)
        check("sound: the left channel's second is kept", stereo.loud.first ?? false, true)
        check("sound: the right channel's second is kept", stereo.loud.last ?? false, true)
        // One second of mono at 8 kHz, the rate of a phone line.
        let narrow = tone(rate: 8_000, channels: 1, seconds: 1) { _, _ in true }
        check("sound: mono 8 kHz gives one second at 16 kHz", near(narrow.samples, 16_000), true)
        check("sound: the 8 kHz tone is kept", narrow.loud, [true])
        let missing = (try? ImportAudio.duration(url: FileManager.default.temporaryDirectory.appendingPathComponent("dictum-selfcheck-none.caf"))) == nil
        check("sound: a file that is not there is refused", missing, true)
        return out
    }

    /// Within 1% of `want` (the resampler may add or drop a few samples at the ends).
    private static func near(_ got: Int64, _ want: Int64) -> Bool { abs(got - want) * 100 <= want }

    /// Writes a made-up tone to the temporary folder (`on(channel, second)` says where it sounds), reads it back
    /// through `ImportAudio.decode`, deletes it, and reports the samples read, whether every piece was a whole number
    /// of samples and a second at most, and which seconds held sound.
    private static func tone(rate: Double, channels: AVAudioChannelCount, seconds: Int,
                             on: (Int, Int) -> Bool) -> (samples: Int64, pieces: Bool, loud: [Bool]) {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("dictum-selfcheck-\(UUID().uuidString).caf")
        defer { try? FileManager.default.removeItem(at: url) }
        guard let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: channels) else { return (0, false, []) }
        let frames = AVAudioFrameCount(rate) * AVAudioFrameCount(seconds)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames), let data = buffer.floatChannelData else { return (0, false, []) }
        buffer.frameLength = frames
        for channel in 0..<Int(channels) {
            for frame in 0..<Int(frames) {
                let second = frame / Int(rate)
                data[channel][frame] = on(channel, second) ? Float(0.5 * sin(2 * Double.pi * 440 * Double(frame) / rate)) : 0
            }
        }
        do {
            // Scoped so the writer is closed before the file is read back.
            try AVAudioFile(forWriting: url, settings: format.settings).write(from: buffer)
        } catch {
            return (0, false, [])
        }
        var pcm = Data()
        var pieces = true
        let samples = (try? ImportAudio.decode(url: url) { piece in
            if piece.isEmpty || piece.count % 2 != 0 || piece.count > 32_000 { pieces = false }
            pcm.append(piece)
        }) ?? 0
        var loud: [Bool] = []
        for second in 0..<seconds {
            let start = second * 32_000 + 3_200, end = min((second + 1) * 32_000 - 3_200, pcm.count) // the middle 0.8 s
            guard start < end else { loud.append(false); continue }
            var sum = 0.0
            for i in stride(from: start, to: end - 1, by: 2) {
                let value = Double(Int16(bitPattern: UInt16(pcm[i]) | (UInt16(pcm[i + 1]) << 8)))
                sum += value * value
            }
            loud.append((sum / Double((end - start) / 2)).squareRoot() > 1_000)
        }
        return (samples, pieces, loud)
    }
}
#endif
