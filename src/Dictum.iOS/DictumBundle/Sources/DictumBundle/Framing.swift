// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import CryptoKit
import Foundation

public enum Framing {
    public static let chunkSize = 1_048_576
    static let sealedMagic = Data([0x44, 0x43, 0x54, 0x4d, 0x45, 0x4e, 0x43, 0x01]) // "DCTMENC" 01
    static let audioMagic = Data([0x44, 0x43, 0x54, 0x4d, 0x41, 0x55, 0x44, 0x01])  // "DCTMAUD" 01

    /// The one AAD builder for DCTMENC and DCTMAUD (both audio operations):
    /// header:24 || sequence:int64LE || length:int32LE || UTF8("<meeting-id>/<entry-name>").
    static func aad(header: Data, sequence: Int64, length: Int32, identity: String) -> Data {
        var aad = Data(capacity: header.count + 12 + identity.utf8.count)
        aad.append(header)
        aad.appendLE(sequence)
        aad.appendLE(length)
        aad.append(contentsOf: identity.utf8)
        return aad
    }

    static func identity(_ meetingId: MeetingId, _ entryName: String) -> String { meetingId.hex + "/" + entryName }

    static func key(_ data: Data) throws -> SymmetricKey {
        guard data.count == 32 else { throw DictumBundleError.invalidKeyLength }
        return SymmetricKey(data: data)
    }

    /// AES-256-GCM with a caller-chosen 12-byte nonce; returns (ciphertext, tag).
    static func seal(_ plain: Data, key: SymmetricKey, nonce: Data, aad: Data) throws -> (Data, Data) {
        guard nonce.count == 12 else { throw DictumBundleError.invalidNonceLength }
        let box = try AES.GCM.seal(plain, using: key, nonce: AES.GCM.Nonce(data: nonce), authenticating: aad)
        return (box.ciphertext, box.tag)
    }

    static func open(_ cipher: Data, tag: Data, key: SymmetricKey, nonce: Data, aad: Data) throws -> Data {
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce), ciphertext: cipher, tag: tag)
            return try AES.GCM.open(box, using: key, authenticating: aad)
        } catch { throw DictumBundleError.authenticationFailed }
    }
}

public struct KeyWrap {
    /// 60 bytes: nonce(12) || ciphertext of the 32-byte content key(32) || tag(16).
    /// AAD is the UTF-8 recipient id only (for example `transfer`).
    public static func wrap(contentKey: Data, recipientKey: Data, recipientId: String, nonce: Data) throws -> Data {
        guard contentKey.count == 32 else { throw DictumBundleError.invalidKeyLength }
        let (cipher, tag) = try Framing.seal(contentKey, key: try Framing.key(recipientKey), nonce: nonce, aad: Data(recipientId.utf8))
        return nonce + cipher + tag
    }

    static func unwrap(_ wrap: Data, recipientKey: Data, recipientId: String) throws -> Data {
        guard wrap.count == 60 else { throw DictumBundleError.malformed }
        let w = Data(wrap)
        return try Framing.open(w.subdata(in: 12..<44), tag: w.subdata(in: 44..<60), key: try Framing.key(recipientKey),
                                nonce: w.subdata(in: 0..<12), aad: Data(recipientId.utf8))
    }
}

public enum SealedFile {
    /// DCTMENC: magic(8) || identity(16), then `length:int32LE || nonce:12 || ciphertext || tag:16` records of at
    /// most 1 MiB, then a zero-length terminal record. Byte-source order: identity, then one nonce per record.
    public static func seal(plaintext: Data, contentKey: Data, meetingId: MeetingId, entryName: String,
                            bytes: inout some ByteSource) throws -> Data {
        let key = try Framing.key(contentKey)
        let header = Framing.sealedMagic + bytes.bytes(16)
        let identity = Framing.identity(meetingId, entryName)
        let plain = Data(plaintext)
        var out = header
        var offset = 0
        var sequence: Int64 = 0
        while true {
            let length = min(Framing.chunkSize, plain.count - offset)
            let part = plain.subdata(in: offset..<offset + length)
            let aad = Framing.aad(header: header, sequence: sequence, length: Int32(length), identity: identity)
            let nonce = bytes.bytes(12)
            let (cipher, tag) = try Framing.seal(part, key: key, nonce: nonce, aad: aad)
            out.appendLE(Int32(length))
            out.append(nonce); out.append(cipher); out.append(tag)
            sequence += 1
            offset += length
            if length == 0 { break }
        }
        return out
    }

