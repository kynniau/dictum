# Dictum v2 iPhone recorder: spike report

Draft, started 08/11/2026 15:30 AEDT on Kyle's Mac (clock: `date`). Updated as measurements land.
Every figure carries its provenance: **V** verified (source and time), **R** recalled, **I** inferred
from stated premises. Times are AEDT, DD/MM/YYYY.

Scope change: on 08/11/2026 12:43 Kyle confirmed the recorder must be demo-ready for a conference on
Monday 09/11/2026 (his words to this session: "Yes, Monday demo"; to the orchestrator at 12:54:
"Record to PC import"). The one-week plan was compressed to rung 3 end to end; everything else is
recorded as run or "not run" with the reason. The demo is remote: the owner travels with iPhone and
iPad only; the PC and Mac desktops stay where they are, driven over Tailscale (Windows App) and RealVNC.

## 1. What was built (V: branch `spike/v2-iphone`, pushed; heads 91d1361, 0c49ad8, 57c6374, 43ce329)

- `src/Dictum.iOS/DictumBundle/`: Swift package writing the `.dictum` bundle per
  `docs/MEETINGS-CONTRACTS.md` §14.A/14.A.1 (DCTMAUD chunked audio sealed as captured, DCTMENC meta and
  confirmations, 60-byte PBKDF2 transfer wrap, stored-only ZIP). CryptoKit AES-GCM; CommonCrypto PBKDF2.
- `src/Dictum.iOS/DictumRecorder/`: SwiftUI iPhone app, iOS 17+, device family iPhone+iPad. Consent
  screen with approved desktop copy verbatim plus four `PROPOSED_` phone points; recording screen with
  persistent indicator, elapsed time, input-level bar and `PROPOSED_NoSoundDetected` after 3 s of zero
  input; sealed screen with file name, size, six-word transfer code and `ShareLink`; recovery of a
  recording cut short. Content key held in the Keychain for the life of a recording.

## 2. Format conformance (V unless marked)

- `swift test` in the package: 18 tests, 0 failures (V, run by this session 08/11/2026 13:12).
  Reproduces `phone-sample-chunked.dictum` and `phone-sample-chunked-incomplete.dictum` byte for byte:
  every entry SHA-256 and whole-bundle SHA-256 equal the README values, and `cmp` against the committed
  files is equal (V, agent run read by this session; re-run by this session 13:04, 17/17 at that time).
- Mutation: with the framing-tag AAD length reverted to 0 the suite fails 10 of 17, including both
  fixture hash tests (V, agent output quoted in its report; restore then 17/17).
- Desktop import of session-written bundles (not the fixture path), C# `BundleImporter` on `origin/main`
  418015d Core, run on the Mac in a throwaway harness outside the repo (V, 13:10 and 13:12):
  complete 3.5 s bundle imports, Source Phone, Duration 00:00:03.5, 112000 PCM bytes = appended;
  session dropped without finish then `recover`: imports, IncompleteAudioChannels [Mixed], 64000 PCM
  bytes read to EOF with no terminal record. Negatives: one flipped ciphertext byte (CRC stale, and
  recomputed) → `CryptographicException: Encrypted chunk authentication failed`, nothing left in the
  store; wrong transfer code → `AuthenticationTagMismatchException` at Unwrap, store root never created.
- Defect found and fixed: a recovered bundle carried Duration 00:00:00 (provisional meta repackaged).
  Fix derives duration from the sealed PCM bytes; the same import then shows 00:00:02 (V, 13:12;
  commit 0c49ad8; new test observed failing with duration forced to 0).

### Format findings for lane A (all accepted by the orchestrator as clarifications, PR 117 → main ee457f4)

1. §14.A.1 did not state the audio nonce draw order (payload nonce first, then framing); the README did.
2. ZIP DOS timestamp time zone unstated; the C# importer ignores it.
3. JSON string escaping beyond the fixture unstated; the reader accepts any valid JSON escaping.
4. The meeting id must sit in plaintext in the phone's session folder for recovery (it is plaintext in the
   manifest anyway).
5. Design change before coding (ruled 08/11/2026): phone writers record straight into the chunked form so
   no plaintext audio ever sits on the phone; a cut-short recording stays importable.

