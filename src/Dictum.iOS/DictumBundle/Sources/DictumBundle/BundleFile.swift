// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation

/// What a bundle's plaintext manifest says. Reading it needs no code and no key.
public struct BundleManifest {
    public let meetingId: MeetingId
    /// The payload entries the manifest lists, in its order, with their exact byte lengths.
    public let files: [ManifestEntry]
    /// The PBKDF2 salt (16 bytes) and the 60-byte transfer wrap of the content key.
    public let salt: Data
    public let wrap: Data
    /// `createdBy.version` and `createdBy.platform`.
    public let appVersion: String
    public let platform: String
}

/// The few metadata fields the phone shows, read from the bundle's own sealed `meta.json.enc`.
public struct BundleMeta: Equatable, Sendable {
    public let title: String
    public let startedAt: Date
    public let duration: TimeInterval
    public let audioIncomplete: Bool
}

/// Reads and re-wraps the phone's own `.dictum` bundles (MEETINGS-CONTRACTS 14.A, 14.A.1).
///
/// This reader accepts what the package writes: classic ZIP, STORED entries only, no data descriptors, no ZIP64. A
/// deflated entry (which the desktop reader would accept) is refused as malformed.
// simplification: payload entries are read into memory whole (the audio of a 60-minute meeting is about 115 MB).
// Ceiling: a much longer meeting needs that much memory to read. Retire with a streaming record reader once a meeting
// over an hour has to be played or transcribed on the phone.
public enum BundleFile {
    static let manifestName = "manifest.json"
    static let metaName = "meta.json.enc"
    static let audioName = "audio-mixed.chunks.enc"
    static let maxManifestBytes = 1 << 20

    struct Entry {
        let name: String
        let crc: UInt32
        let size: Int64
        let localOffset: UInt64
        let dosDate: UInt16
        let dosTime: UInt16
    }

    // MARK: - Public

    /// Reads only the ZIP directory and the manifest entry, and checks the manifest against the entries.
    public static func inspect(url: URL) throws -> BundleManifest {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let entries = try directory(handle)
        return try manifest(handle, entries)
    }

    /// The content key, from the six-word transfer code: PBKDF2 over the normalised code and the manifest's salt, then
    /// the `transfer` wrap is opened. A wrong code throws `authenticationFailed`; a code that is not six words throws
    /// `codeRequired`.
    public static func unlock(url: URL, code: String) throws -> Data {
        let info = try inspect(url: url)
        var transferKey = try TransferCode.deriveKey(code: code, salt: info.salt)
        defer { transferKey.zeroise() }
        return try KeyWrap.unwrap(info.wrap, recipientKey: transferKey, recipientId: "transfer")
    }

    /// One payload entry's bytes (ciphertext for `.enc` entries), or nil when the bundle has no such entry.
    public static func entry(url: URL, name: String) throws -> Data? {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let entries = try directory(handle)
        guard let e = entries.first(where: { $0.name == name }) else { return nil }
        return try read(handle, e)
    }

    /// Title, start, duration and the interrupted-audio flag from the verified `meta.json.enc`.
    public static func meta(url: URL, contentKey: Data) throws -> BundleMeta {
        let info = try inspect(url: url)
        guard let sealed = try entry(url: url, name: metaName) else { throw DictumBundleError.malformed }
        var plain = try SealedFile.open(sealed, contentKey: contentKey, meetingId: info.meetingId, entryName: metaName)
        defer { plain.zeroise() }
        guard let object = try JSONSerialization.jsonObject(with: plain) as? [String: Any],
              let title = object["Title"] as? String,
              let started = object["StartedAt"] as? String, let startedAt = RecordingSession.parseUtcPrefix(started),
              let durationText = object["Duration"] as? String, let duration = parseTimeSpan(durationText) else {
            throw DictumBundleError.invalidMetadata
        }
        let incomplete = (object["AudioIncomplete"] as? Bool) ?? !((object["IncompleteAudioChannels"] as? [Int]) ?? []).isEmpty
        return BundleMeta(title: title, startedAt: startedAt, duration: duration, audioIncomplete: incomplete)
    }

