# Two-model check: API keys privacy and disclosure wording (04/11/2026)

Standing order 02/11/2026: disclosure wording is decided on Fable, challenged by GPT 6.1 sol, both recorded, every
disagreement surfaced to the owner. Neither model approves anything; the owner signs the wording.

- Fable 5.1 decision, 13:06 AEDT, read-only against main cfcbb92, every claim tied to file:line.
- GPT 6.1 sol challenge, 13:12 AEDT, via codex-lane.sh (read-only ask). Only the public wording and a plain-words
  fact list went out; no code, no secrets. GPT did not see the code.

Rows refer to `.demo-preview/approve-api-keys.md`. Nothing here is approved yet.

## Where they agree

| Item | Both say |
|---|---|
| Row 20 (key field), row 38 (Privacy card) | Accurate as written. Show on approval. |
| Row 46 heading "Check this key (only when you press it)" | Keep. It is a different promise from "(off by default)": it names the trigger for every check, and it is accurate. |
| Row 46 detail "to list its models" | Keep. It says why the app makes contact, and it stays true if a custom server answers 404. |
| Row 46 substance | One request, to the consented host only, carrying the key and nothing dictated or written, answer shown with key-like text hidden, refused by Offline only. |

## Where they disagree (owner decides)

| # | Question | Fable | GPT 6.1 sol |
|---|---|---|---|
| D1 | Uninstall wording (rows 38a, 41, 42): when to show it | Hold all three until the Store (MSIX) uninstall test in docs/TESTING-MSIX.md §4 runs, then ship one sentence covering both Windows builds. Splitting the builds in the policy is worse copy. | Ship now, limited to the verified direct-download behaviour, and say plainly: "We have not yet verified whether uninstalling the Microsoft Store version removes saved keys." Do not hold a useful disclosure for a test. |
| D2 | "until you remove them" | Keep. | Drop. It promises how long Windows keeps credentials. |
| D3 | "On Windows" scoping | Not added. Fable's text names Windows Credential Manager. | Add "On Windows". The policy also covers macOS, where keys cannot yet be added. |
| D4 | The "Unnamed key" after-reinstall effect | In the privacy policy (row 42). | In Help only. The policy discloses that keys stay and that names are stored separately. The label is a troubleshooting detail. |
| D5 | Row 46 destination phrase | "It goes to the same address your dictations go to, and nowhere else." | "This check contacts only that host." Narrower, and adds "HTTPS". |

## Where one model is wrong on the facts

- **Row 44 (Help: where Dictum reads your key).** GPT replaced it with a version that drops `.env` and environment
  variables, because the fact list did not state they still work. They do: Fable verified the resolution order in
  `src/Dictum.Core/Settings/EnvFile.cs` (`SecretStore.Resolve`) on main. **Fable's row 44 stands as written.** GPT's
  wording point (say "Dictum never displays the key values") is a style choice, not a correction.

## Texts, side by side

### Row 46, privacy policy §4 (blocks the 1.4 release)

Fable:
> `<h3>Check this key (only when you press it)</h3>`
> Only if you have switched on cloud clean-up or cloud speech, agreed to the service in its consent step, and press
> Check this key beside a key in Settings. Dictum then sends one request to that service, or to the address you entered,
> asking it to list its models. The request carries your key and nothing you dictated or wrote. It goes to the same
> address your dictations go to, and nowhere else. The answer is shown to you with anything that looks like a key hidden.
> Offline only refuses it.

GPT 6.1 sol:
> `<h3>Check this key (only when you press it)</h3>`
> Only if you have switched on cloud clean-up or cloud speech, completed the consent step for that host, and press Check
> this key beside a key in Settings. Dictum sends one HTTPS request to that host — the service or custom server address
> you entered — to request a list of available models. The request includes your API key, but no dictation or written
> text. This check contacts only that host. Dictum displays the response with text that looks like an API key hidden.
> Offline-only mode blocks this check.

### Row 42, privacy policy uninstall paragraph

Fable (held for the Store test):
> Uninstalling Dictum, from the Store or the direct download, does not remove the API keys you added in Dictum: Windows
> keeps them in Credential Manager, for your account on this computer, until you remove them. To remove them, use Remove
> all keys in Settings > Privacy or Remove Dictum completely in Settings > Storage before you uninstall, or remove them in
> Windows' own Credential Manager. Their names (keys.json) are files like the others, so if those are deleted and you
> install Dictum again, the keys show as Unnamed key.

GPT 6.1 sol (ship now):
> On Windows, uninstalling the direct-download version of Dictum leaves the API keys you saved in Dictum in Windows
> Credential Manager, under your Windows account on this computer. Before uninstalling, you can delete those saved keys
> using Remove all keys in Settings > Privacy or Remove Dictum completely in Settings > Storage. You can also delete them
> through Windows Credential Manager. Their labels and dates are stored separately in keys.json, which contains no API
> keys. We have not yet verified whether uninstalling the Microsoft Store version removes saved keys.

### Row 41, in-app Help

