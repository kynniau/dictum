namespace Dictum.Tests;

/// <summary>
/// A clock that only moves when a test calls <see cref="Advance"/>. Timers made from it (<see cref="CreateTimer"/>, and so
/// <c>Task.Delay(delay, timeProvider)</c>) fire, on the advancing thread and in due order, when the clock reaches them, so a test
/// decides exactly how much time passes between two events instead of racing a real timer. Written here rather than taken from
/// Microsoft.Extensions.TimeProvider.Testing so the test project gains no package (see DECISIONS.md, "TimeProvider seam").
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Timers that are armed and not yet fired.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.Due is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock on by <paramref name="by"/>, firing every timer that falls due on the way, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due > _now) _now = next.Due!.Value;
                next.Fired(_now);
            }
            next.Run(); // outside the lock: the callback may create or restart timers
        }
    }

    private sealed class FakeTimer(FakeTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>When the timer next fires; null while it is stopped or disposed.</summary>
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }
            return true;
        }

        /// <summary>Called under the clock's lock as the clock reaches <see cref="Due"/>.</summary>
        public void Fired(DateTimeOffset now) =>
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : now + _period;

        public void Run() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                Due = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
