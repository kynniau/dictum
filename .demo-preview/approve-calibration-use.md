# Calibration window, "Use this": strings for approval (PROPOSED 04/11/2026; branch feature/calibration-use-feedback)

Owner report, 04/11/2026: in the Calibrate my voice window, pressing "Use this" in the results table appeared to do nothing. It did
work (the app log showed the settings change), but the window gave no feedback. Two things now show it: one line in the status line
under Run test, and the row in use reads "In use".

Facts the wording rests on (verified 04/11/2026, this branch, by reading the code and by headless tests; not by running the real app with a real model):

- Use this writes `whisper.model` and `whisper.beam_size` to settings.json and reloads at once, with no restart:
  `ModelCoordinator.TryWrite` writes the keys then calls `reload()`; `AppShell.Reload` re-reads the settings, `_models.SetDecoding`
  rebuilds the decoder in the background (`ModelManager.SetDecoding`), and `Models.Ensure` starts loading a changed model in the
  background (`ModelManager.BeginLoad`).
- When the MODEL changes, the dictation hotkey is ignored while it loads: `AppShell` builds the controller with
  `hotkeyBlocked: () => !_models.IsReady`. So the change applies from the next dictation, once the model has loaded (row 1).
- When only the DECODING changes (same model, greedy to beam search or back), no model loads: `ModelManager.SetDecoding` rebuilds the
  decoder and `IsReady` stays true. So the line does not say "once the model has loaded" (row 2).
- While cloud speech is on (`speech.provider` openai-compatible, consent on record), `CloudSpeech.Create` sends each recording to
  the service, and the local model is only the fallback (`CloudSpeech.cs`, the doc on `Create`). "Dictum now uses {model}" would then
  tell the person their audio stays on this PC when it does not. So that case says what was saved and, for the fallback, the owner's
  APPROVED Settings > Models words (row 3).
- A model whose load FAILS is not in use. `ModelManager.BeginLoad` sets `ModelPath` at once and a fault (`Finished`) does not reset it,
  so `ModelCoordinator.CurrentName` keeps naming the model that failed, while dictation keeps the old model (`GetAsync`: "a failed reload keeps
  the previously loaded model working"). The window therefore also asks `ModelManager.LoadFailed` (through `AppShell.SpeechLoadProblem`)
  before it marks a row or says a line, and it follows `AppShell.Changed` (raised when a load starts, finishes or fails) so a line said at
  the click, before the background load ended, is replaced if that load then fails. The replacement is the tray balloon's own text for the
  failure (`AppShell.LoadBalloon`, from `ModelFailure.Assess`), not new words. The headless test drives a real faulted load through `ModelManager`.
- The line is only shown when the app's own answer to "which model is in use" (`ModelCoordinator.CurrentName`, the model loaded or loading,
  whose latest load has not failed) and settings.json's `whisper.beam_size` match the row pressed. If the file cannot be written, or model
  files are moving, the line is not shown.

| # | Surface | PROPOSED words | Source / what it claims |
|---|---|---|---|
| 1 | Status line under Run test (a polite live region, so a screen reader announces it), after Use this changed the MODEL and it is in use | "Dictum now uses {model} ({decoding}) from the next dictation, once the model has loaded." e.g. "Dictum now uses base.en (greedy) from the next dictation, once the model has loaded." {decoding} is "greedy" or "beam search (5)", as the row's own Model · Decoding column says it. | `CalibrationWindow.UsingLine` (CalibrationResults.cs). Claims: saved and in force now, no restart; the new model is loading in the background and the hotkey is ignored until it is ready. All words PROPOSED (the brief proposed "...from the next dictation."; "once the model has loaded" is added for the reason in Facts). |
| 2 | The same status line when only the DECODING changed (same model) | "Dictum now uses {model} ({decoding}) from the next dictation." e.g. "Dictum now uses tiny.en (beam search (5)) from the next dictation." | Same method. Claims: saved and in force now; no model loads, so nothing to wait for. All words PROPOSED. |
| 3 | The same status line while cloud speech sends each recording to a service | "Saved: {model} ({decoding}) is the speech model on this PC; with cloud speech on, {host} does this and the model here is the fallback." e.g. "Saved: base.en (greedy) is the speech model on this PC; with cloud speech on, speech.example.com does this and the model here is the fallback." | Same method. APPROVED words: "with cloud speech on, {host} does this and the model here is the fallback" (22/10/2026 18:41 AEDT, owner, do not reword: the second half of `ModelsPage.SpeechIntroCloudSentence`; the code cuts it from that sentence so the words cannot drift; the final full stop is mine). PROPOSED words: the frame "Saved: {model} ({decoding}) is the speech model on this PC; ". Claims: the setting is saved and this model is the one on this PC; it makes no claim that it hears dictation. |
| 4 | Accessible name of the Use this button on the row in use (read by a screen reader; not drawn). The button's drawn label is the existing "In use". Every other row keeps its existing name, "Use {model} with {decoding}" (with " (recommended)" on the recommended row), unchanged. | "{model} with {decoding}: in use" (with " (recommended)" before the colon on the recommended row). e.g. "base.en with greedy (recommended): in use" | `CalibrationWindow.UseName` (CalibrationResults.cs). Claims: this row is what dictation uses now. PROPOSED. |

Existing text, used on a new surface (nothing reworded; listed so the owner knows where it now appears):

| # | Existing text | Where it now appears |
|---|---|---|
| 5 | "In use" (`ModelsPage.InUseLabel`, Settings > Models) | The drawn label of the Use this button on the row in use. On Settings > Models it is a tag; here it stays a real button (pressing it does nothing) so keyboard focus is not lost when Use this is pressed. |
| 6 | "Model files are being moved; try again when it finishes." (`ModelCoordinator.MovingRefusal`, APPROVED 22/10/2026 22:52 AEDT) | The status line, when Use this is pressed while model files move. Run test already says it there. This is a behaviour change: Use this used to write the setting during a move (the new model then loaded after the move). It now refuses, so the success line can never be shown while nothing is loaded. |
| 7 | "settings.json could not be updated (it is not valid JSON, holds a key twice, or is read-only). Fix it, then choose again." (`ModelCoordinator.WriteFailed`) | The status line, when Use this or the "Grammar assist for this user" box cannot write settings.json (the box also goes back to the saved value). The tray balloon already says it; the window used to say nothing, because the writer returns false rather than throwing and the window dropped the result. I have not found an APPROVED marker on this text. |
| 8 | The tray balloon's text for a failed speech model load: "{reason} (Speech model: Settings > Models)" (`AppShell.LoadBalloon`, with the reason from `ModelFailure.Assess`) | The status line, when the model Use this switched to then fails to load (the old model keeps hearing dictation). I did not look for approval markers on these texts. |

Not covered, for the owner: if DICTUM_MODEL_PATH or a --model launch override is set, Use this still writes settings.json but that override keeps
deciding the model; the "In use" mark follows the model actually loaded, so the row pressed is not marked and no success line is shown. That
case has no line of its own; say if you want one. Settings > Models' own "In use" mark (`ModelPages.cs`, `inUse = e.Name == Shell.Models.CurrentName`)
has the same blind spot for a failed load; it is not changed here.