Fable (held):
> Uninstalling Dictum does not remove the keys you added in Dictum: they stay in Windows Credential Manager until you
> remove them. Before you uninstall, use Remove all keys in Settings > Privacy, or Remove Dictum completely in
> Settings > Storage, which removes them too.

GPT 6.1 sol (ship now):
> On Windows, uninstalling the direct-download version of Dictum leaves your saved API keys in Windows Credential Manager.
> To delete them before uninstalling, use Remove all keys in Settings > Privacy or Remove Dictum completely in
> Settings > Storage. You can also delete them through Windows Credential Manager after uninstalling.
> If keys.json, the file containing key labels and dates, is deleted, any retained keys appear as 'Unnamed key' when you
> reinstall Dictum.
> We have not yet verified whether uninstalling the Microsoft Store version removes saved keys.

### Row 38a, Settings > Privacy card

Fable (held):
> They stay there if you uninstall Dictum. Remove all keys below removes them, and so does Remove Dictum completely in
> Settings > Storage.

GPT 6.1 sol (ship now):
> On Windows, uninstalling the direct-download version leaves your saved API keys in Windows Credential Manager. Remove
> all keys below deletes them, as does Remove Dictum completely in Settings > Storage.

## Also found by Fable (approved copy made incomplete, not untrue; for the owner)

1. privacy.html §10 paragraph 1 ("That deletes everything Dictum has stored: …"): Remove Dictum completely now also
   removes the API keys. Proposed addition: "the API keys you added in Dictum".
2. privacy.html §10 paragraph 2 (uninstall by hand): the keys are not in those folders. Proposed pointer: "The API keys
   you added in Dictum are not in those folders: see section 3." Same hold as row 42.
3. privacy.html short version, bullet 3: Check this key sends a key, not text or voice. Possible clause: "…or to check an
   API key you added."

## Orchestrator's note (Opus 5.5, not a third opinion on wording)

On D1 the orchestrator recommends holding for the Store test and shipping one verified sentence, because 1.4 is not
imminent. If the Store test cannot run before 1.4, fall back to GPT's direct-download-only text. The owner decides.
Raw outputs: `keys-wording\fable-verdict.md` and `gpt-keys\run.last.md` in this session's scratchpad.

## Owner decisions

- **D1, 04/11/2026 15:23 AEDT, Kyle: hold for the Store test.** Rows 38a, 41 and 42, and privacy.html §10 paragraph 2's
  pointer, wait until the MSIX uninstall check in docs/TESTING-MSIX.md §4 runs; then one sentence covers both Windows
  builds. Fallback if that test cannot run before 1.4: GPT's direct-download-only text. D2, D3 and D4 apply only to the
  held text, so they are decided when that text is final, with the test result in hand.
- **Fact check on D5 (orchestrator, 15:23 AEDT, main cfcbb92):** GPT's "one HTTPS request" is not always true.
  `src/Dictum.Core/Settings/KeyCheck.cs:165` allows plain http to a loopback address (a custom server on this PC), and
  refuses plain http only off this PC.
- **D5, 04/11/2026 16:34 AEDT, Kyle: Fable's phrase.** Row 46 keeps "It goes to the same address your dictations go
  to, and nowhere else." GPT's "HTTPS" and "contacts only that host" are not used.
- **Found while checking row 46 (orchestrator, 16:34 AEDT):** Fable's last sentence, "Offline only refuses it.", is
  false for a server on this PC. `KeyCheck.cs:167` refuses the check under Offline only only when the address is not
  loopback, and privacy.html line 159 already says a server on your own computer still works under Offline only.
  Sent back through the two-model step before row 46 goes to the owner.
- **Correction (orchestrator, 16:36 AEDT):** GPT's "HTTPS" came from the orchestrator's own fact list, which said "one
  HTTPS GET". That fact list was wrong for a server on this PC. GPT was told so in the follow-up ask.
- **GPT 6.1 sol on the Offline only sentence, 16:36 AEDT:** "Offline only refuses this request unless the server is
  running on your own computer." Reason given: keeps the local-server exception and matches the approved Offline only
  section. Fable's decision pending.
- **Fable 5.1 on the Offline only sentence, 16:38 AEDT:** "Offline only refuses it unless that address is on your own
  computer." Not dropped, because every neighbouring §4 entry names Offline only. Verified KeyCheck.cs:167 and
  OfflineGuardHandler.cs:20 (refuse only non-loopback); no other factual error in the entry. Side note: the code comment
  at KeyCheck.cs:35 carries the same error. **The models agree in substance**; Fable's wording keeps the entry's own
  "address" and "it". Owner to approve.
- **Row 46 APPROVED by Kyle, 04/11/2026 16:49 AEDT**, exactly this text, for site/privacy.html §4 (and the in-app
  Privacy copy of the policy). K-Docs places it; nobody rewords it.

  > `<h3>Check this key (only when you press it)</h3>`
  > Only if you have switched on cloud clean-up or cloud speech, agreed to the service in its consent step, and press
  > Check this key beside a key in Settings. Dictum then sends one request to that service, or to the address you entered,
  > asking it to list its models. The request carries your key and nothing you dictated or wrote. It goes to the same
  > address your dictations go to, and nowhere else. The answer is shown to you with anything that looks like a key hidden.
  > Offline only refuses it unless that address is on your own computer.
