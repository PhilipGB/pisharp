using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

[CollectionDefinition("Codemode workers", DisableParallelization = true)]
public sealed class CodemodeWorkerTestCollection { }

[Collection("Codemode workers")]
public sealed class CodemodeWorkerLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonInterruptibleGuestWorkSettlesAndExitsPromptly(bool timeout)
    {
        await WaitForWorkerExitAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = AIFunctionFactory.Create(() => { entered.TrySetResult(); return "ready"; }, name: "ready");
        var registry = new PiSharpToolRegistry([new(ready, ToolExposure.CodeMode)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["ready"] = ready }, _ => { }, "test", null, "codemode",
            new Dictionary<string, object?>());
        using var cancellation = new CancellationTokenSource();
        var code = (timeout ? "// @options: {\"timeout_ms\":2000}\n" : "") +
            "await tools.ready({}); return 'a'.repeat(300000).indexOf('a'.repeat(10000) + 'b');";
        var started = Stopwatch.StartNew();
        var pending = CodemodeSandbox.ExecuteAsync(code, context, new Dictionary<string, JsonElement>(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        var elapsed = Stopwatch.StartNew();
        if (!timeout) cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.Ok);
        if (timeout)
        {
            Assert.Equal("Codemode timed out after 2000 ms.", result.Error);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));
        }
        else Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        elapsed.Restart();
        await WaitForWorkerExitAsync();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConcurrentBurstAdmitsOnlyTwoWorkers()
    {
        await WaitForWorkerExitAsync();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var hold = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            if (Interlocked.Increment(ref count) == 2) bothEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, name: "hold");
        var registry = new PiSharpToolRegistry([new(hold, ToolExposure.CodeMode)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
                () => new Dictionary<string, AIFunction> { ["hold"] = hold }, _ => { }, "test", null, "codemode",
                new Dictionary<string, object?>());
            return CodemodeSandbox.ExecuteAsync("await tools.hold({})", context, new Dictionary<string, JsonElement>(), cancellation.Token);
        })).ToArray();
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            while (calls.Count(call => call.IsCompleted) < 4) await Task.Delay(10, cancellation.Token);
            Assert.Equal(2, CodemodeWorkerLifetime.ActiveWorkers);
            Assert.Equal(4, calls.Count(call => call.IsCompletedSuccessfully && call.Result.Error == CodemodeWorkerLifetime.AdmissionError));
        }
        finally { cancellation.Cancel(); await Task.WhenAll(calls); }
    }

    [Fact]
    public async Task FailedWorkerStartReturnsItsReservedCapacity()
    {
        await WaitForWorkerExitAsync();
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(), () => new Dictionary<string, AIFunction>(),
            _ => { }, "test", null, "codemode", new Dictionary<string, object?>());
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            var result = await CodemodeSandbox.ExecuteAsync("return 1", context, new Dictionary<string, JsonElement>(), default);
            Assert.False(result.Ok);
            Assert.StartsWith("Codemode requires Node.js", result.Error);
            Assert.Equal(0, CodemodeWorkerLifetime.ActiveWorkers);
        }
        finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        var replacement = await CodemodeSandbox.ExecuteAsync("return 2", context, new Dictionary<string, JsonElement>(), default);
        Assert.True(replacement.Ok, replacement.Error);
        Assert.Equal("2", replacement.Text);
    }

    [Fact]
    public async Task CapacityStaysHeldUntilTheActualExitSignalAndReleasesExactlyOnce()
    {
        await WaitForWorkerExitAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        CodemodeWorkerLifetime? first;
        while (!CodemodeWorkerLifetime.TryAcquire(out first)) await Task.Delay(10, deadline.Token);
        CodemodeWorkerLifetime? second;
        while (!CodemodeWorkerLifetime.TryAcquire(out second)) await Task.Delay(10, deadline.Token);
        var firstExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first!.TrackExit(firstExit.Task);
        second!.TrackExit(secondExit.Task);
        try
        {
            Assert.False(CodemodeWorkerLifetime.TryAcquire(out _));
            var registry = new PiSharpToolRegistry([]);
            var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(), () => new Dictionary<string, AIFunction>(),
                _ => { }, "test", null, "codemode", new Dictionary<string, object?>());
            var blocked = await CodemodeSandbox.ExecuteAsync("return 1", context, new Dictionary<string, JsonElement>(), default);
            Assert.Equal(CodemodeWorkerLifetime.AdmissionError, blocked.Error);
            firstExit.TrySetResult();
            CodemodeWorkerLifetime? replacement;
            while (!CodemodeWorkerLifetime.TryAcquire(out replacement)) await Task.Delay(10, deadline.Token);
            Assert.False(CodemodeWorkerLifetime.TryAcquire(out _));
            first.Release();
            Assert.False(CodemodeWorkerLifetime.TryAcquire(out _));
            replacement!.Release();
        }
        finally
        {
            firstExit.TrySetResult();
            secondExit.TrySetResult();
            first.Release();
            second.Release();
        }
    }

    [Fact]
    public async Task BuiltinReturnsPartialOutputAfterCancellationWithoutCommittingStore()
    {
        await WaitForWorkerExitAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }, name: "hold");
        var registration = new ExtensionRegistration();
        registration.AddTool(new PiSharpToolRegistration(hold, ToolExposure.CodeMode));
        CodemodeBuiltin.Configure(registration);
        var registry = new PiSharpToolRegistry(registration.ToolDefinitions);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(["codemode"]),
            () => registration.ToolDefinitions.ToDictionary(tool => tool.Function.Name, tool => tool.Function),
            _ => { }, "test", null, "codemode", new Dictionary<string, object?>());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["code"] = "text('partial'); store('uncommitted', 1); await tools.hold({});"
        })
        { Context = new Dictionary<object, object?> { [PiSharpToolExecutionContext.ContextKey] = context } };
        var pending = registration.ToolDefinitions.Single(tool => tool.Function.Name == "codemode").Function
            .InvokeAsync(arguments, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var result = Assert.IsType<PiSharpToolResult>(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(result.IsError);
        Assert.StartsWith("partial\n", result.Text);
        Assert.Empty(context.CodemodeStore);
    }

    [Fact]
    public async Task TwoConcurrentSessionsShareAdmissionAndAlreadyCanceledCallsDoNotConsumeCapacity()
    {
        await WaitForWorkerExitAsync();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var hold = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }, name: "hold");
        PiSharpToolExecutionContext Context()
        {
            var registry = new PiSharpToolRegistry([new(hold, ToolExposure.CodeMode)]);
            return PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
                () => new Dictionary<string, AIFunction> { ["hold"] = hold }, _ => { }, "test", null, "codemode",
                new Dictionary<string, object?>());
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = CodemodeSandbox.ExecuteAsync("await tools.hold({})", Context(), new Dictionary<string, JsonElement>(), cancellation.Token);
        var second = CodemodeSandbox.ExecuteAsync("await tools.hold({})", Context(), new Dictionary<string, JsonElement>(), cancellation.Token);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var blocked = await CodemodeSandbox.ExecuteAsync("return 3", Context(), new Dictionary<string, JsonElement>(), default);
            Assert.False(blocked.Ok);
            Assert.Equal(CodemodeWorkerLifetime.AdmissionError, blocked.Error);
            using var alreadyCanceled = new CancellationTokenSource();
            alreadyCanceled.Cancel();
            var aborted = await CodemodeSandbox.ExecuteAsync("return 4", Context(), new Dictionary<string, JsonElement>(), alreadyCanceled.Token);
            Assert.Equal("Codemode was cancelled.", aborted.Error);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(first, second);
        }
        using var settled = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        CodemodeSandbox.Result replacement;
        do
        {
            replacement = await CodemodeSandbox.ExecuteAsync("return 5", Context(), new Dictionary<string, JsonElement>(), settled.Token);
            if (replacement.Error == CodemodeWorkerLifetime.AdmissionError) await Task.Delay(10, settled.Token);
        } while (replacement.Error == CodemodeWorkerLifetime.AdmissionError);
        Assert.True(replacement.Ok, replacement.Error);
        Assert.Equal("5", replacement.Text);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("partial")]
    [InlineData("timeout")]
    public async Task CancellationSettlesWithoutWaitingForAnUncooperativeHostCall(string check)
    {
        await WaitForWorkerExitAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = AIFunctionFactory.Create(async () =>
        {
            entered.TrySetResult();
            return await release.Task;
        }, name: "hold");
        var registry = new PiSharpToolRegistry([new(hold, ToolExposure.CodeMode)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["hold"] = hold }, _ => { }, "test", null, "codemode",
            new Dictionary<string, object?>());
        using var cancellation = new CancellationTokenSource();
        var code = (check == "timeout" ? "// @options: {\"timeout_ms\":500}\n" : "") +
            "text('partial'); await tools.hold({});";
        var pending = CodemodeSandbox.ExecuteAsync(code, context,
            new Dictionary<string, JsonElement>(), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var elapsed = Stopwatch.StartNew();
            if (check != "timeout") cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Ok);
            if (check == "prompt")
                Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(750), $"Cancellation waited {elapsed.Elapsed}.");
            else Assert.Equal("partial", result.Text);
            if (check == "timeout")
                Assert.Equal("Codemode timed out after 500 ms.", result.Error);
        }
        finally
        {
            release.TrySetResult("released");
            cancellation.Cancel();
            _ = await pending;
        }
    }

    private static async Task WaitForWorkerExitAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (CodemodeWorkerLifetime.ActiveWorkers > 0) await Task.Delay(10, deadline.Token);
    }
}
