using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CompactionTests
{
    [Fact]
    public async Task ManualCompactionPreservesHistoryAndRestoresShortContextAfterRestartAndBranch()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var conversation = Seed(cwd);
            var previous = conversation.Tree.HeadId;
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var client = new SummaryClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                save: token => store.SaveAsync(conversation, path, token));
            Assert.True(await run.CompactAsync("prioritize files"));
            Assert.Contains("prioritize files", client.LastSummaryRequest);
            Assert.Equal(4, conversation.ActiveMessages().Count);
            Assert.Equal(3, conversation.ContextMessages().Count);
            Assert.Contains("Summary of earlier conversation", conversation.ContextMessages()[0].Text);
            Assert.Equal("second", conversation.ContextMessages()[1].Text);
            Assert.Null(conversation.PrepareCompaction());
            var reloaded = await store.LoadAsync(path);
            Assert.Equal(4, reloaded.ActiveMessages().Count);
            Assert.Equal(3, reloaded.ContextMessages().Count);
            var resumedClient = new SummaryClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedClient, new CodingTools(cwd)), reloaded);
            await foreach (var _ in resumed.RunStreamingAsync("third")) { }
            Assert.Equal(4, resumedClient.SeenMessages!.Count);
            Assert.DoesNotContain(resumedClient.SeenMessages, message => message.Text == "first");
            Assert.True(await resumed.CompactAsync());
            Assert.Contains("Summary of first turn", resumedClient.LastSummaryRequest);
            Assert.Contains("second", resumedClient.LastSummaryRequest);
            Assert.Equal(6, reloaded.ActiveMessages().Count);
            Assert.Equal(3, reloaded.ContextMessages().Count);
            await resumed.SelectAsync(previous);
            Assert.Equal(4, reloaded.ContextMessages().Count);
            Assert.Equal("first", reloaded.ContextMessages()[0].Text);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task CompactionUsageIsPersistedAndIncludedInSessionBilling()
    {
        var conversation = Seed(Path.GetTempPath());
        var client = new SummaryClient
        {
            SummaryUsage = new UsageDetails { InputTokenCount = 500, OutputTokenCount = 50, TotalTokenCount = 550 }
        };
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), conversation,
            pricing: new ModelPricing(Input: 2m, Output: 8m));

        Assert.True(await run.CompactAsync());

        var usage = Assert.Single(conversation.ActiveUsage());
        Assert.Equal("compaction", usage.Source);
        Assert.Equal(550, usage.TotalTokens);
        Assert.Equal(0.0014m, usage.Cost);
        var stats = SessionStatistics.Calculate(conversation);
        Assert.Equal(550, stats.BilledTokens);
        Assert.Equal(0.0014m, stats.Cost);
    }

    [Fact]
    public async Task AutoBudgetCompactsBeforeNextPromptAndPersistsRawHistory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-auto-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('a', 3000)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "earlier answer"));
            conversation.Append(new ChatMessage(ChatRole.User, "latest"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "latest answer"));
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var client = new SummaryClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                save: token => store.SaveAsync(conversation, path, token),
                autoCompaction: new AutoCompactionPolicy(1800, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("continue")) events.Add(item);
            Assert.Contains(events, item => item.Type == "context_compacted");
            Assert.True(events.FindIndex(item => item.Type == "context_compacted") < events.FindIndex(item => item.Type == "prompt_accepted"));
            Assert.Equal(6, conversation.ActiveMessages().Count);
            Assert.Equal("Summary of first turn", conversation.ContextMessages()[0].Text.Split('\n').Last());
            Assert.DoesNotContain(client.SeenMessages!, message => message.Text == new string('a', 3000));
            Assert.Equal(6, (await store.LoadAsync(path)).ActiveMessages().Count);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task AutoBudgetPrefersLatestProviderUsageAfterCurrentCompactionBoundary()
    {
        var conversation = Seed(Path.GetTempPath());
        conversation.AppendUsage(new UsageRecord("fixture", "model", 4300, 200, 0, 0, 4500, null));
        var client = new SummaryClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), conversation,
            autoCompaction: new AutoCompactionPolicy(5000, 1000));
        var events = new List<AgentLifecycleEvent>();

        await foreach (var item in run.RunEventsAsync("continue")) events.Add(item);

        Assert.Contains(events, item => item.Type == "context_compacted");
        Assert.DoesNotContain(client.SeenMessages!, message => message.Text == "first");
        Assert.Null(conversation.LatestContextUsageTokens());
    }

    [Fact]
    public async Task AutoBudgetFailureRollsBackWithoutSendingNewPrompt()
    {
        var conversation = Seed(Path.GetTempPath());
        conversation.Append(new ChatMessage(ChatRole.Assistant, new string('X', 3000)));
        var oldHead = conversation.Tree.HeadId;
        var client = new SummaryClient { FailSummary = true };
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), conversation,
            autoCompaction: new AutoCompactionPolicy(1800, 300));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("continue")) events.Add(item);
        Assert.Contains(events, item => item.Type == "prompt_rejected");
        Assert.DoesNotContain(events, item => item.Type == "prompt_accepted");
        Assert.Null(client.SeenMessages);
        Assert.Equal(oldHead, conversation.Tree.HeadId);
        Assert.Equal(5, conversation.ContextMessages().Count);
    }

    [Fact]
    public void AutoBudgetIsDisabledWithoutKnownContextWindowAndRejectsInvalidLimits()
    {
        Assert.Null(AutoCompactionPolicy.FromEnvironment(_ => null));
        Assert.Throws<ArgumentException>(() => AutoCompactionPolicy.FromEnvironment(name =>
            name == "PISHARP_CONTEXT_WINDOW_TOKENS" ? "invalid" : null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutoCompactionPolicy(1024, 1024).TriggerTokens);
        var policy = AutoCompactionPolicy.FromEnvironment(name => name == "PISHARP_CONTEXT_WINDOW_TOKENS" ? "2048" : null);
        Assert.Equal(1536, policy!.TriggerTokens);
        Assert.Null(ModelPricing.FromEnvironment(_ => null));
        var pricing = ModelPricing.FromEnvironment(name => name switch
        {
            "PISHARP_INPUT_COST_PER_MILLION" => "2.5",
            "PISHARP_OUTPUT_COST_PER_MILLION" => "10",
            _ => null
        });
        Assert.Equal(new ModelPricing(2.5m, 10m), pricing);
        Assert.Throws<ArgumentException>(() => ModelPricing.FromEnvironment(name =>
            name == "PISHARP_INPUT_COST_PER_MILLION" ? "2" : null));
        var usage = UsageRecord.Create("fixture", "model", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 2,
            TotalTokenCount = 0
        }, null);
        Assert.Equal(12, usage.TotalTokens);
    }

    [Fact]
    public void UsageAccountingAppliesRequestWideModelInputPricingTiers()
    {
        var pricing = new ModelPricing(5m, 30m, 0.5m, [new ModelPricingTier(272000, 10m, 45m, 1m)]);
        var below = UsageRecord.Create("tiered", "model", new UsageDetails
        {
            InputTokenCount = 200000,
            CachedInputTokenCount = 100000,
            OutputTokenCount = 1000
        }, pricing);
        var above = UsageRecord.Create("tiered", "model", new UsageDetails
        {
            InputTokenCount = 272001,
            CachedInputTokenCount = 100000,
            OutputTokenCount = 1000
        }, pricing);
        Assert.Equal(0.58m, below.Cost);
        Assert.Equal(1.86501m, above.Cost);
    }

    [Fact]
    public void CutAtLatestUserKeepsToolCallWithItsResult()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, "first"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("tool1", "read", new Dictionary<string, object?>())]));
        conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("tool1", "read output")]));
        conversation.Append(new ChatMessage(ChatRole.User, "second"));
        var plan = conversation.PrepareCompaction();
        Assert.NotNull(plan);
        Assert.Equal(3, plan.MessagesToSummarize.Count);
        Assert.Equal(conversation.Tree.HeadId, plan.FirstKeptEntryId);
        Assert.Equal("second", conversation.ContextMessages().Last().Text);
        conversation.AppendCompaction(plan, "Older tool read output was inspected.");
        Assert.Equal(4, conversation.ActiveMessages().Count);
        Assert.Equal(2, conversation.ContextMessages().Count);
    }

    [Fact]
    public async Task FailedSummaryOrCheckpointCannotReplaceModelContext()
    {
        var cwd = Path.GetTempPath();
        var conversation = Seed(cwd);
        var before = conversation.Tree.HeadId;
        var failed = new SummaryClient { FailSummary = true };
        var run = await ConversationRun.OpenAsync(new PiAgent(failed, new CodingTools(cwd)), conversation);
        await Assert.ThrowsAsync<IOException>(() => run.CompactAsync());
        Assert.Equal(before, conversation.Tree.HeadId);
        var successful = new SummaryClient();
        var saveFails = await ConversationRun.OpenAsync(new PiAgent(successful, new CodingTools(cwd)), conversation,
            save: _ => throw new IOException("checkpoint failed"));
        await Assert.ThrowsAsync<IOException>(() => saveFails.CompactAsync());
        Assert.Equal(before, conversation.Tree.HeadId);
        Assert.Equal(4, conversation.ContextMessages().Count);
    }

    [Fact]
    public void MalformedCompactionOnInactiveBranchIsRejected()
    {
        var conversation = Seed(Path.GetTempPath());
        var first = conversation.Tree.ActivePath().First(node => node.Type == "chat").Id;
        conversation.Tree.Append("compaction", JsonSerializer.SerializeToElement(new { summary = "bad", firstKeptEntryId = "missing" }));
        conversation.Tree.Select(first);
        Assert.Throws<InvalidDataException>(() => ConversationSession.Parse(conversation.ToJson()));
    }

    private static ConversationSession Seed(string cwd)
    {
        var conversation = new ConversationSession(cwd, "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, "first"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
        conversation.Append(new ChatMessage(ChatRole.User, "second"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
        return conversation;
    }

    private sealed class SummaryClient : IChatClient
    {
        public bool FailSummary { get; init; }
        public UsageDetails? SummaryUsage { get; init; }
        public string LastSummaryRequest { get; private set; } = "";
        public List<ChatMessage>? SeenMessages { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastSummaryRequest = messages.Last().Text;
            if (FailSummary) throw new IOException("summary provider failed");
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Summary of first turn")])
            {
                Usage = SummaryUsage
            });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeenMessages = messages.ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
