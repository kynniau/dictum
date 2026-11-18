import Foundation

// Keeps the on-device model's suggested actions honest against the transcript it was given. Pure text rules, no model.
//
// The model is asked to copy, for every action, the words in which the person commits to it. An action stays only when
// part of that quote really is in the transcript, and a due time stays only when its words are in the transcript.
//
// simplification: a quote counts as found when a run of at least `minRun` consecutive words (or the whole quote, when
// shorter) appears in the transcript. Ceiling: this catches a made-up quote, not a real line that is a wish rather than
// a commitment; that part rests on the model's instructions. 18/11/2026, ONE solo test on the phone: six actions, all
// from wishes, each due "now". Trigger to retire: measured meetings showing wishes still coming through as actions.
enum SummaryGrounding {
    struct Suggested: Equatable {
        var quote: String
        var owner: String
        var text: String
        var byWhen: String
    }

    static let minRun = 4

    /// Lowercased words, punctuation and apostrophes dropped.
    static func words(_ text: String) -> [String] {
        text.lowercased().replacingOccurrences(of: "'", with: "").replacingOccurrences(of: "\u{2019}", with: "")
            .components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }
    }

    /// The longest run of consecutive words of `needle` that appears in `haystack`.
    static func longestRun(_ needle: [String], in haystack: [String]) -> Int {
        guard !needle.isEmpty, !haystack.isEmpty else { return 0 }
        var best = 0
        var previous = [Int](repeating: 0, count: haystack.count + 1)
        for n in needle {
            var row = [Int](repeating: 0, count: haystack.count + 1)
            for (j, h) in haystack.enumerated() where h == n {
                row[j + 1] = previous[j] + 1
                best = max(best, row[j + 1])
            }
            previous = row
        }
        return best
    }

    static func quoteFound(_ quote: String, transcript: [String]) -> Bool {
        let q = words(quote)
        return !q.isEmpty && longestRun(q, in: transcript) >= min(minRun, q.count)
    }

    /// The due time, or "" when its words are not in the transcript one after another.
    static func dueTime(_ byWhen: String, transcript: [String]) -> String {
        let w = words(byWhen)
        return !w.isEmpty && longestRun(w, in: transcript) == w.count ? byWhen : ""
    }

    /// The suggestions that stand: quote found in the transcript, due time blanked unless it was said.
    static func ground(_ suggested: [Suggested], transcript text: String) -> [Suggested] {
        let transcript = words(text)
        return suggested.filter { !$0.text.isEmpty && quoteFound($0.quote, transcript: transcript) }.map {
            var kept = $0
            kept.byWhen = dueTime($0.byWhen, transcript: transcript)
            return kept
        }
    }
}

#if DEBUG
/// One line per check, "PASS …" or "FAIL …". No device or model needed.
enum SummaryGroundingSelfCheck {
    static func run() -> [String] {
        var out: [String] = []
        func check<T: Equatable>(_ name: String, _ got: T, _ want: T) {
            out.append("\(got == want ? "PASS" : "FAIL") \(name): got \(got), want \(want)")
        }
        typealias S = SummaryGrounding.Suggested
        let transcript = "00:12 Other 3: I can pull those. I'll send you the regional numbers by Thursday.\n"
            + "00:24 Other 1: I wish the template was easier to use.\n00:34 Other 1: Anyway, I'll book the review meeting."
        let real = S(quote: "I'll send you the regional numbers by Thursday.", owner: "Other 3", text: "Send the numbers",
                     byWhen: "by Thursday")
        check("real quote and said due time stand", SummaryGrounding.ground([real], transcript: transcript), [real])
        check("due time never said is blanked",
              SummaryGrounding.ground([S(quote: "I'll book the review meeting", owner: "Other 1", text: "Book it", byWhen: "now")],
                                      transcript: transcript).map(\.byWhen), [""])
        check("due time said elsewhere but not together is blanked",
              SummaryGrounding.dueTime("Thursday meeting", transcript: SummaryGrounding.words(transcript)), "")
        check("made-up quote drops the action",
              SummaryGrounding.ground([S(quote: "I will email the client tomorrow", owner: "", text: "Email the client",
                                         byWhen: "")], transcript: transcript), [])
        check("empty quote drops the action",
              SummaryGrounding.ground([S(quote: "", owner: "", text: "Do something", byWhen: "")], transcript: transcript), [])
        check("quote with different punctuation and case stands",
              SummaryGrounding.quoteFound("i can pull those ill send you", transcript: SummaryGrounding.words(transcript)), true)
        check("short quote stands when every word is there",
              SummaryGrounding.quoteFound("Book the review", transcript: SummaryGrounding.words(transcript)), true)
        check("three shared words in a long quote are not enough",
              SummaryGrounding.quoteFound("the regional numbers will be ignored completely", transcript: SummaryGrounding.words(transcript)), false)
        check("longest run", SummaryGrounding.longestRun(["a", "b", "c", "x"], in: ["z", "a", "b", "c", "d"]), 3)
        return out
    }
}
#endif