- **Rows 16–39 and 30a APPROVED by Kyle, 04/11/2026 16:50 AEDT**, as written in `.demo-preview/approve-api-keys.md` on
  main at cfcbb92 (the on-screen and log wording: dialog, key list, Check this key results, Privacy card row 38, log
  lines). Rows 38a, 41, 42 held for the Store test; rows 40, 43, 44, 45 and the three Help sentences still open.
- **Help rows 40, 44 and 45 APPROVED by Kyle, 04/11/2026 16:51 AEDT, Windows only**, as written in approve-api-keys.md
  at cfcbb92. K-Docs routes them through the platform words so Windows Help shows them and Mac Help stays as it is until
  the Mac keychain lane (K-Mac) lands.
- **Row 43, 04/11/2026 16:52 AEDT, Kyle: drop the intro.** The key file Dictum writes leaves out .env.example's first four
  lines (the "Copy to %APPDATA%…" paragraph); it starts with the approved Header, a blank line, then .env.example from the
  "# Syntax" line on. The repository's .env.example is unchanged. No new words.
- **PROPOSED (orchestrator, 04/11/2026 16:52 AEDT), the third approved line made incomplete.** Windows only; the Mac keeps
  the approved line until K-Mac. Shown on the Clean-up card, the Privacy summary, and quoted by Help (HelpText.cs:153 calls
  the same function, so it follows). Approved today, kept: "On, but waiting for your {VARIABLE}: nothing is sent until you
  add it to .env." PROPOSED beside it: "On, but waiting for your {VARIABLE}: nothing is sent until you add the key in
  Settings > Clean-up or in .env." The cards' "The key goes in .env in the settings folder: …" lines stay unchanged: still
  true, and the key list with Add a key… now sits directly under them.
- **Waiting line APPROVED by Kyle, 04/11/2026 17:14 AEDT, Windows only:** "On, but waiting for your {VARIABLE}: nothing is
  sent until you add the key in Settings > Clean-up or in .env." The Mac keeps the approved 16/10 line until K-Mac.

## Privacy §10 paragraph 1 and short-version bullet 3 (two-model step, 04/11/2026 17:15 AEDT)

- **Fable 5.1 (decides):**
  - §10 list: "models, recordings, logs, settings, consent records, dictionary, corrections, snippets, <code>.env</code> and
    the API keys you added in Dictum". Verified AppShell.Actions.cs:215 (prompt says "and keys") and :226-235
    (Vault.RemoveAll before the folder plan).
  - Bullet 3, new last sentence: "If you press Check this key beside a key you added, it sends that key, and nothing you
    dictated or wrote, to the same address your dictations go to." Offline only is not in the short version, so the §4
    qualifier is not repeated.
- **GPT 6.1 sol (challenges):**
  - §10 list: **Agree.** "Added in Dictum" limits the claim correctly (no promise about environment variables or revoking
    with the provider).
  - Bullet 3: **Change.** "The same address your dictations go to" can suggest dictations were already sent, and the
    consent condition is implicit. Its text: "If you press Check this key beside a key you added, with that service switched
    on and your consent given, Dictum sends one request containing the key, but no dictated or written content, to the
    address set for that service."
  - Also questioned the approved opening "goes online only when you ask it to"; GPT was not given §4's full list of
    connections, so this rests on missing facts and is out of scope (approved legal copy, not under change).
- **Disagreement for the owner:** bullet 3's last sentence, Fable's or GPT's. GPT's destination objection is the one Kyle
  already decided for §4 (D5, Fable's phrase, 16:34).
- **Bullet 3, 04/11/2026 17:28 AEDT, Kyle: Fable's sentence.** New last sentence of short-version bullet 3: "If you press
  Check this key beside a key you added, it sends that key, and nothing you dictated or wrote, to the same address your
  dictations go to." The rest of the bullet is unchanged.
- **§10 paragraph 1 list APPROVED by Kyle, 04/11/2026 17:37 AEDT:** "models, recordings, logs, settings, consent records,
  dictionary, corrections, snippets, <code>.env</code> and the API keys you added in Dictum".

## Hand-off to K-Docs (04/11/2026 17:37 AEDT)

All wording for 1.4 API keys is decided except the held uninstall items (rows 38a, 41, 42 and the §10 paragraph 2
pointer, with D2–D4), which wait for the Store uninstall check in docs/TESTING-MSIX.md §4.

- **Lens finding 1, website key-file download: DROPPED for 1.4.** Kyle, 04/11/2026 20:42 AEDT ("Drop the download"). The download's header was false for the site copy. The K-Docs lane deleted site/dictum-keys.env, its .gitignore exception, its test and the help.html §9 download sentence (b0a7869). "Create the key file…" in the app is unchanged. Opus re-lens on b0a7869: Approve. PR 88 merged 04/11/2026 21:10 AEDT as 2bd53c1.
