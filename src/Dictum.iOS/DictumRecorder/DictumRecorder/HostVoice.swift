import Foundation

// "Your voice": the phone's owner records about 30 seconds of themselves once, and from then on the speaker group that
// matches that voice is named for them in every meeting. Pure arithmetic only (Foundation, no UIKit, SwiftUI or
// AVFoundation), so it compiles and self-checks on the Mac with swiftc.
//
// What this never does: keep the sound. The set-up audio lives in memory until the voice-print is made, and only the
// voice-print and the typed name are kept, in the Keychain on this device (HostVoiceStore.swift). The voice-print is
// never put in a meeting file, a meeting record, the defaults, a log or the facts box.

enum HostVoice {
    // simplification: a speaker group matches the voice-print when its centre is less than 0.5 cosine distance away.
    // Measured on the Mac, 18/11/2026, one person's print against 3 solo recordings and one three-person meeting:
    // same-person group centres sat 0.088 to 0.294 from the print, other people's groups 0.807 to 1.016, and a print from
    // about 30 s of speech was 0.062 from a print from 161 s. Ceiling: tuned on one person against two other voices.
    // Trigger to retire: more measured voices; then set the cut-off from their distances.
    static let matchCutOff = 0.5

    // simplification: a set-up window whose root-mean-square level is below 0.01 of full scale (about -40 dBFS) is taken
    // as silence and gets no voice print. Ceiling: a very quiet speaker or a far-off phone may lose real speech, and a
    // loud room's noise is kept. Trigger to retire: a device test where set-up says "not enough speech" for clear
    // speech, or a print that misses its owner; then use a voice activity detector.
    static let silenceRms: Float = 0.01

    /// At least this many voice-printed windows (2 s long, 1 s apart) before a voice-print is made.
    static let minWindows = 10

    /// The nearest speaker group to a voice-print.
    struct Match: Equatable, Sendable {
        var speaker: Int
        var distance: Double
    }

    /// The voice-print: every voice print made unit length, their mean, made unit length again. nil when there is none,
    /// when the prints differ in length, or when a value is not finite or a print has no length.
    static func print(from embeddings: [[Float]]) -> [Float]? {
        guard let first = embeddings.first, !first.isEmpty else { return nil }
        var sum = [Double](repeating: 0, count: first.count)
        for e in embeddings {
            guard e.count == first.count, e.allSatisfy(\.isFinite), let unit = unit(e.map(Double.init)) else { return nil }
            for i in sum.indices { sum[i] += unit[i] }
        }
        guard let mean = unit(sum) else { return nil }
        let out = mean.map(Float.init)
        return out.allSatisfy(\.isFinite) ? out : nil
    }

    /// 1 − cosine, floored at 0 (as the speaker step measures it); nil when the lengths differ or either has no length.
    static func distance(_ a: [Float], _ b: [Float]) -> Double? {
        guard a.count == b.count, !a.isEmpty, let ua = unit(a.map(Double.init)), let ub = unit(b.map(Double.init)) else {
            return nil
        }
        let d = max(0, 1 - zip(ua, ub).reduce(0) { $0 + $1.0 * $1.1 })
        return d.isFinite ? d : nil
    }

    /// The group whose centre is nearest the voice-print, at any distance; of equals, the lowest speaker number.
    static func nearest(print: [Float], groupCentres: [Int: [Float]]) -> Match? {
        var best: Match?
        for speaker in groupCentres.keys.sorted() {
            guard let d = distance(print, groupCentres[speaker]!) else { continue }
            if best == nil || d < best!.distance { best = Match(speaker: speaker, distance: d) }
        }
        return best
    }

    /// The speaker number whose group centre is nearest the voice-print, only when that distance is below `cutOff`.
    static func match(print: [Float], groupCentres: [Int: [Float]], cutOff: Double = matchCutOff) -> Int? {
        guard let best = nearest(print: print, groupCentres: groupCentres), best.distance < cutOff else { return nil }
        return best.speaker
    }

    /// Each speaker's group centre: the mean of its unit vectors, made unit again. `speakers[i]` is the speaker number of
    /// `vectors[i]` (nil when it ended in no turn); in segment-mean mode the vectors are the segments' mean prints.
    static func centres(vectors: [[Float]], speakers: [Int?]) -> [Int: [Float]] {
        var sums: [Int: [Double]] = [:]
        for (i, v) in vectors.enumerated() {
            guard i < speakers.count, let speaker = speakers[i], v.allSatisfy(\.isFinite),
                  let u = unit(v.map(Double.init)) else { continue }
            if let sum = sums[speaker], sum.count == u.count {
                sums[speaker] = zip(sum, u).map { $0 + $1 }
            } else if sums[speaker] == nil {
                sums[speaker] = u
            }
        }
        return sums.compactMapValues { sum in unit(sum).map { $0.map(Float.init) } }
    }

    /// Root-mean-square level of samples in -1...1; 0 when empty.
    static func rms(_ samples: ArraySlice<Float>) -> Float {
        guard !samples.isEmpty else { return 0 }
        let sum = samples.reduce(0.0) { $0 + Double($1) * Double($1) }
        return Float((sum / Double(samples.count)).squareRoot())
    }

    /// v / |v|, or nil when it has no length or is not finite.
    static func unit(_ v: [Double]) -> [Double]? {
        let length = v.reduce(0) { $0 + $1 * $1 }.squareRoot()
        guard length > 0, length.isFinite else { return nil }
        return v.map { $0 / length }
    }

