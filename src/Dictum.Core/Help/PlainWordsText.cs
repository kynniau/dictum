namespace Dictum.Help;

/// <summary>
/// "Dictum in plain words" (docs/DECISIONS.md, "Dictum in plain words"): the short version of the site page /plain-words, shown on
/// Settings > About (card "In plain words") and under the Welcome window's first paragraph. APPROVED by the owner 22/10/2026 17:36 AEDT,
/// section 2b of the plain-words statement, word for word: do not reword. Plain constants, so tests can pin them.
/// <see cref="OpenSource"/> and the first sentence of <see cref="Cloud"/> were changed by the owner's legal block (23/10/2026
/// 09:50 AEDT, section C rows O2 and O15). The Welcome window's first step now goes with the Terms of Use acceptance beside its
/// Continue (section C row O12, section D item 2; <see cref="TermsText"/>), which replaced section 2c's "no acceptance gate".
/// </summary>
public static class PlainWordsText
{
    /// <summary>Settings > About card title.</summary>
    public const string CardTitle = "In plain words";

    /// <summary>"sends nothing about you to Kynni": APPROVED 22/10/2026 20:45 AEDT (replaces the 17:36 sentence "sends nothing to Kynni").</summary>
    public const string Free =
        "Dictum is free. Free: no subscription, no account, no ads, no analytics, no telemetry. No spyware: Dictum sends nothing about you to Kynni.";

    /// <summary>APPROVED by the owner 23/10/2026 09:50 AEDT (legal block): do not reword. Section C row O2 dropped "It is provided as is, without warranty."</summary>
    public const string OpenSource =
        "Dictum is open source under the MIT licence. We built it with care, but we cannot promise it will always hear you right.";

    /// <summary>
    /// APPROVED by the owner 23/10/2026 09:50 AEDT (legal block): do not reword. Section C row O15 replaced the first sentence
    /// ("Nothing you say leaves this PC unless …"); the second is the 17:36 one, unchanged.
    /// </summary>
    public const string Cloud =
        "Dictum itself sends nothing you say off your computer unless you switch on a cloud option and agree to it. If you do, you take responsibility for what is sent to that service, and its own terms apply.";

    public const string ReadAll = "Read the whole statement at dictum.kynni.au/plain-words.";

    /// <summary>The About card's button.</summary>
    public const string OpenLabel = "Open in your browser";

    /// <summary>The page the button opens, and the page <see cref="ReadAll"/> names.</summary>
    public const string Url = "https://dictum.kynni.au/plain-words";

    /// <summary>The four paragraphs, in order.</summary>
    public static IReadOnlyList<string> Paragraphs { get; } = [Free, OpenSource, Cloud, ReadAll];
}
