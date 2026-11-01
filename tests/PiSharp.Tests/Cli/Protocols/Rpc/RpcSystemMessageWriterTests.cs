using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

/// <summary>
/// A resumed session must report a system message only when the prompt or tool state the model
/// already knows actually changed. These drive the writer directly so the run's transcript can be
/// applied before the writer examines its event, which is the ordering a channel consumer cannot
/// guarantee on its own.
/// </summary>
public sealed class RpcSystemMessageWriterTests
{
    [Fact]
    public async Task ChangedRestoredPromptEmitsOneSystemMessage()
    {
        var persisted = SystemMessage("restored prompt", tool: null);
        var current = SystemMessage("changed prompt", tool: null);
        var lines = await WriteModelRequestAsync(persisted, current);

        Assert.Equal(1, CountSystemMessages(lines));
        Assert.Contains(lines, line => line.Contains("changed prompt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedRestoredToolStateEmitsOneSystemMessage()
    {
        var persisted = SystemMessage("prompt", tool: null);
        var current = SystemMessage("prompt", tool: """{"name":"late_tool","description":"late","parameters":{"type":"object"}}""");
        var lines = await WriteModelRequestAsync(persisted, current);

        Assert.Equal(1, CountSystemMessages(lines));
        Assert.Contains(lines, line => line.Contains("late_tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnchangedRestoredStateEmitsNoSystemMessage()
    {
        var persisted = SystemMessage("prompt", tool: null);
        var current = SystemMessage("prompt", tool: null);
        var lines = await WriteModelRequestAsync(persisted, current);

        Assert.Equal(0, CountSystemMessages(lines));
    }

    private static async Task<string[]> WriteModelRequestAsync(JsonObject persisted, JsonObject current)
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var agent = new PiAgent(new IdleClient(), new CodingTools(Path.GetTempPath()), noBuiltinTools: true);
        var run = await ConversationRun.OpenAsync(agent, session);
        using var output = new LockedWriter();
        var transcript = new ToolDeclarationTranscript();
        transcript.Restore(JsonSerializer.SerializeToElement(new[]
        {
            new { afterMessageCount = 0,
                message = JsonSerializer.SerializeToElement(
                    TranscriptMessage(persisted), AIJsonUtilities.DefaultOptions) }
        }));
        var restored = transcript.RestoredSystemRevisions;
        // The run applies its own transcript first, exactly as the provider thread can before the
        // event consumer runs: the baseline handed to the writer must still be the restored one.
        var appliedPreamble = current["sections"]!["preamble"]!.GetValue<string>();
        transcript.Project([new ChatMessage(ChatRole.User, "resume")],
            new ChatOptions { Instructions = appliedPreamble });
        Assert.NotEmpty(transcript.SystemRevisions);

        var writer = new RpcEventWriter(new JsonLineWriter(output),
            () => current.DeepClone().AsObject(),
            () => RpcSystemMessageProjector.ProjectPersisted(restored));
        await writer.RunEventsAsync(run, Events(new AgentLifecycleEvent("model_request_started")));
        return output.Lines();
    }

    private static JsonObject SystemMessage(string text, string? tool)
    {
        var message = new JsonObject
        {
            ["role"] = "system",
            ["content"] = "",
            ["sections"] = new JsonObject { ["preamble"] = text },
            ["timestamp"] = 1
        };
        if (tool is not null) message["toolsAdded"] = new JsonArray(JsonNode.Parse(tool));
        return message;
    }

    private static ChatMessage TranscriptMessage(JsonObject systemMessage)
    {
        var message = new ChatMessage(ChatRole.System, "");
        SystemMessageTranscript.ImportMetadata(message, JsonSerializer.SerializeToElement(new
        {
            sections = new Dictionary<string, string>
            {
                ["preamble"] = systemMessage["sections"]!["preamble"]!.GetValue<string>()
            }
        }));
        return message;
    }

    private static int CountSystemMessages(IEnumerable<string> lines) => lines.Count(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("type", out var type) &&
            type.GetString() == "message_start" &&
            document.RootElement.TryGetProperty("message", out var message) &&
            message.TryGetProperty("role", out var role) && role.GetString() == "system";
    });

    private static async IAsyncEnumerable<AgentLifecycleEvent> Events(params AgentLifecycleEvent[] events)
    {
        foreach (var item in events) yield return item;
        await Task.CompletedTask;
    }

    private sealed class IdleClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "idle")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "idle");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
