using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;

var results = new List<object>();
foreach (var how in new[] { "abort", "timeout" })
{
    await WaitForExitAsync();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var hostCanceled = false;
    var hold = AIFunctionFactory.Create(async (CancellationToken token) =>
    {
        using var registration = token.Register(() => hostCanceled = true);
        entered.TrySetResult();
        return await release.Task;
    }, name: "hold");
    var context = Context(hold);
    using var cancellation = new CancellationTokenSource();
    var code = (how == "timeout" ? "// @options: {\"timeout_ms\":500}\n" : "") +
        "text('partial'); store('uncommitted', 1); await tools.hold({});";
    var pending = CodemodeSandbox.ExecuteAsync(code, context, new Dictionary<string, JsonElement>(), cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var elapsed = Stopwatch.StartNew();
    if (how == "abort") cancellation.Cancel();
    try
    {
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        results.Add(new { id = how, failed = !result.Ok, partial = result.Text == "partial",
            settledPromptly = elapsed.ElapsedMilliseconds < 1000, hostCanceled, storeCommitted = result.Store is not null });
    }
    finally { release.TrySetResult("late"); }
}
foreach (var how in new[] { "abort", "timeout" })
{
    await WaitForExitAsync();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ready = AIFunctionFactory.Create(() => { entered.TrySetResult(); return "ready"; }, name: "hold");
    using var cancellation = new CancellationTokenSource();
    var code = (how == "timeout" ? "// @options: {\"timeout_ms\":2000}\n" : "") +
        "await tools.hold({}); return 'a'.repeat(300000).indexOf('a'.repeat(10000) + 'b');";
    var started = Stopwatch.StartNew();
    var pending = CodemodeSandbox.ExecuteAsync(code, Context(ready), new Dictionary<string, JsonElement>(), cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);
    var elapsed = Stopwatch.StartNew();
    if (how == "abort") cancellation.Cancel();
    var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    var prompt = how == "timeout" ? started.ElapsedMilliseconds < 3000 : elapsed.ElapsedMilliseconds < 1000;
    elapsed.Restart();
    await WaitForExitAsync();
    results.Add(new { id = "busy-" + how, failed = !result.Ok, settledPromptly = prompt,
        exitedPromptly = elapsed.ElapsedMilliseconds < 1000 });
}
await WaitForExitAsync();
var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var count = 0;
var blocker = AIFunctionFactory.Create(async (CancellationToken token) =>
{
    if (Interlocked.Increment(ref count) == 2) bothEntered.TrySetResult();
    await Task.Delay(Timeout.Infinite, token);
}, name: "hold");
using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    var first = CodemodeSandbox.ExecuteAsync("await tools.hold({})", Context(blocker), new Dictionary<string, JsonElement>(), cancellation.Token);
    var second = CodemodeSandbox.ExecuteAsync("await tools.hold({})", Context(blocker), new Dictionary<string, JsonElement>(), cancellation.Token);
    try
    {
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = await CodemodeSandbox.ExecuteAsync("return 3", Context(), new Dictionary<string, JsonElement>(), default);
        var aborted = await CodemodeSandbox.ExecuteAsync("return 4", Context(), new Dictionary<string, JsonElement>(), new(true));
        results.Add(new { id = "shared-admission", denied = blocked.Error == CodemodeWorkerLifetime.AdmissionError,
            cancellationWins = aborted.Error == "Codemode was cancelled.", workers = CodemodeWorkerLifetime.ActiveWorkers });
    }
    finally { cancellation.Cancel(); await Task.WhenAll(first, second); }
}
await WaitForExitAsync();
if (!CodemodeWorkerLifetime.TryAcquire(out var lease1) || !CodemodeWorkerLifetime.TryAcquire(out var lease2))
    throw new InvalidOperationException("Previous workers did not exit.");
var exit1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var exit2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
lease1!.TrackExit(exit1.Task);
lease2!.TrackExit(exit2.Task);
try
{
    var blocked = await CodemodeSandbox.ExecuteAsync("return 3", Context(), new Dictionary<string, JsonElement>(), default);
    exit1.SetResult();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (CodemodeWorkerLifetime.ActiveWorkers == 2) await Task.Delay(10, deadline.Token);
    var replacement = await CodemodeSandbox.ExecuteAsync("return 42", Context(), new Dictionary<string, JsonElement>(), default);
    results.Add(new { id = "exit-accounting", heldUntilExit = blocked.Error == CodemodeWorkerLifetime.AdmissionError,
        replacement = replacement.Ok && replacement.Text == "42" });
}
finally { exit2.SetResult(); lease1.Release(); lease2.Release(); }
await WaitForExitAsync();
Console.WriteLine(JsonSerializer.Serialize(results));

static PiSharpToolExecutionContext Context(AIFunction? hold = null)
{
    var registry = new PiSharpToolRegistry(hold is null ? [] : [new(hold, ToolExposure.CodeMode)]);
    return PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
        () => hold is null ? new Dictionary<string, AIFunction>() : new Dictionary<string, AIFunction> { ["hold"] = hold },
        _ => { }, "script", null, "codemode", new Dictionary<string, object?>());
}

static async Task WaitForExitAsync()
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (CodemodeWorkerLifetime.ActiveWorkers > 0) await Task.Delay(10, deadline.Token);
}
