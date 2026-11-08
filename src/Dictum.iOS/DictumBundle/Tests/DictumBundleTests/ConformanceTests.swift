// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation
import XCTest
@testable import DictumBundle

/// Byte-for-byte conformance against the C# desktop's synthetic phone fixtures (hashes from
/// tests/Dictum.Tests/Fixtures/Bundles/phone-sample-chunked.README.md).
final class ConformanceTests: XCTestCase {
    func testCompleteFixtureHashes() throws {
        let built = try Fixture.build(incomplete: false)
        let expected = [
            ("audio-mixed.chunks.enc", 64204, "97ed36d4f3b59dd944431b873f63665a121804a18275bd556bbeb10d914e561f"),
            ("confirmations.json.enc", 387, "606664ca9bd6ee36b01d5b18b7b2c49e62081d26abb81721b8988641623c8139"),
            ("meta.json.enc", 496, "72abb354df4793ea02a6543a2df3b978cd07d099ecc87a595d7bc623170ea623"),
            ("manifest.json", 474, "55d69cac90ec969fe7e5ee8f5807c8aa06ff8906879461e4271cb4cf3fc2deed"),
        ]
        for ((name, data), (expectedName, size, hash)) in zip(built.entries, expected) {
            XCTAssertEqual(name, expectedName)
            XCTAssertEqual(data.count, size, name)
            XCTAssertEqual(sha256(data), hash, name)
        }
        XCTAssertEqual(sha256(built.bundle), "431373203df2eb93c0594a8afb97dd92fb0bcc4735c584ffb169fbf549d99658")
    }

    func testCompleteFixtureMatchesCommittedFile() throws {
        guard let committed = Fixture.committed("phone-sample-chunked.dictum") else {
            throw XCTSkip("Repository fixture not present (package used outside the Dictum repository).")
        }
        let built = try Fixture.build(incomplete: false)
        XCTAssertEqual(built.bundle.count, committed.count)
        XCTAssertNil(firstDifference(built.bundle, committed), "first differing offset")
    }

    func testIncompleteFixtureHashes() throws {
        let built = try Fixture.build(incomplete: true)
        let expected = [
            ("audio-mixed.chunks.enc", 64144, "1fee485537e882c0604aa79c47841d19ec18f060e6b26a454601b5d118c1005c"),
            ("confirmations.json.enc", 387, "606664ca9bd6ee36b01d5b18b7b2c49e62081d26abb81721b8988641623c8139"),
            ("meta.json.enc", 496, "fb6dec6f1bc7709b156fa4868d42e807b28cdd655ec3a94a5d2f795d1956c9c1"),
            ("manifest.json", 474, "cf769f6ba08ef7f186f2a51a8049287edeb176e97e5429be158e2879af6e5222"),
        ]
        for ((name, data), (expectedName, size, hash)) in zip(built.entries, expected) {
            XCTAssertEqual(name, expectedName)
            XCTAssertEqual(data.count, size, name)
            XCTAssertEqual(sha256(data), hash, name)
        }
        XCTAssertEqual(sha256(built.bundle), "b2ce9647adccc98448c1602ba666af77c36eb822d57c390618e28884d6fa1191")
    }

    func testIncompleteFixtureMatchesCommittedFile() throws {
        guard let committed = Fixture.committed("phone-sample-chunked-incomplete.dictum") else {
            throw XCTSkip("Repository fixture not present (package used outside the Dictum repository).")
        }
        let built = try Fixture.build(incomplete: true)
        XCTAssertEqual(built.bundle.count, committed.count)
        XCTAssertNil(firstDifference(built.bundle, committed), "first differing offset")
    }