    /// The mixed audio as PCM16 mono 16 kHz, every returned record verified; nil when the bundle holds no audio.
    /// The interrupted-snapshot rule is taken from the bundle's own metadata. `limit` caps the PCM bytes returned.
    public static func audio(url: URL, contentKey: Data, limit: Int? = nil) throws -> Data? {
        let info = try inspect(url: url)
        let meta = try meta(url: url, contentKey: contentKey)
        guard let sealed = try entry(url: url, name: audioName) else { return nil }
        return try ChunkedAudioReader.pcm(sealed, contentKey: contentKey, meetingId: info.meetingId, channel: "mixed",
                                          incomplete: meta.audioIncomplete, limit: limit)
    }

    /// Re-wraps the content key under a fresh code and salt, replacing the bundle at `url` (a temporary archive beside
    /// it, synced, then renamed over it). Every payload entry is copied byte for byte, with its own CRC, size and DOS
    /// time, in the same entry order; only `manifest.json` changes (fresh salt and wrap, same creator and file list).
    /// The key is first checked by opening the bundle's `meta.json.enc`, so a key that does not belong to the bundle
    /// is never wrapped. Byte-source order: salt (16), wrap nonce (12), then the code (12) when `newCode` is nil.
    public static func rewrap(url: URL, contentKey: Data, newCode: TransferCode? = nil,
                              bytes: inout some ByteSource) throws -> (url: URL, code: TransferCode) {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let entries = try directory(handle)
        let info = try manifest(handle, entries)
        guard let metaEntry = entries.first(where: { $0.name == metaName }) else { throw DictumBundleError.malformed }
        var plain = try SealedFile.open(try read(handle, metaEntry), contentKey: contentKey, meetingId: info.meetingId,
                                        entryName: metaName)
        plain.zeroise()

        let salt = bytes.bytes(16)
        let nonce = bytes.bytes(12)
        let code = newCode ?? TransferCode.random(using: &bytes)
        var transferKey = try TransferCode.deriveKey(code: code.text, salt: salt)
        defer { transferKey.zeroise() }
        let wrap = try KeyWrap.wrap(contentKey: contentKey, recipientKey: transferKey, recipientId: "transfer", nonce: nonce)
        let json = Manifest.json(meetingId: info.meetingId, appVersion: info.appVersion, platform: info.platform,
                                 salt: salt, wrap: wrap, files: info.files)

        var writer = ZipWriter()
        for e in entries {
            if e.name == manifestName {
                writer.addStored(name: e.name, data: json, dosDate: e.dosDate, dosTime: e.dosTime)
            } else {
                writer.addStored(name: e.name, fileURL: url, offset: try dataOffset(handle, e), size: e.size, crc: e.crc,
                                 dosDate: e.dosDate, dosTime: e.dosTime)
            }
        }
        try writer.write(to: url)
        return (url, code)
    }

    // MARK: - ZIP reading

