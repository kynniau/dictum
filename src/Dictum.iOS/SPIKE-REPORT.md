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

## 8. Round 2, the same evening: on-device transcription and speaker labels (branch `spike/v2-ondevice`)

Kyle reopened decision 2 at 16:00 for an experimental beta ("a real beta before tomorrow of on device
processing to test") and at 16:15 asked for speaker identification "ready tonight". The orchestrator's
conditions: iPad only, switch default off, transcript and labels in memory only, nothing written, the
bundle unchanged, the demo branch frozen, nothing experimental shown at the conference unless Kyle says
so on the day, no pyannote. Branch `spike/v2-ondevice` cut from `spike/v2-iphone` ff95459; heads b2b2cb7
(transcript), 0097232 (facts), fbcefc8 (speaker labels), 2b4b775 (threshold 0.7), 2e6db9e (icon),
db87298 (labels on every line, timestamps).

### 8.1 Apple on-device transcription (SpeechAnalyzer / SpeechTranscriber, iOS 26+; API names compiled against the iOS 27.2 SDK)

| Run (iPad Pro 13-inch M4, iPad16,6, iPadOS 27.3 build 24C5037i) | Result (V: Kyle's Sealed-screen screenshots) |
| --- | --- |
| 16:02, two people, ~1 min, build b2b2cb7 | en_AU; model "Yes, in 0.1 s" (later shown to be a nil install request against a present model); Stop-to-final 0.1 s; thermal nominal; coherent punctuated transcript, no speaker labels |
| 16:11, airplane mode (Wi-Fi and data off), Kyle only, ~2 min, build 0097232 | en_AU; model already on device (status before Start: installed); Stop-to-final 0.1 s; nominal; **transcribed with networking off: Yes** |
| 16:56, ~28 s, build 2b4b775 | see 8.3 |

The speech model's status check and any download happen only on the consent screen, on the user's tap,
never during a recording (orchestrator condition). A network event to Apple may have occurred at about
16:01 by the iPad's clock on Kyle's tap; bytes moved unknown (the facts box could not then distinguish a
nil request from a tiny install; fixed in 0097232).

### 8.2 Speaker identification: library, model and the clustering finding

- sherpa-onnx 2.0.1 at commit af2d8d81 (the audited one) built for iOS with a portable CMake 4.4.5
  (Kitware release, SHA-256 checked) in 6 min 38 s, then rebuilt with `SHERPA_ONNX_ENABLE_TTS=OFF` and
  `SHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=OFF` in 4 min 45 s; device slice 17,092,976 bytes, SHA-256
  fa0d30774b67321c48a4d7bb2720e557bbfc316d4cad7450fc977c3f40a37ef1; pyannote symbols 0, piper 0; the
  remaining `OfflineTts*`/`OfflineSpeakerDiarization*` C functions are the library's "not enabled" stubs.
  onnxruntime 1.28.2 zip SHA-256 matches its published digest. espeak-ng was never present (an earlier
  "espeak" count was a case-insensitive false match on `CreateSpeaker`/`wespeaker`; corrected). (V, lane
  command output read by this session.)
- Model: `3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx`, 29,596,978 bytes, SHA-256
  357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b (same as the desktop); Apache-2.0
  (3D-Speaker project, LICENSE fetched and kept with the artefacts). The xcframeworks and the model are
  not committed (over the repository's 5 MiB rule); `Frameworks/README.md` and `Models/README.md` state
  provenance and hashes.
- Voice-print cost: Mac (M-series, cpu, 2 threads) 0.8 s per minute of audio; iPad Pro M4 0.3 s per
  minute (V, facts box 16:56). Embeddings are deterministic (same window twice: cosine 1.000000000) and
  gain-insensitive on a loud window (×0.5, ×0.25: cosine 1.000000); on a quiet window ×0.5 gave 0.9954 (V).
- **Finding (the evening's main result):** the desktop's separation rule (2 s windows, 1 s hop, complete
  linkage, threshold 0.5, `MeetingSpeakers.DefaultThreshold`) over-splits one real voice. On Kyle's
  2-minute solo recording 8efdbec8, windowed by Apple transcript segments on the Mac (25 segments, 63.3 s
  of speech, 20 segments ≥ 1.5 s, 49 windows): same-voice window distances p50 0.4998, p90 0.685, max
  1.027; complete linkage at 0.5 gives 14 clusters with 17 of 20 segments labelled other than the
  majority speaker; average linkage at 0.7 gives 2 clusters with 1 of 20 off. sherpa's public
  two-speaker clip gives exactly 2 clusters at every threshold 0.5–0.8 with either linkage. The
  orchestrator reproduced the over-splitting on the PC with Dictum's own embedder (5aa22eab: 6 clusters
  at 0.5, 1 at 0.7). Kyle ruled "hold and fix" at 16:51: the Windows 1.6.0 tag is held until the desktop
  default is re-tuned on real speech. Calibration data (segment times, window ranges, vectors, distance
  matrices; no audio, no words) is in `calibration-8efdbec8.tar.gz`, SHA-256
  735ae54325f21c9df89dfd1b50068e479a48f73a898330a017e7e319a5e28ef0, delivered to Kyle for the PC lane.
  Evidence base: one solo recording and one public clip; weak, and stated as such.

### 8.3 Speaker labels on the iPad (first run, 16:56, build 2b4b775)

Parameters: transcript segments as units, 2 s/1 s windows inside each (min 1.5 s), one L2-normalised
mean voice-print per segment, average linkage, threshold 0.70 (a phone-side setting from the Mac
sweep; the desktop's values remain reachable in `SpeakerParameters.desktop`). Recording
meeting-2026-11-08-9498f017.dictum, 0.9 MB (~28 s): 8 segments, 4 with voice-prints from 11 windows,
distances between segment vectors median 0.268 / p90 0.432 / max 0.432, largest merge 0.387, **speakers
found 1**; voice-print time 0.2 s, clustering 0.0 s, thermal nominal, 0.9 MB of sound held in memory and
released (V, screenshots). Speaker count per Kyle: pending at the time of writing. With distances this
small the desktop's rule would most likely also have given 1, so this run does not discriminate between
the rules; a two-person recording is the next test. Three lines under 1.5 s showed no label (fixed in
db87298: the phone labels a short line from the nearest turn at any distance; the desktop's rule is
within 1.0 s, kept as the `.desktop` parameter).

### 8.4 Owner asks recorded this evening (release-2 design pass, in Kyle's words)

Model selector; speaker identification ("100% need"); timestamps per line (done); iPhone layout first,
then an iPad layout ("enlarged iPhone app on iPad is the worst experience"); save and review past
transcripts on iPad and iPhone; a settings screen before recording for engine, clean-up and summary
models (on-device or own API key as in desktop 1.6), meeting types and action items; not translation
yet; an in-room end-of-meeting review (brief summary and who-does-what list confirmed before anyone
leaves) followed by a summary email to attendees with their actions (design position: the app composes
the email in the user's Mail app and never sends itself).

### 8.5 Not run this evening

Five-minute recording with the switch on; two-person speaker test on the iPad (pending Kyle); peak
memory (Instruments); whisper.cpp; PBKDF2 timing on the phones; the PC-vs-iPad cosine of the same
30 s window (Mac-side floats delivered; PC side waits on Kyle's email of 8efdbec8); the lock-screen
30-minute recording and incoming call; the 3-second fixture re-record; the PC screen import of 5aa22eab.
