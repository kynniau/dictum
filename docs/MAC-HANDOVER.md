# Dictum for macOS: handover for the Mac build session

Written 19/10/2026 by the Dictum orchestrator session (Windows PC) for a Claude Code session running on Kyle's Mac.
Read this whole file before doing anything. It is the brief; Claude's memory does not travel between machines.

Provenance marks: **Verified** means checked on 19/10/2026 against the named source. **Recalled** means from training,
unverified: treat it as a lead and check it against Apple's or the package's documentation before relying on it.

## 0. Owner decisions of 22/10/2026 (these win over anything below that says otherwise)

Recorded 22/10/2026 by the public-copy lane from the owner's decisions of that day (memory note dictum-mac-beta; not re-asked).

- **Developer ID signed and notarised GitHub pre-release.** The owner joined the Apple Developer Program on 22/10/2026 and chose, at 22:05 AEDT,
  a Developer ID signed and notarised GitHub pre-release as the beta, so a first open shows no Gatekeeper warning. This replaces the 19/10/2026
  decision (ad-hoc signed, no Apple Developer Program membership). The Mac App Store is a later train, not part of the beta.
- **Apple Silicon only (arm64) for the beta.** One zip: `Dictum-<version>-osx-arm64.zip`. Intel (osx-x64) is built **only if a
  tester needs it**; until then no Intel publish, no second zip, no Intel test run.
- **Timing.** The Mac build ships after 1.3 is live. The Mac session may start any time before that, on its own branch.
- **Apple credentials.** The owner creates the Developer ID certificate and the notarisation credentials himself. The Mac session never handles
  Apple credentials: it does not ask for, receive, store or commit a certificate, private key, password or notarisation key, and no such value
  is ever in the repo. How the signing and notarisation steps receive them is settled with the owner in the PR.

