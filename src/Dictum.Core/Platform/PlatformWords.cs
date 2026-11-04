namespace Dictum.Platform;

/// <summary>
/// The words in Dictum's approved copy that name the computer or its operating system (IPlatform.Words). The owner's rule
/// (Mac copy block, APPROVED 23/10/2026 14:06 AEDT, docs/DECISIONS.md): where an approved Windows string says "this PC",
/// "Windows", "Start menu" or "Win+V", the Mac build shows the same sentence with the Mac word in that place; nothing else in
/// the sentence changes and the Windows build is untouched. The call sites compose their sentence from these words, so the
/// Windows values below are the approved Windows words byte for byte. Two answers cannot be made by swapping a word, so the
/// whole sentence is here; the "Remove everything" sentence's phrase is <see cref="IRemovalSteps.StartupItemsPhrase"/>.
/// </summary>
/// <param name="ThisDevice">"this PC" / "this Mac": Welcome steps 1 and 2, the menu-bar panel footer, Settings > Privacy's default line, the calibration intro (rows 3, 4, 8, 9, 11).</param>
/// <param name="OsName">"Windows" / "macOS": the hotkey step's intro and the capture box's "… received:" line (rows 5, 6).</param>
/// <param name="ClipboardHistoryCaption">Settings > Privacy, the caption under "Keep dictations out of clipboard history" (row 10).</param>
/// <param name="RemoveAnswer">Settings > Help, "How do I remove Dictum completely?", the third paragraph (row 12).</param>
/// <param name="ShowsRemoteDesktopNote">Whether the Welcome hotkey step shows the "On a Mac over Remote Desktop…" caption, which describes reaching a Windows PC from a Mac (row 7).</param>
/// <param name="WaitingForKeyEnding">The end of Settings > Clean-up's "On, but waiting for your {VARIABLE}: nothing is sent until you …" line (<c>CloudPolicy.WaitingForKeyLine(string, PlatformWords)</c>), where this platform can add a key in Dictum. Null where it cannot yet (the Mac until K-Mac): the line is then the approved 16/10/2026 one, "…until you add it to .env."</param>
/// <param name="HelpAddKeyParagraph">Settings > Help, "How do I turn on cloud clean-up?": the paragraph directly above "To add a key for cloud clean-up…" (approve-api-keys.md row 40). Null where the platform has no credential store yet: the paragraph is then not shown.</param>
/// <param name="HelpKeySources">The same answer's last paragraph (row 44). Null: the approved 22/10/2026 paragraph, "Dictum reads your key only from the .env file (or from a Windows environment variable). …", stays.</param>
/// <param name="HelpKeyCheck">The same answer's "When Dictum has found your key…" paragraph, its last sentence (row 45). Null: the approved 22/10/2026 sentence, "Dictum cannot check that the key itself is right: …", stays.</param>
public sealed record PlatformWords(string ThisDevice, string OsName, string ClipboardHistoryCaption, string RemoveAnswer, bool ShowsRemoteDesktopNote,
    string? WaitingForKeyEnding = null, string? HelpAddKeyParagraph = null, string? HelpKeySources = null, string? HelpKeyCheck = null)
{
    /// <summary>The approved Windows words, unchanged (each was approved where it was written before this seam existed).</summary>
    public static readonly PlatformWords Windows = new(
        ThisDevice: "this PC",
        OsName: "Windows",
        ClipboardHistoryCaption: "Windows clipboard history (Win+V) and cloud clipboard sync.",
        // APPROVED 22/10/2026 (proposed 21/10/2026, Help): do not reword.
        RemoveAnswer: "It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys, its Start menu shortcut and its start-with-Windows entry, then closes. " +
            "If you got Dictum from the Microsoft Store, Windows Settings then opens so you can uninstall the app itself; otherwise Dictum removes the program too, unless its first warning says it cannot.",
        ShowsRemoteDesktopNote: true,
        // APPROVED by the owner 04/11/2026 17:14 AEDT (the waiting line; Windows only): do not reword.
        WaitingForKeyEnding: "add the key in Settings > Clean-up or in .env.",
        // APPROVED by the owner 04/11/2026 16:51 AEDT (Help rows 40, 44 and 45; Windows only): do not reword.
        HelpAddKeyParagraph: "The easiest way to add a key: choose the service in Settings > Clean-up, press Add a key…, paste the key, and press Save. " +
            "Dictum keeps it in Windows Credential Manager, never in a file, and uses it from the next dictation. " +
            "You can add more than one key for a service, such as a work one and a personal one, and choose which one Dictum uses.",
        HelpKeySources: "Dictum reads your key from the keys you add in Dictum (kept in Windows Credential Manager), the .env file or a Windows environment variable. It never saves the key in its settings, and never shows it on screen or writes it in its log.",
        HelpKeyCheck: "To check the key itself, press Check this key beside it in Settings > Clean-up once cloud clean-up is on. If your dictations come back only lightly tidied, check the key.");

    /// <summary>The Mac words, each APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block): do not reword.</summary>
    public static readonly PlatformWords Mac = new(
        // APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block, rows 3, 4, 8, 9 and 11): do not reword.
        ThisDevice: "this Mac",
        // APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block, rows 5 and 6): do not reword.
        OsName: "macOS",
        // APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block, row 10): do not reword.
        ClipboardHistoryCaption: "Clipboard managers that follow the concealed-item convention, and Universal Clipboard to your other devices.",
        // APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block, row 12): do not reword.
        RemoveAnswer: "It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys and its login item, then closes and moves Dictum.app to the Trash, unless its first warning says it cannot.",
        // APPROVED by the owner 23/10/2026 14:06 AEDT (Mac copy block, row 7): not shown on a Mac.
        ShowsRemoteDesktopNote: false);
}
