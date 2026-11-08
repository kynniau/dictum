// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import CryptoKit
import Foundation
import XCTest
@testable import DictumBundle

/// Synthetic conformance values from tests/Dictum.Tests/Fixtures/Bundles/phone-sample-chunked.README.md.
/// Test data only.
enum Fixture {
    static let id = MeetingId(hex: "0123456789abcdef0123456789abcdef")!
    static let at = Date(timeIntervalSince1970: 1_767_268_800) // 01/01/2026 12:00:00 UTC
    static let code = "abandon ability able about above absent"
    static func range(_ start: Int, _ count: Int) -> Data { Data((0..<count).map { UInt8(truncatingIfNeeded: start + $0) }) }
    static let contentKey = range(0, 32)
    static let salt = range(0x20, 16)
    static let dosDate: UInt16 = (20 << 9) | (1 << 5) | 1 // 01/01/2000
    static let dosTime: UInt16 = 0

    static var agreement: Agreement { Agreement(acknowledgedAt: at, how: "written", scriptVersion: "synthetic-v1", kind: 1) }
    static var confirmations: [Confirmation] {
        [Confirmation(answer: "written", at: at, kind: 1, scriptVersion: "synthetic-v1",
                      script: "Synthetic fixture notice; no real participants.", importerIdentity: "Synthetic phone operator",
                      route: ProcessingRoute(speechHost: nil, summaryEngine: 0, summaryHost: nil, keepAudio: 0, summaryModel: nil))]
    }
    static func meta(incomplete: Bool) -> MeetingMeta {
        MeetingMeta(id: id, title: "Synthetic phone tone", startedAt: at, duration: 2, source: 3, agreement: agreement,
                    incompleteAudioChannels: incomplete ? [2] : [])
    }

    /// trunc(1000 * sin(2 * pi * 440 * n / 16000)), n = 0..31999, as Int16 little-endian (same operation order as C#).
    static var pcm: Data {
        var d = Data(capacity: 64000)
        for n in 0..<32000 {
            let v = Int16(1000 * sin(2 * Double.pi * 440 * Double(n) / 16000))
            d.appendLE(v)
        }
        return d
    }

    struct Built { let entries: [(String, Data)]; let bundle: Data }

    /// Builds the bundle exactly as the README describes, using only the package's public API.
    static func build(incomplete: Bool) throws -> Built {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("dictumbundle-fixture-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let audioURL = dir.appendingPathComponent("audio-mixed.chunks.enc")
        let writer = try ChunkedAudioWriter(fileURL: audioURL, contentKey: contentKey, meetingId: id, channel: "mixed",
                                            bytes: CounterByteSource(start: 0))
        let samples = pcm
        try writer.write(pcm: samples.subdata(in: 0..<32000))
        try writer.write(pcm: samples.subdata(in: 32000..<64000))
        if incomplete { writer.abandon() } else { try writer.complete() }
        let audio = try Data(contentsOf: audioURL)

        var c = CounterByteSource(start: 0x70)
        let confirmations = try SealedFile.seal(plaintext: ConfirmationsJson.json(Self.confirmations), contentKey: contentKey,
                                                meetingId: id, entryName: "confirmations.json.enc", bytes: &c)
        var m = CounterByteSource(start: 0xa0)
        let meta = try SealedFile.seal(plaintext: Self.meta(incomplete: incomplete).json(), contentKey: contentKey,
                                       meetingId: id, entryName: "meta.json.enc", bytes: &m)
        let transferKey = try TransferCode.deriveKey(code: code, salt: salt)
        let wrap = try KeyWrap.wrap(contentKey: contentKey, recipientKey: transferKey, recipientId: "transfer", nonce: range(0xf0, 12))
        let manifest = Manifest.json(meetingId: id, appVersion: "1.5.1.0", platform: "ios", salt: salt, wrap: wrap, files: [
            ManifestEntry(name: "audio-mixed.chunks.enc", bytes: Int64(audio.count)),
            ManifestEntry(name: "confirmations.json.enc", bytes: Int64(confirmations.count)),
            ManifestEntry(name: "meta.json.enc", bytes: Int64(meta.count)),
        ])
        var zip = ZipWriter()
        try zip.addStored(name: "audio-mixed.chunks.enc", fileURL: audioURL, dosDate: dosDate, dosTime: dosTime)
        zip.addStored(name: "confirmations.json.enc", data: confirmations, dosDate: dosDate, dosTime: dosTime)
        zip.addStored(name: "meta.json.enc", data: meta, dosDate: dosDate, dosTime: dosTime)
        zip.addStored(name: "manifest.json", data: manifest, dosDate: dosDate, dosTime: dosTime)
        let out = dir.appendingPathComponent("out.dictum")
        try zip.write(to: out)
        return Built(entries: [("audio-mixed.chunks.enc", audio), ("confirmations.json.enc", confirmations),
                               ("meta.json.enc", meta), ("manifest.json", manifest)],
                     bundle: try Data(contentsOf: out))
    }

    /// The committed C# fixture, or nil when the package is used outside the Dictum repository.
    static func committed(_ name: String) -> Data? {
        // .../src/Dictum.iOS/DictumBundle/Tests/DictumBundleTests/Support.swift -> repository root is 6 levels up.
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<6 { root.deleteLastPathComponent() }
        return try? Data(contentsOf: root.appendingPathComponent("tests/Dictum.Tests/Fixtures/Bundles").appendingPathComponent(name))
    }
}

func sha256(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
func hex(_ data: Data) -> String { data.map { String(format: "%02x", $0) }.joined() }

/// Minimal reader for the tests: walks the central directory of a classic ZIP.
struct ZipEntryInfo { let name: String; let method: UInt16; let size: Int; let data: Data }

func readZip(_ zip: Data) throws -> [ZipEntryInfo] {
    let z = Data(zip)
    guard z.count >= 22, z.readLE(UInt32.self, at: z.count - 22) == 0x0605_4b50 else { throw DictumBundleError.malformed }
    let count = Int(z.readLE(UInt16.self, at: z.count - 12))
    var p = Int(z.readLE(UInt32.self, at: z.count - 6))
    var out: [ZipEntryInfo] = []
    for _ in 0..<count {
        guard z.readLE(UInt32.self, at: p) == 0x0201_4b50 else { throw DictumBundleError.malformed }
        let method = z.readLE(UInt16.self, at: p + 10)
        let size = Int(z.readLE(UInt32.self, at: p + 24))
        let nameLen = Int(z.readLE(UInt16.self, at: p + 28))
        let extra = Int(z.readLE(UInt16.self, at: p + 30)), comment = Int(z.readLE(UInt16.self, at: p + 32))
        let local = Int(z.readLE(UInt32.self, at: p + 42))
        let name = String(decoding: z.subdata(in: p + 46..<p + 46 + nameLen), as: UTF8.self)
        let localData = local + 30 + Int(z.readLE(UInt16.self, at: local + 26)) + Int(z.readLE(UInt16.self, at: local + 28))
        out.append(ZipEntryInfo(name: name, method: method, size: size, data: z.subdata(in: localData..<localData + size)))
        p += 46 + nameLen + extra + comment
    }
    return out
}
