import Foundation

// "Your words": fixes the user keeps for words the phone's transcriber keeps getting wrong (a name, a product, a term).
// Each fix is a pair: the words as the phone writes them, and what to write instead. After a transcription, every final
// line is passed through `YourWords.apply`. Why it works this way: the phone's SpeechTranscriber was given expected words
// (AnalysisContext.contextualStrings) on 18/11/2026 and wrote the same wrong words, so the fix is made afterwards.
//
// Pure rules only (Foundation, no device): the list itself is kept by YourWordsStore, the screens are in
// YourWordsViews.swift.
//
// simplification: a fix is matched inside one final line, so wrong words split across two lines are not fixed, and it is
// made every time the words appear, including when someone really said them (the screens say so). Ceiling: one line and
// exact words. Retire if a transcriber that takes expected words is adopted.

/// One fix: `heard` as the phone writes it, `meant` to write instead.
struct WordFix: Codable, Equatable, Sendable {
    var heard: String
    var meant: String
}

enum YourWords {
    /// The most fixes kept, and the longest either side may be, in characters.
    static let maxFixes = 200
    static let maxLength = 60

    /// A word of `text`: its comparison key (case and accents folded) and where it sits.
    struct Word: Equatable {
        var key: String
        var range: Range<String.Index>
    }

    private static func isWordCharacter(_ c: Character) -> Bool { c.isLetter || c.isNumber }
    private static func isApostrophe(_ c: Character) -> Bool { c == "'" || c == "\u{2019}" }

    /// Runs of letters and digits; an apostrophe counts only between two of them ("don't" is one word).
    static func words(_ text: String) -> [Word] {
        var out: [Word] = []
        var start: String.Index?
        var i = text.startIndex
        func close(_ end: String.Index) {
            guard let from = start else { return }
            let key = text[from..<end].replacingOccurrences(of: "\u{2019}", with: "'")
                .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            out.append(Word(key: key, range: from..<end))
            start = nil
        }
        while i < text.endIndex {
            let c = text[i]
            let next = text.index(after: i)
            let inside = isWordCharacter(c)
                || (isApostrophe(c) && start != nil && next < text.endIndex && isWordCharacter(text[next]))
            if inside {
                if start == nil { start = i }
            } else {
                close(i)
            }
            i = next
        }
        close(text.endIndex)
        return out
    }

