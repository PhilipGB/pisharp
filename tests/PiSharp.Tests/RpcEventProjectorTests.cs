using System.Text.Json;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcEventProjectorTests
{
    [Fact]
    public void ProjectsQueueSnapshotsAndRetainsExplicitFormatForUnmappedEvents()
    {
        var projector = new RpcEventProjector();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var queue = projector.Project(new AgentLifecycleEvent("queue_update",
            Text: "{\"steering\":[\"first\"],\"followUp\":[\"later\"]}"), conversation, null,
            runAccepted: true, api: null);
        var generic = projector.Project(new AgentLifecycleEvent("model_text_delta", Text: "hello"), conversation,
            null, runAccepted: true, api: null);
        var rejectedEnd = projector.Project(new AgentLifecycleEvent("agent_run_completed"), conversation, null,
            runAccepted: false, api: null);

        using var queueRecord = JsonDocument.Parse(JsonSerializer.Serialize(queue));
        Assert.Equal("queue_update", queueRecord.RootElement.GetProperty("type").GetString());
        Assert.Equal("first", queueRecord.RootElement.GetProperty("steering")[0].GetString());
        Assert.Equal("later", queueRecord.RootElement.GetProperty("followUp")[0].GetString());
        using var genericRecord = JsonDocument.Parse(JsonSerializer.Serialize(generic));
        Assert.Equal("pisharp", genericRecord.RootElement.GetProperty("format").GetString());
        Assert.Equal("model_text_delta", genericRecord.RootElement.GetProperty("data").GetProperty("Type").GetString());
        Assert.Null(rejectedEnd);
    }
}
