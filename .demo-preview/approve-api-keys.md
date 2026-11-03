# API keys block: strings for approval (PROPOSED 30/10/2026 except row 15, APPROVED 03/11/2026; K-Core lane, branch feature/api-keys-core)

Brief: `.demo-preview/design-api-keys.md` (decisions D1 to D5 accepted 30/10/2026 18:57 AEDT). The Core lane adds no window
and no page. Every string below is new, marked PROPOSED in the code, and shows only in `--selftest`, the log, the Storage page's
removal list, or an exception the UI lane may show. None is approved; no approved string is edited. The UI lane (K-UI) adds its
own rows below these for the dialog, the key list and Check this key.

No row shows a key or any part of one (decision D2). A label is the user's own text; it is left out wherever it holds any eight
characters of the key.

| # | Surface | Proposed string | Where in the code |
|---|---|---|---|
| 1 | `--selftest`, the key's line, when a stored key is in use | `present (stored key "LABEL")` | `SecretStore.Describe` |
| 2 | The same, when the label is empty or holds part of the key | `present (stored key)` | `SecretStore.Describe` |
| 3 | The same, when two or more keys are stored for that service, none is chosen, and no environment variable or .env line has one (revised after the lens 30/10/2026: the count is row 4's alone, so the whole line reads `absent (none chosen); 2 stored keys`) | `absent (none chosen)` | `SecretStore.Describe` |
| 4 | The same, after the source, whenever keys are stored for that service (the only place the count appears) | `; 1 stored key` / `; N stored keys` | `SelfTestCommand.KeyLines` |
| 5 | `--selftest`, one line before the keys: where keys added in Dictum are kept | `key store` then `Windows Credential Manager`, or `none on this platform` (macOS until the K-Mac lane) | `SelfTestCommand.KeyStoreLine`, `CredentialManagerStore.Name` |
| 6 | Settings > Storage, "Remove Dictum completely" list (description of keys.json) | `names of your stored API keys (keys.json)` | `PathRegistry.Locations` |
| 7 | The same, for its write-in-progress file | `names being saved` | `PathRegistry.Locations` |
| 8 | "Remove Dictum completely", the "Done, except:" list, when the credential store refused | `the API keys stored in STORE (REASON)` (STORE is row 5's name; REASON names the call and the system's code, e.g. "CredDelete failed: Access is denied. (5)") | `AppShell.RemoveEverything` |
| 9 | Log and `--selftest` note, when the credential store cannot be read at load (keys from the environment and .env still work) | `Stored keys could not be read from STORE: REASON` | `SecretVault.Snapshot` |
| 10 | Log and `--selftest` warning: `keys.active` is not an object | `Setting "keys.active" must be an object; no key is chosen.` | `SettingsLoader.KeyChoices` |
| 11 | Log and `--selftest` warning: `keys.active` names a variable Dictum does not read (revised after the lens 30/10/2026: the name is not shown, in case it is a key) | `Setting under "keys.active" names a variable Dictum does not read (not shown); ignored.` | `SettingsLoader.KeyChoices` |
| 11a | Log and `--selftest` warning: the `keys` section has entries Dictum does not read (names not shown, for the same reason) | `Setting "keys" has 1 entry Dictum does not read (not shown); ignored.` / `… has N entries …` | `SettingsLoader.WarnUnknownUnnamed` |
| 12 | Log and `--selftest` warning: a value that is not an id, "env" or "file" (never shown, in case it is a key) | `Setting "keys.active.NAME" has an invalid value (not shown): expected a stored key's id, "env" or "file". No key is chosen for it.` | `SettingsLoader.KeyChoices` |
| 13 | Exception message the UI lane may show: keys.json exists but cannot be read, so it is not written over | `keys.json could not be read, so it was left as it is; fix or delete it, then try again.` | `KeysFile.Unreadable` |
| 14 | Exception messages the UI lane may show when Add is refused | `The label is empty.` / `The key is empty.` / `The key holds a line break or another control character.` / `The key is longer than 2560 bytes.` / `The label holds part of the key.` / `This platform has no credential store Dictum can use.` | `SecretVault.Add`, `NoCredentialStore.Write` |
| 14a | Exception message (shown as row 8's REASON) when Remove all keys could not remove every entry; the others are removed (added after the lens 30/10/2026) | `N of M stored keys could not be removed (system error CODES).` | `SecretVault.RemoveAll` |
| 15 | **APPROVED by the owner 03/11/2026 14:09 AEDT (confirmed 04/11/2026 06:39 AEDT).** site/privacy.html, "What Dictum stores" list (Windows settings folder), a new item directly after the `.env` item; added byte for byte. `StoredFilesPolicyTests.The_privacy_policy_names_every_file_Dictum_stores` needed it. | `<li><code>keys.json</code>: the names you give API keys you add in Dictum, the service each is for and the day you added it, never a key. The keys themselves are kept by your operating system's credential store (Windows Credential Manager), for this computer and your account only.</li>` | `PathRegistry.Locations` (keys.json, `DataCategory.Secrets`) |

## Two-model check, row 15 (standing order 02/11/2026)

- **Fable (orchestrator)** recommended keeping the sentence as written.
- **GPT 6.1 sol** (codex-lane, read-only, 03/11/2026 14:10 AEDT) disagreed on three points:
  - (a) "your operating system's credential store" could imply macOS support.
  - (b) The random id in keys.json is not named.
  - (c) The active-key choice in settings.json is not named.
- **Kyle decided "Keep as written"**, 04/11/2026 06:39 AEDT.

**Note for K-UI and K-Docs:** the privacy policy's Store paragraph says "deletes them when you uninstall Dictum". That sentence is about files. Credential Manager entries survive an MSIX uninstall, so a PROPOSED privacy line must say so, alongside the planned Help line.

## K-UI rows (PROPOSED 04/11/2026, branch feature/api-keys-ui)

Every row below is PROPOSED and marked so in the code. None is approved; no approved string is edited. No row shows a key or any part
of one (D2): a label is the user's own text, and the vault leaves it out when it holds any eight characters of the key. STORE is the
store's name (row 5: "Windows Credential Manager"); SERVICE is the service's name as the Service list shows it; LABEL is the key's label.

| # | Surface | Proposed string | Where in the code |
|---|---|---|---|
| 16 | Settings > Clean-up (Cloud clean-up card) and Settings > Models (Cloud speech card, Advanced view), under the service: the button | `Add a key…` | `KeysPanel.AddLabel` |
| 17 | The dialog's title and heading (the window title is `Dictum: Add a key`, as every Dictum dialog) | `Add a key` | `AddKeyWindow.HeadingText` |
| 18 | The dialog, the line under the heading | `For SERVICE. Paste the API key you created on its website. Dictum keeps it in STORE, never writes it to a file, and never shows it again.` | `AddKeyWindow.Intro` |
| 19 | The dialog, the Label field: caption (also its accessible name), then the hint under it; the field starts with SERVICE | `Label` / `A name for this key, such as Work or Personal. It is shown in the list; the key never is.` | `AddKeyWindow.LabelCaption`, `LabelHint` |
| 20 | The dialog, the Key field: caption, accessible name (a screen reader also announces it as a password field), placeholder | `Key` / `API key, hidden as you paste` / `Paste your key here` | `AddKeyWindow.KeyCaption`, `KeyName`, `KeyWatermark` |
| 21 | The dialog's buttons, and its error line for a refusal the vault does not name (row 14's six are shown byte for byte) | `Save` / `Cancel` / `The key was not saved.` | `AddKeyWindow.SaveLabel`, `CancelLabel`, `Save` |
| 22 | The dialog's link: opens https://dictum.kynni.au/help at the service's section. **Anchors for K-Docs to create:** `#key-anthropic`, `#key-openai`, `#key-gemini`, `#key-azure`, `#key-groq`, `#key-custom` (LLM_API_KEY and SPEECH_API_KEY both) | `How do I get a key?` | `AddKeyWindow.HowLabel`, `GuideAnchor` |
| 23 | In place of row 16 where there is no credential store Dictum can use (macOS until the K-Mac lane) | `Adding a key here needs a credential store Dictum can use, which this computer does not have yet. Put the key in the key file instead.` | `KeysPanel.NoStoreLine` |
| 24 | The key list, each key's second line: where it lives (the date as DD/MM/YYYY) | `Added here on 04/11/2026` (`Added here` when keys.json has no date) / `Key file (.env)` / `Environment variable` | `KeysPanel.Where` |
| 25 | The key list: the mark on the key in use, the button on the others, and the buttons' accessible names | `Active` / `Use this key` / `Use LABEL`, `Check LABEL`, `Remove LABEL` | `KeysPanel.ActiveLabel`, `UseLabel`, `UseName`, `CheckName`, `RemoveName` |
| 26 | The key list, a stored key's button, and its question (buttons `Remove`, danger, and `Cancel`, the default) | `Remove` / title `Remove this key?` / `Dictum removes the key "LABEL" from STORE. There is no undo: to use it again, add it again.` | `KeysPanel.RemoveLabel`, `RemoveTitle`, `RemoveText` |
| 27 | The page's status line after Save (the key just added is also made the one in use), after Save when settings.json could not record that, after Remove, and after Use this key | `Key "LABEL" added. Dictum uses it from the next dictation.` / `Key "LABEL" added, but it could not be made the one in use (REASON). Choose Use this key on it once settings.json can be saved.` / `Key removed.` / `Dictum now uses "LABEL" from the next dictation.` | `KeysPanel.Added`, `AddedNotChosen`, `Removed`, `NowUsing` |
| 28 | The key list, when the stored keys cannot be read (REASON is the store's call and code); the rest of the list and the page still work | `Your stored keys could not be read from STORE (REASON). Keys in the key file and environment variables still work.` | `KeysPanel.CouldNotRead` |
| 29 | The key list: two or more stored, none chosen, nothing else (none is sent); no key at all; a stored key whose label the vault left out | `Two or more keys are stored and none is chosen, so none is used: choose Use this key on one.` / `No key yet.` / `Unnamed key` | `KeysPanel.NoneChosen`, `NoKeyYet`, `Unnamed` |
| 30 | Check this key: the service accepted it | `The key works.` | `KeyCheck.Works` |
| 31 | Check this key: the service answered with an error (CODE and REASON are the HTTP status, MESSAGE the service's own text with every key-like token hidden, row 36; the "says" sentence only when there is a message) | `The key did not work: HOST answered CODE REASON. HOST says: MESSAGE` | `KeyCheck.Refused` |
| 32 | Check this key, offline only on (nothing sent) | `Offline only is on, so the key was not checked.` | `KeyCheck.OfflineRefused` |
| 33 | Check this key, a plain http:// address off this PC (nothing sent; the button is not offered for one, this is the backstop) | `The key was not checked: HOST uses http://, which would send the key unencrypted.` | `KeyCheck.PlainHttpRefused` |
| 34 | Check this key, no answer within 15 seconds | `The key was not checked: HOST did not answer within 15 seconds.` | `KeyCheck.NoAnswer` |
| 35 | Check this key, the request could not be made (REASON as .NET gives it, key-like tokens hidden) | `The key was not checked: HOST could not be reached (REASON).` | `KeyCheck.NotReached` |
| 36 | What stands in for a hidden token in row 31's MESSAGE | `[hidden]` | `KeyCheck.Hidden` |
| 37 | The key list: the button (only once the consent step for that host is on record), the line while it runs, and the shell's two backstops | `Check this key` / `Checking "LABEL"…` / `Switch the service on first: the key is checked only with a service you agreed to in its consent step.` / `That key is no longer there.` | `KeysPanel.CheckLabel`, `Checking`; `AppShell.CheckNeedsConsent`, `KeyGone` |
| 38 | Settings > Privacy, a new last card (shown only where there is a credential store): title, line, button; its question (buttons `Remove all keys`, danger, and `Cancel`, the default); its results (the refusal reuses "Remove Dictum completely"'s `Done, except:` and row 8) | `API keys` / `Keys you add in Dictum are kept in STORE for your account on this computer. They stay there until you remove them here, also if you uninstall Dictum.` / `Remove all keys…` / title `Remove all keys?` / `Dictum removes every API key you added in Dictum from STORE, and their names. Keys in the key file (.env) and in environment variables are not touched. There is no undo: to use a key again, add it again.` / `There were no stored keys to remove.` / `1 stored key removed.` / `N stored keys removed.` / `Done, except: the API keys stored in STORE (REASON)` | `PrivacyPage.KeysTitle`, `KeysLine`, `RemoveAllLabel`; `AppShell.RemoveAllTitle`, `RemoveAllText`, `RemoveAllButton`, `RemovedKeys`, `RemoveAllKeys` |
| 39 | Log lines (never an id, a label, a value or the service's message) | `Key choice saved for VARIABLE.` / `Check this key: one request for VARIABLE to HOST.` / `Check this key: HOST accepted the key.` / `Check this key: HOST did not accept it, or was not reached.` / `Remove all keys: N removed.` | `AppShell.ChooseKey`, `CheckKeyAsync`, `RemoveAllKeys` |
| 40 | In-app Help, "How do I turn on cloud clean-up?": a new paragraph directly above the APPROVED "To add a key for cloud clean-up, choose the service… press Create the key file…." paragraph. **Not shown yet**: it sits beside the approved list as its own constant, and goes in (with the site's help page given the same words, K-Docs) only on approval | `The easiest way to add a key: choose the service in Settings > Clean-up, press Add a key…, paste the key, and press Save. Dictum keeps it in Windows Credential Manager, never in a file, and uses it from the next dictation. You can add more than one key for a service, such as a work one and a personal one, and choose which one Dictum uses.` | `HelpText.ProposedPasteBoxParagraph` |
| 41 | In-app Help, the same answer, after row 40. **Not shown yet**, as row 40. Inferred, not verified on a packaged install (docs/TESTING-MSIX.md step 4 is the check before approval) | `Uninstalling Dictum does not remove the keys you added in Dictum: they stay in Windows Credential Manager until you remove them. To remove them, use Remove all keys in Settings > Privacy before you uninstall.` | `HelpText.ProposedUninstallLine` |
| 42 | **Privacy policy, for the owner: not edited by this lane** (site/privacy.html and in-app Privacy are the owner's). A sentence to follow the Store paragraph's "…and deletes them when you uninstall Dictum." (privacy.html line 110 at 5689b9a), same inference and check as row 41 | `Uninstalling does not remove API keys you added in Dictum: Windows keeps them in Credential Manager, for your account on this computer, until you remove them with Remove all keys in Settings > Privacy (or in Windows' own Credential Manager).` | none yet (K-Docs, on approval) |
| 43 | Shipped `.env` template: "Create the key file…" now writes the APPROVED header (byte for byte), a blank line, then `.env.example` in full with `your-key-here` after each empty key line. No new words, but `.env.example`'s first paragraph ("Copy to %APPDATA%\Dictum\.env (preferred) or next to Dictum.exe and fill in.") now reaches users inside a file that is already in place, and names a Windows path on a Mac too. For the owner and K-Docs (who own `.env.example`) to decide whether to reword it | (the whole of `.env.example`) | `KeyFile.Contents` |

### Approved sentences this block makes untrue once it ships (for the owner; none is edited)

| Where | APPROVED sentence | Why it is no longer true | PROPOSED successor (row) |
|---|---|---|---|
| In-app Help, cloud answer, last paragraph (HelpText.CloudAnswer, approved 22/10/2026) | "Dictum reads your key only from the .env file (or from a Windows environment variable)." | Add a key… stores keys in Credential Manager. | 44: `Dictum reads your key from the keys you add in Dictum (kept in Windows Credential Manager), the .env file or a Windows environment variable. It never saves the key in its settings, and never shows it on screen or writes it in its log.` |
| In-app Help, cloud answer, the "When Dictum has found your key…" paragraph | "Dictum cannot check that the key itself is right: if your dictations come back only lightly tidied, check the key." | Check this key checks it (D3, as the brief foresaw). | 45: `To check the key itself, press Check this key beside it in Settings > Clean-up once cloud clean-up is on. If your dictations come back only lightly tidied, check the key.` |
| The cards' key lines (Clean-up `ShowAddressHints`, approved 16/10/2026; speech `CloudSpeechKeyLine`, approved 22/10/2026) and `CloudPolicy.WaitingForKeyLine` ("…until you add it to .env.") | "The key goes in .env in the settings folder: …" / "…nothing is sent until you add it to .env." | Still true as one way; no longer the only one. Shown unchanged; the key list now sits under them. | none proposed: the owner decides whether they change |
