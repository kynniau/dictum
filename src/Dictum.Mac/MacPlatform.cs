using System.Diagnostics;
using Dictum.Audio;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Mac.Audio;
using Dictum.Mac.Input;
using Dictum.Mac.Shell;
using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.Mac;

/// <summary>
/// Dictum on macOS (SPEC §21; docs/MAC-HANDOVER.md; docs/DECISIONS.md "Dictum for macOS", 22/10/2026): a CGEventTap for the
/// hotkey and the key monitor, CGEvent posting for Cmd-V, NSPasteboard with the nspasteboard.org opt-out types, Core Audio
/// (AudioQueue) capture, SMAppService for the login item, a lock file plus a Unix socket for the single instance, NSWindow
/// flags for the overlays, Avalonia's TrayIcon on the menu bar, UNUserNotificationCenter for notices. Every native
/// declaration lives under Interop/.
/// </summary>
public sealed class MacPlatform : IPlatform, IDisposable
{
    private readonly ILog _log;
    private readonly Lazy<Pasteboard> _clipboard = new(() => new Pasteboard());
    private readonly Lazy<IStartupRegistration> _startup;
    private readonly Lazy<IRemovalSteps> _removal;

    /// <param name="log">Where platform warnings go before the app's own log exists (the CLI passes its log).</param>
    public MacPlatform(ILog? log = null, string? exeDir = null)
    {
        _log = log ?? NullLog.Instance;
        ExeDir = exeDir ?? AppContext.BaseDirectory;
        ExePath = Path.Combine(ExeDir, "Dictum");
        Bundle = AppBundle.Containing(ExeDir);
        Package = new MacPackageInfo();
        _startup = new(() => new LoginItem(_log, Bundle));
        _removal = new(() => new RemovalSteps(_log, Bundle, ExeDir, () => _startup.Value));
        _access = new MacInputAccess(Open, _log);
        _posted = new PostedEvents();
    }

    public string Name => "macOS";

    public PlatformWords Words => PlatformWords.Mac;

    /// <summary>The Accessibility grant: checked silently, asked for with the system prompt and the Settings page (<see cref="MacInputAccess"/>).</summary>
    public IInputAccess InputAccess => _access;

    private readonly MacInputAccess _access;
    private readonly PostedEvents _posted;

    public string ExeDir { get; }

    public string ExePath { get; }

    /// <summary>Dictum.app when running from the shipped bundle; null from a build tree.</summary>
    internal AppBundle? Bundle { get; }

    internal MacPackageInfo Package { get; }

    IPackageInfo IPlatform.Package => Package;

    /// <summary>
    /// Both roots are ~/Library/Application Support/Dictum (settings.json, consent.json and .env at the top; models/, logs/,
    /// calibration/ beside them): one folder the user can find in Finder, that survives updates, and that the system never
    /// purges as it may ~/Library/Caches. HOME decides which user's folder, so a scratch profile can be tested as the
    /// Windows build tests with APPDATA.
    /// </summary>
    public AppPaths ResolvePaths(string? configOverride) => AppPaths.ForRoots(ApplicationSupport, ApplicationSupport, ExeDir, configOverride);

    /// <summary>There was never a Mac build of the earlier app (Dictate): folders that do not exist, so the migration has nothing to do.</summary>
    public LegacyFolders LegacyFolders() => new(
        Path.Combine(ApplicationSupport, AppPaths.LegacyAppFolder),
        Path.Combine(ApplicationSupport, AppPaths.LegacyAppFolder));

    /// <summary>~/Library/Application Support, from HOME (every macOS login shell sets it) or the user profile.</summary>
    internal static string ApplicationSupport
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support");
        }
    }

    public IHotkeyListener CreateHotkeyListener(HotkeyMatcher matcher, Action<HookEvent> post, ILog log) => new EventTapHotkey(matcher, post, log, lost: _access.ReportRevoked, posted: () => _posted.Count);

    public IKeyMonitor CreateKeyMonitor(Action<RawKeyEvent> onKey, ILog log) => new EventTapKeyMonitor(onKey, log, lost: _access.ReportRevoked, posted: () => _posted.Count);

    public bool IsKeyDown(int vk) => KeyState.IsDown(vk);

    public IInputSender CreateInputSender(ILog log) => new CGEventSender(log, posted: _posted);

    public IClipboard Clipboard => _clipboard.Value;

    public IAudioCapture CreateCapture(ILog log) => new AudioQueueCapture(log);

    public IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => CaptureDevices.List();

    public IStartupRegistration Startup => _startup.Value;

    public IInstanceLock AcquireSingleInstance(TimeSpan wait)
    {
        var folder = ResolvePaths(null).LocalDir;
        return SingleInstance.Acquire(Path.Combine(folder, SingleInstance.LockFileName), Path.Combine(folder, SingleInstance.SocketFileName), wait, _log);
    }

    public IWindowFlags WindowFlags { get; } = new WindowFlags();

    public ITrayHost CreateTray(Action<ScreenPoint?> activated, ILog log) => new TrayHost(activated, log, Bundle);

    /// <summary>
    /// <c>open</c> (stdlib Process, no native call): a URL goes to its scheme's handler, a folder to Finder, a file to its
    /// default app. A file with no handler (open exits 1) is shown in the default text editor (<c>open -t</c>), as Windows
    /// falls back to Notepad.
    /// </summary>
    public void Open(string target)
    {
        var isPath = !target.Contains(':', StringComparison.Ordinal) || Path.IsPathFullyQualified(target);
        if (isPath && !Path.HasExtension(target)) Directory.CreateDirectory(target); // folders may not exist yet
        if (Run("open", target) == 0) return;
        if (isPath && File.Exists(target) && Run("open", "-t", target) == 0) return;
        throw new FileNotFoundException("No app opens this on this Mac.", target);
    }

    private static int Run(string program, params string[] arguments)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{program} did not start.");
        process.WaitForExit();
        return process.ExitCode;
    }

    public IRemovalSteps Removal => _removal.Value;

    public void PlayCue(SoundCue cue) => SoundCues.Play(cue);

    /// <summary>No earlier build ever ran on a Mac.</summary>
    public bool EarlierBuildRunning() => false;

    public void Dispose()
    {
        if (_clipboard.IsValueCreated) _clipboard.Value.Dispose();
    }
}
