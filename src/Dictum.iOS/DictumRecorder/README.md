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

## Licence

MIT. Copyright (c) Kynni Pty Ltd.
