import Foundation
import Security

/// Keeps each meeting's 32-byte content key in the Keychain from before the session is created until the bundle is
/// sealed, so a recording cut short by a crash or a kill can still be sealed on the next launch.
enum ContentKeyStore {
    static let service = "com.kynni.dictum.recorder.contentkey"

    enum KeyError: Error { case random(OSStatus), keychain(OSStatus) }

    /// 32 bytes from the system's secure random generator.
    static func newKey() throws -> Data {
        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        guard status == errSecSuccess else { throw KeyError.random(status) }
        defer { for i in bytes.indices { bytes[i] = 0 } }
        return Data(bytes)
    }

    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    static func save(_ key: Data, meetingHex: String) throws {
        SecItemDelete(query(meetingHex) as CFDictionary)
        var add = query(meetingHex)
        add[kSecValueData as String] = key
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeyError.keychain(status) }
    }

    static func load(meetingHex: String) -> Data? {
        var q = query(meetingHex)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess else { return nil }
        return out as? Data
    }

    static func delete(meetingHex: String) {
        SecItemDelete(query(meetingHex) as CFDictionary)
    }
}