    /// Verify-then-release decryption of a whole DCTMENC file (internal; used by recovery and tests).
    static func open(_ sealedData: Data, contentKey: Data, meetingId: MeetingId, entryName: String) throws -> Data {
        let data = Data(sealedData)
        let key = try Framing.key(contentKey)
        guard data.count >= 24, data.prefix(8) == Framing.sealedMagic else { throw DictumBundleError.malformed }
        let header = data.subdata(in: 0..<24)
        let identity = Framing.identity(meetingId, entryName)
        var plain = Data()
        var position = 24
        var sequence: Int64 = 0
        while true {
            guard data.count - position >= 4 else { throw DictumBundleError.malformed }
            let length = Int(data.readLE(Int32.self, at: position))
            guard length >= 0, length <= Framing.chunkSize, data.count - position - 4 >= 12 + length + 16 else { throw DictumBundleError.malformed }
            let nonce = data.subdata(in: position + 4..<position + 16)
            let cipher = data.subdata(in: position + 16..<position + 16 + length)
            let tag = data.subdata(in: position + 16 + length..<position + 32 + length)
            let aad = Framing.aad(header: header, sequence: sequence, length: Int32(length), identity: identity)
            plain.append(try Framing.open(cipher, tag: tag, key: key, nonce: nonce, aad: aad))
            position += 32 + length
            sequence += 1
            if length == 0 {
                guard position == data.count else { throw DictumBundleError.malformed }
                return plain
            }
        }
    }
}

/// Streams PCM16 mono 16 kHz into the DCTMAUD chunked form, sealing each record as it is written,
/// so no plaintext audio touches disk. Each record:
/// `length:int32LE || framingNonce:12 || framingTag:16 || payloadNonce:12 || ciphertext || payloadTag:16`.
/// Both AES-GCM operations use the same AAD whose length field is the record's PCM length.
public final class ChunkedAudioWriter {
    private let handle: FileHandle
    private let key: SymmetricKey
    private let header: Data
    private let identity: String
    private var bytes: any ByteSource
    private var sequence: Int64 = 0
    private var closed = false
    private var failed = false
    public private(set) var bytesWritten: Int64 = 0
    public private(set) var pcmBytes: Int64 = 0

    /// Creates the file (refusing an existing one), writes and syncs the header.
    public init(fileURL: URL, contentKey: Data, meetingId: MeetingId, channel: String, bytes: some ByteSource) throws {
        key = try Framing.key(contentKey)
        var source: any ByteSource = bytes
        header = Framing.audioMagic + source.bytes(16)
        self.bytes = source
        identity = Framing.identity(meetingId, "audio-\(channel).chunks.enc")
        let fd = open(fileURL.path, O_WRONLY | O_CREAT | O_EXCL, 0o600)
        guard fd >= 0 else { throw CocoaError(.fileWriteFileExists, userInfo: [NSFilePathErrorKey: fileURL.path]) }
        handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        do {
            try handle.write(contentsOf: header)
            try handle.synchronize()
            bytesWritten = Int64(header.count)
        } catch {
            try? handle.close()
            throw error
        }
    }

    /// Appends even-length PCM, split into records of at most `Framing.chunkSize`; each record is synced.
    public func write(pcm: Data) throws {
        guard !closed, !failed else { throw DictumBundleError.writerClosed }
        guard pcm.count % 2 == 0 else { throw DictumBundleError.oddPcmLength }
        var offset = pcm.startIndex
        while offset < pcm.endIndex {
            let end = min(offset + Framing.chunkSize, pcm.endIndex)
            try record(Data(pcm[offset..<end]))
            offset = end
        }
    }

    /// Writes the zero-length terminal record (both nonce/tag pairs), then closes.
    public func complete() throws {
        guard !closed, !failed else { throw DictumBundleError.writerClosed }
        try record(Data())
        closed = true
        try handle.close()
    }

