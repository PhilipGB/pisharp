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

    private sealed class Client(string answer, Action<IReadOnlyList<ChatMessage>>? inspect = null) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            inspect?.Invoke(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
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
