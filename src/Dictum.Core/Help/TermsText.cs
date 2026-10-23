namespace Dictum.Help;

/// <summary>
/// Dictum's Terms of Use in the app (docs/DECISIONS.md, "Legal block, app side", 23/10/2026): the version in force, the page,
/// and the words beside Continue on the Welcome window's first step. <see cref="Version"/> is the one place the version lives:
/// settings.json records it when Continue is pressed (terms.accepted, with the account in terms.accepted_by), and
/// <see cref="App.AppRules.AsksTerms"/> shows the step again at every start until the record matches it and this account.
/// Plain constants, so tests can pin them.
/// </summary>
public static class TermsText
{
    /// <summary>
    /// The version of the Terms of Use in force (Terms, heading: "Version 1"). Raise it only with a new version of the Terms:
    /// every copy then asks again before it is used (Terms section 12).
    /// </summary>
    public const string Version = "1";

    /// <summary>The page the Welcome window and Settings > About open.</summary>
    public const string Url = "https://dictum.kynni.au/terms";

    /// <summary>
    /// APPROVED by the owner 23/10/2026 09:50 AEDT (legal block): the first step's primary button, the Continue the sentence
    /// below names (section C, O14; section D, item 2).
    /// </summary>
    public const string ContinueLabel = "Continue";

    /// <summary>
    /// APPROVED by the owner 23/10/2026 09:50 AEDT (legal block, O14): beside Continue. <see cref="AcceptanceLink"/> is the link
    /// to <see cref="Url"/>.
    /// </summary>
    public const string Acceptance = "By clicking Continue you agree to Dictum's Terms of Use.";

    /// <summary>The words of <see cref="Acceptance"/> that are the link.</summary>
    public const string AcceptanceLink = "Terms of Use";

    /// <summary>
    /// APPROVED by the owner 23/10/2026 09:50 AEDT (legal block, O14): below <see cref="Acceptance"/>. <see cref="PrivacyLink"/>
    /// is the link to the privacy policy.
    /// </summary>
    public const string PrivacyLine = "The Privacy Policy explains what Dictum keeps and sends.";

    /// <summary>The words of <see cref="PrivacyLine"/> that are the link.</summary>
    public const string PrivacyLink = "Privacy Policy";

    /// <summary>PROPOSED 23/10/2026 (section D, item 3): Settings > About's button beside "Privacy policy".</summary>
    public const string AboutTermsLabel = "Terms of use";

    /// <summary>PROPOSED 23/10/2026 (section D, item 3): Settings > About's button that opens <see cref="NoticesFile"/>.</summary>
    public const string NoticesLabel = "Third-party notices";

    /// <summary>The notices file every build ships (scripts\publish.ps1, scripts/publish-mac.sh).</summary>
    public const string NoticesFile = "THIRD-PARTY-NOTICES.txt";
}
