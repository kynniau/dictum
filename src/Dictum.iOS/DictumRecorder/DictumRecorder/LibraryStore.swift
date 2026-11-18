import DictumBundle
import Foundation
import LocalAuthentication
import Security

// Meetings kept on this device ("Keep meetings on this device", default on; owner, 17/11/2026).
//
// - The sealed `.dictum` in Documents stays the audio store. Its entries are sealed under the meeting's content key; the
//   six-word code only wraps that key, so with the kept key the app opens its own bundle without the code.
// - The content key is kept in the Keychain behind Face ID / Touch ID with passcode fallback (`.userPresence`), usable
//   only while the device is unlocked, never synchronised and never leaving this device.
// - What the review made (transcript, names, summary, actions, confirmation time, facts) is one JSON record, sealed with
//   the same key in Dictum's DCTMENC framing at `Documents/Library/<meeting-hex>/record.json.enc` (file protection
//   complete). It is never inside the bundle, so the `.dictum` a computer imports is unchanged.
// - Plaintext records and plaintext audio exist only in memory.

// MARK: - The kept key

enum LibraryKeyStore {
    static let service = "com.kynni.dictum.recorder.library"

    enum KeyError: Error { case accessControl, keychain(OSStatus) }

    enum LoadOutcome {
        case key(Data)
        case missing
        case denied
        case failed(OSStatus)
    }

    private static func query(_ account: String?) -> [String: Any] {
        var q: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                kSecAttrService as String: service,
                                kSecAttrSynchronizable as String: false]
        if let account { q[kSecAttrAccount as String] = account }
        return q
    }

    /// Stores the key behind user presence (biometry or passcode), this device only, while unlocked. Saving never prompts.
    static func save(_ key: Data, meetingHex: String) throws {
        SecItemDelete(query(meetingHex) as CFDictionary)
        guard let access = SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
                                                           .userPresence, nil) else { throw KeyError.accessControl }
        var add = query(meetingHex)
        add[kSecValueData as String] = key
        add[kSecAttrAccessControl as String] = access
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeyError.keychain(status) }
    }

    /// The meeting ids with a kept key. Attributes only, through a context that may not show any prompt, so listing
    /// never asks for Face ID.
    static func accounts() -> Set<String> {
        let context = LAContext()
        context.interactionNotAllowed = true
        var q = query(nil)
        q[kSecReturnAttributes as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitAll
        q[kSecUseAuthenticationContext as String] = context
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let items = out as? [[String: Any]] else { return [] }
        return Set(items.compactMap { $0[kSecAttrAccount as String] as? String })
    }

    /// Reads the key with an already-authenticated context, so the Keychain does not prompt a second time. Blocking:
    /// call off the main thread.
    static func load(meetingHex: String, context: LAContext) -> LoadOutcome {
        var q = query(meetingHex)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        q[kSecUseAuthenticationContext as String] = context
        var out: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &out)
        switch status {
        case errSecSuccess:
            guard let data = out as? Data, data.count == 32 else { return .failed(status) }
            return .key(data)
        case errSecItemNotFound: return .missing
        case errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed: return .denied
        default: return .failed(status)
        }
    }

    static func delete(meetingHex: String) {
        SecItemDelete(query(meetingHex) as CFDictionary)
    }
}

/// One Face ID / passcode check opens meetings for a short while.
@MainActor
final class LibraryAuth {
    enum Outcome {
        case ok(LAContext)
        case denied
        /// No passcode (and so no Face ID) on this device: `.userPresence` items cannot be read.
        case unavailable
    }

    // simplification: an evaluated context is reused for 120 seconds (and dropped when the app leaves the foreground),
    // so opening a second meeting straight after the first does not prompt again. Ceiling: anyone holding the unlocked
    // phone in that window can open another meeting without a prompt. Retire if the owner wants a prompt per meeting
    // (set reuseSeconds to 0).
    static let reuseSeconds: TimeInterval = 120
    private var context: LAContext?
    private var authenticatedAt: Date?

