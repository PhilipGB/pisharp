namespace PiSharp.Cli.Tui;

/// <summary>Coalesces screen updates; callers and timer callbacks share the screen's state lock.</summary>
internal sealed class TerminalRenderScheduler(object gate, TimeProvider timeProvider,
    TimeSpan minimumInterval, Func<bool> canRender, Action renderFrame) : IDisposable
{
    private ITimer? _timer;
    private long _lastRenderTimestamp;

    public bool Pending { get; private set; }
    public bool HasRendered { get; private set; }

    public void Request(bool immediate = false)
    {
        if (!canRender()) return;
        if (minimumInterval == TimeSpan.Zero || immediate || !HasRendered)
        {
            Cancel();
            RenderNow();
            return;
        }

        if (Pending) return;
        Pending = true;
        _timer ??= timeProvider.CreateTimer(static state => ((TerminalRenderScheduler)state!).RenderScheduled(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var elapsed = timeProvider.GetElapsedTime(_lastRenderTimestamp);
        var delay = minimumInterval - elapsed;
        _timer.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void RenderScheduled()
    {
        lock (gate)
        {
            if (!Pending) return;
            if (!canRender())
            {
                Pending = false;
                return;
            }

            var elapsed = timeProvider.GetElapsedTime(_lastRenderTimestamp);
            var delay = minimumInterval - elapsed;
            if (delay > TimeSpan.Zero)
            {
                _timer?.Change(delay, Timeout.InfiniteTimeSpan);
                return;
            }

            Pending = false;
            RenderNow();
        }
    }

    public void Cancel()
    {
        Pending = false;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Flush()
    {
        if (!Pending) return;
        Cancel();
        RenderNow();
    }

    private void RenderNow()
    {
        renderFrame();
        _lastRenderTimestamp = timeProvider.GetTimestamp();
        HasRendered = true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
