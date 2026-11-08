// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import Foundation

/// Hand-built JSON matching the desktop's default System.Text.Json output byte for byte for the
/// fixture values. JSONEncoder is not used: it escapes `/`, orders keys freely and cannot emit .NET
/// TimeSpan or DateTimeOffset text.
enum JsonText {
    /// Quoted string, escaped like System.Text.Json's default encoder: printable ASCII passes except
    /// `"` `&` `'` `+` `<` `>` `\` and backtick; \b \t \n \f \r and \\ use short forms; everything else
    /// is \uXXXX (upper-case hex, UTF-16 surrogate pairs above the BMP). The desktop reader accepts any
    /// valid JSON escaping, so only the fixture strings (plain ASCII) are byte-checked.
    static func string(_ value: String) -> String {
        var out = "\""
        for unit in value.utf16 {
            switch unit {
            case 0x08: out += "\\b"
            case 0x09: out += "\\t"
            case 0x0a: out += "\\n"
            case 0x0c: out += "\\f"
            case 0x0d: out += "\\r"
            case 0x5c: out += "\\\\"
            case 0x22, 0x26, 0x27, 0x2b, 0x3c, 0x3e, 0x60: out += escaped(unit)
            case 0x20...0x7e: out.unicodeScalars.append(Unicode.Scalar(UInt8(unit)))
            default: out += escaped(unit)
            }
        }
        return out + "\""
    }

    private static func escaped(_ unit: UInt16) -> String {
        let hex = String(unit, radix: 16, uppercase: true)
        return "\\u" + String(repeating: "0", count: 4 - hex.count) + hex
    }

    static func optional(_ value: String?) -> String { value.map(string) ?? "null" }
    static func optional(_ value: Int?) -> String { value.map(String.init) ?? "null" }

    /// ISO 8601 in UTC with a literal `+00:00` offset; fractional seconds (up to 7 digits, trailing zeros
    /// trimmed) only when non-zero, as System.Text.Json writes DateTimeOffset.
    static func date(_ date: Date) -> String {
        let ticks = Int64((date.timeIntervalSince1970 * 10_000_000).rounded())
        let (seconds, fraction) = floorDivMod(ticks, 10_000_000)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let c = calendar.dateComponents([.year, .month, .day, .hour, .minute, .second],
                                        from: Date(timeIntervalSince1970: TimeInterval(seconds)))
        var text = pad(c.year!, 4) + "-" + pad(c.month!, 2) + "-" + pad(c.day!, 2) + "T" +
            pad(c.hour!, 2) + ":" + pad(c.minute!, 2) + ":" + pad(c.second!, 2)
        if fraction != 0 {
            var digits = pad(Int(fraction), 7)
            while digits.hasSuffix("0") { digits.removeLast() }
            text += "." + digits
        }
        return "\"" + text + "+00:00\""
    }

    /// .NET TimeSpan constant ("c") format: `[-][d.]hh:mm:ss[.fffffff]`, fraction always 7 digits when present.
    static func timeSpan(_ interval: TimeInterval) -> String {
        var ticks = Int64((interval * 10_000_000).rounded())
        var text = ""
        if ticks < 0 { text = "-"; ticks = -ticks }
        let fraction = ticks % 10_000_000
        let totalSeconds = ticks / 10_000_000
        let days = totalSeconds / 86_400
        if days > 0 { text += String(days) + "." }
        text += pad(Int(totalSeconds / 3600 % 24), 2) + ":" + pad(Int(totalSeconds / 60 % 60), 2) + ":" + pad(Int(totalSeconds % 60), 2)
        if fraction != 0 { text += "." + pad(Int(fraction), 7) }
        return "\"" + text + "\""
    }

    private static func pad(_ value: Int, _ width: Int) -> String {
        let s = String(value)
        return s.count >= width ? s : String(repeating: "0", count: width - s.count) + s
    }

    private static func floorDivMod(_ a: Int64, _ b: Int64) -> (Int64, Int64) {
        var q = a / b, r = a % b
        if r < 0 { q -= 1; r += b }
        return (q, r)
    }
}

public struct Agreement: Sendable {
    public var acknowledgedAt: Date
    public var how: String          // spoken | written | platform-announced
    public var scriptVersion: String
    public var kind: Int            // Live = 0, Import = 1
    public init(acknowledgedAt: Date, how: String, scriptVersion: String, kind: Int) {
        self.acknowledgedAt = acknowledgedAt; self.how = how; self.scriptVersion = scriptVersion; self.kind = kind
    }
}

public struct ProcessingRoute: Sendable {
    public var speechHost: String?
    public var summaryEngine: Int   // Local = 0, Ollama = 1, OwnKey = 2
    public var summaryHost: String?
    public var keepAudio: Int?      // Forever = 0, Days30 = 1, Days90 = 2
    public var summaryModel: String?
    public init(speechHost: String? = nil, summaryEngine: Int, summaryHost: String? = nil, keepAudio: Int?, summaryModel: String? = nil) {
        self.speechHost = speechHost; self.summaryEngine = summaryEngine; self.summaryHost = summaryHost
        self.keepAudio = keepAudio; self.summaryModel = summaryModel
    }
}

