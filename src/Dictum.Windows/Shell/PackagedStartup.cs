using Dictum.Core;
using Dictum.Platform;
using WinStartupTask = global::Windows.ApplicationModel.StartupTask;
using WinStartupTaskState = global::Windows.ApplicationModel.StartupTaskState;

namespace Dictum.Windows.Shell;

/// <summary>
/// "Launch at login" for the packaged (MSIX / Store) build (SPEC §18.2): the package's windows.startupTask extension,
/// through the StartupTask API, instead of a Run key a package should not write. The task id must match the manifest's
/// StartupTask TaskId (<see cref="TaskId"/>). The user can turn it off in Task Manager's Startup apps; Dictum then cannot
/// turn it back on and says so.
/// </summary>
public sealed class PackagedStartup(ILog log) : IStartupRegistration
{
    /// <summary>The TaskId the MSIX manifest's &lt;desktop:StartupTask&gt; must use.</summary>
    public const string TaskId = "DictumStartup";

    private WinStartupTask? _task;

    public string Mechanism => "package's startup task (Task Manager > Startup apps)";

    private WinStartupTask? Current()
    {
        try
        {
            return _task ??= WinStartupTask.GetAsync(TaskId).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warn($"Startup task {TaskId} is not available ({ex.GetType().Name}: {ex.Message}); check the package manifest.");
            return null;
        }
    }

    public bool IsEnabled() => Current()?.State is WinStartupTaskState.Enabled or WinStartupTaskState.EnabledByPolicy;

    public async Task<string?> SetEnabledAsync(bool enabled)
    {
        if (Current() is not { } task) return "The package has no startup task, so launch at login is not available in this build.";
        if (!enabled)
        {
            if (task.State == WinStartupTaskState.EnabledByPolicy) return "Your organisation's policy keeps Dictum starting at login.";
            task.Disable();
            return null;
        }
        var state = await task.RequestEnableAsync();
        return state switch
        {
            WinStartupTaskState.Enabled or WinStartupTaskState.EnabledByPolicy => null,
            WinStartupTaskState.DisabledByUser => "Launch at login was turned off in Task Manager > Startup apps; turn it on there.",
            WinStartupTaskState.DisabledByPolicy => "Your organisation's policy does not allow Dictum to start at login.",
            _ => "Windows did not turn on launch at login.",
        };
    }
}