Lane note (public-copy lane, 22/10/2026; its inference, not an owner decision): the "retire when signed" simplifications in section 5
(`Startup`: LaunchAgent login item against `SMAppService`; `CreateTray`'s `Notify`: the transient balloon against `UNUserNotificationCenter`)
are to be re-evaluated by the Mac lane now that signing is available. Sections 1, 5 and 6 still describe ad-hoc signing and a Gatekeeper warning;
where they differ from this section, this section wins and the Mac session rewrites them.

Lane note (public-copy lane, same date; its inference, not an owner decision): Dictum 1.3 adds optional cloud speech (off by default; its own
consent step). It is platform-neutral code, so it should reach the Mac build without Mac work. The Mac copy must not say that audio never leaves
the Mac without saying "unless you turn on cloud speech" (the Windows public pages were brought into line the same day).

## 1. Goal

A beta build of Dictum for macOS that Kyle's friends can download from a GitHub pre-release, open on their own Macs and
use for dictation, without an Apple Developer Program membership (decision 19/10/2026, superseded by section 0: the owner has since joined). The
Windows app is the reference: the Mac app must behave the same wherever the platform allows. Target: Apple Silicon only
(arm64, one zip) for the beta; Intel (a second zip) only if a tester needs it (owner decision 22/10/2026, section 0).

What this means for the user, to be stated plainly in the release notes (all **Recalled**, verify before writing):
- (Superseded by section 0: a Developer ID signed and notarised build should open without this.) First open is blocked by Gatekeeper; the user opens it through System Settings > Privacy & Security > Open Anyway.
- Three permission prompts: Microphone, Accessibility (to post Cmd-V) and Input Monitoring (the event tap).
- Grants are tied to the app's signature; an ad-hoc signed app may be asked again after each update.

## 2. Ground rules on the Mac (same as every Dictum lane)

- **Branch:** work on `feature/mac-platform` cut from `origin/main`. Push often. Open a PR. Never merge; the Windows
  orchestrator session lenses and merges. Never rebase or force-push a branch you did not create; never force-push at all.
- **Private material:** never commit recordings, transcripts, names or test phrases of real people. Their only home is
  the private folder outside the repo on the Windows PC (never named in any tracked file: a test enforces that), and it
  is not on the Mac. If Kyle copies the private word list over by USB, set `git config dictum.privateDenylist <path>`
  (or `DICTUM_PRIVATE_DENYLIST`) so the commit guard checks it; otherwise the guard warns and skips.
- **Hooks:** after cloning run `git config core.hooksPath "$(pwd)/.githooks"` (the hooks are bash; they run on macOS).
- **No telemetry, ever.** No analytics, crash reporting or automatic update checks. Nothing in Dictum contacts a server
  except the model download the user asks for and the cloud clean-up or cloud speech the user turns on.
- **Approved copy is never reworded:** CloudPolicy strings, the consent dialog, the state lines, the Store listing. New
  user-facing strings are marked PROPOSED in docs/DECISIONS.md for Kyle's post-hoc approval.
- **Build ladder and review rubric** (Kyle's global CLAUDE.md, copy it to `~/.claude/CLAUDE.md` on the Mac): read first,
  smallest change in the right place, every non-trivial change leaves a real test, deliberate corner-cuts carry a
  `// simplification:` comment naming the ceiling and the retire trigger.
- **Model tiering:** sonnet for mechanical work, opus for substantive engineering and lenses, fable only with a stated
  reason. Set `model` on every spawn.
- **Never launch a Windows Dictum.exe** is a Windows rule; on the Mac you may run the app you build, that is the point.
- Do not touch `site/`, `packaging/Dictum.iss`, `scripts/package-msix.ps1` or anything Store-related.

## 3. Setting up the Mac

1. Xcode Command Line Tools: `xcode-select --install` (for `codesign`, `ditto`, `plutil`). **Recalled.**
2. .NET 10 SDK: the Windows PC uses 10.0.402; `global.json` pins only the test runner, not an SDK version (**Verified**
   19/10/2026), so any 10.0.x SDK should do. Install with Microsoft's `dotnet-install.sh --channel 10.0` and put it on
   PATH. **Recalled** script name.
3. `git clone https://github.com/kynniau/dictum.git && cd dictum && git config core.hooksPath "$(pwd)/.githooks"`.
4. `dotnet build Dictum.sln -c Release` must pass on the Mac before any Mac code is written: Dictum.Core, Dictum.UI and
   the tests are platform-neutral by design (SPEC §21; `ArchitectureTests` in tests/Dictum.Tests/PlatformTests.cs fails
   if Core or UI references a platform assembly). Dictum.Windows will not build on macOS; exclude it from the solution
   build on Mac (a solution filter or `-p:` condition, decide and record).
5. `dotnet test --project tests/Dictum.Tests -c Release` with `DOTNET_CLI_TELEMETRY_OPTOUT=1
   TESTINGPLATFORM_TELEMETRY_OPTOUT=1`; expect the Windows-only tests to skip (they are gated on
   `OperatingSystem.IsWindows()` or on a desktop; if any fail on macOS, that is finding number one: fix the gate, not the
   test).

## 4. What exists and what is missing

**Verified in the repo (19/10/2026):**
- `src/Dictum.Core` and `src/Dictum.UI` are the whole app bar the OS seam. They compile for any OS.
- `src/Dictum.Core/Platform/IPlatform.cs` is the seam: one interface plus `IHotkeyListener`, `IKeyMonitor`,
  `IStartupRegistration`, `IInstanceLock`, `IWindowFlags`, `ITrayHost`, `IPackageInfo`, `IRemovalSteps`, and
  `IInputSender`, `IClipboard` (src/Dictum.Core/Insert), `IAudioCapture` (src/Dictum.Core/Audio).
- `src/Dictum.Windows` is the reference implementation, laid out as `Audio/`, `Input/`, `Interop/`, `Shell/`,
  `WindowsPlatform.cs`, `Program.cs`. Mirror that layout in `src/Dictum.Mac`.
- `src/Dictum.Mac/README.md` maps every member to its macOS counterpart, with its own provenance marks. It is the
  starting design; this file refines it.
- Packages, all on nuget.org at the versions the solution pins: `Avalonia.Native` 11.3.22, `Whisper.net.Runtime` 1.9.1,
  `Whisper.net.Runtime.CoreML` 1.9.1, `LLamaSharp.Backend.Cpu` 0.27.0. There is **no** `LLamaSharp.Backend.Metal`
  package: the clean-up model runs on the CPU on Mac until the lane finds and verifies a Metal-capable backend inside the
  CPU package (inspect its `runtimes/osx-arm64` folder) or elsewhere. Record what you find in DECISIONS.
- Audio contract: capture hands 16 kHz mono float samples to `CaptureBuffer` (src/Dictum.Core/Audio/CaptureBuffer.cs),
  which does the hands-free pause segmentation; the platform only captures and resamples.
- Hotkeys use the Windows virtual-key codes everywhere (`HotkeyParser`, `Vk`, `HotkeySpec.UsesMouse`); the Mac layer
  maps macOS key codes to those codes once, at the tap.
- Tray states: `Idle, Disabled, Recording, Processing, Error` (`TrayState` in DictationController.cs); the icon bitmaps
  come from `TrayIconArt.Render(state, size, lightTaskbar)` in Core, so the Mac draws nothing new.

**Missing:** everything under `src/Dictum.Mac` except the README, the app bundle packaging, the CI job, the docs.

## 5. Design decisions (made 19/10/2026 by the orchestrator; change only with a recorded reason)

Principle: prefer .NET and Avalonia over native calls; where native is unavoidable prefer plain C APIs (P/Invoke with
`UnmanagedCallersOnly` callbacks) over Objective-C messaging; keep every native declaration under `src/Dictum.Mac/Interop`
exactly as Windows keeps them under `Interop/`.

| Member | Decision | Why |
|---|---|---|
| `Name` | "macOS" | spec |
| `ResolvePaths` | config and local both under `~/Library/Application Support/Dictum` (`config/`? no: settings.json, .env, consent.json at the top; `models/`, `logs/`, `recordings/` beside them, same names as Windows). Not `~/Library/Caches` for models: the system may purge Caches. **Recalled** purge behaviour. | one folder, visible in Finder, survives updates |
| `LegacyFolders` | paths that do not exist (there was never a Mac Dictate) | README |
| `Package` | not packaged: `IsPackaged` false, `RealPath` identity, `OtherInstall` null | no App Store build |
| `CreateHotkeyListener` / `CreateKeyMonitor` | one `CGEventTap` (CoreGraphics C API) on its own thread with a run loop; a listen-only tap for the monitor; an active tap for the hotkey when the matcher suppresses the key, otherwise listen-only. Map `kCGKeyboardEventKeycode` to the Windows VK codes with one table (unit-tested); mouse buttons from `otherMouseDown/Up`, `rightMouseDown/Up` to the same mouse VKs. Re-enable the tap on `kCGEventTapDisabledByTimeout`. **Recalled** API names. | mirrors WH_KEYBOARD_LL; permission: Input Monitoring, and Accessibility for an active tap |
| `IsKeyDown` | `CGEventSourceKeyState(kCGEventSourceStateCombinedSessionState, keycode)` with the reverse VK table. **Recalled.** | |
| `CreateInputSender` | post Cmd-V with `CGEventCreateKeyboardEvent` + `CGEventPost(kCGHIDEventTap)`; Unicode typing with `CGEventKeyboardSetUnicodeString` in chunks. **Recalled.** | mirrors SendInput |
| `Clipboard` | `NSPasteboard` through a small Objective-C messaging helper (`objc_msgSend`, `objc_getClass`, `sel_registerName` from `/usr/lib/libobjc.A.dylib`); snapshot/restore every type the general pasteboard holds; mark Dictum's own item with `org.nspasteboard.ConcealedType` and `org.nspasteboard.TransientType` so clipboard managers skip it. **Recalled** type names (the nspasteboard.org convention). | Avalonia's clipboard cannot set the opt-out types |
| `CreateCapture` / `ListCaptureDevices` | Core Audio **C** API: `AudioQueueNewInput` with a C callback, 16 kHz mono float requested from the queue (let Core Audio convert) or resample in `CaptureBuffer` if the device refuses; device list via `AudioObjectGetPropertyData` (`kAudioHardwarePropertyDevices`, input streams > 0). Not AVAudioEngine (needs Objective-C blocks from C#). **Recalled** API names. | plain C, no blocks; permission: Microphone, via `NSMicrophoneUsageDescription` in Info.plist |
| `Startup` | a LaunchAgent plist in `~/Library/LaunchAgents/au.kynni.dictum.plist` (`RunAtLoad`, program = the bundle's executable path), `Mechanism` = "login item (LaunchAgent)". Not `SMAppService` for the beta. `// simplification:` ceiling: the entry breaks if the user moves the app; retire when the app is signed and `SMAppService` is verified. | plain file I/O, works unsigned |
| `AcquireSingleInstance` | lock file under the local folder opened with `FileShare.None` (**Recalled**: .NET maps it to `flock` on Unix) plus a Unix domain socket for "show yourself"; pure .NET, no native. | |
| `WindowFlags` | on the Avalonia native handle (an `NSWindow`): `setLevel:` floating, `setIgnoresMouseEvents:` YES for the click-through overlays, `setCollectionBehavior:` can-join-all-spaces + stationary, and `setHidesOnDeactivate:` NO; windows are shown with Avalonia `ShowActivated = false`. `ForegroundWindow()` returns the frontmost app's PID (not a window) from `NSWorkspace.sharedWorkspace.frontmostApplication`; `ForegroundBounds` from `CGWindowListCopyWindowInfo` filtered to that PID, layer 0, first entry; `AppName` = `localizedName`; `TryActivate` = `activateWithOptions:`; `ReducedMotion` = `NSWorkspace ... accessibilityDisplayShouldReduceMotion`; `IsWindow` = the PID still runs; `Move` = `setFrameOrigin:` in points (convert from pixels with the screen's backing scale). All **Recalled.** | the Mac has no "foreground window" handle; the PID is the stable identity |
| `CreateTray` | Avalonia `TrayIcon` (cross-platform; **Recalled** that it maps to `NSStatusItem` under Avalonia.Native), icon from `TrayIconArt.Render`; the click opens the flyout at the status item's screen point. `Notify`: for the beta, show the balloon as a transient Avalonia panel near the status item; `UNUserNotificationCenter` is **Recalled** to require a signed bundle and crashes outside one. `// simplification:` retire when signed. | |
| `Open` | `Process.Start("open", target)` (stdlib) | |
| `Removal` | the LaunchAgent plist, then move the app bundle to `~/.Trash` after exit (a detached `sh` step, mirroring the Windows detached PowerShell step); `OpenUninstallSettings` never applies. | |
| `PlayCue` | `NSSound` via the messaging helper, or `afplay` on the shipped WAV; pick the one that never blocks. | |
| `EarlierBuildRunning` | false | |

Avalonia: the Mac project references `Avalonia.Native` and `Avalonia.Skia` (the Windows project references
`Avalonia.Win32` and `Avalonia.Skia`); `Program.cs` mirrors the Windows one, which calls `.UseWin32().UseSkia()` (src/Dictum.Windows/Program.cs:111-112,
**Verified**); the Mac equivalent is `.UseAvaloniaNative().UseSkia()` (**Recalled** method name).

Whisper: reference `Whisper.net.Runtime` and `Whisper.net.Runtime.CoreML`; the first run offers base.en as on Windows
(bundled in the app bundle, see §6). CoreML models are a separate encoder file per model (**Recalled**): if Whisper.net
needs them to use CoreML, ship only the CPU path in the beta and record it.

## 6. The app bundle and the release

- Layout: `Dictum.app/Contents/{Info.plist, MacOS/Dictum, MacOS/models/ggml-base.en.bin, Resources/Dictum.icns,
  Resources/THIRD-PARTY-NOTICES.txt}` with the self-contained publish output in `MacOS/`. `AppPaths.BundledModelsDir`
  is `Path.Combine(ExeDir, "models")` in Core (src/Dictum.Core/Settings/AppPaths.cs:53, **Verified**), so the model
  sits beside the executable under `MacOS/`, not under `Resources/`, unless Apple's bundle rules forbid data files
  there (check, record the source; if they do, add the Mac case in the platform layer, not in Core).
- Info.plist: `CFBundleIdentifier au.kynni.dictum`, `CFBundleName Dictum`, `CFBundleShortVersionString` from
  `Directory.Build.props` `<Version>`, `LSUIElement true` (menu-bar app, no Dock icon), `LSMinimumSystemVersion` (pick
  the lowest Avalonia 11.3 and .NET 10 support and record the source), `NSMicrophoneUsageDescription` (PROPOSED text:
  "Dictum listens only while you hold your hotkey, and keeps the audio on this Mac unless you turn on cloud speech." This
  replaces the earlier proposal, which was untrue with cloud speech on), `NSHumanReadableCopyright`
  "© 2026 Kynni Pty Ltd".
- Publish: `dotnet publish src/Dictum.Mac -c Release -r osx-arm64 --self-contained` (and again for `osx-x64` only if the owner asks for Intel); keep only the
  natives each RID needs (mirror the manifest/prune logic of `scripts/publish.ps1`; `pwsh` exists on GitHub's macOS
  runners, so a `-Mac` path in the existing script is acceptable; a bash script is also acceptable; one or the other, not
  both).
- (Superseded by section 0: Developer ID signing and notarisation, with credentials the owner holds.) Sign ad-hoc: `codesign --force --deep --sign - Dictum.app` (**Recalled**; Apple Silicon refuses unsigned arm64 code,
  ad-hoc needs no account). Verify with `codesign --verify --deep --strict` and `spctl` (expect Gatekeeper rejection:
  that is the documented beta limitation, not a failure).
- Zip with `ditto -c -k --keepParent Dictum.app Dictum-<version>-osx-arm64.zip` (**Recalled**: preserves bundle
  attributes; a plain zip can break the bundle).
- CI: add a `macos-latest` job to `.github/workflows/ci.yml` that builds, tests, publishes osx-arm64 (osx-x64 only if Intel is asked for), signs, zips and
  uploads artifacts; on a `v*` tag attach the zip and its SHA-256 line to the same GitHub Release the Windows job
  creates (the release step already uses `gh release upload --clobber`: do not create a second release). Mark Mac builds
  as beta in the release notes preamble.
- Model download in CI: reuse the pinned commit, size and SHA-256 that `scripts/download-model.ps1` holds for base.en;
  never add a second copy of those numbers.

## 7. Test plan on the Mac (real hardware; nothing here can be verified on CI)

1. Build, sign, open from Finder; note every Gatekeeper and permission prompt and the exact wording, for the release notes.
2. Grant Microphone, Input Monitoring, Accessibility; relaunch if macOS requires it (record whether it does).
3. Hold the hotkey in TextEdit, speak, release: text appears at the caret; the pill shows and never takes focus.
4. Hands-free: double-tap, speak two sentences with a pause, text arrives in parts; Esc stops and keeps what was typed.
5. Default hotkey: a Mac keyboard has no Right Ctrl; choose a default for Mac (PROPOSED: Right Option) and make the first-run
   hotkey page say so; the "Press your hotkey" box must show what macOS delivers.
6. Clipboard: copy something first, dictate, confirm the original clipboard is restored and the dictation does not appear
   in a clipboard manager (if one is installed).
7. Settings > Models: download small.en, then choose another model while it downloads: the question appears, nothing is
   cancelled silently (this is the behaviour the 19/10/2026 Store fix pinned; it lives in Core and must hold on Mac).
8. Login item on and off; quit and relaunch; single instance (open the app twice: the second launch shows the first).
9. Remove Dictum completely.
10. No Intel run is needed: Intel is not built for the beta unless a tester needs it (owner decision 22/10/2026, section 0). If Intel is
    added later, repeat 1 to 3 on an Intel Mac or say in the PR that none was available.

Every test leaves evidence in the PR: the log lines (never recordings), the permission wording, screenshots of prompts.

## 8. What to report back

In the PR body: what works, what does not, every `// simplification:` added, every PROPOSED string verbatim, every
Recalled claim above that turned out wrong (with the correct fact and its source), the suite totals from the Mac run, and a
Not verified list. The orchestrator lenses the PR on Windows (static review, the Mac tests cannot run there) and asks the
Mac session to re-test on the exact merging head.