    // MARK: Keychain value

    /// The kept value: version 1, the name's UTF-8 length (two bytes, little-endian), the name, then the voice-print as
    /// 32-bit floats, little-endian.
    static func encode(print: [Float], name: String) -> Data? {
        let nameBytes = Array(name.utf8)
        guard !print.isEmpty, print.allSatisfy(\.isFinite), !nameBytes.isEmpty, nameBytes.count <= Int(UInt16.max) else {
            return nil
        }
        var out = Data([1, UInt8(nameBytes.count & 0xFF), UInt8(nameBytes.count >> 8)])
        out.append(contentsOf: nameBytes)
        for value in print {
            withUnsafeBytes(of: value.bitPattern.littleEndian) { out.append(contentsOf: $0) }
        }
        return out
    }

    static func decode(_ data: Data) -> (print: [Float], name: String)? {
        let bytes = [UInt8](data)
        guard bytes.count >= 3, bytes[0] == 1 else { return nil }
        let nameCount = Int(bytes[1]) | Int(bytes[2]) << 8
        let floatsAt = 3 + nameCount
        guard nameCount > 0, bytes.count > floatsAt, (bytes.count - floatsAt) % 4 == 0,
              let name = String(bytes: bytes[3..<floatsAt], encoding: .utf8) else { return nil }
        var values: [Float] = []
        values.reserveCapacity((bytes.count - floatsAt) / 4)
        var at = floatsAt
        while at < bytes.count {
            let bits = UInt32(bytes[at]) | UInt32(bytes[at + 1]) << 8 | UInt32(bytes[at + 2]) << 16 | UInt32(bytes[at + 3]) << 24
            values.append(Float(bitPattern: bits))
            at += 4
        }
        return values.allSatisfy(\.isFinite) ? (values, name) : nil
    }
}

#if DEBUG
/// One line per check, "PASS …" or "FAIL …". No device or model needed.
enum HostVoiceSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }
        func near(_ a: [Float]?, _ b: [Float]) -> Bool {
            guard let a, a.count == b.count else { return false }
            return zip(a, b).allSatisfy { abs($0 - $1) < 1e-6 }
        }

        let same: [Float] = [3, 4, 0]
        check("print of identical vectors is that vector made unit",
              near(HostVoice.print(from: [same, same, same]), [0.6, 0.8, 0]), true)
        check("print of empty is nil", HostVoice.print(from: []) == nil, true)
        check("print with a non-finite value is nil", HostVoice.print(from: [[1, .nan]]) == nil, true)
        check("print with a zero vector is nil", HostVoice.print(from: [[0, 0], [1, 0]]) == nil, true)
        check("print weighs non-unit inputs equally",
              near(HostVoice.print(from: [[100, 0], [0, 0.01]]), [Float(0.5.squareRoot()), Float(0.5.squareRoot())]), true)

        let print: [Float] = [1, 0, 0]
        let centres: [Int: [Float]] = [1: [0, 1, 0], 2: [0.9, 0.1, 0], 3: [0.95, 0.4, 0]]
        check("match picks the nearest under the cut-off", HostVoice.match(print: print, groupCentres: centres), 2)
        check("nothing under the cut-off is nil",
              HostVoice.match(print: print, groupCentres: [1: [0, 1, 0], 2: [-1, 0, 0]]), nil)
        let atCut = HostVoice.distance(print, [1, 1, 0])!
        check("exactly at the cut-off is nil",
              HostVoice.match(print: print, groupCentres: [4: [1, 1, 0]], cutOff: atCut), nil)
        check("just under the cut-off matches",
              HostVoice.match(print: print, groupCentres: [4: [1, 1, 0]], cutOff: atCut.nextUp), 4)
        check("a tie goes to the lowest speaker number",
              HostVoice.match(print: print, groupCentres: [5: [2, 1, 0], 3: [2, 1, 0], 4: [2, 1, 0]]), 3)
        check("non-unit centres are measured by direction only",
              HostVoice.match(print: [10, 0, 0], groupCentres: [1: [0.001, 0, 0], 2: [500, 400, 0]]), 1)
        check("a centre of another length is skipped",
              HostVoice.match(print: print, groupCentres: [1: [1, 0], 2: [1, 0.1, 0]]), 2)
        check("no groups is nil", HostVoice.match(print: print, groupCentres: [:]), nil)
        check("the default cut-off is 0.5", HostVoice.matchCutOff, 0.5)

        let c = HostVoice.centres(vectors: [[2, 0], [0, 5], [1, 0], [9, 9]], speakers: [1, 2, 1, nil])
        check("centres group by speaker and skip vectors with no speaker", Set(c.keys), [1, 2])
        check("a centre is the unit mean", near(c[1], [1, 0]) && near(c[2], [0, 1]), true)

        check("rms of a constant", HostVoice.rms([0.5, -0.5, 0.5, -0.5][...]), 0.5)
        check("rms of nothing is 0", HostVoice.rms([][...]), 0)

        let stored = HostVoice.encode(print: [0.25, -1, 3.5], name: "Ab c")
        check("the kept value round-trips",
              stored.flatMap(HostVoice.decode).map { "\($0.name)|\($0.print)" }, "Ab c|[0.25, -1.0, 3.5]")
        check("an empty name is not kept", HostVoice.encode(print: [1], name: "") == nil, true)
        check("a cut-short value is not read", stored.flatMap { HostVoice.decode($0.dropLast(1)) } == nil, true)
        return out
    }
}
#endif
