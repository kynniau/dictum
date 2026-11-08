# Models (not committed)

The speaker model for the experimental speaker identification. This folder's contents are ignored by git (too large for
the repository's 5 MiB private-material limit); only this README is committed.

What goes here, exactly one file:

- `3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx`, 29,596,978 bytes,
  SHA-256 `357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b` (the same CAM++ model and hash the desktop
  uses). Licence: Apache-2.0, from the 3D-Speaker project (modelscope/3D-Speaker). Used through sherpa-onnx 2.0.1
  (commit af2d8d81), see `../Frameworks/README.md`.

Check it before building: `shasum -a 256 3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx`.

This folder is a synchronised folder of the Xcode target with this README excluded, so any file in it is copied into the
app as a resource. With the file absent nothing is bundled and the app falls back to the stub embedder.
