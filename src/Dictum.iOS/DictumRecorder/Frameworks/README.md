# Frameworks (not committed)

The native library for the experimental speaker identification. This folder's contents are ignored by git (too large
for the repository's 5 MiB private-material limit); only this README and `Sherpa.xcconfig.template` are committed.

What goes here:

- `sherpa-onnx.xcframework`: sherpa-onnx pinned to **2.0.1**, commit **af2d8d81** (`af2d8d81dbfa61c4cea4c568da78f3982edca005`,
  the same commit the desktop's speaker code was read against), built with that commit's `build-ios.sh`. It holds the
  static framework `SherpaOnnxC.framework` (module `SherpaOnnxC`, header `sherpa-onnx/c-api/c-api.h`) in the slices
  `ios-arm64` and `ios-arm64_x86_64-simulator`.
- `onnxruntime.xcframework`: the static onnxruntime xcframework that `build-ios.sh` downloads and builds against, with
  `onnxruntime.framework` in the same two slices.
- `Sherpa.xcconfig`: a copy of `Sherpa.xcconfig.template`.

How the switch works: `../DictumRecorder.xcconfig` (the target's base configuration) has `#include? "Frameworks/Sherpa.xcconfig"`.

- With `Sherpa.xcconfig` present, the target gets the two slices' framework search paths and links `SherpaOnnxC`,
  `onnxruntime` and `libc++`. `SpeakerLabels.swift` sees `canImport(SherpaOnnxC)` and compiles `SherpaVoiceEmbedder`.
- Without it, nothing is added: `canImport(SherpaOnnxC)` is false, only `StubVoiceEmbedder` exists, and the Sealed screen
  says the speaker model is not in this build.

Delete `Sherpa.xcconfig` to go back to the stub. Keep both xcframeworks and the model in `../Models/` together: the
library without the model also falls back to the stub at run time.

Licences: sherpa-onnx is Apache-2.0 (k2-fsa); onnxruntime is MIT (Microsoft).