    func testMetadataAndConfirmationPlaintextMatchReadme() {
        XCTAssertEqual(String(decoding: Fixture.meta(incomplete: false).json(), as: UTF8.self),
                       #"{"Id":{"Value":"0123456789abcdef0123456789abcdef"},"Title":"Synthetic phone tone","StartedAt":"2026-01-01T12:00:00+00:00","Duration":"00:00:02","Source":3,"Agreement":{"AcknowledgedAt":"2026-01-01T12:00:00+00:00","How":"written","ScriptVersion":"synthetic-v1","Kind":1},"Markers":[],"Wraps":[],"HasVideo":false,"SummaryEngine":null,"SummaryTemplate":null,"IncompleteAudioChannels":[],"AudioIncomplete":false}"#)
        XCTAssertEqual(String(decoding: Fixture.meta(incomplete: true).json(), as: UTF8.self),
                       #"{"Id":{"Value":"0123456789abcdef0123456789abcdef"},"Title":"Synthetic phone tone","StartedAt":"2026-01-01T12:00:00+00:00","Duration":"00:00:02","Source":3,"Agreement":{"AcknowledgedAt":"2026-01-01T12:00:00+00:00","How":"written","ScriptVersion":"synthetic-v1","Kind":1},"Markers":[],"Wraps":[],"HasVideo":false,"SummaryEngine":null,"SummaryTemplate":null,"IncompleteAudioChannels":[2],"AudioIncomplete":true}"#)
        XCTAssertEqual(String(decoding: ConfirmationsJson.json(Fixture.confirmations), as: UTF8.self),
                       #"[{"Answer":"written","At":"2026-01-01T12:00:00+00:00","Kind":1,"ScriptVersion":"synthetic-v1","Script":"Synthetic fixture notice; no real participants.","ImporterIdentity":"Synthetic phone operator","Route":{"SpeechHost":null,"SummaryEngine":0,"SummaryHost":null,"KeepAudio":0,"SummaryModel":null}}]"#)
    }

    func testDerivedTransferKey() throws {
        let key = try TransferCode.deriveKey(code: Fixture.code, salt: Fixture.salt)
        XCTAssertEqual(hex(key), "e308183fc23692c746e66fcb5b81ee5edff4085d7c612d7695ce14ef58eb14ed")
    }

    func testRecordZeroAad() throws {
        let built = try Fixture.build(incomplete: false)
        let audio = built.entries[0].1
        let header = audio.subdata(in: 0..<24)
        XCTAssertEqual(hex(header), "4443544d41554401000102030405060708090a0b0c0d0e0f")
        let length = audio.readLE(Int32.self, at: 24)
        XCTAssertEqual(length, 32000)
        let expected = "4443544d41554401000102030405060708090a0b0c0d0e0f0000000000000000007d000030313233343536373839616263646566303132333435363738396162636465662f617564696f2d6d697865642e6368756e6b732e656e63"
        // The writer uses the one shared builder for both operations; rebuild both from the on-disk fields.
        let payloadAad = Framing.aad(header: header, sequence: 0, length: length, identity: Fixture.id.hex + "/audio-mixed.chunks.enc")
        let framingAad = Framing.aad(header: header, sequence: 0, length: length, identity: Fixture.id.hex + "/audio-mixed.chunks.enc")
        XCTAssertEqual(hex(payloadAad), expected)
        XCTAssertEqual(payloadAad, framingAad)
        // And both on-disk tags verify under exactly that AAD (so the writer really used it for each).
        let key = try Framing.key(Fixture.contentKey)
        XCTAssertNoThrow(try Framing.open(Data(), tag: audio.subdata(in: 40..<56), key: key, nonce: audio.subdata(in: 28..<40), aad: framingAad))
        XCTAssertEqual(try Framing.open(audio.subdata(in: 68..<32068), tag: audio.subdata(in: 32068..<32084), key: key,
                                        nonce: audio.subdata(in: 56..<68), aad: payloadAad), Fixture.pcm.subdata(in: 0..<32000))
        XCTAssertEqual(hex(audio.subdata(in: 28..<40)), "202122232425262728292a2b")  // framing nonce, record 0
        XCTAssertEqual(hex(audio.subdata(in: 56..<68)), "101112131415161718191a1b")  // payload nonce, record 0
    }

    /// Mutation check: the untouched stream verifies; flipping any AAD-bound byte makes verification fail.
    func testFlippedAadFieldsFailVerification() throws {
        let built = try Fixture.build(incomplete: false)
        let audio = built.entries[0].1
        XCTAssertEqual(try ChunkedAudio.open(audio, contentKey: Fixture.contentKey, meetingId: Fixture.id, channel: "mixed",
                                             incomplete: false), Fixture.pcm)
        for offset in [10, 24, 25] { // header identity byte, record-0 length prefix (low and second byte)
            var mutated = audio
            mutated[offset] ^= 0x02
            XCTAssertThrowsError(try ChunkedAudio.open(mutated, contentKey: Fixture.contentKey, meetingId: Fixture.id,
                                                       channel: "mixed", incomplete: false), "offset \(offset)")
            XCTAssertThrowsError(try ChunkedAudio.open(mutated, contentKey: Fixture.contentKey, meetingId: Fixture.id,
                                                       channel: "mixed", incomplete: true), "offset \(offset) incomplete")
        }
        let other = MeetingId(hex: "1123456789abcdef0123456789abcdef")!
        XCTAssertThrowsError(try ChunkedAudio.open(audio, contentKey: Fixture.contentKey, meetingId: other, channel: "mixed", incomplete: false))

        let meta = built.entries[2].1
        XCTAssertNoThrow(try SealedFile.open(meta, contentKey: Fixture.contentKey, meetingId: Fixture.id, entryName: "meta.json.enc"))
        var mutatedMeta = meta
        mutatedMeta[24] ^= 0x01
        XCTAssertThrowsError(try SealedFile.open(mutatedMeta, contentKey: Fixture.contentKey, meetingId: Fixture.id, entryName: "meta.json.enc"))
        XCTAssertThrowsError(try SealedFile.open(meta, contentKey: Fixture.contentKey, meetingId: Fixture.id, entryName: "confirmations.json.enc"))
    }

    func testCommittedFixtureDecryptsWithPackageReader() throws {
        guard let committed = Fixture.committed("phone-sample-chunked-incomplete.dictum") else {
            throw XCTSkip("Repository fixture not present (package used outside the Dictum repository).")
        }
        let entries = try readZip(committed)
        let audio = try XCTUnwrap(entries.first { $0.name == "audio-mixed.chunks.enc" })
        XCTAssertEqual(try ChunkedAudio.open(audio.data, contentKey: Fixture.contentKey, meetingId: Fixture.id, channel: "mixed",
                                             incomplete: true), Fixture.pcm)
        XCTAssertThrowsError(try ChunkedAudio.open(audio.data, contentKey: Fixture.contentKey, meetingId: Fixture.id, channel: "mixed",
                                                   incomplete: false))
    }

    private func firstDifference(_ a: Data, _ b: Data) -> Int? {
        let x = [UInt8](a), y = [UInt8](b)
        for i in 0..<min(x.count, y.count) where x[i] != y[i] { return i }
        return x.count == y.count ? nil : min(x.count, y.count)
    }
}

final class TransferCodeTests: XCTestCase {
    func testNormaliseCollapsesDotNetWhitespaceAndLowercases() {
        XCTAssertEqual(TransferCode.normalise("  Abandon\tABILITY  able\u{00A0}about\u{3000}above\r\n absent  "),
                       "abandon ability able about above absent")
        XCTAssertEqual(TransferCode.normalise("a\u{2028}b\u{205F}c\u{1680}d\u{0085}e\u{200A}f"), "a b c d e f")
        XCTAssertEqual(TransferCode.normalise("\u{0130}"), "i")
    }

    func testSixTokenRule() throws {
        XCTAssertThrowsError(try TransferCode.deriveKey(code: "abandon ability able about above", salt: Fixture.salt))
        XCTAssertThrowsError(try TransferCode.deriveKey(code: Fixture.code + " zoo", salt: Fixture.salt))
        XCTAssertThrowsError(try TransferCode.deriveKey(code: String(repeating: "a", count: 300) + " b c d e f", salt: Fixture.salt))
        // Whitespace variants derive the same key as the canonical code.
        let canonical = try TransferCode.deriveKey(code: Fixture.code, salt: Fixture.salt)
        XCTAssertEqual(try TransferCode.deriveKey(code: "ABANDON\u{00A0}ability\t\table about\u{3000}above  absent", salt: Fixture.salt), canonical)
    }

    func testRandomCodeUsesSixListWords() {
        var source = CounterByteSource(start: 0)
        let code = TransferCode.random(using: &source)
        XCTAssertEqual(code.words.count, 6)
        // Bytes 00..0b: values 0x0100, 0x0302, ... masked to 11 bits.
        XCTAssertEqual(code.words, [0x100, 0x302, 0x504, 0x706, 0x108, 0x30a].map { TransferCode.wordList[$0] })
        var system = SystemByteSource()
        let random = TransferCode.random(using: &system)
        XCTAssertTrue(random.words.allSatisfy(TransferCode.wordList.contains))
        XCTAssertEqual(TransferCode.wordList.first, "abandon")
        XCTAssertEqual(TransferCode.wordList.last, "zoo")
    }

    func testJsonTextFormats() {
        XCTAssertEqual(JsonText.timeSpan(2), "\"00:00:02\"")
        XCTAssertEqual(JsonText.timeSpan(3.5), "\"00:00:03.5000000\"")
        XCTAssertEqual(JsonText.timeSpan(90_061), "\"1.01:01:01\"")
        XCTAssertEqual(JsonText.date(Date(timeIntervalSince1970: 1_767_268_800.25)), "\"2026-01-01T12:00:00.25+00:00\"")
        let u = "\\" + "u" // avoids a literal backslash-u sequence in this file
        XCTAssertEqual(JsonText.string("a\"b\\c/d+e\n\u{e9}"), "\"a" + u + "0022b\\\\c/d" + u + "002Be\\n" + u + "00E9\"")
    }

    func testDosDateTime() {
        var c = DateComponents()
        c.year = 2000; c.month = 1; c.day = 1; c.hour = 0; c.minute = 0; c.second = 0
        c.timeZone = TimeZone.current
        let date = Calendar(identifier: .gregorian).date(from: c)!
        let (d, t) = ZipWriter.dosDateTime(date)
        XCTAssertEqual(d, Fixture.dosDate)
        XCTAssertEqual(t, 0)
    }
}