    /// The fix with its spaces tidied, or nil when it cannot be kept: no word on the left, nothing on the right, a
    /// control character, either side too long, or both sides the same.
    static func tidy(_ fix: WordFix) -> WordFix? {
        func clean(_ s: String) -> String? {
            guard !s.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) && !CharacterSet.whitespacesAndNewlines.contains($0) })
            else { return nil }
            let joined = s.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            return joined.count <= maxLength ? joined : nil
        }
        guard let heard = clean(fix.heard), let meant = clean(fix.meant), !words(heard).isEmpty, !meant.isEmpty, heard != meant
        else { return nil }
        return WordFix(heard: heard, meant: meant)
    }

    /// `text` with every fix made, and how many were made. Whole words only, any capitals, any punctuation between the
    /// words. The longest `heard` wins where two could start at the same word; text already put in is not looked at
    /// again; words that already read as `meant` are left alone and not counted.
    static func apply(_ text: String, _ fixes: [WordFix]) -> (text: String, changed: Int) {
        let rules = fixes.enumerated()
            .compactMap { at, fix -> (keys: [String], meant: String, at: Int)? in
                let keys = words(fix.heard).map(\.key)
                return keys.isEmpty ? nil : (keys, fix.meant, at)
            }
            .sorted { ($0.keys.count, $1.at) > ($1.keys.count, $0.at) }
        guard !rules.isEmpty else { return (text, 0) }
        let tokens = words(text)
        var out = ""
        var cursor = text.startIndex
        var changed = 0
        var i = 0
        while i < tokens.count {
            guard let rule = rules.first(where: { rule in
                i + rule.keys.count <= tokens.count && rule.keys.indices.allSatisfy { tokens[i + $0].key == rule.keys[$0] }
            }) else {
                i += 1
                continue
            }
            let from = tokens[i].range.lowerBound
            let to = tokens[i + rule.keys.count - 1].range.upperBound
            // Already right here (the fix was made before, or the phone got it right): leave it, so a fix whose right
            // side contains its left side is not made twice.
            let already = text[from...].hasPrefix(rule.meant) || text[..<to].hasSuffix(rule.meant)
            if !already {
                out += text[cursor..<from]
                out += rule.meant
                cursor = to
                changed += 1
            }
            i += rule.keys.count
        }
        out += text[cursor...]
        return (out, changed)
    }

    /// The fix a person made by editing a line from `old` to `new`: the words that differ once the same words at the
    /// start and the end are set aside. Nil when nothing reusable changed (words only added or only removed, no change,
    /// or too much changed to keep).
    static func changed(from old: String, to new: String) -> WordFix? {
        var a = old.split(whereSeparator: \.isWhitespace)
        var b = new.split(whereSeparator: \.isWhitespace)
        while let x = a.first, let y = b.first, x == y {
            a.removeFirst()
            b.removeFirst()
        }
        while let x = a.last, let y = b.last, x == y {
            a.removeLast()
            b.removeLast()
        }
        var heard = a.joined(separator: " ")
        var meant = b.joined(separator: " ")
        // The same punctuation left on both ends is not part of the fix.
        while let x = heard.last, x == meant.last, !isWordCharacter(x) {
            heard.removeLast()
            meant.removeLast()
        }
        while let x = heard.first, x == meant.first, !isWordCharacter(x) {
            heard.removeFirst()
            meant.removeFirst()
        }
        return tidy(WordFix(heard: heard, meant: meant))
    }

    /// `list` with `fix` added. A fix for the same heard words is replaced. Nil when the fix cannot be kept or the list
    /// is full.
    static func adding(_ fix: WordFix, to list: [WordFix]) -> [WordFix]? {
        guard let fix = tidy(fix) else { return nil }
        let keys = words(fix.heard).map(\.key)
        var out = list.filter { words($0.heard).map(\.key) != keys }
        guard out.count < maxFixes else { return nil }
        out.append(fix)
        return out
    }
}

