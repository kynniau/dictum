using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Windows.Audio;
using Dictum.Windows.Input;
using Dictum.Windows.Interop;
using Dictum.Windows.Shell;

namespace Dictum.Windows;

/// <summary>
/// Dictum on Windows 10/11 (SPEC §21): a WH_KEYBOARD_LL hook, SendInput and the Win32 clipboard, WASAPI capture through
/// NAudio, the HKCU Run key or the package's startup task, a named mutex, Win32 overlay styles, Shell_NotifyIcon.
/// </summary>
public sealed class WindowsPlatform : IPlatform, IDisposable
{
    private readonly ILog _log;
    private readonly Lazy<Win32Clipboard> _clipboard = new(() => new Win32Clipboard());
    private readonly Lazy<IStartupRegistration> _startup;
    private readonly Lazy<IRemovalSteps> _removal;

    /// <param name="log">Where platform warnings go before the app's own log exists (the CLI passes its log).</param>
    public WindowsPlatform(ILog? log = null, string? exeDir = null)
    {
        _log = log ?? NullLog.Instance;
        ExeDir = exeDir ?? AppContext.BaseDirectory;
        ExePath = Path.Combine(ExeDir, "Dictum.exe");
        Package = PackageInfo.Detect(_log, Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
            Folder("APPDATA", Environment.SpecialFolder.ApplicationData));
        _startup = new(() => Package.IsPackaged ? new PackagedStartup(_log) : new RunKeyStartup(new RegistryRunKey(), ExePath));
        _removal = new(() => new RemovalSteps(new RegistryRunKey(), ExeDir, ExePath, Package.IsPackaged,
            Folder("APPDATA", Environment.SpecialFolder.ApplicationData), Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData), _log,
            setup: new RegistrySetupRecord()));
    }

    public string Name => "Windows";

    public PlatformWords Words => PlatformWords.Windows;

    /// <summary>Windows has no Accessibility grant: a low-level hook installs for any process (SPEC §21). Nothing to ask for, nothing shown.</summary>
    public IInputAccess InputAccess => InputAccessNotNeeded.Instance;

    public string ExeDir { get; }

    public string ExePath { get; }

    public PackageInfo Package { get; }

    IPackageInfo IPlatform.Package => Package;

    /// <summary>
    /// %APPDATA%\Dictum and %LOCALAPPDATA%\Dictum, from the environment variables (what the README and install.ps1 use, so
    /// a scratch profile can be tested), falling back to the known-folder API when they are unset.
    /// </summary>
    public AppPaths ResolvePaths(string? configOverride) =>
        AppPaths.ForRoots(Folder("APPDATA", Environment.SpecialFolder.ApplicationData), Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
            ExeDir, configOverride);

    public LegacyFolders LegacyFolders() => new(
        Path.Combine(Folder("APPDATA", Environment.SpecialFolder.ApplicationData), AppPaths.LegacyAppFolder),
        Path.Combine(Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData), AppPaths.LegacyAppFolder));

    private static string Folder(string variable, Environment.SpecialFolder fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : Environment.GetFolderPath(fallback);
    }

    public IHotkeyListener CreateHotkeyListener(HotkeyMatcher matcher, Action<HookEvent> post, ILog log) => new InputHook(matcher, post, log);

    public IKeyMonitor CreateKeyMonitor(Action<RawKeyEvent> onKey, ILog log) => new KeyMonitor(onKey, log);

    public bool IsKeyDown(int vk) => Native.IsKeyDown(vk);

    public IInputSender CreateInputSender(ILog log) => new SendInputSender(log);

    public IClipboard Clipboard => _clipboard.Value;

    public IAudioCapture CreateCapture(ILog log) => new WasapiCapture(log);

    public IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => CaptureDevices.List();

    public IStartupRegistration Startup => _startup.Value;

    public IInstanceLock AcquireSingleInstance(TimeSpan wait) => SingleInstance.Acquire(SingleInstance.MutexName, SingleInstance.EventName, wait);

    public IWindowFlags WindowFlags { get; } = new WindowFlags();

    public ITrayHost CreateTray(Action<ScreenPoint?> activated, ILog log) => new TrayHost(activated, log);

    public void Open(string target)
    {
        var isPath = !target.Contains(':', StringComparison.Ordinal) || Path.IsPathFullyQualified(target);
        if (isPath && !Path.HasExtension(target)) Directory.CreateDirectory(target); // folders may not exist yet
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) when (isPath && File.Exists(target))
        {
            // A .json with no handler: Notepad, by full path (a bare name would find a notepad.exe beside Dictum.exe first).
            var notepad = SystemPrograms.Find(SystemPrograms.Root, @"System32\notepad.exe") ?? throw new FileNotFoundException("Notepad is not installed.", target);
            Process.Start(new ProcessStartInfo(notepad, $"\"{target}\"") { UseShellExecute = false })?.Dispose();
        }
    }

    public IRemovalSteps Removal => _removal.Value;

    public void PlayCue(SoundCue cue) => SoundCues.Play(cue);

    /// <summary>The earlier build's single-instance mutex (Dictate's Program.cs named it Local\Dictate.SingleInstance) exists while it runs.</summary>
    public bool EarlierBuildRunning()
    {
        if (!Mutex.TryOpenExisting(EarlierBuildMutex, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public const string EarlierBuildMutex = @"Local\Dictate.SingleInstance";

    /// <summary>
    /// A WinExe has no console of its own. When stdout is not redirected, attach to the parent's console so CLI output is
    /// visible; when it is redirected (pipe/file) the inherited handle is used.
    /// </summary>
    public static void AttachConsoleIfNeeded()
    {
        if (Native.GetStdHandle(Native.STD_OUTPUT_HANDLE) == 0) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
    }

    public void Dispose()
    {
        if (_clipboard.IsValueCreated) _clipboard.Value.Dispose();
    }
}
