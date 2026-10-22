using Avalonia;
using Avalonia.Controls;
using Dictum.Cli;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Storage;
using Dictum.Transcribe;
using Dictum.UI;
using Dictum.UI.Shell;

namespace Dictum.Mac;

/// <summary>
/// Dictum (the executable in Dictum.app/Contents/MacOS): the command-line modes run without any window; the tray app
/// starts Avalonia on its native macOS backend with the shell built in <see cref="Dictum.UI.App.Started"/>. The same
/// shape as src/Dictum.Windows/Program.cs, without its Windows-only steps: no console to attach, no Vulkan to probe (the
/// GPU here is Metal, inside the CPU runtime), no earlier build to migrate from.
/// </summary>
public static class Program
{
    /// <summary>How long a launch waits for a copy that holds the lock but is not yet listening (starting, or quitting); same bound as Windows.</summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(30);

    public static int Main(string[] args)
    {
        // No Vulkan on a Mac: whisper.gpu = auto takes the CPU runtime order, whose arm64 build carries ggml's Metal backend.
        WhisperEngine.VulkanLoaderPresent = () => false;
        // Before any settings are read: the default hotkey (Right Option) and the keys' names on screen (Option, Command).
        Dictum.Hotkey.HotkeyNames.Dialect = Dictum.Hotkey.KeyDialect.Mac;
        var (options, error) = CliOptions.Parse(args);
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

        using var platform = new MacPlatform();
        var paths = platform.ResolvePaths(options.ConfigDir);
        var legacy = platform.LegacyFolders();
        var redactor = new SecretRedactor();
        var initialLevel = LogExtensions.ParseLevel(SettingsLoader.LoadFile(paths.SettingsFile).Settings.LogLevel);
        using var log = new FileLog(paths.LogsDir, redactor, initialLevel, echoToStderr: options.Mode != CliMode.Tray);

        if (options.Mode != CliMode.Tray)
        {
            // Nothing to migrate on a Mac (LegacyFolders never exist), but the same call keeps the two entry points alike.
            var report = LegacyMigration.RunWhenSafe(paths, legacy, platform.EarlierBuildRunning, () => false, copy: false);
            if (report is null) return 1;
        }

        switch (options.Mode)
        {
            case CliMode.Transcribe:
                return CliCommands.TranscribeAsync(options, paths, log, redactor).GetAwaiter().GetResult();
            case CliMode.SelfTest:
                return SelfTestCommand.Run(options, paths, log, redactor, platform);
            case CliMode.PrintModelsDir:
                return CliCommands.PrintModelsDir(paths, Console.Out, Console.Error);
            case CliMode.BenchCleanup:
                return BenchCleanupCommand.RunAsync(options, paths, log, redactor).GetAwaiter().GetResult();
            case CliMode.DownloadModel:
            case CliMode.DownloadCleanupModel:
                return DownloadCommand.RunAsync(options, paths, log).GetAwaiter().GetResult();
        }

        using var instance = platform.AcquireSingleInstance(QuitWait); // held until Dictum exits
        switch (instance.Outcome)
        {
            case InstanceOutcome.SignalledRunningCopy:
                log.Info("Second instance started; signalled the running instance and exited.");
                return 0;
            case InstanceOutcome.TimedOut:
                log.Warn($"Another copy of Dictum held the single-instance lock for {QuitWait.TotalSeconds:0} s without answering (still starting or still quitting); this launch exited. Start Dictum again.");
                return 0;
        }

        log.Info($"Dictum {typeof(Program).Assembly.GetName().Version?.ToString(3)} starting on {platform.Name} ({(platform.Bundle is null ? "not in an app bundle" : platform.Bundle.Name)}, config {paths.ConfigDir}).");
        AppShell? shell = null;
        var code = AppBuilder.Configure(() => new Dictum.UI.App
            {
                Started = lifetime =>
                {
                    var dialogs = new ShellDialogs();
                    var migration = LegacyMigration.RunWhenSafe(paths, legacy, platform.EarlierBuildRunning, () => false, copy: false);
                    if (migration is null)
                    {
                        lifetime.Shutdown();
                        return;
                    }
                    shell = new AppShell(platform, paths, log, redactor, new ShellOptions(options.Model, options.CleanupModel, options.ExitAfterSeconds, migration),
                        dialogs, instance);
                    shell.Start(lifetime);
                },
            })
            .UseAvaloniaNative()
            .UseSkia()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        shell?.Dispose();
        log.Info("Dictum exited.");
        return code;
    }
}
