using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Settings;

namespace Dictum.Platform;

/// <summary>
/// The single seam between Dictum's shared code and the operating system (SPEC §21). Dictum.Core and Dictum.UI reach
/// every platform API through this interface and the service interfaces it hands out; only Dictum.Windows (and later
/// Dictum.Mac) implement it. src/Dictum.Mac/README.md lists every member with its macOS counterpart.
/// </summary>
public interface IPlatform
{
    /// <summary>"Windows" or "macOS", for the log, --selftest and the About page.</summary>
    string Name { get; }

    /// <summary>The words the approved copy uses for this computer and its system: "this PC" / "this Mac" and the rest (Mac copy block, 23/10/2026).</summary>
    PlatformWords Words { get; }

    // ---- App paths -------------------------------------------------------------------------------------------------

    /// <summary>Where Dictum keeps its files: settings and secrets (config), models, logs and recordings (local).</summary>
    /// <param name="configOverride">--config &lt;dir&gt;, or null for the default.</param>
    AppPaths ResolvePaths(string? configOverride);

    /// <summary>The folders the earlier build, named Dictate, used: the source of the one-time migration (SPEC §21).</summary>
    LegacyFolders LegacyFolders();

    /// <summary>Whether this copy runs as an installed package (MSIX / Microsoft Store) and what that changes (SPEC §18.2).</summary>
    IPackageInfo Package { get; }

    // ---- Hotkey listener ---------------------------------------------------------------------------------------------

    /// <summary>A system-wide hotkey listener (Windows: a WH_KEYBOARD_LL hook on its own thread). Not started yet.</summary>
    IHotkeyListener CreateHotkeyListener(HotkeyMatcher matcher, Action<HookEvent> post, ILog log);

    /// <summary>Reports every key the system delivers, swallowing none (the "Press your hotkey" box and --selftest).</summary>
    IKeyMonitor CreateKeyMonitor(Action<RawKeyEvent> onKey, ILog log);

    /// <summary>Whether a key is physically down now (drops modifiers whose key-up was lost).</summary>
    bool IsKeyDown(int vk);

    // ---- Input injector and clipboard --------------------------------------------------------------------------------

    IInputSender CreateInputSender(ILog log);

    IClipboard Clipboard { get; }

    // ---- Audio capture factory ---------------------------------------------------------------------------------------

    IAudioCapture CreateCapture(ILog log);

    IReadOnlyList<AudioDeviceInfo> ListCaptureDevices();

    // ---- Startup registration and single instance --------------------------------------------------------------------

    /// <summary>Launch at login: a Run key value unpackaged, the package's startup task when packaged (SPEC §18.2).</summary>
    IStartupRegistration Startup { get; }

    /// <summary>Takes the single-instance lock, or signals the copy already running (SPEC §8).</summary>
    IInstanceLock AcquireSingleInstance(TimeSpan wait);

    // ---- Window-flag applier ------------------------------------------------------------------------------------------

    IWindowFlags WindowFlags { get; }

    // ---- Tray host ------------------------------------------------------------------------------------------------

    /// <summary>The notification-area (menu bar) icon. <paramref name="activated"/> runs on the UI thread with the click point.</summary>
    ITrayHost CreateTray(Action<ScreenPoint?> activated, ILog log);

    // ---- Shell ------------------------------------------------------------------------------------------------------

    /// <summary>Opens a file, a folder or a URI (https:, mailto:, ms-settings:) with the system's handler.</summary>
    void Open(string target);

    /// <summary>The platform's part of "Remove Dictum completely" (SPEC §17).</summary>
    IRemovalSteps Removal { get; }

    /// <summary>Plays the short start or stop sound (setting sounds.enabled). Never blocks.</summary>
    void PlayCue(SoundCue cue);

    /// <summary>
    /// Whether the earlier build (named Dictate) is running now: the migration never moves its files under it (SPEC §21,
    /// amended). Windows: its single-instance mutex exists.
    /// </summary>
    bool EarlierBuildRunning();
}

/// <summary>The earlier build's folders (it was named Dictate before the rename).</summary>
public sealed record LegacyFolders(string ConfigDir, string LocalDir);

public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public ScreenPoint Centre => new(X + Width / 2, Y + Height / 2);
}

/// <summary>One key event exactly as the system delivered it.</summary>
/// <param name="Vk">The virtual-key code (the hotkey parser's codes; macOS maps its key codes to these).</param>
/// <param name="Injected">Synthesised by a program rather than typed.</param>
public readonly record struct RawKeyEvent(int Vk, bool Down, bool Injected);

public enum SoundCue { Start, Stop }

/// <summary>A system-wide hotkey listener; calls its post action from its own thread, never blocking.</summary>
public interface IHotkeyListener : IDisposable
{
    /// <summary>
    /// When false a new press of the hotkey passes through untouched and Esc is not watched; a press already under way
    /// still finishes as it began, its release swallowed if its press was, and reported (HotkeyMatcher).
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>Installs the listener; false if the system refused. A hotkey change replaces the listener (AppShell).</summary>
    bool Start();
}

/// <summary>Passes every key through and reports it.</summary>
public interface IKeyMonitor : IDisposable
{
    bool Start();
}

public interface IStartupRegistration
{
    /// <summary>"Windows Run key" or "startup task" (packaged), for the Settings page.</summary>
    string Mechanism { get; }