public struct Confirmation: Sendable {
    public var answer: String
    public var at: Date
    public var kind: Int
    public var scriptVersion: String
    public var script: String
    public var importerIdentity: String?
    public var route: ProcessingRoute?
    public init(answer: String, at: Date, kind: Int, scriptVersion: String, script: String,
                importerIdentity: String? = nil, route: ProcessingRoute? = nil) {
        self.answer = answer; self.at = at; self.kind = kind; self.scriptVersion = scriptVersion
        self.script = script; self.importerIdentity = importerIdentity; self.route = route
    }
}

public struct MeetingMeta: Sendable {
    public var id: MeetingId
    public var title: String
    public var startedAt: Date
    public var duration: TimeInterval
    public var source: Int          // Phone = 3
    public var agreement: Agreement?
    public var incompleteAudioChannels: [Int] // [] or [2] (Mixed)

    public init(id: MeetingId, title: String, startedAt: Date, duration: TimeInterval, source: Int = 3,
                agreement: Agreement?, incompleteAudioChannels: [Int] = []) {
        self.id = id; self.title = title; self.startedAt = startedAt; self.duration = duration
        self.source = source; self.agreement = agreement; self.incompleteAudioChannels = incompleteAudioChannels
    }

    /// Exact UTF-8 bytes per MEETINGS-CONTRACTS 14.A.1 (Pascal case, nulls included, `Wraps: []`).
    public func json() -> Data {
        var s = "{\"Id\":{\"Value\":" + JsonText.string(id.hex) + "}"
        s += ",\"Title\":" + JsonText.string(title)
        s += ",\"StartedAt\":" + JsonText.date(startedAt)
        s += ",\"Duration\":" + JsonText.timeSpan(duration)
        s += ",\"Source\":" + String(source)
        if let a = agreement {
            s += ",\"Agreement\":{\"AcknowledgedAt\":" + JsonText.date(a.acknowledgedAt) + ",\"How\":" + JsonText.string(a.how) +
                ",\"ScriptVersion\":" + JsonText.string(a.scriptVersion) + ",\"Kind\":" + String(a.kind) + "}"
        } else {
            s += ",\"Agreement\":null"
        }
        s += ",\"Markers\":[],\"Wraps\":[],\"HasVideo\":false,\"SummaryEngine\":null,\"SummaryTemplate\":null"
        s += ",\"IncompleteAudioChannels\":[" + incompleteAudioChannels.map(String.init).joined(separator: ",") + "]"
        s += ",\"AudioIncomplete\":" + (incompleteAudioChannels.isEmpty ? "false" : "true") + "}"
        return Data(s.utf8)
    }
}

public enum ConfirmationsJson {
    public static func json(_ records: [Confirmation]) -> Data {
        let items = records.map { r -> String in
            var s = "{\"Answer\":" + JsonText.string(r.answer) + ",\"At\":" + JsonText.date(r.at) + ",\"Kind\":" + String(r.kind)
            s += ",\"ScriptVersion\":" + JsonText.string(r.scriptVersion) + ",\"Script\":" + JsonText.string(r.script)
            s += ",\"ImporterIdentity\":" + JsonText.optional(r.importerIdentity) + ",\"Route\":"
            if let route = r.route {
                s += "{\"SpeechHost\":" + JsonText.optional(route.speechHost) + ",\"SummaryEngine\":" + String(route.summaryEngine) +
                    ",\"SummaryHost\":" + JsonText.optional(route.summaryHost) + ",\"KeepAudio\":" + JsonText.optional(route.keepAudio) +
                    ",\"SummaryModel\":" + JsonText.optional(route.summaryModel) + "}"
            } else {
                s += "null"
            }
            return s + "}"
        }
        return Data(("[" + items.joined(separator: ",") + "]").utf8)
    }
}

public struct ManifestEntry {
    public var name: String
    public var bytes: Int64
    public init(name: String, bytes: Int64) { self.name = name; self.bytes = bytes }
}

public struct Manifest {
    /// Camel-case manifest with standard padded base64; `transfer.kind` omitted, as the desktop export does.
    public static func json(meetingId: MeetingId, appVersion: String, platform: String, salt: Data, wrap: Data,
                            files: [ManifestEntry]) -> Data {
        var s = "{\"format\":\"dictum-bundle\",\"version\":1,\"meeting\":" + JsonText.string(meetingId.hex)
        s += ",\"createdBy\":{\"app\":\"Dictum\",\"version\":" + JsonText.string(appVersion) + ",\"platform\":" + JsonText.string(platform) + "}"
        s += ",\"transfer\":{\"kdf\":\"pbkdf2-hmac-sha256\",\"iterations\":600000,\"salt\":\"" + salt.base64EncodedString() +
            "\",\"wrap\":\"" + wrap.base64EncodedString() + "\"}"
        s += ",\"files\":[" + files.map { "{\"name\":" + JsonText.string($0.name) + ",\"bytes\":" + String($0.bytes) + "}" }
            .joined(separator: ",") + "]}"
        return Data(s.utf8)
    }
}
