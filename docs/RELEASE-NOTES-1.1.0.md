# Dictum 1.1.0

Released 17/10/2026. This is the first update to the 1.0.0 release.

## What is new

**Dictate in other languages.** Dictum can now listen in any language Whisper supports and, if you choose, translate what you say into English. Settings > Models has a Language card. Languages other than English need one of the multilingual models (small, medium and large-v3, each an optional download); every one is checked against a fixed SHA-256 before it is used. Cantonese works only with a large-v3 model. The AI clean-up is written for English, so for other languages a plain tidy runs instead and the model does not rewrite your words.

**Hands-free types as you pause.** In hands-free mode (double-tap the hotkey) Dictum now types each part of your speech when you pause, instead of waiting until you stop. A long session shows its text as you go, and the wait at the end is only the last part. A new field in Settings > General sets when hands-free stops by itself, from 30 seconds to 60 minutes. Esc in hands-free stops and keeps what has already been typed. Hold-to-talk is unchanged: one recording, typed when you let go.

**Mouse-button hotkeys.** The hotkey can now be the right, middle or side (Mouse4 and Mouse5) mouse button. The left button is not allowed. The "Fix last dictation" hotkey now has a box that captures the key you press, as the main hotkey does.

**Snippets.** Say a phrase and Dictum pastes the text you linked to it, such as a web address or a sign-off. They are kept in `snippets.txt`, and the tray has a Snippets button to add one.

**Cloud clean-up: ready-made services and stronger warnings.** Cloud clean-up is still off by default and still an advanced option that needs your own API key. Settings > Clean-up now lists Anthropic (Claude), OpenAI, Google Gemini, Ollama on this PC and a custom server, and fills in the address for you. Before anything is sent, the consent box and the Settings page say plainly that your text leaves your PC and that Dictum does not certify the provider.

**Clean-up no longer adds names you did not say.** The AI clean-up checks its reply against the dictionary. If it adds a dictionary word that you did not say (for example, writing a company name where you said "name"), Dictum discards that reply and uses the rule-based tidy instead.

**Check for updates (direct download only).** Settings > About has a Check for updates button. It reads one small file from dictum.kynni.au, sends nothing about you, and runs only when you press it. If a newer version is out, it shows the release notes and a button to the download page. Offline-only mode stops it. The Microsoft Store version has no button because the Store updates it.

**Turning cloud clean-up off now always works.** Before, if Dictum could not write its settings file (for example it was read-only or damaged), "Switch cloud clean-up off" could say it was off while text was still going to the cloud service. Now the switch takes effect straight away, and Dictum keeps retrying to save it until it sticks.

**Choose where models live.** Settings > Models has a Model folder card with Browse and Use default. Dictum looks in your chosen folder, then in its own folder under your profile, then in the models folder that comes with the program. Changing the folder moves no files. A model in a shared folder is checked each time Dictum starts. A download is refused if it would leave the drive with less than 200 MB free.

**The base.en speech model is included, and nothing is downloaded on first run.** Every package now carries the base.en English speech model, so a new install asks no model questions and is ready to dictate. The AI clean-up model is no longer chosen on first run: the rule-based tidy is the default, and "Add AI clean-up (1.1 GB download)" in Settings > Models adds the model when you want it. Larger speech models stay optional downloads.

**The Visual C++ runtime.** The speech and AI engines need Microsoft's Visual C++ runtime. The Store version declares it as a dependency, so the Store installs it. The direct-download installer checks for it before installing and offers Microsoft's download (https://aka.ms/vc14/vc_redist.x64.exe, which needs administrator rights). The portable zip needs the runtime installed first.

**Third-party notices.** `THIRD-PARTY-NOTICES.txt` now lists every third-party component Dictum ships, with its licence, and is included in each package.

## Good to know

- A snippet's line break is typed as Enter. Some chat apps send the message when they get Enter.
- Fix last dictation cannot learn a correction from text a snippet produced.
- The direct download is not code-signed yet, so Windows SmartScreen may warn when you run it.

## For the Store reviewer

The first submission failed because the speech engine could not load on a PC without the Visual C++ runtime. This package declares Microsoft.VCLibs.140.00.UWPDesktop (14.0.33728.0 or later) as a dependency, and it carries the base.en speech model, so no download is needed to test. Dictum is English by default, runs in the notification area, and needs no account. The certification notes in the submission give the dictation steps and the offline test.
