using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Dictum.App;
using Dictum.Audio;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.UI.Shell;

namespace Dictum.Tests;

/// <summary>
/// One Avalonia headless session for the whole test run (a process can start Avalonia once). Window tests run their body
/// on its UI thread; nothing appears on the desktop, so they need no gate.
/// </summary>
internal static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(UiSession), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Called by the headless session through reflection: the app with Dictum's theme on the headless platform.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Dictum.UI.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }); // Skia reads the system fonts; no font package

    public static Task Run(Action body) => Session.Value.Dispatch(body, TestContext.Current.CancellationToken);

    public static Task<T> Run<T>(Func<T> body) => Session.Value.Dispatch(body, TestContext.Current.CancellationToken);

    public static Task<T> RunAsync<T>(Func<Task<T>> body) => Session.Value.Dispatch(body, TestContext.Current.CancellationToken);
}

/// <summary>A platform with no operating system behind it: records what the shell asks of it.</summary>
internal sealed class FakePlatform(AppPaths paths) : IPlatform
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public FakeHotkeyListener? LastListener { get; private set; }
    public List<FakeHotkeyListener> Listeners { get; } = [];
    public bool EarlierRunning { get; set; }
    public FakeKeyMonitor? LastMonitor { get; private set; }
    public FakeTray? Tray { get; private set; }
    public FakeWindowFlags Flags { get; } = new();
    public FakePackage FakePackage { get; } = new();
    public FakeRemoval FakeRemoval { get; } = new();
    public List<string> Opened { get; } = [];

    public string Name => "Test";
    public AppPaths ResolvePaths(string? configOverride) => paths;
    public LegacyFolders LegacyFolders() => new(Path.Combine(paths.ConfigDir, "..", "legacy-config"), Path.Combine(paths.LocalDir, "..", "legacy-local"));
    public IPackageInfo Package => FakePackage;

    /// <summary>Listeners created from now on fail to start, as when Windows refuses the hook.</summary>
    public bool RefuseListeners { get; set; }

    public IHotkeyListener CreateHotkeyListener(HotkeyMatcher matcher, Action<HookEvent> post, ILog log)
    {
        LastListener = new FakeHotkeyListener(matcher, post) { Refused = RefuseListeners };
        Listeners.Add(LastListener);
        return LastListener;
    }
    public IKeyMonitor CreateKeyMonitor(Action<RawKeyEvent> onKey, ILog log) => LastMonitor = new FakeKeyMonitor(onKey);
    public bool IsKeyDown(int vk) => false;
    public IInputSender CreateInputSender(ILog log) => Desktop;
    public IClipboard Clipboard => Desktop;
    public FakeDesktop Desktop { get; } = new();
    /// <summary>Makes every capture created from now on (the shell's own is made when it is built): a test's level-reporting one, say.</summary>
    public Func<IAudioCapture>? CaptureFactory { get; set; }
    public List<IAudioCapture> Captures { get; } = [];
    public IAudioCapture CreateCapture(ILog log)
    {
        var capture = CaptureFactory?.Invoke() ?? new FakeCapture();
        Captures.Add(capture);
        return capture;
    }
    /// <summary>The microphones the system lists; one by default.</summary>
    public List<AudioDeviceInfo> Devices { get; } = [new("id-1", "Test microphone", true)];
    /// <summary>Listing the microphones throws, as the Windows device list does with the audio service stopped.</summary>
    public bool ListDevicesThrows { get; set; }
    public IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => ListDevicesThrows ? throw new System.Runtime.InteropServices.COMException("The audio service is not running.") : Devices;
    public IStartupRegistration Startup { get; } = new FakeStartup();
    public IInstanceLock AcquireSingleInstance(TimeSpan wait) => throw new NotSupportedException();
    public IWindowFlags WindowFlags => Flags;

    public ITrayHost CreateTray(Action<ScreenPoint?> activated, ILog log) => Tray = new FakeTray();

    public void Open(string target)
    {
        Opened.Add(target);
        Calls.Enqueue($"open {target}");
    }

    public IRemovalSteps Removal => FakeRemoval;
    public void PlayCue(SoundCue cue) => Calls.Enqueue($"cue {cue}");
    public bool EarlierBuildRunning() => EarlierRunning;
}

