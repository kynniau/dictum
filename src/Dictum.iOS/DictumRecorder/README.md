# Dictum Recorder (iPhone)

A small SwiftUI app that records a meeting on an iPhone, encrypts the sound as it is captured, and seals it into a
`.dictum` bundle that you move to your computer and import into Dictum. The app sends nothing anywhere.

Status: spike (v2-iphone). Not reviewed for release.

## Open and build

1. Open `src/Dictum.iOS/DictumRecorder/DictumRecorder.xcodeproj` in Xcode.
2. Xcode resolves the local package `../DictumBundle` (the bundle format and encryption) automatically.
3. Choose the `DictumRecorder` scheme.

Command-line check (no signing):

```
xcodebuild -project src/Dictum.iOS/DictumRecorder/DictumRecorder.xcodeproj -scheme DictumRecorder \
  -destination 'generic/platform=iOS Simulator' CODE_SIGNING_ALLOWED=NO build
```

## Sign and run on your iPhone

1. Xcode → Settings → Accounts → sign in with your Apple ID.
2. Select the team. The project is set to team `PX6W25UNPP` with automatic signing; change it under the target's
   Signing & Capabilities if your team differs.
3. Connect the iPhone with a cable. On the phone, tap Trust when asked.
4. On the phone, turn on Developer Mode (Settings → Privacy & Security → Developer Mode), then restart it when asked.
5. In Xcode, pick the phone as the run destination and press Run.
6. On first use of Start, the phone asks for microphone access. Allow it.

Recording keeps going with the screen locked (background audio mode). The sealed file is shown in
Files → On My iPhone → Dictum Recorder, and can be sent with the share button.

## Proposed values to confirm

- Bundle identifier `com.kynni.dictum.recorder` is PROPOSED. Change `PRODUCT_BUNDLE_IDENTIFIER` in the project if
  needed.
- The microphone permission text in `Info.plist` (`NSMicrophoneUsageDescription`) is PROPOSED.
- Every new on-screen string is a `PROPOSED_` constant in `DictumRecorder/Text.swift`. `APPROVED_` constants there are
  copied unchanged from the approved desktop copy and must not be reworded.

## App Store submission question (not verified)

The app uses encryption (AES-256-GCM and PBKDF2-HMAC-SHA256, through `DictumBundle`). `ITSAppUsesNonExemptEncryption` is
deliberately NOT set in `Info.plist`. Before any TestFlight or App Store upload, check Apple's current export
compliance rules and whether this use is exempt or needs documentation. This has not been checked.

## On-device transcript (experimental)

A switch on the first screen, "Experimental: transcribe on this device", default off. It is shown only on iOS 26 or
later when Apple's Speech framework reports `SpeechTranscriber.isAvailable` and a supported English locale.

What it does:

- Turning the switch on asks for speech-recognition permission, picks a locale (`en_AU`, else `en_US`, else the first
  supported English locale), and checks whether Apple's speech model for it is installed.
- If the model is missing, the first screen says so and offers "Download the speech model". That button is the only
  place a download can start, and it needs the internet. Start stays disabled with the switch on until the model is
  installed. The seconds the download took are measured with `ContinuousClock`.
- While recording, the tap's original buffer (before the 16 kHz conversion) also goes to `SpeechAnalyzer` through a
  separate `AVAudioConverter`. Finalised words show in the primary colour; words still being recognised show in the
  secondary colour and italic.
- After sealing, the full transcript is shown with a Copy button and an "Experiment facts" box: locale used; the
  speech model outcome (already on the device, installed on request with the seconds, or no installation request
  needed) and the `AssetInventory` status the switch found before Start; seconds from Stop to the last final result;
  `ProcessInfo.thermalState` at Stop; and whether it transcribed with networking off (an `NWPathMonitor` that only
  observes the path from Start to Stop and makes no requests: "Yes" when no path was ever `.satisfied`).

What it never does:

- It never writes the transcript to disk in any form, and the `.dictum` bundle never gains a transcript entry.
  `SealWriter.swift` and every call into `DictumBundle` are unchanged.
- It never sends the transcript anywhere. Copy puts it on the general pasteboard only when you tap it.
- It never downloads anything during a recording. If the model is missing when recording starts, the status line says
  the transcript is not available and the recording carries on.
- With the switch off, `AudioCapture` gets no second sink and the recording path is unchanged.
- Done clears the transcript and the facts from memory and turns the switch back off.

How to test:

1. On an iOS 26 or later device, turn the switch on and allow speech recognition.
2. If asked, download the speech model while online. Note the seconds in the facts box later.
3. Turn on airplane mode (with Wi-Fi off too). Record, speak, then stop and seal.
4. Check words appeared while recording, the transcript and facts show after sealing, and the bundle imports into
   Dictum as before, with no transcript inside it.
5. Tap Done and check the transcript is gone. Record once more with the switch off and compare the bundle with one
   made before this change.

Speech API names that compiled against the iOS 27.2 SDK (Xcode 27.2): `SpeechTranscriber(locale:transcriptionOptions:
reportingOptions:attributeOptions:)` with `.volatileResults`, `SpeechTranscriber.isAvailable`,
`SpeechTranscriber.supportedLocales`, `SpeechTranscriber.supportedLocale(equivalentTo:)`, `SpeechAnalyzer(modules:)`,
`SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith:)`, `AnalyzerInput(buffer:)`, `analyzer.start(inputSequence:)`,
`finalizeAndFinishThroughEndOfInput()`, `cancelAndFinishNow()`, `transcriber.results` with `result.text` and
`result.isFinal`, `AssetInventory.status(forModules:)`, `AssetInventory.assetInstallationRequest(supporting:)`,
`AssetInstallationRequest.downloadAndInstall()` and `.progress`. Speech permission uses
`SFSpeechRecognizer.requestAuthorization`; the SDK interface for `SpeechAnalyzer` does not say whether it needs that
permission, so it is asked as a precaution.

Not measured or not verified:

- Peak memory is not measured.
- Nothing has been run on a device: availability on iOS 27.2, the model download, airplane-mode behaviour, latency,
  accuracy and thermal effect are all unverified.
- Whether the speech permission is actually required, and whether listing supported locales at launch touches the
  network, are not verified.
- Audio that arrives before the analyzer is ready is dropped, so the first moment of speech may be missing.

## Files

- `DictumRecorder/Text.swift` – every user-facing string.
- `DictumRecorder/AudioCapture.swift` – microphone capture, conversion to 16 kHz mono 16-bit, interruptions and route
  changes.
- `DictumRecorder/SealWriter.swift` – the serial queue that writes to the encrypted session; file locations and
  file protection.
- `DictumRecorder/ContentKeyStore.swift` – the per-meeting content key in the Keychain (this device only, after first
  unlock) until the bundle is sealed.
- `DictumRecorder/RecordingController.swift` – screen state.
- `DictumRecorder/Screens.swift` – the four screens.
- `DictumRecorder/OnDeviceTranscript.swift` – the experimental on-device transcript (memory only).

## Licence

MIT. Copyright (c) Kynni Pty Ltd.
