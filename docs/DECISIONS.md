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
- The tray "Speech model" submenu replaces the old "Download model…" item: it covers download, switch and choose-file. It is rebuilt on every open so "downloaded" is current. (Revised 15/10/2026: a second selection while a download runs now cancels it and proceeds; see "Downloads and model checks".)
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
- **Cancel:** tray "Cancel download" (visible only while downloading) and choosing another model both cancel immediately, including during a backoff wait; the cancelled task deletes its `.part` before the next choice proceeds. This replaces "a second selection while a download runs is refused". Quitting cancels a running download.
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
- Downloads run one at a time (`ModelDownloads`): a download asked for during another waits its turn, Cancel stops the running one and drops the queue, and a switch-over that throws is shown in a balloon. A tray model choice during a download still cancels it (documented behaviour); a Mode preset or a calibration "Download and include" waits behind it, so its bytes are not lost. The download question no longer promises a model switch itself: the callers that switch add "Dictate switches to it when the download finishes"; calibration says "It is added to the test; your current model stays in use".
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
without the matcher, so the first pin drives the matcher and the state machine directly); the 19 gated desktop and tray
tests stay skipped.
