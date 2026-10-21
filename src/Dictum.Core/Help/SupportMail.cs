using System.Text;

namespace Dictum.Help;

/// <summary>
/// Every email Dictum offers to write: the one support address, the one mailto builder, and the feedback email (lane F, 21/10/2026).
/// <para>
/// Dictum sends nothing itself. A button hands the string built here to the system (<c>IPlatform.Open</c>), the user's own
/// mail program opens a new message, and the user reads it, edits it and sends it, or does not. There is no HTTP call, no form
/// and no clipboard use in this file.
/// </para>
/// Switching the support address to dictum@kynni.com.au later is one edit, <see cref="Address"/>, plus the site's pages
/// (site/index.html and site/privacy.html, which have their own copy) and the non-code copies listed in docs/DECISIONS.md.
/// </summary>
public static class SupportMail
{
    /// <summary>The support address: the only copy in code. AppShell.SupportEmail is an alias of it and HelpText names it directly.</summary>
    public const string Address = "hello@kynni.com.au";

    /// <summary>PROPOSED copy (21/10/2026), awaiting the product owner's review: do not reword.</summary>
    public const string FeedbackSubject = "Dictum feedback";

    /// <summary>PROPOSED copy (21/10/2026), awaiting the product owner's review: do not reword. Line breaks are CRLF, as RFC 6068 asks of a mailto body.</summary>
    public const string FeedbackIntro = "What I'd like to say:\r\n\r\n\r\n(You can delete the lines below. They help us understand your setup.)";

    /// <summary>
    /// The common ceiling for a mailto link: some mail programs and Windows' own link handling refuse a longer one. Pinned by a test.
    /// </summary>
    public const int MaxUriLength = 2000;

    /// <summary>The longest any one fact of the setup may be (a model file named by the user, say), so a fact cannot crowd out the message.</summary>
    private const int MaxFactLength = 60;

    /// <summary>
    /// "mailto:&lt;support address&gt;?subject=…&amp;body=…" with both parts percent-encoded as UTF-8 (so "&amp;", "é" and line
    /// breaks survive). A line break in <paramref name="body"/> is encoded as it is written: write CRLF for %0D%0A.
    /// </summary>
    public static string Uri(string subject, string body) =>
        $"mailto:{Address}?subject={System.Uri.EscapeDataString(subject)}&body={System.Uri.EscapeDataString(body)}";

    /// <summary>
    /// The feedback email. Only the five facts in <paramref name="setup"/> are included: nothing is read from the disk, the
    /// settings, the dictionary, the history or the microphone here. Each fact is cleaned (control characters out, a path cut
    /// to its last part, clipped) so a stray path or line break in a name cannot leak a folder or add lines. If the whole link
    /// would pass <see cref="MaxUriLength"/>, the setup lines are left off and the user's own space and the intro are kept.
    /// </summary>
    public static string FeedbackUri(FeedbackSetup setup)
    {
        var body = FeedbackIntro + "\r\n" + string.Join("\r\n",
            $"Dictum {Fact(setup.Version)}",
            Fact(setup.System),
            $"Speech model: {Fact(setup.SpeechModel)} on {Fact(setup.SpeechRuntime)}",
            $"Clean-up: {Fact(setup.Cleanup)}");
        var uri = Uri(FeedbackSubject, body);
        return uri.Length <= MaxUriLength ? uri : Uri(FeedbackSubject, FeedbackIntro);
    }

    /// <summary>
    /// A line of facts joined with " · " (the models line of the header): each part cleaned as a <see cref="Fact"/>, so a path or
    /// line break in a model's name cannot reach the email.
    /// </summary>
    public static string Facts(string? line) => string.Join(" · ", (line ?? "").Split(" · ").Select(Fact));

    /// <summary>A fact as it may appear in the email: no control characters, only the last part of anything path-like, at most 60 characters.</summary>
    internal static string Fact(string? value)
    {
        var text = new StringBuilder();
        foreach (var c in value ?? "")
            text.Append(char.IsControl(c) ? ' ' : c);
        var clean = text.ToString().Trim();
        var cut = clean.LastIndexOfAny(['\\', '/']);
        if (cut >= 0) clean = clean[(cut + 1)..];
        if (clean.Length <= MaxFactLength) return clean;
        var keep = char.IsHighSurrogate(clean[MaxFactLength - 2]) ? MaxFactLength - 2 : MaxFactLength - 1; // never split a pair: a lone half cannot be encoded
        return clean[..keep] + "…";
    }
}

/// <summary>
/// What the feedback email says about the setup, as About already shows it: the Dictum version, the system ("Windows 10.0.x"),
/// the speech model and where it runs ("Vulkan", "CPU"), and the clean-up in use. Nothing else exists to be included.
/// </summary>
public sealed record FeedbackSetup(string Version, string System, string SpeechModel, string SpeechRuntime, string Cleanup);
