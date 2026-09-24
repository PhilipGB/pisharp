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
    public async Task ToolContinuationCompactsOnlyItsRequestAndPreservesCanonicalToolHistory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-loop-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 1400));
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('A', 900)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var client = new ToolLoopBudgetClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                save: token => store.SaveAsync(conversation, path, token),
                autoCompaction: new AutoCompactionPolicy(2100, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read output.txt")) events.Add(item);

            Assert.Equal(2, client.Requests);
            Assert.Equal(1, client.Summaries);
            Assert.True(client.ContinuationSawSummaryAndToolResult);
            Assert.Contains(events, item => item.Type == "context_compacted_in_flight");
            Assert.DoesNotContain(events, item => item.Type == "context_compacted");
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.Equal(900, conversation.ActiveMessages()[0].Text.Length);
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents),
                content => content is FunctionResultContent result && result.Result?.ToString()?.Contains(new string('Z', 1400), StringComparison.Ordinal) == true);
            Assert.Equal(conversation.ActiveMessages().Count, (await store.LoadAsync(path)).ActiveMessages().Count);
            Assert.Null(conversation.LatestContextUsageTokens());
            Assert.Null((await store.LoadAsync(path)).LatestContextUsageTokens());
            Assert.Contains((await store.LoadAsync(path)).Tree.ActivePath(), node => node.Type == "context_projection");
            Assert.Single(conversation.ActiveUsage(), usage => usage.Source == "compaction" && usage.TotalTokens == 48);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task RepeatedToolLoopIterationsReuseUnchangedEarlierTurnSummary()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-loop-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 1400));
            await File.WriteAllTextAsync(Path.Combine(cwd, "small.txt"), "small result");
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('A', 900)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
            var client = new ToolLoopBudgetClient { RepeatRead = true };
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                autoCompaction: new AutoCompactionPolicy(2300, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read both files")) events.Add(item);
            Assert.True(client.Requests == 3, string.Join(" | ", events.Select(item => $"{item.Type}: {item.Error}")));
            Assert.Equal(1, client.Summaries);
            Assert.True(client.ContinuationSawSummaryAndToolResult);
            Assert.Single(events, item => item.Type == "context_compacted_in_flight");
            Assert.Equal(2, conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Count());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Theory]
    [InlineData(70_000, 1)]
    [InlineData(1_100_000, 2)]
    public async Task LargePrefixReusesOnlyBoundedFingerprint(int historySize, int summaries)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-large-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 1400));
            await File.WriteAllTextAsync(Path.Combine(cwd, "small.txt"), "small result");
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('A', historySize)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
            var trigger = AutoCompactionPolicy.Estimate(conversation.ContextMessages(), "read both files") + 300;
            var client = new ToolLoopBudgetClient { RepeatRead = true };
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                autoCompaction: new AutoCompactionPolicy(trigger + 300, 300));
            await foreach (var _ in run.RunEventsAsync("read both files")) { }
            Assert.Equal(3, client.Requests);
            Assert.Equal(summaries, client.Summaries);
            Assert.True(client.ContinuationSawSummaryAndToolResult);
            Assert.Contains(conversation.ActiveMessages(), message => message.Text == new string('A', historySize));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task NewSteeringBoundaryInvalidatesInFlightSummaryCache()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-loop-steering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 1400));
            await File.WriteAllTextAsync(Path.Combine(cwd, "small.txt"), "small result");
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('A', 900)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
            ConversationRun? run = null;
            var client = new ToolLoopBudgetClient
            {
                RepeatRead = true,
                OnFirstSummary = () => Assert.True(run!.TrySteer("explain the second result"))
            };
            run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                autoCompaction: new AutoCompactionPolicy(2300, 300));
            await foreach (var _ in run.RunEventsAsync("read both files")) { }
            Assert.Equal(3, client.Requests);
            Assert.Equal(2, client.Summaries);
            Assert.True(client.ContinuationSawSteering);
            Assert.Contains(conversation.ActiveMessages(), message => message.Text.Contains("explain the second result", StringComparison.Ordinal));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task OversizedMultiCycleTurnCutsOnlyAfterEachCompletedToolResult()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-split-cycles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 4000));
            await File.WriteAllTextAsync(Path.Combine(cwd, "small.txt"), "small result");
            var conversation = new ConversationSession(cwd, "fixture", null);
            var client = new ToolLoopBudgetClient { RepeatRead = true };
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                autoCompaction: new AutoCompactionPolicy(2700, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read two files")) events.Add(item);
            Assert.Equal(3, client.Requests);
            Assert.Equal(2, client.Summaries);
            Assert.True(client.ContinuationSawSummaryWithoutRawToolResult);
            Assert.Equal(2, events.Count(item => item.Type == "context_compacted_in_flight"));
            Assert.Equal(2, conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains(new string('Z', 4000), StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task LongSummaryTranscriptRetainsLatestMessageWithinBound()
    {
        var client = new ToolLoopBudgetClient();
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()));
        var messages = Enumerable.Range(0, 60).Select(i =>
            new ChatMessage(ChatRole.User, new string('A', 1900) + (i == 59 ? " LATEST_MARKER" : $" {i}"))).ToArray();
        await agent.SummarizeAsync(messages, null);
        Assert.Contains("LATEST_MARKER", client.LastSummaryRequest);
        Assert.Contains("Earlier conversation omitted", client.LastSummaryRequest);
        Assert.True(client.LastSummaryRequest.Length < 65 * 1024);
    }

    [Fact]
    public async Task ParallelToolCallBatchCutsOnlyAfterBothResultsAndRetainsRawHistory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-parallel-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "first.txt"), new string('X', 4000));
            await File.WriteAllTextAsync(Path.Combine(cwd, "second.txt"), new string('Y', 4000));
            var conversation = new ConversationSession(cwd, "fixture", null);
            var client = new ParallelToolBudgetClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                autoCompaction: new AutoCompactionPolicy(2700, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read both files")) events.Add(item);
            Assert.Equal(2, client.Requests);
            Assert.Equal(1, client.Summaries);
            Assert.True(client.ContinuationSawSummaryWithoutOrphanedResults);
            Assert.Single(events, item => item.Type == "context_compacted_in_flight");
            var contents = conversation.ActiveMessages().SelectMany(message => message.Contents).ToArray();
            Assert.Equal(2, contents.OfType<FunctionCallContent>().Count());
            Assert.Equal(2, contents.OfType<FunctionResultContent>().Count());
            Assert.Contains(contents.OfType<FunctionResultContent>(), result => result.Result?.ToString()?.Contains(new string('X', 4000), StringComparison.Ordinal) == true);
            Assert.Contains(contents.OfType<FunctionResultContent>(), result => result.Result?.ToString()?.Contains(new string('Y', 4000), StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task OversizedSingleTurnSummarizesOnlyCompletedToolCycleWithoutReplacingHistory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-split-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 4000) + " TAIL_SENTINEL");
            var conversation = new ConversationSession(cwd, "fixture", null);
            var client = new ToolLoopBudgetClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                autoCompaction: new AutoCompactionPolicy(2700, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read output.txt")) events.Add(item);
            Assert.Equal(2, client.Requests);
            Assert.Equal(1, client.Summaries);
            Assert.True(client.ContinuationSawSummaryWithoutRawToolResult);
            Assert.Contains("TAIL_SENTINEL", client.LastSummaryRequest);
            Assert.True(client.LastSummaryRequest.Length < 64 * 1024);
            Assert.Single(events, item => item.Type == "context_compacted_in_flight");
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>(), result => result.Result?.ToString()?.Contains(new string('Z', 4000), StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-loop-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 4000));
            var conversation = new ConversationSession(cwd, "fixture", null);
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var client = new ToolLoopBudgetClient { FailSummary = true };
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                save: token => store.SaveAsync(conversation, path, token),
                autoCompaction: new AutoCompactionPolicy(2700, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read output.txt")) events.Add(item);
            Assert.Equal(1, client.Requests);
            Assert.Equal(1, client.Summaries);
            Assert.Contains(events, item => item.Type == "turn_failed" && item.Error!.Contains("summarizer unavailable", StringComparison.Ordinal));
            var reloaded = await store.LoadAsync(path);
            Assert.Contains(reloaded.Tree.ActivePath(), node => node.Type == "tool_outcome");
            Assert.Contains(reloaded.Tree.ActivePath(), node => node.Type == "run_finished" &&
                node.Payload.GetProperty("completed").GetBoolean() == false);
            await foreach (var _ in run.RunEventsAsync("inspect the previous result before trying again")) { }
            Assert.True(client.SawRecoveryNotice);
            Assert.Equal(2, client.Requests);
            Assert.Contains(conversation.ActiveMessages(), message => message.Text.Contains("Recovery notice", StringComparison.Ordinal));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ToolContinuationSummaryFailureDoesNotSendUnboundedRequestOrReplaceHistory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-loop-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "output.txt"), new string('Z', 1400));
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('A', 900)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var client = new ToolLoopBudgetClient { FailSummary = true };
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), conversation,
                save: token => store.SaveAsync(conversation, path, token),
                autoCompaction: new AutoCompactionPolicy(2100, 300));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("read output.txt")) events.Add(item);
            Assert.Equal(1, client.Requests);
            Assert.Equal(1, client.Summaries);
            Assert.Contains(events, item => item.Type == "turn_failed" && item.Error!.Contains("summarizer unavailable", StringComparison.Ordinal));
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.Equal(900, conversation.ActiveMessages()[0].Text.Length);
            var reloaded = await store.LoadAsync(path);
            Assert.Contains(reloaded.Tree.ActivePath(), node => node.Type == "tool_outcome");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Theory]
    [InlineData("Your input exceeds the context window of this model", false, true)]
    [InlineData("the request exceeds the available context size, try increasing it", false, true)]
    [InlineData("400 `prompt too long; exceeded max context length by 100918 tokens`", false, true)]
    [InlineData("400 Input length (265330) exceeds model's maximum context length (262144).", false, true)]
    [InlineData("400 model runner crashed", false, false)]
    [InlineData("rate limit: too many requests; context_length_exceeded", false, false)]
    [InlineData("Your input exceeds the context window of this model", true, false)]
    public async Task ProviderOverflowRetriesOnceOnlyBeforeContentWithShortenedRequest(string error, bool afterContent, bool recover)
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, new string('P', 900)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
        var client = new OverflowBudgetClient(error, afterContent);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()), noTools: true), conversation,
            autoCompaction: new AutoCompactionPolicy(4000, 500));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("new prompt")) events.Add(item);

        Assert.Equal(recover ? 2 : 1, client.Requests);
        Assert.Equal(recover ? 1 : 0, client.Summaries);
        Assert.Equal(recover, client.RetrySawSummaryWithoutOldRawUser);
        Assert.Equal(recover, events.Any(item => item.Type == "model_context_overflow_recovery"));
        Assert.Contains(events, item => item.Type == (recover ? "turn_completed" : "turn_failed"));
        Assert.Equal(900, conversation.ActiveMessages()[0].Text.Length);
        Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "compaction");
        if (recover) Assert.Single(conversation.ActiveMessages(), message => message.Text == "new prompt");
    }

    [Fact]
    public async Task RepeatedProviderOverflowStopsAfterOneRecoveryAttempt()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, new string('P', 900)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
        var client = new OverflowBudgetClient("context_length_exceeded", afterContent: false, alwaysOverflow: true);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()), noTools: true), conversation,
            autoCompaction: new AutoCompactionPolicy(4000, 500));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("new prompt")) events.Add(item);
        Assert.Equal(2, client.Requests);
        Assert.Equal(1, client.Summaries);
        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.Single(events, item => item.Type == "model_context_overflow_recovery");
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
        var pricing = new ModelPricing(5m, 30m, 0.5m, [new ModelPricingTier(272000, 10m, 45m, 1m, 12.5m)], 6.25m);
        var below = UsageRecord.Create("tiered", "model", new UsageDetails
        {
            InputTokenCount = 200000,
            CachedInputTokenCount = 100000,
            OutputTokenCount = 1000,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cacheWriteTokens"] = 10000 }
        }, pricing);
        var above = UsageRecord.Create("tiered", "model", new UsageDetails
        {
            InputTokenCount = 272001,
            CachedInputTokenCount = 100000,
            OutputTokenCount = 1000,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cacheWriteTokens"] = 10000 }
        }, pricing);
        Assert.Equal(0.6425m, below.Cost);
        Assert.Equal(1.99001m, above.Cost);
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
    public void RecentTokenBudgetKeepsWholeTurnsAndNeverSplitsToolResults()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, "first"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "reply one"));
        conversation.Append(new ChatMessage(ChatRole.User, "second"));
        conversation.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("tool2", "read", new Dictionary<string, object?>())]));
        conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("tool2", "read output")]));
        conversation.Append(new ChatMessage(ChatRole.User, "third"));
        var minimum = conversation.PrepareCompaction(0)!;
        Assert.Equal(5, minimum.MessagesToSummarize.Count);
        var twoTurns = conversation.PrepareCompaction(300)!;
        Assert.Equal(2, twoTurns.MessagesToSummarize.Count);
        conversation.AppendCompaction(twoTurns, "First turn summary", 300);
        Assert.Equal(6, conversation.ActiveMessages().Count);
        Assert.Equal(5, conversation.ContextMessages().Count);
        Assert.Null(conversation.PrepareCompaction(100000));
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

    private sealed class OverflowBudgetClient(string error, bool afterContent, bool alwaysOverflow = false) : IChatClient
    {
        public int Requests { get; private set; }
        public int Summaries { get; private set; }
        public bool RetrySawSummaryWithoutOldRawUser { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Summaries++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Earlier context summary")])
            {
                Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 5, TotalTokenCount = 25 }
            });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Requests == 1)
            {
                if (afterContent) yield return new ChatResponseUpdate(ChatRole.Assistant, "partial output");
                throw new HttpRequestException(error, null, System.Net.HttpStatusCode.BadRequest);
            }
            var snapshot = messages.ToArray();
            RetrySawSummaryWithoutOldRawUser = snapshot.Any(message => message.Text.Contains("Earlier context summary", StringComparison.Ordinal)) &&
                !snapshot.Any(message => message.Text == new string('P', 900));
            if (alwaysOverflow) throw new HttpRequestException(error, null, System.Net.HttpStatusCode.BadRequest);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ParallelToolBudgetClient : IChatClient
    {
        public int Requests { get; private set; }
        public int Summaries { get; private set; }
        public bool ContinuationSawSummaryWithoutOrphanedResults { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Summaries++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Both files were read.")]));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Requests == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("first", "read", new Dictionary<string, object?> { ["path"] = "first.txt" }),
                    new FunctionCallContent("second", "read", new Dictionary<string, object?> { ["path"] = "second.txt" })
                ]);
            else
            {
                var snapshot = messages.ToArray();
                ContinuationSawSummaryWithoutOrphanedResults =
                    snapshot.Any(message => message.Text.Contains("Both files were read.", StringComparison.Ordinal)) &&
                    !snapshot.SelectMany(message => message.Contents).Any(content => content is FunctionCallContent or FunctionResultContent);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolLoopBudgetClient : IChatClient
    {
        public int Requests { get; private set; }
        public int Summaries { get; private set; }
        public bool FailSummary { get; init; }
        public bool ContinuationSawSummaryAndToolResult { get; private set; }
        public string LastSummaryRequest { get; private set; } = "";
        public bool RepeatRead { get; init; }
        public Action? OnFirstSummary { get; init; }
        public bool ContinuationSawSummaryWithoutRawToolResult { get; private set; }
        public bool ContinuationSawSteering { get; private set; }
        public bool SawRecoveryNotice { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Summaries++;
            if (FailSummary) throw new IOException("summarizer unavailable");
            LastSummaryRequest = messages.Last().Text;
            if (Summaries == 1) OnFirstSummary?.Invoke();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Previous question answered.")])
            {
                Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 8, TotalTokenCount = 48 }
            });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Requests == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("read-1", "read", new Dictionary<string, object?> { ["path"] = "output.txt" })]);
            else if (Requests == 2 && RepeatRead)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("read-2", "read", new Dictionary<string, object?> { ["path"] = "small.txt" })]);
            else
            {
                var snapshot = messages.ToArray();
                SawRecoveryNotice = snapshot.Any(message => message.Text.Contains("Recovery notice", StringComparison.Ordinal));
                ContinuationSawSteering = snapshot.Any(message => message.Text.Contains("explain the second result", StringComparison.Ordinal));
                ContinuationSawSummaryWithoutRawToolResult = snapshot.Any(message => message.Text.Contains("Previous question answered.", StringComparison.Ordinal)) &&
                    !snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
                ContinuationSawSummaryAndToolResult = snapshot.Any(message => message.Text.Contains("Previous question answered.", StringComparison.Ordinal)) &&
                    snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                        .Any(result => result.Result?.ToString()?.Contains(new string('Z', 1400), StringComparison.Ordinal) == true) &&
                    !snapshot.Any(message => message.Text == new string('A', 900));
                yield return new ChatResponseUpdate(ChatRole.Assistant, "finished");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(new UsageDetails { InputTokenCount = 20, OutputTokenCount = 4, TotalTokenCount = 24 })]);
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
