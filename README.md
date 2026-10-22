# Dictum

System-wide voice dictation for Windows 10/11 (x64), in the style of Wispr Flow. Hold a key, talk, let go:
your speech is transcribed **on this PC** with whisper.cpp, tidied (by a rule-based tidy, or by a small language model
that also runs **on this PC** with llama.cpp, once you add it), and pasted at the cursor of whatever app has focus.

- **Nothing leaves the machine by default.** No accounts, telemetry, analytics, crash reporting or automatic update
  checks: the direct download checks only when you click Check for updates. Nothing is downloaded to get started: a
  small English speech model (base.en) comes with Dictum, and bigger models and AI clean-up are downloads you choose.
- Free: no subscription, no account, no ads, no analytics, no telemetry. The base product stays free to keep on your PC; optional paid extras may come later. Open source under the MIT licence, by Kynni Pty Ltd.
- Works fully offline; with `offline_only: true` Dictum itself connects to no other machine. A cleanup server on
  this PC is still allowed; what such a server does with the text is up to it (see "Privacy").
- If the cleanup model is missing, slow or gives a bad answer, a rule-based tidy is pasted instead: never a failure.
- Never takes focus from the app you are dictating into.
- Personal: a dictionary of your names and terms, corrections, a profanity filter, Light/Full presets, and a
  calibration that measures which installed model hears you best (it does not train the model). See "Making it hear
  you better".

## Using it

Default hotkey: **Right Ctrl**. Any other key, a chord, or a mouse button (right, middle or a side button) can take its
place: see "Choosing a hotkey". Every gesture below works the same with a mouse button.

| Gesture | What happens |
|---|---|
| Hold the hotkey, speak, release | Records while held; on release transcribes and pastes. |
| Double-tap the hotkey (two taps within 0.4 s) | Hands-free: records until you press the hotkey again, and types each part as you pause (0.7 s of quiet after at least 2 s of speech; a part with no pause is cut at 30 s, at its quietest moment) while you keep talking. No audio between the taps or between the parts is lost. |
| Press the hotkey during hands-free | Stops; what you said since the last pause is typed last. |
| Esc while recording | Holding the key: cancels, audio discarded, nothing pasted. Hands-free: stops; the text already typed stays, and the parts not yet typed are dropped. Esc still reaches your app. |
| Esc while transcribing/polishing | Cancels; nothing more is pasted (after hands-free, the parts already typed stay) and Dictum is ready again at once. Esc still reaches your app. In the background the work stops at its next check: whisper after about 5 s on CPU and under 40 ms with Vulkan (small.en, a 62.8 s recording, cancelled 200 ms in); the cleanup model within a few ms on the GPU, but on a CPU llama.cpp cannot interrupt the step that reads the dictation, so it stays busy for about 1.5 s (qwen2.5-1.5b, measured long-dictation case). A dictation made in that window waits for it. |
| Hotkey while transcribing | Ignored (the pill shows "Busy"). |

Recordings shorter than `min_recording_ms` (0.3 s) or quieter than `silence_threshold` are discarded, because
whisper invents text on silence. A hands-free recording auto-stops and is **transcribed** (never discarded) after
`hands_free_max_seconds` (5 minutes unless changed in Settings > General > "Hands-free stops after", from 30 seconds
to 60 minutes); a held key, a stuck one included, stops the same way after 5 minutes, whatever that setting says. In
hands-free each part goes through the same steps; whisper and the clean-up also get the closing words of what you said
in the last part as context (taken before corrections, the profanity filter and snippets, so never their text), and
those words are never typed again. A clean-up set up since that part was cleaned, such as cloud clean-up switched on
mid-session, is given none of them. A part that carries on a sentence (the last one did not end with . ! ? …, a
a sentence-end mark such as U+0964 or a line break) starts with a small letter unless its first word is "I", has another capital in it,
or is a dictionary term spelt with a capital; with the rule-based tidy, which ends a part with a full stop whenever it
ends with a letter or a digit, nearly every part is typed as its own sentence. Text in a language other than English is
not cleaned up (see "Other languages and translation"), so no letter changes case where its parts join: each part is
typed as whisper wrote it. Stay in the window you are dictating into until the session stops: each part is typed into
whichever window is in front when it is ready, so switching windows mid-session types the next parts there.

### The pill

A small dark pill appears bottom-centre of the monitor with the focused window, 48 px above the taskbar
(docs/design/Pill.dc.html). It is click-through and never takes keyboard focus: the paste always goes to your app.

| Pill | Meaning |
|---|---|
| Recording · let go to finish | Recording while you hold the key (a breathing red dot and five bars following your voice). |
| Hands-free  m:ss  typing as you pause · tap to stop · Esc stops and keeps what's typed | Hands-free recording with the elapsed time; each part is typed as you pause, and it auto-stops (typing the rest) at `hands_free_max_seconds`. |
| Transcribing… | whisper is running (a turning blue ring). |
| Polishing… | The cleanup model is running (amber; not shown for the rule-based tidy, which is instant). |
| Polishing… skipped (long dictation) | The model would not finish before the deadline at its measured speed; the rule-based tidy is used. |
| Pasted  N words | Text was inserted (a green tick, and how many words; fades after a second). |
| Cancelled | Esc pressed. |
| Stopped. Text already typed stays. | Esc pressed in hands-free: the parts already typed stay (a grey square). |
| Busy | Hotkey pressed while still processing. |
| Mic error | The microphone could not be opened (see Troubleshooting), or it was lost mid-recording (a headset unplugged or out of battery): what was captured until then is still transcribed and pasted. |
| No sound from mic | While recording: the microphone has given no sound at all for 2 seconds (a muted or unplugged microphone, or a remote or virtual desktop whose microphone is not redirected). Recording goes on, and the pill goes back to Recording as soon as a sound arrives; a balloon says what to check, once per dictation. When the recording ends that way (a red "!"), nothing is transcribed or typed. It also flashes at once, with its own balloon, when Windows lists no microphone at all (see Troubleshooting). |

`pill.enabled: false` hides it.

### The big recording indicators

For when the pill is too small to notice (docs/design/Indicators.dc.html), off by default. The tray panel's **Big
recording indicator** row (and Settings > General) chooses **Off**, **Orb**, **Edge** or **Both** (`indicator.mode`):

- **Orb**: a 96 px badge with the microphone in a corner of the monitor with the focused window. Its rings follow
  your voice while recording (red), turn blue while transcribing and amber while polishing. It is click-through, so
  it is placed rather than dragged: Settings > General > Orb corner (`indicator.orb_corner`: `bottom-right`,
  `bottom-left`, `top-right`, `top-left`).
- **Edge**: a red glow around every monitor while recording. Click-through, takes no space.

Like the pill, neither ever takes focus; with the pill turned off they still work.

With Windows animation effects turned off (Settings > Accessibility > Visual effects), the dot does not breathe, the
rings and the spinner do not move and nothing fades.

### The tray panel

Click the Dictum icon in the notification area (or select it and press Enter): a panel opens beside it
(docs/design/Flyout.dc.html). A click anywhere else, Esc, or another click on the icon closes it.

- **Status**: the models in use with their runtimes and the hotkey (in the Simple view, the plain names instead: for
  example "Standard · Tidy my words · Right Ctrl", with "AI clean-up" or "No clean-up" for the clean-up; keys are shown by name,
  "Right Ctrl", while settings.json keeps `RightCtrl`), a coloured state (Ready, Recording, Working,
  Loading, Downloading, No model, Error, Off; "No microphone" in place of Ready while Windows lists none) and the **Dictation on** switch (saved as `enabled`). The models line
  sits on its own row under them, on one line; one too long for the panel ends in "…", and its tooltip shows it whole.
