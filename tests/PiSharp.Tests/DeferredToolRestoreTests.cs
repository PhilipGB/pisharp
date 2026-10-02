using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class DeferredToolRestoreTests
{
    [Fact]
    public async Task ResumedToolBecomesDeclaredWhenItsServerRegisters()
    {
        var registration = new ExtensionRegistration();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.AppendToolLoadout(["mcp__docs__search"]);
        var client = new ToolRecordingClient();
        var agent = Agent(registration, client);
        var run = await ConversationRun.OpenAsync(agent, conversation);
        Assert.Empty(run.ActiveToolNames);

        registration.AddTool(Search());

        Assert.Equal(["mcp__docs__search"], run.ActiveToolNames);
        await foreach (var _ in run.RunEventsAsync("use it")) { }
        Assert.Equal(["mcp__docs__search"], Assert.Single(client.RequestTools));
    }

    [Fact]
    public void ReloadedRegistryPreservesPreviouslyLoadedDeferredTools()
    {
        var registry = new PiSharpToolRegistry([Search()]);
        var loadout = registry.CreateLoadout(["mcp__docs__search"]);

        var saved = loadout.Snapshot.ActiveToolNames;
        registry.Replace([]);
        loadout.RestoreActiveTools(saved);
        Assert.Empty(loadout.Snapshot.ActiveToolNames);
        registry.Replace([Search()]);

        Assert.Equal(["mcp__docs__search"], loadout.Snapshot.ActiveToolNames);
    }

    [Fact]
    public async Task ReloadedRunCarriesPriorLoadoutAcrossDelayedRegistration()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var firstRegistration = new ExtensionRegistration();
        firstRegistration.AddTool(Search());
        var first = await ConversationRun.OpenAsync(Agent(firstRegistration, new ToolRecordingClient()),
            conversation, activeToolNamesOverride: ["mcp__docs__search"]);
        var nextRegistration = new ExtensionRegistration();
        var next = await ConversationRun.OpenAsync(Agent(nextRegistration, new ToolRecordingClient()),
            conversation, activeToolNamesOverride: first.ActiveToolNames);

        nextRegistration.AddTool(Search());

        Assert.Equal(["mcp__docs__search"], next.ActiveToolNames);
    }

    [Fact]
    public async Task PromptBoundaryDropsUnregisteredRestoredTools()
    {
        var registration = new ExtensionRegistration();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.AppendToolLoadout(["mcp__docs__search"]);
        var client = new ToolRecordingClient();
        var run = await ConversationRun.OpenAsync(Agent(registration, client), conversation);

        await foreach (var _ in run.RunEventsAsync("go")) { }
        registration.AddTool(Search());
        await foreach (var _ in run.RunEventsAsync("again")) { }

        Assert.Empty(run.ActiveToolNames);
        Assert.All(client.RequestTools, Assert.Empty);
        Assert.Empty(conversation.ActiveToolLoadout()!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAdditiveSelectionsKeepPendingRestoredTools(bool additive)
    {
        var baseline = new PiSharpToolRegistration(AIFunctionFactory.Create(() => "base", name: "base"));
        var added = new PiSharpToolRegistration(AIFunctionFactory.Create(() => "added", name: "added"),
            DefaultActive: false);
        var registry = new PiSharpToolRegistry([baseline, added]);
        var loadout = registry.CreateLoadout();
        loadout.RestoreActiveTools(["base", "mcp__docs__search"]);

        loadout.SetActiveTools(additive ? ["base", "added"] : ["added"]);
        registry.Replace([baseline, added, Search()]);

        Assert.Equal(additive ? ["base", "added", "mcp__docs__search"] : ["added"],
            loadout.Snapshot.ActiveToolNames);
    }

    [Fact]
    public async Task AdditiveSelectionPreservesTranscriptDeclarationOrder()
    {
        var registration = new ExtensionRegistration();
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "base", name: "base")));
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "added", name: "added"), DefaultActive: false));
        var client = new ToolRecordingClient();
        var agent = Agent(registration, client);
        var session = await agent.CreateSessionAsync();
        agent.RestoreToolLoadout(session, ["base", "mcp__docs__search"]);
        agent.GetToolLoadout(session).SetActiveTools(["base", "added"]);
        registration.AddTool(Search());

        await foreach (var _ in agent.RunStreamingAsync("go", session)) { }

        Assert.Equal(["base", "mcp__docs__search", "added"], Assert.Single(client.RequestTools));
    }

    [Theory]
    [InlineData("excluded")]
    [InlineData("allowed")]
    [InlineData("none")]
    public async Task RestoreCannotBypassToolRestrictions(string restriction)
    {
        var registration = new ExtensionRegistration();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.AppendToolLoadout(["mcp__docs__search"]);
        var agent = new PiAgent(new ToolRecordingClient(), new CodingTools(Path.GetTempPath()),
            noBuiltinTools: true, liveExtensionRegistration: registration,
            selectedTools: restriction == "allowed" ? [] : null,
            excludedTools: restriction == "excluded" ? ["mcp__docs__search"] : null,
            noTools: restriction == "none");
        var run = await ConversationRun.OpenAsync(agent, conversation);

        registration.AddTool(Search());

        Assert.Empty(run.ActiveToolNames);
    }

    [Fact]
    public async Task CancellationDoesNotRestoreToolsDroppedAtThePromptBoundary()
    {
        var registration = new ExtensionRegistration();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.AppendToolLoadout(["mcp__docs__search"]);
        using var cancellation = new CancellationTokenSource();
        var client = new ToolRecordingClient { OnRequest = cancellation.Cancel };
        var run = await ConversationRun.OpenAsync(Agent(registration, client), conversation);

        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("go", cancellation.Token)) events.Add(item);
        Assert.Contains(events, item => item.Type == "turn_interrupted");
        registration.AddTool(Search());
        client.OnRequest = null;
        await foreach (var _ in run.RunEventsAsync("again")) { }

        Assert.Empty(run.ActiveToolNames);
        Assert.All(client.RequestTools, Assert.Empty);
    }

    private static PiSharpToolRegistration Search() => new(
        AIFunctionFactory.Create(() => "found", name: "mcp__docs__search"), ToolExposure.Deferred);

    private static PiAgent Agent(ExtensionRegistration registration, IChatClient client) => new(
        client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
        extensionToolRegistrations: registration.ToolDefinitions, liveExtensionRegistration: registration);

    private sealed class ToolRecordingClient : IChatClient
    {
        public List<string[]> RequestTools { get; } = [];
        public Action? OnRequest { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestTools.Add(options?.Tools?.OfType<AIFunction>().Select(function => function.Name).ToArray() ?? []);
            OnRequest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
