using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

/// <summary>Executable acceptance matrix for the tool-result contract milestone.</summary>
public sealed class ToolResultContractMatrixTests
{
    private const string ImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    public static TheoryData<string, bool[], int> TerminationCases => new()
    {
        { "T01", [false], 2 },
        { "T02", [true], 1 },
        { "T03", [false, false], 2 },
        { "T04", [true, false], 2 },
        { "T05", [false, true], 2 },
        { "T06", [true, true], 1 },
        { "T07", [true, true, false], 2 },
        { "T08", [true, true, true], 1 },
        { "T09", [true, true], 1 }
    };

    [Theory]
    [MemberData(nameof(TerminationCases))]
    public async Task TerminationPermutations(string caseId, bool[] terminate, int expectedRequests)
    {
        await using var harness = await MatrixHarness.RunAsync(caseId,
            [new ProviderTurn(Calls(terminate.Length), "done")],
            Enumerable.Range(0, terminate.Length).Select(index => MatrixHarness.ResultTool(
                "tool" + index, () => new PiSharpToolResult("result " + index,
                    Terminate: terminate[index],
                    IsError: caseId == "T09" && index == 1))).ToArray());
        await harness.ExecuteAsync("batch");

        var canonical = harness.CanonicalResults();
        Assert.Equal(expectedRequests, harness.Client.RequestCount);
        Assert.All(Enumerable.Range(0, terminate.Length), index =>
            Assert.Equal(1, harness.Executions.GetValueOrDefault("tool" + index)));
        Assert.Equal(terminate.Length, harness.Events.Count(item => item.Type == "tool_execution_finished"));
        Assert.Equal(Enumerable.Range(0, terminate.Length).Select(index => "call-" + index),
            canonical.Select(result => result.CallId));
        Assert.Equal(canonical.Select(result => result.CallId),
            harness.PiJsonlResults().Select(result => result.GetProperty("toolCallId").GetString()));

        Evidence.Write(caseId, nameof(TerminationPermutations), harness.Observation(caseId, expectedRequests,
            new { terminate }));
    }

    [Fact]
    public async Task ParallelToolsCompleteInReverseOrderWithoutChangingTerminationOrResultOrder()
    {
        var started = new[] { Signal(), Signal() };
        var release = new[] { Signal(), Signal() };
        var calls = Calls(2);
        await using var harness = await MatrixHarness.RunAsync("T10",
            [new ProviderTurn(calls, "done")],
            Enumerable.Range(0, 2).Select(index => MatrixHarness.Tool("tool" + index, async (_, token) =>
            {
                started[index].TrySetResult();
                await release[index].Task.WaitAsync(token);
                return new PiSharpToolResult("result " + index, Terminate: true);
            })).ToArray());

        var collect = harness.ExecuteAsync("reverse order");
        await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        release[1].TrySetResult();
        await harness.WaitForFinishedAsync("call-1");
        release[0].TrySetResult();
        var events = await collect.WaitAsync(TimeSpan.FromSeconds(10));

        var canonical = harness.CanonicalResults();
        Assert.Equal(1, harness.Client.RequestCount);
        Assert.Equal(["tool0", "tool1"], harness.Executions.OrderBy(item => item.Key).Select(item => item.Key));
        Assert.All(harness.Executions, item => Assert.Equal(1, item.Value));
        Assert.Equal(["call-0", "call-1"], canonical.Select(result => result.CallId));
        Assert.Equal(canonical.Select(result => result.CallId),
            harness.PiJsonlResults().Select(result => result.GetProperty("toolCallId").GetString()));
        Assert.Equal(2, events.Count(item => item.Type == "tool_execution_finished"));

        Evidence.Write("T10", nameof(ParallelToolsCompleteInReverseOrderWithoutChangingTerminationOrResultOrder),
            harness.Observation("T10", 1, new { completionOrder = new[] { "call-1", "call-0" } }));
    }

    [Fact]
    public async Task SequentialToolsBothExecuteBeforeTheMixedBatchContinues()
    {
        var active = 0;
        var maximum = 0;
        var order = new ConcurrentQueue<string>();
        await using var harness = await MatrixHarness.RunAsync("T11",
            [new ProviderTurn(Calls(2), "done")],
            [
                MatrixHarness.Tool("tool0", async (_, token) =>
                {
                    var running = Interlocked.Increment(ref active);
                    maximum = Math.Max(maximum, running);
                    order.Enqueue("tool0-start");
                    await Task.Delay(50, token);
                    order.Enqueue("tool0-end");
                    Interlocked.Decrement(ref active);
                    return new PiSharpToolResult("first", Terminate: true);
                }, ToolExecutionMode.Sequential),
                MatrixHarness.Tool("tool1", async (_, token) =>
                {
                    var running = Interlocked.Increment(ref active);
                    maximum = Math.Max(maximum, running);
                    order.Enqueue("tool1-start");
                    await Task.Delay(50, token);
                    order.Enqueue("tool1-end");
                    Interlocked.Decrement(ref active);
                    return new PiSharpToolResult("second");
                }, ToolExecutionMode.Sequential)
            ]);

        var events = await harness.ExecuteAsync("sequential");
        var canonical = harness.CanonicalResults();
        Assert.Equal(2, harness.Client.RequestCount);
        Assert.Equal(1, maximum);
        Assert.Equal(["tool0-start", "tool0-end", "tool1-start", "tool1-end"], order);
        Assert.All(harness.Executions, item => Assert.Equal(1, item.Value));
        Assert.Equal(["call-0", "call-1"], canonical.Select(result => result.CallId));
        Assert.Equal(2, events.Count(item => item.Type == "tool_execution_finished"));

        Evidence.Write("T11", nameof(SequentialToolsBothExecuteBeforeTheMixedBatchContinues),
            harness.Observation("T11", 2, new { maximumConcurrency = maximum, executionOrder = order }));
    }

