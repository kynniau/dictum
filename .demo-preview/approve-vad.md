# Voice activity gate: strings for approval (PROPOSED 04/11/2026; branch claude/distracted-mccarthy-f9ab00)

Lane brief (orchestrator, 04/11/2026): a voice activity detector in front of Whisper, so that a take with no speech types nothing.
Every string below is new and PROPOSED. Rows 1 and 2 are not in the app yet; row 9 is (marked PROPOSED in the code), as are the log lines, and row 10 is in the README. The Help text and every approved paragraph in `HelpText.cs` are
unchanged. No screen shows new words in this PR. The See how it works page shows a take the detector refused with the reason it
already shows, "no speech recognised" (`DictationPipeline.NoSpeech`).

Facts the wording rests on (verified 04/11/2026, this branch): the detector is whisper.cpp's Silero model, run in-process through
Whisper.net 1.9.1 on the CPU; the model file is 885,098 bytes (about 0.9 MB); it is fetched only when asked, from huggingface.co,
like the speech models; without it Dictum behaves as before.

| # | Surface | Proposed string | Where it would go |
|---|---|---|---|
| 1 | Help, questions and answers: a new question beside the existing ones (not in place of any) | Question: "Why does Dictum sometimes type nothing?" Answer: "If Dictum hears no speech in a recording, it types nothing. A small voice detector, under 1 MB, listens to each recording on this PC before the speech model does, so a silent recording is not turned into made-up words. The recording never leaves this PC for this check. Without the detector, Dictum decides by how loud the recording was." | `HelpText` questions list, as a new entry |
| 2 | Help line, short form (if a one-line note fits better than a question) | "Dictum's voice detector checks each recording for speech on this PC, so silence is never turned into made-up words." | `HelpText`, a new line |

Log lines and the settings.json note (not on screen, in the code now, listed for the record; they name the decision and why, never the audio or the text):

| # | Log line | Where in the code |
|---|---|---|
| 3 | "Voice activity gate: on, with <path>; it loads with the first dictation." / "Voice activity gate: off (<why>); the level decides silence (silence_threshold)." | `VoiceGates.Configure` |
| 4 | "Voice activity gate: ggml-silero-v6.2.0.bin loaded in N ms, on the CPU." / "… could not be loaded (Type: message); the level decides silence (silence_threshold) until the file changes." / "Voice activity gate: waiting for the speech model to load the whisper runtime; until then the level decides silence (silence_threshold)." | `VoiceGates.Get` |
| 5 | "Recording discarded: no speech (the voice activity detector found no speech in 5.0 s)." / "Recording: the voice activity detector found speech: 1 segment, 3.9 s of 4.7 s; transcribing." ("Segment N" in hands-free) | `DictationPipeline.VoiceRejectAsync` |
| 6 | "Recording: the voice activity detector failed (Type: message); the level decides instead (silence_threshold)." | `DictationPipeline.VoiceRejectAsync` |
| 7 | `--selftest`: "voice activity  on: <path> (SHA-256 verified; loaded in N ms, on the CPU)" or "off: no ggml-silero-v6.2.0.bin in the model folders; Dictum.exe --download-model silero-v6.2.0 fetches it; silence_threshold decides silence" | `SelfTestCommand.VoiceGateLine` |
| 8 | `--transcribe` stderr: "voice activity: <the detector's verdict>[; nothing transcribed]" or "voice activity: off (<why>)" | `CliCommands.TranscribeAsync` |
| 9 | settings.json `_doc` note for the new `vad` key, PROPOSED (a person reads it in settings.json, not on a screen): "auto: when ggml-silero-v6.2.0.bin is in a model folder (Dictum.exe --download-model silero-v6.2.0), a voice detector on this PC checks each recording for speech first, and one with none is discarded; silence_threshold then no longer decides silence. off: silence_threshold decides, as without the file." | `SettingsFile` `_doc["vad"]` |
| 10 | README, Settings reference table, the new `vad` row (documentation copy), PROPOSED: "`auto`: when `ggml-silero-v6.2.0.bin` is in a model folder (`Dictum.exe --download-model silero-v6.2.0`), whisper.cpp's Silero voice detector checks each recording, and each hands-free part, for speech on this PC first; one with none is discarded, and `silence_threshold` no longer decides silence. `off`: `silence_threshold` decides, as without the file. Any other value: `auto`, with a warning." | README.md, the `vad` row |

Open for the owner: whether the detector should come with Dictum (it is under 1 MB; base.en already does) or stay a download
behind a Settings button. A button needs its own strings; none are proposed here.
