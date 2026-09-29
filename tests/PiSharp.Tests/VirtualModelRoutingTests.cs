using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class VirtualModelRoutingTests
{
    private static ConversationSession Session() => new(Path.GetTempPath(), "auto", null, provider: "router");
    private static VirtualModelRequestRoute Route(IChatClient client, string id = "physical", JsonElement? state = null) =>
        new(client, new ModelDescriptor(id, null, 32000, null, Provider: "provider", Input: ["text"], Api: "test-api"),
            "provider", "high", new ReasoningOptions { Effort = ReasoningEffort.High },
            new ModelPricing(Input: 2m, Output: 10m), null, state, "router", "auto");

    [Theory]
    [InlineData("user")]
    [InlineData("continuation")]
    [InlineData("retry")]
    [InlineData("direct")]
    public async Task RoutesEachRequestWithoutChangingLogicalSelection(string reason)
    {
        var session = Session();
        var physical = new RecordingClient();
        var events = new List<AgentLifecycleEvent>();
        var routed = new RoutedChatClient(new MutableChatClient(new RecordingClient()), events.Add);
        routed.SetSession(session, "low", null);
        VirtualModelRequestContext? seen = null;
        var route = Route(physical);
        routed.SetRouter(request => { seen = request; return Task.FromResult(route); });
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };
        var options = VirtualModelRequestHints.WithHint(new ChatOptions { ModelId = "auto" }, new(reason, "low"));
        var response = await routed.GetResponseAsync(messages, options);
        Assert.Equal(reason, seen!.Reason);
        Assert.Equal("low", seen.ThinkingLevel);
        Assert.Same(session, seen.Session);
        Assert.Same(messages[0], seen.Messages[0]);
        Assert.Equal("auto", session.Model);
        Assert.Equal("router", session.Provider);
        Assert.Equal("physical", physical.Options!.ModelId);
        Assert.Equal(ReasoningEffort.High, physical.Options.Reasoning!.Effort);
        Assert.False(physical.Options.AdditionalProperties!.ContainsKey(VirtualModelRequestHints.PropertyName));
        Assert.Equal("auto", options.ModelId);
        Assert.Equal("physical", response.ModelId);
        var assistant = Assert.Single(response.Messages);
        Assert.Equal("provider", assistant.AdditionalProperties!["pisharp.provider"]);
        Assert.Equal("test-api", assistant.AdditionalProperties["pisharp.api"]);
        Assert.Equal("high", assistant.AdditionalProperties["pisharp.thinkingLevel"]);
        if (reason == "direct") Assert.Empty(events);
        else Assert.Same(route.Pricing, Assert.Single(events).ProviderPricing);
    }

    [Fact]
    public async Task PhysicalImageFilteringPreservesCanonicalMessages()
    {
        var physical = new RecordingClient();
        var routed = new RoutedChatClient(new MutableChatClient(physical), _ => { });
        routed.SetSession(Session(), "off", null);
        routed.SetRouter(_ => Task.FromResult(Route(physical)));
        var message = new ChatMessage(ChatRole.User, [new TextContent("look"), new DataContent(new byte[] { 1 }, "image/png")]);
        await routed.GetResponseAsync([message]);
        Assert.Empty(Assert.Single(physical.Messages!).Contents.OfType<DataContent>());
        Assert.Single(message.Contents.OfType<DataContent>());
        Assert.Contains("does not support images", Assert.Single(physical.Messages!).Text);
    }

    [Fact]
    public async Task StateIsSavedBeforeProviderFailureAndDirectStateIsIgnored()
    {
        var session = Session();
        var physical = new RecordingClient { Failure = new IOException("provider failed") };
        var routed = new RoutedChatClient(new MutableChatClient(physical), _ => { });
        var saved = 0;
        routed.SetSession(session, "off", _ => { saved++; return Task.CompletedTask; });
        routed.SetRouter(_ => Task.FromResult(Route(physical, state: JsonSerializer.SerializeToElement(new { phase = 1 }))));
        await Assert.ThrowsAsync<IOException>(() => routed.GetResponseAsync([new(ChatRole.User, "hello")],
            VirtualModelRequestHints.WithHint(null, new("user", "off"))));
        Assert.Equal(1, saved);
        Assert.Equal(1, session.ActiveVirtualModelState("router", "auto")!.Value.GetProperty("phase").GetInt32());
        routed.SetRouter(_ => Task.FromResult(Route(physical, state: JsonSerializer.SerializeToElement(new { phase = 2 }))));
        await Assert.ThrowsAsync<IOException>(() => routed.GetResponseAsync([new(ChatRole.User, "summary")]));
        Assert.Equal(1, saved);
        Assert.Equal(1, session.ActiveVirtualModelState("router", "auto")!.Value.GetProperty("phase").GetInt32());
    }

    [Fact]
    public async Task CancellationDuringRoutingNeverCallsPhysicalProvider()
    {
        var physical = new RecordingClient();
        var routed = new RoutedChatClient(new MutableChatClient(physical), _ => { });
        routed.SetSession(Session(), "off", null);
        using var cancellation = new CancellationTokenSource();
        routed.SetRouter(async request =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, request.CancellationToken);
            return Route(physical);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => routed.GetResponseAsync([], cancellationToken: cancellation.Token));
        Assert.Equal(0, physical.Calls);
        Assert.Null(routed.CurrentRoute);
    }

    [Fact]
    public void StateFollowsBranchesAndSurvivesNativeAndPiPersistenceAndFork()
    {
        var session = Session();
        session.AppendVirtualModelState("router", "auto", JsonSerializer.SerializeToElement(1));
        var ancestor = session.Tree.HeadId;
        session.AppendVirtualModelState("router", "auto", JsonSerializer.SerializeToElement(2));
        var branch = session.Tree.HeadId;
        session.Tree.Select(ancestor);
        session.AppendVirtualModelState("router", "auto", JsonSerializer.SerializeToElement(3));
        Assert.Equal(3, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        session.Tree.Select(branch);
        Assert.Equal(2, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        foreach (var copy in new[] { session.Fork(), session.ForkInto(Path.GetTempPath()),
            session.Snapshot(), ConversationSession.Parse(session.ToJson()),
            PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session)) })
        {
            Assert.Equal(2, copy.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
            copy.AppendVirtualModelState("router", "auto", JsonSerializer.SerializeToElement(4));
            Assert.Equal(2, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        }
        Assert.Null(session.ActiveVirtualModelState("other", "auto"));
        Assert.Null(Session().ActiveVirtualModelState("router", "auto"));
    }

    [Theory]
    [InlineData("minimal", "low")]
    [InlineData("max", "xhigh")]
    public async Task ProviderLoopPreservesExactSelectedVirtualThinkingLevel(string selected, string mapped)
    {
        var session = Session();
        var physical = new RecordingClient();
        var requests = new List<VirtualModelRequestContext>();
        var agent = new PiAgent(physical, new CodingTools(Path.GetTempPath()), noTools: true,
            reasoning: PiSharp.Cli.ThinkingLevels.ToOptions(mapped), virtualModelRequestRouter: request =>
            {
                requests.Add(request);
                return Task.FromResult(Route(physical));
            });
        var run = await ConversationRun.OpenAsync(agent, session, reasoningLevel: selected);
        await foreach (var _ in run.RunEventsAsync("hello")) { }
        Assert.Equal(selected, Assert.Single(requests).ThinkingLevel);
        Assert.Equal("high", session.ActiveMessages().Last().AdditionalProperties!["pisharp.thinkingLevel"]!.ToString());
    }

    [Fact]
    public async Task ProviderLoopRetriesUsingFailedPhysicalRequestThenChangesRouteOnNextTurn()
    {
        var session = Session();
        var failing = new RecordingClient { Failure = new IOException("overloaded") };
        var successful = new RecordingClient();
        var requests = new List<VirtualModelRequestContext>();
        var agent = new PiAgent(successful, new CodingTools(Path.GetTempPath()), noTools: true,
            retryPolicy: new ProviderRetryPolicy(1), virtualModelRequestRouter: request =>
            {
                requests.Add(request);
                return Task.FromResult(Route(requests.Count == 1 ? failing : successful,
                    requests.Count == 1 ? "first" : "second"));
            });
        var run = await ConversationRun.OpenAsync(agent, session);
        await foreach (var _ in run.RunEventsAsync("hello")) { }
        Assert.Equal(new[] { "user", "retry" }, requests.Select(request => request.Reason));
        Assert.Equal("first", requests[1].Failed!.Model.Id);
        Assert.Equal("overloaded", requests[1].Failed!.Error);
        await foreach (var _ in run.RunEventsAsync("next")) { }
        Assert.Equal("user", requests[2].Reason);
        Assert.Equal("auto", session.Model);
        Assert.Equal("router", session.Provider);
        Assert.All(session.ActiveUsage(), usage => Assert.Equal("second", usage.Model));
        Assert.All(session.ActiveUsage(), usage => Assert.Equal(0.00004m, usage.Cost));
        Assert.Equal("second", session.ActiveMessages().Last().AdditionalProperties!["pisharp.model"]!.ToString());
    }

    [Fact]
    public async Task ConcurrentDirectRequestCannotReplaceProviderDuringStateSave()
    {
        var session = Session();
        var first = new RecordingClient();
        var second = new RecordingClient();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routed = new RoutedChatClient(new MutableChatClient(first), _ => { });
        routed.SetSession(session, "off", async _ => { saving.SetResult(); await release.Task; });
        routed.SetRouter(request => Task.FromResult(request.Reason == "direct" ? Route(second, "second") :
            Route(first, "first", JsonSerializer.SerializeToElement(1))));
        var pending = routed.GetResponseAsync([new(ChatRole.User, "turn")],
            VirtualModelRequestHints.WithHint(null, new("user", "off")));
        await saving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await routed.GetResponseAsync([new(ChatRole.User, "direct")]);
        }
        finally { release.SetResult(); }
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal("first", first.Options!.ModelId);
        Assert.Equal("first", response.ModelId);
    }

    [Fact]
    public async Task ToolContinuationRoutesAgainAndPersistsBothPhysicalIdentities()
    {
        var session = Session();
        session.SelectModel("auto", null, "router");
        var first = new RecordingClient { Contents = [new FunctionCallContent("call", "fixture_tool", new Dictionary<string, object?>())] };
        var second = new RecordingClient();
        var requests = new List<VirtualModelRequestContext>();
        var tool = AIFunctionFactory.Create(() => "tool result", name: "fixture_tool");
        var agent = new PiAgent(second, new CodingTools(Path.GetTempPath()), selectedTools: ["fixture_tool"],
            noTools: true, extensionTools: [tool], virtualModelRequestRouter: request =>
            {
                requests.Add(request);
                return Task.FromResult(Route(requests.Count == 1 ? first : second, requests.Count == 1 ? "first" : "second"));
            });
        var run = await ConversationRun.OpenAsync(agent, session);
        await foreach (var _ in run.RunEventsAsync("use tool")) { }
        Assert.Equal(new[] { "user", "continuation" }, requests.Select(request => request.Reason));
        Assert.Contains(requests[1].Messages, message => message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == "call"));
        var assistants = session.ActiveMessages().Where(message => message.Role == ChatRole.Assistant).ToArray();
        Assert.Equal(new[] { "first", "second" }, assistants.Select(message => message.AdditionalProperties!["pisharp.model"]!.ToString()));
        Assert.Equal(new[] { "first", "second" }, session.ActiveUsage().Select(usage => usage.Model));
        var registry = new PiSharp.Runtime.VirtualModels.VirtualModelRegistry();
        registry.Register(new("router", "auto", "Auto", (_, _) => Task.FromResult(new PiSharp.Runtime.VirtualModels.VirtualModelRoute("provider", "physical", "off"))), "test");
        var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session));
        Assert.True(PiJsonlSessionInterchange.RestoreRegisteredVirtualModelSelection(imported, registry));
        Assert.Equal("auto", imported.Model);
        Assert.Equal("router", imported.Provider);
        Assert.Equal("second", imported.ActiveMessages().Last().AdditionalProperties!["pisharp.model"]!.ToString());
    }

    private sealed class RecordingClient : IChatClient
    {
        public Exception? Failure { get; init; }
        public IList<AIContent>? Contents { get; init; }
        public int Calls { get; private set; }
        public ChatOptions? Options { get; private set; }
        public ChatMessage[]? Messages { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Messages = messages.ToArray();
            Options = options;
            if (Failure is { } failure) throw failure;
            return Task.FromResult(new ChatResponse(Contents is { } contents ? new ChatMessage(ChatRole.Assistant, contents) : new ChatMessage(ChatRole.Assistant, "answer"))
            { Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 } });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