    /// The central directory, in order. Refuses anything this package would not write.
    static func directory(_ handle: FileHandle) throws -> [Entry] {
        let fileSize = try handle.seekToEnd()
        guard fileSize >= 22 else { throw DictumBundleError.malformed }
        let tailLength = min(fileSize, 22 + 0xffff)
        try handle.seek(toOffset: fileSize - tailLength)
        let tail = try handle.read(upToCount: Int(tailLength)) ?? Data()
        guard tail.count == Int(tailLength) else { throw DictumBundleError.malformed }
        var end: Int?
        var i = tail.count - 22
        while i >= 0 {
            if tail.readLE(UInt32.self, at: i) == 0x0605_4b50 { end = i; break }
            i -= 1
        }
        guard let end else { throw DictumBundleError.malformed }
        let count = Int(tail.readLE(UInt16.self, at: end + 10))
        let centralSize = UInt64(tail.readLE(UInt32.self, at: end + 12))
        let centralOffset = UInt64(tail.readLE(UInt32.self, at: end + 16))
        guard centralOffset + centralSize <= fileSize else { throw DictumBundleError.malformed }
        try handle.seek(toOffset: centralOffset)
        let central = try handle.read(upToCount: Int(centralSize)) ?? Data()
        guard central.count == Int(centralSize) else { throw DictumBundleError.malformed }

        var entries: [Entry] = []
        var p = 0
        for _ in 0..<count {
            guard central.count - p >= 46, central.readLE(UInt32.self, at: p) == 0x0201_4b50 else { throw DictumBundleError.malformed }
            let flags = central.readLE(UInt16.self, at: p + 8)
            let method = central.readLE(UInt16.self, at: p + 10)
            let time = central.readLE(UInt16.self, at: p + 12)
            let date = central.readLE(UInt16.self, at: p + 14)
            let crc = central.readLE(UInt32.self, at: p + 16)
            let compressed = central.readLE(UInt32.self, at: p + 20)
            let size = central.readLE(UInt32.self, at: p + 24)
            let nameLength = Int(central.readLE(UInt16.self, at: p + 28))
            let extra = Int(central.readLE(UInt16.self, at: p + 30))
            let comment = Int(central.readLE(UInt16.self, at: p + 32))
            let local = UInt64(central.readLE(UInt32.self, at: p + 42))
            guard central.count - p >= 46 + nameLength + extra + comment else { throw DictumBundleError.malformed }
            guard method == 0, flags == 0, compressed == size, local < fileSize else { throw DictumBundleError.malformed }
            let nameData = central.subdata(in: central.startIndex + p + 46..<central.startIndex + p + 46 + nameLength)
            guard let name = String(data: nameData, encoding: .utf8), !name.isEmpty,
                  !name.contains("/"), !name.contains("\\") else { throw DictumBundleError.invalidEntryName }
            entries.append(Entry(name: name, crc: crc, size: Int64(size), localOffset: local, dosDate: date, dosTime: time))
            p += 46 + nameLength + extra + comment
        }
        guard Set(entries.map { $0.name.lowercased() }).count == entries.count else { throw DictumBundleError.invalidEntryName }
        return entries
    }

    /// Where the entry's data starts, after its local header (whose name must match the directory's).
    static func dataOffset(_ handle: FileHandle, _ e: Entry) throws -> UInt64 {
        try handle.seek(toOffset: e.localOffset)
        let header = try handle.read(upToCount: 30) ?? Data()
        guard header.count == 30, header.readLE(UInt32.self, at: 0) == 0x0403_4b50 else { throw DictumBundleError.malformed }
        let nameLength = Int(header.readLE(UInt16.self, at: 26))
        let extra = Int(header.readLE(UInt16.self, at: 28))
        let name = try handle.read(upToCount: nameLength) ?? Data()
        guard name == Data(e.name.utf8) else { throw DictumBundleError.malformed }
        return e.localOffset + 30 + UInt64(nameLength) + UInt64(extra)
    }

    /// The entry's stored bytes, checked against the directory's CRC.
    static func read(_ handle: FileHandle, _ e: Entry) throws -> Data {
        try handle.seek(toOffset: try dataOffset(handle, e))
        let data = try handle.read(upToCount: Int(e.size)) ?? Data()
        guard data.count == Int(e.size) else { throw DictumBundleError.malformed }
        var crc = Crc32()
        crc.update(data)
        guard crc.value == e.crc else { throw DictumBundleError.entryChanged }
        return data
    }

