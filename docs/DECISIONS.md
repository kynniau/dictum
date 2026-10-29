# Decisions

Judgement calls made while building v1 (15/10/2026), one line of reasoning each. SPEC.md is the brief; this file
records where it was silent or where facts on the ground changed it. "Verified" means checked this session against
the source named; "observed" means measured on the development machine (Windows 11, Xeon W-1250P, Quadro RTX 4000,
driver 596.72, no CUDA Toolkit); "recalled" means from memory and unverified.

## Dependencies

| Package | Version | Why | Source read |
|---|---|---|---|
| `Whisper.net` | 1.9.1 | In-process whisper.cpp binding required by the spec; has `ProcessAsync(float[], CancellationToken)` and a net10.0 TFM. | https://github.com/sandrohanea/whisper.net README (fetched via GitHub API 15/10/2026); package XML docs |
| `Whisper.net.Runtime` | 1.9.1 | CPU native runtime, always the fallback. Needs AVX/AVX2/FMA/F16C and the VC++ 2022 runtime (present here). | same README |
| `Whisper.net.Runtime.Vulkan` | 1.9.1 | GPU runtime without a toolkit install. The README lists "Vulkan Toolkit (>= 1.4.321.1)" as a prerequisite, but **observed**: with only the driver's `vulkan-1.dll` it loads and runs on the GPU (log: `ggml_vulkan: 0 = Quadro RTX 4000`, `Vulkan0 total size = 487.00 MB`). | same README; observed 15/10/2026 |
| `Whisper.net.Runtime.NoAvx` | 1.9.1 | CPU runtime for processors without AVX/AVX2/FMA/F16C (old or virtual CPUs), required by SPEC §2 as the last fallback. | same README; nuget.org |
| (not used) `Whisper.net.Runtime.Cuda` / `.Cuda12` | — | Excluded: the README requires an installed CUDA Toolkit (>= 13.0.1 / >= 12.4.1) and this machine has none. | same README |
| `NAudio.Wasapi` | 3.1.0 | WASAPI capture (`WasapiRecorder`/`WasapiRecorderBuilder`, the non-obsolete 3.x API) plus `NAudio.Core` (WDL resampler, WAV reader) transitively; the `NAudio` meta-package would also pull ASIO/MIDI/WinMM/WinForms parts we do not use. NuGet also lists `NAudio.Wasapi` 22.0.0, but its registration says `"listed": false` (published 1900-01-01), so it was ignored. | nuget.org flat container and registration (15/10/2026); package XML docs |
| `Anthropic` | 12.57.0 | Official C# SDK for the optional `anthropic` provider (the default provider was `anthropic` in v1; `local` since 15/10/2026). | bundled skill `claude-api/csharp/claude-api/README.md`; package XML docs |
| `LLamaSharp` | 0.27.0 | In-process llama.cpp binding for the default `local` cleanup provider (SPEC §5). APIs used, each read in the source before use: `NativeLibraryConfig.All.WithVulkan/WithCuda/WithAutoFallback/WithLogCallback`, `ModelParams { ContextSize, GpuLayerCount }`, `LLamaWeights.LoadFromFile`, `LLamaTemplate` (chat template from the GGUF metadata), `StatelessExecutor.InferAsync` with `InferenceParams { MaxTokens, SamplingPipeline = GreedySamplingPipeline, AntiPrompts }`. | https://raw.githubusercontent.com/SciSharp/LLamaSharp/7cbbc45e421d55794d5050d126e0b96511007007/README.md, `.../docs/Tutorials/NativeLibraryConfig.md` and `.../LLama/LLamaStatelessExecutor.cs` at the same commit; https://api.nuget.org/v3-flatcontainer/llamasharp/0.27.0/llamasharp.nuspec (all 15/10/2026) |
| `LLamaSharp.Backend.Cpu` | 0.27.0 | CPU llama.cpp natives in `noavx`, `avx`, `avx2` and `avx512` builds; LLamaSharp picks one for the CPU at load, so old and virtual CPUs are covered without a separate package. | https://www.nuget.org/packages/LLamaSharp.Backend.Cpu; observed in the `runtimes/win-x64/native/` folders |
| `LLamaSharp.Backend.Vulkan` | 0.27.0 | Vulkan llama.cpp natives, same no-toolkit reasoning as Whisper.net's Vulkan runtime. Pulls `LLamaSharp.Backend.Vulkan.Windows` and `.Vulkan.Linux` 0.27.0 transitively; `publish.ps1` deletes the Linux, macOS and arm64 files. | https://www.nuget.org/packages/LLamaSharp.Backend.Vulkan; `obj/project.assets.json` |
| (not used) `LLamaSharp.Backend.Cuda12` / `.Cuda11` | — | Excluded by the brief (Vulkan and CPU only), as for whisper. | same README |
| `xunit.v3` (tests) | 4.0.2 | xUnit per spec. | nuget.org |
| `System.Speech` (tests) | 10.0.13 | `SpeechSynthesizer` for the integration test's synthesised WAV (not part of the Windows Desktop shared framework). | nuget.org |

Not added: `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`. The .NET 10 SDK refuses VSTest mode for
xunit.v3 4.x projects (observed: "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on
.NET 10 SDK"), so `global.json` opts `dotnet test` into Microsoft.Testing.Platform and those two packages are unneeded.

## Platform and build

- One app project plus a test project referencing it (the spec's layout); pure logic sits in `Core/`, `Hotkey/`, `Settings/`, `Llm/`, `Insert/` so tests need no UI.
- `Program.Main` owns startup and `App.xaml` is a resource page, so CLI modes never create a window or tray icon.
- `OutputType` stays `WinExe` (no console window at login). CLI modes `AttachConsole` to the parent when stdout is not redirected; PowerShell only waits for a GUI exe when its output is piped, so the README says `| Out-Host`.
- `app.manifest`: per-monitor-v2 DPI (WPF reads it from the manifest) and Common Controls v6 (needed by the WinForms `TaskDialog`). The WinForms analyser warning WFO0003 is suppressed with a comment: it targets WinForms-only DPI APIs.
- `System.IO` and `System.Net.Http` added as global usings (WPF's implicit usings omit them); WinForms/System.Drawing removed from global usings to avoid `Application`/`Clipboard`/`MessageBox` ambiguity.
- `scripts/publish.ps1` publishes **self-contained** by default with `SatelliteResourceLanguages=en` (since the LLamaSharp runtimes were added, 15/10/2026: 304 files, 323,738,954 bytes = 308.7 MiB, zip 119,271,754 bytes = 113.7 MiB (re-published after lens pass 4), of which the llama.cpp natives are 83,073,024 bytes; before them: 271 files, 238,207,152 bytes = 227.2 MiB; zip 91,246,613 bytes = 87.0 MiB; before dropping the 13 framework localisation folders it was 492 files, 256,046,148 bytes = 244.2 MiB, zip 97,357,411 bytes; sizes in binary MiB as the script prints them) so `dist\Dictate` runs on a machine with no .NET 10 Desktop Runtime. This machine happens to have one machine-wide (`C:\Program Files\dotnet`: Microsoft.WindowsDesktop.App 10.0.12, runtime only, verified with `dotnet --list-runtimes`), so `-FrameworkDependent` also works here. The script picks the first `dotnet` that lists a 10.x SDK, because the one on PATH has runtimes only (observed: publish failed with "Download a .NET SDK" before this check). It deletes the linux/arm64/x86 native folders Whisper.net's targets copy for every platform. (superseded: Batch 2 and the fix batch below give the current publish figures)
- Whisper runtime order (SPEC §2): `Vulkan → Cpu → CpuNoAvx`; `whisper.gpu = "cpu"` gives `Cpu → CpuNoAvx`. Whisper.net 1.9.1 has no `ForcedRuntimeLibrary` (verified: absent from its XML docs and DLL; the GitHub README describes a newer API), so "CPU forced" for the benchmark means this order.

## Interaction and hotkeys

- A single-key hotkey fires regardless of other held modifiers; a chord needs its modifiers held and tolerates extra ones. Simple and predictable; exact-match chords can come later if conflicts appear.
- Chords made only of modifiers (`Ctrl+Win`) complete in any press order; otherwise the non-modifier must be pressed last.
- When a chord containing Win or Alt completes (unsuppressed), the hook thread injects the unassigned key 0xE8 so releasing Win/Alt does not open Start or a menu bar (AutoHotkey's "menu mask" technique; recalled, not documented by Microsoft). It is posted to the hook thread's message loop so the callback stays match-and-post only.
- `ReleaseModifiers` sends the same mask before releasing a held Win/Alt, for the same reason.
- Esc is reserved and rejected as a hotkey. Raw codes like `0x7B` are accepted for keys without a name.
- Our own `SendInput` events carry `dwExtraInfo = 0x44494354` ("DICT") and the hook ignores them, so pasting never re-triggers or confuses the matcher.
- The matcher tracks key state from the event stream and re-checks modifiers with `GetAsyncKeyState`, dropping ones whose key-up was lost (e.g. to the secure desktop).
- Auto-repeat is filtered twice: in the matcher (never signals) and in the state machine (a down while the key is down is ignored), so the consumed second tap's repeats cannot stop hands-free.
- **Hook watchdog: not built.** No automatic re-install if Windows silently removes the hook (`LowLevelHooksTimeout`). Mitigations: dedicated thread at AboveNormal priority whose callback only matches and posts; "Reload settings" re-installs the hook; README troubleshooting. Marked `// simplification:` in `KeyboardHook.cs` with the trigger to revisit (reports of the hotkey dying under load).
- Disabling via the tray during processing cancels the session (nothing is pasted), matching "Any → Idle".
- Esc during processing also flashes "Cancelled".
- Busy is a 0.7 s flash that reverts to "Transcribing…"/"Polishing…".

## Audio

- Capture uses the device mix format in shared mode and converts in code (decode → average channels → NAudio `WdlResampler` in input-driven mode → 16 kHz), as the 16 kHz-direct request is unverified on every device. The decoder reads WAVE_FORMAT_EXTENSIBLE by SubFormat so float mix formats are not misread as int32.
- Channel downmix is our own averaging, not `StereoToMonoSampleProvider`, so 1-, 4- or 6-channel mic arrays work.
- "Peak level" for `silence_threshold` = the loudest 20 ms block RMS, the same quantity the pill meter shows. A single-sample peak would let a click defeat the silence gate.
- The device is opened per recording and released after, so the mic indicator is off when idle. Start latency and first-packet latency are logged on every recording.
- `audio.device` matches an endpoint ID or a case-insensitive name fragment; not found → warning and the default device.
- `--transcribe` does not apply the min-length/silence gate: it is a verification tool for a known file.

## Transcription

- Segment text is joined, whisper's non-speech annotations (`[BLANK_AUDIO]`, `(silence)`, `[MUSIC]`…) are removed, whitespace collapsed.
- Recommendation (SPEC §10, revised 15/10/2026): GPU runtime → medium.en; CPU only → base.en, small.en offered as "more accurate, a few seconds slower". The physical-core rule and its interop were removed. "GPU runtime loaded" = the Vulkan runtime loaded **and** whisper.cpp did not report 0 Vulkan devices (parsed from its `ggml_vulkan: Found N Vulkan devices` log line; unknown counts as a GPU). The runtime is probed without a model via `WhisperFactory.GetRuntimeInfo()`. (superseded by the fix batch: recommendations and presets now need a discrete GPU; a machine with integrated graphics only counts as CPU; warm-ups still run on any Vulkan device)
- Model overrides, strongest first: `--model` (one launch; a tray choice replaces it), `DICTATE_MODEL_PATH` (tests), `whisper.model`. The tray tooltip and menu tick show the effective model.
- First run with an override present skips the picker and writes the recommendation, because scripted and test launches must not block on a modal dialog; the user has not chosen yet, so nothing they chose is overridden.
- The hotkey is ignored (pill flashes "Busy") whenever no model is ready: while loading, during a hot-swap, or when none is available. The spec names no pill text for this; "Busy" is the closest existing one.
- A failed model reload keeps the previously loaded model working.
- Cancellation (observed 15/10/2026, `CancellationMeasurementTests`, CPU forced, small.en, 62.8 s input whose uncancelled run took 20.9 s): cancelling 200 ms in, `TranscribeAsync` returned 0 ms after `Cancel()` with `TaskCanceledException`, but native whisper kept about 4 cores busy for ~5.1 s more before stopping (well short of the full run). Whisper.net 1.9.1 source (`WhisperProcessor.cs` at tag 1.9.1) explains it: `ProcessAsync` throws as soon as the token fires, while `whisper_full` runs on until whisper.cpp next polls the abort callback; the processor's own semaphore makes the next run wait (an earlier run started immediately took 10.1 s against a 5.6 s baseline), so there is no concurrent use. The same window made `WhisperProcessor.Dispose` throw "Cannot dispose while processing" (observed failing on the reverted behaviour), so `WhisperEngine.Dispose` now uses `DisposeAsync` and waits (5.3 s measured right after a cancel). With Vulkan (`DICTATE_CANCEL_RUNTIME=auto`, same input, uncancelled 2.2 s) the call also returned at once and Dispose right after the cancel took 40 ms, so the native run-on is at most about 40 ms there; the next clip took 309 ms against a 375 ms baseline. README's Esc row gives both figures. `WhisperEngine.Dispose` runs `DisposeAsync` on the thread pool because Whisper.net awaits without `ConfigureAwait(false)` and the UI thread's dispatcher has stopped by the time the host is disposed at quit.
- CPU fallback (observed 15/10/2026, final build): a copy of `dist\Dictate` without `runtimes\vulkan` (simulating a machine where Vulkan cannot load) starts with no dialog beyond the first-run picker (skipped here by an override), loads the CPU runtime, recommends and writes `base.en`, and the tray tooltip reads `Dictate · base.en · CPU · RightCtrl` (re-observed with the final rule, 16:03 15/10/2026). A real GPU-less VM was not available to test.
- Measured (observed 15/10/2026, small.en, 4.66 s clip): Vulkan 317–373 ms per run after the first; the first-ever Vulkan run took 12.3 s while the driver compiled shaders; CPU 5,195 ms. No warm-up pass was added then because later processes were already fast; one was added later for GPU loads (see "Local cleanup").
- In-app download uses our own `HttpClient` against `huggingface.co/ggerganov/whisper.cpp` (not Whisper.net's downloader, which uses a different repository), writes `.part`, verifies the known size (and, since the scope change, the SHA-256; see "Local cleanup"), then renames. Sizes in `ModelCatalog` were verified by HTTP HEAD (`X-Linked-Size`) on 15/10/2026; the redirect went to `us.aws.cdn.hf.co`.

## LLM cleanup

- `max_tokens` input estimate = characters / 3, rounded up (generous; Haiku 5.5's tokenizer yields more tokens per character than older models, and truncation costs a fallback).
- Anthropic client: `MaxRetries = 1`, SDK timeout = `llm.timeout_ms`, plus our own linked cancellation; the whole call is bounded by `timeout_ms`. Thinking disabled + effort low are accepted on claude-haiku-5-5 at effort ≤ high (bundled skill `shared/error-codes.md`). The request wire format is pinned by a test against a recording `HttpClient` handler.
- Commentary/answer detection (v1; now part of the validator under "Local cleanup" below, which falls back to the rule-based tidy rather than the raw text): reply contains a framing phrase the speaker did not say ("here's", "let me know", "I'm sorry"…); raw contained "?" but reply does not; or, for replies of 6+ words, fewer than half the words appear in the raw text. Wrapping quotes are stripped rather than rejected. A false rejection only costs polish; a false acceptance pastes wrong words.
- Secrets (SPEC §5, revised 15/10/2026): process/user/machine environment variable > `%APPDATA%\Dictate\.env` > `.env` beside the exe. An empty environment variable counts as unset. Environment variables are read at process start, so a newly set machine variable needs a Dictate restart.
- Anthropic endpoint pinned to `https://api.anthropic.com` (`llm.base_url` overrides it). Observed: before the fix, with `ANTHROPIC_BASE_URL=https://attacker.example` set, the SDK sent the request to `attacker.example` (wire test failed with Actual "attacker.example"); the test now passes. `llm.base_url` is reused for anthropic rather than adding a key.
- `llm.base_url` with `provider = anthropic` logs a warning at every settings load and shows one balloon per URL ("Anthropic requests will go to <host>"), because a gateway URL left over from `openai-compatible` would otherwise silently receive the key and transcripts. Tested via the log.
- Every log line passes through a redactor that replaces configured key values with `[redacted]`; HTTP errors from the OpenAI-compatible provider report the status only, never the body.
- The OpenAI-compatible provider also treats `finish_reason: "content_filter"` and a `refusal` field as refusals.

## Insertion, pill, tray

- Clipboard via WinForms `Clipboard.SetDataObject(data, copy, 10 retries, 50 ms)`, the framework's own retry for a clipboard held open by another app. Snapshot/restore covers UnicodeText, Text, RTF, HTML, CSV; if the snapshot had none of those the clipboard is cleared on restore. A failed restore leaves the transcript and logs a warning.
- Transcripts carry `CanIncludeInClipboardHistory` = DWORD 0 and `CanUploadToCloudClipboard` = DWORD 0 (names and semantics verified on https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats, page updated 15/04/2025). `ExcludeClipboardContentFromMonitorProcessing` was not used: the two explicit DWORDs say exactly what is meant. A gated test reads the formats back from the Win32 clipboard (observed: both 0; absent when `insert.clipboard_history` is true). New setting `insert.clipboard_history` (default false) because the brief said "by default".
- A clipboard snapshot failure (`restore_clipboard`) is logged and the paste goes ahead without restoring, so the transcript is never lost.
- Insertion runs on the WPF UI thread (STA, required by the OLE clipboard) with async delays; the session token is checked immediately before the first keystroke.
- Typing mode sends 32 characters per `SendInput` batch with a 5 ms pause; `\r\n` and `\n` become Enter.
- The pill is positioned in physical pixels with `SetWindowPos(SWP_NOACTIVATE)` using the target monitor's DPI; the extended styles are set in `SourceInitialized`, before the first show.
- Tray icons are drawn at startup with GDI+ (no binary assets). Changing icon/tooltip never activates anything; opening the tray menu does (user-initiated).
- Single instance: named mutex; a second launch signals a named event and the running copy shows the balloon.
- First-run picker and missing-model prompt use the WinForms `TaskDialog` (framework primitive with radio buttons and custom buttons). Picking a not-downloaded model in the picker counts as the request to download it (the dialog says it will be fetched from huggingface.co). On a reload whose model path did not change, Dictate does not re-prompt; the Speech model menu is the way to fix it.
- No model dialog ever opens on a settings reload: a reload to a model that is not on disk shows a balloon ("model X is not downloaded; keeping Y") and the loaded model stays in use. Reason: the dialog could take focus mid-dictation and its Esc/close maps to Quit. The Download / Choose file… / Quit dialog is startup-only (SPEC §10). Tested with the dialogs behind `IModelPrompts`.
- A file-watcher reload re-installs the keyboard hook only if `hotkey` or `suppress_hotkey` changed (otherwise the running hook and matcher, with their key state, are left alone); the tray's "Reload settings" always re-installs it because that is the documented recovery for a hook Windows removed.
- "Polishing…" goes through the controller inbox like every other pill change, so `pill.enabled = false` and a late report after Esc can no longer show it.
- Writing `enabled`/`whisper.model` back to settings.json re-serialises the file: JSON comments are lost, other keys kept, and an unparseable file is never overwritten.
- Logs: one file per day, newest 14 kept, levels from `log_level`.

## Tests and verification

- The pill focus test needs an interactive desktop, so it is gated by `DICTATE_UI_TESTS=1`. It first checks that an ordinary window *can* take the foreground (so the test can detect activation), then asserts the pill never changes `GetForegroundWindow()`. If no window can be foreground (observed here: `GetForegroundWindow()` = 0 and `SetForegroundWindow` fails in this RDP session, inside and outside the sandbox), it skips with that reason rather than passing vacuously. Later the same session became interactive and the test ran in strong mode: the control window did take the foreground, the pill did not change it across every pill state (observed 15/10/2026).
- The whisper integration test is gated by `DICTATE_MODEL_PATH` (spec §12).
- Two more gated end-to-end tests: `DICTATE_UI_TESTS=1` injects F24 with `SendInput` and checks the hook thread posts exactly one down and one up (repeat ignored, nothing when disabled); `DICTATE_AUDIO_TESTS=1` opens the default microphone for one second (audio stays in memory) and checks ~1 s of 16 kHz audio comes back. The hook test skips (instead of failing) when `SendInput` injects nothing, which Windows does while the session has no input desktop (observed: it passed with the RDP session active and injected 0 events later with it inactive).
- `scripts/download-model.ps1` mirrors the C# catalogue (PowerShell cannot read it); a unit test parses the script and fails if the two differ.

## Models and deployment (scope change of 15/10/2026)

- Catalogue sizes: tiny.en 77,704,715 and base.en 147,964,211 fetched by HTTP HEAD (`X-Linked-Size`) on 15/10/2026; the other eight match the spec and were re-checked the same way (large-v3-turbo-q5_0 574,041,195 included). Trade-off lines state only what the names and sizes support; the README's memory figures (tiny ~273 MB, base ~388 MB, small ~852 MB, medium ~2.1 GB) were verified against the "Memory usage" table of the ggml-org/whisper.cpp README fetched 15/10/2026; they are whisper.cpp's figures, not measured here.
- Sizes display in decimal units with MB floored (487,614,201 → "487 MB", as in the spec's example); GB to one decimal.
- The tray "Speech model" submenu replaces the old "Download model…" item: it covers download, switch and choose-file. It is rebuilt on every open so "downloaded" is current. (Revised 15/10/2026: a second selection while a download runs now cancels it and proceeds; see "Downloads and model checks". Revised again 19/10/2026: it asks first; see "Model choice never silently cancels a download".)
- `--download-model` accepts any valid short name; outside the catalogue it warns and skips the size check (a wrong name fails with HTTP 404, exit 1). A failed or cancelled download deletes its `.part` file. Progress is one `\r` line on a console and one line per 10 % when redirected (Intune/remote logs). (superseded: after the retries run out the `.part` is kept, see Lens pass 3; a user cancel still deletes it)
- App folders come from the `APPDATA` / `LOCALAPPDATA` environment variables, falling back to the known-folder API. Reason: install.ps1 uses the same variables, so the two always agree, and an install can be tested in a scratch profile (observed: the scratch install test kept the real profile untouched).
- `install.ps1`: copies (zip expanded to `%TEMP%` first), stops a running copy from the install folder, and only deletes an existing install folder if it contains `Dictate.exe` or is empty. `-Model` downloads through the installed `Dictate.exe --download-model` so the catalogue, host and size checks are the app's own; if no settings.json exists and `-Settings` is not given, it writes `{ "whisper": { "model": "<name>" } }` so the first launch does not open the picker. The Start Menu shortcut is made with `WScript.Shell`.
- `install.ps1` keeps a `.env` beside `Dictate.exe` across upgrades (`-Force` removes it), downloads the model before writing any settings, and runs `Dictate.exe --download-model` with `$ErrorActionPreference = "Continue"` and an explicit exit-code check, because Windows PowerShell 5.1 makes a native stderr line fatal under "Stop" with `2>&1`.
- `uninstall.ps1` adds `-RemoveData` for unattended removal; with neither switch it asks only in an interactive console host and keeps data otherwise. The exe-folder `.env` counts as user data: when kept, the program files are removed and the `.env` stays in `%LOCALAPPDATA%\Programs\Dictate`, where a reinstall finds it. The question is asked before anything is deleted.
- `uninstall.ps1` also accepts a program folder holding only that `.env` (so `-KeepData` followed by `-RemoveData` completes), and removes the HKCU Run value only when it points at this install's `Dictate.exe`.
- `--exit-after <seconds>` (tray mode) runs the same `Application.Shutdown` as tray > Quit; added so the quit path can be checked from a script.
- `publish.ps1` zips to `dist/Dictate-<version>-win-x64.zip` (version from the exe's ProductVersion) and also strips the no-AVX runtime's linux/x86 folders.
- Benchmark method: one synthesised 10.46 s WAV, `--transcribe --no-llm --model <name>` on the published build, two fresh processes per cell, second run reported; CPU forced via `whisper.gpu = "cpu"`. First runs differed only for Vulkan's first-ever use of a model (driver shader compilation: tiny.en 1.19 s vs 0.24 s).

## Local cleanup (scope change of 15/10/2026)

- **Licences (verified 15/10/2026 from each repo's `LICENSE` and model-card `license:` field):** `Qwen/Qwen2.5-0.5B-Instruct-GGUF` and `Qwen/Qwen2.5-1.5B-Instruct-GGUF`: Apache-2.0 (https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/blob/main/LICENSE, https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/blob/main/LICENSE). `Qwen/Qwen2.5-3B-Instruct-GGUF`: "Qwen RESEARCH LICENSE AGREEMENT", release date 19/09/2024, card `license: other` / `qwen-research`; its grant is "FOR NON-COMMERCIAL PURPOSES ONLY" and commercial users must request a licence (https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/blob/main/LICENSE). So 3B is `CommercialUse = false`: listed with "non-commercial licence", never recommended, and its download confirmation names the licence.
- **SHA-256 pinning (SPEC §5):** every speech and cleanup entry carries the LFS `oid` from `https://huggingface.co/api/models/<repo>/tree/main` (read 15/10/2026, 16:09). The four whisper files already in `%LOCALAPPDATA%\Dictate\models` (tiny.en, base.en, small.en, medium.en) were hashed with `Get-FileHash` and all matched. Both Qwen downloads were verified by the app's own check. `download-model.ps1` carries the same hashes; a unit test fails if the two catalogues differ in any name, size or hash.
- **Download integrity:** SHA-256 is computed while writing the `.part` (`IncrementalHash`), then size and hash are compared before the rename; a mismatch deletes the `.part` and reports both hashes. Retry, resume and the first-use check of files Dictate did not download are under "Downloads and model checks". A file chosen with "Choose .gguf file…" has no catalogue entry and is not hash-checked; the README says so. (superseded by the fix batch: files are matched to the catalogue by name in any folder, so a chosen file with a catalogue name is checked, and a mismatch outside the models folder asks for a rename, never a delete)
- **Backend:** `NativeLibraryConfig.All.WithVulkan(llm.gpu != "cpu").WithCuda(false).WithAutoFallback(true)`, set once per process before the first load (so `llm.gpu` needs a restart). The loaded backend is read from LLamaSharp's own loader log line (`Successfully loaded '<path>\llama.dll'`; a `vulkan` folder means Vulkan), because that names the file that actually loaded; the parser is unit-tested, including a path with spaces (it broke on one before the fix). `GpuLayerCount = 999` offloads every layer when Vulkan loaded, 0 for `cpu`. `ContextSize = 4096` (inferred: prompt, examples, a 5-minute dictation of about 1,000 tokens and its 1,532-token output limit fit; anything larger fails and falls back to the rule-based tidy).
- **Prompting:** SPEC §5 system prompt, then three short raw → cleaned pairs (filler removal; a repeat plus a spoken "question mark"; "scratch that" plus "new line"), then the dictation, through `LLamaTemplate(weights)` so the ChatML template comes from the GGUF metadata, not hard-coded. Greedy (`GreedySamplingPipeline`), `max_tokens = clamp(ceil(1.5 × input tokens) + 32, 64, 2048)`, anti-prompts `<|im_end|>`/`<|endoftext|>` as a backstop to the end-of-turn token.
- **Cancellation:** `StatelessExecutor` ends its loop quietly when the token fires, so a cut-off reply looked finished (observed); the engine now throws `OperationCanceledException` after the loop. Measured with `LocalLlmIntegrationTests.Cancelling_generation_returns_promptly` (cancel 200 ms into a dictation of about 160 words): Vulkan returned 13–20 ms after `Cancel()`; CPU 1.95 s (0.5b) and 3.88 s (1.5b), because the prompt prefill is one native call that only ends when done (inferred from the timings and the executor source, which checks the token once per generated token). Esc puts the state machine back to Idle at once; calls are serialised by a semaphore, so the next cleanup waits for the cancelled one. (The re-prefill per call and its `// simplification:` are retired: the prefix is now cached, see "Lens pass 3 fixes".)
- **Warm-up (reverses the earlier "no warm-up" line under Transcription):** after loading on Vulkan, Dictate runs a 4-token cleanup and a 1 s silent whisper pass, because the first-ever Vulkan cleanup took about 17 s (shader compilation) and timed out into the fallback in `--transcribe` (observed). (Superseded: warm-up now runs on every backend.) `--transcribe` does not warm up, so its first-ever Vulkan run still shows the compile (20.5 s observed once).
- **Validation (all providers):** the spec's five rules (empty; word ratio outside 0.4–1.6 for 8+ words; commentary start; system-prompt wording as any 5-word run; chat-template token), plus the v1 answer guards (framing phrase not dictated; dictated "?" lost; under half the reply's words in the dictation). Added beyond the spec: **coverage**, at least 60% of the dictation's distinct content words (4+ letters, not fillers or spoken commands) must survive when there are 8 or more. Reason: qwen2.5-0.5b on Vulkan returned only the second of two sentences and every other rule accepted it (observed; that exact reply is now a unit test). "Sure," at the start is accepted when the speaker said it (a false rejection was observed). The fallback for every rejection is the deterministic tidy, replacing v1's raw-transcript fallback. The v1 character-length rule (1.5 × raw + 40) was replaced by the spec's word ratio.
- **Deterministic tidy (`none`, and the fallback) (superseded by "Lens pass 3 fixes"):** conservative by design; never invents words and never adds commas. Choices beyond the spec's list: "you know" closing a clause is removed unless it follows do/does/did/don't/didn't/if/whether/what/how (a real question); "that that" and "had had" are kept (often grammatical); stray commas before a stop are dropped; a trailing full stop is added when the text ends in a letter or digit. 17-case table test.
- **CPU recommendation = qwen2.5-0.5b (superseded: now qwen2.5-1.5b, see "Lens pass 3 fixes")** (SPEC §5 rule): measured on the 6-core Xeon with CPU forced, 1.5b added 4.46 s to the 10 s clip (threshold about 2 s), 0.5b 2.48 s. GPU stays 1.5b (0.54 s). Quality cost noted in the README: on CPU 0.5b did not repair whisper's "Umsow… foxer". Benchmark method as for whisper: two fresh `--transcribe` processes per cell, second reported; a 60.9 s second clip; peak working set from the process, GPU memory as the `nvidia-smi` used-memory rise sampled every 250 ms (includes the speech model and driver context).
- **`offline_only`:** downloads are refused before any request (unit-tested with a counting fake server: zero requests), the CLI download commands exit 1, `anthropic` is refused, and `openai-compatible` is allowed only when `base_url` is a loopback address. The last is a decision beyond the spec, which says "cloud providers are refused": a server on the same PC is not cloud, and refusing it would remove the only way to use a larger local model. A refused provider becomes rules-only with one balloon; the About box starts "Offline only". (fix batch: the loopback request now bypasses any proxy, and a loopback server may still relay what it receives, which the README now says)
- **`anthropic` with a local `llm.model`:** since `llm.model` now defaults to `qwen2.5-1.5b`, switching only the provider to `anthropic` would send a non-Anthropic id; a cleanup-catalogue name is mapped to `claude-haiku-5-5`.
- **Missing cleanup model at startup** shows one balloon and uses the rule-based tidy, never the modal Download/Choose/Quit dialog the speech model gets: cleanup is optional and a working tier exists.
- **Tray:** the Cleanup model submenu mirrors Speech model, plus "No AI cleanup (rule-based tidy)" (writes `llm.provider = none`) and "Choose .gguf file…"; picking a model writes `llm.provider = local` and `llm.model` together. The tooltip shows the cleanup model and backend, or `rules` / the cloud provider name.

## Downloads and model checks (bug fix of 15/10/2026)

- **The bug:** every tray download of a large file could fail with `IOException: The decryption operation failed` and the balloon told the user to click again. The installed copy on the development machine (the earlier zip) had no retry. Observed here with the instrumented downloader, every failure had the same chain: `IOException` (HResult 0x80131620) ← `Win32Exception: The specified data could not be decrypted` (0x80004005), i.e. Windows' Schannel TLS layer, at a different byte offset each time (83 MB to 1.59 GB). Git Bash curl (OpenSSL) on the same machine fetched the same files without a drop, per the coordinator.
- **Retry and resume (one implementation, `ModelDownloader`, used by tray, picker, CLI and `install.ps1 -Model`):** up to 8 attempts; waits 1, 2, 4, 8, 16, 16, 16 s; the `.part` file is kept between attempts and resumed with `Range: bytes=<length>-`; a 206 is appended after checking `Content-Range` starts at that byte; a 200 restarts from byte 0 (file truncated, hash reset); size and SHA-256 are verified at the end as before. Transient: `IOException`, `HttpRequestException` without a 4xx status, HTTP 5xx/408/429, and 30 s without a byte (per read, so a stalled connection never hangs the tray). Permanent (no retry): other 4xx; 401/403 report the Hugging Face login message. Bytes are written before they are hashed, so a resume offset always equals the hashed length; disk errors are not retried. Only after the last attempt does the tray say "Model download failed after 8 attempts (connection dropped). Check your network, then choose it again from tray > Speech model" (or Cleanup model). (Superseded: after the last attempt the `.part` is now kept and resumed; see "Lens pass 3 fixes".)
- **Logging:** one warning per failed attempt with the byte offset, the reason and the whole exception chain (type, message, HResult); URL query strings are removed (CDN redirects carry signatures). The negotiated HTTP version and status are logged per attempt.
- **Cancel:** tray "Cancel download" (visible only while downloading) and choosing another model both cancel immediately, including during a backoff wait; the cancelled task deletes its `.part` before the next choice proceeds. This replaces "a second selection while a download runs is refused". Quitting cancels a running download. (Superseded 19/10/2026: choosing a model no longer cancels a download without asking; only "Cancel download" and the Stop answer to the question cancel. See "Model choice never silently cancels a download".)
- **HTTP client:** a dedicated `SocketsHttpHandler` for downloads (connect timeout 30 s, no decompression, no overall timeout because the stall timer bounds each read). .NET sends HTTP/1.1 by default for a hand-built `HttpRequestMessage` (observed: every default-variant attempt logged `HTTP/1.1`), not HTTP/2.
- **TLS and HTTP version, measured 15/10/2026** with the production downloader (gated test `DownloadReliabilityMeasurement`, one fresh process per download, variants interleaved, files to a temporary folder and deleted). Windows' default negotiated TLS 1.3 with both `huggingface.co` and `us.aws.cdn.hf.co` (checked with `SslStream`, PowerShell 5.1 on the same Schannel). Every failure in every variant was the same Schannel error above.

  | File | Variant | Downloads | Dropped attempts per download | Total drops | Needed a retry |
  |---|---|---|---|---|---|
  | large-v3-turbo, 1,624,555,275 B | Windows default TLS (1.3), HTTP/1.1 | 6 | 1, 5, 4, 5, 5, 0 | 20 | 5 of 6 |
  | large-v3-turbo | TLS 1.2 forced, HTTP/1.1 | 6 | 0, 2, 0, 1, 0, 1 | 4 | 3 of 6 |
  | large-v3-turbo | default TLS, HTTP/2 | 3 | 2, 1, 0 | 3 | 2 of 3 |
  | large-v3-turbo | TLS 1.2, HTTP/2 | 3 | 1, 0, 0 | 1 | 1 of 3 |
  | large-v3-turbo-q5_0, 574,041,195 B | default TLS, HTTP/2 | 3 | 0, 0, 0 | 0 | 0 of 3 |
  | large-v3-turbo-q5_0 | default TLS, HTTP/1.1 | 3 | 0, 0, 1 | 1 | 1 of 3 |

  Every download completed (at most 6 attempts). Decision: **TLS 1.2 is pinned for model downloads** (`ModelDownloader.DownloadTls`) because it cut drops about five-fold in the largest sample, and HTTP stays at .NET's default 1.1 because the HTTP/2 samples are small (3 runs) and HTTP/2 was slower on the 574 MB file (32–41 MB/s against 41–53 MB/s). TLS 1.2 is not clean (4 drops), so the KeyUpdate hypothesis is at most partly right (inferred); the resume remains the real fix. Neither the coordinator's "TLS 1.2 is clean" case nor its "similar rates" case applied exactly; this is my call on a five-fold difference. Small samples on one network; other networks may differ.
- **First-use check of files Dictate did not download:** before a catalogue model (matched by file name, any folder) is loaded for the first time by the tray, `--transcribe`, `--selftest` or `--bench-cleanup`, its size and SHA-256 are compared with the catalogue; a mismatch is reported (balloon and tooltip "model error"; `--selftest` `[FAIL]`; CLI exit 1) and the file is not loaded. A match is recorded in `%LOCALAPPDATA%\Dictate\verified-models.json` keyed by full path with size and last-write time, so later loads skip the hash (a `// simplification:` in `ModelIntegrity.cs` names that ceiling). Downloads by Dictate are recorded directly. Measured first-use hashing here: medium.en 1.53 GB 4.0 s, qwen2.5-1.5b 3.0 s, qwen2.5-0.5b 1.3 s, large-v3-turbo 4.1 s, large-v3-turbo-q5_0 1.5 s. The two large-v3-turbo files the coordinator fetched with curl both matched the catalogue (`--selftest --model …`: "SHA-256 verified").
- **`download-model.ps1`:** now uses Windows' own `curl.exe` (8.22.0 here, verified with `--version`) with `--continue-at -` in an 8-attempt loop with the same waits, falling back to `Invoke-WebRequest` without resume if curl is absent. HTTP 4xx is not retried; 401/403 explain the login. It is also Schannel-based: run once with Windows' default TLS on large-v3-turbo it hit `curl: (56) schannel: ... SEC_E_DECRYPT_FAILURE (0x80090330)` four times, resumed each time and finished with the hash verified in 58 s (observed), which also shows the drop is in Schannel, not in .NET. It now pins TLS 1.2 too (`--tlsv1.2 --tls-max 1.2`, verified working on tiny.en; not re-measured on a large file).
- **`install.ps1 -ModelsFrom` copied into the wrong folder** (`%LOCALAPPDATA%\Dictatemodels`: a backslash lost in an earlier scripted edit). Found by the scratch-profile double install; fixed; `SettingsTests.Scripts_use_the_app_models_folder` now pins both scripts to the app's `Dictate\models` and was observed failing on the reverted script.

## Settings repair at load (15/10/2026)

- The development machine's settings.json was written by the first build with `"provider": "anthropic", "model": "claude-haiku-5-5"` (that build's defaults). `EffectiveSettings.Cleanup` runs at every load (tray, `--transcribe`, `--selftest`, `--bench-cleanup`): `anthropic` without `ANTHROPIC_API_KEY` becomes `local` (warning logged; `--selftest` prints a "note" line), and a `local` model that is neither a cleanup catalogue name nor a `.gguf` path becomes the recommendation for this PC. The file is not rewritten, so adding a key later restores the cloud provider. A user who does have a key keeps `anthropic`: see open questions.

## Cleanup catalogue breadth (15/10/2026)

- Source rule: vendor organisation first. Checked with the HF API (`/api/models/<repo>`, `/api/models?author=…&search=…`) and HTTP HEAD, all 15/10/2026. Added: `HuggingFaceTB/SmolLM2-1.7B-Instruct-GGUF` (Apache-2.0 on the card and on the base model card; ungated; HEAD 302 with `X-Linked-Size` 1,055,609,536 and ETag equal to the API's LFS oid), `google/gemma-3-1b-it-qat-q4_0-gguf` and `google/gemma-3-4b-it-qat-q4_0-gguf` (card licence `gemma`, `gated: manual`; anonymous HEAD → 401; sizes 1,003,541,152 and 3,155,051,328 from the API tree listing; the API returns the LFS oid as asterisks to anonymous callers, so no SHA-256 can be pinned). Gemma terms read at https://ai.google.dev/gemma/terms ("Last modified: April 1, 2026"): use by legal entities is allowed (section 2.1–2.2) subject to the Prohibited Use Policy; distribution needs a notice file; no non-commercial clause was found, so `CommercialUse = true`, but gated entries are never recommended and are labelled "requires a Hugging Face login".
- Left out: Phi-4-mini (`microsoft/Phi-4-mini-instruct` has no `.gguf` file; the `microsoft` organisation lists `Phi-4-mini-instruct-onnx` but no Phi-4-mini GGUF; anonymous API call for `microsoft/Phi-4-mini-instruct-gguf` → 401, the answer HF gives for a repository that does not exist or is private); Llama 3.2 1B/3B (`meta-llama` has no GGUF repository; the safetensors repos are gated `manual`; a community conversion was not added because it breaks the vendor-file rule); Qwen3 1.7B/4B (`Qwen/Qwen3-1.7B-GGUF` ships only Q8_0, `Qwen/Qwen3-4B-GGUF` has Q4_K_M; both Apache-2.0; not added because the brief requires driving non-thinking mode reliably through the chat template, which cannot be shown without downloading and running them, which the brief also forbids).
- Gemma and Llama stop tokens (`<end_of_turn>`, `<|eot_id|>`) were added to the anti-prompts and the validator's template-token list; SmolLM2 uses the ChatML tokens already there. None of the new families was run here.
- `--bench-cleanup <wav|txt>` (requested): every downloaded catalogue cleanup model over the same input with the app's prompt, examples, token limit and `llm.gpu`, after the tray's warm-up, with no timeout; prints time, validator verdict and output, plus the rule-based tidy (timed on its second call to exclude regex start-up).

## Cleanup validation, second pass (15/10/2026)

- **Per-sentence coverage** (beyond the spec): a dictated sentence with 4+ distinct content words must keep at least a third of them. Observed on the 60.9 s clip with CPU forced: qwen2.5-1.5b dropped "Other quarterly report is nearly finished and I think we can send it to the client by Friday afternoon." and qwen2.5-0.5b dropped three sentences and repeated one; the overall 60% rule accepted both. Both replies are now unit tests verbatim. Both coverage rules are skipped when the dictation contains "scratch that", whose job is to delete a clause (tested). Mutation checks: with the sentence check disabled the two new tests failed; with the "scratch that" exemption removed its test failed (both observed).
- **The 16:20:15 "empty response" from qwen2.5-1.5b:** that `--transcribe` run (4.66 s clip, Debug build, `--config` pointing at a folder with no settings.json, so `llm.timeout_ms` was the default 15,000) loaded the model at 16:19:57.674 and fell back at 16:20:15.183, 17.5 s later. It was the first-ever Vulkan call of that model (shader compilation), the timeout cancelled the generation before any token, and the build of that minute returned the empty, cancelled reply as if finished, so the validator called it "empty". The fix (`ThrowIfCancellationRequested` after the generation loop, in `LocalLlmEngine.cs` at 16:22) makes it report "timed out after 15000 ms"; the tray warm-up absorbs the shader compilation. Mutation check: with that line removed, `Cancelling_generation_returns_promptly` (CPU, qwen2.5-1.5b) failed with the cancelled call "completed" 4.4 s after `Cancel()` (observed); restored, it passed ("OperationCanceledException", 3.8 s).

## Lens pass 3 fixes (15/10/2026)

These supersede the earlier bullets on the rule-based tidy, the CPU recommendation, the per-call prompt re-read, the `.part` deletion after a failed download and the "scratch that" exemption.

- **Rule-based tidy rewritten to never change meaning** (it is the fallback for every provider). The lens found "I mean it." → "It.", "You know the answer." → "The answer.", "Kind of Blue…" → "Blue…", "the ER" → "the", "10 mm" → "10", "a new line of credit" → a line break, "i.e." → "I.E. The". Now: only the whole tokens um/umm/uh/uhh/erm/ah/hmm (case-insensitive, not after a digit; never "er" or "mm"); "you know", "I mean", "sort of", "kind of", "like" only between a sentence start or comma and a following comma; "new line"/"new paragraph" only as a clause of their own; only the first letter of a sentence and a standalone "i" (and "i'm", "i've"…) are capitalised, never a token with a full stop inside. Two deliberate readings of the brief: (1) "new line … at the very end" is applied only when it is a clause at the end ("Thanks, new line"), because "We need a new line." would otherwise lose its last words (added to the table); (2) the fox sentence now keeps its trailing "you know" ("So the quick brown fox jumps over the lazy dog you know."), which the new comma rule requires and which differs from the scope-change example. 32-case table test; with "er"/"mm" put back in the hesitation list, 2 cases ("Take him to the ER now.", "er, the plan") were observed failing on the reverted behaviour.
- **Prompt prefix cached in the KV cache.** LLamaSharp 0.27.0 (`v0.27.0` = commit 7cbbc45, read: `LLama/Batched/BatchedExecutor.cs`, `Conversation.cs`, `ConversationExtensions.cs`, `LLama.Examples/Examples/BatchedExecutorSimple.cs` and `BatchedExecutorFork.cs`): one `BatchedExecutor` and one `Conversation` per loaded model; the system prompt and examples (template applied without the user turn) are prompted once; each call prompts the rest of the template, samples greedily with `Conversation.Sample(GreedySamplingPipeline)` and `IsEndOfGeneration(vocab)`, decodes with `StreamingTokenDecoder`, and `ConversationExtensions.Rewind` then removes the call's tokens from the KV cache. Not `Conversation.Fork`: `BatchedExecutor.GetNextSequenceId` only ever increments, so a fork per dictation would run through sequence ids (the context's `SeqMax` bounds them; inferred from the source, not tested). The user's cancel is checked between tokens and not passed to `Infer`, because `Infer` dequeues a batch before decoding and a cancelled decode would leave the conversation unusable (read in the source). If the full prompt does not start with the prefix text (another template), that call runs uncached and the cache is rebuilt. The `// simplification:` for per-call re-reading is retired. Warm-up now runs on every backend (it evaluates the prefix and measures the speed estimate).
- **Before → after**, `--bench-cleanup`, second of two runs: Vulkan 10 s clip 0.5b 0.20 → 0.08 s, 1.5b 0.41 → 0.19 s; 60 s clip 0.96 → 0.87 s, 1.33 → 1.19 s. CPU forced 10 s clip 0.5b 2.43 → 1.09 s, 1.5b 4.04 → 1.79 s (1.60 s on the first run); 60 s clip 6.54 → 5.21 s, 12.64 → 10.24 s. Esc on CPU with 1.5b (184-token input, cancel at +200 ms): 3.88 s → 1.46 s; Vulkan 2 ms. RAM and GPU memory re-measured and unchanged within run-to-run noise (README table).
- **CPU recommendation is qwen2.5-1.5b** by the orchestrator's rule (≤ 2 s on the 10 s CPU clip: 1.79 s). qwen2.5-0.5b is never recommended: its reply was rejected for dropping content in 2 of the 4 clip/backend cases (Vulkan 10 s: "8/14 content words kept"; CPU 60 s: "0/4 of its content words kept"), on every run. 1.5b was also rejected on the CPU 60 s clip (it dropped one sentence), so long CPU dictations get the tidy.
- **Spelling variants** (-our/-or, -ise/-ize, -yse/-yze, -tre/-ter incl. -tred/-tered, -ogue/-og, -ence/-ense; words of 5+ letters, both sides alike) are normalised before the overlap and coverage comparisons. With normalisation disabled, both spelling tests (including "We analysed the colour behaviour of the centre." against the US-spelled raw) and 4 normaliser cases were observed failing on the reverted behaviour.
- **"scratch that" no longer disables coverage**: both coverage checks run against the dictation with each retracted clause (the clause before the phrase, back to the previous punctuation mark) removed. With the old exemption restored, `Scratch_that_does_not_hide_dropped_content` was observed failing on the reverted behaviour.
- **Hard deadline and long-dictation skip.** Deadline = 10 s + 0.1 s per word, clamped to 15–60 s as specified, with one integration choice: the lower bound is `llm.timeout_ms` (default 15,000) so the setting still means something, and the upper bound rises to it if set above 60 s. `CleanAsync` awaits the provider with `WaitAsync(deadline, ct)` and also cancels it, so the paste never waits past the deadline even when llama.cpp is mid-prompt. Before calling the local model it estimates (dictation tokens + 10) / prompt rate + (dictation tokens + 8) / generation rate, from the rates measured at warm-up and updated on every call (assumption: the reply is about as long as the dictation); over the deadline → the tidy, a log line, and the pill "Polishing… skipped (long dictation)". Mutation checks, observed failing on the reverted behaviour: without `WaitAsync` the deadline test failed (still waiting after 5 s); without the skip 2 tests failed. (fix batch: in fact the linked source was disposed before its own timer fired, so the token was often never cancelled; the timeout path now cancels it first)
- **Downloader:** a 206 that starts at the wrong byte now truncates the `.part`, resets the hash and retries from byte 0 (was a hard failure); a slow but live stream is not a stall because the 30 s timer restarts on every read; after the retries run out the `.part` is kept (`DownloadFailedException.Resumable`) and the next request hashes it and resumes from its end, so the final SHA-256 still covers every byte (a kept file with wrong bytes fails that check and is deleted, tested). Balloon: "… choose it again from tray > Speech model: it carries on from where it stopped."; the CLI says to run the same command again. Mutation checks observed failing on the reverted behaviour: wrong-206 as a hard failure (1 test), `.part` deleted after exhaustion (1), stall timer not restarted per read (1).
- **offline_only at one choke point:** `OfflineGuardHandler` (a `DelegatingHandler`) sits under every `HttpClient` Dictate creates (the app's provider and download clients, and the CLI's) and refuses any non-loopback request while the setting is on, reading it per request; the Anthropic SDK now gets that shared client too (a test shows the SDK works on a shared client that has already sent requests). The download loop re-reads the flag per chunk. (Lens pass 4: the provider client now has `AllowAutoRedirect = false`, because redirects are followed inside `SocketsHttpHandler` after the guard and a loopback server answering 307 got the body forwarded; a 3xx is an HTTP error and falls back to the tidy. Test with two loopback listeners: the redirect target receives no request; with `AllowAutoRedirect = true` it received one, observed failing on the reverted behaviour. The download client still follows redirects and is refused outright under offline_only. A gated test also shows the prompt cache carries nothing between dictations: on Vulkan with qwen2.5-1.5b, B on a fresh model, B after A, and B after a cancelled A all gave "So the quick brown fox jumps over the lazy dog.") Mutation checks observed failing on the reverted behaviour: handler check removed (3 tests; the download-client test then reached huggingface.co for real), per-chunk check removed (1).
- **Markers:** Gemma entries show "size-checked only" in the tray and README; the README's integrity line says pinned SHA-256 "where the catalogue has one; Gemma files are size-checked only".
- **Orchestrator decisions:** users upgrading with an Anthropic key keep cloud cleanup, no balloon, one README sentence; no Qwen3 now.

## Batch 2 (SPEC §16, 15/10/2026)

No NuGet package was added. Calls beyond the brief, one line of reasoning each.

**Structure**
- Feature code lives in `Personal/` and `Calibration/`; `PersonalFeatures` is batch 2's composition point, so the app host only loads, finishes and remembers through it. Shared code got four narrow hooks: `DictationPipeline(finish, inserted)`, `TrayIcon(extras, opening)`, `DictationController.IsIdle`, and `ModelCoordinator.Write`/`ApplyWithDownloads`. Reason: no feature logic scattered into shared code.
- One reader/writer for every plain text file (`TextList`) and one whole-word matcher (`WholeWords`) serve the dictionary, corrections, profanity list and calibration script; the profanity filter and corrections share the matcher rather than each having one.
- The fix_hotkey is a second `KeyboardHook` instance (same primitive, own thread) with the same `suppress_hotkey`; it follows `enabled`, and a value equal to `hotkey` is ignored with a warning at load. Reason: reuse without touching the dictation hook. (fix batch: any overlap on virtual keys is now ignored with a warning, not only an equal value)

**Whisper decoding and the dictionary**
- Whisper.net 1.9.1 API read in its source at tag 1.9.1 (`WhisperProcessorBuilder.cs`, `BeamSearchSamplingStrategyBuilder.cs`, `WhisperProcessorOptions.cs`, raw.githubusercontent.com, 15/10/2026) and in the package XML docs: `WithPrompt(string)` sets the initial prompt; options default to `GreedySamplingStrategy`; `WithBeamSearchSamplingStrategy(b => b.WithBeamSize(n))` switches to beam search and returns the builder; an unset beam size is 5 ("If not configured, 5 beam sizes are used").
- A dictionary edit or a new `whisper.beam_size` rebuilds only the processor from the loaded factory (`WhisperEngine.Reconfigure`, after any transcription in progress); the model is not reloaded. Reason: a dictionary edit must not cost a model load.
- On a GPU the rebuilt decoder gets the same 1 s silent warm-up as a model load, in the background. Observed 15/10/2026 with `--transcribe` from `dist`: small.en's first transcription with a dictionary prompt took 26,139 ms, the next 469 ms, the same clip without a prompt 441 ms; after re-publishing to the same `dist` folder the prompt run took 26,579 ms again while the no-prompt run stayed at 396 ms, and after a third publish it took 416 ms. In the tray test, the 5-character prompt warmed up in 407 ms and the 11-character one in 24,115 ms (657 ms on the next run). Shader compilation for the decoder's longer prompt batch, cached by the driver per program, is the inferred cause, not established. A dictation made during a background warm-up waits for it (the engine is serialised); the hotkey is only blocked during a model load, as before.
- Prompt budget: Whisper.net exposes no tokenizer, so a term is estimated at (length + 2) / 3 + 1 tokens against 224, keeping the newest terms and placing them last (nearest the audio). Marked `// simplification:` with its ceiling and trigger.
- Duplicate dictionary terms collapse case-insensitively; the later line's spelling and position win (it is the most recently added).
- The cleanup prompt's dictionary line is capped at 1,000 characters, newest kept, because the prompt shares the 4,096-token context with the dictation.
- The grammar-assist sentence and the dictionary line are appended on their own lines to whatever base prompt is in use, including `llm.prompt_file`.
- The validator's "repeats the system prompt" check now also knows the grammar-assist sentence and the dictionary header (tested: a reply that is the grammar sentence is rejected).
- When the prompt changes (dictionary edit, grammar assist), a loaded local model re-evaluates the new prefix into its cache in the background, and warm-up uses the actual prompt, so the next dictation does not pay the prefix (which costs seconds on a CPU).
- `whisper.beam_size` accepts 0–8: my bound; whisper.cpp's decoder limit is recalled as 8, not verified.

**Corrections, fix-last and profanity**
- A word character is a letter or a digit; an apostrophe is a boundary, so `kinney => Kynni` also fixes "Kinney's". One regex pass: overlapping rules resolve longest-first and an output is never re-matched (no chains); a later line for the same phrase wins; a line missing either side is ignored with a warning that never contains the line.
- Case: a written form with capitals is used as written; one without takes a capital first letter only when the heard word was capitalised and not an all-capitals acronym (so "the ER" → "the emergency room", "Gonna" → "Going to").
- Fix-last suggestions come from a word LCS diff; case-only and punctuation-only edits, pure insertions or deletions, and replacements of more than 4 words are not offered (they are rewrites, not mishearings). A substitution plus adjacent added words is one suggestion (shown unticked, so a poor one costs nothing).
- "Paste fix" hands the foreground back to the window the last dictation went to (recorded when it was pasted), checks that window is in front, then uses the normal insert path; if Windows refuses, it says to use Copy instead of pasting somewhere else. The fixed text is not filtered again (the user typed it) and becomes the new last dictation.
- The fix_hotkey is ignored while a dictation is recording or processing (`IsIdle`), because the editor taking focus then would receive the paste. Windows opened from the tray are activated by their opener after `Show`, not by themselves, so a test can show them without taking focus.
- Profanity list: common profanities with plural/-ing/-ed forms; words that are also ordinary words or names ("bloody", "hell", "cock", "Dick", "damning") are left out to avoid masking real content. Remove mode tidies only around each removal (double space, dangling comma, the lost sentence capital), never elsewhere in the text.
- The override `profanity.txt` replaces the shipped list (an empty override means no words), matching "overridable".
- Observed 15/10/2026 (`--transcribe`, large-v3-turbo + qwen2.5-1.5b on Vulkan): the cleanup model turned "send the damn report" into "send the report" and the validator accepted it (every content word kept), so the mask had nothing to act on. Left as is and documented in the README; whether the prompt should tell the model to keep swearing as dictated is an open question for Kyle.

**Presets**
- A preset compares and writes only the keys in its table (`whisper.model`, `llm.provider`, and for Full `llm.model`), so a calibrated beam size or the profanity mode does not turn it into Custom.
- Missing models for a preset: one confirmation naming every file and the total size; the files download in turn through the existing downloader; settings are written only when all have arrived; declining, or `offline_only`, writes nothing.

**Calibration**
- Latency ceiling: each clip is allowed 2.5 s × max(10, its length) / 10, compared on the totals; whisper.cpp's cost is nearly flat for clips up to 30 s (README: small.en on CPU 5.2 s for a 4.7 s clip, 6.4 s for 10.5 s), so dividing a 4 s clip's time by 4 s would fail every model on a CPU. Ties go to the faster combination; when none qualifies there is no recommendation and no suggestions.
- WER normalisation beyond the brief's number map: apostrophes dropped; `$4,250` → "4250 dollars"; `%` → "percent"; `@` → "at" and a dot between letters → "dot" (e-mail addresses); ordinal suffixes dropped; runs of number words parsed ("four thousand two hundred and fifty" → 4250); clock times "10:30", "10.30", "1030" and "ten thirty" all → 1030. Observed 15/10/2026 on the synthesised clips: tiny.en wrote "1030" and small.en "10.30" for "ten thirty", which a plain word map would have scored as errors.
- "Consistent miss": the word appears at least twice in the script, was missed at least twice and in at least two thirds of its appearances; a correction is offered only when every miss was heard identically. Numbers, words under 3 letters, terms already in the dictionary and existing corrections are not suggested. Extra words next to a miss join its "heard" text ("shiv on").
- The script repeats three names (Siobhan ×3, Kowalski ×2, Parramatta ×2) because a word that appears once can never be "consistent across clips".
- The test runs with the user's current dictionary prompt, so it measures how dictation will hear them. Beam search uses 5 beams (Whisper.net's default). On a GPU each decoder gets a 1 s silent warm-up before timing (shader compilation, as at model load); not on a CPU.
- Recordings are 16 kHz mono 16-bit PCM WAV (NAudio `WaveFileWriter.WriteSamples`, read in the 3.1.0 XML docs), samples clamped to ±1; a take under 1 s or below `silence_threshold` is refused ("record it again").
- Only catalogue models in the models folder are tested (a "Choose model file" path has no catalogue name to write back to `whisper.model`).

**Files, CLI and verification**
- The settings watcher also reloads on `dictionary.txt`, `corrections.txt` and `profanity.txt`, including their deletion (deleting a file switches its feature off); deleting settings.json still does not reload, as before. (fix batch: deleting or renaming `.env` reloads too; deleting a `profanity.txt` override returns to the shipped list rather than switching the filter off)
- `--transcribe` prints a new `final:` line (after the profanity filter and corrections) and uses the dictionary in both prompts; `--no-llm` stays raw-only. `--bench-cleanup` uses the same prompt build. `--selftest` prints the preset, decoding, counts of personal entries (never the words) and fails if a shipped data file is missing.
- The brief names `DICTATE_CLEANUP_MODEL_PATH` as a test gate, but the existing local-LLM tests read `DICTATE_LLM_PATH`; both were set for the verification run and the report says so. (fix batch: the tests now read `DICTATE_CLEANUP_MODEL_PATH` first and fall back to `DICTATE_LLM_PATH`)
- `publish.ps1` now also fails if `profanity.txt` or `calibration-sentences.txt` is missing from the output (both are `Content` items linked to the output root). Published 15/10/2026 after batch 2: 306 files, 323,901,961 bytes = 308.9 MiB; zip 119,335,134 bytes = 113.8 MiB.
- Quit now hides the tray icon before releasing the models, because releasing waits for a whisper warm-up still running. Observed in `TrayStartupTests`: quitting 8 s after a dictionary edit waited 15.6 s ("Whisper engine disposed … in 15556 ms") while that edit's 24.1 s first-time shader compile finished; on the next run, with the shader cached, the warm-up took 657 ms and dispose 23 ms.
- `TrayStartupTests` (gated by `DICTATE_TRAY_TESTS=1` and `DICTATE_MODEL_PATH`) builds the tray host directly, bypassing Program's single-instance mutex, with a scratch config, scratch logs and both hotkeys off, so it can run beside an installed copy: it checks startup, the watcher reload of dictionary.txt, the decoder rebuild without a model reload, the fix_hotkey hook, and the quit path.
- `CalibrationIntegrationTests` accepts `DICTATE_CALIBRATION_GPU=cpu` (run the class alone, as the runtime is per process) so the latency ceiling can be seen excluding a model: on this CPU small.en took 6.27 s per clip and tiny.en with beam search was recommended.
- Two pre-existing blank lines inside the README's settings table (which split it into headerless tables) were removed while adding the new rows.

## Fix batch after the nine-lens review (15/10/2026)

Decisions taken by the orchestrator are marked (orchestrator); the rest are this lane's, with the reason.

**Privacy and network**

- (orchestrator) A request to a server on this machine never goes through a proxy: the cleanup and download clients use `LoopbackDirectProxy`, which returns no proxy for a loopback destination and defers to the system proxy otherwise. Before, `HTTP_PROXY` with `base_url` on localhost sent the transcript, the dictionary prompt and `LLM_API_KEY` to the proxy (three lens probes). A remote gateway reached through a corporate proxy is not supported; `UseProxy` stays on only so that other hosts keep the system proxy. Pinned by a test that runs a listener as the proxy and sees no request.
- (orchestrator) The README now promises only that Dictate's own request stays on this PC: a server on this machine (a local proxy or company gateway) can still relay what it receives, and `offline_only` cannot see that.
- The Anthropic SDK client is built with `OpenTelemetry = false`, so a tracing exporter configured for other programs cannot receive transcripts through the SDK's spans.
- The config-folder watcher reloads when `.env` is deleted or renamed away, so a removed key stops being used at once.

**Settings**

- (orchestrator) One tested loader (`ConfigLoader`) replaces the three copies in the tray, the CLI and `--selftest`. A settings.json that cannot be read at startup runs with the defaults and `offline_only` forced on, with the balloon "settings.json could not be read; running offline with defaults until it is fixed"; one that breaks on a reload keeps the previous settings and says so; the download commands exit 1. A present `offline_only` that is not `true` or `false` counts as `true` (fail closed). A key given twice warns that only the last one is used. An empty `KEY=` in a `.env` fills nothing, so it cannot hide a key from the other `.env`.
- `SettingsFile.SetValue` now guards the write as well as the parse (read-only file, duplicate keys) and leaves no `.tmp`; the tray's Enabled item and a download's switch-over report a failure instead of throwing.

**Model lifecycle**

- Each speech or cleanup load belongs to a generation; a newer choice, `Unload` or quit makes an older load stale, and a stale load disposes the engine it made. Loads run one after another (each waits for the previous one), the swap happens inside the lock, and `GetAsync` waits for a newer load that started meanwhile. Before, two quick choices left dictation and decoder changes on different engines and one engine resident until quit.
- A same-path `Ensure` retries a load that failed, so a re-download (or tray > Reload settings) of a damaged file is loaded.
- Downloads run one at a time (`ModelDownloads`): a download asked for during another waits its turn, Cancel stops the running one and drops the queue, and a switch-over that throws is shown in a balloon. A tray model choice during a download still cancels it (documented behaviour); a Mode preset or a calibration "Download and include" that needs a download waits behind it, so its bytes are not lost. (Corrected 19/10/2026: a Mode preset whose models were all on this PC did not wait; it cancelled the running download, and pressing the Mode already chosen re-ran it. Both are fixed; see "Model choice never silently cancels a download".) The download question no longer promises a model switch itself: the callers that switch add "Dictate switches to it when the download finishes"; calibration says "It is added to the test; your current model stays in use".
- (orchestrator) The first-run pickers also run for a settings.json that names no speech model or no cleanup choice (only the open questions are asked, and only the answers are written into the existing file). A file that cannot be read gets no picker.
- (orchestrator) Recommendations and the Mode presets treat a machine as a GPU machine only with a discrete GPU: ggml_vulkan's device lines (`N = name | uma: X`, format read from this PC's own log) mark integrated GPUs with `uma: 1`; a Vulkan runtime that never describes its devices keeps the earlier rule. Reason: every GPU figure was measured on the discrete Quadro; none on an iGPU. Warm-ups still run on any Vulkan device, which compiles shaders too.
- The first-use check keeps matching catalogue files by name in any folder (kept per the review's item 21), but a mismatch outside the models folder asks the user to rename the file and never to delete it (a chosen path is only forgotten, SPEC §17).
- The cleanup warm-up now cleans the first few-shot example with up to 16 tokens instead of answering "ok" with 4, so it measures the generation speed too (it used to log "generation 0.0 tokens/s", leaving the long-dictation skip off until the first dictation). A call stopped at its deadline also records the speed it measured.
- (orchestrator) Quit posts the same input as unticking Enabled before closing the controller, so a dictation being transcribed or cleaned is cancelled instead of holding the engines. Warm-ups run under a shutdown token. A second launch that finds the lock held but no "already running" event (the owner is starting or quitting) waits up to 30 s, checking for the event as it goes, and takes over a released or abandoned lock; it never logs a false "signalled". 30 s is longer than the longest uninterruptible step recorded here (the first-ever Vulkan prompt compile, about 17 s): inferred, not measured on a quitting copy.

**Validator, tidy and text pipeline**

- (orchestrator) Validator: the coverage and retraction checks use the text left after the smallest retraction plus its replacement words; negations ("not", "no", "never" and n't forms) may not be dropped; every dictated number must be kept; framing and commentary match whole words, with curly quotes mapped first; a reply in a script the dictation did not use (Cyrillic, CJK…) is rejected; the prompt-echo check uses the prompt actually sent, a custom `llm.prompt_file` included. Spoken command phrases and hesitations are not counted in the ratio (SPEC §5 amended by the orchestrator); dictated quotes and a trailing dictated line break are kept; the profanity list is checked unless `profanity.mode` is `remove`. Rejection reasons never carry dictated text (pinned over every fallback route).
- The tidy's hesitations are case-sensitive ("ERM" stays), a capitalised reduplication stays ("Wagga Wagga"), a full stop that starts a word stays attached (".NET"). Whisper's non-speech annotations are removed per segment before the join: a segment that is only bracketed text ("[farting]", "(laughs)") is dropped, and square-bracketed spans are cut from the rest (the fixed list of nine tags missed the others). "Ah" stays unfixed.
- On a timeout the cleaner cancels the provider's token before the linked source is disposed (the lens measured 396 of 400 timed-out tokens never cancelled with all cores busy; 0 after).
- (orchestrator) Corrections now run before the profanity filter, so a correction cannot bring back a filtered word (SPEC §16.3/16.4 order amended by the orchestrator). Corrections lower-case a capitalised match unless it starts a sentence, and whole-word matching treats a contraction's stem as a word ("don't"). Remove mode takes an attached `'s`/`'re`/`'ll`/`'ve`/`'d` and, for a word that was a sentence on its own, its punctuation. The shipped list gained arsed, bullshits, bullshitter(s), dumbass(es), fuckhead(s), fuckwit(s), jackass(es), shite, shithouse and shitload, and lost "craps" and "damning" (ordinary words).

**Hotkeys and audio**

- Two hotkeys conflict when pressing one can complete the other on virtual keys (a chord inside another, a key group containing the other's key, a single key anywhere in the other); such a `fix_hotkey` is ignored with a warning naming both. The parser refuses a chord naming one key twice under two names (`Ctrl+RightCtrl`), which completed on a lone RightCtrl.
- A key-down for a key the matcher thinks is held counts as a fresh press when its previous down is more than 1.2 s old (a `// simplification:` with its ceiling and trigger in `HotkeyMatcher`), and the auto-stop forgets the held key; together they fix the press swallowed after a key-up lost to UAC or Win+L.
- A microphone lost mid-recording now transcribes what was captured (as the auto-stop does) with the "Mic error" flash; a lone tap waiting for its second tap is still discarded, and a microphone that cannot be opened is unchanged.
- Capture latency is logged on one clock: the hook stamps key-down with `Stopwatch.GetTimestamp()`, and the first packet's WASAPI QPC position (100-ns units: IAudioCaptureClient::GetBuffer on Microsoft Learn, and NAudio.Wasapi 3.1.0's own docs, both read 15/10/2026) is compared with it, beside the recorder's `LatencyMilliseconds`. No microphone figures were recorded in this batch.

**Calibration**

- (orchestrator) WER rules: dates (`[the] N[th] [of] <month>` and `<month> [the] N[th]` → `N month`), clock times only for `h:mm`, or `h.mm` that is not money, a percentage or part of a longer number, and `at hhmm`, with am/pm dropped; "per cent" joins to "percent"; a hyphenated word is one word unless a part is a number word ("twenty-three" stays 23); "$4,250.00" equals 4,250 dollars but "$42.50" does not; ordinals keep their suffix ("fourteen" is not "14th"); "I will" equals "I'll" (a short table of contractions, a `// simplification:`); "A.U." and spelled letters after "dot" join. No early stopping and no beam skipping (orchestrator). Results measured before this batch (including the real-voice result below) were scored with the old rules and are not directly comparable.
- Each combination is reported as soon as it is measured, so Cancel keeps the finished rows. The window's rules live in `CalibrationSession` (tested without WPF): no run during a take, the take in progress can always be stopped, no delete during a run, a take longer than 30 s or whose microphone fails is discarded, results are cleared when a take is saved or the recordings deleted and are tied to the recordings they came from (a "Download and include" after re-recording runs every model again), and "Download and include" is one click per model with progress text. `takes.txt` records the sentence each take was recorded for; a take for an older wording is not scored. Delete recordings removes failed-save leftovers too and counts only recordings. A suggested correction is kept only if it changes what whisper wrote.

**CLI, scripts and catalogue**

- (orchestrator) Every catalogue URL is pinned to a commit: whisper.cpp `5359861c739e955e79d9a303bcbc70fb988958b1`, and one commit per cleanup repository (`CleanupCatalog`, `download-model.ps1`, kept in sync by the unit test). Read 15/10/2026 through the HF API: each repository's current commit, then its tree at that commit, which lists every catalogue file with the same size and LFS SHA-256 (the two gated Gemma files: size only, hash masked); a HEAD of every pinned URL answered 302 with matching `X-Linked-Size` and `X-Linked-ETag` (Gemma: 401), redirecting to the same CDN host (`us.aws.cdn.hf.co`) as `resolve/main`. The downloader also refuses a response whose announced size differs from the catalogue's before reading it.
- `--print-models-dir` prints the folder the app resolves (`whisper.models_dir`, else the default; exit 1 for an unreadable settings.json); `install.ps1 -ModelsFrom` copies there (orchestrator). The install no longer moves an exe-folder `.env` through `%TEMP%` and keeps it under `-Force` too, and clears the read-only flag on a seeded settings.json. `uninstall.ps1` treats a failed `Read-Host` (`powershell -NonInteractive`) as "keep the data". `download-model.ps1` adds `--fail`, a 30 s connect and stall limit, keeps the `.part` when its 8 attempts run out, takes the Hugging Face token from `HF_TOKEN` or a hidden prompt (`-Token` still works but warns), and sends it only for a gated model.
- A CLI flag's value is never another flag or empty, and two different modes in one launch are refused. `--bench-cleanup` exits 1 for an unreadable input instead of crashing, and runs a `--cleanup-model` file as well.
- Test gates: `DICTATE_CLEANUP_MODEL_PATH` (falling back to `DICTATE_LLM_PATH`); the README lists every gate and what it touches. Tests never reach a real host: guard tests use `dictate-test.invalid` (RFC 2606) and counting handlers.
- Provenance of two README figures questioned by the review: the memory table was measured during Batch 2 on 15/10/2026 by the method the README states (process peak working set and the rise in `nvidia-smi` "memory used", sampled every 250 ms, both models loaded); the raw samples were not kept. The shader-compile times (20.5 s, 8.7 s, 10.0 s, 1.3 s) are Batch 2 observations on this machine the same day whose exact runs were not recorded, so they stand as observations, not measurements. Neither was re-measured in this batch.
- Published 15/10/2026 after the fix batch: 306 files, 324,019,442 bytes = 309.0 MiB; zip 119,387,205 bytes = 113.9 MiB.
- The calibration engine on the ten synthesised clips after the fix batch (Vulkan, `CalibrationIntegrationTests`): tiny.en 6.4 % greedy / 4.0 % beam, small.en 0.8 % / 0.8 %, so small.en greedy is now recommended; under the old rules it scored 2.4 % only because its correct "14 March at 10.30" counted as two errors.

## Recalled, not verified (check before relying on them)

- README examples `ollama pull llama3.2`, Ollama's `http://localhost:11434/v1` and LM Studio's `http://localhost:1234/v1` endpoints.
- SmartScreen's "More info > Run anyway" wording (fix batch: "Windows protected your PC" and "Run anyway" verified on Microsoft Learn's "SmartScreen reputation for Windows app developers", 15/10/2026, with the Smart App Control quotes the README now carries; "More info" is still recalled).
- The 0xE8 menu-mask technique (AutoHotkey) preventing the Start menu on Win release.
- `Set-Service Audiosrv -StartupType Automatic; Start-Service Audiosrv` (service name and cmdlets) in the README's Windows Server note; the "disabled by default on Server" fact is from SPEC §15 and was not re-checked on a Server VM.
- The Remote Desktop client path (Local Resources > Remote audio > "Record from this computer") and the "Allow audio recording redirection" policy name are from SPEC §15. Observed: this machine's session records through a device named "Remote Audio". (fix batch: the policy's name, TS_CLIENT_AUDIO_CAPTURE, and its path verified on the Policy CSP ADMX_TerminalServer page, 15/10/2026; the client's menu path is still recalled)
- Batch 2: whisper.cpp keeps the end of an initial prompt that exceeds its budget (n_text_ctx / 2 = 224 tokens) and drops the start; whisper.cpp's maximum of 8 decoders behind the `whisper.beam_size` bound; Remote Desktop audio redirection compressing the microphone (the README calls it "may compress … not measured here").
- Fix batch: Windows' longest keyboard delay setting being 1 s (behind the 1.2 s late-repeat rule in `HotkeyMatcher`); that ggml_vulkan's `uma: 1` marks every integrated GPU (seen here only for one Intel UHD P630 beside a Quadro, whose line says `uma: 0`); the Windows Audio Endpoint Builder service listing devices while Windows Audio is stopped (the README's Server note says "expected, not tested").

- Polish lane: the Shell_NotifyIcon constants NOTIFYICON_VERSION_4 = 4, NIN_SELECT = 0x400 and NIN_KEYSELECT = 0x401
  (the icon was added in every tray run; a click on it was never exercised); APPMODEL_ERROR_NO_PACKAGE = 15700; the
  `LocalCache\Roaming` / `LocalCache\Local` layout of a package's redirected app data; `ms-settings:appsfeatures`
  opening Settings > Apps; a StartupTask turned off in Task Manager reporting DisabledByUser; every macOS API name
  marked *(recalled)* in src/Dictum.Mac/README.md; that WS_EX_LAYERED with WS_EX_TRANSPARENT is what makes a top-level
  window click-through (now observed working by the probe, but the rule itself is from memory).

## Real-voice calibration result (15/10/2026, 20:4x AEST)

First calibration on a real voice (Australian English), microphone redirected over
Remote Desktop ("Remote Audio"), Quadro RTX 4000 via Vulkan, ten sentences. Word error
rate / seconds per clip: large-v3-turbo greedy 4.7% / 0.51 s (recommended);
large-v3-turbo-q5_0 beam 4.7% / 0.65 s; large-v3-turbo-q5_0 greedy 5.5% / 0.58 s;
large-v3-turbo beam 6.3% / 0.63 s; medium.en beam 6.3% / 0.80 s; small.en beam 7.1% /
0.45 s; medium.en greedy 9.4% / 0.56 s; small.en greedy 10.2% / 0.31 s; base.en beam
12.6% / 0.37 s; tiny.en greedy 15.0% / 0.17 s; base.en greedy 15.0% / 0.28 s; tiny.en beam
16.5% / 0.25 s. No consistently missed word. Follow-ups for the polish lane: show the raw
error count beside the percentage (ten sentences is about a hundred words, so one error
is about one point), and break near-ties (within one point) toward the faster decoding.

## Polish lane (16/10/2026)

The app is now **Dictum**; sections above this one describe the earlier build, named Dictate, and keep its names.

### Structure (SPEC §21)

- Three projects and one seam: `src/Dictum.Core` (net10.0), `src/Dictum.UI` (net10.0, Avalonia), `src/Dictum.Windows`
  (net10.0-windows10.0.19041.0, the entry point `Dictum.exe`), `src/Dictum.Mac/README.md` (an empty slot listing every
  `IPlatform` member with its macOS counterpart). `IPlatform` (src/Dictum.Core/Platform/IPlatform.cs) is the only way
  shared code reaches the operating system. `ArchitectureTests` fail if Core or UI references a platform assembly
  (Avalonia.Win32/Native/X11/Desktop, WPF, WinForms, NAudio.Wasapi, the Windows SDK projection, Dictum.Windows…) or
  declares a P/Invoke. WPF and WinForms are gone: the tray icon is our own Shell_NotifyIcon host (Avalonia's TrayIcon
  has no balloon notifications), the dialogs are Avalonia windows, the clipboard is raw Win32.
- **Avalonia 11.3.22**, not 12: SPEC §21 says "Avalonia 11". nuget.org (read during this lane, 15–16/10/2026) lists
  12.1.4 as current (published 09/10/2026) and 11.3.22 as the newest 11.x (published 11/09/2026). Moving to 12 is an
  open question for Kyle, not a lane decision. Packages: `Avalonia` and `Avalonia.Themes.Fluent` (UI); `Avalonia.Win32`
  and `Avalonia.Skia` (Windows: `Avalonia.Desktop` would also bring the macOS and X11 backends); `Avalonia.Headless`
  (tests: `Avalonia.Headless.XUnit` depends on xunit 2's xunit.core, and this suite is xunit.v3). No font package: the
  system font (Segoe UI Variable, falling back to Segoe UI).
- **Build telemetry.** Avalonia's build package (Avalonia.BuildServices) runs an `AvaloniaStats` target that sends
  anonymous build statistics unless `AVALONIA_TELEMETRY_OPTOUT=1`. `Directory.Build.targets` replaces that target with
  an empty one (MSBuild's "Overriding target AvaloniaStats" message confirmed it applies), and the build scripts set the
  variable too (`scripts\build-common.ps1`). Disclosure: the very first Dictum.UI build in this lane ran before the
  override existed, so that one build may have sent Avalonia's statistics; what it sends was not inspected. It also
  wrote `%LOCALAPPDATA%\AvaloniaUI\BuildServices\id` (16 bytes, created 16/10/2026 00:10:25, the minute the override
  was added; seen on disk, contents not read), which the opt-out leaves in place: delete it to drop that identifier.
- Overlay styling stays behind `IPlatform`: the UI hands `IWindowFlags.OverlayStyles` to Avalonia's
  `Win32Properties.AddWindowStylesCallback`, which lives in the cross-platform Avalonia.Controls assembly and does
  nothing off Windows; Avalonia rebuilds a window's extended style on every show, and the callback keeps
  WS_EX_NOACTIVATE | TRANSPARENT | LAYERED | TOOLWINDOW | TOPMOST each time (Avalonia 11.3.22's Win32 WindowImpl read
  for this). That WindowImpl creates a window with WS_EX_NOREDIRECTIONBITMAP when it renders through a composition
  surface, so whether a layered window would still draw was the open risk. Observed on this desktop, 16/10/2026, by `tests/Dictum.UiProbe`: the pill, orb and edge leave
  GetForegroundWindow() unchanged, carry all five styles, WindowFromPoint at their centre returns the window beneath,
  and the pill's pixels are on screen (#1D2430 while shown, the window beneath's #F6F6F6 after it hid).
- The probe is a separate exe because a process can host one Avalonia platform, and the test process hosts the
  headless one. The headless session runs with `AvaloniaTestIsolationLevel.PerAssembly`: the default per-test
  isolation failed every window test with `KeyNotFoundException: 'fonts:SystemFonts'` (observed); Skia drawing gives
  the headless platform the system fonts.
  What runs ungated: the overlays' window properties (no activation, no hit testing, no taskbar button) on the headless
  platform, and the pure `OverlayStyles` function. The real Win32 styles, the foreground check, click-through and drawing
  are checked only by `PillFocusTests` through the probe, gated by `DICTUM_UI_TESTS` because it shows windows on the
  desktop.

### Behaviour decisions

- **Indicators default off; the orb is placed by corner, not dragged.** The design says "drag it anywhere", the brief
  says the orb is click-through and never activates; a window that ignores the mouse cannot be dragged, so the brief
  wins and Settings > General > Orb corner places it (`indicator.orb_corner`). The edge glow shows only while
  recording; the orb shows while recording (red, rings following the voice), transcribing (blue) and polishing
  (amber).
  - `// simplification:` the edge glow is one transparent window covering each monitor. Ceiling: a full-monitor topmost
    window could interact with fullscreen-app detection or cost GPU memory on large displays. Trigger to retire: such a
    report; then four edge strips per monitor.
- The controller now sends dictation states when the pill **or** a big indicator is on
  (`AppSettings.ShowsDictationState`); with both off nothing but "hide" reaches the UI, as before.
- **Start and stop sounds** come from the design's tray toggle (`sounds.enabled`, default off): short tones played from
  memory with PlaySound, never blocking.
- **Tray panel** closes when it loses focus (like the system's flyouts); a click on the icon within 300 ms of that
  close does not reopen it. The microphone meter there moves only during a dictation (the microphone is opened only to
  dictate); Settings > Microphone has a five-second test that opens it on purpose.
- **Dialogs are synchronous** (the model prompts' contract): each runs a nested dispatcher frame until it closes. The
  file chooser borrows an unshown window's handle.
- **Hotkey capture (SPEC §20):** `HotkeyCapture` (Core) reads a gesture as the keys held together at its peak:
  modifiers first, the last other key as the trigger, sided names (`LeftCtrl+LeftShift+Space`, `RightAlt`); Esc alone
  names nothing. Injected keys count, as they do for the dictation hook (Remote Desktop and remapping tools inject).
  While the box listens the dictation hotkey is paused, so holding a key to test it does not dictate.
- **Cloud consent (SPEC §22):** `llm.cloud_consent` holds the host the user agreed to in the in-app dialog; a cloud
  provider runs only while it matches the host it would send to. The settings repair turns an unconsented cloud
  provider into `local` (logged), and `TranscriptCleaner.Create` refuses it again as a backstop. A plain `http://`
  address off this PC is refused three times over: by the settings loader, before the consent dialog, and by the
  cleaner. A loopback server needs no consent. Consequence: a first-build settings.json with `anthropic` and a key now
  runs the local model until the consent step is done; three existing tests that set `anthropic` with a key gained
  `cloud_consent` (FixBatchSettingsTests, IntegrityAndRepairTests, LlmTests).
- **Report a problem** opens `mailto:hello@kynni.com.au` (SPEC §18.4's support address) with the version, platform and
  models; nothing else, and never dictated text. GitHub issues (`github.com/kynniau/dictum`, SPEC §18) and the privacy
  policy (`dictum.kynni.au/privacy`, SPEC §18.4) are linked beside it.
- **"Clear dictation history" is not built:** Dictum keeps no history (the last dictation lives in memory only), so
  Settings > Storage says that instead.
- Deleting recordings or logs leaves their (empty) folders; only "Remove Dictum completely" removes the app folders.
- **Migration (SPEC §21):** runs in `Program.Main` before anything opens a file, for every mode. Per item, never over an
  item Dictum already has; a held file is left and retried next start; with `--config` the settings folder is skipped;
  paths inside the old folders are rewritten in settings.json and in the verified record. Every moved item is logged.
- **Packaged mode (SPEC §18.2):** `GetCurrentPackageFullName` (ERROR_INSUFFICIENT_BUFFER means packaged; the value of
  APPMODEL_ERROR_NO_PACKAGE, 15700, is recalled); launch at login through the StartupTask API with TaskId
  `DictumStartup`, which the future MSIX manifest must use; Settings > Storage shows the redirected folder when one
  exists (the `LocalCache\Roaming|Local` layout under `%LOCALAPPDATA%\Packages\<family>` is recalled); "the other build
  is installed" is inferred from `%LOCALAPPDATA%\Packages\Kynni.Dictum_*` or `%LOCALAPPDATA%\Programs\Dictum\Dictum.exe`.
  None of this ran packaged: there is no package yet.
- **Full removal (SPEC §17):** the program folder goes in a detached `powershell -EncodedCommand` step that waits for
  Dictum's process to exit, re-checks for Dictum.exe and deletes only that folder; it refuses a packaged install, a
  drive root, a folder without Dictum.exe and any folder below a `Dictum.sln` (a build output). `ms-settings:appsfeatures`
  (packaged builds) is recalled, not verified.
- Variables: the app reads `DICTUM_MODEL_PATH`, then the earlier `DICTATE_MODEL_PATH`; the test gates read `DICTUM_*`,
  then `DICTATE_*` (`TestEnv`).
- `--transcribe` exits 1 on an unreadable settings.json like every other command (SPEC §22, the lens re-check);
  a malformed `offline_only` value was already read as true (`FixBatchSettingsTests`).

### Tests moved, none deleted

Four tests were rewritten for the new UI under new names: `Showing_the_pill_does_not_change_the_foreground_window` →
`PillFocusTests.Showing_the_overlays_does_not_change_the_foreground_window` (now also the orb, the edge, the five styles,
click-through and drawing, through the probe); `The_pill_is_built_to_never_take_focus_without_being_shown` →
`Overlay_windows_get_the_no_activate_click_through_styles_on_every_style_rebuild` and
`The_pill_never_takes_focus_and_lets_clicks_through`; `Fix_last_editor_offers_unticked_corrections_and_writes_only_ticked_ones`
→ `Fix_last_offers_unticked_corrections_and_saves_only_the_ticked_ones`; `Calibration_window_lists_the_ten_sentences_and_the_missing_models`
→ `The_calibration_window_starts_at_the_first_sentence_and_lists_missing_models` (the designed window shows one sentence
at a time). `TrayStartupTests` now runs the real Dictum.exe in a child process for 12 s (was 15 s in-process).
Changed expectations, each a deliberate behaviour change: the pipeline's log line now reads "Corrections and the
profanity filter changed the text." (the order fix); the three cloud-consent tests above.
One assertion was dropped: `TrayStartupTests` no longer requires a "Whisper warm-up on" log line. Run as a child
process for 12 s, the model loaded and its first Vulkan warm-up with a dictionary prompt had not finished when the run
quit (observed, 16/10/2026); the earlier in-process test could see it, presumably because other whisper tests in the
same process had compiled the shaders first (inferred). The warm-up itself still runs and logs (see the
`--exit-after 5` run below).

### The lens re-check's five items (16/10/2026)

| # | Item | What changed | Pin, and the mutant it was observed failing on |
|---|---|---|---|
| 1a | Whole-word commentary matching (`ValidatorText.ContainsPhrase`) | No code change: the pin was missing | `AmendmentTests.Commentary_is_matched_as_whole_words_in_the_dictation_too`; substring matching fails it and only it |
| 1b | The sent-prompt echo check (`CleanupValidator`) | No code change | `A_reply_echoing_the_sent_prompt_is_rejected_by_the_echo_check_alone`, a 12-word reply against 10 dictated words; dropping only the sent-prompt part of the check fails it and only it (dropping the whole echo check also fails two older tests) |
| 1c | The remove-mode exemption (`PersonalText.WordsTheCleanupMustKeep`) | No code change | `Swear_words_are_protected_from_the_clean_up_unless_the_filter_removes_them`; removing the exemption fails it and only it |
| 2 | `offline_only` fails closed; every CLI command exits 1 on an unreadable settings.json | `--transcribe` now checks `CliCommands.Unreadable` like the others (the malformed-`offline_only` rule was already in place and pinned by `FixBatchSettingsTests`) | `Every_cli_command_stops_on_an_unreadable_settings_file` (no model needed) and `Transcribe_exits_1_on_an_unreadable_settings_file` (gated on `DICTUM_MODEL_PATH`: exit 1 on broken JSON, exit 0 once it is readable); deleting the check fails the gated one |
| 3 | "profanity filter, then corrections" wording | CliCommands, DictationPipeline (its log line now reads "Corrections and the profanity filter changed the text."), PersonalText, ProfanityFilter and Corrections comments, README's command-line table | The pipeline test that checks the log line was updated to the new wording |
| 4 | The SPEC §22 prompt sentence | `CleanupPrompt.KeepSwearWords`, in the default prompt and every built prompt | `The_prompt_keeps_swear_words_and_names_the_filter_as_the_only_control`; benched below |
| 5 | A deterministic timeout pin | — | `A_cleanup_that_times_out_cancels_the_provider_before_it_returns`: the provider gives up with a TimeoutException at once while the deadline is a minute away, so only the timeout path's own `Cancel()` can cancel its token; 20 calls per run, each asserting the token is cancelled when `CleanAsync` returns. Fails on both mutants: `Cancel()` deleted, and the old `CancelAfter(deadline)` timer restored without `Cancel()` |

### Mutation checks (16/10/2026)

Each mutant was applied to the source, the tests rebuilt and the whole ungated suite run (663 tests: the five dialog tests came after), then the
file restored. Failing tests per mutant:

| Mutant | Failed |
|---|---|
| Auto-stop limit ×1000 (`HandsFreeMaxSeconds * 1000000L`) | 4: `Hands_free_auto_stops_at_300_seconds_and_transcribes`, `Stuck_hold_key_auto_stops_at_300_seconds_and_transcribes`, `A_held_key_is_auto_stopped_and_transcribed_at_the_limit`, `An_auto_stop_forgets_the_held_key_so_the_next_press_records` |
| Esc during processing no longer cancels the token | 2: `Esc_during_processing_cancels_the_token`, `Late_polishing_report_after_Esc_does_not_replace_Cancelled` |
| Raw transcript always logged (`log_transcripts` ignored) | 2: `Transcript_text_reaches_the_log_only_when_log_transcripts_is_true(False)`, `Finish_runs_before_the_insert_and_the_pasted_text_is_remembered(False)` |
| Corrections whole-word: look-behind removed from `WholeWords` | 4: `Whole_word_only_ER_never_matches_her`, `A_correction_never_matches_the_stem_of_a_contraction`, `Word_boundaries_leave_class_assess_and_Scunthorpe_alone` (mask, remove) |
| Profanity mask stars nothing | 6, among them `Mask_keeps_the_first_letter_and_stars_the_rest` and `Corrections_run_before_the_profanity_filter_so_a_misheard_listed_word_can_be_fixed` |
| Calibration ceiling `<=` → `<` | 3: `Recommends_the_lowest_WER_at_or_under_the_latency_ceiling`, `Short_clips_are_measured_against_a_ten_second_dictation`, `The_latency_ceiling_is_two_and_a_half_seconds_per_ten_second_clip` |
| 1a, 1b, 1c, 2, 5 | as in the table above |

A harness flaw, caught and corrected: restoring a file by moving its backup back gave it the backup's older timestamp,
so the next incremental build kept the last mutant (the `--transcribe` one) compiled in, and the following gated run
failed that test. Every mutated file was then touched and the solution rebuilt with `--no-incremental` before the
final runs below.

### Verification record (16/10/2026, this machine: Quadro RTX 4000 over Vulkan, the microphone "Remote Audio")

- `dotnet build Dictum.sln -c Release --no-incremental`: 0 warnings, 0 errors.
- Tests on that build. Ungated: 668 total, 651 passed, 17 skipped, 0 failed. Gated (`DICTUM_MODEL_PATH` = small.en,
  `DICTUM_CLEANUP_MODEL_PATH` = qwen2.5-1.5b, `DICTUM_UI_TESTS`, `DICTUM_TRAY_TESTS`, `DICTUM_AUDIO_TESTS` = 1): 668
  total, 666 passed, 2 skipped (the cancel measurement and the download benchmark, not set by design), 0 failed.
- Flaky, not caused by this lane: `Microphone_capture_starts_and_returns_16kHz_audio` failed in 2 of 7 runs over Remote
  Audio (600 ms and 660 ms captured in its 1 s window; it wants 700–1300). `WasapiCapture` is the earlier
  `AudioCapture` with only its interface moved (diffed), so the flake predates the lane (inferred; its earlier rate
  was not recorded).
- The overlay probe on this (interactive) desktop: strong mode (the probe owned the foreground); an ordinary window
  took the foreground; the pill, the orb and the edge each left it unchanged and carried WS_EX_NOACTIVATE, TOOLWINDOW,
  TRANSPARENT, LAYERED and TOPMOST; a click at each centre reached the window beneath; the pill was drawn (#1D2430
  while shown, #F6F6F6 after it hid); every pill state left the foreground unchanged and the pill hid itself after
  Done.
- `scripts\publish.ps1`: 274 files, 291,865,328 bytes (278.3 MiB) in `dist\Dictum`; `dist\Dictum-0.1.0-win-x64.zip`
  103,115,916 bytes (98.3 MiB). The fix-batch WPF build was 306 files, 309.0 MiB / 113.9 MiB.
- From `dist\Dictum\Dictum.exe`, always with scratch APPDATA and LOCALAPPDATA, `--config` a scratch folder and hotkey F24:
  - `--selftest`: PASS; build "unpackaged (direct download)", the config, local, logs, calibration and program
    folders, "Press your hotkey (F24) now: watching for 5 s" (no key pressed: not a failure), mode preset Custom.
  - `--transcribe` on the 4.66 s fox clip with qwen2.5-1.5b: the first run's clean-up timed out at 15 s (a cold Vulkan
    shader cache for the new exe path; the rule-based tidy was pasted); runs 2 and 3: clean-up by the model in 535 ms
    and 301 ms, final text "I'm so the quick brown fox jumps over the lazy dog." (whisper heard "Um so" as "I'm so").
  - `--bench-cleanup` on the 10.46 s clip, speech by medium.en, with the new prompt sentence: qwen2.5-1.5b on Vulkan
    180 ms then 177 ms, accepted both times ("So the quick brown fox jumps over the lazy dog. The quarterly report is
    ready for review. Please send comments by Friday."); the README's previous figure was 0.19 s.
  - Tray mode `--exit-after 5`: exit code 0 after 6.1 s; the log shows the tray icon added, the F24 hook installed,
    whisper's warm-up (281 ms) and the clean-up model's (276 ms), "--exit-after 5: shutting down the same way as Quit",
    the hook removed, both models disposed, "Dictum exited", and no process left.
  - Tray mode `--exit-after 10`: exit code 0 after 11.0 s, tray icon and hook as above (no clean-up model in that
    scratch folder: the notice said so).
  - Migration: a scratch `Dictate` profile (settings.json naming a model inside the old local folder, dictionary.txt,
    models with tiny.en, calibration, logs, verified-models.json). `--selftest` without `--config`: all six moved and
    logged, both Dictate folders gone, `whisper.model` rewritten to the Dictum folder, the record's key rebased,
    tiny.en verified at its new path, PASS. The Intel driver also wrote a shader cache under the scratch profile's
    `LocalLow`: the driver's file, not Dictum's.

### Fix batch after the restructure lens (16/10/2026)

The lens's verdict was Block. SPEC §22 gained three amendments for it (program-folder removal, migration, cloud consent).

- **Program removal by manifest (Blocker).** `scripts\publish.ps1` now writes `manifest.txt` (relative path, bytes,
  SHA-256 of every shipped file) into `dist\Dictum` before zipping: 274 files listed. The packaging lane owns that
  script; only that block was added. The detached step deletes only the listed files still there with the same name
  and size, then manifest.txt, then the folders those files were in once empty (deepest first, never any other
  folder), then the program folder only if empty. No manifest: nothing is deleted, and the first warning says so.
  The first warning lists each program file. The lens's case is now a test: a Downloads folder with two of the user's
  files keeps both, and the folder. Name and size, not the hash: a hash check would mean reading about 280 MB in the
  detached step, and a file that changed size is already refused.
- **Skipped secrets (Major).** The migration records every item it skips because Dictum already has that name, lists
  them in a notification and the log, and "Remove Dictum completely" now deletes everything left in
  `%APPDATA%\Dictate` and `%LOCALAPPDATA%\Dictate`, so a key in Dictate's `.env` cannot survive it. These are listed in
  the first warning too.
- **Earlier build running (Major).** Before migrating, `LegacyMigration.RunWhenSafe` checks `Local\Dictate.SingleInstance`
  (the name in Dictate's Program.cs; the running Dictate on the development machine was seen holding it, 16/10/2026). The tray app shows "Quit
  Dictate first" (Retry / Quit Dictum); the command line exits 1. Nothing else happens until it has quit. The check runs
  only when there is something to move, so a profile with no Dictate folders never waits on a running Dictate. After a
  successful move Dictum offers to remove the old build:
  - its Run value, only when it starts a `Dictate.exe`;
  - its Start menu shortcut;
  - its program folder, only when exactly `%LOCALAPPDATA%\Programs\Dictate` holding `Dictate.exe`.
  The shortcut goes too because the folder's removal would leave it pointing nowhere.
- **Concurrent native loads (Major).** `NativeLoadGate`, one process-wide lock, now wraps every whisper model load,
  every whisper processor build and every llama model load, so calibration's loads and the app's own queue instead of
  overlapping. The model-loading test classes joined the "Process-wide state" collection, which runs on its own.
- **Consent (Minor).** Cloud clean-up consent now lives in `%APPDATA%\Dictum\consent.json` (provider, host, time, app
  version), written only by the dialog and read at load. An `llm.cloud_consent` key in settings.json is ignored with a
  warning. The path registry lists consent.json.
- **Fix hotkey (Minor).** The capture box now pauses `fix_hotkey` as well as the dictation hotkey.
- **About (Minor).** Settings > About says "Offline only" when it is on (SPEC §5).
- **Mutation checks**, each on the whole ungated suite, each restored and touched before the next build:

  | Mutant | Tests failed |
  |---|---|
  | Removal script deletes the folder wholesale | 3 |
  | Refusal without the manifest check | 2 |
  | Skipped item not recorded | 1 |
  | Dictate's leftover folders left out of full removal | 2 |
  | Migration ignores the running earlier build | 1 |
  | settings.json consent honoured again (loader and consent file) | 1 |
  | Fix hotkey not paused | 1 |
- **Verification**:
  - Release `--no-incremental` build: 0 warnings, 0 errors.
  - Ungated: 679 total, 662 passed, 17 skipped, 0 failed.
  - The gated suite ran six times in a row (model paths, UI, tray; audio left out because of its known Remote Audio
    flake). Each run: 679 total, 676 passed, 3 skipped, 0 failed, exit 0, no crash.
  - Published again: 275 files including manifest.txt, 291,920,287 bytes (278.4 MiB); zip 103,140,232 bytes (98.4 MiB).
  - From that dist with a scratch profile: `--selftest` PASS; `--transcribe` on the fox clip, clean-up by the model in
    286 ms and 334 ms.
  - With a scratch Dictate profile while the development machine's Dictate ran, `--selftest` printed "quit Dictate first" and exited 1;
    both scratch files were still in place.

### Final batch after the packaging lane (16/10/2026)

The packaging lane's four items for the source lanes (below), plus two lens re-check findings on the removal code.

- **Packaged migration copies (data loss).** Microsoft Learn ("Understanding how packaged desktop apps run on
  Windows", read 16/10/2026): new files a packaged app writes under AppData go to a per-package location that Windows
  cleans up at uninstall. So when `PackageInfo` says packaged, the first start copies Dictate's files instead of moving
  them (junctions and symbolic links skipped), leaves Dictate's folders whole, says so in the notice, does not offer to
  remove the earlier build, and "Remove Dictum completely" never lists Dictate's folders. The notices are now a pure
  function (`AppShell.MigrationNotices`) so a test reads them. The two `copy: platform.Package.IsPackaged` call sites
  in Program.cs have no test of their own.
- **Inno uninstaller** (tightened in "Last two fixes" below). When `unins000.exe` and `manifest.txt` are in the
  running exe's own folder, the detached step runs `unins000.exe /VERYSILENT /SUPPRESSMSGBOXES` after Dictum exits, so
  Inno's files, shortcuts and the Apps & features entry go too. Anywhere else the manifest deletion runs as before (the
  portable zip). The first warning lists the uninstaller. The C# side is tested with a fake launcher and the script's
  uninstaller branch is tested to delete nothing itself; the uninstaller itself was not run from this path.
- **Folder path as data (security).** The detached script is a fixed constant (`-EncodedCommand`); the folder, the
  process to wait for and the mode reach it in `DICTUM_REMOVE_FOLDER`, `DICTUM_REMOVE_AFTER_PID` and
  `DICTUM_REMOVE_MODE`, and the script checks the folder again (rooted, not a drive root, holding Dictum.exe and
  manifest.txt). PowerShell also ends a single-quoted string at a typographic quote, so escaping ' was never enough:
  with the path put back into the script text (the mutant below), a folder named `Dictum’; New-Item …; ‘x` created
  the injected file in %TEMP% (observed 16/10/2026); with the fix it does not, and the tests also cover ‘ ‚ ‛ and ;.
- **Links never followed (data loss).** The leftover sweep of Dictate's folders, the earlier-build removal and the
  detached step never enter a junction or symbolic link. Links are listed and removed as links; a Dictate folder that is
  itself a link is unlinked, not emptied; a manifest path that crosses a link (or a missing step) is skipped, in C# and
  in the script alike. The lens suggested `AttributesToSkip = ReparsePoint`: the sweep sees links instead so it can
  remove them as links (skipped, they would keep the folder from emptying); recursion never enters them.
- **Icon.** `packaging/tools` gained an `ico` verb sharing the logos' renderer: 16, 24, 32, 48, 64 and 128 px as
  32-bit DIBs and 256 px as PNG, from `site/dictum.svg` into `src/Dictum.Windows/Dictum.ico` (106,040 bytes).
  `<ApplicationIcon>` is set, the file ships beside Dictum.exe, and the tray's idle state loads it (`LoadImageW`
  with `LR_LOADFROMFILE`); the other states keep their drawn icons.
- **Scrub.** The personal name and the development path are gone from `CalibrationResults.cs`, `CleanupPrompt.cs`,
  `FixBatchValidatorTests.cs` and `CleanupCatalogTests.cs` (that case now uses a temp path with spaces).
- **Version 1.0.0** in `Directory.Build.props`; README's zip name follows. Left for their owners: the example in
  `scripts/install.ps1` (`Dictum-0.1.0-win-x64.zip`), `packaging/AppxManifest.xml` (written by package-msix.ps1),
  docs/STORE.md, docs/TESTING-MSIX.md and site/version.json.
- **Tests.** New in `RemovalSafetyTests.cs` (14). The four identical private `FakeRunKey` copies (Pipeline, Platform,
  RestructureFix and RemovalSafety tests) are now one, in `TestFakes.cs`.
- **Mutation checks**, each on the whole ungated suite (693 tests), restored and touched before the next build. Every
  mutant failed exactly the tests named and no others:

  | Mutant | Failed |
  |---|---|
  | Packaged migration moves instead of copying | 1: copies and leaves Dictate's folders whole |
  | Packaged full removal lists Dictate's folders | 1: never lists or deletes Dictate's folders |
  | A copy offers to remove the earlier build | 1: notice test |
  | The copy notice says moved | 1: notice test |
  | Uninstaller never used | 1: runs its uninstaller |
  | Uninstaller guard without the same-folder check | 1: only beside the running exe |
  | Detached step deletes files in uninstaller mode | 1: leaves every file to the uninstaller |
  | Folder path back in the script text, ' escaped | 3: all three folder names |
  | Leftover sweep follows links | 1: junction in Dictate's leftovers |
  | Dictate folder that is a junction swept through | 1: unlinked, not emptied |
  | Detached step crosses junctions | 1: never deletes through a junction |
  | Manifest listing crosses junctions | 1: never deletes through a junction |
  | Earlier-build removal follows junctions | 1: earlier build unlinks junctions |
  | Earlier-build folder that is a junction counts as ours | 1: earlier build unlinks junctions |
- **Verification** (16/10/2026, this machine):
  - Release `--no-incremental` build: 0 warnings, 0 errors.
  - Ungated: 693 total, 676 passed, 17 skipped, 0 failed. Gated (model paths read only, UI, tray; audio off for its
    Remote Audio flake): 693 total, 690 passed, 3 skipped, 0 failed.
  - `scripts\publish.ps1` (unchanged): 276 files in `dist\Dictum`, 275 of them in `manifest.txt` (Dictum.ico
    included), 292,357,120 bytes; `dist\Dictum-1.0.0-win-x64.zip` 103,173,867 bytes. Dictum.exe file version 1.0.0.0.
  - `[System.Drawing.Icon]::ExtractAssociatedIcon` on `dist\Dictum\Dictum.exe`: 32 × 32, identical pixel for pixel
    to Dictum.ico's 32 px frame.
  - `--selftest` from `dist\Dictum` (scratch APPDATA, LOCALAPPDATA and `--config`, hotkey F24, tiny.en copied in):
    "Dictum 1.0.0 self-test on Windows", PASS. The real `%APPDATA%\Dictum` and `%LOCALAPPDATA%\Dictum` still do not
    exist.
  - `scripts\package-msix.ps1 -Store -NoPublish`: `dist\Dictum-1.0.0-store.msix`, 105,918,649 bytes, 350 files,
    unsigned; no version refusal. That run rewrites `packaging\AppxManifest.xml`; the packaging lane's copy was put
    back afterwards.
  - Not verified: the tray's idle icon on a real taskbar, the uninstaller branch against a real installed copy (since
    done: see below), and anything packaged at run time.

### Last two fixes (16/10/2026)

From the lens's probes on the removal step (a planted `unins000.exe` ran silently; a `powershell.exe` beside the exe
ran instead of Windows').

- **Uninstaller only for the setup's own copy (Major).** Uninstaller mode now needs all of: the running exe's folder;
  `unins000.exe`, `unins000.dat` and `manifest.txt` there; Dictum's own entry
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{91637A2B-54D2-44E8-A6F4-B7A8319E9E65}_is1` with
  `InstallLocation` equal to that folder (full path, case-insensitive, trailing separator ignored) and
  `UninstallString` naming that folder's `unins000.exe`. Anything else gets the manifest step. The AppId lives once, in
  `RegistrySetupRecord.AppId`; a test parses `packaging\Dictum.iss` (Inno's `{{` is one brace: Inno Setup help,
  "Constants", read 16/10/2026; "_is1" is Inno's suffix: "AppId", same date). The first warning now lists
  `unins000.dat` too.
- **Windows' programs by full path (same class).** The detached step starts
  `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` (SystemRoot from the environment, the file checked);
  without it the program's files stay and the first warning says why. The whole tree had one other bare-name start in
  the app, Notepad (the fallback for a `.json` with no handler), now `%SystemRoot%\System32\notepad.exe`; and two in the
  tests (`cmd.exe`, `powershell.exe`), now full paths. `ms-settings:appsfeatures` is a protocol, not a path, and stays.
  The scripts' only `powershell` mentions are usage text; `package-msix.ps1` calls `publish.ps1` by full path.
- **Mutation checks** (whole ungated suite, 708 tests then):

  | Mutant | Failed |
  |---|---|
  | InstallLocation not compared | 1: the entry names another folder |
  | InstallLocation compared as raw text | 1: the setup's own copy (upper case, trailing backslash) |
  | No setup entry needed | 3: no entry, entry elsewhere, uninstall string elsewhere |
  | unins000.dat not needed | 1: no unins000.dat |
  | UninstallString not checked | 1: uninstall string elsewhere |
  | PowerShell by bare name | 1: full path, never one beside Dictum |
  | Missing PowerShell not refused | 1: the program stays and the warning says why |
- **End to end (16/10/2026, this machine).** `Dictum-Setup-1.0.0.exe /VERYSILENT /DIR=<scratch>`: the entry appeared
  under the AppId above, `InstallLocation` with a trailing backslash, `UninstallString` the quoted `unins000.exe`, 278
  files. The gated test `A_real_setup_install_is_recognised_and_its_uninstaller_removes_it`
  (`DICTUM_SETUP_INSTALL_DIR`) chose uninstaller mode from the real registry and ran the real step: the folder, the
  entry and the Start menu shortcut were gone afterwards.
- **Verification** (16/10/2026, this machine): Release `--no-incremental` 0 warnings, 0 errors. Ungated 709 total,
  691 passed, 18 skipped, 0 failed; gated (models read only, UI, tray) 709 total, 705 passed, 4 skipped, 0 failed.
  `publish.ps1`: 276 files, 292,360,312 bytes, zip 103,175,067 bytes; `build-installer.ps1 -NoPublish`:
  `Dictum-Setup-1.0.0.exe` 55,233,411 bytes, SHA-256 `5611E2A7F7ABB8D8639A2D4A76426E8A12700D8A23F06D6D93DB73B8B3713254`,
  not signed; `package-msix.ps1 -Test -NoPublish`: `Dictum-1.0.0-test.msix` 105,929,435 bytes, 350 files, signed
  `CN=Dictum Test Signing` (it rewrote `packaging\AppxManifest.xml` to 1.0.0.0, which matches that package). The
  0.1.0 artefacts were already gone from dist; the Store package built earlier from the previous dist was deleted.
  `--selftest` from `dist\Dictum` (scratch profile, F24): "Dictum 1.0.0", PASS; the exe's icon 32 × 32.

## Packaging lane (16/10/2026)

MSIX (SPEC §18.1), the Inno Setup installer (§18.3), CI and the public-readiness pass. Microsoft facts below were read
on Microsoft Learn on 16/10/2026 unless marked otherwise.

### Build-time tools (none ships in Dictum)

| Tool | Version | Why | Source and checks |
|---|---|---|---|
| Inno Setup | 7.1.0, 64-bit edition, installed for the current user only (`%LOCALAPPDATA%\Programs\Inno Setup 7`) | Compiles `packaging/Dictum.iss` | https://jrsoftware.org/isdl.php (read 16/10/2026) lists 7.1.0 (12/08/2026; "Using the 64-bit edition is recommended") and 6.7.3 (26/05/2026). Download: https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe, 14,304,168 bytes, SHA-256 `0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`. Checked three ways, as https://jrsoftware.org/isdl-verify.php describes: Authenticode valid, signer "Pyrsys B.V."; the hash equals the GitHub release's asset digest; `gh release verify-asset` succeeded. Installed with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /NOICONS`. CI installs the same file after the same hash and signer checks (the windows-latest image ships Inno Setup 6.7.1, per actions/runner-images' Windows2025-VS2026 readme, image 20260925.250.1). |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2705 (tools folder `bin\10.0.28000.0\x64`) | MakeAppx, MakePri and SignTool without a machine-wide Windows SDK | nuget.org (read 16/10/2026): newest stable version, published 26/08/2026, listed; nupkg 22,297,017 bytes. `packaging/tools` downloads it with `PackageDownload` into `packaging/out/packages`. `dotnet nuget verify --all`: exit 0 (Microsoft author signature and nuget.org repository signature); makeappx, makepri and signtool Authenticode-valid, Microsoft Corporation. |
| `Svg.Skia` | 5.2.3 (brings `SkiaSharp` 4.148.0 and `HarfBuzzSharp` 14.2.0) | Windows ships no SVG rasteriser; renders the 72 logo PNGs from `site/dictum.svg` at package time | nuget.org (read 16/10/2026): newest stable, published 27/08/2026, MIT, dependencies from its nuspec. `dotnet nuget verify --all`: exit 0, nuget.org repository signature only (no author signature). Third-party (Wiesław Šoltés). Build-time only. |

**Inno Setup licence, for Kyle to decide.** The licence file permits use "for any purpose, including commercial
applications". Separately, https://jrsoftware.org/isorder.php (read 16/10/2026) asks commercial users (a for-profit
organisation with annual revenue over USD 5,000) to buy a licence, says that is "not strictly required", and says a
compiler run by a CI build script expects at least a single-user licence.

### MSIX decisions

- **Two flavours from one template.** `packaging/AppxManifest.template.xml` → `packaging/AppxManifest.xml`
  (generated, ignored by git). `-Store`: Partner Center identity, **unsigned** ("App package requirements for MSIX
  app": the Store "will automatically re-sign your MSIX/AppX packages"). `-Test`: same `Kynni.Dictum` name and display
  names, Publisher = the test certificate's subject `CN=Dictum Test Signing`, signed with it.
- **Version.** The Store needs the first part non-zero and the fourth 0 (same page, "Package version numbering").
  `<Version>` is 0.1.0, so `-Store` refuses to build; **Kyle to decide the first Store version (1.0.0 or later)**. A
  `-Version` switch (passed to `publish.ps1`, so Dictum.exe and the package agree) built and packed the Store flavour
  at 1.0.0 once to prove the path (105,862,817 bytes, NotSigned, identity as SPEC §18.1); that file was then deleted.
- **OS range.** MinVersion `10.0.17763.0`: Store installs need Windows 10 1809 or later ("MSIX features and supported
  platforms"), which is also Dictum.exe's SupportedOSPlatformVersion. MaxVersionTested `10.0.26100.0`, the value
  "Grant package identity by packaging with external location manually" uses. Below build 19041 the `uap10` attributes
  do not exist, so the Application uses `EntryPoint="Windows.FullTrustApplication"` ("Generating MSIX package
  components"; "Application", Remarks).
- **Language `en-AU`**, matching the listing copy. Partner Center accepts listing languages the package does not
  declare ("Add and edit Store listing info for MSIX app"), so an en-US listing remains possible.
- **Startup task** `DictumStartup`, `Enabled="false"` ("desktop:StartupTask": `true` would enable it at first launch
  without `RequestEnableAsync`).
- **App execution alias `Dictum.exe`** (lane decision; Kyle may veto): without it a packaged copy's command-line modes
  cannot run with package identity, and SPEC §18.1's test plan runs `--selftest` from the package. It is an extension,
  not a capability ("Integrate your desktop app with Windows by using packaging extensions").
- **Logos.** 72 PNGs ("Construct your Windows app's icon"): Square44x44Logo, StoreLogo, Square71x71Logo,
  Square150x150Logo, Wide310x150Logo and Square310x310Logo at scale 100/125/150/200/400 (sizes rounded half up, as
  Learn's table does), and the app-list icon at target sizes 16 to 256 in plated, `altform-unplated` and
  `altform-lightunplated` forms. Icons fill their canvas; tiles show the mark at half their height, inside the "at least
  16% margins on each side" of "Construction guidelines for Windows 10 icons" (the 50% is a design choice). The
  dark-surface (`unplated`) forms use `site/favicon.svg`, the same mark with its faint light outline (observed: white at
  alpha 102 on the outline, transparent there in the plain form).
- **One resources.pri.** `makepri createconfig`'s default splits scales into `resources.scale-*.pri` (observed: four
  extra files on the first build), which only a bundle's resource packages load. The script removes the `<packaging>`
  element, so one `resources.pri` holds all 72 candidates (observed: 6 named resources, 72 candidates).
- **Test certificate.** Learn's recipe ("Create a certificate for package signing": `-Type Custom`, DigitalSignature,
  code-signing EKU, end-entity basic constraint) rather than SPEC §18.1's `-Type CodeSigningCert` wording; both give a
  code-signing certificate. Kept in `Cert:\CurrentUser\My`; `.cer`, `.pfx` and its random password exported to
  `packaging/test-cert/` (ignored by git). No script changes trust: importing into Local Machine > Trusted People is
  the tester's elevated step (docs/TESTING-MSIX.md). CI makes a new certificate per tagged build and publishes only
  its `.cer`.
- Windows App Certification Kit: Learn now calls it "deprecated and no longer maintained" ("Package a desktop or UWP
  app in Visual Studio"); it is not in the BuildTools package, so it was not run.

### Installer decisions (`packaging/Dictum.iss`)

- Per user (`PrivilegesRequired=lowest`): `{autopf}\Dictum` resolved to `%LOCALAPPDATA%\Programs\Dictum` (observed),
  the folder install.ps1 uses and PackageInfo checks. Start menu `Dictum.lnk` at the path RemovalSteps and install.ps1
  use; launch at login writes the Run value `Dictum` = `"<exe>"`, the format RunKeyStartup reads.
- Upgrade: `[InstallDelete]` empties `{app}\runtimes` first, so no stale native DLL survives a version change
  (observed: a planted stale DLL was gone after reinstalling); a `.env` beside Dictum.exe is never shipped and stays
  (observed).
- `AppMutex=Local\Dictum.SingleInstance`: observed, a silent setup while Dictum ran exited 1 and changed nothing.
- Uninstall: removes the Run value only while it starts this copy (as uninstall.ps1 and the app do); asks before
  deleting `%LOCALAPPDATA%\Dictum`, `%APPDATA%\Dictum` and an exe-folder `.env` (default No); a silent uninstall keeps
  them. Warns, without blocking, when the Store build or the earlier Dictate build is present.
- Inno does not honour scratch `LOCALAPPDATA`/`APPDATA` variables (observed: it installed into the real
  `%LOCALAPPDATA%\Programs\Dictum`), so the install test used that new folder and removed it afterwards.
- AppId `{91637A2B-54D2-44E8-A6F4-B7A8319E9E65}`: never change it.

### CI decisions (`.github/workflows/ci.yml`)

- windows-latest; build, ungated tests (`dotnet test --solution Dictum.sln -c Release --no-build`), publish, zip as an
  artifact on every push and pull request. On a `v*` tag: the tag must equal `v<Version>`, then the installer, the test
  MSIX, its `.cer` and `SHA256SUMS.txt`; a separate job on ubuntu-latest, the only one with `contents: write`, creates
  the release with `gh release create --generate-notes`. No secrets.
- Actions pinned to commit SHAs resolved 16/10/2026 through the GitHub API: checkout v7.0.1, setup-dotnet v6.0.0,
  upload-artifact v7.0.2, download-artifact v8.0.2. Dependabot: NuGet and Actions monthly, one grouped pull request per
  ecosystem at most (keeps under the open-PR alarm), Avalonia major versions ignored (SPEC §21 says Avalonia 11).

### For the source lanes (not changed here: outside this lane's files)

1. A packaged first start runs the Dictate → Dictum migration. Windows writes new files under a packaged app's AppData
   to a private per-package folder, allows deletes of existing files, and removes the private folder at uninstall
   ("Understanding how packaged desktop apps run on Windows"), so migrated files could end up only in the package and
   be lost with it (inferred). Skip or copy-migrate when packaged; docs/TESTING-MSIX.md warns testers meanwhile.
2. "Remove Dictum completely" deletes the manifest's files but not Inno's `unins000.exe`/`unins000.dat` or its Apps &
   features entry. SPEC §18.3 says the removal path can invoke the uninstaller: run `unins000.exe /VERYSILENT
   /SUPPRESSMSGBOXES /NORESTART` when it sits beside Dictum.exe.
3. Dictum.exe has no icon resource (no `ApplicationIcon`), so the Start menu, Apps & features and the installer show
   the generic program icon; the MSIX has its own logos.
4. Public-readiness leftovers in `src/` and `tests/`: "Kyle" in `CalibrationResults.cs` (2), `CleanupPrompt.cs` (1)
   and `FixBatchValidatorTests.cs` (1, a dictated name); a development folder path in `CleanupCatalogTests.cs`.

### Verification record (16/10/2026, this machine)

- `scripts\publish.ps1`: 274 files plus `manifest.txt`, 291,892,292 bytes; `dist\Dictum-0.1.0-win-x64.zip`
  103,140,232 bytes.
- `scripts\build-installer.ps1 -NoPublish`: `dist\Dictum-Setup-0.1.0.exe`, 55,217,665 bytes, SHA-256
  `e289c42d678c1a093603e8a6292074f408eaf0154c15b0b59d721dba724b6002`, not signed.
- `scripts\package-msix.ps1 -Test -NoPublish`: `dist\Dictum-0.1.0-test.msix`, 105,894,052 bytes, 349 files, signed
  `CN=Dictum Test Signing`; the manifest inside equals `packaging\AppxManifest.xml`; no `.env` inside.
  `Add-AppxPackage` without trust: `0x800B0109`, nothing installed. Trust, install, launch and uninstall: **not run**
  (no elevation; a person's decision).
- Installer, silent, with `/TASKS="startup"`: 277 files, Start menu shortcut, Run value, Apps & features entry
  "Dictum 0.1.0". `--selftest` from the installed copy (scratch APPDATA, LOCALAPPDATA and `--config`, hotkey F24,
  tiny.en copied into the scratch folder): PASS, "unpackaged (direct download)". Silent uninstall: folder, shortcut,
  Run value and Apps & features entry gone; the scratch user data kept; the real `%APPDATA%\Dictum` and
  `%LOCALAPPDATA%\Dictum` never created; the running Dictate build untouched.
- The CI build and test commands, run locally: build 0 warnings, 0 errors; tests 679 total, 662 passed, 17 skipped,
  0 failed. The workflow itself has not run on GitHub (nothing is pushed); its YAML parses (js-yaml).
- Source files changed while this lane worked (other lanes), so these artefacts are a snapshot of 16/10/2026 ~02:27;
  rebuild before any release.

## Orchestrator change after the last lens tick (16/10/2026)

`SystemPrograms.Root` now derives the Windows folder from `Environment.SystemDirectory`
(GetSystemDirectory) instead of the `SystemRoot` environment variable, which a parent
process could set to point PowerShell and Notepad at a planted copy (lens probe, Minor).
Pinned by `SystemProgramsTests`; observed failing with the variable lookup restored
("Expected: C:\WINDOWS, Actual: ...\Temp\fakewin"). This crosses no privilege boundary
but closes the last way the removal step could be steered from outside the app.

## Mouse-button hotkeys, fix-hotkey capture box and snippets (16/10/2026, branch feature/mouse-hotkey-snippets)

Asked for: hold a mouse button instead of a key; choose the fix-last hotkey by pressing it; "say a phrase, paste a
link" snippets.

**Mouse buttons as hotkeys**

- A mouse button is a virtual key to Windows (VK_RBUTTON 0x02, VK_MBUTTON 0x04, VK_XBUTTON1 0x05, VK_XBUTTON2 0x06;
  Virtual-Key Codes page on Microsoft Learn, read 16/10/2026), so `HotkeySpec.CanFireTogether` and the state machine
  needed no change and `HotkeyMatcher` and `HotkeyCapture` treat a button as a key: the parser gained the names
  (`RightMouse`, `MiddleMouse`, `Mouse4`, `Mouse5`, with aliases) and `HotkeySpec.UsesMouse`. The matcher's one
  difference for a button (review fix, below) is that a button trigger is always swallowed.
- One thread hosts both hooks. `LowLevelMouseProc` and `LowLevelKeyboardProc` are each "called in the context of the
  thread that installed it" (Microsoft Learn, read 16/10/2026), so installing WH_MOUSE_LL from the existing hook thread
  serialises both callbacks on it and one matcher serves keys and buttons without a lock; `Ctrl+Mouse4` works for free.
  The class is now `InputHook` (file `InputHook.cs`, formerly `KeyboardHook.cs`); the log line reads "Input hook
  installed" and `TrayStartupTests` counts that.
- A hotkey listener installs the mouse hook only when its hotkey names a mouse button, decided once when it starts, on
  the hook thread. A hotkey change recreates the listener (the shell disposes it and starts another); there is no
  in-place swap (`IHotkeyListener.UpdateMatcher` was unused in production and was deleted with its refresh message in
  the review fixes). Reason: a WH_MOUSE_LL hook is on the path of every mouse event on the PC; users of a keyboard
  hotkey should not pay for it. The callback decodes only the six button messages (WM_RBUTTONDOWN/UP 0x0204/0x0205,
  WM_MBUTTONDOWN/UP 0x0207/0x0208, WM_XBUTTONDOWN/UP 0x020B/0x020C; side button from the high word of
  MSLLHOOKSTRUCT.mouseData, XBUTTON1 = 1, XBUTTON2 = 2; all read on Microsoft Learn 16/10/2026) and passes moves and
  wheel turns on without reading the struct (pinned since the review fixes by decoding tests on a marshalled struct,
  with WM_MOUSEMOVE 0x0200 and WM_LBUTTONDOWN 0x0201 from their Microsoft Learn pages, read 16/10/2026).
- A hotkey listener's `Start()` returns false when its mouse hook fails, so the existing "The global hotkey could not
  be registered" notice covers it. A fix_hotkey listener that fails to start is now disposed before the notice (review
  fix), so its thread and a keyboard hook that did install do not live on unreferenced.
- The left button is refused by the parser (names, `0x01`, inside a chord) with the reason: holding it to dictate would
  swallow every click. The hook never decodes it, so `HotkeyCapture` can only ever see it from a fake; `NameOf(0x01)`
  says "LeftMouse" for the capture box's "Windows received" line.
- A mouse-button trigger is always swallowed, down and up, whatever `suppress_hotkey` says (review fix C): with
  suppression off its click reached the app under the pointer on release (a context menu on the right button, Back in
  a browser on Mouse4) and wrecked the dictation. `HotkeyMatcher` suppresses when `suppress_hotkey` is on or the key is
  a mouse button; `suppress_hotkey` still governs keys. Settings has no note for that case any more.
- The dictation listener is on only while `enabled` is true, no capture box pauses it, and, for a mouse-button hotkey,
  the speech model is ready (`ModelManager.IsReady`; review fix A) or a dictation is under way (the controller is not
  idle; lens 2, below). Reason: the button is always swallowed, so during the first-run download (the hook starts
  before `Models.Ensure`), a failed load or a model swap it vanished from every app with nothing to dictate with; but a
  dictation already under way keeps it, so a model load that starts mid-dictation cannot cut off its release, its
  stop tap or Esc. One helper, `AppShell.ApplyHookEnabled`, applies the rule from `StartHook`, `ApplyEnabled`,
  `PauseHotkey` and its undo, every model state change, and every tray state the controller reports (so the listener
  goes off again when that dictation ends); the shell now subscribes to both model managers' `Changed` in its
  constructor instead of in `Start` (nothing loads before `Start`, so production behaviour is the same, and the rule
  holds from the first load). A keyboard hotkey keeps its old rule (the controller ignores a press while no model is
  ready), and so does the fix hotkey. The edge accepted here before lens 2 (a load starting mid-hold swallowed the
  release with no Up signal, so the recording ran on to the next press or the `hands_free_max_seconds` auto-stop) was
  narrowed by it, not gone: the gate still closes as a dictation ends, which can be while its stop tap is down, and the
  matcher then swallowed that release with no Up, so the next press was taken for auto-repeat and started nothing. The
  lens on dd2bef3 (below) found it; since that fix a listener switched off stops new presses only, and a press under
  way still has its release swallowed and reported.
- The key monitor (capture box, `--selftest`) always installs the mouse hook: it is short-lived, and the box must be
  able to name a button when one is pressed. So the mouse is also watched while a capture box listens or `--selftest`
  runs, whatever the hotkey (the README said "only while a hotkey names a mouse button" until the review fixes). For
  the monitor the mouse hook is best-effort (`MouseRequired` is false): a refused WH_MOUSE_LL is logged as a warning,
  keys are still reported and `Start()` succeeds.
- Observed here (16/10/2026, after the review fixes; ungated test
  `The_mouse_hook_is_installed_only_for_a_hotkey_that_names_a_mouse_button`): a real `InputHook` for Mouse5 logged
  "Mouse hook installed", and one for F24 logged "Input hook installed" with no mouse hook line. The gated SendInput
  test for Mouse4 (`DICTUM_UI_TESTS=1`, `Hook_thread_sees_an_injected_side_button_press`) passed on this desktop after
  the review fixes, as it had before them: an injected XBUTTON1 down and up reached the real WH_MOUSE_LL hook and the
  matcher posted exactly one Down and one Up (the F24 keyboard test passed in the same run). Full suite after the
  review fixes: 781 tests, 762 passed, 19 skipped, 0 failed (before them: 752, 733, 19, 0).
- Not changed: the Mac row in `src/Dictum.Mac/README.md` names the CGEventTap mapping that would be needed.

**Fix-hotkey capture box**

- Settings → General now has a second `HotkeyCaptureBox` (caption and prompt are constructor parameters; "None" when
  no hotkey is set; `Reset` after "Turn off"), with "Use for Fix last dictation" and "Turn off". The box raises
  `Started`, and the page stops the other box, so a press names one hotkey, never both; both hotkeys stay paused while
  either box listens (the existing `PauseHotkey` counter). The old text field is gone.
- Keyboard reach (review fix D): a keyboard user tabbing to a box's Save button made Tab the candidate, and a bare Enter
  or Space (the buttons' own activation keys) did the same. The parser now refuses Tab like Esc ("Tab moves keyboard
  focus everywhere and cannot be the hotkey", by name and as `0x09`), and `HotkeyCapture.Feed` names no hotkey for a
  gesture whose trigger is Tab or for a bare Enter or Space; chords such as `Ctrl+Shift+Space` stay valid, and the box
  still shows what arrived ("Tab up"). A settings.json naming `Tab` now gets the loader's usual treatment of an invalid
  hotkey: a warning and the default.
- Each box's Change… / Stop listening button has an accessible name tied to its caption ("Press your hotkey: change",
  "Press the key for Fix last dictation: stop listening"), so the two boxes' buttons can be told apart (review fix).
- A box the user has not captured a new key in follows settings.json on every redraw: the page remembers the value it
  last saw per box and resets a box still showing it; a new capture is kept until it is saved (review fix).

**Snippets (`snippets.txt`)**

- Reuse, not a new primitive: a snippet is a `Corrections` instance with `Verbatim = true`
  (`Corrections.LoadSnippets`), loaded into `PersonalText.Snippets`. Same file format, whole-word matching,
  longest-first and later-line-wins rules; the only differences are verbatim output (a correction would have written
  "Https://…" for "My calendly" at a sentence start, because `Cased` capitalises a lower-case written form there) and
  `\n` standing for a line break and `\\` for a backslash (corrections keep the characters as written).
- Exact text (review fix B): the `Corrections` constructor no longer trims a snippet's text (it trimmed after `\n` had
  become a line break, so a break at either end, or a text that was only a break, was lost); corrections still trim.
  `SnippetLine` writes each `\` as `\\` before writing line breaks as `\n`, and loading reads both escapes in one
  left-to-right pass (`\\(\\|n)`), so a typed `\\server\new-clients` reads back as typed instead of with a line break
  in it; a hand-written lone backslash before any other character is left as it is. `CheckNewSnippet` refuses a phrase
  containing a line break (it would split the line in the file).
- `PersonalService.CheckNewSnippet` is an instance method (review fix): it also refuses a phrase the profanity filter
  would change, because the filter runs before the snippets and such a snippet could never match.
- Type mode types a line break as the Enter key (`SendInputSender`), which sends the message in some chat apps. Not
  changed in code; the README and the Add-a-snippet window say so.
- Known and unhandled: Fix last dictation compares the user's edit with what was pasted, which includes any snippet
  text, so a correction it suggests from a change inside snippet output (a word of a pasted address, say) is written
  to corrections.txt but can never fire: corrections run on the dictated text, before the snippets.
- Logging: counts only, as for the other files; the pipeline's line now reads "Corrections, the profanity filter or
  snippets changed the text.", and the pasted text (snippet text included) reaches the log only with `log_transcripts`.
- Order: corrections, profanity filter, snippets, paste. Snippets last because their text is typed by the user, not
  dictated, and must be pasted exactly; it also lets a correction repair a misheard trigger before the snippet matches
  and keeps a correction or the filter from rewriting a word inside a pasted URL. Trade-off, deliberate: the filter does
  not see a snippet's text, so a user can paste a word the filter would mask by putting it in a snippet themselves.
- `snippets.txt` is a personal file everywhere the others are: `AppRules.PersonalFiles` (watcher reload),
  `PathRegistry` (Storage page, removal), the removal confirmation text, `PersonalText.Summary` (count only). It is not
  in the Dictate→Dictum migration list because the earlier build never had one.
- UI: tray panel → "Snippets" (the fifth action button; "Settings" spans the last row) and "Add a snippet…" in the
  dictionary window open `AddSnippetWindow`: two fields, the second multi-line, `PersonalService.CheckNewSnippet` for
  the refusals, "Updated." when the phrase already existed. Quitting closes it with the shell's other windows (review
  fix: `CloseWindows` had left it out).

**Review fixes: pins (16/10/2026)**

Each test below was observed failing on the reverted behaviour, then passing with the fix restored:

- A: `A_mouse_hotkey_lets_its_button_through_until_a_speech_model_is_ready`, with the readiness condition removed from
  `ApplyHookEnabled`, and separately with the call removed from `ModelStateChanged`.
- B: `A_snippet_keeps_a_line_break_at_either_end_or_one_that_is_all_of_it` (the trim restored);
  `A_backslash_is_written_as_two_and_read_back_as_typed` (the old escaping, and separately the old unescape alone);
  the three line-break rows of `A_snippet_needs_a_one_line_phrase_with_a_word_in_it_and_some_text` (the refusal removed).
- C: `A_mouse_button_is_swallowed_even_with_suppression_off` (suppression following the setting again).
- D: `Tab_is_refused_like_Esc`, `Tab_in_any_gesture_and_a_bare_Enter_or_Space_name_no_hotkey` and
  `Tab_and_a_bare_Enter_or_Space_in_a_listening_box_choose_nothing` (the parser refusal removed); the last two again
  with only the bare Enter/Space rule removed.
- Minor fixes: `A_phrase_the_profanity_filter_would_change_is_refused`,
  `A_fix_hotkey_listener_the_system_refuses_is_disposed`, `Quitting_closes_the_add_a_snippet_window`,
  `A_box_with_no_new_capture_follows_an_edit_to_settings_json` and the accessible-name lines of
  `Only_one_capture_box_listens_at_a_time`, each with its fix reverted.
- Not pinned by a test: the key monitor's best-effort mouse hook (it needs Windows to refuse WH_MOUSE_LL).

**Recalled, not verified (this batch)**

- That Windows applies a left/right button swap before WH_MOUSE_LL sees the messages (so `RightMouse` would be the
  physical left button on a swapped mouse). The README avoids the claim and says only that a name means whichever
  button Windows reports as that button.
- That an Avalonia `TextBox` with `AcceptsReturn` keeps Enter from firing the dialog's default button (the Add-a-snippet
  window relies on it for the multi-line field).

**Lens 2, 16/10/2026**

The second lens returned Needs changes: one Major, five Minor, no blockers. All six are fixed:

1. Major: the hook callbacks' swallow had no test (the lens made `MouseProc` and `KeyboardProc` never return 1 and the
   suite stayed green). Both are now `internal` (Dictum.Windows already grants the tests its internals) and tests call
   them on a never-started `InputHook` with a struct marshalled as Windows hands it over:
   `The_mouse_hook_callback_swallows_the_hotkey_button_and_passes_the_others` (Mouse5, `suppress_hotkey` on and off:
   XBUTTON2 down and up return 1 and post Down and Up; the right button and XBUTTON1 pass) and
   `The_keyboard_hook_callback_swallows_the_hotkey_when_told_and_passes_other_keys` (F24).
2. Minor: a model load that started mid-dictation turned a mouse listener off, so a hands-free stop tap reached the app
   and Esc posted nothing until the load ended or the auto-stop. The gate now also holds while the controller is not
   idle, and `SetTrayState` re-applies it (the bullet above). For it to go off again reliably when the dictation ends,
   `DictationController` now reports the end of processing to the UI from `Finished`, after the machine is idle: it had
   reported it from the pipeline task before even queuing `DoneInput`, so the gate, re-applied on that news, could still
   read the dictation as under way and keep the button swallowed after it ended with no model ready (after a model
   removed mid-dictation, until something else re-applied the gate, such as the next model change). Pins:
   `The_UI_hears_that_a_dictation_ended_only_once_the_controller_is_idle` (ControllerTests) and
   `A_mouse_hotkey_stays_on_through_a_dictation_while_a_model_loads_and_goes_off_when_it_ends`, on a started shell
   (added beside the readiness test above, whose shell is never started and so has no controller).
3. Minor: Settings never said that the chosen button stops reaching other apps altogether (a quick click is swallowed
   too). `GeneralPage.MouseNote` now says so, the fix-last card (whose listener has no model to wait for) says "While
   Dictum is on, a mouse button chosen here no longer reaches other apps at all.", and the README says both. Pin:
   `Settings_says_a_mouse_button_hotkey_no_longer_reaches_other_apps`.
4. Minor: the Add-a-snippet window and `Corrections.SnippetLine` trimmed all whitespace, losing a typed line break at
   either end. Both now trim only spaces and tabs; those are still dropped at either end, as a `phrase => text` line
   cannot hold them (the reader trims around the arrow). Pin, through the window:
   `A_line_break_typed_at_either_end_of_a_snippet_is_kept`.
5. Minor: `CheckNewSnippet` now also refuses a phrase a correction would change (corrections run first, so its snippet
   could never fire), compared by `WholeWords.Key`, so a correction that only changes case passes (snippets match any
   case). Pin: `A_phrase_a_correction_would_change_is_refused_but_a_change_of_case_passes`.
6. Minor: the parser's comment stated the button swap as fact; it now says it is recalled, not verified. Pin:
   `The_parser_does_not_state_the_unverified_button_swap_as_fact` (reads the source).

Each pin was observed failing on the reverted behaviour, then passing with the fix restored: (1) each callback with its
`return 1` removed ("Expected: 1, Actual: 0"); (2) the gate without the controller term, `SetTrayState` without
`ApplyHookEnabled`, and the controller's old order (the UI was told Idle, and Error, while `IsIdle` was false; the
started-shell test passed on the old order in the run made, most likely because its 10 ms polling lets the queued
`DoneInput` land first, which is why the controller test exists); (3) each sentence removed; (4) `Trim()` restored in the window, and separately in
`SnippetLine` (each wrote `my sign off => Kyle Manson\nKynni Pty Ltd`); (5) the check removed, and separately made
case-sensitive; (6) the old comment restored. Full suite after these fixes: 791 tests, 772 passed, 19 skipped, 0 failed
(before: 781, 762, 19, 0).

Not verified by the lens, and still not verified: whether Windows applies a button swap before WH_MOUSE_LL; whether an
Alt or Win + mouse-button chord can still open the menu bar or Start once its click is swallowed (the matcher sends no
menu mask for a swallowed trigger); cursor latency and hook timeouts with the mouse hook installed; the 19 gated desktop
and tray tests, skipped under Remote Desktop, were not re-run.

**Lens on dd2bef3, 16/10/2026**

No blockers or majors; two minors, both fixed:

1. Minor: a release swallowed while the listener was off reported no Up. Since lens 2 the readiness gate closes as a
   dictation ends, which can be while the hotkey button is still down. The lens, on a started shell with Mouse4,
   deleted all app data mid-dictation (Settings > Storage, `ModelManager.Unload`) and tapped Mouse4 to stop; the
   transcription failed, `Finished` closed the gate, and the tap's release was swallowed with no signal, so once a
   model was ready the first press started nothing (the state machine still held the button down and read the press
   as auto-repeat) and the second worked. A second path did the same: any key or button arriving while the listener
   was off cleared the matcher's `_active`, so a release after the listener came back on was swallowed with no Up too.
   The lens also found the swallow itself unpinned (the off branch returning `Pass` kept the suite green). Fix, in
   `HotkeyMatcher.OnKey`: a listener switched off stops new presses only. The early return for an off listener is
   gone; an off key-down passes untouched unless it is the auto-repeat of a swallowed trigger, and a release takes the
   same path on or off, so the trigger's release is swallowed if its press was and reports Up if Down was reported and
   Up was not yet. `_active` already meant exactly that everywhere but the old off branch, so it is the owed-Up flag
   and there is no new state. The auto-repeat of a swallowed key held while off now stays swallowed too: it passed
   before while its release was swallowed, which showed the app a key going down that never came up. Chosen over the
   lens's one-line alternative (Up from the off branch's swallow), which leaves the second path open (observed: the
   second pin below fails on it) and would report a second Up for a chord whose modifier was let go first. Who hears
   the new Up: the state machine acts only on an Up for a key it holds (switched off, or reset by `SetEnabled`, it
   ignores it); the fix-last listener acts on Down only (`AppShell.ApplyFixHotkey`); the calibration window
   (`CalibrationWindow.OnHotkey`, through the shell's redirect) acts on Up only while a take is recording, so a take
   started by holding the hotkey now ends on its release even if the listener went off mid-hold (a model load
   starting), where before it ran on to Stop or the take limit, which drops it as too long; an Up while no take
   records is ignored. That calibration path is read, not tested (its code is unchanged).
2. Minor: the README said only that a mouse-button hotkey leaves its button alone until a speech model is ready. It now
   adds that a dictation already under way keeps it swallowed until that dictation ends, so the stop tap and Esc still
   work (as SPEC §3 says). The README had no test: the lens 2 record names
   `Settings_says_a_mouse_button_hotkey_no_longer_reaches_other_apps` for "the README says both", but that test reads
   the Settings window only.

Pins, each observed failing on the reverted behaviour (dd2bef3's `HotkeyMatcher.cs`; for the last, dd2bef3's README),
then passing with the fix restored:

- `A_trigger_released_while_disabled_is_swallowed_and_reports_Up` (MouseHotkeyTests): the lens's sequence at matcher
  and state machine level (Mouse4 hands-free, the stop tap, processing fails, the release with the listener off, the
  next press). Reverted: "Expected ... Signal = Up ... Actual ... Signal = None"; with that check switched off for one
  run, the next press returned no effects instead of starting a capture. It also failed with the release passed on
  while off (Suppress False) and with the swallow kept but no Up.
- `A_trigger_still_held_when_the_listener_comes_back_reports_Up_once_on_release` (MouseHotkeyTests): off and on again
  with typing in between, then a chord whose modifier was let go first. Reverted: the release after the listener came
  back reported None. It also failed with only the second path restored (an off event clearing `_active`), with the
  lens's one-liner, and, in its chord half, with a variant that reports a second Up.
- `A_swallowed_key_still_held_when_disabled_keeps_its_repeats_and_release_swallowed` (HotkeyMatcherTests, RightCtrl).
  Reverted: the auto-repeat with the listener off passed through (Suppress False). It also failed with the off check
  moved above the auto-repeat check, and with the swallow kept but no Up.
- `The_README_says_a_mouse_hotkey_stays_swallowed_through_a_dictation_under_way` (reads README.md). Reverted:
  "Sub-string not found".

Full suite after these fixes: 795 tests, 776 passed, 19 skipped, 0 failed (before: 791, 772, 19, 0).

Not verified here: the lens's started-shell scenario was not re-run (the shell tests' fake listener posts signals
without the matcher, so the first pin drives the matcher and the state machine directly); the 19 env-gated tests
(12 model/LLM/download, 4 desktop, 1 audio, 1 tray, 1 setup) stay skipped.

## Languages and translation (16/10/2026, branch feature/languages, worktree .worktrees/languages)

- Built from main in its own worktree while `feature/mouse-hotkey-snippets` was under review in the main tree, so the two
  lanes never edited one file at the same time. Merge order is Kyle's call; both touch README.md, docs/SPEC.md and this
  file only in separate sections.
- Facts, all fetched 16/10/2026 and recorded in SPEC §23: Whisper's 100-language table (some languages are not in it, so they cannot be chosen), the README's words on the turbo model and translation, Whisper.net 1.9.1's `WithTranslate` and
  `WithLanguageDetection` (its XML docs in the NuGet cache), and the HF API tree at the pinned commit for the six
  multilingual files (controls: small.en, medium.en, large-v3-turbo and its q5_0 matched the catalogue byte for byte).
- One guard function rather than scattered checks: `WhisperLanguages.Problem(model, language, translate)` is the only
  place that knows an `.en` model is English-only and that turbo cannot translate; the Models page, the engine's load
  warning and `--selftest` all call it. English-only is read from the name (".en"), turbo from the "large-v3-turbo"
  prefix, so a user's own file named like a catalogue entry is judged the same way and an unknown name is trusted, except
  for Cantonese, which is allowed only for names starting with large-v3 (see the Cantonese bullet below).
- Language and translate are decoder options (`DecodeOptions`), rebuilt through the existing `SetDecoding` →
  `Reconfigure` path like the dictionary prompt, not a model reload. The first cut reloaded the model; the lens pointed
  out that a reload of large-v3 (3.1 GB plus the warm-up) leaves `IsReady` false and the hotkey dead meanwhile, that the
  extra reload rule and the trailing optional `translate` parameter were a second mechanism for something the decoder
  rebuild already does, and that calibration inheriting the dictation language would score English sentences decoded
  as another language. With the options in `DecodeOptions`, calibration's own `DecodeOptions(prompt, beam)` is English by
  construction, so the calibration notes were retired.
- The clean-up is skipped (`TranscriptCleaner.Plain`, whitespace only) whenever the output is not English: the rules
  capitalise a standalone "i" (Polish "and") and the prompt asks for Australian English, so running either on any text other than English
  would change it. Corrections and the profanity filter still run; they are whole-word matches the user
  wrote. Status "skipped: …" is visible in About and `--selftest`.
- Combining marks are part of a word (lens, 16/10/2026). A vowel sign, virama or anusvara is a combining mark
  (`\p{M}`), not a letter, so the shared whole-word matcher (`WholeWords`: corrections, snippets, the profanity list)
  treated it as a word boundary: a correction on a short word turned a longer word containing it (one with the vowel sign
  U+093E) into a different word, a snippet on a short word fired inside a longer one, Fix last suggested nothing for an edit
  that changed only a vowel sign or anusvara (U+093E, U+0902), and mask starred the base letters but left a vowel sign
  (U+0940) showing. The matcher's look-arounds now count
  `\p{M}` as inside a word, and the Fix-last diff and the profanity mask and removal ask `WholeWords.IsWordChar` (a letter,
  a digit, or a non-spacing or spacing combining mark). English matching (apostrophes, possessive "s", digits, hyphens) is
  unchanged. This extends Batch 2's "a word character is a letter or a digit".
- Known limitation: Detect automatically without translation always gets the plain cleaner, because the cleaner is built
  when the settings load, before whisper has detected any dictation's language; English speech under `auto` is therefore
  not tidied either, and the README tells an English speaker to choose English. Retire when the cleaner is chosen per
  dictation from the language whisper detected: Whisper.net 1.9.1 reports it for each segment (`SegmentData.Language`,
  "Gets the language of the current segment." in its XML docs in the NuGet cache, read 16/10/2026).
- Settings > Clean-up and Privacy for text that is not English (16/10/2026): the page falls through to the approved "On, but
  nothing is sent: the clean-up in use runs on this PC." (Privacy: "Nothing leaves this PC: …") instead of the local-server
  line, and takes precedence over the offline-only line, because switching offline only off would still send nothing. For
  English text the offline-only line keeps its precedence, since it tells the user why nothing is sent
  (`CleanupPage.OfflineOnlyIsWhyNothingIsSent`).
- The Mode presets were left on English-only models (no multilingual twin of Light/Full), because choosing which
  multilingual model a preset should write needs measurements on the staff's actual machines that do not exist yet.
  The Mode page's note is computed per preset from the one guard (`WhisperLanguages.ModeNote`): on a GPU, Full writes
  large-v3-turbo, which hears every language, so only Light is named unless translation is on.
- Every speech-model load passes through `ModelCoordinator.BeginSpeechLoad`, which shows a warning balloon (and still
  loads) when the model cannot do what the language settings ask: the Settings page refuses only its own two controls,
  and a model chosen from the tray picker, a Mode preset, Choose file, calibration's Use this or a hand edit used to
  get a log line nobody reads.
- Cantonese (`yue`) is refused unless the model name starts with "large-v3": openai/whisper's tokenizer takes the first
  `num_languages` entries of its table (99 by default; read in tokenizer.py 16/10/2026) and `yue` is the 100th; which
  models carry the 100th token (the large-v3 family) is recalled from openai/whisper's model loading, not re-read.
- Observed with the first cut (the small.en runs below): an English-only model given `translate: true` or
  `language: auto` loads and transcribes English after the guard's warning, so whisper.cpp proceeds rather than fails;
  the combination is now refused in Settings and `--selftest`, and ballooned on every other path.
- Recalled, not verified: whisper's handling of code-switched speech (two languages in one sentence) under a fixed language (the README now
  says "not tested here"); CPU timings for the multilingual `medium` (the README table has only the `.en` twins).
- Not updated on this branch, for Kyle to settle before the Store submission: docs/STORE.md says "Dictum dictates in
  English" (and its Policy 10.1 row), and site/index.html says "English dictation." Both are outward-facing copy.
- Not measured here: no audio in the target languages was available in this session. The owner tests with real recordings;
  that result decides whether translation or accented English with a larger model reads better.
- Observed 16/10/2026 with the published build from this worktree (`dist\Dictum\Dictum.exe`, scratch APPDATA,
  LOCALAPPDATA and `--config`):
  - `--selftest` with `small.en` and a language other than English printed `language <code> (<Name>): small.en is an English-only model … [FAIL]`;
    with `medium`, a language other than English and `translate: true` it printed `language <code> (<Name>), translating to English [OK]`
    (the code is withheld under the 21/10/2026 neutrality rule; the model file
    was absent in the scratch profile, so that line failed as expected).
  - `--transcribe` of a 6.37 s synthesised English clip on `ggml-small.en.bin` with `translate: true` and again with
    `language: auto`: both loaded (log "loaded … (language en (English), translating to English, greedy)" and "(language
    auto (detected per dictation), greedy)"), each preceded by the guard's WARN line, and both transcribed the clip
    correctly; so `WithTranslate` and `WithLanguageDetection` on an English-only model warn rather than fail. 9.5 s and
    13.2 s per clip on Vulkan here, first use of the runtime in each fresh process (shader compilation, see the polish
    lane), not a figure for the feature.
  - No audio in the target languages was available: translation quality is unmeasured and is the owner's test.
- After the lens round (16/10/2026, same day): build 0 warnings, 0 errors; 743 tests, 725 passed, 18 skipped, 0 failed
  (the orchestrator's own run, not the lane's report). The re-published build's `--selftest` printed `language <code> (<Name>),
  translating to English [OK]` for medium + a language other than English + translate (code withheld under the 21/10/2026
  neutrality rule), and for small.en + that language without translate the `language` line
  failed with the English-only reason and the `cleanup` line read "skipped: the text is not English … whitespace tidied
  only".
- Rebased onto main 79bffb7 on 16/10/2026, after #1 (mouse hotkeys, snippets), #3 (cloud presets, one key per service),
  #5 (site) and #4 (the dictionary-term rule), for the product owner's decision 14 of the same day: merge now, with
  English the default. Git conflicted in `TranscriptCleaner`, `AppShell.Config`, `CliCommands` and this file only. The
  cleaner's constructor takes #4's `validation` and then `plain`; `Create` takes #4's `PersonalCleanup` and then
  `englishText`; the tray app and `CliCommands.Cleaner` (`--transcribe`) pass both, and `--selftest` passes `englishText`
  as before. Text that is not English gets `TranscriptCleaner.Plain`, which returns before any model is asked, so no
  validator rule runs on it, the dictionary-term rule included; English text keeps #3's key routing and #4's checks as
  on main, whose tests for both pass unchanged. Snippets (#1) run in `PersonalText.Finish` after the plain clean-up as
  after any other, so SPEC §23 and the README now name them beside corrections and the profanity filter. The README's
  settings row for `whisper.language` said a change reloads the model; it rebuilds the decoder, as the section above it
  says, and the row now says so.
- A new test pins `--transcribe`'s language rule (`CliCommands.Cleaner`: plain for a language other than English without translation, the model
  and the dictionary-term check with it). Observed failing with `englishText` removed from `CliCommands.Cleaner`; the two
  Models-page tests failed with it removed from `AppShell.Config`; the new test and the plain-text test failed with the
  constructor's `_plain = plain` removed. Full suite on the rebased head (this lane's run): build 0 warnings, 0 errors;
  1,015 tests, 996 passed, 19 skipped, 0 failed.

## Hands-free segments (16/10/2026, PR #8, branch feature/hands-free-segments)

Product owner decisions 3 and 5 (16/10/2026): hands-free types each part as the speaker pauses, and the auto-stop moves
into Settings > General, from 30 s to 60 minutes. SPEC §3 "Hands-free segments" is the amendment; §7, §9, §20 and §22
carry the rest. This section is the build lane's record as folded by the orchestrator, corrected to the code after the
merge lens's fixes (below).

**Constants** (blocks are the 20 ms level-meter blocks `CaptureBuffer` already measured; the segmenter's timings, its
floor and the carried-word count each sit under a `// simplification:` note with a trigger to retire it):

| Constant | Value | What it does |
|---|---|---|
| `PauseBlocks` | 35 (0.7 s) | Quiet that ends a part |
| `MinSpeechBlocks` | 100 (2 s) | Speech a part needs before a pause may end it |
| `LimitBlocks` | 1,500 (30 s) | Part length at which the hard cut happens |
| `LookBackBlocks` | 150 (3 s) | How far back the hard cut looks for its quietest block |
| `RelativeFloor` | 0.1 | Quiet threshold as a fraction of the recent speech level |
| `LevelDecay` | 0.986 per block | The speech level halves in about a second (0.986^50 ≈ 0.49) |
| `SegmentJoin.CarriedWords` | 10 | Closing words carried to the next part |
| `HandsFreeLimit.SettleMs` | 400 ms | The slider writes once it is still this long (the number box has no pause: Enter, focus leaving or the page being left) |
| `StateMachineOptions.DefaultHoldMaxSeconds` | 300 | A held key's auto-stop (not a setting) |

- **Cut rule.** A pause cut lands in the middle of the quiet run, so both parts keep some quiet; the hard cut takes the
  quietest block of the last 3 s, preferring one that was below the floor when it arrived. A block is quiet below the
  larger of `silence_threshold` and 0.1 × the recent speech level (the loudest recent block, decaying). Why: with
  `silence_threshold` alone (0.01) a room noisier than that never shows a pause and every part runs to 30 s.
- **The noise ceiling is about 24 dB, not 20** (merge lens finding 10, recomputed in this lane): the floor decays with
  the level through the pause, so the 35th quiet block is measured against 0.1 × 0.986^34 = 0.0619 of the loudest recent
  block, 20·log10(0.0619) = −24.2 dB. Room noise within about 24 dB of that block defeats a 0.7 s pause and only the
  30 s cut splits. Pinned on the real segmenter: noise at 0.0615 of the speech (−24.2 dB) is cut at the pause, at 0.0625
  (−24.1 dB) it is not (`A_pause_is_found_only_in_noise_more_than_about_24_dB_under_the_speech`).
- **Whisper gets a processor per part.** Whisper.net 1.9.1 takes the prompt when a processor is built (`WithPrompt`,
  read at tag 1.9.1 in Batch 2); the build lane found no way to change it on a built processor (not re-checked in the
  fix lane), so a processor with the dictionary terms and then the carried words is built and freed per part, the
  decoder in use is left alone, and a build that fails transcribes the part without the words. Ceiling, marked in
  `WhisperEngine`: on Vulkan a prompt of a length not seen before can compile shaders on its first run (24–26 s once
  for a longer dictionary prompt, Batch 2 above), so the first parts of a session could stall.
- **Context placement.** Cloud providers get the carried words as a line after the system prompt (`CleanupPrompt
  .WithCarried`); the local model gets them as a system turn after its cached prefix, so the prefix stays evaluated
  (`CleanupSession.Turns`, pinned since the lens). The line the model reads: "The dictation continues text that is
  already typed; use it only as context and do not repeat it: " and the carried words in quotes.
- **Carried words are what was said** (merge lens finding 1, Blocker): the part's text after the clean-up and the
  boundary rule, before corrections, the profanity filter and snippets. Taken from the typed text, a snippet's address
  reached a cloud provider's prompt. A cloud clean-up therefore repeats at most 10 words of the previous part per request:
  same host, same consent, that part's own cleaned dictation (the reply the host returned, or the tidy of the transcript
  sent to it), never text from the user's files. Since the lens on 93e7e72 a clean-up gets them only if it is the one that
  cleaned them (`HandsFreePart.CarriedFrom`): every settings reload builds a new one, so cloud clean-up switched on
  mid-session is sent none of what was said before it was. Whisper, on this PC, still hears them.
- **Validator.** Carried words are prompt text, never dictation: every rule, the dictionary-term rule included,
  measures the reply against the part's own words. A reply that repeats two adjacent carried words (one a content word)
  more often than the part said them is rejected: "...we should move the meeting to" then "thursday" → "Move the
  meeting to Thursday." Since the lens (finding 2) a pair counts as said when the dictation has two adjacent words that
  sound like it word by word (`ValidatorText.SoundsLike`), read without spoken commands and hesitations and both with and
  without the prompt's fillers (`DeterministicCleaner.FillerPhrases`, the one list). Since the lens on 93e7e72 the pairs
  are counted, as the term rule counts a term's forms: the reply may have as many pairs that sound like a carried pair as
  one reading of the dictation has. Ceilings: a fix that changes a word's sound or inserts a word into a carried pair is
  still rejected ("buys" → "buy", "go to shop" → "Go to the shop.": the tidy is typed, polish lost); a reply that swaps a
  pair the part said for the carried pair it sounds like is accepted, since the part's pair pays for it once ("I spoke to
  the team" carried, "we went to town afterwards" → "We went to the team afterwards."; pinned as a ceiling).
- **Esc and the token.** One cancellation token per session, from the second tap through the stop: Esc while recording
  or after the stop drops the parts in flight or queued and keeps what is typed ("Stopped. Text already typed stays.").
  A lost microphone types the rest last, under the same token (pinned since the lens); one failed part does not end the
  session (pinned since the lens). `CaptureFailed`'s hands-free branch could never run and is gone (finding 11).
- **The field.** Settings > General > Hands-free: a number with its unit, a 30 s–60 min slider and quick picks. The
  slider writes once it settles (400 ms); the number box writes only on Enter, when it loses focus or when the page is
  left (finding 6: typing "120" wrote 30 first, and each write reached the state machine on reload; a settle timer on
  the box, the first fix, still wrote 30 at ordinary typing speed, since the box's value does not change while "1" and
  "12" both clamp to 30 s: the lens on 93e7e72, row 1 below). A value from 5 to 29 left in a file is
  raised to 30 at load with a log line and the file is not rewritten; the field writes the new value when next changed.
  `GeneralPage.MouseNote` lost its sentence pointing at `hands_free_max_seconds` in settings.json. The wording is
  PROPOSED, awaiting the product owner.
- **A held key keeps 5 minutes** (orchestrator decision 7a; finding 3): the field limits hands-free only. With it shared,
  a 30 s field cut a 45 s hold short and a 60-minute field let a stuck key record for an hour.
- `AppShell.cs`: one line passes the dictionary terms to the pipeline (`names: () => Personal.Text.Dictionary.Terms`),
  for the boundary rule's capital exception.
- **One sentence-start rule** (finding 9): `SentenceStart` (TextList.cs) serves the corrections, the profanity filter
  and the boundary rule: the start of the text, or after a line break or a mark that ends a sentence, read back past
  spaces, quotes and brackets. Aligned on purpose, each pinned: the corrections and the filter now take an ellipsis as a
  sentence end and pass over curly quotes and closing brackets; the boundary rule passes over opening ones. Not folded
  in: the tidy's own token-by-token capitalisation (`DeterministicCleaner.Capitalise`), a different shape and a pinned
  table of its own; and `CorrectionSuggestions.Edge`, the characters trimmed from a suggested correction's ends, which
  is not a sentence rule and is left for its own follow-up (it lacks …, the sentence-end marks U+0964 and U+0965, « » and [ ]).
- **U+0964 ends a sentence** (orchestrator, 16/10/2026, for scripts whose sentence marks differ from English, from the languages PR): the marks are
  `SentenceStart.IsEndMark`: . ! ? … and U+0964 (the sentence-end mark) and U+0965 (its doubled form). The profanity
  filter's remove mode reads the same marks after a removed word, so a removed word before U+0964 leaves no space before the mark (the languages lens saw
  a space left before U+0964, while "It's shit." gave "It's.") and a word that was a sentence of its own takes
  its sentence-end mark with it. English pins unchanged; aligned with it, an ellipsis after a removed word now behaves like a full
  stop ("It's shit… really." → "It's… really.", was "It's … really."; "Shit… I forgot." → "I forgot.", was "… I forgot.").
- **Fix last dictation holds the last part typed** (finding 8, kept by decision): each part is remembered as it is
  pasted, with the window it went to. The editor shows that part, not the session.

**Ceilings, documented in the code with triggers to retire them**

- A 0.7 s thinking pause mid-sentence ends a part; a speaker who never pauses for 0.7 s gets 30 s parts.
- A name the dictionary does not list is lower-cased when a part carries on a sentence ("…meeting with" then "sarah
  tomorrow.").
- Ten carried words after a long dictionary prompt can pass whisper's prompt budget; whisper.cpp then drops the oldest
  prompt tokens itself (recalled, as under Batch 2 above).
- **Parts are typed into whatever window is in front** (finding 5): a window change mid-session sends the next parts
  to the other app, where with `insert.method = type` a spoken "new line" is the Enter key. The README tells the user to
  stay in the window until the session stops. Holding parts while another window is in front is **Kyle's decision,
  due 17/10/2026**; nothing is held in this change.

**Open question for Kyle (finding 7b): the boundary rule rarely carries a sentence on.** The rule-based tidy ends every
line that ends with a letter or a digit with a full stop (`DeterministicCleaner.TidyLine`), so with it nearly every part
is typed as its own sentence and the next starts with a capital (`Parts_are_typed_in_order_while_recording_goes_on`:
"We met on tuesday.", " And agreed the plan."); the continuation (a small letter after a space) fires only after a part
with no closing mark, which a clean-up model's reply can be. Accepted for now. The options: (1) accept it; (2) hold back
a part's closing full stop until the next part shows whether the sentence went on; (3) lengthen the pause that ends a
part, so parts end at sentence ends more often, at the cost of later feedback.

**Merge lens on 193ca49 (Block), and the fixes**

| # | Severity | Finding | Fix | Pins (each observed failing on the reverted behaviour) |
|---|---|---|---|---|
| 1 | Blocker | Carried words from the finished text: a snippet's text reached a cloud prompt | `HandsFreePart.Spoken`; `Carried` is its tail | P1 through the controller and a cloud-style client; a unit pin |
| 2 | Major | The pair rule rejected filler drops, homophone fixes and dictionary respellings | Sound-alike pairs, read with and without fillers | P2 a–c, plus d ("like" as a word) and the ceiling |
| 3 | Major | `hands_free_max_seconds` also limited a held key | `HoldMaxSeconds` (300); AppRules passes 300 | P3, P3b |
| 4 | Major | Three claims no test held | `CleanupSession.Turns`; pins | carried turn, P5, P6 |
| 5 | Major | Parts follow the window in front | Documented (decision due 17/10/2026) | README sentence pinned |
| 6 | Minor | The number box wrote on every keystroke | Enter, focus and page flush (a settle timer was the first fix and is gone from the box: the lens on 93e7e72, row 1) | P7 (three ways to finish) |
| 7 | Minor | SPEC was cited but not amended | SPEC §3, §7, §9, §20, §22; this section | — |
| 7b | Minor | The rule-based tidy closes nearly every part | README corrected; options above | existing pin (`Parts_are_typed_in_order…`) |
| 8 | Minor | Fix last holds the last part | Kept and documented | existing pin (`Remembered.Last()`) |
| 9 | Minor | Three sentence-boundary checks | `SentenceStart`, with U+0964 and U+0965 (orchestrator addition) | rows per caller, U+0964 rows |
| 10 | Minor | "Within 20 dB" | About 24 dB, recomputed | the 24 dB pin |
| 11 | Minor | A dead `CaptureFailed` branch | Deleted | P5 |

**Mutation checks of the fix lane**, each a `--no-incremental` build and the whole filtered suite on the finished code
(1,091 tests before the U+0964 addition; the sentence-start, U+0964 and carried-word rows re-run on 1,101 after it), each
source restored byte-identical (SHA-256 compared). Failed per mutant:

| Mutant | Failed |
|---|---|
| Carried words from the typed text (finding 1 reverted) | 2: P1 and the unit pin |
| The old exact-pair rule (finding 2 reverted) | 3: P2 a, b, c |
| No sound matching / no filler reading / fillers always dropped | 2 (b, c) / 1 (a) / 1 (d) |
| No pair rule at all | 2: the original rejection pin and the ceiling pin |
| A hold uses `hands_free_max_seconds` (finding 3 reverted) / AppRules passes the field | 3 / 3 |
| Carried words folded into the user turn | 1 |
| A lost microphone in hands-free sent down the drop path (the lens's M10) | 2: P5 (state machine and controller) |
| `break` after a failed part (the lens's M11) | 1: P6 |
| The number box reports each change at once (finding 6 reverted) / no Enter flush / no lost-focus flush | 3 / 2 / 1 |
| The three old sentence checks back (finding 9 reverted) | 17: the 7 aligned rows, the 8 U+0964 rows and the 2 ellipsis rows |
| `SentenceStart` without … / without curly quotes / without brackets | 5 / 3 / 5 |
| Without U+0964 and U+0965 / Remove's lists back to the English literals / both files before U+0964 and U+0965 | 8 / 5 / 10 |
| The level does not decay (`LevelDecay` 1.0) | 2: the 24 dB pin's −24.1 dB row and `A_cut_lands_on_a_quiet_block_when_the_look_back_has_one` |
| The README's stay-in-the-window sentence removed | 1 |
| `CarriedWords` 9 | 1 |

**Verification** (16/10/2026, this machine): Release `--no-incremental` build, 0 warnings, 0 errors. Filtered suite
(`FullyQualifiedName!~Install_copies_models`; that test runs a copied Dictum.exe and is CI's): 1,101 total, 1,082
passed, 19 skipped (the same 19 env-gated tests as before), 0 failed; at 193ca49 it was 1,043, 1,024, 19, 0. Not run in
this lane: any real model or GPU (the merge lens ran its hands-free probes once against real models; their results are
in its report, not repeated here), the desktop- and tray-gated tests, and Dictum.exe itself.

**Rebase onto main a88e492 (16/10/2026)**, after PR #7 (check for updates) and PR #2 (languages) merged. `git rebase`
replayed the ten commits; two stopped on conflicts:

| Commit | File | Hunk | Resolution |
|---|---|---|---|
| 8bd6fc8 (now 20cc540) | `WhisperEngine.cs` | `DecodeOptions` members: #2's `DescribeLanguage`, this branch's `WithCarried` | Both kept; `WithCarried`'s summary now says only the prompt changes |
| 8bd6fc8 | `WhisperEngine.cs` | The private constructor | `(factory, threads, decode, modelPath, loadTime, ILog log)`: #2 removed the language, this branch added the log |
| 8bd6fc8 | `WhisperEngine.cs` | `Load`'s construction | `new WhisperEngine(factory, threads, options, modelPath, TimeSpan.Zero, log)` |
| 8bd6fc8 | `WhisperEngine.cs` | Not flagged by git: the carried-words build still read `_language`, which #2 removed | `Build(_factory, _threads, decode)`; `decode` is `Decode.WithCarried(…)`, whose `with` keeps the language and translate |
| 3983d5d (now 8996650) | `DECISIONS.md` | Both appended a section at the end | #2's "Languages and translation", then this section |
| 3983d5d | `SPEC.md` | #2's new §23 against this branch's two amendment bullets after §22's | The bullets stay in the amendments list; §23 follows them |

Everything else merged by itself and was read afterwards: `TextList.cs` keeps both `WholeWords.IsWordChar` and
`SentenceStart`; the README's settings table and sections carry both sides; `TranscriptCleaner.CleanAsync` returns the
plain text before it looks at a part's carried words, so text that is not English never reaches a model with them. Each
of the ten rebased commits builds (`--no-incremental`, 0 warnings, 0 errors), and the rebased head's test methods are
main's and this branch's together (main renamed one of the old ones, now `Every_network_client_refuses…`).

Two changes after the rebase:

- **Text that is not English keeps its case where parts join.** The plain clean-up leaves such text exactly as whisper
  wrote it (SPEC §23), but the boundary rule re-cased each part's first letter: a capitalised place name after a part with
  no sentence end was typed in lower case, and a small letter after a sentence end was capitalised. `SegmentJoin.Join`
  takes `keepCase` and the pipeline passes the cleaner's `IsPlain`; the separator rules are unchanged. A part
  in a script without case, or a small letter after a part with no sentence end, comes out the same with or without the
  rule (such a script has no case, and carrying on only lowers a capital), so the pins use a capital after no sentence
  end and a small letter after one; the rows in that script document the behaviour.
- **README, Privacy:** the cloud row and "Optional cloud polish" say that in hands-free the system prompt also carries
  up to 10 words of what the last part said (taken before corrections, the profanity filter and snippets). The consent
  dialog's copy is unchanged: it says every dictation's text is sent.

| Mutant (whole filtered suite, `--no-incremental`; each source restored byte-identical) | Failed |
|---|---|
| The boundary rule re-cases regardless of `keepCase` | 2: the `SegmentJoin` pin and the session pin |
| The pipeline passes `keepCase: false` | 1: the session pin |
| `WithCarried` builds `new DecodeOptions(prompt, BeamSize)` (language and translate dropped) | 1: `A_parts_whisper_prompt_adds_the_carried_words_and_keeps_the_language_and_translate` |
| The README row without its hands-free clause | 1: `The_README_privacy_table_says_a_hands_free_cloud_request_carries_the_last_parts_words` |

Filtered suite: the rebased head 1,198 total, 1,179 passed, 19 skipped, 0 failed; with the two changes 1,202, 1,183,
19, 0 (main a88e492 alone, the same filter: 1,077, 1,058, 19, 0).

**Lens on 93e7e72 (Needs changes), and the fixes** (the lens 16/10/2026, the fixes 16 and 17/10/2026). No blocker and no
privacy regression. Three minors, the nits, and the consent tightening the lens suggested; the code follows the
replacements the lens had already trialled.

| # | Severity | Finding | Fix | Pins (each observed failing on the reverted behaviour) |
|---|---|---|---|---|
| 1 | Minor | The number box's settle timer counted from the first key: `ValueChanged` does not fire while the clamped value stays the same ("1" and "12" both clamp to 30 s), so 1, 2, 0 at 250 ms a key wrote 30 at about 500 ms, then 120 | The box never arms the timer; a `_typed` flag holds its value until Enter, focus leaving the box or the page being left (`Settling` is the timer or the flag; `Flush` and `Choose` clear it); the slider keeps the timer | `Typing_at_ordinary_speed_never_writes_the_digits_on_the_way_under_the_real_timer` (the real `DispatcherTimer`, `Task.Delay(250)` between keys, the file never holds 30 and ends at 120 after Enter) |
| 2 | Minor | A said pair that only sounds like a carried pair paid for it even when the reply also kept the said pair: "so we had the meeting" carried, "it came to nothing in the end" → "The meeting came to nothing in the end." (also "The meeting. It came to nothing in the end."; "I spoke to the team", "we went to town afterwards" → "The team went to town afterwards.") was accepted | Counted as the term rule's `Allowance` counts: for each carried pair in the reply, one reading of the dictation (with the fillers, or without) must hold at least as many pairs sounding like it as the reply does | `A_reply_that_adds_a_carried_pair_beside_a_said_pair_that_only_sounds_like_it_is_rejected` (three rows); a pair said twice and kept twice is accepted (a row of `A_part_that_says_the_carried_words_again_is_accepted`) |
| 3a | Minor | The settle timer's own write was pinned by nothing: every slider pin ended by leaving the page, which flushes | Pin only | `The_slider_timer_writes_the_value_by_itself_with_the_page_still_open` |
| 3b | Minor | Which auto-stop line is logged (the limit that fired) was pinned by nothing | Pin only | `A_hands_free_auto_stop_logs_hands_free_max_seconds_never_the_hold_limit`, `A_held_keys_auto_stop_logs_the_hold_limit_never_hands_free_max_seconds` (`HoldMaxSeconds` 1 against `HandsFreeMaxSeconds` 2) |
| 4 | Nit | `ValidationContext.Carried` said "typed before it"; a code comment and a test comment named the product owner | "What the part before it said, before corrections, the profanity filter and snippets"; "the product owner's decision" | — |
| 4b | Minor (the lens's optional tightening) | Cloud clean-up switched on mid-session: its first request carried up to 10 words that were said, and cleaned on this PC, before it was switched on | `HandsFreePart.CarriedFrom` and `CleanedBy`: the pipeline gives the clean-up the carried words only if it is the clean-up that cleaned them; every settings reload builds a new one | `A_clean_up_built_since_the_last_part_gets_none_of_its_words` (through the controller: the new clean-up gets none, whisper still hears them, the part after carries on as usual) |

Cost of 4b: the first part after any settings reload mid-session is cleaned without the words of the part before it (that
part's context only: the boundary rule reads the typed text, not the carried words). The boundary rule, whisper and the
typed text are unchanged.

Ceiling added, in the code under `RepeatsCarried`'s `// simplification:` note with its trigger and pinned as a ceiling
(`A_reply_that_swaps_a_said_pair_for_the_carried_one_it_sounds_like_is_still_accepted`, a pin that passes before and
after, so not a mutation pin): a reply that swaps a pair the part said for the carried pair it sounds like is accepted,
since the part's pair pays for it once.

| Mutant (whole filtered suite, `--no-incremental`; each source restored byte-identical, SHA-256 of the seven files compared) | Failed |
|---|---|
| The number box arms the settle timer again (finding 1 reverted) | 1: the real-timer pin, which saw `[300, 300, 30, 30, 120]` where `[300, 300, 300, 300, 300]` is expected |
| The number box holds nothing: no timer, no `_typed` flag | 5: the three rows of `Typing_a_value_writes_it_once_never_the_digits_on_the_way`, the clamp test and the real-timer pin |
| The old uncounted carried-pair check (finding 2 reverted) | 3: the three new rows |
| The slider's settle timer callback emptied | 1 |
| The two auto-stop log lines swapped | 2 |
| The clean-up gets the carried words whichever clean-up cleaned them | 1 (see below) |
| The clean-up gets them only if it cleaned the current part, so never | 3: the pin and two existing context pins |

The first run of the "whichever clean-up" mutant also failed `OfflineAndDownloadTests.A_slow_but_live_connection_is_not_a_stall`
(2 download attempts where 1 was expected; its reads are 40 ms apart against a 150 ms stall limit). The test is timing-bound
and untouched here; that run took 49 s against about 30 s for the others, so machine load is the likely cause (inferred, not
measured). The re-run failed only the pin.

**Verification** (16 and 17/10/2026, this machine): Release `--no-incremental` build, 0 warnings, 0 errors. Filtered suite
(`FullyQualifiedName!~Install_copies_models`; CI runs that test): 93e7e72 1,202 total, 1,183 passed, 19 skipped, 0 failed;
with these changes 1,212, 1,193, 19, 0 (ten more test cases). Not run in this lane: any real model or GPU, Dictum.exe, and the
desktop- and tray-gated tests (the 19 skipped). No on-screen string changed.

## Clean-up validator: dictionary terms the speaker did not say (16/10/2026)

- **What happened** (observed 16/10/2026): with the dictionary {Dictum, Kynni, Kynni Pty Ltd}, qwen2.5-1.5b turned "Add a name to the dictionary once. … Dictum, by Kynni." into "Add Kynni Pty Ltd to the dictionary once. … Dictum, by Kynni Pty Ltd.". Every validator rule accepted it and it was typed. `--bench-cleanup u3.txt` reproduces it on every run (greedy decoding). With {Dictum, Kynni} it writes "Add Kynni to the dictionary once.", with grammar assist on or off. Nothing checked the words a reply adds.
- **The rule** (amended after the lenses on ad38c63 and fc177ba): a reply is rejected when two things are both true: it has a term of the prompt's dictionary line, or a content word (3+ letters, not a function word) of a multi-word term, more often than the whole dictation, retracted clauses included; and it has that term in some form more often than the dictation does. Both counts read the reply and the dictation the same way. The log never names the term. A rejection falls back to the rule-based tidy, then the corrections list.
- **Spelling and case:** a term with a capital letter is counted as spelt in dictionary.txt, capitals included, with "'s" removed, on both sides (the dictation's capitals are whisper's). A term with no capital letter matches any case. Counted in any case, everyday words counted as terms: on the lens bench on ad38c63 all three models' "She needs a new loan…" was rejected for {Loan Market}, and "I will send…" for {Will}; probes showed the same for IT, US, May, The Hub, bank and finance. Counting the dictation in any case while the reply was exact let each lower-case "will", "bill", "it" or "may" let one capitalised term through ("ask him if he will call me" → "Ask Will if he will call me." was accepted on fc177ba).
- **"In some form" (the allowance)**, counted the same way on both sides: a word with the same sound key, or with it once a final "s" is dropped (words of 4+ letters: kinneys, mcdonalds, loans); a run of words whose letters equal the term's letters exactly, joined either with a spoken "dot", "at", "dash" or "hyphen" left out or with every word kept (pay pal, git hub, email and e dash mail for e-mail, dictum dot kynni dot com dot au, door dash for DoorDash; exact letters, not sounds, so "can i" never spells Kynni); for a part of a multi-word term, the larger of its own allowance and the whole term's. With the reply counted by sound alone, a kept plural or split word paid for an added term ("pay the bills today please" → "Bill, pay the bills today please." was accepted on fc177ba).
- **Spoken commands and hesitations** ("comma", "question mark", "open quote", "close quote", "new line", "new paragraph", "full stop", "scratch that", um, uh, er, ah, hmm) are left out of the allowance on both sides: "comma" shares Kynni's sound key and paid for an inserted Kynni; "mark" for Mark, "quote" for Kate, "line" for Lena, "um" for Owen. Leaving them out of the dictation only rejected faithful replies that keep the word ("put a comma after kynni" → "Put a comma after Kynni.").
- **Command-word terms (fix lane 3, a600aeb):** Spoken commands and hesitations are left out of both term counts on both sides, in any case. Whisper writes some in capitals ("Question Mark"), and that "Mark" paid for an added {Mark}; the same held for Full Stop/{Stop}, New Line/{Line}, New Paragraph/{Paragraph} and Open Quote/{Quote} (lens on 8e5a28c, 16/10/2026). A term that is or contains a command or hesitation ("ER", "ERM", "UM", "Comma", "New Line Cinema") is counted with them kept on both sides. Before, adding one was never caught: {ER} "the patient was seen this morning and sent home" → "The patient was seen in the ER this morning and sent home." was accepted. Two documented costs, pinned: a command or hesitation the speaker dictated pays for a term spelt like it ({ER} "er the patient was seen this morning" → "…in the ER…" accepted; retire when a typed reply is observed turning a dictated command or hesitation into a term); a word whisper capitalised right after "question", "full", "new", "open" or "close" with no punctuation between is read as the command ({Mark} "I have a question Mark can you help" → "I have a question, Mark, can you help?" is rejected, accepted with whisper's commas; retire when a wanted reply is observed rejected that way).
- **Near miss = sound key** (`ValidatorText.SoundsLike`, a `// simplification:` with its ceiling): letters map to classes (vowels and y; b f p v; c g k q; s x z; d t; m n; j; l; r), h and w dropped, accents removed, runs merged; an "x" after the first letter is k then s; digits match only themselves; text is read in NFC. Levenshtein needs a threshold of 4 to admit kimmy → Kynni (name is 5) and then admits funny, money, Kelly, kidney, Kim.
- **Scope:** only `PersonalDictionary.CleanupTerms` (the newest terms that fit the 1,000-character prompt line) are checked; the model sees no others.
- **Accepted ceilings:** a term inserted in other capitals than its own is not caught (both insertions observed on 16/10/2026 copied the dictionary spelling); an everyday word the reply adds at a sentence start counts when it spells a capitalised term, so "will you come tomorrow i see you then" → "Will you come tomorrow? I will see you then." and "may be late i come tomorrow" → "May be late. I may come tomorrow." are rejected unless whisper wrote the capitals (polish only); a mishearing that splits the word ("kin knee") or adds a sound ("kidney") is rejected and corrections.txt fixes it after the tidy; sound-key collisions against the 49,823 stems of LibreOffice's en_AU.dic (16/10/2026): Kynni 73 (come, game, gonna, canny, Kenny, Connie…), Xero 37 (zero, sorry, Sarah…), Priya 29 (free, pray…), Dictum 3 (diction, tighten, touchdown), Parramatta 0, so "email gemma today" → "Email Kynni today." is accepted; forms are counted per side rather than paired, so real words whose letters spell a term ({PowerPoint} "the power point in the kitchen is broken") and an insertion paid for by a rewritten sound-alike ("i'm gonna add a name…" → "I'm going to add Kynni…"; {Xero} "put zero on the invoice" → "Put 0 on the Xero invoice") pass; retire when a typed reply is observed adding a term either way. Still rejected: number words written as digits ({Windows 11}), abbreviations ("pty limited" → "Pty Ltd"), a singular written as a plural ("team call" → "Teams call"), an added sound ("zoo call" → "Zoom call").
- **Prompt line unchanged.** "Known names and terms, keep their spelling:" stays: a reworded candidate benched worse with all three downloaded models on 14 inputs (126 replies per wording: faithful acceptances 50 against 33; the candidate accepted "The name is John." for "Add a name to the dictionary once." twice).
- **Performance:** 200 terms with a 176-word dictation cost 1.4–2.1 ms per validate (a600aeb, Release, after warm-up) after the lazy whole-term count.
- **Wiring:** `PersonalText.ForCleanup` → `PersonalCleanup` → `TranscriptCleaner.Create` for the local, openai-compatible and anthropic branches; call sites AppShell.Config.cs, CliCommands.cs (--transcribe) and BenchCleanupCommand.cs; the constructor forces the validation context's prompt to the prompt it sends.
- **Pins:** `DictionaryInsertionValidatorTests` (121 cases at the merge, a600aeb). Every rejection pin was observed failing with the rule removed; every acceptance pin on its own mutant; the wiring pins on the terms dropped at each site.
- **Known, not fixed here:** `ValidatorText.HasNonLatinLetters` treats Vietnamese letters (U+1E00–U+1EFF) as another script, so a term "Nguyễn" makes every faithful reply fail with "reply is in a script the dictation did not use". A reply that drops a sentence of 3 or fewer content words ("And open source.") is not caught by DroppedContent (floors: 8 overall, 4 per sentence); lowering the floor needs sound-alikes counted as kept first, then a measurement on the calibration clips.

## Cloud clean-up stays an advanced option, with presets (16/10/2026)

Kyle's direction of 16/10/2026: advanced users may point clean-up at cloud APIs, behind strong warnings that the text leaves the PC and that Dictum does not validate or certify the service. Kyle approved every string below on 16/10/2026 as written (decisions 7, 8, 12 and 13); each is inserted verbatim and never reworded by a lane.

- **Consent dialog** (`CloudPolicy.ConsentWarning`, its own paragraph before "Nothing is sent until you agree.", for anthropic and openai-compatible hosts alike): "WARNING: this is an advanced option and it is less private than local clean-up. Your text leaves this PC to be processed on {host}'s servers. Dictum encrypts the connection (HTTPS) but cannot see, validate or certify how that service stores, protects or uses your text, or who else can read it. Do not dictate confidential, personal or client information with cloud clean-up on unless you have checked that service's terms yourself."
- **State line** (`CloudPolicy.StateLineWarning`, while cloud clean-up is on for a host off this PC): "Advanced option: your text leaves this PC. Dictum does not validate or certify how that service handles it."
- **Privacy policy** (end of the Anthropic and OpenAI-compatible paragraphs; "Last updated" 16/10/2026): "This is an advanced option: Dictum does not validate or certify how that service stores, protects or uses the text it receives. Check its terms before dictating anything sensitive." The OpenAI-compatible paragraph names api.openai.com and generativelanguage.googleapis.com and says a local Ollama can forward to Ollama's cloud once signed in.
- **Store description**, at the next submission: after "or to your own server" add ", an advanced option; that service's own terms apply". Done in the 1.1.0 listing (docs/STORE.md, 17/10/2026).
- **Local server and key lines:** tray "Clean-up via a server on this PC" and "Cloud clean-up on · text leaves this PC"; Settings "On: a server on this PC ({host}, model {model}) tidies each dictation. Dictum cannot see whether that server forwards your text elsewhere."; "On, but waiting for your {KEY}: nothing is sent until you add it to .env." (OPENAI_API_KEY, GEMINI_API_KEY or ANTHROPIC_API_KEY); "On, but offline only is on: nothing is sent until you switch offline only off."; "On, but nothing is sent: the clean-up in use runs on this PC."
- **No "MCP" option:** MCP is a tool protocol, not a text-completion endpoint.
- **Presets** (`src/Dictum.Core/Llm/CloudPresets.cs`, read on 16/10/2026): OpenAI `https://api.openai.com/v1`, default model `gpt-5.4-nano` (developers.openai.com: chat reference, the gpt-5.4-nano model page, the GPT-5.4 guide, deprecations); Google Gemini `https://generativelanguage.googleapis.com/v1beta/openai/`, `gemini-3.8-flash` (ai.google.dev/gemini-api/docs/openai, page updated 14/10/2026; deprecations: stable, no shutdown date); Ollama on this PC `http://localhost:11434/v1/`, no key (docs.ollama.com/api/openai-compatibility); Custom server, typed by the user. A preset fills only provider `openai-compatible`, `llm.base_url`, the model hint (used only when the address still equals the preset's) and the `.env` key name; it adds no settings key and records no consent; switching on still goes through `AppShell.EnableCloud`.
- **Perplexity left out:** its only Chat Completions endpoint, the Orchestrator API at `https://api.perplexity.ai/router/v1`, was in private preview on 16/10/2026; the Agent API uses the Responses format; Sonar Chat Completions support ended on 27/09/2026. Adding it later is one table row.
- **Keys:** one variable per service (`OPENAI_API_KEY`, `GEMINI_API_KEY`, `ANTHROPIC_API_KEY`; `LLM_API_KEY` only for a custom address; none for Ollama), chosen by `CloudPresets.ForAddress` on host and port. The first version sent one `LLM_API_KEY` to every host and sent dictations with no key at all when it was missing; both were caught by the lens before merge. `KeyRequired` presets fail closed.
- **Anthropic at plain `http://` on this PC is now refused** (so the HTTPS sentence in the warning is always true); an existing settings.json with that address falls back to the rule-based tidy when the key is set.
- **OpenAI ceiling:** `gpt-5.4-nano` shuts down on 01/04/2027 (developers.openai.com deprecations, read 16/10/2026); OpenAI's current GPT-6 models need `reasoning_effort: "none"` or no `temperature`, and the client sends `temperature: 0`. Before that date the client needs the change and saved settings need a migration (an `EffectiveSettings` mapping with a log line, or a notice).
- **Gemini:** Google advises temperature 1.0 for Gemini 3 models; the client sends 0, and the validator still checks every reply.
- **Not done:** watching the `.env` beside Dictum.exe (a key added there takes effect at the next reload; until then both pages truthfully say they are waiting). `ModelCoordinator.CurrentCleanupName` still names the configured provider, so "In use: …" on the Models page, the flyout "· cloud" suffix and the tooltip may name a cloud provider while the tidy runs.
- **Pins:** `CloudWarningAndPresetsTests` (65 tests at the merge), each observed failing on the reverted behaviour across the lanes' mutation checks (consent text, state lines, presets writing nothing, HasConsent, offline refusals, key routing, fail-closed, host naming on both site pages with HTML comments stripped, the local-server and waiting lines read from the cleaner in use).

## Check for updates (decision 11, 16/10/2026, PR #7)

Design: one GET of https://dictum.kynni.au/version.json through its own client, built like the model-download client (offline_only guard, TLS 1.2) but following no redirect (`AppRules.cs:33`, `CreateDownloadHandler(followRedirects: false)`; loopback bypass); no query string, no header of Dictum's own (a loopback listener saw only the request line and Host); no retry; 10 s timeout (chosen, not measured); at most 16 KiB read (version.json was 76 bytes on 16/10/2026); numeric compare only (a +sha or -suffix ignored; 1.0.10 is newer than 1.0.9; a missing part is 0); "Open the download page" opens the fixed releases/latest address, never an address from the file; offline_only refuses before anything is sent and the guard also refuses if it comes on as the request leaves; the packaged build has no button and the core refuses packaged builds; a second press while a check runs is ignored. The live file was checked with curl on 16/10/2026 (HTTP 200, 76 bytes, Cache-Control max-age=300, TLS 1.2 fine). Strings (proposed 16/10/2026): "Check for updates"; "Checking reads one small file from dictum.kynni.au and sends nothing about you."; "You have the latest version ({version})."; "{version} is available."; "Open the download page"; "Offline only is on, so Dictum did not check."; "Could not check right now. Try again later."; "Updates come from the Microsoft Store." Site: privacy page paragraph and landing page card "Only when you open the privacy policy from Settings, or click Check for updates in the direct-download version, which reads one small file and sends nothing about you; the Store build is updated by the Store."; privacy short-version bullet names the check. Pins: UpdateCheckTests (41 tests); 35 mutants, every one observed failing on the reverted behaviour. Suite at 248fa63: 902 total, 883 passed, 19 skipped, 0 failed (CI). (All of this is from the build lane's report of 16/10/2026; the suite figures were not re-run for this record.)

- **The update check follows no redirect** (`UpdateCheck.cs`: an HTTP error or redirect is a failure), so https://dictum.kynni.au/version.json must answer 200 directly. Verified 17/10/2026 10:55 AEDT with `curl -sI`: HTTP 200, no `Location`, `Cache-Control: public, max-age=300, must-revalidate`; the body is `{"version":"1.1.0",...,"published":"2026-10-17"}`. `site/vercel.json` has `cleanUrls: true` and one headers-only rule for `/version.json` (the Cache-Control above); never add a redirect or rewrite for that path. The app reads `version` and `notes` (shown under the newer line) and ignores `published`; `version` must be bare digits (STORE.md §10, step 7).
- **Not done:** Settings > Privacy's "What leaves this PC" card still names model downloads only (needs an approved sentence); `.github/SECURITY.md` was reported on 16/10/2026 as still saying version 0.1.0 with no release (not re-checked for this record).

## Visual C++ runtime (Store certification fix, 17/10/2026, branch fix/vc-runtime)

**The finding** (investigation lane, 16/10/2026; evidence in that session's scratchpad: `sbx\vcrt-check.wsb`,
`sbx\in\run.ps1`, `sbx\out\*`, `pe-imports.ps1`, `vcrt-exports-check.ps1`, `logs\vclibs-*`). The first Store submission
failed certification; the tester's screenshot showed "model error" and "error". Every whisper.cpp and llama.cpp DLL
imports `MSVCP140.dll`, `VCRUNTIME140.dll` and `VCRUNTIME140_1.dll`, and the ggml CPU builds also `VCOMP140.DLL`; the
.NET runtime and Avalonia's native libraries import none of them. The Store package carried none and declared only
`TargetDeviceFamily`. In Windows Sandbox 10.0.26100 (no VC++ DLLs in System32, no redistributable registry key, no
VCLibs packages) `--selftest` printed `whisper runtime … model load failed: Exception: Failed to load native whisper
library …` and `cleanup backend … load failed: TypeInitializationException: The type initializer for
'LLama.Native.NativeApi' threw an exception.` while the UI ran. With the four VC++ 14.40.33728 DLLs beside each native
runtime folder both engines loaded and `--transcribe` transcribed a 10.74 s clip. Whisper.net also found a runtime DLL
beside Dictum.exe; LLamaSharp did not.

Measured again on this branch (17/10/2026): the published layout has 38 native DLLs in 8 folders under `runtimes\`; all
38 import the VC++ runtime and 10 of them `vcomp140.dll`. Their PE headers name MSVC linker 14.44 (Whisper.net.Runtime
and .Vulkan 1.9.1, LLamaSharp.Backend.Cpu and .Vulkan.Windows 0.27.0) and 14.51 (Whisper.net.Runtime.NoAvx 1.9.1).
The investigation's export check, re-run against the 14.40.33728 framework DLLs: 1,977 imported VC++ functions across
the five packages, 0 missing.

**Sources** (Microsoft Learn, read 17/10/2026, unless marked):
- "C++ Runtime framework packages for Desktop Bridge" (KB 3176696): desktop apps that depend on the C++ runtime "must
  specify the corresponding version of the C++ Runtime framework package" instead of redistributing the runtime, with the
  `PackageDependency` for `Microsoft.VCLibs.140.00.UWPDesktop` and Publisher
  `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US`.
- "MSIX troubleshooting guide", Missing dependencies: through the Store "framework dependencies are automatically
  downloaded and installed"; a sideload installs https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx first.
- "Basic troubleshooting for Windows App" lists `Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0` as a current framework
  file. The build PC has 14.0.33728.0 (x64 and x86) from the Store, Publisher as above, its DLLs 14.40.33728.0 signed
  Microsoft Corporation (`Get-AppxPackage`, `Get-AuthenticodeSignature`, 17/10/2026). MinVersion is that version, the one
  tested.
- "Dynamic-link library search order": a packaged process searches its package dependency graph for a DLL's
  dependencies "even if the first DLL was loaded by specifying a full path".
- "Redistribute Visual C++ files" and "Distributable Code for Visual Studio 2022": distribution of the redistributable
  package, merge modules and individual binaries "is limited to licensed Visual Studio users"; the files come from
  `[VisualStudioFolder]\VC\redist`; app-local copies are not recommended for servicing reasons; the redistributable's
  registry key and its `/install /quiet /norestart` switches; run unelevated, it asks for UAC.
- "Microsoft Visual C++ Redistributable latest supported downloads": https://aka.ms/vc14/vc_redist.x64.exe;
  "Redistribution is permitted only for licensed Visual Studio users"; the installed redistributable "must be the same
  or later than the version of the MSVC Build Tools used to create your application".
- Whisper.net 1.9.1's readme (in its NuGet package): "Microsoft Visual C++ Redistributable for at least Visual Studio
  2022 (x64)" and "Windows 11 or Windows Server 2022 (or newer) is required".

**Fix, Store package.** `packaging/AppxManifest.template.xml` declares
`<PackageDependency Name="Microsoft.VCLibs.140.00.UWPDesktop" MinVersion="14.0.33728.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />`.
`package-msix.ps1` leaves any app-local VC++ DLL out of the layout (observed: "Left out 32 app-local Visual C++ runtime
DLLs" when packaging a `-VcRuntimeDir` publish) and ends by naming the dependency. Rebuilt on 17/10/2026 from this
branch with `package-msix.ps1 -Store -NoPublish` over a plain publish: `dist\Dictum-1.0.0-store.msix`, 105,953,677
bytes, unsigned, 350 files; rebuilt again with the bundled model (below), 239,909,521 bytes, 352 files. In both, the
`AppxManifest.xml` read back out of the .msix carries the element, and the .msix holds no VC++ DLL.

**Fix, direct download: the route taken, and why.**
- No licensed app-local source is on the build PC: no Visual Studio or Build Tools (no `Microsoft Visual Studio` folder
  under either Program Files, no vswhere), no Windows SDK, no `Microsoft.VC14*.CRT` folder under `C:\Program Files*`,
  `C:\ProgramData`, `D:\`, `G:\` or `E:\` (the private data folder excluded), and no VCLibs `.appx` in the NuGet cache or
  anywhere else searched. The only copies are the installed redistributable in System32 (14.51.36247) and the installed
  framework package (14.40.33728); neither is a documented source to redistribute from.
- Microsoft limits redistribution of the runtime in every form, the vc_redist package included, to licensed Visual
  Studio users, so bundling vc_redist.x64.exe into Setup needs the same licence as app-local DLLs. Setup is also per user
  (`PrivilegesRequired=lowest`): it never runs elevated, so it could not run vc_redist silently.
- So this branch redistributes nothing. `publish.ps1 -VcRuntimeDir <Visual Studio or Build Tools VC\Redist\MSVC\<version>\x64>`
  copies the four DLLs beside each of the 8 native runtime folders and refuses a DLL that is not Microsoft-signed or is
  older than 14.51 (`$MinVcRuntime` in `scripts/build-common.ps1`: the newest linker above); without it, publish warns.
  A Setup built from such a layout carries the runtime. Any other Setup reads
  `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64` (Installed, Major, Minor) in `InitializeSetup`, before it
  installs anything, and asks: Yes opens https://aka.ms/vc14/vc_redist.x64.exe and carries on, No carries on, Cancel stops
  Setup; its Launch Dictum box waits for the key. (Amended 17/10/2026, "Lens fixes" below: the first version read the key
  after the files were copied and launched Dictum anyway.) README: Install (both direct-download routes), Build from
  source, Troubleshooting and staff deployment.
- **For Kyle to decide:** whether Kynni installs Visual Studio or Build Tools whose redistributable is 14.51 or later on
  the build PC, under a licence it holds, and builds releases with `-VcRuntimeDir`. Staff without administrator rights
  need that, since the redistributable needs an administrator. The first such release must carry whatever notice the
  Visual Studio licence asks for (the licence text was not read here), which is why `THIRD-PARTY-NOTICES.txt` carries no
  notice for Microsoft's runtime DLLs: none is redistributed yet. CI does not pass `-VcRuntimeDir`; whether Kynni may redistribute DLLs from GitHub's
  runner image is a licence question this branch does not settle.

**Test.** `tests/Dictum.Tests/VcRuntimeTests.cs`: a C# port of `pe-imports.ps1` (import and delay-import tables,
machine, linker version) and 7 tests, one a theory of 3. Every whisper and llama engine imports the runtime and nothing
beyond `$VcRuntimeDlls`; the template declares the tested framework from Microsoft and the layout carries no copy; a
manifest without the element, with an older MinVersion or with another publisher is refused; a Store layout with its
own `vcruntime140.dll` is refused; a direct-download layout needs the DLLs beside every DLL that imports them (one
missing, named); `$MinVcRuntime` is no older than the newest linker; and, gated on `DICTUM_PUBLISHED_DIR`, a real layout
(the Store check when it holds an `AppxManifest.xml`, else app-local). Observed failing on the reverted behaviour, each
then restored:
- the template's PackageDependency line deleted: 4 of the 9 failed (the Store test, "Collection was not empty", and the
  three theory cases);
- `$MinVcRuntime` set to 14.44: 1 failed, "accepts a Visual C++ runtime from 14.44, but a native DLL was linked by MSVC
  14.51";
- `vcomp140.dll` dropped from `$VcRuntimeDlls`: 2 failed;
- one shipped DLL removed from a `-VcRuntimeDir` publish (`runtimes\win-x64\native\avx2\vcomp140.dll`): the gated test
  failed, naming `runtimes\win-x64\native\avx2\ggml-cpu.dll imports vcomp140.dll`;
- the plain publish fails the gated test too, by design; the Store layout passes it, also when packaged from a
  `-VcRuntimeDir` publish.

**Clean-PC check** (`scripts/sandbox-check.ps1`, `scripts/sandbox-check.wsb`, `scripts/sandbox-check-inside.ps1`;
Windows Sandbox 10.0.26100 with no VC++ DLLs in System32, no redistributable key and no VCLibs packages; ggml-base.en and
qwen2.5-0.5b mapped read-only; 17/10/2026 from 00:32 AEDT; microphone lines fail in every run, as there is no audio, and
do not count):
- (these layouts were built before the base.en model was bundled, below; the model file does not change which DLLs
  load)
- the rebuilt zip layout (`publish.ps1` without `-VcRuntimeDir`), gpu auto and cpu: `whisper runtime … model load
  failed: Exception: Failed to load native whisper library … [FAIL]` and `cleanup backend … load failed:
  TypeInitializationException … [FAIL]`. The README's redistributable step is required for it.
- a `-VcRuntimeDir` layout built only for this check from the build PC's System32 copies (14.51.36247; never shipped,
  kept in the session scratchpad): auto `whisper runtime … Vulkan (whisper.gpu = auto; loaded in 4.1 s) [OK]` and
  `cleanup backend … Vulkan (llm.gpu = auto; loaded in 4.4 s; …native/vulkan/llama.dll) [OK]`; cpu `CPU (whisper.gpu =
  cpu; loaded in 0.7 s) [OK]` and `CPU (llm.gpu = cpu; loaded in 3.5 s; …native/avx2/llama.dll) [OK]`.
- the Store layout: Windows refused it without the framework, `0x80073CF3`, "Windows cannot install package
  Kynni.Dictum_1.0.0.0_x64__43s584kx66f64 because this package depends on a framework that could not be found. Provide the
  framework "Microsoft.VCLibs.140.00.UWPDesktop" published by "CN=Microsoft Corporation, O=Microsoft Corporation,
  L=Redmond, S=Washington, C=US", with neutral or x64 processor architecture and minimum version 14.0.33728.0": the
  declared dependency is read and enforced as written. Registering the framework from a copy of the build PC's installed
  folder failed both ways (Developer Mode: "DevelopmentMode is not allowed when registering framework or resource
  packages"; without it: "the manifest is not in the package root"), so the packaged self-test did not run. It needs
  Microsoft's signed `.appx`, which is not on the build PC; docs/TESTING-MSIX.md has the command.

**Risks, and what is not verified.**
- The packaged app loading both engines from the framework: inferred, not observed. Premises: the manifest declares the
  framework (read back from the .msix, and enforced by Windows above); a packaged process searches its dependency graph
  for the dependencies of a DLL loaded by full path ("Dynamic-link library search order"); the framework's 14.40.33728
  DLLs export every import and loaded both engines' Vulkan builds (investigation).
- The framework's runtime (14.40.33728) is older than the build tools of the native DLLs (14.44 and 14.51), and
  Microsoft's rule is a runtime at least as recent as the build tools. No newer UWPDesktop framework was seen: 14.0.33728.0
  is what Learn's Windows App page names and what the build PC's Store-updated install has. Every import is exported by
  14.40; the CPU and no-AVX builds were not exercised on 14.40.
- Setup's runtime check compiled (Inno Setup 7.1.0), as first written and again after the 17/10/2026 rewrite; its behaviour
  at install time is not verified here ("Lens fixes", below, says what was and was not run).
- Windows 10 (F6): Whisper.net's readme says Windows 11 or Server 2022, the manifest allows 10.0.17763, and nothing has
  run on Windows 10 (docs/STORE.md, section 4). No change made.
- Partner Center and a package version it has already seen: recalled, not verified, that it refuses one; STORE.md's
  go-live list gives `-Version 1.0.1` as the fallback.

**Also on this branch, F7.** `ModelDownloader.LoginRequired` ends the balloon for a model behind a Hugging Face login
(and the 401/403 error). Before: "requires a Hugging Face login to download; use install.ps1 -ModelsFrom or
download-model.ps1 with a token" (scripts the Store package does not ship). After: "requires a Hugging Face login to
download; download it in your browser, then choose it in Settings > Models". The Models page's buttons are "Choose model
file…" and "Choose .gguf file…", so the text names the page, which every build has. A balloon: it joins the morning
batch. `A_login_wall_points_at_Settings_not_at_scripts_the_Store_package_lacks` pins it (observed failing with the old
text). Not changed in that first version: `OfflineOnlyException` also named `install.ps1 -ModelsFrom` (changed under "Lens
fixes", below).

**Also on this branch: the bundled base.en model (Decision 16, Kyle, 17/10/2026, through the orchestrator).** Every
package layout carries the base.en speech model, so first use and Store testing need no download.
- `scripts/publish.ps1` puts `models\ggml-base.en.bin` and `THIRD-PARTY-NOTICES.txt` beside Dictum.exe in
  `dist\Dictum`, and both are in its required-file list. Every layout takes that folder whole: the zip
  (`Compress-Archive` of the folder), the installer (`[Files]` `{#PublishDir}\*` with `recursesubdirs`), `install.ps1`
  (copies the whole build) and both MSIX flavours (the layout is a copy of `dist\Dictum`). The model comes from
  `dist\model-cache`, else from a copy of `%LOCALAPPDATA%\Dictum\models\ggml-base.en.bin` (read only), else from
  huggingface.co at commit `5359861c739e955e79d9a303bcbc70fb988958b1`. In every case
  `download-model.ps1 -Model base.en -ModelsDir dist\model-cache` checks the size and SHA-256 against its table (the
  catalogue's values, kept in sync by an existing unit test) and throws on a mismatch, which stops the publish and, in
  CI, the job; the tag build packs the installer and the test MSIX from that same `dist\Dictum`.
- Observed 17/10/2026 on the build PC (reused copy): "…\dist\model-cache\ggml-base.en.bin already exists and matches
  (147964211 bytes). Nothing to do."; the copy in `dist\Dictum\models` and the entry inside the .msix both 147,964,211
  bytes, SHA-256 `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002` (`Get-FileHash`; SHA-256 of the zip
  entry). The download path was not run here; CI runs it on every push.
- Sizes, 17/10/2026, before and after: `dist\Dictum` 275 files, 292,416,648 bytes, then 277 files, 440,383,016 bytes
  (each plus manifest.txt); zip 103,208,176 then 236,986,310 bytes; Setup 55,248,402 then 184,530,924 bytes; Store MSIX
  105,953,677 then 239,909,521 bytes (350 then 352 files; SHA-256 of the new one
  `cda44d16b58b3ca30c7f845362146a2dc138615ca5ff96c54376c38912a9a526`).
- Licence, verified 17/10/2026: the Hugging Face API for ggerganov/whisper.cpp gives `license: mit`, at sha
  `5359861c…` (the pinned commit is the current one); openai/whisper's README says "Whisper's code and model weights are
  released under the MIT License", and its LICENSE reads "Copyright (c) 2022 OpenAI"; ggml-org/whisper.cpp's LICENSE
  reads "Copyright (c) 2023-2026 The ggml authors". `THIRD-PARTY-NOTICES.txt` carries both copyright lines and the MIT
  text, and ships beside Dictum.exe. The brief asked for the entry "as the repo's notices already do for other
  components", but there were none: no notices file in the repository, and no licence or notice file in `dist\Dictum`
  for the libraries it bundles (whisper.cpp, llama.cpp, Whisper.net, LLamaSharp, Avalonia and others; their licences are
  recalled as MIT-style, not verified here). Flagged in that first version; fixed under "Lens fixes", below.
- Tests: `tests/Dictum.Tests/BundledModelTests.cs` pins publish.ps1's `$bundledModel` to base.en in the catalogue, and
  the notice to the catalogue's file, commit, size and SHA-256 and to both copyright lines; gated on
  `DICTUM_PUBLISHED_DIR`, a layout must carry the model byte for byte and the notice. Observed failing: the gated test on
  the layout built before this change ("…\models\ggml-base.en.bin is missing"), and the notice test with the OpenAI line
  removed. Both gated layout tests pass on the Store layout; on `dist\Dictum` the model test passes and the VC runtime
  test fails, by design (no `-VcRuntimeDir`).
- Not on this branch: the app reading the bundled file. A separate lane makes the app look in `<exe dir>\models` as a
  read-only fallback; until that merges, every build carries the file and the first run still offers a download. Build
  the Store package after both have merged.

### Lens fixes (17/10/2026)

An opus lens reviewed ae5543c and found one major and several minor problems. This subsection records the rebase and each
fix; the passages above say "amended" where a fix changed them.

**Rebase.** origin/main had moved to 23acf35 (PR #8, 24 commits). `git rebase origin/main` conflicted only in
docs/DECISIONS.md, where both sides had appended at the end: main's hands-free sections are kept and this entry follows
them. Rebased head f3f9ff9c20dca309aa2cf654d42d218889a2e67f, pushed with
`--force-with-lease=fix/vc-runtime:ae5543c558bda14ef60a54eec4c67bba45c308b8`. Release `--no-incremental` build: 0 warnings,
0 errors. Filtered suite (`FullyQualifiedName!~Install_copies_models`): 1,279 total, 1,258 passed, 21 skipped, 0 failed.

**1. The installer asked after it had installed (major).** Before, `CurStepChanged(ssPostInstall)` read the key once the
files were copied, and the Finished page then offered "Launch Dictum" (ticked) whether or not the runtime was there: on a
clean PC a user could click Yes to the download prompt, then Finish, and Dictum started without the runtime and showed
"model error". After, in `packaging/Dictum.iss` under `#ifdef CheckVcRedist` (a layout without the runtime):
- `InitializeSetup` runs the Store and Dictate warnings, then `if not VcRuntimeReady() then Result := OfferVcRuntime();`.
  It runs before the wizard exists and before a file is copied (Inno Setup help: "Called during Setup's initialization.
  Return False to abort Setup, True otherwise.").
- `VcRuntimeReady` is `VcRuntimeAt(HKLM64) or VcRuntimeAt(HKLM32)`. `VcRuntimeAt` reads
  `SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`: `Installed` = 1, and `Major` above the floor, or `Major` equal
  and `Minor` at or above it (the floor, 14.51, reaches the script from `$MinVcRuntime` as `/DMinVcMajor` and `/DMinVcMinor`).
  Read on this PC, 17/10/2026: Installed 1, Major 14, Minor 51, Bld 36247 (so it would pass; the check was not run as Setup).
- `OfferVcRuntime` logs, then asks with Yes, No and Cancel (Yes is the default button): "Dictum's speech recognition and AI
  clean-up need the Microsoft Visual C++ Redistributable (x64), version 14.51 or later. This PC has none, or one older than
  14.51. Installing it needs administrator rights." and one line per button. **Yes** opens
  https://aka.ms/vc14/vc_redist.x64.exe as the original user (a failure to open is logged) and Setup carries on; **No**
  carries on without it; **Cancel** returns False and Setup ends. With `/SUPPRESSMSGBOXES` (which "only has an effect when
  combined with /SILENT or /VERYSILENT"; a silent install without it still shows the box) `SuppressibleMsgBox` returns its
  default, No: Setup carries on and the log line records the missing runtime.
- The `[Run]` launch line carries `Check: VcRuntimeReady` in the `#ifdef CheckVcRedist` branch, and is the plain line in
  the `#else` branch. A missing or too-old runtime therefore shows no "Launch Dictum" checkbox. `VcRuntimeReady` only
  reads the registry, because the help says Setup "might call each check function several times". Not verified: when
  Inno evaluates a postinstall entry's Check (the help says only that these entries are processed after the Setup
  Completed page is shown). If it is before the user could finish installing the runtime during Setup, the box stays
  hidden and Dictum starts from the Start menu, the safe direction.
- Build evidence, Inno Setup 7.1.0 (the version CI pins): `ISCC.exe /DAppVersion=1.0.0 /DNumericVersion=1.0.0.0
  /DPublishDir=<scratch folder of two stand-in files> /DOutputDir=<scratch> packaging\Dictum.iss`, with and without
  `/DCheckVcRedist /DMinVcMajor=14 /DMinVcMinor=51`: "Successful compile", exit 0 both times (2,099,722 and 2,098,972 bytes).
  Negative control: a copy with the launch line's Check misspelt exits 2 with "Required function or procedure
  'VcRuntimeReadyTypo' not found", so the compile does check the Check reference. **Not run:** the installer itself, so the
  dialog, the browser opening, the exit code of a Cancel and the Finished page are not observed.
- `scripts/build-installer.ps1` (comment and message) and the README's Install paragraph now describe this flow. The lens
  named "Microsoft Visual C++ 2015-2022 runtime" for the site; 14.51's own entry in Apps & features on this PC is
  "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247" (registry read 17/10/2026), so the site, the README and
  the installer use "Microsoft Visual C++ Redistributable (x64) 14.51 or later", the name of Microsoft's page.

**2. STORE.md no longer says Fixed (minor).** Section 1's row and section 9, row 22, now read "Fix in package; packaged
self-test pending". Section 10 gains step 4, a packaged self-test before upload (`Dictum.exe --selftest` from the installed
package, on a PC or Sandbox with Microsoft.VCLibs.140.00.UWPDesktop 14.0.33728.0 or later and no Visual C++
Redistributable in System32; `whisper runtime` and `cleanup backend` both `[OK]`; `scripts\sandbox-check.ps1 -Package
... -Framework ... -CleanupModel ...` does it in Windows Sandbox), and the later steps are renumbered. The framework
`.appx`'s licensed source (Visual Studio with the UWP workload,
`%ProgramFiles(x86)%\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs.Desktop\14.0`, per KB 3176696) is the
lens's statement, marked in STORE.md as recalled, not verified: it was not re-read in this lane. The README's sentence
that the Store build "needs nothing" is qualified the same way.

**3. The direct download's requirement, on the site and in the release notes (minor).** `site/index.html`'s "What you
need" list gains "Microsoft Visual C++ Redistributable (x64) 14.51 or later, for the direct download", with the installer's
check and the administrator-rights caveat (the same page says Dictum installs with no admin rights, which stays true of
Dictum itself). The release job builds its notes with `gh release create ... --generate-notes` and has no checkout, so it
now checks out the repository (`persist-credentials: false`, the build job's pinned `actions/checkout`) and passes
`--notes-file packaging/release-notes-preamble.md` ahead of the generated notes. The preamble states the runtime floor,
the installer's check, the zip's lack of one, the test MSIX's framework prerequisite and the bundled base.en model.
Provenance for the flags: `gh release create --help` (gh 2.97.0) documents that extra notes "can be prepended to
automatically generated notes" with `--notes`; the source at tag v2.97.0 (`pkg/cmd/release/create/create.go`, read with the
GitHub API) shows `--notes-file` filling the same body field and `--generate-notes` setting `generate_release_notes`. The
GitHub API's own statement that a body is prepended is recalled, not verified. The preamble assumes releases come from CI,
which does not pass `-VcRuntimeDir`; if releases are ever built with it, rewrite its first paragraph. Its base.en paragraph
says what each file carries, not that first use needs no download, because that also needs the separate app change (item 5).

**4. `OfflineOnlyException` (minor).** Before: "...so Dictum makes no network calls. Copy the file into the models folder
instead (install.ps1 -ModelsFrom)." After: "...so Dictum makes no network calls. Copy the file into the models folder instead
(Settings > Storage shows the folder)." The Storage page draws "Models folder: <path>"
(`src/Dictum.UI/Views/Settings/ModelPages.cs`, `StoragePage`). The text reaches the Store build's balloon
(`ModelDownloads.cs`) and the `--download-model` output (`DownloadCommand.cs`); a balloon, so it joins the morning batch.
`An_offline_refusal_points_at_Settings_not_at_install_ps1_which_the_Store_package_lacks` pins it (observed failing with
the old text; passes with the new).

**5. The sentences that assume the parallel model-folder change** (docs/STORE.md sections 6 and 7, `publish.ps1`'s model
comment, `BundledModelTests.cs`'s summary) are left as they are: the app change that reads `<program folder>\models`
and this PR both merge before any upload.

**6. Pins (18 new tests; each observed failing on its reverted text, rebuilt `--no-incremental` each time, the file
restored byte-identical and checked by SHA-256).**
- `InstallerVcCheckTests` (8): the key `...\Runtimes\x64` and its `Installed`, `Major`, `Minor` values; the comparison; both
  registry views; the one call of `OfferVcRuntime` sits in `InitializeSetup` and `ssPostInstall` is gone; Yes, No and
  Cancel; the wording "has none, or one older than"; the launch line's `Check: VcRuntimeReady` and its plain twin identical
  otherwise; `build-installer.ps1` passes `/DCheckVcRedist` and the floor from `$MinVcRuntime`. Mutations, one test failed
  each: key x64 to x86; `or VcRuntimeAt(HKLM32)` deleted; `Minor >=` flipped to `<=`; the question moved back to
  `CurStepChanged(ssPostInstall)`; `Check:` removed; the plain line's description changed; `/DMinVcMinor=0`; "does not have
  it" restored; `if Answer = IDYES` to `IDNO`; Cancel returning True; `MB_YESNOCANCEL` to `MB_YESNO`.
- `OfflineAndDownloadTests.An_offline_refusal...` (1): described above; 1 of 1 failed on the old text.
- `ReleaseWorkflowTests` (5): every action in ci.yml is pinned to a 40-character commit; the cache step is whole-line
  `path: dist/model-cache` and `key: model-ggml-base.en-<the catalogue's SHA-256>` and comes before the publish step; the
  release job checks out before it downloads and passes `--notes-file packaging/release-notes-preamble.md` with
  `--generate-notes`; the preamble names the floor read from `$MinVcRuntime`, the aka.ms URL, `models\ggml-base.en.bin`
  and `THIRD-PARTY-NOTICES.txt`; README, site and preamble state the same floor. Mutations: key one character off; path
  `dist/model-cachee` (this first passed because the assertion was a substring, so the assertions were made whole-line,
  then it failed); key with a suffix; the cache action pinned to a tag; `--notes-file` removed; the release checkout
  removed; the preamble without the model; the site at 14.50 (1 failed); the preamble at 14.50 (2 failed).
- `ThirdPartyNoticesTests` (4): item 8.
- The workflow YAML was not parsed here (no YAML parser on the build PC); its first run is the proof.

**7. CI downloads the model once (minor).** `.github/workflows/ci.yml` restores `dist/model-cache` with `actions/cache`
before the publish step, keyed
`model-ggml-base.en-a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002` (the pinned SHA-256, kept equal to the
catalogue's by a test). A restored copy is trusted for nothing: `download-model.ps1` hashes it again and, on a mismatch,
downloads to a `.part` file, verifies it and replaces the copy (read in the script). actions/cache v6.1.0 is the latest
release (published 2026-06-26) and its tag points at commit `55cc8345863c7cc4c66a329aec7e433d2d1c52a9` (GitHub API,
17/10/2026), pinned by SHA like the other actions; its `action.yml` at that commit has `path` and `key` inputs and a post
step that saves only if the job has succeeded. Observed in the two CI runs for ad72ef5: "Cache not found for input keys",
then the publish step downloaded the file ("Done: ...ggml-base.en.bin (147964211 bytes, size and SHA-256 verified)"), then
"Cache saved with key: model-ggml-base.en-a03779c8..." in each run (the push run's entry belongs to the branch, the
pull-request run's to the PR). A restore had not been observed when this was written; the next push to the branch should
show one.

**8. THIRD-PARTY-NOTICES.txt now covers every third-party component in the published folder (needed before
resubmission).** The inventory comes from the layout the earlier lane published (`dist\Dictum`, 278 files) and the
restore result (`src\Dictum.Windows\obj\project.assets.json`, 39 packages in the graph). Audit, run with a script over
those two sources: all 278 files have an owner. 14 are Dictum's own (Dictum.exe, Dictum.dll, Dictum.Core.dll,
Dictum.UI.dll, their symbols, deps.json, runtimeconfig.json, the icon, the two data files, manifest.txt and the notices
file itself), 1 is the model, and 263 belong to 30 packages and 2 runtime packs, each of which has an entry (the .NET
runtime pack 187 files, 172 managed and 15 native, per `Dictum.deps.json`; the Windows SDK projection 2 files;
Whisper.net's natives 13 and LLamaSharp's 25 under `runtimes\`, which arrive by build targets and are not in deps.json).
The audit found one package the csproj files do not name: `LLamaSharp.Backend.Vulkan.Windows`, the transitive package
that carries the Vulkan llama.cpp natives (`LLamaSharp.Backend.Vulkan` is a meta-package with no files of its own, kept
as an entry because the csproj names it). Packages in the graph whose natives are for Linux, macOS or the web
(`Whisper.net.Runtime.Metal`, `LLamaSharp.Backend.Vulkan.Linux`, `SkiaSharp.NativeAssets.Linux/macOS/WebAssembly`,
`HarfBuzzSharp.NativeAssets.Linux/WebAssembly`) and the build-time `Avalonia.BuildServices` put no file in the folder and
have no entry. The file was written by a one-off script (not committed) that read the nuspec and licence files below; the
text is kept by hand from now on. Annexes A to C are the packages' own third-party notice files, reproduced with LF line
endings (source SHA-256 in each annex header; the copies in the other packages were hash-compared and are identical). The
file is UTF-8 with a byte-order mark, because the nuspec copyright lines contain the copyright sign and Windows 10 1809's
Notepad needs the mark to read UTF-8. Size 285,154 bytes (was 2,157). The layout becomes 440,694,290 bytes, 420.3 MiB, and
the zip 237,035,759 bytes, 226.1 MiB (a copy of `dist\Dictum` with the new file and a regenerated manifest.txt, zipped
with `Compress-Archive -CompressionLevel Optimal`, as publish.ps1 does; the README's figures are updated to these).

Sources, every file read on 17/10/2026 from the NuGet cache of the build PC (`%USERPROFILE%\.nuget\packages`; each of the
33 package folders' `.nupkg.metadata` names https://api.nuget.org/v3/index.json as its source; the package signatures were
not verified here). Licence and copyright are quoted as found. Beside these, the model section is unchanged, and
`dist\Dictum\Dictum.deps.json` and `src\Dictum.Windows\obj\project.assets.json` were read for the inventory:

| Component | Version | Licence as found | Copyright as found | Files read (under `%USERPROFILE%\.nuget\packages\<id in lower case>\<version>\`) |
|---|---|---|---|---|
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 | MIT (nuspec expression) + package LICENSE.TXT | nuspec: "© Microsoft Corporation. All rights reserved."; LICENSE.TXT: "Copyright (c) .NET Foundation and Contributors" | microsoft.netcore.app.runtime.win-x64.nuspec; LICENSE.TXT; THIRD-PARTY-NOTICES.TXT (Annex A source) |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | none: licenseUrl https://aka.ms/WinSDKLicenseURL only (requireLicenseAcceptance true); not reproduced | nuspec: "© Microsoft Corporation. All rights reserved." | microsoft.windows.sdk.net.ref.nuspec |
| Whisper.net | 1.9.1 | nuspec license type "file" -> LICENSE (text: "MIT License") | nuspec: none stated; LICENSE: "Copyright (c) 2024 sandrohanea" | whisper.net.nuspec; LICENSE |
| Whisper.net.Runtime | 1.9.1 | MIT (nuspec expression) | nuspec: none stated | whisper.net.runtime.nuspec; content/readme.md (the whisper.cpp note) |
| Whisper.net.Runtime.NoAvx | 1.9.1 | MIT (nuspec expression) | nuspec: none stated | whisper.net.runtime.noavx.nuspec |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT (nuspec expression) | nuspec: none stated | whisper.net.runtime.vulkan.nuspec |
| LLamaSharp | 0.27.0 | MIT (nuspec expression) | nuspec: "MIT, SciSharp STACK 2026" | llamasharp.nuspec |
| LLamaSharp.Backend.Cpu | 0.27.0 | MIT (nuspec expression) | nuspec: "Copyright 2023 The llama.cpp Authors. All rights reserved." | llamasharp.backend.cpu.nuspec |
| LLamaSharp.Backend.Vulkan | 0.27.0 | MIT (nuspec expression) | nuspec: "Copyright 2023 The llama.cpp Authors. All rights reserved." | llamasharp.backend.vulkan.nuspec |
| LLamaSharp.Backend.Vulkan.Windows | 0.27.0 | MIT (nuspec expression) | nuspec: "Copyright 2023 The llama.cpp Authors. All rights reserved." | llamasharp.backend.vulkan.windows.nuspec |
| Anthropic | 12.57.0 | MIT (nuspec expression) + package LICENSE | nuspec: "Copyright 2026 Anthropic"; LICENSE: "Copyright 2023 Anthropic, PBC." | anthropic.nuspec; LICENSE |
| NAudio.Core | 3.1.0 | MIT (nuspec expression) | nuspec: "© Mark Heath 2026" | naudio.core.nuspec |
| NAudio.Wasapi | 3.1.0 | MIT (nuspec expression) | nuspec: "© Mark Heath 2026" | naudio.wasapi.nuspec |
| Avalonia | 11.3.22 | MIT (nuspec expression) | nuspec: "Copyright 2013-2026 © The AvaloniaUI Project" | avalonia.nuspec |
| Avalonia.Themes.Fluent | 11.3.22 | MIT (nuspec expression) | nuspec: "Copyright 2013-2026 © The AvaloniaUI Project" | avalonia.themes.fluent.nuspec |
| Avalonia.Win32 | 11.3.22 | MIT (nuspec expression) | nuspec: "Copyright 2013-2026 © The AvaloniaUI Project" | avalonia.win32.nuspec |
| Avalonia.Skia | 11.3.22 | MIT (nuspec expression) | nuspec: "Copyright 2013-2026 © The AvaloniaUI Project" | avalonia.skia.nuspec |
| Avalonia.Remote.Protocol | 11.3.22 | MIT (nuspec expression) | nuspec: "Copyright 2013-2026 © The AvaloniaUI Project" | avalonia.remote.protocol.nuspec |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | nuspec license type "file" -> LICENSE (no identifier named) | nuspec: "Copyright 2013-2025 © The AvaloniaUI Project"; LICENSE: "Copyright 2018 The ANGLE Project Authors." | avalonia.angle.windows.natives.nuspec; LICENSE |
| MicroCom.Runtime | 0.11.0 | MIT (nuspec expression) | nuspec: "Copyright 2021 © Nikita Tsukanov" | microcom.runtime.nuspec |
| SkiaSharp | 2.88.9 | MIT (nuspec expression) + package LICENSE.txt | nuspec: "© Microsoft Corporation. All rights reserved."; LICENSE.txt: "Copyright (c) 2015-2016 Xamarin, Inc." and "Copyright (c) 2017-2018 Microsoft Corporation." | skiasharp.nuspec; LICENSE.txt; THIRD-PARTY-NOTICES.txt (Annex B source) |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT (nuspec expression) + package LICENSE.txt | nuspec: "© Microsoft Corporation. All rights reserved."; LICENSE.txt: same two lines | skiasharp.nativeassets.win32.nuspec; LICENSE.txt; THIRD-PARTY-NOTICES.txt (hash-compared with Annex B) |
| HarfBuzzSharp | 8.3.1.1 | MIT (nuspec expression) + package LICENSE.txt | nuspec: "© Microsoft Corporation. All rights reserved."; LICENSE.txt: same two lines | harfbuzzsharp.nuspec; LICENSE.txt |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT (nuspec expression) + package LICENSE.txt | nuspec: "© Microsoft Corporation. All rights reserved."; LICENSE.txt: same two lines | harfbuzzsharp.nativeassets.win32.nuspec; LICENSE.txt; THIRD-PARTY-NOTICES.txt (hash-compared with Annex B) |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT (nuspec expression) + package License.md | nuspec: "(c) .NET Foundation and Contributors. All rights reserved."; License.md: "Copyright © .NET Foundation and Contributors" | communitytoolkit.highperformance.nuspec; License.md; ThirdPartyNotices.txt (Annex C source) |
| System.Interactive.Async | 7.0.0 | MIT (nuspec expression) | nuspec: "Copyright (c) .NET Foundation and Contributors." | system.interactive.async.nuspec |
| System.Linq.Async | 7.0.0 | MIT (nuspec expression) | nuspec: "Copyright (c) .NET Foundation and Contributors." | system.linq.async.nuspec |
| Microsoft.Bcl.AsyncInterfaces | 10.0.5 | MIT (nuspec expression) | nuspec: "© Microsoft Corporation. All rights reserved." | microsoft.bcl.asyncinterfaces.nuspec; THIRD-PARTY-NOTICES.TXT (hash-compared with Annex A) |
| Microsoft.Bcl.Memory | 10.0.5 | MIT (nuspec expression) | nuspec: "© Microsoft Corporation. All rights reserved." | microsoft.bcl.memory.nuspec; THIRD-PARTY-NOTICES.TXT (hash-compared with Annex A) |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | MIT (nuspec expression) | nuspec: "© Microsoft Corporation. All rights reserved." | microsoft.extensions.ai.abstractions.nuspec |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.5 | MIT (nuspec expression) | nuspec: "© Microsoft Corporation. All rights reserved." | microsoft.extensions.dependencyinjection.abstractions.nuspec; THIRD-PARTY-NOTICES.TXT (hash-compared with Annex A) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.5 | MIT (nuspec expression) | nuspec: "© Microsoft Corporation. All rights reserved." | microsoft.extensions.logging.abstractions.nuspec; THIRD-PARTY-NOTICES.TXT (hash-compared with Annex A) |
| System.Numerics.Tensors | 10.0.5 | MIT (nuspec expression) | nuspec: "© Microsoft Corporation. All rights reserved." | system.numerics.tensors.nuspec; THIRD-PARTY-NOTICES.TXT (hash-compared with Annex A) |

Annex sources are the same table's files: `THIRD-PARTY-NOTICES.TXT` of the .NET runtime pack (78,041 bytes, SHA-256
`6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21`, identical in five Microsoft packages), SkiaSharp's
`THIRD-PARTY-NOTICES.txt` (139,775 bytes, `21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5`, identical in the two
native-assets packages) and CommunityToolkit.HighPerformance's `ThirdPartyNotices.txt` (8,600 bytes,
`7774f8b0ab66bfb47ec154ce24a2d6d574febc44a74d940b8a037d1c56756163`).

**What the notices do not settle.**
- **Microsoft.Windows.SDK.NET.Ref 10.0.19041.57** (`Microsoft.Windows.SDK.NET.dll`, 24.9 MB, and `WinRT.Runtime.dll`)
  states its licence only as a URL, https://aka.ms/WinSDKLicenseURL, with `requireLicenseAcceptance` true. The package
  holds no licence text and no identifier (its nuspec and file list, read in the NuGet cache on 17/10/2026). The
  orchestrator read that URL on 17/10/2026 and passed this lane what follows; this lane could read nothing but git and
  gh and did not read it again. The URL led to
  https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf, titled "MICROSOFT
  SOFTWARE LICENSE TERMS - MICROSOFT WINDOWS SOFTWARE DEVELOPMENT KIT (SDK) FOR WINDOWS 10". Its "Distributable Code"
  section sets the conditions on which an application may include SDK files, and those conditions include an
  end-user-terms requirement. The entry in `THIRD-PARTY-NOTICES.txt` gives the title and the address and says to read
  the terms there; it does not reproduce them, and this record does not copy the requirement's wording either.
  **Pending:** the product owner's decision on the end-user-terms requirement (morning batch). `WinRT.Runtime.dll` is
  covered separately: its version information (file version 2.2.0.48161, product version
  `2.2.0.48161+8649ee3eeb2445ca2a36d80d878ef60b96a6c65d`, copyright "Copyright (c) Microsoft Corporation. All rights
  reserved.", read in the NuGet cache on 17/10/2026) names commit 8649ee3 of https://github.com/microsoft/CsWinRT. The
  `LICENSE` at that commit, read through the GitHub API on 17/10/2026 (git blob
  `3d8b93bc7987d14c848448c089e2ae15311380d7`, 1,162 bytes), is the MIT License, "Copyright (c) Microsoft Corporation.";
  the entry reproduces it verbatim, keeping the source's four-space indent and changing only its line endings, from CRLF
  to LF.
- The native libraries (whisper.cpp, ggml, llama.cpp and the Vulkan builds) are covered by their packages' nuspec licence
  (MIT) and copyright line. What those binaries embed beyond that (shaders, Vulkan headers, other code) was not
  examined; Annex B covers Skia and HarfBuzz because their package says so.
- Whisper.net.Runtime, .NoAvx and .Vulkan state no copyright line; their entries say so and point to whisper.cpp's lines
  in the model section (read by the earlier lane, not re-read here).
- LLamaSharp's copyright element reads "MIT, SciSharp STACK 2026"; it is quoted as found, not tidied.
- A shipped package's licence can change with a version. The test below makes a bump fail until its entry is re-read:
  Dependabot's monthly NuGet pull request will fail CI until `THIRD-PARTY-NOTICES.txt` names the new versions. That is
  deliberate; if it proves too much friction, relax the comparison to package ids only.

**The notices pin** (`tests/Dictum.Tests/ThirdPartyNoticesTests.cs`, 4 tests). Every package in the restore result that
puts a managed library (a runtime asset) or a Windows x64 native DLL (any `.dll` under a `win-x64` path in its file list)
in the output has a `Package: <id> <version>` entry at that version; the 3 app projects' `PackageReference` elements
likewise (this catches the meta-package); the self-contained runtime pack and the Windows SDK projection have entries
(publish.ps1 is `--self-contained`); every entry has "Ships as", "Licence" and "Copyright (nuspec)" lines. The first test
also asserts it found `LLamaSharp.Backend.Vulkan.Windows` and `SkiaSharp.NativeAssets.Win32`, so a restore-result change
that hides them fails the test rather than weakening it. Observed failing: an entry deleted (NAudio.Core: the shipping and
the PackageReference tests, 2 failed); a version changed (Avalonia 11.3.21: 2 failed); the transitive native package's
entry deleted (`LLamaSharp.Backend.Vulkan.Windows`: only the shipping test failed, which the csproj-only rule would have
missed); the runtime pack's entry renamed (1 failed); the meta-package's entry renamed (only the PackageReference test
failed); one entry's copyright line deleted (1 failed). It needs `src\Dictum.Windows\obj\project.assets.json`, which any
build writes, and does not read the NuGet cache.

**Verification after the fixes** (this lane, 17/10/2026): Release `--no-incremental` build, 0 warnings, 0 errors. Filtered
suite (`FullyQualifiedName!~Install_copies_models`): 1,297 total, 1,276 passed, 21 skipped, 0 failed (the rebased head had
1,279, 1,258, 21, 0; 18 new tests). CI on ad72ef5 (the push run and the pull-request run, 17/10/2026): both succeeded, with
1,298 tests (those 1,297 plus `Install_copies_models`, which CI runs), 1,277 passed, 21 skipped, 0 failed; the two
"GitHub Release" jobs were skipped (tag builds only), so the release job's new checkout and `--notes-file` have not run.
Not run: the installer, the Store package build, Windows Sandbox, a release build, and the packaged self-test.

## Model folder and the bundled base.en (17/10/2026, branch feature/model-folder-bundled)

Decisions 15 and 16 (product owner, 16-17/10/2026). Staff on shared virtual desktops with no GPU have little
space on the system drive, and the Store resubmission should need no download to test.

- **Decision 15: a user can nominate another drive or folder for the models.** `models.folder` in settings.json, written by
  Settings > Models > Model folder (Browse…, Use default). An absolute folder or empty; a relative or invalid value falls
  back to the default with a warning line, as every other key does. `whisper.models_dir`, which already moved the models
  folder for both kinds of model, is kept as its earlier name: read when `models.folder` is not set, ignored with a warning
  when both are set and differ, and cleared by Browse… and Use default so the folder chosen there is the one in force. The
  key left `whisper` because it was never only whisper's. New settings files write `models.folder` only. `install.ps1`
  (not this lane's) asks the app (`--print-models-dir`), which now reads either.
- **Decision 16: base.en ships inside every package** (a parallel lane puts `models\ggml-base.en.bin` beside `Dictum.exe`
  in the MSIX, the zip and the installer, per the orchestrator's brief; not checked from this lane), **no clean-up model by
  default** (the rule-based tidy), and the entry level must run on small machines.
- **The lookup, for both kinds (`ModelFolders`):** the chosen folder, then `%LOCALAPPDATA%\Dictum\models`, then the bundled
  `models` folder beside `Dictum.exe`. A name resolves to the first folder holding its file with the catalogue's exact size;
  nothing found resolves to where a download puts it. Chosen first, so an administrator's or the user's choice wins; the
  per-user folder next, so models downloaded before this change keep working; the bundled folder last, so a copy the user
  downloads or copies in takes over without the bundled one being touched. The tray panel, Settings, Mode presets,
  calibration, `--transcribe` and `--selftest` all resolve through it (`SettingsFile.EffectiveModelPath` and
  `CleanupModelPath`, `ModelCoordinator.IsDownloaded`, `CalibrationHost`).
- **The check:** the size in every folder (one file-system read: Settings > Models looks every catalogue model up on each
  redraw, on the UI thread); in the bundled folder the tray app also runs the full catalogue check
  (`ModelIntegrity.Verify`: size, then SHA-256, a pass remembered in verified-models.json, the result kept per file version
  for the session). A bundled file that fails is passed over as if absent, said once in the log, and never deleted or
  replaced. Every catalogue file is still size- and SHA-256-checked before it loads, wherever it was found, as before. Two
  `// simplification:` notes: in the user's folders a same-size file with the wrong bytes is found and then refused at load
  rather than passed over (`ModelFolders.Locate`); the first lookup of a bundled file after an install or update hashes it
  on the calling thread, the UI thread during a start (`ModelCoordinator.BundledFilePasses`; base.en took 0.5 s, below).
- **The bundled folder is read-only.** It is never a download destination (`AppPaths.ChosenModelsDir` ignores a chosen
  folder that is it or inside it, and Browse… refuses it with a sentence); it is not in the path registry, so "Delete all
  app data" and Storage's Delete never reach it (`ModelFiles.Delete` refuses it too); Settings > Storage shows its models
  with no Delete. Removing the program's own files stays the packaging lane's.
- **First run:** when base.en is found in the lookup order no model question is asked: the choices settings.json leaves open
  are answered with `whisper.model` = `base.en` and `llm.provider` = `none`, exactly what the pickers write for base.en and
  "No AI cleanup (rule-based tidy)" (a test compares the two files byte for byte, new and seeded). The hotkey check stays.
  Without it (a package without the file, a deleted one, one failing its check) the pickers run as before. Settings >
  Models offers "Add AI clean-up (1.1 GB download)" while the rule-based tidy is in use: the catalogue's own path for the
  recommended model (asked first, size and host named, then downloaded, checked and switched to). The in-memory default
  (`llm.provider` = `local` when the key is absent) is unchanged: every first run writes the key, and changing the default
  would change what a hand-edited file without it means.
- **No-move rule:** changing the folder moves no files, and the note under the field says so. Copying gigabytes behind the
  user's back from a Settings click, possibly across a network, is not done.
- **Network folders:** a `\\server\share` path (read from the text alone, so deciding touches no share) or a mapped network
  drive (Windows' drive type) gets a one-line warning under the field. A warning, not a refusal: an administrator may still
  choose a share. Not measured: how slowly models load from an Azure file share; the product owner's concern is the reason
  for the warning and for the layout below.
- **Recommended layout for shared virtual desktops** (amended after the lens, below): one local folder per VM shared by its
  users (for example `E:\Dictum models` on a persistent data disk), readable by every user and writable only by
  administrators, seeded as `models.folder` for every user with `install.ps1 -Settings` and filled once by an administrator
  with `-ModelsFrom`; a user who wants another model uses Use default. Dictum then looks only in their own folder and the
  models that come with it, no longer in the shared folder, so the models they use from it are downloaded again. Not a
  cool-tier Azure file share.
- **Free space:** a download is refused, before it is asked and again where it starts, when its drive would keep less than
  200 MB (decimal, as every size Dictum shows); a kept `.part` needs only the rest. When the free space cannot be read the
  download goes ahead, as the existing 2 GB warning does. Recalled, not checked here: .NET's `DriveInfo` refuses a UNC root,
  so a download into a share is never refused for space.

Strings, all PROPOSED in code comments for the product owner (17/10/2026):
- Settings > Models card and field label "Model folder"; buttons "Browse…" (screen-reader name "Browse for a model
  folder") and "Use default" ("Use the default model folder"); note "Dictum looks here first and downloads go here.
  Changing it does not move any files."; network line "Models load slowly from a network folder; a folder on this PC is
  faster."; chooser title "Choose a folder for Dictum's models"; refusal "That folder holds the models that come with
  Dictum, which Dictum never changes. Choose another folder."
- "Add AI clean-up (1.1 GB download)" (the size from the catalogue), "Add AI clean-up" once the model is on the PC; a
  speech model used from the bundled folder reads "Included".
- Settings > Storage: "Downloads go to: <folder>" (was "Models folder: <folder>"), "Dictum looks for a model in these
  folders, in this order:", "1. Chosen folder: …", "2. Default folder: …", "3. Included with Dictum (read-only): …", each
  model row "<folder name> · <path>", "No model files yet." (was "No model files in this folder.").
- Download refusal: "Not enough space for <file> (<size>): <free> is free on that drive, and Dictum keeps 200 MB spare.
  Free some space, or choose another model folder in Settings > Models."
- The first run adds no sentence: with base.en on the PC its model pickers are simply not shown.

Files outside the lane's list that the deliverables needed (none is on its do-not-edit list): `Storage/DiskSpace.cs` (the
refusal, the network test), `Storage/ModelFiles.cs` (each model's folder; the bundled ones for Storage only),
`Storage/PathRegistry.cs` (comment), `App/PersonalService.cs` and `Calibration/CalibrationHost.cs` (the lookup),
`Shell/IShellDialogs.cs` and `Shell/ShellDialogs.cs` (the folder chooser; `ChooseFile` shares its owner-window code now).

Not done, for the orchestrator (the first two, and the STORE.md lines, were done in the lens fixes below):
- `Cli/CliOptions.cs` and `Cli/CliCommands.cs` (not this lane's) still describe `--print-models-dir` as "whisper.models_dir,
  else the default"; the command prints what the app now resolves (`models.folder` first), and the README says so.
- In the CLI (`--selftest`, `--transcribe`) a bundled file is held to the size check only at lookup; a same-size damaged one
  is then refused by the CLI's own check with ModelIntegrity's remedy ("rename it"), which does not fit a read-only folder.
  `ModelIntegrity`'s remedy also treats only the download folder as Dictum's, so a damaged file in the per-user folder
  while another folder is chosen is told "rename it" (as with `whisper.models_dir` before). Both are outside this lane.
- The network line's wiring on the page is pinned through `ModelsPage.FolderWarning` only: a headless page test with a UNC
  folder would look every model up on that share.
- docs/STORE.md lines the store-description text did not list still describe a first-run download: the minimum
  requirements ("An internet connection to download a model on first use, or model files copied in"), section 9 rows 3, 6,
  11 and 17, the offline-test note (copy "ggml-base.en.bin" into the models folder) and its date line. Left as they are, as
  instructed ("everything not listed stays exactly as it is").

**Verification** (17/10/2026, this machine). Release `--no-incremental` builds, 0 warnings, 0 errors. Filtered suite
(`FullyQualifiedName!~Install_copies_models`): main at c0aa989 1,130 total, 1,111 passed, 19 skipped, 0 failed; this lane's
first commit on it 1,162 / 1,143 / 19 / 0 (32 new cases; the 33rd, the preset and calibration pin, was then run with its
class, 33 of 33). Rebased onto main at 23acf35 (no conflicts): main 1,267 / 1,248 / 19 / 0; this lane 1,300 / 1,281 / 19 / 0
(33 new cases in `ModelFolderTests`). One full run on the rebased docs commit had one failure in an
untouched test, `FixBatchPrivacyTests.A_request_to_this_machine_never_goes_through_a_configured_proxy(host: "localhost")`
(2.0 s; it sets the process-wide `HttpClient.DefaultProxy`); its class then passed three times alone and the full suite
passed again (failure text not kept).

The built app (`Dictum.exe --selftest`, a copy of this build's output with this machine's own
`%LOCALAPPDATA%\Dictum\models\ggml-base.en.bin`, read only, copied into its `models` folder; scratch APPDATA and
LOCALAPPDATA; settings naming base.en): the speech model resolved to the bundled file,
"SHA-256 verified", whisper loaded on Vulkan, PASS, and the log line "Verified ggml-base.en.bin against the catalogue on
first use (SHA-256) in 0.5 s". With `models.folder` naming a scratch folder holding a 1,000-byte `ggml-base.en.bin`, that
copy was passed over for the bundled one ("SHA-256 verified earlier", PASS); with a good copy there, the chosen copy was
used ("SHA-256 verified", PASS). The bundled folder held only the copied file afterwards, unchanged. Not run: the tray app
(the product owner's own copy is running), the first-run windows on a real desktop, a real network share, a mapped drive, a package
built by the packaging lane.

**Mutation checks**, each on a Release `--no-incremental` build, the pin class run, the source restored with
`git checkout` and the tree checked clean (scratchpad `mutate.ps1`, exact single-occurrence replacements). All 29 on
1ce0523 (before the rebase); each observed failing on the reverted behaviour:
the lookup with the per-user folder before the chosen one, 2 failures (the precedence pin and the Storage listing); with
the bundled folder first, 2 (the same); with no size check, so a wrong-size copy is used, 1 (precedence); the bundled
folder accepted as the chosen folder, 1 (the download pin: the download landed there); Browse… accepting it, 2 (that pin's
refusal and the Browse pin); a bundled file used unchecked, 2 (both cases of the failing-file pin); its check not
remembered, so it is checked and logged at every lookup, 2 (the same, logged more than once); the first run's bundled
branch removed, 5 (the first start end to end, both first-run cases, the seeded file, the seeded folder); the bundled first
run writing the local clean-up model instead of the tidy, 4; the first run never asking, base.en or not, 4 (both "as
before" halves and both failing-file cases); a seeded `models.folder` ignored at first run, 1; no free-space refusal
anywhere, 1; none where a download starts, 1 (the first run's download); the 200 MB spare dropped, 2 (the rule and the
sites); a kept `.part` not counted, 1; a UNC path not seen as a network folder, 2 (the two UNC strings; `\\?\UNC\` has its
own branch); Browse… writing without a reload, 2 (both Browse pins); Browse… ignoring the folder picked, 1; Browse…
leaving `whisper.models_dir` set, 1; Storage offering Delete for a bundled model, 1; "Add AI clean-up" shown whatever the
provider, 1; a bundled model reading "Downloaded", 1; `models.folder` accepting any text, 2 (the relative and the invalid
value); no warning when the two keys differ, 1; calibration looking only in the download folder, 1; Mode presets the
same, 1; a new settings.json still writing `whisper.models_dir`, 1; the speech model resolved in the download folder only,
5; the clean-up model the same, 1.
After the rebase, ten of them again on f91b672 (the lines they change are the same), each failing as often as before: the
per-user folder before the chosen one 2, the bundled folder accepted as the chosen folder 1, a bundled file used unchecked
2, the first run's bundled branch removed 5, the local clean-up model written instead of the tidy 4, the first run never
asking 4, no free-space refusal 1, a UNC path not seen as a network folder 2, Browse… writing without a reload 2, a new
settings.json still writing `whisper.models_dir` 1. The tree was clean at f91b672 after each.

### Lens fixes (17/10/2026: the opus lens on 4fb1a6f returned Block)

The lens's probes P1 to P4, P6 and P7 (each asserted a defect and passed at 4fb1a6f) are pins of the fixed behaviour now,
in `tests/Dictum.Tests/ModelFolderSafetyTests.cs`, with its P5 pin added as it wrote it and a pin of its own for the
deletion backstop (18 cases in all).

1. **A chosen folder that other users may share (the blocker).** The layout above had the shared folder "writable too, if
   they may download into it", and two things trusted whatever was in it: ModelIntegrity's verified-models.json (size and
   last-write time, so a same-size file swapped in with its time put back loaded without a hash: P7) and "Delete all app
   data" (it deleted the folder's catalogue files, asking with a count and a size only). Now:
   - The docs say the folder is readable by every user and writable only by administrators, who fill it once with
     `install.ps1 -ModelsFrom`; a user who wants another model uses Use default. Dictum then looks only in their own
     folder and the models that come with it, no longer in the shared folder, so the models they use from it are
     downloaded again. The example is `E:\Dictum models` on a persistent data disk: D: was dropped because on Azure VMs it
     is often the temporary disk (the orchestrator's note; recalled, not checked here).
   - ModelIntegrity takes the lookup order (`ModelFolders.LookupOrder`) and trusts the record only for a file in the
     per-user folder or the bundled folder. Any other file (the chosen folder, a file chosen by path) is hashed at its first
     load in each session; a pass is remembered for that session, and a file Dictum has just downloaded counts as checked
     for the session. The `// simplification:` note now names the in-session ceiling as well.
   - A chosen folder outside the user's profile (`AppPaths.ProfileDir`, from `Environment.SpecialFolder.UserProfile`;
     `InProfile` follows links) has its own category in the path registry, `SharedModels`, which "Delete all app data"
     (with or without settings) leaves alone; its question then ends "Left alone: <folder> (…)". Storage's Delete still
     removes one of its models, permissions allowing, and "Remove Dictum completely" still lists and deletes each one, as
     before (it shows every path; whether it should leave them too is for the orchestrator). An item a deletion cannot
     remove is now reported as "in use or not permitted".
2. **The replacement for a model in use** (P2): `Replacement` looks in the bundled folder too and compares names, so the
   bundled base.en is offered, switched to and never deleted, and a second copy of the model in use is never its own
   replacement.
3. **A seeded clean-up provider** (P6): `SettingsLoadResult.NamesCleanupProvider`; a seed naming `llm.provider` (local, no
   `llm.model`) keeps it at a first run with base.en and gets the recommended model written. Nothing downloads, and the
   existing "Cleanup model … is not downloaded; using the rule-based tidy" notice shows.
4. **The bundled folder by another name** (P1 deleted the bundled base.en through a junction): `AppPaths.IsBundled` also
   compares final paths (`PathRegistry.FinalPath`: each junction or symbolic link along the path followed, 8.3 names and
   a `\\?\C:\` path normalised; not a subst drive or a mounted volume). Checked on this machine with a scratch
   program (17/10/2026, the .NET 10 runtime of the SDK here): `Path.GetFullPath` expands 8.3 names (`C:\PROGRA~1` became
   `C:\Program Files`) but keeps `\\?\` and `\\.\`;
   `DirectoryInfo.ResolveLinkTarget(true)` returns a junction's target and null for a path through one, hence the walk
   part by part (about 60 µs a part here). A `// simplification:` note: links are not followed on a network folder, and a
   subst drive or a volume mounted in a folder is not followed at all (that needs a native call, which Dictum.Core may
   not declare: ArchitectureTests).
   The backstop: `ModelFiles.Delete` and `RemovalPlan` refuse any model file whose final path is inside the bundled
   folder, and the settings load logs a `models.folder` (or `whisper.models_dir`) it ignores.
5. **The advice for a damaged file** (P3) follows its folder: the chosen and per-user folders "Delete it and download it
   again.", the bundled one "It came with Dictum: reinstall Dictum, or download <name> in Settings > Models.", a file
   anywhere else "rename it" as before.
6. **One lookup** (P4): `CalibrationHost` takes the tray app's lookup (`ModelCoordinator.Find`), so a bundled base.en
   failing its SHA-256 is passed over there too and offered as a download. `--download-model` and `--bench-cleanup` look
   in every model folder (`ModelFolders.Find`, the bundled one held to `ModelIntegrity.Passes`), so `--download-model
   base.en` with the bundled copy downloads nothing; the existing-copy check now goes through ModelIntegrity. install.ps1
   and the README use `-Model small.en` as the example.
7. **Help and docs:** `--print-models-dir` (help and summary), install.ps1's folder lines and its `-Model` text, STORE.md
   (screenshot 5 is now the Settings > Models page, the requirements line, the Esc sentence in the notes, section 9 rows
   3, 4, 6, 11 and 17; line 287 left for PR #10) and SPEC §18.4.
8. **Settings > Models' lookups:** the catalogue (every speech and clean-up model: 22 lookups, each up to three folders)
   is looked up again only after a settings load, a download that ended or a deletion (`AppShell.ModelFilesVersion`), or
   when the page is shown again, never on a download's progress tick. `ModelCoordinator.Lookups` counts lookups for the pin.

Strings, PROPOSED in code comments for the product owner (17/10/2026):
- Delete all app data's question, when a chosen folder is outside the user's profile: "Left alone: <folder> (outside
  your user folder, so other users may share its models; Delete beside a model removes one)."
- The advice for a damaged model that came with Dictum: "It came with Dictum: reinstall Dictum, or download <name> in
  Settings > Models."
- Storage's Delete on a file in the bundled folder by another name: "<file> came with Dictum, so Dictum does not delete it."
- Changed: "… item(s) could not be deleted (in use or not permitted); see the log." (was "(in use)").
- Log line and `--selftest` note: `Setting "models.folder" (<folder>) is the folder of the models that come with Dictum,
  or inside it, which Dictum never changes, so it is ignored: models download to <per-user folder>.` (or
  `whisper.models_dir`).
- Command line: "<path> is already present and matches the catalogue (<bytes> bytes, SHA-256 verified). Nothing to do."
  or "(…, SHA-256 verified earlier)" (was "(…, SHA-256 <hash>)"), the path now the copy the lookup found; and
  `--print-models-dir`'s help line.
- STORE.md: screenshot 5 "Settings, Models page (…)" with "Choose a bigger speech model or add AI clean-up; base.en comes
  with Dictum."; requirement "No download needed to start: base.en is included; bigger speech models and AI clean-up are
  optional downloads"; the notes' "Esc cancels a hold-to-talk dictation and nothing is pasted; in hands-free, Esc stops
  and keeps what is already typed."

**Verification** (17/10/2026, this machine). Release `--no-incremental` builds, 0 warnings, 0 errors. Filtered suite
(`FullyQualifiedName!~Install_copies_models`): 4fb1a6f 1,300 total, 1,281 passed, 19 skipped, 0 failed; c1929ed (the
fixes) 1,317 / 1,298 / 19 / 0; d88e09a (the backstop pin on its own) 1,318 / 1,299 / 19 / 0, 18 new cases, the junction
ones run, not skipped. The built app (`Dictum.exe`, a copy of this build's output; scratch APPDATA and LOCALAPPDATA;
`offline_only` on; this machine's own `ggml-base.en.bin` copied, read only, into its `models` folder and into a scratch
chosen folder): the bundled copy "SHA-256 verified", then "SHA-256 verified earlier" at the next start; the chosen
folder's copy "SHA-256 verified" at both starts (hashed again in the next session); with `models.folder` a junction to
the bundled folder, `--print-models-dir` printed the per-user folder and `--selftest` showed the ignored-folder note and
used the bundled file ("SHA-256 verified earlier", PASS); a same-size zero-filled bundled `ggml-base.en.bin` in a second
copy of the app was refused with "… It came with Dictum: reinstall Dictum, or download base.en in Settings > Models."
[FAIL]. The bundled folder was unchanged and the junction removed. Not run: the tray app, a real network share, a subst
drive, a package built by the packaging lane, `--download-model` against the network (its pin uses a counting server).

**Mutation checks**, each on a Release `--no-incremental` build and the whole filtered suite, the files restored with
`git checkout`, each checked byte-identical to HEAD (`git hash-object` against HEAD's blob) and the tree clean
(scratchpad `fixlane/mutate.pl`, exact single-occurrence replacements). Run 1, all 18 on c1929ed, each observed failing on
the reverted behaviour: the record trusted in every folder, 1 (the P7 pin); a chosen folder outside the profile counted
with the user's own models, 1 (the delete-all pin, outside case); a failed deletion reading "(in use)" again, 1; the
replacement looked for without the bundled folder, 1 (P2); the replacement compared by path, not name, 1 (two copies);
a seeded provider turned to none, 1 (P6); `IsBundled` by path text only, 1 (P1); no backstop in `ModelFiles.Delete`, 1
(P1, on the refusal's message); no backstop in `RemovalPlan`, 1 (P1); no log line for an ignored folder, 1 (P1); the
per-user folder told "rename it" while another is chosen, 2 (P3 and FixBatchLifecycleTests' picked-file pin); the bundled
folder told "rename it", 1 (P3); calibration on the plain lookup, 1 (P4); `--download-model` in the download folder only,
1; Settings > Models looking every model up on each redraw, 1 (110 lookups for five progress ticks, 22 each); a download
that ended not seen by the page, 1 (only Select's own lookup followed it); the page not looking again when shown again, 1;
the bundled folder accepted as the chosen folder, 2 (P1 and ModelFolderTests' download pin; the lens's P5 pin held, since
the `RemovalPlan` backstop still skipped the bundled files). Run 2 on d88e09a, after the backstop case got its own pin:
`IsBundled` by path text only 2 (P1, and the backstop pin, whose bundled file was then deleted through the junction); no
backstop in `ModelFiles.Delete` 2 (P1, and the backstop pin on the deletion itself: `Deleted` was true); no backstop in
`RemovalPlan` 1 (the backstop pin: "Delete all app data" listed the bundled file); no log line 1 (P1); the bundled folder
as the chosen folder 2 (as in run 1); that and no `RemovalPlan` backstop together 6 (P1, the download pin, the backstop
pin and three cases of the lens's P5 pin). Every restore was byte-identical and the tree clean (18 and 6).

Not done, for the orchestrator:
- "Remove Dictum completely" still deletes the catalogue files of a chosen folder outside the user's profile (it lists
  each path first); the brief named "Delete all app data" only.
- A subst drive, a mounted volume, or a share that points back at this PC, is not seen as the bundled folder (the
  simplification note).
- `--selftest` and `--transcribe` still find a bundled file by size alone; a damaged one is then refused with the new
  advice (shown above), not passed over.
- The help's `--download-model` example (`e.g. base.en`, in `CliOptions` and the README's command list) was left as it was;
  with base.en in the package it says the file is already present. (Changed to `e.g. small.en` in the re-lens fixes below.)
- Settings > Models resolves the speech model in use through the folders on a redraw only while no model is loaded
  (`CurrentName`); the 22 catalogue lookups were the finding. (The re-lens found the language note doing it on every
  redraw, a progress tick included, even with a model loaded; it now reads the loaded model: see the re-lens fixes below.)
- `--bench-cleanup`'s lookup had no pin of its own (running it needs a clean-up model). (Pinned in the re-lens fixes
  below, through a clean-up model whose SHA-256 fails, which needs no engine.)

**Rebased onto main 6daff77** (17/10/2026, at the orchestrator's request: PR #10, the VC++ runtime framework and base.en in
every package, had merged). Conflicts only in docs: DECISIONS.md (both appended after the hands-free section; PR #10's
section kept first, then this one) and STORE.md (the certification notes' model paragraph: this PR's text kept, as the
orchestrator asked; PR #10's offline-test note and its other lines merged as they were); README merged cleanly. The patch
this PR makes to `src`, `tests` and `scripts` on 6daff77 is byte-identical to the one it made on 23acf35 (the two
`git diff` outputs compared). The rebase renamed the commits: c1929ed is 0967f4d, d88e09a is b954028, 8c319cf is 74eeb47.
Release `--no-incremental` build, 0 warnings, 0 errors; filtered suite on 74eeb47: 1,348 total, 1,327 passed, 21 skipped,
0 failed (main's 1,297 with 21 skipped, per PR #10's record, plus this PR's 51 cases). Run 3 of the mutation checks, all 19
on 74eeb47 the same way, each again observed failing on the reverted behaviour: M1 the verified record trusted in every folder: 1; M2 a chosen folder outside the profile emptied by Delete all app data: 1; M3 a failed deletion said "(in use)" again: 1; M4 the replacement looked for without the bundled folder: 1; M5 the replacement chosen by path, not name: 1; M6 a seeded llm.provider turned to none at first run: 1; M7 IsBundled comparing path text only: 2; M8 no backstop in ModelFiles.Delete: 2; M9 no backstop in RemovalPlan: 1; M10 no log line for an ignored bundled models.folder: 1; M11 the per-user folder told "rename it" while another is chosen: 2; M12 the bundled folder told "rename it": 1; M13 calibration on the plain lookup again: 1; M14 --download-model looking only at the download folder: 1; M15 Settings > Models looking every model up on each redraw: 1; M16 a download that ended not seen by Settings > Models: 1; M17 Settings > Models not looking again when shown again: 1; M18 the bundled folder accepted as the chosen folder: 2; M19 M18 and M9 together: the bundled folder chosen and no backstop in RemovalPlan: 6. Every restore was
byte-identical to HEAD and the tree clean (19 of 19). Left for the orchestrator: PR #10's sentence before the certification
notes in STORE.md ("The 'no download is needed' lines … hold only for a package built after the app change that reads the
bundled model … has merged") describes this PR as still to come, and can go once it merges (reworded in the re-lens fixes
below: it now says "merged together with the model-folder change (PR #11)"); and the notes now say twice
that no download is needed (PR #10's "CHANGED SINCE THE LAST SUBMISSION" paragraph and this PR's), kept as instructed.

### Re-lens fixes (17/10/2026: the opus re-lens on fa73e02 returned Needs changes)

No blocker; six minors and four nits. The re-lens's probes R9, R12 and R13 (each asserted a defect and passed at fa73e02) are pins
of the fixed behaviour now, in `tests/Dictum.Tests/ModelFolderSafetyTests.cs`, beside a pin for each other finding: 21 new cases,
20 net (the second-copy pin became the first of two cases of the delete-a-copy pin), 38 in the file.

1. **F1, the bundled folder by its volume name.** `models.folder` = `\\?\Volume{GUID}\...` (the bundled folder, its volume
   name from `mountvol C:\ /L`) was accepted. `PathRegistry.Plain` unwraps only a drive or UNC path behind `\\?\` or `\\.\`, so
   no comparison took the volume name for the bundled folder; the re-lens saw `--print-models-dir` print it and `ModelFiles.Delete`
   delete its base.en (its probe R9b). `SettingsLoader.FolderProblem` now refuses every device path other than `\\?\X:\` and
   `\\?\UNC\` (`PathRegistry.IsUnsupportedDevicePath`, beside `Plain`): a volume or GLOBALROOT name, `\\.\` and `\??\` in any form,
   either slash, a bare `\\?\C:`. The usual warning ("expected an absolute folder path") and the default folder follow, for
   `whisper.models_dir` too. The `// simplification:` note in `PathRegistry.FinalPath` names mounted volumes, and the claims in
   README, SPEC §24, item 4 above and four code comments (`AppPaths`, `ConfigLoader`, `ModelFiles`) now say "a `\\?\C:\` path; not a
   subst drive or a mounted volume" or "a name `IsBundled` sees through".
2. **F2, a replacement the tray app would not load.** `AppShell.Replacement` offered a bundled base.en that fails its SHA-256
   (Storage showed base.en as not on this PC, yet Delete on tiny.en offered "Switch to base.en first", and accepting left
   `whisper.model` = `base.en` naming a file the tray app passes over). It offers a candidate only when the tray app's lookup loads
   that very file (`WouldLoad`): for a speech model `Models.Find` returns its path, for a clean-up model
   `Models.IsCleanupDownloaded`.
3. **F3, a downloaded copy of the model in use.** A base.en downloaded by 1.0.x (CPU PCs were recommended it), beside the one that
   comes with Dictum, could not be deleted: "base.en is in use and no other model is downloaded", which was untrue. When the same
   model is also further along the lookup and passes the tray app's check (`ModelFolders.Locate(except)`,
   `ModelCoordinator.FindBesides`; the bundled copy is held to its SHA-256), Delete asks "Delete this copy of base.en (147 MB)?
   Dictum then uses the copy that came with it." (titled "Delete a model"; for a next copy in another of Dictum's folders, "…
   Dictum then uses the other copy, in <folder>."), releases the model (`Unload`), deletes the file with that path left out of the
   in-use check, and reloads, as "Delete all app data" does. If the deletion throws, the reload still runs and loads the same
   file again. The pins' stand-in engine holds its file open without sharing deletion, as a memory-mapped model does on Windows,
   so a deletion that does not release the model first fails.
4. **F4, a lookup on every progress tick.** `ModelsPage.Redraw` read `Shell.Models.ModelPath` for the language note, which resolves
   the model through the folders (file reads, on the UI thread) on every redraw, a progress tick included, even with a model
   loaded; the counting pin counted only `ModelCoordinator.Locate`. The note and the "In use" row now read
   `ModelCoordinator.CurrentPath` (the model loaded or loading, else `ModelPath`, as `CurrentName` did), and `ModelPath` counts as
   a lookup, so the pin sees every route. Checked against `ModelFolders.Locate` itself with a temporary static counter (not
   committed): five ticks with the page open and a model loaded made 5 `Locate` calls before and 0 now, the coordinator's count
   agreeing (5 and 0). With no model loaded, `CurrentName` still resolves the model once per redraw, as before.
5. **F5, Use default.** "Dictum then looks only in their own folder and the models that come with it, no longer in the shared
   folder, so the models they use from it are downloaded again." follows the Use default advice in README, SPEC §24 and above
   (twice).
6. **F6, pins.** R12: `InProfile` through a junction inside the profile that points out of it counts as outside, and the reverse as
   inside, with the registry category asserted for each (`SharedModels`, `Models`). `--bench-cleanup`: with another folder chosen,
   a clean-up model in the per-user folder is found through the app's lookup (the qwen2.5-0.5b file, at the catalogue's size, fails
   its SHA-256, which is logged, so no engine is needed).
7. **Nits.** The `--download-model` examples (help, the error for a bad name, README) are `small.en`: base.en is already on the PC.
   PROPOSED markers on the deletion result's "(in use or not permitted)", on the ignored-folder log line
   (`ConfigLoader.BundledFolderIgnored`) and on the command line's "already present" lines. STORE.md section 9 row 3 says the
   refusal applies with `offline_only` on and that with the network off a download fails after its retries (8 attempts, README
   "Model downloads and checks"); the certification caveat reads "has merged together with the model-folder change (PR #11)".

Strings, PROPOSED in code comments for the product owner (17/10/2026):
- Settings > Storage > Delete on the copy of a model in use when the same model is next in the lookup, titled "Delete a model":
  "Delete this copy of <name> (<size>)? Dictum then uses the copy that came with it." and, when the next copy is in another of
  Dictum's folders (an addition to the wording asked for), "Delete this copy of <name> (<size>)? Dictum then uses the other copy,
  in <folder>."
- Marked PROPOSED now, wording unchanged: "… item(s) could not be deleted (in use or not permitted); see the log.", the
  ignored-folder log line and `--selftest` note, and the command line's "<path> is already present … Nothing to do." lines.
- Changed: the help line "download a model (e.g. small.en) to the models folder with" and the error "--download-model needs a
  model name such as small.en (see --help)." (both were "base.en").
- Not new: a refused device path gets the existing warning, `Setting "models.folder" has invalid value "<value>": expected an
  absolute folder path. Using default null.`
- docs/STORE.md section 9 row 3 and the certification caveat, as in item 7.

**Verification** (17/10/2026, this machine). Release `--no-incremental` builds, 0 warnings, 0 errors. Filtered suite
(`FullyQualifiedName!~Install_copies_models`): fa73e02, run first, 1,348 total, 1,327 passed, 21 skipped, 0 failed (the
numbers recorded above); the code and pins (26a727d) 1,368 / 1,347 / 21 / 0 (the 21 skips are the gated integration
tests and the desktop ones, none of the junction or volume-name pins); the head (the docs, the `--download-model` error
text and one doc comment on top, after the mutation checks) 1,368 total, 1,347 passed, 21 skipped, 0 failed. The two counters (the
`ModelFolders.Locate` one of item 4 and the `PathRegistry.FinalPath` one below) were temporary instrumentation, never
committed. Not run: the built app (`Dictum.exe`), a real network share, a subst drive, a volume mounted in a folder, a
package, `--download-model` against the network.

**Mutation checks**, each an exact single-occurrence replacement on the committed tree (ab0b8d8), a Release
`--no-incremental` build and the whole filtered suite, the file restored with `git checkout`, checked byte-identical to
HEAD's blob (`git hash-object`), and `src`, `tests` and `scripts` clean (scratchpad `mf3/mutate.js`). All 16 observed
failing on the reverted behaviour, with this many failures each: F1 the refusal of device paths removed 9; the `\\?\X:\`
form not accepted 2; the `\\?\UNC\` form not accepted 2; `//?/` forms not seen as device paths 2; F2 any speech model
offered as the replacement 1; any clean-up model offered as the replacement 1; F3 no next copy ever found 3; no reload
after the deletion 3; the next copy not held to the tray app's check 1; the question always saying the copy that came
with Dictum 1; the copy being deleted still counted as in use 3; the model not released before its file is deleted 2; F4
the language note looking the model up on every redraw 1; CurrentPath always looking the model up 2; F6 InProfile
comparing text only 1; --bench-cleanup looking in the download folder only 1. Every restore was byte-identical and the
tree clean (16 of 16).

Not done, for the orchestrator:
- A refused `\\?\Volume{...}` value gets the existing "expected an absolute folder path" (blunt: it is absolute). A clearer sentence
  would be a new PROPOSED string; left for you.
- The new question's title is "Delete a model" (the plain Delete's), and its second form (a next copy in another of Dictum's
  folders) is an addition to the wording asked for. Strike either if unwanted.
- Settings > Models' redraw also evaluates `AppPaths.IsBundled` (read from the code: through `Shell.ModelsDir` and
  `ChosenModelsDir`) on every tick while a folder is chosen: five ticks with the page open, a model loaded and a folder
  chosen made 20 `PathRegistry.FinalPath` calls, four a tick, each a walk along a path with a file-system call per part
  on a local drive (a temporary counter, not committed; not timed). It is not in the re-lens's list and is unchanged.
- The brief's `ConfigLoader.cs:353-354` does not exist (the file has 66 lines): the PROPOSED marker went on
  `ConfigLoader.BundledFolderIgnored`, the only string that file gained in this PR.

## Runtime resilience (17/10/2026, branch fix/runtime-resilience)

Four findings of the Store certification investigation (16/10/2026; its logs `logs\p630-*` and `logs\cpu-*` and its
`harness\Program.cs` are in that session's scratchpad). "Measured here" means this lane's own runs on 17/10/2026, through a
scratch harness (`scratchpad\resil-harness`, not in the repo) that drives Dictum.Core's `ModelManager`, `LocalLlmManager`
and `TranscriptCleaner` the way the tray app does, with `LOCALAPPDATA` and `APPDATA` pointed at a scratch folder and the
models read from `%LOCALAPPDATA%\Dictum\models`. "The P630" is the Intel UHD Graphics P630 alone, selected with
`GGML_VK_VISIBLE_DEVICES=1`, so the Quadro RTX 4000 that the running copy of Dictum uses was not touched. The clip is the
investigation's 10.74 s WAV; the clean-up input is its 23-word sentence.

### F2: a model that cannot load is named

- `ModelFailure` (in `ModelManager.cs`; in its own file since the lens fixes below) turns the two messages the investigation saw in Windows Sandbox into "The Microsoft
  Visual C++ runtime is missing; install it from Microsoft, then restart Dictum" when the runtime is not on the PC:
  Whisper.net's "Failed to load native whisper library…" and LLamaSharp's failed `LLama.Native.NativeApi` type initialiser
  (both texts read in the two libraries' binaries in the NuGet cache, 17/10/2026), and a `DllNotFoundException`. The runtime
  counts as present when each of `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` and `vcomp140.dll` loads by name
  (`NativeLibrary.TryLoad`: System32, the program's folder, a packaged app's framework dependency) or sits in one of the
  program's `runtimes` folders, where `publish.ps1 -VcRuntimeDir` (fix/vc-runtime) puts copies beside the engines. The brief
  named `vcruntime140.dll`; the four come from the import tables of this build's 54 engine DLLs, read 17/10/2026 with the
  investigation's `pe-imports.ps1`: all 54 import MSVCP140 and VCRUNTIME140, 38 also VCRUNTIME140_1 and 14 (ggml's CPU and
  base builds) VCOMP140; the other 9 DLLs there are Avalonia's and import none. A native failure with the runtime present
  keeps the engine's own words, since its cause is not known.
- `LastError` carries the plain words, so the existing load balloon (`AppShell.cs`, outside this lane) shows them too. It
  still ends "(Speech model: Settings > Models)", which does not fit the runtime case: left for the owner of `AppShell.cs`.
  (Done in the lens fixes below.)
- The tray panel shows a line under its header, and About under its models line: "Speech model: …", "Clean-up model: …",
  or one "Speech and clean-up models: …" when both share the reason (a missing runtime stops both). Beside it in the panel,
  "Open log folder" (About has its own under Files) and, when it can help, "Try on the CPU". The line also explains a model
  that runs on the CPU after a GPU failure (F3); the models line then says `CPU`.
- About's clean-up line said "Clean-up: local/… on this PC (nothing leaves it)" while the local model had failed; it now
  says "Clean-up: {model} could not be loaded, so the rule-based tidy is used." until a load succeeds or starts.
- "Try on the CPU" appears when a model failed inside a GPU runtime (whisper's Vulkan runtime, llama.cpp's Vulkan build) in
  a way the CPU-only runtime might get past (not a missing engine library, model file or catalogue match) and its gpu
  setting is not `cpu`. With F3 in place that means the retry on the CPU inside that runtime has failed too (the lens
  showed that this offers the button exactly when the CPU has failed already: changed in the lens fixes below). It writes
  `whisper.gpu` and/or `llm.gpu` = `cpu` through the settings writer (`AppShell.Write`); the reload loads the model again off
  the GPU at once (`WhisperEngine.Load` now passes Whisper.net's `UseGpu = false` for `cpu`, and `LocalLlmEngine.Load` gives
  no layers to the GPU and turns op offload off), and if that fails as well the line adds "Restart Dictum to use the CPU.",
  since the CPU-only runtime loads only at a start. Where no GPU runtime was in use the button is not offered: the model
  already ran on the CPU. Measured here: `whisper.gpu = cpu` after whisper's Vulkan runtime had loaded (the P630), base.en
  "loaded in 0.4 s on runtime Vulkan, on the CPU", 2.42 s and 2.38 s for the clip.
- Proposed strings (17/10/2026, awaiting the product owner's review; each pinned word for word in `RuntimeResilienceTests`):
  - "The Microsoft Visual C++ runtime is missing; install it from Microsoft, then restart Dictum" (the brief's wording)
  - "The graphics driver could not run the model; Dictum will use the CPU" (the brief's wording)
  - "Try on the CPU" (button; the brief's name)
  - "Restart Dictum to use the CPU." (after a model's reason, see above)
  - "Speech model: …", "Clean-up model: …", "Speech and clean-up models: …" (the line's prefixes; a full stop is added when
    the reason has none)
  - "Clean-up: {model} could not be loaded, so the rule-based tidy is used." (About)
  - "Open log folder" is About's existing label, reused in the panel.
- Not done, outside this lane's files: every hotkey press still flashes "Busy" while no speech model is ready
  (`DictationController.cs`, `PillText.cs`); the problem report's "Models:" line (`AppShell.Actions.cs`) does not carry the
  reason; the balloon suffix above (done in the lens fixes below).

### F3: one more try on the CPU after a GPU failure

- `ModelManager.Load` and `LocalLlmManager.LoadAsync`: a load or warm-up that throws while the GPU is in use (whisper:
  `WhisperEngine.VulkanActive`; llama.cpp: its Vulkan build loaded) is tried once more with gpu `cpu`, unless
  `ModelFailure.WorthCpuRetry` says the CPU cannot help (a missing engine library, model file or catalogue match, a load
  stopped by quit). One warning line names the GPU failure (since the lens fixes: "failed while the GPU was in use");
  `Notice` then holds the GPU-failed sentence and `RunsOnCpu` is true. If the CPU fails too, `LastError` is the CPU's own
  failure.
- Leak fix: an engine whose warm-up throws is disposed before the failure goes on (before, `ModelManager.Load` lines
  144-146 left it loaded); `WhisperEngine.Load` also frees its factory when the processor cannot be built.
- Measured here, a GPU failure provoked with `GGML_VK_FORCE_MAX_BUFFER_SIZE=16777216`, which makes ggml-vulkan refuse any
  buffer over 16 MiB (the variable is a string in both shipped ggml-vulkan DLLs).
  `GGML_VK_FORCE_MAX_ALLOCATION_SIZE=16777216` was tried first and provoked nothing (base.en loaded and transcribed):
  - Clean-up, qwen2.5-0.5b, `llm.gpu = vulkan`, the P630: "llama_model_load: error loading model: unable to allocate
    Vulkan0 buffer", `LoadWeightsFailedException`, the warning line, then "loaded in 0.6 s on Vulkan, on the CPU", warm-up
    8.2 s, and two clean-ups by the model in 2.51 s and 1.83 s.
  - The same run with llama.cpp's default op offload: the retry, with no layers on the GPU, still asked Vulkan for its prompt
    buffers ("failed to initialize the context: failed to allocate compute pp buffers") and the process crashed (Git Bash
    reported exit status 139). So `cpu` turns op offload off (`OpOffload = false`; LLamaSharp's default is null, which keeps
    llama.cpp's own). The brief allowed it only if measured better: the retry succeeds with it off and crashes with it on.
    Speed is the same either way: qwen2.5-1.5b on the CPU inside the Vulkan build, the P630, 5.84 s and 5.21 s per clean-up
    with it off (here), 5.96 s and 5.02 s with it on (investigation). `NoKqvOffload` was not changed: with no layers on the
    GPU it has nothing to keep off.
  - Speech, base.en on the P630, the same variable: whisper.cpp stopped at `GGML_ASSERT(buffer) failed`
    (ggml-backend.cpp:194) and the process ended with 0xC0000409; no .NET exception reached Dictum. No in-process retry can
    catch that case; it needs a crash marker (an unclean exit during a GPU load, so the next start goes to the CPU), not
    built here (built in the lens fixes below). The speech retry is pinned with stand-ins only. README, Troubleshooting,
    says what to do.
- Not covered: a warm-up that fails after a decoding change (`ModelManager.Reconfigure`) is logged, as before, not retried.

### F4: the clean-up model stays off integrated graphics

- `LocalLlmEngine.ConfigureBackend`, for `llm.gpu = auto`, asks whisper's runtime which Vulkan devices it found
  (`WhisperEngine.VulkanDevicesForCleanup`, which loads that runtime if no speech model has yet) and uses llama.cpp's CPU
  build when every device is integrated graphics (`uma: 1`): `LocalLlmEngine.UseVulkan`. `vulkan` and `cpu` still force.
  `ModelManager.BeginLoad` now sets whisper's runtime order when a speech load is asked for, so the order is known when the
  clean-up load asked just after it decides. The probe never loads whisper's Vulkan runtime against `whisper.gpu = cpu`.
- Ceiling: when whisper's order is not set yet (no speech load asked for, for example a missing speech model) or is the CPU
  order, the earlier rule stands (Vulkan, then CPU), so a PC with integrated graphics, `whisper.gpu = cpu` and
  `llm.gpu = auto` still puts the clean-up model on Vulkan.
- Measured here, the P630 with base.en and qwen2.5-1.5b both on `auto`, the speech load asked first as at tray start:
  "Cleanup model backend order: CPU (llm.gpu = auto; the only GPUs Vulkan found are integrated graphics, where the CPU is
  faster)", loaded from the avx2 llama.dll in 1.9 s, warm-up 5.06 s (both passes, F5), and two clean-ups by the model in
  2.18 s and 2.14 s. On Vulkan on the same GPU: 4.22 s and 4.21 s (F5's run below) and 4.3 s (investigation), with a
  12-21 s warm-up. This PC with both GPUs visible reports a discrete device and keeps Vulkan: unchanged.

### F5: an honest speed estimate

- A model's first warm-up runs twice and the estimate keeps only the second pass's figures (`SpeedEstimate.WarmUpAsync`,
  in `LocalLlmEngine.cs`); a prefix's rate stands in for the prompt rate only when that call evaluated the prefix
  (`CleanupSession.PrefixEvaluations`). Later warm-ups (a changed prompt) run once.
- Measured here, qwen2.5-1.5b forced onto Vulkan on the P630 (`llm.gpu = vulkan`): both passes took 20.2 s; the first
  dictation after start-up was cleaned by the model in 4.22 s, and the second in 4.21 s. Before (investigation): every
  dictation was skipped as "estimated 23.9-28.9 s". The second pass measured 4.1 tokens/s writing and 5,675 tokens/s
  reading; the reading figure is not real: on Vulkan the prompt's decode returns before the GPU has finished and the time
  lands on the first generated token (the investigation's direct call logged "34 tokens in 4 ms"), so the writing rate
  carries it. Inferred, not logged: from those rates and `SpeedEstimate.Predict`'s formula, with about 26 tokens for the
  sentence, the estimate was roughly 8-9 s, over the real 4.2 s but inside its 15 s deadline, so the dictation ran.
- `LocalLlmClient`: after three estimated dictations in a row that did not run the model, the next gets no estimate, runs
  the model (its deadline still holds) and measures it again, a run stopped at its deadline included. If the model is still
  too slow the allowance doubles (3, 6, 12, then 24 at most), so a PC that really is too slow waits for a deadline less and
  less often; a dictation the estimate lets through puts it back to three. The brief asked for a decay or re-measure after N
  dictations; the doubling is this lane's addition, so that a re-measure costs a slow PC one deadline wait less and less
  often. The pill and the clean-up ask about the same dictation, the same string, which counts once; the client holds that
  string weakly. `TranscriptCleaner` is unchanged, so its skip still returns `Skipped: true`, and a hands-free part counts as
  a dictation of its own.

### Pins and mutation checks

`tests/Dictum.Tests/RuntimeResilienceTests.cs`, 22 tests: the proposed strings word for word; the two engine messages, a
nested and an aggregate form and a missing DLL named as the missing runtime, and their own words kept when the runtime is
present; the runtime counted present by loading or by copies beside the engines (one DLL missing is enough to fail); the
problem line's prefixes and its merge; on the headless platform, the tray panel naming the runtime with Open log folder and
no Try on the CPU, Try on the CPU writing `whisper.gpu = cpu` and the line then asking for a restart, no button where no GPU
runtime was in use, the CPU fallback's line and `CPU` in the models line, and About's clean-up line and problem line after
a clean-up model fails its size check (nothing native loads in any of these); the speech and clean-up retries on the CPU,
each engine whose warm-up failed released, the CPU's own failure when it fails too, no retry for a missing engine library
or with no GPU runtime in use; the integrated-graphics rule from real device lines; the warm-up's two passes, its reset
between them, the re-measure after three skips, the doubling, and the pill and the clean-up counting one dictation once.

Each mutant reverted one behaviour in the rebased head; Release `--no-incremental` build, then the whole filtered suite
(1,289 tests); the six source files restored after each, SHA-256 compared with copies taken before the first. Every
mutant was observed failing on the reverted behaviour:

| Mutant | Failed (RuntimeResilienceTests) |
|---|---|
| M1 speech CPU retry off | 5 |
| M2 an engine whose warm-up failed not disposed | 2 |
| M3 clean-up CPU retry off | 2 |
| M4 native-library failures not recognised | 3 |
| M5 copies beside the engines ignored | 1 |
| M6 `auto` keeps Vulkan on integrated graphics | 1 |
| M7 the first warm-up runs once | 2 |
| M8 the first pass's figures kept | 1 |
| M9 a skipping estimate never re-measured | 2 |
| M10 the allowance never doubles | 1 |
| M11 the pill and the clean-up counted separately | 1 |
| M12 Try on the CPU writes nothing | 1 |
| M13 About keeps "on this PC" for a failed clean-up model | 1 |
| M14 the tray panel hides the problem line | 3 |
| M15 a shared reason not merged | 1 |
| M16 About hides the problem line | 1 |
| M17 the models line keeps the GPU runtime's name | 1 |
| M18 the speech restart hint never set | 2 |
| M19 Try on the CPU offered with no GPU runtime in use | 2 |
| M20 the clean-up restart hint never set | 1 |
| M21 the problem line drops the restart hint | 1 |

Not caused by these changes: in 12 of the 21 mutated runs, and in 2 of 3 unmutated full runs of the rebased head,
`FixBatchPrivacyTests.A_request_to_this_machine_never_goes_through_a_configured_proxy(host: "localhost")` failed too
(expected 0 connections to its stand-in proxy, saw 1), the failure the hands-free lane traced to the test host's own
telemetry reaching the process-wide proxy that test sets. Those runs had no telemetry opt-out in their environment; with
`DOTNET_CLI_TELEMETRY_OPTOUT=1` and `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` (CI sets the first) 2 of 2 full runs passed
it. Two runs prove little, and the mechanism is the hands-free lane's finding, not re-traced here; both variable names
are strings in the test output's `Microsoft.Testing.Platform.dll` (read 17/10/2026). The runs without the opt-out may have
let the test host try to connect out. Not mutation-checked, because
only a real model exercises them: `WhisperEngine.Load`'s `UseGpu`, `LocalLlmEngine.Load`'s `OpOffload`, and
`WhisperEngine.VulkanDevicesForCleanup` with `ModelManager.BeginLoad` setting whisper's order early (the measured runs above
exercise them).

**Real runs** (17/10/2026, this PC, harness as above; one model per run unless named): F2 one (whisper.gpu `cpu` inside
the Vulkan runtime); F3 five (the allocation-size variable that provoked nothing, the clean-up retry with op offload off,
the same with it on, the speech crash, and that crash again under PowerShell for its exit code and stderr; three more than the
brief's one or two, to find a variable that provokes a failure and to measure op offload as the brief asked); op offload's
speed one (qwen2.5-1.5b on the CPU inside the Vulkan build); F4 one (base.en with qwen2.5-1.5b); F5 one. Each ran once;
none was looped.

**Verification** (17/10/2026, this PC, rebased on main 23acf35): Release `--no-incremental` build, 0 warnings, 0 errors.
Filtered suite (`FullyQualifiedName!~Install_copies_models`), telemetry opted out as above: 1,289 total, 1,270 passed, 19
skipped, 0 failed (the 22 new tests included), twice. Before the rebase, on c0aa989: 1,130 total, 1,111
passed, 19 skipped, 0 failed without these changes; 1,150, 1,131, 19, 0 with the first 20 of the new tests. Not run: the
desktop-, tray- and model-gated tests (the 19 skipped), Dictum.exe itself (the running copy was left alone), and any run
on a PC without the Visual C++ runtime (the investigation's Windows Sandbox evidence stands in for it).

### Lens fixes (17/10/2026, the opus lens on ece0d1c: Needs changes, 3 majors and 6 minors)

The orchestrator's decision on the lens's open question: a start after the GPU ended Dictum switches that model to the CPU
automatically, with no click, and says so. The lens's probes and its real run R1 (a base.en cut to its first 60 MiB,
through the production loader on the P630, `whisper.gpu = auto`) are in that session's scratchpad (`lens-res`), not here.

- **The GPU ending Dictum (major).** whisper.cpp ends the process when the GPU refuses a model's memory (F3's run above:
  "ggml_vulkan: Device memory allocation of size 147365888 failed … ErrorOutOfDeviceMemory … GGML_ASSERT(buffer) failed",
  exit 0xC0000409), so the next start tried the GPU again and ended again, at every sign-in. Any discrete GPU counts as a
  GPU machine whatever its memory, and a GPU machine is recommended medium.en (1,533,774,781 bytes) at the first run and
  given large-v3-turbo by Full, so a small card meets this. `GpuLoadMarker` (new, `Transcribe/GpuLoadMarker.cs`): each
  manager writes `gpu-load-speech.txt` or `gpu-load-cleanup.txt` naming the model (its full path) just before a load
  with its gpu setting not `cpu` (the load and its warm-up), and deletes it in a finally around that attempt and at
  Dispose (only a file this run wrote: quitting is not the GPU ending Dictum). The first load of a run reads the file:
  when it names that model and its gpu setting is not `cpu`, the model loads with `cpu` for the rest of the run (speech:
  `ConfigureRuntime("cpu")` before the load, so whisper's Vulkan runtime is never loaded; clean-up: `LocalLlmEngine.Load`
  with `cpu`, whose first call sets llama.cpp's CPU build), `Notice` is the new proposed line "The graphics driver could
  not run the model last time; Dictum is using the CPU", and the file stays, so starts do not alternate between the GPU
  and the CPU. Another model at a first load tries the GPU: its own GPU load replaces the file, and its finally deletes it.
  The folder: both managers already hold `ModelIntegrity`, whose `verified-models.json` lives in `AppPaths.LocalDir` (under
  MSIX the package's LocalCache), so the marker sits beside it (`ModelIntegrity.Folder`) and `AppShell.cs` needed no change
  for it. `AppPaths` names both files and `PathRegistry` lists them under `ModelVerification`, so Delete all app data and
  Remove Dictum completely delete them; StorageTests writes them through `GpuLoadMarker`. Limits: a load and its warm-up
  are covered, not a dictation or a decoding change on the GPU after the load (the `// simplification:` note in
  `ModelManager.Load`); and when something set whisper's runtime order before the first speech load (the first-run probe,
  or the clean-up repair's probe when `llm.model` is not valid), `ConfigureRuntime("cpu")` changes nothing and the model
  loads on the CPU inside the Vulkan runtime (Whisper.net's `UseGpu = false`; R1's log shows `use gpu = 0` and
  "CPU total size = 147.37 MB" for that path).
- **Try on the CPU's gating (major).** `CpuMightHelp` came from the CPU retry's own failure, so the button appeared
  exactly when the CPU had failed already: in R1 the GPU and then the CPU failed ("ERROR not all tensors loaded from model
  file - expected 245, got 14" both times), `CpuMightHelp` was true, and pressing the button would repeat that attempt and
  write `whisper.gpu = cpu` for good. Now the retry's failure is marked (`ModelFailure.CpuTried`, in the exception's
  `Data`) and `ModelFailure.Assess` offers the button only when no retry on the CPU ran for that failure; the line shows
  the CPU attempt's error, with Open log folder. The case left: a start that loaded the model with `cpu` because of the
  marker, inside a GPU runtime set before it, where that load fails: the CPU-only runtime has not been tried, so the
  button is offered, once (pressing it writes `cpu`; the reload's failure then asks for a restart). In production a
  marker start sets the CPU-only order itself, so that case needs one of the earlier probes above. The warning line says
  "failed while the GPU was in use", since the GPU is not known to be the cause (R1's was the file).
- **About's clean-up line (major, a regression).** For text that is not English the cleaner in use is SPEC §23's plain
  one (whitespace only), and with a failed local model About said "Clean-up: {model} could not be loaded, so the
  rule-based tidy is used." The line now also needs `!Shell.Cleaner.IsPlain`, so the plain cleaner's own status (it says
  "whitespace tidied only") stays, as on main (the lens's P5b passes on main 23acf35 and failed on ece0d1c).
- **Pins the lens found missing (minor).** A later model that loads on the GPU clears the GPU-failed notice (the lens's
  P1); About offers Try on the CPU when the panel does (P2, now on the marker start, the one case left that offers it);
  the clean-up models line says `CPU` after the retry, through a new `cleanupModels` parameter on `AppShell` like
  `speechModels` (`AppShell.cs`: a doc line, the signature and line 81); op offload off only for `cpu`, through
  `LocalLlmEngine.Parameters(gpuSetting)`, which returns the two values (layers, op offload) rather than LLamaSharp's
  `ModelParams`: constructing a `ModelParams` loads llama.cpp's native library (seen in this lane's test host,
  17/10/2026: the Vulkan build's ggml-base, ggml-vulkan, ggml and llama DLLs and the avx2 ggml-cpu appeared among the
  process's modules right after it, with no model), so a pin on it would load the GPU stack in every test run; and the
  clean-up's device probe never loads whisper's runtime against `whisper.gpu = cpu` (`WhisperEngine.ProbeAllowed`).
- **Simplification notes (minor)**, each with its ceiling and trigger: `WhisperEngine.VulkanDevicesForCleanup` and
  `LocalLlmEngine.ConfigureBackend` (with `whisper.gpu = cpu`, a marker start or no speech load yet, `auto` keeps Vulkan on
  a PC with integrated graphics only; Try on the CPU makes that likelier); `BenchCleanupCommand.RunOneAsync` (the bench
  never sets whisper's order, so it times Vulkan there; the note only, the file is outside this lane); `UseVulkan` (one
  integrated GPU measured decides for all); `LocalLlmClient` (the pill and the clean-up matched by string instance);
  `ModelManager.Load` (no retry survives a native abort; the marker covers loads only); `WhisperEngine.GpuMachine` (below).
  README's `llm.gpu` row names the exceptions.
- **Keyboard focus (minor).** Try on the CPU hides itself and its line while the reload loads the model, and focus went
  nowhere (the lens's headless probe P3). Pressing it now moves focus first to a control that stays: the tray panel's
  Dictation on switch, and in About the next control, where Tab would go (`KeyboardNavigationHandler.GetNext`: Check for
  updates in the download build, Report a problem in the Store build), both with `NavigationMethod.Tab`, and only when
  the button had focus. The brief offered About's heading; a headless probe (17/10/2026) found that a TextBlock given
  focus gets `:focus-visible` but no `FocusAdorner`, and Dictum's own styles (`Theme/Controls.axaml`) have no focus visual
  for it, so nothing would show a sighted keyboard user where focus went; a button has the Fluent theme's keyboard focus
  visual (recalled, not looked at here). The pins check `:focus-visible` on the control focus lands on; no ring was looked
  at (headless, no screenshots).
- **Structure (minor).** `ModelFailure` moved to its own file; one `ModelFailure.Assess(error, gpuInUse, gpuSetting,
  runtimePresent)` gives both managers `LastError`, `CpuMightHelp` and `RestartForCpu` (each manager keeps one
  `Assessment` field behind its three properties); one `ModelFailure.RetryOnCpu` holds the retry rule both managers used;
  `ModelManager`'s `warmUp` parameter is `gpuInUse`, as `LocalLlmManager`'s (13 callers in 6 other test files renamed).
- **Runtime check (minor).** `VisualCppRuntimePresent()` is true off Windows (Dictum.Core also targets macOS, where no such
  runtime exists), and an `msvcp140.dll` older than 14.51 counts as missing: the floor is a copy of `$MinVcRuntime` in
  `scripts/build-common.ps1` (on main since PR #10, 6daff77, read 17/10/2026), which records Microsoft's rule and the
  newest linker among the native DLLs. The version is read from the file the process loads by name, or from a copy beside
  the engines; a version that cannot be read counts as present, as before. A test holds the floor and the four DLL names
  equal to the script's, read through PR #10's own `VcRuntimeLayout`. Only `msvcp140.dll`'s version is weighed, as the
  brief asked.
- **One-line gaps (minor).** The load balloon (`AppShell.cs` line 200, one line, with `AppShell.LoadBalloon` in
  `AppShell.Status.cs`) drops "(Speech model: Settings > Models)" for a missing runtime, which no model choice fixes, and
  shows it once when both models carry it. `GpuMachine` is unchanged: it is a fact about the runtime, so after the retry
  on the CPU (Vulkan still loaded) Full still offers large-v3-turbo; a marker start sets the CPU-only order, which already
  reads as a CPU machine. The fix is one line in `AppShell.cs` (line 77: `_gpuMachine = gpuMachine ?? (() =>
  WhisperEngine.GpuMachine && !_models.RunsOnCpu);`), outside this lane's files, so it is a `// simplification:` note on
  `GpuMachine` and is left for the owner of `AppShell.cs`.
- Proposed string (17/10/2026, awaiting the product owner's review; pinned word for word in `RuntimeResilienceTests`):
  "The graphics driver could not run the model last time; Dictum is using the CPU" (the brief's wording). No other
  user-facing copy is new; log lines: "… failed while the GPU was in use (…); loading it again on the CPU." and
  `GpuLoadMarker`'s warnings (a marker that names the model, or that could not be read, written or deleted).

**Real runs (lens fixes)**, 17/10/2026, this PC, two runs, each once: the P630 alone (`GGML_VK_VISIBLE_DEVICES=1`) with
`GGML_VK_FORCE_MAX_BUFFER_SIZE=16777216` as in F3, base.en read from `%LOCALAPPDATA%\Dictum\models`, `LOCALAPPDATA` and
`APPDATA` pointed at a scratch profile, the marker in a scratch folder; the scratch harness's new `marker` mode drives
`ModelManager` with the production loader and a `ModelIntegrity` on that folder, as the tray app does.

- Run 1: the marker named the model during the GPU load, then "alloc_tensor_range: failed to allocate Vulkan0 buffer of
  size 147365888" and whisper.cpp ended the process at `GGML_ASSERT(buffer) failed` (stderr the same three lines as F3's
  run; Git Bash reported exit status 127, and the Windows exit code was not captured). The marker was left, naming
  base.en.
- Run 2, the same command: "gpu-load-speech.txt names ggml-base.en.bin: the GPU ended Dictum while it loaded that model,
  so it loads on the CPU this time.", "Whisper runtime order: Cpu -> CpuNoAvx (whisper.gpu = cpu)", one ggml backend
  (whisper's Vulkan runtime not loaded), "loaded in 0.4 s on runtime CPU", `RunsOnCpu` true, `Notice` the new line,
  `CpuMightHelp` false, `GpuMachine` false; the 10.74 s clip transcribed in 2.31 s; the marker still there after the load
  and after quit. Nothing was written under the scratch profile.

Not intended, and recorded here: before `LocalLlmEngine.Parameters` returned two values, its pin built a `ModelParams`,
which loaded llama.cpp's Vulkan build DLLs in the test host, without `GGML_VK_VISIBLE_DEVICES` and without a model, in
four test-host runs on 17/10/2026 (two runs of the 49 related tests, the first full suite, and the probe that found it).
Whether ggml-vulkan enumerated the Quadro then was not checked; the running copy of Dictum was not touched otherwise.

**Pins and mutation checks (lens fixes).** `tests/Dictum.Tests/RuntimeResilienceTests.Marker.cs`, 22 tests: the marker
files are the path registry's; for each engine, a start whose marker names the model asks the loader for the CPU only and
keeps the marker, the marker names the model during a GPU attempt and is gone after it (for speech also after a failed
one, before the retry), and a marker naming another model leaves this one to the GPU; quitting removes the marker of an
attempt under way and never one read at the start; the clean-up's GPU and CPU failures leave the CPU's error, no button
and the new log wording; a marker start that fails inside a GPU runtime offers Try on the CPU once; About's line for the
plain clean-up (the lens's P5b); the lens's P1 and P2; the clean-up label after the retry (through `cleanupModels`); op offload; the
device probe; keyboard focus in the panel and About, with `:focus-visible`; the version floor with stand-in probes; the
floor and DLL names held to `scripts/build-common.ps1`; the load balloon's words and its one balloon for both models.
Changed in `RuntimeResilienceTests.cs`: the GPU-failed-last-time line pinned word for word, the R1 shape in the panel
(replacing the test that expected the button after both failures), `CpuMightHelp` false after the CPU failed, the new log
wording, the runtime check's new probe signature. `StorageTests` writes both markers through `GpuLoadMarker`.

Each mutant reverted one behaviour; Release `--no-incremental` build, then the whole filtered suite with the telemetry
opt-out; the original bytes written back and their SHA-256 compared after each (scratch runner `runmut.js`). First pass on
a719e5e (before the rebase, 1,311 tests), final pass on fe052d3 (after it, 1,341 tests). Every mutant was observed failing
on the reverted behaviour in each pass it ran in, and every source was restored byte for byte:

| Mutant | Reverts | Failed, a719e5e | Failed, fe052d3 |
|---|---|---|---|
| N1 | speech: the marker is never read at a start | 6 | 6 |
| N2 | clean-up: the marker is never read at a start | 1 | 1 |
| N3 | speech: no marker during a GPU attempt | 3 | 3 |
| N4 | speech: the marker is left after a GPU attempt | 2 | 2 |
| N5 | clean-up: no marker during a GPU attempt | 1 | 1 |
| N6 | clean-up: the marker is left after a GPU attempt | 1 | 1 |
| N7 | a marker naming another model forces the CPU too | 2 | 2 |
| N8 | quitting leaves the marker of a GPU attempt under way | 1 | 1 |
| N9 | quitting also deletes a marker read at the start | 1 | 1 |
| N10 | speech: the CPU retry's failure is not marked | 2 | 2 |
| N11 | clean-up: the CPU retry's failure is not marked | 1 | 1 |
| N12 | Assess ignores a CPU try | 3 | 3 |
| N13 | the marker start's CPU load counts as a CPU try (no button) | 4 | 4 |
| N14 | speech: the warning says the GPU failed | 1 | 1 |
| N15 | clean-up: the warning says the GPU failed | 1 | 1 |
| N16 | About's failed-model line replaces the plain clean-up's | 1 | 1 |
| M10 | a later GPU load keeps the GPU-failed notice | 1 | 1 |
| M14 | About hides Try on the CPU | 2 | 2 |
| M13 | the clean-up label keeps the build's name after the retry | 1 | 1 |
| M11 | op offload off on the GPU too | 1 | 1 |
| M12 | the clean-up probe loads whisper's runtime against whisper.gpu = cpu | 1 | 1 |
| N17 | the tray panel leaves focus on the hidden button | 1 | 1 |
| N18 | About leaves focus on the hidden button | 1 | 1 |
| N19 | an old msvcp140 counts as present | 1 | 1 |
| N20 | the runtime balloon keeps the model-page hint | 2 | 2 |
| N21 | a balloon per model for the same runtime reason | 2 | 2 |
| N22 | the path registry misses the markers | 1 | 1 |
| N23 | the runtime floor drifts from the build scripts (14.44) | (new) | 2 |
| N24 | the runtime check forgets vcomp140.dll | (new) | 2 |

N18 reverted About's move to the heading in the first pass and the move to the next control in the final pass (see
"Keyboard focus" above). No run in either pass failed `FixBatchPrivacyTests`' localhost proxy case.

**Verification (lens fixes)** (17/10/2026, this PC): Release `--no-incremental` build, 0 warnings, 0 errors. Filtered
suite with the telemetry opt-out: on a719e5e 1,311 total, 1,291 passed, 20 skipped, 0 failed (the 20th skip was the
floor guard, waiting for PR #10). After the rebase on main 6daff77, on fe052d3, twice: 1,341 total, 1,320 passed, 21
skipped, 0 failed; and 1,341, 1,319, 21, 1 failed: `OfflineAndDownloadTests.A_slow_but_live_connection_is_not_a_stall`
expected one download attempt and saw two. Inferred, not traced: its 150 ms stall limit tripped under the suite's
parallel load; the download code and that test are not in this lane's diff, and the test passed in the other run and in
all 29 mutated runs of the final pass. The floor guard runs on this head. The 21 skipped are gated: published layouts,
the download measurement, the desktop, tray and setup tests, and the real-model integration tests. Not run: those,
Dictum.exe itself, a PC without the Visual C++ runtime or with one older than 14.51, macOS (no test exercises the
`OperatingSystem.IsWindows()` guard), the clean-up side of the marker with a real llama.cpp end, and a look at the focus
rings (headless only).

## Runtime resilience: PR #12 merged, and its lens chain (17/10/2026)

PR #12 (head 246e605) merged into main as ba2ce5f at 23:53 UTC on 16/10/2026, which is 10:53 AEDT on 17/10/2026 (GitHub `mergedAt`, read 17/10/2026). The full record is the "Runtime resilience" section above, whose "Lens fixes" parts hold the tables; this section keeps the chain of what each lens changed. The PR's own title on GitHub is "Model failures named in the UI, CPU retry after a Vulkan failure, clean-up on the CPU with integrated graphics, honest speed estimate".

- **Lens on ece0d1c** (opus, Needs changes: 3 majors, 6 minors): the GPU-load marker (`gpu-load-speech.txt` / `gpu-load-cleanup.txt`) so a GPU that ends Dictum is not tried again at the next start; Try on the CPU offered only where it can help; About's clean-up line right again for text that is not English; focus after Try on the CPU; the Visual C++ runtime floor (`msvcp140.dll` older than 14.51 counts as missing); `ModelFailure` in its own file with one `Assess` and one retry rule.
- **Second lens on ae959a3** (Needs changes), fixed in 3f86820:
  - **Shared GPU-load gate.** `GpuLoadMarker` takes one gate per marker folder, shared by both managers and held from `Begin` to `End`, so start-up GPU loads (warm-up included) run one after the other and at most one marker is written by a run. Moving the marker inside `NativeLoadGate` does not hold, because that gate wraps only the native load call. The ceiling is a `// simplification:` note: start-up time is the sum of both GPU loads.
  - **Auto-only CPU pin.** `CpuForThisRun` applies only when the setting is `auto`, so a hand-set `vulkan` always tries the GPU.
  - **`isWindows` parameter.** `VisualCppRuntimePresent` has a testable overload taking `isWindows`; the public one passes `OperatingSystem.IsWindows()`, so the off-Windows guard is pinned.
  - **PROPOSED copy.** "The graphics driver could not run this model, so Dictum uses the CPU for it." replaces "… could not run the model last time; Dictum is using the CPU", which was untrue from the second start on. It is a string pin only (not mutation-checked).
- **Third lens on 3f86820** (Needs changes, four minors), fixed in 5ec3b61:
  - **Docs.** README and SPEC now say a run writes at most one marker at a time and that a file left by an earlier start can sit beside it.
  - **Auto-only pins.** Each half of the auto-only rule has its own test; each was observed failing with its line reverted alone.
  - **Superseded warm-up cancellation.** `LocalLlmManager` gives each load a cancellation that the next `BeginLoad` or `Unload` fires, so a stale clean-up warm-up releases the shared gate and speech no longer waits behind it. `Begin` moved inside the try whose `finally` calls `End`.
- **Fourth lens on 5ec3b61:** one missing pin (major), for `stale.Cancel()` in `BeginLoad`, observed failing with the line commented out; a corrected ceiling comment (c8536c1).
- **Fifth lens on c8536c1:** a race in that new test (the loader returned the disposed first engine, and the test could proceed before the warm-up started); fixed by a fresh engine per call and a wait on a `WarmingUp` flag, in both gate tests (246e605). Test-only; five of five repeat runs passed.
- **Strings.** Every user-facing string of this PR, the CPU-fallback line above included, stays PROPOSED pending the product owner's approval (the list under "Proposed strings" in the PR body and in the section above). Nothing here records an approval.
- **Verification, from the PR body (not re-run for this record):** full suite on the final head, excluding `Install_copies_models`: 1,476 total, 1,455 passed, 21 skipped, 0 failed; `RuntimeResilienceTests` 50 of 50 on 5ec3b61; 51 tests on 246e605 (not re-run here). Not verified by the PR: a real-GPU run of the stale warm-up path, and an exception thrown from `Begin` itself.
- **Not done (outside the PR's files):** Full still offers large-v3-turbo after an in-run CPU retry (`WhisperEngine.GpuMachine` describes the runtime; the fix is one line at `AppShell.cs:78`; the ceiling is a `// simplification:` note at `WhisperEngine.cs:210`); `--bench-cleanup` times Vulkan on integrated graphics; every hotkey press flashes "Busy" while no speech model is ready; the problem report's "Models:" line does not carry the reason; the marker covers a load and its warm-up, not the GPU ending Dictum during a later dictation.

## Model choice never silently cancels a download (Store certification fix, 19/10/2026, branch fix/no-silent-cancel)

**The failure.** On 19/10/2026 the Store submission failed policy 10.1.2.10 with the tester's note, as relayed to this lane by the orchestrator (the report itself was not read here): "Product shows message download cancelled when trying to download any model."

**Root cause** (read in the code at main 2acceb4 and pinned by the tests below, each observed failing on the old behaviour):

- `ModelCoordinator.Select` and `SelectCleanup` called `CancelThenAsync` whenever any download was running or queued. That cancelled it (`ModelDownloads.Cancel`, balloon "Download of X cancelled.", `.part` deleted) before the new model was even asked about; declining the new question left only the cancellation.
- `ApplyWithDownloads` with nothing missing (a Mode preset whose models are all on this PC) also cancelled, with no question. Pressing the Mode segment that was already checked re-ran the preset (`Segments` re-ran its action), and the 1.1.0 defaults match the Light preset, so one press on Light cancelled a download.
- The UI invited the retry that cancels: Settings > Models showed the model being downloaded as "Not downloaded" with **Download…** enabled, and the tray panel's picker list was reset on every progress refresh.
- DECISIONS ("Model lifecycle") said a Mode preset waits behind a running download; that was true only when the preset needed a download (corrected there).

**Decision (the orchestrator's, implemented as given):**

1. Choosing the model whose file is downloading or waiting its turn cancels nothing: a balloon says "<name> is already downloading (<n>%)." or "<name> is already waiting to download.", and it becomes the latest choice again (see the rule below), so it is switched to when it arrives, also when the download was started by calibration.
2. Choosing another model that needs a download while one runs asks first, with the offline, low-space and login checks still before the question. The question says what is downloading and how far it has got, and offers **Download after it** (default; queued through `ModelDownloads.Start`, so the running download keeps its bytes), **Stop it and download <new>** (the only path that cancels, after the answer; until the lens of 19/10/2026 it read "Stop <running> and download <new>") and **Cancel** (nothing changes; Esc and closing the window mean Cancel). It reuses the one question window (`IShellDialogs.Ask`, `QuestionWindow`) through a new `IModelPrompts.ConfirmDownloadWhileBusy`; there is no second dialog implementation.
3. Choosing a model already on this PC switches to it at once; the running download carries on.
4. `ApplyWithDownloads` with nothing missing applies its values without cancelling anything. Pressing the segment that is already checked does nothing (it stays checked). Every `Segments` caller was checked: the Mode segments (tray panel and Settings > Mode), the big recording indicator (tray panel and Settings > General), the profanity filter, and the hands-free presets. For each, pressing the chosen segment again would only rewrite the value already in force; for the hands-free presets, a typed value still waiting is flushed when focus leaves the box, which should happen on the press, before the click (inferred from how the box flushes on LostFocus; not tested).
5. `ModelDownloads.Cancel` takes a reason (`DownloadCancelReason`): the Cancel download link keeps "Download of <file> cancelled."; the Stop answer says "Download of <file> stopped: you chose <name>."; quit and Remove Dictum completely only log. The log lines are kept (the Stop one adds which model was chosen).
6. Settings > Models: the row whose file is downloading shows "Downloading <n>%" (or "Waiting to download") and its button, disabled, reads "Downloading…" (or "Waiting…") with the accessible name "<name> is already downloading" (or "…is already waiting to download"); the other rows stay usable. The rows are rebuilt only when a download starts, ends or joins the queue; a progress tick only rewrites the downloading row's percentage, so keyboard focus on another row's button is not lost on every tick. Tray panel: the entry carries "(downloading)" or "(waiting to download)", picking it triggers rule 1, and the picker lists are replaced only when their entries change, so an open drop-down is no longer reset by progress ticks.

**Last choice wins (the rule implemented for rule 3).** `ModelCoordinator` keeps the latest choice of each kind (speech; clean-up) as a token: a choice that downloads holds its destination's full path, and every written choice (`TryWrite` of `whisper.model`, or of `llm.provider` / `llm.model`, from any page) holds a fresh marker. A download's switch runs only while its token is still the latest of its kind; otherwise the log says "<name> arrived, but a later model choice stands, so Dictum does not switch to it." and the "Downloaded and verified" balloon still shows. A Mode preset that needed downloads is one choice: if a later choice of either kind it sets is made before its files arrive, it writes nothing and tells its caller false. The first run's clean-up download loads its model on arrival only while it is still the configured clean-up model. A hand edit of settings.json is not seen by this rule (the arriving download would still switch).

**Ceilings marked `// simplification:`** `CancelThenAsync`: the Stop answer also drops any download waiting behind the running one (each logged, none ballooned), so a model queued with Download after it is lost when a third choice answers Stop.

**PROPOSED strings, for the product owner's approval (none is approved; do not treat them as final copy):**

- Balloon: "<name> is already downloading (<n>%)." (the percentage reads "<n> MB" when the file's size is unknown)
- Balloon: "<name> is already waiting to download."
- Balloon: "Download of <file> stopped: you chose <name>."
- Question text: "<running> is downloading (<n>% done). Download <file> (<size>) from huggingface.co after it, or stop <running> and download <name> now? Stopping deletes what <running> has downloaded so far." followed by a blank line and the existing note and checks sentence of the download question.
- Question buttons: "Download after it", "Stop it and download <name>", "Cancel" (the existing word). (Changed by the lens of 19/10/2026 from "Stop <running> and download <name>": the long form pushed Cancel off the window.)
- Settings > Models row state: "Downloading <n>%" (or "Downloading" before the first figure), "Waiting to download".
- Settings > Models disabled buttons: "Downloading…", "Waiting…"; accessible names "<name> is already downloading", "<name> is already waiting to download".
- Tray pickers: " (downloading)", " (waiting to download)".

**Pins** (`tests/Dictum.Tests/NoSilentCancelTests.cs`, two classes; real catalogue entries, downloads from an in-memory server that holds each file until released, so no real download is made; `ModelCoordinator.TestSpecs`, internal, swaps the catalogue's size and SHA-256 for ones that server can serve). Each was observed failing with its mutation applied and passing without it; the PR body has the table.

**Not verified:** the change on a Store build or on the tester's PC; a real download over the network; screen-reader output for the disabled buttons (the accessible names are pinned, not heard); the question window's layout with the longest button label at 100 % and 150 % scaling.

### Lens fixes (19/10/2026), first lens on ada6b37

The first lens on PR #15 (head ada6b37, which adds "Version 1.1.1" on top of ab06076) returned Block with one blocker, two majors and seven minors. All ten are fixed on the same branch:

1. **Blocker: the question's buttons left the window.** The three buttons sat in a non-wrapping row in the 520-DIP question window, and the long Stop label pushed Cancel partly or wholly off it (measured by the lens, headless at 100 %). Both remedies the orchestrator chose are in: the Stop label is now "Stop it and download <name>" (PROPOSED; the text above the buttons already names the running model), and every dialog's button row (`DialogWindow.Compose`) is a right-aligned `WrapPanel`, so a row that does not fit wraps instead of clipping. The tab order is unchanged and the default button is still first and focused. A headless layout pin checks the longest speech and clean-up pairs.
2. **Major: a preset or calibration download of a file already on its way downloaded it twice.** `ApplyWithDownloads` now leaves a file that is downloading or waiting out of the question's names, its total and the space check. `DownloadInTurn` waits for that download (`AlsoOnArrival` with an on-failure hook) instead of starting a second one, and skips a file that arrived meanwhile. When every missing file is already on its way, nothing is asked, and the "already downloading" or "already waiting" balloon says so for each.
3. **Major: the space check ignored the downloads already under way.** `ModelDownloads.PendingBytes(folder)` is the bytes the running and waiting downloads on that drive will still write: each one's size less its `.part`. `RefuseLowSpace` and the 2 GB warning count it. A queued download is checked again when its turn comes (Decision 15: "again before it starts"). If it is refused there, the existing low-space balloon shows and its failure path runs. Two new PROPOSED variants are used only when other downloads are pending:
   - "Not enough space for <file> (<size>): <free> is free on that drive, the downloads already under way need <pending> of it, and Dictum keeps 200 MB spare. Free some space, or choose another model folder in Settings > Models."
   - "Warning: only <free> is free on that drive, not enough for this download beside the downloads already under way".

   The existing strings are unchanged when nothing is pending.
4. **Repeat picks stacked arrival actions.** `AlsoOnArrival` takes a key and ignores a repeat.
5. **Stop could cancel a download the question did not name.** The running destination is captured before asking. If another download has taken its place by the time Stop is answered, nothing is cancelled and the chosen model waits its turn.
6. **A preset overtaken halfway kept downloading.** Before each further file, the chain checks the preset's tokens. If they are stale, it logs, does not download the rest, and tells its caller false.
7. **The microphone picker was still replaced on every refresh.** It uses the same compare-before-set as the model pickers (`SetItems`).
8. **No pin for the pickers.** A UI test opens each tray picker (speech, clean-up, microphone) mid-download, sends a status refresh, and checks it is the same list and still open.
9. **"Add AI clean-up" stayed enabled while its model downloaded.** It is now disabled with the accessible name "<name> is already downloading" (or "…waiting to download"), as the rows are.
10. **STORE.md** says version 1.1.1 / package 1.1.1.0.

PROPOSED strings added or changed by these fixes: the Stop label above, and the two pending-space variants. Nothing is approved.

### Lens fixes (19/10/2026), second lens on c5f6ebe

The second lens returned Needs changes (no blockers). All five findings are fixed:

1. **Missing pin for waiting on a file already on its way** (`DownloadInTurn`'s `IsFetching` branch). Two new pins:
   - small.en is downloading and Full is pressed: the server sees ggml-small.en.bin exactly once.
   - A preset waiting on a file already downloading, when Cancel download is pressed, is told false exactly once.
2. **Stop was never offered when only Stop makes room.** A choice is now refused before asking only when the file would not fit even with the running download stopped (`PendingBytes(folder, exceptRunning: true)`). When it fits only that way, the question offers just **Stop it and download <name>** and **Cancel**, and Enter presses Cancel (my choice: the safe answer is the default). The body then reads (PROPOSED): "<running> is downloading (<n>% done). There is not enough space to download it after <running>. Stop <running> and download <file> (<size>) from huggingface.co now? Stopping deletes what <running> has downloaded so far." That is followed by the usual note and checks sentence. Its 2 GB warning counts the waiting downloads but not the running one.
3. **One owner's action that threw skipped the others.** `ModelDownloads.Report` now runs the download's own action and each other owner's action in its own try. On the failure path, a throw is logged as "After the download of <file> did not complete, what was waiting on it failed: …", with no "downloaded and verified" balloon.
4. **Pins for the pending space.** New tests cover:
   - the 2 GB warning counting the running download;
   - `PendingBytes` subtracting a kept `.part` and counting only the same drive root;
   - both PROPOSED `DiskSpace` variants and their thresholds.
5. **STORE.md tester notes.** The header now reads "Notes written 19/10/2026 for Dictum 1.1.1.", the CHANGED paragraph is rewritten, and the 19/10/2026 failure is added to the certification record. **PROPOSED** tester-notes paragraph, for the product owner to approve before it is pasted into Partner Center:

   > CHANGED SINCE THE LAST SUBMISSION: no download is needed to test. The base.en speech model ships inside the package, so dictation works as soon as Dictum starts; larger speech models and AI clean-up are optional downloads. The last submission reported "download cancelled" when downloading a model: choosing a second model while one was downloading cancelled the first. Dictum 1.1.1 never cancels a download unless you press Cancel download, answer "Stop it and download …" to the question it now asks first, or quit Dictum; choosing the model that is already downloading only says so.

   The orchestrator asked for "every feature works with no download". The paragraph instead says dictation works with no download and that larger models and AI clean-up are optional downloads. AI clean-up and the multilingual models do need a download, so "every feature" would not be accurate.

## Launch crash without a working Vulkan driver (live incident, 21/10/2026, branch fix/no-vulkan-crash)

**The failure.** On any PC where Whisper.net cannot load its Vulkan runtime (no Vulkan loader, or any failure in the
Vulkan DLL graph), Dictum 1.1.1 ends at launch with `ggml.cpp:22: GGML_ASSERT(prev != ggml_uncaught_exception) failed`,
exit code 0xC0000409, before the tray icon appears. It happens at every start: the crash comes before settings.json is
written, so every start is a first run again.

**Root cause.** An Opus investigation reproduced it on 21/10/2026 in Windows Sandbox with System32\vulkan-1*.dll renamed.
Its findings, as relayed to this lane (the parts this lane re-checked are marked):

- Whisper.net 1.9.1's `NativeLibraryLoader` loads ggml-base-whisper, ggml-cpu-whisper and ggml-vulkan-whisper one by
  one. When one fails, it frees (`NativeLibrary.Free`) the ones already loaded.
- Its runtime search list holds the same `runtimes\vulkan\win-x64` folder twice: once from `AppDomain.BaseDirectory`,
  which ends in a separator, and once from the assembly or argv folder, which does not, so `Distinct` keeps both. The
  second try maps ggml-base-whisper.dll again at the same address.
- ggml's static initialiser (ggml.cpp) then finds its own stale `ggml_uncaught_exception` as the previous terminate
  handler, and the `GGML_ASSERT` aborts the process.
- The initialiser is skipped when the C runtime's `getenv("GGML_NO_BACKTRACE")` returns a value. ggml's backtrace printer
  is "platform not supported" on Windows in this ggml, so skipping it loses nothing. Re-checked here only as far as the
  DLLs go: the shipped ggml-base DLLs (whisper's three, and LLamaSharp's) contain the `GGML_NO_BACKTRACE` string and no
  backtrace text.
- `Environment.SetEnvironmentVariable` alone did not stop the crash in the investigation (the C runtime keeps its own
  copy of the environment); P/Invoke `_putenv_s` in ucrtbase.dll did (exit 0, CPU runtime).
- Separately, `ModelCoordinator.FirstRun` probed the runtime with `new WhisperSettings().Gpu` (auto) even when settings.json
  said `cpu`, so the documented `whisper.gpu = cpu` escape did not work on a first run, which is exactly when this crash
  happens.

Re-checked by this lane:

- The crash on the development PC without the sandbox. A copy of the 1.1.1 build (`dist\Dictum` in the main checkout,
  1.1.1+757e66b) has `ggml-vulkan-whisper.dll`'s import `vulkan-1.dll` patched to `vulkan-X.dll` (same length; the
  string occurs exactly once in the file). `--selftest` with `whisper.gpu = auto`, APPDATA and LOCALAPPDATA in a
  temporary folder, exited -1073740791 (0xC0000409) with that `GGML_ASSERT` line on stderr.
- In Windows Sandbox with `scripts\sandbox-check.ps1 -NoVulkanLoader` (below), the 1.1.1 build crashed the same way.
- The duplicate folder and the free-then-retry in Whisper.net's loader were not re-read by this lane; the merge lens
  verified them from source on 21/10/2026: `NativeLibraryLoader.cs` at tag 1.9.1 frees already-loaded dependencies when
  one fails, and its search list has `AppDomain.BaseDirectory` (trailing separator) and
  `GetSafeDirectoryName(assemblyLocation)` (a `Path.GetDirectoryName` wrapper; no trailing separator) before an ordinal `Distinct`, so the Vulkan folder appears twice.

**Why the sandbox runs missed it.** `sandbox-check.wsb` turns the virtual GPU off (`VGpu Disable`), and its comments read
as if that made a PC without a GPU. It does not:

- The sandbox keeps a Vulkan loader in System32. The environment line of the 21/10/2026 run lists `vulkan-1.dll` and
  `vulkan-1-999-0-0-0.dll`.
- Vulkan has still found a device there. The investigation saw an Intel device. The 17/10/2026 clean-PC check above
  records `whisper runtime … Vulkan … [OK]` in the sandbox.
- So the Vulkan runtime loaded and the failing path was never taken. The comments are corrected, and `-NoVulkanLoader`
  now takes the loader away.

**Fix.**

1. `src/Dictum.Windows/GgmlEnvironment.cs`: `Apply()` calls `_putenv_s("GGML_NO_BACKTRACE", "1")`, declared in
   `Interop/Native.cs` with the other native calls. It also sets the variable with `Environment.SetEnvironmentVariable`,
   so .NET readers agree. A non-zero return is logged as a warning once the log exists; it never throws. The
   `// simplification:` note names the ceiling (ggml's terminate chain is never installed; no backtrace is lost on
   Windows) and the trigger to retire it (Whisper.net stops freeing and retrying a ggml library, or ggml drops the
   assert).
2. `Program.Main` calls `GgmlEnvironment.Apply()` as its first statement, before the arguments are read. That covers
   every mode (tray, `--selftest`, transcribe, bench, downloads) before any thread starts. `_putenv_s` is not
   thread-safe.
3. The first-run probe honours an explicit `whisper.gpu`. `ModelCoordinator.FirstRunProbeGpu(seeded)` gives the seeded
   (or in-use) settings' value, else auto, and `ProbeGpuMachine(gpu)` passes it to `WhisperEngine.ProbeRuntime`. Every
   caller of `ProbeRuntime` was checked: `SelfTestCommand`, `CliCommands` and `AppShell.ProbeGpu` already passed the
   settings' value, and `ModelCoordinator.FirstRun` was the only one that did not.
4. Start-up on a PC without a Vulkan loader. `WhisperEngine.RuntimeOrder` gives the CPU order for `whisper.gpu = auto`
   when `WhisperEngine.VulkanLoaderPresent` says `vulkan-1.dll` does not load, and the order line in the log says why.
   - Dictum.Windows sets that seam in `Main` to `GgmlEnvironment.VulkanLoaderLoads`, which calls `NativeLibrary.TryLoad`
     and frees the handle at once. So the native call stays in the platform layer, and the ArchitectureTests still hold.
   - `vulkan` still tries Vulkan first, and `cpu` never looks.
   - The cost it removes: in the 21/10/2026 sandbox run, the 1.1.1 build's order line came about 10 s before its exit
     (the run's timestamps are in whole seconds). The investigation measured about 9.4 s of failing Vulkan tries per start.
   - A PC with a loader but a broken driver still tries Vulkan, and fix 1 is what keeps that start alive.

**What the merge lens established about `SetEnvironmentVariable` (21/10/2026).** `Environment.SetEnvironmentVariable`
alone does **not** stop the crash. The investigation observed this, and the merge lens reproduced it on 21/10/2026: a
published build whose `Apply()` used only `SetEnvironmentVariable` (mutation M2 below) aborted 0xC0000409 in both gated
(v) cases. Reason, from the lens's .NET 10 console experiment (framework-dependent and self-contained win-x64):
`ucrtbase.dll` is already loaded at the first line of `Main` with `*__p__environ()` and `*__p__wenviron()` both
non-null, and after `Environment.SetEnvironmentVariable("LENS_A","1")` ucrtbase `getenv("LENS_A")` returned null. In a
.NET process the UCRT's environment copy therefore already exists before `Main`, so a change made through the OS
environment block is never seen by `getenv`; ggml-base-whisper.dll imports `api-ms-win-crt-environment-l1-1-0.dll`, so
ggml reads ucrtbase's copy. `_putenv_s` is required; `SetEnvironmentVariable` is kept only so .NET code reading the
variable agrees. (An earlier version of this lane's record said the published M2 build did not crash and inferred that
the UCRT builds its copy lazily; the lens disproved both, and this paragraph replaces them.)

**Pins** (`tests/Dictum.Tests/NoVulkanCrashTests.cs`):

- (i) After `Apply()`, ucrtbase's own `getenv("GGML_NO_BACKTRACE")` returns "1". The test first removes the variable with
  `_putenv_s` and checks it is gone.
- (ii) `Apply()` is the first statement of `Main`, the Vulkan seam the second, and argument parsing the third (a source
  pin, as other pins read source).
- (iii) `FirstRunProbeGpu` returns cpu for a seeded cpu, and auto for null or defaults. `FirstRun` itself hands the
  seeded value to the runtime probe, through the internal `TestRuntimeProbe` stand-in.
- (iv) `RuntimeOrder`:
  - auto with a loader that does not load gives the CPU order and a reason;
  - auto with a loader, or with no check, gives Vulkan first;
  - vulkan gives Vulkan first;
  - cpu never calls the check.
- (v) `PublishedVulkanFailureTests`, gated on `DICTUM_PUBLISHED_DIR` and run by CI after the Publish step. It copies the
  published layout to a temporary folder, patches the import as above (asserting exactly one occurrence), and runs
  `Dictum.exe --selftest` with `whisper.gpu` auto and vulkan, APPDATA and LOCALAPPDATA in the temporary folder. It
  asserts:
  - the exit code is not 0xC0000409, and 0 or 1;
  - stderr has no `GGML_ASSERT`;
  - there is a `whisper runtime … CPU … [OK]` line;
  - and either the log's order line is Vulkan first (so the CPU line is the fallback from a failed Vulkan load: the path
    that aborted), or, for auto on a PC with no loader, the log gives the CPU order's reason.

  The vulkan case takes the failing Vulkan load whatever the PC's loader does.
- A pin that ci.yml runs (v) after the publish step.

**Mutation checks** (each run and read on 21/10/2026; the PR body has the table):

- M1, `Apply()` doing nothing: (i) and both (v) cases failed, and the published build aborted 0xC0000409.
- M2 (SetEnvironmentVariable only): pin (i) and both (v) cases observed failing on the reverted behaviour (lens, 21/10/2026).
- M3, `Apply()` moved after argument parsing: (ii) failed.
- M4, `new WhisperSettings().Gpu` restored in `ProbeGpuMachine`: (iii) failed.
- M5, the loader check ignored: (iv) failed.

**Sandbox results (21/10/2026).** The command was `scripts\sandbox-check.ps1 -Gpu auto,cpu -NoVulkanLoader`.

- Layouts:
  - this branch, published with `-VcRuntimeDir` from the development PC's System32 copies (14.51.36247), for this
    check only, never shipped;
  - a copy of the 1.1.1 build with the same four DLLs beside its native folders.

  The sandbox has no Visual C++ runtime, so a layout without them loads no runtime at all, and nothing can show the
  crash. A first run with the plain layouts failed exactly that way, with `Failed to load native whisper library`.
- The sandbox's System32 had `vulkan-1-999-0-0-0.dll` and `vulkan-1.dll`. Both were renamed, leaving none for the runs.
- Engine lines:
  - this branch, auto: `whisper runtime CPU (whisper.gpu = auto; loaded in 0.9 s) [OK]`, exit 1 after 9 s. The log reads
    `Whisper runtime order: Cpu -> CpuNoAvx (whisper.gpu = auto; the Vulkan loader, vulkan-1.dll, does not load on this
    PC, so the Vulkan runtime is not tried)`.
  - this branch, cpu: `CPU (whisper.gpu = cpu; loaded in 0.6 s) [OK]`, exit 1 after 9 s.
  - 1.1.1, auto: no engine line, exit -1073740791 after 19 s, the `GGML_ASSERT` line as the output's last line.
  - 1.1.1, cpu: `CPU (whisper.gpu = cpu; loaded in 0.7 s) [OK]`.
- Exit 1 in every completed run is the microphone line, which fails by design in the sandbox.

**Upstream defects to report** (not reported by this lane):

- Whisper.net's runtime search list keeps the same folder twice when one path ends in a separator and the other does
  not. It should normalise before `Distinct`.
- Whisper.net frees a ggml library after a sibling fails and then loads the same library again from the same folder,
  which ggml's static initialiser does not survive.

**Not verified:**

- On a real PC without Vulkan, or with a broken driver, outside the sandbox.
- Tray mode, which was never run on the development PC because a copy of Dictum runs there.
- The first-run probe fix end to end in the tray (it is pinned through `FirstRun` with a stand-in probe).
- The duplicate-folder mechanics inside Whisper.net.
- CI's first run of the new step, including whether `--selftest` behaves on a runner without audio devices or a desktop.

## Public copy is language-neutral (21/10/2026, branch fix/language-neutral)

- **The rule (product owner, 21/10/2026):** Dictum is going out worldwide, and everything in this repository, the GitHub
  pull-request descriptions and the Store listing is public. Public copy names no specific languages or regions tied to the
  owner's team. It says "any of the languages Whisper supports" and points to the list in the app (Settings > Models >
  Language); where an absence has to be mentioned it says "some languages are not in Whisper's list, so they cannot be
  chosen" and never names the absent language. Technical examples use language codes that belong to nobody's team (`es`, `de`,
  `ja`).
- **Applied to:** the README language section, SPEC §16 and §23 to §24 (the "Asked for" sentences are now purpose
  statements), the earlier entries of this file (their substance is kept, entries are not deleted), the site's roadmap card,
  the Settings > Models > Language text, code comments and the tests' example languages.
- **Language picker order (SPEC §23):** `auto`, English, then every other language alphabetically by its English name. No
  language is featured any more, so `WhisperLanguages.Featured` is gone and the picker needs no list to maintain.
- **Pins:** `LanguageTests.The_picker_lists_auto_then_english_then_every_other_language_alphabetically_by_name`, and
  `PublicCopyNeutralityTests`, a sweep of every text file in the repository for the words (spelled in pieces in the test, so
  the test does not contain them), over the files git lists as public (tracked, plus untracked files that are not ignored),
  so ignored build output cannot fail it. The one allowance is Whisper's own language table: the `("code", "Name")` entries
  inside the `Table` initializer of `src/Dictum.Core/Transcribe/WhisperLanguages.cs`, which list all 100 languages alike.
  Region words are in the list too. Text in the script of the sentence-end marks is allowed in test fixtures under `tests/`
  (they test a script rule, not a language); everywhere else such examples are described by code point, and the sweep
  fails any file outside `tests/` that contains a character in U+0900 to U+097F.
- **Limits:** commit messages and the merged history cannot be rewritten (no force-push), so earlier commits keep their
  wording. The pull-request descriptions were edited instead.

## In-app Help: How Dictum works, and questions and answers (21/10/2026, branch feat/in-app-help)

**The decision (Kyle, 21/10/2026, as relayed by the orchestrator).** "The thing we are missing is explaining how it
works": someone of 95 should be able to use Dictum with the help. Help lives in two places. A short "How Dictum works"
and questions and answers sit inside the app, work with no internet and keep the privacy promise. A full illustrated
guide lives on https://dictum.kynni.au/help (built by another lane), and the app links to it.

**What this lane built.**

- **Settings → Help**, the last entry in the Settings list (after About), and a **Help** button in the tray panel that
  opens it (the lane's last commit, a single hunk in `FlyoutWindow.cs`, which lane A owns).
- **The copy** is plain constants in `src/Dictum.Core/Help/HelpText.cs`, with no platform code, so tests pin it. The page,
  `HelpPage` in `src/Dictum.UI/Views/Settings/OtherPages.cs`, only lays it out.
- **Network.** None from the page itself. **Open the full guide** hands https://dictum.kynni.au/help to `IPlatform.Open`
  (the system browser), and **Email support** hands `mailto:hello@kynni.com.au` to it (the user's mail app). No other
  address, no prefilled body, no request by Dictum.
- **Reading it.**
  - The page title is heading level 1, the three cards (More help, How Dictum works, Questions and answers) are level 2,
    and the accuracy part and each question are level 3 (`AutomationProperties.HeadingLevel`).
  - Each block of text (how it works, why it mishears, each question with its answer) is a tab stop named after its
    heading. Tab walks through them in order, Avalonia scrolls the focused one into view, and Page Down scrolls the
    page from there (both observed headless). A ring in the link colour (light or dark) shows only while the focus came
    from the keyboard.
  - Both buttons carry their sentence as automation help text.
  - The help paragraphs are 14 px against 13 px body text elsewhere: they are read, not glanced at (my call).
- **The More help card is first**, so the guide and support are the first thing a stuck reader sees. The answers point
  to it as "at the top of this page".

**Where the copy departs from the brief's wording, and why.** These are my calls, for the product owner to confirm. Each
is worded to be true at main 6dfede8 and after the other 1.2 lanes land.

1. **"More accurate", "Standard" and "Test my setup"** do not exist in the app at 6dfede8: a search of `src`, `docs`,
   `site` and the README found none. The copy says "choose a bigger speech model in Settings > Models" and names the
   shipped **Calibrate my voice**. Settings > Models itself says "Bigger is more accurate and slower."
2. **"Standard works for most people"** is not measured anywhere in the repository (no test across speakers). The copy
   says the model that comes with Dictum "is a good place to start".
3. **"No sound from mic"** is not a message Dictum shows. The pill says "Mic error" (`PillText`), and a recording quieter
   than `silence_threshold` is dropped with no message. The question reads: It says "Mic error", or nothing happens when
   I talk.
4. **"Settings > Models > Advanced > run on the CPU"** does not exist. What ships is the **Try on the CPU** button
   (tray panel and Settings > About, only after a model failed inside the GPU runtime) and `whisper.gpu = cpu` in
   settings.json. The copy names Try on the CPU and where it appears, and for a Dictum that still closes, the support address.
5. **"Updated in 1.1.2"**: there is no 1.1.2. `Directory.Build.props` says 1.1.1, and the launch-crash fix (19b09f6, PR
   #21) merged after 1.1.1. The copy says "The version you are using now has the fix", which holds for any build that carries this page (lens fix, below).
6. **"Videos" in the full guide** are not promised in the app. The guide note says "step by step, with pictures",
   because the app cannot check the site and that lane had not shipped when this was written.

**Figures in the copy.**

- "About 150 MB": base.en is 147,964,211 bytes, from the README's catalogue table, which the README says was verified by
  HTTP HEAD on 15/10/2026.
- "Several GB": large-v3 alone is 3,095,033,483 bytes, from the same table.
- "Ten short sentences": the calibration window counts "Sentence 1 of 10" (`UiWindowTests`).

**Recalled, not verified.**

- The small up arrow beside the clock that shows hidden tray icons (Windows' own behaviour).
- That many headsets have a mute button.

**Dependencies on other lanes.**

- **Renames.** `HelpPageTests.Every_label_the_help_names_exists_in_the_app` fails when the app drops a label the help
  names: Calibrate my voice, Dictionary, Fix last dictation, Save corrections, Settings, Dictation on, General, Models,
  Storage, Microphone, Clean-up, Press your hotkey, Change…, Use this hotkey, Test microphone, Model folder, Browse…,
  Remove Dictum completely…, Try on the CPU, Mic error, Open the full guide, Email support and Help (the tray button).
  Any lane renaming one of them, for example Calibrate my voice to Test my setup, must change `HelpText` with it, and
  that copy change needs the product owner's review.
- **Tier names (Simple and Advanced).** The copy names pages only ("Settings > Models"), never a tier, so it stays true.
- **The onboarding folder step.** The copy says the Model folder is in Settings > Models, with Browse…; it stays true
  unless that lane moves the control.
- **A Settings > Models > Advanced run-on-the-CPU control**, if a lane adds it: question 4 could point there. That is a
  copy change, for review.
- **A "No sound from mic" message**, if a lane adds it: question 3 should name it. The pin above checks only "Mic error".
- **dictum.kynni.au/help** must be live, with pictures, before a release carries this page.
- **The privacy policy and the landing page (approved copy, not changed here).** `site/privacy.html` ("This website:
  dictum.kynni.au") and the landing page's dictum.kynni.au card both say the app reaches that host "Only when you open the
  privacy policy from Settings, or click Check for updates". **Open the full guide** is a third case: the browser opens
  dictum.kynni.au/help. Before a release carries this page, the product owner must approve an update to both. This lane
  may not reword approved copy.
- **The release that carries this page** must carry the launch-crash fix. Any build from main after 6dfede8 does.
- **FlyoutWindow.cs (lane A).** The Help button is one hunk in its own commit, at the end of the branch.
- **The Mac beta.** The copy is Windows copy: the clock, Windows Settings, the right Ctrl key and the Microsoft Store.
  A Mac build needs its own wording for those answers.

**Pins (`tests/Dictum.Tests/HelpPageTests.cs`), each observed failing on the reverted behaviour (21/10/2026).**

| Pin | Mutation | Observed |
|---|---|---|
| Help is in the navigation (last) and opens from the list and by name | "Help" removed from the page list | failed: expected "Help", found "About" |
| Every sentence and every question renders, each question a named tab stop | the first question left out | failed: item not found |
| Open the full guide opens exactly https://dictum.kynni.au/help through the platform, and Email support exactly mailto:hello@kynni.com.au | the button opened the privacy address; the constant changed to the site root | failed both times: collections differ at index 0 |
| No request reaches either HTTP client, `Open` gets only the two addresses, and the page and copy name no network API | `Shell.CheckForUpdatesAsync()` added to the page; an `HttpClient` named in the page source; Email support replaced by Report a problem | failed all three: 1 request against 0; the source scan matched; the mailto had a subject added |
| The copy has no "always accurate", "100%", "perfect", "guarantee", telemetry, analytics or tracking word, and no US spelling | "always accurate", "no telemetry" and "100% accurate" added in turn | failed all three |
| Every label the help names exists in the copy and in the app | the copy's "Test microphone" changed; the app's Test microphone button renamed; the pill's "Mic error" changed | failed all three |
| No text or button runs past the visible width at 860 × 640, 853 × 480 (a 1280 × 720 screen at 150 %) and 640 × 420 (the smallest window), and text wraps | paragraphs set to no wrapping; a 700-DIP minimum width | failed in all three sizes both times |
| Headings, keyboard order, focus ring, scroll into view, Page Down | blocks not focusable; ring removed; heading levels removed; ring left on after focus moved | failed all four |
| The tray panel's Help button opens Settings → Help, with Settings no longer spanning its row | the button removed; the button opening Settings with no page; the column span kept | failed all three (the label pin alone still passed with the button removed, because the Settings list also shows "Help") |

Removing an explicit `BringIntoView` call **did not** fail the keyboard pin: Avalonia already scrolls a focused control
into view. The call was deleted as redundant, so the scroll assertion now pins the framework's behaviour.

An early mutation run reused a stale `Dictum.UI.dll`. A restored file kept the backup's older timestamp, and a change
in Dictum.Core's internals does not rebuild its dependants. That run (the pill label) was repeated with
`--no-incremental` builds and a touch after each restore, and failed as expected.

**Not verified.**

- A real window at 150 % or 200 % scaling. The headless platform renders at scale 1, so the pin measures the DIP sizes
  that scaling leaves.
- Screen-reader speech. Heading levels, names and help text are pinned as properties, not heard in Narrator or NVDA.
- The page in a real Settings window on the desktop (tray mode is never run on the development PC).
- https://dictum.kynni.au/help itself, which did not exist when this was written.
- The tray panel on a small screen. Its height does not change: the lens on f69cf24 measured 748 DIP before and after, because Help takes the second column of the row Settings used to span.
- Whether a person of 95 can follow it. Only a person can test that.

### Lens fixes (21/10/2026), lens on f69cf24

The lens on PR #24 returned **Block**, on the privacy sentence. That one is the product owner's call and is handled
separately: `site/privacy.html` and `site/index.html` are not touched here.

Finding 4 (the Email support button, and making the support address one value) went to lane F, which owns
`feat/feedback`, stacked on this branch. `HelpText.SupportAddress` and the button are unchanged here.

The other findings are fixed on this branch. The wording of each new string is the lens's, and every string is
PROPOSED.

| # | Finding | Fix |
|---|---|---|
| 2 | Major: the crash answer said "fixed in later versions", which is wrong in a build whose About page says 1.1.1 | "On some PCs, Dictum 1.1.1 closed as soon as it started. The version you are using now has the fix. If Dictum closes on another PC, install the latest version from the Microsoft Store or from dictum.kynni.au." |
| 3 | Major: "choose Email support at the top of this page" cannot be followed when Dictum will not open | "If it still closes, email hello@kynni.com.au and tell us.", built from `SupportAddress` |
| 5 | Minor: Esc also cancels after the key is let go, and the program typed into also receives it | "To cancel instead, press Esc before your words appear, while you are holding the key or just after you let go. Nothing is typed. The program you are typing in also receives the Esc." |
| 6 | Minor: the removal answer left out snippets, keys, the shortcut, the start-with-Windows entry and the program | see the note below the table |
| 7 | Minor: the answer did not say where Try on the CPU is | "If the Dictum menu, or Settings > About, shows a button called Try on the CPU, choose it." |
| 8 | Minor: Enter or Space alone cannot be the hotkey either | "…, and neither can Enter or Space on their own." |
| 9 | Minor: a DNS lookup added to the page passed all 11 tests | `Dns`, `Ping`, `NetworkInformation`, `Process.Start` and `ShellExecute` join the no-network pin's source scan |
| 10 | Minor: the tray panel was said to grow by a row | "No height change" in DECISIONS and the PR. `FlyoutWindow.cs`'s summary says "six actions"; that is a comment-only edit |

**Finding 6: the new wording.** The answer now reads:

> It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys, its Start menu
> shortcut and its start-with-Windows entry, then closes. If you got Dictum from the Microsoft Store, Windows Settings
> then opens so you can uninstall the app itself; otherwise Dictum removes the program too, unless its first warning says
> it cannot.

The last clause, "unless its first warning says it cannot", is mine and not the lens's. A copy with no `manifest.txt`
is left in place, and the first warning says so (`AppShell.Actions.cs`, "The program itself is not removed: …"). That
covers a build from source and one older than 16/10/2026. Without the clause the sentence would over-promise. Snippets
are in the removal list (`PathRegistry`, "snippets").

**Mutation check.** The DNS lookup (`System.Net.Dns.GetHostAddresses("localhost")` in the page's constructor) now fails
`The_page_reaches_the_network_only_through_the_platform_seam` with "Assert.DoesNotMatch() Failure: Match found". That
was observed failing on the reverted behaviour, in a `--no-incremental` build.

**A shared-scratchpad mishap.** This lane's mutation script lived in the shared scratchpad, and another lane (onboarding)
replaced it with its own script of the same name. One call here ran that script. With this lane's argument it matched
none of its mutations, so it changed no file. It did build `.worktrees/onboarding` and print that worktree's status.
This lane's scripts now live in their own folder.

**Full page text at 2d7e861 (supersedes the 4fe362c block).** Generated by printing `HelpText` at that commit (a throwaway
test, not committed; every `HelpText.AllText()` entry is in it). `FreeLine` and `FreeLaterNote` are APPROVED verbatim
(owner, 21/10/2026 22:18 AEDT); the rest is PROPOSED, awaiting the product owner's review: do not reword without it. The
constants are the source; this copy is for review.

> **Help** (page title)
>
> **More help** (section heading)
>
> The full guide on the Dictum website goes step by step, with pictures. It opens in your web browser and needs the internet.
>
> [Button: Open the full guide] opens https://dictum.kynni.au/help
>
> Email support opens a new email to hello@kynni.com.au in your own email program, with the version of Dictum and the models in use filled in. Dictum sends nothing by itself.
>
> Send feedback or suggest a feature opens a new email to hello@kynni.com.au in your own email program, with the version of Dictum, your Windows build and the models in use filled in. You can read and change it before you send it. Dictum sends nothing by itself.
>
> [Button: Email support] opens mailto:hello@kynni.com.au
>
> [Button: Send feedback or suggest a feature] opens mailto:hello@kynni.com.au
>
> **How Dictum works** (section heading)
>
> Dictum types what you say into the program you are using.
>
> 1. Click where you want the words to go, for example in an email.
>
> 2. Hold down the Dictum key. Unless you changed it, this is the Ctrl key on the right-hand side of the keyboard.
>
> 3. Speak in your normal voice. A small bar above the taskbar shows that Dictum is listening.
>
> 4. Let go of the key. A moment later, your words appear where you clicked.
>
> In that moment, three things happen. First, your voice is turned into text on this PC by a speech model. A speech model is a program that has learned what spoken words sound like. Next, the text is tidied up: punctuation and capital letters go in, and ums and ahs come out. Last, the text is pasted where you clicked, as if you had typed it.
>
> All of this happens on this PC. Nothing you say is sent anywhere unless you turn on cloud clean-up in Settings > Clean-up, and it is off unless you turn it on.
>
> The Dictum menu: click the Dictum icon near the clock, usually at the bottom right of the screen. If you cannot see the icon, click the small up arrow beside the clock first. The menu has Settings, Help and more.
>
> In this help, "Settings > Models" means: open Settings from the Dictum menu, then choose Models on the left.
>
> **Why Dictum hears some people better than others**
>
> Everyone's voice, accent and microphone are different, so a speech model hears some people better than others.
>
> The speech model that comes with Dictum is a good place to start.
>
> If words often come out wrong, choose a bigger speech model in Settings > Models. Bigger models are more accurate, but slower, and each one is a download you choose.
>
> Not sure which one to choose? Open the Dictum menu and choose Calibrate my voice. You read ten short sentences aloud, and Dictum tells you which of the speech models on this PC hears you best.
>
> **Questions and answers** (section heading)
>
> **Where does my voice go?**
>
> Nowhere. Dictum turns your voice into text on this PC, then throws the recording away. It never saves your voice to a file and never sends it anywhere.
>
> The only recordings Dictum keeps are the ones you make on purpose in Calibrate my voice. They stay on this PC until you delete them.
>
> If you turn on cloud clean-up (it is off unless you turn it on), the text of each dictation, and the words in your dictionary, are sent to the service you chose. Your voice still stays here.
>
> **Why did it type the wrong words?**
>
> Dictum can mishear words, just as people can. These things help:
>
> • Keep the microphone close to your mouth, and speak at your normal speed.
>
> • If a name or word keeps coming out wrong, add it with Dictionary in the Dictum menu.
>
> • To teach Dictum a fix, choose Fix last dictation in the Dictum menu. Correct the text, tick the changes you want kept, then choose Save corrections. Dictum makes that fix from then on.
>
> • For better accuracy overall, see "Why Dictum hears some people better than others" above.
>
> **It says "Mic error" or "No sound from mic", or nothing happens when I talk. What do I check?**
>
> If Dictum shows "No sound from mic", it says: Dictum is not hearing anything. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.
>
> If Dictum cannot find a microphone at all, it says: No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.
>
> • Check the microphone is plugged in, switched on and not muted. Many headsets have a mute button.
>
> • Open Settings > Microphone. Choose your microphone from the list, then choose Test microphone and speak. The bars move when Dictum hears you.
>
> • In Windows Settings, go to Privacy & security > Microphone. "Microphone access" and "Let desktop apps access your microphone" must both be on.
>
> • On a remote or virtual desktop (a computer you use over the internet or your office network), your own microphone has to be passed through to it. This is called microphone redirection, and it must be turned on. If you are not sure how, ask whoever looks after your computers.
>
> **Dictum closed straight away when I opened it. What do I do?**
>
> Dictum 1.1.2 fixes a start-up crash on PCs without a working Vulkan graphics driver (the app closed before its tray icon appeared; it now runs the speech model on the CPU there). If Dictum closes on another PC, install the latest version from the Microsoft Store or from dictum.kynni.au.
>
> If the Dictum menu, or Settings > About, shows a button called Try on the CPU, choose it. Dictum then runs the speech model on the computer's main chip (the CPU) instead of the graphics card. This can be slower.
>
> If it still closes, email hello@kynni.com.au and tell us.
>
> **How do I change the key I hold?**
>
> Open Settings > General. Under Press your hotkey (the hotkey is the key you hold), choose Change…, then press the key you want. When Dictum shows its name, choose Use this hotkey.
>
> Most keys work, and so do the side buttons on a mouse. The Fn key, Esc and Tab cannot be used, and neither can Enter or Space on their own.
>
> **How do I stop it listening?**
>
> Let go of the key. Dictum stops listening and types what you said.
>
> To cancel instead, press Esc before your words appear, while you are holding the key or just after you let go. Nothing is typed. The program you are typing in also receives the Esc.
>
> If you started hands-free (by tapping the key twice quickly), tap the key once more to stop. Esc also stops hands-free, and what is already typed stays.
>
> To turn Dictum off for a while, use the on/off switch at the top right of the Dictum menu.
>
> **Can I use it in Word, Outlook, Teams or a web browser?**
>
> Yes. Dictum types into almost any program where you can type, including Word, Outlook, Teams and web browsers. Click where the words should go first.
>
> The exception is a program running as administrator (with extra permissions): Windows does not let Dictum type into it.
>
> **Where are the model files, and how big are they?**
>
> Settings > Models shows the Model folder, where Dictum keeps the models you download. The speech model that comes with Dictum is about 150 MB (megabytes) and is kept with the program itself.
>
> Bigger speech models and AI clean-up are downloads you choose. If you add the largest, most accurate ones, they can take up several GB (gigabytes).
>
> To keep them on another drive, choose Browse… beside Model folder in Settings > Models. Settings > Storage lists every model with its size, and you can delete the ones you do not need there.
>
> **How do I remove Dictum completely?**
>
> Open Settings > Storage. At the bottom, choose Remove Dictum completely…
>
> Dictum first lists everything it will delete, with sizes. Then it asks you to type DELETE to confirm.
>
> It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys, its Start menu shortcut and its start-with-Windows entry, then closes. If you got Dictum from the Microsoft Store, Windows Settings then opens so you can uninstall the app itself; otherwise Dictum removes the program too, unless its first warning says it cannot.
>
> **Is Dictum free?**
>
> Free: no subscription, no account, no ads, no analytics, no telemetry.
>
> The base product stays free to keep on your PC; optional paid extras may come later.
>
> There is no charge and no word limit. Dictum is open source, which means anyone can read the program's code. It is made by Kynni Pty Ltd and shared under the MIT licence.
>
> **Where is the full guide?**
>
> The full guide is on the Dictum website at dictum.kynni.au/help. Choose Open the full guide at the top of this page.
>
> It opens in your web browser and needs the internet. Everything on this page works without the internet.

### Help reconciled with main (21/10/2026, after PR #29 and the merge of origin/main 8419f10)

Only `CrashFixLine`, the reworded third paragraph of the free answer, the cloud sentence and the on/off switch sentence are PROPOSED, awaiting the product owner's review; `FreeLine` and `FreeLaterNote` are APPROVED (below). The strings are constants in `HelpText.cs`, pinned by
`The_crash_free_and_calibration_answers_carry_the_reconciled_copy` (observed failing on the reverted wording).

- **Start-up crash.** The "Dictum closed straight away" answer now opens with the 1.1.2 note's sentence word for word
  (`HelpText.CrashFixLine`, the same text as docs/STORE.md "CHANGED SINCE THE LAST SUBMISSION") and keeps its second
  sentence about installing the latest version. The old "Dictum 1.1.1 closed..." and "The version you are using now has
  the fix" sentences are gone. The microphone answer already reused `NoSoundText` (PR #29) and no literal in `HelpText.cs`
  repeats a `NoSoundText` sentence.
- **Is Dictum free?** The answer is three paragraphs. `HelpText.FreeLine` ("Free: no subscription, no account, no ads, no
  analytics, no telemetry.") and `HelpText.FreeLaterNote` ("The base product stays free to keep on your PC; optional paid
  extras may come later.") are **APPROVED verbatim for Help, site and Store (owner, 21/10/2026 22:18 AEDT): do not
  reword.** (Earlier in the day the "Free:" line was only the wording submitted in the Store listing, and it was not found
  on origin/main; the owner's approval supersedes that note.) The third paragraph keeps the earlier answer's facts (no
  charge, no word limit, open source, MIT licence, Kynni Pty Ltd). **PROPOSED reword (orchestrator, 21/10/2026):** its
  opening "Yes." is gone, because two paragraphs have already answered, and "no subscription" is not repeated, so it now
  begins "There is no charge and no word limit."
- **Cloud clean-up sends the dictionary too (PROPOSED).** "Where does my voice go?" said only the text of each dictation is
  sent. `CleanupPrompt.Build` also adds the personal dictionary's line to the prompt, and `site/privacy.html` already says
  so. It now reads "…the text of each dictation, and the words in your dictionary, are sent to the service you chose."
  `CloudPolicy` strings are untouched. Pinned: the answer must mention the dictionary (observed failing on the reverted
  wording).
- **The on/off switch (PROPOSED).** "Dictation on" is only the switch's screen-reader name (it shows no text), so the stop
  answer now says "use the on/off switch at the top right of the Dictum menu." The labels-exist pin no longer lists
  "Dictation on"; a pin forbids that name in the Help and requires the new phrase (observed failing on the reverted wording).
- **Test exemption.** The old pin forbade the words telemetry and analytics anywhere in the Help. It now removes the one
  `FreeLine` sentence before scanning, so any other use of those words still fails.
- **Calibration.** The tray button on this branch is "Calibrate my voice" (FlyoutWindow.cs); the Help uses that name and
  nothing else (pinned). A 1.2 rename to "Test your setup" is planned and not done: it must change the button and the Help
  together (`Every_label_the_help_names_exists_in_the_app` fails until it does).
- **Tier names.** Model names are untouched; reconcile after feat/simple-view merges.

## Feedback and feature requests through the user's mail program (21/10/2026, branch feat/feedback, lane F, stacked on feat/in-app-help)

**The decision (Kyle, as relayed by the orchestrator, 21/10/2026).** Users need a way to say what they would like Dictum to do.
It must not break the privacy promise, so Dictum sends nothing: the feedback goes through the user's own mail program.

**What this lane built.**

- **Settings → Help → "Send feedback or suggest a feature"**, beside **Email support**. It builds a `mailto:` link and hands it to
  `IPlatform.Open`, exactly as Report a problem does. The user's mail program opens a new message; the user reads it, edits it
  and sends it, or closes it.
- **No network statement.** Dictum makes no HTTP request, posts no form, and uses no clipboard for this. The only thing it does is
  `IPlatform.Open(<mailto: link>)`. A source pin and a network pin (both clicked through the real page, with the model-download and
  update-check clients counting requests) hold this.
- **One mail builder.** `SupportMail.Uri(subject, body)` in `src/Dictum.Core/Help/SupportMail.cs` is the only place a support
  `mailto:` link is made. `AppShell.ReportUri` (Report a problem) was changed to call it, with its output pinned identical to what it
  produced before. Report a problem's body keeps its LF line breaks (unchanged on purpose); the feedback body uses CRLF.
- **One address constant** (coordinator's addition, PR #24 lens finding 4). `SupportMail.Address` is the only copy of
  `hello@kynni.com.au` in code. `AppShell.SupportEmail` is an alias of it, and `HelpText` names it directly (`HelpText.SupportAddress`
  and `HelpText.SupportUri` were removed). A pin scans `src/**/*.cs` and fails on a second quoted copy.
- **Email support now opens the Report a problem email** (the same builder and the same body: the version, the platform and the
  models line), not a bare `mailto:`. Its note was reworded (PROPOSED, below). The models line is passed through
  `SupportMail.Facts` first, which cuts any path down to its last part.
- **About's card titled "Help" is now titled "Support"** (PROPOSED), so "Help" names only the Settings page.

**Where the support address lives, so the switch to `dictum@kynni.com.au` is known (decision for later; not made here).**

| Place | What to change |
|---|---|
| Code: `src/Dictum.Core/Help/SupportMail.cs`, `Address` | the one constant; the About text, Help page notes, Report a problem, Email support and feedback all follow it |
| `site/index.html` | the footer's plain address link (`mailto:hello@kynni.com.au`, its text) and the feedback link (`mailto:hello@kynni.com.au?subject=Dictum%20feedback`) |
| `site/privacy.html` | the privacy contact paragraph and the footer link (the site's own copy; **not touched by this lane**) |
| `packaging/Dictum.iss` | `AppContact=` (the installer; not code that can read the constant) |
| `.github/SECURITY.md` | the security contact |
| `docs/STORE.md` | the Partner Center support contact and the listing's support line (approved copy: product owner's call) |
| README.md, docs/SPEC.md, this file | prose mentions |

A pin (`The_site_links_to_the_same_email_from_its_footer_and_has_no_form`) fails if the site's feedback link and the app's address or
subject drift apart, so the site and the code cannot be switched half way unnoticed.

**What the email says (PROPOSED copy, awaiting the product owner's review; do not reword).**

- Button: `Send feedback or suggest a feature`
- Note under Help (screen-reader help text on the button too): `Send feedback or suggest a feature opens a new email to hello@kynni.com.au in your own email program, with the version of Dictum, your Windows build and the models in use filled in. You can read and change it before you send it. Dictum sends nothing by itself.`
- Email support's note (reworded): `Email support opens a new email to hello@kynni.com.au in your own email program, with the version of Dictum and the models in use filled in. Dictum sends nothing by itself.` (it was: `Email support opens a new email to hello@kynni.com.au in your own email program. Dictum sends nothing by itself.`)
- Subject: `Dictum feedback`
- Body (CRLF line breaks; `<…>` is filled in):

```
What I'd like to say:


(You can delete the lines below. They help us understand your setup.)
Dictum <version>
Windows build <version>
Speech model: <name> on <GPU or CPU>
Clean-up: <provider or rule-based tidy>
```

- About card title: `Support` (was `Help`).
- Website footer link text: `Feedback and feature requests`, href `mailto:hello@kynni.com.au?subject=Dictum%20feedback`.

**Where each fact comes from (`AppShell.FeedbackFacts`).** The version is `AppShell.AppVersion` (About's "Version"). "Windows
build <version>" is the platform name About shows ("On Windows…"), the word "build", and `Environment.OSVersion.Version` to three parts (for example 10.0.26100: Windows 11 reports 10.0, so the word "build" stops it reading as a version of Windows 10): **About does not show this, so it is the one new fact** (a number only). The speech model is `Models.CurrentName` (a model's name, or a
custom file's name without its folder). The runtime is the one About shows (`Vulkan`, `CPU`, `loading`, or `no runtime (model error)`).
The clean-up is `rule-based tidy` (provider none, or a local model that failed to load), `<model> on this PC` (local), or the cloud
provider's name alone (with a note when its key is missing): **not** About's full status line, because that names the server host, which
for a company's own server could be private. Every fact is cleaned by `SupportMail.Fact`: control characters out, only the last part of
anything path-like kept, 60 characters at most.

**Length.** The whole link is kept at 2,000 characters or fewer (`SupportMail.MaxUriLength`, the common mailto limit as recalled, not
verified against a specific mail program in this lane). If the four setup lines (five facts) would take it over, they are left off and the writing space and
note stay. A normal email is under 600 characters.

**Pins (`tests/Dictum.Tests/FeedbackTests.cs`, and edits to `HelpPageTests.cs`), each observed failing on the reverted behaviour
(21/10/2026, mutation run on c3c0339 and then on the follow-up commit).** Failing tests are the ones named; "also" lists others that failed.

| Pin | Mutation | Observed |
|---|---|---|
| The exact link for a sample setup | body line breaks changed from CRLF to LF (intro, then the join) | failed: exact-link pin, encoding pin, length pin, path pin, real-shell pin |
| `&`, `é` and line breaks encoded (one `&` only, all breaks `%0D%0A`, no raw space or LF) | body left unencoded except spaces | failed: encoding pin, exact-link pin, length pin, and the Report a problem pin |
| A path, folder or line break in a name never reaches the email | the cut at the last path separator removed | failed: path pin |
| (same) | control-character removal removed | failed: path pin |
| Link under 2,000 characters | the over-length fallback removed; the 60-character clip removed | failed: length pin (each) |
| Button exists, named, Open called exactly once with a mailto | button removed | failed: button pin, the Help keyboard pin, the "every sentence renders" pin and the network pin |
| (same) | `Open` called twice; `Open` given a GitHub URL instead | failed (each): button pin, real-shell pin and network pin |
| Nothing private in a real shell's email (dictionary word, dictated text, app name, scratch folders, user and machine name) | the dictionary file's text appended to the clean-up fact | **the first mutation did not fail**: with the path cut in place the file's path is cut to `dictionary.txt`, which is correct behaviour. With the cut also removed (the real leak): failed real-shell, path and `Facts` pins. The same for the config folder's path |
| Site link present with the exact href and text | link text changed; href's subject changed | failed: site pin (each) |
| The site has no form | `<form>` added to the footer | failed: site pin |
| One address constant in code | `AppShell.SupportEmail` given its own literal; `HelpText` given its own literal | failed: single-literal pin (each) |
| Subject `Dictum feedback` | subject changed to `Feedback` | failed: exact-link, button, site and network pins |
| Report a problem still builds the link it did before | `?subject=` broken in the shared builder | failed: Report pin, ShellTests' Report a problem, button, exact-link and network pins |
| Email support opens the Report a problem email | the button opens a bare `mailto:hello@kynni.com.au` again | failed: Email support pin, single-literal pin, Help "full guide" pin, network pin |
| About's card is titled Support | title put back to Help | failed: About card pin |
| A models line is cleaned part by part | the split removed | failed: `Facts` pin |

**Not killed (stated, not hidden).** Removing `SupportMail.Facts(...)` from `ReportProblem` fails no test (`nofacts` mutation): there
is no input today that puts a path into the models line (a speech model shows its file name, and a clean-up model that is not in the
catalogue or a full `.gguf` path is repaired away by the settings loader), so the call there is defence in depth; the `Facts` function
itself is pinned.

**Tests adapted from PR #24 (lane C)**, because this lane changes what they pin: the Help page's "network" pin now expects three `Open`
calls (guide, the Report a problem email, the feedback email) and allows `Shell.ReportProblem` and `SendFeedback` in the page source while
still forbidding direct network calls (the regex gained `Dns|Ping|NetworkInformation|Process\.Start|ShellExecute`, as the PR #24 lens asked);
the keyboard pin starts Tab from the last button, which is now Send feedback; the Email support pin expects the prefilled email.

**The Store certification notes (docs/STORE.md, "Notes for certification").** They describe Report a problem ("users can report a problem
from Settings (Report a problem)…"), so one clause was added about Settings > Help. The live 1.1.1 package has no such button; the clause
holds for the next submission (1.2) only. Update it if 1.2 does not ship with this feature.

**Not verified.** No mail program was opened: the link was checked as a string, and `Open` is a fake in tests. How Outlook, Gmail's
protocol handler or the Windows default mail app treat a `%0D%0A` body and a 600-character link was not tested here. The 2,000-character
limit is the figure the brief gave, not one checked against a mail program. The Settings > Help page was not seen on screen (headless
tests only), including the two buttons sitting side by side at 150 % scaling. The site change was not deployed or rendered.

### Lens fixes (21/10/2026), lens on 45768fc (PRs #24 and #29: Needs changes, ten Minors, no new blocker)

| # | Finding | Change | Pin, and the mutation observed failing |
|---|---|---|---|
| 1 | The no-network source scan did not read `SupportMail.cs` | the scan reads it too | an `HttpClient` field added to `SupportMail.cs` failed the network pin |
| 2 | `SupportMail.Uri` was public and open | made private; `SupportMail.ReportUri` (public, moved from `AppShell.ReportUri`) is the only other way in, so no code outside the class can make a support email with other content. A source pin: `mailto:` appears in code only in `SupportMail.cs`. A sentinel (`context.txt` in the config folder, standing in for a future personal-context file) in the real-shell and Email support pins | a second `"mailto:" + "x"` constant failed the mailto pin; the sentinel text put in the models line (Email support) and in the speech-model fact (feedback) failed their pins. **Stated:** text appended after the clean-up fact did not fail the feedback pin, because `Fact` clips each fact to 60 characters and the sentinel sat past it; that is the clip working, not a gap in the pin |
| 3 | The single-address scan needed a `"` on the same line | counts `hello@kynni.com.au` per `.cs` file over code lines only (lines starting `//` or `///` are skipped); exactly one in all code, in `SupportMail.cs` | a raw multi-line literal holding the address failed it |
| 4 | Local provider with the clean-up model not downloaded: the email said "<model> on this PC", About says "rules until downloaded" | the fact follows the header's order: loaded or loading, else failed, else `rule-based tidy (clean-up model not downloaded)` | the old expression restored failed the pin (it also asserts the header still says "rules until downloaded") |
| 5 | The Store note clause named a feature the live 1.1.1 lacks | clause now reads "from 1.2, send feedback from Settings > Help …" | none (copy) |
| 6 | "five lines" | "four lines (five facts)" in SPEC, README, this file, the PR body | none (copy) |
| 7 | `ReportUri` had no length cap | same `MaxUriLength` fallback: the models line is dropped. `ReportUri` now also cleans the models line itself (`Facts`), so no caller can skip it (this closes the earlier "not killed" `nofacts` case) | the fallback removed failed the report pin; the cleaning removed failed it too |
| 8 | Help answer order | the Try on the CPU paragraph now comes before "If it still closes, email …" (PROPOSED order change; text unchanged) | none (order; no pin added) |
| 9 | "Windows 10.0.26100" reads as Windows 10 on Windows 11 | the line is `Windows build 10.0.26100`; the Help note says "your Windows build" (PROPOSED) | the note back to "version" failed the note pin; the word "build" removed failed the real-shell pin; the sample link pin changed with the body |
| 10 | Tab order of the three buttons unpinned | a pin walks `GetNext` from Open the full guide: guide, Email support, Send feedback | the row's buttons swapped failed it (and the older keyboard pin) |

**PROPOSED copy changes in this round.** The feedback body's second setup line is `Windows build <version>` (was `Windows <version>`);
the Help note for the feedback button ends "…the version of Dictum, your Windows build and the models in use filled in…"; the Try on the
CPU paragraph moves one place up in the "Dictum closed straight away" answer.

## Privacy sentence for dictum.kynni.au covers "Open the full guide" (PROPOSED, 21/10/2026, branch site/privacy-guide-sentence)

**PROPOSED, pending the product owner's approval. Do not merge or deploy until approved.**

- **Why.** The 1.2 in-app Help page (PR #24) adds "Open the full guide", which opens https://dictum.kynni.au/help in the browser. The current site sentence says the app reaches dictum.kynni.au only for the privacy policy or Check for updates, so it would no longer be complete.
- **Old sentence** (privacy.html and index.html, pinned by `UpdateCheckTests.ProposedSiteSentence`): "Only when you open the privacy policy from Settings, or click Check for updates in the direct-download version, which reads one small file and sends nothing about you; the Store build is updated by the Store."
- **Proposed sentence:** "Only when you open the privacy policy or the full guide from Settings, or click Check for updates in the direct-download version, which reads one small file and sends nothing about you; the Store build is updated by the Store."
- **Where.** `site/privacy.html` (paragraph under "This website: dictum.kynni.au") and `site/index.html` (the dictum.kynni.au card). The pin in `tests/Dictum.Tests/UpdateCheckTests.cs` is updated to match.
- **On approval.** The policy's "Changes to this policy" section says "we will update this page and its date before that version is released". The date is the `Last updated` line in `site/privacy.html` (line 34, `<time datetime="2026-10-16">16/10/2026</time>`). It must be changed to the approval date, in both the `datetime` attribute and the text. This lane did not change it: no document in docs/ lets a lane set that date, so it stays with the owner's approval step.

**Second PROPOSED sentence, same branch (21/10/2026).** The 1.2 Help page's Email support, Report a problem and Send feedback buttons open the user's own mail program with a pre-filled email (Dictum version, speech model and runtime, clean-up choice; Send feedback alone adds the Windows build number, per the orchestrator's reading of the code on feat/in-app-help 65b99a4; no paths or names). Dictum sends nothing itself. Proposed addition to `site/privacy.html`, "What we collect" paragraph, after "...for example if you email us.": "The Email support, Report a problem and Send feedback buttons in Settings open a new email in your own mail program with Dictum’s version and the models in use filled in; Send feedback also includes your Windows build number. You can change or delete any of it before you send it." Also PROPOSED pending the owner's approval; the same `Last updated` line (`site/privacy.html:34`) applies on approval. No test pins this sentence yet.

## Warn when the microphone delivers no sound (21/10/2026, branch feat/silence-warning)

**The report** (product owner, 21/10/2026, relayed by the orchestrator): on a virtual desktop whose microphone was not passed through, he pressed the hotkey and the pill showed "Recording" and stayed there. Nothing said the microphone was delivering no audio, so the app looked broken.

**What the code did** (read at main 033809f):

- A microphone that opened and delivered only silence showed "Recording" for the whole hold. The pipeline then rejected the recording as silent (`RecordingGate`) and the pill hid without saying why.
- A microphone that could not be opened (`WasapiCapture.Start` throws, which includes no default capture device: `CaptureDevices.Resolve`) flashed "Mic error". The controller then went on to execute the rest of the key-down's effects, which included the "Recording · hold" pill. So "Mic error" was replaced at once by "Recording · hold", and that label stayed with nothing recording. This is the most likely cause of the "no device at all in some paths" symptom. It is fixed here: after a failed open, the rest of that batch is skipped (`DictationController.Execute`), and the open-failure pins check that the last pill is the flash.

**Decision (built as briefed, with the choices below):**

1. **Detection layer.** `CaptureBuffer` meters every 20 ms block already; it now calls back once per recording at the first block louder than `CaptureBuffer.SoundFloor`. `WasapiCapture` raises this as `IAudioCapture.SoundHeard`. The controller posts it to its inbox, and the pure state machine decides on its existing 50 ms `Tick`, using `DictationStateMachine.NoSoundGraceMs`. Nothing polls from the UI. The interface defaults (`ReportsSound => false`, a `SoundHeard` that never fires) keep every other capture, and every existing test fake, on the old behaviour: the machine only watches a session after `WatchForSound`, which the controller calls when the capture opened and `ReportsSound` is true.
2. **Grace period: 2000 ms**, hold-to-talk and hands-free alike, a constant with a comment, not a setting (the brief).
3. **The floor: 1e-5 RMS** (corrected by the lens on dc4e121; see "Lens fixes" below). 1e-5 is the 20 ms block RMS after decoding, downmixing and resampling to 16 kHz, not the level at the device: the 48 to 16 kHz resample lowers white noise by about 6 dB (mono) or 9 dB (stereo). Measured 21/10/2026 through `CaptureBuffer.Append` with 16-bit Gaussian noise at 48 kHz: digital zeros, and white noise quieter than about −91 dBFS (stereo) or −94 dBFS (mono) at the device, read as no sound; −84 dBFS passes. Real microphones were not measured; the warning's log line now gives the session's peak block RMS, so their noise floors can be read from logs.
4. **The grace counts only until the first sound of the session.** Once the microphone has been heard, a pause is the speaker pausing, so a hands-free session never warns between parts. This is stricter than "no audio for 2 s at any time", which would warn on every long pause with a microphone whose noise suppression sends exact zeros. Ceiling: a microphone muted halfway through a session is not caught.
5. **While the warning is up, recording goes on.** The pill reads "No sound from mic" with the red dot, meter, orb and edge glow kept, since it is still recording. The first sound puts the recording label back. The balloon shows once per session.
6. **Ending with the warning up** (key released, hands-free stop, auto-stop): the capture is discarded and nothing is transcribed or typed. The pill flashes "No sound from mic" for 1.8 s with a red "!" badge, instead of "Transcribing…". A hands-free session's token is cancelled (it has typed nothing). A hold shorter than the grace period is transcribed as before, and the pipeline rejects it if it is silent, so a fumbled press never claims the microphone is dead.
7. **No microphone at all.** The device list is checked only after the microphone could not be opened, so a press that opens it pays nothing for the check. If `IPlatform.ListCaptureDevices()` is empty, the pill flashes the warning at once with the no-microphone balloon, and nothing stays on Recording. Any other open failure, or a list that cannot be read, stays "Mic error". **This differs from the brief's wording**, which said to check the list when recording starts. Checking before every `Start` would add a device enumeration to each press's capture latency. With WASAPI, an empty list always makes `Start` throw (`CaptureDevices.Resolve`), so the outcome is the same. A list that is empty while `Start` somehow succeeds still gets the silence warning after 2 s.
8. **Mic error is unchanged**, both for an open failure with a microphone listed and for a microphone lost mid-recording, including with the warning up. The only change is that its flash is no longer overwritten by "Recording · hold" (see above).
9. **Tray panel line (item 2, small enough to build).** After a session that ended with no sound (point 6 or 7), the problem line in the tray panel and on Settings > About also reads "Last dictation heard nothing: check the microphone". It goes when a dictation types text (`ProcessingOutcome.Inserted`). It is held in memory only, so a restart clears it.

**PROPOSED strings, for the product owner's approval (none is approved; do not treat them as final copy):**

- Pill: "No sound from mic"
- Balloon (silence): "Dictum is not hearing anything. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on."
- Balloon (no microphone listed): "No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again."
- Tray panel and Settings > About line: "Last dictation heard nothing: check the microphone"

They live in `NoSoundText` (Core), and one pin checks them verbatim. No approved copy (CloudPolicy, consent dialog, state lines) was changed.

**What it might break.** A Bluetooth headset whose first packet takes longer than 2 s to arrive will show the warning and one balloon, then go back to Recording when its audio starts (not measured). A microphone whose own noise suppression sends exact zeros until speech can show the warning if the speaker waits more than 2 s before talking.

**Pins** (`tests/Dictum.Tests/SilenceWarningTests.cs`, 13 tests: pure state-machine pins in the style of `StateMachineTests`, a `CaptureBuffer` pin, and 4 controller pins with a stand-in microphone). Each was observed failing on the reverted behaviour; the PR body has the mutation table.

**Not verified:** on a real virtual desktop or Remote Desktop session without microphone redirection; with a real muted microphone, a Bluetooth headset, or a microphone with its own noise suppression; how the balloon looks on Windows 10 and 11; the pill's look with the new label (no screenshot was taken, and no Dictum build was launched on this PC).

### Lens fixes (21/10/2026), lens on dc4e121

The lens returned Needs changes, with no blockers. Its findings and what was done:

1. **Major (figure honesty): the floor's wording was false.** "Any real signal quantised to 16 bits or more should pass" ignored that the floor applies after the downmix and the 48 to 16 kHz resample. The lens measured through `CaptureBuffer.Append` with 16-bit Gaussian noise at 48 kHz. A probe in this lane, with another random seed, gave the same heard or silent result at each level, with block peaks within about 5 % of the lens's:

   | Level | Mono | Stereo |
   |---|---|---|
   | −84.2 dBFS | heard | heard |
   | −90.0 dBFS | heard (peak 1.61e-5) | heard (peak 1.15e-5) |
   | −92.7 dBFS | heard (peak 1.21e-5) | silent (peak 8.68e-6) |
   | −95.2 dBFS | silent | silent |

   The value stays 1e-5, as the orchestrator directed. The wording is corrected in `CaptureBuffer.SoundFloor`, decision 3 above, the README and the PR body. A new pin checks the clear ends: −84.2 dBFS heard and −96 dBFS silent, mono and stereo. The warning's log line now carries the session's peak block RMS and how many 20 ms blocks arrived. These are numbers only, so real microphones' floors, and "no audio at all", can be read from logs. A pin checks the line.
2. **Minor, accepted: the end-of-recording decision can miss a very late first sound.** It reads the warning flag when the key event is handled, before a first-sound report that may still be queued (the capture's 50 ms buffer plus the inbox). A first sound shorter than that, arriving after 2 s of silence and just before the release, is discarded with the warning instead of transcribed. This is inferred, not measured. It is marked `// simplification:` at `EndRecording`. Trigger to retire: a report of a dropped first word.
3. **Minor: the README's "No sound from mic" entry** now also says to check that Windows allows desktop apps to use the microphone (Settings > Privacy & security > Microphone). The balloon is unchanged. **Not verified:** whether a microphone privacy denial gives silence (the warning) or an open failure ("Mic error").
4. **Nit, left as a known gap:** in hands-free, while the warning is up, the pill's "tap to stop · Esc stops and keeps what's typed" hint is hidden, because the hint shows only with the hands-free label.
5. **Minor test race:** the second dictation in `The_controller_warns_once_for_a_silent_microphone…` reported its sound before the new session had started, so the sound could be counted against the old one. The test now waits for the new recording's pill first. It still proves its claim: a dictation that types text clears the heard-nothing line.

### Lens fixes (21/10/2026), lens on 28d16b7

The lens returned Needs changes with three minors and no change to src behaviour wanted. All three are fixed:

1. **Test race.** `WarnNoSound` emits the pill before the balloon and log effects. So a test that waited for the "No sound from mic" pill could read the balloon list or the log before they were written. The effect order is unchanged. Instead, the two tests now wait for the balloon (`The_controller_warns_once_for_a_silent_microphone…`) and for the log line (`The_warning_log_line…`) themselves. To check the wait, I added a temporary `Thread.Sleep(50)` before the warning's log line and balloon in `DictationController`: all 17 tests in the class passed with it. It was then removed.
2. **Pin gap.** `CaptureBuffer.PeakLevel` and `BlocksMeasured` now have their own pin. A fresh buffer reads 0 and 0. One block at RMS 0.3 followed by three at 0.01 reads a peak of 0.3 and 4 blocks.
3. **README basis restored.** The entry now reads "digital silence, or white noise at a 48 kHz microphone below about −91 dBFS in stereo or −94 dBFS in mono". The −91 and −94 dBFS figures hold only for a 48 kHz device:
   - A 16 kHz device skips the resampler (`MonoResampler` returns early when the input rate is the output rate), so its floor at the device is the raw 1e-5, which is −100 dBFS for a mono device. This is inferred from the code, not measured. A 16 kHz stereo device still goes through the downmix, which was not measured either.
   - 44.1 kHz devices were not measured.

### After merging main (21/10/2026, main at de944cc: PR #22's "No sound from mic" and release 1.1.2)

- **Merge.** `git merge origin/main` into feat/feedback (no rebase). The only conflict was this file; both sides' sections are kept
  (the warning section from PR #22 follows this one). `Directory.Build.props` came in with main's version change: this lane did not touch it.
- **The Help question about the microphone** now reads `It says "Mic error" or "No sound from mic", or nothing happens when I talk. What do I check?`
  (PROPOSED), and its answer starts with two paragraphs built from `NoSoundText` itself, so the help and the message cannot drift apart:
  `If Dictum shows "No sound from mic", it says: <NoSoundText.SilentBalloon>` and
  `If Dictum cannot find a microphone at all, it says: <NoSoundText.NoMicrophoneBalloon>`. The bullet checks that were there stay below them.
  Pins: the "labels the help names exist in the app" pin now includes "No sound from mic" (the help copy and the pill constant), and
  `The_mic_help_answer_quotes_the_no_sound_message_from_the_same_constants`.

### Lens fixes (21/10/2026), re-lens on 4dc2ad7: the hotkey comes out of the support email

**The finding (Major, privacy and disclosure).** Report a problem and Email support sent `Status.Models` as the "Models:" line, and
`Status.Models` is the header line: `{speech} · {runtime} · {clean-up} · {hotkey}` (the raw `hotkey` text if it does not parse). The copy
says only the version and the models go in (`HelpText.SupportNote`, this file's Report a problem entry, SPEC, README). **The email never
carried the hotkey by design; main had sent it since Report a problem shipped; fixed 21/10/2026.** Decision: the hotkey comes out of the
email and the copy stays as it is.

**The fix, in one place.** `AppShell.ModelsLine` (speech · runtime · clean-up, no hotkey) is now what the header builds on
(`Status.Models` = `ModelsLine` + ` · ` + the hotkey) and what `ReportProblem` passes to `SupportMail.ReportUri`. Callers of
`Status.Models` checked first: the tray panel (`FlyoutWindow`) and About's runtime line keep the full header, hotkey included; only the email
loses it. The runtime wording was lifted into one private helper (`HeaderRuntime`) used by both.

**Pins.** `Email_support_opens_the_report_email_with_the_version_and_models_and_nothing_private` and ShellTests' Report a problem pin now
refuse the hotkey: the test text "F24" and the key as the header actually shows it. (ShellTests' Report pin and the header pin use an unstarted
shell, which shows its default `RightCtrl` rather than the file's `F24`, so they check the header's own last segment; the Email support pin
opens Settings, where the shell shows `F24`, and checks both.) `The_header_keeps_the_hotkey_and_the_support_models_line_does_not` pins that the header still ends with the key and that
`ModelsLine` is the header without it. The sample models line in the Report-link pin no longer has a hotkey.

**Mutation.** `ReportProblem` put back to pass `Status.Models` (the hotkey back in the email line): **observed failing**: the Email support pin and
ShellTests' Report a problem pin. Restored.

## Simple and Advanced views for models (Dictum 1.2, 21/10/2026, branch feat/simple-view)

**The product decision (Kyle, 21/10/2026, relayed in the lane brief):** users do not understand base.en vs small.en vs q5_1 vs large-v3-turbo. Settings > Models and the tray panel's pickers default to a Simple view that a 95-year-old can understand, with explainers; today's full detail stays as an Advanced view one click away.

**What was built.**

1. `ui.view` (`UiSettings` in `AppSettings.cs`, bound in `SettingsLoader.BindUi`, documented in the default file): `"simple"` (default, also when absent) or `"advanced"`; any other value warns and uses `"simple"`. The Simple | Advanced segments at the top of Settings > Models write it through `SettingsPage.Write` (the shell's settings writer), so the reload redraws the page and the tray panel and the next session opens in the same view.
2. `SimpleView` (`src/Dictum.Core/Settings/SimpleView.cs`): the tiers, the clean-up rule and every PROPOSED string, in one place for both surfaces. No download logic: a tier is a catalogue entry, chosen through `ModelCoordinator.Select`; "Add AI clean-up" and the Tidy row go through `SelectCleanup` / `SelectRulesOnly`.
3. Settings > Models (`ModelPages.cs`): the Simple view replaces the two catalogue lists with the tier rows and the Tidy row, built by the existing `CatalogueRow` / `ModelRow` primitives (so the 1.1.1 "Downloading <n>%" and "Waiting to download" states, the disabled button and its accessible name come with them). Only the shown view's rows are built; the compare-before-set key now includes the view and the machine class, so a progress tick still rebuilds nothing and keyboard focus stays on its button. Both views keep the Language card, the model folder and the Storage link; Advanced alone has the catalogue lists, decoding, the file choosers, "Rule-based tidy only" and the cloud pointer.
4. Tray panel (`FlyoutWindow.cs`): in Simple, the speech picker lists the tier names and the clean-up picker "Tidy my words" (plus "Add AI clean-up" while the rule-based tidy is in use), each with the existing "(download)", "(downloading)" and "(waiting to download)" marks, and a "Custom: <name>" entry first for anything else in use. Advanced is unchanged. The pickers keep `SetItems`' compare-before-set.

**Tier mapping (machine class = `WhisperEngine.GpuMachine`, as the catalogue's recommendation uses it).** Read from the code, not invented: Standard is `ModelCatalog.Bundled` (= `ModePresets.LightSpeech`); on a CPU, More accurate is `ModePresets.FullSpeechCpu`, which is also the model `ModelCatalog.Recommend(false)` offers as "more accurate, a few seconds slower"; on a GPU, More accurate is `ModelCatalog.Recommend(true)` and Most accurate is `ModePresets.FullSpeechGpu`. The CPU's Most accurate is medium.en, the largest English entry whose catalogue trade-off does not say "needs a GPU" ("high accuracy; wants a GPU or a fast desktop CPU").

| Tier | CPU only | GPU machine |
|---|---|---|
| Standard | base.en | base.en |
| More accurate | small.en | medium.en |
| Most accurate | medium.en | large-v3-turbo |

The brief's example for a GPU (base.en / small.en / large-v3-turbo) differs in the middle tier: the code already recommends medium.en to a GPU machine, so medium.en is used. The order follows the Full preset. One measurement supports it, weakly: the real-voice calibration result of 15/10/2026 (above; one voice, ten sentences, about a hundred words, so one error is about one point, and the word error rules of that build, since changed) had large-v3-turbo greedy at 4.7% against medium.en beam at 6.3% and greedy at 9.4%. (Corrected by the lens of 21/10/2026: this section first said no such measurement existed.)

**Clean-up and the Mode presets.** The Mode presets (Light, Full) set speech and clean-up together; the tiers are speech only, so the Mode page and the tray's Mode segments do not express the same choice and Simple could not reuse them as its mechanism. Simple reuses their constants instead (above), and the Mode segments stay in both views unchanged. "Tidy my words" is in use while `llm.provider` is `none` or `local` with the recommended clean-up model (`CleanupCatalog.Recommend`); from anything else, its **Use** selects that model when it is on this PC, else the rule-based tidy. A cloud provider is never offered in Simple. One chosen in Advanced shows as a "Custom: <provider>" row with no button, because hiding a provider that sends text off the PC would be less honest than naming it.

**Not built, and why: the "Off" clean-up row.** The brief asked for "Off" with the sub-line "Paste exactly what was heard." Dictum has no such mode: `llm.provider = none` is the rule-based tidy (filler removal, punctuation, capitals), and only a non-English dictation gets whitespace-only tidying (`TranscriptCleaner.Plain`). Shipping "Off" over `none` would make the sub-line untrue; making it true needs a new provider value through `TranscriptCleaner.Create`, `SettingsLoader`, the status lines and the docs, which is pipeline work outside this lane's files. Recommendation: a small follow-up lane adds `llm.provider = "off"` (whitespace-only, reusing `DeterministicCleaner.WhitespaceOnly`), then Simple gains the row.

**Deviation from the brief's explainer.** The brief's text sends people to "Settings > Test my setup", which does not exist in this build (no such text on main or on any remote branch, checked 21/10/2026). The explainer names the existing calibration test instead, with a **Calibrate my voice** button under it that opens the same window as the tray panel's button. When a "Test my setup" page lands, the sentence and the button can point there.

**Ceilings marked `// simplification:`.** First commit: `SimpleView` mapped only the English ladder. Retired by the lens fixes below (multilingual tiers and the guard).

**File paths in the brief.** The brief named `src/Dictum.Core/Transcribe/ModelCatalog.cs` and `src/Dictum.Core/Llm/CleanupCatalog.cs`; both catalogues live in `src/Dictum.Core/Settings/`. Neither was changed: the plain names live in `SimpleView`, so the catalogues needed no display-name additions.

**PROPOSED strings (21/10/2026), for the product owner's approval; none is approved:**

- Segments: "Simple", "Advanced"; accessible names "Models view: Simple", "Models view: Advanced".
- Help line under them: "Simple shows what each choice means. Advanced shows every model and its details."
- Explainer: "Dictum turns your voice into text on this PC. The speech model does the listening. Standard works for most people. If words come out wrong, try a more accurate model, or press Calibrate my voice to see which hears you best." (the brief's wording, with "run the test in Settings > Test my setup" replaced as explained above). The button under it reuses the existing label "Calibrate my voice".
- Tier names: "Standard", "More accurate", "Most accurate".
- Tier lines: "Included. Works well for most people."; "Works well for most people." (Standard when its model did not come with this copy, so "Included" would be untrue); on a GPU machine "Bigger download, a bit slower." and "Best results. Needs a strong PC or patience."; on a CPU see the lens fixes below.
- Tier state for a file not on this PC: its size alone (for example "487 MB"; first commit "487 MB download", changed by the lens fixes). The other states reuse the existing words: "In use", "Downloaded", "Downloading <n>%", "Waiting to download", and the buttons "Use", "Download…", "Downloading…", "Waiting…", "In use".
- Tier buttons' accessible names: "Use <tier>", "Download <tier>" (for example "Download More accurate"); a busy one reuses the 1.1.1 pattern with the tier name: "More accurate is already downloading".
- Custom row: "Custom: <name>", with the line "In use. Not one of the choices above." (first commit "In use · Chosen in Advanced view", changed by the lens fixes).
- Clean-up row: "Tidy my words (recommended)", line "Fixes punctuation and removes ums and ahs. Runs on this PC."; state "In use, with AI clean-up" while the recommended model tidies; its button's accessible name "Use Tidy my words".
- Tray clean-up picker (Simple): "Tidy my words"; "Add AI clean-up" reuses the existing label, with the existing marks.

**Pins** (`tests/Dictum.Tests/SimpleViewTests.cs`, headless as `UiWindowTests`; no model is downloaded: an in-memory server holds the one download). Each was observed failing with its mutation applied and passing without it; the PR body has the table. Two existing UI tests that check Advanced catalogue rows now set `"ui": { "view": "advanced" }` (`NoSilentCancelUiTests.The_downloading_model_shows_as_downloading_with_a_disabled_button`, `ModelFolderTests.Settings_models_marks_the_bundled_model_as_included`); their assertions are unchanged. The test shell (`Scratch.Shell`) gained a `gpuMachine` option for the GPU mapping pin.

**Not verified:** the views on a real desktop, at 150 % scaling, or in dark mode (headless only); screen-reader output (the accessible names are pinned, not heard); the GPU mapping on a real GPU machine (pinned with a stand-in machine class); a real download; tray mode, never run on the development PC.

### Lens fixes (21/10/2026), first lens on 1316ea6

The lens on PR #23 (head 1316ea6) returned Needs changes with three majors, six minors and one nit, and no blockers. All ten are fixed on the same branch, with the orchestrator's decisions where it gave one:

1. **Major: a user whose language is not English could be switched to an English-only model, and the multilingual model in use vanished from Simple.** Orchestrator's decision: when the text is not plain English speech (`whisper.language` is not `en`, or Translate is on), the tiers map to multilingual models. Standard is `small` and More accurate is `medium`. On a GPU, Most accurate is `large-v3-turbo`, or `large-v3` when translating, because turbo cannot translate (OpenAI, as `WhisperLanguages` cites). On a CPU there is **no multilingual Most accurate**. The catalogue has nothing above `medium` whose trade-off line does not say "needs a GPU", and a second `medium` row would repeat More accurate, so the CPU shows two rows.

   As a guard, every tier carries `WhisperLanguages.Problem` for its model with the language settings in force. A tier with a problem gets no button on the page, only its line, and is left out of the tray picker unless it is the model in use. For an English-only model the line is the PROPOSED "English only: switch to Advanced to choose a model for <language>." (or "…a model that detects the language." for Detect automatically); any other problem (Cantonese, which only the large-v3 models have) shows the existing `Problem` sentence. The `// simplification:` is retired.
2. **Major: the CPU lines understated the measured slowness.** README "Measured speed" (15/10/2026, a 10.46 s clip, CPU forced) gives base.en 1.90 s, small.en 6.43 s and medium.en 20.1 s; calibration's ceiling is 2.5 s per 10 s. The lines now follow the machine class. CPU More accurate: "Bigger download. Takes a few seconds longer on this PC." CPU Most accurate, and the multilingual `medium` on a CPU: "Slow without a graphics card: can take longer than you spoke." The GPU keeps "Bigger download, a bit slower." and "Best results. Needs a strong PC or patience." The multilingual `medium` was not timed; the same line is used for it because it is the same size as medium.en (inferred, not measured).
3. **Major: "Download More accurate" opened the catalogue question** ("Download ggml-small.en.bin (487 MB) from huggingface.co? …"). That text is not approved copy. A Simple choice now passes its tier name to `ModelCoordinator.Select(name, plainName)`, which asks through two new `IModelPrompts` members with default implementations (`ConfirmPlainDownload`, `ConfirmPlainDownloadWhileBusy`; the defaults ask the catalogue question, so other stand-ins are unchanged).

   The question reads "Download <tier> (<size>)? It comes from the model makers' official download page and is checked before use.", followed by the existing low-space warning when there is one. The busy question names both models by their tier names where the Simple view started them. The "already downloading" balloon and the Stop balloon also use the tier name. Advanced keeps today's wording.

   **Known Simple gaps, for a later lane** (deliberately not changed here):
   - Calibration still speaks in catalogue terms (for example "large-v3-turbo-q5_0 · beam" and error counts).
   - "Add AI clean-up" still asks the catalogue clean-up question (file name, host, licence line).
4. **Minor: "In use · Chosen in Advanced view" was untrue** for a model applied by calibration, a Mode preset, the first run, `--model` or `DICTUM_MODEL_PATH`. The Custom line is now "In use. Not one of the choices above."
5. **Minor: with text that is not English the clean-up is skipped** (`TranscriptCleaner.Plain`: whitespace only), so the Tidy line now reads "Tidies spacing only for <language>.", or "Tidies spacing only while the language is detected automatically." for Detect automatically. "In use, with AI clean-up" is not shown then, because no clean-up model runs.
6. **Minor: the closed tray pickers cut long entries off** ("Custom: large-v3-turbo-q5_0"). Both model pickers now use an item template whose text ends in an ellipsis (`TextTrimming.CharacterEllipsis`).
7. **Minor: choosing "Tidy my words" in the tray was unpinned.** It is now pinned.
8. **Minor: `A_progress_refresh_leaves_the_tray_pickers_alone` ran only in Simple.** It is now a Theory over both views.
9. **Minor: the claim that no measurement compared large-v3-turbo with medium.en was wrong.** It is corrected above, citing the real-voice calibration result with its caveats.
10. **Nit: "1.5 GB download · Bigger download, …" repeated "download".** A tier not on this PC now shows its size alone ("1.5 GB · Bigger download, a bit slower.").

**PROPOSED strings added or changed by these fixes** (none approved):
- "Bigger download. Takes a few seconds longer on this PC."
- "Slow without a graphics card: can take longer than you spoke."
- "English only: switch to Advanced to choose a model for <language>."
- "English only: switch to Advanced to choose a model that detects the language."
- "Download <tier> (<size>)? It comes from the model makers' official download page and is checked before use." (plus the existing low-space warning)
- Busy question, Simple: "<running> is downloading (<n>% done). Download <tier> (<size>) after it, or stop <running> and download <tier> now? Stopping deletes what <running> has downloaded so far." then a blank line and "It comes from the model makers' official download page and is checked before use."
- Busy question, Simple, without room to wait: "<running> is downloading (<n>% done). There is not enough space to download it after <running>. Stop <running> and download <tier> (<size>) now? Stopping deletes what <running> has downloaded so far." then the same sentence.
- The existing busy buttons and balloons, with the tier name in place of the model name: "Stop it and download <tier>", "<tier> is already downloading (<n>%).", "<tier> is already waiting to download.", "Download of <file> stopped: you chose <tier>."
- "In use. Not one of the choices above." (replaces "In use · Chosen in Advanced view")
- "Tidies spacing only for <language>."
- "Tidies spacing only while the language is detected automatically."
- The tier state for a file not on this PC is its size alone (replaces "<size> download").

**Not verified (these fixes):** the plain question in the real question window (only its text is pinned, through the test dialogs); the ellipsis at 150 % scaling or with other fonts (headless only); the multilingual `small` and `medium` speed on a CPU.

## Clean-up Off: paste exactly what was heard (Dictum 1.2, 21/10/2026, branch feat/cleanup-off)

**Why.** The Simple view brief (above) asked for an "Off" clean-up row, "Paste exactly what was heard.", which was left out because `llm.provider = none` is the rule-based tidy and no off mode existed. This lane adds the mode, stacked on `feat/simple-view`.

**What was built.**

1. A fifth provider value, `llm.provider = "off"` (`LlmSettings.Off`, accepted by `SettingsLoader.BindLlm` in any case like the others, documented in the default settings file). `none` keeps its name and its behaviour.
2. `TranscriptCleaner.Create` checks `off` first and returns `TranscriptCleaner.Off()`: no client, so no model load, no request and no reply check. It is checked before the prompt file is read and before the reply checks are built (`PersonalCleanup.Checks`), and before the not-English skip, so the status names what the user chose. `Off()` reuses the whitespace-only step that text in another language already gets (`DeterministicCleaner.WhitespaceOnly`, `plain: true`): ends trimmed, runs of spaces, tabs, CR and LF made one space (other whitespace inside the text, such as a no-break space, is kept). No second implementation was written. Because it is plain, `SegmentJoin` keeps whisper's case at a hands-free part's boundary (`keepCase: cleaner.IsPlain`), so no letter changes case anywhere. `IsPlain`'s comment now says it covers both cases; every reader of it was checked (the CLI's "whitespace-only tidy" line, the pipeline's keepCase, Settings > Clean-up's `OfflineOnlyIsWhyNothingIsSent`, which is false for off anyway because `OfflineRefusal` has nothing to refuse, and Settings > About's cleanup-error line, which reads `local` only).
3. **Spoken commands.** Read from the code: "new line" and "new paragraph" are applied only by the rule-based tidy (`DeterministicCleaner.SpokenBreak`) and by a model following its prompt; `SegmentJoin.LeadingBreak` puts a break between hands-free parts only when the clean-up removed the opening command, which off never does. So with off, every spoken command ("new line", "new paragraph", "comma", "full stop", "scratch that"…) is typed as the words heard. Pinned in the pipeline, not only in the cleaner.
4. Settings > Models: Simple gains an **Off** row under "Tidy my words (recommended)" and its Add AI clean-up button (a separate panel, so that button stays directly under the Tidy row); it is its own row, never "Custom". Advanced gains an "Off (paste exactly what was heard)" button beside "Rule-based tidy only", and its "In use:" line reads "In use: no clean-up (paste exactly what was heard)". `ModelCoordinator.SelectOff` writes the key through the same `SetValues` path as `SelectRulesOnly`; `CurrentCleanupName` names it "no clean-up" (lens on d6b4ea8: "off" in the tray line read as Dictum being off). The tray panel's clean-up picker gains "Off" in Simple and "Off (paste exactly what was heard)" in Advanced, so an off in use never shows as a "· cloud" entry.
5. The pipeline's log line says "the whitespace-only tidy" for a plain clean-up instead of "the rule-based tidy" (it was already wrong for text in another language).

**Privacy lines: reused, none new.** `NothingLeaves` is `!usesModel || …` (`CloudPolicy.NothingLeaves`), and the off cleaner has no client, so it is true: the tray panel shows "Nothing leaves this PC" with its lock, and Settings > Privacy shows the approved "Nothing leaves this PC: speech recognition and clean-up both run here." Settings > Clean-up shows the cleaner's status and none of the cloud "On" lines. **For the product owner:** with clean-up off, "clean-up both run here" is still true in the narrow sense (the whitespace step runs here), but a reader may find it odd. The approved line is not reworded; if a separate line is wanted for off, it needs the product owner's wording.

**Open decision, not built: corrections, profanity filter and snippets.** They run after every clean-up (SPEC §22 order) and still run with off, because the user set each of them up and none is on by default except what the user adds (the profanity filter's default is `off`). If "exactly what was heard" should also skip them, that is a one-line change in the pipeline's finish step; recommendation: keep them, because a correction or snippet the user wrote is their own intent, not Dictum's tidying.

**PROPOSED strings (21/10/2026), for the product owner's approval; none is approved:**

- Simple row: "Off", line "Paste exactly what was heard."; in use: "In use · Paste exactly what was heard."; its button's accessible name "Use Off".
- Advanced button and Advanced tray entry: "Off (paste exactly what was heard)".
- Advanced "In use:" line: "In use: no clean-up (paste exactly what was heard)".
- Tray Simple entry: "Off".
- Tray panel models line and the icon's tooltip, in the clean-up place: "no clean-up" (for example "base.en · Vulkan · no clean-up · F9" and "Dictum · base.en · Vulkan · no clean-up · F9"), from `ModelCoordinator.CurrentCleanupName`.
- Status (Settings > Clean-up's current line, About's "Clean-up:" line, the self-test and the log): "off: the text is pasted as whisper wrote it, whitespace tidied only (llm.provider = off)".
- Log line (not shown in the UI): "Clean-up is off (llm.provider = off): the text is pasted as whisper wrote it, with whitespace tidied only."

**Pins** (`tests/Dictum.Tests/CleanupOffTests.cs`; one existing assertion in `SimpleViewTests.The_simple_view_never_shows_cloud` now expects "Off" after "Add AI clean-up" in the tray list). Each was observed failing with its mutation applied and passing with it reverted:

| Mutation | Pins that failed |
|---|---|
| M1: `"off"` removed from `SettingsLoader`'s provider list | `The_off_provider_round_trips`, `The_simple_rows_switch_between_tidy_and_off`, `The_advanced_list_offers_off`, `The_tray_pickers_offer_off`, `The_privacy_lines_say_nothing_leaves` (5 of 20) |
| M2: `off` returns the rule-based tidy with the off status (exactly `return RulesOnly(OffStatus);`; `return RulesOnly("off");` also fails the privacy pin, M2b below) | all 10 rows of `Off_pastes_exactly_what_was_heard`, `Off_builds_no_validator_loads_no_model_and_sends_nothing`, `Off_wins_over_the_language_skip`, `The_simple_rows_switch_between_tidy_and_off` (13 of 20) |
| M3: the `off` branch in `Create` removed (falls through to the default `anthropic` branch) | the same 13 plus `The_privacy_lines_say_nothing_leaves` (14 of 20) |
| M5: the pipeline's `keepCase` forced to false | `A_hands_free_part_with_off_is_typed_as_heard` |
| M6: the Simple Off row's Use writes the rule-based tidy | `The_simple_rows_switch_between_tidy_and_off` |
| M7: the tray's Off entry writes the rule-based tidy | `The_tray_pickers_offer_off` |
| M8: the Advanced Off button writes the rule-based tidy | `The_advanced_list_offers_off` |
| M9 (`none` unchanged): `none` returns `Off()` | the existing `LanguageTests.Text_in_another_language_gets_whitespace_tidied_only_never_the_english_rules`, all 10 rows of `Off_pastes_exactly_what_was_heard` (their `none` column) and `The_simple_rows_switch_between_tidy_and_off` (12 of 72 in the cleanup, language, LLM, rules and Simple view tests) |
| M11: off shown as a "Custom" row as well | `The_simple_rows_switch_between_tidy_and_off` |
| M12: the tray's Simple picker selects Tidy while off is in use | `The_tray_pickers_offer_off` |

**Not verified:** the rows on a real desktop, at 150 % scaling or in dark mode (headless only); a screen reader (the accessible name is pinned, not heard); a real dictation through whisper with off (the pipeline pin uses a fake transcriber); tray mode, never run on the development PC.

### Merge of feat/simple-view 787b300 and lens fixes (21/10/2026), lens on d6b4ea8

The lens on PR #27 (head d6b4ea8) returned Needs changes: no blockers or majors, three minors and one nit. All four are fixed in the same round as the merge of `origin/feat/simple-view` at 787b300 (lane A's lens fixes). The merge was a plain merge, not a rebase.

**Merge.** The Off row sits with lane A's new rows and guards. It has no language dependency, because it never runs a clean-up, so it is offered for every language, unlike the tiers that the language rules out. That is pinned by `Off_is_offered_for_every_language`, run with French and with Detect automatically. The Custom clean-up row uses A's new line ("In use. Not one of the choices above.") and still never shows for off. The tray's Off entries use A's ellipsis item template, which applies to every entry of both pickers. That is pinned by `The_tray_line_says_no_clean_up_and_the_off_entry_is_trimmed`. A's new `Choosing_tidy_my_words_in_the_tray_switches_to_the_rule_based_tidy` now expects "Off" at the end of the Simple tray list. Its other assertions are unchanged.

1. **Minor: the tray line read as Dictum being off.** `ModelCoordinator.CurrentCleanupName` returned "off", so the tray panel's models line and the icon's tooltip read "base.en · Vulkan · off · F9". It now returns "no clean-up" (PROPOSED). The Advanced "In use:" line becomes "In use: no clean-up (paste exactly what was heard)".
2. **Minor: two comments said "plain" means "not English".** The `keepCase` comments in `SegmentJoin` and `DictationPipeline` now say "text that is not English (SPEC §23) or clean-up off (§5)". So does `SegmentJoin`'s summary.
3. **Minor: the M2 row did not name its mutation.** The row now names it exactly (`return RulesOnly(OffStatus);`, 13 of 20 on d6b4ea8). M2b below confirms the lens's figure for `RulesOnly("off")`.
4. **Nit: the whitespace wording was too broad.** README and SPEC now say "runs of spaces, tabs, CR and LF become one space". Other whitespace inside the text, such as a no-break space, is kept: `DeterministicCleaner.WhitespaceOnly` collapses `[ \t]+` after turning CR and LF into spaces, and trims the ends.

**Mutations for these fixes.** Run on the merged tree with 23 pins in `CleanupOffTests`. Each mutation failed the pins listed and was restored byte for byte.

| Mutation | Pins that failed |
|---|---|
| M2b: `off` returns `RulesOnly("off")` | 14 of 23: the 13 of M2 plus `The_privacy_lines_say_nothing_leaves` |
| M13: the tray name back to "off" | `The_advanced_list_offers_off`, `The_tray_line_says_no_clean_up_and_the_off_entry_is_trimmed` |
| M14: the Off row shown only for English text | both cases of `Off_is_offered_for_every_language` |

**PROPOSED strings changed by these fixes** (none approved):
- Tray panel models line and icon tooltip: "no clean-up".
- Advanced "In use:" line: "In use: no clean-up (paste exactly what was heard)" (replaces "In use: off (paste exactly what was heard)").

## Onboarding: the Welcome to Dictum window (Dictum 1.2, 21/10/2026, branch feat/onboarding, stacked on feat/simple-view)

**The product decision (Kyle, 21/10/2026, relayed in the lane brief):** onboarding must explain how Dictum works, ask where to keep the model files (Kyle is low on C: on his VM; the models take 200 MB to 10 GB), test the microphone (a VM with no microphone passthrough showed only "Recording"), and explain that accents and microphones differ, so people try a more accurate model or the calibration test when words come out wrong. A 95-year-old must be able to complete it.

**What was built** (SPEC §26, now §27).

1. `OnboardingWindow` (`src/Dictum.UI/Views/Onboarding/`): one window, six steps (Welcome, Model files, Speech model, Your hotkey, Microphone, Try it), a step line and dots, Back and the step's own buttons, larger text than the other windows, 640 × 420 DIP client area (corrected by the lens on 6bacce9, below). It replaces the speech picker, the clean-up picker and `HotkeyCheckWindow` at first run.
2. `OnboardingFlow`: the writes, apart from the window, so the shell and the tests drive the same code. Each step writes its own key when completed, only when it changed (`models.folder`, `whisper.model` with the clean-up answer, `hotkey`, `audio.device`). `SkipRest` (Esc, closing the window) writes the speech answer with today's defaults and nothing else.
3. `ModelCoordinator.FirstRun` is split into `PlanFirstRun` (the GPU probe, the recommendations, whether base.en is on this PC) and `AnswerFirstRun` (the writes), with `FirstRun` unchanged in behaviour on top of them (its pins, in ModelFolderTests, FixBatchModelTests, NoVulkanCrashTests, ModelCoordinatorTests and ModelFolderSafetyTests, all pass). The window's speech step calls the two halves.
4. `ModelCoordinator.SetModelsFolder` gains `reloadAfter` (false for the window: nothing is loaded yet, and a reload before `Start` loads the configuration would start a model load and a hotkey listener mid-wizard) and a shared `FolderRefusal` (the bundled folder, as before, and now a device path, below).
5. `AppRules.Onboards` (the window shows exactly when the first-run hotkey check did: the speech choice is open and no model override) and `AppRules.AsksFolder` (no folder named), beside `FirstRunQuestions`, which is unchanged.
6. `IShellDialogs.HotkeyCheck` is replaced by `IShellDialogs.Onboard(OnboardingFlow)`. `AppShell.Start` opens the window instead of `FirstRun` + `FirstRunHotkeyCheck` (removed), and calls `OnboardingFlow.BeginTryIt` at its end, once the hotkey listens.
7. `HotkeyCheckWindow` is gone; its strings live in `HotkeyCheck` (Dialogs.cs) and are reused word for word by the hotkey step and by Settings > General (`HotkeyCheck.MacNote`, one reference changed in GeneralPages.cs).

**Decisions made in this lane, and why.**

- **No clean-up question in the window.** The brief's six steps have none, and "Skip everywhere leaves today's defaults". With base.en (every package) the first run already wrote the rule-based tidy without a question; without base.en the window now does the same, instead of the clean-up picker. The Simple view's "Tidy my words" and "Add AI clean-up" are where AI clean-up is chosen. The clean-up picker stays for a seeded settings.json that names `whisper.model` but leaves the clean-up choice open, without base.en (no window then).
- **Interruption.** The brief: "a crash mid-way does not repeat finished steps; use the existing FirstRunQuestions mechanism and extend it minimally". The window shows while settings.json names no `whisper.model`; the folder step only while it names no folder. So a start after the folder step skips it, and a start after the speech step opens no window at all. simplification (`AppRules.Onboards`): the hotkey and microphone steps are not shown again after a crash that follows the speech step, exactly as the hotkey check was not before 1.2. Ceiling: a person whose PC stops between steps 3 and 6 does not see steps 4-6. Trigger to retire: a report of it; then record the steps finished (a settings.json key, which needs the loader to know it).
- **A folder chosen first makes a sparse settings.json.** The folder step writes `models.folder` before the speech step, so with no settings.json yet the file starts with that key alone (no `_doc` comments); the speech step then adds its keys, as for a seeded file. The loader takes defaults for the rest. Ceiling: that file lacks the comment block. Trigger: a request for it; then write the full default file at the folder step without `whisper.model`.
- **Device paths refused.** The brief: "refuse the bundled folder and device paths; show their existing messages". The bundled folder's sentence exists (`BundledFolderRefused`); a device path had none in the UI (the settings load ignores such a `models.folder` with a log warning), so `DevicePathRefused` is new and PROPOSED. `SetModelsFolder` uses the same rule, so Settings > Models' Browse… now refuses such a path too instead of writing one the load then ignores.
- **Try it watches `PersonalService.Last`.** The paste goes through the normal path into the focused box; "a successful dictation" is the shell recording a pasted dictation, not text appearing in the box (which typing would also do). Any dictation counts, including one pasted into another program while the window is open.
- **Waiting for the model.** Try it says "Getting the speech model ready…" until the model is ready, and only then starts the 30-second "nothing came through" clock ("a reasonable time" in the brief; 30 s is this lane's choice). A speech model that fails to load shows the try-later line at once.
- **The microphone test's floor** (superseded for "Heard you." by the lens on 6bacce9, below: only "Nothing heard." uses it now) is 1e-5 RMS, PR #22's `CaptureBuffer.SoundFloor` ("No sound from mic"), so the test and the warning agree on what silence is. It is a constant here (`MicCheck.SoundFloor`) until main has PR #22; see "Merge with PR #22".
- **The meter and reduced motion.** With `WindowFlags.ReducedMotion` (Windows' "Show animations" setting off) the meter is redrawn at most every 500 ms, showing the loudest level since, instead of every 50 ms.
- **The capture runs while the step is on screen** (limited by the lens on 6bacce9, below: it closes about 5 s after "Heard you." or after a minute), not only until the first result, so a person who unmutes after "Nothing heard." sees it turn into "Heard you."; it is stopped on every way out of the step.
- **Calibrate my voice closes the window** and opens the calibration window, so the two do not compete for the person's attention.
- **Size at 150 %.** Superseded: the first version took 470 DIP as the whole window height and a 32 DIP taskbar; see the lens fixes below.

**Merge with PR #22 (feat/silence-warning, not on main yet).** Three things are written to match it and must be joined when the orchestrator asks for `git merge origin/main`: `OnboardingText.NoMicrophone` becomes a reference to `NoSoundText.NoMicrophoneBalloon` (same words today); `MicCheck.SoundFloor` becomes `CaptureBuffer.SoundFloor`; `OnboardingText.NothingHeard` repeats the advice of `NoSoundText.SilentBalloon` after a different opening ("Nothing heard." is the brief's) and stays its own constant unless the product owner wants one sentence for both. The microphone step's note ("If Dictum hears nothing when you dictate, it will tell you.") is true only once PR #22 is on main: it is the "No sound from mic" warning. Merge PR #22 before this lane, or the note is untrue in a build of this branch alone.

**PROPOSED strings (21/10/2026), for the product owner to approve; word for word.**

| Where | String |
|---|---|
| Window title and header | Welcome to Dictum |
| Buttons | Back · Next · Finish · Skip |
| Step line | Step {n} of {total}: {name} (names: Welcome · Model files · Speech model · Your hotkey · Microphone · Try it) |
| 1 heading | How Dictum works |
| 1 text | Dictum types what you say. Hold down your hotkey, speak, then let go: your words appear wherever you were typing, in any program. Everything happens on this PC. |
| 1 text | The next few steps take a minute or two. Press Next to begin. |
| 2 heading | Where to keep the model files |
| 2 text | Dictum keeps its speech models on this PC. The included one is about 150 MB; adding more models can take several gigabytes more. |
| 2 free space | {drive} has {size} free (for example "C: has 41.0 GB free") |
| 2 buttons | Choose another drive or folder… · Use the suggested folder |
| 2 field name (screen readers) | Folder for the model files |
| 2 refusal (new) | Dictum cannot use a folder named that way. Choose a folder on a drive letter or a network share. |
| 3 heading | Speech model |
| 3 with base.en | The Standard model is included and ready. |
| 3 without base.en | Choose how carefully Dictum listens. Bigger models are more accurate but slower. A model that is not on this PC yet is downloaded from huggingface.co when you reach the end of these steps. You can change this later in Settings > Models. |
| 3 link | Show every model |
| 5 heading | Test your microphone |
| 5 lines | Say a few words. · Heard you. |
| 5 silence | Nothing heard. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on. |
| 5 no microphone (PR #22's words) | No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again. |
| 5 note | You can skip this test. If Dictum hears nothing when you dictate, it will tell you. |
| 5 meter name (screen readers) | Microphone level |
| 6 heading | Try it |
| 6 waiting | Getting the speech model ready… |
| 6 instruction | Click in the box, hold {hotkey}, say a sentence, then let go. |
| 6 box name (screen readers) | Practice box |
| 6 explainer | Everyone's accent, voice and microphone are different. The Standard model works well for most people. If words come out wrong, choose More accurate in Settings > Models, or press Calibrate my voice to find which model hears you best. |
| 6 nothing | Nothing came through. You can try again any time: Settings > Help explains how. |

Reused, not new: the hotkey step's heading, text, Mac note and buttons (`HotkeyCheck`, unchanged since 15/10/2026); the tier names and sub-lines (`SimpleView`, PROPOSED in lane A); "Choose model file…" and "· recommended" (the first-run picker); "Microphone", "System default", "(default)", "(not connected)" and "The microphone could not be opened: {reason}" (Settings > Microphone); "Calibrate my voice" (`ModelsPage.CalibrateLabel`); `BundledFolderRefused` and the network-folder note (Settings > Models).

**The product owner's answers (relayed by the orchestrator, 21/10/2026), applied in the merge round.**

1. Step 1 drops "Nothing you say is sent anywhere." and keeps "Everything happens on this PC.", which is true whatever the cloud option says; cloud clean-up has its own consent step.
2. Settings > Help will exist (lane C's PR #24, a Settings page titled "Help", merged before this one). Nothing re-runs this window, so the try-later line says where to read how: "Nothing came through. You can try again any time: Settings > Help explains how." (PROPOSED).
3. Step 2 gives no exact total: "adding more models can take several gigabytes more" (PROPOSED). The basis, read from the catalogues on 21/10/2026 (inferred by summing their byte sizes, not measured on disk): every speech model together is 13.19 GB, every clean-up model 8.93 GB, and the largest of each (large-v3, 3.10 GB; gemma-3-4b, 3.16 GB) 6.25 GB together.

**Merged with feat/simple-view at 787b300 (lane A's lens fixes).** No code conflict; DECISIONS kept both entries. Lane A's two new `IModelPrompts` members (`ConfirmPlainDownload`, `ConfirmPlainDownloadWhileBusy`) sit beside this lane's `FirstRunPlan`; the window asks neither (its speech pick downloads at startup, as the first-run picker's did). The speech step now uses lane A's language-aware tiers (`SimpleView.Tiers(gpu, language, translate)` with the language settings.json names): multilingual models when it is not plain English, and a tier whose model the language rules out is left out. `SimpleView.DownloadSize` is gone in lane A's fixes; a tier not on this PC shows its size in brackets after its line. Not changed: with base.en on this PC and a seeded language that is not English, the first run still writes base.en (as before 1.2, with the existing language warning after it) and the step says "The Standard model is included and ready."; a follow-up could ask the speech question in that case.

**Pins and mutation checks** (`tests/Dictum.Tests/OnboardingTests.cs`, headless). Each pin was observed failing on the reverted behaviour: the mutation was applied alone, the test project rebuilt, the pin run, the file restored (26 mutations on 386fc76, and one more after the merge with 787b300, 21/10/2026).

| Pin | Mutation (reverted behaviour) | Observed |
|---|---|---|
| Each_step_renders_and_advances_to_Try_it | leaving the microphone step does not end steps 1-5 (`FinishFirstSteps` removed) | failed: `Assert.True` (FirstStepsDone) |
| Back_returns_to_the_step_before | Back does nothing | failed: `Assert.IsType` |
| The_folder_step_writes_models_folder_only_when_changed | the "only when changed" guard removed | failed: `Assert.False` (settings.json written on an unchanged Next) |
| The_folder_step_refuses_the_bundled_folder_and_a_device_path | `FolderRefusal` refuses nothing | failed: refusal text not found |
| A_named_model_folder_skips_the_folder_step | the folder step always added | failed: `Assert.DoesNotContain` |
| The_free_space_line_names_the_drive | the drive root shown untrimmed | failed: strings differ |
| The_speech_step_asks_nothing_when_base_en_is_on_this_PC | the choice shown with base.en present | failed: "included and ready" not found |
| Without_base_en_the_speech_step_offers_the_three_tiers | the full catalogue instead of the tiers | failed: collections differ |
| The_hotkey_step_keeps_the_key_pressed_or_the_current_one (moved from DialogTests) | the hotkey never written | failed: strings differ |
| A_seeded_language_that_is_not_English_gets_the_multilingual_tiers (after the merge) | the English tiers whatever the language | failed: collections differ |
| The_microphone_test_hears_a_level_above_the_floor | sound only above 0.5 RMS | failed: strings differ |
| The_microphone_test_gives_the_advice_after_five_seconds_of_silence | no silence result | failed: strings differ |
| With_no_microphone_listed_the_test_says_so_and_opens_nothing | no early return for an empty list | failed: strings differ |
| The_test_capture_is_stopped_on_leaving_the_step (Next, Skip, Back, close) | `Leave` does not stop the capture | failed: all 4 cases (microphone still open) |
| The_microphone_chosen_is_tested_and_written_at_Next | audio.device never written | failed: strings differ |
| The_meter_respects_reduced_motion | the reduced-motion rate removed | failed: the reduced case (40 redraws, not 4); the normal case passes, as it should |
| A_microphone_that_cannot_be_opened_says_why | the reason replaced by "Say a few words." | failed: strings differ |
| Try_it_after_the_first_dictation_shows_the_accent_explainer | (a) the explainer not shown; (b) a dictation never noticed | (a) failed: explainer not found; (b) failed: `Assert.True` (Succeeded) |
| Try_it_says_nothing_came_through_after_thirty_seconds | the 30-second rule removed | failed: `Assert.True` (NothingShown) |
| Try_it_waits_for_the_shell | the instruction shown before the shell is ready | failed: strings differ |
| Esc_mid_way_leaves_the_defaults_and_a_valid_settings_file | closing the window does not skip the rest | failed: `Assert.True` (no usable settings.json) |
| A_first_run_with_every_step_skipped_starts_on_the_defaults | `AppShell.Start` does not call `SkipRest` | failed: values differ (no base.en written) |
| A_seeded_model_or_a_model_override_opens_no_window | `Onboards` ignores the model override | failed: values differ (the window opened) |
| The_window_and_its_folder_step_follow_settings_json | `AsksFolder` ignores whisper.models_dir | failed: `Assert.False` |
| Every_step_fits_at_150_percent_and_names_its_controls | (a) the window 600 DIP high; (b) the folder field's accessible name removed | (a) failed: `Assert.True` (size); (b) failed at the name check |

The first-run pins in ModelFolderTests and ModelFolderSafetyTests (`(0, 0, 1)`: no model question, the hotkey step shown once) pass unchanged: `ScriptedDialogs.Onboard` counts as the hotkey step. The model-picker and question-window pins in DialogTests are unchanged.

**Verified.** Full Release build (`--no-incremental`, no warning lines in its output) and the full suite on this branch before the docs-only DECISIONS edit: 1552 tests, 1529 passed, 23 skipped (gated), 0 failed, with the `Install_copies_models` filter.

**Verified after the merge with 787b300.** Full Release build (`--no-incremental`, no warning lines) and the full suite: 1564 tests, 1541 passed, 23 skipped (gated), 0 failed.

**Not verified.**
- The window on a real desktop: the nested frame of `RunFirstSteps`, the system folder chooser, visible keyboard focus rendering, a real 150 % display, a screen reader reading the live regions. All pins are headless.
- A real microphone or WASAPI capture in the microphone test (a fake capture stands in), and a real paste into the Try it box (the pin drives a dictation through the real controller and pipeline, with a fake clipboard and input sender, and watches `PersonalService.Last`).
- A Dictum build launched in tray mode on this PC (not allowed by the lane brief), so no first run end to end.
- The merge with PR #22 (above).

### Lens fixes (21/10/2026), lens on 6bacce9

The lens on PR #28 (head 6bacce9) returned Needs changes, no blockers: three majors and six minors. All fixed:

1. **(Major) A seeded language that is not English got "The Standard model is included and ready." and base.en**, which is English only. The speech step now asks when settings.json names a language that is not English, or translate (`OnboardingFlow.AsksSpeech` = `Plan.AsksPickers || !Plan.Overridden && SimpleView.Multilingual(...)`), and `AnswerFirstRun` lets a speech pick win over base.en on this PC (`FirstRun` passes no pick when base.en is there, so it is unchanged). Esc in that case keeps base.en: dictation works at once and the existing language warning follows (SPEC §26, now §27).
2. **(Major) "Heard you." fired on background hiss**: any level above 1e-5 counted, about 60 dB below `silence_threshold` (0.01 by default), under which a dictation is discarded as silence. "Heard you." now needs about 3 s in all at or above settings.json's `silence_threshold`; the 1e-5 floor is kept only for "Nothing heard." (five seconds in a row of digital silence, PR #22's rule). A level between the two is neither.
3. **(Major) The window did not fit a 1366 × 768 screen at 150 %**: 470 DIP was the client height, so with the caption and taskbar Back, Next and Finish sat under the taskbar. The client height is now 420 DIP, and on opening the window is clamped to its screen's work area (`Screens.ScreenFromWindow(this).WorkingArea` over the scaling, less the frame's height; `OnboardingWindow.FitHeight`) and moved inside it, the body scrolling. The budget: 1366 × 768 at 150 % is 911 × 512 DIP; the Windows 11 taskbar is 48 DIP (recalled, not measured; the lens brief's 480 DIP budget took it as 32 DIP, the 48 physical pixels of 100 % scaling), leaving about 464 DIP, and a caption of about 32 DIP (recalled) with 420 fits. The first version's DECISIONS line ("640 × 470 so it fits with a 48 px taskbar (32 DIP)") was wrong on both counts.
4. **(Minor) Try it focuses the practice box**, not Finish (a hotkey held without clicking first pasted into the button, and the step still counted it); the try-later line is a polite live region.
5. **(Minor) The hotkey step gives focus to nothing**, as the old hotkey window did, so Space chosen as the hotkey does not also press "Use this hotkey"; Enter (never a hotkey) still presses it.
6. **(Minor) The `// simplification:` comment** on `AppRules.Onboards` was missing; added, with the ceiling (a crash after the speech step means no window on the next start) and the trigger to retire it.
7. **(Minor) The microphone test had no time limit.** It now closes the microphone about 5 s after "Heard you." (`MicrophoneStep.StopAfterHeardMs`) or after 60 s (`StopAfterMs`), keeps its result line, and shows "Test again".
8. **(Minor) Copy.** "The included one is about 150 MB" only when base.en is on this PC, else "The Standard model is about 150 MB to download"; "downloaded … when you finish" is now "when you reach the end of these steps" (the download starts as Try it opens, before Finish); the accent explainer says "The Standard model works well…" only when the model in use is the Standard choice for its language, else "The model you chose works well…"; README "always left complete and valid" is now "always left valid". The PR body records the merge order: #28 merges only after #22 and #24 are on main.
9. **(Minor) A microphone list that throws** (the Windows device list does with the audio service stopped) is caught and logged, and the step shows the no-microphone sentence.

**Changed and new PROPOSED strings (21/10/2026).**

| Where | String |
|---|---|
| 2 text without base.en (new) | Dictum keeps its speech models on this PC. The Standard model is about 150 MB to download; adding more models can take several gigabytes more. |
| 3 without base.en (changed) | Choose how carefully Dictum listens. Bigger models are more accurate but slower. A model that is not on this PC yet is downloaded from huggingface.co when you reach the end of these steps. You can change this later in Settings > Models. |
| 5 button (new) | Test again |
| 6 explainer, another model in use (new) | Everyone's accent, voice and microphone are different. The model you chose works well for most people. If words come out wrong, choose a more accurate model in Settings > Models, or press Calibrate my voice to find which model hears you best. |

**Pins and mutation checks (this round).** Each was observed failing on the reverted behaviour (applied alone, rebuilt, run, restored):

| Pin | Mutation | Observed |
|---|---|---|
| With_base_en_a_seeded_language_that_is_not_English_still_asks | (a) `AsksSpeech` back to `Plan.AsksPickers`; (b) base.en wins over a pick again | (a) failed: "included and ready" found; (b) failed: strings differ (base.en written) |
| Hiss_under_the_silence_threshold_is_not_heard | "Heard you." counts any level above 1e-5 | failed: strings differ |
| The_microphone_test_uses_the_settings_silence_threshold | the threshold fixed at 0.01 | failed: strings differ |
| The_window_height_is_clamped_to_the_work_area | `FitHeight` returns the wanted height | failed: values differ |
| Every_step_fits_at_150_percent_and_names_its_controls (budget corrected) | client height back to 470 | failed: "470 + 32 > 464" |
| Try_it_focuses_the_practice_box | Try it focuses Finish | failed: not the same instance |
| The_hotkey_step_focuses_nothing | the hotkey step focuses its primary button | failed: "focus is on Avalonia.Controls.Button" |
| The_microphone_closes_after_hearing_you_and_can_test_again | no stop after "Heard you." | failed: `Assert.False` (microphone open) |
| The_microphone_closes_after_a_minute | no one-minute stop | failed: `Assert.False` (microphone open) |
| The_copy_follows_what_is_on_this_PC_and_what_was_chosen | (a) "the included one" always; (b) the Standard explainer always | (a) failed: sub-string not found; (b) failed: strings differ |
| A_microphone_list_that_throws_says_no_microphone | the exception not caught | failed: COMException "The audio service is not running." |

The existing microphone pins still use levels at or above 0.01 for "Heard you." (0.02, 0.05, 0.1), so they pass unchanged.

**Verified after the lens fixes and the merge with c7d736d (head b097f03).** Full Release build (`--no-incremental`, no warning lines) and the full suite: 1597 tests, 1574 passed, 23 skipped (gated), 0 failed. The only change after that run is this line.

**Merged with feat/simple-view at deda293 (main's PR #22 inside).** No conflicts. As planned in "Merge with PR #22": `OnboardingText.NoMicrophone` is gone and the step shows `NoSoundText.NoMicrophoneBalloon`, and `MicCheck.SoundFloor` is gone in favour of `CaptureBuffer.SoundFloor`; the words and the value are byte-identical to what this lane shipped, pinned by `The_no_microphone_sentence_and_the_floor_are_PR_22s_unchanged` (observed failing with the floor counted as sound, and with one character of the balloon changed). `OnboardingText.NothingHeard` stays its own constant (its opening, "Nothing heard.", is not the balloon's). The step-5 note ("If Dictum hears nothing when you dictate, it will tell you.") is now true in this tree.

### Lens fixes (21/10/2026), lens on 36fa43e

The lens on 36fa43e (and a854a41) verified the merge and the constant swaps and returned Needs changes on the microphone test and the window clamp. No new strings: every line below reuses existing copy.

1. **(Major) Hiss for a minute ended on "Say a few words." beside a dead meter.** When the test closes at 60 s with nothing decided (hiss between the floor and `silence_threshold`), the line is now "Nothing heard. …" (existing copy).
2. **(Major) `silence_threshold` 0 made digital silence "Heard you."** (the loader accepts 0 to 1, and level 0 is `>= 0`). A level now counts towards "Heard you." only when it is at or above the threshold and above `CaptureBuffer.SoundFloor`.
3. **(Minor) The clamp had no pin on its wiring.** The placement is one pure function, `OnboardingWindow.Fit(height, frame, work, scaling, position) -> (height, position)`, pinned at 150 % on 1366 × 768 with a 48 DIP taskbar (recalled, not measured) and on second screens whose work area starts at a non-zero Y; the Opened handler counts its runs (`FitsOnOpen`, an internal property as the steps' other test seams are), and a pin checks it ran once on opening (the headless platform has a screen).
4. **(Minor) One late tick could count as 3 s of speech.** The gap fed to `MicCheck` is capped at two ticks (`Math.Clamp(now - _last, 0, 2 * TickMs)`). Ceiling: if the timer always fires later than 100 ms, "about 3 s" takes longer.
5. **(Minor)** The 48 DIP taskbar and 32 DIP caption are labelled recalled in the size pin's comment and in the PR's Not verified list; a check on a real 150 % desktop is still outstanding.

**Pins and mutation checks (this round).** Each observed failing on the reverted behaviour (applied alone, rebuilt, run, restored):

| Pin | Mutation | Observed |
|---|---|---|
| The_microphone_closes_after_a_minute (extended: the line is "Nothing heard.") | the 60 s branch leaves the line alone | observed failing: strings differ |
| A_threshold_of_zero_never_hears_digital_silence | the floor guard removed | observed failing: values are equal (Heard) |
| Opening_the_window_fits_it_to_its_screen | the Opened hook removed | observed failing: values differ (0 runs) |
| The_window_is_fitted_to_the_work_area_of_its_screen | `Fit` returns the position unchanged | observed failing: values differ |
| One_late_tick_is_not_three_seconds_of_speech | the gap uncapped | observed failing: strings differ (Heard you.) |

## Integration round on feat/simple-view (21/10/2026): merged main 8419f10; findings from the 787b300 and 86132af lenses

**Merge.** `origin/main` at 8419f10 (PRs #30, #34, #32/#33, #22, #21) merged into the branch at ffb388a with a plain merge: no conflicts. DECISIONS keeps both tails, main's entries first. The public-copy sweep (`PublicCopyNeutralityTests`) passes on the merged tree; a grep for its word list found nothing to reword.

**Simple view (lens on 787b300).**

1. **(Major) Pins for the plain download question.** `A_plain_question_warns_when_little_space_would_be_left` checks the low-space warning reaches the plain question in its own words (and is absent with 2 GB to spare); `A_plain_busy_question_says_when_it_fits_only_if_the_running_download_stops` checks the `canWait = false` wording and its two buttons. The test prompts (`ScriptedPrompts`) now record the plain questions' words and then answer as the interface's defaults do.
2. **(Minor) `GuardLine` is `WhisperLanguages.Problem`.** Its English-only branch could not be reached: `SimpleView.Tiers` offers an English-only model only for plain English without translate, which it can do. The two PROPOSED "English only: …" lines are removed.
3. **(Minor) The busy question's name for the running download was remembered from when it started.** `ModelCoordinator` kept the tier name of each download it started from the Simple view; after a language change (English to es, say) it still called a running small.en "More accurate". The name is now read from the tiers in force when the question is asked (`PlainNameOf`), and nothing is remembered: a file that is no tier any more is named by its short name.
4. **(Minor) "Add AI clean-up" is not offered in the Simple view, page or tray, while the text is not English.** No clean-up model runs on it (`TranscriptCleaner.Plain`), so the download would change nothing. English keeps it, and the Advanced view keeps it with every other detail.
5. **Off.** `TranscriptCleaner.OffStatus` starts "no clean-up:", not "off:", as the tray name does ("Clean-up: off" read as Dictum being off). PROPOSED copy. Owner decision, not changed: for text that is not English, the Tidy row's line ("Tidies spacing only for <language>.") and Off do the same thing (whitespace only).

**Onboarding (lens on 86132af).**

1. **(Minor) The clamp's result had no pin.** `FitToWorkArea` now records `Fit`'s result (`LastFit`) and hands it to `Apply((double Height, PixelPoint Position))`, which holds the two assignments. `Opening_the_window_fits_it_to_its_screen` opens a window taller than the headless screen and below it, and checks the window's height and position equal `Fit`'s result; it then applies a 1366 × 768 at 175 % placement and checks it again.
2. **(Minor) The 1 DIP floor in `Fit`** is kept, with a comment: it only keeps the height positive for a degenerate work area. 1366 × 768 at 175 % leaves about 359 DIP (inferred from the recalled 48 DIP taskbar and 32 DIP caption, not measured). The nav row's height is not a constant (it follows the 17 px text), so it was not used as the floor.
3. **(Nit) `MicrophoneStep.Tick`'s clamp** carries a `// simplification:` naming its ceiling (a timer that always fires later than 100 ms makes "about 3 s" take longer) and its retire trigger (feed the check the capture's own sample time, or a report of the test running long).

**Pins and mutation checks (this round).** Each was applied alone, the test project rebuilt, the pin run, the file restored:

| Pin | Mutation | Observed |
|---|---|---|
| A_plain_question_warns_when_little_space_would_be_left | the plain question is passed no warning | observed failing |
| A_plain_busy_question_says_when_it_fits_only_if_the_running_download_stops | `PlainBusyText` ignores `canWait` | observed failing |
| same | the coordinator passes `canWait: true` to the plain busy question | observed failing |
| The_running_download_is_named_by_its_tier_in_force_when_asked | the name read with the English tiers whatever the language | observed failing |
| same | `ModelCoordinator.cs` reverted to the branch's previous head (the remembered name) | observed failing |
| Add_ai_clean_up_is_not_offered_in_simple_for_text_that_is_not_english | the page's language condition removed | observed failing |
| same | the tray's language condition removed | observed failing |
| CleanupOffTests.Off_wins_over_the_language_skip | `OffStatus` back to "off:" | observed failing |
| Opening_the_window_fits_it_to_its_screen | `Apply`'s two assignments made no-ops (all 43 onboarding tests run) | observed failing (that pin only) |
| same | the `Apply(fit)` call removed from `FitToWorkArea` | observed failing |

## Final round on feat/simple-view (21/10/2026): merged main 500148a (PR #24, in-app Help); Help made true for the Simple view

**Merge.** `origin/main` at 500148a merged with a plain merge (7d8d4be). Three conflicts:
- This file's tail: both kept, main's entries first.
- SPEC: both sides added "## 26." Main's In-app Help keeps §26. Onboarding is now **§27**, and its four references elsewhere in SPEC say §27. The two places above that cite "SPEC §26" for onboarding now read "SPEC §26, now §27".
- `FlyoutWindow`'s class summary: this branch's tier wording and main's "six actions".

**Help and the Simple view (all PROPOSED unless marked).** `HelpText` now holds for a user in the Simple view, which is the default:
- "How Dictum works" gains a paragraph: "Tidy my words, in Settings > Models, does the tidying. Unless the spoken language is set to English (or Translate to English is on), only the spacing is tidied. To paste exactly what was heard, with no tidying at all, choose Off there instead."
- Its cloud sentence now also says what cloud clean-up sends, as "Where does my voice go?" does: "… and it is off unless you turn it on. If you turn it on, the text of each dictation and the words in your dictionary go to the service you chose; your voice still stays here."
- Accuracy: "For English, the speech model that comes with Dictum is a good place to start: it is Standard in Settings > Models. For other languages, Standard is a multilingual model, a download you choose." and "If words often come out wrong, open Settings > Models and choose More accurate, or Most accurate where it is listed. These are bigger speech models: more accurate, but slower, and each one is a download you choose. To see every model by its name and size, choose Advanced at the top of that page." "Where it is listed" is there because a CPU with a language other than English has no Most accurate tier.
- Model files: "Bigger speech models (More accurate and Most accurate) and AI clean-up (Add AI clean-up, shown under Tidy my words when the text is English) are downloads you choose. If you add the largest, most accurate ones, they can take up several GB (gigabytes)."
- A twelfth question, "What does the name Dictum mean?" (PROPOSED), answered by the name line, **APPROVED verbatim by the owner 21/10/2026 22:21 AEDT** (`HelpText.NameLine`).
- `FreeLine` and `FreeLaterNote` are unchanged (approved).

`Every_label_the_help_names_exists_in_the_app` now runs in the Simple view with the rule-based tidy, the state the first run leaves with base.en. It also collects the tray pickers' entries, which the app shows. It checks Standard, More accurate, Most accurate, Advanced, Tidy my words, Off and Translate to English as exact labels, and "Add AI clean-up" as the start of a shown label, because the app appends its size or download mark.

**Nits.**
- `SimpleView.OffersAddCleanup(AppSettings)` is the one predicate for "Add AI clean-up", used by the page and the tray.
- The busy question without room to wait now names the model asked for: "There is not enough space to download <tier> after <running>." in the Simple view, and "… download <model> after <running>." in the catalogue question (both PROPOSED; pins updated).

**Mutation checks (this round).** Each was applied alone, the tests rebuilt and run, and the file restored:

| Mutation | Pins | Observed |
|---|---|---|
| The name line's curly quotes made straight | The_name_question_carries_the_approved_line_verbatim | observed failing |
| Accuracy reverted to "choose a bigger speech model in Settings > Models…" | Every_label_the_help_names_exists_in_the_app, The_help_names_the_simple_view_choices_as_the_app_does | observed failing (both) |
| The app's tier renamed ("More accurate model") | the same two | observed failing (both) |
| `OffersAddCleanup` ignores the language | The_help_names_the_simple_view_choices_as_the_app_does, SimpleViewTests.Add_ai_clean_up_is_not_offered_in_simple_for_text_that_is_not_english | observed failing (both) |
| `OffersAddCleanup` always false | The_help_names_the_simple_view_choices_as_the_app_does | observed failing |
| "Tidy my words" misnamed in the help | The_help_names_the_simple_view_choices_as_the_app_does | observed failing |
| The cloud sentence's dictionary clause removed | Every_mention_of_turning_on_cloud_clean_up_says_what_it_sends | observed failing |
| Simple busy text back to "download it after" | A_plain_busy_question_says_when_it_fits_only_if_the_running_download_stops | observed failing |
| Catalogue busy text back to "download it after" | The_space_check_counts_the_download_already_running | observed failing |

### Final-lens fixes (21/10/2026), lens on 4b5ee4f

The lens returned Needs changes, with no blockers. Its findings and the fixes:

1. **Off's line was untrue (orchestrator's decision: keep the behaviour, make the copy accurate).** Off skips the tidy only: the user's own corrections, snippets and the profanity filter (when on) still run after it (`DictationPipeline`). The lines now read (PROPOSED):
   - Off's line: "Paste what was heard. Only your own corrections, snippets and the profanity filter (if on) still apply."
   - The Advanced button and tray entry: "Off (paste what was heard)".
   - The Advanced "In use:" suffix: " (paste what was heard; your corrections, snippets and the profanity filter still apply)".
   - The Help's Off sentence: "To paste what was heard with no tidying, choose Off there instead; only your own corrections, snippets and the profanity filter (if on) still apply."
   - README and SPEC follow.
2. **The Help was true only in the Simple view.**
   - Added (PROPOSED): "This help names the choices as the Simple view shows them; if you see model names instead, choose Simple at the top of that page."
   - The model files answer now reads "(Add AI clean-up, shown under Tidy my words while the rule-based tidy is in use and the text is English)".
   - The labels pin also opens the Advanced view and checks "Simple" is shown there.
   - The onboarding's accent explainer now reads "choose More accurate in the Simple view of Settings > Models" (PROPOSED), so it holds when a seeded settings.json opens the Advanced view.
3. **PR #21's safety was unpinned on the wizard path.**
   - `Scratch.Shell` now takes a null `gpuMachine` (the app's own probe path) and a stand-in runtime probe. `ModelCoordinator.TestRuntimeProbe` is settable for that.
   - `The_wizard_probes_the_runtime_with_the_seeded_gpu_setting` seeds `whisper.gpu = cpu` and checks the probe sees "cpu".
4. **Two first-run rules were unpinned.**
   - Without base.en and with no clean-up question, the wizard writes the rule-based tidy: an assertion was added to `Without_base_en_the_speech_step_offers_the_three_tiers`.
   - The plan is looked up again after a folder is chosen: `The_plan_is_looked_up_again_after_a_folder_is_chosen` picks a folder already holding ggml-base.en.bin, and the speech step then says the Standard model is ready.
5. **Shared builders, bytes unchanged.**
   - `MicrophoneChoices.Build(devices, chosen)` serves the tray, Settings > Microphone and the onboarding test.
   - `NoSoundText.SilentAdvice` is the tail of both "Nothing heard. …" and the "No sound from mic" balloon.
   - Pins check the labels and both sentences byte for byte.
6. **`OnboardingTests.cs` was 1,046 lines at 4b5ee4f (1,131 once this round's pins were added).** It is now one partial class in three files: `OnboardingTests.cs` (the steps and the first run), `OnboardingTests.Microphone.cs` and `OnboardingTests.Window.cs` (Try it, size, placement and focus). No test was renamed, and the class still runs 48 tests.

**Mutation checks.** Each was applied alone, rebuilt, run and restored:

| Mutation | Pin | Observed |
|---|---|---|
| The Help's "choose Simple" sentence removed | Every_label_the_help_names_exists_in_the_app, The_help_names_the_simple_view_choices_as_the_app_does | observed failing (both) |
| The model files answer back to "when the text is English" | The_help_names_the_simple_view_choices_as_the_app_does | observed failing |
| The Simple / Advanced switch hidden in the Advanced view | Every_label_the_help_names_exists_in_the_app | observed failing |
| The accent explainer back to "in Settings > Models" | The_accent_explainer_says_where_More_accurate_is | observed failing |
| `OnboardingFlow.Plan` calls `PlanFirstRun(null)` | The_wizard_probes_the_runtime_with_the_seeded_gpu_setting | observed failing |
| The no-clean-up-pick branch in `AnswerFirstRun` made unreachable | Without_base_en_the_speech_step_offers_the_three_tiers | observed failing |
| `_plan = null` removed from `CompleteFolder` | The_plan_is_looked_up_again_after_a_folder_is_chosen | observed failing |
| Off's line back to "Paste exactly what was heard." | The_off_lines_say_what_still_applies | observed failing |
| `SilentAdvice` reworded | The_nothing_heard_line_and_the_no_sound_balloon_share_their_advice | observed failing |
| "(not connected)" changed in `MicrophoneChoices` | The_microphone_lists_are_built_in_one_place | observed failing |

### Re-lens on 852f222 (21/10/2026): Off's status and the settings help

The re-lens closed every finding of the final lens and raised one minor and one nit, both fixed:
- **Minor:** the Off status now says what still applies (PROPOSED): "no clean-up: whitespace tidied only; your corrections, snippets and the profanity filter still apply (llm.provider = off)". It still starts with "no clean-up:".
- **Nit:** the settings.json help text for `llm` (off) now reads "off (no clean-up: spaces tidied only; your corrections, snippets and the profanity filter still apply; spoken commands such as "new paragraph" are typed as words)".
- **Pin:** `The_off_lines_say_what_still_applies` checks the status word for word. Mutation, with the old status put back: **observed failing**.

## 1.2 polish (22/10/2026, branch fix/1.2-polish)

**Why.** A demo lane drove the real 1.2 build (main at cd46812) in Windows Sandbox and recorded each screen. Its frames showed fourteen defects in the Welcome to Dictum window (§ Onboarding above), the tray panel, calibration, Settings > Models and Settings > Help (§ In-app Help above). This branch fixes them. The version is not bumped (the release lane does it).

**What changed, per item.**

1. **The last line of every wizard paragraph was clipped** ("gigabytes", "let go", "begin"). Cause: `OnboardingWindow.LargeText` set body text to 17 px but kept the theme's fixed 19 px line height (Theme/Controls.axaml, made for 13 px). The window's body style now sets both, the line height in the theme's proportion (`LineHeightFor`: 17 × 19 / 13, rounded up, 25 px).
2. **The hotkey step's "Hold to test" and "Stop listening" were under the buttons.** The 640 × 420 client left the body 214 DIP; the hotkey step needs 264 and the speech choice 270 at 720 wide (measured headless, with this host's fonts). The client is now 720 × 500 DIP, so every step fits without scrolling. On opening, `Fit` still cuts it to the screen's work area. On 1366 × 768 at 150 % that is 432 DIP, and the body scrolls there. The scroll bar no longer auto-hides, so the scrolling is visible.
3. **Try it showed "Getting the speech model ready…" above an empty box.** The status is now inside the empty box, in body text, over it as a placeholder; typing hides it. The theme's own watermark was tried first: its template draws it at half opacity (#66000000 at 0.5, measured headless), which a style cannot change, so it was too faint to read. The instruction, with the key, shows from the start.
4. **The key showed as "RightCtrl" while Help says "the Ctrl key on the right-hand side".** There is one helper, `HotkeyNames.Display` (Core): "Right Ctrl", "Mouse 4", "Caps Lock", "Ctrl+Shift+Space". It is used by the tray panel header and tooltip, the hotkey box (its key and its "Windows received" line, which now drops "(right side)" because the name says it), calibration's "or hold …", Try it and Settings > General's messages. The token stays the settings value. The CLI, the logs and the settings loader's warnings also keep the token, because that is what goes into settings.json. `HotkeyParser.SideOf` had no caller left and was removed.
5. **With no microphone the panel said "Ready" and the picker "System default".** `MicrophoneChoices.Build`, the one builder for the three lists, now labels the first entry "No microphone found" when the system lists none. The tray panel shows PR #22's `NoSoundText.NoMicrophoneBalloon` on its problem line, under any model problem. A list that throws counts as none, as in the wizard, and is logged once per panel. The state word stays "Ready" (Owner decisions).
6. **The no-microphone wording differed between the wizard and calibration.** Calibration showed the Windows capture's own sentence. The Windows capture now throws `NoMicrophoneException` (Core), whose message is `NoMicrophoneBalloon`, the wizard's line, byte for byte. Calibration and the wizard's microphone test both show it.
7. **Calibration's error was in small print under Run test.** A take that fails (Record, Skip, the hotkey) is now said under Record, in body text, in the sentence card. Run test, Delete recordings and Save failures stay under Run test.
8. **Calibration's intro printed the recordings folder**, which names the Windows account. It now says where without the path, and has a button that opens the folder. The brief suggested "the folder you chose in Settings > Storage", but that would be untrue: the recordings folder is Dictum's own (`AppPaths.CalibrationDir`), not a chosen one. Settings > Storage lists it under "Models, logs, calibration recordings", so the line says that instead.
10. **The panel led with model names in the Simple view.**
    - In the Simple view the header is built from `SimpleView`'s one table: the tier, "loading…" or "model error" while that holds, then the clean-up ("Tidy my words", "AI clean-up", "Off", or "Custom: …"), then the key. There is no runtime in this view. Advanced and the support email keep `ModelsLine`.
    - The Mode line under Light | Full uses the same names in Simple.
    - The runtime: the header was already built from `WhisperEngine.LoadedRuntime`, the runtime that loaded, not the configured order. The demo run's own log (20261021-234550-first-run, `dictum-logs`) shows the Vulkan runtime loaded, one Vulkan device found (Intel UHD Graphics P630) and the model placed on Vulkan0. So "Vulkan" was true there, and the brief's premise that the virtual GPU was off does not match that log.
    - One real gap was fixed: a Vulkan runtime that reports no device leaves ggml on the CPU (as `WhisperEngine.VulkanActive` already assumes), and the header now says "CPU" then (`AppShell.RuntimeLabel`).
11. **The Simple view's Language card was jargon.** It now uses the orchestrator's wording. The Advanced card keeps its text, with the capital after its semicolon corrected.
12. **"In use" was a greyed button.** It is now a tag (`Ui.Tag`): link-coloured text in an outlined pill, not focusable, with the accessible name "In use".
13. **The model folder box cut the path off ("…\mod"), and Browse… opened in Documents.**
    - The box is a `PathBox`: trimmed from the left (Avalonia's `LeadingCharacterEllipsis`), the whole path in its tooltip and its accessible name. It is a tab stop with a focus ring, and keyboard focus opens the tooltip.
    - The chooser starts in the folder, or in the nearest folder above it that exists (`ShellDialogs.StartFolder`). The cause is inferred from the code and the demo log, not reproduced: the default model folder is made only by a download (`ModelDownloader`), the demo PC had downloaded nothing, and `WithStorage` passed no start folder for a folder that does not exist.
14. **The Help was one long page.** It now opens with "Questions on this page", one link per question in `HelpText.Questions` (no copy of the questions). Each link is a tab stop with the theme's focus ring. Pressing one moves keyboard focus to its answer, which scrolls into view with its ring.

Item 9 was not in the brief. Item 16: no version bump. Item 17 (the pill's "Recording · hold") is approved copy and is not this lane's: see Owner decisions.

**PROPOSED strings (22/10/2026), for the product owner's approval; none is approved:**
- "No microphone found" (the microphone pickers' first entry when none is listed)
- "Nothing is uploaded: the recordings stay in Dictum's folder on this PC (Settings > Storage lists it), so the test can be re-run when you add a model." (calibration intro, after the unchanged `Framing`)
- "Open the recordings folder"
- "AI clean-up" (Simple header); "Full: <tier> and AI clean-up. Light: <tier> and Tidy my words." (Simple Mode line, for example "Full: More accurate and AI clean-up. Light: Standard and Tidy my words.")
- "Dictum comes with an English-only model. For other languages it downloads a model that understands them; choose the language here." (Simple Language card)
- The Advanced Language card with "; any of the languages…" (was "; Any…")
- "Questions on this page"; the links' screen-reader hint "Moves to the answer, further down this page."
- Keys by their display names: "On a Mac over Remote Desktop use the right Option key (Right Alt) or right Command (Right Win)." (was "(RightAlt)", "(RightWin)"); Settings > General's mouse note "(Mouse 4, Mouse 5)"; "Windows received: Right Alt down"; "The hotkey is now Right Ctrl." and the other messages that name a key.

**Owner decisions (not changed here):**
- Item 17: the pill's "Recording · hold" is an approved state line.
- The panel's state word stays "Ready" with no microphone. The no-microphone sentence shows under it. A new state word would be new tray state copy.
- The Mode (Light | Full) segments stay in the Simple view, as the Simple view's own decision recorded. Only their line now uses Simple names.
- The Simple header names Off as "Off" (the brief's name), next to the switch and a state word that can also read "Off". "Clean-up off" would be clearer but is new copy.
- The Simple Language card's "Translate to English" hint still names models ("OpenAI recommends medium or large-v3…"). It was not in the brief.

**Pins** (`PolishTests.cs`, `OnboardingTests.Polish.cs`; headless). Each was observed failing with its mutation applied and passing without it (applied alone, rebuilt, run, restored):

| Item | Mutation | Pin | Observed |
|---|---|---|---|
| 1 | the LineHeight setter removed from the body style | No_step_cuts_off_its_text (both forms), The_body_style_sets_size_and_line_height_together | observed failing |
| 2 | WindowHeight back to 420 | Every_step_fits_the_default_window_without_scrolling (both forms) | observed failing |
| 2 | the scroll bar auto-hiding again | Every_step_fits_the_default_window_without_scrolling | observed failing |
| 3 | no status in the box | Try_it_shows_the_wait_inside_the_box, Try_it_waits_for_the_shell | observed failing |
| 4 | `HotkeyNames.Display` returns the token | A_hotkey_is_shown_by_its_display_name (9 rows), The_panel_box_and_calibration_show_the_display_name, Try_it_after_the_first_dictation_shows_the_accent_explainer, Try_it_shows_the_wait_inside_the_box | observed failing |
| 4 | calibration's hint back to `spec.Display` | No_window_shows_the_parser_token, The_panel_box_and_calibration_show_the_display_name | observed failing |
| 5 | the panel's no-microphone line removed | With_no_microphone_the_panel_says_so, A_microphone_list_that_throws_is_none_in_the_panel | observed failing |
| 5 | the picker's first entry back to "System default" | With_no_microphone_the_panel_says_so, The_microphone_lists_are_built_in_one_place | observed failing |
| 6 | the Windows capture's own sentence put back | The_platform_says_no_microphone_with_the_one_sentence | observed failing |
| 6 | calibration's failure text always "That did not work: …" | Calibration_says_no_microphone_next_to_Record, Other_failures_keep_their_words | observed failing |
| 6 | the wizard's test ignores the no-microphone failure | A_capture_with_no_microphone_says_the_one_sentence | observed failing |
| 7 | a take's failure written under Run test | Calibration_says_no_microphone_next_to_Record | observed failing |
| 8 | the folder path appended to the intro | Calibration_shows_no_path_and_opens_the_folder | observed failing |
| 10 | the header always `ModelsLine` | The_panel_header_follows_the_view(simple), The_header_keeps_the_hotkey_and_the_support_models_line_does_not | observed failing |
| 10 | the Mode line always the model names | The_panel_header_follows_the_view(simple) | observed failing |
| 10 | the Vulkan-with-no-device rule removed | The_runtime_is_where_the_model_runs (Vulkan, 0) | observed failing |
| 11 | the Simple card shows the old jargon | The_language_card_speaks_plainly_in_the_simple_view(simple) | observed failing |
| 12 | "In use" back to a disabled quiet button | In_use_is_a_tag_not_a_disabled_button (both views) | observed failing |
| 13 | trimming at the end (`CharacterEllipsis`) | The_model_folder_shows_its_end | observed failing |
| 13 | the chooser no longer looks above the folder | The_folder_chooser_starts_at_the_nearest_folder_that_exists | observed failing |
| 14 | a link that does nothing | Help_lists_its_questions_and_each_jumps_to_its_answer | observed failing |
| 14 | the question list left out | Help_lists_its_questions_and_each_jumps_to_its_answer, The_page_has_headings_and_reads_with_the_keyboard | observed failing |

**Existing pins changed, and why:**
- `Every_step_fits_at_150_percent_and_names_its_controls` now applies `Fit` for that screen first. The default no longer fits it by design, and the buttons stay in the window.
- `The_window_is_fitted_to_the_work_area_of_its_screen` keeps its cases with a 420 DIP client, and adds the 500 DIP default being cut to 432.
- `Try_it_waits_for_the_shell` reads the placeholder.
- The received-line pins read "Right Alt down", "Mouse 5 down" and "Left Mouse up".
- `The_microphone_lists_are_built_in_one_place` expects "No microphone found" for an empty list.
- The Feedback and runtime-resilience header pins read the Advanced line, or name the view they check.
- The model-folder pin reads the `PathBox`.
- The Help headings pin starts with the list's heading.

**Not verified:**
- Any of this on a real desktop, at 150 % or with other fonts (headless on this host only; the layout heights were measured with this host's fonts).
- The tooltip opening on keyboard focus on a desktop (pinned headless by the lens fixes below).
- Dark mode contrast of the tag and the box's status (theme brushes chosen for it, not measured).
- Screen-reader output (names and help texts are pinned, not heard).
- The folder chooser's start folder on Windows (the helper is pinned; the dialog was not opened).
- Tray mode (not run on this host).

### Lens fixes (22/10/2026), lens on 4abba54

The lens returned Needs changes, with no blocker. It re-ran 18 of the mutations above, all caught, and CI was green. Its findings, and the fixes on the same branch:

1. **Major, privacy.** When the recordings folder could not be opened, calibration printed `OpenPath`'s reason, and that reason contains the full path (the Windows account name). The reason now goes to the log, and the window says "The recordings folder could not be opened." (PROPOSED). `OpenPath` and Settings > Storage are unchanged.
2. **Major, item 10.** `SimpleView.ModeHint` ignored the language. With German, Detect automatically or translate it read "Full: Custom: small.en and AI clean-up…" and promised AI clean-up where none runs. It now returns the Simple line only while the text is English (`WhisperLanguages.OutputIsEnglish`) and both preset models are tiers; otherwise the panel keeps the model-name line.
3. **Major, accessibility, item 13.** `PathBox` (a Border) was a tab stop with no automation peer, so a screen reader could not read it. It now has a peer: a read-only Edit, named "Model folder", whose value is the whole path, as the text box it replaced was. The entry above said the path was "part of the accessible name"; that is corrected: the name is "Model folder" and the path is the value. `Ui.Tag` set a name on its Border, which has no peer either; that line is dropped, and the tag's TextBlock, read by its own Text peer, says "In use".
4. **Major, items 5 and 6.**
   - There is now one guarded device lister, `MicrophoneChoices.List`, used by the tray panel, the welcome window's test and Settings > Microphone. Settings > Microphone threw COMException with the audio service stopped. Each caller logs a failure in its own words, once where it redraws often.
   - The dictation controller now treats `NoMicrophoneException` as the no-microphone case, as the wizard and calibration do, even while the list still names a device.
5. **Major, item 4.** The entry above claimed the logs keep the token. That was untrue at 4abba54: the "Tray tooltip:" log line carried "Right Ctrl". The tooltip is now built for the key it is given (`TooltipLine`): the display name on screen, the settings token (`RightCtrl`) in the log.
6. **Minor.** Settings > General's fix-hotkey note now reads "F8 or Mouse 5 work well." (PROPOSED).
7. **Minor.** SPEC is updated: the Simple header and its combinations, the Mode line's condition, the display names, the runtime rule, the no-microphone line, the "In use" tag, the model folder field and Browse…, the Help question list, calibration's intro and its open failure, and the Mac note.
8. **Minor.** The PROPOSED list is completed below.
9. **Nit.** The caret overdrew the "G" of the in-box status. The status now starts 16 px into the box (it was 12).

The lens also asked for a pin that keyboard focus opens the `PathBox` tooltip, if cheap. It was cheap and is added; the tooltip opening is therefore no longer under Not verified, but only headless.

**PROPOSED strings added by these fixes** (none approved):
- "The recordings folder could not be opened."
- "It must not share a key with the dictation hotkey; F8 or Mouse 5 work well." (was "Mouse5")
- The model folder's accessible name "Model folder", with the path as its value. At 4abba54 it was the name "Model folder: <path>".
- The Simple header's combinations:
  - "<tier> · <clean-up> · <key>", for example "Standard · Tidy my words · Right Ctrl"
  - "<tier> · loading… · <clean-up> · <key>"
  - "<tier> · model error · <clean-up> · <key>"
  - the clean-up as "Tidy my words", "AI clean-up", "AI clean-up · loading…", "AI clean-up · error", "Off" or "Custom: <name>"
  - the tier as "Standard", "More accurate", "Most accurate" or "Custom: <model>"

**Mutation checks** (each applied alone, rebuilt, run and restored):

| Finding | Mutation | Pin | Observed |
|---|---|---|---|
| 1 | the open failure shows `OpenPath`'s reason | Calibration_says_an_open_failure_without_a_path | observed failing |
| 2 | the Simple Mode line always, whatever the language | The_simple_mode_line_is_only_for_plain_English (7 rows), The_panel_keeps_model_names_in_the_mode_line_for_another_language | observed failing |
| 3 | `PathBox` without its automation peer | The_model_folder_shows_its_end | observed failing |
| 3 | keyboard focus not opening the tooltip | The_model_folder_shows_its_end | observed failing |
| 4 | Settings > Microphone listing without the guard | Settings_microphone_survives_a_list_that_throws | observed failing |
| 4 | the controller ignoring `NoMicrophoneException` | SilenceWarningTests.The_controller_warns_of_no_microphone_when_the_platform_says_so | observed failing |
| 5 | the tooltip log line with the display name | The_tooltip_log_keeps_the_token | observed failing |
| 9 | the in-box status back at 12 px | Try_it_shows_the_wait_inside_the_box | observed failing |

## 1.2 polish round 2 (22/10/2026, branch fix/1.2-polish-2)

**Why.** A second Windows Sandbox recording, of the round-1 build (main at 4ab800e, PR #38), showed six small defects, and the round-1 lens left two nits. This branch fixes them. The version is not bumped.

**What changed, per item.**

1. **The tray panel's header wrapped the key onto a second line** ("…Right" / "Ctrl", first-run-poster.png), **and the Mode line wrapped "Tidy my / words."**
   - Cause: the models line shared a row with the state word and the switch, and wrapped (the caption style).
   - The header now has two rows: the mark, "Dictum", the state and the switch, then the models line under the last three. The line never wraps; one longer than the panel ends in an ellipsis, whole in its tooltip.
   - Measured headless at the panel's 380 px, with the longest state word ("Downloading"): every Simple line (each tier, each clean-up, "Right Ctrl" and "Mouse 5") and each preset's model-name line on a CPU and a GPU machine fit whole. With the old layout restored, 25 of the 26 lines broke, "Standard · Tidy my words · Right Ctrl" among them.
   - The Mode line's rule: one sentence per line. `SimpleView.ModeHint` now returns its two sentences. The panel joins them with line breaks, after "Custom: your settings match neither preset." when that applies (`FlyoutWindow.ModeLine`). So a line breaks between presets, never inside a name. At the panel's width each sentence fits on its line, in both views, on both machine classes, with and without the Custom note. The words are unchanged; only the space between the sentences became a line break.
2. **"Open log folder" stood under the no-microphone sentence.** The link now shows only while `ShellStatus.Problem` is set (a model problem, or "Last dictation heard nothing"), as before round 1 added the no-microphone line. The sentence alone has no link.
3. **In Try it the caret blinked in front of "Getting the speech model ready…".** While the status shows, `TryItStep.InitialFocus` gives focus to nothing. When the status goes (the model is ready, or failed to load, or the person typed), the box takes the focus, unless the person has put it somewhere else meanwhile (Tab to Finish, say). Hiding the caret instead was considered and not chosen: the box would still have the focus, and nothing on screen would show it.
4. **A Help index jump left the answer at the bottom edge with its last line cut off** (help/07, help/09). Focus alone scrolls only as far as needed. The link now scrolls the page so the answer's heading is 8 px below the top of the view (`HelpPage.JumpGap`). The answers near the end, which the page cannot scroll that far, show whole.
   - The position is measured against the scrolled content, not the view. Focus has already moved the offset, and the view's coordinates follow only at the next layout pass.
   - The first try measured against the view and overshot by the earlier offset. The pin caught it.
5. **The Simple view said "In use" twice on a row** (the tag and the line, change-model/05-standard.png). Cause: round 1 added the tag to `ModelRow` while the lines still began with the state. A row with the tag now says it once, in both views and for every kind of row that can be in use:
   - the catalogue row (`CatalogueRow` drops the state when the row is in use);
   - a tier the language rules out;
   - Tidy: "With AI clean-up · …" when the model does it, else the line alone;
   - Off;
   - the Custom rows ("Not one of the choices above.");
   - the Advanced row for a model file from disk (its path).

   The Advanced view had the same duplicate and is fixed with it, since it is the same cause.
6. **Calibration's Record stayed active with no microphone** (test-setup/04-sentence.png).
   - The window now looks for a microphone when it opens, when it is activated, before a take the hotkey starts (a disabled button does not stop the hotkey), and after a take fails for want of a microphone.
   - With none listed, or a list that throws (the one guarded lister, `MicrophoneChoices.List`), Record is disabled. The no-microphone sentence stands under it, and the sentence to read stays. Once a microphone is listed, Record works again and the line goes. Stop is never disabled.
   - A take that fails with `NoMicrophoneException` while the list still names a device leaves Record on, with the sentence. A first version disabled Record then too, so only the next activation turned it back on. The round-1 pin (Calibration_says_no_microphone_next_to_Record) caught that activation clearing the line. The list is what says a microphone is there.
7. **Nits from the round-1 lens.**
   - `PathBox.Path` had two summaries. The stale one is gone and the blank line is back.
   - In SPEC §24 the Browse… amendment was spliced into the middle of the Settings window sentence. It now follows that sentence.

**PROPOSED strings (22/10/2026)**, for the product owner's approval; none is approved:
- "With AI clean-up" (`SimpleView.TidyWithAiState`, the Tidy row's line start; was "In use, with AI clean-up")
- "Not one of the choices above." (`SimpleView.CustomNote`; was "In use. Not one of the choices above.")
- Lines whose "In use · " prefix is dropped, the rest unchanged: the tiers' and the catalogue's lines, Tidy's, Off's, and a tier the language rules out.
- The Mode line's sentences, one per line. The words are unchanged from round 1's PROPOSED line.

No approved copy is touched. CloudPolicy, the consent dialog, the pill and tray state lines, the Store listing, the 1.1.2 note, FreeLine, FreeLaterNote and the name line are unchanged.

**Pins** (`Polish2Tests.cs`, and `OnboardingTests.Window.cs` for Try it; headless). Each was observed failing with its mutation applied alone, rebuilt and run, and passing once restored:

| Item | Mutation | Pin | Observed |
|---|---|---|---|
| 1 | the models line back beside the state and the switch, wrapping | The_panel_header_is_one_whole_line (26 lines) | observed failing (25 of 26; "Standard · Tidy my words · Right Ctrl" broke before "Right Ctrl") |
| 1 | the models line wrapping, on its own row | The_panel_header_never_wraps | observed failing (3 lines, not 1) |
| 1 | the Mode line joined with a space | The_mode_line_breaks_between_presets_only (8 rows) | observed failing (8 of 8) |
| 2 | the log link for any line under the header | Open_log_folder_is_for_errors_not_the_microphone_line | observed failing |
| 3 | Try it focusing the box while it loads | Try_it_focuses_the_practice_box_once_the_model_is_ready | observed failing |
| 3 | no focus to the box when the model is ready | Try_it_focuses_the_practice_box_once_the_model_is_ready | observed failing |
| 4 | the jump without the scroll to the top | A_help_jump_puts_the_answer_at_the_top | observed failing (the answer 439 px into a 640 px view) |
| 5 | the catalogue row's line keeping "In use" | In_use_is_said_once_on_a_row; SimpleViewTests.The_default_view_is_simple, A_user_whose_language_is_not_english_is_never_switched_to_an_english_only_model | observed failing (6 tests) |
| 5 | Off's line starting "In use · " again | In_use_is_said_once_on_a_row (Off); CleanupOffTests.The_simple_rows_switch_between_tidy_and_off | observed failing |
| 6 | Record enabled whatever the list says | Calibration_record_waits_for_a_microphone, Calibration_with_a_list_that_throws_waits_for_a_microphone | observed failing |

Not pinned:
- the hotkey path's re-check (the hotkey is redirected through the shell's listener, which these headless tests do not drive);
- item 7 (comments and docs).

**Changed pins.** Round 1's pins that asserted the old text were updated to the new rule, not deleted:
- PolishTests: the Mode line's text now has a line break between its sentences.
- CleanupOffTests and SimpleViewTests: they checked the "In use · …" lines; they now check the tagged rows (`Polish2Tests.InUseRows`).
- OnboardingTests: lens 4's Try_it_focuses_the_practice_box asserted the box had focus while the model loaded. It is now Try_it_focuses_the_practice_box_once_the_model_is_ready, and Try_it_leaves_focus_the_person_moved is added.

**Not verified.**
- Nothing here was run on a desktop or in Windows Sandbox.
- The measurements use Avalonia's headless platform with Skia and this host's system fonts (Segoe UI Variable). A desktop at another scale, or a PC without that font, may set the lines differently.
- A new Sandbox recording would confirm all six items on screen.

## Polish pins (22/10/2026, branch fix/polish-pins)

- The transient models lines (a model still loading, a download pending, an error, with the longest runtime and key names) trim by design at the panel's 380 px: one line, an ellipsis, the whole line in its tooltip and its accessible name (lens finding 1 on PR #39). `Polish2Tests.A_transient_header_line_that_is_too_long_is_trimmed_whole_in_its_tooltip` pins five of them as expected-trimmed; the steady-state lines stay pinned as whole. No wording changed.
- `CalibrationWindow.Toggle`'s `CheckMicrophone()` guard (the hotkey starts a take even though Record is disabled) is pinned: `Calibration_hotkey_with_no_microphone_starts_no_take`.

## Release 1.2.0 prepared (22/10/2026)

Branch `release/1.2.0` from origin/main cd46812. All new wording below is PROPOSED until the owner approves it. `site/version.json` is not touched (set at release time) and no tag is created.

1. `Directory.Build.props`: `<Version>` 1.1.2 to 1.2.0 (package 1.2.0.0).
2. `docs/STORE.md` certification notes (PROPOSED): header "Notes written 22/10/2026 for Dictum 1.2.0." and a new CHANGED SINCE THE LAST SUBMISSION paragraph (the Welcome window, the Simple and Advanced views, clean-up Off, the Help page, the Send feedback and Email support buttons). Every other paragraph of the block is unchanged, except one sentence added to TO DICTATE (PROPOSED): "on the first start Dictum opens a Welcome window; click Next through its steps (or press Esc to skip it and keep the defaults), then" before "click into any text field". The block had no first-run hotkey dialog sentence to replace.
3. `docs/STORE.md` listing (PROPOSED): a "What's new in this version (1.2.0)" note; the feature line "No account, no analytics, no telemetry" replaced by the approved line "Free: no subscription, no account, no ads, no analytics, no telemetry."; a FREE paragraph in the description with the two approved lines ("Free: no subscription, no account, no ads, no analytics, no telemetry." and "The base product stays free to keep on your PC; optional paid extras may come later.").
4. `docs/STORE.md` version references (section 1 package-version row, section 6) say 1.2.0 / 1.2.0.0; the certification record gains "21/10/2026 1.1.2 submitted (Submission 2), marked mandatory, in certification" and "22/10/2026 Dictum 1.2.0 prepared".
5. `README.md`: a bullet under the opening list carries the two approved lines verbatim, plus open source, MIT and Kynni Pty Ltd. No other fact was removed.
6. `docs/STORE.md` section 9 row 17 (PROPOSED): the first run opens the Welcome window with a hotkey check, a microphone test and a choice of where model files are kept, and Esc keeps the defaults.

## Website help guide, landing infographic and the name line (21/10/2026, branch site/help-guide)

1. **The guide lives at `/help`** (`site/help.html`; `vercel.json` has `cleanUrls`), the address of `HelpText.GuideUrl` on feat/in-app-help. Its copy is PROPOSED, for the owner to approve; it agrees with the in-app Help question for question and adds more.
2. **The CSP is unchanged.** An earlier commit on this branch added `media-src 'self'` for `<video>` demos. The demos are GIF-only on the site: the private-data guard refuses `.mp4`, and the GIFs and posters arrive through PR #36. So that change is reverted, and `site/vercel.json` matches main. The planned markup is a poster still with a Play `<details>` that shows the GIF. It needs no script and nothing beyond `img-src 'self'`.
3. **Landing infographic** (owner, 21/10/2026): an inline SVG on `site/index.html` ("Dictum at a glance"), with all text as SVG `<text>`, colours from the site's tokens (light and dark), a wide version from 720 px and a tall one below, an `aria-label` and a visually hidden text equivalent. Its panel and badge copy is PROPOSED, except the free line, which is approved copy used verbatim. A shorter three-panel version sits in the guide's "Dictate" section.
4. **The name line is approved copy** (owner, 21/10/2026, 22:21). It replaces the 22:00 version, which ended “something that has been said”, a phrase neither source below contains. It is used verbatim on the landing page (Why section) and in the guide's questions, with the Latin word in `<em>`: "The name: a dictum is a saying, something stated. From the Latin *dictum*, “something said”."
   - **Verified 21/10/2026**, Wiktionary, "dictum", https://en.wiktionary.org/wiki/dictum, Latin section, sense 1: "a word, saying, something said". Read as raw wikitext (`action=raw`). The same entry's Latin Etymology 1: "Neuter form of dictus (“said, spoken”), perfect passive participle of dīcō (“to say, to speak”)." Its English sense 1: "An authoritative statement; a dogmatic saying; a maxim, an apothegm."
   - **Verified 21/10/2026**, Merriam-Webster, "dictum", https://www.merriam-webster.com/dictionary/dictum. Read in a browser, because scripted fetches are refused with HTTP 403. Sense 1: "a noteworthy statement: such as a : a formal pronouncement of a principle, proposition, or opinion b : an observation intended or regarded as authoritative". Etymology: "borrowed from Latin, "utterance, order, promise, saying, witticism," noun derivative from neuter of dictus, past participle of dīcere "to talk, speak, say, utter"". Plural: "dicta also dictums".
5. **Landing-page language lines follow 1.2** (coordinator, 21/10/2026). "What you need" no longer says English only. The roadmap's language item moves to a new "Done" status (`.status-done`): text #1B6E46 on its tint in light mode, #7EE0AE in dark. Computed contrast is at least 4.82:1 on either page background. The guide and these lines describe 1.2, so this branch is not deployed before the 1.2 build ships.
6. **Lens fixes on 8c0b919** (21/10/2026; the lens returned Needs changes, with no blockers):
   - Cloud clean-up's disclosure now names the dictionary as well as the text. `CleanupPrompt.Build` adds the dictionary line.
   - "Does Dictum need the internet?" is answered per language. Only the English Standard model ships with Dictum.
   - The empty `<video>` players are removed until the demo files exist. Their markup is kept in HTML comments.
   - Two roadmap items move to Done: languages, and long hands-free sessions.
   - The infographic's language badge reads "Works in 100 languages". **Verified 21/10/2026:** `git show origin/main:src/Dictum.Core/Transcribe/WhisperLanguages.cs | sed -n 27,46p | grep -o '("' | wc -l` prints 100. That is every entry of `WhisperLanguages.Table` on main at 8419f10, English included.
7. **Aligned with the final in-app Help** (main cd46812, 22/10/2026). Every sentence of `HelpText.cs`' How Dictum works, Accuracy and Questions lists that the guide also says is now word for word the same in `site/help.html`. That makes 122 sentences, checked on the page's visible text. The exception is the in-app "Where is the full guide?" answer, whose site counterpart is "Is there help inside Dictum itself?". The guide's section 4 now has HelpText's heading, "Why Dictum hears some people better than others". Shared sentences use HelpText's straight apostrophes and quotes, while the rest of the site keeps curly ones.
8. **Demos as a poster with Play** (22/10/2026, main d47e7f9). There are seven demos: install, first-run, dictation-stills, test-setup, change-model, model-folder and help. The GIFs and posters arrive on main through PR #36, which merges before this PR.
   - **Markup:** each demo is `<details class="demo-play">`. Its `<summary>` holds the poster (`img/demos/<name>-poster.png`) and a Play label. The body holds the GIF (`img/demos/<name>.gif`, `loading="lazy"`). Opening it hides the poster and shows the GIF, with Stop. There is no script, and no CSP change: `img-src 'self'` covers it.
   - **Lazy loading, verified 22/10/2026** in the Claude desktop app's browser pane (Chromium), with local copies of the GIFs from the demos-1.2 worktree (not committed). After scrolling past all seven closed demos, no GIF had been requested; six posters had. The seventh poster sits inside a closed FAQ answer. Opening the first demo requested `install.gif` alone.
   - **Missing files:** with the files absent, each poster box keeps its 1600:1116 shape, shows its alt text and the Play button, and the page does not scroll sideways at 375 px.
   - **Layout:** the Play button sits at the top of the poster, because the bottom of each poster is the demo's own caption band.
   - **Polish-round screens:** the guide's steps now say "Right Ctrl" for the key name, "In use" is shown in place of a button, there is an "Open the recordings folder" button, Help has a "Questions on this page" list, and the welcome window's microphone step can show "No microphone found".
9. **Final lens on ad8ae40** (22/10/2026). The lens returned Block only because main had moved; everything else passed. The findings are fixed:
   - **Model folder (finding 2).** The guide now says that models in Dictum's usual folder are still found, and models in a folder chosen earlier are not. `ModelFolders.LookupOrder` searches the current chosen folder, `%LOCALAPPDATA%\Dictum\models` and the bundled folder, and nothing else. Product candidate: remember previously chosen folders, or offer to move the models.
   - **Roadmap.** Three items move to Done. Their merge commits are each contained in v1.1.0, v1.1.1 and v1.1.2 (`git tag --contains`, 22/10/2026): mouse-button hotkeys and snippets (PR #1, b5363bb); the check that clean-up never inserts unsaid dictionary names (PR #4, 79bffb7); the cloud presets with their warning (PR #3, 13ba7e5). The language item (PR #2, a88e492) and segmented hands-free (PR #8, 23acf35) are in the same three tags.
   - **Landing page wording.** The landing page's "No account, no cloud, no telemetry" and its meta and og descriptions now name opt-in cloud clean-up.
   - **FAQ styles.** These now apply only to the FAQ's own items, so the demo player inside an answer is not styled as one.
   - **Demo players.** Each is named "Demo: <title>", and the poster alt text says "a still from the demo".

## Site: Store-first (22/10/2026, branch site/store-first)

**Owner decision (22/10/2026), in substance:** the Microsoft Store is the default and easy-to-find way to get Dictum,
and in future the Apple App Store; the site does not advertise that the direct download is unsigned; every reference on
the site to the manual download and to getting past SmartScreen goes; pro users can still download from GitHub without
an explanation. New wording below is PROPOSED until the owner approves it.

1. **Removed from `site/index.html`:** the hero's "Download for Windows" button (GitHub releases) and its note ("It
   isn't code-signed yet ... here's what to click", linking `/help#smartscreen`); the FAQ "Windows warned me about the
   download. What do I click?" (`#faq-smartscreen`); the "What you need" item "Microsoft Visual C++ Redistributable
   (x64) 14.51 or later, for the direct download" with its installer and administrator-rights caveat; the closing band's
   "Download for Windows" and "See the source" buttons; the footer's "Source on GitHub" link (replaced by item 3).
2. **Removed from `site/help.html`:** "The direct download" steps in Get Dictum (the releases link, Dictum-Setup, the
   SmartScreen and "can't be reached" steps, the Visual C++ Redistributable and vc_redist.x64.exe steps, Setup's pages);
   the install demo player (its GIF shows the SmartScreen prompt; the files stay, see `docs/DEMOS.md`); the
   "Windows protected your PC" box (`#smartscreen`: More info, Run anyway, code-signing, the Edge Keep steps). The
   updates FAQ's last direct-download step now reads "Install the newer version from that page. Your settings and
   models are kept." (lens, 22/10/2026); it no longer points to section 1, which no longer describes Setup.
3. **Added:** the Store button is the only button on the landing page (hero and closing band); one quiet footer line on
   all three pages (replacing their "Source on GitHub" links),
   "Source code and release files are on GitHub.", linking https://github.com/kynniau/dictum; "What you need" says to
   get Dictum from the Microsoft Store, which installs everything else Dictum needs (the package declares the
   `Microsoft.VCLibs.140.00.UWPDesktop` framework, which the Store installs: "Visual C++ runtime" above, MSIX
   troubleshooting guide, read 17/10/2026); Get Dictum in the guide is Store only.
4. **Kept, because it is true and needed by people who already have the direct download:** the approved sentence
   "Only when you open the privacy policy ... click Check for updates in the direct-download version ..." (pinned by
   `UpdateCheckTests`) on the landing page and the privacy policy; the privacy policy's text (only its footer changed; it covers both
   builds, and its bullets are approved copy); the guide's FAQ answers on updates, uninstalling and model folders for
   the direct download (orchestrator's call, 22/10/2026: keep them). The roadmap's "Mac version" line is unchanged: it says nothing about where the Mac version will
   be sold, and no Apple App Store plan or date is recorded to cite.
5. **Not the site, so not changed:** `README.md`, `docs/STORE.md`, the release-notes preamble and the in-app Help
   (`HelpText.cs`).
6. **Pins:** `ReleaseWorkflowTests` no longer requires the site to state the Visual C++ runtime floor (README and
   preamble still must, and agree); a new test requires that no page of the site names it.
   New `SiteStoreFirstTests` pin the Store as every landing-page button, the absence of the removed phrases on all
   three pages, the one GitHub footer line on each page, a Store-only Get Dictum section, and that every same-site anchor lands.

## TimeProvider seam (22/10/2026, branch fix/slow-connection-test)

`OfflineAndDownloadTests.A_slow_but_live_connection_is_not_a_stall` failed intermittently on CI (run 39689142461 attempts 1 and 2, earlier runs on PRs #15 and #26): `Assert.Equal(1, attempts)` saw 2. Cause: the downloader's stall limit was a real timer (`CancelAfter`), and the test raced it with 40 ms reads against a 150 ms limit, so a ~110 ms scheduling hiccup on a loaded runner was reported, correctly, as a stall. The detector had no bug.

1. **The seam** (owner, 22/10/2026): the stall timer and the back-off wait take a clock. `ModelDownloader` is a static class with no constructor, so the clock is an optional `TimeProvider? Time` on `DownloadPolicy`, which the tests already vary with `Fast with { ... }`. Null means `TimeProvider.System`; no caller outside the tests sets it (`grep "DownloadPolicy("` finds only the record itself and `DownloadPolicy.Default`).
2. **The stall timer** is now `policy.Time.CreateTimer(...)` cancelling the attempt's linked token source, restarted with `Change(StallTimeout, Infinite)` before every read (the line that used to be `stall.CancelAfter`). It does not use `new CancellationTokenSource(TimeSpan, TimeProvider)`, because the attempt's source is a linked source (`CreateLinkedTokenSource`), which takes no clock. The callback swallows `ObjectDisposedException`: the timer is disposed just before the source, and a callback already running could otherwise reach a disposed source on a thread-pool thread.
3. **No new package.** `tests/Dictum.Tests/FakeTimeProvider.cs` is a 98-line clock that moves only on `Advance`, firing due timers in order on the advancing thread. `Microsoft.Extensions.TimeProvider.Testing` would do the same; a test helper this small does not justify a dependency (build ladder, rung 5).
4. **Pins.** `A_slow_but_live_connection_is_not_a_stall` (13 reads, the clock advanced 40 ms per read against a 150 ms limit, 520 ms in all: one attempt) and its inverse `A_connection_that_stops_sending_is_a_stall` (one advance past a 200 ms limit with no byte: two attempts, "connection stalled: no data for 0.2 s" logged). Neither waits on real time; the 30 s in the test driver only bounds how long a hang takes to fail. `A_stalled_connection_is_retried_on_the_system_clock` (renamed from `A_stalled_connection_counts_as_a_drop_and_is_retried` on 22/10/2026 after it failed on a loaded runner, expecting 2 attempts and getting 3; the likely cause, inferred from the code and not reproduced, is that the retry itself outlasted the 300 ms limit) is kept on the real clock on purpose: it is the one test that runs the default path end to end, with a 2 s limit and loose assertions (at least one retry, content identical). The exact attempt count, request count and retry log line are pinned only on the fake clock.
5. **Mutation.** Removing the per-read restart fails the slow pin; making the timer callback do nothing fails the stall pin (observed 22/10/2026).
6. **Not checked:** whether `CancellationTokenSource.CancelAfter` itself honours a `TimeProvider` given at construction. The seam does not depend on it.

## Owner approvals (22/10/2026)

Recorded on branch `fix/1.2.1-copy` for 1.2.1 (no version bump in this change). The earlier entries are left as written;
this entry supersedes their "PROPOSED" and "awaiting the product owner" notes where it says so.

1. **Decision 3, the hold pill.** "Recording · hold" becomes "Recording · let go to finish" (`PillText.RecordingHold`).
   Hands-free's line is unchanged. The landing page's static pill and the two design files show the new words. The demo
   GIF `site/img/demos/dictation-stills.gif` is re-rendered with the new pill (see docs/DEMOS.md).
2. **Decision 4, no microphone.** With no microphone listed, the tray panel's state word is "No microphone"
   (`NoSoundText.NoMicrophoneState`), and "Ready" again once one is listed (the panel reads the list on every refresh).
   It replaces "Ready" only: Off, Recording, Working, Downloading, Loading, No model, Model error and Error are kept, as
   each says more than the microphone does. The dot takes the error colour, as "No model" does (this lane's choice: the
   decision names the word, not the colour). The no-microphone sentence under the header is unchanged.
3. **Decision 5, Light | Full.** In the Simple view the tray panel hides "Mode", the Light | Full switch and the line
   under it; the Advanced view keeps all three, with the presets' model names. `SimpleView.ModeHint` (the Simple names
   for that line) had no caller left and is removed, with its two pins; the line's layout pin now measures the model-name
   line only.
4. **Decision 6, "No clean-up".** With clean-up off, the panel header's clean-up part reads "No clean-up" in both views
   (`SimpleView.OffHeaderLabel`). The Advanced header shares its clean-up part with the icon's tooltip and the support
   email's models line, so those say "No clean-up" too. The choice keeps its name: "Off" in the Simple view and the
   Simple picker, "Off (paste what was heard)" in the Advanced view. Settings > Models' Advanced "In use:" line keeps
   "no clean-up" (`ModelCoordinator.CurrentCleanupName`).
5. **Block approval (item 8).** The owner approved every PROPOSED 1.2 string as a block. Every "PROPOSED" marker in
   `src/` and `tests/` comments now reads "APPROVED 22/10/2026", keeping the date it was proposed, and the "awaiting the
   product owner" and "for the product owner to approve" notes beside them are removed. That includes the lower-case
   "Proposed 16/10/2026" markers in `UpdateCheck.cs`. Of the 92 lines with an upper-case marker, 45 were already in the v1.1.2 tag (each line's text looked
   up in `git show v1.1.2:<file>`); they are flipped too, because the brief names `NoSoundText.cs` and `ModelPrompts.cs`, whose markers date from before v1.1.2. No
   string's wording changed. docs/STORE.md and the README change only where a sentence now read wrong. The PROPOSED notes
   in docs/SPEC.md, docs/DEMOS.md and docs/MAC-HANDOVER.md, and in earlier entries of this file, are left as written.

## Cloud speech, Core (22/10/2026)

Branch `feat/cloud-speech-core` from origin/main 049b73a. Owner decision 22/10/2026 (10:31): cloud speech is approved behind
its own express consent, and local speech stays the default. This is the Core lane of the three in the design brief's
"Cloud speech" appendix (Core, UI, Copy): no window, no site and no Store copy change here. The speech consent text and its
two buttons are APPROVED (owner, 22/10/2026, 10:50 AEDT); every other new user-facing string is PROPOSED (item 10).

1. **Settings (`speech.*`).** `provider` (`local`, the default, or `openai-compatible`), `base_url` (`https://` only, on this PC too: lens fix 2
   below), `model` (required; for Azure OpenAI the deployment name), `timeout_ms` (1000-120000).
   `whisper.*` stays the local engine's. No key can grant consent; an unknown key such as `speech.consent` warns.
   `timeout_ms` defaults to 30000, which is not measured: it carries a `simplification:` comment and is to be set from a
   measured round trip (the demo screen) before cloud speech ships.
2. **Keys.** `CloudPresets.ForAddress`'s address rule is now `CloudPresets.Match`, shared by the clean-up presets and the new
   `SpeechPresets`; a preset may carry a `HostSuffix` (Azure OpenAI: any `https://` host ending `.openai.azure.com`).
   `OPENAI_API_KEY` goes to `api.openai.com`, new `AZURE_OPENAI_API_KEY` to Azure OpenAI hosts, new `GROQ_API_KEY` to
   `api.groq.com` (each required there), new `SPEECH_API_KEY` to any other address (optional). The three new names are in
   `SecretStore.KeyNames`, so the log redactor and the self-test cover them. No default model id is filled in: the ids in
   the brief came from a page summary it says to re-read first.
3. **Consent file.** `consent.json` is now `{ "cleanup": {...}, "speech": {...} }`, each the existing record (provider,
   host, time, app version). The earlier single-record file reads as a clean-up consent and never as a speech consent.
   Every write uses the new shape (`Write` keeps a speech entry, `WriteSpeech` keeps a clean-up entry, `WithdrawSpeech`
   removes only the speech entry and deletes the file when nothing is left). `Delete` withdraws everything; Switch cloud clean-up
   off now uses `WithdrawCleanup` (lens fix 1 below). An earlier Dictum
   reading a new-shape file finds no top-level record, so no consent (it fails closed).
4. **The gate** (`CloudSpeech.Create`, at every settings load, in this order): `local` gives the local model; `base_url` or
   `model` unset, nothing sent; `offline_only` with a host off this PC, refused (`CloudPolicy.OfflineRefusal`, now one rule
   and one wording for clean-up and speech); plain `http://` refused (`CloudPolicy.PlainHttpRefusal` off this PC, shared likewise; on this PC too since lens fix 2
   below); no speech consent naming this exact host, nothing sent; `whisper.translate` on, refused (v1; the owner's open
   question was refuse or route to the local model, and the brief chose refuse); the key the address needs missing, nothing
   sent and `MissingKey` set. Each logs one line and leaves a status. Unlike clean-up, a server on this PC still needs the
   speech consent: the audio leaves Dictum either way.
5. **The request.** One multipart POST per recording (per part in hands-free) to `{base_url}/audio/transcriptions`: `file`
   (16 kHz mono 16-bit WAV built in memory by `Wav.Encode`, now also what calibration's `Save` writes), `model`, `language`
   from `whisper.language` (left out for `auto`), `prompt` (the dictionary terms, then a part's carried words, exactly as
   `DecodeOptions.WithCarried` gives whisper), `response_format=json`; the reply's `text` is tidied by
   `TranscriptText.Normalise`. The key the address decides goes as `Authorization: Bearer` (Azure OpenAI: `api-key`, lens fix 8). The `HttpClient` is the shell's
   offline-guarded provider client (no redirects), so `OfflineGuardHandler` is still the backstop.
6. **Failure is never silent.** An HTTP error, the deadline, an empty or non-JSON reply, the offline guard, or a settings
   reload: the local model hears the same samples, the log says "Cloud speech via {host} not used, the local model heard
   this dictation instead: {reason}." (status code and host, never audio, text or key), and `Status` says the last request
   failed until the next success. Esc propagates and pastes nothing. A reload retires the old provider: its request is
   cancelled and it starts no other. The local model stays loaded as before (`ModelManager` is unchanged); with none
   installed, a failed request fails the dictation as a missing model does today.
7. **Wiring.** The pipeline's one transcriber seam now asks `CloudSpeech.GetAsync` (`AppShell.cs`), rebuilt in
   `LoadConfiguration` beside the cleaner; the "Settings loaded" log line and the self-test gain a speech status. The cloud
   engine does not wait for the local model; it asks for it only when a request fails.
8. **Network sweep.** The Help page's sweep (`HelpPageTests.The_page_reaches_the_network_only_through_the_platform_seam`)
   covers that page only; Core had no sweep, so `CloudSpeechTests.Only_the_known_clients_make_network_requests` pins the
   files that make HTTP requests, with `CloudSpeechEngine.cs` the one new entry.
9. **For the UI lane.** The consent dialog shows `SpeechPolicy.ConsentText`, `AgreeButton` and `DeclineButton` and records
   with `CloudConsentFile.WriteSpeech`; switching off calls `WithdrawSpeech`. `CloudSpeech.Status`, `SendsAudio`, `Host`
   and `MissingKey` feed Settings > Models, Privacy, About and the tray; `SpeechStatus.FallbackPill` is the pill after a
   failed request. There is no change event yet: after a failed request the status changes without telling the UI.
10. **Approved and PROPOSED strings.** APPROVED 22/10/2026 (owner, 10:50 AEDT), verbatim from the brief's §3, with `{host}`
    and `{VAR}` filled in; marked "do not reword" and pinned byte for byte in `CloudSpeechTests`: `SpeechPolicy.ConsentText`,
    `AgreeButton` ("Send my voice to {host}") and `DeclineButton` ("Keep speech on this PC"). PROPOSED, awaiting the owner:
    `SpeechStatus.Local`, `On`, `Failed`, `NotAgreed`, `TranslateRefused`, `NothingSent`, `FallbackPill`;
    the log lines in items 4 and 6; the `speech` entry in the default settings.json's `_doc`. `SpeechStatus.WaitingForKey`
    and `OfflineOnly` reuse clean-up's approved lines word for word.
    *Revised (cloud speech UI lane, owner approval 22/10/2026, 16:31 AEDT):* one clause of `ConsentText`'s last paragraph,
    because offline only keeps allowing a speech server on this PC (loopback https), as it does for clean-up. "offline only
    mode refuses it" became "offline only mode refuses any service that is not on this PC"; the last paragraph now reads
    "Nothing is sent until you agree. You can switch it off at any time in Settings > Models, and offline only mode refuses any
    service that is not on this PC; your speech model on this PC is used again." The rest of the constant is byte-identical.
11. **Not verified.** No request was made to any real service. That Azure OpenAI and Groq accept the key as
    `Authorization: Bearer` on `/audio/transcriptions` (the brief: Azure's REST reference lists the key in the
    `authorization` header; Groq's header is Recalled); that `gpt-transcribe` takes `language` (the brief's table says it
    takes `languages`) and `prompt`; any latency or cost figure.

### Lens fixes on b190186 (22/10/2026, same branch)

The lens on b190186 (and the merge 94e8621) found no path that sends audio or a key without consent for the exact host, and
returned Needs changes. Fixed:

1. **F1, clean-up off kept withdrawing the speech consent.** `SaveCloudOff` (Switch cloud clean-up off) and "Allow" undone
   when settings.json cannot be written called `CloudConsentFile.Delete`. Both now call the new `WithdrawCleanup`, which keeps a
   speech entry and deletes the file only when nothing is left (clearing read-only, as `Delete` does). `Delete` is unused by the
   shell now.
2. **F2, orchestrator decision: plain `http://` is refused for cloud speech everywhere, a server on this PC included.** The
   approved consent says the recording goes "over the internet" and that Dictum "encrypts the connection (HTTPS)"; the precedent
   is Anthropic clean-up's refusal of any `http://` address. This supersedes the spec's §2 on this point and item 4 above. The
   loader ignores an `http://` `speech.base_url` with a warning, and the gate refuses one built any other way, with its own
   PROPOSED reason (`SpeechPolicy.LoopbackHttpRefusal`) on this PC.
3. **F3, the shell wiring is pinned:** a started shell with the speech consent sends one dictation's audio through its pipeline
   to a fake server under the real offline guard (`AppShell` and `AppRules.CreateClients` take a `providerHandler`, like the
   download and update handlers). **When feat/how-it-works merges, `AppShell.HowItWorks.cs` must use `_speech.GetAsync`.**
4. **F4:** a server on this PC still needs the speech consent (pinned with `https://localhost`).
5. **F5:** the Core network sweep matches every `HttpClient` sending method, the handler types and the Anthropic SDK client;
   the allow-list is `AnthropicLlmClient`, `AppRules`, `AppShell`, `CloudSpeechEngine`, `ModelDownloader`, `OfflineGuardHandler`,
   `OpenAiCompatibleLlmClient` and `UpdateCheck`.
6. **F6:** hosts are compared without a final "." (`CloudPresets.HostOf`), and a preset's own host name on another port gets
   no key at all, for clean-up and speech alike; the README and `.env.example` "never sent to" lines say so.
7. **F7:** the endpoint is built with `UriBuilder.Path`, so a query in `speech.base_url` stays after `/audio/transcriptions`
   and a fragment is dropped.
8. **F8:** Azure OpenAI gets its key in an `api-key` header (`CloudPreset.KeyHeader`), per Microsoft Learn's transcription
   reference as read by the lens on 22/10/2026
   (https://learn.microsoft.com/azure/foundry/openai/reference-preview-latest#create-transcription; not re-read by this lane).
   OpenAI, Groq and custom servers keep `Authorization: Bearer`. Groq's header is **Recalled**, not read on its page.
9. **F9:** the storage list labels consent.json "consent to cloud clean-up and cloud speech" (PROPOSED).
10. **Reload and quit:** the reload's retire of the old speech step is pinned, and `AppShell.Dispose` retires the speech step
    before the controller, the models and the provider client go, so quitting mid-request starts nothing more.
11. **F10:** the consent text has the owner's approval (22/10/2026, 10:50 AEDT) and no separate legal review.

## Storage page can change the model folder (22/10/2026, branch fix/storage-change-folder)

Owner report: "in 1.2 I can see where the folders and data is saved, but I have no option to change this location."

1. **Cause.** Settings > Storage listed the model folders read-only. The change lived only on Settings > Models ("Model folder": Browse…, Use default), so a user who read the folder on Storage had nowhere to change it there.
2. **Fix.** Storage's "Downloads go to" row gets two buttons, **Change…** and **Use default**, and the page repeats the Models note and adds one sentence under it. They call the same code as Models: `ModelFolderActions.BrowseFolder` (the `IShellDialogs.ChooseFolder` seam, then `ModelCoordinator.SetModelsFolder`, which writes `models.folder`, refuses the bundled folder and reloads) and `ModelFolderActions.UseDefaultFolder`. `ModelsPage`'s private `BrowseFolder` and its inline "use default" lambda are gone: `BrowseFolder` is defined once. Storage redraws at once after either action (its once-a-second throttle would otherwise have kept the old path). Use default is disabled while no folder is chosen, as on Models.
3. **Not moved, by design.** The settings, dictionary and log folders get no button; the "Where Dictum keeps things" rows keep only Open.
4. **PROPOSED strings** (for the product owner): button **Change…** (accessible name "Change the model folder"); the sentence **"A slower drive makes the speech model slower to load and to switch."** The note "Dictum looks here first and downloads go here. Changing it does not move any files." is the approved Models wording, repeated. The second button reuses Models' existing label **Use default** (accessible name "Use the default model folder", now one constant for both pages) rather than a second wording ("Use the default" was the brief's proposal).
5. **Pin.** `ModelFolderTests.Storage_changes_the_model_folder_with_the_same_handlers_as_Models`: Change… and Use default sit in the "Downloads go to" row with their accessible names and Tab stops; one Change… on the page; the note and sentence show; Change… opens the shared chooser in the folder in use, writes `models.folder`, reloads and shows the new path at once; Use default clears it; the bundled folder is refused with its reason; a cancelled chooser changes nothing.
6. **Mutation.** Removing Change… from Storage's folder row fails the pin (observed failing, 22/10/2026).
7. **Help** names only Models' Browse… (HelpText) and is unchanged; no Help label changed or was added.
8. **Gate.** Release `--no-incremental` build: 0 warnings, 0 errors. The suite excluding `Install_copies_models` (run locally; the public-copy sweep `PublicCopyNeutralityTests` is inside it): 1,799 total, 1,776 passed, 23 skipped, 0 failed, on 5dc3221 (before the lens fixes below). After them: `ModelFolderTests` and the sweep re-run on the fixing head, passing.
9. **Lens fixes.** The sentence now reads "A slower drive makes the speech model slower to load and to switch." (PROPOSED; the earlier "and run" overclaimed, the model loads once). Storage also shows Models' approved network-folder warning (`ModelsPage.FolderWarning`) under the folder note when the folder is on a share (pinned by `Storage_warns_when_the_model_folder_is_on_a_network_share`; blanking the warning line fails it, observed 22/10/2026). Storage still calls `Draw()` after each action rather than resetting its redraw throttle: the settings reload redraws the page inside the throttle window and the old path stayed (observed under the lens), so the explicit redraw is needed.

## See how it works, stages 1-5 (22/10/2026)

Branch `feat/how-it-works` from origin/main 049b73a. Owner intent and the design are in SPEC §29; the brief was section B
of the design note "four asks, designed and sized" (22/10/2026), stages 1-5 only. Compare mode is a later lane.

1. **One pipeline, no second path.** The page does not build its own chain of engines (the brief's `DemoRun`). It calls
   `DictationPipeline.DemonstrateAsync`, which shares `RunAsync`'s body (`JourneyAsync`) with a `paste: false` switch
   and a progress callback. So the gate, the speech model, the clean-up (whatever is configured, cloud included when
   consented and on) and the finishing step are the dictation's own. The shell builds the one pipeline lazily
   (`AppShell.Pipeline`), and the controller and the page both use it.
2. **No second timer.** The pipeline already reads one stopwatch twice: transcription and clean-up. The progress
   reports carry exactly those values (the clean-up reading is now held in `cleanupMs` and used by the log line and the
   report alike). A pin compares each reported time with the number in its log line. The rule-based tidy is not timed on
   its own when a model is in use (it runs inside the clean-up, or not at all), and the finishing step is not timed, so
   the page shows no time for them rather than inventing one.
3. **Stage 3 beside an accepted reply** shows `DeterministicCleaner.Clean(raw)`, marked "Not used" and untimed. The
   pipeline did not run it; it is the text a rejected reply would have got. This is the only text on the page not
   reported by the pipeline, and it comes from the tidy's own function.
4. **The rejected reply's text is not shown.** `CleanupResult` does not carry it, and adding it would change a record
   seven test sites in four test files build by value (grep `new CleanupResult(` in tests/, 22/10/2026). The verdict shows the validator's or provider's own reason string, which is already plain
   words ("reply drops a negation").
5. **The hotkey records here** while the page is shown in the window in front, as calibration does
   (`RedirectHotkey`). It is released on deactivation, on another page, and on close.
6. **No Help link.** The orchestrator asked for the page to be reachable from Help, but Settings → Help is built in
   `OtherPages.cs` and its copy is in `HelpText.cs`, both owned by other live lanes. That is left for the Help copy lane: one
   button calling `SettingsWindow.ShowPage(HowItWorksText.PageTitle)`.
7. **Duplicate kept on purpose.** Each stage's focusable block repeats Help's private `Readable` (`OtherPages.cs`). It is
   marked `// simplification:` with its trigger: move `Readable` to `Ui` when that file is free.
8. **"The sound never leaves this PC"** is true today (speech recognition is local only). If cloud speech recognition
   (design section C) ships, `HowItWorksText.MemoryNote` must change with it.

**PROPOSED copy (22/10/2026), awaiting the product owner's approval**, all in `src/Dictum.Core/Core/HowItWorksText.cs`:

- Page title: "See how it works"
- Intro: "Record one short dictation and watch what Dictum does with it, step by step. It uses your current settings: the same speech model and clean-up as the hotkey."
- In-memory line: "This page pastes nothing. The recording is kept in memory only, never saved to disk, and the sound never leaves this PC."
- Sends-text line: "Your clean-up sends text to another program ({status}), so this recording's words go there too, as a dictation's would."
- Buttons: "Record" / "Stop"; screen-reader names "Record one dictation" / "Stop recording and show the steps"; beside them "or hold {hotkey}" (calibration's wording).
- Status: "Recording: say a sentence or two, then press Stop."; "Stopped at 30 s, the longest this page records."; "A dictation is in progress: try again when it has finished."; "Cancelled: nothing was kept."; "Done. Record again to try another sentence."; "That did not work: {message}".
- Stage titles and descriptions:
  - "1. What Dictum hears": "Sound, not words: the bars show how loud your microphone is."
  - "2. What the speech model heard": "The speech model on this PC turns the sound into words. This is its raw text."
  - "3. Rule-based tidy": "Fixed rules remove hesitations such as "um", add capitals and a full stop, and follow spoken commands such as "new line"."
  - "4. AI clean-up": "A language model polishes the words, then Dictum checks its reply and uses it only if it passes every check."
  - "5. What would be pasted": "After your corrections, the profanity filter and snippets. A dictation pastes this; this page does not."
- Stage states: "Waiting", "Working…", "Done", "Not used", "Not reached".
- Figures: "Took {ms} ms"; "{seconds} s of sound".
- Notes: "Dictum stops here ({reason}), as it would for a dictation."; "The rule-based tidy did not run: only spaces were tidied."; "No AI clean-up is in use, so this is the clean-up."; "Used, because the AI reply was not (step 4)."; "Not needed this time: the AI reply passed. Dictum uses this text when a reply does not."; "Not in use. Clean-up in use: {status}."; "Accepted: the reply passed every check, so Dictum used it."; "Not used: {reason}. Dictum used the rule-based tidy (step 3) instead."; "Not asked: {reason}. Dictum used the rule-based tidy (step 3) instead."; "A dictation would paste this. This page pasted nothing."; "Your corrections, the profanity filter or snippets changed the text."; "Nothing would be pasted: no text is left."
- Reused, not new: `NoSoundText.SilentBalloon` (silent take), `NoSoundText.NoMicrophoneBalloon` (no microphone), the
  pill's "Transcribing…" and "Polishing…" (`PillText`), each clean-up's status line, and the validator's reasons.

**Lens fixes (22/10/2026), lens on 17ae8c3 (Needs changes):**

1. The cloud line is pinned: shown for a clean-up that sends text, absent for local, rules and off.
2. A dictation started elsewhere (hands-free or toggle) keeps its hotkey and Esc. While the page has no take of its own,
   it hands them to the controller (`AppShell.ForwardHotkey`). Record's Busy line is pinned.
3. Stop from the keyboard moves focus to step 2. Record is off while the steps run, so focus had gone nowhere.
4. A pin keeps the finished text out of the "nothing pasted" log line when `log_transcripts` is off.
5. README: "one whole recording" (hands-free parts are not demonstrated), and the Settings row says the recording is never saved.
6. The capture's `Failed` event drops the take and says so, as calibration does. The take is also dropped when the window
   goes behind another (its key-up would not come back). That second path has no pin: the headless platform does not deactivate a window.
7. `AppShell.DemonstrateAsync` reads the lazy pipeline on the UI thread before `Task.Run`.
8. Record is off until a speech model is ready. PROPOSED (new): "No speech model is ready yet, so Record is off. Settings > Models shows the model's state." While one loads, the welcome window's "Getting the speech model ready…" is reused.
9. The 30 s limit path is pinned (it stops, runs, and keeps "Stopped at 30 s…").
10. Each stage's text and verdict TextBlocks are polite live regions (a Border has no automation peer, so the text carries it).
11. A missing space in `AppShell.cs` is fixed.

**Re-lens fixes (22/10/2026), re-lens on aebd2fd:**

1. **One decision per press (major).** Forwarding was decided per event. A press could be split: its key-down went to a
   dictation, Esc was forwarded and cancelled it, and the key-up then reached the page (now idle) and was dropped. The
   controller kept the key held and ignored the next real press as auto-repeat (`DictationStateMachine`). Now a press is the
   page's only if the page saw its key-down with no dictation under way, or with its own take open. Any other press goes to
   the controller whole, its key-up and Esc included. Pinned with that exact sequence: the next press dictates and pastes.
2. The window-deactivated drop is pinned through `WindowDeactivated()`, which the Deactivated handler calls.
3. Stop moves focus to step 2 only when Record's focus came from the keyboard (Tab or the arrows); after a click focus is left
   alone. Both cases are pinned.
4. Record turning on when the model finishes loading is pinned end to end through the shell's own events (ModelManager.Changed,
   ModelStateChanged, StatusChanged, Changed, SettingsWindow's refresh, the page's Redraw), not `page.Refresh()`.

**After cloud speech Core (PR #48, merged into this branch at 39fd139):**

1. `AppShell.Pipeline` asks the speech step (`token => _speech.GetAsync(token)`), as #48's inline pipeline did, and the
   controller and the page share it. Pinned: with cloud speech on, the demonstration is one request to the speech service a
   dictation uses and shows that service's text. Pointing the pipeline back at the local model fails both this pin and
   `CloudSpeechShellTests.A_dictation_through_the_shell_goes_to_cloud_speech`.
2. "The sound never leaves this PC" is shown only while the model on this PC is the speech step. PROPOSED for cloud speech:
   "This page pastes nothing. The recording is kept in memory only and never saved to disk. With cloud speech on, the sound is
   sent to {host} to be turned into text." and, for step 2, "With cloud speech on, {host} turns the sound into words (the
   speech model on this PC, if that fails). This is the raw text." Pinned.
3. The `SpeechReady` gate is unchanged. Record gates on the local model, as the hotkey does (`hotkeyBlocked: !_models.IsReady`).
   The local model stays loaded as cloud speech's fallback (`CloudSpeech` summary), so the page and the hotkey agree.
   The page does not say when cloud speech fell back to the local model for a take, because `PipelineProgress` does not carry it.

**Final re-lens on 7a40131 (one minor):** letting go of the hotkey (`Release()`: deactivation, another page, close) now also ends the page's press (`_ownPress = false`). Before, a take started with the hotkey and dropped by deactivation left the page owning that press, so a later hold dictation released after the page took the hotkey back had its release swallowed. Pinned with that sequence.

## Anthropic with no key shows the waiting line (22/10/2026, branch fix/anthropic-missing-key-line)

Found by the cloud-setup-help lane: with Anthropic chosen in Settings > Clean-up, "Allow" pressed and no `ANTHROPIC_API_KEY`, Dictum used the local model with nothing on screen saying why, the card stayed in its off state ("Use cloud clean-up…", no off switch), and the page said "Cloud clean-up is on.". The OpenAI and Google Gemini presets already showed the approved waiting line on the card in the same case, but the page said "Cloud clean-up is on." above it too. Nothing was sent in either case.

1. **Cause.** `EffectiveSettings.Cleanup` (every load) switched `anthropic` without its key to `local`, also when a consent was on file. The settings in use then named no cloud host, so the card read as off, and the cleaner in use was the local model, so it recorded no missing key. The page's "Cloud clean-up is on." was a fixed string, said whenever `EnableCloud` returned no error, whatever the clean-up in use did.
2. **Fix, settings.** The key repair now runs only when no consent is on file (`!CloudPolicy.HasConsent`). That keeps its purpose, a settings.json written by the first build (which defaulted to `anthropic` and has no consent), and its log line. Anthropic agreed to keeps its provider, so `TranscriptCleaner.Create` takes its existing no-key branch: the rule-based tidy, nothing sent, `MissingKey = ANTHROPIC_API_KEY`. This matches the presets, which use the rule-based tidy, not the local model, while they wait for a key (SPEC §5 keys).
3. **Fix, page.** After a successful switch-on the page says "Cloud clean-up is on." only while the clean-up in use sends the text (`TranscriptCleaner.SendsText`); otherwise it says the card's own state line (`CleanupPage.StateLine`, factored out of `Redraw` unchanged), so the waiting line or "On, but nothing is sent: the clean-up in use runs on this PC.". The live status line therefore announces the true state to a screen reader as well.
4. **Copy.** No new or reworded string. The Anthropic case reuses the approved `CloudPolicy.WaitingForKeyLine` with `ANTHROPIC_API_KEY` as its variable; the approval of 16/10/2026 already lists that variable (see "Local server and key lines" above, and SPEC §5 keys). This is reuse of approved copy, not a reword.
5. **Tray and About.** Both already read the clean-up in use. With the fix, About says "Clean-up: rule-based tidy only: ANTHROPIC_API_KEY is not set (environment or .env)." and the tray panel's privacy line says "Nothing leaves this PC", which SPEC §5 requires while the clean-up in use sends nothing and no server is involved. SPEC §5 has no tray form of the waiting line, so none is added; a tray waiting line would need new approved copy.
6. **Pins.** `CloudWarningAndPresetsTests.Switching_on_from_the_page_without_the_key_shows_the_waiting_line_and_never_says_on` (Anthropic and OpenAI, through the page's own button): the cleaner waits for the key and nothing leaves; straight after Allow the page shows the approved waiting line and not "Cloud clean-up is on."; the card shows the on state with its off switch and the waiting line; Privacy carries the waiting line; About names the rule-based tidy and the missing key; the tray panel shows "Nothing leaves this PC" with its lock and not the cloud line. `SettingsRepairTests.Anthropic_agreed_to_without_a_key_stays_anthropic_so_the_pages_can_say_it_waits_for_the_key` pins the repair's new boundary. `FixBatchSettingsTests.The_cleanup_repair_is_told_whether_the_key_is_present` pinned the old behaviour (consent and no key gave `local`); it now checks through `ConfigLoader` that the key's absence is the logged reason without a consent, that a key alone does not pass a missing consent, and that a consent keeps `anthropic` with and without the key.
7. **Mutation (observed 22/10/2026).** Removing the consent condition from the repair fails the repair pin, the ConfigLoader pin and the Anthropic case of the page pin (the clean-up in use is the local model again). Restoring the fixed "Cloud clean-up is on." fails both cases of the page pin.
8. **README** lines for `llm.provider` and `ANTHROPIC_API_KEY` now say what each case does.

## Release 1.2.1 prepared (22/10/2026)

Branch `release/1.2.1` from origin/main afda8d9. All new wording below is PROPOSED until the owner approves it. `site/version.json` is not touched (set at site deploy time), no tag is created, and nothing is built, published or submitted: the tag and the publish wait for the owner.

1. `Directory.Build.props`: `<Version>` 1.2.0 to 1.2.1 (package 1.2.1.0). `README.md` is unchanged: the 1.2.0 release PR changed one feature bullet (the "Free" line, already in place), and the README carries no version line that moves with a release.
2. `docs/STORE.md` (PROPOSED): a "What's new in this version (1.2.1)" block above the 1.2.0 one (the 1.2.0 block stays as history); the certification notes header "Notes written 22/10/2026 for Dictum 1.2.1." and a rewritten CHANGED SINCE THE LAST SUBMISSION paragraph, every other paragraph of the block byte-identical; the version references in sections 1 and 6 and the header line; a certification-record row.
3. **In 1.2.1, user-visible** (each found in the entries above and in `git log v1.2.0..origin/main`): the hold pill "Recording · let go to finish"; the tray state "No microphone"; the Simple view without the Light | Full choice; the tray header "No clean-up" when clean-up is Off ("Owner approvals (22/10/2026)", PR #45); Settings > Storage can change the model folder, with the drive-speed sentence and the network-folder warning (PR #51); Settings > See how it works (PR #47); Help's "How do I turn on cloud clean-up?", also on the site's help page with a demo (PR #49); the "On, but waiting for your ANTHROPIC_API_KEY" line for Anthropic with consent and no key (PR #52).
4. **Not new in 1.2.1, so left out of the notes:** hotkeys shown by their key names (`HotkeyNames.Display`) and Help's "Questions on this page" index. Both are already in the v1.2.0 tag (commit f6c0950, "1.2 polish").
5. **Not user-visible, so not in the Store notes:** deterministic test fixes (PRs #42, #43, #44, #50), the TimeProvider seam, and the cloud speech engine (PR #48, Core only), which has no screen yet and is not named in any public note.
6. **PROPOSED, awaiting the owner:** the 1.2.1 "What's new" paragraph and the 1.2.1 CHANGED paragraph of the certification notes. Store screenshot 1 is to be retaken with the new hold pill. No tag, installer, MSIX, site deploy or Store submission is made until the owner approves the wording.

## Owner approvals (22/10/2026, the 1.2.1 block)

Recorded on branch `docs/approvals-1.2.1` from origin/main 8869d11. Markers only: no string, caption or paragraph changed a character.
The earlier entries are left as written; this entry supersedes their "PROPOSED" and "awaiting the product owner" notes for exactly the
items listed in 1 to 4 below, and for nothing else.

1. **Section 1, Settings > Help: "How do I turn on cloud clean-up?"** (approved 14:25 AEDT). `HelpText.CloudQuestion`, `HelpText.CloudAnswer`
   and the comment where the question joins the list (`src/Dictum.Core/Help/HelpText.cs`) now read APPROVED 22/10/2026. The site's help
   section (`site/help.html`, #cloud) and the cloud-cleanup demo data (`scripts/demos/cloud-cleanup.json`) carried no PROPOSED marker, so
   they are unchanged; `docs/DEMOS.md` gets a line saying demo 7's captions are APPROVED (the marker on the other demos' captions is untouched).
2. **Section 2, Settings > Storage** (14:25 AEDT). The **Change…** button (`StoragePage.ChangeLabel`), its accessible name "Change the model
   folder" (`ChangeName`) and "A slower drive makes the speech model slower to load and to switch." (`SlowerDriveNote`), in
   `src/Dictum.UI/Views/Settings/ModelPages.cs`; and the consent.json label "consent to cloud clean-up and cloud speech"
   (`PathRegistry.Locations`, `src/Dictum.Core/Storage/PathRegistry.cs`).
3. **Section 3, Settings > See how it works** (14:25 AEDT). Every string in `HowItWorksText` (`src/Dictum.Core/Core/HowItWorksText.cs`), its
   one marker covering the page title, intro, memory notes (both cloud speech variants), buttons and names, status lines (including "No speech
   model is ready yet…"), stages, states, figures and notes. `HowItWorksPage.cs`, `AppShell.HowItWorks.cs` and `HowItWorksRun.cs` carried no
   PROPOSED marker. SPEC §29 and the README line about the page now say APPROVED. The page still has no Help link; that is a follow-up.
4. **The two `docs/STORE.md` 1.2.1 paragraphs** (a later message, 14:33 AEDT). The "What's new in this version (1.2.1)" paragraph, and the
   certification notes' "Notes written 22/10/2026 for Dictum 1.2.1." line with its CHANGED SINCE THE LAST SUBMISSION paragraph, are APPROVED as
   written, marked the way the 1.2.0 block is. A history row records it. Item 6 of "Release 1.2.1 prepared" (above) is superseded by this.
5. **Pins.** `OwnerApprovalsTests` gains three tests pinning sections 1 to 3 word for word, independently of the app's constants (the Help
   question and its first and last paragraphs; Storage's three strings and the consent label; the See how it works strings).
6. **Still PROPOSED (not approved, markers untouched).** Section 4 of the owner's list: the cloud speech engine lines in
   `src/Dictum.Core/Transcribe/SpeechPolicy.cs` (`SpeechStatus.*`, the plain-http refusal, the log lines), the `speech` note in the default
   settings.json, `SettingsLoader.SpeechBaseUrlProblem`'s wording, and `CloudSpeechTests`' note on them. Also anything on other branches
   (for example the cloud speech UI PR). The consent text and its two buttons were already APPROVED (10:50 decision).

## Cloud speech, UI (22/10/2026, branch feat/cloud-speech-ui)

Lane C-UI of the design brief's "Cloud speech" appendix (§2, §3, §4, §7 item 9, §8), on the Core half (PR #48). `site/help.html`
was edited only on the owner's instruction, to keep the site's Help equal to the app's (items 16, 19, 23 and 25); README and Store
copy are the C-Copy lane's, gated on owner approval.

1. **Cause.** The Core lane built the gate, the request and the fallback, but nothing in the app could record the speech
   consent or switch cloud speech off, and the surfaces that say what leaves this PC did not know about audio: with cloud
   speech on, the tray panel and Settings > Privacy would still have said "Nothing leaves this PC".
2. **Consent step (`AppShell.EnableCloudSpeech`).** Mirrors clean-up's `EnableCloud`: a plain `http://` address and offline
   only are refused before any dialog; then a second dialog, apart from clean-up's, with the approved `SpeechPolicy.ConsentText`
   and buttons (`AgreeButton(host)` / `DeclineButton`, the default). Every address needs it, a server on this PC included.
   Declining writes nothing. Agreeing writes the speech entry of consent.json (`CloudConsentFile.WriteSpeech`: provider, host,
   time, app version), then speech.provider / base_url / model; when settings.json cannot be written, the speech entry is
   withdrawn again, and if that fails too the switch-off below holds instead. A clean-up consent on file is never touched.
3. **Switch cloud speech off (`DisableCloudSpeech`, `SpeechChoices`, `SaveSpeechOff`).** Mirrors `DisableCloud`: in memory
   first (`_speechWithdrawn`: speech.provider local and no speech consent, whatever the files say), so the reload retires the
   cloud engine; then `CloudConsentFile.WithdrawSpeech` (the clean-up entry stays; with none left the file goes) and
   speech.provider = local. Until both files keep it, it holds and is retried at every reload and at quit. One flag, not
   clean-up's two: every speech address needs the consent, so there is no server-on-this-PC case to keep apart.
4. **Settings > Models (Advanced view only).** A "Cloud speech" card under "Speech model", laid out as Settings > Clean-up's
   Cloud clean-up card, with the same automation names (Service, Server address, Model). The address line shows only for
   Azure OpenAI (the customer's resource name) and a custom server; OpenAI and Groq use their preset address. No model is
   filled in: `SpeechPresets` carries no model id (the Core lane's note: ids from a page summary are not put into code), so
   the field's placeholder says what to type. The key line follows the address (`SpeechPresets.ForAddress`). The Simple view
   hides the whole card.
5. **Surfaces, all from the speech step in use** (`CloudSpeech.Status`, `SendsAudio`, `LastFailure`; the last two and `Model`
   are new pass-throughs on `CloudSpeech`). The card's line is `SpeechStatus` (Core). The tray panel shows the Speech model value
   under the pickers and, in the footer, the same line: it takes the place of the approved "Nothing leaves this PC" row while
   clean-up sends nothing (that row is hidden, not reworded) and sits above the clean-up row otherwise. Privacy has a speech
   line above the clean-up summary; About adds a speech line; the pill takes a label from the shell (`AppShell.PillLabel`).
   After a failed request the card, tray, Privacy and About show `SpeechStatus.Failed` and the pill says
   `SpeechStatus.FallbackPill` in place of "Pasted", until the next success. The load log line is now built by
   `CloudSpeech.LoadLine` with the consent's date (consent.json is read for the date only) and the local model's name; the
   per-failure line is the Core lane's, already the spec's wording.
6. **Approved sentences made untrue by cloud speech: hidden or replaced, never reworded.** Privacy's default "Nothing leaves
   this PC: speech recognition and clean-up both run here." gives way to the speech line. Privacy's cloud clean-up sentence
   says "Audio never leaves this PC.", so with both on it gives way to the Clean-up card's own on line
   (`CleanupPage.StateLine`, now internal, unchanged), which says the same about the text without the audio claim. The Clean-up
   card's introduction ends "Audio never leaves this PC." and is one string, so it is hidden while cloud speech sends audio.
   All three come back byte for byte when cloud speech is off. **Owner decisions pending:** a replacement for the Clean-up
   introduction (the spec's §5 proposes "Audio never leaves this PC unless cloud speech is on.") and for the Privacy clean-up
   sentence when both are on. Left as is and not edited (C-Copy territory): `HelpText` says "your voice still stays here" in
   three places (the cloud clean-up answers), untrue while cloud speech is on; Privacy's offline-only hint names downloads
   and cloud clean-up but not cloud speech (still true: "No network at all").
7. **PROPOSED strings** (each marked `PROPOSED` at its source):
   - Consent dialog title: "Send your voice to a cloud service?"
   - Declined: "Cloud speech was not switched on; nothing is sent."
   - Offline refusal: "offline_only is on, so cloud speech is refused. Switch offline only off in Settings > Privacy first."
   - Missing fields: "Type the service's https:// address and the model (for Azure OpenAI, the deployment name)."
   - Card: title "Cloud speech"; buttons "Use cloud speech…" and "Switch cloud speech off"; introduction "Optional, for a slow
     PC or one without a GPU. A cloud service turns each recording into text instead of the speech model on this PC, but the
     recording of every dictation, your voice, then goes to that service. The speech model on this PC stays as the
     fallback."; address hint "Dictum adds /audio/transcriptions. Cloud speech needs an https:// address, also for a server
     on this PC."; address placeholders "https://your-resource.openai.azure.com/openai/v1" (Azure OpenAI) and
     "https://your-server.example.com/v1"; key lines (clean-up's sentences) "The key goes in .env in the settings folder:
     {VAR}=your {service} API key. Without it nothing is sent." and "If the server needs a key, it goes in .env in the settings
     folder: {VAR}=your key."; offline note "Offline only is on (Privacy), so cloud speech cannot be switched on."; "Cloud
     speech is on."; "Cloud speech is off; the speech model on this PC hears your dictations again."
   - Card state lines: the Core lane's `SpeechStatus` (On, Failed, NotAgreed, TranslateRefused, NothingSent; WaitingForKey and
     OfflineOnly are clean-up's approved lines).
   - Tray panel Speech model value and footer: "Cloud speech · audio leaves this PC ({host})", with the Failed line under it after a fallback.
   - Privacy: "Your voice goes to {host} for speech recognition. Advanced option: your audio leaves this PC. Dictum does not
     validate or certify how that service handles it." (with the Failed line under it after a fallback; the speech step's own
     line while cloud speech is on but sends nothing).
   - About: "Speech: cloud, {host} ({model}); {local model} on this PC is the fallback." ("Speech: {status}." after a
     fallback or while it sends nothing).
   - Load log: "Cloud speech on: audio goes to {host} ({model}); consent recorded {yyyy-MM-dd}; {local model} is the fallback."
   - Pill: "Hearing… (cloud)"; after a fallback, the Core lane's "Heard on this PC (cloud speech failed)".
   - Switch-off not saved (mirrors clean-up's approved `OffNotSaved`): "Cloud speech is off, but consent.json could not be
     changed ({reason}). Nothing is sent. Dictum tries again at the next reload and when it quits; until it is changed, your
     speech consent stays on file."; "Cloud speech is off for this session, but settings.json could not be saved ({reason}).
     Nothing is sent. Dictum tries again at the next reload and when it quits."; and the both-files sentence ending "the
     next start turns cloud speech back on, unless settings.json cannot be read, in which case Dictum starts offline."
8. **Pins** (`CloudSpeechUiTests`, headless): (1) declining records nothing, writes nothing, sends nothing, with the approved
   text and buttons and decline as the default; settings.json alone never switches it on; (2) agreeing writes only the speech
   entry and the card, tray (value and footer), Privacy, About, load log and pill show the on state, "Nothing leaves this PC"
   gone; (3) Switch cloud speech off withdraws only the speech entry (clean-up consent and cloud clean-up untouched), surfaces
   back to the default, "Nothing leaves this PC" back when clean-up is local; a switch-off that cannot be saved holds; (4)
   waiting-for-key and offline-only lines on the card, Privacy and About, and switching on refused under offline only; (5) a
   dictation whose request fails is heard on this PC, the log names the reason, the card, tray, Privacy, About and pill show
   the fallback; (6) the Simple view hides the card; (7) the approved default sentences byte for byte against literal copies.
9. **Mutations (observed 22/10/2026).** Pin 1 observed failing with the consent gate removed (the dialog's answer ignored:
   consent.json was written). Pin 3 observed failing with the speech withdrawal removed (the speech consent stayed on file),
   and observed failing with the withdrawal replaced by deleting consent.json (the clean-up consent went too). Pin 7 observed
   failing with the Privacy default sentence changed by one word. Pin 2 observed failing with the approved default shown
   while cloud speech sends audio.
10. **Gates.** Release build with `--no-incremental`: 0 warnings, 0 errors. Full suite (xunit.v3, Release): 1897 total, 0 failed, 23 skipped, 0 errors.
11. **Lens fix round (lens on 59ae16f, verdict Needs changes, no blocker).**
    - *Failure-path pins (major).* The lens deleted the withdraw-again call after a failed settings.json write and the suite
      stayed green. New pins in `CloudSpeechUiTests`: L1 settings.json read-only at switch-on (the write-failed sentence, no
      consent.json, settings.json unchanged, nothing sent); L2 consent.json held open from the "agreed to" log line (the
      consent stays on file but unused, also after settings.json is edited by hand to that host, and is withdrawn at the
      next reload once let go); L3 a clean-up consent survives a failed switch-on; L4 switch-off with settings.json
      read-only (the "settings.json could not be saved" sentence; the next start shows NotAgreed and sends nothing); L8
      switch-off with both files blocked, both let go before quitting (after quit the speech consent is gone and
      speech.provider is local).
    - *Mutations (observed 22/10/2026), each restored afterwards.* With the withdraw-again try/catch in `EnableCloudSpeech`
      removed: L1 observed failing (consent.json left on file), L2 observed failing (the hand-edited host was used), L3
      observed failing (the speech consent stayed beside the clean-up one). With the speech withdrawal in `SaveSpeechOff`
      removed: L4 observed failing (the next start was not NotAgreed). With `SaveSessionChoices()` at quit removed
      (`AppShell.Dispose`): L8 observed failing (the speech consent was still on file after quit).
    - *Header, tooltip, feedback (minor).* While cloud speech sends each recording, the tray panel's header (both views),
      the icon's tooltip and the support emails' models line carry the PROPOSED token "cloud speech" after the speech runtime
      (`AppShell.CloudSpeechToken`), and the feedback email has a PROPOSED line "Speech: cloud speech (openai-compatible)"
      (`FeedbackSetup.CloudSpeech`: the provider only, never the host, as the clean-up fact). Pinned.
    - *One consent.json read (minor).* `ConfigLoadResult.SpeechConsentAgreedAt` carries the loader's read of the speech
      entry's date to the load line; the shell no longer reads the file a second time.
    - *Markers (minor).* `SpeechTrayValue` and `SpeechAboutValue` are marked PROPOSED at the source.
    - *Left for the owner, unchanged:* Privacy can show a clean-up "nothing is sent" line (waiting for a key, offline only)
      under the speech line without saying it is about clean-up (options: a PROPOSED "Cloud clean-up:" caption before it);
      the Simple view hides the card's on state and its off switch too (options: show the state line and "Switch cloud
      speech off" in Simple while cloud speech is on).
    - *Correction.* Item 10 earlier called the 23 skips "the existing environment-gated skips"; that was not checked against
      main, so the claim is removed.
12. **Gates after the fix round** (after merging origin/main): Release build with `--no-incremental`, 0 warnings, 0 errors; full suite 1906 total, 0 failed, 23 skipped, 0 errors.
13. **Owner decisions on the open points (22/10/2026, 16:22-16:31 AEDT), applied.**
    1. *Privacy caption.* While cloud speech sends audio and Privacy shows a clean-up line, the caption "Cloud clean-up:"
       (`AppShell.CloudCleanupCaption`, APPROVED 22/10/2026; the lens's wording) sits above it, so a clean-up "nothing is
       sent" line is not read as being about speech. The approved clean-up lines are byte-identical. Pinned.
    2. *Simple view.* While cloud speech is on (`CloudSpeechOn`), the Simple view shows the card's state line and "Switch
       cloud speech off"; the setup fields (service, address, model, key line) stay Advanced-only. With it off, Simple shows
       nothing of the card. This supersedes item 4's "The Simple view hides the whole card". Pinned both ways, and the off
       switch works from Simple.
    3. *Replacement sentence.* APPROVED 22/10/2026 16:27 AEDT, verbatim (`AppShell.AudioByDefaultSentence`): "Audio never
       leaves this PC by default, though advanced users can add their own cloud speech and clean-up services in settings."
       While cloud speech sends audio it takes the place of "Audio never leaves this PC." in the Clean-up card's introduction
       and in Privacy's cloud clean-up sentence (`AppShell.WithAudioSentence`, an ordinal replace on the approved line). With
       cloud speech off both originals are byte-identical. This supersedes item 6's hiding of the introduction and the Clean-up
       card's on line in Privacy (`CleanupPage.StateLine` is private again). Pinned in both states, byte for byte.
    4. *Tray.* The speech line is shown once, in the footer, where "Nothing leaves this PC" sits when off (with the failed line
       under it after a fallback); the Speech model picker keeps the model's name. Pinned (one occurrence; the picker's
       selection starts with the model's name).
    5. *Offline only.* The owner keeps allowing a speech server on this PC (loopback https) under offline only, as clean-up
       does: no gate change. `SpeechPolicy.ConsentText`'s last paragraph has one clause revised and APPROVED (16:31 AEDT):
       "offline only mode refuses it" became "offline only mode refuses any service that is not on this PC"; the rest of the
       constant is byte-identical. Marked "APPROVED 22/10/2026 (clause revised 16:31 AEDT): do not reword"; the byte pins in
       `CloudSpeechTests` and `CloudSpeechUiTests` (now a literal copy too), SPEC §28 and the Core entry's item 10 are updated.
       The switch-on path still refuses under offline only before any dialog, as clean-up's does.
    - *Mutations (observed 22/10/2026), each restored.* One character changed in the replacement constant ("settings." to
      "settings!"): the replacement pin observed failing (the introduction's on state). One character changed in the Clean-up
      introduction literal ("service." to "service,"): the replacement pin and the default-sentence pin observed failing. One
      character changed in Privacy's approved clean-up sentence ("on." to "on,"): the replacement pin (Privacy's on state) and
      the clean-up-sentence pin observed failing. One character changed in the caption ("Cloud clean-up:" to "Cloud clean-up;"):
      the replacement pin observed failing.
14. **Gates after the owner's decisions** (after merging origin/main 3f0eb06): Release build with `--no-incremental`, 0 warnings, 0 errors; full suite 1907 total, 0 failed, 23 skipped, 0 errors.
15. **Owner approval of the PROPOSED strings (22/10/2026, 17:22 AEDT), markers only.** The owner approved, as a block, every
    string item 7 lists, the Core lane's `SpeechStatus` lines, the settings loader's
    https message (`SettingsLoader.SpeechBaseUrlProblem`), the load and per-failure log lines, the `speech` note in the default
    settings.json, the "cloud speech" token and the support email's "Speech: cloud speech (provider)" line. Each marker now
    reads "APPROVED 22/10/2026 17:22 AEDT (proposed 22/10/2026): do not reword"; the per-failure log line and the `speech`
    note had no marker and now carry that comment. No string changed: in the marker commit, `git diff -U0 HEAD~1 -- src`
    changes 88 lines. 70 of them are comment lines. The other 18 are code lines whose only change is their trailing
    comment, checked by stripping the comment and comparing what is left. This supersedes the "PROPOSED" notes in items 7,
    11 and 13 and in the Core entry's item 10 for exactly these strings.
16. **Help qualifier (owner, 22/10/2026, 17:22 AEDT).** The three Help answers that said "your voice still stays here" (the
    "How Dictum works" cloud sentence, "Where does my voice go?"'s last paragraph, and the first paragraph of "How do I turn
    on cloud clean-up?") now say "your voice still stays here unless you also turn on cloud speech"; nothing else in them
    changed. `site/help.html` has the same change in its four matching paragraphs (the cloud answer is on the page twice).
    Markers: "APPROVED 22/10/2026 17:22 AEDT (qualifier added; proposed 21/10/2026)" on the first two; the cloud answer's
    marker keeps its own history (proposed 22/10/2026) and adds the 17:22 qualifier approval. `OwnerApprovalsTests`' literal
    for that paragraph is updated. New pin
    `HelpPageTests.The_voice_stays_here_answers_carry_the_cloud_speech_qualifier` (the three app answers and the four site
    paragraphs, none without the qualifier). It was observed failing (22/10/2026) with the qualifier removed from "Where does
    my voice go?", then restored. Not changed, for the owner: the first paragraph of "Where does my voice go?" still says
    Dictum "never sends it anywhere", which is untrue while cloud speech is on.
17. **Gates after items 15 and 16** (origin/main 3f0eb06 merged; no newer main): Release build with `--no-incremental`, 0 warnings, 0 errors; full suite 1908 total, 0 failed, 23 skipped, 0 errors.
18. **Re-lens on 3423468 (Needs changes, no blocker), fixes.**
    - *Approvals record (major).* Item 15 claimed `SpeechPolicy.LoopbackHttpRefusal` was approved; it is not in the owner's
      17:22 list (§3 lists only `SettingsLoader.SpeechBaseUrlProblem`). Its marker is PROPOSED again, and its doc comment no
      longer says the settings loader uses it (the loader's warning is `SpeechBaseUrlProblem`). Item 15 is corrected. Two more
      are marked PROPOSED, pending the owner, because the list did not name them. **Still PROPOSED** (verbatim):
      - `SpeechPolicy.LoopbackHttpRefusal`: "speech.base_url {host} uses http://, but cloud speech needs an https:// address,
        also on this PC, because its consent step says the connection is encrypted"
      - About's fallback form (`AppShell.SpeechAboutValue`): "Speech: {status}." (the speech step's line, after a fallback
        or while cloud speech is set but sends nothing)
      - The both-files switch-off sentence's last clause (`AppShell.SpeechOffNotSaved`): "; if both are still unchanged
        then, the next start turns cloud speech back on, unless settings.json cannot be read, in which case Dictum starts
        offline."
    - *Word-for-word pins (major).* `OwnerApprovalsTests` gains a "Cloud speech UI block (22/10/2026 17:22 AEDT)": one test per
      surface, each a literal `Assert.Equal` (the both-files sentence as `StartsWith` up to its PROPOSED clause). The tests cover
      the dialog and switch-on lines; the card (title, buttons, intro, hint, offline note, switched on/off, both placeholders,
      both key lines); the off-not-saved sentences; the tray line and token; the support-email line; the Privacy and About
      lines; the pill lines; the load log line and the loader's https message. *Mutations (observed 22/10/2026), one per
      group, each restored:* dialog title "?" to "!": the dialog pin observed failing. Card intro "stays as the fallback" to
      "stays the fallback": the card pin observed failing. Settings-only off-not-saved "quits." to "quits!": the off-not-saved
      pin observed failing. Tray line "leaves" to "goes off" (the lens's): the tray pin observed failing. Support email
      "Speech: " to "Speech - ": the email pin observed failing. Privacy "recognition" to "transcription" (the lens's): the
      Privacy and About pin observed failing. Pill "…" to "...": the pill pin observed failing. Load line "fallback" to
      "fall-back": the log and loader pin observed failing.
19. **Help, "Where does my voice go?" (owner, 22/10/2026 about 17:45 AEDT).** Its first paragraph now ends "and never sends it
    anywhere unless you turn on cloud speech."; nothing else changed. `site/help.html` has the same change. Marker: "APPROVED
    22/10/2026 17:45 AEDT (qualifier added; proposed 21/10/2026)". This supersedes item 16's "Not changed" note. Pinned byte for byte
    (app and site) in `HelpPageTests.The_voice_stays_here_answers_carry_the_cloud_speech_qualifier`, which was observed failing
    with the qualifier removed and then restored. The paragraph still opens "Nowhere."; the owner approved only the clause.
20. **Owner wording pending (no edit until the owner's words arrive).** Sentences still untrue while audio is sent:
    `ModelPages.cs` "Turns your voice into words, on this PC."; `SimpleView.cs` "Dictum turns your voice into text on this PC.";
    `HelpText.cs` and `site/help.html` "Nothing you say is sent anywhere unless you turn on cloud clean-up"; `CloudPolicy.cs`
    "(never the audio)" in clean-up's consent text when the clean-up and speech hosts are the same. Also recorded, owner
    option, no change: with both features on under offline only, Privacy can show the offline-only line twice with no caption (the speech
    step's line and the clean-up line; the caption shows only while audio is sent).
21. **Gates after items 18 and 19** (origin/main d838326 merged): Release build with `--no-incremental`, 0 warnings, 0 errors; full suite 1916 total, 0 failed, 23 skipped, 0 errors.
22. **The three strings left PROPOSED in item 18, approved (owner, 22/10/2026 18:46 AEDT), markers only.**
    `SpeechPolicy.LoopbackHttpRefusal`, About's fallback form "Speech: {status}." and the both-files switch-off sentence
    whole now read "APPROVED 22/10/2026 18:46 AEDT (proposed 22/10/2026): do not reword". No wording changed. They are in the
    `OwnerApprovalsTests` cloud speech UI block: the refusal and the both-files sentence as literal `Assert.Equal`s, and the
    fallback form through a shell agreed to under offline only. Nothing on this PR is PROPOSED any more.
23. **Owner wording for the remaining untrue sentences (22/10/2026 18:41 AEDT).**
    1. *Settings > Models, Advanced Speech model card.* While cloud speech sends audio, "Turns your voice into words, on this
       PC." gives way to "Turns your voice into words; with cloud speech on, {host} does this and the model here is the
       fallback." (`ModelsPage.SpeechIntroFor`); the rest of the line is unchanged. The approved original shows otherwise.
    2. *Settings > Models, Simple explainer.* While cloud speech sends audio, "Dictum turns your voice into text on this PC."
       gives way to "Dictum sends your voice to {host} to be turned into text while cloud speech is on."
       (`SimpleView.ExplainerFor`); the rest is unchanged.
    3. *Help, "How Dictum works" (app and site), permanent.* "Nothing you say is sent anywhere unless you turn on cloud
       clean-up" now reads "…unless you turn on cloud clean-up or cloud speech"; nothing else changed. Marker "APPROVED
       22/10/2026 18:41 AEDT (qualifier added; proposed 21/10/2026)". **For the owner:** the sentence goes on "in Settings >
       Clean-up", so it now places cloud speech in Settings > Clean-up, but cloud speech is in Settings > Models. I applied
       the wording as approved and did not reword it.
    4. *Cloud clean-up consent (`CloudPolicy.ConsentText`, SPEC §22), on the owner's instruction, counsel review pending.* The
       parenthesis "(never the audio)" is now "(the audio only if you also turn on cloud speech)"; every other character is
       unchanged. Marker "APPROVED 22/10/2026 18:41 AEDT (clause revised on the owner's instruction; counsel review pending):
       do not reword".
    5. *Same consent, new paragraph (owner, 16:35 AEDT).* After `ConsentWarning` and before "Nothing is sent until you agree":
       "By switching cloud clean-up on you accept responsibility for the security and privacy of the text sent there."
       (`CloudPolicy.ConsentResponsibility`). Same marker with 16:35.
    - *Quoted copies of the consent.* The Help answer and `site/help.html` quote only the warning paragraph (`ConsentWarning`),
      which did not change, so neither needed an edit. `scripts/demos/cloud-cleanup.json` does not quote the consent: its
      caption says only "with a warning". Updated pins: `CloudWarningAndPresetsTests` (the warning-paragraph pin now expects the
      new paragraph after it; the key-sentence pin likewise) and `ShellTests` (it checked "never the audio"), plus a new
      whole-text byte pin for Anthropic and a custom host, now in `OwnerApprovalsTests` (item 26).
      Not edited (C-Copy): `README.md` describes the consent as sending "the dictation's text, never the audio". The demo
      poster (`site/img/demos/cloud-cleanup-poster.png`, step 26 of 29, checked on 22/10/2026) shows the old dialog: "(never the
      audio)" and no responsibility paragraph. The GIF (`site/img/demos/cloud-cleanup.gif`) comes from the same recording and was
      not checked frame by frame. Re-recording the cloud clean-up demo is a 1.3 demos item.
    - *Pins.* `CloudSpeechModelsPageTests.The_models_page_on_this_PC_sentences_follow_cloud_speech` (moved there, item 26) (Advanced and Simple, both
      states, byte for byte); the Help pin gains the "or cloud speech" sentence (app and site); the consent byte pins above.
    - *Mutations (observed 22/10/2026), one character each, restored afterwards.*
      - Advanced replacement ("fallback." to "fallback!") and Simple replacement ("is on." to "is on!"): the models-page pin
        observed failing in both views.
      - Help "cloud speech" to "cloud-speech": the Help qualifier pin observed failing.
      - Consent parenthesis "speech)" to "speech.)": the consent byte pin observed failing.
      - Responsibility paragraph "there." to "there!": the consent byte pin and the warning-paragraph pin observed failing.
      - Original Advanced introduction ("slower." to "slower!") and original Simple explainer ("best." to "best!"): the
        models-page pin observed failing in both views.
      - The three 18:46 strings, each changed by one character ("encrypted" to "encrypted!", "Speech: " to "Speech - ",
        "offline." to "offline!"): the log-and-loader pin, the About fallback pin and the off-not-saved pin observed failing.
24. **Gates after items 22 and 23** (origin/main d838326 merged): Release build with `--no-incremental`, 0 warnings, 0 errors; full suite 1920 total, 0 failed, 23 skipped, 0 errors. A first full run printed no summary line (cause not found); the rerun, whose whole output was kept, is the one recorded.
25. **Help, "How Dictum works", paths named (owner, 22/10/2026 19:10 AEDT).** The sentence item 23.3 flagged now reads, app
    and site: "Nothing you say is sent anywhere unless you turn on cloud clean-up (Settings > Clean-up) or cloud speech
    (Settings > Models), and it is off unless you turn it on." The rest of the answer is unchanged. Marker "APPROVED 22/10/2026
    19:10 AEDT (paths named; qualifier 18:41; proposed 21/10/2026)". The Help qualifier pin now checks the sentence byte for
    byte in the app and on the site, and that neither still says "or cloud speech in Settings". This resolves item 23.3's note.
    Gates: Release build with `--no-incremental`, 0 warnings, 0 errors; HelpPageTests, OwnerApprovalsTests,
    PublicCopyNeutralityTests and SiteStoreFirstTests, 62 total, 0 failed, 0 skipped (the full suite was not rerun for this edit).
26. **Final lens on 4a3903e (housekeeping).**
    - *File sizes.* Two test files had grown past 1,000 lines. The whole-text consent byte pin moved to `OwnerApprovalsTests`,
      with the other literal pins, and carries its own literal copy of the warning. The Models-page pin moved to a new
      `CloudSpeechModelsPageTests.cs`. Line counts now: `CloudWarningAndPresetsTests.cs` 975, `CloudSpeechUiTests.cs` 972,
      `OwnerApprovalsTests.cs` 378, `CloudSpeechModelsPageTests.cs` 86.
    - *Docs.* Item 23's quoted-copies note is corrected. The demo poster does show the old consent wording (checked), the
      demo data does not quote the consent, and the re-record is listed for 1.3. The entry's opening line now says
      `site/help.html` was edited on the owner's instruction.
    - *Typos.* Two typos introduced by this branch's edits are fixed, so both lines match main again: `SimpleView.cs`
      "whichdoes" and `CloudWarningAndPresetsTests.cs` `ApprovedStateLine ="`.
    - Gates: Release build with `--no-incremental`, 0 warnings, 0 errors; CloudSpeechModelsPageTests, OwnerApprovalsTests,
      CloudWarningAndPresetsTests and CloudSpeechUiTests: 110 total, 0 failed, 0 skipped. The full suite was not rerun for this move.
27. **Owner edits, 22/10/2026 19:42 and 19:45 AEDT.**
    1. *Clean-up consent parenthesis corrected (19:42; on the owner's instruction, counsel review pending).* Item 23.4's wording
       was wrong. The sentence reads "every dictation's text (…) is sent to {host}", and {host} is the clean-up host, so "(the
       audio only if you also turn on cloud speech)" said the audio goes to the clean-up service. Cloud speech sends it to its
       own service, which may be a different host. The parenthesis is now exactly "(not the audio: cloud speech, if you turn
       it on, sends the audio to the service you choose for it)"; nothing else in the consent changed. The marker reads
       "APPROVED 22/10/2026 19:42 AEDT (parenthesis corrected: the earlier 18:41 wording named the clean-up host for the audio;
       counsel review pending): do not reword". Pins updated: the `OwnerApprovalsTests` consent byte pin, the
       `CloudWarningAndPresetsTests` warning-paragraph pin and `ShellTests`.
       Mutation (observed 22/10/2026): "for it)" changed to "for it.)". The consent byte pin, the warning-paragraph pin and
       `ShellTests.Cloud_clean_up_is_switched_on_only_through_the_consent_dialog` observed failing; then restored.
    2. *Simple explainer, second sentence (19:45).* While cloud speech sends audio, "The speech model does the listening."
       becomes "The cloud service does the listening; the speech model on this PC is the fallback."
       (`SimpleView.ExplainerCloudListening`, marked APPROVED 22/10/2026 19:45 AEDT). The original is byte-identical when cloud
       speech is off. `CloudSpeechModelsPageTests` now pins both states with both replacements. Mutation (observed
       22/10/2026): "fallback." changed to "fallback!". The Simple case of the Models-page pin observed failing; then restored.
    - Gates: Release build with `--no-incremental`, 0 warnings, 0 errors. OwnerApprovalsTests, CloudWarningAndPresetsTests, ShellTests,
      CloudSpeechModelsPageTests, CloudSpeechUiTests and SimpleViewTests: 138 total, 0 failed. Full suite: 1920 total, 0 failed, 23 skipped.

## Clean-up explainers (APPROVED 22/10/2026 19:20 AEDT, branch feat/clean-up-explainers)

Owner's question: new users do not know what "clean-up" is or why they would want it. Source: `.demo-preview/strategy-inclusive.md` section 3. **Every string below was proposed 22/10/2026 and APPROVED by the owner as a block at 19:20 AEDT, exactly as at head 1baa400; no string changed on approval, and no previously approved string was changed.** Facts behind the wording, each checked against the code: the rule-based tidy removes ums, ahs, comma-bounded fillers and a doubled "the the", and follows "scratch that" and "new line" only when said as a phrase of their own (`DeterministicCleaner`, `CleanupRulesTests`); the clean-up model's prompt asks for filler phrases such as "you know", false starts and "scratch that" (`CleanupPrompt.Default`; the prompt is an instruction, not a guarantee of what the model does); its answer is checked, and the rule-based tidy is used if the check fails or the model is too slow or not ready (`HowItWorksText.Rejected`, `NotAsked`); Off pastes what was heard (`SimpleView.OffSubLine`); both tidy choices run in full only when the language setting is English or Translate to English is on, otherwise spacing only (`WhisperLanguages.OutputIsEnglish`, the approved sentence in "How Dictum works"). The strategy draft's "Does not change your words" and "smooths grammar" were not used: grammar assist is a separate setting. Three row explainers in the first version of this branch were dropped after the lens (the Simple view already has approved sub-lines).

1. **Help question "What is clean-up?"** (`HelpText.CleanupQuestion`, `CleanupAnswer`), after "Why did it type the wrong words?" in the list and so in "Questions on this page":
   - "Clean-up is the tidying Dictum does between hearing you and typing. When we speak, we say ums and ahs and repeat ourselves. Clean-up takes those out and tidies the punctuation and capitals before your words are typed."
   - "You choose it in Settings > Models. Tidy my words uses fixed rules: it fixes punctuation and capitals, takes out ums, ahs, doubled words such as \"the the\" and fillers such as \"you know\" set off by commas, and follows \"scratch that\" and \"new line\" when they are said as a phrase of their own. It needs no download."
   - "AI clean-up (Add AI clean-up) is an optional download that runs on this PC. It also takes out \"you know\" and false starts anywhere in a sentence, and follows \"scratch that\" even mid-sentence. Dictum checks its answer against what you said; if the check fails, or the model is too slow, not ready or runs into an error, it uses the rule-based tidy instead."
   - "Off pastes what was heard; only your own corrections, snippets and the profanity filter (if on) still apply."
   - "Unless the spoken language is set to English (or Translate to English is on), only the spacing is tidied."
   - "Clean-up happens on this PC. The text of each dictation and the words in your dictionary go to another service only if you turn on cloud clean-up in Settings > Clean-up, and it is off unless you turn it on. Your voice still stays here unless you also turn on cloud speech. Cloud speech also sends the words in your dictionary, as a hint."
   - "To watch clean-up work on your own words, open Settings > See how it works and record a short dictation."
2. **Help question "How can I see what Dictum does with my words?"** (`HelpText.SeeHowQuestion`, `SeeHowAnswer`), directly after the one above:
   - "Open Settings > See how it works, press Record, say a sentence or two, then press Stop. The page shows five steps: what Dictum hears, what the speech model heard, the rule-based tidy, the AI clean-up, and what would be pasted."
   - "It uses your current settings. It pastes nothing, and the recording is never saved to disk."
3. **Site** (`site/help.html`): both questions are added to the questions list as `#faq-what-is-cleanup` and `#faq-see-how`, before the existing "What does clean-up do? Can I turn it off?" (left as it was), with the answers above word for word. `site/index.html` has no clean-up sentence that is wrong (its lines say tidy-up runs on your computer, cloud clean-up is off unless turned on, and an optional 1.1 GB tidy-up model), so it is unchanged.
4. **Not done.** The Welcome window has no step where clean-up is chosen (its six steps are Welcome, Model files, Speech model, Your hotkey, Microphone, Try it), so no example line was added there. The strategy's draft put a before/after example after the first dictation in step 6 (`TryItStep`, beside the accent explainer); that is a layout change in `OnboardingSteps.cs` and waits for the owner's word.
5. **Pins.** `OwnerApprovalsTests` (the block approved at 19:20 AEDT): both questions with their answers word for word and their place in the list. `HelpPageTests`: the question count (15); the claims of "What is clean-up?" checked against `DeterministicCleaner` (ums, doubled words, "scratch that", "new line", and "a new line of credit" left alone), `CleanupPrompt.Default`, the approved language sentence, `WhisperLanguages.OutputIsEnglish` (English, "auto", another language, another language with Translate on) and `TranscriptCleaner.Plain`; the See how it works answer against `HowItWorksText` (page title, Record, Stop, the five stage titles in order, pastes nothing, never saved to disk); the index lists both questions; the site's two answers equal the in-app ones paragraph for paragraph. The existing pin on "turn on cloud clean-up" paragraphs now counts four (the new cloud paragraph is one) and keeps its exact-case check for the other three; the new paragraph is checked on its own (it begins "The text of each dictation"), with the cloud speech sentence.
6. **Mutation (observed).** One letter changed in the first paragraph of "What is clean-up?" ("tidying" to "tidyng"): `The_site_guide_carries_the_clean_up_answers_word_for_word` observed failing (site and app differ at paragraph 0), as did the word-for-word pin in `OwnerApprovalsTests`. Restored. Re-run after the lens round; see the PR.

## Dictum in plain words (22/10/2026, branch feat/plain-words)

The owner approved the plain-words statement (`.demo-preview/terms-disclaimer.md`, sections 2a, 2b, 2c and 3) at 17:36 AEDT, and the landing-page phrase change at 17:42 AEDT. Built word for word:

1. **Site page** `site/plain-words.html` (section 2a), the privacy page's layout, marked APPROVED 22/10/2026 17:36 AEDT in its source. Linked from the footers of `index.html`, `help.html`, `privacy.html` and itself. The privacy policy's body is untouched: its link is its footer. `vercel.json` has `cleanUrls`, so `/plain-words` is served as `/privacy` is. The page prints no code path.
2. **Short version** (section 2b) as `PlainWordsText` constants, shown in the Welcome window's first step after `OnboardingText.WelcomeText` and before the "next few steps" line, as plain text blocks (four paragraphs, the last being "Read the whole statement at dictum.kynni.au/plain-words."). The paragraphs sit in a group with 4 px spacing so the step still fits the default window without scrolling (`Every_step_fits_the_default_window_without_scrolling`: 290 of 294 px).
3. **No acceptance gate** (section 2c). Welcome's Esc-keeps-defaults behaviour and the Store tester notes are unchanged.
4. **Store listing** (section 3): the sentence "Dictum is provided as is under the MIT licence; if you choose a cloud service, you take responsibility for what is sent to it." ends PRIVATE BY DESIGN in `docs/STORE.md`, marked APPROVED 22/10/2026 17:36 AEDT. The listing must be pasted again on the next submission.
5. **Landing page** (17:42 AEDT): "Free for life" became "Free to keep on your PC" at its four places in `site/index.html` (meta description, the at-a-glance list, the Free card heading, the comparison table's Price row). The approved Free line and the paid-extras line are byte-identical. No test, doc or source outside `site/index.html` carried the old phrase (grep of site, tests, docs, src, README).
6. **About card** (built after PR #54 merged): see item 10. The constants (`CardTitle`, `OpenLabel`, `Url`) are in `PlainWordsText`.
7. **Clean-up consent sentence (brief section 2c)**: built and approved separately on the cloud speech UI work (`CloudPolicy.ConsentResponsibility`, PR #54); nothing is added here for it.
8. **Brief section 5**: this build changed none of the sentences the brief lists as at risk, and re-audited none of them; only "Free for life" was grepped across the repo (now gone from the site). The pre-existing tensions are as the brief lists them: Help lines that say "your voice still stays here" until cloud speech releases, and the privacy page's "Nothing else: no telemetry, analytics or crash reports" beside its Store crash-summary paragraph (the new page carries the Store paragraph).
9. **Known tight fit (Welcome step 1):** with the four paragraphs the first step measures 290 of 294 px in the default window (`Every_step_fits_the_default_window_without_scrolling`). `// simplification:` the paragraphs sit in a 4 px-spaced group to make it fit. Ceiling: 4 px of slack. Trigger that retires it: a font or text-size change, or that test reporting a scroll bar on the Welcome step; then raise `OnboardingWindow.WindowHeight` or move the short version to its own step.
10. **About card** (merged after PR #54): "In plain words" is the last card on Settings > About, with the four `PlainWordsText.Paragraphs` and the button "Open in your browser", which opens `PlainWordsText.Url` through the same `Shell.Platform.Open` the Privacy policy button uses. The other cards are unchanged.
11. **Two replacements, APPROVED 22/10/2026 20:45 AEDT (replace the 17:36 sentences; the lens found the originals contradicted on a literal reading).** Page, second paragraph: "It does not count how you use it. It does not phone home. Dictum sends nothing to Kynni." became "It keeps no usage statistics for anyone but you: its log stays on your PC and never records what you said. It does not phone home: the only request to Kynni's website is Check for updates, which you press, and it sends nothing about you." Short version (About and Welcome): "No spyware: Dictum sends nothing to Kynni." became "No spyware: Dictum sends nothing about you to Kynni." Applied in `site/plain-words.html`, `PlainWordsText.Free` and the test literals.
12. **Privacy policy route, APPROVED 22/10/2026 20:47 AEDT:** `site/privacy.html`, "This website" list, now reads "Only when you open the privacy policy, Dictum in plain words or the full guide from Settings, or click Check for updates…", the rest of the sentence unchanged; the README network table has a matching row. The policy's "Last updated" date is not changed here: it is set to the 1.3 release date at tag time (the C-Copy lane owns that).
13. **Mechanical (lens on a95dd5e):** the `// simplification:` comment is in `WelcomeStep`; the landing-page pin counts exactly four case-sensitive "Free to keep on your PC" (mutation: the at-a-glance item changed to another phrase, observed failing, restored); README's About row and Welcome step 1 describe the card and the short version.

## Clean-up: your own instructions and Create the key file (22/10/2026, branch feat/own-instructions, release 1.3)

Two owner requests for Settings > Clean-up, built from main 04287d6. Every new string is PROPOSED; none is approved yet.

1. **Your own instructions.** New key `llm.instructions` (string or null, default null, multi-line, at most 2,000 characters:
   `LlmSettings.InstructionsMaxLength`). `CleanupPrompt.Build` adds them after the base prompt (built-in or `llm.prompt_file`)
   and before the grammar-assist sentence and the dictionary line, once, between `OwnInstructionsStart` and `OwnInstructionsEnd`.
   With `llm.prompt_file` set too, the file replaces the built-in prompt and the instructions are still added. The local model
   and cloud clean-up get them; `none` and `off` never do. Every reply is still checked by the validator, which gained one rule on the owner's decision (item 4).
   - *Private text.* `ModelCoordinator.TryWrite` logged every written value as JSON, so the box would have put the text in the
     log ("Set from the app: llm.instructions = …"). It now logs `SettingsFile.LogValue`, which gives the length for a key in
     `SettingsFile.PrivateKeys`. The loader's generic warning quotes up to 57 characters of a bad value, so the key has its own
     reader (`ObjectReader.PrivateText`), whose warnings give a length only. The clean-up's load line gives the length. The
     feedback email, the state lines and See how it works carry no prompt text (pinned).
   - *Validator gap, found by this lane, then closed on the owner's decision (22/10/2026 20:55 AEDT): see item 4.* A short
     sentence added on the same line as the dictation passed every rule. Probed 22/10/2026 with "um so i think we should uh
     move the meeting to thursday and tell the whole team before lunch": "… before lunch. Thanks for reading.", "… before
     lunch. Kind regards." and "… before lunch. Please call me if you have any questions." were all accepted.
   - *Page placement (done after PR #59 merged; see item 5).* The Clean-up page is `CleanupPage` in `src/Dictum.UI/Views/Settings/OtherPages.cs`, which
     PR #59 owned until it merged. The box is `OwnInstructionsCard` and the button `KeyFileButton` (both in `CleanupOwnInstructions.cs`).
     Before PR #59 merged, the wiring was only tried in this worktree and then removed.
2. **Create the key file.** `KeyFile.CreateIfMissing` writes `AppPaths.EnvFile` with `KeyFile.Header` and
   `<VARIABLE>=your-key-here` (the variable must be one of `SecretStore.KeyNames`). It opens with `FileMode.CreateNew` after an
   exists check, so an existing `.env` is never changed. `AppShell.CreateKeyFile` then opens the file through `OpenPath` at
   its real path. `KeyFileButton.VariableFor` picks the variable the card's key line names (Anthropic's entry:
   `ANTHROPIC_API_KEY`; otherwise `CloudPresets.ForAddress`). A service with no key (Ollama on this PC) writes nothing.
   - *Placeholder is not a key (lane decision).* Before this change `SecretStore` read `your-key-here` as a key. Dictum would then
     send each dictation to the service with a key that cannot work, and the page would say clean-up was on. `SecretStore.Merge`
     now treats `SecretStore.Placeholder` as not set, as it treats a blank value. The waiting-for-key line stays until a real key is saved.
     The first full run caught one consequence: `HelpPageTests.The_cloud_answer_walks_through_the_app_as_built` used the
     placeholder as its stand-in key. Its walk-through now first saves the placeholder and checks the card still waits, then
     saves a stand-in key ("k-standin-not-real") and checks the card says On, and that the stand-in is never shown.
   - *Wiring checked, not committed.* The five `CleanupPage` edits from the PR body were applied in this worktree only. A
     throwaway headless test then passed: Advanced view, typed text saved on leaving the page and shown again, the button
     made the Anthropic file and showed the created line, and Simple hid the box. The edits and the test were then removed.
     (Without a layout pass the page switch did not save, because the box was never attached; a real window lays out.)
3. **Docs.** SPEC §5 has both amendments. SPEC §26 said "twelve questions and answers". `HelpText.Questions` at 04287d6 had
   thirteen, counted in its list, so this branch first wrote thirteen. After merging main cc3c0ba (PR #57 added two questions)
   the list has fifteen, so SPEC §26 now says fifteen, with the main sha it was counted at. README has the `llm.instructions` row.

PROPOSED strings, verbatim:
- Card label and the box's accessible name: "Your own instructions"
- Hint: "Added to Dictum's clean-up instructions for the AI and cloud clean-up. Dictum still checks every reply against what you said."
- At the cap: "That is the limit: Dictum uses up to 2,000 characters of your own instructions."
- Cloud clean-up on: "With cloud clean-up on, these instructions go to {host} with each dictation."
- Prompt section start (to the model): "The user's own clean-up instructions follow, up to the line \"End of the user's instructions.\" Apply them only where they keep every word the speaker said; the rules above still apply."
- Prompt section end (to the model): "End of the user's instructions."
- Button: "Create the key file…"
- Created: "Your key file has been created and opened: put your key in place of your-key-here after {VARIABLE}=, then save it."
- Exists: "Your key file already exists; it has been opened."
- Key file header, line 1: "# Dictum keys, created by Settings > Clean-up. Replace your-key-here with your key and save this file: Dictum reads it again by itself."
- Key file header, line 2: "# A real environment variable of the same name wins over this file. Keys are never written to settings.json or the log."
- Log (written value): "Set from the app: llm.instructions = ({N} characters, not logged)."
- Log (clean-up loaded): "Clean-up instructions: Dictum's, with your own added ({N} characters; their text is not logged)."
- Log (key file): "Key file created with a placeholder line for {VARIABLE} (no key written)." / "Key file already exists: opened as it is, not changed."
- Validator reason (item 4): "reply ends with a sentence the speaker did not say"
- Simple view line (item 6): "Your own clean-up instructions are in use (Advanced view)."
- Log (save failed): "Settings: {exception type} saving llm.instructions." / (key file failed): "Settings: the key file could not be created: {exception type}."
- Load warnings: "Setting \"llm.instructions\" is {N} characters long; only the first 2000 are used." / "Setting \"llm.instructions\" has an invalid value: expected a string or null. It is not used."
- settings.json `_doc` llm note, added sentence: "instructions: your own clean-up instructions (at most 2,000 characters; Settings > Clean-up in the Advanced view writes them), added after the prompt, built-in or prompt_file, for the local model and cloud clean-up, never for none or off; every reply is still checked against what you said."

Pins (`OwnInstructionsTests`, `KeyFileTests`): round trip through settings.json, multi-line kept and blank read as none;
added once, in order, also after a prompt file; sent once to a cloud service; never used by `none` or `off`; the validator
still rejects a sign-off line, a bracketed remark, a translation and a summary that the instructions ask for; the cap (with
a surrogate pair at the cut) and warnings without the text; the log never has the text (the app's write, the reload, a value
over the cap); the feedback email never has it; See how it works never shows it; the box is Advanced-only, saves trimmed
text when focus leaves it, and shows the limit and cloud-host lines; the key file has the right variable for each service;
an existing file stays the same byte for byte; the file is opened at its path; both status lines; the placeholder counts as
not set and sends nothing.

4. **Final-sentence rule (owner decision 22/10/2026 20:55 AEDT).** `CleanupValidator.AddedFinalSentence`: in a reply of two
   sentences or more, the last sentence's words that the "scratch that" checks count (`IsCorrectionWord`: three letters or
   more, no function word, filler or spoken command) must be at least half said (how a word counts as said was reworked twice: items 6 and 7),
   the share the final-line rule uses. It runs after the dictionary-term and hands-free repeat rules: placed inside the
   final-line checks it first took the reason of `DictionaryInsertionValidatorTests` ("Push it to GitHub. GitHub.", still
   rejected, but with the new reason), so it moved and every existing reason stands. PROPOSED reason: "reply ends with a
   sentence the speaker did not say". Ceiling (`// simplification:` at the rule): a last sentence of words said earlier in
   another order passes; a short last sentence a grammar fix rewrites into other words is rejected (polish lost only).
   Pins: the three probed replies are rejected with that reason; the four earlier cases are still rejected with their own
   reasons; three replies ending with the speaker's own last sentence, tidied (fillers out, "their" fixed to "there",
   "scratch that" and "new line" followed), are used. The validator, cleaner, rules, dictionary, hands-free, LLM, calibration
   and See how it works test classes all pass (421 total, 0 failed, 2 skipped). README's rule list and SPEC §5 updated.

Mutations (each observed failing, 22/10/2026, then restored):
- Validator pin: `CleanAsync` used the model's reply without `CleanupValidator.Validate`. All four cases of
  `The_validator_still_rejects_what_the_instructions_ask_to_add_or_change` observed failing.
- Log pin: `TryWrite` logged `v.Value?.ToJsonString()` again. `The_log_gets_their_length_and_never_their_text` observed
  failing on `DoesNotContain(Marker)` ("zebracorn-7316" found).
- No-overwrite pin: `KeyFile.CreateIfMissing` without its exists check and with `FileMode.Create`.
  `An_existing_file_is_left_byte_for_byte_and_opened` observed failing on the byte comparison.
- Placeholder: `Merge` without `v != Placeholder`. `The_placeholder_counts_as_not_set_so_nothing_is_sent_with_it` observed
  failing ("your-key-here" returned as the key).
- Final-sentence rule: the rule's call disabled (`false &&`). The three same-line cases of
  `The_validator_still_rejects_what_the_instructions_ask_to_add_or_change` observed failing; the other four still passed.

Gates: Release build with `--no-incremental`, 0 warnings, 0 errors. OwnInstructionsTests, KeyFileTests, CloudWarningAndPresetsTests, CleanupOffTests, FixBatchSettingsTests, PublicCopyNeutralityTests and HelpPageTests: 166 total, 0 failed, 0 skipped. Full suite, first run: 1951 total, 1 failed (`HelpPageTests.The_cloud_answer_walks_through_the_app_as_built`, the placeholder stand-in, fixed as above), 23 skipped. Full suite after the fix: 1951 total, 1928 passed, 0 failed, 23 skipped.
Gates after merging main cc3c0ba (merge d33a8e6): Release build with `--no-incremental`, 0 warnings, 0 errors; the named classes plus HelpPageTests and OwnerApprovalsTests 192 total, 0 failed; full suite 1957 total, 1934 passed, 0 failed, 23 skipped. Gates with the final-sentence rule (item 4): Release build with `--no-incremental`, 0 warnings, 0 errors; the same classes 198 total, 0 failed, 0 skipped; full suite 1963 total, 1940 passed, 0 failed, 23 skipped.

5. **Clean-up page wiring (after PR #59 merged; main 28c3e65 merged as 8e2f205).** `CleanupPage` (OtherPages.cs) gains:
   the `_own` field and its construction; `_own.Card` between the Grammar and Cloud clean-up cards; `_own.Redraw(s)` in
   `Redraw`; `KeyFileButton.Create(…)` beside "Open the settings folder" (variable from `KeyFileButton.VariableFor(Preset,
   Address(Preset))`, the card's own key line for a service with no key); and a `Leave` override that saves the box, so
   text is kept when another page is chosen or the window closes, besides focus loss. Every other card is unchanged.
   Pins (`CleanupPageOwnInstructionsTests`): the Advanced view shows the box in place and saves it on leaving the page and
   on closing the window; the Simple view hides it (and a switch to Advanced shows it); the button sits in the folder
   button's row, creates the Anthropic key file and shows the created line; an existing `.env` is opened and not changed.
   Mutation: `OwnInstructionsCard.Redraw` showing the card in both views; `The_Simple_view_hides_the_box` observed failing,
   then restored. Gates: Release build with `--no-incremental`, 0 warnings, 0 errors; OwnInstructionsTests, KeyFileTests,
   CleanupPageOwnInstructionsTests, CleanupOffTests, CloudOffTakesEffectTests, CloudWarningAndPresetsTests, HelpPageTests,
   OwnerApprovalsTests and PublicCopyNeutralityTests 230 total, 0 failed, 0 skipped; full suite 1982 total, 1959 passed,
   0 failed, 23 skipped.

6. **Lens on 5e9c227: Needs changes, no blocker; fixed (22/10/2026).**
   1. *"… day." sign-offs passed.* `SoundsLike` matched "day" against function words ("to", "the" share its sound key). The
      rule now sound-matches only against dictated words that are themselves counted (`IsCorrectionWord`).
   2. *Honest tidies rejected.* The lens's trial fix is in: words with a digit are left to the number rule; an apostrophe word
      is said when the part before the apostrophe was; a word that sounds like two or three dictated words joined is said
      (the sound-key routes were replaced in item 7).
      `FinalSentenceRuleTests` pins the lens's corpus (its harness `final-fix.fsx`): 36 honest tidies accepted (four of its 40
      are left out: three rejected by older rules on main too, and "i asked him he go" → "I asked him. He went.", pinned as
      the one recorded exception), and 25 closing sentences rejected ("Have a great day.", "Have a nice day.", "Good day.",
      "Kind regards.", "Thanks.", "Talk soon.", "Take care." and the rest). Two of the lens's 27 pass and are not pinned:
      "Team meeting Thursday." (all its words were said, the recorded ceiling) and "Thanks, team.".
   3. *Echo rule and the user's own words.* The five-word echo check now reads `CleanupPrompt.WithoutOwnText(prompt)`: the
      marker lines stay, the user's text between them is left out, so an honest reply that repeats words of the instructions
      the dictation has too is used ("Write the company name as Kynni Pty Ltd." with "please write the company name as kinney
      pty ltd in the letter").
   4. *Markers.* `CleanupPrompt.OwnInstructions` takes typed copies of both marker lines out before wrapping, and text that is
      only markers adds no section. The clean-up's load line is logged only when the section is in the prompt.
   5. *Docs.* DEMOS.md (the placeholder no longer counts as a key), item 1 here, SPEC §5 (the widened rule, the echo
      change, the Simple line), README "Secrets" (the button and the placeholder rule).
   6. *PROPOSED list.* The two save-failure log lines and the Simple line are added above.
   7. *Accessibility.* The limit line is a polite live region (`AutomationProperties.SetLiveSetting`), pinned.
   8. *Pins.* The translate and summarise cases now pin their exact reasons: "reply shares too few words with the dictation
      (0/14)" and "reply has 0.12x the dictation's words (minimum 0.4x)"; the whole theory compares reasons exactly.
   9. *Simple view (orchestrator decision).* `OwnInstructionsCard.View` holds the card and one line, "Your own clean-up
      instructions are in use (Advanced view).", shown in the Simple view while `llm.instructions` is set; the page places
      `View`. Pinned shown and hidden.

   Mutations (each observed failing, then restored):
   - Item 1: sound-matching against every dictated word again. "A9 have a great day", "A17 good day" and "A21 have a nice
     day" observed failing.
   - Item 2: the digit, apostrophe and joined-words allowances removed. "11 ordinal digits alone", "12 ordinal 1st alone",
     "13 contraction alone", "19 initialism", "28 tomorrow split", "33 alright" and "37 ordinal 2nd" observed failing.
   - Item 3: the echo check reading the whole prompt. `A_reply_may_repeat_words_of_the_users_own_instructions_that_were_dictated`
     observed failing.
   - Item 4: the text wrapped as typed. `Typed_marker_lines_cannot_step_outside_the_section` observed failing.
   - Item 9: the Simple line never shown. `The_Simple_view_says_when_your_own_instructions_are_in_use` observed failing.

   Gates: Release build with `--no-incremental`, 0 warnings, 0 errors. The validator, cleaner, rules, dictionary, hands-free,
   LLM, amendment, Batch 2, personal-text, See how it works and calibration classes with OwnInstructionsTests,
   FinalSentenceRuleTests, KeyFileTests, CleanupPageOwnInstructionsTests, CleanupOffTests, CloudWarningAndPresetsTests,
   HelpPageTests, OwnerApprovalsTests and PublicCopyNeutralityTests: 644 total, 642 passed, 0 failed, 2 skipped. Full suite:
   2048 total, 2025 passed, 0 failed, 23 skipped.

7. **Re-lens on c796dc8: Needs changes; fixed (22/10/2026).** The lens's trial validator (lens60b/trial) is applied as written,
   with one addition (item 3 below).
   1. *Sound-key collisions (compliance).* 14 of 50 sign-offs passed on the lens's five dictations ("Bye.", "Take care.",
      "Today.", "Sure thing.", "Thanks again.", "Good luck." twice, "Happy to discuss.", "Hope all is well.", "Hope this
      helps.", "Hope you are well.", "All good.", "Chat soon.", "Have a good one."). Now:
      - joined and counted words match only exactly, or when both have five letters or more and are one edit apart;
      - accents are folded;
      - `SoundsLike` is used only for a reply word that belongs to a dictionary term (the rule now receives `DictionaryTerms`).
   2. *"-n't" contractions.* A stem map: "won't" → "will", "can't" → "can", "shan't" → "shall", any other "-n't" without it,
      else the part before the apostrophe.
   3. *Four letters.* Joins also take four single-letter words ("a s i c" → "ASIC"). This is the one result that differs from
      the trial's table.
   4. *Markers.* `CleanupPrompt.OwnText` removes every copy of either marker line, again and again until none is left, in any
      case, with curly apostrophes read as straight (`ValidatorText.MapQuotes`, one character for one).
   5. *Apostrophe stem* pinned on its own ("i will call you i would like that" → "… I'd like that.").
   6. *Docs.* SPEC §5's rule paragraph is rewritten for the new matching; items 4 and 6 here point to this item; the PR body
      count says fifteen (`HelpText.Questions` at 28c3e65).
   - *Pins.* `FinalSentenceRuleTests` now pins the re-lens's whole corpus with the result each case gets:
     - 84 honest tidies: 79 accepted; four rejected by older rules (main rejects them too); and the recorded ceiling "16".
     - 50 sign-offs on five dictations: 13 recorded passes, 11 rejected first by an older rule, and every other one rejected
       by this rule.
     - The first lens's 27 closing sentences.
     - The 14 collisions, the six "-n't" cases, "ASIC", and twelve honest respellings ("ATO", "email", "tomorrow",
       "alright", "cancelled", "Catherine", "café", "résumé", "Zoë", "kinney" → "Kynni", "zero" → "Xero", "dictim" →
       "Dictum").
     - The lens's harness had "café", "résumé" and "Zoë" double-encoded; they are written correctly here and pass.
   - Mutations (each observed failing, then restored):
     - Item 1: SoundsLike in place of the one-edit rule for joined and counted words. All 14 cases of
       `A_sound_key_collision_does_not_let_a_sign_off_through` observed failing.
     - Item 2: the "-n't" stem lines removed. `A_contraction_with_nt_is_said_by_its_stem` observed failing on "won't". The
       other five still pass by other routes ("shouldn" is one edit from "should"; "isnot" joined is one edit from "isn't").
     - Item 3: the four-letter join removed. `Four_spelt_out_letters_are_said_as_their_initialism` observed failing.
     - Item 4: a single exact, unmapped pass. All three cases of `Nested_curly_and_capitalised_markers_are_taken_out`
       observed failing.
     - Item 5: the apostrophe stem removed (the word kept whole). `An_apostrophe_word_is_said_when_the_part_before_it_was`
       observed failing.
   - Gates:
     - Release build with `--no-incremental`: 0 warnings, 0 errors.
     - The lens's 23 classes: 870 total, 869 passed, 0 failed, 1 skipped. "Llm" is run as the four classes of `LlmTests.cs`:
       TranscriptCleanerTests, CleanupValidatorTests, ProviderParsingTests and SecretRedactorTests.
     - Full suite: 2384 total, 2361 passed, 0 failed, 23 skipped.

8. **Owner approval, 22/10/2026 22:52 AEDT.** The owner approved the 1.3 block. Sections A and B of
   `.demo-preview/approve-1.3-block.md` cover every PROPOSED string of this PR, and no string changed. Each marker now reads
   "APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword":
   - the card label and hint; the limit, cloud and Simple-view lines;
   - the validator reason; the two prompt marker lines;
   - the log lines and the loader warnings;
   - the button, its two status lines, the key-file header lines and the key-file log lines.

   Strings that had no marker of their own (the validator reason, the log lines, the warnings) get a comment line above
   them. The change to `src` is comments only, counted in the PR body. `OwnerApprovalsTests` gains an approved block that
   pins each string word for word, independently of the app's constants. The two save-failure log lines are approved but not
   pinned, because only an exception reaches them. SPEC §5's four PROPOSED marks for these strings now say APPROVED.

## Public copy for cloud speech and the 1.3 features (22/10/2026, branch copy/cloud-speech-public, APPROVED 22/10/2026 22:52 AEDT except the tester-notes CHANGED paragraph)

Lane C-Copy of the cloud speech work, on origin/main 28c3e65 (cloud speech Core PR #48 and UI PR #54 merged; the own-instructions
lane PR #60 was not; it has since merged at 672f85b into main 4a4072a). Every sentence below was proposed on 22/10/2026 and **APPROVED by the owner as the 1.3 block (22/10/2026 22:52 AEDT; item 11)**, except the 1.3.0 tester-notes CHANGED paragraph, which stays PROPOSED until the Store submission; none is a counsel-owned or "approved ... as written" string.
Where an approved sentence is touched, the old one is quoted beside the new one. `privacy.html`'s "Last updated" date is not changed
(it is set at tag time). Nothing in `OtherPages.cs`, `ModelPages.cs`, `CloudPolicy.cs`, `SpeechPolicy.cs`, `Settings/*.cs` or
`src/Dictum.Core/Storage/*` was edited.

1. **Why.** Cloud speech sends each recording to a service the user chooses, but four public surfaces still said the voice never
   leaves the PC: the landing page, the web guide, the privacy policy and the Store text. The release gate: no unqualified "voice never
   leaves" claim on any public surface.
2. **Old -> new, `site/index.html`** (the design brief's section 5 table, plus the rows marked *beyond the table*):
   - hero: "Your voice never leaves your PC." -> "Your voice stays on your PC unless you turn on cloud speech."
   - og description: "nothing leaves your PC unless you turn on cloud clean-up." -> "nothing leaves your PC unless you turn on cloud clean-up or cloud speech."
   - *beyond the table* meta description: "and nothing you say leaves your computer unless you turn on cloud clean-up." -> "and nothing you say leaves your computer unless you turn on cloud clean-up or cloud speech."
   - at-a-glance list: "Your voice stays on your PC." -> "Your voice stays on your PC by default."
   - card heading: "Your voice stays on your PC" -> "Your voice stays on your PC by default"
   - both SVG badges: "Your voice stays on your PC" -> "Your voice stays on your PC" / "by default" on two lines. The text does not fit the one-line
     badge at 15 px bold (about 38 characters against 248 px of room in the wide graphic; this is an estimate: no browser measured it), so the
     wide badge is 72 px high like the Free badge beside it, and the narrow graphic is 20 px taller (viewBox 360x760 -> 360x780) with the two
     badges below it moved down by 20.
   - *beyond the table* card paragraph: "Cloud clean-up only if you turn it on." -> "Cloud clean-up and cloud speech only if you turn them on."
   - FAQ "Does it learn my voice?" (the table's ":332"): "your voice never leaves your PC" -> "your voice stays on your PC unless you turn on cloud speech"
   - hosts list under "Nothing leaves your PC, by default" (heading unchanged): new card, h3 "api.openai.com, {resource}.openai.azure.com, api.groq.com" with
     the "Off by default" tag, "Cloud speech (off by default): the recording of each dictation goes to the service you chose, OpenAI, Azure OpenAI, Groq or a server you name,
     with your own key, behind its own consent step. Your dictionary words go with it as a hint. Dictum does not validate or certify how that service handles your voice."
   - dictum.kynni.au card (the brief): "Only when you open the privacy policy or the full guide from Settings, ..." -> "Only when you open the privacy policy, Dictum in plain words or the full guide from Settings, ..."
     (the privacy page's approved sentence; `UpdateCheckTests` now pins the card to it).
   - *beyond the table* comparison table, Dictum, "Where your speech is processed": "On your PC" -> "On your PC by default"; note: "Dictum's optional cloud clean-up is off by default and comes with a warning." ->
     "Dictum's optional cloud clean-up and cloud speech are off by default and come with a warning."
3. **Old -> new, `site/help.html`** (the table's ":358", ":359", ":611", found by text):
   - section 7: "Dictum turns your voice into text on this PC, then throws the recording away. Your voice never leaves this PC." -> "... then throws the recording away. Your voice stays on this PC unless you turn on cloud speech, an advanced option that is off by default."
   - ":359" "your voice still stays here": already qualified by the owner (17:22 AEDT), unchanged.
   - FAQ "Does it learn my voice?": "your voice never leaves this PC" -> "your voice stays on this PC unless you turn on cloud speech"
   - *beyond the table* FAQ "What is cloud clean-up? Is it safe?": "Your voice still stays on this PC." -> "Your voice still stays on this PC unless you also turn on cloud speech." (the owner's qualifier, 17:22 AEDT, applied to the one answer it missed)
   - *beyond the table* FAQ "Does Dictum need the internet?": "That means three things:" + a fourth item "cloud speech, if you turn it on." (the third now ends ";"), and "stops all three" -> "stops all four".
   - Cloud section (twin of the Help change, item 6): two paragraphs before the sixteen steps.
   - New FAQ "Can I give the clean-up my own instructions?" (twin of the Help question, item 6).
4. **Old -> new, `site/privacy.html`** (the brief's list; "We never receive your audio or your text." is unchanged, Kynni never does):
   - short version: "Your speech is turned into text on your PC, and the text is tidied on your PC." -> "Your speech is turned into text on your PC, unless you turn on cloud speech, and the text is tidied on your PC."
   - short version: "... to send text to a cloud clean-up service or a server you choose." -> "... to send text to a cloud clean-up service, or your voice to a cloud speech service, or a server you choose."
   - Microphone: "It is never written to disk, except the calibration recordings you make on purpose (below)." -> the same, then "With cloud speech on, the recording is sent to the service you chose and then discarded here."
   - *beyond the table* `.env` bullet: "an API key, only if you add one for cloud clean-up." -> "an API key, only if you add one for cloud clean-up or cloud speech."
   - new h3 "Cloud speech (off by default)" before "This website": "Only if you switch on cloud speech in Settings > Models, agree to the service in its own consent step and add your own API key. After each dictation (each part, in hands-free) Dictum then
     sends that dictation's recording, as a WAV file, to the service you chose, together with the words in your personal dictionary as a hint and, in hands-free, the closing words of the part before.
     The service is OpenAI (api.openai.com), Azure OpenAI ({resource}.openai.azure.com, your own resource), Groq (api.groq.com) or a custom https:// address you enter. That service handles your voice under its own terms.
     This is an advanced option: Dictum cannot validate or certify how that service stores, protects or uses your voice. Check its terms before dictating anything sensitive."
   - Offline only: "downloads, cloud clean-up and servers that aren't on your PC are all refused. A clean-up server running on your own PC still works." -> "downloads, cloud clean-up, cloud speech and servers that aren't on your PC are all refused. A clean-up or speech server running on your own PC still works." (offline_only allows an https server on this PC for speech, as for clean-up: `SpeechPolicy.OfflineRefusal`)
   - Connection list: "Hugging Face, Anthropic, OpenAI, Google Gemini and any server you name ... Model downloads and requests to Anthropic, OpenAI and Google Gemini use HTTPS." -> "Hugging Face, Anthropic, OpenAI, Azure OpenAI, Groq, Google Gemini and any server you name ... requests to Anthropic, OpenAI, Azure OpenAI, Groq and Google Gemini use HTTPS." (cloud speech accepts only https://, also on this PC)
5. **README, Store, Mac handover.**
   - README: new row in the Privacy network table ("After each dictation (each part in hands-free), only if you switch cloud speech on in Settings > Models, agree to the host in its own consent step, and a key is set where the service needs one |
     the host in `speech.base_url` (...): that dictation's audio as WAV, your dictionary terms as a hint, and in hands-free the carried words"); the consent description "(every dictation's text, never the audio, with the clean-up instructions and your dictionary words)" ->
     "(every dictation's text, "not the audio: cloud speech, if you turn it on, sends the audio to the service you choose for it", together with Dictum's clean-up instructions, which include the words in your personal dictionary)" (the consent's own wording, `CloudPolicy.ConsentText`);
     `offline_only` paragraph gains "It refuses cloud speech to any service that is not on this machine too, and the speech model on this PC hears the dictation."; the Audio bullet gains "With cloud speech on, it is also sent to the service you chose (see the table above), then discarded here.";
     and the `speech.provider` row's "Settings does not have its consent step yet, so in this build nothing in Dictum records that consent." (untrue since PR #54) -> "The consent step is in Settings > Models (Advanced view), under Cloud speech."
     The `speech.*` rows, the key variables and the `offline_only` row already named cloud speech (Core lane) and are unchanged. The validator rule list is unchanged (it was written before PR #60 merged; #60 added no bullet for its final-sentence rule there, and this lane does not own that list).
   - Store (`docs/STORE.md`): the description sentence and the AI paragraph sentence (the brief's `:104`, `:107`), the age-rating answer, the properties-page answer, certification row 5 (privacy policy lists the cloud speech hosts) and row 13 (the speech consent dialog satisfies 10.5.2 for audio),
     an APPROVED (22:52) "What's new in this version (1.3.0)" block above 1.2.1's, and a certification-notes block that stays PROPOSED until the Store submission (Notes written line, CHANGED paragraph, cloud paragraph). The certification-record row "1.3.0 prepared" is not added: it is the release lane's.
     The description's first sentence ("Speech recognition and clean-up run on your PC.") and the first product feature are unchanged: both describe the default, and the sentence after names the option.
   - `docs/MAC-HANDOVER.md`: new section 0 records the owner's 22/10/2026 decisions (GitHub beta first, ad-hoc signed, no Apple fee; Apple Silicon (arm64) only; Intel only if a tester needs it; ships after 1.3 is live), and the lines that said Intel too are brought into line (section 1 target, publish, CI, test plan item 10). The no-telemetry line names cloud speech and the proposed `NSMicrophoneUsageDescription` gains "unless you turn on cloud speech".
6. **Help (`HelpText.cs`), APPROVED 22/10/2026 22:52 AEDT.** The own-instructions lane's sentences (PR #60), placed:
   - cloud answer, after "To get a key, ...": "To add a key for cloud clean-up, choose the service in Settings > Clean-up and press Create the key file…. Dictum makes the file and opens it: put your key in place of your-key-here, save, and Dictum reads it. If you already have a key file, Dictum opens it as it is." (verbatim, including its "…." ), then
     "If the button does not work for you, make the key file yourself with steps 2 to 15 below; steps 1 and 16 are the same either way." (this lane's). The sixteen steps are unchanged and not renumbered.
   - new question "Can I give the clean-up my own instructions?" after "How do I turn on cloud clean-up?": paragraph 1 "In the Advanced view, Settings > Clean-up has a box for your own instructions, such as "use short sentences" or "write product names in capitals". Dictum adds them to its own clean-up instructions for the AI clean-up on this PC and for cloud clean-up; the rule-based tidy and Off do not use them. With cloud clean-up on, your instructions go to that service with each dictation, as part of the text it tidies." (as first proposed; "as part of Dictum's clean-up instructions." in item 10)
     (the lane's three sentences, verbatim); paragraph 2 as first proposed: "Dictum still checks every reply against what you said, so instructions to translate, summarise, answer or add a line are not followed. A reply that ends with a sentence you did not say is rejected, and the rule-based tidy is pasted instead." (replaced in item 10).
   - The site's help page carries both word for word (pinned). Questions: fifteen -> sixteen.
   - **These Help strings describe features that are not on main.** The box and the button exist only in PR #60 (wired into `CleanupPage` at 5e9c227; merged at 672f85b into main 4a4072a, so the controls and the Help now agree on main). Merge order: #60 (done), then #61, then this PR. (The wording of the second paragraph and the cloud sentence was replaced in the lens round, item 10.)
7. **Sweep** (the brief's grep over site, docs, README.md, src and tests, plus "never sends it anywhere" and "turned into text on your PC"): every remaining hit is a sentence already qualified, a code comment, or history. The PR body lists them one by one. The release gate holds for the four site pages by `SiteStoreFirstTests.No_page_says_the_voice_never_leaves_the_PC_without_an_unless`.
8. **Not done, for the owner.** (a) *Closed in the lens round, item 10:* `README.md` line 4, the Help's "First, your voice is turned into text on this PC" (and its site twin), the Store description's first sentence and first feature were unqualified in themselves; they are qualified now (lens, and the owner's six qualifiers of 22:05 AEDT). (b) `site/privacy.html` does not list `consent.json` among the files Dictum keeps in `%APPDATA%\Dictum`, and did not before this lane (cloud clean-up has written it since 16/10/2026). (c) "Settings > Storage offers to move model files" is in the brief's 1.3 list but was not in `src` at 28c3e65 (only the change-folder button of PR #51, which "does not move any files"). It is PR #61 (head 1730818 when this was written, open), which offers the move from Change…, Browse… and Use default (owner, 21:45 AEDT); the Store What's new sentence for it stays conditional on that merge. (d) Settings > Models' own sentences (`ModelPages.cs`, `SimpleView.cs`) are the UI lane's and the owner's (DECISIONS, Cloud speech UI, item 20/23).
9. **Pins.** `SiteStoreFirstTests` (the sweep across the four pages; every new or replaced sentence on its page; the old sentences absent), `HelpPageTests` (the key file button leads the cloud answer and the sixteen steps stay; the own-instructions answer; the site twin), `PlainWordsTests` (README, Store and Mac handover sentences), `OwnerApprovalsTests` (a PROPOSED block for the new Help strings), `UpdateCheckTests` (the landing page's dictum.kynni.au card and the privacy short-version bullet follow the new sentences). The question count pin is 16.
   **Mutation, observed failing (22/10/2026):** "Your voice never leaves this PC." put back in `site/help.html`, section 7, in place of the new sentence: `No_page_says_the_voice_never_leaves_the_PC_without_an_unless(help.html)`, `The_old_unqualified_sentences_are_gone(help.html)` and `The_cloud_speech_sentences_are_on_the_page(help.html)` failed; restored.
10. **Lens round on e4e17f1 (Needs changes, no blocker) and the owner's decisions of 22/10/2026 22:05 AEDT.** Old to new, verbatim:
    - *Own-instructions answer, paragraph 2 (PROPOSED, the lens's wording; the code contradicted the first one):* "Dictum still checks every reply against what you said, so instructions to translate, summarise, answer or add a line are not followed. A reply that ends with a sentence you did not say is rejected, and the rule-based tidy is pasted instead." -> "Dictum still checks every reply against what you said. A reply that drops or changes your words, translates or summarises them, answers you instead of tidying, or adds a sign-off you did not say is usually rejected, and the rule-based tidy is pasted instead. The check cannot catch everything, so read what Dictum typed."
    - *Same answer, paragraph 1, last words (the instructions go in the system prompt, not the dictation text):* "...as part of the text it tidies." -> "...as part of Dictum's clean-up instructions."
    - *Store description, AI paragraph (the inserted sentence made "If its answer fails Dictum's checks" read as being about cloud speech):* removed from after "...answer or summarise anything." and placed after "...and you can switch AI clean-up off completely.": "With cloud speech on (off by default), the recording goes to the speech service you chose instead of whisper.cpp; if that request fails, whisper.cpp on your PC hears it." (it replaces "With cloud speech on, off by default, the recording goes to the service you chose.")
    - *privacy.html, cloud speech:* "...agree to the service in its own consent step and add your own API key." -> "...agree to the service in its own consent step and, for OpenAI, Azure OpenAI or Groq, add your own API key (a server you name may need none)." *index.html card:* "with your own key, behind its own consent step" -> "with your own key where the service needs one, behind its own consent step". (A custom server's key is optional: `SPEECH_API_KEY`.)
    - *Stale dependency notes:* PR #60 was wired into Settings > Clean-up at 5e9c227 and merged at 672f85b into main 4a4072a; PR #61 (head 1730818 when this was written, open) offers the move from Change…, Browse… and Use default (owner, 21:45 AEDT) and is pending; the Store What's new and certification notes, item 6, item 8(c), `HelpText.cs`'s comments and SPEC §31 now name the shas. Merge order: #60, #61, #62; when main is merged into this branch, SPEC §26 must read "sixteen questions" and "unless cloud clean-up or cloud speech is turned on".
    - *README:* line 4 "your speech is transcribed **on this PC** with whisper.cpp" -> "...**on this PC by default** with whisper.cpp"; the tray panel line "(\"Nothing leaves this PC\", or that clean-up uses a server on this PC or sends text off it)" gains the cloud speech footer "Cloud speech · audio leaves this PC ({host})"; the offline-only paragraph's "Nothing leaves this PC" now also says offline_only still allows a speech server on this PC, which Dictum cannot see into.
    - *Sweep test:* the pattern "turned into text on (this|your( own)?) PC" is added, with the same "unless" or "by default" requirement. **Mutation, observed failing (22/10/2026):** `site/privacy.html`'s short version put back to "Your speech is turned into text on your PC, and the text is tidied on your PC." made the sweep fail for privacy.html (and the pin and old-sentence tests); restored.
    - *Mac handover:* the inference about cloud speech reaching the Mac build moved below the owner's list and labelled the lane's note.
    - **Owner, 22:05 AEDT, six qualifiers, each "APPROVED 22/10/2026 22:05 AEDT (qualifier added)":**
      1. Help "Where does my voice go?": "Nowhere. Dictum turns your voice into text on this PC..." -> "Nowhere, by default. Dictum turns your voice into text on this PC..." (app and `site/help.html`).
      2. Help and guide "How Dictum works": "First, your voice is turned into text on this PC by a speech model." -> "First, your voice is turned into text on this PC (unless you turn on cloud speech) by a speech model."
      3. `site/plain-words.html`: "Your voice is turned into text on your own PC. The text is tidied on your own PC." -> "Your voice is turned into text on your own PC unless you turn on cloud speech. The text is tidied on your own PC." (`PlainWordsText` holds only the short version and has no such sentence.)
      4. `site/index.html` hero list: "Runs on your PC" -> "Runs on your PC by default".
      5. `site/index.html`: "Dictum does the work on your own computer, for free." -> "Dictum does the work on your own computer by default, for free."
      6. `docs/STORE.md`: the feature "Speech recognition runs on your PC with whisper.cpp and works offline" -> "Speech recognition runs on your PC by default with whisper.cpp and works offline"; PRIVATE BY DESIGN's first sentence "Speech recognition and clean-up run on your PC." -> "Speech recognition and clean-up run on your PC by default." The heading AI THAT RUNS ON YOUR PC stays.
    - **Owner, 22:05 AEDT, Mac:** the owner joined the Apple Developer Program on 22/10/2026 and chose a Developer ID signed and notarised GitHub pre-release as the beta (no Gatekeeper warning), Apple Silicon only, the Mac App Store a later train. `docs/MAC-HANDOVER.md` section 0 now says so (it replaces "ad-hoc signed, no Apple Developer Program"), adds that the owner creates the certificate and notarisation credentials himself and the Mac session never handles Apple credentials, and adds the lane note that the "retire when signed" simplifications (`Startup`: LaunchAgent against `SMAppService`; `CreateTray`'s `Notify`: transient balloon against `UNUserNotificationCenter`) are to be re-evaluated by the Mac lane. Sections 1 and 6 carry "superseded by section 0" marks on the ad-hoc and Gatekeeper lines; the Mac session rewrites them. The Intel lines are as before.
    - **Pins:** `HelpPageTests` and `OwnerApprovalsTests` (the new second paragraph, "usually rejected", the ending, no "are not followed"); `HelpPageTests.The_two_22_05_qualifiers_are_in_the_app_and_on_the_site`; `PlainWordsTests` (Store feature and opening sentence, the plain-words bullet, the Mac section 0 wording); `SiteStoreFirstTests` (the index and plain-words sentences, the privacy and index card sentences).
11. **Owner approval of the 1.3 block (22/10/2026 22:52 AEDT), markers only.** The owner approved every sentence this entry proposes for the landing page, the help page, the privacy policy, the README, the Store listing and the 1.3.0 What's new, the two Help paragraphs and the new Help question, and the Mac handover section (section D of the approval list). Each marker now reads "APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword" (source comments in `HelpText.cs`, HTML comments in `site/index.html`, `site/privacy.html` and `site/help.html`, the Store notes, the SPEC and these headings). No text changed. **Still PROPOSED:** the 1.3.0 tester-notes CHANGED paragraph (and the notes block around it, `docs/STORE.md`), which goes with the Store submission. The pin for the new Help strings moved from a PROPOSED block to an approved block in `OwnerApprovalsTests` (`The_approved_own_instructions_and_key_file_help_strings_are_pinned`). This PR merges last, after #60 and #61; main is not merged into it yet, and SPEC §26 then becomes "sixteen questions ... unless cloud clean-up or cloud speech is turned on".

12. **Re-lens on a1546e3 (23/10/2026): one marker withdrawn, small fixes.**
    - **`site/privacy.html`, the "Cloud speech (off by default)" paragraph: marker changed to "PROPOSED 23/10/2026: shipped text differs from the owner-approved summary by the sentences quoted here; owner confirmation pending".** The owner approved the 1.3 block (22:52 AEDT) from a summary (`.demo-preview/approve-1.3-block.md`, section D, privacy policy) that gave the paragraph's last sentences as: "...or a custom https:// address you type. Dictum cannot validate or certify how that service stores, protects or uses your voice." The page ships: "...or a custom `https://` address you enter. That service handles your voice under its own terms. This is an advanced option: Dictum cannot validate or certify how that service stores, protects or uses your voice. Check its terms before dictating anything sensitive." So the exact differences are (a) "you type" in the summary, "you enter" on the page; (b) the sentence "That service handles your voice under its own terms." is on the page only; (c) "This is an advanced option: " opens the next sentence on the page only; (d) the sentence "Check its terms before dictating anything sensitive." is on the page only. The page text is kept (it matches the cloud clean-up paragraphs and is the more protective); the owner is to be asked. Every other 22:52 marker stands. The page's text is unchanged by this item. **Resolved 23/10/2026 06:15 AEDT: the owner confirmed the paragraph as shipped, so its source comment now reads "APPROVED 23/10/2026 06:15 AEDT (confirmed as shipped; proposed 22/10/2026): do not reword", with no text change (the later lines in this record that say the confirmation is pending are history).**
    - `docs/SPEC.md`, `docs/STORE.md` and item 10 above: PR #61 is named by its head when this was written (1730818, open), not by the earlier sha; the status stays pending.
    - `README.md` line 158: "your voice becomes text on this PC" is now "...on this PC unless you turn on cloud speech".
    - `docs/MAC-HANDOVER.md`: "so a first open shows no Gatekeeper warning" is now "so a first open is not blocked by Gatekeeper (Recalled: macOS still asks once to confirm an app downloaded from the internet; the Mac lane records the exact prompt)", and the claim in section 1 that a Developer ID signed and notarised build opens without the Open Anyway step is marked Recalled, not verified.
    - `docs/SPEC.md` §26: the question count is dated "23/10/2026 at a1546e3 (main 4a4072a plus PR #62's question)".

## Release 1.3.0 prepared (23/10/2026)

Branch `release/1.3.0` from origin/main cd48645. No tag is created and nothing is built, published, deployed or submitted: the tag, the publish and the site deploy wait for the orchestrator, after this PR merges.

1. **Edits.** `Directory.Build.props`: `<Version>` 1.2.1 to 1.3.0 (package 1.3.0.0). `docs/STORE.md`: the version references in the header line, section 1 and section 6, a certification-record row, and "1.3.0" added to the packaged-self-test sentence. `site/version.json`: version 1.3.0, published 2026-10-23, notes the APPROVED 1.3.0 "What's new" paragraph from STORE.md verbatim. `site/privacy.html`: "Last updated" 23/10/2026, because the policy promised an update before a version changes what it sends and cloud speech ships in 1.3.0. `README.md` is unchanged (as in the 1.2.1 release PR).
2. **In 1.3.0 (merged):** PR #54, cloud speech: the consent step and the Settings > Models choice and state lines. PR #57, Help questions and site pages explaining what clean-up is. PR #58, the first-run demo step 16 caption (approved wording). PR #59, "Dictum in plain words": the site page, the Welcome line, the Store sentence and the About card. PR #60, Settings > Clean-up: the box for your own instructions and the Create the key file button. PR #62, the public copy for cloud speech and the 1.3 features.
3. **Pending:** PR #61 (Settings > Storage offers to move model files when the folder changes; open at head 1730818 when this was written). The Store "What's new" paragraph says "Settings > Storage offers to move your model files when you change their folder." If PR #61 does not land in 1.3.0, that sentence must be removed from the paragraph before submission (and from `site/version.json` `notes`, which carries the same paragraph), and the certification-record row's mention of PR #61 corrected.
4. **Still PROPOSED:** the 1.3.0 tester notes ("Notes written" line, CHANGED paragraph and cloud paragraph, in "Notes for certification: PROPOSED for 1.3.0"); the owner approves them with the submission. Also awaiting owner confirmation: the `site/privacy.html` "Cloud speech (off by default)" paragraph, whose shipped text carries two sentences beyond the owner-approved summary (marker withdrawn to PROPOSED in PR #62).
5. **Approved and unchanged:** the 1.3.0 "What's new" paragraph (APPROVED 22/10/2026 22:52 AEDT) is not reworded; `site/version.json` carries it as is.
6. **Not done here:** tag, installer, MSIX, GitHub release, site deploy and Store submission, and the packaged self-test (not run for 1.3.0). Store screenshot 1 is still to be retaken with the 1.2.1 hold pill.

## Move the model files when the folder changes (22/10/2026, branch feat/move-on-change, for 1.3)

Owner approval 22/10/2026: when the model folder changes, Dictum offers to move the existing model files. Design brief: `.demo-preview/design-storage.md` sections A-C. **Every new string below was proposed 22/10/2026 and is now APPROVED (the 1.3 block, 22/10/2026 22:52 AEDT; the folder note at 22:42 AEDT, item 12), except as noted: the Quit sentence of item 13.8 is PROPOSED for 1.3.1 and does not ship (item 14); no approved string was changed without the owner.** The folder note was then "Dictum looks here first and downloads go here. Changing it does not move any files.", true for "Leave them where they are"; item 12 rewords it.

1. **Where it hooks in.** The shared handler `ModelFolderActions` (moved out of `ModelPages.cs` into its own file), which both Settings > Models **Browse…** and Settings > Storage **Change…** call. **Use default** offers the same question and the same move, back to the per-user folder (item 10; at first it offered none, which was put to the owner). The Welcome step's "Model files" (which calls `ModelCoordinator.SetModelsFolder` directly) is unchanged. `ModelCoordinator` is not edited.
2. **The offer.** Shown only when the folder in use holds model files Dictum knows: catalogue speech and clean-up files and their `.part` downloads, in that folder itself (`ModelMover.Known`). Not for the same folder, not when the folder in use is the bundled one. Closing the question changes nothing.
3. **The move** (`src/Dictum.Core/Storage/ModelMover.cs`). Per file, in name order: copy to `<new>\<name>.move`; check the copy's size against the original; for a finished catalogue file hash the copy and compare it with the catalogue's pinned SHA-256 (`ModelIntegrity.Expected`); rename to the final name; `integrity.Remember(new)`; only then delete the original and `integrity.Forget(old)`. A `.part` is copied and renamed with no hash (it has nothing to be checked against; the downloader checks it when it resumes). A file of the same name already in the new folder is not overwritten. The first file that fails stops the move: its copy is deleted, its original kept. A failed delete of the original after a verified copy leaves both copies and is reported.
   - `// simplification:` **no rollback of already-moved files.** Ceiling: after a failure the models are split across two folders, and those left in a chosen folder Dictum no longer looks in read "Not downloaded" until that folder is chosen again. Retire when: a report of a split folder that confused someone, or the archive folder (design brief D) adds a second folder the lookup searches anyway.
   - Same-drive moves still copy (no rename shortcut), and there is no free-space pre-check: a full drive fails the copy, which deletes the partial copy and keeps the original. Both are ceilings, not built (brief B.5 and B.7).
   - The startup clean-up of stray `.move` files and a registry pattern for them (brief B, "Interrupted move") are not built: a `.move` is left only if Dictum ends mid-copy; it is never loaded (not a catalogue name) and the next attempt overwrites it.
4. **Order of the flow** (`ModelFolderActions.ChangeAsync`): the existing refusals (bundled folder, device path); the offer; on **Move the files**, refuse while a download is running (`ModelCoordinator.IsDownloading`: the downloader holds its `.part` open with `FileShare.None`) and when one folder is inside the other; release both engines; move; write `models.folder` with `SetModelsFolder(folder, reloadAfter: false)`; reload. The folder is written **after** the move because every settings.json write fires the settings watcher's reload (500 ms debounce), which would load a model from the old folder mid-move. If the write fails, the result line adds the approved `ModelCoordinator.WriteFailed`. The page's status line (live, polite) shows each file and then the result; a progress report still queued at the end is dropped so it never overwrites the result. A second folder change while a move runs is ignored (`AppShell.MovingModels`).
5. **The release seam.** New `ModelManager.ReleaseAsync()` and `LocalLlmManager.ReleaseAsync()`: take the last load task (`_tail`), call the existing `Unload()` (bumps the generation and disposes the loaded engine), then await that task, so a load still under way finishes and, being stale, disposes its engine. `LocalLlmEngine.Dispose` disposes the context and `LLamaWeights`. New `AppShell.ReleaseModelsAsync()` (new partial file `AppShell.ModelMove.cs`, outside the lane's named files, so no existing shell file was edited) awaits both. Verified 22/10/2026 by running a scratch program against LLamaSharp 0.27.0: `new ModelParams(path).UseMemorymap` defaults to `True`, and `LocalLlmEngine.Load` does not set it, so the clean-up model is memory-mapped. The lens on f5ec71c measured what blocks the move (its result, relayed by the orchestrator; not re-run by this lane): the block comes from the engine's open file handle, not from the mapping, and copying is always allowed, so an engine left unreleased can only leave two copies (the original's delete refused), never zero. The pins use a fake engine that holds its file open with `FileShare.Read` until disposed, which is that case.
6. **PROPOSED strings, verbatim** (`ModelFolderActions`, `ModelMover`):
   - Title "Move your model files?"; body "Your model files are in {old}. Move them to {new} so Dictum finds them there? {n} files, {size}. Dictum copies each file, checks it, then removes the original." ("1 file" for one); buttons "Move the files" (default) / "Leave them where they are".
   - "A download is running; wait for it to finish, then change the folder again to move the files."
   - "One of these folders is inside the other, so Dictum does not move the files. Choose a folder outside the current one." (new; the brief named the refusal but gave no wording)
   - Progress "Moving {name} ({i} of {n})…"; results "Moved {n} files to {new}." and "Moved {k} of {n}; {name} could not be moved: {reason}. The rest stay in {old}."
   - Reasons after "could not be moved: ": "a file of that name is already in the new folder"; "the copy is not the same size as the original, so the copy was deleted"; "the copy did not match the catalogue, so the copy was deleted"; otherwise the system's own message.
7. **Pins** (`tests/Dictum.Tests/ModelMoveTests.cs`, 16 tests with item 10, temp folders only): `Known` lists catalogue files and `.part` files in the folder itself, not `.move`, other files or subfolders; copy-check-rename-delete order (a hasher that records, for each file, whether the original still exists and whether the final name exists yet: both "original there, final not there"); a fake hash failing on the second of three files leaves its original byte for byte, deletes its copy, keeps the first moved and leaves the third untouched, and the verified record holds only the moved copy; `.part` moved with a hasher that throws if called; no overwrite of a same-name file; `Nested`; both `ReleaseAsync` seams (the held file cannot be moved while loaded and can after release; the clean-up one also waits for a load under way); no offer when the old folder holds no model files; the offer's exact text and buttons, Leave writes the folder and moves nothing, closing changes nothing; end to end through the shell and Storage's Change… (engines released, files moved, folder written, the model loaded from the new folder, which is first in the lookup); refusal while a download runs (`GateHandler`; folder and files unchanged, download still running); nested refusal.
8. **Mutations, each observed failing on 22/10/2026, then restored:** delete-before-verify (the original deleted before the hash): the order pin observed failing. The download refusal removed: `Moving_is_refused_while_a_download_runs` observed failing. The hash check removed: the order pin observed failing (the third file was then processed). Also: the `ReleaseModelsAsync` call removed: the end-to-end pin observed failing ("Moved 1 of 2; ggml-tiny.en.bin could not be moved"); `LocalLlmManager.ReleaseAsync` without its wait: the clean-up seam pin observed failing.
9. **Gates** (22/10/2026): Release build with `--no-incremental`, 0 warnings, 0 errors. ModelFolderTests, ModelFolderSafetyTests, OfflineAndDownloadTests, ModelIntegrityTests, FixBatchDownloadTests, ModelMoveTests and PublicCopyNeutralityTests: 137 total, 0 failed, 2 skipped. Full suite once: 1941 total, 0 failed, 23 skipped. After merging origin/main 28c3e65 (merge 0618c98, DECISIONS tails kept, main's first): the same gates 137 total, 0 failed, 2 skipped; full suite 1956 total, 0 failed, 23 skipped.
10. **Use default offers the move too (owner decision, 22/10/2026 21:45 AEDT):** "all three ways of changing the folder behave alike". `ModelFolderActions.UseDefaultFolder` now goes through `ChangeFolderAsync(shell, null, …)`: the same question, the same refusals and the same move, into `AppPaths.DefaultModelsDir`; models.folder is then cleared (`SetModelsFolder(null)`). No new string. Pins: `ModelFolderTests.Browse_writes_models_folder_and_reloads_and_Use_default_clears_it` now expects the question when the chosen folder holds a model (closing it changes nothing; Leave clears the folder and the file stays) and none when the chosen folder holds no model files; new `ModelMoveTests.Use_default_offers_the_move_and_moves_the_files_back_to_the_default_folder` (Storage's Use default, Move the files: the file arrives in the default folder, models.folder is cleared, the model loads from there). Mutation: Use default writing the folder without the offer: both pins observed failing (22/10/2026), then restored. Gates on this head: Release build `--no-incremental` 0 warnings, 0 errors; the gate classes above 138 total, 0 failed, 2 skipped; full suite 1957 total, 0 failed, 23 skipped.
11. **Lens on f5ec71c (Needs changes; no blocker). This item supersedes items 3 and 4 where they differ.** The approved note (`ModelsPage.FolderNote`, lens item 4) is with the owner and is not edited here.
    1. *Order pinned.* New pins: a rename that fails (the hasher puts a file under the final name after the existence check) keeps the original byte for byte, leaves no `.move` and reports the system's reason; a copy of the wrong size (a copy seam, `copier`, writes three bytes) is deleted and its original kept.
    2. *Name clash and the model in use.* A file of the same name in the new folder counts as moved when it is the same file (same size and, for a finished catalogue file, the pinned SHA-256): the original is deleted and the verified record moves. Otherwise that file is skipped. Any file that fails is skipped and **the move goes on** (it no longer stops at the first failure). The models in use (`AppShell.ModelFilesInUse`, read before the release) move last (brief B.7). The folder is **not** written when a model in use stayed behind, or when the move was stopped. The line then ends with `FolderKept`. This pins the lens's reproduction: the chosen folder holds base.en and tiny.en (in use), the default folder already holds the same base.en, Use default; now "Moved 2 files", the folder is cleared and the model loads from the default folder.
    3. *One move flag, in `ModelCoordinator`* (`MovingFiles`, replacing `AppShell.MovingModels`). While it is set:
       - `StartDownload`, `ApplyWithDownloads` and `AppShell.DeleteModelAsync` refuse with `MovingRefusal`.
       - `Ensure` and `EnsureCleanup` load nothing until the reload after the move.
       - Pinned: S2 (a download asked for mid-move sends no request and writes no `.part`; Delete mid-move is refused and the file stays) and S3 (a reload mid-move loads no model; afterwards the model loads from the new folder).
       - Not guarded: "Delete all app data" mid-move.
    4. *Minor items.*
       - An original that cannot be removed after its checked copy is in place counts as moved, with the reason `OriginalKept`.
       - System messages lose their closing full stop.
       - Progress and result lines use catalogue names (`ModelMover.DisplayName`).
       - **"Leave them where they are" is the default button** (orchestrator decision: Enter must not start a multi-GB move). Pinned through a new `ScriptedDialogs.AskedDefault`.
    5. *Quit mid-move (built).*
       - `AppShell.Quit` defers while a move runs (`MoveCancel`). It cancels the move's token and quits when the move has ended, with no reload and no folder written.
       - Cancellation takes effect between files and inside a copy or hash. The file being copied stops: its copy is deleted and its original kept. A file whose copy was checked finishes its rename and delete.
       - Only Quit cancels; there is no Cancel button.
       - A shutdown that calls `Dispose` directly (not through Quit) is not deferred.
       - Pinned at both levels.
    6. *`PathRegistry.IsInside` and drive roots.* "D:\Models" was not inside "D:\": a separator was added to a root that already ends with one. Fixed (a separator is added only when missing, and a path is never inside itself) and pinned, including `Nested` across a root.
    7. *Mutations, each observed failing on 22/10/2026, then restored (full `--no-incremental` rebuild each):*
       - Delete original before rename: `A_rename_that_fails_keeps_the_original_and_leaves_no_copy` observed failing (and `An_original_that_cannot_be_removed…`).
       - `.move` clean-up skipped on the size branch: `A_copy_of_the_wrong_size_is_deleted_and_the_original_kept` observed failing.
       - `.move` clean-up skipped in the IO catch: `A_rename_that_fails…` observed failing.
       - Folder written although the model in use stayed behind: `The_folder_is_not_changed_when_the_model_in_use_stays_behind` observed failing (and the quit pin).
       - In-use-last ordering removed: `The_models_in_use_move_last` observed failing.
       - Download guard removed: `Downloads_and_deletions_are_refused_while_files_move` observed failing.
       - `Ensure` guard removed: `A_reload_while_files_move_loads_no_model` observed failing.
       - Delete guard removed: `Downloads_and_deletions_are_refused_while_files_move` observed failing.
       - `IsInside` fix reverted: `A_folder_is_inside_its_drive_root` and both root cases of `Nested_folders_are_one_inside_the_other` observed failing.
       - Re-run on the new code: hash check removed, delete before verify, release removed and Use default skipping the offer, each observed failing.
    8. *New PROPOSED strings:*
       - "Model files are being moved; try again when it finishes."
       - "it was copied, but the original could not be removed"
       - "a different file of that name is already in the new folder" (replaces "a file of that name is already in the new folder")
       - " 1 other file could not be moved either; the log says why." / " {m} other files could not be moved either; the log says why."
       - " The model folder is still {old}, because the model in use did not move; the files that did are in {new}; choose that folder again to use them." (or, with nothing moved, only its first clause)
       - "Moved {k} of {n}." (stopped with no problem: only on quit)
       - "unfinished {name} download" (a `.part`'s name in the lines)
    9. *Gates:* Release build `--no-incremental` 0 warnings, 0 errors; ModelMoveTests, ModelFolderTests, ModelFolderSafetyTests, OfflineAndDownloadTests, ModelIntegrityTests, FixBatchDownloadTests and PublicCopyNeutralityTests 152 total, 0 failed, 2 skipped; full suite once 1971 total, 0 failed, 23 skipped.
12. **Model folder note reworded, APPROVED 22/10/2026 22:42 AEDT (owner).** `ModelsPage.FolderNote`, shown on Models and on Storage (one constant), now reads "Dictum looks here first and downloads go here. Changing it asks before moving any files." (was "…Changing it does not move any files."; the second sentence became untrue once the move was offered). Marker "APPROVED 22/10/2026 22:42 AEDT (second sentence reworded for move-on-change; proposed 22/10/2026)". Pins: the literal in `ModelFolderTests.Browse_writes_models_folder_and_reloads_and_Use_default_clears_it` updated; a byte pin added to `OwnerApprovalsTests.The_storage_page_strings_are_pinned` (it held no literal of the note before). SPEC §17 and §24 updated. Mutation: "files." changed to "files!": `The_storage_page_strings_are_pinned` and `Browse_writes_models_folder_and_reloads_and_Use_default_clears_it` observed failing, then restored. Gates: Release build `--no-incremental` 0 warnings, 0 errors; ModelFolderTests, ModelMoveTests, OwnerApprovalsTests and PublicCopyNeutralityTests 90 total, 0 failed, 0 skipped (the full suite was not rerun for this one-string change).
13. **Re-lens on 8001fa1/ea6303b (Block; fixed on top of 1730818 and the main merge 6a6c3b8, 23/10/2026). Supersedes items 3, 4 and 11.2 where they differ.**
    1. *Two paths to one folder (blocker).* A junction, subst or mapped drive, a share, or a packaged app's redirected folder can name the folder in use under another path. Each file then looked "already there", matched itself, and the original (the only copy) was deleted.
       - (a) `ModelMover.SameFolder` writes a uniquely named probe file in the new folder and looks for it in the old one. If it is found, no question is asked, the folder is written, and nothing moves.
       - (b) The mover never deletes an original that may be the target itself. `SameFileOnDisk` is managed only, because `PlatformTests` (`ArchitectureTests.Shared_code_declares_no_native_call`) forbids a native call in Dictum.Core. The lens suggested a Win32 file-ID comparison; a first version used `GetFileInformationByHandle` and the full suite failed on that rule. The check opens the original for itself alone, then opens the target. A refused open means "may be one file" (or something else holds the target), so the original is kept and the line reports it; both opening means two files. A file of the same name that may be the original counts as in place and its original is kept.
       - Pinned with a real junction (`mklink /J`, no administrator rights; the tests skip if the host cannot make one; on this PC they ran).
    2. *The models in use as one unit (blocker).* They move last. Each is copied, checked and renamed, but no original of theirs is deleted until all have arrived. If one fails or the move is stopped, the copies this move made of them are deleted (a file that was already there stays) and every original is kept.
    3. *A model chosen by its full path* (`whisper.model` / `llm.model`, "Choose model file…") that moved is rewritten to its new path in the same settings write as `models.folder` (`SetModelsFolder(..., alsoWrite)`), written **before** `models.folder` (corrected by item 14.3; first written after it).
    4. *No pinned hash means byte for byte.* A file of the same name and size counts as the same only when its bytes match: a `.part`, or a catalogue entry without a SHA-256. Otherwise it is skipped with "a different file of that name is already in the new folder".
    5. *Guards pinned one by one*:
       - `EnsureCleanup`
       - `ApplyWithDownloads`
       - Browse's and Use default's busy checks
       - a stopped move never writing the folder
       - the original's verified record forgotten
       - the default copy stopping when cancelled
       - the check before each file
    6. *Delete all app data* and *Remove Dictum completely* refuse with `MovingRefusal` while files move. Remove Dictum completely says it in a notice titled "Remove Dictum completely".
    7. *Calibration* loads no model while files move. `CalibrationHost.Refusal()` gives `MovingRefusal`, the results view shows it instead of running, and `CreateEngine` (and each load inside a run already under way) throws it.
    8. *Quit wording.* This round added " The model folder is still {old}, because the move was stopped." (PROPOSED 23/10/2026) for a move stopped by Quit; item 14.1 removed it before 1.3.0, so it does not ship. The PR body is corrected to match: "a different file of that name…", the singular " 1 other file…", the short `FolderKept` form, and failures no longer stopping the move.
    9. *Mutations, each observed failing on 23/10/2026, full `--no-incremental` rebuild each, then restored:*
       - 1a, probe removed: `Choosing_another_path_to_the_same_folder_moves_nothing` observed failing.
       - 1b, file identity always "different": `The_mover_never_deletes_an_original_that_is_the_target_itself` observed failing.
       - 2, models in use not treated as a unit: `When_one_model_in_use_fails_no_model_in_use_leaves` and `Quit_while_the_models_in_use_move_keeps_every_original` observed failing (and `The_models_in_use_move_last`).
       - 2, their copies not rolled back: the same two observed failing.
       - 3, full path not rewritten: `A_model_chosen_by_its_path_follows_the_move` observed failing.
       - 4, size-only sameness: `Equal_size_but_other_bytes_is_not_the_same_file` observed failing.
       - 5, `EnsureCleanup` guard removed: `EnsureCleanup_loads_nothing_while_files_move` observed failing.
       - 5, `ApplyWithDownloads` guard removed: `ApplyWithDownloads_is_refused_while_files_move_before_anything_is_asked` observed failing.
       - 5, Browse busy check removed: `Browse_opens_no_chooser_while_files_move` observed failing.
       - 5, Use default busy check removed: `Use_default_changes_nothing_while_files_move` observed failing.
       - 5, `result.Cancelled ||` removed from `kept`: `A_stopped_move_never_changes_the_folder` observed failing.
       - 5, `integrity.Forget` removed: `Moving_a_file_forgets_the_originals_verified_record` observed failing.
       - 5, default copy ignoring the token: `The_default_copy_stops_when_cancelled` observed failing.
       - 5, check before each file removed: `No_file_starts_after_a_cancel` observed failing.
       - 6, Delete all app data guard removed: `Delete_all_app_data_is_refused_while_files_move` observed failing.
       - 6, Remove Dictum completely guard removed: `Remove_Dictum_completely_is_refused_while_files_move` observed failing.
       - 7, calibration guard removed: `Calibration_loads_no_model_while_files_move` observed failing.
    10. *Files.* New `tests/Dictum.Tests/ModelMoveSafetyTests.cs`, so `ModelMoveTests.cs` stays under 1,000 lines; its helpers became `internal` for that.
    11. *Gates on the merged head 6ae49cb (this item plus origin/main cd48645), 23/10/2026:*
        - Release build `--no-incremental`: 0 warnings, 0 errors.
        - Gate classes (ModelMoveTests, ModelMoveSafetyTests, ModelFolderTests, ModelFolderSafetyTests, OfflineAndDownloadTests, ModelIntegrityTests, FixBatchDownloadTests, FixBatchCalibrationTests, PublicCopyNeutralityTests, OwnerApprovalsTests): 221 total, 0 failed, 2 skipped. The filter `~CalibrationTests` matched FixBatchCalibrationTests; no class has that name.
        - Full suite: 2495 total, 0 failed, 23 skipped.
14. **Round-3 lens on 6ae49cb/ffab59b (Needs changes, minors only; no data loss found under junction, subst, admin-share, hard-link, symlink and mmap attacks). Fixed on top of ffab59b, 23/10/2026.**
    1. *Nothing unapproved ships in 1.3.0.* The PROPOSED Quit sentence " The model folder is still {old}, because the move was stopped." is removed. When Quit stops a move the line is only the approved "Moved {k} of {n}." (now `ModelFolderActions.MovedStopped`, pinned in OwnerApprovalsTests), with no folder sentence. The fuller sentence stays PROPOSED for 1.3.1.
    2. *A kept original is said.* When a same-content file is already in the new folder and the original cannot be opened for itself alone, the original is kept. It is now reported with the approved "it was copied, but the original could not be removed" (it still counts as moved); before, it was kept silently. Pinned: `A_kept_original_beside_a_same_file_is_reported`.
    3. *Write order.* `SetModelsFolder` writes `alsoWrite` (a moved model's full path) **before** `models.folder`. The keys are written one by one, so a failure after the first leaves the setting naming the new copy, which exists, never the deleted original. Pinned through the "Set from the app" log line's key order in `A_model_chosen_by_its_path_follows_the_move`.
    4. *Calibration guards tested.* `A_calibration_run_already_under_way_loads_no_model_once_files_move` turns the flag on after `CreateEngine`: the next load is refused, the loader is never called, and the run says the refusal. `The_calibration_window_refuses_to_run_while_files_move` clicks Run test. The window's check now comes first, before the recordings and models are checked, so it is reachable.
    5. *Docs.* The PR body now says items 1 to 14, its strings heading says APPROVED, and its gate class names are corrected. Item 13 has its gate results (13.11). This entry's opening line now reads "all APPROVED except as noted". Item 13.3 and SPEC §24 give the write order; item 13.8 and SPEC give the Quit line.
    6. *Mutations, each observed failing on 23/10/2026, full `--no-incremental` rebuild each, then restored:*
       - 2, kept original returned silently: `A_kept_original_beside_a_same_file_is_reported` observed failing.
       - 3, `alsoWrite` written after `models.folder`: `A_model_chosen_by_its_path_follows_the_move` observed failing.
       - 4, per-load calibration guard removed: `A_calibration_run_already_under_way_loads_no_model_once_files_move` observed failing.
       - 4, the results view's guard removed: `The_calibration_window_refuses_to_run_while_files_move` observed failing.
    7. *Gates:*
       - Release build `--no-incremental`: 0 warnings, 0 errors.
       - ModelMoveTests, ModelMoveSafetyTests, ModelFolderTests, ModelFolderSafetyTests, OfflineAndDownloadTests, ModelIntegrityTests, FixBatchDownloadTests, FixBatchCalibrationTests, CalibrationEngineTests, PublicCopyNeutralityTests, OwnerApprovalsTests and ArchitectureTests (each by exact class name): 230 total, 0 failed, 2 skipped.
       - Full suite once: 2498 total, 0 failed, 23 skipped.

## Dialogs: the opener's mouse capture (23/10/2026, branch fix/dialog-mouse-capture, release 1.3.0)

1. **Defect (probe, 23/10/2026, read-only).** A question opened by a mouse click on a Settings button (both cloud consent dialogs, delete model, delete data, Remove Dictum completely, the download questions) could not be clicked: mouse input kept going to Settings until the dialog closed, while keyboard and UI Automation worked. Root cause: `Ui.Button` runs its action from Avalonia's `Click`, which is raised inside the button's `OnPointerReleased`, before the window releases the Win32 mouse capture; `Modal.Run` then showed the dialog on its own (`Show()` + `Activate()`, `CenterScreen`) and ran a nested `PushFrame` inside that `Click`, so the capture was released only once the dialog had closed. Evidence: a scratch Avalonia 11.3.22 app on real Win32, clicked with `WM_LBUTTONDOWN`/`WM_LBUTTONUP` sent to the opener, read `GetCapture()` inside the nested frame: the opener with `Show()` + `Activate()`; 0 with `ShowDialog(owner)`; 0 with `ReleaseCapture()` before `Show()`.
2. **Fix (chosen: ShowDialog with the opener as owner).** `Ui.Button` runs its action through `Modal.From(window, action)`, which records the window whose button is being clicked for the length of the action. `Modal.Run` shows the dialog with `ShowDialog(opener)` and `WindowStartupLocation.CenterOwner` when that window is visible; Avalonia disables the owner (which releases the capture), keeps the dialog above it and activates the owner again when it closes, so focus returns to the button. The `Closed` to frame-exit path is unchanged. With no click behind it (first run, the Welcome window's own frame, a question from the tray or from another thread), the dialog is shown on its own, centred on the screen, as before. `Run` clears the recorded opener while its frame runs, so a job the dispatcher runs inside the frame (a prompt from another thread) is never owned by it, and restores it afterwards for a second question from the same click (the two warnings of Remove Dictum completely). The tray panel is never an owner: it closes itself on `Deactivated`, and Avalonia closes owned windows with their owner, which would answer the question as Cancel. A second click on an opener whose dialog is up (the disabled owner stops the mouse; this stops any other way in) activates the open dialog and answers Cancel. No user-visible string changed. Files: `src/Dictum.UI/Views/Modal.cs`, `src/Dictum.UI/Views/Ui.cs`; `Dictum.UI.csproj` gains `InternalsVisibleTo Dictum.UiProbe` for the Win32 pin.
3. **Owner lookup, deviation from the brief.** The brief proposed the Settings window when open, else the active window among the desktop lifetime's `Windows`. The opener of the click is used instead: it is exactly the window that holds the capture (Settings only when the click was in Settings; a Settings window open behind a clicked window would not have released that window's capture), and it works on the headless platform, where `ApplicationLifetime` is null and `IsActive` stays false after `Activate()` (measured in a scratch headless test this lane), so the headless pins can run the real path.
4. **Alternative considered, not taken:** `IWindowFlags.ReleaseMouseCapture()` in `Dictum.Windows` (`ReleaseCapture`), called before `Show()`. It also releases the capture (probe), but leaves the opener enabled, so a second click could open a second dialog, and the dialog would stay unowned and centred on the screen. `ShowDialog(owner)` caused no regression in the existing pins, so the brief's preferred fix stands.
5. **Pins.** `DialogOwnerTests` (new): (a) `The_cloud_consent_opened_from_Settings_is_its_modal_dialog_and_one_at_a_time`: the real `ShellDialogs` and nested frame on the headless platform; "Use cloud clean-up…" in Settings opens the consent owned by Settings, `CenterOwner`; a second click while it is up opens no second window; Escape closes it unanswered and nothing is written. (b) `Enter_and_the_buttons_answer_the_consent_as_before`: Enter answers Cancel (the default) and writes nothing; "Allow cloud clean-up" records consent.json and the provider. (c) `A_question_with_no_click_behind_it_or_from_the_tray_panel_is_shown_on_its_own`: unowned and `CenterScreen`. (d) `Dialog_opened_by_a_click_gets_the_mouse_on_Win32` (gated by `DICTUM_UI_TESTS=1`, as `PillFocusTests`): runs `Dictum.UiProbe --dialog-capture` (`tests/Dictum.UiProbe/Program.Dialogs.cs`), which clicks a `Ui.Button` with `WM_LBUTTONDOWN`/`WM_LBUTTONUP` for three openers (ordinary, Topmost, closed while its dialog is up) and checks inside the frame: `GetCapture()` is not the opener, the dialog's Win32 owner is the opener, the opener is disabled, the dialog is the window at its own centre (above a Topmost opener too), a second click opens no second dialog; then OK clicked with mouse messages answers 0, the opener is enabled again and focus is back on the button; closing the opener closes its dialog and Ask returns -1. Run on this host: all PASS.
6. **Mutations, observed failing (23/10/2026), each restored.** (M1) `Modal.Run`'s owner forced to null (the dialog shown on its own, as before the fix): (a) failed (two windows) and (d) failed with "FAIL ordinary opener: inside the dialog's frame the opener no longer holds the mouse capture (GetCapture 0x13E12BC, opener 0x13E12BC)", the same for the Topmost and closed openers, and "Ask returned -1 (0, the OK clicked)": OK clicked with mouse messages did nothing, the reported symptom. (M2) the tray panel exclusion removed: (c) failed (owner FlyoutWindow, CenterOwner). (M3) the second-click guard removed: (a) failed (two windows).
7. **Not verified.** The installed Dictum.exe was not run (tray mode is never run on this host); the fix is proven on Avalonia's real Win32 backend through the probe, not in the app's own Settings window. Whether UI Automation's Invoke reaches a button of a disabled window was not tested; the guard covers it either way. A download question raised by a choice in a combo box (the tray panel's model lists) is not a `Ui.Button` click, so it keeps the earlier behaviour; it was not probed. Focus returning to the button is checked in the probe, not on a Settings page that redraws after its action.
8. **Gates, on de892f5 (this branch with origin/main 1960a5a, PR #61, merged in; the commit after it changes only this file).** Release build `--no-incremental`: 0 warnings, 0 errors. `DialogOwnerTests` and `DialogTests` with `DICTUM_UI_TESTS=1`: 8 total, 8 passed (the Win32 pin ran). Full suite once (without `DICTUM_UI_TESTS`): 2502 total, 0 failed, 24 skipped. PR #61's move question (Change…, Browse…, Use default in Settings) is asked from a `Ui.Button` too, so it is now owned by Settings like the others.

## 1.3.0 released; 1.3.1 backlog (23/10/2026)

1. **Released.** v1.3.0 was tagged on b8b072d at 02:21 AEDT; the workflow published the GitHub release (run 39803808616); dictum.kynni.au was deployed at 02:33 AEDT with version.json 1.3.0. The Store package and the sizes are in STORE.md, section 11. SPEC.md needed no change (no version line called 1.3.0 "prepared").
2. **PR #65 lens minors (1.3.1).**
   - Pins for `Modal.Run`'s opener clear (line ~68) and restore (~75). Scratch tests exist at `C:\Users\Kynni\AppData\Local\Temp\claude\E--Dictation-tool\bcfca6f1-7a8d-40ba-8c95-d1b0dbd7c70f\scratchpad\LensScratchTests.cs`: copy them into `DialogOwnerTests` in 1.3.1, not now.
   - `Modal.Wait` frames should clear and restore the opener like `Run`.
   - SPEC §32 wording: "opens no second dialog; that second request is answered as Cancel and the open dialog is brought forward".
3. **PR #61 not-built list (1.3.1).**
   - Free-space pre-check.
   - Rename on the same drive.
   - Start-up clean-up of stray `.move` files.
   - The fuller Quit sentence " The model folder is still {old}, because the move was stopped." (PROPOSED).
4. **PR #60 lens notes.**
   - The ceiling of the final-sentence rule: a closing made of dictated words passes.
   - Two log lines are approved but not pinned (the save-failure paths).
5. **PR #62 lens.**
   - The privacy "Cloud speech (off by default)" paragraph's two extra sentences await the owner's confirmation.
   - The README validator list is complete.
6. **Demos.**
   - PR #64 captions are PROPOSED.
   - help.html section 8 figcaption "stops at the consent step without agreeing" is to be reworded on the owner's approval.
   - The consent-dialog click defect was real and was fixed in PR #65 (demo lane finding).
7. **Owner-only items outstanding.**
   - Store upload of 1.3.0.
   - Store screenshot 1.
   - flyout.png.
   - The tester paragraph.
   - Counsel review of the consents and the privacy policy.
## Dictum for macOS (22-23/10/2026, branch feature/mac-platform)

Built on Kyle's Mac (Apple Silicon, macOS 27.2) from docs/MAC-HANDOVER.md, with the owner's decisions of 22/10/2026 that
override the handover: an Apple Developer Program membership exists, so the beta is a Developer ID signed and notarised
GitHub pre-release (no Gatekeeper warning), Apple Silicon only (Intel only if a tester needs it), the Mac App Store as a
later train. The owner creates the signing certificate and the notarisation credentials himself; nothing in the repo or
the build sessions ever holds them. The release waits until Dictum 1.3 is live on Windows. Provenance marks as in the
handover: **Verified** means read this session against the named source; **Recalled** is unverified.

**Owner approvals (APPROVED as a block at 23:59 AEDT 22/10/2026, relayed by the Windows orchestrator session):**

- a. Login item through `SMAppService` (ServiceManagement, macOS 13+), not the handover's LaunchAgent plist: the entry
  survives a moved bundle and appears under System Settings > General > Login Items. The handover's "retire when signed"
  simplification is retired before it was built. **Verified** 22/10/2026 on developer.apple.com (SMAppService: macOS
  13.0+; `register()` returns kSMErrorAlreadyRegistered when already registered and kSMErrorLaunchDeniedByUser when the
  user denied it).
- b. Notifications through `UNUserNotificationCenter`, guarded: outside an app bundle (a build tree, `dotnet run`) the
  centre has no bundle identifier, so the title and text go to the log instead. The handover's transient-panel
  simplification is not built.
- c. Permissions: both event taps are active taps (kCGEventTapOptionDefault), so the beta asks for Accessibility and
  Microphone only, not Input Monitoring as a third prompt. To be confirmed on hardware in the test plan (§7).
- d. Default hotkey on a Mac: Right Option (settings token `RightAlt`); on screen the Alt key reads "Option" and the Win key
  "Command" (`HotkeyNames.Dialect`, set by Dictum.Mac's entry point; parser tokens unchanged, so settings.json, logs and
  --selftest keep `RightAlt`, `Win`). Strings "Option" and "Command" APPROVED 23:59 AEDT 22/10/2026.
- e. `LSMinimumSystemVersion` 15.0: .NET 10 supports macOS 15 and later, Arm64 and x64 (**Verified** 22/10/2026,
  github.com/dotnet/core release-notes/10.0/supported-os.md); Avalonia 11 lists macOS 10.14 and later (**Verified**
  v11.docs.avaloniaui.net, Supported Platforms).
- f. Speech and clean-up runtimes: `Whisper.net.Runtime` 1.9.1 and `LLamaSharp.Backend.Cpu` 0.27.0 only. Both ship ggml's
  Metal backend for arm64 (**Verified** 22/10/2026 in the restored packages: `build/macos-arm64/libggml-metal-whisper.dylib`
  and `LLamaSharpRuntimes/osx-arm64/native/libggml-metal.dylib`), so the "CPU" runtime uses the GPU on Apple Silicon; the
  log keeps every `ggml_metal` line at info so the truth is in the log. `Whisper.net.Runtime.CoreML` is not referenced:
  Core ML needs a separate encoder file per model (`ggml-<model>-encoder.mlmodelc`, **Verified** whisper.cpp README) that
  the catalogue does not carry. The handover's "no Metal-capable backend" lead was wrong: it is inside the CPU package.
- g. Bundle identifier `au.kynni.dictum`; Developer ID signing with the hardened runtime and these entitlements:
  `com.apple.security.cs.allow-jit`, `com.apple.security.cs.allow-unsigned-executable-memory`,
  `com.apple.security.cs.allow-dyld-environment-variables`, `com.apple.security.cs.disable-library-validation` (the .NET
  host's defaults, **Verified** 22/10/2026 learn.microsoft.com "macOS Catalina notarization", dated 23/04/2026) plus
  `com.apple.security.device.audio-input` (**Verified** developer.apple.com Hardened Runtime).
- h. The publish and bundle script is bash (`scripts/publish-mac.sh`): this Mac has no pwsh; the handover allows either.

**Where the Mac keeps things.** Both roots are `~/Library/Application Support/Dictum` (settings.json, consent.json, .env at
the top; models/, logs/, calibration/ beside them), from `HOME` so a scratch profile can be tested as the Windows build
tests with APPDATA. Not `~/Library/Caches` (the handover's reason, that the system may purge Caches, stays Recalled and was
not relied on: one visible folder was reason enough). The owner's own install points `models.folder` at
a folder on an external volume through Settings > Storage; no code knows that path.

**Project shape.** `src/Dictum.Mac` is plain `net10.0` with `RuntimeIdentifiers` osx-arm64 (not `net10.0-macos`, which
brings a workload and bindings this app does not use); assembly name `Dictum.Mac`, because the tests load it beside
Dictum.Windows, whose assembly is `Dictum`; the bundle script renames the apphost to `Contents/MacOS/Dictum` (the host finds
its .dll by the name built into it). The solution builds on macOS: `Directory.Build.props` sets `EnableWindowsTargeting`
off Windows (the SDK otherwise refuses net10.0-windows with NETSDK1100, **Verified** by the first build here), and the x64
`PlatformTarget` of Dictum.Windows and the tests applies on Windows only (an x64-marked assembly cannot load into the arm64
test host: "The assembly architecture is not compatible", **Verified** 22/10/2026). The handover's "the tests are
platform-neutral by design" was wrong: tests/Dictum.Tests targets net10.0-windows and used Dictum.Windows types in 18
files; it now also references Dictum.Mac, and the Windows-only tests skip on macOS with a reason (lane report below).

**Core changes for the Mac** (src/Dictum.Core): `HotkeyNames.Dialect` and `DefaultHotkey` (decision d); `AppSettings.Hotkey`
defaults to `HotkeyNames.DefaultHotkey`; `LocalLlmEngine.LoadedLibraryPath` accepts `libllama.dylib`; both engines keep
`ggml_metal` lines at info. Pinned in tests/Dictum.Tests/MacCoreTests.cs.

**Known wording left for the orchestrator:** with `WhisperEngine.VulkanLoaderPresent` returning false on a Mac, the
whisper.gpu = auto log line still says "the Vulkan loader, vulkan-1.dll, does not load on this PC" (NoVulkanCrashTests pins
it); true as stated, but Windows wording. Also `WhisperEngine.LoadedRuntime` and `LocalLlmEngine.Backend` say "CPU" on a
Mac while Metal is in use inside that runtime; the ggml_metal log lines carry the fact. Both are candidates for a wording
pass with approved copy, not changed here.

**Lane findings (23/10/2026, each Verified on this Mac unless marked):**

- *Tests on macOS.* `dotnet test --project tests/Dictum.Tests -c Release` on the merged head: 2015 total, 0 failed, 1831
  succeeded, 184 skipped (the Windows-only ones skip through `TestEnv.WindowsOnly(reason)`; 54 methods gated, 13 converted
  to OS-neutral paths through `TestPaths.Native`). Three real cross-platform defects came out of it, each fixed at its root:
  (1) `SettingsFile.WriteAtomic` replaced a read-only settings.json or consent.json on macOS (a rename over a read-only file
  is allowed there, refused on Windows), so the "could not be saved" paths of the off switches never ran; off Windows a
  read-only target is now refused before the .tmp is written. (2) `Path.GetPathRoot` is "/" for every path on macOS, so free
  space, `ModelDownloads.PendingBytes` and `DiskSpace.IsNetwork` treated every folder as the startup disk; `DiskSpace.VolumeRoot`
  returns the deepest mount point there (text match, `// simplification:` in the file). (3) The Welcome window's free-space
  line read " has 41.0 GB free" on a Mac; a root with no name now uses `OnboardingText.StartupDisk`, PROPOSED: "The startup
  disk" (on screen: "The startup disk has 41.0 GB free"; another disk keeps its mount path, "/Volumes/Backup has 41.0 GB
  free"). Open: the theme names Segoe UI only (src/Dictum.UI/Theme/Controls.axaml), so macOS draws a fallback font and the
  longest tray-panel header line trims at 308 px; `Polish2Tests.The_panel_header_is_one_whole_line` is gated on macOS and a
  Mac font choice is for the owner.
- *Audio.* `AudioQueueCapture` asks the queue for 16 kHz mono float and reads back the format it records; a one-second
  capture on this Mac returned 1,013 ms of audio, and `kAudioDevicePropertyDeviceIsRunningSomewhere` went 1 while recording
  and 0 after Stop. Stopwatch and `mach_absolute_time` are the same clock here (Stopwatch.Frequency 1e9, timebase 125/3), so
  the first-frame figure is on the key-down clock. Finding: the first about 460 ms of every recording arrive as exact zeros
  on this Mac's only microphone (a USB camera microphone, 48 kHz), whether or not the queue converts, and the first start
  after launch took 5.1 s (later starts 78-150 ms). Cause unknown (device or system); other devices not tried. The pure
  parts (device resolution, format fallback, host-time conversion) are pinned in MacAudioTests; the real capture runs under
  DICTUM_AUDIO_TESTS=1.
- *Input.* The SDK's CGEvent.h says taps "may only receive key up and down events if … the caller is enabled for assistive
  device access", and that only root may tap at kCGHIDEventTap: the taps sit at kCGSessionEventTap and need Accessibility
  (handover §1's "Input Monitoring" was wrong for active taps); posting still goes to kCGHIDEventTap. Reading the legacy
  `NSStringPboardType` from the pasteboard returns data but writing it back is refused, so an early Restore cleared the
  clipboard (the owner's clipboard text was lost during the lane's gated test, 23/10/2026); the snapshot now keeps
  public.utf8-plain-text, public.rtf and public.html only, builds the new item before clearing, and leaves a refused type
  out. Fn maps to no key (the parser refuses it; its wording is approved copy). PrintScreen, ScrollLock, Pause and F21-F24
  have no Mac key (a test pins that set). Two `// simplification:` comments: Caps Lock reports a press and release together
  on every event (retire once hardware shows what macOS sends), and typed text goes 20 UTF-16 units per event (Recalled
  limit; retire once a hardware test shows bigger chunks arrive whole). The input lane also found that C `BOOL` is one byte
  and the shared interop's default four-byte bool marshalling could read undefined register bits on arm64: every bool return
  now crosses as I1. Not verified without Accessibility: tap installation, swallowing, Cmd-V and typing arriving; the two
  end-to-end tests are written and skip until the test host is trusted.
- *Packaging.* codesign treats every file under Contents/MacOS as nested code (signing the executable fails with "code object
  is not signed at all" while a managed .dll is unsigned), so `scripts/publish-mac.sh` signs every file there, the
  executable last, then the bundle, never `--deep` (`man codesign` on macOS 27.2: deprecated for signing as of macOS 13.0).
  The bundled model is fetched and verified by the published app itself (`--download-model base.en` with HOME in a scratch
  folder), so the pinned commit, size and SHA-256 stay in Core's catalogue only. `macos-15` runners are Arm64 (actions/
  runner-images README); `secrets` is not available in `if:`, so the job carries two booleans in `env`. The icon is built at
  publish time from site/dictum.svg (ten iconset PNGs, `iconutil`), not committed.
- *Shell.* Decision reversed on evidence: Avalonia.Native 11.3.22 never raises `TrayIcon.Clicked` (its `trayicon.mm` sets no
  target or action on the NSStatusItem and `avn.idl`'s `IAvnTrayIcon` has no click callback, **Verified** in the Avalonia
  source for the tag), so `TrayHost` creates its own NSStatusItem with a target class defined through `ObjC.DefineClass`
  and `sendActionOn:` left or right mouse-up; Avalonia only encodes `TrayIconArt`'s output as PNG. Kept: the click point from
  `NSEvent.mouseLocation`, colour icons, creation on the UI thread. Avalonia.Native's screen "pixels" are points with
  scaling 1 and a top-left origin flipped against the primary display (`Screens.mm`, `main.mm` `ConvertPointY`), so `Move`
  applies no backing scale and `ForegroundBounds` (CGWindowList, top-left points) needs no conversion. Overlays sit at window
  level 25 (`NSStatusWindowLevel` = `kCGStatusWindowLevel`, NSWindow.h and CGWindowLevel.h). `TryGetPlatformHandle()` gives
  descriptor "NSWindow" with the NSWindow pointer (`MacOSTopLevelHandle`, TopLevelImpl.cs). SMAppService registers even a
  bare executable outside a bundle (a scratch apphost went NotFound → Enabled → NotRegistered), so the bundle-only guard
  stays a policy, not a necessity; that scratch run left a disabled "exp" record in `sfltool dumpbtm` on the owner's Mac.
  `AppShell.RemoveEverything` passes `Paths.ExeDir` (Contents/MacOS), so `RemovalSteps` accepts either that or the bundle
  path when it belongs to the running bundle, refuses anything else, and refuses a path under `/AppTranslocation/` (an
  unsigned app opened from Downloads runs from a read-only translocated copy). A notification delegate implementing
  `willPresentNotification:` is needed or macOS suppresses banners while Dictum is frontmost. FileShare.None on Unix is now
  **Verified** (a second open throws IOException). The tone synthesis moved to Core as `Dictum.Audio.CueTones` (byte-identical;
  Windows calls it). `// simplification:` in `SingleInstance.ListenForOtherLaunches`: a Unix socket path is limited to 104
  bytes on macOS (Recalled), so a home path over about 50 characters makes the bind fail and a second launch time out after
  30 s; retire by binding in a shorter per-user folder when a tester hits it. Approved Windows wording still shown on a Mac
  and not reworded here: "notification area (system tray)", "Windows did not let Dictum switch back…", "everything Dictum
  has put on this PC" (AppShell).

**Merge of main at 1.3.0 (b8b072d, PRs #54-#65) into this branch, 23/10/2026 02:38 AEDT, at the Windows orchestrator's
request before the PR.** Conflicts: this file (both appended sections kept, main's first) and `Dictum.UI.csproj` (the
`InternalsVisibleTo` lines for Dictum.Mac and Dictum.UiProbe both kept). Mac consequences, each Verified on this Mac
unless marked:
- Main's model-move tests (PR #61) failed ten ways on the Mac, all test-side: three make a directory junction with
  `cmd.exe /c mklink /J` (the helper now returns false off Windows and its callers skip, as they already did when mklink
  fails); five pin `ModelMover.Nested` / `PathRegistry.IsInside` with drive-letter paths (now through `TestPaths.Native`,
  so `D:\` is `/D` there and the same containment is checked); two pin that a loaded model file cannot be moved (a Windows
  sharing rule: `TestEnv.WindowsOnly(TestEnv.HeldOpen)`, the gate the tests lane used for the same rule). The mover's own
  code is platform-neutral: `ModelMover.SameFileOnDisk` relies on FileShare.None, which .NET implements on Unix with an
  advisory lock (the lane's earlier finding), and `ModelMoveSafetyTests` pass unchanged on the Mac. Suite after the merge:
  2599 total, 0 failed, 2407 passed, 192 skipped.
- The dialog-ownership fix (PR #65, `Modal.Run` shows a dialog with `ShowDialog(owner)` from the clicked window and
  disables the owner) is designed for the Mac too: Avalonia.Native's `WindowImpl.SetEnabled(false)` calls
  `MouseDevice.PlatformCaptureLost()` because "macOS doesn't have the concept of mouse capture … release the mouse capture
  when the owner window is disabled. This behavior matches win32" (Avalonia source, branch release/11.3.22,
  src/Avalonia.Native/WindowImpl.cs, read 23/10/2026), and `SetParent` hands the owner to the native window. From the tray
  panel (`FlyoutWindow`) and at first run the dialog is still shown on its own, as before. **Not verified** on the Mac: that
  the first click on a dialog opened from a Settings button lands (the real-backend check, `Dictum.UiProbe`, is a Windows
  executable, net10.0-windows, referencing Dictum.Windows); it is in the PR's owner steps (Settings > Storage > Use default).
- `scripts/publish-mac.sh` reads `DICTUM_MAC_DIST` (default `dist/`), added so the merged head could be built and signed
  while the earlier bundle's notarisation was still waiting on Apple in `dist/` (submitted 00:26 AEDT, still "In Progress"
  at 02:39; Apple's developer status feed listed the Developer ID Notary Service with no events at 02:34).
- Nothing else on main touches a platform seam: the cloud speech UI, own clean-up instructions, Help questions, plain-words
  page and the key file are Core/UI code the Mac build takes as is (the key file, `KeyFile.cs`, is a plain .NET
  create-new write of `.env` in the config folder).
- `NSMicrophoneUsageDescription` now reads "Dictum listens only while you hold your hotkey, and keeps the audio on this Mac
  unless you turn on cloud speech." (PROPOSED), the wording main's handover §0 replaced the earlier proposal with, because
  the earlier sentence is untrue once cloud speech (1.3) is on. Pinned in MacPackagingTests.

**Launching the signed bundle on this Mac (23/10/2026 02:44-02:56 AEDT), each Verified here:**
- `Dictum.app/Contents/MacOS/Dictum --selftest` runs from the signed bundle (hardened runtime, Developer ID): speech runtime,
  profanity list, calibration script and the microphone list all [OK]; the result was FAIL only because the owner's
  settings name medium.en, which is not downloaded here.
- The first three tray-mode launches (through `open` and from a terminal, Developer ID and ad-hoc alike, bundled and not)
  died two seconds after "Dictum 1.3.0 starting": an unhandled `InvalidOperationException`, "Avalonia.Native was not able to
  start the RenderTimer. Native error code is: -6661". Cause: the display was asleep (`system_profiler SPDisplaysDataType`:
  "Display Asleep: Yes"); a Swift probe showed `CGGetActiveDisplayList` returning 0 displays and
  `CVDisplayLinkCreateWithActiveCGDisplays` returning -6661 (kCVReturnInvalidArgument) while
  `CVDisplayLinkCreateWithCGDisplay(CGMainDisplayID())` succeeded; Avalonia 11.3.22 creates its timer with the former
  (`native/Avalonia.Native/src/OSX/PlatformRenderTimer.mm` line 24, read through the GitHub API). With the display woken
  (`caffeinate -u`) the probe passed and the same bundle ran. So Dictum cannot start while every display is asleep (a
  launch at login or a relaunch over a remote session with the screen off): not a defect in this branch, and not caught
  anywhere; a tester who sees that crash report (SIGABRT, "RenderTimer", -6661) should check the display. Candidate for a
  guard in `Program.Main` (wait for an active display, or log a plain message) if a tester hits it.
- With the display awake the signed bundle (272.0 MiB; `codesign --verify --deep --strict`: valid on disk, satisfies its
  Designated Requirement; `spctl --assess`: rejected, "source=Unnotarized Developer ID", as expected before notarisation)
  started Avalonia on its native backend and showed one window, 720 x 528 points at layer 0 (`CGWindowListCopyWindowInfo`
  for the process): the Welcome to Dictum window (`OnboardingWindow.WindowWidth` = 720), both with the owner's settings (its
  first-run questions are unanswered on this Mac) and with a scratch `--config`. `--exit-after` did not quit it because
  `AppShell.Start` runs the Welcome flow before `ScheduleDiagnosticExit`, as on Windows; the windows were closed with
  SIGTERM after 30 s, so nothing past the Welcome window (menu-bar icon, taps, panel) was exercised: those stay in the
  owner's test plan.
- `screencapture` is refused for this session ("could not create image from display": no Screen Recording permission), so
  no screenshot; the window list above is the evidence.

**Lens round on PR #67 (Windows orchestrator, 23/10/2026 ~03:05 AEDT, verdict Block), fixed on this branch the same night:**
- B1, release wiring: the Windows `release` job needs `build` alone and downloads its two artifacts by name
  (`dictum-win-x64-zip`, `dictum-release-files`), never every artifact of the run, so no Mac zip reaches a Windows release
  and a Mac job failure cannot block one. The Mac zip ships only through a new `release-mac` job on a `mac-v<version>` tag
  (`tags: ['v*', 'mac-v*']`), as a GitHub pre-release with its own notes file, `packaging/release-notes-mac.md` (the
  PROPOSED paragraph moved there from the Windows preamble). Pinned in MacPackagingTests.
- B2, public README: both Mac sections removed (the beta is not announced until the owner approves the copy and it ships);
  the text lives in src/Dictum.Mac/README.md, marked PROPOSED. Pinned ("macOS (beta)" absent from README.md).
- B3.1, neutrality sweep: `CGEventSourceButtonState` carries one of the words across a letter boundary. The test now cuts an
  exact-identifier allow-list (`CGEventSourceButtonState`, `EventSourceButtonState`) out of a file's text before the sweep,
  never widening the match to word boundaries; pins: the identifier passes, the bare word, the identifier in lower case and
  a piece of it still fail. M1 with it: the test looks for `git` off Windows, so the sweep now runs on the Mac (it had
  always skipped here, which is why the branch's "0 failed" hid this).
- B3.2, `MacShellTests.A_translocated_copy_is_never_moved`: the fake bundle joins its subfolder with "/" on every OS, since
  `RemovalSteps` looks for "/AppTranslocation/" as macOS writes it.
- M2, microphone text: `NSMicrophoneUsageDescription` is now "Dictum uses the microphone only while it is recording (from
  your hotkey until the recording stops) and for the microphone test. The audio stays on this Mac unless you turn on cloud
  speech." (PROPOSED; the hotkey-only sentence was untrue for hands-free recording and the microphone test). Handover line
  changed to match.
- M3, entitlements experiment (Verified on this Mac): a bundle signed with `allow-jit`, `allow-unsigned-executable-memory`
  and `device.audio-input` only, without `allow-dyld-environment-variables` and `disable-library-validation`, ran
  `--selftest` (PASS: whisper runtime loaded, base.en SHA-256 verified), transcribed a 4.09 s synthesised wav through
  base.en on Metal in 707 ms (the sentence came back exact), and started the tray app (the Welcome window on screen for
  30 s). The two entitlements are dropped. Not exercised: the clean-up model load (qwen2.5-1.5b is not on this Mac and the
  owner's model folder does not exist yet; its dylibs load the way whisper's do, all signed by the same team) and a
  dictation through the taps (needs Accessibility). If a tester's clean-up load fails with a library-validation error in
  the crash report, restore `disable-library-validation` and record the error here.
- M4, `SingleInstance.ListenForOtherLaunches`: a bind failure (SocketException, ArgumentOutOfRangeException, IOException,
  UnauthorizedAccessException) is a logged warning and a no-op handle, never an exception out of `AppShell.Start`; the
  simplification comment says so. Pinned by a test with a 120-character socket folder; observed failing with the catch
  filter disabled (ArgumentOutOfRangeException from UnixDomainSocketEndPoint), then restored.
- M5, `HotkeyNames`: pure overloads `Display(hotkey, dialect)`, `Display(spec, dialect)`, `Key(name, dialect)` and
  `DefaultHotkeyFor(dialect)`; MacCoreTests use them and no test sets the global `Dialect` any more (the collection
  attribute is gone with it).
- m1, this file no longer names the owner's volume path; m2, `.DS_Store` untracked (and, since the follow-up PR after the merge, listed in .gitignore; the lens found it was not); m3, the
  network-audit pin compares repo-relative paths; m4, the handover's two "Input Monitoring" lines now follow decision c.

**Notarisation (23/10/2026, after PR #68 merged at 03:57 AEDT; main 8d43a08), each Verified on this Mac:**

- The first submission (00:26 AEDT, the pre-merge 1.2.1 bundle with the five-key entitlements) sat "In Progress" for
  about five and a half hours and came back Accepted at 06:00 AEDT; stapled; `spctl -a -vv -t exec` "accepted,
  source=Notarized Developer ID". That bundle is not the beta.
- A rebuild from main 8d43a08 by the agent session at 06:01 signed and verified, then `notarytool` stopped with "No
  Keychain password item found for profile: dictum-notary": the owner's keychain profile is not reachable from the agent's
  shells (it was at 00:26). The agent did not inspect the keychain (owner's credentials). The owner ran
  `scripts/publish-mac.sh` with both variables in their own terminal at 06:08: submission d405b5d5-f883-45f5-b864-c4490b046ff6
  was Accepted within minutes, stapled at 06:10, zipped at 06:13. `dist/Dictum-1.3.0-osx-arm64.zip`, SHA-256
  5d8ac4bc9ef79979619d1f6ae81d010ce62939c1429a0bc99a08f42bbf86703f; `stapler validate` and `spctl` both pass; the bundle's
  entitlements are the three keys of M3, so Apple accepts that set.
- Consequence for the release path: notarisation runs from the owner's terminal or from CI with the five `MAC_*` secrets,
  never from an agent session. The hands-on checklist (Welcome window, Microphone, Accessibility, hotkey, Cmd-V, login item,
  removal, Gatekeeper on the quarantined zip) is next and is the owner's; no `mac-v` tag before the owner approves the
  Mac copy.
- Owner's first dictation (23/10/2026, reported by the owner at about 06:20 AEDT, in a line dictated with Dictum into the
  agent session's terminal): the notarised 1.3.0 bundle launched, was granted Microphone and Accessibility, recorded on the
  hotkey, transcribed, and pasted the text into another app. That covers the launch, the two permission dialogs, the event
  tap, the hotkey and Cmd-V paste end to end. Not yet exercised by the owner: the login item, removal, the owned dialogs,
  and whether the launch was from the quarantined zip copy (the Gatekeeper first-open dialog) or from dist/Dictum.app.
- Mac copy APPROVED by the owner (23/10/2026 06:2x AEDT, "approved both"): the release-notes paragraph in
  packaging/release-notes-mac.md and the install paragraph in src/Dictum.Mac/README.md. The PROPOSED label is gone from
  both and the packaging test now pins it absent (observed failing against the labelled file before the file changed).
  Still PROPOSED, not covered by that approval: the `NSMicrophoneUsageDescription` sentence (M2 above) and the Right Option
  default hotkey (decision d). The public README stays silent about the Mac until the beta ships.
- `NSMicrophoneUsageDescription` APPROVED by the owner (23/10/2026, "yes" to the sentence as written in M2). The handover
  and the packaging test's comment say so. The Right Option default hotkey (decision d) is the one Mac string still
  PROPOSED.
- Right Option as the Mac default hotkey APPROVED by the owner (23/10/2026), with this rider from the owner: the default is
  exactly why users must test and assign their key at install and first use. The Welcome flow already does that on both
  platforms: step 4 "Your hotkey" (`HotkeyStep`, the capture box listening from the moment the step appears, "Use this
  hotkey" / skip) and step 6, a first dictation into a box in the window through the real path. No Mac string is PROPOSED
  any more.
- Gatekeeper test on the draft pre-release (23/10/2026 06:49 AEDT, the owner's hands, Verified by the agent afterwards): the
  owner downloaded `Dictum-1.3.0-osx-arm64.zip` from the `mac-v1.3.0` draft with Safari, unzipped it in Downloads and opened
  it. It went straight to the menu bar with no dialog at all: no Gatekeeper warning, and no Welcome window or permission
  prompt because the settings, consent and the Microphone and Accessibility grants from the morning's bundle carried over
  (same bundle identifier and signing identity). Checked afterwards: `~/Downloads/Dictum.app` carries the Safari quarantine
  attribute; `spctl -a -vv -t exec` says "accepted, source=Notarized Developer ID"; the running process was that copy,
  under `/private/var/folders/.../AppTranslocation/.../Dictum.app`, which is macOS running a quarantined app from a
  read-only image because it was opened from Downloads rather than Applications. That is the case
  `MacShellTests.A_translocated_copy_is_never_moved` covers; the release notes' "move Dictum.app to Applications" is what a
  tester should do before turning on launch at login, since the translocated path is not stable.
- Dictum for macOS 1.3.0 (beta) PUBLISHED as a GitHub pre-release, tag `mac-v1.3.0` on main eec9c2e, by the Windows
  orchestrator at 06:52 AEDT 23/10/2026 on the owner's "yes, tell them to publish" (06:53 AEDT in the Mac session; the
  owner also answered in the Windows session). Verified by the agent at 06:53 from the public download URL, unauthenticated:
  `Dictum-1.3.0-osx-arm64.zip` 186,316,219 bytes, `shasum -a 256 -c SHA256SUMS-mac.txt` OK (SHA-256
  5d8ac4bc9ef79979619d1f6ae81d010ce62939c1429a0bc99a08f42bbf86703f, the owner's locally notarised zip). The CI `release-mac`
  job for the tag fails by design (no MAC_* secrets); the pre-release was created by hand with the approved notes. Still
  open from the Mac checklist, none a blocker: login item, removal, the owned dialogs, the clean-up model load without
  `disable-library-validation` (first tester with that model will show it).

**First beta finding, Settings > Storage (owner, 23/10/2026 ~06:58 AEDT, branch fix/storage-page-copy):** the owner asked why
the page shows the model folder with Change… and Use default at the top, then "Where Dictum keeps things" below with no
Change. The two panels are different things: the top is the one folder a user can move (models, with the 1.3 move-on-change
flow); the bottom is a read-only map of the folders Dictum does not let anyone move (the config folder holds settings.json
itself, so it cannot be stored in a setting; the logs and recordings folder; the program). Two defects on that page, both in
shared UI code, so Windows had the first as well:

- The "Models, logs, calibration recordings" row always printed Dictum's own data folder. That is right while no model folder
  is chosen (downloads land in that folder's models subfolder, the lookup order's default folder) and redundant once a model
  folder is chosen (the Models card lists every model folder, Dictum's own models subfolder included, in lookup order). The
  state is read from the lookup order's first entry, not the raw setting: choosing the default folder's own path in the
  chooser writes models.folder but LookupOrder drops it, and the row keeps the models (re-lens on PR #71; third test,
  observed failing against the raw-setting predicate). Rarer edge, left as is: a folder chosen inside Dictum's own data
  folder (that folder itself, say) is a Chosen entry, so the row drops the models although they are still under it. The lens on this PR caught that the
  first fix dropped the models from the row in both states; the owner decided (07:18 AEDT, through the lens): the row follows
  the state. `StoragePage.DataFolderRow(modelFolderChosen)`: no model folder chosen, "Models, logs, calibration recordings"
  (the existing words, byte for byte); a model folder chosen, "Logs, calibration recordings" (PROPOSED). The Open button's
  accessible name follows the row. One ModelFolderTests test per state, each observed failing on the reverted behaviour
  (the state-blind label put back), then restored.
- "Remove everything" said "the Start menu shortcut and the launch-at-login entry" on a Mac. The sentence now takes the
  platform's own words from a new `IRemovalSteps.StartupItemsPhrase`: Windows "the Start menu shortcut and the launch-at-login
  entry" (unchanged on Windows: PlatformTests pins the whole sentence byte for byte), macOS "the login item" (PROPOSED, pinned in MacShellTests). The page
  pin was observed failing with the Windows literal put back, then restored.
- Not in this change, left for a Mac help pass: `HelpText` "How do I remove Dictum completely?" still says "its Start menu
  shortcut and its start-with-Windows entry" and the Microsoft Store sentence on a Mac; HelpText is plain constants with no
  platform code by design, so that is a copy decision for the owner (PROPOSED Mac wording to follow), not a one-liner.
- Dictum for Mac (beta) announcement APPROVED by the owner (23/10/2026 07:10 AEDT), sections A, B and C of the proposed block as
  one block, built word for word in `site/index.html` (meta description, hero eyebrow, second hero button, hero note, the new
  "Dictum for Mac, in beta" section, the "Runs on" table row, the roadmap item moved up to Done, three "What you need" items,
  the closing band's second button), `README.md` (the install section's fourth route and "Four routes") and
  `site/privacy.html` (the scope sentence only). Every Mac button links to
  https://github.com/kynniau/dictum/releases/tag/mac-v1.3.0, to be updated with each Mac release. Each changed block carries an
  "APPROVED by the owner 23/10/2026 07:10 AEDT: do not reword" comment. `site/styles.css` gains `.cta-pair` (two buttons stacked
  on a phone, side by side from 640px) and the unused `.btn-secondary` border is now `--text-3` so the button edge meets the
  3:1 non-text contrast in both themes. Tests: `SiteStoreFirstTests` accepts the Store or the Mac link as a landing-page button
  (the first is still the Store, in the hero) and asserts exactly 3 Mac buttons (hero, Mac section, closing band) and 2 Store buttons, each in its section, and pins every approved sentence of the Mac section's two paragraphs, its button note and the other Mac lines; `MacPackagingTests` pins the README's four routes. Mutations observed failing against the pins (23/10/2026): "Intel Macs are not supported." removed (1 failure); "right Option" changed to "right Ctrl" in the Mac section (1); the closing-band Mac button deleted (1); both Next items removed (3); index.html alone put back to origin/main (21 of 91); index.html and privacy.html both put back (22 of 91). `SiteStoreFirstTests` baseline 91 of 91.
  The Microsoft Store listing stays silent about the Mac (Store policy 10.1.5; the first submission was rejected for mentioning
  it, `docs/STORE.md`, 16/10/2026), and `docs/STORE.md` is not touched. The privacy policy's storage paragraph still names
  Windows folders only (on a Mac everything lives in `~/Library/Application Support/Dictum`): FLAGGED FOR COUNSEL, not changed
  here. Not in this block: Help and the site help page say nothing Mac-specific yet (a later copy block).
- The owner approved (23/10/2026 07:47 AEDT) two Next items for "What’s next", after the Done items and in this order: "Models on another drive" ("Keep the big model files on a second drive or a shared folder, for shared desktops with little disk.") and "Add a word by hotkey" ("Say a name once, fix its spelling, and Dictum gets it right from then on."). They use the existing `status-next` markup, carry an APPROVED comment, and are pinned by `SiteStoreFirstTests` (observed failing when removed: 3 failures). The section heading and intro are unchanged.

## Calibration: the download question and "Downloading…" (23/10/2026 09:07 AEDT, the fix commit f0b1bed; lens follow-up the same morning; branch fix/calibration-consent-owner)

1. **Finding (Mac beta, through the demo rig; capture `37-hidden-dialog.png` on the Mac, not in the repo).** In Calibrate my voice, "Download and include" (small.en) opened "Dictum: Download a model" behind the calibration window, exactly centred on the screen, and the window already said "Downloading small.en: progress shows in the tray panel. It joins the test when it arrives." with the button reading "Downloading…", although nothing had started.
2. **Cause, checked on Windows on the headless platform.** Two parts. Only the second is in the shared code as diagnosed.
   - **The dialog's owner: not reproduced.** The diagnosis handed to this lane was that the calibration buttons never call `Modal.From`, so `Modal.Run` takes its no-owner branch. That does not hold on origin/main 5b5088c. Every calibration button is a `Ui.Button` (CalibrationWindow.cs lines 78-104 on main; CalibrationResults.cs lines 106 and 132), and `Ui.Button` runs every click through `Modal.From(TopLevel.GetTopLevel(button) as Window, …)` (Ui.cs line 42, PR #65). The chain from the click through `PersonalService.DownloadForCalibration`, `ModelCoordinator.ApplyWithDownloads` and `ShellDialogs.ConfirmDownload` to `Modal.Run` is synchronous on the UI thread. The new pin (a) passed on origin/main: the question's owner is the calibration window, `CenterOwner`. Inferred, not verified on a Mac: the calibration window itself opens `CenterScreen` (CalibrationWindow.cs line 76), so an owned, `CenterOwner` question over a calibration window that was not moved is also exactly screen-centred, and the capture's position does not tell the two branches apart. A question owned by the calibration window but shown behind it would be a macOS ordering matter in Avalonia.Native's `ShowDialog`; the macOS section above already records the owned dialog as not verified on the Mac. The other possibility is that the capture came from a build without PR #65, but `mac-v1.3.0` contains 73401e7 (`git merge-base --is-ancestor`). A re-capture on the Mac with this branch is needed.
   - **The button and status line: confirmed.** `CalibrationResults.RenderMissing` marked the model as downloading, disabled the button, set "Downloading…" and wrote the status line *before* `DownloadSpeechModel` asked the question. So they showed while the question was open, and a Cancel left them until the posted callback redrew the list and wrote "small.en was not downloaded, so it was not added to the test." Pin (b) failed on origin/main.
3. **Fix.**
   - `ModelCoordinator.ApplyWithDownloads` takes an optional `agreed` callback. It is told true once the download goes ahead (agreed to, or already on its way and so not asked about again) and false when the question is declined. It is not told when the download is refused before the question (offline_only, no space, login-gated, files moving), or when nothing needs downloading. It is called synchronously on the calling thread, before `ApplyWithDownloads` returns; the on-arrival and finished callbacks may come later, from a download.
   - `PersonalService.DownloadForCalibration` and `CalibrationHost.DownloadSpeechModel` pass it through. The host's delegate gains a parameter, in the order name, agreed, on arrival, finished.
   - `CalibrationResults.RenderMissing` marks the model, redraws the list from the session and writes the status line only on true. Redrawing, rather than changing the clicked button, keeps a list that was redrawn while the question was open (a test run ending) right.
   - On false, the posted finished callback returns without touching the window.
   - A refusal still says "… was not downloaded, so it was not added to the test." beside the coordinator's balloon, as before.
   - No user-visible string changed. No `Modal.From` was added, because the existing `Ui.Button` path already owns the question. The other callers of `ApplyWithDownloads` (the Mode presets) pass no `agreed` and are unchanged.
4. **Pins** (`DialogOwnerTests`: the real `ShellDialogs` and nested frame on the headless platform, no speech model installed, a fake download host that answers HTTP 403 and counts requests):
   - (a) `The_calibration_download_question_is_the_calibration_windows_modal_dialog`: "Download small.en and include it in the test" opens "Dictum: Download a model" owned by the calibration window, `CenterOwner`, and nothing is requested.
   - (b) `Declining_the_calibration_download_leaves_the_window_as_it_was_and_downloads_nothing`: while the question is open the button still reads "Download and include", enabled, and every text in the window is as before. After Cancel the same holds, with no request, no download running and none ended.
   - (c) `Agreeing_to_the_calibration_download_starts_it_and_says_so`: Download gives "Downloading…", disabled, and the status line. The request reaches the host, and the failure then restores the button and says it was not downloaded.
   - (d) `A_refused_calibration_download_asks_nothing_and_says_it_was_not_downloaded`: under offline_only no question opens, the window says it was not downloaded, the button is usable and nothing is requested.
   - (e) `A_list_redrawn_while_the_calibration_question_is_open_still_shows_the_download`: the list is redrawn inside the question's frame, then Download; the button shown reads "Downloading…", disabled.
   - (f) `Including_a_model_already_downloading_asks_nothing_and_shows_the_download` (lens on PR #73): small.en is first started by another caller of `ApplyWithDownloads` and held by the fake host; "Download and include" then opens no question, the button reads "Downloading…", disabled, the status line says so, and there is still one request. Cancelling that download makes the window say it was not downloaded.
5. **Mutations, observed failing (23/10/2026), each restored.**
   - (M1) The five production files put back to origin/main, run again with all six pins: (b) failed with "Assert.Equal() Failure: Values differ, Expected: Download and include, Actual: Downloading…" (the button while the question was open). (a), (c), (d), (e) and (f) passed, as expected: main already owned the question, set the state on Download (before the question), reported a refusal, and drew a redrawn list from the session it had already marked.
   - (M2) `Ui.Button`'s opener replaced with null (the cause the Mac session suspected): (a) failed with "Assert.Same() Failure: Values are not the same instance, Expected: CalibrationWindow …, Actual: null". PR #65's cloud-consent pin failed too (two windows).
   - (M3) `agreed?.Invoke(true)` removed: (c) failed with "Expected: Downloading…, Actual: Download and include".
   - (M4) `agreed?.Invoke(false)` removed: (b) failed with "Collections differ at index 17, Expected: "", Actual: "small.en was not downloaded, so it was not added t…"".
   - (M5) The window's finished guard changed to "not downloading means declined": (d) failed with "Assert.Contains() Failure: Item not found in collection".
   - (M6) The clicked button changed in place instead of the list being redrawn: (e) failed with "Expected: Downloading…, Actual: Download and include". (c) passed.
   - (M7) `agreed(true)` called only when the question was asked (moved into the asked branch; the lens's mutation, which the lens reports no test caught before (f)): (f) failed with "Assert.Equal() Failure: Values differ, Expected: Downloading…, Actual: Download and include". Every other pin passed.
6. **Other unowned call sites found, not changed here.**
   - **Settings > Mode > Light | Full** (`GeneralPages.cs` line 370, a `Segments` toggle; its `IsCheckedChanged` does not go through `Modal.From`). A preset that needs a download opens "Dictum: Download a model" with no owner, `CenterScreen`. Verified with a throwaway headless probe on this branch, deleted afterwards: one question, owner null, `CenterScreen`.
     - On Win32 a toggle's click is raised inside `OnPointerReleased`, as `Ui.Button`'s is, so PR #65's first-click capture problem probably applies there too. That is inferred and was not probed.
     - Candidate fix: run `Segments`' change through `Modal.From(TopLevel.GetTopLevel(this) as Window, …)`, as `Ui.Button` does. That is one site in the shared primitive, and it also covers the tray panel's copy, which `Modal.Run` keeps unowned anyway.
   - **No owner, by design:** the tray panel's Mode toggle and model lists (`FlyoutWindow.cs` lines 84, 477, 487; `Modal.Run` excludes the panel, pinned); the first run's pickers and the missing-model question (`ModelCoordinator.cs` lines 247, 252, 454); and the earlier-build removal offer at start (`AppShell.Actions.cs` line 245).
   - **The system file and folder choosers** (`ShellDialogs.ChooseFile` / `ChooseFolder`, from the Welcome window's steps and Settings > Models) are lent a window that is never shown, so they are never owned by the window whose button opened them. That is a different mechanism, by design per its comment; their stacking was not checked.
   - **Checked and owned** (a `Ui.Button`, synchronous up to the question): every calibration button; both cloud consents; delete model (every question before its first await); delete data; Remove Dictum completely (both warnings); the model folder move question; the Settings > Models catalogue and Simple rows; and the Welcome window's buttons. The Fix last dictation, Add word and Add snippet windows open no question.
7. **Not verified.** Nothing ran on a Mac. The calibration path was not run on the real Win32 backend: `DICTUM_UI_TESTS` was not set, so the gated probe pin skipped. The installed app was not run, because tray mode is never run on this host. After Download, keyboard focus leaves the clicked button, as it already did when a download ended and the list was redrawn.
8. **Gates, on this branch after the lens follow-up (23/10/2026).** Release build `--no-incremental`: 0 errors, 5 warnings, all xUnit2013 in `MacPackagingTests.cs` and `SiteStoreFirstTests.cs`, which this branch does not touch. `DialogOwnerTests`: 10 total, 9 passed, 1 skipped (the gated Win32 probe). Full suite (`dotnet test Dictum.sln`, Debug, without `DICTUM_UI_TESTS`): 2629 total, 0 failed, 2588 passed, 41 skipped. It ran on the final tree before these counts were written into this file. On f0b1bed, before pin (f): 2628 total, 0 failed, 2587 passed, 41 skipped.

## Legal block, site side: Terms of Use page, Privacy Policy rewrite, footers, pins, host and file tests (23/10/2026 11:10 AEDT, branch legal/site)

The owner approved, at 09:50 AEDT on 23/10/2026, draft 3 of the Terms of Use and the Privacy Policy and the build tasks in
`.demo-preview/legal/drafts/basis-and-changes.md` (not in the repo). This lane built the site side (build items D.1, D.10, D.11 and
the site pins of D.12) on origin/main bd99a24. An app lane builds the in-app side in parallel and owns `site/plain-words.html`,
`PlainWordsTests`, `CloudWarningAndPresetsTests`, `OwnerApprovalsTests` and `src/`; none of them was edited here. Every new or
replaced piece of copy carries `APPROVED by the owner 23/10/2026 09:50 AEDT (legal block): do not reword`; every older APPROVED
comment stays where it was.

1. **`site/terms.html` (new).** The Terms of Use, every heading and sentence of draft 3 in order, in the privacy page's skeleton
   (head, header, `main.wrap.doc`, h1 "Terms of use", "Dictum by Kynni. Version 1, last updated 23/10/2026.", sixteen sections with
   `aria-labelledby`, footer). The draft's italic status line and its square-bracket notes are left out. Section 16 reads "Kynni Pty
   Ltd, Australia. Email hello@kynni.com.au. Dictum's source code and release files are at github.com/kynniau/dictum." until the
   owner supplies the ACN and postal address. Typographic apostrophes and quotation marks, as the site's prose uses; bold runs as in
   the draft; no script. Title "Terms of use · Dictum by Kynni"; canonical https://dictum.kynni.au/terms. The meta description is the
   one sentence this lane wrote ("Dictum's terms of use: what we promise and do not promise for the Dictum app, the Dictum for Mac
   beta and this website."), drawn from section 1. It is not part of the approved text and carries a `PROPOSED 23/10/2026 (meta
   description; owner approval needed)` comment (lens on PR #75). `/terms`
   is served by the `cleanUrls: true` that already serves `/privacy`; `site/vercel.json` is unchanged. Links: /privacy,
   ai.google.dev/gemma/terms (the URL `CleanupCatalog.cs` already uses), hello@kynni.com.au and github.com/kynniau/dictum.
2. **`site/privacy.html` (rewritten from draft 3).** Kept byte for byte, with their APPROVED comments: the scope sentence (07:10),
   the cloud speech heading and paragraph (22:52 and 06:15), the OpenAI-compatible paragraph with "your PC" twice, and the
   website-route sentence (22/10/2026 20:47). The PROPOSED sentence "In hands-free mode, up to ten words of what you said just before
   go with each dictation's text." is added as its own paragraph after the OpenAI-compatible one, because
   `CloudWarningAndPresetsTests` compares that paragraph whole. Not applied: O9 ("your PC" to "your computer") and O10 (the new
   website-route sentence); see item 7. Left out: the bracketed Zoho data-processing sentence. The representative sentence reads "We
   will review that within a year of this policy's date, and sooner if use of Dictum there grows, and say so here." Section 12 reads
   "Kynni Pty Ltd, Australia, www.kynni.com.au." until the ACN and address arrive. "Last updated" is 23/10/2026 (the shell clock).
   Markup: the draft's bold run-in headings in sections 2 and 4 are h3 headings, as the page already had them (the kept cloud speech
   heading and three existing pins need the h3s); section 3's two folder headings are cards with an h3; every other bold run is
   `<strong>`. Links added for the addresses the text names: vercel.com/legal/dpa, vercel.com/legal/privacy-notice, ico.org.uk,
   www.oaic.gov.au (link text "oaic.gov.au" as written) and github.com/kynniau/dictum. Each answered HTTP 200 to curl on 23/10/2026.
   The page's meta description is now "Dictum sends nothing about you to Kynni. What it keeps on your computer, when it connects to the
   internet, and how to delete everything." (was "Dictum collects nothing. What it keeps on your PC, ..."), under the same PROPOSED
   comment (lens on PR #75). Neither meta description is pinned.
3. **Footers.** `<li><a href="/terms">Terms of use</a></li>` right after the Privacy policy link on `index.html`, `help.html`,
   `privacy.html` and `terms.html`. On `terms.html` it carries `aria-current="page"`, as each page marks its own footer link.
   `plain-words.html` gets it from the app lane.
4. **D.10 lines, old to new.**
   - `index.html` "Free to keep on your PC" card: "No subscription, no word limits, no upsell." to "No subscription, no word limits."
   - `index.html` "Model downloads" card: "Every file is checked against a fingerprint built into Dictum before it is used." to "Every
     file Dictum downloads is checked against a fingerprint built into Dictum before it is used."
   - `index.html` FAQ "What's the licence?", second paragraph: "The speech and tidy-up models are made by other organisations and
     carry their own licences, which Dictum shows when you pick one. The recommended tidy-up model, Qwen2.5 1.5B Instruct, is licensed
     Apache-2.0. Qwen2.5 3B is for non-commercial use only, and Dictum never recommends it." to "The speech and tidy-up models are
     made by other organisations and carry their own licences, listed in the Terms of Use. The recommended tidy-up model, Qwen2.5 1.5B
     Instruct, is licensed Apache-2.0."
   - `help.html` section 7 quote: "We collect nothing. Dictum has no account, no analytics, no telemetry and no crash reporting. We
     never receive your audio or your text." to the policy's new first short-version bullet, word for word.
   Each was checked by grep before and after.
5. **Gates and pins.**
   - `SiteStoreFirstTests`: `terms.html` is in the page list, the footer GitHub-line theory, the "unless" gate, the direct-download
     gate, the anchor routes (`/terms`) and a new per-page neutrality theory that names the page (`PublicCopyNeutralityTests` already
     sweeps every public file).
   - The "unless" gate's "never leaves" and "turned into text on" patterns now match "computer" wherever they matched "PC".
   - Two exemptions, each removing one exact string before its scan, so every other use still fails:
     - the policy's sentence "A clean-up server on your own computer may use http://localhost, which never leaves your computer.",
       in the "unless" gate. It is about a localhost connection, not the voice or the text leaving, so it has no "unless".
     - on privacy.html only, the policy's "in the direct download for Windows and in the Mac beta", in the direct-download gate. It
       is the policy's name for that build, but contains "Download for Windows" when case is ignored. The gate asserts exactly three
       uses (one starts section 5 with a capital "In") and removes only those; the phrase on any other page still fails (lens on
       PR #75: the first version removed "direct download for Windows" on every page).
   - Pins replaced with the new sentences: the brief's :175, :178, :183 and :184, and also :176 and :177. The new text removes the
     sentences those two pinned, so they would fail. :178's old sentence was still on the page; it was replaced as briefed with the
     whole new `.env` sentence.
   - New theory `The_legal_block_sentences_are_on_the_page` pins, word for word:
     - from the Terms: section 4's guarantees paragraph, section 9's A$100 cap sentence, section 14's home-courts bullet and section
       15's Mac paragraph;
     - from the policy: the four short-version bullets, the Australia paragraph and the Zoho sentence;
     - the three new `index.html` lines.
   - `The_terms_never_exclude_the_laws_remedies` pins the Australian Consumer Law, UK device-damage and product-liability bullets
     inside the "We never exclude or limit liability for:" list itself.
   - `The_lines_the_legal_block_replaced_are_gone` checks that the replaced lines have left the page that had them.
   - `Every_page_links_to_the_terms_from_its_footer_after_the_privacy_policy` checks the four pages above.
   - `UpdateCheckTests`: the short-version bullet constant is replaced with the new bullet. `PrivacySiteSentence` is unchanged (O10
     waits).
   - `HelpPageTests.The_site_guide_quotes_the_privacy_policys_short_version_word_for_word` ties the guide's quote to the policy's
     first bullet.
   - New `OutboundHostsTests` (3 tests): it reads the hosts from `ModelCatalog.DownloadHost`, `UpdateCheck.VersionUrl`,
     `AnthropicLlmClient.DefaultBaseUrl` (the brief named the first, second and fourth groups; Anthropic's default address lives
     here, not in `CloudPresets`) and every clean-up and speech preset off this computer (Azure as its `.openai.azure.com` suffix).
     It asserts:
     - the hosts equal huggingface.co, dictum.kynni.au, api.anthropic.com, api.openai.com, generativelanguage.googleapis.com,
       api.groq.com and openai.azure.com;
     - each is named on `privacy.html`;
     - every host-like name on the page is one of those or on a short list of names the page gives the reader: us.aws.cdn.hf.co,
       github.com, www.kynni.com.au, vercel.com, ico.org.uk, oaic.gov.au, www.oaic.gov.au and nspasteboard.org.
   - New `StoredFilesPolicyTests` (3 tests): it calls `PathRegistry.Locations` with a scratch profile it never creates, the app
     folders aside. It asserts that the registry's names equal a table that gives each name the policy's word for it ("Working
     files" for the three `.tmp` saves, the folder for files inside calibration, logs and models), and that each word appears on
     `privacy.html` as code. The reverse test (lens on PR #75) asserts that every .json, .txt or .env name the storage section writes
     as code is a name the registry stores.
6. **Mutations, observed failing on 23/10/2026, each restored and re-checked byte for byte.**
   - (a) terms.html section 9 cap sentence deleted.
   - (b) the cap changed to "A$1,000 (one thousand Australian dollars)".
   - (c) section 4's "Section 9 does not reduce any such guarantee, or what the law entitles you to if we fail to meet one."
     deleted.
   - (a) to (c) each failed `The_legal_block_sentences_are_on_the_page` for its sentence.
   - (d) the old help quote put back: the twin test and the gone test failed.
   - (e) `api.groq.com` taken off privacy.html: the hosts-named test (and the cloud speech pin) failed.
   - (f) a fake host, api.example-cloud.com, added to privacy.html: the no-other-host test failed.
   - (g) `snippets.txt` taken off privacy.html: the stored-files test failed.
   - (h) a fake name added to the stored-files table: the registry test failed.
   - (i) api.groq.com taken out of the host list: the host-list test failed.
   - (j) "Your voice never leaves your computer." and, separately, "Your speech is turned into text on your computer." added to
     terms.html: the extended "unless" gate failed.
   - (k) with the localhost exemption removed, the gate failed on privacy.html.
   - (l) "Download for Windows." added to terms.html: the direct-download gate failed.
   - (m) with the direct-download exemption removed, the gate failed on privacy.html.
   - (n) the help.html footer Terms link removed: the footer test failed.
   - (o) the policy's online bullet put back to the 22/10/2026 text: `UpdateCheckTests.The_privacy_policy_says_when_Dictum_reads_dictum_kynni_au`
     and two pins failed.
7. **Deferred to a follow-up PR after both lanes merge.**
   - O9 ("your PC" to "your computer" in the OpenAI-compatible paragraph). It is pinned whole in `CloudWarningAndPresetsTests`,
     which the app lane owns.
   - O10 (the website-route sentence that adds the Terms of Use and the Mac beta). The same sentence is pinned in
     `UpdateCheckTests` (privacy page and the `index.html` dictum.kynni.au card) and in `PlainWordsTests`, so one PR must change
     all three after both lanes merge.
   - plain-words.html's footer link and its row in the new footer test go with the follow-up too.
8. **Publication order (D.0).**
   - `/terms`, the footer links to it and the `index.html` licence line ("listed in the Terms of Use") merge to main with this PR.
     They must not be deployed until the first release that contains both the in-app acceptance gate (build item 2) and the
     catalogue change that removes qwen2.5-3b (build item 9); until then Terms section 3 is false for every installed copy, and
     Terms section 7 and the index.html licence line are false for qwen2.5-3b. privacy.html is not deployed before the O10
     follow-up merges. At deploy, terms.html:36 (the date line) carries the publication date. The Mac release that ships PR #74
     removes privacy.html's Universal Clipboard sentence and Terms section 11's clipboard clause (build item 16) in the same deploy.
   - The orchestrator deploys the site, so every site deploy from main after this merge waits for that release, not only
     `/terms`. Alternatively, deploy from an earlier commit.
   - D.0 lets the privacy policy go live earlier only without its Terms link. The rewritten `privacy.html` here carries the footer
     Terms link, so an earlier policy deploy needs that link taken out first.
9. **Open, for the owner.**
   - The ACN and postal address (Terms section 16, Policy section 12).
   - The two PROPOSED meta descriptions above.
   - `help.html` and `index.html` still say the policy "is short"; it is now twelve sections. Not in the brief, so unchanged.
10. **Gates, on this branch (23/10/2026).**
    - Release build: 0 errors and the 5 xUnit2013 warnings main already had (MacPackagingTests.cs, and SiteStoreFirstTests.cs's
      landing-page button test, untouched here).
    - Full suite (`dotnet test --solution Dictum.sln -c Release`, without `DICTUM_UI_TESTS`): 2669 total, 0 failed, 2628 passed,
      41 skipped. The entry above recorded 2629 on PR #73's head (Debug), and this lane adds 40 tests (2629 + 40 = 2669); main itself was not re-run here. It ran on the final tree before these
      counts were written into this file; `PublicCopyNeutralityTests` and the site tests were run again afterwards.
    - `site/terms.html` and `site/privacy.html` pass a tag-balance, duplicate-id and `aria-labelledby` check. Every href on them
      is a site path, the mailto address, or one of: dictum.kynni.au, github.com/kynniau/dictum, www.kynni.com.au,
      ai.google.dev/gemma/terms, vercel.com/legal/dpa, vercel.com/legal/privacy-notice, ico.org.uk and www.oaic.gov.au.
    - Not verified: the pages were not rendered in a browser, and nothing ran on a Mac.
11. **Lens on PR #75 (a2ce283, Needs changes, no blockers), fixed on this branch.**
    - The direct-download exemption now applies to privacy.html only, and to exactly three uses of "in the direct download for
      Windows and in the Mac beta" (item 5). The lens's own code asked for that exact lowercase string three times. The page has it
      twice, plus once with a capital "In" starting section 5, so the gate matches "[Ii]n".
    - `StoredFilesPolicyTests` gains the reverse direction (item 5).
    - The per-page neutrality theory calls `PublicCopyNeutralityTests.Offences` (already internal), so it is the sweep's rule, not a copy.
    - The never-exclude bullets test asserts that each bullet appears exactly once on the page.
    - Both meta descriptions are PROPOSED (items 1 and 2).
    - The first publication-order bullet (item 8) is replaced as the lens wrote it. Two changes: the opening sentence naming what
      merges is kept, because the lens's text begins "They"; and the date line is cited as terms.html:36, because the PROPOSED
      comment moved it down from 35.
    - Mutations, observed failing on 23/10/2026, each restored:
      - "Prefer the direct download for Windows? It is on GitHub" added to index.html;
      - "Get the direct download for Windows from our GitHub releases." added to help.html;
      - a fourth use of the policy's phrase added to privacy.html: the count assertion fails every row;
      - with the exemption's removal line deleted, the gate failed on privacy.html;
      - a fake `history.json` item added to the storage list: the reverse test failed;
      - the Australian Consumer Law bullet copied into the not-liable list: the never-exclude test failed;
      - a banned word added to terms.html: the per-page neutrality theory failed.
    - Gates after these fixes (23/10/2026):
      - Release build: 0 errors, the same 5 xUnit2013 warnings.
      - Full suite (`dotnet test --solution Dictum.sln -c Release`): 2670 total, 0 failed, 2629 passed, 41 skipped. That is one more
        test than item 10: the reverse stored-files test. It ran before this line was written.
      - `PublicCopyNeutralityTests` and the site tests were run again afterwards.

## Legal block, app side (23/10/2026 12:34 AEDT, branch legal/app; first written 11:40 AEDT, lens fix round 12:34 AEDT)

The owner approved, at 09:50 AEDT on 23/10/2026, draft 3 of the Terms of Use and the Privacy Policy, the approved-copy changes
O1 to O15 and the build tasks of `.demo-preview/legal/drafts/basis-and-changes.md` (sections C and D). This entry is the app
lane's part. The site lane builds `site/terms.html` and the new `site/privacy.html` in parallel.

1. **Acceptance gate (section D item 2; O12, O14).**
   - The Welcome window's first step has **Continue** as its button. Beside it, in the button row, it says "By clicking
     Continue you agree to Dictum's Terms of Use." with "Terms of Use" a link to https://dictum.kynni.au/terms, and below that
     "The Privacy Policy explains what Dictum keeps and sends." with "Privacy Policy" a link to https://dictum.kynni.au/privacy.
     Both links open through `IPlatform.Open`, as Settings opens pages. The notice sits in the button row, not the scrolling
     body, so it never scrolls away from Continue on a small screen. Each row reaches UI Automation as a group named with
     its whole sentence, and on this step Continue's help text is the acceptance sentence (items 16 and 17). At ca15ede this
     line said the rows were named, but nothing was set. At 5f878a6 the name was set, but a row (a WrapPanel) has Avalonia's
     NoneAutomationPeer, so only Continue's help text reached UI Automation.
   - The words are `TermsText` constants in `src/Dictum.Core/Help/TermsText.cs`, which also holds the one Terms version,
     `TermsText.Version = "1"`.
   - **When it shows:** at every start until `settings.json` records `terms.accepted` = the version in force and
     `terms.accepted_by` = the account name Dictum runs as (`Environment.UserName`), compared exactly (`AppRules.AsksTerms`).
     A new install sees it as step 1 of the whole window. Any other copy without that record sees the window with that step
     alone: an upgrade from 1.3.0 or earlier, an administrator's seeded file, a launch with `--model` or
     `DICTUM_MODEL_PATH`. An unreadable `settings.json` asks too, because what it records is unknown.
   - An acceptance recorded for another account name does not count.
   - **Terms step alone:** no step line and no "next few steps" line; Continue records the acceptance, closes the window and
     Dictum starts on the settings it had.
   - **Closing before Continue quits.** Esc or the close box on the first step, before Continue, quits Dictum. Nothing is
     written: the old `SkipRest` would have written the speech answer, so a person who closed at step 1 would next have seen
     only the Terms step.
   - **Recording.** One write of the `terms` object (both keys) through `SettingsFile.SetValue`, every other key kept. A new
     install has no `settings.json` at Continue, so its record is written right after the first-run answer creates the file
     (keeping the file's documented layout with `_doc`). A model-override launch with no file gets it after
     `Models.FirstRun`. If the file cannot be written, Dictum goes on this time, logs a warning and asks again next start.
   - The settings loader knows `terms.accepted` and `terms.accepted_by` (no "unknown setting" warning). README's settings
     reference lists both.
   - **Window size.** At 720 × 500 the first step measured 315 of 278 px (the notice in the button row, and the longer O15
     sentence). `OnboardingWindow.WindowHeight` is now 560. `Fit` still clamps it to the work area: 1366 × 768 at 150 % was
     already cut. At 125 % the body probably scrolls now; that is inferred from the recalled 48 DIP taskbar and 32 DIP caption
     and was not measured.
   - `PlainWordsText`'s "No acceptance gate goes with it (section 2c)" note is replaced. PlainWordsTests'
     `The_welcome_step_shows_the_short_version_and_asks_for_no_acceptance` becomes `The_welcome_step_shows_the_short_version`.
     The acceptance pins are in the new `TermsAcceptanceTests`.
2. **Settings > About (D.3):** "Terms of use" after "Privacy policy" (opens https://dictum.kynni.au/terms) and "Third-party
   notices" (opens the shipped `THIRD-PARTY-NOTICES.txt` through `IPlatform.Open`: `AppPaths.ThirdPartyNoticesFile`, beside
   Dictum.exe on Windows, in Dictum.app's `Contents/Resources` on a Mac). Labels PROPOSED 23/10/2026. The Support card's
   buttons now sit in a wrap panel, since five may not fit one row.
3. **Settings > Privacy (D.4):** the Offline only description is now "No network at all: downloads, Check for updates, cloud
   clean-up and cloud speech are refused; a server on this PC still works." (PROPOSED 23/10/2026).
   `LanguageUiTests.The_clean_up_page_says_a_server_on_this_PC_tidies_each_dictation_only_while_it_does` exempts that one line
   from its "no line names a server on this PC" check.
4. **Approved copy changed (section C), each with the new marker:**
   - O2 and O15 in `PlainWordsText`.
   - O1 to O6 and the footer Terms link on `site/plain-words.html`. The O6 line sits before "Questions:", which is a placement
     choice; its words are approved.
   - O7 and O8 in the two consent texts.
   - O11 in `docs/STORE.md`.
   - O13 (a per-provider sentence in the dialogs) is not built, on the owner's word.
5. **Installer (D.5):** `packaging/TERMS.txt` is the Terms draft 3 as plain text. It drops the italic status line, the
   bracketed notes and the Markdown syntax, and keeps every sentence and heading.
   - The heading line "Version 1, [date of publication]." reads "Version 1." with the bracket dropped.
   - §16 reads as the brief gives it until the ACN and postal address are supplied.
   - `Dictum.iss` gains `LicenseFile=TERMS.txt`. ISCC 7 compiled the script with a dummy payload ("Reading file
     (LicenseFile)", successful compile). Setup itself was not run.
6. **Catalogue (D.9):** qwen2.5-3b removed from `CleanupCatalog`, `scripts/download-model.ps1` and the README tables.
   - The Gemma entries' marker, and the download note at `ModelCoordinator.SelectCleanup`, name "Gemma Terms of Use apply
     (ai.google.dev/gemma/terms)" (PROPOSED), through `CleanupModelInfo.LicenceNote`.
   - The two Gemma models are login-gated, so they never reach that download note: the balloon they get, "requires a Hugging
     Face login", is unchanged.
   - A settings.json with `llm.model` = `qwen2.5-3b` now gets the recommendation with a log line
     (`EffectiveSettings.cs:34-38`), as for any unknown name.
7. **TRADEMARKS.md (D.7)** with Terms §8's first three sentences; README links it under its licence line; LICENSE unchanged.
8. **THIRD-PARTY-NOTICES.txt (D.8):**
   - "Copyright (upstream licence):" lines sit under the .nuspec lines: llama.cpp in the three LLamaSharp.Backend entries,
     LLamaSharp, NAudio.Core and .Wasapi, and the six Avalonia packages.
   - Each line was checked against the project's LICENSE on 23/10/2026 (raw.githubusercontent.com), at the tag of the version
     shipped where there is one: Avalonia "Copyright (c) AvaloniaUI OÜ" at 11.3.22 (followed upstream by "All Rights
     Reserved"); LLamaSharp "Copyright (c) 2025 SciSharp STACK" at v0.27.0; NAudio "Copyright 2008-2026 Mark Heath" at
     v3.1.0 and on main; llama.cpp "Copyright (c) 2023-2026 The ggml authors" on master.
   - Corrected in the lens round: the first version gave NAudio "Copyright 2020 Mark Heath", read from NAudio's stale
     `master` branch (its default branch is `main`; the 2020 line is release/2.x's).
9. **SECURITY.md (D.13):** current release 1.3.0 (23/10/2026), and the guarantees list gains cloud speech: the consent-gated
   host, and "unless you turn on cloud speech".
10. **README:**
    - The download-script line says the script is in the source code, not the direct download.
    - The install section has the Terms line.
    - The Privacy section's "Nothing leaves the machine by default." paragraph gains Terms §11's clipboard sentence.
    - The first-run section describes the gate.
11. **Pins and mutations.** Each pin was observed failing on the reverted or broken behaviour (23/10/2026, Debug build, the classes named), then the
    file was restored byte for byte:
    - (M1) The Terms step alone switched off in `AppShell.Start` (the gate as before this branch: upgrades, seeded files and
      model overrides never asked). Four `TermsAcceptanceTests` failed:
      - `Closing_the_window_before_Continue_quits_and_writes_nothing`: "Dictum started after the window was closed before
        Continue ({ "whisper": … })".
      - `An_upgrade_sees_the_Terms_step_alone_and_Continue_starts_Dictum` and `An_acceptance_by_another_account_does_not_count`:
        "Assert.Equal() Failure: Values differ, Expected: 1, Actual: 0" (`TermsWindows`).
      - `Continue_records_the_version_and_the_account`: "Expected: … [["accepted"] = "1", ["accepted_by"] = <this account>], Actual:
        null".
    - (M2a) The quit after the whole window, closed before Continue, removed: `Closing_the_window_…` failed with "Dictum
      started after the window was closed before Continue (new install)".
    - (M2b) The same for the Terms step alone: the same test failed with "… ({ "whisper": … })".
    - (M24) `SkipRest` writing the speech answer before Continue: the same test failed with "a new install closed on its
      first step wrote settings.json".
    - (M3) The account comparison removed from `AppRules.AsksTerms`:
      - `An_acceptance_by_another_account_does_not_count` failed with "Expected: 1, Actual: 0".
      - `The_step_shows_until_this_account_has_accepted_the_version_in_force` failed with "Assert.True() Failure".
    - (M4) `TermsText.Acceptance` put back to the 08:48 D1 wording ("… and confirm you have read its Privacy Policy."):
      `The_first_step_says_the_approved_sentences_beside_Continue_and_links_both_pages` failed with "Collections differ at index
      0 … Actual: ··· to Dictum's Terms of Use and confirm you have rea···".
    - (M22) The button label put back to "Next": that test and the upgrade test failed with "Expected: Continue, Actual: Next".
    - (M5) The privacy link opening the Terms page: that test failed with "Collections differ at index 1, Expected:
      https://dictum.kynni.au/privacy, Actual: https://dictum.kynni.au/terms".
    - (M6) `terms.accepted_by` not written. Three tests failed:
      - `Continue_records_…` and the upgrade test: "Expected: [["accepted"] = "1", ["accepted_by"] = <this account>], Actual:
        [["accepted"] = "1"]".
      - The other-account test: "KeyNotFoundException: The given key 'accepted_by' was not present".
    - (M7) A new install's record not written with the first-run answer: `Continue_records_…` failed with "Assert.True()
      Failure" (`TermsRecorded`).
    - (M20) Continue on the Terms step alone not ending the window: the upgrade test failed with "Assert.False() Failure"
      (`window.IsVisible`).
    - (M8) Settings > About's "Terms of use" removed: `Settings_About_opens_the_Terms_and_the_third_party_notices` failed with
      "Collections differ at index 1, Expected: Terms of use, Actual: Third-party notices".
    - (M23) The notices file looked for only beside the program: `The_notices_file_is_found_beside_the_program_or_in_the_app_bundle`
      failed ("Expected: ···Dictum.app\Contents\Resources\THIRD-PARTY-NOT···").
    - (M9) The old Offline only description: `Settings_Privacy_says_what_Offline_only_refuses` failed with "Assert.Contains()
      Failure: Item not found in collection".
    - (M10) TERMS.txt's cap sentence shortened to "is A$100.": `Setup_shows_the_Terms_with_the_cap_and_the_guarantees_word_for_word`
      failed with "Assert.Contains() Failure: Sub-string not found".
    - (M21) `LicenseFile=TERMS.txt` removed from `Dictum.iss`: the same test failed with "Assert.Contains() Failure: Sub-string
      not found".
    - (M11) qwen2.5-3b put back in the catalogue:
      - `Catalogue_matches_the_spec_sizes_hashes_and_licences` failed with "Collections differ at index 0, Expected:
        qwen2.5-0.5b, Actual: qwen2.5-3b".
      - `No_catalogue_entry_is_non_commercial_and_the_Gemma_entries_name_their_terms` failed with "Assert.All() Failure: 1 out
        of 6 items in the collection did not pass".
    - (M12) The Gemma note dropped from `Marker`:
      - `Catalogue_matches_…` failed with "Strings differ … Expected: ···login · size-checked only · Gemma Terms of Use
        app···".
      - The non-commercial test failed with "Assert.All() Failure: 2 out of 2 items … Assert.EndsWith() Failure".
    - (M13) LLamaSharp's upstream line removed from THIRD-PARTY-NOTICES.txt:
      `The_upstream_copyright_line_sits_beside_the_nuspec_line(package: "LLamaSharp", …)` failed with "Assert.Matches()
      Failure: Pattern not found in value".
    - (M14) `PlainWordsText.OpenSource` with "It is provided as is, without warranty." put back, failing at "Collections
      differ at index 1 … Expected: ···e under the MIT licence. We built it with care, bu··· Actual: ···e under the MIT
      licence. It is provided as is, wit···". Four tests failed:
      - `The_short_version_is_pinned_word_for_word`, `The_about_page_ends_with_the_plain_words_card` and
        `The_welcome_step_shows_the_short_version`.
      - `OwnerApprovalsTests.The_plain_words_short_version_is_pinned`.
    - (M15) `PlainWordsText.Cloud`'s first sentence put back: the same four failed at "Expected: "Dictum itself sends nothing
      you say off your compu"··· Actual: "Nothing you say leaves this PC unless you switch o"···".
    - (M16) O7's sentence removed from the clean-up consent:
      - `OwnerApprovalsTests.The_clean_up_consent_text_is_the_approved_text_byte_for_byte` failed with "Collections differ …
        (pos 287)".
      - The three `CloudWarningAndPresetsTests.The_consent_text_names_the_key_that_goes_with_the_text` rows failed with
        "Sub-string not found".
    - (M17) O8's sentence removed from the speech consent:
      - `CloudSpeechTests.The_consent_text_is_the_approved_text_byte_for_byte` failed with "Expected: ···l dictionary as a
        hint. In hands-free mode, the cl··· Actual: ···l dictionary as a hint. It uses your own API key (···".
      - `CloudSpeechUiTests.Declining_the_speech_consent_step_records_nothing_and_sends_nothing` failed with "Collections
        differ … (pos 204)".
    - (M18) STORE.md's sentence put back to "provided as is": `The_store_listing_carries_the_approved_sentence` failed with
      "Assert.EndsWith() Failure … Expected end: " Dictum is free and open source under the MIT lice"···".
    - (M19) A line added to plain-words.html: `The_plain_words_page_is_the_approved_statement_word_for_word` failed with
      "Collections differ at index 17 … Actual: We cannot pay for a loss it causes.".
12. **Left to the site lane, and follow-up.** The site lane owns `site/terms.html`, `site/privacy.html`, `site/index.html`,
    `site/help.html`, `site/styles.css` and the pins in `SiteStoreFirstTests`, `HelpPageTests` and `UpdateCheckTests`.
    - **O9** ("your PC" → "your computer" twice in the privacy policy's OpenAI-compatible paragraph) and **O10** (the
      website-route sentence) change `site/privacy.html`. They are pinned by tests this lane owns:
      `CloudWarningAndPresetsTests.The_privacy_policy_carries_the_approved_OpenAI_compatible_wording_word_for_word` (its
      `ApprovedOpenAiCompatibleParagraph` and `ApprovedLocalServerClause`) and
      `PlainWordsTests.The_privacy_policy_and_the_readme_name_the_plain_words_route`. They are also pinned by
      `UpdateCheckTests.cs:44`, which the site lane owns.
    - This branch leaves those pins on today's `site/privacy.html`. Whichever PR merges second updates them to the new
      sentences in the same change.
    - The README's own route row (`| Only when you open the privacy policy, Dictum in plain words …`) gets the Terms page
      added then.
13. **Publication order (section D item 0).**
    - /terms, the footer links to it, the installer licence page and Partner Center's "Additional license terms" go live with
      the first release that contains this gate, never before.
    - Until then Terms section 3 ("Dictum still asks you to accept them the first time it starts") is false for every
      installed copy.
    - This branch puts the installer licence page and the plain-words footer link in the same release as the gate.
      `docs/STORE.md` notes the Partner Center step for the owner or orchestrator.
    - The privacy policy can go live earlier, provided its Terms link comes only with the Terms.
14. **Gates** (this branch, 23/10/2026).
    - `dotnet test Dictum.sln` (Debug, 11:41 to 11:49 AEDT): 2653 total, 2612 passed, 0 failed, 41 skipped. The skipped
      tests are gated by environment variables, as on main.
    - The same run on main bd99a24, in a separate worktree before this branch: 2629 total, 2588 passed, 0 failed, 41 skipped.
    - Release build `--no-incremental`: 0 errors and 5 warnings, all xUnit2013 in `MacPackagingTests.cs` and
      `SiteStoreFirstTests.cs`, which this branch does not touch.
    - `PublicCopyNeutralityTests` is within that run, and was run again on its own after this entry was finished.
    - ISCC 7 compiled `Dictum.iss` with a dummy payload.
    - **Not run:**
      - the installed app, and tray mode, which is never run on this host;
      - the 41 gated tests (desktop, tray, audio, published layout);
      - anything on a Mac;
      - Setup itself.
    - The Welcome step was rendered headless (720 × 560, and 480 wide) and looked at: the notice sits beside Continue, each
      sentence on one row at the default size.
15. **Open points for the owner** (not changed here, because no approved wording covers them).
    - **"Press Next to begin."** The Welcome window's first step still shows `OnboardingText.WelcomeNext` ("The next few
      steps take a minute or two. Press Next to begin.", approved 22/10/2026), but its button is now **Continue**. The
      sentence needs the owner's words. It is hidden on the Terms step alone.
    - **O15 on plain-words.html:44.** Section C row O15 names `site/plain-words.html:44` as well as `PlainWordsText`. This
      lane's brief limited the page to rows O1 to O6 and kept every other line byte for byte, so line 44 still says
      "Nothing you say leaves your PC unless you switch on a cloud option and agree to it.". If O15 is meant there too, it is
      one line plus its pin.
    - **"as it is" remains.** The page keeps "Dictum is free and we give it to you as it is." before the O1 sentences: O1
      and O2 did not touch it.
    - **SPEC is stale.** `docs/SPEC.md` §27 item 1 ("one paragraph, then Next") predates the gate. This lane does not own
      SPEC.
    - **The account name is stored.** `settings.json` now records the account name that accepted the Terms
      (`terms.accepted_by`). The privacy policy's file list describes `settings.json` as "your settings"; whether it should
      name the account too is for the site lane and the owner.
    - **No date in the installer's Terms.** `packaging/TERMS.txt` has no publication date: its heading line reads "Version
      1." until the owner sets the date of publication.
    - **Store version not checked.** SECURITY.md says the Store carries a release "once Microsoft has certified it". The
      Store's current version was not checked on 23/10/2026; GitHub's latest release is 1.3.0, published
      2026-10-22T15:31:44Z (`gh release list`), which is 23/10/2026 in Sydney.
16. **Lens round on ca15ede (verdict Needs changes, no Block; fixed by 12:34 AEDT).** The lens observed all 8 of its mutations
    failing, found TERMS.txt byte-faithful and every approved string byte-exact. Fixed on this branch with ordinary commits,
    after the merge of main 5e5e9ba (PR #75, the site lane: its DECISIONS entry sits before this one):
    - **O15 on the page.** It now also applies to `site/plain-words.html` ("Dictum itself sends nothing you say off your
      computer unless you switch on a cloud option and agree to it. Dictum tells you what would be sent, and to where, before
      you agree.", with the new marker). The page pin and the marker count (7) follow. Item 15's O15 point is resolved.
    - **Accessibility.** The Continue button's help text is the acceptance sentence on step 1 and none on the later steps, so
      a screen-reader user hears the sentence with the button. Each notice row was given its sentence as its name, but at
      5f878a6 that did not reach UI Automation: the re-lens found the row's peer is a NoneAutomationPeer. Item 17 fixes it,
      and the pin now reads the peer.
    - **NAudio.** NAudio's upstream line is "Copyright 2008-2026 Mark Heath" (item 8, corrected there).
    - **Store notes.** Row 17 of `docs/STORE.md` gains a PROPOSED sentence about the Continue step (the owner approves it
      before the next submission). At 5f878a6 the approved TO DICTATE tester sentence was also rewritten in place; item 17
      restored it.
    - **Demo scripts.** Nine of `scripts/demos/*.json` click the Welcome window first, and that first click now targets
      Continue. `dictation-stills.json` has no Welcome click, and `install.json` only points at the Welcome window at its end.
      - `first-run.json`'s caption is now "Dictum explains how it works and links its Terms of Use. Click Continue."
        (PROPOSED).
      - `install.json` gains the Setup licence step: "Setup shows Dictum's Terms of Use. Choose I accept the agreement, then
        click Next." (PROPOSED), clicking the "I accept the agreement" radio button and Next.
      - JSON carries no markers, so the PROPOSED status is recorded here.
      - Not run: the demos need Windows Sandbox and a built Setup.
      - Its next step's caption still says "Setup opens. Click Next." although Setup now opens on the licence page; that
        caption is not in the lens list and is left for the owner.
    - **Command-line modes (owner's decision).** `--transcribe` and `--bench-cleanup` run only once settings.json records
      this account's acceptance (`AppRules.AsksTerms`, the tray app's rule). Otherwise they print "Start Dictum once and
      press Continue to accept its Terms of Use." (PROPOSED, `CliCommands.TermsNotAccepted`) on stderr, log one line and
      exit 1. The check sits in Core (`CliCommands.RefusedWithoutTerms`), so the Windows and Mac entry points share it; it
      runs after the unreadable-settings check, whose message is the more useful one for a broken file.
      - `--selftest`, `--download-model`, `--download-cleanup-model` and `--print-models-dir` are administrator and CI modes
        and are not gated.
      - The README's command-line section says so.
      - Three existing tests that run the two gated modes now record the acceptance first (`TestTerms`):
        `ModelFolderSafetyTests.Bench_cleanup_looks_in_every_model_folder…`,
        `FixBatchDownloadTests.Bench_cleanup_reports_an_unreadable_wav_and_exits_1`, and the gated
        `AmendmentTests.Transcribe_exits_1_on_an_unreadable_settings_file`.
    - **Markers.** The README Terms line and TRADEMARKS.md's "See the Terms of Use…" line are PROPOSED 23/10/2026; neither
      is in section C or D.
    - **"Press Next to begin."** `OnboardingText.WelcomeNext` keeps its approved text, with the comment `// PROPOSED
      23/10/2026: "The next few steps take a minute or two. Press Continue to begin." (owner decision pending)`.
    - **Welcome step spacing.** The step's body spacing is 12 (was 14), because the lens measured a new install's step 1 2
      DIP over at 1366 × 768 at 125 %. Measured headless after the change, with the fitted 534.4 DIP client (the recalled
      48 DIP taskbar and 32 DIP caption): 311 of 313 DIP.
    - **Fixture names.** The rule test uses "alex" and "ALEX".
    - **One write attempt.** The acceptance write is tried once (`OnboardingFlow._recordTried`): again later only when
      settings.json did not exist at Continue. At ca15ede a refused write warned twice.
      - New pin: `A_settings_file_that_cannot_record_the_acceptance_starts_warns_once_and_asks_again`, read-only and
        unparseable.
      - In both cases Dictum starts, the Terms are accepted but not recorded, the file is unchanged, the warning is logged
        once and the next start asks again.
    - **SECURITY.md** names 1.3.0 as live on GitHub and in the Store. The orchestrator verified the Store in Partner Center
      (live since about 08:30 AEDT 23/10/2026); this lane did not check it. Item 15's Store point is resolved.
    - **SPEC.**
      - `docs/SPEC.md`'s clean-up list drops qwen2.5-3b (with a note), and its licence and recommendation rules follow.
      - §27 step 1 describes Continue, the acceptance record, the Terms step alone and the command-line gate.
      - Item 15's SPEC point is resolved.
    - **README leftover file.** README says a downloaded `qwen2.5-3b-instruct-q4_k_m.gguf` stays where it is, because
      Settings > Storage lists catalogue files only (`ModelFiles.IsModelFile`) and Dictum deletes nothing else.
    - **Release-notes line.** PROPOSED 23/10/2026, for the notes of the release that carries this change. It was not added
      to `packaging/release-notes-preamble.md`, which CI publishes as it is: "qwen2.5-3b is no longer on Dictum's list of
      clean-up models, because its licence allows non-commercial use only. If you chose it, Dictum now uses the recommended
      clean-up model instead (the rule-based tidy until that is downloaded). Its 2.1 GB file stays in your models folder and
      Settings > Storage no longer lists it: delete qwen2.5-3b-instruct-q4_k_m.gguf yourself to free the space."
    - **New pins, each observed failing on the reverted behaviour (23/10/2026):**
      - (M25) Continue's help text not set: `A_screen_reader_hears_the_acceptance_sentence_with_Continue` failed with
        "Strings differ, Expected: "By clicking Continue you agree to Dictum's Terms o"···, Actual: null".
      - (M26) The rows' names not set: the same test failed with "Collections differ … (pos 0)".
      - (M27) The single-try guard removed: both rows of `A_settings_file_that_cannot_record_the_acceptance…` failed with
        "Assert.Single() Failure: The collection contained 2 matching items".
      - (M28) The `--transcribe` gate removed: `Transcribe_and_bench_cleanup_refuse_until_the_Terms_are_accepted` failed with
        "Assert.Contains() Failure: Filter not matched in collection".
      - (M29) The `--bench-cleanup` gate removed: the same test failed the same way.
      - (M30) O15 reverted on the page: `The_plain_words_page_is_the_approved_statement_word_for_word` failed with
        "Collections differ at index 6".
      - (M31) NAudio.Core's line put back to "Copyright 2020 Mark Heath":
        `The_upstream_copyright_line_sits_beside_the_nuspec_line(package: "NAudio.Core", …)` failed with "Assert.Matches()
        Failure: Pattern not found in value".
    - **Gates for this round:**
      - `dotnet test Dictum.sln` (Debug, 12:27 to 12:34 AEDT, on the tree committed here): 2698 total, 2657 passed, 0
        failed, 41 skipped.
      - Release build `--no-incremental`: 0 errors and 5 warnings, all xUnit2013 in `MacPackagingTests.cs` and
        `SiteStoreFirstTests.cs` (not touched here).
      - `PublicCopyNeutralityTests` was run again after this text.
      - **Not run:** the demos, Setup, the installed app, tray mode, a Mac, and the 41 gated tests.
17. **Re-lens on 5f878a6 (verdict Block; fixed by 13:06 AEDT).**
    - **B1. Approved copy changed in place: restored.** The TO DICTATE tester sentence in `docs/STORE.md` is back to its text
      on origin/main, byte for byte. Approved copy is changed only by the owner's yes, never in place by a lane or on an
      orchestrator's instruction. The proposal sits beside it, after the 1.2.1 notes block: "replace TO DICTATE's opening,
      up to "then click into any text field", with "on the first start Dictum opens a Welcome window. Click Continue on its
      first step (this accepts Dictum's Terms of Use; closing the window or pressing Esc on that step quits Dictum), then
      click Next through the other steps (or press Esc to skip them and keep the defaults), then"." (PROPOSED 23/10/2026;
      the owner approves it before the next submission).
    - **M1. The rows now reach UI Automation.** Each notice row (a WrapPanel) keeps its sentence as its name and is now set to
      `AccessibilityView.Content` with the control type `Group`, so its peer is a named control element.
      - `A_screen_reader_hears_the_acceptance_sentence_with_Continue` now reads the automation peers
        (`ControlAutomationPeer.CreatePeerForElement`), not the attached properties. For each row it checks the peer is a
        control element, that its name is the sentence and that its type is Group. For Continue it checks the peer's help
        text.
    - **m1. The refusal's stderr line is pinned.** `CliCommands.RefusedWithoutTerms` takes `TextWriter? error = null`,
      the `PrintModelsDir` pattern. The pin reads it through a StringWriter: exactly the PROPOSED line and a new line when
      refused, nothing when the Terms are accepted.
    - **m2. DEMOS.md.** `docs/DEMOS.md`'s PROPOSED captions:
      - Install gains step 5, "Setup shows Dictum's Terms of Use. Choose I accept the agreement, then click Next.", and is
        renumbered; its online-only note now says the video counts 8 steps.
      - First-run step 4 is "Dictum explains how it works and links its Terms of Use. Click Continue.".
      - The welcome-defaults sentence lists Continue first.
    - **Observed failing (23/10/2026), each restored:**
      - The two setters removed: the pin failed with "the row "By clicking Continue you agree to Dictum's Terms of Use." is
        not in the automation tree".
      - `SetAccessibilityView` alone removed: the same failure.
      - `SetControlTypeOverride` alone removed: the pin first passed, so it gained the Group check, after which it failed
        with "Expected: Group, Actual: None".
      - The refusal's `WriteLine` removed: `Transcribe_and_bench_cleanup_refuse_until_the_Terms_are_accepted` failed with
        "Strings differ, Expected: "Start Dictum once and press Continue to accept its"···".
    - **Gates:**
      - `dotnet test Dictum.sln` (Debug, 12:58 to 13:06 AEDT, on the tree committed here): 2698 total, 2657 passed, 0
        failed, 41 skipped.
      - `PublicCopyNeutralityTests` was run again after this text.
      - **Not run:** a screen reader on a real desktop (the peers were read headless), the demos, Setup, a Mac, and the 41
        gated tests.

## Legal block, follow-up: O9 and O10, footer test, Terms consistency test (23/10/2026 13:30 AEDT, branch legal/followup)

The owner approved section C of the legal block's basis document at 09:50 AEDT on 23/10/2026, including O9 and O10, which were
deferred because their pins sat in both lanes' tests (see "Legal block, site side", item 7). Both lanes are merged (main 7a966ce),
so this one PR applies them. Approved strings change only where the brief said; each changed place carries the marker
`APPROVED by the owner 23/10/2026 09:50 AEDT (legal block): do not reword`.

1. **O9 applied.** `site/privacy.html`, the OpenAI-compatible paragraph: "If the server runs on your PC, for example Ollama at
   localhost, nothing leaves your PC unless …" is now "If the server runs on your computer, for example Ollama at localhost,
   nothing leaves your computer unless …". Nothing else in the paragraph or the page changed (word diff checked).
   - The marker sits above the `<h3>`, not between the heading and the paragraph, because
     `CloudWarningAndPresetsTests.The_privacy_policy_carries_the_approved_OpenAI_compatible_wording_word_for_word` reads the raw
     page and requires `<p>` straight after the heading.
   - Pins: `ApprovedOpenAiCompatibleParagraph` carries the new words. A new `ApprovedPolicyLocalServerClause` is the new clause;
     `ApprovedLocalServerClause` stays as the landing page's card still has it.
     `The_landing_page_says_a_server_on_this_PC_may_forward_the_text` now checks that the paragraph ends with the new clause and
     that the two clauses differ by "your PC" to "your computer" and nothing else.
2. **O10 applied.** `site/privacy.html`, "This website: dictum.kynni.au", the sentence is now exactly: "Only when you open the
   privacy policy, the Terms of Use, Dictum in plain words or the full guide from Dictum, or click Check for updates in the direct
   download for Windows and in the Mac beta, which reads one small file and sends nothing about you; the Store build is updated by
   the Store." It replaces the 22/10/2026 20:47 AEDT sentence ("… the privacy policy, Dictum in plain words or the full guide from
   Settings, or click Check for updates in the direct-download version, …") and its marker.
   - Pins: `UpdateCheckTests.PrivacySiteSentence` (new words); `PlainWordsTests.The_privacy_policy_and_the_readme_name_the_plain_words_route`
     (the whole new sentence, the new marker text, and the old 20:47 marker absent).
   - **The gate count changes from 3 to 4.** `SiteStoreFirstTests.No_page_explains_the_direct_download_or_the_warning` asserts how
     many times `privacy.html` says "in the direct download for Windows and in the Mac beta" (first letter either case) before it
     removes them for the scan. The new sentence is the fourth use, so the assertion is `Equal(4, …)` and its comment says why.
3. **Footer test.** `SiteStoreFirstTests.Every_page_links_to_the_terms_from_its_footer_after_the_privacy_policy` gains
   `plain-words.html` (it has carried the footer link since the app lane). Its summary no longer says "four pages".
4. **Terms consistency test (new).** `tests/Dictum.Tests/TermsTextConsistencyTests.cs` reads `packaging/TERMS.txt` (what Setup shows)
   and the `<main>` of `site/terms.html` and requires the same 86 blocks (every heading, paragraph and list item) in the same order.
   - Ignored: the title and date lines of both, and everything outside `<main>`. Smoothed: markup, character references, typographic
     apostrophes and quotation marks (the page uses ’ “ ”, the text file ' and "), runs of spaces, and the "- " of a list item.
   - It also checks that all sixteen numbered headings were read from both copies, so it cannot pass by reading nothing.
   - **Mutations (observed failing 23/10/2026, each restored):** one word in `TERMS.txt` (section 9, "pay you" to "owe you"):
     failed at block 61 and printed both blocks; the same word changed in `site/terms.html` instead: failed the same way; the
     last line of `TERMS.txt` (section 16's body) deleted: failed with "packaging/TERMS.txt has 85 blocks and site/terms.html has 86".
5. **Other mutations (observed failing 23/10/2026, each restored):** the second "your computer" of O9 put back to "your PC":
   `The_privacy_policy_carries_the_approved_OpenAI_compatible_wording_word_for_word` failed; the O10 sentence put back to the
   22/10/2026 text: the gate, `PlainWordsTests…plain_words_route` and `UpdateCheckTests.The_privacy_policy_says_when_Dictum_reads_dictum_kynni_au`
   failed; the O10 sentence reworded so only three uses remain, and a fifth use added: the gate failed both ways; the
   `plain-words.html` footer Terms link removed: the footer test failed for that page.
6. **Two PROPOSED comments, no text changed (new copy for the owner).**
   - `site/privacy.html`, above the `settings.json` list item of section 3: `PROPOSED 23/10/2026: "settings.json: your settings, and which
     version of the Terms of Use you accepted, with the account name (your Windows or macOS user name) that accepted it."` The app now
     records both (`terms.accepted`, `terms.accepted_by`), so the live line "your settings." understates what the file holds.
   - `site/help.html`, above the Welcome step's "Click Next." in the first-run walkthrough: `PROPOSED 23/10/2026: "Click Continue."`
     The Welcome step's button is now Continue.
7. **Left alone on purpose (O9 and O10 name the privacy policy only), for the owner to decide.**
   - `site/index.html`, the "A server you name" card still says "If the server runs on your PC, … nothing leaves your PC unless …".
   - `site/index.html`, the `dictum.kynni.au` card still has the 22/10/2026 sentence (… "from Settings, …" "direct-download version").
     It matched the policy until now; `UpdateCheckTests` pins it as `LandingSiteSentence` and the test that said it "says the same"
     is renamed `The_landing_page_card_for_dictum_kynni_au_keeps_its_22_10_2026_sentence`. `SiteStoreFirstTests` already pins this
     card's opening words.
   - `README.md`, the network table's `dictum.kynni.au` row, still reads "Only when you open the privacy policy, Dictum in plain
     words (`/plain-words`) or the full guide from Settings (About, Privacy, Help)"; the app lane's note said the Terms page would be
     added there with this follow-up, but the brief for this PR does not list it.
   - So the policy and those three surfaces now say different things about the same two facts until the owner decides.
8. **What it might break, and how to verify.** Site text and test pins only; no program code changed. A page that quotes either
   changed sentence would now disagree with the policy (item 7). Verify: `dotnet test Dictum.sln`, and read
   `git diff --word-diff` of `site/privacy.html`: the only changes are "PC" to "computer" twice, the O10 sentence and its marker, the
   O9 marker and the `settings.json` comment.
9. **Gates.** `dotnet test Dictum.sln` (Debug, 13:23 to 13:29 AEDT, on the tree before this entry was added): 2700 total, 2659
   passed, 0 failed, 41 skipped. `PublicCopyNeutralityTests` passed in that run and in the targeted runs before it. The app lane's
   last recorded run was 2698 total, 2657 passed; the two extra tests are the consistency test and the `plain-words.html` footer row
   (inferred from the test names, not measured on a main checkout).
   - **Not run:** a Release build, the demos, Setup, the installed app, tray mode, a Mac, and the 41 gated tests.

## Legal strings block 2: approved labels, captions, contact details (23/10/2026 14:11 AEDT, branch legal/strings)

The owner approved sections 1 to 4 of the legal strings basis document at 13:20 AEDT on 23/10/2026, chose option (a) of its
section 5 at 13:50, supplied the contact facts at 13:51 (Kynni Pty Ltd, ACN 700 150 956, Suite 402, 88 Phillip Street,
Parramatta NSW 2150) and approved applying the O9 and O10 wording to three more surfaces at 13:57. This one PR applies all of it.
Each changed place carries `APPROVED by the owner 23/10/2026 13:20 AEDT (legal strings block 2): do not reword` (13:50, 13:51 or
13:57 where the approval was then); JSON carries no markers, so this entry records the two demo captions.

1. **Flipped to APPROVED, text unchanged (13:20).** Settings > About buttons "Terms of use" and "Third-party notices"
   (`TermsText.AboutTermsLabel`, `NoticesLabel`); the Offline only description "No network at all: downloads, Check for updates,
   cloud clean-up and cloud speech are refused; a server on this PC still works." (`OtherPages.OfflineOnlyDescription`); the Gemma
   note "Gemma Terms of Use apply (ai.google.dev/gemma/terms)" (`CleanupCatalog.GemmaNote`; `ModelCoordinator.SelectCleanup` reads it
   through `LicenceNote` and carries no marker of its own); the CLI line "Start Dictum once and press Continue to accept its Terms of
   Use." (`CliCommands.TermsNotAccepted`, and the README sentence that quotes it); README's "Dictum's Terms of Use: https://dictum.kynni.au/terms
   (shown on first start)."; TRADEMARKS.md's last line "See the Terms of Use at https://dictum.kynni.au/terms."; both meta
   descriptions (privacy.html, terms.html; not pinned, as briefed).
2. **Changed (13:20).**

   | Where | Old | New |
   |---|---|---|
   | `OnboardingText.WelcomeNext` | The next few steps take a minute or two. Press Next to begin. | The next few steps take a minute or two. Press Continue to begin. |
   | `docs/STORE.md`, TO DICTATE opening | on the first start Dictum opens a Welcome window; click Next through its steps (or press Esc to skip it and keep the defaults), then | on the first start Dictum opens a Welcome window. Click Continue on its first step (this accepts Dictum's Terms of Use; closing the window or pressing Esc on that step quits Dictum), then click Next through the other steps (or press Esc to skip them and keep the defaults), then |
   | `site/help.html`, first-run Welcome step | Click Next. | Click Continue. |
   | `site/privacy.html` section 3, `settings.json` item | `settings.json`: your settings. | `settings.json`: your settings, and which version of the Terms of Use you accepted, with the account name (your Windows or macOS user name) that accepted it. |
   | `scripts/demos/install.json`, step after the licence step | Setup opens. Click Next. | Setup moves on. Click Next. |
   | `site/plain-words.html`, "What we cannot take responsibility for", first paragraph | Dictum is free and we give it to you as it is. We say it in plain words: we have built it with care, | Dictum is free. We have built it with care, |

   - The `STORE.md` PROPOSED note under the 1.2.1 notes block became an APPROVED note; row 17's appended sentence is flipped.
   - The first-run Welcome caption and the install licence caption were already the approved text; `docs/DEMOS.md` marks both
     (and "Setup moves on. Click Next.") APPROVED and records the approval at the top.
   - **Plain-words tidy, completed on the coordinator's instruction (owner's section 4 text).** The paragraph must start "Dictum is free. We have
     built it with care, …", so "and we give it to you as it is" went too (the open point "as it is remains", `Legal block, app side`,
     item 15, is closed). It first landed with only the five words dropped; the second commit finishes it. Marker 13:20.
3. **Section 5 (13:50).** `OnboardingText.WelcomeText`, last sentence: "Everything happens on this PC." becomes "Everything happens
   on this PC unless you turn on a cloud option." No test held the old sentence literally (they read the constant).
4. **Contact lines (13:51).** `site/terms.html` and `packaging/TERMS.txt` section 16: "Kynni Pty Ltd, Australia. Email …" becomes
   "Kynni Pty Ltd (ACN 700 150 956), Suite 402, 88 Phillip Street, Parramatta NSW 2150, Australia. Email …" (rest unchanged;
   `TermsTextConsistencyTests` ties the two copies). `site/privacy.html` section 12: "Kynni Pty Ltd, Australia, www.kynni.com.au"
   becomes "Kynni Pty Ltd (ACN 700 150 956), Suite 402, 88 Phillip Street, Parramatta NSW 2150, Australia, www.kynni.com.au". The
   mailto and GitHub links are as they were. The Terms stay "Version 1, last updated 23/10/2026" (same day; inferred that no
   version bump is wanted for a contact detail; say if the owner wants one).
5. **O9 and O10 on the landing page and the README (13:57).**
   - `site/index.html`, "A server you name" card: "your PC" becomes "your computer" twice, so the clause equals the policy's
     (`CloudWarningAndPresetsTests.ApprovedLocalServerClause` now equals `ApprovedPolicyLocalServerClause`, asserted).
   - `site/index.html`, `dictum.kynni.au` card: now the policy's O10 sentence word for word ("Only when you open the privacy policy,
     the Terms of Use, Dictum in plain words or the full guide from Dictum, or click Check for updates in the direct download for
     Windows and in the Mac beta, which reads one small file and sends nothing about you; the Store build is updated by the
     Store."). `UpdateCheckTests` pins it equal to `PrivacySiteSentence`; the test is `The_landing_page_card_for_dictum_kynni_au_says_the_same`
     again; the `SiteStoreFirstTests` sentence pin carries the new sentence.
   - **The direct-download gate flagged index.html** (the new sentence contains the exempted phrase), so
     `SiteStoreFirstTests.No_page_explains_the_direct_download_or_the_warning` now exempts exactly one use on `index.html` as well
     as exactly four on `privacy.html`; a second use on the landing page still fails.
   - `README.md` network table: the Check for updates row now says "(the direct download for Windows and the Mac beta; …)" and the
     privacy-policy row "Only when you open the privacy policy, Dictum in plain words (`/plain-words`), the Terms of Use (from the
     Welcome window or Settings > About) or the full guide from Settings (About, Privacy, Help)"; its pinned start is unchanged.
6. **New pins** (`OwnerApprovalsTests`, one per surface): the two Welcome lines, the README and TRADEMARKS lines, the STORE
   TO DICTATE opening, the help page step, the privacy `settings.json` item, the two contact sections, the demo captions and the
   two README rows. Changed pins: `PlainWordsTests` (the paragraph and a count of the 13:20 marker), `TermsAcceptanceTests`
   (section 16 of TERMS.txt), `CloudWarningAndPresetsTests`, `UpdateCheckTests`, `SiteStoreFirstTests` (above).
7. **Still PROPOSED (unchanged here).** The Mac copy block and everything else marked PROPOSED on main.
8. **What it might break, and how to verify.** Copy, comments and test pins; the only program-code changes are two string
   constants (`WelcomeNext`, `WelcomeText`). A person who read the old Welcome step sees "Continue" and the cloud qualifier;
   the landing page now mentions the Terms of Use, and it is not deployed until the gated release. Verify: `dotnet test Dictum.sln`
   and `git diff --word-diff`.
9. **Gates.** `dotnet test Dictum.sln` (Debug, 14:11 to 14:20 AEDT, on the final tree): 2708 total, 2667 passed, 0 failed, 41 skipped (the previous entry recorded 2700, 2659, 0, 41 on main; the eight extra are the new `OwnerApprovalsTests` facts). `PublicCopyNeutralityTests`: 3 of 3 passed.
   - **Mutation checks (observed failing, 23/10/2026, 14:11, each file restored byte for byte and compared):** with every changed string put back to its old text at once (Welcome lines, README lines and rows, TRADEMARKS line, STORE TO DICTATE opening, help step, privacy `settings.json` item, the three contact sections, install caption, plain-words words, both index.html cards), 27 tests failed (the 8 new facts, `PlainWordsTests` whole-page, `TermsAcceptanceTests` section 16, `CloudWarningAndPresetsTests` server clause, `UpdateCheckTests` card, `SiteStoreFirstTests` sentence pin and the direct-download gate, which counts exactly one use on index.html). `TermsTextConsistencyTests` did not fail because both Terms copies were reverted together; it was not mutated one-sided in this PR.
   - **Not run:** a Release build, the demos, Setup, the installed app, tray mode, a Mac, and the 41 gated tests. Settings > About and the Welcome window were not looked at on screen.

## Release 1.3.1 prepared (23/10/2026, branch release/1.3.1, release-prep lane)

The owner decided at 14:05 AEDT on 23/10/2026 that 1.3.1 is the next release for Windows and the Mac beta, carrying everything merged since v1.3.0 (main ec6102d) plus two PRs still open when this was written: the Mac copy block and PR #74 (Mac pasteboard, dictations current-host-only while clipboard history is kept off). No tag is created and nothing is built for release, published, deployed or submitted; none of that is done by this lane.

1. **Edits.** `Directory.Build.props`: `<Version>` 1.3.0 to 1.3.1 (package 1.3.1.0). `packaging/release-notes-preamble.md`: a new first paragraph, "What is new in 1.3.1", above the existing runtime and model paragraphs. `docs/STORE.md`: the version references (header, section 1, section 6), a "What's new in this version (1.3.1)" block above the 1.3.0 one, a "Notes for certification: PROPOSED for 1.3.1" block after the 1.3.0 one, go-live step 5a (Additional license terms), two certification-record rows. `site/version.json`: version 1.3.1 and the PROPOSED What's new paragraph as `notes`.
2. **`published` in `site/version.json` is still 2026-10-23, the 1.3.0 date.** It is only for people (the app ignores it, STORE.md section 10 step 7); set it to the deploy day when `site/` is deployed with the GitHub release.
3. **APPROVED by the owner 23/10/2026 15:53 AEDT: do not reword. The GitHub release notes paragraph (`packaging/release-notes-preamble.md`) and the `site/version.json` `notes` string.** The workflow publishes `packaging/release-notes-preamble.md` as it is, followed by GitHub's generated notes (`gh release create ... --notes-file packaging/release-notes-preamble.md --generate-notes`). The new paragraph is written as final prose, so approving it means leaving the file as it is; changing it means editing the file before the tag. Because the file is not versioned, the 1.3.1 paragraph would be published again by a later release unless it is replaced then (the Qwen line is the DECISIONS "Release-notes line" PROPOSED text of the legal block, app side, item 14, extended).
4. **APPROVED by the owner 23/10/2026 15:53 AEDT: do not reword. Store What's new (1.3.1)** and **certification notes (1.3.1)**, both in STORE.md and quoted in the PR body. The certification notes' second paragraph points back to the 1.3.0 notes for the cloud options instead of repeating the "Notes written 23/10/2026 for Dictum 1.3.0" block; if Partner Center wants a self-contained field, the owner can paste the 1.3.0 cloud paragraph after it.
5. **Mac is deliberately absent from the Store texts.** The first submission failed on Store Policies 10.1.5 ("promotes another platform"). The two Mac changes (PR #74 pasteboard host-only; the Mac copy block, Mac words where Windows strings said PC or Windows) are not in the Windows GitHub release notes either: `MacPackagingTests` requires the Windows preamble to contain no "macOS".
6. **`packaging/release-notes-mac.md` is unchanged.** It is the approved, version-free, single-paragraph Mac beta text ("Dictum-<version>-osx-arm64.zip"), `MacPackagingTests` pins it to one line starting "**Dictum for macOS (beta).**" with nothing else, and the owner approved it on 23/10/2026. A 1.3.1 line would need the owner's wording; PROPOSED, not applied: "Dictum on a Mac now marks each dictation it writes to the clipboard as this-Mac-only while clipboard history is kept off, so Universal Clipboard does not carry it to your other devices, and the Mac build says Mac where it said PC or Windows." If approved it needs the test changed too (the file would no longer be only the first paragraph), which this lane does not own.
7. **Not applied literally.** (a) "The Storage page's folder row": the coordinator corrected the text to "names its folders more precisely" (the row existed; only its label changes once a model folder is chosen); PR #71's labels are "Models, logs, calibration recordings" or "Logs, calibration recordings" (PROPOSED in its own entry), so the sentence names the row without quoting either label. (b) "the Mac 'login item' wording (PR #71)" and the Mac pasteboard change are left out of the Windows texts, per item 5. (c) "The installer's licence page" is in the GitHub notes but not the Store texts: the Store package has no installer. (d) The brief's "Mac copy block" had no open PR number visible to this lane (`gh pr list` showed only #74 at 14:xx AEDT), so it is described from the brief only.
8. **Certification-record row for Submission 4 (1.3.0).** Recorded as passed, about 08:30 AEDT on 23/10/2026, on the orchestrator's report; this lane did not read Partner Center.
9. **What it might break, and how to verify.** Docs, release text and the version number. A version pin or a text pin that quotes the old version, STORE.md or the preamble could fail: `dotnet build Dictum.sln -c Release` and `dotnet test Dictum.sln` (counts in the PR). The About page and the update check read the packaged version, so a 1.3.0 install will see 1.3.1 as newer once `site/version.json` is deployed.
10. **Approval (owner, 23/10/2026 15:53 AEDT, as relayed by the coordinator).** The three 1.3.1 texts (Store What's new, certification notes, GitHub release notes paragraph, and the version.json `notes` string that repeats What's new) were approved as a block, exactly as on head 80437f2. Markers changed; the texts are byte for byte. The version.json `published` date is still the deploy-day item in item 2.

## Mac copy block: Mac words on the platform seam, Windows strings unchanged (23/10/2026 15:06 AEDT, branch mac/copy-block)

The owner approved at 14:06 AEDT on 23/10/2026 the Mac copy block (written 14:02 AEDT from the Mac session's findings in the demo
guest and the two PROPOSED strings of PR #71): one rule and twelve rows, built as one change.

1. **The rule (APPROVED).** Where an approved Windows string says "this PC", "your PC", "Windows", "Start menu" or "Win+V", the Mac
   build shows the same sentence with the Mac word in that place. Nothing else in the sentence changes. The Windows build is untouched.
2. **The rows (each APPROVED by the owner 23/10/2026 14:06 AEDT, marked in the code "(Mac copy block, row N): do not reword").**

   | # | Surface | Windows (unchanged) | Mac | Where |
   |---|---|---|---|---|
   | 1 | Settings > Storage, folder row once a model folder is chosen | "Logs, calibration recordings" | the same words (PR #71's PROPOSED string, approved as is) | `StoragePage.DataFolderRow` (ModelPages.cs) |
   | 2 | Settings > Storage, Remove everything | "…the Start menu shortcut and the launch-at-login entry, then the program itself." | "…the login item, then the program itself." | `IRemovalSteps.StartupItemsPhrase` (Mac RemovalSteps.cs; PR #71's PROPOSED string) |
   | 3 | Welcome step 1 | "Everything happens on this PC unless you turn on a cloud option." | "…on this Mac…" | `OnboardingText.WelcomeText(words)` |
   | 4 | Welcome step 2 | "Dictum keeps its speech models on this PC." | "…on this Mac." | `OnboardingText.FolderText(words)`, `FolderTextNotIncluded(words)` |
   | 5 | Welcome hotkey step, intro | "…the box shows exactly what Windows receives." | "…what macOS receives." | `HotkeyCheck.Intro(words)` (Dialogs.cs) |
   | 6 | Hotkey capture box | "Windows received: Right Option up" | "macOS received: Right Option up" | `HotkeyCaptureBox.Received(words, key)` |
   | 7 | Welcome hotkey step, Remote Desktop caption | "On a Mac over Remote Desktop use the right Option key (Right Alt) or right Command (Right Win)." | not shown | `PlatformWords.ShowsRemoteDesktopNote` (OnboardingSteps.cs `HotkeyStep`) |
   | 8 | Menu-bar panel footer | "Nothing leaves this PC" | "Nothing leaves this Mac" | `FlyoutWindow.NothingLeavesLine(words)` |
   | 9 | Settings > Privacy, default line | "Nothing leaves this PC: speech recognition and clean-up both run here." | "Nothing leaves this Mac: …" | `PrivacyPage.NothingLeavesLine(words)` (OtherPages.cs) |
   | 10 | Settings > Privacy, clipboard caption | "Windows clipboard history (Win+V) and cloud clipboard sync." | "Clipboard managers that follow the concealed-item convention, and Universal Clipboard to your other devices." | `PlatformWords.ClipboardHistoryCaption` |
   | 11 | Calibration window, intro | "…in Dictum's folder on this PC (Settings > Storage lists it)…" | "…on this Mac…" | `CalibrationWindow.RecordingsNote(words)` |
   | 12 | Settings > Help, "How do I remove Dictum completely?" | the Windows paragraph, unchanged | "It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys and its login item, then closes and moves Dictum.app to the Trash, unless its first warning says it cannot." | `PlatformWords.RemoveAnswer`, `HelpText.QuestionsFor(words)` |

   Not in this block (the owner's file, section 3): the site help page (one text for both platforms, keeps "PC") and the Mac App
   Store listing.
3. **The seam.** One record, `Dictum.Platform.PlatformWords(ThisDevice, OsName, ClipboardHistoryCaption, RemoveAnswer,
   ShowsRemoteDesktopNote)`, handed out by a new `IPlatform.Words`: `WindowsPlatform` returns `PlatformWords.Windows`,
   `MacPlatform` returns `PlatformWords.Mac` (both in Dictum.Core, plain strings, no platform code; ArchitectureTests still pass).
   The Windows values are the approved Windows words byte for byte. The call sites compose their sentence from the words; none
   branches on the operating system. Two answers cannot be made by swapping a word (rows 10 and 12), so the record holds the whole
   sentence; row 2's phrase already lived on `IRemovalSteps`. Row 7 hides the caption (`IsVisible`) rather than removing it, so the
   Windows step is laid out as before. `HelpText.Questions` stays the Windows list (HelpPageTests and the site twin read it);
   `QuestionsFor(words)` swaps only the remove answer's last paragraph and keeps every other entry as the same object. The test
   fake (`FakePlatform.Words`) defaults to the Windows words, so every existing window test sees what it saw before.
4. **Pins** (`MacCopyBlockTests`, 17 tests): one per row, each a theory over both word sets where the row is on a window, asserting
   the composed string and the window built on the fake platform with those words (the onboarding window, the capture box, the
   panel, Settings > Privacy and Help, the calibration window); rows 1 and 2 as plain facts (row 2 through the real
   `WindowsPlatform` and `MacPlatform` removal steps, so it runs on Windows, where MacShellTests' "the login item" pin is skipped);
   and one that each platform hands out its own words. Existing pins changed only to call the composed form with
   `PlatformWords.Windows` (OnboardingTests, OwnerApprovalsTests, PlainWordsTests, TermsAcceptanceTests, PolishTests); the
   PROPOSED notes on rows 1 and 2 in ModelFolderTests and MacShellTests now say APPROVED.
5. **Mutation checks (observed failing, 23/10/2026 between 14:55 and 15:05 AEDT, source restored with `git checkout` after each).**
   Mac side, each call site put back to its pre-change literal: rows 1 (the row's word changed), 2, 3, 5, 6, 8, 9, 11 and 12 at
   once, then rows 4, 7 and 10 on their own; every `(mac: True)` case and the row 1 and 2 facts failed (17 run, 9 failed, then 3).
   Windows side, the Windows words changed: ThisDevice, OsName, the caption, the remove answer and the note flag at once (18 of 280
   failed, among them OwnerApprovalsTests, CloudSpeechUiTests, CleanupOffTests, UiWindowTests, PolishTests, OnboardingTests and
   every `(mac: False)` case), then the caption, the note flag and FolderText alone (5 of 73 failed: rows 4, 7 and 10), then the
   Windows startup phrase (row 2 failed).
6. **Follow-up block for the owner (Mac-visible "this PC" / "Windows" strings outside the twelve rows, not changed here).** Seen while
   working (not an exhaustive sweep): the Welcome speech step's "A model that is not
   on this PC yet…" (`OnboardingText.SpeechChoice`), Settings >
   Privacy's card titles "What leaves this PC" and "Kept on this PC", its "the log stays on this PC" caption and the Offline only
   description ("a server on this PC"), the panel's "Clean-up via a server on this PC", `CloudPolicy.TrayLine` ("text leaves this
   PC"), `SpeechPolicy.DeclineButton` ("Keep speech on this PC"), and Help's "your Windows build" (FeedbackNote) and "on this PC"
   (How Dictum works). The lens on 32c6c1e added these sites (its file:line on that commit, not re-checked here):
   AppShell.Actions.cs:210-211, AppShell.CloudSpeech.cs:35 and :41, HelpText.cs:226-227, :249 and :275, GeneralPages.cs:451,
   ModelPages.cs:27, :72 and :75, SimpleView.cs:44, :72 and :98, SpeechPolicy.cs:104, HowItWorksText.cs:19 and :70.
7. **What it might break, and how to verify.** Windows: nothing visible; every Windows string is pinned byte for byte. The Mac
   build shows the twelve surfaces in Mac words; nothing here ran on a Mac. Verify: `dotnet test Dictum.sln`; on the Mac, open the
   twelve surfaces listed in the PR.
8. **Gates.** `dotnet test Dictum.sln` (Debug, 15:07 to 15:15 AEDT, on the final tree): 2725 total, 2684 passed, 0 failed, 41
   skipped (the previous entry recorded 2708, 2667, 0, 41 on main; the 17 new tests are MacCopyBlockTests). PublicCopyNeutralityTests
   3 of 3 passed. **Not run:** a Release build, the installed app, tray mode, a Mac, and the 41 gated tests (MacShellTests' 11
   among them, skipped off a Mac).
9. **CI fix (23/10/2026 15:36 AEDT).** The macOS CI job failed on two of these tests with DllNotFoundException ("kernel32.dll"):
   they built the real `WindowsPlatform` on the macOS runner. Each real platform class is now built only on its own system
   (`The_windows_platform_hands_out_the_windows_words`, `The_mac_platform_hands_out_the_mac_words`, gated with
   `Assert.SkipUnless(OperatingSystem.IsWindows() / IsMacOS())`), and row 2 reads each platform's phrase through a new
   `RemovalSteps.StartupItems` constant (one per platform, the property returns it) on any system. Observed failing: the Mac constant
   set to the Windows phrase (row 2 failed), and the Windows property returning another phrase (the Windows platform test failed).
   `dotnet test Dictum.sln` (Debug, 15:27 to 15:36 AEDT): 2727 total, 2685 passed, 0 failed, 42 skipped (the Mac platform test is
   the new skip on Windows).
10. **Lens on 32c6c1e (Needs changes, no blockers), fixed 23/10/2026 16:03 AEDT.** The rule covers a string wherever it appears:
   Settings > General's hotkey card now hides the same Remote Desktop caption on a Mac (`GeneralPage.MacNote()`, row 7 marker;
   pin `Row07_the_general_page_hides_the_remote_desktop_note_on_a_mac`, observed failing with the `IsVisible` line removed:
   "Expected: False, Actual: True"), and the calibration results hint says "…per 10 s of speech on this Mac…" on a Mac
   (`CalibrationWindow.ResultsHintStart(words)`, row 11 marker; `Row11_…` extended to the hint, observed failing with "this PC"
   put back). HelpText's "The questions, in order" summary is back above `Questions`. `dotnet test Dictum.sln` (Debug, 15:52 to
   16:03 AEDT): 2729 total, 2687 passed, 0 failed, 42 skipped.

### Pasteboard writes are current-host-only (23/10/2026, branch mac/pasteboard-host-only)

- **Finding (privacy re-lens, via the orchestrator):** a dictation written to the general pasteboard with Handoff on could be
  carried to the user's other Apple devices by Universal Clipboard, which contradicts "nothing you say leaves this Mac".
- **API, verified 23/10/2026 from the installed macOS SDK header** (`AppKit.framework/Headers/NSPasteboard.h`, Xcode SDK):
  `- (NSInteger)prepareForNewContentsWithOptions:(NSPasteboardContentsOptions)options`, `API_AVAILABLE(macos(10.12))`,
  "Prepares the pasteboard for new contents, clearing the existing contents … Any options specified will persist until
  prepareForNewContentsWithOptions: or clearContents is called"; `NSPasteboardContentsCurrentHostOnly = 1 << 0`, "the pasteboard
  contents should not be available to other devices". Apple's web pages for both symbols returned 404 or an empty body that day.
- **Change:** `Pasteboard.Write` replaces its `clearContents` with `prepareForNewContentsWithOptions:`; `SetText` passes
  `ContentsOptions(keepOutOfHistory)` = current-host-only when history is kept off (the default), 0 when the user allows
  clipboard history. `Restore` passes 0, so the user's own contents are offered to their devices again as before. The
  nspasteboard.org concealed, transient and source types are unchanged. Reasoning for tying it to the history switch
  rather than always on: Windows already gates `CanUploadToCloudClipboard` (cloud clipboard sync) by the same setting, so a
  user who allows history gets the same behaviour on both platforms. Owner decision 23/10/2026 13:52 AEDT: keep host-only tied to the history switch.
  No user-visible string changes. The Mac caption (PlatformWords.Mac, approved 23/10/2026 14:06) names Universal Clipboard; this change makes it true.
- **Pin:** `MacInput_pasteboard_dictation_is_current_host_only_unless_history_is_allowed` (constant, both switch states).
  Mutation check recorded in the PR.
- **Not verified:** that another device actually declines to show the dictation (no second device in the lane); what is
  verified is the selector exists and the write path still pastes (paste check on a real Mac, 27/10/2026, in the PR).
- **Lens follow-up (27/10/2026):** source-scan pin `MacInput_pasteboard_source_applies_the_host_only_option_on_every_dictation_write` (runs on Windows; Pasteboard.cs must contain the SetText call `Write(Payload(text, keepOutOfHistory), ContentsOptions(keepOutOfHistory))`, the `prepareForNewContentsWithOptions:` send with `(nint)options`, and exactly one `ObjC.Sel("clearContents")`). Mutation checks, each observed failing then reverted: SetText passing `0` instead of `ContentsOptions(keepOutOfHistory)` failed with "Assert.Contains() Failure: Sub-string not found"; Write calling `clearContents` in place of `prepareForNewContentsWithOptions:` failed with the same message.

## 27/10/2026 14:00 AEDT: 1.3.1 publication dates

- The Terms of Use page carries its publication date, 27/10/2026 (section D item 0 of the legal block: "At deploy, terms.html carries the publication date"); `site/version.json` `published` is 2026-10-27, the 1.3.1 deploy day. The privacy policy's "Last updated" stays 23/10/2026, the date of its last text change. No other file changes; no pin holds either date.

## Request access: the Accessibility grant on the platform seam, one button on every surface that fails for want of it (27/10/2026, branch feature/mac-access-request)

Owner's ask (27/10/2026, at the Mac, during the PR #74 re-test): "we need to add option in app to check access and if access not give a
request access key, for example right now it tells me in the hotkey selector tool that the system refused to report keys, but no
solution." Lane brief from the orchestrator 16:47 AEDT. Built on Fable 5.1 (new feature, per the owner's tiering order).

1. **The seam.** `IPlatform.InputAccess` (`IInputAccess`: `Check()` returning `InputAccessState` NotApplicable / Granted / Denied, and
   `Request()`), beside the listeners it governs. `WindowsPlatform` hands out `InputAccessNotNeeded.Instance` (always NotApplicable, a
   request does nothing): a low-level hook installs for any process, there is no grant to ask for, and no Windows surface changes.
   `MacPlatform` hands out `MacInputAccess` (src/Dictum.Mac/Input/InputAccess.cs): `Check` is `AXIsProcessTrusted`; `Request` runs
   `AXIsProcessTrustedWithOptions(prompt)` and then opens System Settings at the Accessibility page whatever the check answered.
   Both answers are logged.
2. **The Settings link, verified 27/10/2026 on this Mac (macOS 27.2, build 26B118):** `open
   "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"` shows the page (the owner saw the Dictum row on
   it), and the SecurityPrivacyExtension binary on that Mac carries the `Privacy_Accessibility` anchor. On macOS 27 the page is titled
   "Device Control and Data Access" (the owner's screenshot); on 15 and 26 "Accessibility" (recalled, not checked on those). The
   on-screen strings therefore name no page: "turn Dictum on in the System Settings page that opens".
3. **Why the page always opens.** 27/10/2026 18:13 AEDT, ad-hoc test build: `AXIsProcessTrustedWithOptions(prompt)` answered trusted
   while the same process's event taps were refused ("not allowed to control this Mac yet") and `AXIsProcessTrusted` answered no. The
   System Settings list showed one Dictum row, switched on, which belonged to the Developer-ID-signed copy in /Applications (same bundle
   id, different signature). So the prompting check alone cannot decide, and no prompt appears in that case; the page is where the
   switch is either way. Observed, not explained; the first version of this lane returned early on a trusted answer and the owner
   landed on a page "already telling me it has access".
4. **The shell.** `InputAccessWatch` (src/Dictum.UI/Shell/InputAccessWatch.cs, `AppShell.Access`): when a listener is refused
   (`StartHook`, `ApplyFixHotkey`) and the check says Denied, it polls the silent check once a second (`PollFor` = 15 minutes, a marked
   simplification) and on Granted replaces both listeners (`AppShell.RestartListeners`), puts the icon back to the real state, and says
   so. `Request()` asks and polls. The old "then quit and reopen Dictum" log line is reworded (APPROVED 27/10/2026 19:00 AEDT, row 7 of the approval file).
5. **No relaunch needed: proved on hardware** 27/10/2026 18:37 AEDT, macOS 27.2, ad-hoc build: request logged 18:37:47, the owner
   switched Dictum on, "Event tap installed … (hotkey listener for LeftCtrl)" and "Input access granted; the hotkey listener is
   installed." at 18:37:50, the Settings line changed to "Dictum can now see the hotkey." on its own. The owner's words: "the line
   changed to 'Dictum can now see the hotkey.' without reopening Dictum." `RestartFailed` and its string (row 6) are kept for the case
   where a grant still leaves the tap refused; not seen on hardware.
6. **Surfaces (no new page).** `InputAccessLine` (src/Dictum.UI/Views/InputAccessLine.cs): the status (a polite live region), the
   stale-entry caption, and the named "Request access" button; hidden while NotApplicable or Granted with nothing to say, so every page
   is laid out as before. In Settings > General's Hotkey card (above the capture box; the two boxes there build no line of their own,
   `showsAccessLine: false`, so the card has one button); in the hotkey capture box on its own (the Welcome hotkey step), replacing the
   "The system refused to report keys…" dead end when the grant is what is missing, and listening again by itself once the grant
   arrives; and in the menu-bar panel's problem line with a "Request access" link (`ShellStatus.RequestAccess`), because the icon's
   "!" said nothing (owner finding 27/10/2026, 18:20 AEDT: "it also doesnt tell me anywhere why the logo is the orange warning"). Any
   other refusal keeps the log pointer.
7. **Stale rows.** The owner's loop on 27/10/2026 was seven `au.kynni.dictum` Accessibility rows behind one row shown on; `tccutil reset
   Accessibility au.kynni.dictum` cleared them. The app cannot read the list and never runs tccutil; the caption (row 2) says to remove
   the row with − and request again, which is what completed the hands-on (the ad-hoc build's row removed, re-added by the request).
8. **Strings.** All seven APPROVED by the owner 27/10/2026 19:00 AEDT as written ("approve 1 to 7"), in `.demo-preview/approve-mac-access.md` (seven rows); none ships on Windows; no approved
   string edited. The device word is the Mac copy block's "this Mac".
9. **Pins** (`InputAccessTests`, 21 tests after the item 14 round, 20 after item 13; this line said 11 at b60dc44, where the file held 13): the no-grant platform answers NotApplicable; the real Windows platform hands out it (Windows
   only); the real Mac platform answers Granted or Denied as `AXIsProcessTrusted` does and its link is the verified one (macOS only);
   with no grant to ask for, Settings > General shows no line, no button, no problem, and the box keeps its old sentence; a listener
   refused for want of the grant shows the line, the caption and the button, and the button requests; the grant arriving replaces both
   listeners and says so, without a reload; a grant that still leaves the listener refused says so; the box on its own shows the button
   and listens once granted; Settings > General shows one button even with a refused box; the panel says why the icon shows the error
   and offers the request, and drops both once granted; the button is named and the status is a polite live region.
   `MacInput_tap_refused_without_accessibility_fails_cleanly_and_says_where_to_allow_it` now also requires "Press Request access" and
   no "reopen" in the log line.
10. **Mutation checks (observed failing 27/10/2026 between 18:39 and 18:41 AEDT, source restored with `git checkout` after each):**
    (a) `ListenerRefused` returning at once, `InputAccessNotNeeded.Check` answering Denied, and the Settings link without its anchor,
    together: 7 of 10 run failed (the no-grant fact, the Mac platform pin, the capture box, the General line, the one-button page, the
    panel, the names). (b) `Tick` not restarting the listeners: 2 failed (the grant arriving, the grant that still fails).
    (c) the line showing itself on a no-grant platform: the Windows-surface pin passed at first (it looked for the Denied words only),
    was strengthened to assert no visible `InputAccessLine`, no Granted or RestartFailed text, no problem and no request on the status,
    and then failed on the mutation: 1 failed.
11. **Not verified / follow-ups.** Second grant on macOS 15 or 26 (page name, prompt behaviour); whether the prompting check's "trusted"
    answer for a refused build is a macOS 27 behaviour or a TCC-cache one. Follow-up the owner raised from Zoom's installer: a floating
    panel with a draggable tile of Dictum.app to drop onto the Settings list, which would side-step the stale-row loop; not in this lane.
    `src/Dictum.Mac/README.md`'s IPlatform table has no row for `InputAccess` (the file is outside this lane's files).
12. **Runs on the Mac at ff9c3f9 (27/10/2026 18:47 AEDT, macOS 27.2, Release; no Mac run is recorded here at b60dc44 or after the item 13 fix round):** `dotnet test --project tests/Dictum.Tests`: 2742 total,
    2550 passed, 0 failed, 192 skipped (the Windows-only pins among them). `DICTUM_UI_TESTS=1`, MacInputTests and InputAccessTests:
    34 total, 33 passed, 0 failed, 1 skipped (the Windows platform pin). Ad-hoc bundle built by scripts/publish-mac.sh for the hands-on.
13. **Merge-lens fix round (27/10/2026, from b60dc44; lens on Opus reproduced each with a probe test).** Windows behaviour and copy
    unchanged: every change sits behind a Denied answer, which `InputAccessNotNeeded` never gives. No approved string changed.
    - MAJOR 1. The Welcome hotkey step never showed Request access on a fresh install: the box read the cached state, which only turns
      Denied after the shell's own listener is refused, and the Welcome window runs before Start installs it. Fix: the box's refusal
      path calls `Access.ListenerRefused()` (a fresh check, which also starts the watch) before reading the state; and
      `RestartListeners` installs nothing and returns true while no listener was ever installed (`_hook` null), since Start installs
      both and does not dispose a listener installed before it. The guard reads `_hook`, not `_controller` as the lens suggested:
      `StartHook` always keeps the listener it made, refused or not, and the existing pins install listeners by a reload on a shell
      Start has not built the controller for, so a `_controller` guard would have turned every restart in them into a no-op. Pins:
      `The_welcome_box_asks_for_itself_before_the_shell_has_installed_a_listener`,
      `A_grant_during_the_welcome_window_leaves_one_listener_after_start`.
    - MAJOR 2. Request access with the grant already in place (polling lapsed, Dictum switched on by hand) changed the words but
      installed no listener. Fix: `Request()` starts polling and runs `Tick()` at once, so both answers take the one path. Pin:
      `Request_access_with_the_grant_already_in_place_installs_the_listeners`.
    - MINOR 3. `Tick` runs from a DispatcherTimer with nothing above it to catch a failure. Fix: its body is wrapped; on an exception
      it stops polling, logs `Input access restart failed: {ex}` and shows the grant as a failed restart (row 6). Pin:
      `A_restart_that_throws_is_caught_and_shown_as_a_failed_restart` (the fake's listener factory throws once).
    - MINOR 4. Row 4 ("Dictum can now see the hotkey.") showed on every General page opened later in the session, even after a
      revoke, because the first render set it. Fix: only a Denied-to-Granted change seen while the line is attached sets it; attaching
      resets it. Pin: `The_granted_line_shows_only_for_a_grant_seen_while_attached`.
    - MINOR 5. On Settings > General both refused boxes resumed on the grant (a refused Start raises no Started, so the
      one-box-at-a-time wiring never cleared the other), and the fix box could end up listening. Fix: only a box that carries its own
      line resumes by itself; on General the card's line is the cue and Change… starts one. Pin:
      `Two_refused_general_boxes_do_not_resume_on_the_grant`.
    - MINOR 6. The 15-minute cut-off read `DateTime.UtcNow`. Fix: `InputAccessWatch` takes a `TimeProvider` (null is the system
      clock), as the "TimeProvider seam" entry does. Pin: `Polling_stops_after_the_cut_off_on_the_injected_clock`.
    - **Mutation checks (27/10/2026, finished before 19:32 AEDT by the clock read after the last one, Windows, Release; source restored from the index with `git checkout`
      after each, and the full suite then passed):** MAJOR 1, the box's fresh check removed: its pin observed failing (the state stayed
      NotApplicable). MAJOR 1, the `RestartListeners` guard removed: its pin observed failing at "nothing installed before Start", and
      with that inner assertion also removed, observed failing at the count after Start (expected 1 live listener, actual 2). MAJOR 2,
      the old `Request()` body restored: observed failing (expected 4 listeners, actual 2). MINOR 6, the cut-off line removed:
      observed failing (polling still on). MINOR 3, the catch disabled: observed failing (the exception escaped). MINOR 4, the grant
      flag set on every render: observed failing (row 4 shown on the later page). MINOR 5, the own-line condition removed: observed
      failing (the second box listening).
    - **Runs on Windows after the fix:** `dotnet test tests/Dictum.Tests -c Release`: 2751 total, 2708 passed, 0 failed, 43 skipped.
      The Mac runtime is not verified after this round (no Mac run from this Windows lane).
    - **Known nuance, not changed:** when the grant arrives during the Welcome window, the log carries the guard's line ("…they install
      when Dictum finishes starting.") and then `Tick`'s existing "Input access granted; the hotkey listener is installed." a few
      moments before Start installs it. A log line only; left as written for the owner to decide.
14. **Re-lens fix round (27/10/2026, from f3b4388; the re-lens returned Needs changes with two minors, both in the item 13 guard).**
    - F1. The guard's "not installed yet" test, `_hook` null, is also true after Start when a listener threw while being made (the
      MINOR 3 state): refused and Denied, the grant arrives and the restart throws, the grant is revoked and a General box's refusal
      reports Denied, the grant is given again, and `Tick` left the state Granted with no live listener, no problem line, and the log
      saying "…they install when Dictum finishes starting" long after start. Fix: `_listenersTried`, set as the first statement of
      `StartHook`, and the guard reads it. Pin: `A_grant_after_a_restart_that_threw_still_installs_a_listener`.
    - F2. `Tick` logged "Input access granted; the hotkey listener is installed." on the guard path, where no listener exists (item 5
      cites that line as the hardware proof, so it must be true). Fix: the outcome line moves from `Tick` into `RestartListeners`,
      after `StartHook`; the guard path logs only its own line. This retires item 13's "known nuance". Pin:
      `A_grant_during_the_welcome_window_leaves_one_listener_after_start` now also asserts no "the hotkey listener is installed" line
      before Start.
    - **Mutation checks (27/10/2026, before 19:56 AEDT by the clock read after them, Windows, Release; restored from the index with
      `git checkout` after each):** F1, the guard put back to `_hook is null`: observed failing ("the grant left no listener and said
      nothing"). F2, the outcome line put back in `Tick`: observed failing (the "installed" line found before Start).
    - **Runs on Windows after this round:** `dotnet test tests/Dictum.Tests -c Release`: 2752 total, 2709 passed, 0 failed,
      43 skipped. The Mac runtime is not verified after the fix rounds (b60dc44 onwards).
15. **Mac round-2 fix round (29/10/2026, from 811c1c5; hands-on re-test on the owner's Mac, macOS 27.2).**
    - **B (blocker, safety).** The owner's grant was reset with `tccutil` while a test build ran: the installed hotkey tap stayed, and
      from 08:10:51 AEDT the log repeated "Event tap (hotkey listener) was disabled by macOS (timeout) and re-enabled." while no app on
      the Mac received keys, until the process was killed at 08:19:06. `EventTap.Handle` re-enabled a disabled tap unconditionally (the
      same code is in tag mac-v1.3.1). Fix: `TapDisableGuard` lets a disabled tap back on only while `AXIsProcessTrusted` says trusted
      and macOS has not disabled it again within 60 s (marked `// simplification:`); otherwise the tap logs that it was removed, stops its
      run loop (so `Run` removes the tap) and raises `IInputAccess.Revoked`, a new event on the seam (no-op on Windows). The shell treats
      it as a refusal, and `InputAccessWatch` now removes every installed listener as the state turns Denied (`AppShell.StopListeners`),
      which also covers the case seen at 08:10:24: a fresh tap refused while an older one was still installed.
      Pins: `The_disable_guard_reenables_once_and_never_for_a_repeat_or_without_the_grant`,
      `A_tap_macOS_keeps_disabling_is_removed_and_reported_instead_of_turned_back_on`,
      `A_listener_that_lost_the_grant_removes_every_listener_and_shows_the_button`,
      `With_no_grant_to_ask_for_a_refusal_removes_no_listener`.
    - **A (major).** After that reset, the hotkey box's monitor was refused ("…although Accessibility reports this app as allowed") and
      the box showed the generic "The system refused to report keys…" with no line and no button: `ListenerRefused` returned unless the
      check said Denied, and the check went on saying trusted (as it did for the stale rows of 27/10). Fix: where there is a grant, a
      refused or removed listener is Denied whatever the check says; the check is then not believed until it has answered Denied once or
      the user presses Request access (one more trial), so a stale "trusted" starts no restart loop; Granted is set only after a listener
      installed, and a refused restart no longer reinstalls the fix-last listener. The old pin
      `A_grant_that_still_leaves_the_listener_refused_says_so` described the behaviour this replaces and becomes
      `A_listener_refused_while_the_system_says_allowed_shows_the_line_and_the_button`. `RestartFailed` (row 6) is now reached only when
      a restart throws.
    - Two new log lines are PROPOSED for owner approval (rows 8 and 9 of `.demo-preview/approve-mac-access.md`). No screen string changes.
    - **Mutation checks (29/10/2026, Mac, Release; sources restored from a copy after each):** the tap put back to re-enabling always:
      observed failing (`A_tap_macOS_keeps_disabling…`). The early return put back in `ListenerRefused`: observed failing (both A and B
      shell pins). The listener removal taken out: observed failing (`A_listener_that_lost_the_grant…`). The stale "trusted" believed
      again in `Tick`: observed failing (both shell pins).
    - Hand-test rule until B is proven on hardware: never reset Accessibility while a Dictum build runs; any reset-while-running test
      runs under a shell watchdog that kills the test build after 60 s unless cancelled.
16. **Liveness round (29/10/2026, from 73c460d; design approved by the orchestrator at 21:37 AEDT with seven conditions).** On
    hardware, 73c460d did not stop the lock: reset at about 21:34:00 AEDT with the build running, the owner could not type in any app,
    the only log line was one "disabled by macOS (timeout) and re-enabled" at 21:34:51.680 (the check still said trusted), and the
    watchdog killed the build at 21:35:00. Keys were held about 50 s before macOS's first notice, so no rule keyed on that notice can
    prevent the lock. Fix: `TapLiveness` compares the HID system's count of the tap's own event types (`CGEventSourceCounterForEventType`
    over `TapEvents.Types`, never mouse moves) with the count the tap's callback has seen; four events behind for a second means the
    tap is starved. A 500 ms `System.Threading.Timer` runs it off the tap thread and never waits on that thread; on starvation the tap is
    disabled at once from the timer, its run loop stopped (Run invalidates and releases it after waiting for the timer), and
    `IInputAccess.Revoked` raised; nothing is re-created in place. Paused while Secure Event Input is on (`IsSecureEventInputEnabled`)
    and for one gap after, and after a stalled timer (sleep). The first timeout-disable with the check saying trusted re-enables only if
    a liveness probe passes. Thresholds carry `// simplification:` comments. Head-insert placement unchanged (condition 7).
    Pins: `Liveness_calls_a_tap_starved_only_when_the_system_sees_keys_the_tap_does_not`,
    `Liveness_pauses_for_secure_input_and_for_one_gap_after_it`, `Liveness_pauses_after_the_timer_stalled`,
    `Liveness_counts_only_the_event_types_in_the_tap_mask`, `A_starved_tap_removes_itself_and_tells_the_shell_once`,
    `A_disabled_tap_the_keys_stopped_reaching_is_removed_not_turned_back_on`.
    - **Mutation checks (29/10/2026, Mac, Release; sources restored from a copy after each), each observed failing on the reverted
      behaviour:** the timer never calling a tap starved (three pins); no pause for secure input (two pins); no pause after a stalled
      timer (one); the first disable turned back on without the probe (one); mouse moves counted (one).
    - Unverified until the hands-on: that the HID counters advance while a starved tap holds the keys, and that secure input withholds
      keys from taps while the counters still move (both recalled). Two more log lines PROPOSED (rows 10 and 11).