internal sealed class FakeHotkeyListener(HotkeyMatcher matcher, Action<HookEvent> post) : IHotkeyListener
{
    public HotkeyMatcher Matcher { get; } = matcher;
    public bool Enabled { get; set; } = true;
    public bool Disposed { get; private set; }
    public bool Refused { get; init; }
    public bool Start() => !Refused;
    public void Post(HookEvent e) => post(e);
    public void Dispose() => Disposed = true;
}

internal sealed class FakeKeyMonitor(Action<RawKeyEvent> onKey) : IKeyMonitor
{
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public bool Start() => Started = true;
    public void Press(int vk, bool down) => onKey(new RawKeyEvent(vk, down, Injected: false));
    public void Dispose() => Disposed = true;
}

internal sealed class FakeTray : ITrayHost
{
    public List<(string Title, string Text, bool Warning)> Notices { get; } = [];
    public TrayState State { get; private set; }
    public string Tooltip { get; private set; } = "";
    public bool Disposed { get; private set; }
    public void SetState(TrayState state) => State = state;
    public void SetTooltip(string text) => Tooltip = text;
    public void Notify(string title, string text, bool warning) => Notices.Add((title, text, warning));
    public void Dispose() => Disposed = true;
}

internal sealed class FakeWindowFlags : IWindowFlags
{
    public bool Reduced { get; set; }
    public nint Foreground { get; set; } = 42;
    public (uint Style, uint ExStyle) OverlayStyles(uint style, uint exStyle) => (style, exStyle);
    public void MakeOverlay(nint handle) { }
    public void Move(nint handle, int x, int y) { }
    public nint ForegroundWindow() => Foreground;
    public ScreenRect? ForegroundBounds() => null;
    public bool ReducedMotion => Reduced;
    public string? AppName(nint window) => "Notepad";
    public bool IsWindow(nint handle) => handle != 0;
    public bool TryActivate(nint handle) => handle == Foreground;
}

internal sealed class FakePackage : IPackageInfo
{
    public bool IsPackaged { get; set; }
    public string? FamilyName => IsPackaged ? "Kynni.Dictum_test" : null;
    public string RealPath(string path) => path;
    public string? OtherInstall() => null;
}

internal sealed class FakeRemoval : IRemovalSteps
{
    public List<string> Steps { get; } = [];
    public IReadOnlyList<OwnedLocation> PlatformLocations() => [];

    public IReadOnlyList<string> RemoveShortcutAndStartup()
    {
        Steps.Add("shortcut and startup");
        return [];
    }

    public bool ScheduleProgramFolderRemoval(string folder, out string? reason)
    {
        Steps.Add($"program folder {folder}");
        reason = null;
        return true;
    }

    public void OpenUninstallSettings() => Steps.Add("uninstall settings");
    public string? Problem { get; set; }
    public string? ProgramFolderProblem() => Problem;
    public List<string> Leftovers { get; } = [];
    public IReadOnlyList<string> EarlierBuildLeftovers() => Leftovers;

    public IReadOnlyList<string> RemoveEarlierBuild()
    {
        Steps.Add("earlier build");
        return [];
    }
}

internal sealed class FakeStartup : IStartupRegistration
{
    public bool On { get; private set; }
    public string Mechanism => "test startup";
    public bool IsEnabled() => On;

    public Task<string?> SetEnabledAsync(bool enabled)
    {
        On = enabled;
        return Task.FromResult<string?>(null);
    }
}

/// <summary>The shell's questions answered by a script: each Ask returns the next answer (0 = the first button).</summary>
internal sealed class ScriptedDialogs : IShellDialogs
{
    public Queue<int> Answers { get; } = new();
    public bool ConfirmWord { get; set; }
    public bool ConfirmDownloads { get; set; } // the answer to the download question: no, until a test says yes
    public List<(string Title, string Text)> Asked { get; } = [];
    public List<string> Told { get; } = [];