    /// Parses and checks the manifest per 14.A.1, and checks its file list against the ZIP entries exactly.
    static func manifest(_ handle: FileHandle, _ entries: [Entry]) throws -> BundleManifest {
        guard let m = entries.first(where: { $0.name == manifestName }), m.size <= maxManifestBytes else {
            throw DictumBundleError.malformed
        }
        let data = try read(handle, m)
        guard let o = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              o["format"] as? String == "dictum-bundle", integer(o["version"]) == 1,
              let meetingHex = o["meeting"] as? String, let meetingId = MeetingId(hex: meetingHex),
              let createdBy = o["createdBy"] as? [String: Any], createdBy["app"] as? String == "Dictum",
              let appVersion = createdBy["version"] as? String, !appVersion.trimmingCharacters(in: .whitespaces).isEmpty,
              let platform = createdBy["platform"] as? String, ["windows", "mac", "ios"].contains(platform),
              let transfer = o["transfer"] as? [String: Any], transfer["kdf"] as? String == "pbkdf2-hmac-sha256",
              integer(transfer["iterations"]) == 600_000,
              let saltText = transfer["salt"] as? String, let salt = Data(base64Encoded: saltText), salt.count == 16,
              let wrapText = transfer["wrap"] as? String, let wrap = Data(base64Encoded: wrapText), wrap.count == 60,
              let files = o["files"] as? [[String: Any]] else { throw DictumBundleError.malformed }
        if let kind = transfer["kind"], !(kind is NSNull), kind as? String != "transfer-code" { throw DictumBundleError.malformed }
        let listed = try files.map { f -> ManifestEntry in
            guard let name = f["name"] as? String, let bytes = integer(f["bytes"]), bytes >= 0 else { throw DictumBundleError.malformed }
            return ManifestEntry(name: name, bytes: bytes)
        }
        let payloads = entries.filter { $0.name != manifestName }
        guard listed.count == payloads.count, Set(listed.map(\.name)).count == listed.count,
              listed.allSatisfy({ l in payloads.contains { $0.name == l.name && $0.size == l.bytes } }) else {
            throw DictumBundleError.malformed
        }
        return BundleManifest(meetingId: meetingId, files: listed, salt: salt, wrap: wrap, appVersion: appVersion, platform: platform)
    }

    /// A JSON integer (not a boolean, not a fraction).
    static func integer(_ value: Any?) -> Int64? {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() else { return nil }
        let d = n.doubleValue
        guard d.rounded() == d, abs(d) < 9.0e15 else { return nil }
        return n.int64Value
    }

    /// .NET TimeSpan "c" text, `[-][d.]hh:mm:ss[.fffffff]`, as `JsonText.timeSpan` writes it.
    static func parseTimeSpan(_ text: String) -> TimeInterval? {
        var s = Substring(text)
        var sign = 1.0
        if s.hasPrefix("-") { sign = -1; s = s.dropFirst() }
        let clock = s.split(separator: ":", omittingEmptySubsequences: false)
        guard clock.count == 3 else { return nil }
        var days = 0.0
        var hoursText = clock[0]
        if let dot = hoursText.firstIndex(of: ".") {
            guard let d = Double(hoursText[..<dot]) else { return nil }
            days = d
            hoursText = hoursText[hoursText.index(after: dot)...]
        }
        guard let hours = Double(hoursText), let minutes = Double(clock[1]) else { return nil }
        let secondsText = clock[2]
        var seconds: Double
        if let dot = secondsText.firstIndex(of: ".") {
            guard let whole = Double(secondsText[..<dot]) else { return nil }
            let digits = secondsText[secondsText.index(after: dot)...]
            guard !digits.isEmpty, digits.count <= 7, digits.allSatisfy(\.isASCIIDigit), let f = Double(digits) else { return nil }
            seconds = whole + f / pow(10, Double(digits.count))
        } else {
            guard let whole = Double(secondsText) else { return nil }
            seconds = whole
        }
        return sign * (((days * 24 + hours) * 60 + minutes) * 60 + seconds)
    }
}

private extension Character {
    var isASCIIDigit: Bool { ("0"..."9").contains(self) }
}
