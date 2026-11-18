import Foundation
import Security

/// The "Your words" list as one Keychain item on this device: available while the phone is unlocked, never
/// synchronised, never backed up to another device. The list can hold people's names, so nothing else keeps it: not the
/// meeting bundle, the defaults or a log. (A fix that was made shows in the transcript it was made in.) Blocking: the
/// `async` calls run it off the main thread.
enum YourWordsStore {
    static let service = "com.kynni.dictum.recorder.words"

    /// The kept list; empty when there is none or the item cannot be read (fails soft: no fixes are made).
    static func load() -> [WordFix] {
        var q = query()
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data,
              let list = try? JSONDecoder().decode([WordFix].self, from: data) else { return [] }
        return list
    }

    /// Replaces the kept list (an empty list removes the item). False when it could not be saved.
    @discardableResult
    static func save(_ fixes: [WordFix]) -> Bool {
        let removed = SecItemDelete(query() as CFDictionary)
        guard !fixes.isEmpty else { return removed == errSecSuccess || removed == errSecItemNotFound }
        guard let data = try? JSONEncoder().encode(fixes) else { return false }
        var add = query()
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    static func current() async -> [WordFix] {
        await Task.detached(priority: .utility) { load() }.value
    }

    static func replace(_ fixes: [WordFix]) async -> Bool {
        await Task.detached(priority: .utility) { save(fixes) }.value
    }

    /// Adds one fix to the kept list (YourWords.adding). False when it cannot be kept, the list is full or saving failed.
    static func add(_ fix: WordFix) async -> Bool {
        await Task.detached(priority: .utility) {
            guard let next = YourWords.adding(fix, to: load()) else { return false }
            return save(next)
        }.value
    }

    private static func query() -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrAccount as String: "list", kSecAttrSynchronizable as String: false]
    }
}
