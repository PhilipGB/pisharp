using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>
/// A tool that has already done its work must publish its completion before an accepted steer can be
/// observed. Pinned Pi gets that ordering from one event loop; PiSharp keeps a gate open from the moment
/// a tool returns until its completion event has been published.
/// </summary>
public sealed class ToolCompletionGateTests
{
    [Fact]
    public async Task AFinishedToolHoldsTheGateUntilItsCompletionIsPublished()
    {
        var gate = new ToolCompletionGate();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new DurableToolFunction(
            AIFunctionFactory.Create(() =>
            {
                returned.TrySetResult();
                return "done";
            }, name: "finish"),
            () => null,
            item =>
            {
                if (item.Type != "tool_execution_finished") return;
                publishing.TrySetResult();
                releasePublish.Task.GetAwaiter().GetResult();
            },
            new ToolBatchTermination(),
            completionGate: gate);

        var invocation = Task.Run(() => tool.InvokeAsync(new AIFunctionArguments()).AsTask());
        await returned.Task;
        await publishing.Task;

        // The tool has finished its work and is publishing: a steer must still be ordered after it.
        Assert.False(gate.WaitForIdleAsync().IsCompleted);

        releasePublish.SetResult();
        await invocation;
        await gate.WaitForIdleAsync();
        Assert.True(gate.WaitForIdleAsync().IsCompleted);
    }

    [Fact]
    public async Task ABlockedToolDoesNotHoldTheGate()
    {
        var gate = new ToolCompletionGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new DurableToolFunction(
            AIFunctionFactory.Create(async () =>
            {
                started.TrySetResult();
                await release.Task;
                return "done";
            }, name: "blocked"),
            () => null,
            _ => { },
            new ToolBatchTermination(),
            completionGate: gate);

        var invocation = Task.Run(() => tool.InvokeAsync(new AIFunctionArguments()).AsTask());
        await started.Task;

        // A tool that has not returned yet must not delay steering: only completed tools are ordered.
        Assert.True(gate.WaitForIdleAsync().IsCompleted);

        release.SetResult();
        await invocation;
    }

    [Fact]
    public void ConcurrentToolsKeepTheGateOpenUntilTheLastCompletionIsPublished()
    {
        var gate = new ToolCompletionGate();
        var first = gate.Begin();
        var second = gate.Begin();

        first.Dispose();
        // One completed tool still owes its completion: acceptance must keep waiting.
        Assert.False(gate.WaitForIdleAsync().IsCompleted);

        second.Dispose();
        Assert.True(gate.WaitForIdleAsync().IsCompleted);
    }
}