    func authenticate() async -> Outcome {
        if let context, let at = authenticatedAt, Date().timeIntervalSince(at) < Self.reuseSeconds { return .ok(context) }
        invalidate()
        let fresh = LAContext()
        fresh.localizedReason = PROPOSED_FaceIDReason
        var error: NSError?
        guard fresh.canEvaluatePolicy(.deviceOwnerAuthentication, error: &error) else { return .unavailable }
        do {
            guard try await fresh.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: PROPOSED_FaceIDReason) else {
                return .denied
            }
        } catch {
            return .denied
        }
        context = fresh
        authenticatedAt = Date()
        return .ok(fresh)
    }

    func invalidate() {
        context?.invalidate()
        context = nil
        authenticatedAt = nil
    }
}

// MARK: - The meeting record

/// What is kept about a meeting beside its sealed bundle. Sealed whole; never written in plaintext.
struct MeetingRecord: Codable, Equatable {
    static let currentSchema = 1

    struct Segment: Codable, Equatable {
        /// Seconds from the start of the recording, when known.
        var start: Double?
        var end: Double?
        var text: String
        /// Speaker number (1 = first voice heard), when the speaker step labelled it.
        var speaker: Int?
    }

    struct Action: Codable, Equatable {
        var owner: String
        var text: String
        var byWhen: String
    }

    struct Facts: Codable, Equatable {
        var engine: String?
        var locale: String?
        var speakersFound: Int?
        var threshold: Double?
        /// How many "Your words" fixes were made in this transcript; nil when it was made before 18/11/2026.
        var wordFixes: Int?
    }

    var schemaVersion = MeetingRecord.currentSchema
    var title: String
    var meetingTypeId: String?
    var startedAt: Date
    var duration: TimeInterval
    var segments: [Segment] = []
    /// Names given to speaker numbers.
    var speakerNames: [Int: String] = [:]
    var summary = ""
    var actions: [Action] = []
    var confirmedAt: Date?
    var facts = Facts()

    /// The minimum the bundle itself can tell: title, start and duration from its sealed metadata.
    static func minimal(_ meta: BundleMeta) -> MeetingRecord {
        MeetingRecord(title: meta.title, meetingTypeId: nil, startedAt: meta.startedAt, duration: meta.duration)
    }
}

enum LibraryFiles {
    static let entryName = "record.json.enc"

    static var root: URL { SessionFiles.documents.appendingPathComponent("Library", isDirectory: true) }
    static func folder(_ hex: String) -> URL { root.appendingPathComponent(hex, isDirectory: true) }
    static func recordURL(_ hex: String) -> URL { folder(hex).appendingPathComponent(entryName) }

    static func hasRecord(_ hex: String) -> Bool { FileManager.default.fileExists(atPath: recordURL(hex).path) }

    /// Seals the record (identity `<meeting-id>/record.json.enc`) and replaces the file atomically, protection complete.
    static func write(_ record: MeetingRecord, contentKey: Data, meetingId: MeetingId) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        var plain = try encoder.encode(record)
        defer { plain.resetBytes(in: 0..<plain.count) }
        var bytes = SystemByteSource()
        let sealed = try DictumBundle.SealedFile.seal(plaintext: plain, contentKey: contentKey, meetingId: meetingId,
                                         entryName: entryName, bytes: &bytes)
        let protection: [FileAttributeKey: Any] = [.protectionKey: FileProtectionType.complete]
        try FileManager.default.createDirectory(at: folder(meetingId.hex), withIntermediateDirectories: true, attributes: protection)
        try sealed.write(to: recordURL(meetingId.hex), options: [.atomic, .completeFileProtection])
    }

    /// The verified record, or nil when there is none yet.
    static func read(contentKey: Data, meetingId: MeetingId) throws -> MeetingRecord? {
        let url = recordURL(meetingId.hex)
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        var plain = try DictumBundle.SealedFile.open(try Data(contentsOf: url), contentKey: contentKey, meetingId: meetingId, entryName: entryName)
        defer { plain.resetBytes(in: 0..<plain.count) }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(MeetingRecord.self, from: plain)
    }

    static func remove(_ hex: String) {
        try? FileManager.default.removeItem(at: folder(hex))
    }
}

// MARK: - The list

