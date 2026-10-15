using Dictum.Platform;

namespace Dictum.Windows.Shell;

/// <summary>
/// The single-instance mutex (SPEC §8). Holding it means this process is the running copy; the owner releases it when the
/// handle is disposed (or Windows does when the process ends). The running copy also creates a named event that a later
/// launch sets, so the running copy can say it is already running.
/// </summary>
public sealed class SingleInstance : IInstanceLock
{
    public const string MutexName = @"Local\Dictum.SingleInstance";
    public const string EventName = @"Local\Dictum.AlreadyRunning";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private readonly Mutex _mutex;
    private readonly string _eventName;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned, string eventName)
    {
        _mutex = mutex;
        _owned = owned;
        _eventName = eventName;
    }

    public bool Owned => _owned;

    public InstanceOutcome Outcome { get; private set; }

    /// <summary>
    /// Takes the lock, or signals the running copy through its "already running" event. When the lock is held but the
    /// event is not there (the other copy is still starting, or quitting: it closes the event first), waits for up to
    /// <paramref name="wait"/>, checking for the event as it goes: a copy that finishes quitting hands the lock over (an
    /// abandoned lock, from a copy that ended without letting go, counts as handed over), one that finishes starting is
    /// signalled. Bounded, so a copy stuck while quitting cannot hang a second launch.
    /// </summary>
    public static SingleInstance Acquire(string mutexName, string eventName, TimeSpan wait)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        var instance = new SingleInstance(mutex, createdNew, eventName);
        if (createdNew) return instance.With(InstanceOutcome.Owned);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var signal))
            {
                using (signal) signal.Set();
                return instance.With(InstanceOutcome.SignalledRunningCopy);
            }
            var left = wait - waited.Elapsed;
            if (left <= TimeSpan.Zero) return instance.With(InstanceOutcome.TimedOut);
            try
            {
                instance._owned = mutex.WaitOne(left < Poll ? left : Poll);
            }
            catch (AbandonedMutexException)
            {
                instance._owned = true; // Windows hands an abandoned mutex to the next waiter
            }
            if (instance._owned) return instance.With(InstanceOutcome.Owned);
        }
    }

    private SingleInstance With(InstanceOutcome outcome)
    {
        Outcome = outcome;
        return this;
    }

    public IDisposable ListenForOtherLaunches(Action onSignal)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
        var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listener(handle, wait);
    }

    private sealed class Listener(EventWaitHandle handle, RegisteredWaitHandle wait) : IDisposable
    {
        public void Dispose()
        {
            wait.Unregister(null);
            handle.Dispose();
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not owned by this thread any more
            }
            _owned = false;
        }
        _mutex.Dispose();
    }
}
