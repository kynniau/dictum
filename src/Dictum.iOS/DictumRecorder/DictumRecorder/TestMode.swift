import DictumBundle
import Foundation
import Security

// Test mode (owner, 17/11/2026): "while its just me testing this app ... turn off the 6 word secrets ... we can add the
// security again when we launch to customers".
//
// simplification: with test mode on, every meeting this phone seals or sends again is wrapped under ONE standing code
// made on this phone, in place of a fresh code per meeting. The file stays a normal sealed `.dictum`, so the
// computer's importer and the notice read to the room are unchanged. Ceiling: anyone holding the standing code can
// open every test-mode file, so it is for the owner's own testing only. Trigger to retire: before any build leaves
// the owner's hands; the whole type compiles to "off" outside DEBUG.
enum TestMode {
    static let flagKey = "settings.testMode"
    static let service = "com.kynni.dictum.recorder.testcode"

    #if DEBUG
    static var isOn: Bool { UserDefaults.standard.bool(forKey: flagKey) }

    /// The code to seal with, or nil for a fresh code (test mode off, or no usable standing code).
    static func sealingCode() -> TransferCode? {
        guard isOn, let text = storedCode() else { return nil }
        return code(from: text)
    }

    /// Six non-empty words, else nil.
    static func code(from text: String) -> TransferCode? {
        let words = TransferCode.normalise(text).split(separator: " ").map(String.init)
        return words.count == 6 ? TransferCode(words: words) : nil
    }

    /// The standing code, made on first use and kept in the Keychain (this device only, never synchronised).
    @discardableResult
    static func ensureCode() -> String? {
        if let existing = storedCode(), code(from: existing) != nil { return existing }
        var bytes = SystemByteSource()
        let text = TransferCode.random(using: &bytes).text
        var add = query()
        add[kSecValueData as String] = Data(text.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        SecItemDelete(query() as CFDictionary)
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess ? text : nil
    }

    static func storedCode() -> String? {
        var q = query()
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private static func query() -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: "standing", kSecAttrSynchronizable as String: false]
    }
    #else
    static var isOn: Bool { false }
    static func sealingCode() -> TransferCode? { nil }
    #endif
}
