import Foundation
import Security

/// The "Your voice" voice-print and the name to show, as one Keychain item on this device: available while the phone is
/// unlocked, never synchronised, never backed up to another device. Nothing else keeps them: not the meeting bundle, the
/// meeting record, the defaults, a log or the facts box. Blocking: call off the main thread.
enum HostVoiceStore {
    static let service = "com.kynni.dictum.recorder.hostvoice"

    struct Stored: Sendable {
        var print: [Float]
        var name: String
    }

    /// The kept voice-print and name, or nil when none is kept or the item cannot be read (fails soft).
    static func load() -> Stored? {
        var q = query()
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data,
              let value = HostVoice.decode(data) else { return nil }
        return Stored(print: value.print, name: value.name)
    }

    /// Replaces any kept voice-print. False when it could not be saved.
    @discardableResult
    static func save(print: [Float], name: String) -> Bool {
        guard let data = HostVoice.encode(print: print, name: name) else { return false }
        SecItemDelete(query() as CFDictionary)
        var add = query()
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    /// Deletes the kept voice-print and name. True when none is left.
    @discardableResult
    static func remove() -> Bool {
        let status = SecItemDelete(query() as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }

    private static func query() -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: "host", kSecAttrSynchronizable as String: false]
    }
}