    /// <summary>The button labels of each <see cref="Ask"/>, in order, so a test can pin a label the help names.</summary>
    public List<IReadOnlyList<string>> AskedChoices { get; } = [];

    /// <summary>The index of each <see cref="Ask"/>'s default button (Enter), or -1 for none.</summary>
    public List<int> AskedDefault { get; } = [];

    public int Ask(string title, string text, params DialogChoice[] choices)
    {
        Asked.Add((title, text));
        AskedChoices.Add(choices.Select(c => c.Label).ToList());
        AskedDefault.Add(Array.FindIndex(choices, c => c.IsDefault));
        return Answers.Count > 0 ? Answers.Dequeue() : -1;
    }

    public void Tell(string title, string text) => Told.Add(text);

    public bool TypeToConfirm(string title, string text, string word)
    {
        Asked.Add((title, text));
        return ConfirmWord;
    }

    /// <summary>How many times each first-run step was shown: the speech picker, the cleanup picker, the hotkey check.</summary>
    public (int Speech, int Cleanup, int Hotkey) FirstRunSteps { get; private set; }

    /// <summary>What Settings > Models > Model folder > Browse… picks (null: the chooser was cancelled), and the folder it opened in.</summary>
    public string? Folder { get; set; }
    public List<string> FolderChooserOpenedIn { get; } = [];

    /// <summary>
    /// The Welcome to Dictum window: counted as the hotkey step (it holds the first-run hotkey check), then <see cref="Onboarding"/>
    /// answers through the flow if a test set it; otherwise every step is skipped (the shell then writes today's defaults).
    /// </summary>
    public void Onboard(Dictum.UI.Views.Onboarding.OnboardingFlow flow)
    {
        FirstRunSteps = FirstRunSteps with { Hotkey = FirstRunSteps.Hotkey + 1 };
        LastOnboarding = flow;
        Onboarding?.Invoke(flow);
    }

    public Action<Dictum.UI.Views.Onboarding.OnboardingFlow>? Onboarding { get; set; }

    public Dictum.UI.Views.Onboarding.OnboardingFlow? LastOnboarding { get; private set; }

    public ModelPick FirstRunPicker(string recommended, string reason, Func<ModelInfo, bool> isDownloaded, string modelsDir)
    {
        FirstRunSteps = FirstRunSteps with { Speech = FirstRunSteps.Speech + 1 };
        return new(recommended, null, false);
    }

    public CleanupPick CleanupPicker(string recommended, string reason, Func<CleanupModelInfo, bool> isDownloaded, string modelsDir)
    {
        FirstRunSteps = FirstRunSteps with { Cleanup = FirstRunSteps.Cleanup + 1 };
        return new(null, null, true, false);
    }

    public MissingModelChoice MissingModel(string shortName, bool canDownload) => MissingModelChoice.Quit;

    /// <summary>The file names every download question named, in order.</summary>
    public List<string> DownloadQuestions { get; } = [];

    public bool ConfirmDownload(string fileName, string sizeText, string note)
    {
        DownloadQuestions.Add(fileName);
        return ConfirmDownloads;
    }

    /// <summary>The answer to the download question asked while another download runs (Cancel, until a test says otherwise).</summary>
    public BusyDownloadChoice BusyDownload { get; set; } = BusyDownloadChoice.Cancel;

    public BusyDownloadChoice ConfirmDownloadWhileBusy(string fileName, string sizeText, string note, string chosen, string running, string runningProgress, bool canWait = true)
    {
        DownloadQuestions.Add(fileName);
        return BusyDownload;
    }

    /// <summary>The words of every plain (Simple view) question, as the window would show them; each also adds its file to <see cref="DownloadQuestions"/>.</summary>
    public List<string> PlainQuestions { get; } = [];

