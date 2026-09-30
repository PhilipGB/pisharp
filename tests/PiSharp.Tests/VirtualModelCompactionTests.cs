using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class VirtualModelCompactionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(50000)]
    [InlineData(100000)]
    [InlineData(1500)]
    public async Task ToolContinuationCompactsOnlyAfterSelectingItsSmallerPhysicalRoute(int previousWindow)
    {
        var session = new ConversationSession(Path.GetTempPath(), "auto", null, "router");
        session.SelectModel("auto", null, "router");
        session.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 18000)));
        session.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        var reasons = new List<string>();
        var summaryRequests = 0;
        var large = new Client("", contents: [new FunctionCallContent("call", "fixture_tool", new Dictionary<string, object?>())]);
        var small = new Client("small answer", request =>
        {
            Assert.Contains(session.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.True(AutoCompactionPolicy.Estimate(request, "") <= 8000);
            var calls = request.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).ToArray();
            var results = request.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId).ToArray();
            Assert.Equal(calls.Order(), results.Order());
            Assert.DoesNotContain(request, message => message.Text.Contains(new string('x', 1000), StringComparison.Ordinal));
        });
        var summary = new Client("tool result summary");
        var tool = AIFunctionFactory.Create(() => new string('y', 12000), name: "fixture_tool");
        var agent = new PiAgent(large, new CodingTools(Path.GetTempPath()), noTools: true,
            selectedTools: ["fixture_tool"], extensionTools: [tool], virtualModelRequestRouter: request =>
            {
                reasons.Add(request.Reason);
                if (request.Reason == "continuation")
                {
                    Assert.Equal(new[] { "user", "continuation" }, reasons);
                    Assert.Contains(request.Messages.SelectMany(message => message.Contents), content => content is FunctionResultContent);
                }
                var direct = request.Reason == "direct";
                var first = request.Reason == "user";
                return Task.FromResult(new VirtualModelRequestRoute(direct ? summary : first ? large : small,
                    new ModelDescriptor(direct ? (++summaryRequests == 1 ? "history-summary" : "turn-summary") : first ? "large" : "small", null, first ? 10000 : 8000,
                        null, Provider: "physical", Api: "test-api"), "physical", "off", null,
                    direct && summaryRequests == 2 ? new ModelPricing(Input: 7m, Output: 20m) :
                        new ModelPricing(Input: 2m, Output: 10m),
                    new AutoCompactionPolicy(first ? 10000 : 8000, ReserveTokens: 0, KeepRecentTokens: 1)));
            });
        var run = await ConversationRun.OpenAsync(agent, session, autoCompaction: previousWindow == 0 ? null :
            new AutoCompactionPolicy(previousWindow, ReserveTokens: 0, KeepRecentTokens: 1));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("read the large file")) events.Add(item);
        Assert.True(!events.Any(item => item.Type == "turn_failed"), string.Join("\n", events.Where(item => item.Type == "turn_failed").Select(item => item.Error)));
        Assert.Equal(new[] { "user", "continuation", "direct", "direct" }, reasons);
        Assert.Equal(1, large.Calls);
        Assert.Equal(1, small.Calls);
        Assert.Equal(2, summary.Calls);
        Assert.Single(session.Tree.ActivePath(), node => node.Type == "compaction");
        Assert.DoesNotContain(events, item => item.Type == "context_compacted_in_flight");
        Assert.Single(session.ActiveMessages(), message => message.Role == ChatRole.User && message.Text == "read the large file");
        Assert.Single(session.ActiveMessages(), message => message.Role == ChatRole.Assistant && message.Text == "small answer");
        Assert.Equal("auto", session.Model);
        Assert.Equal("small", session.ActiveMessages().Last().AdditionalProperties!["pisharp.model"]!.ToString());
        Assert.Equal(new[] { "large", "history-summary", "turn-summary", "small" }, session.ActiveUsage().Select(usage => usage.Model));
        Assert.Equal(new[] { 0.00004m, 0.00004m, 0.00011m, 0.00004m }, session.ActiveUsage().Select(usage => usage.Cost));
        Assert.Equal(0.00023m, session.ActiveUsage().Sum(usage => usage.Cost));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50000)]
    public async Task OverflowRoutesRetryBeforeCompactingWithItsPhysicalPolicy(int previousWindow)
    {
        var session = new ConversationSession(Path.GetTempPath(), "auto", null, "router");
        session.SelectModel("auto", null, "router");
        session.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 12000)));
        session.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        var reasons = new List<string>();
        var failing = new Client("", failure: new HttpRequestException("context_length_exceeded", null, System.Net.HttpStatusCode.BadRequest));
        var successful = new Client("retry answer", request =>
        {
            Assert.Contains(session.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.DoesNotContain(request, message => message.Text.Contains(new string('x', 1000), StringComparison.Ordinal));
        });
        var summary = new Client("history summary");
        var agent = new PiAgent(failing, new CodingTools(Path.GetTempPath()), noTools: true,
            virtualModelRequestRouter: request =>
            {
                reasons.Add(request.Reason);
                if (request.Reason == "retry")
                {
                    Assert.Equal(new[] { "user", "retry" }, reasons);
                    Assert.Equal("large", request.Failed!.Model.Id);
                }
                var direct = request.Reason == "direct";
                var first = request.Reason == "user";
                return Task.FromResult(new VirtualModelRequestRoute(direct ? summary : first ? failing : successful,
                    new ModelDescriptor(direct ? "summary-model" : first ? "large" : "retry-model", null, 50000,
                        null, Provider: "physical", Api: "test-api"), "physical", "off", null,
                    new ModelPricing(Input: 2m, Output: 10m),
                    new AutoCompactionPolicy(50000, ReserveTokens: 0, KeepRecentTokens: 1)));
            });
        var run = await ConversationRun.OpenAsync(agent, session, autoCompaction: previousWindow == 0 ? null :
            new AutoCompactionPolicy(previousWindow, ReserveTokens: 0, KeepRecentTokens: 1));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("next")) events.Add(item);
        Assert.True(!events.Any(item => item.Type == "turn_failed"), string.Join("\n", events.Where(item => item.Type == "turn_failed").Select(item => item.Error)));
        Assert.Equal(new[] { "user", "retry", "direct" }, reasons);
        Assert.Single(events, item => item.Type == "compaction_start" && item.CompactionReason == "overflow");
        Assert.Equal("auto", session.Model);
        Assert.Equal(new[] { "summary-model", "retry-model" }, session.ActiveUsage().Select(usage => usage.Model));
        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, successful.Calls);
        Assert.Equal(1, summary.Calls);
    }

    [Fact]
    public async Task FirstRequestWithUnknownLogicalLimitsCompactsForPhysicalRouteWithoutRerouting()
    {
        var session = new ConversationSession(Path.GetTempPath(), "auto", null, "router");
        session.SelectModel("auto", null, "router");
        session.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 18000)));
        session.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        session.AppendVirtualModelState("router", "auto", JsonSerializer.SerializeToElement(1));
        var reasons = new List<string>();
        var inference = new Client("physical answer", request =>
        {
            Assert.Contains(session.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.DoesNotContain(request, message => message.Text.Contains(new string('x', 1000), StringComparison.Ordinal));
            Assert.True(AutoCompactionPolicy.Estimate(request, "") <= 5000);
        });
        var summarizer = new Client("summary");
        var agent = new PiAgent(inference, new CodingTools(Path.GetTempPath()), noTools: true,
            virtualModelRequestRouter: request =>
            {
                reasons.Add(request.Reason);
                return Task.FromResult(new VirtualModelRequestRoute(request.Reason == "direct" ? summarizer : inference,
                    new ModelDescriptor("small", null, 5000, null, Provider: "physical", Api: "test-api"),
                    "physical", "off", null, new ModelPricing(Input: 2m, Output: 10m),
                    new AutoCompactionPolicy(5000, ReserveTokens: 0, KeepRecentTokens: 1),
                    JsonSerializer.SerializeToElement(2), "router", "auto"));
            });
        var run = await ConversationRun.OpenAsync(agent, session);
        await foreach (var _ in run.RunEventsAsync("next")) { }
        Assert.Equal(new[] { "user", "direct" }, reasons);
        Assert.Equal(1, inference.Calls);
        Assert.Equal(1, summarizer.Calls);
        Assert.Equal("auto", session.Model);
        Assert.Equal(2, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        Assert.Single(session.Tree.ActivePath(), node => node.Type == "compaction");
        Assert.Equal("physical answer", session.ActiveMessages().Last().Text);
        Assert.Equal("physical answer", session.ContextMessages().Last().Text);
        Assert.All(session.ActiveUsage(), usage => Assert.Equal("small", usage.Model));
        Assert.All(session.ActiveUsage(), usage => Assert.Equal(0.00004m, usage.Cost));
        Assert.Single(session.ActiveUsage(), usage => usage.Source == "compaction");
        var resumed = ConversationSession.Parse(session.ToJson());
        Assert.Equal("auto", resumed.Model);
        Assert.Equal(2, resumed.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        Assert.DoesNotContain(resumed.ContextMessages(), message => message.Text.Contains(new string('x', 1000), StringComparison.Ordinal));
    }

    [Fact]
    public async Task SmallerRouteOnLaterTurnCompactsAndSubsequentTurnUsesCompactedHistory()
    {
        var session = new ConversationSession(Path.GetTempPath(), "auto", null, "router");
        session.SelectModel("auto", null, "router");
        var reasons = new List<string>();
        var userRequests = 0;
        var large = new Client(new string('x', 18000));
        var small = new Client("small answer", request =>
        {
            Assert.Contains(session.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.DoesNotContain(request, message => message.Text.Contains(new string('x', 1000), StringComparison.Ordinal));
            Assert.True(AutoCompactionPolicy.Estimate(request, "") <= 5000);
        });
        var summary = new Client("history summary");
        var agent = new PiAgent(large, new CodingTools(Path.GetTempPath()), noTools: true,
            virtualModelRequestRouter: request =>
            {
                reasons.Add(request.Reason);
                var direct = request.Reason == "direct";
                var first = !direct && ++userRequests == 1;
                return Task.FromResult(new VirtualModelRequestRoute(direct ? summary : first ? large : small,
                    new ModelDescriptor(direct ? "summary-model" : first ? "large" : "small", null, first ? 50000 : 5000,
                        null, Provider: "physical", Api: "test-api"), "physical", "off", null,
                    new ModelPricing(Input: 2m, Output: 10m),
                    new AutoCompactionPolicy(first ? 50000 : 5000, ReserveTokens: 0, KeepRecentTokens: 1),
                    direct ? null : JsonSerializer.SerializeToElement(userRequests), "router", "auto"));
            });
        var run = await ConversationRun.OpenAsync(agent, session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("first")) events.Add(item);
        Assert.Empty(events.Where(item => item.Type == "turn_failed").Select(item => "first: " + item.Error));
        await foreach (var item in run.RunEventsAsync("second")) events.Add(item);
        Assert.Empty(events.Where(item => item.Type == "turn_failed").Select(item => "second: " + item.Error));
        await foreach (var item in run.RunEventsAsync("third")) events.Add(item);
        Assert.Empty(events.Where(item => item.Type == "turn_failed").Select(item => "third: " + item.Error));
        Assert.Equal(new[] { "user", "user", "direct", "user" }, reasons);
        Assert.Equal(1, large.Calls);
        Assert.Equal(2, small.Calls);
        Assert.Equal(1, summary.Calls);
        Assert.Single(events, item => item.Type == "compaction_end" && item.CompactionResult is not null);
        Assert.DoesNotContain(events, item => item.Type == "turn_failed");
        Assert.Equal(new[] { "large", "summary-model", "small", "small" }, session.ActiveUsage().Select(usage => usage.Model));
        Assert.All(session.ActiveUsage(), usage => Assert.Equal(0.00004m, usage.Cost));
        Assert.Equal(3, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        Assert.Equal("auto", session.Model);
        Assert.Equal(6, session.ActiveMessages().Count);
        Assert.DoesNotContain(session.ContextMessages(), message => message.Text.Contains(new string('x', 1000), StringComparison.Ordinal));
    }

    [Fact]
    public async Task BranchSelectionRestoresSuccessfulPhysicalPolicyInsteadOfLastDispatchedBranch()
    {
        var session = new ConversationSession(Path.GetTempPath(), "auto", null, "router");
        session.SelectModel("auto", null, "router");
        var root = session.Tree.HeadId;
        var large = new Client("large answer", inputTokens: 9000);
        var small = new Client("small answer");
        var reasons = new List<string>();
        var agent = new PiAgent(large, new CodingTools(Path.GetTempPath()), noTools: true,
            virtualModelRequestRouter: request =>
            {
                reasons.Add(request.Reason);
                var isSmall = request.Messages.Last().Text == "small branch";
                return Task.FromResult(new VirtualModelRequestRoute(isSmall ? small : large,
                    new ModelDescriptor(isSmall ? "small" : "large", null, isSmall ? 5000 : 50000, null,
                        Provider: "physical", Api: "test-api"), "physical", "off", null, null,
                    new AutoCompactionPolicy(isSmall ? 5000 : 50000, ReserveTokens: 0, KeepRecentTokens: 1),
                    JsonSerializer.SerializeToElement(isSmall ? 2 : 1)));
            });
        VirtualModelPhysicalContextResolver resolver = (messages, _) =>
        {
            var previous = messages.LastOrDefault(message => message.Role == ChatRole.Assistant);
            var model = previous is null ? null : ChatMessageProperties.String(previous.AdditionalProperties!, "pisharp.model");
            return Task.FromResult(model is null ? null : new VirtualModelPhysicalContext(
                new ModelDescriptor(model, null, model == "small" ? 5000 : 50000, null),
                new AutoCompactionPolicy(model == "small" ? 5000 : 50000, ReserveTokens: 0, KeepRecentTokens: 1)));
        };
        var run = await ConversationRun.OpenAsync(agent, session, physicalContextResolver: resolver);
        await foreach (var _ in run.RunEventsAsync("large branch")) { }
        var largeHead = session.Tree.HeadId;
        await run.SelectAsync(root);
        await foreach (var _ in run.RunEventsAsync("small branch")) { }
        Assert.Equal(2, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        await run.SelectAsync(largeHead);
        Assert.Equal(1, session.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("continue large branch")) events.Add(item);
        Assert.DoesNotContain(events, item => item.Type == "turn_failed");
        Assert.Equal(new[] { "user", "user", "user" }, reasons);
        Assert.Equal(2, large.Calls);
        Assert.Equal(1, small.Calls);
        Assert.DoesNotContain(session.Tree.ActivePath(), node => node.Type == "compaction");
        foreach (var copy in new[] { ConversationSession.Parse(session.ToJson()), session.Fork(),
            session.ForkInto(Path.GetTempPath()), session.Snapshot() })
        {
            var resumed = await ConversationRun.OpenAsync(agent, copy,
                autoCompaction: new AutoCompactionPolicy(5000, ReserveTokens: 0, KeepRecentTokens: 1),
                physicalContextResolver: resolver);
            await foreach (var item in resumed.RunEventsAsync("continue restored large branch"))
                Assert.NotEqual("turn_failed", item.Type);
            Assert.DoesNotContain(copy.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.Equal(1, copy.ActiveVirtualModelState("router", "auto")!.Value.GetInt32());
            Assert.Equal("auto", copy.Model);
        }
        Assert.DoesNotContain("direct", reasons);
    }

    [Fact]
    public void CanonicalHistoryRemovesOnlyRecognizedFrameworkAttributionAndPreservesOwnedMetadata()
    {
        var attribution = new AgentRequestMessageSourceAttribution(new AgentRequestMessageSourceType("ChatHistory"),
            typeof(InMemoryChatHistoryProvider).FullName!);
        foreach (var value in new object[] { attribution, JsonSerializer.SerializeToElement(attribution) })
        {
            var message = new ChatMessage(ChatRole.Assistant, "answer")
            {
                AdditionalProperties = new()
                {
                    [AgentRequestMessageSourceAttribution.AdditionalPropertiesKey] = value,
                    ["pisharp.model"] = "physical",
                    ["extension.marker"] = "keep"
                }
            };
            var projected = ChatMessageProperties.WithoutRequestAttribution(message);
            Assert.False(projected.AdditionalProperties!.ContainsKey(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey));
            Assert.Equal("physical", projected.AdditionalProperties["pisharp.model"]);
            Assert.Equal("keep", projected.AdditionalProperties["extension.marker"]);
            Assert.True(message.AdditionalProperties.ContainsKey(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey));
        }
        var unrelated = new ChatMessage(ChatRole.Assistant, "answer")
        { AdditionalProperties = new() { [AgentRequestMessageSourceAttribution.AdditionalPropertiesKey] = "extension value" } };
        Assert.Same(unrelated, ChatMessageProperties.WithoutRequestAttribution(unrelated));
    }

    private sealed class Client(string answer, Action<IReadOnlyList<ChatMessage>>? inspect = null, long inputTokens = 10, IList<AIContent>? contents = null, Exception? failure = null) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (failure is not null) throw failure;
            inspect?.Invoke(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents ?? [new TextContent(answer)]))
            { Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = 2, TotalTokenCount = inputTokens + 2 } });
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