    [Fact]
    public async Task ParallelMixedBatchRunsEveryToolBeforeContinuing()
    {
        var started = new[] { Signal(), Signal() };
        var release = new[] { Signal(), Signal() };
        await using var harness = await MatrixHarness.RunAsync("T12",
            [new ProviderTurn(Calls(2), "done")],
            Enumerable.Range(0, 2).Select(index => MatrixHarness.Tool("tool" + index, async (_, token) =>
            {
                started[index].TrySetResult();
                await release[index].Task.WaitAsync(token);
                return new PiSharpToolResult("result " + index, Terminate: index == 0);
            })).ToArray());

        var collect = harness.ExecuteAsync("parallel mixed");
        await Task.WhenAll(started.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        release[1].TrySetResult();
        release[0].TrySetResult();
        await collect.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, harness.Client.RequestCount);
        Assert.All(harness.Executions, item => Assert.Equal(1, item.Value));
        Assert.Equal(["call-0", "call-1"], harness.CanonicalResults().Select(result => result.CallId));

        Evidence.Write("T12", nameof(ParallelMixedBatchRunsEveryToolBeforeContinuing),
            harness.Observation("T12", 2, new { terminate = new[] { true, false } }));
    }

    [Fact]
    public async Task SuccessfulResultContinuesWithFalseErrorState()
    {
        await using var harness = await MatrixHarness.RunAsync("E01",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("fine"))]);
        await harness.ExecuteAsync("success");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        Assert.Equal(2, harness.Client.RequestCount);
        Assert.False(finished.IsError);
        Assert.Null(result.Exception);
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.False(contract.IsError);

        Evidence.Write("E01", nameof(SuccessfulResultContinuesWithFalseErrorState),
            harness.Observation("E01", 2, new { }));
    }

    [Fact]
    public async Task ReturnedErrorIsNotAThrownException()
    {
        await using var harness = await MatrixHarness.RunAsync("E02",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("bad", Details: new { source = "domain" },
                IsError: true, Error: "domain failure", StructuredContent: Json("""{"count":2}""")))]);
        await harness.ExecuteAsync("returned error");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        Assert.True(finished.IsError);
        Assert.Equal("domain failure", finished.Error);
        Assert.Null(result.Exception);
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.True(contract.IsError);
        Assert.Equal("domain failure", contract.Error);
        Assert.Equal("bad", harness.Client.Requests[1].ToolResults.Single().Text);
        Assert.Null(harness.Client.Requests[1].ToolResults.Single().StructuredContent);

        Evidence.Write("E02", nameof(ReturnedErrorIsNotAThrownException),
            harness.Observation("E02", 2, new { exceptionRecorded = result.Exception is not null }));
    }

    [Fact]
    public async Task ThrownExceptionRemainsDistinctFromReturnedError()
    {
        await using var harness = await MatrixHarness.RunAsync("E03",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.Tool("tool0", (_, _) => throw new InvalidOperationException("boom"))]);
        await harness.ExecuteAsync("thrown error");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        Assert.True(finished.IsError);
        Assert.Equal("boom", finished.Error);
        Assert.NotNull(result.Exception);
        Assert.Contains("boom", result.Exception!.Message);
        Assert.Equal(2, harness.Client.RequestCount);
        Assert.NotNull(harness.Client.Requests[1].ToolResults.Single().ExceptionType);

        Evidence.Write("E03", nameof(ThrownExceptionRemainsDistinctFromReturnedError),
            harness.Observation("E03", 2, new { exceptionRecorded = result.Exception is not null }));
    }

    [Fact]
    public async Task UnknownInvalidAndBlockedInvocationsProduceErrorsWithoutRunningHandlers()
    {
        var blocked = 0;
        var invalid = 0;
        var callHook = new PiSharpToolCallHook((context, _) =>
            ValueTask.FromResult(context.ToolName == "blocked"
                ? PiSharpToolCallDecision.Block("policy blocked")
                : PiSharpToolCallDecision.Allow));
        await using var harness = await MatrixHarness.RunAsync("E04",
            [new ProviderTurn([
                new("call-unknown", "unknown", new Dictionary<string, object?>()),
                new("call-invalid", "invalid", new Dictionary<string, object?> { ["value"] = "not-an-integer" }),
                new("call-blocked", "blocked", new Dictionary<string, object?>())
            ], "done")],
            [
                new PiSharpToolRegistration(AIFunctionFactory.Create((int value) =>
                {
                    invalid++;
                    return new PiSharpToolResult("ran");
                }, name: "invalid"), ToolExposure.ModelOnly),
                MatrixHarness.Tool("blocked", (_, _) => { blocked++; return Task.FromResult(new PiSharpToolResult("ran")); })
            ], callHooks: [callHook]);
        await harness.ExecuteAsync("invalid batch");

        Assert.Equal(0, invalid);
        Assert.Equal(0, blocked);
        Assert.Equal(2, harness.Client.RequestCount);
        var canonical = harness.CanonicalResults();
        Assert.Equal(["call-unknown", "call-invalid", "call-blocked"], canonical.Select(item => item.CallId));
        Assert.All(canonical, item => Assert.False(string.IsNullOrWhiteSpace(
            ToolResultOutput.TryRead(item.Result, out var text, out _) ? text : item.Result?.ToString())));

        Evidence.Write("E04", nameof(UnknownInvalidAndBlockedInvocationsProduceErrorsWithoutRunningHandlers),
            harness.Observation("E04", 2, new { invalidExecutions = invalid, blockedExecutions = blocked }));
    }

    [Fact]
    public async Task OutputSchemaViolationIsAVisibleErrorResult()
    {
        var schema = Json("""{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""");
        await using var harness = await MatrixHarness.RunAsync("E05",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("invalid",
                StructuredContent: Json("""{"count":"wrong"}""")), OutputSchema: schema)]);
        await harness.ExecuteAsync("schema");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        Assert.True(finished.IsError);
        Assert.Contains("does not match", finished.Error);
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.True(contract.IsError);
        Assert.Contains("does not match", contract.Text);
        Assert.Contains("does not match", harness.Client.Requests[1].ToolResults.Single().Text);
        Assert.Equal(2, harness.Client.RequestCount);

        Evidence.Write("E05", nameof(OutputSchemaViolationIsAVisibleErrorResult),
            harness.Observation("E05", 2, new { error = finished.Error }));
    }

    [Fact]
    public async Task FailedAndSuccessfulSiblingBothContinueBecauseFailureDoesNotTerminate()
    {
        await using var harness = await MatrixHarness.RunAsync("E06",
            [new ProviderTurn(Calls(2), "done")],
            [
                MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("bad", IsError: true, Error: "domain")),
                MatrixHarness.ResultTool("tool1", () => new PiSharpToolResult("good"))
            ]);
        await harness.ExecuteAsync("failed sibling");
        var canonical = harness.CanonicalResults();
        Assert.Equal(2, harness.Client.RequestCount);
        Assert.Equal(2, canonical.Count);
        Assert.True(ToolResultOutput.TryReadContract(canonical[0], out var failed));
        Assert.True(failed.IsError);
        Assert.False(ToolResultOutput.TryReadContract(canonical[1], out var success) && success.IsError);

        Evidence.Write("E06", nameof(FailedAndSuccessfulSiblingBothContinueBecauseFailureDoesNotTerminate),
            harness.Observation("E06", 2, new { }));
    }

    [Fact]
    public async Task ErrorResultTerminationStillDependsOnTheWholeBatch()
    {
        await using var single = await MatrixHarness.RunAsync("E07-single",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("bad", IsError: true, Error: "domain",
                Terminate: true))]);
        await single.ExecuteAsync("single error terminate");
        Assert.Equal(1, single.Client.RequestCount);

        await using var mixed = await MatrixHarness.RunAsync("E07-mixed",
            [new ProviderTurn(Calls(2), "done")],
            [
                MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("bad", IsError: true, Error: "domain",
                    Terminate: true)),
                MatrixHarness.ResultTool("tool1", () => new PiSharpToolResult("good"))
            ]);
        await mixed.ExecuteAsync("mixed error");
        Assert.Equal(2, mixed.Client.RequestCount);
        Assert.Equal(2, mixed.CanonicalResults().Count);

        Evidence.Write("E07", nameof(ErrorResultTerminationStillDependsOnTheWholeBatch),
            new { single = single.Observation("E07-single", 1, new { }), mixed = mixed.Observation("E07-mixed", 2, new { }) });
    }

    [Fact]
    public async Task TextStructuredAndUiDetailsStayDistinct()
    {
        var usage = new UsageDetails { TotalTokenCount = 11, InputTokenCount = 4, OutputTokenCount = 7 };
        await using var harness = await MatrixHarness.RunAsync("R01",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("model text",
                Details: new { ui = "details" }, StructuredContent: Json("""{"count":2}"""), Usage: usage))]);
        await harness.ExecuteAsync("representations");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.Equal("model text", contract.Text);
        Assert.Equal(2, contract.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.Contains("details", contract.Details!.ToString());
        Assert.Equal(11, contract.Usage!.TotalTokenCount);
        Assert.Equal("model text", harness.Client.Requests[1].ToolResults.Single().Text);
        Assert.Null(harness.Client.Requests[1].ToolResults.Single().StructuredContent);
        Assert.Equal(2, finished.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.NotNull(finished.Details);
        Assert.Equal(11, finished.ToolUsage?.TotalTokenCount);

        Evidence.Write("R01", nameof(TextStructuredAndUiDetailsStayDistinct),
            harness.Observation("R01", 2, new { representations = new[] { "text", "structured", "details" } }));
    }

    [Fact]
    public async Task ValidStructuredOutputPassesTheDeclaredSchemaWithoutLosingData()
    {
        var schema = Json("""{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""");
        await using var harness = await MatrixHarness.RunAsync("R02",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("valid",
                StructuredContent: Json("""{"count":2}""")), OutputSchema: schema)]);
        await harness.ExecuteAsync("valid schema");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        Assert.False(finished.IsError);
        Assert.Null(result.Exception);
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.Equal(2, contract.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.Equal(2, harness.Client.RequestCount);

        Evidence.Write("R02", nameof(ValidStructuredOutputPassesTheDeclaredSchemaWithoutLosingData),
            harness.Observation("R02", 2, new { }));
    }

    [Fact]
    public async Task TextAndImageContentReachTheModelAndTheCanonicalResult()
    {
        var image = new PiSharpToolImage("image/png", ImageBase64);
        await using var harness = await MatrixHarness.RunAsync("R03",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("with image", Images: [image]))]);
        await harness.ExecuteAsync("text and image");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        var result = Assert.Single(harness.CanonicalResults());
        var providerResult = harness.Client.Requests[1].ToolResults.Single();
        Assert.Equal("with image", providerResult.Text);
        Assert.Equal(1, providerResult.ImageCount);
        Assert.Equal("image/png", Assert.Single(harness.Client.Requests[1].Images));
        Assert.Single(finished.Images!);
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.Equal("with image", contract.Text);
        Assert.Equal("image/png", Assert.Single(contract.Images!).MimeType);
        var exported = Assert.Single(harness.PiJsonlResults());
        Assert.Contains(exported.GetProperty("content").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "image");

        Evidence.Write("R03", nameof(TextAndImageContentReachTheModelAndTheCanonicalResult),
            harness.Observation("R03", 2, new { image = "image/png" }));
    }

    [Fact]
    public async Task ProgressUpdatesAreOrderedAndFinalUsageIsRetained()
    {
        var usage = new UsageDetails { TotalTokenCount = 13 };
        await using var harness = await MatrixHarness.RunAsync("R04",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.Tool("tool0", (arguments, _) =>
            {
                var context = PiSharpToolExecutionContext.Get(arguments)!;
                context.ReportProgress("first");
                context.ReportProgress("second");
                return Task.FromResult(new PiSharpToolResult("finished", Usage: usage));
            })]);
        var events = await harness.ExecuteAsync("progress");
        var lifecycle = events.Where(item => item.Type.StartsWith("tool_execution", StringComparison.Ordinal))
            .Select(item => item.Type + ":" + item.Text).ToArray();
        Assert.Equal(["tool_execution_started:", "tool_execution_update:first", "tool_execution_update:second",
            "tool_execution_finished:finished"], lifecycle);
        var result = Assert.Single(harness.CanonicalResults());
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.Equal(13, contract.Usage!.TotalTokenCount);
        var exported = Assert.Single(harness.PiJsonlResults());
        Assert.Equal(13, exported.GetProperty("usage").GetProperty("totalTokens").GetInt32());

        Evidence.Write("R04", nameof(ProgressUpdatesAreOrderedAndFinalUsageIsRetained),
            harness.Observation("R04", 2, new { lifecycle }));
    }

    [Fact]
    public async Task NestedStructuredResultAndErrorMetadataReachTheParent()
    {
        PiSharpToolExecutionResult? nested = null;
        var child = MatrixHarness.ResultTool("child", () => new PiSharpToolResult("child error",
            Details: new { child = true }, StructuredContent: Json("""{"count":2}"""),
            IsError: true, Error: "child failed"));
        var parent = MatrixHarness.Tool("parent", async (arguments, token) =>
        {
            nested = await PiSharpToolExecutionContext.Get(arguments)!.ExecuteToolAsync("child",
                cancellationToken: token);
            return new PiSharpToolResult("parent ok", Details: new { nested = nested.Text });
        });
        await using var harness = await MatrixHarness.RunAsync("R05",
            [new ProviderTurn(Calls(1).Select(call => call with { Name = "parent" }).ToList(), "done")],
            [parent with { Exposure = ToolExposure.ModelOnly, AllowNestedInvocation = false }, child with { Exposure = ToolExposure.Deferred }]);
        await harness.ExecuteAsync("nested");
        Assert.NotNull(nested);
        Assert.True(nested.IsError);
        Assert.Equal("child failed", nested.Error);
        Assert.Equal(2, nested.StructuredContent!.Value.GetProperty("count").GetInt32());
        var childStart = Assert.Single(harness.Events, item => item.Type == "tool_execution_started" && item.Tool == "child");
        var childEnd = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished" && item.Tool == "child");
        Assert.Equal("call-0/1", childStart.ToolCallId);
        Assert.Equal("call-0", childStart.ParentToolCallId);
        Assert.True(childEnd.IsError);
        Assert.Equal("child failed", childEnd.Error);
        var providerResults = harness.Client.Requests[1].ToolResults;
        Assert.Single(providerResults);
        Assert.Equal("call-0", providerResults[0].CallId);
        Assert.Equal("parent ok", providerResults[0].Text);

        Evidence.Write("R05", nameof(NestedStructuredResultAndErrorMetadataReachTheParent),
            harness.Observation("R05", 2, new
            {
                nested = new
                {
                    nested.ToolCallId,
                    nested.IsError,
                    nested.Error,
                    nested.Text,
                    structured = nested.StructuredContent?.GetRawText()
                }
            }));
    }

    [Fact]
    public async Task ResultHookCanRequestTermination()
    {
        await using var harness = await MatrixHarness.RunAsync("H01",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("ok"))],
            resultHooks:
            [
                (context, _) =>
                {
                    ToolResultOutput.TryReadContract(context.Result, out var contract);
                    context.Result = contract with { Terminate = true };
                    return ValueTask.CompletedTask;
                }
            ]);
        await harness.ExecuteAsync("hook terminate");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        Assert.True(finished.Terminate);
        Assert.Equal(1, harness.Client.RequestCount);
        Assert.True(ToolResultOutput.TryReadContract(Assert.Single(harness.CanonicalResults()), out var canonical));
        Assert.True(canonical.Terminate);
        Assert.True(Assert.Single(harness.PiJsonlResults()).GetProperty("terminate").GetBoolean());

        Evidence.Write("H01", nameof(ResultHookCanRequestTermination),
            harness.Observation("H01", 1, new { }));
    }

    [Fact]
    public async Task ResultHookCanCancelTerminationAndContinue()
    {
        await using var harness = await MatrixHarness.RunAsync("H02",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("ok", Terminate: true))],
            resultHooks:
            [
                (context, _) =>
                {
                    ToolResultOutput.TryReadContract(context.Result, out var contract);
                    context.Result = contract with { Terminate = false };
                    return ValueTask.CompletedTask;
                }
            ]);
        await harness.ExecuteAsync("hook continue");
        Assert.Equal(2, harness.Client.RequestCount);
        Assert.True(ToolResultOutput.TryReadContract(Assert.Single(harness.CanonicalResults()), out var canonical));
        Assert.False(canonical.Terminate);
        Assert.False(Assert.Single(harness.PiJsonlResults()).TryGetProperty("terminate", out _));

        Evidence.Write("H02", nameof(ResultHookCanCancelTerminationAndContinue),
            harness.Observation("H02", 2, new { }));
    }

    [Fact]
    public async Task ResultHookTextAndErrorAreObservedByEventsProviderAndHistory()
    {
        await using var harness = await MatrixHarness.RunAsync("H03",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("original",
                Details: new { ui = 1 }, Usage: new UsageDetails { TotalTokenCount = 7 }))],
            resultHooks:
            [
                (context, _) =>
                {
                    context.Result = "hooked";
                    context.IsError = true;
                    context.Error = "hooked error";
                    return ValueTask.CompletedTask;
                }
            ]);
        await harness.ExecuteAsync("hook text");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        Assert.Equal("hooked", finished.Text);
        Assert.True(finished.IsError);
        Assert.Equal("hooked error", finished.Error);
        Assert.Equal("hooked", harness.Client.Requests[1].ToolResults.Single().Text);
        Assert.True(ToolResultOutput.TryReadContract(Assert.Single(harness.CanonicalResults()), out var canonical));
        Assert.Equal("hooked", canonical.Text);
        Assert.True(canonical.IsError);
        Assert.Equal("hooked error", canonical.Error);
        Assert.Equal(7, canonical.Usage!.TotalTokenCount);

        Evidence.Write("H03", nameof(ResultHookTextAndErrorAreObservedByEventsProviderAndHistory),
            harness.Observation("H03", 2, new { }));
    }

    [Fact]
    public async Task ContentOnlyHookReplacementDropsStructuredContentButKeepsMetadata()
    {
        await using var harness = await MatrixHarness.RunAsync("H04",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("original",
                Details: new { ui = 1 }, StructuredContent: Json("""{"count":1}"""),
                Usage: new UsageDetails { TotalTokenCount = 7 }))],
            resultHooks:
            [
                (context, _) =>
                {
                    context.Result = "redacted";
                    return ValueTask.CompletedTask;
                }
            ]);
        await harness.ExecuteAsync("hook redact");
        Assert.Equal("redacted", harness.Client.Requests[1].ToolResults.Single().Text);
        Assert.True(ToolResultOutput.TryReadContract(Assert.Single(harness.CanonicalResults()), out var canonical));
        Assert.Equal("redacted", canonical.Text);
        Assert.Null(canonical.StructuredContent);
        Assert.Contains("ui", canonical.Details!.ToString());
        Assert.Equal(7, canonical.Usage!.TotalTokenCount);

        Evidence.Write("H04", nameof(ContentOnlyHookReplacementDropsStructuredContentButKeepsMetadata),
            harness.Observation("H04", 2, new { structuredContent = canonical.StructuredContent is not null }));
    }

    [Fact]
    public async Task ThrowingResultHookBecomesAnErrorResultNotASuccess()
    {
        await using var harness = await MatrixHarness.RunAsync("H05",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("original"))],
            resultHooks: [new PiSharpToolResultHook((_, _) => throw new InvalidOperationException("hook boom"))]);
        await harness.ExecuteAsync("hook throws");
        var finished = Assert.Single(harness.Events, item => item.Type == "tool_execution_finished");
        Assert.True(finished.IsError);
        Assert.Equal("hook boom", finished.Text);
        Assert.Equal("hook boom", finished.Error);
        Assert.Equal("hook boom", harness.Client.Requests[1].ToolResults.Single().Text);
        Assert.True(ToolResultOutput.TryReadContract(Assert.Single(harness.CanonicalResults()), out var canonical));
        Assert.True(canonical.IsError);
        Assert.Equal("hook boom", canonical.Text);

        Evidence.Write("H05", nameof(ThrowingResultHookBecomesAnErrorResultNotASuccess),
            harness.Observation("H05", 2, new { }));
    }

    [Fact]
    public async Task ProviderContinuationReceivesEveryResultInModelCallOrder()
    {
        var release = new[] { Signal(), Signal(), Signal() };
        await using var harness = await MatrixHarness.RunAsync("P01",
            [new ProviderTurn(Calls(3), "done")],
            Enumerable.Range(0, 3).Select(index => MatrixHarness.Tool("tool" + index, async (_, token) =>
            {
                await release[index].Task.WaitAsync(token);
                return new PiSharpToolResult("result " + index);
            })).ToArray());
        var collect = harness.ExecuteAsync("provider continuation");
        await Task.Delay(30);
        release[2].TrySetResult();
        release[1].TrySetResult();
        release[0].TrySetResult();
        await collect.WaitAsync(TimeSpan.FromSeconds(10));
        var providerResults = harness.Client.Requests[1].ToolResults;
        Assert.Equal(["call-0", "call-1", "call-2"], providerResults.Select(result => result.CallId));
        Assert.Equal(["result 0", "result 1", "result 2"], providerResults.Select(result => result.Text));
        Assert.Equal(2, harness.Client.RequestCount);

        Evidence.Write("P01", nameof(ProviderContinuationReceivesEveryResultInModelCallOrder),
            harness.Observation("P01", 2, new { completion = new[] { "call-2", "call-1", "call-0" } }));
    }

    [Fact]
    public async Task CanonicalSaveAndReloadPreservesTheResultWithoutReexecution()
    {
        await using var harness = await MatrixHarness.RunAsync("P02",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("persisted",
                Details: new { ui = 1 }, StructuredContent: Json("""{"count":2}"""),
                Usage: new UsageDetails { TotalTokenCount = 9 }))]);
        await harness.ExecuteAsync("save");
        var restored = ConversationSession.Parse(harness.Session.ToJson());
        var result = Assert.Single(restored.ActiveMessages().SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.Equal("persisted", contract.Text);
        Assert.Equal(2, contract.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.Equal(9, contract.Usage!.TotalTokenCount);
        Assert.Equal(1, harness.Executions["tool0"]);

        var next = new MatrixClient([new ProviderTurn([], "next")]);
        var agent = new PiAgent(next, new CodingTools(harness.Root), noBuiltinTools: true,
            extensionToolRegistrations: [MatrixHarness.ResultTool("tool0",
                () => new PiSharpToolResult("should not run"))]);
        var run = await ConversationRun.OpenAsync(agent, restored, save: _ => Task.CompletedTask);
        await foreach (var _ in run.RunEventsAsync("next")) { }
        Assert.Equal(1, next.RequestCount);
        Assert.Equal(1, harness.Executions["tool0"]);

        Evidence.Write("P02", nameof(CanonicalSaveAndReloadPreservesTheResultWithoutReexecution),
            harness.Observation("P02", 2, new { reexecutions = harness.Executions["tool0"] - 1 }));
    }

    [Fact]
    public async Task PiJsonlRoundTripPreservesSupportedResultFields()
    {
        var image = new PiSharpToolImage("image/png", ImageBase64);
        var usage = new UsageDetails { TotalTokenCount = 12 };
        await using var harness = await MatrixHarness.RunAsync("P03",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("round trip",
                Details: new { ui = 1 }, StructuredContent: Json("""{"count":2}"""),
                Images: [image], Usage: usage, Terminate: true))]);
        await harness.ExecuteAsync("jsonl");
        var exported = Assert.Single(harness.PiJsonlResults());
        Assert.True(exported.GetProperty("terminate").GetBoolean());
        Assert.Equal(12, exported.GetProperty("usage").GetProperty("totalTokens").GetInt32());
        Assert.True(exported.GetProperty("piSharpToolResult").GetProperty("piSharpToolResult").GetBoolean());

        var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(harness.Session));
        var result = Assert.Single(imported.ActiveMessages().SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.True(ToolResultOutput.TryReadContract(result, out var contract));
        Assert.Equal("round trip", contract.Text);
        Assert.Equal(2, contract.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.Contains("ui", contract.Details!.ToString());
        Assert.Equal("image/png", Assert.Single(contract.Images!).MimeType);
        Assert.Equal(12, contract.Usage!.TotalTokenCount);
        Assert.True(contract.Terminate);
        Assert.Null(result.Exception);

        Evidence.Write("P03", nameof(PiJsonlRoundTripPreservesSupportedResultFields),
            harness.Observation("P03", 1, new { roundTrip = true }));
    }

    [Fact]
    public async Task ReturnedAndThrownToolErrorsRemainDistinctAfterReload()
    {
        await using var harness = await MatrixHarness.RunAsync("P04",
            [new ProviderTurn(Calls(2), "done")],
            [
                MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("returned", IsError: true,
                    Error: "domain")),
                MatrixHarness.Tool("tool1", (_, _) => throw new InvalidOperationException("thrown"))
            ]);
        await harness.ExecuteAsync("reload distinction");
        var canonicalJson = ConversationSession.Parse(harness.Session.ToJson());
        var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(harness.Session));
        foreach (var session in new[] { canonicalJson, imported })
        {
            var results = session.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().ToArray();
            Assert.Equal(["call-0", "call-1"], results.Select(result => result.CallId));
            Assert.Null(results[0].Exception);
            Assert.True(ToolResultOutput.TryReadContract(results[0], out var returned));
            Assert.True(returned.IsError);
            Assert.Equal("domain", returned.Error);
            Assert.NotNull(results[1].Exception);
            Assert.Contains("thrown", results[1].Exception!.Message);
        }

        Evidence.Write("P04", nameof(ReturnedAndThrownToolErrorsRemainDistinctAfterReload),
            harness.Observation("P04", 2, new { }));
    }

    [Fact]
    public async Task TerminatingBatchIsNotReplayedByANewUserTurn()
    {
        await using var harness = await MatrixHarness.RunAsync("P05",
            [new ProviderTurn(Calls(1), "done")],
            [MatrixHarness.ResultTool("tool0", () => new PiSharpToolResult("terminal", Terminate: true))]);
        await harness.ExecuteAsync("first");
        Assert.Equal(1, harness.Client.RequestCount);
        Assert.Equal(1, harness.Executions["tool0"]);

        await harness.ExecuteAsync("next");
        Assert.Equal(2, harness.Client.RequestCount);
        Assert.Equal(1, harness.Executions["tool0"]);
        var second = harness.Client.Requests[1];
        Assert.Contains("next", second.UserTexts);
        Assert.Equal(["call-0"], second.ToolResults.Select(result => result.CallId));
        Assert.Equal("terminal", second.ToolResults[0].Text);

        Evidence.Write("P05", nameof(TerminatingBatchIsNotReplayedByANewUserTurn),
            harness.Observation("P05", 2, new { reexecutions = harness.Executions["tool0"] - 1 }));
    }

    private static List<ToolCall> Calls(int count) =>
        Enumerable.Range(0, count).Select(index => new ToolCall("call-" + index, "tool" + index)).ToList();

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}

