using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolDeclarationHistoryTests
{
    [Fact]
    public async Task CompactionStartsANewToolPrefixAndProviderReplacementRetainsIt()
    {
        var client = new RecordingClient();
        var registration = new ExtensionRegistration();
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "base", name: "base_tool")));
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var agent = CreateAgent(client, registration);
        var run = await ConversationRun.OpenAsync(agent, conversation);
        await foreach (var _ in run.RunEventsAsync("before")) { }
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "late", name: "late_tool")));
        await foreach (var _ in run.RunEventsAsync("after")) { }
        var plan = Assert.IsType<ConversationSession.CompactionPlan>(conversation.PrepareCompaction(0));
        conversation.AppendCompaction(plan, "summary", 0);
        var restored = await ConversationRun.OpenAsync(agent, conversation);
        await foreach (var _ in restored.RunEventsAsync("compacted")) { }
        var transcript = Assert.IsType<JsonElement>(client.Options[^1].AdditionalProperties?["pisharp.toolTranscript"]);
        var initial = Assert.Single(transcript.EnumerateArray()).GetProperty("message").Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!;
        Assert.Equal(["base_tool", "late_tool"], SystemMessageTranscript.AddedTools(initial).Select(tool => tool.GetProperty("name").GetString()));
        var replacement = new RecordingClient();
        agent.SetModelRuntime(replacement, supportsImages: true, imageResizeOptions: null);
        await foreach (var _ in restored.RunEventsAsync("switched")) { }
        Assert.True(JsonElement.DeepEquals(transcript,
            Assert.IsType<JsonElement>(replacement.Options[0].AdditionalProperties?["pisharp.toolTranscript"])));
    }

    [Fact]
    public async Task RuntimeRetainsInitialToolDefinitionsAcrossChangesAndResume()
    {
        var client = new RecordingClient();
        var registration = new ExtensionRegistration();
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "base", name: "base_tool", description: "original")));
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var agent = CreateAgent(client, registration);
        var run = await ConversationRun.OpenAsync(agent, conversation);
        await foreach (var _ in run.RunEventsAsync("before")) { }
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "late", name: "late_tool", description: "later")));
        await foreach (var _ in run.RunEventsAsync("after")) { }
        var transcript = Assert.IsType<JsonElement>(client.Options[^1].AdditionalProperties?["pisharp.toolTranscript"]);
        var projected = transcript.EnumerateArray().Select(revision => revision.GetProperty("message")
            .Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!).ToArray();
        Assert.Equal("base_tool", Assert.Single(SystemMessageTranscript.AddedTools(projected[0])).GetProperty("name").GetString());
        var update = Assert.Single(projected.Skip(1), message => message.Role == ChatRole.System);
        Assert.Equal("late_tool", Assert.Single(SystemMessageTranscript.AddedTools(update)).GetProperty("name").GetString());
        var resumed = ConversationSession.Parse(conversation.ToJson());
        var restoredClient = new RecordingClient();
        var restored = await ConversationRun.OpenAsync(CreateAgent(restoredClient, registration), resumed);
        await foreach (var _ in restored.RunEventsAsync("resume")) { }
        var replay = Assert.IsType<JsonElement>(restoredClient.Options[0].AdditionalProperties?["pisharp.toolTranscript"])
            .EnumerateArray().Select(revision => revision.GetProperty("message").Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!).ToArray();
        Assert.Equal("base_tool", Assert.Single(SystemMessageTranscript.AddedTools(replay[0])).GetProperty("name").GetString());
        Assert.Single(replay.Skip(1), message => message.Role == ChatRole.System);
        Assert.Equal(2, restoredClient.Options[0].Tools!.Count);
    }

    private static PiAgent CreateAgent(IChatClient client, ExtensionRegistration registration) =>
        new(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true, systemPrompt: "base prompt",
            extensionToolRegistrations: registration.ToolDefinitions, liveExtensionRegistration: registration);

    private sealed class RecordingClient : IChatClient
    {
        internal List<ChatOptions> Options { get; } = [];
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options.Add(options!.Clone());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