#if DEBUG
/// One line per check, "PASS …" or "FAIL …". No device or model needed.
enum YourWordsSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }
        func applied(_ text: String, _ fixes: [WordFix]) -> String { YourWords.apply(text, fixes).text }
        func count(_ text: String, _ fixes: [WordFix]) -> Int { YourWords.apply(text, fixes).changed }
        let name = WordFix(heard: "the stick to", meant: "Dictum")

        // Words.
        check("words: letters and digits, punctuation left out", YourWords.words("Hi, there 2day!").map(\.key), ["hi", "there", "2day"])
        check("words: an apostrophe inside a word stays", YourWords.words("don\u{2019}t 'quote'").map(\.key), ["don't", "quote"])
        check("words: accents and capitals fold", YourWords.words("Café").map(\.key), ["cafe"])

        // Apply.
        check("apply: the measured case (18/11/2026 solo test)",
              applied("uploading the stick to recording to the Mac", [name]), "uploading Dictum recording to the Mac")
        check("apply: counted once", count("uploading the stick to recording to the Mac", [name]), 1)
        check("apply: any capitals", applied("The Stick To works", [name]), "Dictum works")
        check("apply: punctuation around it stays", applied("Use (the stick to).", [name]), "Use (Dictum).")
        check("apply: punctuation between the words does not stop it", applied("the stick, to go", [name]), "Dictum go")
        check("apply: whole words only", applied("the sticker to", [name]), "the sticker to")
        check("apply: part of a longer word is left", applied("bathe stick tomorrow", [name]), "bathe stick tomorrow")
        check("apply: every time it appears", count("the stick to and the stick to", [name]), 2)
        check("apply: no fixes, no change", applied("the stick to", []), "the stick to")
        check("apply: no match, nothing counted", count("nothing here", [name]), 0)
        check("apply: the longer heard wins",
              applied("the stick to", [WordFix(heard: "the", meant: "a"), name]), "Dictum")
        check("apply: text put in is not fixed again",
              applied("alpha", [WordFix(heard: "alpha", meant: "beta"), WordFix(heard: "beta", meant: "gamma")]), "beta")
        check("apply: already right is left and not counted", count("Kynni said", [WordFix(heard: "kynni", meant: "Kynni")]), 0)
        check("apply: capitals are fixed", applied("kynni said", [WordFix(heard: "kynni", meant: "Kynni")]), "Kynni said")
        let grows = WordFix(heard: "dictum", meant: "Dictum Recorder")
        check("apply: a fix that contains its own heard words is not made twice (start)",
              applied(applied("open dictum now", [grows]), [grows]), "open Dictum Recorder now")
        let growsBack = WordFix(heard: "recorder", meant: "Dictum Recorder")
        check("apply: a fix that contains its own heard words is not made twice (end)",
              applied(applied("open recorder now", [growsBack]), [growsBack]), "open Dictum Recorder now")
        check("apply: spacing outside the fix is kept", applied(" the stick to  now ", [name]), " Dictum  now ")

        // The fix made by editing a line.
        check("changed: the words that differ", YourWords.changed(from: "uploading the stick to recording", to: "uploading Dictum recording"), name)
        check("changed: shared punctuation is not part of it",
              YourWords.changed(from: "use the stick to.", to: "use Dictum."), name)
        check("changed: the whole line", YourWords.changed(from: "kinney", to: "Kynni"), WordFix(heard: "kinney", meant: "Kynni"))
        check("changed: no change is no fix", YourWords.changed(from: "same words", to: "same words"), nil)
        check("changed: words only added is no fix", YourWords.changed(from: "a b", to: "a new b"), nil)
        check("changed: words only removed is no fix", YourWords.changed(from: "a um b", to: "a b"), nil)
        check("changed: too much changed is no fix",
              YourWords.changed(from: String(repeating: "word ", count: 20), to: "short"), nil)

        // Keeping.
        check("tidy: spaces tidied", YourWords.tidy(WordFix(heard: "  the  stick to ", meant: " Dictum ")), name)
        check("tidy: nothing on the right is refused", YourWords.tidy(WordFix(heard: "x", meant: " ")), nil)
        check("tidy: no word on the left is refused", YourWords.tidy(WordFix(heard: "...", meant: "x")), nil)
        check("tidy: both sides the same is refused", YourWords.tidy(WordFix(heard: "same", meant: "same")), nil)
        check("tidy: too long is refused", YourWords.tidy(WordFix(heard: String(repeating: "a", count: 61), meant: "x")), nil)
        check("tidy: a control character is refused", YourWords.tidy(WordFix(heard: "a\u{0}b", meant: "x")), nil)
        check("adding: a new fix goes on the end", YourWords.adding(name, to: [grows]), [grows, name])
        check("adding: the same heard words are replaced",
              YourWords.adding(WordFix(heard: "The Stick To", meant: "Dictum app"), to: [name, grows]),
              [grows, WordFix(heard: "The Stick To", meant: "Dictum app")])
        let full = (0..<YourWords.maxFixes).map { WordFix(heard: "word\($0)", meant: "x") }
        check("adding: a full list refuses a new fix", YourWords.adding(name, to: full) == nil, true)
        check("adding: a full list still replaces", YourWords.adding(WordFix(heard: "word0", meant: "y"), to: full)?.count, YourWords.maxFixes)
        return out
    }
}
#endif
