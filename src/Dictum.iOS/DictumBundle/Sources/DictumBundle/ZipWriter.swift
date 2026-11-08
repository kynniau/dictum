// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation

/// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320), as used by ZIP.
struct Crc32 {
    private static let table: [UInt32] = (0..<256).map { n -> UInt32 in
        var c = UInt32(n)
        for _ in 0..<8 { c = (c & 1) != 0 ? 0xedb8_8320 ^ (c >> 1) : c >> 1 }
        return c
    }
    private var state: UInt32 = 0xffff_ffff
    mutating func update(_ data: Data) {
        var c = state
        for b in data { c = Crc32.table[Int((c ^ UInt32(b)) & 0xff)] ^ (c >> 8) }
        state = c
    }
    var value: UInt32 { state ^ 0xffff_ffff }
}

/// Minimal classic-ZIP writer: STORED entries only, ASCII names, no extra fields, comments, data
/// descriptors or ZIP64; made-by 2.0/DOS, version needed 2.0, flags 0, attributes 0.
public struct ZipWriter {
    private enum Source { case data(Data), file(URL) }
    private struct Entry { let name: String; let source: Source; let crc: UInt32; let size: Int64; let date: UInt16; let time: UInt16 }
    private var entries: [Entry] = []
    private static let copyBlock = 1 << 20

    public init() {}

    public mutating func addStored(name: String, data: Data, dosDate: UInt16, dosTime: UInt16) {
        var crc = Crc32()
        crc.update(data)
        entries.append(Entry(name: name, source: .data(data), crc: crc.value, size: Int64(data.count), date: dosDate, time: dosTime))
    }

    /// Streams the file once for its CRC now; `write(to:)` streams it again and refuses it if it changed.
    public mutating func addStored(name: String, fileURL: URL, dosDate: UInt16, dosTime: UInt16) throws {
        let (crc, size) = try Self.scan(fileURL, into: nil)
        entries.append(Entry(name: name, source: .file(fileURL), crc: crc, size: size, date: dosDate, time: dosTime))
    }

    /// Writes a temporary archive beside the destination, syncs it, then renames it over the destination.
    public func write(to url: URL) throws {
        for e in entries {
            guard !e.name.isEmpty, e.name.utf8.count <= 255, e.name.utf8.allSatisfy({ $0 >= 0x21 && $0 <= 0x7e && $0 != 0x2f && $0 != 0x5c }) else {
                throw DictumBundleError.invalidEntryName
            }
        }
        guard Set(entries.map { $0.name.lowercased() }).count == entries.count else { throw DictumBundleError.invalidEntryName }
        guard entries.count < 0xffff else { throw DictumBundleError.tooLargeForZip }

        let temp = url.deletingLastPathComponent().appendingPathComponent("." + url.lastPathComponent + "." + UUID().uuidString + ".tmp")
        let fd = open(temp.path, O_WRONLY | O_CREAT | O_EXCL, 0o600)
        guard fd >= 0 else { throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: temp.path]) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        var renamed = false
        defer {
            try? handle.close()
            if !renamed { try? FileManager.default.removeItem(at: temp) }
        }

        var offset: Int64 = 0
        var central = Data()
        for e in entries {
            let name = Data(e.name.utf8)
            guard offset <= 0xffff_ffff, e.size <= 0xffff_ffff else { throw DictumBundleError.tooLargeForZip }
            var local = Data()
            local.appendLE(UInt32(0x0403_4b50))
            local.appendLE(UInt16(20)); local.appendLE(UInt16(0)); local.appendLE(UInt16(0)) // needed, flags, method
            local.appendLE(e.time); local.appendLE(e.date); local.appendLE(e.crc)
            local.appendLE(UInt32(e.size)); local.appendLE(UInt32(e.size))
            local.appendLE(UInt16(name.count)); local.appendLE(UInt16(0))
            local.append(name)
            try handle.write(contentsOf: local)
            switch e.source {
            case .data(let data): try handle.write(contentsOf: data)
            case .file(let fileURL):
                let (crc, size) = try Self.scan(fileURL, into: handle)
                guard crc == e.crc, size == e.size else { throw DictumBundleError.entryChanged }
            }
            central.appendLE(UInt32(0x0201_4b50))
            central.appendLE(UInt16(20)); central.appendLE(UInt16(20)) // made by 2.0 / DOS, needed 2.0
            central.appendLE(UInt16(0)); central.appendLE(UInt16(0))   // flags, method
            central.appendLE(e.time); central.appendLE(e.date); central.appendLE(e.crc)
            central.appendLE(UInt32(e.size)); central.appendLE(UInt32(e.size))
            central.appendLE(UInt16(name.count)); central.appendLE(UInt16(0)); central.appendLE(UInt16(0)) // name, extra, comment
            central.appendLE(UInt16(0)); central.appendLE(UInt16(0)); central.appendLE(UInt32(0))       // disk, internal, external
            central.appendLE(UInt32(offset))
            central.append(name)
            offset += Int64(local.count) + e.size
        }
        guard offset + Int64(central.count) + 22 <= 0xffff_ffff else { throw DictumBundleError.tooLargeForZip }
        var end = Data()
        end.appendLE(UInt32(0x0605_4b50))
        end.appendLE(UInt16(0)); end.appendLE(UInt16(0))
        end.appendLE(UInt16(entries.count)); end.appendLE(UInt16(entries.count))
        end.appendLE(UInt32(central.count)); end.appendLE(UInt32(offset))
        end.appendLE(UInt16(0))
        try handle.write(contentsOf: central)
        try handle.write(contentsOf: end)
        try handle.synchronize()
        try handle.close()
        guard rename(temp.path, url.path) == 0 else { throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path]) }
        renamed = true
    }

    /// DOS date and time in the device's current time zone (the ZIP convention); years before 1980 clamp to 1980-01-01.
    public static func dosDateTime(_ date: Date) -> (UInt16, UInt16) {
        let calendar = Calendar(identifier: .gregorian)
        let c = calendar.dateComponents(in: TimeZone.current, from: date)
        guard let year = c.year, year >= 1980 else { return (0x0021, 0) }
        let clampedYear = min(year, 2107)
        let d = UInt16((clampedYear - 1980) << 9 | c.month! << 5 | c.day!)
        let t = UInt16(c.hour! << 11 | c.minute! << 5 | c.second! / 2)
        return (d, t)
    }

    private static func scan(_ url: URL, into output: FileHandle?) throws -> (UInt32, Int64) {
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        var crc = Crc32()
        var size: Int64 = 0
        while let block = try input.read(upToCount: copyBlock), !block.isEmpty {
            crc.update(block)
            size += Int64(block.count)
            try output?.write(contentsOf: block)
        }
        return (crc.value, size)
    }
}
