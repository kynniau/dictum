# Dictum.Mac (empty slot)

The macOS platform is not built yet (SPEC §21: Windows ships first). This folder holds no project on purpose: when it is
built, `Dictum.Mac` implements `Dictum.Platform.IPlatform` (src/Dictum.Core/Platform/IPlatform.cs) and is the macOS
entry point, exactly as `src/Dictum.Windows` is on Windows. `Dictum.Core` and `Dictum.UI` need no change: a test
(`ArchitectureTests`) fails if either references a platform assembly or declares a native call.

Provenance of the macOS names below: the ones SPEC §21 names (CGEventTap, AVAudioEngine, NSPasteboard with Cmd-V,
NSStatusItem, SMAppService, NSPanel) come from the spec. Everything marked *(recalled)* is from memory, not checked
against Apple's documentation, and must be verified before it is used.

Permissions the app will need: Microphone, Accessibility (to post Cmd-V) and Input Monitoring (for the event tap)
(SPEC §21).

| IPlatform member | Windows (Dictum.Windows) | macOS counterpart |
|---|---|---|
| `Name` | "Windows" | "macOS" |
| `ResolvePaths(configOverride)` | `%APPDATA%\Dictum`, `%LOCALAPPDATA%\Dictum` | `~/Library/Application Support/Dictum` for both, or a separate caches folder for models *(recalled)* |
| `LegacyFolders()` | `%APPDATA%\Dictate`, `%LOCALAPPDATA%\Dictate` | none: there was no macOS Dictate (return folders that do not exist) |
| `Package` (`IPackageInfo`) | `GetCurrentPackageFullName`; MSIX redirection | App Store sandbox container detection *(recalled: the sandbox redirects to `~/Library/Containers/<bundle id>`)*; `OtherInstall()` checks for the other build |
| `CreateHotkeyListener(matcher, post, log)` | WH_KEYBOARD_LL hook on its own thread | CGEventTap on its own run loop thread; key codes mapped to the virtual-key codes `HotkeyParser` uses |
| `CreateKeyMonitor(onKey, log)` | the same hook, passing every key | a listen-only CGEventTap |
| `IsKeyDown(vk)` | `GetAsyncKeyState` | `CGEventSourceKeyState` *(recalled)* |
| `CreateInputSender(log)` (`IInputSender`) | SendInput (Ctrl+V, Unicode typing) | CGEvent keyboard events posting Cmd-V *(recalled: `CGEventCreateKeyboardEvent`, `CGEventPost`)* |
| `Clipboard` (`IClipboard`) | raw Win32 clipboard with the history opt-out formats | NSPasteboard; the history opt-out has a macOS convention *(recalled: `org.nspasteboard.TransientType` / `ConcealedType`)* |
| `CreateCapture(log)` (`IAudioCapture`) | WASAPI through NAudio | AVAudioEngine input node, resampled to 16 kHz mono by `CaptureBuffer` |
| `ListCaptureDevices()` | WASAPI capture endpoints | AVCaptureDevice / Core Audio device list *(recalled)* |
| `Startup` (`IStartupRegistration`) | HKCU Run key, or the package's StartupTask | SMAppService (login item) |
| `AcquireSingleInstance(wait)` (`IInstanceLock`) | named mutex `Local\Dictum.SingleInstance` and an "already running" event | a lock file plus a distributed notification, or `NSRunningApplication` *(recalled)* |
| `WindowFlags` (`IWindowFlags`) | WS_EX_NOACTIVATE / TRANSPARENT / LAYERED / TOOLWINDOW / TOPMOST through Avalonia's style callback; `GetForegroundWindow`; SPI_GETCLIENTAREAANIMATION | NSPanel non-activating style, `ignoresMouseEvents`, a floating window level; the frontmost application; Reduce Motion *(recalled: `accessibilityDisplayShouldReduceMotion`)* |
| `CreateTray(activated, log)` (`ITrayHost`) | Shell_NotifyIcon with NOTIFYICON_VERSION_4 | NSStatusItem; notifications through UserNotifications *(recalled)* |
| `Open(target)` | ShellExecute | NSWorkspace open *(recalled)* |
| `Removal` (`IRemovalSteps`) | Start menu shortcut, Run key, a detached PowerShell step deleting only the files the program folder's manifest.txt lists; the earlier build's leftovers after a migration | the login item, and the app bundle moved to the Trash after exit *(recalled)*; App Store builds are removed by the user |
| `PlayCue(cue)` | `PlaySound` from memory | NSSound *(recalled)* |
| `EarlierBuildRunning()` | the earlier build's mutex `Local\Dictate.SingleInstance` exists | always false: there was no earlier macOS build |

Avalonia: the macOS app project references `Avalonia.Native` (Avalonia's macOS backend) instead of `Avalonia.Win32`
*(package name recalled; verify on nuget.org at the same version as the rest, 11.3.22)*. Whisper.net ships a CoreML
runtime for macOS (SPEC §21, verified 15/10/2026); a LLamaSharp Metal backend package was not found, so the clean-up
model's GPU use on a Mac is unverified and CPU is assumed until measured.