internal sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, object?>? Arguments = null);

internal sealed record ProviderTurn(IReadOnlyList<ToolCall> Calls, string FinalText = "done");

internal sealed record ProviderToolResult(string CallId, string? Text, string? ExceptionType,
    string? ExceptionMessage, int ImageCount, JsonElement? StructuredContent);

internal sealed record ProviderRequest(int Index, IReadOnlyList<string> UserTexts,
    IReadOnlyList<string> ToolCallIds, IReadOnlyList<ProviderToolResult> ToolResults,
    IReadOnlyList<string> Images);

internal sealed class MatrixClient(IReadOnlyList<ProviderTurn> turns) : IChatClient
{
    private int _requests;
    public int RequestCount => Volatile.Read(ref _requests);
    public List<ProviderRequest> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = Interlocked.Increment(ref _requests);
        var snapshot = messages.ToArray();
        Requests.Add(new ProviderRequest(request,
            snapshot.Where(message => message.Role == ChatRole.User)
                .SelectMany(message => message.Contents.OfType<TextContent>().Select(content => content.Text)).ToArray(),
            snapshot.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
                .Select(call => call.CallId).ToArray(),
            snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Select(result => new ProviderToolResult(result.CallId,
                    result.Result as string ?? result.Result?.ToString(),
                    result.Exception?.GetType().Name, result.Exception?.Message,
                    snapshot.SelectMany(message => message.Contents).OfType<DataContent>().Count(),
                    result.AdditionalProperties?.TryGetValue(
                        ToolResultOutput.StructuredContentProperty, out var structured) == true
                        ? JsonSerializer.SerializeToElement(structured) : null)).ToArray(),
            snapshot.SelectMany(message => message.Contents).OfType<DataContent>()
                .Select(image => image.MediaType ?? "").ToArray()));
        if (request <= turns.Count)
        {
            var turn = turns[request - 1];
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                turn.Calls.Select(call => (AIContent)new FunctionCallContent(call.Id, call.Name,
                    call.Arguments is null
                        ? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>(call.Arguments))).ToArray());
        }
        else yield return new ChatResponseUpdate(ChatRole.Assistant, turns[^1].FinalText);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class MatrixHarness : IAsyncDisposable
{
    private readonly List<AgentLifecycleEvent> _allEvents = [];

    private MatrixHarness(string root, MatrixClient client, ConversationSession session, ConversationRun run,
        ConcurrentDictionary<string, int> executions)
    {
        Root = root;
        Client = client;
        Session = session;
        Run = run;
        Executions = executions;
    }

    public string Root { get; }
    public MatrixClient Client { get; }
    public ConversationSession Session { get; }
    public ConversationRun Run { get; }
    public ConcurrentDictionary<string, int> Executions { get; }
    public List<AgentLifecycleEvent> Events => _allEvents;

    public static PiSharpToolRegistration ResultTool(string name, Func<PiSharpToolResult> result,
        JsonElement? OutputSchema = null, ToolExecutionMode mode = ToolExecutionMode.Parallel) =>
        Tool(name, (_, _) => Task.FromResult(result()), mode, OutputSchema: OutputSchema);

    public static AsyncLocal<ConcurrentDictionary<string, int>?> CurrentExecutions { get; } = new();

    public static PiSharpToolRegistration Tool(string name,
        Func<AIFunctionArguments, CancellationToken, Task<PiSharpToolResult>> body,
        ToolExecutionMode mode = ToolExecutionMode.Parallel, JsonElement? OutputSchema = null)
    {
        var function = AIFunctionFactory.Create(async (AIFunctionArguments arguments, CancellationToken token) =>
        {
            CurrentExecutions.Value?.AddOrUpdate(name, 1, (_, value) => value + 1);
            return await body(arguments, token);
        }, name: name);
        return new PiSharpToolRegistration(function, ToolExposure.ModelOnly, OutputSchema: OutputSchema,
            ExecutionMode: mode);
    }

    public static async Task<MatrixHarness> RunAsync(string caseId, IReadOnlyList<ProviderTurn> turns,
        IReadOnlyList<PiSharpToolRegistration> tools,
        IReadOnlyList<PiSharpToolResultHook>? resultHooks = null,
        IReadOnlyList<PiSharpToolCallHook>? callHooks = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tool-result-" + caseId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executions = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in tools.Select(item => item.Function.Name)) executions[name] = 0;
        CurrentExecutions.Value = executions;
        var wrapped = tools;
        var client = new MatrixClient(turns);
        var session = new ConversationSession(root, "fixture", null);
        var agent = new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
            extensionToolRegistrations: wrapped,
            extensionToolResultHooks: resultHooks,
            extensionToolCallHooks: callHooks);
        var run = await ConversationRun.OpenAsync(agent, session, save: _ => Task.CompletedTask);
        return new MatrixHarness(root, client, session, run, executions);
    }

    public async Task<List<AgentLifecycleEvent>> ExecuteAsync(string prompt)
    {
        CurrentExecutions.Value = Executions;
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in Run.RunEventsAsync(prompt))
        {
            lock (_allEvents) _allEvents.Add(item);
            events.Add(item);
        }
        return events;
    }

    public async Task WaitForFinishedAsync(string callId)
    {
        while (!_allEvents.Any(item => item.Type == "tool_execution_finished" && item.ToolCallId == callId))
            await Task.Delay(5);
    }

    public IReadOnlyList<FunctionResultContent> CanonicalResults() => Session.ActiveMessages()
        .SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();

    public IReadOnlyList<JsonElement> PiJsonlResults() => PiJsonlSessionInterchange.ProjectEntries(Session)
        .Where(entry => entry.TryGetProperty("message", out var message) &&
            message.GetProperty("role").GetString() == "toolResult")
        .Select(entry => entry.GetProperty("message")).ToArray();

    public object Observation(string caseId, int expectedRequests, object detail) => new
    {
        caseId,
        expectedRequests,
        requests = Client.RequestCount,
        executions = Executions.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
        calls = Client.Requests.FirstOrDefault()?.ToolCallIds ?? [],
        order = _allEvents.Where(item => item.Type is "tool_execution_started" or "tool_execution_finished")
            .Select(item => (item.Type == "tool_execution_started" ? "start:" : "end:") + item.ToolCallId).ToArray(),
        finished = _allEvents.Where(item => item.Type == "tool_execution_finished").Select(item => new
        {
            item.ToolCallId,
            item.Tool,
            item.IsError,
            item.Terminate,
            item.Text,
            item.Error,
            structured = item.StructuredContent?.GetRawText(),
            usage = item.ToolUsage?.TotalTokenCount
        }).ToArray(),
        canonical = CanonicalResults().Select(result =>
        {
            ToolResultOutput.TryReadContract(result, out var contract);
            return new
            {
                result.CallId,
                exception = result.Exception?.GetType().Name,
                text = contract?.Text,
                isError = result.Exception is not null || contract?.IsError == true,
                terminate = contract?.Terminate,
                structured = contract?.StructuredContent?.GetRawText(),
                details = contract?.Details is { } details
                    ? JsonSerializer.SerializeToElement(details).GetRawText()
                    : null,
                usage = contract?.Usage?.TotalTokenCount,
                images = contract?.Images?.Count
            };
        }).ToArray(),
        piJsonl = PiJsonlResults().Select(message => new
        {
            callId = message.GetProperty("toolCallId").GetString(),
            isError = message.GetProperty("isError").GetBoolean(),
            terminate = message.TryGetProperty("terminate", out var terminate) && terminate.GetBoolean(),
            text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? string.Join('\n', content.EnumerateArray()
                    .Where(item => item.GetProperty("type").GetString() == "text")
                    .Select(item => item.GetProperty("text").GetString()))
                : null
        }).ToArray(),
        providerRequests = Client.Requests.Select(request => new
        {
            request.Index,
            request.ToolCallIds,
            toolResults = request.ToolResults.Select(result => new
            {
                result.CallId,
                result.Text,
                result.ExceptionType,
                result.ImageCount,
                structured = result.StructuredContent?.GetRawText()
            }).ToArray()
        }).ToArray(),
        detail
    };

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class Evidence
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(string caseId, string test, object observations)
    {
        var directory = Environment.GetEnvironmentVariable("PISHARP_TOOL_RESULT_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var payload = new
        {
            @case = caseId,
            test,
            implementationSha = Environment.GetEnvironmentVariable("PISHARP_IMPLEMENTATION_SHA") ?? "unknown",
            piSha = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6",
            status = "passed",
            observations
        };
        File.WriteAllText(Path.Combine(directory, caseId + ".json"),
            JsonSerializer.Serialize(payload, Options) + "\n");
    }
}
