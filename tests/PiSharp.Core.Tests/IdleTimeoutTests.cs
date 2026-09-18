using PiSharp.Cli;

namespace PiSharp.Core.Tests;

/// <summary>
/// Item 2: the HTTP idle-timeout guard must cancel the actual in-flight provider operation
/// (not merely abandon it via a WhenAny race), reset the window on each chunk, link caller
/// cancellation, and leave no unobserved task on dispose.
/// </summary>
public sealed class IdleTimeoutTests
{
    /// <summary>
    /// An in-flight operation awaiting the guard token must be CANCELLED when the idle window
    /// elapses (the pre-fix WhenAny race left it running and unobserved). Awaiting the op
    /// proves it completed (was cancelled) rather than being abandoned.
    /// </summary>
    [Fact]
    public async Task IdleTimeoutCancelsTheInFlightOperation()
    {
        var guard = new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(60), CancellationToken.None);
        var op = Task.Delay(Timeout.Infinite, guard.Token);

        var finished = await Task.WhenAny(op, Task.Delay(2_000));
        Assert.Same(op, finished); // the op finished (was cancelled) well before the safety bound

        await Observe(op); // observing the (cancelled) op: no unobserved task
        Assert.True(guard.Token.IsCancellationRequested);

        await guard.DisposeAsync();
    }

    /// <summary>
    /// Resetting after each unit of activity keeps the window alive: with 60ms windows and a
    /// reset every ~20ms the token must not fire until a full un-reset window elapses.
    /// </summary>
    [Fact]
    public async Task ResetExtendsTheIdleWindow()
    {
        var guard = new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(60), CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(20);
            guard.Reset();
        }

        // 60ms of activity with resets: still within the (reset) window, not cancelled.
        Assert.False(guard.Token.IsCancellationRequested);

        // Now let a full window elapse with no reset: it must fire.
        var op = Task.Delay(Timeout.Infinite, guard.Token);
        var finished = await Task.WhenAny(op, Task.Delay(2_000));
        Assert.Same(op, finished);
        await Observe(op);
        Assert.True(guard.Token.IsCancellationRequested);

        await guard.DisposeAsync();
    }

    /// <summary>Caller cancellation is linked through: cancelling the parent cancels the guard token.</summary>
    [Fact]
    public async Task CallerCancellationIsLinked()
    {
        using var parent = new CancellationTokenSource();
        var guard = new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(10_000), parent.Token);
        Assert.False(guard.Token.IsCancellationRequested);

        parent.Cancel();
        Assert.True(guard.Token.IsCancellationRequested);

        await guard.DisposeAsync();
    }

    /// <summary>
    /// Normal completion: when the operation finishes before the window elapses, the token is
    /// never cancelled and dispose is clean (nothing to abort).
    /// </summary>
    [Fact]
    public async Task NoCancellationWhenOperationCompletesFirst()
    {
        var guard = new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        var op = Task.Delay(10, guard.Token);
        await op; // completes before the window

        Assert.False(op.IsCanceled);
        Assert.False(guard.Token.IsCancellationRequested);

        await guard.DisposeAsync();
    }

    /// <summary>
    /// Dispose must cancel a still-in-flight operation and observe its task: the op completes
    /// (cancelled) as a result of disposal, and awaiting it does not hang or fault.
    /// </summary>
    [Fact]
    public async Task DisposeCancelsAndObservesAnInFlightOperation()
    {
        var guard = new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(10_000), CancellationToken.None);
        var op = Task.Delay(Timeout.Infinite, guard.Token);

        var dispose = guard.DisposeAsync().AsTask();
        // Dispose cancels the guard token, which cancels the in-flight op.
        var finished = await Task.WhenAny(op, Task.Delay(2_000));
        Assert.Same(op, finished);

        await dispose; // completes: the watchdog was observed, nothing left behind
        await Observe(op); // the op is cancelled (observed, no unobserved task)
        Assert.True(op.IsCanceled);
    }

    /// <summary>Awaits a task that is expected to be cancelled, proving it was observed (not abandoned).</summary>
    private static async Task Observe(Task task)
    {
        try
        {
            await task;
            Assert.Fail("expected the operation to be cancelled");
        }
        catch (OperationCanceledException)
        {
            // expected: the in-flight operation was cancelled
        }
    }
}
