# Dictum: Microsoft Store submission kit

Written 15/10/2026 for version 1.0.0 and updated 23/10/2026 for 1.3.0 (SPEC §18.4). Everything Partner Center asks for, in the order
it asks, plus the risks to clear first.

**Provenance.** Microsoft rules are quoted or paraphrased from Microsoft Learn pages retrieved on
15/10/2026; each section names its page. "Store Policies" means *Microsoft Store Policies* document
version 7.20 (published 15/09/2026, effective 22/10/2026), https://learn.microsoft.com/windows/apps/publish/store-policies.
Anything stated from memory is marked **(recalled, not verified)**. Dictum facts come from
`docs/SPEC.md` and `README.md`; features from SPEC §17 to §21 (Settings → Storage, "Remove Dictum
completely", the Dictum folder names, the update check) were not yet in `src/` on 15/10/2026, so
re-check every line against the release build before submitting.

## 1. Before you submit (blockers found on 15/10/2026)

| Check | Observed 15/10/2026 | Needed |
|---|---|---|
| Privacy policy URL https://dictum.kynni.au/privacy | HTTP 404 (served by Vercel) | Deploy `site/` and confirm HTTP 200. The URL is required: Store Policies 10.5.1 says Win32 and Desktop Bridge products "must always have privacy policies". |
| Website https://dictum.kynni.au | HTTP 404 (served by Vercel) | Same deployment. |
| Source https://github.com/kynniau/dictum | Public, but empty: no files, no LICENSE, no releases | Push the code and LICENSE before the listing or the site calls it open source. |
| Listing https://apps.microsoft.com/detail/9N0DG1DHTR57 | HTTP 410, "ProductNotFound" | Expected until first publish. |
| Generative AI report path (section 9, item 12) | Not in the app | A "Report a problem" action in the app. |
| Cloud clean-up consent (section 9, item 13) | Enabled only by editing settings.json | An in-app opt-in that says what is sent and to whom. |
| Privacy link inside the app (section 9, item 5) | Not in the app | A Privacy link in About or Settings. |
| Package version (added 16/10/2026) | `<Version>` 1.3.0, so the package is 1.3.0.0 (1.0.0.0 was the first submission) | The Store rejects a first part of 0 (section 6); the product moved to 1.0.0 on 16/10/2026, to 1.1.0 on 17/10/2026, to 1.1.1 on 19/10/2026, to 1.1.2 on 21/10/2026, to 1.2.0 on 22/10/2026, to 1.2.1 on 22/10/2026 and to 1.3.0 on 23/10/2026, so `package-msix.ps1 -Store` builds. |
| Visual C++ runtime (added 17/10/2026) | The second submission (resubmitted 16/10/2026) failed certification: the tester's screenshot showed "model error". Reproduced 16/10/2026 in Windows Sandbox: whisper.cpp and llama.cpp import the Visual C++ runtime, which a clean Windows lacks, and the package neither declared nor carried it | Fix in package; packaged self-test pending: the manifest declares Microsoft's framework package `Microsoft.VCLibs.140.00.UWPDesktop` (section 6), but no packaged app has yet loaded an engine with it. Rebuild with `package-msix.ps1 -Store`, pass the packaged self-test (section 10, step 4), then upload that file. |

## 2. Identity (verified: SPEC §18.1, from the Partner Center Product identity page)

| Field | Value |
|---|---|
| App name (reserved) | Dictum |
| Package/Identity/Name | `Kynni.Dictum` |
| Package/Identity/Publisher | `CN=CDA37BA0-481B-4F79-9D16-A8AD5ABB1D76` |
| PublisherDisplayName | Kynni |
| Package Family Name | `Kynni.Dictum_43s584kx66f64` |
| Store ID | 9N0DG1DHTR57 |
| Listing URL | https://apps.microsoft.com/detail/9N0DG1DHTR57 |

## 3. Properties page

Source: "Enter app properties for MSIX apps", "Categories and subcategories for MSIX app",
"Privacy policy and support info for MSIX app", "Product declarations for MSIX apps" and "System
requirements for MSIX apps" (Microsoft Learn, retrieved 15/10/2026).

- **Category:** Productivity. Microsoft lists no subcategories for Productivity. Optional secondary
  category: none.
- **Privacy policy:** answer **Yes** to "accesses, collects, or transmits personal information" (the
  app uses the microphone and can send text, or with cloud speech the recording, to a cloud service the user turns on; Microsoft marks
  this Yes anyway when the package declares capabilities that could allow it). URL:
  **https://dictum.kynni.au/privacy**
- **Website:** https://dictum.kynni.au
- **Support contact:** hello@kynni.com.au (that the field accepts an email address is
  **recalled, not verified**).
- **Product declarations:**
  - "This product incorporates generative AI features": **tick it.** Microsoft: required "if your
    app generates new content dynamically using AI models, whether the models are built into your
    app" or reached through a cloud API. Dictum's clean-up model rewrites the user's dictation.
  - "This product has been tested to meet accessibility guidelines": **leave unticked** unless the
    app has been tested end to end with Narrator, Magnifier, High Contrast and keyboard only, as the
    page requires.
  - "Windows can include this product's data in automatic backups to OneDrive" (ticked by default):
    **recommend unticking.** Dictum's data includes `.env` (an API key, if the user added one),
    calibration voice recordings and the personal dictionary. Decide before submitting; if it stays ticked,
    add a sentence about Windows backup to `site/privacy.html`.
  - "This product allows users to make purchases, but does not use the Microsoft Store commerce
    system": leave unticked (no purchases).
  - "Customers can install this product to alternate drives or removable storage" (ticked by
    default): leave as is.
- **System requirements (hardware boxes):** Microphone = Minimum. Keyboard = Minimum (dictation is
  started by holding a key). Memory = 4 GB, Minimum. Microsoft notes these produce a warning, not a
  block, on devices that lack them.

## 4. Store listing (English)

Source: "Add and edit Store listing info for MSIX app" and "Add app screenshots, images, and
trailers for MSIX apps" (Microsoft Learn, retrieved 15/10/2026). Limits: description 10,000
characters of plain text, required, and Microsoft says not to put HTML, code or URLs in it; short
description 1,000 characters, best under 270 (the target here: under 100); up to 20 product
features of 200 characters each; at least one screenshot; "What's new in this version" left blank on
a first submission.

Listing language: match the language the package declares (Store Policies 10.7). Whether the
manifest declares en-AU or en-US is not decided in the SPEC; the copy below uses Australian spelling.

### Product name

Dictum

### Short description (88 characters)

```text
Local dictation for Windows. Hold a key, talk, let go, and your words appear in any app.
```

### Description

```text
Dictum turns your speech into text in any Windows app. Hold the right Ctrl key, talk, let go: your words are transcribed on your PC, tidied up, and pasted at the cursor.

Dictum is ready to use as soon as it is installed: a small English speech model (base.en, about 148 MB) is included, so nothing is downloaded on first run. Larger, more accurate models are optional downloads from the model makers' official download page in Settings > Models. You also need a microphone. Dictum dictates in English; an optional multilingual model lets it listen in other languages.

PRIVATE BY DESIGN
Speech recognition and clean-up run on your PC by default. There is no account, no analytics, no telemetry and no crash reporting. Dictum connects to the internet only to download a model you choose, or, if you switch it on, to send your text to a cloud clean-up service (Anthropic, OpenAI or Google Gemini) with your own API key, or your voice to a cloud speech service (OpenAI, Azure OpenAI or Groq) with your own API key, or to your own server, an advanced option; that service's own terms apply. Turn on offline-only mode and it never connects to anything outside your PC. Dictum is free and open source under the MIT licence, and its Terms of Use apply; if you choose a cloud service, you take responsibility for what is sent to it.

AI THAT RUNS ON YOUR PC
Dictum uses AI models: whisper.cpp to recognise speech and, if you add it in Settings > Models (a 1.1 GB download), a small generative language model (Qwen2.5 1.5B Instruct, run by llama.cpp) to tidy the result. Out of the box a rule-based tidy fixes punctuation and capitals and follows spoken commands such as "new paragraph"; the language model also removes filler words and smooths grammar. The clean-up model sees only your words, never your audio, and is instructed not to add, answer or summarise anything. If its answer fails Dictum's checks, a rule-based tidy is used instead, and you can switch AI clean-up off completely. With cloud speech on (off by default), the recording goes to the speech service you chose instead of whisper.cpp; if that request fails, whisper.cpp on your PC hears it. To report a problem with text Dictum produced, email hello@kynni.com.au.

HOW IT WORKS
- Hold Right Ctrl, or a key you choose, speak, and release.
- Double-tap for hands-free dictation: Dictum types each sentence as you pause; press the key again to stop.
- Esc cancels a hold-to-talk dictation and nothing is pasted; in hands-free, Esc stops and keeps what is already typed.
- A small pill at the bottom of the screen shows when Dictum is listening.

MAKE IT HEAR YOU
- Calibration: read ten sentences and Dictum measures which installed model hears you best on your PC. It does not train or change the model.
- Personal dictionary for names and jargon, and corrections for words it keeps mishearing.
- Grammar assist for speakers whose first language is not English.

FOR WORK
Runs on PCs without a graphics card and on Windows virtual machines over Remote Desktop with microphone redirection. Models, recordings and logs can be deleted from Settings, or everything removed in one step.

GOOD TO KNOW
- Windows 10 or 11, 64-bit (x64). A graphics card is optional but makes the larger, more accurate models fast.
- Windows does not let Dictum paste into apps running as administrator, or into sign-in and UAC screens.
- The Fn key cannot be used as the hotkey.

FREE
Free: no subscription, no account, no ads, no analytics, no telemetry. The base product stays free to keep on your PC; optional paid extras may come later.

Free and open source under the MIT licence, by Kynni Pty Ltd.
```

The last sentence of PRIVATE BY DESIGN ("Dictum is free and open source under the MIT licence, and its Terms of Use apply; if you choose a cloud service, you take responsibility for what is sent to it.") is APPROVED by the owner 23/10/2026 09:50 AEDT (legal block): do not reword. It replaced the sentence approved 22/10/2026 17:36 AEDT (plain-words statement, section 3), whose "provided as is" read as rights taken away (legal block, section C row O11). Plain text, no URL. Paste the whole description again on the next submission.

**Additional license terms (legal block, section D item 6).** At the release that carries the Terms of Use acceptance in the Welcome window, and not before it (section D item 0: until then the Terms say Dictum asks on first start when no installed copy does), set Partner Center's "Additional license terms" field to https://dictum.kynni.au/terms, once that page is live. Owner or orchestrator; not done by a build lane. With it, the Terms replace Microsoft's Standard Application License Terms for Dictum (Terms section 15).

APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword, for the 1.3.0 listing update: in PRIVATE BY DESIGN the sentence "Dictum connects to the internet only to download a model you choose, or, if you switch it on, to send your text to a cloud clean-up service (Anthropic, OpenAI or Google Gemini) with your own API key, or to your own server, an advanced option;" gains "or your voice to a cloud speech service (OpenAI, Azure OpenAI or Groq) with your own API key," before "or to your own server", and in AI THAT RUNS ON YOUR PC the sentence "With cloud speech on (off by default), the recording goes to the speech service you chose instead of whisper.cpp; if that request fails, whisper.cpp on your PC hears it." follows "...and you can switch AI clean-up off completely." (it is not put beside the clean-up model's sentences: "If its answer fails Dictum's checks" would then read as being about cloud speech) APPROVED 22/10/2026 22:05 AEDT (qualifier added): the opening sentence of PRIVATE BY DESIGN was "Speech recognition and clean-up run on your PC." and is now "Speech recognition and clean-up run on your PC by default."; the first product feature was "Speech recognition runs on your PC with whisper.cpp and works offline" and is now "Speech recognition runs on your PC by default with whisper.cpp and works offline". The heading AI THAT RUNS ON YOUR PC stays. Whether a description-only change can be submitted without a new package is recalled, not verified: check the Partner Center flow.

### Product features (one per line, no bullets: the Store adds them)

```text
Hold a key, talk and let go: your words are pasted at the cursor in any app
Speech recognition runs on your PC by default with whisper.cpp and works offline
Optional local clean-up removes filler words and fixes punctuation
Hands-free mode types each sentence as you pause: double-tap to start, tap to stop
Calibration finds the installed model that hears you best
Personal dictionary and corrections for names and jargon
Grammar assist for speakers whose first language is not English
Offline-only mode: no connection to anything outside your PC
Free: no subscription, no account, no ads, no analytics, no telemetry.
Runs on PCs without a graphics card and over Remote Desktop
Delete models, recordings and logs from Settings, or remove everything at once
```

### What's new in this version (1.3.0)

APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. Paste it in "What's new in this version" on the 1.3.0 submission, and keep only the sentences whose feature is in the build: the sentences on cloud speech depend on the cloud speech UI (merged); the sentences on your own instructions and the key file depend on the own-instructions lane (PR #60, merged at 672f85b into main 4a4072a); and "Settings > Storage offers to move your model files" depends on PR #61 (head 1730818 when this was written, open), which offers the move from Change…, Browse… and Use default (owner, 22/10/2026 21:45 AEDT).

```text
Adds optional cloud speech: an advanced setting, off by default, that sends each recording to OpenAI, Azure OpenAI or Groq with your own API key, after a consent step of its own. The speech model on this PC is still used by default and when a request fails. Settings > Clean-up has a box for your own clean-up instructions and a Create the key file button. Settings > Storage offers to move your model files when you change their folder. Help explains what clean-up is, and Settings > About has a new "In plain words" statement, also on the Dictum website.
```

### What's new in this version (1.2.1)

APPROVED by the owner 22/10/2026 14:33 AEDT (proposed 22/10/2026). Paste it in "What's new in this version" on the 1.2.1 submission.

```text
Clearer wording while you dictate: the pill says "Recording · let go to finish", and the tray says "No microphone" or "No clean-up" when that is the case. Settings > Storage can now change where model files are kept. A new "See how it works" page shows what happens to one dictation, step by step, and pastes nothing. Help explains how to turn on cloud clean-up, and a cloud clean-up waiting for its key now says so.
```

### What's new in this version (1.2.0)

APPROVED by the owner 22/10/2026. Paste it in "What's new in this version" on the 1.2.0 submission.

```text
Adds a Welcome window with a microphone test, a Simple view with plain model choices, a Help page in Settings, and feedback buttons. Also includes the 1.1.2 fix for PCs without a working graphics driver.
```

### Search terms (if Partner Center shows the field)

Store Policies 10.1.3: at most seven unique terms, relevant, no pricing terms, and no other
products' titles. So no comparison with other dictation apps anywhere in the listing (the README's
"in the style of …" line must not be copied in).

```text
dictation
speech to text
voice typing
voice to text
transcription
offline dictation
push to talk
```

### Supplemental fields (if shown for MSIX; the fields below are documented for MSI/EXE listings)

- Copyright and trademark info: `© 2026 Kynni Pty Ltd`
- Developed by: `Kynni Pty Ltd`

### Screenshots to capture from the real app

Rules (Microsoft Learn, retrieved 15/10/2026): PNG, desktop at 1366 × 768 or larger (4K
accepted), at most 50 MB each; one required, four or more recommended; a caption of up to 200
characters each; keep key content in the top two-thirds; no extra logos, icons or marketing text.
Store Policies 10.1 requires screenshots to reflect the product accurately, so capture the shipping
build: never upload the design-canvas mockups. Use made-up sample text, never real client details.

| # | Capture | Caption |
|---|---|---|
| 1 | The pill while recording ("Recording · let go to finish"), over a text document, edge glow on | Hold Right Ctrl and talk. The pill shows Dictum is listening. |
| 2 | The tray flyout, opened from the notification area, in the Advanced view (the Simple view hides the Light or Full switch since 1.2.1) | Everything in one place: on or off, Light or Full mode, models, microphone and tools. |
| 3 | The calibration window with results | Calibration finds the installed model that hears you best. It does not train the model. |
| 4 | Settings, Storage page (or the hotkey capture box) | See and delete everything Dictum stores, or remove it completely. |
| 5 (optional) | Settings, Models page (replaces the first-run model picker, which a package with base.en no longer shows; noted 17/10/2026) | Choose a bigger speech model or add AI clean-up; base.en comes with Dictum. |
| 6 (optional) | Fix last dictation | Fix a misheard word once and save it as a correction. |

### Minimum system requirements (Additional system requirements, up to 11 items of 200 characters)

Minimum hardware:

```text
Windows 10 or Windows 11, 64-bit (x64)
A microphone
4 GB RAM for the small speech models
78 MB to 1.6 GB of disk space for a speech model, plus 1.1 GB for the optional clean-up model
No download needed to start: base.en is included; bigger speech models and AI clean-up are optional downloads
```

Recommended hardware:

```text
A graphics card with a Vulkan driver, for the larger and more accurate models
```

Sizes: tiny.en 77,704,715 bytes, large-v3-turbo 1,624,555,275, base.en 147,964,211,
qwen2.5-1.5b 1,117,320,736 (README, verified by HTTP HEAD 15/10/2026), shown here in decimal MB/GB.
The Vulkan requirement for the GPU runtime is from the README's troubleshooting section.

**Windows 10: untested, and upstream does not claim it (noted 17/10/2026; no change made).** Whisper.net's readme
(1.9.1, in its NuGet package, read 17/10/2026) lists "Windows 11 or Windows Server 2022 (or newer)" as a prerequisite,
while the package allows Windows 10 1809 (`MinVersion="10.0.17763.0"`) and the listing above says "Windows 10 or
Windows 11". Every clean-PC run so far was Windows 11 24H2 (10.0.26100, Windows Sandbox). Test on Windows 10 before
relying on it, or raise MinVersion and say Windows 11.

## 5. Age ratings (IARC questionnaire)

Source: "Generate age ratings for MSIX app" (Microsoft Learn, retrieved 15/10/2026). The
questionnaire is answered in Partner Center; the first question picks the app's category and the
rest follow from it. Microsoft shares the publisher display name and email address with IARC.

Guidance for the answers (the questionnaire wording itself is **recalled, not verified**):

- Category: a productivity or utility app, not a game.
- Violence, fear, sexual content, nudity, drugs, alcohol, tobacco, gambling, crude humour: **No.**
  The app shows no content of its own.
- User-generated content shared with other users, or communication between users: **No.** Dictum
  types what the user says into the user's own apps; nothing is published or shared with anyone.
- Sharing the user's location: **No.** Digital purchases: **No.**
- Personal information shared with third parties: **No by default.** Only if the user turns on cloud
  clean-up with their own Anthropic, OpenAI or Google Gemini key, or their own server, the text of each
  dictation goes to that service. APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword: or cloud speech
  (their own OpenAI, Azure OpenAI or Groq key, or their own server), the audio of each dictation goes to that service.
- Profanity: the app contains none. It can type swearing the user dictates, and the optional
  profanity filter (off by default) can mask it.
- Expected result: the lowest, general-audience rating. That is an expectation, not a guarantee.

## 6. Packages

- One MSIX, x64: `scripts\package-msix.ps1 -Store` writes `dist\Dictum-<version>-store.msix` with
  the identity in section 2, **unsigned**. Microsoft signs Store MSIX packages on publish; no
  code-signing certificate of our own is needed (Microsoft Learn, "The app certification process
  for MSIX app", retrieved 15/10/2026; "App package requirements for MSIX app": the Store "will
  automatically re-sign your MSIX/AppX packages", retrieved 16/10/2026). Partner Center accepts a
  plain `.msix` upload; Microsoft recommends `.msixupload` for UWP apps, which MakeAppx does not
  make (same pages, 16/10/2026).
- **Version: resolved.** The Store requires the package version's first part to be non-zero and
  its fourth part to be 0 ("App package requirements for MSIX app", Package version numbering,
  retrieved 16/10/2026). `<Version>` in `Directory.Build.props` is 1.3.0 (set 23/10/2026; 1.2.1 from 22/10/2026, 1.2.0 from 22/10/2026, 1.1.2 from 21/10/2026, 1.1.1 from 19/10/2026, 1.1.0 from 17/10/2026, 1.0.0 from 16/10/2026), so
  `package-msix.ps1 -Store` builds.
- **Dependency: the Visual C++ runtime (added 17/10/2026).** whisper.cpp and llama.cpp import `msvcp140.dll`,
  `vcruntime140.dll` and `vcruntime140_1.dll`, and their CPU builds `vcomp140.dll`. The manifest declares
  `<PackageDependency Name="Microsoft.VCLibs.140.00.UWPDesktop" MinVersion="14.0.33728.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />`;
  "Windows desktop applications that have a dependency on the C++ Runtime libraries must specify the corresponding
  version of the C++ Runtime framework package" instead of redistributing the runtime ("C++ Runtime framework packages
  for Desktop Bridge", KB 3176696), and the Store installs framework dependencies automatically ("MSIX troubleshooting
  guide"; both Microsoft Learn, read 17/10/2026). So `package-msix.ps1` leaves any app-local copy out, and
  `tests\Dictum.Tests\VcRuntimeTests.cs` pins the element. The publisher string and version were also checked against
  the framework package installed on the build PC (14.0.33728.0, the version tested).
- **The base.en speech model ships inside (Decision 16, 17/10/2026).** `scripts\publish.ps1` puts
  `models\ggml-base.en.bin` (147,964,211 bytes, SHA-256 checked by `download-model.ps1` against the catalogue's pin) and
  `THIRD-PARTY-NOTICES.txt` (the model's MIT notices and those of every other third-party component in the folder)
  beside Dictum.exe, so the zip, the installer and both MSIX flavours carry them. Measured 17/10/2026: the Store package grew from 105,953,677 to 239,909,521 bytes (352 files). The app reads
  the file only once the separate app-side change (the exe folder's `models` as a read-only fallback) has merged.
- Capabilities: `runFullTrust` (restricted) and `microphone` (device). Extensions: `windows.startupTask`
  and an app execution alias, `Dictum.exe`, so the command-line modes (such as `--selftest`) run from
  a terminal with the package identity; the alias is a packaging decision recorded in docs/DECISIONS.md,
  not a capability. Nothing else: "declare only the capabilities that your app needs" (Microsoft Learn,
  "App capability declarations", retrieved 15/10/2026) and Store Policies 10.6.
- Launch at login must stay off until the user turns it on (Store Policies 10.2.8: user consent
  before changing Windows settings). The manifest sets `Enabled="false"` on the StartupTask
  (verified: "desktop:StartupTask", Microsoft Learn, retrieved 16/10/2026: `true` would enable it at
  first launch without `RequestEnableAsync`).
- Manifest language: `en-AU`, matching the listing's Australian spelling. Partner Center also
  accepts listings in languages the package does not declare, so an en-US listing can be added
  ("Add and edit Store listing info for MSIX app", retrieved 16/10/2026).

## 7. Submission options

Source: "Manage submission options for MSIX apps" (Microsoft Learn, retrieved 15/10/2026). For each
restricted capability, "tell us why your app needs to declare the capability and how it is used";
an unapproved capability fails certification, and the review "may add some additional time".

### Restricted capability justification: runFullTrust

```text
Dictum is a voice dictation app: the user holds a key in any app, speaks, and the words are typed into that app. It is a full-trust desktop (Win32) process packaged as MSIX, and a packaged medium-integrity app must declare runFullTrust. Dictum uses full trust for exactly two things:

1. A global push-to-talk hotkey. Dictum installs a low-level keyboard hook (SetWindowsHookEx with WH_KEYBOARD_LL) so it can see the hotkey (Right Ctrl by default) and Esc (cancel) while another app has focus. The hook only compares each key with the configured hotkey and Esc. It does not record, store or transmit keystrokes. It passes every key on to the app in focus, except the configured hotkey itself, which it can withhold so the app does not also react to it. In Settings, a "Press your hotkey" box shows which key the user pressed so they can choose one; only the chosen key's name is saved.

2. Typing the dictated text into the app the user is working in. Dictum puts the text on the clipboard and sends Ctrl+V with SendInput, or, if the user prefers, types the text with SendInput. Input injected from a sandboxed package reaches only its own app container, so this needs full trust.

There is no other reason. Dictum does not request elevation, install services or drivers, or read other apps' data, and it needs no account. The source code is public under the MIT licence: https://github.com/kynniau/dictum
```

Basis: "a Medium IL app *needs* to declare the **runFullTrust** restricted capability", and input
injection under `inputInjectionBrokered` "will only be received by processes in the same App
Container" (Microsoft Learn, "App capability declarations", retrieved 15/10/2026). Hook behaviour:
SPEC §2, §3 and §20; README "Permissions on Windows".

### Notes for certification

APPROVED by the owner 22/10/2026 14:33 AEDT (proposed 22/10/2026): the "Notes written" line and the CHANGED paragraph in the block below are written for 1.2.1 (the 1.2.0 paragraph they replace was approved 22/10/2026). Update the date line when you submit. Microsoft asks for the date, no-account status, and steps to
reach anything not obvious. The "no download is needed" lines (Decision 16, 17/10/2026) hold only for a package built
after the app change that reads the bundled model from `<program folder>\models` has merged together with the
model-folder change (PR #11): without it the package carries the file but the first run still asks for a download.

```text
Notes written 22/10/2026 for Dictum 1.2.1.

CHANGED SINCE THE LAST SUBMISSION: Dictum 1.2.1 changes wording and adds one page. The hold-to-talk pill now says "Recording · let go to finish"; the tray panel says "No microphone" in place of "Ready" when no microphone is listed, and "No clean-up" in its header when clean-up is Off; the Simple view no longer shows the Light | Full choice (the Advanced view still does). Settings > Storage can now change the model folder (Change… and Use default) and says that a slower drive makes the speech model slower to load and to switch, with a warning for network folders. Settings has a new page, "See how it works": record one dictation and watch the five steps it goes through; it pastes nothing. Settings > Help has a new question, "How do I turn on cloud clean-up?" (cloud clean-up stays optional and off by default). With Anthropic chosen as the cloud clean-up provider, consent given and no ANTHROPIC_API_KEY set, Settings now shows "On, but waiting for your ANTHROPIC_API_KEY: nothing is sent until you add it to .env." instead of quietly using the local model. No download is needed to test: the base.en speech model ships inside the package and dictation works as soon as Dictum starts; larger speech models and AI clean-up are optional downloads.

No account or sign-in is needed. Dictum runs in the notification area (system tray); click its icon to open the panel.

NO DOWNLOAD IS NEEDED TO TEST. Dictum ships with the base.en speech model inside the package and starts ready to dictate; the clean-up model is off by default (a rule-based tidy is used). Larger speech models and the optional clean-up model (qwen2.5-1.5b, about 1.1 GB) are downloads from huggingface.co that you can start in Settings > Models; every downloaded file is checked against a pinned SHA-256 before use. On a PC without a discrete graphics card Dictum runs the models on the CPU. The package declares a dependency on the Microsoft.VCLibs.140.00.UWPDesktop framework (the Visual C++ runtime used by whisper.cpp and llama.cpp), which the Store installs with the app. Model files are data (ggml and GGUF weights read by whisper.cpp and llama.cpp), not executable code: Dictum never downloads or runs code.

TO TEST OFFLINE: before starting Dictum, set "offline_only": true in settings.json (the bundled base.en model needs no copying; optionally copy qwen2.5-1.5b-instruct-q4_k_m.gguf into Dictum's models folder). Settings > Storage shows both folders. Dictum then connects to nothing outside the PC, and download requests are refused with a message.

TO DICTATE: on the first start Dictum opens a Welcome window. Click Continue on its first step (this accepts Dictum's Terms of Use; closing the window or pressing Esc on that step quits Dictum), then click Next through the other steps (or press Esc to skip them and keep the defaults), then click into any text field (Notepad is fine), hold Right Ctrl, say a sentence, and release. The text is pasted at the cursor a few seconds later. Double-tap Right Ctrl for hands-free; press it again to stop. Esc cancels a hold-to-talk dictation and nothing is pasted; in hands-free, Esc stops and keeps what is already typed. A microphone is required; on a virtual machine, redirect one. If the keyboard has no Right Ctrl key (for example when connecting from a Mac), choose another key in Settings; the "Press your hotkey" box shows what Windows receives.

GENERATIVE AI: the clean-up step runs a local language model on the user's own dictated words only; it is instructed not to add or answer anything, its output is checked against the dictation, and users can report a problem from Settings (Report a problem), from 1.2, send feedback from Settings > Help (Send feedback or suggest a feature; both open an email in the user's own mail app, and Dictum sends nothing itself), or write by email.

Cloud clean-up (Anthropic, OpenAI or Google Gemini with the user's own key, or the user's own server) is optional and off by default; it needs an in-app consent and, for a cloud provider, the user's own API key. It is not needed for testing.

Launch at login is off until the user turns it on.

The package contains profanity.txt, a word list used only by the optional profanity filter (off by default) to mask swearing in the user's own dictation. It is never displayed.

Support: hello@kynni.com.au
```

PROPOSED 23/10/2026 (legal block, lens on PR #76; the owner approves it before the next submission): the first sentence of TO DICTATE above now says to click Continue on the Welcome window's first step, which accepts Dictum's Terms of Use, and that closing the window or pressing Esc on that step quits Dictum. It was "on the first start Dictum opens a Welcome window; click Next through its steps (or press Esc to skip it and keep the defaults), then".

### Notes for certification: APPROVED for 1.3.0

APPROVED by the owner 23/10/2026 06:13 AEDT (proposed 22/10/2026): the "Notes written" line and the CHANGED paragraph below are the 1.3.0 certification notes; paste them in place of the 1.2.1 block when submitting.

```text
Notes written 23/10/2026 for Dictum 1.3.0.

CHANGED SINCE THE LAST SUBMISSION: Dictum 1.3.0 adds optional cloud speech. In Settings > Models, in the Advanced view, a new Cloud speech card lets the user send each recording to OpenAI, Azure OpenAI, Groq or a server of their own instead of using the speech model on this PC. It is off by default. It asks for express consent in its own dialog, which names the host and says the recording leaves the PC; it needs the user's own API key; offline only mode refuses it for any service that is not on the PC; and if a request fails, the speech model on this PC hears the dictation. Settings > Clean-up, in the Advanced view, has a new box for the user's own clean-up instructions and a Create the key file button that makes the .env file for a cloud clean-up key. Settings > Storage offers to move model files when the model folder changes. Settings > Help has new questions ("What is clean-up?", "How can I see what Dictum does with my words?" and "Can I give the clean-up my own instructions?"), and Settings > About and the Welcome window show a short "In plain words" statement. No download is needed to test: the base.en speech model ships inside the package and dictation works as soon as Dictum starts; larger speech models and AI clean-up are optional downloads.

Cloud clean-up (Anthropic, OpenAI or Google Gemini with the user's own key, or the user's own server) and cloud speech (OpenAI, Azure OpenAI or Groq with the user's own key, or the user's own server) are optional and off by default; each needs its own in-app consent and, for a cloud service, the user's own API key. Neither is needed for testing.
```

## 8. What to expect from certification

Source: "The app certification process for MSIX app" (Microsoft Learn, retrieved 15/10/2026).

- Certification "can take up to three business days": security tests (malware scan), technical
  compliance (Windows App Certification Kit), then content compliance. The runFullTrust review can
  add time.
- A failure comes with a certification report naming the test or policy; fix it and create a new
  submission. Questions: reportapp@microsoft.com.
- After it passes, the listing is usually visible within about 15 minutes. Microsoft also spot-checks
  published apps.

## 9. Common rejection reasons for full-trust desktop apps, and how Dictum avoids each

Sources: "Resolve submission errors for MSIX app: Avoid common certification failures" (Microsoft
Learn, retrieved 15/10/2026), cited as CF #n, and the Store Policies (7.20).

| # | Rejection reason | Source | How Dictum avoids it | Status |
|---|---|---|---|---|
| 1 | Incomplete app, placeholder text, links to unfinished pages | CF #1 | Ship only finished features. Every in-app link must resolve: the site and /privacy were 404 and the repo empty on 15/10/2026 (section 1). | Blocker until deployed |
| 2 | Fails the Windows App Certification Kit | CF #2, certification process | Microsoft now calls the kit (WACK) "deprecated and no longer maintained" and certifies on submission ("Package a desktop or UWP app in Visual Studio", retrieved 16/10/2026), so a local run is optional. Not run here: no part of the kit is in the Microsoft.Windows.SDK.BuildTools package the scripts use (searched 16/10/2026). | Optional |
| 3 | Crashes or hangs without a network connection | CF #4, Store Policies 10.4.2 | Offline is a degraded mode, never a failure (SPEC §1). base.en is in the package, so a first run with the network off asks no model question and starts ready; test that, and that with offline_only on a download asked for is refused with a message, and that with the network off (offline_only off) a download fails after its retries, with a message (amended 17/10/2026). | Test |
| 4 | Testers cannot test it | CF #5, Store Policies 10.3 | No account. Section 7 notes give the dictation steps, that no download is needed (base.en is in the package) and the offline path. | Done |
| 5 | Missing or inadequate privacy policy | CF #6, Store Policies 10.5.1 | /privacy lists what is accessed (microphone audio, text, clipboard, hotkey), where it is stored, every host and when, who receives what, user controls and deletion. APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword: for 1.3.0 it also lists the cloud speech hosts (api.openai.com, {resource}.openai.azure.com, api.groq.com and a custom address) under "Cloud speech (off by default)", before submission. Microsoft's FAQ also says to "include a privacy policy URL and display it in the app": add a Privacy link in About or Settings. | Page written; in-app link to do |
| 6 | Description does not match the app | CF #7, Store Policies 10.1, 10.1.1 | The description states English only, microphone required, base.en included with the bigger models optional downloads, the administrator-window limit and the Fn key. Screenshots come from the real app. | Done; re-check against release |
| 7 | Inaccurate age-rating answers | CF #8, Store Policies 11.11 | Section 5. | Done |
| 8 | Accessibility declared without testing | CF #9, Product declarations | Leave the box unticked unless tested end to end. | Done |
| 9 | Restricted capability not approved | Submission options page, Store Policies 10.6 | Justification in section 7; only runFullTrust, microphone and the startup task are declared. | Done |
| 10 | Dynamic inclusion of code | Store Policies 10.2.2 | Models are data, checked by SHA-256; Dictum downloads no code. The Store build has no self-updater (SPEC §18.2). | Done |
| 11 | Undisclosed dependency | Store Policies 10.2.4 | No longer a first-run download (amended 17/10/2026): base.en is in the package, and the description's second paragraph says so and that the larger models are optional downloads from the model makers' official download page (the listing never names Hugging Face). | Done |
| 12 | Live generative AI not disclosed, or no way to report content | Store Policies 11.16, Product declarations | Tick the generative AI declaration and keep the description's AI paragraph. 11.16 also requires "a means for users to report inappropriate content to the developer": add "Report a problem" (a mail link to hello@kynni.com.au) in the app; the description's email line is a stopgap. | In-app action to do |
| 13 | Personal information sent to a third party without opt-in consent | Store Policies 10.5.2 | Cloud clean-up is off by default, but today it is switched on by editing settings.json. 10.5.2 wants "express permission in the product user interface" after saying what is shared and with whom, plus a way to opt out later: add a consent prompt when the user picks Anthropic or a non-local server, and an off switch. APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword: for audio, the cloud speech consent dialog (its own, apart from clean-up's: it names the host, says the recording is sent over the internet, and has Send my voice to {host} and Keep speech on this PC buttons) is what satisfies 10.5.2, with Switch cloud speech off in Settings > Models as the way out. | Risk: app change needed |
| 14 | Personal information transmitted insecurely | Store Policies 10.5.4 | Hugging Face, Anthropic and the update check use HTTPS. A user-entered `http://` server that is not on the PC would carry transcripts unencrypted: refuse or warn for non-loopback `http://` addresses. | Risk: app change needed |
| 15 | Cannot be cleanly uninstalled | Store Policies 10.2.7 | Uninstalling an MSIX removes the package and the AppData files Windows redirected for it (Microsoft Learn, "Understanding how packaged desktop apps run on Windows"). Settings → Storage → "Remove Dictum completely" deletes the data first. Files that already existed in the real AppData folder (for example from an earlier direct-download install) are modified in place and survive uninstall, which is one more reason the app warns when both builds are installed (SPEC §18.2). | Done in SPEC; verify in build |
| 16 | Changes Windows settings without consent | Store Policies 10.2.8 | Launch at login only when the user ticks it; no default-app changes. | Verify manifest |
| 17 | Value not clear on first run | Store Policies 10.1.1 | With base.en in the package the first run shows no model picker and starts ready (amended 17/10/2026). Approved by the owner 22/10/2026, for 1.2.0: the first run opens the Welcome window with a hotkey check, a microphone test and a choice of where model files are kept, and Esc keeps the defaults; after setup, show how to dictate ("Hold Right Ctrl and talk"). PROPOSED 23/10/2026 (legal block; the owner approves it before the next submission): the Welcome window's first step now asks the user to click Continue, which accepts Dictum's Terms of Use; closing the window or pressing Esc on that step quits Dictum, and the tester notes say so (TO DICTATE). | Verify in build |
| 18 | No message on an unsupported device | Store Policies 10.4.1 | No microphone: the pill shows "Mic error" and the README explains it; make sure first run says so plainly too. The package is x64 only; whether Windows on Arm runs it under emulation is **recalled, not verified** and untested. | Decide and test |
| 19 | Other products' names in the metadata | Store Policies 10.1.1, 10.1.3 | No other dictation product is named in the listing or the search terms. | Done |
| 20 | Flagged by the security scan | Certification security tests, Store Policies 10.2.3 | Antivirus may flag programs with global keyboard hooks (README). Build on a clean machine; the hook's purpose is stated in the notes. | Done |
| 21 | Listing language does not match the package | Store Policies 10.7 | Use the language the manifest declares (section 4). | Decide |
| 22 | Does not work on a clean PC: a framework dependency missing (added 17/10/2026; this failed the second submission, resubmitted 16/10/2026) | "MSIX troubleshooting guide", Missing dependencies | The manifest declares `Microsoft.VCLibs.140.00.UWPDesktop` (section 6), which the Store installs. `scripts\sandbox-check.ps1` runs the self-test on a clean Windows Sandbox (docs/TESTING-MSIX.md). | Fix in package; packaged self-test pending (section 10, step 4) |

## 10. Go-live

1. Deploy `site/` to Vercel as its own project and point dictum.kynni.au at it. On 15/10/2026 the
   domain already answered from Vercel with a 404 and a `Referrer-Policy: strict-origin-when-cross-origin`
   header, so it is attached to some other project now; move it.
2. Check https://dictum.kynni.au/privacy returns HTTP 200 and that the response carries the headers
   in `site/vercel.json`. `cleanUrls` serves `privacy.html` at `/privacy` (Vercel docs, retrieved
   15/10/2026).
3. Build the package from a checkout that has the Visual C++ runtime fix and the bundled model (fix/vc-runtime,
   17/10/2026) and the app change that reads that model: `package-msix.ps1 -Store` must print "Bundled speech model:
   models\ggml-base.en.bin" and end with "Depends on Microsoft.VCLibs.140.00.UWPDesktop". That file is the one to upload
   once step 4 has passed; an earlier `Dictum-1.0.0-store.msix` lacks the dependency and is the build that failed
   certification. If Partner Center refuses a package version it has already seen (recalled, not verified that it
   does), build with `-Version 1.0.1`, which also stamps Dictum.exe 1.0.1.
4. **Packaged self-test, before upload (added 17/10/2026).** The Visual C++ fix is in the package, but no packaged app
   has yet loaded an engine with the framework (docs/DECISIONS.md, "Visual C++ runtime"), so it is unproven until this
   passes. Run `Dictum.exe --selftest` from the installed package on a PC or Windows Sandbox that has
   Microsoft.VCLibs.140.00.UWPDesktop 14.0.33728.0 or later installed, and require the `whisper runtime` and
   `cleanup backend` lines both `[OK]`. The PC must not have a Visual C++ Redistributable in System32, or the engines
   load from there without the framework and the run proves nothing (inferred, not tested). In Windows Sandbox, which has none:
   `scripts\sandbox-check.ps1 -Package packaging\out\msix-store\layout -Framework <Microsoft's signed Microsoft.VCLibs.x64.14.00.Desktop.appx> -CleanupModel <a catalogue .gguf>`
   (docs/TESTING-MSIX.md). The licensed source of that framework `.appx` is, per KB 3176696 (**recalled, not verified**,
   from the 17/10/2026 review; not re-read in this lane): Visual Studio with the UWP workload, under
   `%ProgramFiles(x86)%\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs.Desktop\14.0`.
   Not run for 1.1.0 (17/10/2026): Microsoft's signed VCLibs framework .appx is not available on this PC without a Visual Studio or Windows SDK install; the evidence accepted instead is the 17/10/2026 review that ran the CPU engines unpackaged on the framework's own 14.40.33728 runtime DLLs (`--selftest` PASS, `--transcribe` correct).
5. Submit (sections 3 to 7), with the certification notes of section 7 as they now read.
6. When the listing is live (https://apps.microsoft.com/detail/9N0DG1DHTR57 returns 200): in
   `site/index.html`, replace the pending Store link and its "Coming soon" note with
   `<a class="btn btn-primary" href="https://apps.microsoft.com/detail/9N0DG1DHTR57">Get it from the Microsoft Store</a>`,
   change the Direct download button to `btn-secondary`, then redeploy. The HTML comment beside the
   button says the same.
7. For each direct-download release, update `site/version.json` (`version`, `notes`, and `published`) in
   the same deployment as the GitHub release. The app reads `version` and `notes` (shown under the newer line) and ignores `published`
   (`UpdateCheck.cs`, read 17/10/2026); `version` must be bare digits such as `1.1.0` (no `v`, and a `+sha`
   or `-suffix` is ignored). `published` is for people; an ISO date such as `2026-10-17` is the existing form.
8. After the deployment, check the file the update check reads: `curl -sI https://dictum.kynni.au/version.json`
   must show `200` and no `Location` header, because the update check follows no redirect (verified 17/10/2026
   10:55 AEDT: 200, no `Location`). `site/vercel.json` has `cleanUrls: true` and a headers-only rule for
   `/version.json` (Cache-Control); never add a redirect or rewrite for that path.

## 11. Certification record

| Date | Event |
|---|---|
| 16/10/2026 | First submission (1.0.0) failed on Store Policies 10.1.5, "promotes another platform": the description mentioned an upcoming Mac version. The sentence was removed and the submission was resubmitted the same day. |
| 16/10/2026 | The resubmission failed on 10.1.2.10, "Unusable Feature: AI models". Cause: the package neither shipped nor declared the Visual C++ runtime, and the whisper.cpp and llama.cpp natives import MSVCP140, VCRUNTIME140 and VCRUNTIME140_1 (section 6; reproduced in Windows Sandbox). |
| 17/10/2026, 08:10 AEDT | Dictum 1.1.0 resubmitted: the framework dependency `Microsoft.VCLibs.140.00.UWPDesktop` declared, base.en bundled, the clean-up model off by default. Status "In certification". The listing and notes are the text in sections 4 and 7. |
| 19/10/2026 | The 1.1.0 resubmission failed on 10.1.2.10: "Product shows message download cancelled when trying to download any model." Cause: choosing a model while another downloaded cancelled the running download before asking, and the UI invited that retry (DECISIONS, 19/10/2026). Fixed for 1.1.1 in PR #15; the tester notes' CHANGED paragraph in section 7 is rewritten for it. |
| 19/10/2026, about 17:30 AEDT | Dictum 1.1.1 resubmitted (package 1.1.1.0, the no-silent-cancel fix, tester notes rewritten). Status "In certification". |
| 20/10/2026 | Certification passed. Partner Center shows "In Microsoft Store" and https://apps.microsoft.com/detail/9N0DG1DHTR57 answers 200 with the Dictum listing (both checked 20/10/2026, 10:45 AEDT). The landing page's Store button went live the same day. |
| 21/10/2026 | Dictum 1.1.2 prepared: launch crash without a working Vulkan driver fixed (PR #21), "No sound from mic" warning (PR #22). Published to GitHub and dictum.kynni.au first; the Store package is staged for the owner's upload. |
| 21/10/2026 | 1.1.2 submitted (Submission 2), marked mandatory, in certification. |
| 22/10/2026 | 1.1.2 (Submission 2) passed certification and is live in the Store (Partner Center overview: "Your product is now updated"; read 22/10/2026 03:54 AEDT). |
| 22/10/2026 | Dictum 1.2.0 prepared (version bump, tester notes, listing text); all new wording is PROPOSED until the owner approves it. |
| 22/10/2026 | 1.2.0 submitted (Submission 3, id 1152921505702194758) at about 09:00 AEDT; package Dictum-1.2.0-store.msix 240,077,036 bytes, SHA-256 7AF77EA8D5E695D4DA974E3CD4F0198BFB71575543F2702F96994B79B76A100F, v1.2.0.0 x64, validated with only the runFullTrust warning; 1.1.2 package removed on save; not marked mandatory; certification notes and What's new as in this file (owner approved 22/10/2026). |
| 22/10/2026 | 1.2.0 (Submission 3) passed certification and is live in the Store (Partner Center overview: "Your product is now updated", Store presence "Submission 3"; read 22/10/2026 14:45 AEDT). |
| 22/10/2026 | The owner approved every PROPOSED 1.2 string as a block (DECISIONS, "Owner approvals (22/10/2026)"). 1.2.1 (not yet built or submitted) changes the hold pill to "Recording · let go to finish"; screenshot 1 needs retaking with it. |
| 22/10/2026 | Dictum 1.2.1 prepared (version bump, tester notes, listing text); wording PROPOSED until the owner approves it; Store screenshot 1 to be retaken with the new hold pill. |
| 22/10/2026 | The owner approved the 1.2.1 "What's new" paragraph and the 1.2.1 certification-notes lines ("Notes written" and CHANGED) as written, at 14:33 AEDT (DECISIONS, "Owner approvals (22/10/2026, the 1.2.1 block)"). Store screenshot 1 is still to be retaken with the new hold pill. |
| 23/10/2026 | Dictum 1.3.0 prepared (version bump; listing text and What's new approved 22/10/2026; tester notes PROPOSED). Contents: cloud speech (optional, consent), own clean-up instructions, Create the key file, "What is clean-up?" Help, Dictum in plain words, public copy for cloud speech; storage move-on-change (PR #61) pending at the time of writing. |
| 23/10/2026 | Dictum 1.3.0 tagged (v1.3.0 on b8b072d, 02:21 AEDT); GitHub release published by the workflow (run 39803808616: Setup 184,697,595 bytes, zip 237,203,246 bytes, test msix 240,149,267 bytes); dictum.kynni.au deployed at 02:33 AEDT with version.json 1.3.0. |
| 23/10/2026 | Store package Dictum-1.3.0-store.msix built from b8b072d: 240,138,818 bytes, SHA-256 C9F759E471802A99AEB22C18B46BD073DEC79FD7FFC9AFEB931EC5BE346210C2, manifest 1.3.0.0, unsigned (the Store signs); staged for the owner's upload. Tester notes CHANGED paragraph still PROPOSED. Store screenshot 1 and the landing-page panel are still to be captured by the owner on a PC with a microphone. |

Sources: the 16/10/2026 failures and the 17/10/2026 08:10 AEDT resubmission are as reported by the product
owner in the session briefs, and the 19/10/2026 failure as relayed by the orchestrator; they are not re-read from Partner Center, which this record cannot reach. The
fixes are in sections 4, 6 and 9 (rows 6, 11 and 22). The packaged self-test of section 10, step 4 was not run
for 1.1.0, 1.1.1, 1.1.2, 1.2.0, 1.2.1 or 1.3.0 (no packaged run on a clean PC yet).
