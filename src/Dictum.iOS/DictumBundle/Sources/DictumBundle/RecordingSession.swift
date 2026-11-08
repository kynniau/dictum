// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation

public struct FinishedBundle {
    public let url: URL
    public let transferCode: String
    public let bytes: Int64
    public let incomplete: Bool
}

/// One phone recording, sealed as it is captured, then packaged as a `.dictum` bundle.
///
/// Session directory contents (all ciphertext except the id file):
/// `meeting.id` (the 32-hex meeting id, which the bundle manifest also carries in plaintext; recovery needs
/// it because the id is bound into every AAD), `confirmations.json.enc`, `meta.json.enc`, `audio-mixed.chunks.enc`.
///
/// simplification: key material is held in Swift `Data` and zeroed in place when done, but copy-on-write and
/// CryptoKit's `SymmetricKey` may leave copies the package cannot reach; retire if a threat model names memory
/// forensics on an unlocked phone (move the key into a locked, explicitly managed buffer).
public final class RecordingSession {
    static let audioName = "audio-mixed.chunks.enc"
    static let metaName = "meta.json.enc"
    static let confirmationsName = "confirmations.json.enc"
    static let idName = "meeting.id"
    static let recordBytes = 32_000 // one second of PCM16 mono 16 kHz

    private let directory: URL
    private let meetingId: MeetingId
    private var contentKey: Data
    private let startedAt: Date
    private let title: String
    private let agreement: Agreement
    private let appVersion: String
    private var bytes: SharedBytes
    private let audio: ChunkedAudioWriter
    private var buffer = Data(count: RecordingSession.recordBytes)
    private var filled = 0
    private var finished = false

    public init(directory: URL, meetingId: MeetingId, contentKey: Data, startedAt: Date, title: String,
                agreement: Agreement, confirmations: [Confirmation], appVersion: String,
                bytes: some ByteSource = SystemByteSource()) throws {
        guard contentKey.count == 32 else { throw DictumBundleError.invalidKeyLength }
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        guard !fm.fileExists(atPath: directory.appendingPathComponent(Self.audioName).path) else { throw DictumBundleError.sessionExists }
        self.directory = directory
        self.meetingId = meetingId
        self.contentKey = Data(contentKey)
        self.startedAt = startedAt
        self.title = title
        self.agreement = agreement
        self.appVersion = appVersion
        var shared = SharedBytes(box: SharedByteBox(bytes))

        try Self.writeAtomic(Data(meetingId.hex.utf8), to: directory.appendingPathComponent(Self.idName))
        var plainConfirmations = ConfirmationsJson.json(confirmations)
        defer { plainConfirmations.zeroise() }
        try Self.writeAtomic(try SealedFile.seal(plaintext: plainConfirmations, contentKey: contentKey, meetingId: meetingId,
                                                 entryName: Self.confirmationsName, bytes: &shared),
                             to: directory.appendingPathComponent(Self.confirmationsName))
        let provisional = MeetingMeta(id: meetingId, title: title, startedAt: startedAt, duration: 0, source: 3,
                                      agreement: agreement, incompleteAudioChannels: [2])
        try Self.writeMeta(provisional, directory: directory, contentKey: contentKey, bytes: &shared)
        audio = try ChunkedAudioWriter(fileURL: directory.appendingPathComponent(Self.audioName), contentKey: contentKey,
                                       meetingId: meetingId, channel: "mixed", bytes: shared)
        self.bytes = shared
    }

    /// Buffers PCM16 little-endian mono 16 kHz into whole one-second records and writes each one as it fills.
    /// Any partial record (including a trailing odd byte) is held until the next append or `finish`.
    public func append(pcm16Mono16k pcm: Data) throws {
        guard !finished else { throw DictumBundleError.sessionFinished }
        var index = pcm.startIndex
        while index < pcm.endIndex {
            let take = min(Self.recordBytes - filled, pcm.endIndex - index)
            buffer.replaceSubrange(filled..<filled + take, with: pcm[index..<index + take])
            filled += take
            index += take
            if filled == Self.recordBytes {
                try audio.write(pcm: buffer)
                filled = 0
            }
        }
    }

    /// Completes the audio, seals the final metadata and packages the bundle beside the session directory.
    /// A final odd byte (half a sample) is dropped.
    public func finish(duration: TimeInterval, at: Date = Date(), transferCode: TransferCode? = nil) throws -> FinishedBundle {
        guard !finished else { throw DictumBundleError.sessionFinished }
        finished = true
        defer { buffer.zeroise(); contentKey.zeroise() }
        let tail = filled & ~1
        if tail > 0 { try audio.write(pcm: buffer.subdata(in: 0..<tail)) }
        filled = 0
        try audio.complete()
        let meta = MeetingMeta(id: meetingId, title: title, startedAt: startedAt, duration: duration, source: 3,
                               agreement: agreement, incompleteAudioChannels: [])
        try Self.writeMeta(meta, directory: directory, contentKey: contentKey, bytes: &bytes)
        let code = transferCode ?? TransferCode.random(using: &bytes)
        return try Self.package(directory: directory, meetingId: meetingId, contentKey: contentKey, startedAt: startedAt,
                                appVersion: appVersion, code: code, at: at, incomplete: false, bytes: &bytes)
    }

