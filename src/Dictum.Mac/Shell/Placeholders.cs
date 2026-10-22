// PLACEHOLDER: replaced by the shell lane (docs/MAC-HANDOVER.md §5). Nothing here ships.
using Dictum.Platform;
using Dictum.Storage;

namespace Dictum.Mac.Shell;

internal sealed class LoginItem(ILog log, AppBundle? bundle) : IStartupRegistration
{
    public string Mechanism => "login item";
    public bool IsEnabled() => throw new NotImplementedException("shell lane");
    public Task<string?> SetEnabledAsync(bool enabled) => throw new NotImplementedException("shell lane");
}

internal sealed class SingleInstance : IInstanceLock
{
    public const string LockFileName = "dictum.lock";
    public const string SocketFileName = "dictum.sock";
    public static SingleInstance Acquire(string lockFile, string socketFile, TimeSpan wait) => throw new NotImplementedException("shell lane");
    public InstanceOutcome Outcome => InstanceOutcome.Owned;
    public IDisposable ListenForOtherLaunches(Action onSignal) => throw new NotImplementedException("shell lane");
    public void Dispose() { }
}

internal sealed class WindowFlags : IWindowFlags
{
    public (uint Style, uint ExStyle) OverlayStyles(uint style, uint exStyle) => (style, exStyle);
    public void MakeOverlay(nint handle) => throw new NotImplementedException("shell lane");
    public void Move(nint handle, int x, int y) => throw new NotImplementedException("shell lane");
    public nint ForegroundWindow() => throw new NotImplementedException("shell lane");
    public ScreenRect? ForegroundBounds() => throw new NotImplementedException("shell lane");
    public bool ReducedMotion => false;
    public string? AppName(nint window) => null;
    public bool IsWindow(nint handle) => false;
    public bool TryActivate(nint handle) => false;
}

internal sealed class TrayHost(Action<ScreenPoint?> activated, ILog log, AppBundle? bundle) : ITrayHost
{
    public void SetState(TrayState state) => throw new NotImplementedException("shell lane");
    public void SetTooltip(string text) => throw new NotImplementedException("shell lane");
    public void Notify(string title, string text, bool warning) => throw new NotImplementedException("shell lane");
    public void Dispose() { }
}

internal sealed class RemovalSteps(ILog log, AppBundle? bundle, string exeDir, Func<IStartupRegistration> startup) : IRemovalSteps
{
    public IReadOnlyList<OwnedLocation> PlatformLocations() => throw new NotImplementedException("shell lane");
    public string? ProgramFolderProblem() => throw new NotImplementedException("shell lane");
    public IReadOnlyList<string> RemoveShortcutAndStartup() => throw new NotImplementedException("shell lane");
    public bool ScheduleProgramFolderRemoval(string folder, out string? reason) => throw new NotImplementedException("shell lane");
    public void OpenUninstallSettings() { }
    public IReadOnlyList<string> EarlierBuildLeftovers() => [];
    public IReadOnlyList<string> RemoveEarlierBuild() => [];
}

internal static class SoundCues
{
    public static void Play(SoundCue cue) => throw new NotImplementedException("shell lane");
}