## 3. Real recordings on the iPhone 17 Pro Max (iOS 27.2, build 24B115; V `devicectl`)

| File | Sealed | Bytes | Duration | Audio | Notes |
| --- | --- | ---: | --- | --- | --- |
| meeting-2026-11-08-9d709037.dictum | 13:52 | 4,333,226 | 00:02:15.08 | all zero (2,161,349 samples, peak 0) | made during a Mac/Xcode screen-share session of the phone; V Mac 14:30 and PC |
| meeting-2026-11-08-4ec771a9.dictum | 14:17 | 117,621 | 00:00:03.59 | all zero (57,507 samples) | same session; fixture candidate, NOT committed |
| meeting-2026-11-08-5aa22eab.dictum | ~15:16 | 745,990 | 00:00:23.19 | peak 30,264 (−0.7 dBFS), RMS 3,575 (−19.2 dBFS) | phone in hand after a restart, no screen share; V orchestrator on the PC 15:22 |

All three decrypt and import on the desktop path. The two silent files are the day's main finding:
correct framing around silence. The input-level bar and no-sound warning (commit 43ce329) were added so
the screen shows capture state; Kyle observed the bar moving on the hand-held recording. Mechanism of the
silence (microphone withheld while the phone is screen-shared) is **R/I**, supported by one measurement,
not verified as a mechanism. Peak at −0.7 dBFS on the hand-held recording is close to clipping (V);
input gain is a follow-up, not a demo blocker.

## 4. Transfer routes (V unless marked)

- iCloud Drive → PC: **not available**; iCloud for Windows is not installed on the PC (V orchestrator 13:57).
- OneDrive share sheet → PC: not run for a successful transfer (Kyle used email instead).
- Email (phone share sheet → mail → Zoho Mail web on the PC → saved by hand to Downloads): worked for
  all three files (V orchestrator; file times 14:18:10 and 14:18:17 for the first two).
- USB (Apple Devices), Phone Link: **not run, not available at the demo** (the desktops are not at the venue).
- Mac import: **not possible at 418015d**: macOS has no credential store, `MeetingsLockedException`
  before any meeting opens (V Mac lane and orchestrator from source, 08/11/2026).

## 5. Not run, and why

- Day-1 Voice Memos baseline: not run, deferred for the demo.
- 30-minute lock-screen recording and incoming call on each phone: not run yet (08/11/2026 15:30).
- Speed and voice print (whisper.cpp, SpeechTranscriber, PBKDF2 timing, sherpa-onnx cosine): not run;
  release-2 questions, after the demo.
- iPhone 13 and iPhone XS: not connected; nothing run on them.
- iPad Pro: updated to iOS 27 and connected; app not installed (Developer Mode not yet on there).

## 6. Swift API names verified (V: compiled against the iOS 27.2 SDK, Xcode 27.2 27B52)

`AVAudioSession.sharedInstance().setCategory(.record, mode: .default, options: [])`, `.setActive(_:)`,
`interruptionNotification`, `routeChangeNotification`, `mediaServicesWereResetNotification`;
`AVAudioEngine.inputNode.installTap(onBus:bufferSize:format:block:)`, `AVAudioConverter(from:to:)`,
`.convert(to:error:withInputFrom:)`, `AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16000,
channels: 1, interleaved: true)`; `AVAudioApplication.shared.recordPermission`,
`AVAudioApplication.requestRecordPermission()`; CryptoKit `AES.GCM.seal(_:using:nonce:authenticating:)`;
CommonCrypto `CCKeyDerivationPBKDF`; Security `SecItemAdd/CopyMatching/Delete`,
`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`; `FileProtectionType.completeUntilFirstUserAuthentication`;
SwiftUI `ShareLink(item:)`. Install and launch: `xcrun devicectl device install app`, `... process launch`.

## 7. Go / no-go per rung (as of 08/11/2026 15:30)

- Rung 3 (record, encrypt, hand to PC): **go**, with the hand-held recording as evidence; the app-screen
  import on the PC and the fixture re-record are the remaining rehearsal steps.
- Rungs above 3: not measured; no promise.