    /// Packages an unfinished session (the app was killed): the audio stays as it is (no terminal record) and the
    /// sealed metadata on disk is kept, so a provisional `IncompleteAudioChannels [2]` stays. Returns nil when the
    /// directory holds no audio file.
    public static func recover(directory: URL, appVersion: String, contentKey: Data,
                               bytes: some ByteSource = SystemByteSource()) throws -> FinishedBundle? {
        let fm = FileManager.default
        guard fm.fileExists(atPath: directory.appendingPathComponent(audioName).path) else { return nil }
        guard let idData = try? Data(contentsOf: directory.appendingPathComponent(idName)),
              let id = MeetingId(hex: String(decoding: idData, as: UTF8.self)) else { throw DictumBundleError.sessionMissingId }
        var plain = try SealedFile.open(try Data(contentsOf: directory.appendingPathComponent(metaName)),
                                        contentKey: contentKey, meetingId: id, entryName: metaName)
        defer { plain.zeroise() }
        guard let object = try JSONSerialization.jsonObject(with: plain) as? [String: Any],
              let started = object["StartedAt"] as? String, let startedAt = parseUtcPrefix(started),
              let channels = object["IncompleteAudioChannels"] as? [Int] else { throw DictumBundleError.invalidMetadata }
        var source = SharedBytes(box: SharedByteBox(bytes))
        let code = TransferCode.random(using: &source)
        return try package(directory: directory, meetingId: id, contentKey: contentKey, startedAt: startedAt,
                           appVersion: appVersion, code: code, at: Date(), incomplete: !channels.isEmpty, bytes: &source)
    }

    // MARK: - Internals

    private static func package(directory: URL, meetingId: MeetingId, contentKey: Data, startedAt: Date, appVersion: String,
                                code: TransferCode, at: Date, incomplete: Bool, bytes: inout SharedBytes) throws -> FinishedBundle {
        let salt = bytes.bytes(16)
        var transferKey = try TransferCode.deriveKey(code: code.text, salt: salt)
        defer { transferKey.zeroise() }
        let wrap = try KeyWrap.wrap(contentKey: contentKey, recipientKey: transferKey, recipientId: "transfer", nonce: bytes.bytes(12))
        let names = [audioName, confirmationsName, metaName]
        let urls = names.map { directory.appendingPathComponent($0) }
        let sizes = try urls.map { try fileSize($0) }
        let manifest = Manifest.json(meetingId: meetingId, appVersion: appVersion, platform: "ios", salt: salt, wrap: wrap,
                                     files: zip(names, sizes).map { ManifestEntry(name: $0, bytes: $1) })
        let (dosDate, dosTime) = ZipWriter.dosDateTime(at)
        var writer = ZipWriter()
        for (name, url) in zip(names, urls) { try writer.addStored(name: name, fileURL: url, dosDate: dosDate, dosTime: dosTime) }
        writer.addStored(name: "manifest.json", data: manifest, dosDate: dosDate, dosTime: dosTime)
        let destination = directory.deletingLastPathComponent().appendingPathComponent(bundleName(meetingId: meetingId, startedAt: startedAt))
        try writer.write(to: destination)
        for url in urls + [directory.appendingPathComponent(idName)] { try? FileManager.default.removeItem(at: url) }
        if (try? FileManager.default.contentsOfDirectory(atPath: directory.path))?.isEmpty == true {
            try? FileManager.default.removeItem(at: directory)
        }
        let size = try fileSize(destination)
        return FinishedBundle(url: destination, transferCode: code.text, bytes: size, incomplete: incomplete)
    }

    private static func fileSize(_ url: URL) throws -> Int64 {
        guard let n = try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber else { throw DictumBundleError.malformed }
        return n.int64Value
    }

    /// `meeting-<yyyy-MM-dd local start date>-<first 8 hex of the id>.dictum`; no title or names.
    static func bundleName(meetingId: MeetingId, startedAt: Date) -> String {
        let f = DateFormatter()
        f.calendar = Calendar(identifier: .gregorian)
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        f.dateFormat = "yyyy-MM-dd"
        return "meeting-" + f.string(from: startedAt) + "-" + String(meetingId.hex.prefix(8)) + ".dictum"
    }

    /// Parses the `yyyy-MM-ddTHH:mm:ss` prefix this package writes (always UTC, `+00:00`).
    static func parseUtcPrefix(_ text: String) -> Date? {
        guard text.count >= 19 else { return nil }
        let f = DateFormatter()
        f.calendar = Calendar(identifier: .gregorian)
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return f.date(from: String(text.prefix(19)))
    }

    private static func writeMeta(_ meta: MeetingMeta, directory: URL, contentKey: Data, bytes: inout SharedBytes) throws {
        var plain = meta.json()
        defer { plain.zeroise() }
        try writeAtomic(try SealedFile.seal(plaintext: plain, contentKey: contentKey, meetingId: meta.id, entryName: metaName, bytes: &bytes),
                        to: directory.appendingPathComponent(metaName))
    }

    /// Temp file in the same folder, synced, then renamed over the target.
    private static func writeAtomic(_ data: Data, to url: URL) throws {
        let temp = url.deletingLastPathComponent().appendingPathComponent("." + url.lastPathComponent + "." + UUID().uuidString + ".tmp")
        let fd = open(temp.path, O_WRONLY | O_CREAT | O_EXCL, 0o600)
        guard fd >= 0 else { throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: temp.path]) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        do {
            try handle.write(contentsOf: data)
            try handle.synchronize()
            try handle.close()
            guard rename(temp.path, url.path) == 0 else { throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path]) }
        } catch {
            try? handle.close()
            try? FileManager.default.removeItem(at: temp)
            throw error
        }
    }

    deinit { buffer.zeroise(); contentKey.zeroise() }
}
