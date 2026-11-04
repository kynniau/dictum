# Calibration window, "Use this": strings for approval (PROPOSED 04/11/2026; branch feature/calibration-use-feedback)

Owner report, 04/11/2026: in the Calibrate my voice window, pressing "Use this" in the results table appeared to do nothing. It did
work (the app log showed the settings change), but the window gave no feedback. Two things now show it: one line in the status line
under Run test, and the row in use reads "In use".

Facts the wording rests on (verified 04/11/2026, this branch, by reading the code; not by running the real app with a real model):

- Use this writes `whisper.model` and `whisper.beam_size` to settings.json and reloads at once, with no restart:
  `ModelCoordinator.TryWrite` writes the keys then calls `reload()`; `AppShell.Reload` re-reads the settings, `_models.SetDecoding`
  rebuilds the decoder in the background (`ModelManager.SetDecoding`), and `Models.Ensure` starts loading the new model in the
  background (`ModelManager.BeginLoad`).
- While the new model loads, the dictation hotkey is ignored: `AppShell` builds the controller with `hotkeyBlocked: () => !_models.IsReady`.
  So the change applies from the next dictation, once the model has loaded.
- The line is only shown when the app's own answer to "which model is in use" (`ModelCoordinator.CurrentName`, the model loaded or
  loading) and settings.json's `whisper.beam_size` match the row pressed. If the file cannot be written, or model files are moving,
  or the model could not be switched to, the line is not shown.

| # | Surface | PROPOSED words | Source / what it claims |
|---|---|---|---|
| 1 | Calibration window, status line under Run test (a polite live region, so a screen reader announces it), after Use this took effect | "Dictum now uses {model} ({decoding}) from the next dictation, once the model has loaded." e.g. "Dictum now uses base.en (greedy) from the next dictation, once the model has loaded." {decoding} is "greedy" or "beam search (5)", as the row's own Model · Decoding column says it. | `CalibrationWindow.UsingLine` (CalibrationResults.cs). Claims: the setting is saved and in force now, no restart; the new model is loading in the background and the hotkey is ignored until it is ready. Proposed in the brief as "...from the next dictation."; "once the model has loaded" added because the hotkey is ignored while it loads (see Facts). |
| 2 | Accessible name of the Use this button on the row in use (read by a screen reader; not drawn). The button's drawn label is the existing "In use". Every other row keeps its existing name, "Use {model} with {decoding}" (with " (recommended)" on the recommended row), unchanged. | "{model} with {decoding}: in use" (with " (recommended)" before the colon on the recommended row). e.g. "base.en with greedy (recommended): in use" | `CalibrationWindow.UseName` (CalibrationResults.cs). Claims: this row is what dictation uses now. |

Existing text, used on a new surface (nothing reworded; listed so the owner knows where it now appears):

| # | Existing text | Where it now appears |
|---|---|---|
| 3 | "In use" (`ModelsPage.InUseLabel`, Settings > Models) | The drawn label of the Use this button on the row in use. On Settings > Models it is a tag; here it stays a real button (pressing it does nothing) so keyboard focus is not lost when Use this is pressed. |
| 4 | "Model files are being moved; try again when it finishes." (`ModelCoordinator.MovingRefusal`, APPROVED 22/10/2026 22:52 AEDT) | The status line, when Use this is pressed while model files move. Run test already says it there. This is a behaviour change: Use this used to write the setting during a move (the new model then loaded after the move). It now refuses, so the success line can never be shown while nothing is loaded. |
| 5 | "settings.json could not be updated (it is not valid JSON, holds a key twice, or is read-only). Fix it, then choose again." (`ModelCoordinator.WriteFailed`) | The status line, when Use this cannot write settings.json. The tray balloon already says it; the window used to say nothing, because the writer returns false rather than throwing and the window dropped the result. I have not found an APPROVED marker on this text. |

Not covered, for the owner: if the model pressed cannot be loaded (its file was deleted since the test), the tray balloon says so
(`ModelCoordinator.Ensure`), the old model stays, and this window shows neither the success line nor the "In use" mark on that row. If
DICTUM_MODEL_PATH or a --model launch override is set, Use this still writes settings.json but that override keeps deciding the model;
the "In use" mark follows the model actually loaded, so the row pressed is not marked and no success line is shown. Neither case has
a line of its own; say if you want one.
