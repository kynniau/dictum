namespace Dictum.Help;

/// <summary>
/// "Dictum in plain words" (docs/DECISIONS.md, "Dictum in plain words"): the short version of the site page /plain-words, shown on
/// Settings > About (card "In plain words") and under the Welcome window's first paragraph. APPROVED by the owner 22/10/2026 17:36 AEDT,
/// section 2b of the plain-words statement, word for word: do not reword. Plain constants, so tests can pin them.
/// No acceptance gate goes with it (section 2c): the two cloud consents are the only places data leaves the PC, and both gate it.
/// </summary>
public static class PlainWordsText
{
    /// <summary>Settings > About card title.</summary>
    public const string CardTitle = "In plain words";

    public const string Free =
        "Dictum is free. Free: no subscription, no account, no ads, no analytics, no telemetry. No spyware: Dictum sends nothing to Kynni.";

    public const string OpenSource =
        "Dictum is open source under the MIT licence. It is provided as is, without warranty. We built it with care, but we cannot promise it will always hear you right.";

    public const string Cloud =
        "Nothing you say leaves this PC unless you switch on a cloud option and agree to it. If you do, you take responsibility for what is sent to that service, and its own terms apply.";

    public const string ReadAll = "Read the whole statement at dictum.kynni.au/plain-words.";

    /// <summary>The About card's button.</summary>
    public const string OpenLabel = "Open in your browser";

    /// <summary>The page the button opens, and the page <see cref="ReadAll"/> names.</summary>
    public const string Url = "https://dictum.kynni.au/plain-words";

    /// <summary>The four paragraphs, in order.</summary>
    public static IReadOnlyList<string> Paragraphs { get; } = [Free, OpenSource, Cloud, ReadAll];
}
