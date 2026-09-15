using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class TurnMessagingTests
{
    [Fact]
    public void Queue_DrainsOneAtATimeInEnqueueOrder()
    {
        var queue = new TurnMessageQueue();
        queue.EnqueueSteering("first");
        queue.EnqueueSteering("second");

        Assert.Equal("first", Assert.Single(queue.DrainSteering()).Text);
        Assert.Equal("second", Assert.Single(queue.DrainSteering()).Text);
    }

    [Fact]
    public void Queue_AllModeDrainsTheWholeBatch()
    {
        var queue = new TurnMessageQueue { SteeringMode = QueueDrainMode.All };
        queue.EnqueueSteering("first");
        queue.EnqueueSteering("second");

        Assert.Equal(["first", "second"], queue.DrainSteering().Select(message => message.Text));
        Assert.Empty(queue.DrainSteering());
    }

    [Fact]
    public async Task Coordinator_RunsFollowUpsAfterInitialExecution()
    {
        var queue = new TurnMessageQueue();
        var coordinator = new LiveTurnCoordinator(queue);
        var executions = new List<string>();
        queue.EnqueueFollowUp("follow-up");

        var result = await coordinator.RunAsync(
            "initial",
            (prompt, _) =>
            {
                executions.Add(prompt);
                return Task.FromResult(new TurnExecutionResult(prompt.ToUpperInvariant()));
            });

        Assert.Equal(["initial", "follow-up"], executions);
        Assert.Equal("INITIALFOLLOW-UP", result.AssistantText);
        Assert.Equal(["initial", "follow-up"], result.DeliveredPrompts);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Coordinator_DeliversSteeringBeforeFollowUp()
    {
        var queue = new TurnMessageQueue();
        var coordinator = new LiveTurnCoordinator(queue);
        var executions = new List<string>();

        var result = await coordinator.RunAsync(
            "initial",
            (prompt, _) =>
            {
                executions.Add(prompt);
                if (prompt == "initial")
                {
                    queue.EnqueueFollowUp("follow-up");
                    queue.EnqueueSteering("steer");
                }

                return Task.FromResult(new TurnExecutionResult(prompt));
            });

        Assert.Equal(["initial", "steer", "follow-up"], executions);
        Assert.Equal(["initial", "steer", "follow-up"], result.DeliveredPrompts);
    }

    [Fact]
    public async Task Coordinator_AbortPreservesQueuedFollowUps()
    {
        var queue = new TurnMessageQueue();
        var coordinator = new LiveTurnCoordinator(queue);
        queue.EnqueueFollowUp("preserve me");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = coordinator.RunAsync(
            "initial",
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new TurnExecutionResult("unreachable");
            });

        await started.Task;
        coordinator.Abort();
        var result = await run;

        Assert.True(result.Cancelled);
        Assert.Equal("preserve me", Assert.Single(queue.Snapshot().FollowUp).Text);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task Coordinator_DoesNotRunConcurrently()
    {
        var coordinator = new LiveTurnCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunAsync(
            "first",
            async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return new TurnExecutionResult("done");
            });
        await started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(
            "second",
            (_, _) => Task.FromResult(new TurnExecutionResult("not run"))));

        release.SetResult();
        await first;
    }
}
