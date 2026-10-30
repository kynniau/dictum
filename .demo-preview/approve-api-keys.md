# API keys block: strings for approval (ALL PROPOSED 30/10/2026, K-Core lane, branch feature/api-keys-core)

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
| 3 | The same, when two or more keys are stored for that service, none is chosen, and no environment variable or .env line has one | `absent (N stored keys, none chosen)` | `SecretStore.Describe` |
| 4 | The same, after the source, whenever keys are stored for that service | `; 1 stored key` / `; N stored keys` | `SelfTestCommand.KeyLines` |
| 5 | `--selftest`, one line before the keys: where keys added in Dictum are kept | `key store` then `Windows Credential Manager`, or `none on this platform` (macOS until the K-Mac lane) | `SelfTestCommand.KeyStoreLine`, `CredentialManagerStore.Name` |
| 6 | Settings > Storage, "Remove Dictum completely" list (description of keys.json) | `names of your stored API keys (keys.json)` | `PathRegistry.Locations` |
| 7 | The same, for its write-in-progress file | `names being saved` | `PathRegistry.Locations` |
| 8 | "Remove Dictum completely", the "Done, except:" list, when the credential store refused | `the API keys stored in STORE (REASON)` (STORE is row 5's name; REASON names the call and the system's code, e.g. "CredDelete failed: Access is denied. (5)") | `AppShell.RemoveEverything` |
| 9 | Log and `--selftest` note, when the credential store cannot be read at load (keys from the environment and .env still work) | `Stored keys could not be read from STORE: REASON` | `SecretVault.Snapshot` |
| 10 | Log and `--selftest` warning: `keys.active` is not an object | `Setting "keys.active" must be an object; no key is chosen.` | `SettingsLoader.KeyChoices` |
| 11 | Log and `--selftest` warning: `keys.active` names a variable Dictum does not read | `Setting "keys.active.NAME" is not a key Dictum reads; ignored.` | `SettingsLoader.KeyChoices` |
| 12 | Log and `--selftest` warning: a value that is not an id, "env" or "file" (never shown, in case it is a key) | `Setting "keys.active.NAME" has an invalid value (not shown): expected a stored key's id, "env" or "file". No key is chosen for it.` | `SettingsLoader.KeyChoices` |
| 13 | Exception message the UI lane may show: keys.json exists but cannot be read, so it is not written over | `keys.json could not be read, so it was left as it is; fix or delete it, then try again.` | `KeysFile.Unreadable` |
| 14 | Exception messages the UI lane may show when Add is refused | `The label is empty.` / `The key is empty.` / `The key holds a line break or another control character.` / `The key is longer than 2560 bytes.` / `The label holds part of the key.` / `This platform has no credential store Dictum can use.` | `SecretVault.Add`, `NoCredentialStore.Write` |
| 15 | site/privacy.html, "What Dictum stores" list, a new item after the `.env` item. **Needed before this branch merges**: `StoredFilesPolicyTests.The_privacy_policy_names_every_file_Dictum_stores` fails until the policy names keys.json. The page is approved legal copy, so the lane has not edited it; the sentence is for the owner (or the K-Docs lane's privacy paragraph, brought forward). | `<li><code>keys.json</code>: the names you give API keys you add in Dictum, the service each is for and the day you added it, never a key. The keys themselves are kept by your operating system's credential store (Windows Credential Manager), for this computer and your account only.</li>` | `PathRegistry.Locations` (keys.json, `DataCategory.Secrets`) |
