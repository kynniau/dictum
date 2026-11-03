using Avalonia;
using Avalonia.Controls;
using Dictum.Cli;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI;
using Dictum.UI.Shell;

namespace Dictum.Windows;

/// <summary>
/// Dictum.exe: the command-line modes run without any window; the tray app starts Avalonia on the Win32 backend with the
/// shell built in <see cref="Dictum.UI.App.Started"/>. Before anything opens a file, the one-time move from the earlier build's
/// folders (named Dictate) runs (SPEC §21).
/// </summary>
public static class Program
{
    /// <summary>
    /// How long a launch waits for a copy that holds the lock but has no "already running" event (starting, or quitting).
    /// Inferred bound: longer than the longest step quit cannot interrupt that DECISIONS records (the first-ever Vulkan
    /// prompt compile, about 17 s); quit cancels transcription and warm-ups, so it is normally much shorter.
    /// </summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(30);

    [STAThread]
    public static int Main(string[] args)
    {
        // First, before arguments are read or any thread is started: every mode (tray, --selftest, transcribe, bench) loads
        // whisper.cpp, and _putenv_s is not thread-safe (GgmlEnvironment).
        var ggmlEnvironment = GgmlEnvironment.Apply();
        WhisperEngine.VulkanLoaderPresent = GgmlEnvironment.VulkanLoaderLoads;
        var (options, error) = CliOptions.Parse(args);
        if (options is null || options.Mode != CliMode.Tray) WindowsPlatform.AttachConsoleIfNeeded();
        if (options is null)
        {
            Console.Error.WriteLine($"error: {error}");
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }
        if (options.Mode == CliMode.Help)
        {
            Console.Out.WriteLine(CliOptions.Usage);
            return 0;
        }

        using var platform = new WindowsPlatform();
        var paths = platform.ResolvePaths(options.ConfigDir);
        var legacy = platform.LegacyFolders();
        var redactor = new SecretRedactor();
        var initialLevel = LogExtensions.ParseLevel(SettingsLoader.LoadFile(paths.SettingsFile).Settings.LogLevel);
        using var log = new FileLog(paths.LogsDir, redactor, initialLevel, echoToStderr: options.Mode != CliMode.Tray);
        if (ggmlEnvironment != 0)
            log.Warn($"Could not set GGML_NO_BACKTRACE in the C runtime's environment (_putenv_s returned {ggmlEnvironment}); on a PC where the Vulkan runtime fails to load, Dictum may close at start.");

        // The command line cannot ask, so while the earlier build is running it stops here (SPEC §21, amended); the tray app
        // asks once it is up (below).
        if (options.Mode != CliMode.Tray)
        {
            var report = LegacyMigration.RunWhenSafe(paths, legacy, platform.EarlierBuildRunning, () =>
            {
                Console.Error.WriteLine($"error: {EarlierBuildRunning} Then run this again.");
                return false;
            }, copy: platform.Package.IsPackaged);
            if (report is null) return 1;
            LogMigration(log, report);
        }

        switch (options.Mode)
        {
            case CliMode.Transcribe:
                return CliCommands.TranscribeAsync(options, paths, log, redactor, new SecretVault(paths, platform.Credentials)).GetAwaiter().GetResult();
            case CliMode.SelfTest:
                return SelfTestCommand.Run(options, paths, log, redactor, platform);
            case CliMode.PrintModelsDir:
                return CliCommands.PrintModelsDir(paths, Console.Out, Console.Error);
            case CliMode.BenchCleanup:
                return BenchCleanupCommand.RunAsync(options, paths, log, redactor, new SecretVault(paths, platform.Credentials)).GetAwaiter().GetResult();
            case CliMode.DownloadModel:
            case CliMode.DownloadCleanupModel:
                return DownloadCommand.RunAsync(options, paths, log).GetAwaiter().GetResult();
        }

        using var instance = platform.AcquireSingleInstance(QuitWait); // held until Dictum exits
        switch (instance.Outcome)
        {
            case InstanceOutcome.SignalledRunningCopy: // second launch: the running copy shows its notice
                log.Info("Second instance started; signalled the running instance and exited.");
                return 0;
            case InstanceOutcome.TimedOut:
                log.Warn($"Another copy of Dictum held the single-instance lock for {QuitWait.TotalSeconds:0} s without answering (still starting or still quitting); this launch exited. Start Dictum again.");
                return 0;
        }

        log.Info($"Dictum {typeof(Program).Assembly.GetName().Version?.ToString(3)} starting on {platform.Name} ({(platform.Package.IsPackaged ? "packaged" : "unpackaged")}, config {paths.ConfigDir}).");
        AppShell? shell = null;
        var code = AppBuilder.Configure(() => new Dictum.UI.App
            {
                Started = lifetime =>
                {
                    var dialogs = new ShellDialogs();
                    var migration = LegacyMigration.RunWhenSafe(paths, legacy, platform.EarlierBuildRunning, () =>
                        dialogs.Ask("Quit Dictate first", $"{EarlierBuildRunning} Quit it from its tray icon, then choose Retry.",
                            new DialogChoice("Retry", IsDefault: true), new DialogChoice("Quit Dictum")) == 0, copy: platform.Package.IsPackaged);
                    if (migration is null)
                    {
                        log.Info("The earlier build (Dictate) is running and the user chose to quit; nothing was moved.");
                        lifetime.Shutdown();
                        return;
                    }
                    LogMigration(log, migration);
                    shell = new AppShell(platform, paths, log, redactor, new ShellOptions(options.Model, options.CleanupModel, options.ExitAfterSeconds, migration),
                        dialogs, instance);
                    shell.Start(lifetime);
                },
            })
            .UseWin32()
            .UseSkia()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        shell?.Dispose();
        log.Info("Dictum exited.");
        return code;
    }

    private const string EarlierBuildRunning =
        "Dictate, the earlier build of this app, is running. Its settings, models and recordings move to Dictum when Dictum first starts, which cannot happen while Dictate has them open: quit Dictate first.";

    private static void LogMigration(ILog log, MigrationReport report)
    {
        foreach (var line in report.Moved) log.Info($"Migration from Dictate: {(report.Copied ? "copied" : "moved")} {line}.");
        foreach (var line in report.Left) log.Warn($"Migration from Dictate: could not move {line}; it stays where it is and is tried again next start.");
        foreach (var line in report.Skipped) log.Warn($"Migration from Dictate: skipped {line}; it stays in Dictate's folder, which Remove Dictum completely also deletes.");
    }
}
