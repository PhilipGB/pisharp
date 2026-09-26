using System.Text.Json;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcEventProjectorTests
{
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
}
