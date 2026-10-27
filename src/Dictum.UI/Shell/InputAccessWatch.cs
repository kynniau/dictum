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
    private readonly TimeProvider _time;
    private DateTimeOffset _pollingSince;

    /// <param name="restartListeners">Replaces the hotkey listeners; true when the dictation listener installed.</param>
    /// <param name="time">The clock the <see cref="PollFor"/> cut-off reads; null is the system clock, and only tests set it (DECISIONS.md, "TimeProvider seam").</param>
    public InputAccessWatch(IInputAccess access, ILog log, Func<bool> restartListeners, TimeProvider? time = null)
    {
        _access = access;
        _log = log;
        _restartListeners = restartListeners;
        _time = time ?? TimeProvider.System;
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

    /// <summary>
    /// Asks the system for the grant and watches for the answer. The answer goes through <see cref="Tick"/> either way, so a grant
    /// already in place (the user turned Dictum on by hand after polling stopped) restarts the listeners too, instead of only
    /// changing the words (merge lens, PR #82, 27/10/2026).
    /// </summary>
    public void Request()
    {
        _log.Info("Input access requested by the user.");
        _access.Request();
        StartPolling();
        Tick();
    }

    /// <summary>Whether the once-a-second check is running, for the pins.</summary>
    internal bool Polling => _timer.IsEnabled;

    /// <summary>
    /// One poll: on a grant, stop and restart the listeners. Internal so a test ticks it without the timer. It runs from a
    /// DispatcherTimer with nothing above it to catch a failure, so a failure is caught here, logged, and shown as a grant the
    /// listener could not use, as the debounced reload catches its own (AppShell.DebouncedReload).
    /// </summary>
    internal void Tick()
    {
        try
        {
            var state = _access.Check();
            if (state == InputAccessState.Denied)
            {
                Set(state, restartFailed: false);
                if (_time.GetUtcNow() - _pollingSince > PollFor) StopPolling();
                return;
            }
            StopPolling();
            if (state != InputAccessState.Granted) return;
            var installed = _restartListeners(); // logs its own outcome (AppShell.RestartListeners)
            Set(state, restartFailed: !installed);
        }
        catch (Exception ex)
        {
            StopPolling();
            _log.Error($"Input access restart failed: {ex}");
            Set(InputAccessState.Granted, restartFailed: true);
        }
    }

    private void StartPolling()
    {
        _pollingSince = _time.GetUtcNow();
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
