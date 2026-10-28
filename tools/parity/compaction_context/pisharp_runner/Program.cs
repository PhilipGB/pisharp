using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
var scenario = args.Length > 1 ? args[1] : "length-recovery";
var contextBoundaryLength = scenario == "context-boundary-length";
var staleOldModelOverflow = scenario == "stale-old-model-overflow";
var reportedInputTokens = scenario switch
{
    "context-boundary-length" => 10_000,
    "stale-old-model-overflow" => 1_000,
    "silent-usage-overflow" => 10_001,
    "unknown-model-window" => 20_000,
    _ => 80
};
var reportedOutputTokens = contextBoundaryLength || staleOldModelOverflow ? 0 : 16;
var alwaysLength = scenario == "second-length-stop";
var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compaction-oracle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workingDirectory);
try
{
    if (scenario.StartsWith("manual-", StringComparison.Ordinal))
    {
        var manualConversation = new ConversationSession(workingDirectory, "fixture", null);
        manualConversation.Append(new ChatMessage(ChatRole.User, "first"));
        if (scenario is "manual-eligible" or "manual-failure" or "manual-split-turn" or "manual-queue-order")
        {
            manualConversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
            manualConversation.Append(new ChatMessage(ChatRole.User, "second"));
            manualConversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
        }
        if (scenario == "manual-split-turn")
        {
            manualConversation.Append(new ChatMessage(ChatRole.User, "current request"));
            manualConversation.Append(new ChatMessage(ChatRole.Assistant, "partial answer"));
            manualConversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
            {
                prompt = "current request",
                partialAssistantText = "partial answer",
                terminalType = "turn_interrupted",
                stopReason = "aborted",
                errorMessage = "Request was aborted"
            }));
        }
        var manualClient = new ManualSummaryClient(scenario == "manual-failure", scenario);
        var manualAgent = new PiAgent(manualClient, new CodingTools(workingDirectory), noTools: true);
        var manualRun = await ConversationRun.OpenAsync(manualAgent, manualConversation,
            pricing: new ModelPricing(Input: 10_000m, Output: 10_000m, CachedInput: 10_000m, CachedWrite: 10_000m),
            keepRecentTokens: 1);
        var manualEvents = new List<AgentLifecycleEvent>();
        var headBefore = manualConversation.Tree.HeadId;
        ConversationCompactionResult? manualResult = null;
        string? manualError = null;
        Task? queuedRunTask = null;
        try
        {
            manualResult = await manualRun.CompactWithResultAsync(
                scenario == "manual-eligible" ? "prioritize files" : null,
                publishEvent: item =>
                {
                    manualEvents.Add(item);
                    if (scenario == "manual-queue-order" && item.Type == "compaction_end" &&
                        item.CompactionResult is not null)
                    {
                        using (ExecutionContext.SuppressFlow())
                        {
                            queuedRunTask = Task.Run(async () =>
                            {
                                await foreach (var _ in manualRun.RunEventsAsync("queued after compaction")) { }
                            });
                        }
                    }
                    return Task.CompletedTask;
                });
            if (queuedRunTask is not null) await queuedRunTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            manualError = error.Message;
        }
        var manualEntries = manualConversation.Tree.ActivePath().ToList();
        var firstKeptEntryIndex = manualResult is null
            ? (int?)null
            : manualEntries.FindIndex(entry => entry.Id == manualResult.FirstKeptEntryId);
        var manualOutput = new
        {
            scenario,
            compactionSucceeded = manualResult is not null,
            error = manualError,
            canonicalStateUnchanged = manualResult is null && manualConversation.Tree.HeadId == headBefore,
            result = manualResult is null ? null : new
            {
                summary = manualResult.Summary,
                firstKeptEntryIndex,
                tokensBefore = manualResult.TokensBefore,
                estimatedTokensAfter = manualResult.EstimatedTokensAfter,
                usage = manualResult.Usage,
                details = manualResult.Details,
            },
            summaryRequests = manualClient.Requests,
            compactionCount = manualEntries.Count(entry => entry.Type == "compaction"),
            entries = manualEntries.Select(entry => new
            {
                type = entry.Type,
                summary = entry.Type == "compaction" ? entry.Payload.GetProperty("summary").GetString() : null,
                message = EntryMessageText(entry),
                usage = entry.Type == "compaction" && entry.Payload.TryGetProperty("usage", out var usage)
                    ? usage : (JsonElement?)null,
            }),
            contextMessages = manualConversation.ContextMessages().Select(MessageSnapshot),
            activeMessages = manualConversation.ActiveMessages().Select(MessageSnapshot),
            events = manualEvents.Select(item => new
            {
                type = item.Type,
                reason = item.CompactionReason,
                willRetry = item.CompactionWillRetry,
                aborted = item.CompactionAborted,
                error = item.Error,
            }),
        };
        var manualJson = JsonSerializer.Serialize(manualOutput, new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(manualJson);
        else await File.WriteAllTextAsync(outputPath, manualJson + Environment.NewLine);
        return;
    }

    var client = new LengthStopClient(scenario, reportedInputTokens, reportedOutputTokens, alwaysLength);
    var agent = new PiAgent(client, new CodingTools(workingDirectory), noTools: true, maxOutputTokens: 100);
    var conversation = new ConversationSession(workingDirectory, "fixture", null);
    conversation.Append(new ChatMessage(ChatRole.User, "first"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
    conversation.Append(new ChatMessage(ChatRole.User, "second"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
    AutoCompactionPolicy? compactionPolicy = contextBoundaryLength || scenario == "silent-usage-overflow"
        ? new AutoCompactionPolicy(10_000, 0, KeepRecentTokens: 1)
        : staleOldModelOverflow ? new AutoCompactionPolicy(1_000, 0, KeepRecentTokens: 1)
        : scenario == "unknown-model-window" ? null
            : new AutoCompactionPolicy(50_000, 10_000, KeepRecentTokens: 1);
    var run = await ConversationRun.OpenAsync(agent, conversation, autoCompaction: compactionPolicy);
    var events = new List<AgentLifecycleEvent>();
    var modelSwitchApplied = false;
    await foreach (var item in run.RunEventsAsync("continue"))
    {
        if (staleOldModelOverflow && !modelSwitchApplied && item.Type == "model_text_delta")
            modelSwitchApplied = run.TrySetModelDuringRun("new-model", null, "new-provider", null,
                new AutoCompactionPolicy(1_000_000, 0, KeepRecentTokens: 1), 1, "off", null,
                () => agent.SetModelRuntime(client, supportsImages: true, imageResizeOptions: null,
                    maxOutputTokens: 100));
        events.Add(item);
    }

    var result = new
    {
        scenario,
        modelSwitchApplied,
        selectedModel = conversation.Model,
        streamingRequests = client.Requests.Count,
        providerRequests = client.Requests,
        summaryRequests = client.SummaryRequests,
        summaryProviderRequests = client.SummaryRequestsCapture,
        events = events.Select(item => new
        {
            item.Type,
            item.Text,
            item.Error,
            item.CompactionReason,
            item.CompactionWillRetry,
            item.ProviderFinishReason,
            item.ProviderDesiredMaxOutputTokens,
            inputTokens = item.ProviderUsage?.InputTokenCount,
            outputTokens = item.ProviderUsage?.OutputTokenCount
        }),
        contextMessages = conversation.ContextMessages().Select(MessageSnapshot),
        activeMessages = conversation.ActiveMessages().Select(MessageSnapshot),
        activeEntries = conversation.Tree.ActivePath().Select(entry => new
        {
            entry.Type,
            summary = entry.Type == "compaction" ? entry.Payload.GetProperty("summary").GetString() : null
        })
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    if (outputPath is null) Console.WriteLine(json);
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);
    }
}
finally
{
    Directory.Delete(workingDirectory, recursive: true);
}

static object MessageSnapshot(ChatMessage message) => new
{
    role = message.Role.Value,
    text = message.Text,
    contentTypes = message.Contents.Select(content => content.GetType().Name)
};

static string? EntryMessageText(ConversationNode entry) => entry.Type == "chat"
    ? entry.Payload.GetProperty("Message").Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)?.Text
    : null;

sealed class LengthStopClient(string scenario, long reportedInputTokens, long reportedOutputTokens,
    bool alwaysLength) : IChatClient
{
    public List<object> Requests { get; } = [];
    public List<object> SummaryRequestsCapture { get; } = [];
    public int SummaryRequests => SummaryRequestsCapture.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages.ToArray();
        SummaryRequestsCapture.Add(new
        {
            messages = request.Select(MessageSnapshot),
            maxOutputTokens = options?.MaxOutputTokens
        });
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "compacted history summary")])
        {
            Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 8, TotalTokenCount = 48 }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages.ToArray();
        Requests.Add(new
        {
            messages = request.Select(MessageSnapshot),
            maxOutputTokens = options?.MaxOutputTokens
        });
        if (Requests.Count == 1 && scenario == "pre-content-overflow")
            throw new HttpRequestException("context_length_exceeded: prompt exceeds context window");
        if (Requests.Count == 1 && scenario == "partial-content-overflow")
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial output");
            throw new HttpRequestException("context_length_exceeded: prompt exceeds context window");
        }
        if (Requests.Count == 1)
        {
            var responseText = scenario is "silent-usage-overflow" or "unknown-model-window"
                ? "completed response" : "partial output";
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails
                {
                    InputTokenCount = reportedInputTokens,
                    OutputTokenCount = reportedOutputTokens,
                    TotalTokenCount = reportedInputTokens + reportedOutputTokens
                })])
            {
                FinishReason = scenario is "silent-usage-overflow" or "unknown-model-window"
                    ? ChatFinishReason.Stop : ChatFinishReason.Length
            };
        }
        else
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued response");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 60, OutputTokenCount = 12, TotalTokenCount = 72 })])
            {
                FinishReason = alwaysLength ? ChatFinishReason.Length : ChatFinishReason.Stop
            };
        }
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    private static object MessageSnapshot(ChatMessage message) => new
    {
        role = message.Role.Value,
        text = message.Text,
        contentTypes = message.Contents.Select(content => content.GetType().Name)
    };
}

sealed class ManualSummaryClient(bool failSummary, string scenario) : IChatClient
{
    public List<object> Requests { get; } = [];
    public List<object> StreamingRequests { get; } = [];
    private int _summaryCount;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages.ToArray();
        Requests.Add(new
        {
            messages = request.Select(MessageSnapshot),
            maxOutputTokens = options?.MaxOutputTokens,
        });
        if (failSummary) throw new IOException("summary provider failed");
        _summaryCount++;
        var summary = scenario == "manual-split-turn"
            ? _summaryCount == 1 ? "history summary" : "turn prefix summary"
            : "compacted history summary";
        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, summary)])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 40,
                OutputTokenCount = 20,
                CachedInputTokenCount = 30,
                TotalTokenCount = 100,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cacheWriteTokens"] = 40 },
            }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamingRequests.Add(messages.Select(MessageSnapshot).ToArray());
        if (scenario == "manual-queue-order")
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "queued response")
            {
                FinishReason = ChatFinishReason.Stop
            };
        }
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    private static object MessageSnapshot(ChatMessage message) => new
    {
        role = message.Role.Value,
        text = message.Text,
        contentTypes = message.Contents.Select(content => content.GetType().Name),
    };
}
