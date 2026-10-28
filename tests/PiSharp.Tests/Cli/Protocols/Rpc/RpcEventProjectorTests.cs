using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcEventProjectorTests
{
    [Fact]
    public void CompactionResultOmitsUnknownCostBreakdownComponents()
    {
        var usage = UsageRecord.Create("fixture", "compaction", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 5,
            CachedInputTokenCount = null,
            TotalTokenCount = 15,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cacheWriteTokens"] = 0 }
        }, new ModelPricing(1m, 2m));
        var result = new ConversationCompactionResult("summary", "kept-entry", 100, 20, usage,
            new ConversationCompactionDetails([], []));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(RpcCompactionResultProjector.Project(result)));
        var cost = json.RootElement.GetProperty("usage").GetProperty("cost");

        Assert.False(cost.TryGetProperty("input", out _));
        Assert.Equal(0.00001m, cost.GetProperty("output").GetDecimal());
        Assert.False(cost.TryGetProperty("cacheRead", out _));
        Assert.Equal(0m, cost.GetProperty("cacheWrite").GetDecimal());
        Assert.Equal(0.00002m, cost.GetProperty("total").GetDecimal());
    }

    [Fact]
    public void BashInitialSnapshotHasEmptyContentAndOmitsAbsentDetails()
    {
        var projector = new RpcEventProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var projected = Assert.Single(projector.Project(new AgentLifecycleEvent("tool_execution_update", Tool: "bash")
        {
            ToolCallId = "bash-call",
            OutputIsSnapshot = true
        }, conversation, null, null, runAccepted: true, api: null));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected));
        var partial = json.RootElement.GetProperty("partialResult");
        Assert.Empty(partial.GetProperty("content").EnumerateArray());
        Assert.False(partial.TryGetProperty("details", out _));
    }

    [Fact]
    public void ProjectsRetryEventsWithPiFieldsAndOmitsAbsentFinalError()
    {
        var projector = new RpcEventProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var start = Assert.Single(projector.Project(new AgentLifecycleEvent("auto_retry_start", Error: "overloaded")
        {
            RetryAttempt = 2,
            RetryMaxAttempts = 3,
            RetryDelayMs = 4000
        }, conversation, null, null, runAccepted: true, api: null));
        var end = Assert.Single(projector.Project(new AgentLifecycleEvent("auto_retry_end")
        {
            RetryAttempt = 2,
            RetrySuccess = true
        }, conversation, null, null, runAccepted: true, api: null));

        using var startRecord = JsonDocument.Parse(JsonSerializer.Serialize(start));
        Assert.Equal(["type", "attempt", "maxAttempts", "delayMs", "errorMessage"],
            startRecord.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, startRecord.RootElement.GetProperty("attempt").GetInt32());
        Assert.Equal(3, startRecord.RootElement.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(4000, startRecord.RootElement.GetProperty("delayMs").GetInt64());
        using var endRecord = JsonDocument.Parse(JsonSerializer.Serialize(end));
        Assert.Equal(["type", "success", "attempt"], endRecord.RootElement.EnumerateObject()
            .Select(property => property.Name));
        Assert.True(endRecord.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(2, endRecord.RootElement.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public void ProjectsQueueSnapshotsAndRetainsExplicitFormatForUnmappedEvents()
    {
        var projector = new RpcEventProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var queue = Assert.Single(projector.Project(new AgentLifecycleEvent("queue_update",
            Text: "{\"steering\":[\"first\"],\"followUp\":[\"later\"]}"), conversation, null, null,
            runAccepted: true, api: null));
        Assert.Empty(projector.Project(new AgentLifecycleEvent("model_text_delta", Text: "hello"), conversation,
            null, null, runAccepted: true, api: null));
        var generic = Assert.Single(projector.Project(new AgentLifecycleEvent("custom_lifecycle"), conversation,
            null, null, runAccepted: true, api: null));
        Assert.Empty(projector.Project(new AgentLifecycleEvent("agent_run_completed"), conversation, null, null,
            runAccepted: false, api: null));

        using var queueRecord = JsonDocument.Parse(JsonSerializer.Serialize(queue));
        Assert.Equal("queue_update", queueRecord.RootElement.GetProperty("type").GetString());
        Assert.Equal("first", queueRecord.RootElement.GetProperty("steering")[0].GetString());
        Assert.Equal("later", queueRecord.RootElement.GetProperty("followUp")[0].GetString());
        using var genericRecord = JsonDocument.Parse(JsonSerializer.Serialize(generic));
        Assert.Equal("pisharp", genericRecord.RootElement.GetProperty("format").GetString());
        Assert.Equal("custom_lifecycle", genericRecord.RootElement.GetProperty("data").GetProperty("Type").GetString());
    }

    [Fact]
    public void ProjectsNestedToolLifecycleWithParentCallIdentity()
    {
        var projector = new RpcEventProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var start = Assert.Single(projector.Project(new AgentLifecycleEvent("tool_execution_started", Tool: "child")
        {
            ToolCallId = "root/1",
            ParentToolCallId = "root"
        }, conversation, null, null, runAccepted: true, api: null));
        var update = Assert.Single(projector.Project(new AgentLifecycleEvent("tool_execution_update", Tool: "child", Text: "working")
        {
            ToolCallId = "root/1",
            ParentToolCallId = "root"
        }, conversation, null, null, runAccepted: true, api: null));
        var end = Assert.Single(projector.Project(new AgentLifecycleEvent("tool_execution_finished", Tool: "child", Text: "done")
        {
            ToolCallId = "root/1",
            ParentToolCallId = "root"
        }, conversation, null, null, runAccepted: true, api: null));

        foreach (var projected in new[] { start, update, end })
        {
            using var record = JsonDocument.Parse(JsonSerializer.Serialize(projected));
            Assert.Equal("root/1", record.RootElement.GetProperty("toolCallId").GetString());
            Assert.Equal("root", record.RootElement.GetProperty("parentToolCallId").GetString());
        }
    }
}
