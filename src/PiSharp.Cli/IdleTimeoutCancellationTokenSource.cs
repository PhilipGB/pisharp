namespace PiSharp.Cli;

/// <summary>
/// A linked cancellation token source that fires after <paramref name="idleTimeout"/> of
/// inactivity. <see cref="Reset"/> reschedules the window after each unit of activity (a
/// streamed chunk), mirroring pinned <c>httpIdleTimeoutMs</c> (header/body idle timing that
/// resets on every received chunk).
///
/// <see cref="Token"/> is what the in-flight provider operation observes. On an idle timeout
/// the in-flight operation is actually cancelled (not merely abandoned, as a
/// <c>Task.WhenAny</c> race leaves it), and caller cancellation is linked through. Disposing
/// cancels the pending window, observes the watchdog task, and cancels the linked source so a
/// still-in-flight operation is aborted — no unobserved task is left behind.
/// </summary>
internal sealed class IdleTimeoutCancellationTokenSource : IAsyncDisposable
{
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _linked;
    private readonly object _gate = new();
    private CancellationTokenSource _delay;
    private Task _watchdog;
    private bool _disposed;

    public IdleTimeoutCancellationTokenSource(TimeSpan idleTimeout, CancellationToken parent)
    {
        _idleTimeout = idleTimeout;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _delay = new CancellationTokenSource();
        _watchdog = StartWatchdog(_delay);
    }

    /// <summary>The token the in-flight provider operation should observe.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>
    /// Reschedules the idle window. The new window is installed before the previous delay is
    /// cancelled so there is no gap in coverage; the previous delay (already &lt; the window)
    /// is dropped.
    /// </summary>
    public void Reset()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _delay;
            _delay = new CancellationTokenSource();
            _watchdog = StartWatchdog(_delay);
        }

        previous.Cancel();
        previous.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource delay;
        Task watchdog;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            delay = _delay;
            watchdog = _watchdog;
        }

        // Stop the pending window, then observe the (non-throwing) watchdog so the timer task
        // is not left unobserved, and only then cancel/dispose the linked source so a
        // still-in-flight operation is aborted before its token goes away.
        delay.Cancel();
        delay.Dispose();
        try
        {
            await watchdog.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The delay can only complete cancelled; the continuation swallows it.
        }

        _linked.Cancel();
        _linked.Dispose();
    }

    /// <summary>
    /// The watchdog: await the current idle window and, on a non-cancelled completion (the
    /// window elapsed), cancel the linked source. The continuation never throws, so the
    /// returned task never faults and is safe to observe (or drop on Reset, where it simply
    /// completes when its delay is cancelled).
    /// </summary>
    private Task StartWatchdog(CancellationTokenSource delaySource) =>
        Task.Delay(_idleTimeout, delaySource.Token).ContinueWith(
            static (delay, state) =>
            {
                if (!delay.IsCanceled)
                {
                    ((IdleTimeoutCancellationTokenSource)state!)._linked.Cancel();
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
}
