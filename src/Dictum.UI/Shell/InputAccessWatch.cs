using Avalonia.Threading;
using Dictum.Core;
using Dictum.Platform;

namespace Dictum.UI.Shell;

/// <summary>
/// The shell's view of the input-access grant (IPlatform.InputAccess; docs/DECISIONS.md "Request access", 27/10/2026). While a
/// listener was refused for want of the grant, it polls the silent check once a second; when the grant arrives it restarts the
/// listeners, so the hotkey works without quitting Dictum. <see cref="Request"/> is the one button every surface shows: the
/// system prompt where macOS gives one, else the Settings page. On Windows the check is NotApplicable and nothing here ever shows.
/// </summary>
public sealed class InputAccessWatch
{
    /// <summary>
    /// simplification: polling stops after this long. Ceiling: a user who grants access later than this must press Request
    /// access (which polls again) or reopen Dictum. Trigger to retire: a TCC change notification worth the native code.
    /// </summary>
    internal static readonly TimeSpan PollFor = TimeSpan.FromMinutes(15);

    private readonly IInputAccess _access;
    private readonly ILog _log;
    private readonly Func<bool> _restartListeners;
    private readonly DispatcherTimer _timer;
    private DateTime _pollingSince;

    /// <param name="restartListeners">Replaces the hotkey listeners; true when the dictation listener installed.</param>
    public InputAccessWatch(IInputAccess access, ILog log, Func<bool> restartListeners)
    {
        _access = access;
        _log = log;
        _restartListeners = restartListeners;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick()) { IsEnabled = false };
    }

    /// <summary>The last answer the system gave; NotApplicable until a listener is refused or a request is made.</summary>
    public InputAccessState State { get; private set; }

    /// <summary>The grant arrived but the listener still did not install: Dictum has to be reopened (not seen on hardware yet; kept for the case).</summary>
    public bool RestartFailed { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="State"/> or <see cref="RestartFailed"/> changes.</summary>
    public event Action? Changed;

    /// <summary>A listener could not install: if the grant is missing, say so and start watching for it.</summary>
    public void ListenerRefused()
    {
        var state = _access.Check();
        if (state != InputAccessState.Denied) return;
        Set(state, restartFailed: false);
        StartPolling();
    }

    /// <summary>Asks the system for the grant and watches for the answer.</summary>
    public void Request()
    {
        _log.Info("Input access requested by the user.");
        _access.Request();
        Set(_access.Check(), restartFailed: false);
        if (State == InputAccessState.Denied) StartPolling();
    }

    /// <summary>One poll: on a grant, stop and restart the listeners. Internal so a test ticks it without the timer.</summary>
    internal void Tick()
    {
        var state = _access.Check();
        if (state == InputAccessState.Denied)
        {
            if (DateTime.UtcNow - _pollingSince > PollFor) StopPolling();
            return;
        }
        StopPolling();
        if (state != InputAccessState.Granted) return;
        var installed = _restartListeners();
        _log.Info(installed ? "Input access granted; the hotkey listener is installed." : "Input access granted, but the hotkey listener still did not install.");
        Set(state, restartFailed: !installed);
    }

    private void StartPolling()
    {
        _pollingSince = DateTime.UtcNow;
        _timer.Start();
    }

    private void StopPolling() => _timer.Stop();

    private void Set(InputAccessState state, bool restartFailed)
    {
        if (state == State && restartFailed == RestartFailed) return;
        State = state;
        RestartFailed = restartFailed;
        Changed?.Invoke();
    }

    public void Dispose() => _timer.Stop();
}
