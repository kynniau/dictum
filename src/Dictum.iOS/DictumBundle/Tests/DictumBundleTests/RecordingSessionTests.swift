// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation
import XCTest
@testable import DictumBundle

final class RecordingSessionTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("dictumbundle-session-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    /// About 3.5 s of PCM: 112000 bytes, appended in odd-sized pieces.
    private func syntheticPcm() -> Data {
        var d = Data()
        for n in 0..<56_000 { d.appendLE(Int16(truncatingIfNeeded: (n * 37) % 20_000 - 10_000)) }
        return d
    }

    private func appendInPieces(_ session: RecordingSession, _ pcm: Data) throws {
        let sizes = [1, 7, 333, 31_999, 2, 12_345, 1_001]
        var offset = 0, i = 0
        while offset < pcm.count {
            let n = min(sizes[i % sizes.count], pcm.count - offset)
            try session.append(pcm16Mono16k: pcm.subdata(in: offset..<offset + n))
            offset += n; i += 1
        }
    }

    private func newSession(_ dir: URL, id: MeetingId, key: Data) throws -> RecordingSession {
        try RecordingSession(directory: dir, meetingId: id, contentKey: key, startedAt: Fixture.at, title: "Synthetic session",
                             agreement: Fixture.agreement, confirmations: Fixture.confirmations, appVersion: "0.1.0")
    }

    func testFinishProducesFourStoredEntriesWithMatchingManifest() throws {
        let id = MeetingId.random()
        var keySource = SystemByteSource()
        let key = keySource.bytes(32)
        let dir = root.appendingPathComponent("session")
        let session = try newSession(dir, id: id, key: key)
        let pcm = syntheticPcm()
        try appendInPieces(session, pcm)
        let code = TransferCode(words: Fixture.code.split(separator: " ").map(String.init))
        let bundle = try session.finish(duration: 3.5, transferCode: code)

        XCTAssertFalse(bundle.incomplete)
        XCTAssertEqual(bundle.transferCode, Fixture.code)
        XCTAssertEqual(bundle.url.deletingLastPathComponent().standardizedFileURL.path, root.standardizedFileURL.path)
        XCTAssertEqual(bundle.url.lastPathComponent, RecordingSession.bundleName(meetingId: id, startedAt: Fixture.at))
        XCTAssertTrue(bundle.url.lastPathComponent.hasSuffix("-" + id.hex.prefix(8) + ".dictum"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.path), "session folder removed after packaging")

        let zipData = try Data(contentsOf: bundle.url)
        XCTAssertEqual(Int64(zipData.count), bundle.bytes)
        let entries = try readZip(zipData)
        XCTAssertEqual(entries.map(\.name), ["audio-mixed.chunks.enc", "confirmations.json.enc", "meta.json.enc", "manifest.json"])
        XCTAssertTrue(entries.allSatisfy { $0.method == 0 })

        let manifest = try XCTUnwrap(JSONSerialization.jsonObject(with: entries[3].data) as? [String: Any])
        XCTAssertEqual(manifest["meeting"] as? String, id.hex)
        let files = try XCTUnwrap(manifest["files"] as? [[String: Any]])
        XCTAssertEqual(files.map { $0["name"] as? String }, entries.prefix(3).map(\.name))
        XCTAssertEqual(files.map { $0["bytes"] as? Int }, entries.prefix(3).map(\.size))
        let transfer = try XCTUnwrap(manifest["transfer"] as? [String: Any])
        let salt = try XCTUnwrap(Data(base64Encoded: transfer["salt"] as! String))
        let wrap = try XCTUnwrap(Data(base64Encoded: transfer["wrap"] as! String))
        XCTAssertEqual(salt.count, 16)
        XCTAssertEqual(wrap.count, 60)

        // The importer's path: derive, unwrap, verify every payload.
        let transferKey = try TransferCode.deriveKey(code: bundle.transferCode, salt: salt)
        let content = try KeyWrap.unwrap(wrap, recipientKey: transferKey, recipientId: "transfer")
        XCTAssertEqual(content, key)
        XCTAssertEqual(try ChunkedAudio.open(entries[0].data, contentKey: content, meetingId: id, channel: "mixed", incomplete: false), pcm)
        let meta = try SealedFile.open(entries[2].data, contentKey: content, meetingId: id, entryName: "meta.json.enc")
        let metaObject = try XCTUnwrap(JSONSerialization.jsonObject(with: meta) as? [String: Any])
        XCTAssertEqual(metaObject["Duration"] as? String, "00:00:03.5000000")
        XCTAssertEqual(metaObject["IncompleteAudioChannels"] as? [Int], [])
        XCTAssertEqual(metaObject["Source"] as? Int, 3)
        XCTAssertEqual(try SealedFile.open(entries[1].data, contentKey: content, meetingId: id, entryName: "confirmations.json.enc"),
                       ConfirmationsJson.json(Fixture.confirmations))
        // Records are whole seconds (32000 bytes) except the last: 3 full + 16000 tail + terminal.
        XCTAssertEqual(entries[0].size, 24 + 3 * (60 + 32_000) + (60 + 16_000) + 60)
    }

    func testKilledSessionRecoversAsIncomplete() throws {
        let id = MeetingId.random()
        var keySource = SystemByteSource()
        let key = keySource.bytes(32)
        let dir = root.appendingPathComponent("killed")
        var session: RecordingSession? = try newSession(dir, id: id, key: key)
        let pcm = syntheticPcm()
        try appendInPieces(session!, pcm)
        session = nil // app killed: no finish, buffered partial second is lost

        let recovered = try XCTUnwrap(RecordingSession.recover(directory: dir, appVersion: "0.1.0", contentKey: key))
        XCTAssertTrue(recovered.incomplete)
        let entries = try readZip(try Data(contentsOf: recovered.url))
        XCTAssertEqual(entries.map(\.name), ["audio-mixed.chunks.enc", "confirmations.json.enc", "meta.json.enc", "manifest.json"])
        let salt = try XCTUnwrap(Data(base64Encoded: ((JSONSerialization.jsonObject(with: entries[3].data) as! [String: Any])["transfer"]
            as! [String: Any])["salt"] as! String))
        let wrap = try XCTUnwrap(Data(base64Encoded: ((JSONSerialization.jsonObject(with: entries[3].data) as! [String: Any])["transfer"]
            as! [String: Any])["wrap"] as! String))
        let content = try KeyWrap.unwrap(wrap, recipientKey: try TransferCode.deriveKey(code: recovered.transferCode, salt: salt),
                                         recipientId: "transfer")
        let meta = try SealedFile.open(entries[2].data, contentKey: content, meetingId: id, entryName: "meta.json.enc")
        let metaObject = try XCTUnwrap(JSONSerialization.jsonObject(with: meta) as? [String: Any])
        XCTAssertEqual(metaObject["IncompleteAudioChannels"] as? [Int], [2])
        XCTAssertEqual(metaObject["AudioIncomplete"] as? Bool, true)
        // Three whole seconds were sealed; no terminal record, so strict verification fails and snapshot verification passes.
        XCTAssertEqual(try ChunkedAudio.open(entries[0].data, contentKey: content, meetingId: id, channel: "mixed", incomplete: true),
                       pcm.prefix(96_000))
        XCTAssertThrowsError(try ChunkedAudio.open(entries[0].data, contentKey: content, meetingId: id, channel: "mixed", incomplete: false))

        XCTAssertNil(try RecordingSession.recover(directory: dir, appVersion: "0.1.0", contentKey: key), "nothing left to recover")
    }

    /// Desktop import of a recovered bundle showed Duration 00:00:00 for 2 s of audio (coordinator, 08/11/2026).
    func testRecoveredMetaCarriesSealedAudioDuration() throws {
        let id = MeetingId.random()
        var keySource = SystemByteSource()
        let key = keySource.bytes(32)
        let dir = root.appendingPathComponent("killed-two-seconds")
        var session: RecordingSession? = try newSession(dir, id: id, key: key)
        try appendInPieces(session!, syntheticPcm().prefix(64_000))
        session = nil

        let recovered = try XCTUnwrap(RecordingSession.recover(directory: dir, appVersion: "0.1.0", contentKey: key))
        XCTAssertTrue(recovered.incomplete)
        let zipData = try Data(contentsOf: recovered.url)
        XCTAssertEqual(Int64(zipData.count), recovered.bytes)
        let entries = try readZip(zipData)
        let transfer = (try JSONSerialization.jsonObject(with: entries[3].data) as! [String: Any])["transfer"] as! [String: Any]
        let salt = try XCTUnwrap(Data(base64Encoded: transfer["salt"] as! String))
        let wrap = try XCTUnwrap(Data(base64Encoded: transfer["wrap"] as! String))
        let content = try KeyWrap.unwrap(wrap, recipientKey: try TransferCode.deriveKey(code: recovered.transferCode, salt: salt),
                                         recipientId: "transfer")
        let meta = try SealedFile.open(entries[2].data, contentKey: content, meetingId: id, entryName: "meta.json.enc")
        let expected = MeetingMeta(id: id, title: "Synthetic session", startedAt: Fixture.at, duration: 2, source: 3,
                                   agreement: Fixture.agreement, incompleteAudioChannels: [2]).json()
        XCTAssertEqual(String(decoding: meta, as: UTF8.self), String(decoding: expected, as: UTF8.self))
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: meta) as? [String: Any])
        XCTAssertEqual(object["Duration"] as? String, "00:00:02")
        XCTAssertEqual(object["IncompleteAudioChannels"] as? [Int], [2])
    }

    func testSecondSessionInSameDirectoryIsRefused() throws {
        let id = MeetingId.random()
        let key = Fixture.contentKey
        let dir = root.appendingPathComponent("twice")
        let first = try newSession(dir, id: id, key: key)
        XCTAssertThrowsError(try newSession(dir, id: id, key: key))
        _ = first
    }
}
