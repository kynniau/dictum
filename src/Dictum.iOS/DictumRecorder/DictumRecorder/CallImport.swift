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

/// The notices that say transcripts and summaries can be made and kept on this device: the keep-on notice read to a
/// room, and the keep-on notice confirmed for a recording brought in. A meeting whose first recorded notice is any
/// other (the keep-off notices say the computer makes them) is never transcribed on the phone.
enum OnDeviceNotice {
    static let versions: Set<String> = [PROPOSED_ScriptVersionKept, PROPOSED_ImportScriptVersionKept]

    /// `history` is the bundle's recorded script versions, oldest first; the first is the notice it was made under.
    static func covers(_ history: [String]) -> Bool {
        history.first.map(versions.contains) ?? false
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

/// The share menu hands the app its own copy of the file, in `Documents/Inbox`, not encrypted by this app. That copy is
/// removed as soon as it has been sealed or turned away. A file opened where it lives (picked in Files) is the user's
/// original and is never removed.
enum ImportInbox {
    static let folder = "Inbox"
    /// A handed-over copy still there this long after it arrived was left by an import that never finished.
    static let staleAfter: TimeInterval = 3600

    /// True only for a file directly inside `<documents>/Inbox/`. Paths are compared after resolving links.
    static func isHandedOver(_ path: String, documents: String) -> Bool {
        let inbox = (documents.hasSuffix("/") ? documents : documents + "/") + folder + "/"
        guard path.hasPrefix(inbox) else { return false }
        let name = path.dropFirst(inbox.count)
        return !name.isEmpty && !name.contains("/")
    }

    static func isStale(modified: Date, now: Date) -> Bool {
        now.timeIntervalSince(modified) > staleAfter
    }

    /// Removes `url` when it is a handed-over copy; leaves every other file alone.
    static func remove(_ url: URL, documents: URL) {
        guard isHandedOver(url.resolvingSymlinksInPath().path, documents: documents.resolvingSymlinksInPath().path) else { return }
        try? FileManager.default.removeItem(at: url)
    }

    /// At launch: removes handed-over copies left by an import that never finished.
    /// simplification: only copies older than `staleAfter` go, because the app can be launched to receive a copy that
    /// arrived a moment before. Ceiling: a copy from an import cut short stays, unencrypted by this app, for up to an
    /// hour and until the next launch. Retire if the system offers a way to tell the copy being delivered from the rest.
    static func sweep(documents: URL, now: Date = Date()) {
        let inbox = documents.appendingPathComponent(folder, isDirectory: true)
        let items = (try? FileManager.default.contentsOfDirectory(at: inbox, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        for item in items {
            let modified = (try? item.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            if isStale(modified: modified, now: now) { remove(item, documents: documents) }
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
        check("on-device: the keep-on room notice", OnDeviceNotice.covers([PROPOSED_ScriptVersionKept]), true)
        check("on-device: the keep-on brought-in notice", OnDeviceNotice.covers([ImportNotice.version(keep: true)]), true)
        check("on-device: not the keep-off room notice", OnDeviceNotice.covers([PROPOSED_ScriptVersion]), false)
        check("on-device: not the keep-off brought-in notice", OnDeviceNotice.covers([ImportNotice.version(keep: false)]), false)
        check("on-device: no notice, no transcript", OnDeviceNotice.covers([]), false)
        check("on-device: the first notice decides",
              OnDeviceNotice.covers([ImportNotice.version(keep: false), ImportNotice.version(keep: true)]), false)

        // The handed-over copy: only a file directly in Documents/Inbox is ever removed.
        check("inbox: a file in Inbox", ImportInbox.isHandedOver("/d/Documents/Inbox/call.m4a", documents: "/d/Documents"), true)
        check("inbox: trailing slash on Documents", ImportInbox.isHandedOver("/d/Documents/Inbox/call.m4a", documents: "/d/Documents/"), true)
        check("inbox: not a file beside the meetings", ImportInbox.isHandedOver("/d/Documents/call.m4a", documents: "/d/Documents"), false)
        check("inbox: not a sealed meeting", ImportInbox.isHandedOver("/d/Documents/meeting-2026-11-18-00000000.dictum", documents: "/d/Documents"), false)
        check("inbox: not the folder itself", ImportInbox.isHandedOver("/d/Documents/Inbox/", documents: "/d/Documents"), false)
        check("inbox: not a folder with a longer name", ImportInbox.isHandedOver("/d/Documents/Inboxes/call.m4a", documents: "/d/Documents"), false)
        check("inbox: not deeper than Inbox", ImportInbox.isHandedOver("/d/Documents/Inbox/sub/call.m4a", documents: "/d/Documents"), false)
        check("inbox: not another app's Inbox", ImportInbox.isHandedOver("/other/Documents/Inbox/call.m4a", documents: "/d/Documents"), false)
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
