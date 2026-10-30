# Design brief: API keys for cloud services (Dictum 1.4 lane group K)

Drafted 30/10/2026 13:31 AEDT by the Dictum orchestrator on Fable high, from Kyle's direction at 13:26 to 13:30 AEDT:
clear step-by-step instructions with screenshots; a ready-made key file to paste into; several keys per service, kept
and switched between; and a paste box in the app ("paste box is much better", 13:30). Every user-facing string in this
brief is PROPOSED until Kyle approves it. Approved copy named below stays byte for byte as it is.

## 1. Where things stand today (read from main at 3fbbc85, 30/10/2026)

- A key is one value per service, named by a variable: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`,
  `LLM_API_KEY` (custom clean-up server), `AZURE_OPENAI_API_KEY`, `GROQ_API_KEY`, `SPEECH_API_KEY` (custom speech
  server). `SecretStore` (src/Dictum.Core/Settings/EnvFile.cs) reads a real environment variable first, then the
  config-folder `.env`, then the `.env` beside the exe. Values never reach settings.json, the log (the redactor
  registers every name) or the screen.
- Each preset sends its own variable to its own host and to nothing else (`CloudPresets.ForAddress`, lens finding
  16/10/2026). Nothing is sent without the consent step for that exact host; plain http off this PC is refused;
  offline only refuses every service off this PC. None of this changes.
- The only in-app help is one line under the service ("The key goes in .env in the settings folder: …") and the
  button "Create the key file…", which writes a `.env` with one placeholder line for the chosen service and opens it.
  Its header and the Help paragraph about it are APPROVED copy (22/10/2026 22:52).
- The Help answer carries 16 Notepad steps as the fallback (HelpText.cs lines 128 to 155) and two generic
  sentences about getting a key. The site's help.html repeats the two sentences. No screenshots anywhere.
- `.env.example` in the repo root already is a full template with every variable and a comment per service, but
  it ships only in the source tree; users never see it.

## 2. What the user gets

1. **Paste box.** Under each cloud service in Settings > Clean-up and Settings > Models, "Add a key…" opens a small
   dialog: a Label field (prefilled with the service name) and a masked Key field. Paste, Save. The key goes into
   the operating system's credential store, never into a file Dictum writes in clear.
2. **Several keys per service, switchable.** The service shows its keys as a short list: the label, where the key
   lives (added here / key file / environment variable), an Active mark, and Remove. One key per service is active;
   choosing another is one click and takes effect at the next dictation. A `.env` line and an environment variable
   appear in the same list as entries the user can pick, so nothing that works today stops working.
3. **Ready-made key file.** "Create the key file…" writes the full template (every service, in `.env.example`'s
   words) instead of one line, so a user who prefers the file opens it and pastes under the right heading. The same
   file is downloadable from the site's help page. The file stays one key per service: several keys per service live
   in the credential store only (decision D4).
4. **Step-by-step guides with screenshots** for Anthropic, OpenAI, Google Gemini, Azure OpenAI and Groq on the
   site's help page, one section per service, each with: where to sign in, the exact clicks to create a key, what to
   copy, then the Dictum steps (open Settings, choose the service, Add a key…, paste, Save, Use cloud clean-up…).
   The dialog's "How do I get a key?" link opens the right section. In-app Help carries the same steps as text
   (the in-app Help is text only; SPEC §26).
5. **Check the key** (decision D3, recommended): after the consent step for that host, a "Check this key" button
   sends the service's cheapest authenticated request that carries no user text (listing models) and reports
   "The key works" or the service's refusal in plain words. Without consent for the host, the button is not shown.
   Today Help says "Dictum cannot check that the key itself is right"; that sentence is APPROVED copy and is
   replaced only on Kyle's approval of its PROPOSED successor.

## 3. Storage design

- **One abstraction, one implementation per platform**, behind `IPlatform` like InputAccess: `ISecretVault` with
  `Add(variable, label, value)`, `List(variable)` (ids and labels, never values), `Get(id)`, `Remove(id)`.
  - Windows: Credential Manager generic credentials via advapi32 `CredWrite`/`CredRead`/`CredEnumerate`/`CredDelete`,
    target name `Dictum/{VARIABLE}/{id}`, persistence local machine. Recalled, lane verifies on Microsoft Learn
    before coding: generic credentials with local-machine persistence do not roam with a Microsoft account, and
    `CredWrite` is allowed to a full-trust MSIX app. If either fails verification or the Store build cannot use it,
    the fallback is a DPAPI-protected file (`System.Security.Cryptography.ProtectedData`, in-box) in the config
    folder, and the privacy copy says so.
  - macOS: login Keychain via Security.framework `SecItemAdd`/`SecItemCopyMatching`/`SecItemDelete`, generic
    password, service `au.kynni.dictum`, account `{VARIABLE}/{id}`, not synchronisable. Recalled, lane verifies on
    hardware: an ad-hoc signed app whose signature changes each build may get a Keychain prompt on every new build.
    If so, the beta's answer is the prompt (documented), not a weaker store.
- **Labels and the active choice are not secrets.** `keys.json` in the config folder holds `{id, variable, label,
  added}` per entry; settings.json holds `keys.active` as variable → id. Neither ever holds a value.
- **Resolution order** in `SecretStore.Get(variable)`: the active entry if one is chosen (vault, key file or
  environment, whichever the user picked); otherwise today's order (environment, config `.env`, exe `.env`), then a
  vault entry if it is the only one. Every vault value is registered with the log redactor at load.
- **Self-test and Privacy page** list each variable as present/absent with its source, as today, plus the count of
  stored keys. Never a value, never any part of a value (decision D2).
- **Remove all keys** in Settings > Privacy, and a Help line that uninstalling Dictum leaves the credential-store
  entries behind until the user removes them (true of Credential Manager and Keychain).
- **Build ladder:** rung 4 (a native platform feature over app code). No new dependency: P/Invoke only.

## 4. Copy and compliance

- All new strings PROPOSED, collected in `.demo-preview/approve-api-keys.md` with the same table as approve-mac-access.md.
- APPROVED copy untouched: the key-file Header, the Help paragraph for the button, the consent texts, the cloud
  warnings. The Help answer's new paste-box paragraph is PROPOSED to sit above the approved paragraph; the Notepad
  steps stay as the fallback.
- Privacy policy (site/privacy.html, in-app Privacy): PROPOSED paragraph naming the credential store per platform,
  that keys stay on this PC under the user's account, and the roaming facts once verified. SPEC §5 and §9 amended;
  DECISIONS.md entry; security CHANGELOG entry for the new storage surface.
- Screenshots: taken fresh by Kyle from his own accounts against a shot list the lane writes (the lane cannot sign in
  to third-party consoles); cropped to the control, account details blanked, each guide dated "Screens as of
  DD/MM/YYYY". Dictum's own screens the lane captures itself. Provider logos are not used; names only.

## 5. Lanes, order and owners

| Lane | Model | Scope | Owns (hot files) | After |
|---|---|---|---|---|
| K-Core | Opus | `ISecretVault`, Windows implementation, `keys.json`, `keys.active`, resolution order, redactor, self-test, tests with mutation checks, SPEC §5/§9 | Dictum.Core/Settings/*, Dictum.Windows (new file), docs/SPEC.md | now |
| K-Mac | Opus, Mac session | Keychain implementation, hardware check of the ad-hoc prompt | Dictum.Mac (new file), docs/MAC-HANDOVER.md | K-Core merged |
| K-UI | Opus | Add a key dialog, key list with Active/Remove, Check this key, full-template key file, state lines; all strings PROPOSED | Dictum.UI/Views/Settings/*, AppShell.KeyFile.cs, CloudPolicy.cs, approve-api-keys.md | K-Core merged |
| K-Docs | Sonnet | help.html guide sections, in-app Help text proposals, downloadable template, privacy paragraph, shot list for Kyle | HelpText.cs, site/help.html, site/privacy.html, .env.example | K-UI's strings exist (so the guide matches the screens) |

Each lane: its own worktree from origin/main, Opus lens on the exact merging head, pins observed failing on the
reverted behaviour, never merges. The Mac beta with the PR #82 fix is built from main first and is not held by
this group.

## 6. Decisions (recommendation first on each line; ALL FIVE ACCEPTED as recommended by Kyle, 30/10/2026 18:57 AEDT, in the orchestrator session)

- **D1 Store.** Credential Manager on Windows and Keychain on macOS (recommended: visible to the user in the OS's own
  tools, no file in clear), or a DPAPI/Keychain-encrypted file.
- **D2 On screen.** Show only the label, never any part of the key (recommended: keeps the approved sentence
  "never shows it on screen" true), or show the last four characters as many apps do.
- **D3 Check this key.** Include it behind the existing consent (recommended), or leave key checking out.
- **D4 Key file.** Stays one key per service, the fallback path (recommended), or also learns several keys.
- **D5 Screenshots.** Kyle takes them from a shot list (recommended; the only honest source), or the guides ship
  text-only first and screenshots follow.