    bool IsEnabled();

    /// <summary>Null when done, else why it could not be changed (for example turned off in Task Manager).</summary>
    Task<string?> SetEnabledAsync(bool enabled);
}

public enum InstanceOutcome
{
    /// <summary>This process holds the lock and runs.</summary>
    Owned,

    /// <summary>Another copy is running; it was asked to show itself.</summary>
    SignalledRunningCopy,

    /// <summary>Another copy held the lock for the whole wait without answering (starting slowly or still quitting).</summary>
    TimedOut,
}

public interface IInstanceLock : IDisposable
{
    InstanceOutcome Outcome { get; }

    /// <summary>Calls <paramref name="onSignal"/> (on a worker thread) whenever a later launch signals this copy.</summary>
    IDisposable ListenForOtherLaunches(Action onSignal);
}

/// <summary>
/// Flags for windows that must never take focus: the pill, the orb and the screen-edge glow (SPEC §7, §21). The paste
/// goes to the foreground window, so these may never become it.
/// </summary>
public interface IWindowFlags
{
    /// <summary>
    /// The window styles an overlay keeps every time the UI framework recomputes them (Win32: no-activate, click-through,
    /// topmost, tool window, layered). Identity on platforms without such styles.
    /// </summary>
    (uint Style, uint ExStyle) OverlayStyles(uint style, uint exStyle);

    /// <summary>Applies the overlay flags to a native window now, before its first show.</summary>
    void MakeOverlay(nint handle);

    /// <summary>Moves a native window without activating it (physical pixels).</summary>
    void Move(nint handle, int x, int y);

    /// <summary>The window that would receive a paste now (0 if none).</summary>
    nint ForegroundWindow();

    /// <summary>The foreground window's screen rectangle, or null when there is none.</summary>
    ScreenRect? ForegroundBounds();

    /// <summary>The system's "reduce motion" / animation-effects setting: true means no pulse and no fades.</summary>
    bool ReducedMotion { get; }

    /// <summary>A short name for the app that owns a window ("Outlook"), or null.</summary>
    string? AppName(nint window);

    bool IsWindow(nint handle);

    /// <summary>Hands the foreground back to a window (Fix last dictation → Paste fix). False when the system refused.</summary>
    bool TryActivate(nint handle);
}

public interface ITrayHost : IDisposable
{
    void SetState(TrayState state);

    void SetTooltip(string text);

    /// <summary>A system notification (balloon / banner).</summary>
    void Notify(string title, string text, bool warning);
}

public interface IPackageInfo
{
    /// <summary>True when running from an installed package (MSIX).</summary>
    bool IsPackaged { get; }

    /// <summary>The package family name when packaged, else null.</summary>
    string? FamilyName { get; }

    /// <summary>
    /// Where a file under the user's app-data folder really is on disk: packaged writes are redirected by the system to a
    /// per-package folder, so Settings → Storage shows that one (SPEC §18.2). Unpackaged: the path itself.
    /// </summary>
    string RealPath(string path);

    /// <summary>A warning when the other build (Store or direct download) is installed too, which is unsupported; else null.</summary>
    string? OtherInstall();
}

/// <summary>The platform's steps in "Remove Dictum completely" (SPEC §17, §18.2).</summary>
public interface IRemovalSteps
{
    /// <summary>
    /// What the platform adds to the removal list: the shortcut, the launch-at-login entry, and the program's own files
    /// (those its manifest.txt lists and that are still there unchanged), then its folder if that ends up empty.
    /// </summary>
    IReadOnlyList<Storage.OwnedLocation> PlatformLocations();

    /// <summary>Why the program's files will not be removed (packaged, no manifest.txt, a source tree…), or null when they will.</summary>
    string? ProgramFolderProblem();

    /// <summary>Removes the Start menu shortcut and the launch-at-login entry; returns what could not be removed.</summary>
    IReadOnlyList<string> RemoveShortcutAndStartup();

    /// <summary>
    /// What <see cref="RemoveShortcutAndStartup"/> removes, as the Storage page's "Remove everything" sentence names it, lower
    /// case and without a full stop: "the Start menu shortcut and the launch-at-login entry" on Windows, "the login item" on macOS.
    /// </summary>
    string StartupItemsPhrase { get; }

    /// <summary>
    /// Starts a detached step that, after this process exits, deletes the files <paramref name="folder"/>'s manifest.txt
    /// lists (same name and size), the folders that leaves empty, and the folder itself only if it is then empty (SPEC §17,
    /// amended). False (with the reason) when it is not done.
    /// </summary>
    bool ScheduleProgramFolderRemoval(string folder, out string? reason);

    /// <summary>Packaged builds cannot remove themselves: opens the system page where the package is uninstalled.</summary>
    void OpenUninstallSettings();

    /// <summary>
    /// What the earlier build (Dictate) still has installed after its data moved to Dictum: its launch-at-login entry, Start
    /// menu shortcut and program folder (that folder only when it is exactly where install.ps1 put it and holds
    /// Dictate.exe). One line per item, for the offer after a migration; empty when there is nothing.
    /// </summary>
    IReadOnlyList<string> EarlierBuildLeftovers();

    /// <summary>Removes what <see cref="EarlierBuildLeftovers"/> lists; returns what could not be removed.</summary>
    IReadOnlyList<string> RemoveEarlierBuild();
}
