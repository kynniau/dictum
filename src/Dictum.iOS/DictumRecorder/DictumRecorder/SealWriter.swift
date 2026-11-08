import DictumBundle
import Foundation

/// Owns the `RecordingSession` and serialises every call to it on one queue, so the audio thread only enqueues.
/// `@unchecked Sendable`: every mutable field and every session call is confined to `queue`; `onFailure` is set once
/// before capture starts and only read on the main thread.
final class SealWriter: @unchecked Sendable {
    private let queue = DispatchQueue(label: "dictum.seal")
    private let session: RecordingSession
    // The fields below are touched only on `queue`.
    private var bytesAppended: Int64 = 0
    private var closed = false

    /// Called on the main thread once, the first time an append fails.
    var onFailure: (() -> Void)?

    init(session: RecordingSession) {
        self.session = session
    }

    /// Called from the audio tap's thread. Never blocks.
    func append(_ pcm: Data) {
        queue.async { [self] in
            guard !closed else { return }
            do {
                try session.append(pcm16Mono16k: pcm)
                bytesAppended += Int64(pcm.count)
            } catch {
                closed = true
                DispatchQueue.main.async { self.onFailure?() }
            }
        }
    }

    /// Seals the bundle after every append already queued. Appends that arrive later are dropped.
    /// The duration is the audio actually captured (16 000 samples × 2 bytes per second), not the wall-clock time,
    /// so time lost to an interruption is not counted as recorded.
    func finish(at date: Date) async throws -> FinishedBundle {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
                closed = true
                let duration = TimeInterval(bytesAppended) / 32_000
                continuation.resume(with: Result { try session.finish(duration: duration, at: date) })
            }
        }
    }
}

/// Runs blocking work (sealing a recovered session) off the main thread.
func offMain<T>(_ work: @escaping () throws -> T) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
        DispatchQueue.global(qos: .userInitiated).async {
            continuation.resume(with: Result { try work() })
        }
    }
}

/// Where sessions and sealed bundles live, and the file protection they get.
enum SessionFiles {
    static let prefix = ".session-"

    static var documents: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
    }

    /// Readable after the first unlock, so capture keeps writing while the screen is locked.
    static let protection: [FileAttributeKey: Any] = [.protectionKey: FileProtectionType.completeUntilFirstUserAuthentication]

    static func sessionDirectory(hex: String) -> URL {
        documents.appendingPathComponent(prefix + hex, isDirectory: true)
    }

    static func makeSessionDirectory(hex: String) throws -> URL {
        let url = sessionDirectory(hex: hex)
        // Documents gets the same class so the sealed bundle written beside the session folder inherits it.
        try? FileManager.default.setAttributes(protection, ofItemAtPath: documents.path)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: protection)
        return url
    }

    /// Sets the protection class on a file or folder and everything inside it.
    static func protect(_ url: URL) {
        let fm = FileManager.default
        try? fm.setAttributes(protection, ofItemAtPath: url.path)
        if let items = fm.enumerator(at: url, includingPropertiesForKeys: nil) {
            for case let item as URL in items { try? fm.setAttributes(protection, ofItemAtPath: item.path) }
        }
    }

    /// Unfinished session folders left by a recording that was not sealed, in name order.
    static func pending() -> [(url: URL, hex: String)] {
        let items = (try? FileManager.default.contentsOfDirectory(at: documents, includingPropertiesForKeys: [.isDirectoryKey])) ?? []
        return items
            .filter { $0.lastPathComponent.hasPrefix(prefix) && ((try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false) }
            .map { (url: $0, hex: String($0.lastPathComponent.dropFirst(prefix.count))) }
            .sorted { $0.hex < $1.hex }
    }

    static func remove(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }
}

enum AppInfo {
    /// For example "0.1.0.1": CFBundleShortVersionString + "." + CFBundleVersion, read from the built Info.plist.
    static var version: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let short = info["CFBundleShortVersionString"] as? String ?? "0"
        let build = info["CFBundleVersion"] as? String ?? "0"
        return short + "." + build
    }
}
