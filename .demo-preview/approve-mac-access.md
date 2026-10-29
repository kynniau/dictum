# Mac request-access block: strings for approval (APPROVED by the owner 27/10/2026 19:00 AEDT, rows 1-7 as written; branch feature/mac-access-request)

Owner's ask (27/10/2026): "we need to add option in app to check access and if access not give a request access key".
Every string below is new, marked APPROVED in the code, and shows only on a platform that has a grant to ask for (macOS).
No Windows string changes; no approved string is edited.

The device word in rows 1 and 5 is the approved Mac copy block word ("this Mac", row 3 of that block). The lines name no
Settings page, because macOS 15 and 26 call it "Accessibility" and macOS 27 "Device Control and Data Access" (your screenshot
of 27/10/2026); the button opens the page, whichever name it has.

| # | Surface | Proposed string | Where in the code |
|---|---|---|---|
| 1 | Settings > General, Hotkey card, and the menu-bar panel's problem line, while the grant is missing | "Dictum is not allowed to control this Mac yet, so it cannot see the hotkey or paste. Press Request access, then turn Dictum on in the System Settings page that opens." | `InputAccessText.Denied(words)` |
| 2 | Under row 1, the caption (the stale-entry loop you hit: seven rows, one switch shown on) | "If Dictum is already listed there and on but still refused, remove it from that list with the − button, then press Request access again." | `InputAccessText.StaleEntryNote` |
| 3 | The button (Settings > General, the Welcome hotkey step's box) and the link (menu-bar panel) | "Request access" | `InputAccessText.RequestLabel` |
| 4 | Row 1's place once the grant arrived and the hotkey listener installed | "Dictum can now see the hotkey." | `InputAccessText.Granted` |
| 5 | The hotkey capture box's line in place of "The system refused to report keys…" when the grant is what is missing | "Dictum cannot listen for keys until it is allowed to control this Mac." | `InputAccessText.BoxDenied(words)` |
| 6 | Row 1's place if the grant arrived but the listener still did not install (not seen on hardware; kept for the case) | "Access was granted, but the hotkey still could not be set up. Quit and reopen Dictum." | `InputAccessText.RestartFailed` |
| 7 | Log line (not on screen) when a tap cannot install; replaces the 22/10/2026 PROPOSED log line that said "quit and reopen Dictum" | "Event tap (…) not installed: Dictum is not allowed to control this Mac yet. Press Request access in Settings > General (or the menu-bar panel), or turn Dictum on in System Settings > Privacy & Security > Accessibility; the hotkey starts working as soon as it is on." | `EventTap.Run` |
| 8 | Log line (not on screen), APPROVED by the owner 29/10/2026 23:29 AEDT (round 2): a tap macOS keeps disabling is removed instead of turned back on | "Event tap (…) removed: macOS keeps disabling it (timeout), which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel)." | `EventTap.Disabled` |
| 9 | Log line (not on screen), APPROVED by the owner 29/10/2026 23:29 AEDT (round 2): a listener refused while the system says Dictum is allowed | "Input access: a hotkey listener was refused although the system reports Dictum as allowed; treating it as not allowed until it is granted again or Request access is pressed." | `InputAccessWatch.ListenerRefused` |
| 10 | Log line (not on screen), APPROVED by the owner 29/10/2026 23:29 AEDT (wording per the orchestrator 22:50: what was measured, no time claim): keys stopped getting through and macOS refused a fresh tap | "Event tap (…) removed: N key events reached macOS but not Dictum, and macOS refused a fresh tap, which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel)." (the first clause reads "N key events reached macOS but no app" when the keys were held after the tap, and "macOS disabled it (timeout) and N key events reached macOS but not Dictum" when macOS's disable notice tripped it) | `EventTap.AfterRecreate` |
| 11 | WITHDRAWN 29/10/2026 (its case now ends in row 10 or row 13) | | |
| 12 | WITHDRAWN 29/10/2026 (the probe failed on hardware) | | |
| 13 | Log line (not on screen), APPROVED by the owner 29/10/2026 23:29 AEDT: a false positive, the fresh tap allowed | "Event tap (…) re-created: N key events reached macOS but not Dictum (or: but no app), but macOS allowed a fresh tap, so another app or macOS took those keys and the hotkey carries on." | `EventTap.AfterRecreate` |
| 14 | Log line (not on screen), APPROVED by the owner 29/10/2026 23:29 AEDT (re-create round): a second trigger within 30 s of a fresh tap | "Event tap (…) removed: keys stopped reaching it again within 30 s of a fresh tap, which happens when Dictum is no longer allowed to control this Mac. It is not turned back on, so keys reach other apps; press Request access in Settings > General (or the menu-bar panel)." | `EventTap.Trigger` |

Not in this block: the Mac startup log's "speech on this PC" (`SpeechPolicy.DeclineButton`, approved; for the Mac copy block).

Rows 8, 9, 10, 13 and 14 APPROVED by the owner 29/10/2026 23:29 AEDT ("Approve all five"); 11 and 12 are withdrawn. The per-tick "Tap diag (…)" line exists only with DICTUM_TAP_DIAG=1 in the environment and is not a user-facing string. To approve: reply with the row numbers approved as is, and the new wording for any row changed.
