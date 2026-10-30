# Dictum.Mac

The macOS platform (SPEC §21): `MacPlatform` implements `Dictum.Platform.IPlatform` and `Program` is the entry point, the
executable inside `Dictum.app/Contents/MacOS` (built by `scripts/publish-mac.sh`), exactly as `src/Dictum.Windows` is on
Windows. `Dictum.Core` and `Dictum.UI` needed four small changes for the Mac (docs/DECISIONS.md, "Dictum for macOS",
22-23/10/2026); a test (`ArchitectureTests`) still fails if either references a platform assembly or declares a native call.

Plain `net10.0`, RID `osx-arm64`, assembly `Dictum.Mac` (the tests load it beside Dictum.Windows's `Dictum`; the bundle
script renames the apphost to `Dictum`). Every native declaration is under `Interop/`: `ObjC.cs` (the Objective-C runtime:
classes, selectors, `objc_msgSend` per signature, C#-implemented classes, autorelease pools), `CoreFoundation.cs` (CF types
and run loops) and one file per framework beside them. Apple Silicon only for the beta: on arm64 every message goes through
`objc_msgSend`; an x64 build would need the `_stret` variant for large struct returns.

Permissions the app needs: Microphone (Core Audio prompts for it; `NSMicrophoneUsageDescription` is in
packaging/mac/Info.plist) and Accessibility (the event taps and the posted Cmd-V; the SDK's CGEvent.h: taps "may only
receive key up and down events if … the caller is enabled for assistive device access"). Both taps are active taps, so
Input Monitoring is not asked for (owner decision c, 22/10/2026).

| IPlatform member | Windows (Dictum.Windows) | macOS (this project) |
|---|---|---|
| `Name` | "Windows" | "macOS" |
| `ResolvePaths(configOverride)` | `%APPDATA%\Dictum`, `%LOCALAPPDATA%\Dictum` | both `~/Library/Application Support/Dictum` (from `HOME`): settings.json, consent.json and .env at the top, models/, logs/, calibration/ beside them |
| `LegacyFolders()` | `%APPDATA%\Dictate`, `%LOCALAPPDATA%\Dictate` | `~/Library/Application Support/Dictate`, which never exists (there was no Mac build of the earlier app) |
| `Package` (`IPackageInfo`) | `GetCurrentPackageFullName`; MSIX redirection | `MacPackageInfo`: never packaged, paths as they are, no other install (the App Store train comes later) |
| `CreateHotkeyListener(matcher, post, log)` | WH_KEYBOARD_LL hook on its own thread, plus WH_MOUSE_LL while the spec uses the mouse | `EventTapHotkey`: an active CGEventTap at kCGSessionEventTap on its own CFRunLoop thread; `KeyCodeMap` turns kVK_* codes into the Windows virtual keys the matcher uses; modifiers come from flagsChanged with left/right state; mouse buttons when the spec uses them; re-enabled after a timeout; swallows only what the matcher says |
| `CreateKeyMonitor(onKey, log)` | the same hooks, passing every key and mouse button (never the left one) | `EventTapKeyMonitor`: the same tap, passing every event through |
| `IsKeyDown(vk)` | `GetAsyncKeyState` | `KeyState`: `CGEventSourceKeyState` / `CGEventSourceButtonState` through the reverse map |
| `CreateInputSender(log)` (`IInputSender`) | SendInput (Ctrl+V, Unicode typing) | `CGEventSender`: Cmd-V and Unicode typing (20 UTF-16 units per event) from a private event source, posted to kCGHIDEventTap, each event carrying Dictum's marker so the taps ignore it; Accessibility checked first |
| `Clipboard` (`IClipboard`) | raw Win32 clipboard with the history opt-out formats | `Pasteboard`: NSPasteboard; the snapshot keeps public.utf8-plain-text, public.rtf and public.html; a dictation is written with `org.nspasteboard.ConcealedType` and `org.nspasteboard.TransientType` (nspasteboard.org) and `org.nspasteboard.source`, and current-host-only (`prepareForNewContentsWithOptions:` with `NSPasteboardContentsCurrentHostOnly`) so Universal Clipboard does not carry it to other devices; allowing clipboard history lifts all of these, as it lifts the Windows cloud-clipboard opt-out |
| `Credentials` (`ICredentialStore`) | Credential Manager generic credentials `Dictum/{VARIABLE}/{id}`, CRED_PERSIST_LOCAL_MACHINE (`CredentialManagerStore`) | `NoCredentialStore` until the K-Mac lane (30/10/2026 brief §3): the login Keychain, generic password, service `au.kynni.dictum`, account `{VARIABLE}/{id}`, not synchronisable; values only, `SecretVault` keeps the labels |
| `CreateCapture(log)` (`IAudioCapture`) | WASAPI through NAudio | `AudioQueueCapture`: Core Audio's AudioQueue (C API, an `UnmanagedCallersOnly` callback), 16 kHz mono float asked for and the recorded format read back; `CaptureBuffer` meters, resamples if needed and cuts hands-free parts |
| `ListCaptureDevices()` | WASAPI capture endpoints | `CaptureDevices`: AudioObject properties (devices with input streams, name, UID, default); `Resolve` by UID, then name, then default |
| `Startup` (`IStartupRegistration`) | HKCU Run key, or the package's StartupTask | `LoginItem`: SMAppService's main-app service (register / unregister / status); needs the installed Dictum.app |
| `AcquireSingleInstance(wait)` (`IInstanceLock`) | named mutex `Local\Dictum.SingleInstance` and an "already running" event | `SingleInstance`: a lock file (FileShare.None) and a Unix domain socket under the app folder, pure .NET |
| `WindowFlags` (`IWindowFlags`) | WS_EX_NOACTIVATE / TRANSPARENT / LAYERED / TOOLWINDOW / TOPMOST; `GetForegroundWindow`; SPI_GETCLIENTAREAANIMATION | `WindowFlags`: the NSWindow behind Avalonia's handle (level, ignores mouse events, collection behaviour); the frontmost app's process id stands for the foreground window; `CGWindowListCopyWindowInfo` for its bounds; `accessibilityDisplayShouldReduceMotion` |
| `CreateTray(activated, log)` (`ITrayHost`) | Shell_NotifyIcon with NOTIFYICON_VERSION_4 | `TrayHost`: Avalonia's `TrayIcon` (an NSStatusItem under Avalonia.Native) drawn from `TrayIconArt`; the click point from `NSEvent.mouseLocation`; notices through UNUserNotificationCenter inside the bundle, the log outside it |
| `Open(target)` | ShellExecute | `open` (a URL, a folder, a file; `open -t` for a file with no handler) |
| `Removal` (`IRemovalSteps`) | Start menu shortcut, Run key, a detached PowerShell step deleting only the files manifest.txt lists | `RemovalSteps`: the login item, then a detached `sh` step that moves Dictum.app to the Trash after exit (paths through the environment, never the script's text); refuses anything that is not the verified bundle |
| `PlayCue(cue)` | `PlaySound` from memory | `SoundCues`: NSSound from the same two tones (`Dictum.Audio.CueTones`, shared with Windows) |
| `EarlierBuildRunning()` | the earlier build's mutex exists | always false |

Avalonia: this project references `Avalonia.Native` and `Avalonia.Skia`; `Program.cs` calls `.UseAvaloniaNative().UseSkia()`.
Whisper and llama: `Whisper.net.Runtime` and `LLamaSharp.Backend.Cpu`, whose arm64 builds carry ggml's Metal backend
(`libggml-metal*.dylib`), so the "CPU" runtime uses the GPU on Apple Silicon; no Core ML (it needs a separate encoder file
per model that the catalogue does not carry). `Program.Main` sets `HotkeyNames.Dialect` to Mac (Right Option is the default
hotkey; Alt and Win read Option and Command) and tells `WhisperEngine` there is no Vulkan loader.

Build and run from source: `dotnet build Dictum.sln -c Release` then `dotnet run --project src/Dictum.Mac -- --selftest`
(from a build tree the login item and notifications are off, as the table says); the bundle: `scripts/publish-mac.sh`.
Tests: `tests/Dictum.Tests/Mac*Tests.cs` (pure parts everywhere; the ones that need the Mac, Accessibility or the microphone
skip with a reason unless `DICTUM_UI_TESTS=1` / `DICTUM_AUDIO_TESTS=1`).

Installing a build (the words below were approved by the owner on 23/10/2026 for the release notes and, once the beta ships,
the public README, which says nothing about the Mac until then): the beta is for Apple Silicon Macs on macOS 15 or
later. Download `Dictum-<version>-osx-arm64.zip`, check it against `SHA256SUMS-mac.txt` (`shasum -a 256 <zip>`), unzip it
and move `Dictum.app` to Applications. A build signed with the owner's Developer ID certificate and notarised opens normally;
an ad-hoc build is refused by Gatekeeper until allowed in System Settings > Privacy & Security. The app lives in the menu bar
(no Dock icon). On first use macOS asks for Microphone access and for Accessibility access (to see the hotkey and to paste
into other apps), and for approval in System Settings > General > Login Items if launch at login is on. The base.en speech
model is inside the app. Building it: install the .NET 10 SDK (`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s --
--channel 10.0`, then put its folder on `PATH`), `dotnet build Dictum.sln -c Release`, then `scripts/publish-mac.sh`; it
writes `dist/Dictum.app`, `dist/Dictum-<version>-osx-arm64.zip` and `dist/SHA256SUMS-mac.txt` (`DICTUM_MAC_DIST` chooses the
folder). The script reads two names from the environment, `DICTUM_MAC_SIGN_IDENTITY` and `DICTUM_MAC_NOTARY_PROFILE`, and
never a certificate, key or password.