    /// Closes without a terminal record (the stream then reads as an incomplete snapshot).
    public func abandon() {
        guard !closed else { return }
        closed = true
        try? handle.close()
    }

    private func record(_ pcm: Data) throws {
        do {
            let aad = Framing.aad(header: header, sequence: sequence, length: Int32(pcm.count), identity: identity)
            let payloadNonce = bytes.bytes(12)
            let (cipher, payloadTag) = try Framing.seal(pcm, key: key, nonce: payloadNonce, aad: aad)
            let framingNonce = bytes.bytes(12)
            let (_, framingTag) = try Framing.seal(Data(), key: key, nonce: framingNonce, aad: aad)
            var out = Data(capacity: 4 + 56 + pcm.count)
            out.appendLE(Int32(pcm.count))
            out.append(framingNonce); out.append(framingTag)
            out.append(payloadNonce); out.append(cipher); out.append(payloadTag)
            try handle.write(contentsOf: out)
            try handle.synchronize()
            sequence += 1
            bytesWritten += Int64(out.count)
            pcmBytes += Int64(pcm.count)
        } catch {
            failed = true
            throw error
        }
    }

    deinit { if !closed { try? handle.close() } }
}

/// Internal reader mirroring the desktop's verification rules; used by tests and recovery checks.
enum ChunkedAudio {
    /// Returns the PCM after verifying every record. `incomplete` accepts EOF without a terminal record and a
    /// torn final record, but only after an intact framing tag has verified (section 13.2).
    static func open(_ fileData: Data, contentKey: Data, meetingId: MeetingId, channel: String, incomplete: Bool) throws -> Data {
        let data = Data(fileData)
        let key = try Framing.key(contentKey)
        guard data.count >= 24, data.prefix(8) == Framing.audioMagic else { throw DictumBundleError.malformed }
        let header = data.subdata(in: 0..<24)
        let identity = Framing.identity(meetingId, "audio-\(channel).chunks.enc")
        var pcm = Data()
        var position = 24
        var sequence: Int64 = 0
        while true {
            let remaining = data.count - position
            if incomplete && remaining < 4 { return pcm }
            guard remaining >= 4 else { throw DictumBundleError.malformed }
            let length = Int(data.readLE(Int32.self, at: position))
            guard length >= 0, length <= Framing.chunkSize, length % 2 == 0 else { throw DictumBundleError.malformed }
            if incomplete && remaining - 4 < 28 { return pcm }
            guard remaining - 4 >= 28 else { throw DictumBundleError.malformed }
            let aad = Framing.aad(header: header, sequence: sequence, length: Int32(length), identity: identity)
            _ = try Framing.open(Data(), tag: data.subdata(in: position + 16..<position + 32), key: key,
                                 nonce: data.subdata(in: position + 4..<position + 16), aad: aad)
            let body = position + 32
            if incomplete && data.count - body < length + 28 { return pcm }
            guard data.count - body >= length + 28 else { throw DictumBundleError.malformed }
            pcm.append(try Framing.open(data.subdata(in: body + 12..<body + 12 + length),
                                        tag: data.subdata(in: body + 12 + length..<body + 28 + length), key: key,
                                        nonce: data.subdata(in: body..<body + 12), aad: aad))
            position = body + 28 + length
            sequence += 1
            if length == 0 {
                guard position == data.count else { throw DictumBundleError.malformed }
                return pcm
            }
        }
    }
}

/// Test helper: verifies a sealed entry file by its header magic and file name.
enum Verifier {
    static func verify(fileURL: URL, contentKey: Data, meetingId: MeetingId, incomplete: Bool = false) throws -> Data {
        let data = try Data(contentsOf: fileURL)
        let name = fileURL.lastPathComponent
        if data.prefix(8) == Framing.audioMagic {
            guard name.hasPrefix("audio-"), name.hasSuffix(".chunks.enc") else { throw DictumBundleError.malformed }
            let channel = String(name.dropFirst(6).dropLast(11))
            return try ChunkedAudio.open(data, contentKey: contentKey, meetingId: meetingId, channel: channel, incomplete: incomplete)
        }
        return try SealedFile.open(data, contentKey: contentKey, meetingId: meetingId, entryName: name)
    }
}
