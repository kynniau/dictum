// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation
import XCTest
@testable import DictumBundle

/// TEST ONLY: hands out the given byte strings in order (a predictable source, never for real recordings).
struct ScriptedByteSource: ByteSource {
    var queue: [Data]
    mutating func bytes(_ count: Int) -> Data {
        let next = queue.removeFirst()
        precondition(next.count == count, "scripted byte source: asked for \(count), next has \(next.count)")
        return next
    }
}

/// Reading, unlocking and re-wrapping the phone's own bundles, against the committed C# fixtures.
final class BundleFileTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("dictumbundle-file-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    /// A temporary copy of a committed fixture, or a skip outside the Dictum repository.
    private func fixtureCopy(_ name: String) throws -> URL {
        guard let data = Fixture.committed(name) else {
            throw XCTSkip("Repository fixture not present (package used outside the Dictum repository).")
        }
        let url = root.appendingPathComponent(name)
        try data.write(to: url)
        return url
    }

    private var fixtureCode: TransferCode { TransferCode(words: Fixture.code.split(separator: " ").map(String.init)) }

    func testSealedFileOpenVerifiesEveryRecordIncludingTerminal() throws {
        var bytes = SystemByteSource()
        let plain = Data((0..<3_000_000).map { UInt8(truncatingIfNeeded: $0 &* 7) }) // three records plus terminal
        let sealed = try SealedFile.seal(plaintext: plain, contentKey: Fixture.contentKey, meetingId: Fixture.id,
                                         entryName: "record.json.enc", bytes: &bytes)
        XCTAssertEqual(try SealedFile.open(sealed, contentKey: Fixture.contentKey, meetingId: Fixture.id, entryName: "record.json.enc"), plain)
        // Without the 32-byte zero-length terminal record, with a trailing byte, or with a flipped terminal tag: refused.
        XCTAssertThrowsError(try SealedFile.open(sealed.prefix(sealed.count - 32), contentKey: Fixture.contentKey,
                                                 meetingId: Fixture.id, entryName: "record.json.enc"))
        XCTAssertThrowsError(try SealedFile.open(sealed + Data([0]), contentKey: Fixture.contentKey,
                                                 meetingId: Fixture.id, entryName: "record.json.enc"))
        var flipped = sealed
        flipped[flipped.count - 1] ^= 0x01
        XCTAssertThrowsError(try SealedFile.open(flipped, contentKey: Fixture.contentKey, meetingId: Fixture.id, entryName: "record.json.enc"))
        XCTAssertThrowsError(try SealedFile.open(sealed, contentKey: Fixture.contentKey, meetingId: Fixture.id, entryName: "meta.json.enc"))
    }

    func testKeyWrapUnwrapPublic() throws {
        let transferKey = try TransferCode.deriveKey(code: Fixture.code, salt: Fixture.salt)
        let wrapped = try KeyWrap.wrap(contentKey: Fixture.contentKey, recipientKey: transferKey, recipientId: "transfer",
                                       nonce: Fixture.range(0xf0, 12))
        XCTAssertEqual(try KeyWrap.unwrap(wrapped: wrapped, recipientKey: transferKey, recipientId: "transfer"), Fixture.contentKey)
        XCTAssertThrowsError(try KeyWrap.unwrap(wrapped: wrapped, recipientKey: transferKey, recipientId: "owner"))
        XCTAssertThrowsError(try KeyWrap.unwrap(wrapped: wrapped.prefix(59), recipientKey: transferKey, recipientId: "transfer"))
    }

    func testInspectReadsManifestOnly() throws {
        let url = try fixtureCopy("phone-sample-chunked.dictum")
        let info = try BundleFile.inspect(url: url)
        XCTAssertEqual(info.meetingId, Fixture.id)
        XCTAssertEqual(info.appVersion, "1.5.1.0")
        XCTAssertEqual(info.platform, "ios")
        XCTAssertEqual(info.salt, Fixture.salt)
        XCTAssertEqual(info.wrap.prefix(12), Fixture.range(0xf0, 12))
        XCTAssertEqual(info.files.map(\.name), ["audio-mixed.chunks.enc", "confirmations.json.enc", "meta.json.enc"])
        XCTAssertEqual(info.files.map(\.bytes), [64204, 387, 496])
    }

    func testUnlockWithCode() throws {
        let url = try fixtureCopy("phone-sample-chunked.dictum")
        XCTAssertEqual(try BundleFile.unlock(url: url, code: "  ABANDON ability able about above absent "), Fixture.contentKey)
        XCTAssertThrowsError(try BundleFile.unlock(url: url, code: "abandon ability able about above zoo")) {
            XCTAssertEqual($0 as? DictumBundleError, .authenticationFailed)
        }
        XCTAssertThrowsError(try BundleFile.unlock(url: url, code: "abandon ability able about above")) {
            XCTAssertEqual($0 as? DictumBundleError, .codeRequired)
        }
    }

    func testMetaAndAudio() throws {
        let complete = try fixtureCopy("phone-sample-chunked.dictum")
        let meta = try BundleFile.meta(url: complete, contentKey: Fixture.contentKey)
        XCTAssertEqual(meta, BundleMeta(title: "Synthetic phone tone", startedAt: Fixture.at, duration: 2, audioIncomplete: false))
        XCTAssertEqual(try BundleFile.audio(url: complete, contentKey: Fixture.contentKey), Fixture.pcm)
        XCTAssertEqual(try BundleFile.audio(url: complete, contentKey: Fixture.contentKey, limit: 32000), Fixture.pcm.prefix(32000))
        XCTAssertThrowsError(try BundleFile.meta(url: complete, contentKey: Fixture.range(1, 32)))

        let incomplete = try fixtureCopy("phone-sample-chunked-incomplete.dictum")
        XCTAssertTrue(try BundleFile.meta(url: incomplete, contentKey: Fixture.contentKey).audioIncomplete)
        XCTAssertEqual(try BundleFile.audio(url: incomplete, contentKey: Fixture.contentKey), Fixture.pcm)

        // The public reader: complete mode needs the terminal record, incomplete mode accepts its absence.
        let audio = try XCTUnwrap(try BundleFile.entry(url: incomplete, name: "audio-mixed.chunks.enc"))
        XCTAssertEqual(try ChunkedAudioReader.pcm(audio, contentKey: Fixture.contentKey, meetingId: Fixture.id, incomplete: true), Fixture.pcm)
        XCTAssertThrowsError(try ChunkedAudioReader.pcm(audio, contentKey: Fixture.contentKey, meetingId: Fixture.id, incomplete: false))
    }

    func testRewrapWithFreshCode() throws {
        let url = try fixtureCopy("phone-sample-chunked.dictum")
        let before = try readZip(try Data(contentsOf: url))
        var bytes = SystemByteSource()
        let (out, code) = try BundleFile.rewrap(url: url, contentKey: Fixture.contentKey, bytes: &bytes)
        XCTAssertEqual(out, url)
        XCTAssertEqual(code.words.count, 6)
        XCTAssertNotEqual(code.text, Fixture.code)

        XCTAssertEqual(try BundleFile.unlock(url: url, code: code.text), Fixture.contentKey)
        XCTAssertThrowsError(try BundleFile.unlock(url: url, code: Fixture.code)) {
            XCTAssertEqual($0 as? DictumBundleError, .authenticationFailed)
        }

        let after = try readZip(try Data(contentsOf: url))
        XCTAssertEqual(after.map(\.name), before.map(\.name))
        XCTAssertTrue(after.allSatisfy { $0.method == 0 })
        for (a, b) in zip(after, before) where a.name != "manifest.json" {
            XCTAssertEqual(sha256(a.data), sha256(b.data), a.name)
        }
        XCTAssertEqual(sha256(after[0].data), "97ed36d4f3b59dd944431b873f63665a121804a18275bd556bbeb10d914e561f")
        XCTAssertEqual(sha256(after[1].data), "606664ca9bd6ee36b01d5b18b7b2c49e62081d26abb81721b8988641623c8139")
        XCTAssertEqual(sha256(after[2].data), "72abb354df4793ea02a6543a2df3b978cd07d099ecc87a595d7bc623170ea623")
        let info = try BundleFile.inspect(url: url)
        XCTAssertNotEqual(info.salt, Fixture.salt)
        XCTAssertEqual(info.files.map(\.bytes), [64204, 387, 496])
        XCTAssertEqual(try BundleFile.audio(url: url, contentKey: Fixture.contentKey), Fixture.pcm)
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: root.path).filter { $0.hasSuffix(".tmp") }
        XCTAssertEqual(leftovers, [])
    }

    /// The same salt, nonce and code as the fixture give the fixture back byte for byte (whole-bundle SHA-256 from
    /// phone-sample-chunked.README.md).
    func testRewrapWithFixtureValuesIsByteIdentical() throws {
        let url = try fixtureCopy("phone-sample-chunked.dictum")
        // Start from a different wrap, so the test shows rewrap rebuilt the manifest rather than leaving it alone.
        var fresh = SystemByteSource()
        _ = try BundleFile.rewrap(url: url, contentKey: Fixture.contentKey, bytes: &fresh)
        XCTAssertNotEqual(sha256(try Data(contentsOf: url)), "431373203df2eb93c0594a8afb97dd92fb0bcc4735c584ffb169fbf549d99658")

        var scripted = ScriptedByteSource(queue: [Fixture.salt, Fixture.range(0xf0, 12)])
        _ = try BundleFile.rewrap(url: url, contentKey: Fixture.contentKey, newCode: fixtureCode, bytes: &scripted)
        XCTAssertEqual(scripted.queue.count, 0)
        let data = try Data(contentsOf: url)
        XCTAssertEqual(data.count, 66027)
        XCTAssertEqual(sha256(data), "431373203df2eb93c0594a8afb97dd92fb0bcc4735c584ffb169fbf549d99658")
        let manifest = try XCTUnwrap(try readZip(data).first { $0.name == "manifest.json" })
        XCTAssertEqual(sha256(manifest.data), "55d69cac90ec969fe7e5ee8f5807c8aa06ff8906879461e4271cb4cf3fc2deed")
    }

    func testRewrapRefusesAKeyThatIsNotTheBundles() throws {
        let url = try fixtureCopy("phone-sample-chunked.dictum")
        let original = try Data(contentsOf: url)
        var bytes = SystemByteSource()
        XCTAssertThrowsError(try BundleFile.rewrap(url: url, contentKey: Fixture.range(1, 32), bytes: &bytes)) {
            XCTAssertEqual($0 as? DictumBundleError, .authenticationFailed)
        }
        XCTAssertEqual(try Data(contentsOf: url), original)
    }

    func testRewrapOfANewlyRecordedSession() throws {
        let id = MeetingId.random()
        var keySource = SystemByteSource()
        let key = keySource.bytes(32)
        let session = try RecordingSession(directory: root.appendingPathComponent("s"), meetingId: id, contentKey: key,
                                           startedAt: Fixture.at, title: "Synthetic session", agreement: Fixture.agreement,
                                           confirmations: Fixture.confirmations, appVersion: "0.1.0")
        try session.append(pcm16Mono16k: Fixture.pcm)
        let bundle = try session.finish(duration: 2)
        var bytes = SystemByteSource()
        let (_, code) = try BundleFile.rewrap(url: bundle.url, contentKey: key, bytes: &bytes)
        XCTAssertEqual(try BundleFile.unlock(url: bundle.url, code: code.text), key)
        XCTAssertThrowsError(try BundleFile.unlock(url: bundle.url, code: bundle.transferCode))
        XCTAssertEqual(try BundleFile.meta(url: bundle.url, contentKey: key).title, "Synthetic session")
    }

    func testParseTimeSpan() {
        XCTAssertEqual(BundleFile.parseTimeSpan("00:00:02"), 2)
        XCTAssertEqual(BundleFile.parseTimeSpan("00:00:03.5000000"), 3.5)
        XCTAssertEqual(BundleFile.parseTimeSpan("1.01:01:01"), 90_061)
        XCTAssertNil(BundleFile.parseTimeSpan("01:01"))
        for value in [0.0, 2, 3.25, 3599.5, 90_061] {
            let text = JsonText.timeSpan(value)
            XCTAssertEqual(BundleFile.parseTimeSpan(String(text.dropFirst().dropLast())), value)
        }
    }
}
