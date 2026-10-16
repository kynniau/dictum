# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Email **hello@kynni.com.au** with "Security" in the
subject, or use GitHub's private vulnerability reporting (Security > Report a vulnerability) if it is switched on for
this repository. Include the Dictum version (Settings > About, or `Dictum.exe --selftest`), how it was installed
(Microsoft Store, `Dictum-Setup-<version>.exe`, the zip, or a build from source), and the steps that show the problem.
Never send real API keys, recordings or dictated text: describe them instead.

## Supported versions

Dictum is at version 0.1.0 and has no published release yet. Fixes go into the next release; older releases are not
patched.

## What Dictum promises, so what counts as a vulnerability

These are the guarantees the README states ("Privacy"); anything that breaks one is a security bug:

- Nothing leaves the PC by default. Dictum connects only to `huggingface.co` (and its download CDN) when you ask for a
  model, to `dictum.kynni.au` for one small file (`/version.json`, sending nothing about you) when you click Check for
  updates in the direct download, and to a clean-up service off this PC only after you switch it on and agree to that
  host in the consent step. No telemetry, analytics, crash reports or automatic update checks.
- With `offline_only: true`, Dictum itself connects to no other machine.
- Dictation audio stays in memory and is never written to disk; transcripts are not written to disk, and reach the
  log only if you turn on `log_transcripts`.
- API keys come only from environment variables or `.env`, are never written to `settings.json`, the windows or error
  messages, and are redacted from the log.
- Downloaded models are data, checked against pinned SHA-256 hashes where the catalogue has one; Dictum never
  downloads or runs code.
- The global keyboard hook only compares keys with the configured hotkeys and Esc (and, while the "Press your
  hotkey" box is open, shows the key pressed); it stores and sends no keystrokes.

## Release integrity

Release files are built by GitHub Actions (`.github/workflows/ci.yml`) from the tagged commit, and each release lists
their SHA-256 hashes in `SHA256SUMS.txt`. The installer and the zip are not code-signed yet. The test MSIX is signed
with a self-signed certificate made for that build only (its private key is not kept); the Microsoft Store version is
signed by Microsoft.