- **A model problem**, under the status when there is one: why a model could not load (in plain words for a missing
  Visual C++ runtime, else the engine's own message), or that it runs on the CPU because the graphics driver could not
  run it, now or the last time it started (see Troubleshooting). A model that fails on the GPU is tried once more on the
  CPU at once; if that fails too, the line shows the CPU's own error. Beside the line, **Open log folder** and, when a
  model failed inside the GPU runtime before any try on the CPU, **Try on the CPU**, which saves `whisper.gpu` or
  `llm.gpu` = `cpu` and loads the model again off the GPU; if it fails again, the line asks you to restart Dictum, which
  then loads the CPU-only runtime. Settings > About shows the same line.
- **Last dictation heard nothing: check the microphone**, in the same place, after a dictation that ended with "No sound
  from mic"; it goes when a dictation types text again.
- **Download progress** with **Cancel download** while a model downloads or waits its turn. Downloads run one at a
  time; cancelling deletes the partial file and drops any download waiting behind it. A dropped connection is resumed
  automatically (see "Model downloads and checks").
- **Mode**: **Light** | **Full** (see "Mode presets"), in the Advanced view only; neither is selected when settings.json
  matches neither (Custom). The line under it names the presets' models.
- **Speech model** and **Clean-up** pickers. A model marked "(download)" is not on disk yet: choosing it asks first
  (name, size, host), downloads it, then switches. One marked "(downloading)" or "(waiting to download)" is on its way;
  choosing it again cancels nothing (see "Model downloads and checks"). Clean-up also offers **Rule-based tidy** and **Off (paste what was heard)**.
  In the Simple view (the default, `ui.view`) the pickers show the plain choices instead: **Standard**, **More
  accurate** and **Most accurate** for speech, and **Tidy my words** (with **Add AI clean-up** while the rule-based
  tidy is in use and the text is English) and **Off** for clean-up, with the same download marks; a model chosen in the Advanced view shows as
  "Custom: <name>". The Advanced view shows the model names as above.
- **Microphone** picker (System default, or a named device: `audio.device`; "No microphone found" when Windows lists
  none, and then the no-microphone sentence shows under the status, without **Open log folder**, which is for model
  problems) and a level meter that moves while you
  dictate.
- **Big recording indicator** (Off | Orb | Edge | Both), **Profanity filter** (on = Mask; Remove is in Settings >
  Clean-up), **Grammar assist**, **Start and stop sounds** (`sounds.enabled`, off by default).
- **Calibrate my voice**, **Dictionary** (add a word, open the dictionary or corrections file), **Snippets** (a phrase
  you say and the text pasted in its place, see "Snippets"), **Fix last dictation**, **Settings**, **Help** (opens Settings > Help).
- The privacy line ("Nothing leaves this PC", or that clean-up uses a server on this PC or sends text off it) and **Quit**.

The icon's tooltip reads `Dictum · <speech model> · <runtime> · <cleanup model> · <runtime> · <hotkey>`. Notices
(a model missing, a second copy started, settings.json unreadable) appear as Windows notifications from the icon.

Only one copy runs at a time; starting it again shows a notice from the running copy. Starting it while the running
copy is still quitting waits for it (up to 30 s) and then starts; Quit itself cancels a dictation in progress first.

### Settings

The Settings window (tray panel > Settings) writes every change into settings.json, which stays the source of truth:
an edit made in the file is picked up within a second and shows in the window.

| Page | What is there |
|---|---|
| General | Dictation on, Launch at login (the Run key, or the startup task when installed from the Store), the **Press your hotkey** box (see "Keyboards and Remote Desktop"), **Hands-free stops after** (30 seconds to 60 minutes), the Fix last dictation hotkey, the pill, the big indicator and orb corner, start and stop sounds, paste or type, trailing space, restore clipboard. |
| Mode | Light or Full, with what each means on this PC. |
| Models | A **Simple \| Advanced** switch at the top (`ui.view`; Simple is the default, see "Speech models"). Simple: an explainer with **Calibrate my voice**, the three plain speech choices, **Tidy my words** and **Off**. Advanced: every speech and cleanup catalogue model with size, state (Included for the base.en that comes with Dictum) and Use / Download, a file chooser for each, greedy or beam-search decoding, **Add AI clean-up** while the rule-based tidy is in use, **Rule-based tidy only** and **Off (paste what was heard)**, and the **Model folder** with **Browse…** and **Use default** (see "Model folder"). |
| Storage | Where everything is, the model folders in the order Dictum looks in them (the model folder has **Change…** and **Use default**, as on Models), free space, every model file with its folder, size and Delete (none for the models that come with Dictum), delete recordings / logs / all app data, and **Remove Dictum completely** (see "Storage and removing Dictum"). |
| Microphone | The device and a five-second test with a level meter (nothing is recorded or kept). |
| Clean-up | What tidies the text now, the profanity filter, grammar assist, and cloud clean-up behind its consent step (see "Optional cloud polish"). |
| Privacy | What leaves the PC, Offline only, clipboard history, transcripts in the log. |
| About | Version, runtime and models, **Check for updates** (the direct download only; see "Privacy"), **Report a problem**, GitHub issues, the privacy policy, Reload settings, Open settings file, Open log folder. |
| See how it works | Record one dictation and watch each step: what Dictum hears, what the speech model heard, the rule-based tidy, the AI clean-up and whether its reply was used, and what would be pasted. Nothing is pasted, and the recording is never saved. See "See how it works". |
| Help | **How Dictum works** and **Questions and answers** in plain words, readable with no internet; **Open the full guide** (https://dictum.kynni.au/help, in your browser) and **Email support** and **Send feedback or suggest a feature** (each a new email to hello@kynni.com.au in your own mail app). See "Help" and "Feedback and feature requests". |

The windows follow Windows' light or dark setting.

### Help

Help is in two places:

- **In the app**, with no internet needed: Settings > Help, or **Help** in the tray panel. It opens with a list of its
  questions; each is a link (Tab reaches it) that moves to its answer further down. It explains how Dictum works in
  plain words (hold the key, speak, let go; your voice becomes text on this PC; the tidy; the paste; why some people are
  heard better than others) and answers the common questions: where your voice goes, wrong words, "Mic error", a Dictum
  that closes at start, changing the key, stopping, which programs it types into, model files and sizes, removing
  Dictum, and the price.
- **On the web**: the full guide, with pictures, at https://dictum.kynni.au/help. **Open the full guide** on the Help
  page opens it in your browser; Dictum itself fetches nothing.

The Help page's words were approved by the owner on 22/10/2026 (docs/DECISIONS.md) and live in `src/Dictum.Core/Help/HelpText.cs`.

### See how it works

Settings > **See how it works** shows what Dictum does with one whole recording. Press **Record** (or hold your hotkey while
the page is in front), say a sentence or two, and press **Stop**. Five boxes fill in as each step finishes: the
microphone's level (Dictum starts with sound, not words), the raw text the speech model heard, the rule-based tidy, the
AI clean-up with whether Dictum accepted its reply and, if not, the check it failed (for example "reply drops a
negation"), and the text a dictation would paste. Each timed step shows the time Dictum's log records for it. It uses
your current settings: the same speech model and clean-up as your dictations. The page pastes nothing and keeps the
recording in memory only; it is never saved to disk. If your clean-up sends text to a cloud service, this text goes
there too, and the page says so. With cloud speech on, the sound goes to the speech service, as a dictation's does, and the
page says that instead of "the sound never leaves this PC". The page's words are APPROVED 22/10/2026 (docs/DECISIONS.md).

### Feedback and feature requests

Settings > Help has **Send feedback or suggest a feature**. It opens a new email to hello@kynni.com.au in your own mail
app, with the subject "Dictum feedback" and a space to write in. Below that space are four lines about your setup
(five facts), which you can delete: the Dictum version, your Windows build, the speech model and whether it runs on the GPU or the
CPU, and the clean-up in use. Nothing else goes in: no folder or file path, no dictionary words, nothing you dictated,
no microphone name and no PC or user name.

Dictum sends nothing itself. It hands the email to your mail app, and you read it, change it and press Send, or close
it and nothing is sent. Dictum makes no web request and uses no form for this. **Email support** works the same way and
is the same email as **Report a problem** in Settings > About: the version and the models are filled in.

The website's footer has a matching link, "Feedback and feature requests" (it opens the same email).

### Choosing a hotkey

Any single key (`RightCtrl`, `CapsLock`, `F9`…`F24`, `Pause`, `RightAlt`, `ScrollLock`, `Insert`, a letter,
or a raw virtual-key code like `0x7B`) or modifiers plus one key (`Ctrl+Win`, `Ctrl+Shift+Space`). Aliases such as
`RCtrl`/`RightControl` work. With `suppress_hotkey: true` a single-key hotkey never reaches the focused app (so
Right Ctrl + C no longer copies); in a chord only the final non-modifier key is swallowed, never Ctrl/Shift/Alt/Win.
A single-key hotkey fires even while Ctrl, Shift, Alt or Win is held, so with a letter or an F-key that key's own app
shortcuts (Ctrl+F, Alt+F9) start Dictum instead; use a chord if that matters. A chord naming one key twice under two
names (`Ctrl+RightCtrl`) is refused, and so are Esc (it cancels a dictation) and Tab (it moves keyboard focus).

**The Fn key cannot be used.** It is handled at firmware level on nearly all keyboards and no Microsoft source lists
an Fn virtual key, so Windows never sees it. Use `RightCtrl`, `CapsLock`, an F-key or `Ctrl+Win` instead.

**A mouse button can be the hotkey.** `RightMouse`, `MiddleMouse`, `Mouse4` and `Mouse5` (the two side buttons, also
written `XButton1`/`XButton2`) work exactly like a key: hold to record, double-tap for hands-free, alone or as the last
part of a chord (`Ctrl+Mouse4`). Choose one in the **Press your hotkey** box by pressing it, or write it in
`settings.json`. The button is always hidden from the app under the pointer, whatever `suppress_hotkey` says, so
holding the right button opens no menu and a side button does not go Back in a browser (that click would otherwise
arrive when you let go, in the middle of the dictation). So while Dictum is on and a speech model is ready, that button
no longer reaches other apps at all, not even a quick click (no right-click menu, no middle-click, no Back); a side
button you do not use is the best choice. Until a speech model is ready (none downloaded yet, one still
loading, or one that failed to load) a mouse-button dictation hotkey leaves its button alone, so it keeps its normal
job while there is nothing to dictate with; a dictation already under way keeps it swallowed until that dictation
ends, so the stop tap and Esc still work. Each name means whichever physical button Windows reports as that button
(Dictum reads the button messages Windows sends, not the hardware). **The left button cannot be used**: holding it to
dictate would swallow every click.
Dictum watches the mouse (a WH_MOUSE_LL hook on the same thread as the keyboard hook) only while a hotkey names a mouse
button, a **Press your hotkey** box is listening, or `--selftest` runs; the hook decodes button presses only and passes
moves and wheel turns on untouched. The fix-last hotkey can be a mouse button too; while Dictum is on, that button no
longer reaches other apps at all either.

### Keyboards and Remote Desktop

A Mac keyboard has no right Control key, and Remote Desktop from a Mac sends Option as Alt and Command as the Windows
key, so the default `RightCtrl` never fires there. **On a Mac over Remote Desktop use the right Option key
(`RightAlt`) or right Command (`RightWin`).** Single keys that reach a Remote Desktop session: `RightAlt`, `RightWin`,
`CapsLock` and the F-keys. Fn never reaches Windows (above).

To see what Windows actually receives, use the **Press your hotkey** box (Settings > General, and the Welcome to Dictum window at first run): press
**Change…**, then the key or mouse button. The box names exactly what arrived, by the key's name, which says its side ("Right Alt
down", "Mouse 5 down"), and its **Hold to test** light is on for as long as the chosen key is held; a key that never
arrives is obvious in seconds. While the box listens, both hotkeys are paused, so testing does not start a dictation.
Settings > General has a second box of the same kind for the **Fix last dictation** hotkey: press **Change…**, press the
key or button, then **Use for Fix last dictation**; **Turn off** removes it. Only one box listens at a time. Tab, and
Enter or Space pressed on their own, choose nothing in a box: with the keyboard they move to its buttons and press
them (a chord such as `Ctrl+Shift+Space` still counts).
`Dictum.exe --selftest` runs the same check from the command line: "Press your hotkey (…) now", watching for 5 s.

### Other languages and translation

Dictum can listen for any of the languages Whisper supports (100 of them) and, if you want, write English whatever you
speak: a Spanish speaker dictates in Spanish and English is pasted. Recognition and translation run on this PC; if
cloud clean-up is on (Settings > Clean-up), the English text still goes to that provider, as every dictation does.
Settings > Models > **Language** shows the full list:

- **Spoken language**: English first, then every other language Whisper supports alphabetically by name (Cantonese,
  `yue`, only with the large-v3 models), or **Detect automatically** (whisper works it out from the first seconds of each
  dictation, useful for a mixed team). The code goes into `whisper.language`.
- **Translate to English** (`whisper.translate`): the text is written in English whatever was spoken, and the clean-up,
  dictionary and corrections run on that English. With it off and a language other than English (or Detect
  automatically), the text is written in the language spoken and the clean-up is skipped, because its rules and prompt
  are written for English (the rule-based tidy would capitalise Polish "i", for one); only whitespace is tidied, while
  corrections, the profanity filter and snippets still run. Detect automatically also switches the clean-up off for
  English speech, so an English speaker should choose English. The About page and `--selftest` show "skipped" as the
  clean-up state. A change of language or translation rebuilds the decoder in place; the model is not loaded again.
- **The model must be multilingual.** The `.en` models hear English only. `small`, `medium`, `large-v3` and their
  quantised files hear every language; the page refuses a language or the translate switch that the chosen model
  cannot do, and `--selftest` prints a "language" line that fails for the same reasons. OpenAI's README (read
  16/10/2026) says to use medium or large for the best translation and that `large-v3-turbo` "is not trained for
  translation tasks" and returns the original language even when asked to translate, so turbo is refused for
  translation. Medium needs a GPU or a fast desktop CPU (see the speed table below for its English twin). Choosing a
  model that cannot do what the language settings ask, from the tray picker, a Mode preset, Choose file, calibration's
  Use this or a hand edit, still loads it, with a warning balloon saying why it will not work.
- **Not possible with Whisper:** some languages are not in Whisper's list (openai/whisper `tokenizer.py`, read
  16/10/2026), so they cannot be chosen, transcribed or translated.
- **Calibrate my voice** always listens in English (it reads English sentences), whatever language dictation uses.
  The Light and Full presets in Mode write their own speech models (`base.en`; `small.en` on a CPU, `large-v3-turbo`
  on a GPU): the Mode page names any preset whose model cannot hear your language or translate.
- Mixed speech (two languages in one sentence) has not been tested here: try the language you set first, then Detect
  automatically with translation on, and judge by ear. Translation can lose nuance that transcription keeps, so for
  staff who speak accented English a bigger multilingual model with the language left on English may read better;
  test both on real recordings.

## Install

Three routes, each per user and none needing administrator rights. Pick one: the Store build and a direct-download
build on the same PC is not supported (see "Store and direct-download builds"). Coming from the earlier build, named
Dictate: see "Moving from Dictate".

1. **Microsoft Store.** https://apps.microsoft.com/detail/9N0DG1DHTR57 (live since 20/10/2026, version 1.1.1). The
   Store signs the package and keeps Dictum updated.
2. **Direct download: `Dictum-Setup-<version>.exe`** from the project's GitHub releases,
   https://github.com/kynniau/dictum/releases (none published yet on 16/10/2026). It installs into
   `%LOCALAPPDATA%\Programs\Dictum` with a Start menu entry, offers a desktop shortcut and launch at login (both off
   unless you tick them), and appears in Settings > Apps for uninstalling. Upgrading keeps your settings and models,
   which live outside the program folder; quit Dictum first (the installer asks you to). Settings > About > **Check for
   updates** says whether a newer version is out; it checks only when you click it. The installer is **not
   code-signed yet**, so Windows SmartScreen may say "Windows protected your PC": choose **More info**, then **Run
   anyway**, but only for a file you downloaded from the release page, and check it against the release's
   `SHA256SUMS.txt` first (`Get-FileHash .\Dictum-Setup-<version>.exe`). Uninstalling asks whether to delete your
   models, settings and recordings too; a silent uninstall (`unins000.exe /VERYSILENT`) keeps them.
3. **Portable zip: `Dictum-<version>-win-x64.zip`** from the same releases. Unzip it anywhere and run `Dictum.exe`,
   or install it per user with `scripts\install.ps1` (see "Deploying to staff machines and Azure VMs"). The build is
   self-contained: no .NET install is needed.

**The direct download needs the Microsoft Visual C++ Redistributable (x64), version 14.51 or later**, unless its
`runtimes` folders carry the Visual C++ runtime DLLs (a build made with `publish.ps1 -VcRuntimeDir`; see "Build from
source"). The speech engine (whisper.cpp) and the clean-up engine (llama.cpp) are built with Microsoft's C++ compiler
and load its runtime; many PCs already have it, but a clean Windows does not, and then the tray shows "model error"
and the `whisper runtime` line of `--selftest` says `model load failed`. Install it from Microsoft,
https://aka.ms/vc14/vc_redist.x64.exe ("Microsoft Visual C++ Redistributable latest supported downloads", read
17/10/2026); that needs administrator rights. The installer checks for it before it installs anything and asks:
**Yes** opens that download and carries on installing Dictum, **No** carries on without it, **Cancel** stops Setup (an
install run with `/SUPPRESSMSGBOXES` carries on and logs it). Its "Launch Dictum" box appears at the end only when the
runtime is there by then. The Store build declares Microsoft's runtime framework package for the Store to install with
it (no packaged run has confirmed that yet: docs/STORE.md, section 10).

**Every build carries the base.en speech model** (Decision 16, 17/10/2026): `models\ggml-base.en.bin` beside
`Dictum.exe`, the catalogue's file byte for byte. `THIRD-PARTY-NOTICES.txt` beside it carries the model's MIT notices
and the licence notice of every other third-party component in the folder (a test fails when a shipped package is
missing from it).

Testing the self-signed MSIX package before the Store listing is live: see [docs/TESTING-MSIX.md](docs/TESTING-MSIX.md).

### Build from source

1. Install the **.NET 10 SDK** (the current LTS; support ends 14/11/2028 —
   https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Either:
   - the standard installer from https://dotnet.microsoft.com/download/dotnet/10.0 (adds `dotnet` to PATH), or
   - a user-local install with `dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"`.
     That script adds the folder to PATH for the current session only and does not set DOTNET_ROOT ("dotnet-install
     scripts reference", learn.microsoft.com, read 15/10/2026); in each new shell run
     `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"`.
2. Build and test: `dotnet build Dictum.sln -c Release` then `dotnet test` (from the repo root). The solution is
   `src\Dictum.Core` (everything that is not a window or a platform API), `src\Dictum.UI` (the Avalonia windows),
   `src\Dictum.Windows` (the Windows platform and `Dictum.exe`), `tests\Dictum.Tests` and `tests\Dictum.UiProbe`
   (shows the overlays on the real desktop for the focus test). The window tests run on Avalonia's headless
   platform and need nothing. Tests that need a model, the desktop or the network are skipped (and reported as
   skipped) unless their variable is set (the earlier `DICTATE_*` names still work):

   | Variable | Runs | Touches |
   |---|---|---|
   | `DICTUM_MODEL_PATH` = a catalogue ggml `.bin` | real whisper runs (transcription, cancelling, calibration engine, the first-use hash check on a temporary copy) | that file, read only; the GPU |
   | `DICTUM_CLEANUP_MODEL_PATH` (or its earlier name `DICTUM_LLM_PATH`) = a `.gguf`; `DICTUM_LLM_GPU=cpu` forces the CPU | real cleanup runs (reply accepted by the validator, cancelling, warm-up speeds, the prompt cache) | that file, read only; the GPU |
   | `DICTUM_CALIBRATION_GPU=cpu` | the calibration engine on the CPU (run that class alone) | CPU for minutes |
   | `DICTUM_TRAY_TESTS=1` (with `DICTUM_MODEL_PATH`) | the real Dictum.exe in tray mode for 12 s, beside an installed copy (scratch APPDATA and LOCALAPPDATA, hotkeys off) | a tray icon briefly |
   | `DICTUM_UI_TESTS=1` | the overlays' focus and click-through (tests\Dictum.UiProbe), the real clipboard, an injected F24 | the desktop: do not type meanwhile |
   | `DICTUM_AUDIO_TESTS=1` | opens the default microphone for one second | the microphone |
   | `DICTUM_CANCEL_TESTS=1` (with `DICTUM_MODEL_PATH`; `DICTUM_CANCEL_RUNTIME=auto` for the GPU figure) | the CPU-forced cancel measurement (run that class alone) | CPU |
   | `DICTUM_DOWNLOAD_BENCH=<model>:<variant>:<runs>` | the download reliability measurement | **downloads real files** |
   | `DICTUM_SETUP_INSTALL_DIR` = a folder `Dictum-Setup-<version>.exe /VERYSILENT /DIR=…` installed into | the real Settings > Apps entry selects the uninstaller, and the removal step runs it | **uninstalls that copy** (its entry and Start menu shortcut too) |

   Run a gated job with `-failSkips` (or check the skip count) if a missing variable must fail it rather than skip.
3. Publish: `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1` → `dist\Dictum\Dictum.exe` and
   `dist\Dictum-<version>-win-x64.zip` (self-contained: 278 files with `manifest.txt`, 420.3 MiB unpacked / 226.1 MiB zipped, measured 17/10/2026 for 1.0.0 with the bundled model and the full third-party notices; 276 files, 278.8 MiB / 98.4 MiB without it on 16/10/2026; including the two data files `profanity.txt` and `calibration-sentences.txt`, the base.en speech model in `models\` with `THIRD-PARTY-NOTICES.txt`, Avalonia's Skia, HarfBuzz and ANGLE libraries,
   the Vulkan, CPU and no-AVX whisper runtimes and the Vulkan and CPU llama.cpp runtimes; English resources only; `-FrameworkDependent` gives a small build that needs the .NET 10 Runtime: no Desktop Runtime, as there is no WPF or WinForms).
   `-VcRuntimeDir <folder>` copies the Visual C++ runtime (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`,
   `vcomp140.dll`) beside each of the 8 native runtime folders, so the zip and the installer run on a PC without the
   redistributable. The folder is `VC\Redist\MSVC\<version>\x64` of a Visual Studio or Build Tools install, version
   14.51 or later; Microsoft allows only licensed Visual Studio users to redistribute these files
   (docs\DECISIONS.md, "Visual C++ runtime"). Without it, publish warns that the build needs the redistributable.
   The bundled base.en model comes from `dist\model-cache`, else from a matching copy in `%LOCALAPPDATA%\Dictum\models`
   (read only), else from huggingface.co at the catalogue's pinned commit; `download-model.ps1` checks its size and
   SHA-256 every time and stops the publish on a mismatch.
4. Installer: install [Inno Setup](https://jrsoftware.org/isdl.php) 7 for your user (the version used is in
   `docs\DECISIONS.md`), then `powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1` →
   `dist\Dictum-Setup-<version>.exe` (the version comes from `<Version>` in `Directory.Build.props`).
5. MSIX: `scripts\make-test-cert.ps1` once, then `scripts\package-msix.ps1 -Test` →
   `dist\Dictum-<version>-test.msix`, signed with that self-signed certificate (see
   [docs/TESTING-MSIX.md](docs/TESTING-MSIX.md)); or `-Store` → `dist\Dictum-<version>-store.msix` with the Partner
   Center identity, unsigned because the Store signs what it publishes. The Store refuses a version whose first part
   is 0, so `-Store` needs `<Version>` 1.0 or later. Both declare Microsoft's Visual C++ runtime framework package,
   `Microsoft.VCLibs.140.00.UWPDesktop`, which the Store installs and a sideload needs first (docs/TESTING-MSIX.md);
   any Visual C++ runtime DLLs in `dist\Dictum` are left out of the package. `scripts\sandbox-check.ps1` runs the
   self-test of a build or a package layout in Windows Sandbox, a clean Windows (docs/TESTING-MSIX.md). The first run restores the Windows SDK build tools (MakeAppx,
   MakePri, SignTool) and Svg.Skia, which renders the logos from `site\dictum.svg`, from nuget.org into
   `packaging\out`; no Windows SDK install is needed.
6. CI: `.github/workflows/ci.yml` builds, runs the ungated tests and publishes the zip on every push and pull
   request (the publish downloads the base.en model, hash-checked, and fails the job on a mismatch); a tag `v<version>` matching `<Version>` also builds steps 4 and 5 and publishes a GitHub release with
   the zip, the installer, the test MSIX, its certificate and `SHA256SUMS.txt`.

### First run

1. **Welcome to Dictum (1.2).** With no `settings.json` yet (or a seeded one that names no `whisper.model`), Dictum opens
   one window that walks through the first run in large text, with a step line ("Step 2 of 6: Model files"), **Back**
   and **Next**. Every step is reachable by keyboard; **Esc** or closing the window skips the rest and keeps the
   defaults below, and the settings file is always left valid. Each step writes its own setting when you
   finish it, and only when you changed something:
   1. **How Dictum works**: hold your hotkey, speak, let go, and the words appear where you were typing; everything
      happens on this PC.
   2. **Where to keep the model files**: the suggested folder (`%LOCALAPPDATA%\Dictum\models`) with its drive's free
      space ("C: has 41.0 GB free"), and **Choose another drive or folder…** for a PC that is short of space on C:. It
      follows Settings > Models > Model folder's rules: the folder of the models that come with Dictum is refused, and so
      is a `\\?\Volume{…}` style path. Writes `models.folder` only when you choose another folder. Not shown when
      `settings.json` already names a folder.
   3. **Speech model**: base.en comes with Dictum (`models\ggml-base.en.bin` beside `Dictum.exe`; inside the package
      for the Store build), so this step just says "The Standard model is included and ready." It writes
      `whisper.model` = `base.en` and `llm.provider` = `none` (the rule-based tidy), and Dictum starts with nothing
      downloaded. Bigger speech models and AI clean-up are downloads you choose later in Settings > Models; **Add AI
      clean-up (1.1 GB download)** fetches the recommended cleanup model, `qwen2.5-1.5b`, after asking (see "Cleanup
      models"). A base.en in your own models folder, or in the one you chose in step 2, counts the same.
      **Without base.en** (a build without its `models` folder, or the file removed or failing its check) the step
      offers the Simple view's choices (Standard, More accurate, Most accurate; multilingual models when settings.json
      names a language that is not English), the recommendation for this PC chosen, and **Show every model** for the
      whole catalogue and **Choose model file…**. A `settings.json` that names a language that is not English (or
      translate) gets the same choice even with base.en, which is English only; **Esc** there keeps base.en, so dictation
      works at once and the language warning follows. A model that is not on disk yet
      is downloaded from huggingface.co when the window reaches Try it (progress in the tray panel and the icon's
      tooltip) and checked against the catalogue's size and SHA-256. The window asks no clean-up question: it writes the
      rule-based tidy, as with base.en. Nothing is downloaded without your choice.
   4. **Your hotkey**: the hotkey check (SPEC §20), unchanged: the "Press your hotkey" box with `RightCtrl` chosen, the
      note for Mac keyboards over Remote Desktop, and **Use this hotkey** or **Skip** (see "Keyboards and Remote
      Desktop").
   5. **Test your microphone**: the microphone list from Settings > Microphone, a live level meter (it moves at most
      twice a second when Windows' "Show animations" setting is off), and "Say a few words." After about three seconds in all at
      or above `silence_threshold` (the level below which a dictation counts as silence) it says "Heard you."; after five
      seconds of digital silence it explains what to check, including microphone
      redirection on a remote or virtual desktop; with no microphone listed it says so. The microphone is open only
      while this step is on screen, and closes by itself about five seconds after "Heard you." or after a minute;
      **Test again** opens it again. **Skip** is fine.
   6. **Try it**: a box in the window, which takes the keyboard focus once the speech model is ready (until then it shows "Getting the speech model ready…" and has no cursor). Click in it if you have moved away, hold your hotkey, say a sentence and let go: the real dictation path
      types into it. After your first dictation the window explains that accents, voices and microphones differ, with
      **Calibrate my voice** and **Finish**. If nothing arrives within 30 seconds of the model being ready, it says so,
      and **Finish** still works.

   A `settings.json` seeded by an administrator gets only the choices it leaves open. One that names `whisper.model`
   opens no window; one without `llm.model` or `llm.provider` (but with `whisper.model`) gets the clean-up choice alone,
   answered without a question when base.en is on the PC and in the clean-up picker when it is not. One that names
   `llm.provider` = `local` but no `llm.model` keeps `local` when base.en is on the PC: it gets the recommended cleanup
   model written, which is not downloaded, so nothing downloads and a notice says the rule-based tidy is used until you
   download it in Settings > Models. A `settings.json` that cannot be read gets neither. With `DICTUM_MODEL_PATH` or
   `--model` set no window opens and the recommendations are written. If Dictum stops after the speech step (a crash,
   say), the next start opens no window: the hotkey and microphone steps were not shown again before 1.2 either.
   Recommendations: speech `medium.en` when a discrete GPU is found, `base.en` otherwise, integrated graphics included
   (`small.en` offered as more accurate, a few seconds slower); cleanup `qwen2.5-1.5b` on every machine (see "Cleanup
   models" for why). `--selftest` prints both, with the Vulkan devices it found.
2. If the configured speech model file is missing when Dictum starts, it asks: **Download**, **Choose file…** or
   **Quit**. (While running, a missing model only produces a notification; the loaded model stays in use.) A missing
   cleanup model produces one notification and the rule-based tidy until you download it (tray panel > Clean-up, or
   Settings > Models).
3. Check everything with `.\Dictum.exe --selftest | Out-Host`.
4. Optional, off by default: cloud polish with Anthropic, OpenAI, Google Gemini or your own server (see "Optional cloud polish").

### Speech models

**Simple and Advanced views (1.2).** Settings > Models opens in the Simple view: three plain choices instead of
model names, each with a line that says what it means, its download size until it is on this PC, and the same In use,
Downloaded, Download… and Downloading states as the full list. Choosing one is the same as choosing its model in the
full list. The **Advanced** segment at the top of the page (or `"ui": { "view": "advanced" }`) shows every model and
its details, the decoding choice and the file choosers; the choice is saved and the tray panel's pickers follow it.
Which model each plain choice is depends on whether Dictum found a discrete GPU:

| Choice | No discrete GPU | Discrete GPU |
|---|---|---|
| Standard | base.en | base.en |
| More accurate | small.en | medium.en |
| Most accurate | medium.en | large-v3-turbo |

The mapping reuses the existing recommendation (base.en, or medium.en on a GPU) and the Mode presets' models
(small.en, or large-v3-turbo on a GPU, for Full).

When you speak another language, choose Detect automatically, or turn Translate on (see "Other languages and
translation"), the choices are multilingual: Standard is small, More accurate is medium, and with a discrete GPU Most
accurate is large-v3-turbo (large-v3 when translating, which turbo cannot do). Without a GPU there is no multilingual
Most accurate, because every catalogue model above medium is marked as needing a GPU. A choice that cannot hear your
language is never offered. On a PC without a GPU the lines say how slow the bigger models are there (see "Measured
speed"). Choosing a model to download asks in plain words: "Download More accurate (487 MB)? It comes from the model
makers' official download page and is checked before use." A model in use that is none of the choices shows as
"Custom: <name>". Clean-up in the Simple view is **Tidy my words**: the rule-based tidy, or the
recommended clean-up model once it is on this PC, with **Add AI clean-up** to download that model (offered only while
the text is English: no clean-up model runs on any other language), or **Off**, which
pastes what was heard with no clean-up at all, in any language (your own corrections, snippets and the profanity filter still apply) (see "Cleanup"). Cloud clean-up is never
offered in the Simple view.

`base.en` comes with Dictum (see "First run"). The others are stored in the model folder
(`%LOCALAPPDATA%\Dictum\models` unless you choose another: see "Model folder") as `ggml-<name>.bin`, downloaded from
`https://huggingface.co/ggerganov/whisper.cpp/resolve/<commit>/ggml-<name>.bin`, pinned to commit
`5359861c739e955e79d9a303bcbc70fb988958b1` so the URL keeps serving the bytes the hashes describe even if the
repository changes. Sizes verified by HTTP HEAD (`X-Linked-Size`) on 15/10/2026, and again at that commit. Get one
with the tray (Speech model), `Dictum.exe --download-model <name>`, or `scripts\download-model.ps1 -Model <name>`.
Every catalogue download is checked against the exact size and SHA-256 in the catalogue
(`src\Dictum\Settings\ModelCatalog.cs`, mirrored in `download-model.ps1`; LFS hashes from the Hugging Face API tree,
read 15/10/2026); a server that offers a file of another size is refused before it is downloaded, and a mismatch
deletes the partial file and reports the error. A name outside the catalogue (`--download-model` and
`install.ps1 -Model` accept any whisper.cpp model name) is fetched from the same repository with no size or hash to
check against: the CLI warns, and that file is loaded unchecked.

| Name | Bytes | Trade-off |
|---|---|---|
| tiny.en | 77,704,715 | fastest, lowest accuracy; for slow CPUs; ~273 MB memory |
| base.en | 147,964,211 | fast on any CPU, fair accuracy; the CPU default; ~388 MB memory |
| small.en | 487,614,201 | more accurate than base.en, a few seconds slower on a CPU; ~852 MB memory |
| small.en-q5_1 | 190,098,681 | small.en quantised to 5-bit: smallest small download |
| small.en-q8_0 | 264,477,561 | small.en quantised to 8-bit: about half the size |
| medium.en | 1,533,774,781 | high accuracy; wants a GPU or a fast desktop CPU; ~2.1 GB memory |
| medium.en-q5_0 | 539,225,533 | medium.en quantised to 5-bit: about a third of the size |
| medium.en-q8_0 | 823,382,461 | medium.en quantised to 8-bit: about half the size |
| large-v3-turbo | 1,624,555,275 | multilingual, about 8× faster than large-v3 (OpenAI's table) but cannot translate; needs a GPU |
| large-v3-turbo-q5_0 | 574,041,195 | large-v3-turbo quantised to 5-bit; still needs a GPU |
| small | 487,601,967 | any language, translates to English; the smallest multilingual model, the same size as small.en |
| small-q5_1 | 190,085,487 | small quantised to 5-bit: smallest multilingual download |
| medium | 1,533,763,059 | any language, translates well to English; wants a GPU or a fast desktop CPU |
| medium-q5_0 | 539,212,467 | medium quantised to 5-bit: about a third of the size |
| large-v3 | 3,095,033,483 | the most accurate in any language and the best for translation; needs a GPU |
| large-v3-q5_0 | 1,081,140,203 | large-v3 quantised to 5-bit; still needs a GPU |

The six multilingual rows were added 16/10/2026 with sizes and SHA-256 hashes read from the Hugging Face API tree at
the pinned commit; four existing rows (small.en, medium.en, large-v3-turbo and its q5_0) were read the same way as
controls and matched the catalogue exactly. No HTTP HEAD was made for the six. Because they are catalogue names, any
file named `ggml-small.bin`, `ggml-medium.bin`, `ggml-large-v3.bin` or one of their quantised names is now
hash-checked before loading wherever it sits: a file of your own under one of those names must match the pinned bytes,
or be renamed to load unchecked. See "Other languages and translation".

Memory figures are the "Memory usage" table of the whisper.cpp README (https://github.com/ggml-org/whisper.cpp,
fetched 15/10/2026) for the tiny/base/small/medium families; not measured here. That table gives no figures for
the quantised or large-v3-turbo files.

### Measured speed (15/10/2026, development machine)

Hardware: Intel Xeon W-1250P (6 physical cores), 64 GB RAM, NVIDIA Quadro RTX 4000 8 GB (driver 596.72), Windows 11.
Input: the same 10.46 s synthesised WAV for every row, `Dictum.exe --transcribe <wav> --no-llm --model <name>`
from the published build. "Transcribe" is the time from audio to text (model already loaded), from the second of
two fresh-process runs; CPU was forced with `"whisper": { "gpu": "cpu" }`; threads at Whisper.net's default.

| Model | Vulkan (GPU) | CPU forced | Model load (Vulkan / CPU) |
|---|---|---|---|
| tiny.en | 0.24 s | 0.92 s | 0.4 s / 0.2 s |
| base.en | 0.26 s | 1.90 s | 0.5 s / 0.2 s |
| small.en | 0.38 s | 6.43 s | 1.1 s / 0.7 s |
| medium.en | 0.73 s | 20.1 s | 2.6 s / 2.0 s |

whisper.cpp processes audio in 30-second windows, so a short utterance costs nearly as much as a 10-second one
(small.en on CPU: 5.2 s for a 4.7 s clip, 6.4 s for this 10.5 s clip). The first-ever Vulkan run of a model on a
machine is slower while the NVIDIA driver compiles and caches shaders (12.3 s for small.en here, 1.2 s for tiny.en).
Untested here: large-v3-turbo, quantised models, and other CPUs; Azure VM figures will differ.

### Cleanup models

The two models do different jobs: whisper turns your audio into words; the cleanup model only tidies those words
(fillers, punctuation, spoken commands) and never hears the audio. A speech model is required; a cleanup model is
optional, and without one the rule-based tidy runs. None is set up by default (decided 17/10/2026): the first run uses
the rule-based tidy, and Settings > Models > **Add AI clean-up (1.1 GB download)** fetches the recommended one.

Stored in the model folder (see "Model folder"), downloaded from the vendor's own organisation on
huggingface.co (`https://huggingface.co/<repo>/resolve/<commit>/<file>`, each pinned to the repository commit listed
below) and verified by size and SHA-256 like the speech models. Get one with the tray panel's Clean-up picker or Settings > Models,
`Dictum.exe --download-cleanup-model <name>`, or `scripts\download-model.ps1 -Cleanup <name>`. Every value below was
read on 15/10/2026: sizes by HTTP HEAD (`X-Linked-Size`; the HF API file listing for the gated Gemma repositories,
whose HEAD answers 401), hashes and commits from the HF API (`https://huggingface.co/api/models/<repo>` and
`.../tree/<commit>`, whose listing has every file below at the size and hash shown), licences from the model cards.

| Name | Vendor | Repository / file | Bytes | Licence | Notes |
|---|---|---|---|---|---|
| qwen2.5-0.5b | Qwen | `Qwen/Qwen2.5-0.5B-Instruct-GGUF` / `qwen2.5-0.5b-instruct-q4_k_m.gguf` | 491,400,032 | Apache-2.0 | faster; never recommended (dropped content in 2 of 4 measured cases) |
| qwen2.5-1.5b | Qwen | `Qwen/Qwen2.5-1.5B-Instruct-GGUF` / `qwen2.5-1.5b-instruct-q4_k_m.gguf` | 1,117,320,736 | Apache-2.0 | the recommendation on every machine |
| qwen2.5-3b | Qwen | `Qwen/Qwen2.5-3B-Instruct-GGUF` / `qwen2.5-3b-instruct-q4_k_m.gguf` | 2,104,932,768 | Qwen Research License: **non-commercial** | never recommended |
| smollm2-1.7b | Hugging Face | `HuggingFaceTB/SmolLM2-1.7B-Instruct-GGUF` / `smollm2-1.7b-instruct-q4_k_m.gguf` | 1,055,609,536 | Apache-2.0 | not tested here |
| gemma-3-1b | Google | `google/gemma-3-1b-it-qat-q4_0-gguf` / `gemma-3-1b-it-q4_0.gguf` | 1,003,541,152 | Gemma Terms of Use | **requires a Hugging Face login**; size-checked only; not tested here |
| gemma-3-4b | Google | `google/gemma-3-4b-it-qat-q4_0-gguf` / `gemma-3-4b-it-q4_0.gguf` | 3,155,051,328 | Gemma Terms of Use | **requires a Hugging Face login**; wants a GPU; size-checked only; not tested here |

SHA-256:

- qwen2.5-0.5b `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`
- qwen2.5-1.5b `6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e`
- qwen2.5-3b `626b4a6678b86442240e33df819e00132d3ba7dddfe1cdc4fbb18e0a9615c62d`
- smollm2-1.7b `decd2598bc2c8ed08c19adc3c8fdd461ee19ed5708679d1c54ef54a5a30d4f33`
- gemma-3-1b, gemma-3-4b: not published to anonymous callers (the HF API masks the hash of a gated file), so Dictum
  checks their size only; `download-model.ps1` reads the hash with your token and checks it.

Commits: qwen2.5-0.5b `9217f5db79a29953eb74d5343926648285ec7e67`, qwen2.5-1.5b
`91cad51170dc346986eccefdc2dd33a9da36ead9`, qwen2.5-3b `7dabda4d13d513e3e842b20f0d435c732f172cbe`, smollm2-1.7b
`2d4a76a30b4af41ecd395c35725ac11688d4cfe4`, gemma-3-1b `d1be121d36172a4b0b964657e2ee859d61138593`, gemma-3-4b
`15f73f5eee9c28f53afefef5723e29680c2fc78a`.

Licences: Qwen 0.5B/1.5B `https://huggingface.co/<repo>/blob/main/LICENSE` (Apache-2.0); Qwen 3B the "Qwen RESEARCH
LICENSE AGREEMENT" (`https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/blob/main/LICENSE`), non-commercial use
only; SmolLM2 the model card's `apache-2.0`
(`https://huggingface.co/HuggingFaceTB/SmolLM2-1.7B-Instruct`); Gemma the card's `gemma` licence, i.e. the Gemma
Terms of Use (`https://ai.google.dev/gemma/terms`, "Last modified: April 1, 2026"), which permit use by
organisations subject to the Gemma Prohibited Use Policy and require a notice if you redistribute the file (for
example from a share used by `-ModelsFrom`). Do not deploy any model in a business without your own licence review.

**Gated models (Gemma):** Google requires you to accept its licence on huggingface.co while logged in, so an
anonymous download gets HTTP 401. Dictum does not handle Hugging Face tokens; tray, picker and
`--download-cleanup-model` say "requires a Hugging Face login to download; download it in your browser, then choose it
in Settings > Models" instead of trying. Accept the licence on the repository page, then either download the file there
and pick it with Settings > Models > Choose .gguf file…, or (direct download, which ships the scripts) run
`scripts\download-model.ps1 -Cleanup gemma-3-1b` and enter the token when asked (typing is hidden), or set
`HF_TOKEN` for that session. The token goes only to huggingface.co and only for a gated model, in a temporary header
file, and is never written to settings or logs. `-Token <token>` still works but warns: a token on the command line
can be read by other programs running as you while the download runs.

**Not in the catalogue, and why** (HF API searches, 15/10/2026): `microsoft/Phi-4-mini-instruct` contains no GGUF
file and the `microsoft` organisation has no Phi-4-mini GGUF repository (an ONNX one exists); `meta-llama` has no
GGUF repository for Llama 3.2 1B/3B Instruct, and a community conversion would break the "vendor's own files, pinned hash" rule above; Qwen3 1.7B/4B GGUFs exist from
`Qwen`, but whether their "thinking" mode can be switched off reliably through the chat template could not be
checked without downloading them. Any `.gguf` can still be used with "Choose .gguf file…".

**A GGUF file is data, not a program.** It is a container of tensors and metadata that llama.cpp reads; it holds
no code that runs. The residual risk is a bug in the loader that a crafted file could exploit; Dictum reduces it
by downloading only from the vendors' repositories above and refusing any file whose SHA-256 does not match. A
catalogue file that arrived some other way (copied, curl, an older build) is hashed the first time it is loaded
and refused if it does not match (see "Model downloads and checks"). Files are matched to the catalogue by file name,
in any folder: a file you pick with "Choose .gguf file…" (or "Choose model file…") that has a catalogue file's name
is checked against that entry, and if it does not match, the message asks you to rename it (Dictum never tells you
to delete a file outside its models folder). A picked file with any other name has no reference hash: only use files
you trust.

**Compare models on your own words:** `Dictum.exe --bench-cleanup <file.wav|file.txt>` runs every downloaded
cleanup model over the same input. On the 10.46 s clip, Vulkan, speech by medium.en, with the prompt cache, second of
two runs of the published build (15/10/2026, after the fix batch; the GPU was shared with a running copy of Dictum):

| Model | Time | Verdict | Output |
|---|---|---|---|
| rule-based tidy | 0.2 ms | — | So the quick brown foxer jumps over the lazy dog you know. The quarterly report is ready for review, please send comments by Friday. |
| qwen2.5-0.5b | 77 ms | rejected: drops dictated content (8/14 content words kept) | The quarterly report is ready for review, please send comments by Friday. |
| qwen2.5-1.5b | 178 ms | accepted | So the quick brown fox jumps over the lazy dog. The quarterly report is ready for review. Please send comments by Friday. |
| smollm2-1.7b | 169 ms | accepted | The quick brown fox jumps over the lazy dog. The quarterly report is ready for review. Please send comments by Friday. |

One run on one clip: smollm2-1.7b's accepted reply here is not a measurement of its quality (it is still "not tested
here" in the catalogue table), and the same clip with small.en's transcript got it rejected for dropping content.

#### Measured cleanup cost (15/10/2026, same development machine)

`Dictum.exe --bench-cleanup <wav>` (each model loaded and warmed up as the tray app does, then timed on the
cleanup call alone), second of two runs. Clips: the 10.46 s WAV above and a 60.9 s synthesised WAV. Vulkan rows
transcribe with medium.en; CPU rows force `"gpu": "cpu"` for both models and use base.en. "Before" is the build
that re-read the system prompt and examples on every call; "after" keeps them evaluated (see "Cleanup").

| Backend | Model | 10 s clip, before → after | 60 s clip, before → after | Verdicts (both clips) |
|---|---|---|---|---|
| Vulkan | qwen2.5-0.5b | 0.20 s → 0.08 s | 0.96 s → 0.87 s | 10 s rejected (dropped a sentence), 60 s accepted |
| Vulkan | qwen2.5-1.5b | 0.41 s → 0.19 s | 1.33 s → 1.19 s | both accepted |
| CPU | qwen2.5-0.5b | 2.43 s → 1.09 s | 6.54 s → 5.21 s | 10 s accepted, 60 s rejected (dropped a sentence) |
| CPU | qwen2.5-1.5b | 4.04 s → 1.79 s | 12.64 s → 10.24 s | 10 s accepted, 60 s rejected (dropped a sentence) |

**Why every machine is recommended qwen2.5-1.5b:** the rule is 1.5b on CPU if it adds at most about 2 s to a
10-second dictation, otherwise the rule-based tidy; never 0.5b. With the cached prompt 1.5b added 1.79 s on CPU
(1.60 s on the first run; it was 4.04 s before). qwen2.5-0.5b is never recommended: its reply was rejected for
dropping content in 2 of the 4 measured clip/backend cases above (every run), and on CPU it left whisper's
"Umsow … foxer" unrepaired. If 1.8 s is too slow on a CPU, choose "No AI cleanup (rule-based tidy)".

**Memory budget** (re-measured 15/10/2026 with the cached prompt: process peak working set, and the rise in
`nvidia-smi` "memory used" during the run, both models plus driver context, sampled every 250 ms): both models stay
loaded while Dictum runs.

| Backend | Models | Peak RAM | GPU memory |
|---|---|---|---|
| Vulkan | medium.en + qwen2.5-0.5b | 870–881 MiB | 2,059–2,211 MiB |
| Vulkan | medium.en + qwen2.5-1.5b | 1,540–1,550 MiB | 2,835–2,840 MiB |
| CPU | base.en + qwen2.5-0.5b | 730–746 MiB | none |
| CPU | base.en + qwen2.5-1.5b | 1,942–1,958 MiB | none |

Other combinations were not measured.


**First run on a GPU:** the first-ever cleanup on a machine compiles GPU shaders (observed 20.5 s once for 0.5b,
and 8.7 s for the first 60 s clip with 1.5b); the driver caches the result. A copy of Dictum in a new folder paid
it again here (10.0 s for the 60 s clip on its first run, 1.3 s on its second), so the cache appears to be per
program location (inferred, not documented by the driver). The tray app warms the model up after loading on
every backend, which also evaluates the prompt once. Untested here: qwen2.5-3b, SmolLM2, Gemma, other GPUs and
CPUs, Azure VMs.

### Model downloads and checks

- **Downloads resume.** A model is written to a `.part` file and checked against the catalogue's size and SHA-256
  before it is renamed into place. If the connection drops or stalls (30 s without data), Dictum waits 1, 2, 4,
  8, 16, 16, 16 s between attempts and resumes from the byte it reached, up to 8 attempts, with progress in the tray
  tooltip continuing from there. Only then does it say "Model download failed after 8 attempts (…). Check your
  network, then choose it again …: it carries on from where it stopped": the partial file is kept, and the next
  request hashes it and resumes from its end (the final SHA-256 check still covers the whole file; a kept file that
  does not match is deleted). **Cancel download** (tray panel or Settings > Models) stops at once and deletes the
  partial file. The log has one warning per failed attempt with the full exception chain.
- **Choosing a model never cancels a download by itself** (since 19/10/2026). Choosing the model that is downloading
  again only says "<name> is already downloading (42%)." (or "…is already waiting to download."); in Settings > Models
  its row shows "Downloading 42%" (or "Waiting to download") with its button disabled, and the tray pickers mark it
  "(downloading)". Choosing a model that is already on this PC switches to it at once and the download carries on.
  Choosing another model that needs a download asks first, saying what is downloading and how far it has got:
  **Download after it** (the default: it waits its turn, and the running download keeps its bytes), **Stop it
  and download <new>** (the only answer that cancels; the balloon says "Download of <file> stopped: you chose <name>."),
  or **Cancel** (nothing changes). When the new model would fit only if the running download stopped, the question offers
  only **Stop it and download <new>** and **Cancel** (Enter presses Cancel). A Mode preset whose models are all on this PC is applied without cancelling anything,
  and pressing the Mode that is already chosen does nothing. **The last choice wins:** a download that arrives after you
  chose another model of the same kind (speech, or clean-up) does not switch you to it, and a Mode preset overtaken
  that way stops downloading the rest of its models. A Mode preset or a calibration download that needs a model already
  on its way waits for that download instead of asking about it or downloading it again. The space check counts what
  the downloads already under way will still write, and a download waiting its turn is checked again when it starts.
- **Files Dictum did not download are checked on first use.** A catalogue model that arrived another way (copied
  with `install.ps1 -ModelsFrom`, fetched with curl, left by an older build) is hashed the first time it is loaded
  and refused if it does not match: a notification, an error in the tooltip, `[FAIL]` in
  `--selftest`, and the file is not loaded. A match is remembered in `%LOCALAPPDATA%\Dictum\verified-models.json` (path, size, time),
  so a file in your own folders (`%LOCALAPPDATA%\Dictum\models` and the models that come with Dictum) is not hashed
  again. A file anywhere else (in a model folder you chose, which other users may be able to change, or one you picked by
  path) is hashed again the first time it is loaded in each session. Hashing took 4.0 s for the 1.5 GB medium.en here.
  Gated models without a published hash are size-checked only.
- **Why downloads drop here.** On this development machine Windows' TLS layer (Schannel) dropped large downloads
  from huggingface.co's CDN with "The decryption operation failed", at a different point each time. Measured
  15/10/2026 on the 1.62 GB large-v3-turbo, six downloads each: 20 dropped connections with Windows' default
  (TLS 1.3), 4 with TLS 1.2. Dictum therefore uses TLS 1.2 for model downloads and always resumes; every test
  download completed. Details in `docs/DECISIONS.md`.

### Model folder

Dictum looks for a model, speech (`.bin`) or clean-up (`.gguf`), in three folders, in this order, and uses the first
copy whose size matches the catalogue:

1. **The folder you choose** (`models.folder`): Settings > Models > **Model folder** > **Browse…**, for example a
   folder on another drive when C: is short of space. Downloads go here. The box shows the end of a long path (the
   start is cut, "…\Dictum\models"); the whole path is its tooltip, which keyboard focus opens too. **Browse…** starts
   in that folder, or the nearest folder above it that exists (the default one is made by the first download).
2. **Your own folder**, `%LOCALAPPDATA%\Dictum\models`. Downloads go here while no folder is chosen (**Use default**).
3. **The models that come with Dictum**: the `models` folder beside `Dictum.exe` (inside the package for the Store
   build), which holds `base.en`. Dictum never downloads into it, changes it or deletes anything in it, and it cannot be
   chosen as the model folder, under another name for it either (a junction or symbolic link, an 8.3 short name, a
   `\\?\C:\` path; not a subst drive or a mounted volume): such a setting is ignored and the log says so. Of the
   device paths, only the `\\?\C:\` and `\\?\UNC\` forms are accepted as the model folder; any other (a `\\?\Volume{...}` name,
   say) is refused with a warning in the log, and the default folder is used.

Every model is still checked against the catalogue's size and SHA-256 before it is loaded, wherever it was found. A
copy of the wrong size is passed over for the next folder; a model that came with Dictum and fails its check is passed
over too (the log says so once) and never deleted.

- **Changing the folder moves no files.** Copy the models across yourself, or download them again; the old copies stay
  until you delete them in Settings > Storage, which lists the three folders and the folder each model is in.
- **A folder on a network share** (a `\\server\share` path or a mapped network drive) works, but models load slowly
  from it, and Settings > Models says so under the folder. A folder on the PC is faster.
- **A folder other users can reach.** A model in the folder you chose is hashed again the first time it is loaded in
  each session (see "Model downloads and checks"). **Delete all app data** leaves a chosen folder outside your user
  folder alone, and its question names it; **Delete** beside one of its models in Settings > Storage still removes that
  model.
- **Free space:** a download is refused, with a message saying how much is free, when it would leave its drive with
  less than 200 MB (a download that resumes a kept partial file needs only the rest); one that would leave under 2 GB
  warns first. When the free space cannot be read, the download goes ahead.
- `whisper.models_dir`, this setting's earlier name, is still read when `models.folder` is not set; when both are set
  and differ, `models.folder` wins and the log says so. Choosing a folder in Settings clears the earlier name.

For shared virtual desktops, see "Deploying to staff machines and Azure VMs".

## Permissions on Windows

There is no Accessibility or Input Monitoring grant on Windows (unlike macOS). What matters:

- **Microphone:** Settings > Privacy & security > Microphone: "Microphone access" on and **"Let desktop apps access
  your microphone"** on (https://support.microsoft.com/en-us/windows/windows-camera-microphone-and-privacy-a83257bc-e990-d54a-d212-b5e41beba857).
  The microphone is only open while you are recording, so the Windows mic indicator is off when idle.
- **Global hotkey and pasting** need no grant for normal apps.
- **Elevated (administrator) windows:** Windows blocks `SendInput` from a normal process into a higher-integrity
  window ("Applications are permitted to inject input only into applications that are at an equal or lesser
  integrity level"), and "neither GetLastError nor the return value will indicate the failure was caused by UIPI
  blocking" (https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput, read 15/10/2026). So pasting into an admin
  window does nothing unless Dictum itself runs as administrator. **Expected, not documented by Microsoft — confirm on
  your machine:** the hotkey may also not be seen while an elevated window is focused; Microsoft's UIPI design notes
  only say a UIAccess process "has read input for all integrity levels using low-level hooks"
  (https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10)).
- **Secure desktop** (UAC prompts, the lock screen, Ctrl+Alt+Del): expected not to work there (recalled, not verified
  against a Microsoft source); a key released there is handled when you come back (see "Choosing a hotkey").
- **SmartScreen and Smart App Control:** Dictum's files are not signed. An unsigned file shows "Windows protected
  your PC" and needs "Run anyway", and "Enterprise policy can prevent continuation entirely"; on Windows 11, Smart App
  Control "will block execution of unsigned files unless the file has a positive reputation", and its checks "apply
  to all executable files" ("SmartScreen reputation for Windows app developers",
  https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation, read 15/10/2026). Where Smart
  App Control or such a policy is on, sign every shipped binary (Dictum.exe, Dictum.dll and the whisper and llama
  DLLs), not only Dictum.exe, or deploy through a channel your organisation trusts.
- **Antivirus** may flag programs that install global keyboard hooks. Dictum's hook only watches for the hotkey
  and Esc; the source is here to check.

## Settings reference

File: `%APPDATA%\Dictum\settings.json` (or `<dir>\settings.json` with `--config <dir>`). JSON with `//` comments
and trailing commas tolerated. Keys starting with `_` are comments. An unknown key or invalid value is logged as a
warning and the default is used; Dictum never fails to start because of settings. Saving the file reloads it.

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Hotkey on/off (tray "Enabled"). |
| `hotkey` | `"RightCtrl"` | See "Choosing a hotkey". A key, a chord, or a mouse button (`RightMouse`, `MiddleMouse`, `Mouse4`, `Mouse5`, alone or ending a chord); never the left button. |
| `suppress_hotkey` | `true` | Hide the hotkey from the focused app (chords: final non-modifier key only). A mouse button is always hidden. |
| `tap_ms` | `300` | A press shorter than this is a tap, not a hold (50–2000). |
| `double_tap_ms` | `400` | Window for the second tap that starts hands-free (100–2000). |
| `min_recording_ms` | `300` | Shorter recordings are discarded (0–10000). |
| `hands_free_max_seconds` | `300` | Auto-stop and type the rest after this long (30–3600; Settings > General > "Hands-free stops after"). A value from 5 to 29 left from an earlier version runs as 30, with a line in the log. Hands-free only: a held key stops after 5 minutes. |
| `silence_threshold` | `0.01` | Discard if the loudest 20 ms block's RMS (0–1) stays below this. |
| `audio.device` | `null` | `null` = Windows default microphone; else an endpoint ID or part of the device name (`--selftest` lists them). |
| `whisper.model` | `base.en` when it comes with Dictum, else your first-run pick (`medium.en` if the file has no value) | Short name → `ggml-<name>.bin`, looked up in the model folders (see "Model folder"), or an absolute path to a `.bin`. The tray panel's Speech model picker and Settings > Models write this. |
| `whisper.models_dir` | `null` | The earlier name of `models.folder`, still read when that is not set (ignored, with a warning, when both are set and differ). Choosing a folder in Settings clears it. |
| `models.folder` | `null` | `null` = `%LOCALAPPDATA%\Dictum\models`; else an absolute folder (another drive, or a `\\server\share`, from which models load slowly) that Dictum looks in first and downloads to. A relative or invalid value is ignored with a warning. Settings > Models > Model folder writes it; changing it moves no files. See "Model folder". |
| `whisper.language` | `"en"` | The language you speak: a whisper code from openai/whisper's list (`es`, `de`, `ja`…) or `auto` to detect it from the first seconds of each dictation. Anything but `en` needs a multilingual model (`small`, `medium`, `large-v3`), not an `.en` one. Settings > Models > Language writes it; a change rebuilds the decoder and does not load the model again. |
| `whisper.translate` | `false` | `true`: write English whatever language is spoken. Needs a multilingual model; `large-v3-turbo` cannot translate (OpenAI's README: the turbo model "is not trained for translation tasks"). |
| `whisper.threads` | `0` | CPU threads; 0 = Whisper.net's default. |
| `whisper.gpu` | `"auto"` | `vulkan`: try Vulkan, then CPU, then CPU-without-AVX. `auto`: the same when the Vulkan loader (`vulkan-1.dll`, installed with a GPU driver that supports Vulkan) loads on this PC; when it does not, Vulkan cannot load either, so `auto` starts straight on the CPU (the log says why). `cpu`: CPU, then CPU-without-AVX, including on the first start, when Dictum looks at the GPU to recommend a model. The runtime is chosen once per start, so a change needs a restart; a model loaded after a change to `cpu` already stays off the GPU. A model whose load or warm-up fails on the GPU is loaded once more on the CPU, and a start after the GPU ended Dictum while it loaded the model uses the CPU-only runtime for that run (Troubleshooting, "Dictum quits by itself"). |
| `whisper.beam_size` | `0` | `0` = greedy decoding (fastest); `1`–`8` = beam search with that many beams (more accurate, slower). Calibration's **Use this** writes it. |
| `speech.provider` | `"local"` | Which engine turns each recording into text. `local`: the speech model in `whisper.*`, on this PC (nothing leaves it). `openai-compatible`: cloud speech, an advanced option. Each recording (each part, in hands-free) is sent as audio (16 kHz WAV) to the service at `speech.base_url`, with your dictionary terms (and, in hands-free, the closing words of the part before) as a hint. It runs only after its own consent step, which records the host in `consent.json` apart from the clean-up consent; settings.json alone never switches it on, `offline_only` refuses it off this PC, and with `whisper.translate` on it is not used (the transcriptions endpoint does not translate). The local model stays loaded either way: when a request fails (no key, an HTTP error, `speech.timeout_ms`, an empty or unreadable reply) it hears that recording instead, and the log names the reason (status and host, never the audio or text). Settings does not have its consent step yet, so in this build nothing in Dictum records that consent. |
| `speech.base_url` | `null` | Required for `openai-compatible`; Dictum appends `/audio/transcriptions`. For example `https://api.openai.com/v1`, `https://YOUR-RESOURCE.openai.azure.com/openai/v1/` or `https://api.groq.com/openai/v1`, or your own server. `https://` only, also for a server on this PC (the consent step says the connection is encrypted); an `http://` address is ignored with a warning. A query (such as an `api-version`) stays after the added path. The address decides which key goes with each request (see "Secrets"). |
| `speech.model` | `null` | Required for `openai-compatible`: the service's transcription model id (for Azure OpenAI, your deployment name). No default is filled in. |
| `speech.timeout_ms` | `30000` | The deadline for one request (1000–120000); past it the local model hears that recording. Not a measured figure: it is to be set from a measured round trip before cloud speech ships. |
| `llm.provider` | `"local"` when the key is absent; the first run writes `"none"` when base.en comes with Dictum (a seeded value is kept) | `local` (the cleanup model on this PC; Settings > Models > Add AI clean-up writes it), `none` (rule-based tidy only), `off` (no clean-up: the text as whisper wrote it, spaces tidied only; see "Cleanup"), or the opt-in cloud/server providers `anthropic` and `openai-compatible` (see "Optional cloud polish"). `anthropic` with no `ANTHROPIC_API_KEY` and no consent on file is treated as `local` (logged), so a settings.json written by the first build, which defaulted to `anthropic`, gets local cleanup instead of none. Anthropic agreed to in the consent step but with no key sends nothing: the rule-based tidy runs and Settings > Clean-up and Privacy say "On, but waiting for your ANTHROPIC_API_KEY: nothing is sent until you add it to .env." (unless offline only is on, whose line wins), as for the OpenAI and Google Gemini presets. A cloud provider also needs the in-app consent step (`consent.json`, see "Optional cloud polish"): without it `local` is used and the log says so. |
| `llm.model` | your first-run pick (`qwen2.5-1.5b` if the file has no value) | For `local`: a cleanup catalogue name or an absolute path to a `.gguf`; the Clean-up picker and Settings > Models write this. Anything else (for example `claude-haiku-5-5` left by the first build) is treated as unset and the recommendation is used (logged). For `openai-compatible`: the server's model id. With `anthropic` a local name means `claude-haiku-5-5`. |
| `llm.gpu` | `"auto"` | Cleanup model backend. `vulkan`: try Vulkan, then CPU. `auto`: the same, except that when every GPU Vulkan finds is integrated graphics the CPU build is used (faster there: on the development PC's Intel UHD P630 and 6-core Xeon, 2.1-2.2 s per clean-up against 4.2-4.3 s on Vulkan, qwen2.5-1.5b, 17/10/2026). Dictum learns the GPUs from the speech model's runtime, so `auto` keeps Vulkan on such a PC when that runtime is the CPU-only one (`whisper.gpu = cpu`, which **Try on the CPU** writes, or a start after the GPU ended Dictum on the speech model) or when no speech model load has been asked for (a missing speech model, say), and `--bench-cleanup` always times Vulkan there; set `llm.gpu` to `cpu` for the CPU build in those cases. `cpu`: CPU only. The backend is chosen once per start, so a change needs a restart; a model loaded after a change to `cpu` already stays off the GPU. A model whose load or warm-up fails on the GPU is loaded once more on the CPU, and a start after the GPU ended Dictum while it loaded the model uses the CPU build for that run. |
| `llm.base_url` | `null` | Required for `openai-compatible`, e.g. `http://localhost:11434/v1`. For `anthropic` it is optional and replaces `https://api.anthropic.com` (e.g. a company gateway). The `ANTHROPIC_BASE_URL` environment variable is ignored. Because both providers share this key, a URL left over from `openai-compatible` would receive the Anthropic key and transcripts: Dictum logs a warning and shows a balloon "Anthropic requests will go to <host>" whenever `anthropic` is used with a `base_url`. |
| `llm.timeout_ms` | `15000` | The shortest cleanup deadline (1000–120000). The deadline is 10 s + 0.1 s per dictated word, at least this and at most 60 s (or this, if longer); past it the rule-based tidy is pasted. |
| `llm.prompt_file` | `null` | Absolute path to a custom system prompt; the built-in prompt otherwise. The grammar-assist sentence and the dictionary line are added to it too. |
| `llm.instructions` | `null` | Your own clean-up instructions (Settings > Clean-up, Advanced view; at most 2,000 characters, a longer value is cut with a warning). Added in their own section after the prompt (built-in or `llm.prompt_file`) for the local model and cloud clean-up, never for `none` or `off`; for cloud clean-up they are part of the text sent. Every reply is still checked by the validator. The log gets their length, never their text. |
| `llm.grammar_assist` | `false` | `true` adds the grammar-assist sentence to the cleanup prompt (see "Calibration"). Tray panel > Grammar assist. |
| `llm.cloud_consent` | — | Ignored, with a warning in the log: consent to cloud clean-up is not a setting. It is recorded in `consent.json` in the settings folder (provider, host, time, app version), written only by Settings > Clean-up's consent step (SPEC §22). |
| `insert.method` | `"paste"` | `paste` (clipboard + Ctrl+V) or `type` (simulated typing, for apps that block paste). |
| `insert.restore_clipboard` | `false` | Put your previous clipboard back after pasting. Text formats only: images and files are not restored. |
| `insert.trailing_space` | `true` | Add a space after the inserted text. |
| `insert.paste_delay_ms` | `150` | Wait after Ctrl+V before restoring the clipboard (0–2000). |
| `insert.clipboard_history` | `false` | `false` marks each transcript so Windows keeps it out of clipboard history (Win+V) and cloud clipboard sync. `true` leaves that to your Windows settings. |
| `pill.enabled` | `true` | Show the pill. |
| `indicator.mode` | `"off"` | The big recording indicator: `off`, `orb`, `edge` or `both` (see "The big recording indicators"). |
| `indicator.orb_corner` | `"bottom-right"` | Where the orb sits: `bottom-right`, `bottom-left`, `top-right` or `top-left`. |
| `sounds.enabled` | `false` | A short sound when recording starts and stops. |
| `profanity.mode` | `"off"` | `off`, `mask` or `remove` (see "Profanity filter"). Settings > Clean-up; the tray panel's switch turns Mask on and off. |
| `ui.view` | `"simple"` | How Settings > Models and the tray panel's pickers list the models: `simple` (three plain speech choices and Tidy my words) or `advanced` (every model and its details). The **Simple \| Advanced** switch at the top of Settings > Models writes it (see "Speech models"). |
| `fix_hotkey` | `null` | A second hotkey, same format as `hotkey`, that opens Fix last dictation. Ignored (with a warning) if one press could complete both hotkeys: the same key, a chord containing the other (`Ctrl+Space` and `Ctrl+Shift+Space`), or a key group containing the other's key (`Ctrl+Shift+F` with the default `RightCtrl`, when pressed with the right-hand Ctrl). Use keys the hotkey does not use, for example `LeftCtrl+Shift+F`. Follows `enabled`. |
| `log_level` | `"info"` | `debug`, `info`, `warning`, `error`. |
| `log_transcripts` | `false` | `true` writes transcript text to the log. Off by default. |
| `offline_only` | `false` | `true`: Dictum itself connects to no other machine. Model downloads, Check for updates and the `anthropic` provider are refused with a clear message, and so is cloud speech (`speech.provider`) off this machine; `openai-compatible` is allowed only to a server on this machine (`localhost`, `127.0.0.1`, `::1`), and that request goes to it directly, never through a proxy; what that server does with the text is up to it (a local proxy or gateway may forward it). Any value other than `true` or `false` counts as `true` (with a warning). A settings.json that cannot be read at startup runs with the defaults and `offline_only` on until it is fixed; one that breaks while Dictum runs keeps the previous settings. Copy models in with `install.ps1 -ModelsFrom`. Settings > About says "Offline only". |


Model overrides, strongest first: `--model <name|path>` (this launch only; a tray choice replaces it), then the
environment variable `DICTUM_MODEL_PATH` (a file path, used for testing), then `whisper.model`. For cleanup,
`--cleanup-model <name|path>` uses that local model for this launch only (and switches the provider to `local`).


### Secrets: environment variables and `.env`

Standard dotenv behaviour: a real environment variable (process, user or machine) **wins**; `.env` only fills in
keys that are not set. Dictum reads `%APPDATA%\Dictum\.env` and the `.env` beside `Dictum.exe` (the AppData file
wins between the two). So a staff machine or Azure VM can carry a machine-level `ANTHROPIC_API_KEY` and no `.env`
at all. See `.env.example`.

| Key | Used by |
|---|---|
| `ANTHROPIC_API_KEY` | `llm.provider = "anthropic"`. Missing key → nothing is sent. Agreed to in the consent step, the rule-based tidy runs and Settings says it is waiting for the key; never agreed to (a first-build settings.json), the local cleanup model is used (logged), or the rule-based tidy if that model is not downloaded. |
| `OPENAI_API_KEY` | The OpenAI preset (`openai-compatible` at `api.openai.com`), sent as `Authorization: Bearer` to that host only. Required: without it nothing is sent and the rule-based tidy is used. Cloud speech at `api.openai.com` uses it the same way (without it nothing is sent and the local speech model is used). |
| `GEMINI_API_KEY` | The Google Gemini preset (`openai-compatible` at `generativelanguage.googleapis.com`), sent as `Authorization: Bearer` to that host only. Required, as for OpenAI. |
| `LLM_API_KEY` | `llm.provider = "openai-compatible"` at any other address (a custom server), sent as `Authorization: Bearer` when set. Optional for local servers. Never sent to the OpenAI or Google Gemini hosts (on any port; a final "." in the address does not make another host) or to the Ollama preset address. |
| `AZURE_OPENAI_API_KEY` | Cloud speech (`speech.provider = "openai-compatible"`) at an Azure OpenAI resource: any `https://` host ending `.openai.azure.com`, sent in the `api-key` header to such a host only. Required there: without it nothing is sent and the local speech model is used. |
| `GROQ_API_KEY` | Cloud speech at `api.groq.com`, sent as `Authorization: Bearer` to that host only. Required there, as for Azure OpenAI. |
| `SPEECH_API_KEY` | Cloud speech at any other address (a custom server), sent as `Authorization: Bearer` when set. Optional. Never sent to the OpenAI, Azure OpenAI or Groq hosts (on any port: their own host on another port gets no key at all). |

## Cleanup

Cleanup removes filler words and false starts, fixes punctuation and casing, applies spoken commands ("new line",
"comma", "scratch that"…) and uses Australian English spelling. Three tiers:

1. **`local` (the default): a small model running on this PC** through llama.cpp (LLamaSharp), with Vulkan on a
   GPU, else the CPU. Nothing leaves the machine. Choose the model in the tray panel or Settings > Models ("Cleanup models" above).
   Greedy decoding, the built-in prompt plus three short worked examples, and a token limit of 1.5 × the
   dictation's tokens + 32 (64–2048). The prompt and examples are evaluated once when the model loads and kept in
   the model's cache, so each dictation only pays for its own words and the reply.
2. **`none`: the rule-based tidy.** Instant, and built never to change meaning, so it only touches what cannot be
   content: the standalone hesitations um, umm, uh, uhh, erm, ah, hmm (never "er" or "mm", never right after a
   number); "you know", "I mean", "sort of", "kind of", "like" only between a sentence start or comma and a
   following comma ("you know, we…"); immediate repeats ("the the"; "that that" and "had had" are kept); "new line"
   and "new paragraph" only as a clause of their own ("First point. New line. Second point."), never inside one
   ("a new line of credit"); the first letter of each sentence and a standalone "i" (tokens with a full stop inside,
   such as "i.e." and "a.m.", are left as they are); spaces before punctuation; and a final full stop. It never
   fixes misheard words or adds commas. Example: "um so the quick brown fox uh jumps over the lazy dog you know" →
   "So the quick brown fox jumps over the lazy dog you know." ("you know" stays: it is not between commas).
3. **The fallback, for every provider:** if the model is not loaded yet, runs past its deadline, or its reply fails
   the checks below, the rule-based tidy is pasted and the reason is logged (never the text). A cleanup failure never
   stops the paste.

**`off`: no clean-up** (Settings > Models > **Off**, or **Off (paste what was heard)** in the Advanced view
and the tray panel). The text is pasted as whisper wrote it: only the spaces are tidied (the ends trimmed, and runs of
spaces, tabs, CR and LF made one space; other whitespace inside the text, such as a no-break space, is kept). The tray
panel's models line and the icon's tooltip say "No clean-up" where the clean-up model would be. Nothing else changes: no filler removal, no capitals, no full stop, and
spoken commands are not applied, so saying "new paragraph", "new line", "comma" or "scratch that" types those words.
No cleanup model is loaded and nothing is sent anywhere, so Settings > Privacy and the tray panel still say "Nothing
leaves this PC". Your corrections, the profanity filter and snippets still run after it, as they do with every
clean-up. Example: "um so the quick brown fox uh jumps" is pasted as "um so the quick brown fox uh jumps". `none` is
still the rule-based tidy.

**Deadline:** 10 s plus 0.1 s per dictated word, at least `llm.timeout_ms` (15 s by default) and at most 60 s. It is
a hard limit: Dictum stops waiting then, and tells the model to stop, even if it has not stopped. Before calling the
local model, Dictum estimates the time from the speed measured at warm-up (a short cleanup run right after the model
loads, run twice the first time with only the second run counted, since the first pays one-off costs) and on the
latest dictations, including ones stopped at their deadline; if the estimate is over the deadline it skips the model,
uses the rule-based tidy and the pill says "Polishing… skipped (long dictation)". A skipped dictation measures nothing,
so after three in a row the next one runs the model anyway and measures its speed again; while the model stays too
slow that allowance doubles (6, 12, at most 24), and a dictation the estimate lets through puts it back to three.

A reply is rejected if it is empty; has more than 1.6× or fewer than 0.4× the dictation's words (dictations of 8+
words; spoken commands such as "new line" and "comma" and hesitations are not counted, and the lower bound is
measured after any clause you retracted with "scratch that"); starts with commentary ("Here is", "Sure", "Cleaned
text:") or contains framing such as "I'm sorry" or "let me know" that you did not say (whole words, curly quotes
included); repeats five or more words of the system prompt actually sent (a custom `llm.prompt_file` included);
contains a chat-template token (`<|im_end|>`, `<end_of_turn>`…); drops a "?" you dictated; shares fewer than half its
words with the dictation (replies of 6+ words); is in a script the dictation did not use (for example Chinese for
an English dictation); ends with a line, a bracketed remark or (in a reply of two sentences or more) a sentence you did not say, or contains one of the built-in example
sentences; drops a "not", "no", "never" or a contraction such as "can't" that you said; drops or changes a number you
said; drops a word on the profanity list (unless `profanity.mode` is `remove`, which removes them afterwards anyway);
keeps most of a clause you retracted with "scratch that", or loses the words that replaced it; keeps fewer than 60%
of the dictation's content words (8+
of them); or keeps less than a third of the content words of any one dictated sentence (4+ of them). The content
checks exist because qwen2.5-0.5b and qwen2.5-1.5b both returned replies with whole sentences missing that every
other check accepted; they are measured against the dictation with each retracted clause removed, so a correct
"scratch that" is not counted as a loss but the rest of the dictation is still protected. Dictated quotation marks
and a final dictated line break are kept. British/Australian and US spellings of the same word (colour/color,
analyse/analyze, centre/center, catalogue/catalog, licence/license) count as one. In hands-free, the words carried from
the last part are context, never dictation, and a reply is also rejected when it repeats two neighbouring carried words
(one of them a content word) more often than this part said them; each pair of neighbouring words of this part that
sounds like them counts as said once, with or without fillers such as "like" or "you know" between them.

**Esc** during "Polishing…" cancels the cleanup and pastes nothing; Dictum is ready again at once. Measured with
the long test dictation (184 tokens) and qwen2.5-1.5b, cancel 200 ms in: on Vulkan the model stopped 2 ms after
Esc; on a CPU 1.46 s after, because llama.cpp cannot interrupt the step that reads the dictation (before the prompt
was cached this was 3.9 s).

### Optional cloud polish

Off by default. Cloud clean-up can use Anthropic, OpenAI, Google Gemini or any OpenAI-compatible server with your own
key, behind a consent step and a warning. Each sends the raw transcript and the system prompt, which includes your
dictionary terms (and in hands-free up to 10 words of what you said in the last part), to another program after every
dictation. `offline_only` refuses Anthropic and any server not on this machine.

**Switching it on takes an explicit consent step (SPEC §22).** Settings > Clean-up > Cloud clean-up: choose the
service, the server address and the model, then **Use cloud clean-up…**. A dialog says what is sent (every
dictation's text, never the audio, with the clean-up instructions and your dictionary words) and to which host; only
**Allow cloud clean-up** switches it on and records the consent in `consent.json` in the settings folder (provider, host,
time, app version); only that dialog writes the file. Editing settings.json alone never switches it on: without a
matching consent the local model is used and the log says why (an `llm.cloud_consent` key there is ignored). **Switch cloud clean-up off** returns to the local model and withdraws the consent. A plain `http://`
address that is not on this PC is refused outright (in the dialog, in settings.json, and by the cleaner itself), so
dictations never travel unencrypted.

A settings.json from the first build that says `anthropic` therefore runs the local model after this upgrade, until
the consent step is done in the app.

**Anthropic:** choose Anthropic in the consent step and set `ANTHROPIC_API_KEY` (environment variable, or `.env`).
Model `claude-haiku-5-5` (or another Anthropic model id), thinking off, effort low. Missing key → nothing is sent.
Agreed to in the consent step, the rule-based tidy runs and Settings shows the waiting line; never agreed to (a
first-build settings.json), the local cleanup model is used (logged), or the rule-based tidy if that model is not downloaded.

**Your own server (Ollama, LM Studio, a company gateway):** choose "Ollama on this PC" or "Custom server (OpenAI-compatible)", for example

```json
"llm": { "provider": "openai-compatible", "model": "llama3.2", "base_url": "http://localhost:11434/v1" }
```

A server on this PC (`localhost`, `127.0.0.1`, `::1`) needs no consent step, because Dictum's own request does not
leave the PC; a server elsewhere needs `https://` and the consent step. Any OpenAI-compatible
`POST {base_url}/chat/completions` endpoint works; LM Studio's server is typically `http://localhost:1234/v1`
(recalled, not verified) and Ollama's is `http://localhost:11434/v1` (docs.ollama.com, read 16/10/2026). A server on
this machine is allowed under `offline_only` too: the request goes to the server directly, never through a proxy, and
redirects are not followed. Whether the transcript stays on this PC then depends on that server: a proxy or company
gateway running on this machine may forward it elsewhere, and Ollama can serve its cloud models once you sign in to
it. So the tray panel and Settings never say "Nothing leaves this PC" while such a server does the clean-up. Each key
goes as a bearer token to its own service only: `OPENAI_API_KEY` to OpenAI's server and `GEMINI_API_KEY` to Google
Gemini's (required there: without it nothing is sent), none to Ollama on this PC, and `LLM_API_KEY`, when set, to any
other address.

## Mode presets

The tray panel's **Mode** (Light | Full, in the Advanced view; also Settings > Mode) picks a whole setup in one click. Choosing a preset writes plain values into settings.json (you can
see and edit them) and, if a model it needs is not downloaded, asks once (names, total size, huggingface.co) and
downloads it with the usual checks before switching (after any download already running); refused with a message
under `offline_only`. "Custom" is ticked when settings.json matches neither preset. Which column applies depends on
whether a discrete GPU was found: a PC with integrated graphics only gets the CPU column, because every GPU figure
here was measured on a discrete card (`--selftest` lists the Vulkan devices).

| Preset | Discrete GPU | CPU, or integrated graphics only |
|---|---|---|
| Light | `base.en` + rule-based tidy (`llm.provider = none`) | `base.en` + rule-based tidy |
| Full | `large-v3-turbo` + `qwen2.5-1.5b` | `small.en` + `qwen2.5-1.5b` |

A preset sets only `whisper.model`, `llm.provider` and (Full) `llm.model`; a beam size chosen by calibration, the
profanity filter and everything else stay as they are and do not turn the preset into Custom. `--selftest` prints the
active preset.

## Making it hear you better

Four plain text files in `%APPDATA%\Dictum` (or your `--config` folder) personalise Dictum. Each is one entry per
line, `#` starts a comment, and saving the file takes effect at once. Deleting `dictionary.txt`, `corrections.txt` or
`snippets.txt` switches that feature off; deleting a `profanity.txt` override returns to the shipped list (set
`profanity.mode` to `off` to stop filtering).

**Personal dictionary (`dictionary.txt`).** Names, products and jargon you want spelt your way, one per line (tray panel >
Dictionary: add a word, or Open dictionary file). They are used twice: as whisper's initial prompt (a comma-separated
list, which nudges whisper towards those spellings; whisper's prompt holds about 224 tokens (recalled, not verified), so with a long list
only the most recently added terms are used, newest last) and as a line in the cleanup prompt ("Known names and terms,
keep their spelling: …"). An empty file changes neither prompt. Observed here (15/10/2026, small.en, a synthesised
sentence): without a dictionary whisper wrote "in zero … to Kinney"; with `Kynni` and `Xero` in it, "in Xero … to
Kynni". With a GPU, the first transcription with a dictionary prompt can compile GPU shaders (26.1 s here, and 26.6 s
again after the build was re-published to the same folder; 0.4–0.5 s afterwards). Dictum pays this in a silent
warm-up when the model loads and again whenever the dictionary changes; a dictation started during that warm-up waits
for it.

**Corrections (`corrections.txt`).** Lines `heard => written`, e.g. `kinney => Kynni`. Whole words only, any case
("ER" never touches "her"); the longest match wins where two overlap; one correction's output is never corrected
again. A lowercase replacement for a capitalised word at a sentence start keeps the capital; a replacement with
capitals ("Kynni", "iPhone") is used exactly as written, and a lowercase one replaces a capitalised word in lower
case unless it starts a sentence. A rule matches whole words, including the stem of a contraction ("don't", "it's").
They run on the text just before it is pasted, after the cleanup and before the profanity filter (so a correction
cannot bring back a word the filter hides), with every provider including the rule-based tidy.

**Snippets (`snippets.txt`).** A short phrase you say and the text Dictum pastes in its place: `my email =>
kyle@example.com`, `my calendly => https://calendly.com/example/30min`, `my signature => Kyle Manson\nKynni Pty Ltd`.
Tray panel > **Snippets** (or **Add a snippet…** in the Dictionary window) writes a line for you; the file is plain
text like the others. The same whole-word, any-case matching as corrections ("my emails" is left alone; "my email
address" is expanded), the longest phrase wins where two overlap, and a later line for the same phrase replaces the
earlier one. The difference from a correction is that the text is pasted *exactly as written*: a URL at the start of a
sentence stays lower-case, and in the file `\n` stands for a line break and `\\` for a backslash (the Add window writes
both for you, so `\\server\new-clients` typed there is pasted as typed). In Type mode (`insert.method = "type"`) each
line break is typed as Enter, which sends the message in some chat apps. The Add window refuses a phrase with a line
break in it, or one the profanity filter would change (the filter runs first, so that snippet could never match).
Snippets run last, after the corrections and the profanity filter, so nothing rewrites a word inside a pasted
address, and a correction can repair a misheard phrase first (`calendar lee => calendly` makes "my calendar lee"
reach the `my calendly` snippet). The clean-up model never sees the pasted text, only the phrase you said; if it tends to
rewrite your phrase (for example "my e-mail"), add that form as a second line. Say the phrase as whole words with
nothing attached, and keep it something you would not say by accident.

**Fix last dictation** (tray, or an optional `fix_hotkey`). Opens a small always-on-top editor with the last text
Dictum pasted (kept in memory only, never written to disk); in hands-free, Fix last dictation holds the last part typed.
**Copy**; **Paste fix** (switches back to the window the
dictation went to and pastes through the normal insert path: select the wrong text there first to replace it);
**Save corrections** (compares your edit with the original word by word and lists each replaced phrase as "Add
correction: X => Y", unticked; only the ones you tick are added). Added or deleted words and punctuation or
capital-letter changes are not offered as corrections. The fix_hotkey is ignored while a dictation is recording or
processing, because a window taking focus then would receive the paste.

### Calibration

Tray panel > **Calibrate my voice**: *Calibration measures which installed model hears you best and suggests dictionary
entries. It does not train or change the model.* Nothing is uploaded and it works under `offline_only`.

1. **Record** the ten sentences (everyday business English with numbers, a date, an amount, an e-mail address and a
   few names that come back in more than one sentence; shipped as `calibration-sentences.txt` beside `Dictum.exe`).
   Recordings are kept in `%LOCALAPPDATA%\Dictum\calibration` (with `takes.txt`, which notes the sentence each one was
   recorded for, so a reworded sentence after an upgrade asks to be recorded again) so you can re-run the test after
   adding a model (the window does not print that path, which names your Windows account: **Open the recordings folder**
   opens it); **Delete recordings** removes them all, including any left by a save that failed. A take stops by
   itself after 30 s and is discarded (each sentence takes about 5–10 s), and one whose microphone fails is discarded
   too; either way the row says to record it again. With no microphone listed, **Record** is greyed out and the window
   says so under it; plug one in and come back to the window, and Record works again.
2. **Run test** (not while a take is being recorded): every installed speech model transcribes every recording twice,
   with greedy and with beam-search decoding (5 beams), using your dictionary prompt. For each combination you see the
   word error rate (WER: words substituted, dropped or added, against the script, after lower-casing and removing
   punctuation, with written and spoken forms of the same thing counted as one: "20" and "twenty", "14th" and
   "fourteenth", "14 March", "March 14" and "the fourteenth of March", "10:30", "10.30", "at 1030" and "ten thirty"
   with or without "a.m.", "12%" and "12 per cent", "$4,250.00" and "four thousand two hundred and fifty dollars",
   "e-mail" and "email", "I'll" and "I will", "A.U." and "a u" after "dot"; a different amount, time or rank is still
   an error, so "$42.50" is not 4,250 dollars and "fourteen" is not "fourteenth") and the average seconds per clip on
   this PC. Each row appears as soon as it is measured, and **Cancel test** keeps the rows already finished. Results
   always belong to the recordings on disk: recording a sentence again clears them, and Delete recordings is not
   available during a test. **Recommended** is the lowest WER among the combinations at or under 2.5 s per 10 s of
   audio (a recording shorter than 10 s counts as 10 s, because whisper.cpp costs about the same for any clip up to
   30 s; the window says so). **Use this** writes `whisper.model` and `whisper.beam_size`. Models not installed are
   listed with their sizes and **Download and include**, which asks first (the download is added to the test; your
   current model stays in use), then shows "Downloading…" until it arrives, waiting behind any download already
   running.
3. **Suggestions**: a word of the script that the recommended combination got wrong in at least two of its
   appearances (and at least two thirds of them) is offered as "Add to dictionary: <word>", and, when it was heard the
   same way every time and a correction would change what whisper wrote, "Add correction: <heard> => <word>". All
   unticked.
4. **Grammar assist** (also a tray toggle, `llm.grammar_assist`): for speakers whose first language is not English,
   the cleanup prompt gains "The speaker may not be a native English speaker: also fix articles, tenses, prepositions
   and word order while keeping every fact and name exactly as dictated." The reply checks are unchanged, so a reply
   that drops a fact still falls back to the rule-based tidy. It needs a cleanup model (it does nothing with
   `llm.provider = none`).

Measured with the calibration engine on this development machine (15/10/2026, `CalibrationIntegrationTests`): the ten
sentences read by Windows' synthesised voice (System.Speech), 58.3 s of audio, no dictionary. A synthesised voice is
not a person; your own figures will differ, which is the point of calibrating. The Vulkan run is from after the fix
batch, scored with the current rules; the CPU timings are Batch 2's (timing does not depend on the scoring rules).

| Model | Decoding | WER, Vulkan | Seconds per clip, Vulkan | Seconds per clip, CPU forced |
|---|---|---|---|---|
| tiny.en | greedy | 6.4 % | 0.13 | 0.90 |
| tiny.en | beam search (5) | 4.0 % | 0.24 | 1.07 |
| small.en | greedy | 0.8 % | 0.33 | 6.27 (over the ceiling) |
| small.en | beam search (5) | 0.8 % | 0.41 | 7.70 (over the ceiling) |

Recommended on Vulkan: small.en greedy (it ties beam search at 0.8 % and is faster). Under Batch 2's rules small.en
greedy scored 2.4 % and beam search was recommended, only because its correct "14 March at 10.30" counted as two
errors against "the fourteenth of March at ten thirty". On the CPU, small.en is over the latency ceiling; Batch 2's CPU
run (old rules, not re-run) recommended tiny.en with beam search and suggested "Add to dictionary: Kowalski" (tiny.en
misheard it differently each time, so no correction was offered).

**Over Remote Desktop**, the microphone reaches Dictum through Remote Desktop's audio redirection, which may compress
it (not measured here); recognition can be less accurate than with a local microphone. Calibrate in the same setup you
dictate in, so the recommendation reflects it.

## Profanity filter

`profanity.mode` (Settings > Clean-up > Profanity filter): `off` (default), `mask` (first letter kept, the rest starred: "f\*\*\*"),
or `remove` (the word is dropped, with an `'s`, `'re`, `'ll`, `'ve` or `'d` joined to it, and the double space, stray
comma or lost sentence capital it leaves is tidied; a word that was a sentence on its own, "Shit!", goes with its
punctuation). Whole words only, any case, so "class", "assess" and "Scunthorpe" are never touched. The word list is
`profanity.txt` beside `Dictum.exe` (a modest list of common English profanities with plural, -ing and -ed forms
listed; words that are also ordinary words or names, such as "bloody" or "Dick", are left out). A `profanity.txt` in
`%APPDATA%\Dictum` replaces it completely. The filter runs last, after the cleanup and the corrections. A cleanup
model may drop a swear word by itself (qwen2.5-1.5b turned "send the damn report" into "send the report" once here),
so with `off` or `mask` a reply that drops a word on the list is rejected and the rule-based tidy is pasted instead:
the word you said survives, to be masked or left as it is.

## Privacy

**Nothing leaves the machine by default.** Speech recognition and cleanup both run on this PC. Dictum opens a
network connection only in these cases, and only to these hosts:

| When | Host |
|---|---|
| Only when you ask for a model download (the Welcome to Dictum window's speech step, a choice only when base.en is not on the PC; the tray panel's Speech model, Clean-up or Mode, Settings > Models, calibration's Download and include, missing-model prompt, `--download-model`, `--download-cleanup-model`, `install.ps1 -Model`, `scripts\download-model.ps1`) | `huggingface.co`, which redirects to its CDN (observed `us.aws.cdn.hf.co` on 15/10/2026; the CDN host can vary by region). The download names the file; nothing about you or your dictation is sent. Only `download-model.ps1` can send something more: your Hugging Face token, and only for a gated model. |
| Only when you click **Check for updates** in Settings > About (the direct download; the Store build has no such button, because the Store updates it) | `dictum.kynni.au`: one HTTPS GET of `/version.json` (the latest version number and its release notes), with no query string and no header of Dictum's own, so nothing about you or your dictation is sent. It goes through its own client, built like the model-download one (the `offline_only` guard, TLS 1.2), follows no redirect, does not retry, and gives up after 10 seconds. If a newer version is out, **Open the download page** opens https://github.com/kynniau/dictum/releases/latest in your browser. |
| After each dictation, only if you switch cloud clean-up on in Settings > Clean-up, agree to the host in the consent step, and a key is set | `api.anthropic.com` (the raw transcript and the system prompt, which includes your dictionary terms and, in hands-free, up to 10 words of what you said in the last part: taken before corrections, the profanity filter and snippets, so never their text), or the host in `llm.base_url` if you set one. The `ANTHROPIC_BASE_URL` environment variable is deliberately ignored, so a variable set for other tools cannot redirect transcripts or the key. |
| After each dictation, only if you set up an `openai-compatible` server (one off this PC also needs the consent step and `https://`) | the host in `llm.base_url` (the same text). With `localhost`, Dictum's request stays on this PC; what that server does with it is up to the server. |

Nothing else: no telemetry, analytics, crash reports or automatic update checks. Dictum configures no OpenTelemetry
exporter, and turns off the Anthropic SDK's own tracing, so transcripts cannot reach a tracing exporter configured for
other programs.

**`offline_only: true` stops Dictum itself from connecting to any other machine.** With it set, Dictum refuses
every download, in the app and on the command line, with a message saying why, refuses Check for updates without
sending anything, and refuses the `anthropic` provider and any `openai-compatible` server that is not on this
machine, falling back to the rule-based tidy with a notification.
Settings > Privacy shows the switch, Settings > About says "Offline only", and the tray panel's privacy line says "Nothing leaves this PC" (unless an OpenAI-compatible server on this PC does the clean-up: it may forward the text, which Dictum cannot see). Install models on such machines with `install.ps1 -ModelsFrom <folder>`.
Behind those messages, every HTTP client in Dictum sends through one guard that refuses any request to a host off
this machine while `offline_only` is on (read per request, so it follows a settings change), and a download in
progress stops at its next chunk when the setting is turned on. A request to a server on this machine never goes
through a proxy (a proxy is another machine), and the cleanup providers' client does not follow redirects (a redirect
is an error and the rule-based tidy is used), so Dictum never sends a transcript to a host other than
`llm.base_url`. A server on this machine can still forward what it receives: a local proxy or company gateway may
send it on, and `offline_only` cannot see that.

- **Audio** of a dictation is kept in memory only while recording and processing, then discarded. It is never written
  to disk. The only audio Dictum stores is what you record on purpose in the calibration window: ten WAV files in
  `%LOCALAPPDATA%\Dictum\calibration`, kept until you press **Delete recordings** (calibration window, or Settings > Storage; or uninstall with `-RemoveData`),
  never uploaded, and used only by that window.
- **Your own text files** in `%APPDATA%\Dictum`: `dictionary.txt`, `corrections.txt`, `snippets.txt` and an optional
  `profanity.txt` override. Plain text you can read, edit and delete; nothing else reads them. Corrections, snippets
  and the profanity list never leave the PC (a snippet's text, such as an address or a link, is added after the
  clean-up, so even a cloud clean-up never receives it). The dictionary goes into the cleanup prompt, so with `anthropic` or an `openai-compatible`
  server its terms are sent with every dictation (to this PC's own server, or to the cloud).
- **The last dictation** is kept in memory for "Fix last dictation" and is gone when Dictum quits. It is never written
  to disk or the log.
- **Transcripts** are not stored. With `insert.method = "paste"` and `restore_clipboard = false` the last transcript
  stays on the clipboard so you can paste it again. It is marked with the `CanIncludeInClipboardHistory` and
  `CanUploadToCloudClipboard` formats (value 0) so Windows keeps it out of clipboard history (Win+V) and does not sync
  it to your other devices (https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats); set
  `insert.clipboard_history: true` to allow both. Clipboard managers that ignore those formats can still see it.
- **Logs** (`%LOCALAPPDATA%\Dictum\logs`, one file per day, newest 14 kept) contain timings, character counts,
  device names, the runtimes and models, which cleanup tier ran and why a reply was rejected, settings warnings and
  errors, how many dictionary terms, corrections, snippets and profanity words were loaded (never the words), and
  calibration's WER and timing figures. Transcript text (including what a snippet pasted) and calibration text only if
  `log_transcripts` is `true`. API keys are never
  logged: any occurrence of a configured key is replaced with `[redacted]`.
- **API keys** come only from environment variables or `.env`; never from or into settings.json, the windows, or error
  messages. With the default `local` provider no key is needed for cleanup.
- **GPU-load markers** (`%LOCALAPPDATA%\Dictum\gpu-load-speech.txt`, `gpu-load-cleanup.txt`) hold the path of a model
  while it loads on the GPU, and nothing else. GPU loads run one after the other, so this run writes at most one file at
  a time; a file left by an earlier start can sit beside it. One stays whenever Dictum ends during that load without quitting normally: the GPU ending it, but also Task Manager ending
  it, a power loss, or Windows signing out or shutting down before Dictum quits. Dictum cannot tell these apart, so the
  next start puts that model on the CPU when its gpu setting is `auto` (see Troubleshooting). To let the model try the
  GPU again, quit Dictum and delete the file, or set its gpu setting to `vulkan`. Settings > Storage > Delete all app
  data and Remove Dictum completely delete them.
- **Model files** are data, not programs (see "Cleanup models"), and downloads are checked against the pinned SHA-256
  where the catalogue has one; Gemma files are size-checked only.
- **Report a problem** (Settings > About) opens an email to hello@kynni.com.au in your own mail app, with Dictum's
  version, the platform and the models filled in. Dictum sends nothing itself and never attaches what you dictated;
  GitHub issues (https://github.com/kynniau/dictum/issues) is the other way to reach us.
- **Send feedback or suggest a feature** (Settings > Help) and **Email support** are emails in the same way: your mail
  app opens a new message to hello@kynni.com.au; Dictum sends nothing, and no path, dictionary word or dictated text is
  included (see "Feedback and feature requests").

### Storage and removing Dictum

Settings > Storage (SPEC §17) shows where everything is, the model folders in the order Dictum looks in them (see
"Model folder"), the free space on the drive downloads go to, every model file with its folder and size, and the total.
Every deletion says how much it frees and asks once; sizes are read from the disk, not from a stored count.

- **Delete** beside a model removes the `.bin` or `.gguf`, its unfinished `.part` and its verified record. The model
  in use cannot be deleted: the dialog offers to switch to the recommended other model on this PC first (base.en, which
  comes with Dictum, counts while it passes its check: it is switched to, never deleted). When Dictum has another copy of
  the same model to go on with (a base.en downloaded by version 1.0.x, beside the one that comes with Dictum), the copy in
  use can be deleted: it asks "Delete this copy of base.en (<size>)? Dictum then uses the copy that came with it.", then
  loads the other copy. A model you chose by path from elsewhere is never deleted, only forgotten, and the models that
  come with Dictum have no Delete: they are part of the program. A download that would leave under 2 GB free warns
  first; one that would leave under 200 MB is refused.
- **Delete calibration recordings**, **Delete logs**, **Delete all app data** (models, recordings, logs and the
  verified record; settings, dictionary and corrections are kept unless "Including settings, dictionary and
  corrections" is ticked). None of them touches `.env` or the program folder. A model folder you chose outside your
  user folder, which other users may share, is left alone by **Delete all app data**, and its question says so. Dictum
  keeps no dictation history, so there is nothing else to clear.
- **Remove Dictum completely** deletes every model, recording, log, settings.json, `consent.json`, dictionary,
  corrections, the `.env` files, whatever the migration left in Dictate's old folders (see "Moving from Dictate"; a
  Store copy never touches those), the Start menu shortcut, the launch-at-login entry and the program's own files,
  then quits. The first warning lists every file with its size and the total, the program's files included; the
  second needs **DELETE** typed and says there is no undo. Junctions and symbolic links inside the folders it empties
  are removed as links: what they point to is never listed or deleted. The program's files go last, in a step that
  waits for Dictum to exit (a running program cannot delete itself); it is Windows PowerShell, started by its full path
  (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`, never by name), and without it the program's files
  stay and the first warning says so. When the setup installed this copy (Dictum's own Settings > Apps entry names
  this folder and its `unins000.exe`, and `unins000.dat` and `manifest.txt` are there), that step runs the setup's
  uninstaller silently, which removes what the setup installed and created, the Settings > Apps entry included; the
  first warning lists `unins000.exe` and `unins000.dat`. A silent uninstall keeps user data, but by then Dictum has
  deleted it. Any other `unins000.exe` (one beside a portable copy, or another program's) is never run. Otherwise
  the step deletes only the files `manifest.txt` lists, written when the build was published, that are still there
  with the same name and size and are not reached through a junction or symbolic link; then the folders that leaves
  empty; then the program folder, only if nothing else is in it. So a copy unzipped into Downloads takes nothing else
  with it. A program folder without `manifest.txt` (a build from source, or one older than 16/10/2026) is left in
  place and the first warning says so; a drive root or a build folder inside a source checkout is never touched. The
  folder path reaches that step as data (an environment variable the step checks), never as script text, so no folder
  name can change what it runs.
  Afterwards, of what Dictum wrote, only the per-user temp folder (which Windows clears itself) may hold traces; in
  the program folder, files the setup or `manifest.txt` did not ship stay, and in a portable copy so does a shipped
  file that has changed since. What Windows or a GPU driver keeps about programs that have run is theirs, not
  Dictum's, and is not removed: for example the drivers' shader caches (Intel's under
  `%USERPROFILE%\AppData\LocalLow\Intel\ShaderCache`, observed during this lane's tests).
  The list comes from one path registry in the code, and a test checks it against every file the app writes.

### Moving from Dictate

This app was called Dictate until 16/10/2026. On its first start Dictum moves Dictate's files across once (a Store copy copies them instead: see below):
`%APPDATA%\Dictate` (settings.json, .env, dictionary.txt, corrections.txt, profanity.txt) to `%APPDATA%\Dictum`,
and `%LOCALAPPDATA%\Dictate` (models, calibration recordings, logs and the verified record) to
`%LOCALAPPDATA%\Dictum`. Paths in settings.json that pointed into the old folders are rewritten, and the verified
record follows the model files, so they are not hashed again. With `--config` the settings folder is left alone.

- **Dictate must not be running.** If it is (its single-instance lock is held) and there is something to move, Dictum
  shows **Quit Dictate first** with **Retry** and **Quit Dictum**, and does nothing else until Dictate has quit; the
  command line prints the same and exits 1.
- **A file Dictum already has is never replaced.** The Dictate copy is skipped, stays in Dictate's folder, is listed
  in a notification and the log, and is deleted by **Remove Dictum completely** with the rest of those folders (an
  API key in Dictate's `.env` included).
- A file that cannot be moved is left where it is, named in a notification and the log, and tried again at the next
  start.
- **A Store (packaged) copy copies instead of moving.** Windows writes a packaged app's new files under `AppData` to
  a per-package location and cleans them up when the app is uninstalled (Microsoft Learn, "Understanding how packaged
  desktop apps run on Windows", read 16/10/2026), so a move would leave your only copy where an uninstall deletes it.
  The packaged Dictum copies Dictate's files, leaves Dictate's folders as they were and says so in a notification;
  it does not offer to remove the earlier build, and its **Remove Dictum completely** never lists or deletes
  Dictate's folders.
- After a successful move Dictum offers to remove the earlier build itself: its launch-at-login entry (the Run value
  named Dictate, only when it starts a `Dictate.exe`), its Start menu shortcut, and its program folder, which is
  deleted only when it is exactly `%LOCALAPPDATA%\Programs\Dictate` and holds `Dictate.exe` (where Dictate's
  `install.ps1` put it). **Keep it** leaves all three.

The test variables accept their old `DICTATE_` names, and `DICTATE_MODEL_PATH` still works for the app.

### Store and direct-download builds

Dictum detects whether it runs as an installed package (`GetCurrentPackageFullName`, SPEC §18.2): Settings > About
says which. Packaged, Settings > About has no Check for updates button and says "Updates come from the Microsoft
Store." Packaged, launch at login uses the package's startup task (it can also be turned off in Task Manager >
Startup apps, which then wins: Microsoft Learn, "Integrate your desktop app with Windows by using packaging extensions", read 16/10/2026, says a task the user disables there cannot be re-enabled by the app; not yet seen on a package); Settings > Storage shows the real on-disk folders, because Windows redirects a
package's app-data writes to a per-package folder (`%LOCALAPPDATA%\Packages\<family>\LocalCache\…`: the layout is
recalled, not verified); "Remove Dictum completely" deletes the data it owns, never Dictate's folders, and then opens
Settings > Apps, where Windows uninstalls the package. Installing both builds on one PC is unsupported, and each warns when it finds the
other. `scripts\package-msix.ps1` builds the package (see "Build from source" and
[docs/TESTING-MSIX.md](docs/TESTING-MSIX.md)); on 16/10/2026 it had not yet run as an installed package.


## Command line

```
Dictum.exe                                   tray mode
Dictum.exe --transcribe <file.wav> [--no-llm] print "raw: ..." (and, unless --no-llm, "cleaned: ..." and "final: ...",
                                              the text the tray app would paste after the corrections and the
                                              profanity filter), with the dictionary in both prompts; exit 0, or 1
                                              when settings.json cannot be read
Dictum.exe --selftest                        speech and cleanup models with their runtimes, recommendations, the
                                              active Mode preset, decoding, personal file counts, offline_only,
                                              microphones, hotkey, key presence (never values); exit 0/1
Dictum.exe --download-model <name>           download a speech model (e.g. small.en), verify size and SHA-256; exit 0/1
Dictum.exe --download-cleanup-model <name>   the same for a cleanup model (e.g. qwen2.5-1.5b)
Dictum.exe --bench-cleanup <file.wav|file.txt> every downloaded cleanup model (and --cleanup-model's file) over the
                                              same input: output, time, validator verdict, side by side (a .wav is
                                              transcribed first); an unreadable file exits 1
Dictum.exe --print-models-dir                the folder the app downloads models to (models.folder, else its
                                              earlier name whisper.models_dir, else the default); install.ps1 uses it
Dictum.exe ... --model <name|path>           use this speech model for this launch only
Dictum.exe ... --cleanup-model <name|path>   use this local cleanup model for this launch only
Dictum.exe ... --config <dir>                use another settings folder
Dictum.exe --exit-after <seconds>            tray mode, then quit exactly as Quit in the tray panel does (scripted checks)
```

A download of a file that is already present in any of the model folders (so `--download-model base.en` when base.en
comes with Dictum) and matches its hash does nothing and exits 0. With `offline_only`
on, or with a settings.json that cannot be read, both download commands refuse and exit 1; for a gated model they
print the login message and exit 1. `--download-model` also accepts a whisper.cpp name outside the catalogue: it
warns, and that file has no size or hash to be checked against. A value is never another flag or empty (PowerShell
5.1 drops an empty `$variable`, so `--config $empty --selftest` is refused rather than read as a folder called
`--selftest`), and two different modes in one launch are refused.

`Dictum.exe` is a Windows GUI program, so PowerShell does not wait for it unless you pipe the output:
`.\Dictum.exe --selftest | Out-Host`. Diagnostics go to stderr, results to stdout.

## Troubleshooting

- **Nothing is pasted.** If the target window runs as administrator, see "Elevated windows" above. Try
  `"insert": { "method": "type" }` for apps that block paste. Legacy console windows may not honour Ctrl+V; use
  `type` there too.
- **Wrong microphone.** Run `--selftest` to list devices and set `audio.device` to part of the name.
- **"No sound from mic" (pill), "Dictum is not hearing anything" (balloon).** The microphone opened but gave no sound at
  all for 2 seconds after the key was pressed: every 20 ms of audio was silent (digital silence, or white noise
  at a 48 kHz microphone below about −91 dBFS in stereo or −94 dBFS in mono), or no audio arrived. Check that the
  microphone is plugged in, not muted (its own switch, and Windows Sound settings), is the one Dictum uses
  (`--selftest`, `audio.device`), and that Windows allows desktop apps to use the microphone (Settings > Privacy &
  security > Microphone). On a remote or virtual desktop (Remote Desktop, Azure Virtual Desktop) the
  microphone must be redirected from your own PC: see "Remote Desktop microphone" under "Deploying to staff machines and
  Azure VMs". Once the microphone has been heard in a dictation, later pauses never bring the warning back. A microphone
  whose own noise suppression sends exact silence between words can show it until you start speaking (not seen; inferred
  from how the check works). The log line is `No sound from the microphone 2000 ms into the recording: peak block RMS … over … block(s) of 20 ms`
  (0 blocks means no audio arrived at all; the peak is a level, never what was said).
- **"No microphone found" (balloon).** The microphone could not be opened and Windows lists no microphone at all. Plug
  one in, or on a remote or virtual desktop turn on microphone redirection, then press the hotkey again. On Windows
  Server, also check the Windows Audio service (see "Deploying to staff machines and Azure VMs").
- **"Mic error".** Check the microphone privacy settings above and that the device is enabled in Sound settings. If
  it appeared mid-dictation, the device was lost (unplugged, out of battery); what was recorded until then was still
  transcribed and pasted.
- **Slow transcription.** Check which runtime loaded (the tray icon's tooltip, the tray panel, `--selftest`, or the log line
  `Whisper model ... loaded ... on runtime ...`). On `CPU`, pick `small.en` or `base.en` in the tray panel or Settings > Models
  (see the measured table above). The Vulkan runtime needs a GPU driver with Vulkan support; without one Dictum
  uses the CPU runtime, which is expected, not an error.
- **Dictum closes at start, before its tray icon appears** (version 1.1.1, on a PC whose Vulkan runtime cannot load: no
  Vulkan loader, `vulkan-1.dll`, or a GPU driver whose Vulkan files fail to load). Running `Dictum.exe --selftest` from a
  command prompt shows `GGML_ASSERT(prev != ggml_uncaught_exception) failed`. Fixed after 1.1.1 (docs\DECISIONS.md,
  21/10/2026): such a PC now starts on the CPU runtime. To keep Dictum off the GPU entirely, put
  `{ "whisper": { "gpu": "cpu" } }` in `%APPDATA%\Dictum\settings.json`; from the fix on, this holds on the very first
  start too (before it, the first start's GPU check ignored it).
- **"model error" with a model that is there**, and the `whisper runtime` line of `--selftest` says `model load
  failed: Exception: Failed to load native whisper library` (the `cleanup backend` line: `load failed:
  TypeInitializationException: The type initializer for 'LLama.Native.NativeApi' threw an exception`). The PC lacks
  the Microsoft Visual C++ runtime that both engines load. Install the Microsoft Visual C++
  Redistributable (x64) from https://aka.ms/vc14/vc_redist.x64.exe (administrator rights), then restart Dictum. See
  "Install".
- **Model missing.** Use Settings > Models, `Dictum.exe --download-model small.en`,
  `scripts\download-model.ps1 -Model small.en`, or point `whisper.model` at a file you already have.
- **"The Microsoft Visual C++ runtime is missing" (tray panel, Settings > About).** The speech and clean-up engines need
  the Microsoft Visual C++ Redistributable (x64), version 14.51 or later, and neither could load without it (an older
  `msvcp140.dll` counts as missing). Install it as the "model error" entry above says, then restart Dictum. Until then
  nothing can be dictated; Open log folder shows the engines' own messages. The balloon says this once for both models.
- **"The graphics driver could not run the model; Dictum will use the CPU".** The model failed on the GPU (log:
  `failed while the GPU was in use (…); loading it again on the CPU`) and runs on the CPU now. Update the graphics
  driver, or set `whisper.gpu` / `llm.gpu` to `cpu` to start on the CPU every time. If the CPU fails too, the line shows
  the CPU's own error instead, with Open log folder; Try on the CPU is not offered then, since the CPU has been tried.
- **Dictum quits by itself while it loads a model on the GPU.** whisper.cpp (and llama.cpp) can end the program, rather
  than report an error, when the GPU refuses the model's memory (seen with an artificial 16 MiB buffer limit, 17/10/2026).
  While a model loads on the GPU, Dictum keeps its path in `gpu-load-speech.txt` or `gpu-load-cleanup.txt` in
  `%LOCALAPPDATA%\Dictum` and deletes the file when the load ends. GPU loads run one after the other, so this run writes
  at most one file at a time; a file left by an earlier start can sit beside it. If Dictum was ended instead (by the GPU, or by anything else during that load), the next
  start that loads that model first, with its gpu setting `auto`, loads it on the CPU for that run (the CPU-only
  runtime) and says "The graphics driver could not run this model, so Dictum uses the CPU for it." The file stays, so
  later starts do the same; loading another model on the GPU replaces it, deleting the file (with Dictum quit) lets that
  model try the GPU again, and `"gpu": "vulkan"` always tries the GPU. To keep a model off the GPU for good,
  set `"whisper": { "gpu": "cpu" }` (or `llm.gpu`) in settings.json. Not covered: the GPU ending Dictum during a
  dictation, after the model had loaded.
- **Slow cleanup.** Check the cleanup backend in the tray panel or `--selftest`. On `CPU`, choose
  "No AI cleanup (rule-based tidy)" (tray panel > Clean-up > Rule-based tidy) if 1.5b is too slow (see the measured table). If the
  log shows `applying the rule-based tidy instead: timed out` or `skipped: estimated …`, the dictation was too long
  for the model on this machine; raise `llm.timeout_ms` or dictate in shorter pieces.
- **Cleanup model missing.** Use Settings > Models, `Dictum.exe --download-cleanup-model qwen2.5-1.5b`,
  `scripts\download-model.ps1 -Cleanup qwen2.5-1.5b`, or copy the `.gguf` into the models folder. Until then the
  rule-based tidy runs.
- **"Model download failed after 8 attempts".** The connection kept dropping. The log has one warning per attempt
  with the exception chain (on this development machine it was always "The decryption operation failed" from
  Windows' TLS layer). Check the network, then choose the model again; or fetch it with
  `scripts\download-model.ps1` (curl with resume; 30 s without data ends an attempt, and if all 8 fail the partial
  file is kept, so running the same command again carries on from where it stopped) or copy it in with
  `install.ps1 -ModelsFrom`.
- **"does not match the catalogue … it was not loaded".** For a file in one of Dictum's model folders (the one you
  chose, or `%LOCALAPPDATA%\Dictum\models`): it is damaged or not the catalogue file; delete it and download it again.
  For a model that came with Dictum: reinstall Dictum, or download that model in Settings > Models (the download is
  used instead). For a file you picked from another folder: it has the same name as a catalogue file, so it is checked
  as that file; rename it, then choose it again. `--selftest` shows the same check.
- **"settings.json could not be read; running offline with defaults".** The file has a JSON error. Dictum runs with
  the defaults and `offline_only` on until you fix it; the log names the problem. A file that breaks while Dictum
  runs keeps the previous settings until it is fixed.
- **"offline_only is on" messages.** Expected: downloads and cloud providers are refused. Copy model files in
  (`install.ps1 -ModelsFrom`) or set `offline_only` to `false`.
- **Hotkey stopped working (e.g. after heavy load).** Windows silently removes a low-level keyboard hook that does
  not respond within `LowLevelHooksTimeout` (https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc).
  Dictum keeps its hook on a dedicated thread to make this unlikely. Use Settings > About > Reload settings (re-installs the
  hook) or restart Dictum.
- **Old or virtual CPUs.** The main CPU runtime needs AVX, AVX2, FMA and F16C (Whisper.net README); on CPUs without
  them Dictum falls back to the bundled no-AVX runtime (slower). The tray icon's tooltip then shows `CPU (no AVX)`.

## Deploying to staff machines and Azure VMs

Targets: GPU workstations, staff laptops/desktops (usually CPU only or integrated graphics, which Dictum treats as
CPU for its recommendations and Mode presets; untested on an integrated GPU) and Azure Windows VMs used over Remote
Desktop (CPU only, often Windows Server).

**Build once:** `scripts\publish.ps1` → `dist\Dictum-<version>-win-x64.zip` (self-contained; the Vulkan, CPU and
no-AVX whisper runtimes, the Vulkan and CPU llama.cpp runtimes and the base.en speech model are inside).

**Visual C++ runtime:** both engines need it (see "Install"). A build made with `publish.ps1 -VcRuntimeDir` carries it
and needs nothing more, which suits per-user installs without administrator rights. Otherwise every machine needs the
Microsoft Visual C++ Redistributable (x64) 14.51 or later, installed once by an administrator, for example
`vc_redist.x64.exe /install /quiet /norestart` ("Redistribute Visual C++ files", Microsoft Learn, read 17/10/2026, has
the switches). Fresh Azure and Windows Server images may not have it; `--selftest` shows it (`whisper runtime`).

**Install per user (no admin)**, locally, over remote PowerShell, or from Intune:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Source .\Dictum-1.0.0-win-x64.zip -Model small.en -LaunchAtLogin
```

| Switch | Effect |
|---|---|
| `-Source <folder or zip>` | The published build (default: `..\dist\Dictum` or the newest zip there). |
| `-Model <name>` | Pre-downloads that model with `Dictum.exe --download-model`; if there is no settings.json (and no `-Settings`), writes it as `whisper.model` so the first launch skips the speech picker. With base.en beside `Dictum.exe` the first launch then writes the rule-based tidy without asking; without it the cleanup picker still asks. To choose the clean-up yourself, seed `llm.model` or `llm.provider` with `-Settings`, and supply that model with `-ModelsFrom` or `download-model.ps1 -Cleanup`. |
| `-ModelsFrom <folder>` | Copies every `.bin` and `.gguf` in that folder (for example a network share) into the models folder the installed app resolves (`models.folder` from the seeded settings.json, or its earlier name `whisper.models_dir`, else `%LOCALAPPDATA%\Dictum\models`) instead of downloading: for air-gapped or `offline_only` machines. A file already present with the same size is skipped. Copied catalogue files are hashed the first time Dictum loads them (in a chosen folder, the first time in each session) and refused if they do not match; `--selftest` runs the same check. |
| `-Settings <path>` | Seeds `%APPDATA%\Dictum\settings.json` if none exists (a read-only seed is copied writable, so the tray can still save choices). |
| `-EnvFile <path>` | Copies a prepared `.env` to `%APPDATA%\Dictum\.env` if none exists. |
| `-LaunchAtLogin` | Adds `HKCU\...\Run\Dictum` (same value as the tray item). |
| `-Force` | Allows replacing an existing `%APPDATA%\Dictum` settings.json / .env. A `.env` beside `Dictum.exe` is never replaced or removed. |

It installs to `%LOCALAPPDATA%\Programs\Dictum`, creates a Start Menu shortcut, replaces an older copy (stopping
it first) and prints what it did. An upgrade leaves a `.env` placed beside `Dictum.exe` where it is, `-Force`
included; it is never copied anywhere else, so an interrupted install cannot leave a key in `%TEMP%`.
`scripts\uninstall.ps1` removes the program files, shortcut and Run value; models, logs, settings and both `.env`
locations are user data: `-KeepData` keeps them, `-RemoveData` deletes them, and with neither it asks in an
interactive console and keeps them in a non-interactive session (`powershell -NonInteractive`, Intune, remoting).

**Or the installer, silently:** `Dictum-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` installs the
same per-user folder and Start Menu entry (add `/TASKS="startup"` for launch at login), and
`"%LOCALAPPDATA%\Programs\Dictum\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES` removes it and keeps the user's data.
While Dictum is running, a silent setup stops with exit code 1 and changes nothing (observed 16/10/2026); the
uninstaller makes the same check (not tested silently). Quit Dictum first.
It does not take `-Model`, `-Settings` or `-ModelsFrom`; use `install.ps1` for those.

**API key** (only for the optional Anthropic cloud polish; the default local cleanup needs none): set `ANTHROPIC_API_KEY` as a machine or user environment variable instead of shipping a `.env`; the
environment wins over `.env`. Dictum reads environment variables when it starts, so restart it (or sign out and in)
after setting one.

**Which model** (latencies are this repo's measurements above, one GPU workstation; expect CPU-only laptops and VMs
to be slower than the 6-core Xeon figures):

| Machine | Model | Measured transcribe time for 10.5 s of speech |
|---|---|---|
| Any CPU-only or integrated-graphics VM, laptop or desktop | `base.en` (the recommendation there, and what the first run uses when base.en comes with Dictum; without it settings.json's own default is `medium.en`, so seed `whisper.model`); `tiny.en` if that is still too slow | base.en 1.9 s, tiny.en 0.9 s on the 6-core CPU |
| CPU-only, accuracy matters more than speed | `small.en`: more accurate, a few seconds slower | small.en 6.4 s on the 6-core CPU |
| Discrete GPU (Vulkan) or a fast desktop | `medium.en` | 0.73 s with Vulkan; 20.1 s on the 6-core CPU |
| GPU only | `large-v3-turbo` | not measured |

**Which cleanup model:** none by default: the first run uses the rule-based tidy. For AI clean-up, `qwen2.5-1.5b`
everywhere (adds about 1.8 s per 10 s dictation on the 6-core CPU, about 0.2 s with a GPU), from Settings > Models >
Add AI clean-up. On slow VMs keep the rule-based tidy; `qwen2.5-0.5b` is not recommended (see "Cleanup models"). To
pre-set AI clean-up, seed a settings.json with `-Settings`
(`{ "whisper": { "model": "base.en" }, "llm": { "provider": "local", "model": "qwen2.5-1.5b" } }`); to supply the
model offline, put the `.gguf` in the `-ModelsFrom` folder, or run `scripts\download-model.ps1 -Cleanup qwen2.5-1.5b`.

**Air-gapped or locked-down machines:** seed a settings.json with `-Settings` such as
`{ "offline_only": true, "whisper": { "model": "base.en" }, "llm": { "provider": "local", "model": "qwen2.5-1.5b" } }`
and supply the clean-up model file, and any speech model other than base.en (which comes with Dictum), with
`-ModelsFrom`. Name
the models: with base.en beside `Dictum.exe` a seed that leaves them out gets base.en and the rule-based tidy; without
it, the Welcome to Dictum window's speech choice (or, when the seed names the speech model, the clean-up picker), and settings.json's own default speech model is `medium.en`. Dictum itself then connects to
no other machine.

**Shared virtual desktops (Azure Virtual Desktop and similar: no GPU, little space on C:):** give each VM one local
folder that all its users share, for example `E:\Dictum models` on a persistent data disk, readable by every user and
writable only by administrators, so no user can change or delete a model another user loads. Seed it as the model
folder for each user (`-Settings` with `{ "models": { "folder": "E:\\Dictum models" } }`); an administrator fills it once
with `install.ps1 -ModelsFrom` (a file already there with the same size is skipped, so a user's install with the same
`-ModelsFrom` copies nothing). Users then keep no large file on C:, and base.en needs nothing at all: it comes with
Dictum. A user who wants a model the folder does not hold chooses **Use default** in Settings > Models > Model folder,
so downloads go to their own folder. Dictum then looks only in their own folder and the models that come with it, no
longer in the shared folder, so the models they use from it are downloaded again. Dictum hashes a model in a chosen
folder again the first time it loads it in each session, and **Delete all app data** leaves a chosen folder outside the
user's own folder alone. An Azure file share, cool tier especially, is not recommended: models would load across the
network at every start (Settings > Models warns about any network folder; not measured here). See "Model folder".

**SmartScreen and Smart App Control:** the files are unsigned, so the first launch may show "Windows protected your
PC" (More info > Run anyway), enterprise policy can prevent continuing, and Smart App Control blocks unsigned files
(see "Permissions on Windows" for the sources). Where either applies, sign every shipped binary (`Dictum.exe`,
`Dictum.dll`, the whisper and llama DLLs) with your organisation's code-signing certificate before distributing.

**Remote Desktop microphone:** in the Remote Desktop client (mstsc) open Show Options > Local Resources > Remote
audio > Settings and choose **Record from this computer** (the menu path is recalled, not verified; Microsoft
describes "the remote audio settings on the Local Resources tab in Remote Desktop Connection"). The server's "Allow
audio recording redirection" policy (TS_CLIENT_AUDIO_CAPTURE, under Remote Desktop Session Host > Device and
Resource Redirection; https://learn.microsoft.com/windows/client-management/mdm/policy-csp-admx-terminalserver, read
15/10/2026) must not be disabled. The redirected microphone appears in Windows (and in `--selftest`) as **Remote
Audio**; this development machine is itself used over RDP and records through it.

**Windows Server:** the Windows Audio service is disabled by default on Server (recalled; not re-checked on a Server
VM during this build); set it to Automatic and start it (as an administrator:
`Set-Service Audiosrv -StartupType Automatic; Start-Service Audiosrv`). Without it there is no microphone; whether
`--selftest` then lists 0 capture devices is not tested (inferred: devices may still be listed, through the separate Windows Audio Endpoint Builder service, while recording fails).

**No GPU** (most Azure VMs): the Vulkan runtime cannot load or finds no device, so Dictum uses the CPU runtime.
That is expected, not an error; there is no dialog, and the tooltip shows `CPU`.

**Verify a machine:** `& "$env:LOCALAPPDATA\Programs\Dictum\Dictum.exe" --selftest | Out-Host` reports the
speech and cleanup models with their runtimes, the recommendations, offline_only, microphones and key presence. It
exits 1 if the speech model, a microphone or a shipped data file is missing, or settings.json cannot be read; a
missing cleanup model is reported but does not fail it.

## Contributing, security and licence

See [.github/CONTRIBUTING.md](.github/CONTRIBUTING.md) to build, test and propose changes, and
[.github/SECURITY.md](.github/SECURITY.md) to report a vulnerability privately. Dictum is MIT licensed (`LICENSE`,
Kynni Pty Ltd).
