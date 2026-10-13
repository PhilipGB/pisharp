namespace PiSharp.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private long _timestamp;

    internal int ActiveTimerCount { get { lock (_gate) return _timers.Count(timer => timer.DueTimestamp is not null); } }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _timestamp;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
        long target;
        lock (_gate) target = _timestamp + amount.Ticks;

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.DueTimestamp is { } due && due <= target)
                    .MinBy(timer => timer.DueTimestamp);
                if (next is null)
                {
                    _timestamp = target;
                    return;
                }

                _timestamp = next.DueTimestamp!.Value;
                next.AdvanceDueTime();
            }
            next.Callback(next.State);
        }
    }

    private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            if (timer.IsDisposed) return;
            timer.Period = period;
            timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : _timestamp + dueTime.Ticks;
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueTimestamp { get; set; }
        public TimeSpan Period { get; set; }
        public bool IsDisposed { get; private set; }
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Change(this, dueTime, period);
            return !IsDisposed;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void AdvanceDueTime()
        {
            DueTimestamp = Period <= TimeSpan.Zero || Period == Timeout.InfiniteTimeSpan
                ? null
                : DueTimestamp + Period.Ticks;
        }
    }
}
