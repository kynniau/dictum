# Two-model check: Calibration "Use this" status line while cloud speech is on (PR 89, approval row 3)

Recorded by the Dictum orchestrator, 04/11/2026. Standing order: Fable decides, GPT 6.1 sol challenges, Kyle signs.

## Why it needed the check
The re-lens on 3ac83f0 (Opus, 22:38 AEDT) found the lane's draft reused the APPROVED Settings > Models clause
("with cloud speech on, {host} does this and the model here is the fallback", Kyle 22/10/2026 18:41 AEDT) in a new
sentence, where "does this" lost its antecedent. It is wording about where audio goes.

## Fact checked by the orchestrator before the challenge
CloudSpeechEngine.TranscribeAsync (src/Dictum.Core/Transcribe/CloudSpeechEngine.cs:102-122): every cloud failure except
the user cancelling falls back to the local model, waiting for it to load. In the waiting-for-key state nothing is sent
and the local line shows instead (CloudSpeech.cs:38, :102; re-lens).

## Fable (decider), reported 22:40 AEDT
"Saved: {model} ({decoding}) is now the fallback. Cloud speech is on, so {host} turns your voice into words; Dictum uses this model only if that fails."
Reuses "turns your voice into words", "cloud speech ... on", "{host}", "fallback" in their original sense; drops "does
this" and "the model here" (ambiguous in a list of models); no timing claim.

## GPT 6.1 sol (challenge), by 22:41 AEDT: agree with change
"Saved: {model} ({decoding}) is now the fallback. Cloud speech is on, so each recording is sent to {host} to turn your voice into words; Dictum uses this model only if the cloud request fails."
Reasons: says outright that recordings are sent (clearer privacy consequence); "the cloud request" is clearer than
"that" through a screen reader; "only if" is safe.

## Disagreement for Kyle
Two words differ: GPT adds "each recording is sent to {host}" and replaces "that" with "the cloud request".
Orchestrator recommends GPT's (more explicit about where audio goes). Kyle decides.

## Owner decision
APPROVED: GPT's sentence. Kyle, 04/11/2026 22:48 AEDT ("gpt"), after both were put to him with the orchestrator recommending GPT's.
