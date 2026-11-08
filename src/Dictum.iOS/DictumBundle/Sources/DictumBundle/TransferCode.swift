// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import CommonCrypto
import Foundation

/// Six independent uniform words from the BIP-39 English list (MIT; see transfer-words.LICENSE).
/// Only the dictionary is reused: there is no BIP-39 checksum or seed derivation.
public struct TransferCode: Sendable {
    public let words: [String]
    public var text: String { words.joined(separator: " ") }

    public init(words: [String]) { self.words = words }

    static let wordList: [String] = {
        guard let url = Bundle.module.url(forResource: "transfer-words", withExtension: "txt"),
              let text = try? String(contentsOf: url, encoding: .utf8) else {
            preconditionFailure("transfer-words.txt resource missing")
        }
        let words = text.split(whereSeparator: { $0 == " " || $0 == "\n" || $0 == "\r" || $0 == "\t" }).map(String.init)
        precondition(words.count == 2048 && Set(words).count == 2048, "transfer-words.txt must hold 2048 unique words")
        return words
    }()

    /// 2048 = 2^11 divides 2^16, so masking a 16-bit value to 11 bits is exactly uniform:
    /// no modulo bias and no rejection loop needed.
    public static func random(using source: inout some ByteSource) -> TransferCode {
        let raw = source.bytes(12)
        let list = wordList
        let words = (0..<6).map { i -> String in
            let value = UInt16(raw[raw.startIndex + 2 * i]) | UInt16(raw[raw.startIndex + 2 * i + 1]) << 8
            return list[Int(value & 0x07ff)]
        }
        return TransferCode(words: words)
    }

    /// .NET `char.IsWhiteSpace` set, as listed in MEETINGS-CONTRACTS 14.A.1.
    static func isDotNetWhitespace(_ s: Unicode.Scalar) -> Bool {
        switch s.value {
        case 0x09...0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000: return true
        default: return false
        }
    }

    /// Splits on .NET whitespace, drops empty tokens, joins with one 0x20 and lowercases.
    /// Lowercasing is per scalar with the Unicode simple mapping (U+0130 maps to U+0069), which is
    /// what .NET invariant lowercasing does for dictionary words; non-ASCII parity is not verified.
    public static func normalise(_ code: String) -> String {
        var tokens: [String] = []
        var current = String.UnicodeScalarView()
        func flush() { if !current.isEmpty { tokens.append(String(current)); current = String.UnicodeScalarView() } }
        for scalar in code.unicodeScalars {
            if isDotNetWhitespace(scalar) { flush(); continue }
            if scalar.value == 0x130 { current.append("i"); continue }
            let lower = scalar.properties.lowercaseMapping.unicodeScalars
            if lower.count == 1 { current.append(lower.first!) } else { current.append(scalar) }
        }
        flush()
        return tokens.joined(separator: " ")
    }

    /// PBKDF2-HMAC-SHA256, 600000 iterations, 32 bytes, over the UTF-8 normalised code.
    /// Requires exactly six tokens and at most 256 UTF-16 code units, like the desktop reader.
    public static func deriveKey(code: String, salt: Data) throws -> Data {
        let normalised = normalise(code)
        guard normalised.split(separator: " ", omittingEmptySubsequences: false).count == 6,
              normalised.utf16.count <= 256 else { throw DictumBundleError.codeRequired }
        // simplification: PBKDF2 rather than a memory-hard KDF, matching the desktop format; retire when the
        // bundle format version adopts a memory-hard KDF.
        var password = Data(normalised.utf8)
        defer { password.zeroise() }
        var derived = Data(count: 32)
        let status = derived.withUnsafeMutableBytes { out in
            password.withUnsafeBytes { pw in
                salt.withUnsafeBytes { s in
                    CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2),
                                         pw.baseAddress?.assumingMemoryBound(to: CChar.self), pw.count,
                                         s.baseAddress?.assumingMemoryBound(to: UInt8.self), s.count,
                                         CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), 600_000,
                                         out.baseAddress?.assumingMemoryBound(to: UInt8.self), 32)
                }
            }
        }
        guard status == kCCSuccess else { throw DictumBundleError.keyDerivationFailed }
        return derived
    }
}