    public bool ConfirmPlainDownload(string fileName, string plainName, string sizeText, string? warning)
    {
        PlainQuestions.Add(ModelPromptText.PlainConfirmText(plainName, sizeText, warning));
        DownloadQuestions.Add(fileName);
        return ConfirmDownloads;
    }

    public BusyDownloadChoice ConfirmPlainDownloadWhileBusy(string fileName, string plainName, string sizeText, string? warning, string running, string runningProgress, bool canWait = true)
    {
        PlainQuestions.Add(ModelPromptText.PlainBusyText(plainName, sizeText, warning, running, runningProgress, canWait));
        DownloadQuestions.Add(fileName);
        return BusyDownload;
    }

    public string? ChooseFile(string initialDir, bool cleanupModel) => null;

    public string? ChooseFolder(string initialDir)
    {
        FolderChooserOpenedIn.Add(initialDir);
        return Folder;
    }
}

/// <summary>A scratch profile: a settings folder, a local folder and an exe folder under one temporary directory.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch(string? settingsJson = null)
    {
        Root = Directory.CreateTempSubdirectory("dictum-test-");
        Paths = new AppPaths(Path.Combine(Root.FullName, "config"), Path.Combine(Root.FullName, "local"), Path.Combine(Root.FullName, "exe"));
        Directory.CreateDirectory(Paths.ConfigDir);
        Directory.CreateDirectory(Paths.ExeDir);
        if (settingsJson is not null) File.WriteAllText(Paths.SettingsFile, settingsJson);
    }

    public DirectoryInfo Root { get; }

    /// <summary>The scratch profile's folders; a test may change them (another ProfileDir, say) before it makes a shell.</summary>
    public AppPaths Paths { get; set; }

    /// <summary>A shell over a fake platform with scripted dialogs, not started (no tray, no hook, no models).</summary>
    /// <param name="speechModels">A speech model manager with a fake loader, for tests that make a model ready; else the real one.</param>
    /// <param name="options">Launch options (a --model override, say); none by default.</param>
    /// <param name="downloadHandler">A fake server under the model-download client's real offline_only guard; the network otherwise.</param>
    /// <param name="updateHandler">The same for the Check for updates client.</param>
    /// <param name="log">Where the shell logs (a <see cref="RecordingLog"/>, say); nowhere by default.</param>
    /// <param name="cleanupModels">A clean-up model manager with a fake loader; else the real one.</param>
    /// <param name="providerHandler">A fake server under the clean-up and cloud speech provider client's real offline_only guard; the network otherwise.</param>
    /// <param name="gpuMachine">What the shell takes the machine for (WhisperEngine.GpuMachine): a CPU-only PC by default.</param>
    public (AppShell Shell, FakePlatform Platform, ScriptedDialogs Dialogs) Shell(Dictum.Transcribe.ModelManager? speechModels = null, ShellOptions? options = null,
        HttpMessageHandler? downloadHandler = null, HttpMessageHandler? updateHandler = null, ILog? log = null, Dictum.Llm.LocalLlmManager? cleanupModels = null,
        bool? gpuMachine = false, Func<string, ILog, string>? runtimeProbe = null, HttpMessageHandler? providerHandler = null)
    {
        var platform = new FakePlatform(Paths);
        var dialogs = new ScriptedDialogs();
        // gpuMachine null: the app's own path, which probes the whisper runtime (runtimeProbe stands in for it, so nothing native loads).
        var shell = new AppShell(platform, Paths, log ?? NullLog.Instance, new SecretRedactor(), options ?? new ShellOptions(), dialogs,
            gpuMachine: gpuMachine is { } gpu ? () => gpu : null,
            speechModels: speechModels, downloadHandler: downloadHandler, updateHandler: updateHandler, cleanupModels: cleanupModels,
            providerHandler: providerHandler);
        if (runtimeProbe is not null) shell.Models.TestRuntimeProbe = runtimeProbe;
        return (shell, platform, dialogs);
    }

    public void Dispose()
    {
        try
        {
            Root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