/// One `.dictum` in Documents, with what can be known without unlocking it.
struct LibraryEntry: Identifiable, Equatable {
    let file: SealedFile
    /// From the plaintext manifest; nil when the file could not be read as a bundle.
    let meetingHex: String?
    /// A key for it is kept on this device (or its record exists, which only a kept key could have written).
    let saved: Bool
    /// Seconds, estimated from the audio entry's length in the manifest.
    let durationEstimate: TimeInterval?

    var id: URL { file.url }
}

enum Library {
    /// Every bundle in Documents, newest first, with no prompt and no decryption.
    static func entries() -> [LibraryEntry] {
        let accounts = LibraryKeyStore.accounts()
        return SealedFiles.list().map { file in
            let info = try? BundleFile.inspect(url: file.url)
            let hex = info?.meetingId.hex
            let audio = info?.files.first { $0.name == "audio-mixed.chunks.enc" }?.bytes
            return LibraryEntry(file: file, meetingHex: hex,
                                saved: hex.map { accounts.contains($0) || LibraryFiles.hasRecord($0) } ?? false,
                                durationEstimate: audio.map(estimatedSeconds))
        }
    }

    /// The package writes one-second records of 32,000 PCM bytes, each with 60 bytes of framing, after a 24-byte header,
    /// so the audio entry is about 24 + 32,060 bytes per second. An estimate, shown as "about".
    static func estimatedSeconds(audioBytes: Int64) -> TimeInterval {
        max(0, Double(audioBytes - 24) / 32_060)
    }

    /// For bundles with no kept key: a key the earlier build may have left under the crash-recovery service is checked
    /// against the bundle and, if it opens it, moved to the library. Expected to find nothing, because that build
    /// deleted the key at seal. Blocking; call off the main thread.
    static func adoptLeftoverKeys(_ entries: [LibraryEntry]) -> Int {
        var adopted = 0
        for entry in entries where !entry.saved {
            guard let hex = entry.meetingHex, let id = MeetingId(hex: hex),
                  var key = ContentKeyStore.load(meetingHex: hex) else { continue }
            defer { key.resetBytes(in: 0..<key.count) }
            guard let meta = try? BundleFile.meta(url: entry.file.url, contentKey: key) else { continue }
            do {
                try LibraryKeyStore.save(key, meetingHex: hex)
                if !LibraryFiles.hasRecord(hex) { try LibraryFiles.write(.minimal(meta), contentKey: key, meetingId: id) }
                ContentKeyStore.delete(meetingHex: hex)
                adopted += 1
            } catch {
                continue
            }
        }
        return adopted
    }

    /// Deletes everything this device holds for one meeting: the bundle, its record folder and its kept key.
    static func delete(url: URL, meetingHex: String?) -> Bool {
        // The key and record go only once the bundle has gone, so a failed delete never leaves a file nobody can open.
        do { try FileManager.default.removeItem(at: url) } catch { return false }
        if let hex = meetingHex {
            LibraryFiles.remove(hex)
            LibraryKeyStore.delete(meetingHex: hex)
        }
        return true
    }

    /// The script versions in the bundle's sealed confirmation history, oldest first (empty when it has none).
    static func scriptVersions(url: URL, contentKey: Data, meetingId: MeetingId) -> [String] {
        guard let sealed = try? BundleFile.entry(url: url, name: "confirmations.json.enc"),
              var plain = try? DictumBundle.SealedFile.open(sealed, contentKey: contentKey, meetingId: meetingId,
                                                           entryName: "confirmations.json.enc") else { return [] }
        defer { plain.resetBytes(in: 0..<plain.count) }
        let items = (try? JSONSerialization.jsonObject(with: plain)) as? [[String: Any]] ?? []
        return items.compactMap { $0["ScriptVersion"] as? String }
    }

    /// True when a transcript may be made on this device: the meeting was recorded here under the keep-on notice, which
    /// tells the people in it so. A recording brought in counts only in the owner's test mode, because the people on it
    /// were not told (OnDeviceNotice, CallImport.swift). Meetings under a keep-off notice were told the computer makes
    /// them, so they are never transcribed on the phone.
    static func consentCoversOnDevice(_ versions: [String]) -> Bool {
        OnDeviceNotice.covers(versions, testMode: TestMode.isOn)
    }
}
