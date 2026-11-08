// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation

/// Errors carry no user-facing text; the app maps them to its own approved wording.
public enum DictumBundleError: Error, Equatable, Sendable {
    case invalidKeyLength
    case invalidNonceLength
    case oddPcmLength
    case writerClosed
    case codeRequired
    case tooLargeForZip
    case invalidEntryName
    case entryChanged
    case sessionExists
    case sessionFinished
    case sessionMissingId
    case invalidMetadata
    case authenticationFailed
    case malformed
    case keyDerivationFailed
}

/// A Dictum meeting id: 32 lowercase hexadecimal characters (16 random bytes).
public struct MeetingId: Equatable, Hashable, Sendable {
    public let hex: String

    public init?(hex: String) {
        guard hex.utf8.count == 32,
              hex.utf8.allSatisfy({ ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) }) else { return nil }
        self.hex = hex
    }

    public static func random() -> MeetingId {
        var source = SystemByteSource()
        return MeetingId(hex: Hex.encode(source.bytes(16)))!
    }
}

/// Supplies header identities, nonces, salts and transfer-code entropy.
public protocol ByteSource {
    mutating func bytes(_ count: Int) -> Data
}

/// Cryptographically secure bytes from the system generator (arc4random_buf on Apple platforms).
public struct SystemByteSource: ByteSource {
    public init() {}
    public mutating func bytes(_ count: Int) -> Data {
        var generator = SystemRandomNumberGenerator()
        var data = Data(count: count)
        data.withUnsafeMutableBytes { raw in
            for i in 0..<count { raw[i] = UInt8.random(in: .min ... .max, using: &generator) }
        }
        return data
    }
}

/// TEST ONLY. Deterministic, predictable bytes for conformance fixtures: never use for real
/// recordings, because predictable nonces and salts destroy the encryption's guarantees.
/// Each call returns `count` bytes counting up by one from its start value; the first call
/// starts at `start`, and every later call starts 0x10 above the previous call's start
/// (wrapping at 0xff), matching the C# fixture generator.
public struct CounterByteSource: ByteSource {
    private var next: UInt8
    public init(start: UInt8 = 0) { next = start }
    public mutating func bytes(_ count: Int) -> Data {
        let first = next
        next = next &+ 0x10
        return Data((0..<count).map { first &+ UInt8(truncatingIfNeeded: $0) })
    }
}

/// Lets several holders draw from one byte source so a stateful (test) source never repeats.
final class SharedByteBox {
    var source: any ByteSource
    init(_ source: any ByteSource) { self.source = source }
}

struct SharedBytes: ByteSource {
    let box: SharedByteBox
    mutating func bytes(_ count: Int) -> Data { box.source.bytes(count) }
}

enum Hex {
    static func encode(_ data: Data) -> String {
        let digits = Array("0123456789abcdef".utf8)
        var out = [UInt8]()
        out.reserveCapacity(data.count * 2)
        for b in data { out.append(digits[Int(b >> 4)]); out.append(digits[Int(b & 0x0f)]) }
        return String(decoding: out, as: UTF8.self)
    }
}

extension Data {
    /// Overwrites the bytes in place. Swift may have made copies earlier (copy-on-write);
    /// see the simplification note in RecordingSession.
    mutating func zeroise() { resetBytes(in: 0..<count) }

    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        var little = value.littleEndian
        Swift.withUnsafeBytes(of: &little) { append(contentsOf: $0) }
    }

    func readLE<T: FixedWidthInteger>(_ type: T.Type, at offset: Int) -> T {
        var value: T = 0
        for i in 0..<MemoryLayout<T>.size {
            value |= T(truncatingIfNeeded: self[self.startIndex + offset + i]) << (8 * i)
        }
        return value
    }
}
