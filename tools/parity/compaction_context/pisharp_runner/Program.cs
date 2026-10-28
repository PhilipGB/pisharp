using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
var scenario = args.Length > 1 ? args[1] : "length-recovery";
var contextBoundaryLength = scenario == "context-boundary-length";
var staleOldModelOverflow = scenario == "stale-old-model-overflow";
var thresholdScenario = scenario is "threshold-after-turn" or "threshold-equal" or "threshold-over" or "queued-steering";
var reportedInputTokens = scenario switch
{
    "context-boundary-length" => 10_000,
    "stale-old-model-overflow" => 1_000,
    "threshold-after-turn" => 9_950,
    "threshold-equal" => 9_820,
    "threshold-over" => 9_821,
    "queued-steering" => 10_050,
    "disabled-setting" => 2_000,
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
    if (scenario == "c3")
    {
        var c3Json = JsonSerializer.Serialize(C3Oracle.Run(workingDirectory), new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(c3Json);
        else await File.WriteAllTextAsync(outputPath, c3Json + Environment.NewLine);
        return;
    }

    if (scenario == "c4")
    {
        var c4Json = JsonSerializer.Serialize(C4Oracle.Run(workingDirectory), new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(c4Json);
        else await File.WriteAllTextAsync(outputPath, c4Json + Environment.NewLine);
        return;
    }

    if (scenario == "c6")
    {
        var c6Json = JsonSerializer.Serialize(await C6Oracle.RunAsync(), new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(c6Json);
        else await File.WriteAllTextAsync(outputPath, c6Json + Environment.NewLine);
        return;
    }

    if (scenario == "c7")
    {
        var c7Json = JsonSerializer.Serialize(await C7Oracle.RunAsync(), new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(c7Json);
        else await File.WriteAllTextAsync(outputPath, c7Json + Environment.NewLine);
        return;
    }

    if (scenario == "c8")
    {
        var c8Json = JsonSerializer.Serialize(await C8Oracle.RunAsync(workingDirectory), new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(c8Json);
        else await File.WriteAllTextAsync(outputPath, c8Json + Environment.NewLine);
        return;
    }

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

    if (scenario == "physical-route-change")
    {
        var routeConversation = new ConversationSession(workingDirectory, "auto", null, "router");
        routeConversation.SelectModel("auto", null, "router");
        routeConversation.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 18_000)));
        routeConversation.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        routeConversation.Append(new ChatMessage(ChatRole.User, "second"));
        routeConversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
        var largeClient = new LengthStopClient(scenario, 9_500, 16, false);
        var smallClient = new LengthStopClient(scenario, 100, 16, false);
        var summaryClient = new LengthStopClient(scenario, 60, 12, false);
        var routeDecisions = new List<(string Reason, string Model)>();
        var userRoutes = 0;
        VirtualModelRequestRouter router = request =>
        {
            var modelId = request.Reason == "direct" ? "large" : userRoutes++ == 0 ? "large" : "small";
            routeDecisions.Add((request.Reason, modelId));
            var large = modelId == "large";
            var routePolicy = new AutoCompactionPolicy(large ? 50_000 : 10_000,
                ReserveTokens: large ? 0 : 1_000, KeepRecentTokens: 1);
            var model = new ModelDescriptor(modelId, null, large ? 50_000 : 10_000, null,
                Provider: "physical", Api: "test-api");
            return Task.FromResult(new VirtualModelRequestRoute(
                request.Reason == "direct" ? summaryClient : large ? largeClient : smallClient,
                model, "physical", "off", null, null, routePolicy));
        };
        var routedAgent = new PiAgent(largeClient, new CodingTools(workingDirectory), noTools: true,
            virtualModelRequestRouter: router, maxOutputTokens: 100);
        var routedRun = await ConversationRun.OpenAsync(routedAgent, routeConversation,
            autoCompaction: new AutoCompactionPolicy(50_000, 0, KeepRecentTokens: 1));
        var routedEvents = new List<AgentLifecycleEvent>();
        await foreach (var item in routedRun.RunEventsAsync("first on large route")) routedEvents.Add(item);
        await foreach (var item in routedRun.RunEventsAsync("next on small route")) routedEvents.Add(item);
        var routeRequestCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["large"] = 0,
            ["small"] = 0
        };
        var providerRequests = new List<object>();
        foreach (var route in routeDecisions.Where(item => item.Reason != "direct"))
        {
            var clientRequest = route.Model == "large"
                ? largeClient.Requests[routeRequestCounts["large"]++]
                : smallClient.Requests[routeRequestCounts["small"]++];
            var requestJson = JsonSerializer.SerializeToElement(clientRequest);
            providerRequests.Add(new
            {
                model = route.Model,
                messages = requestJson.GetProperty("messages").EnumerateArray().Select(message => new
                {
                    role = message.GetProperty("role").GetString(),
                    text = message.GetProperty("text").GetString()
                })
            });
        }
        var routedEntries = routeConversation.Tree.ActivePath().ToList();
        var routedOutput = new
        {
            scenario,
            selectedModel = routeConversation.Model,
            routeDecisions = routeDecisions.Select(item => new { reason = item.Reason, model = item.Model }),
            physicalRouteModels = routeDecisions.Where(item => item.Reason != "direct").Select(item => item.Model),
            providerRequests,
            summaryRequests = summaryClient.SummaryRequests,
            compactionCount = routedEntries.Count(entry => entry.Type == "compaction"),
            events = routedEvents.Select(item => new
            {
                Type = item.Type,
                item.Error,
                item.CompactionReason,
                item.CompactionWillRetry,
                inputTokens = item.ProviderUsage?.InputTokenCount,
                outputTokens = item.ProviderUsage?.OutputTokenCount
            }),
            activeMessages = routeConversation.ActiveMessages().Select(MessageSnapshot),
            activeEntries = routedEntries.Select(entry => new { entry.Type })
        };
        var routedJson = JsonSerializer.Serialize(routedOutput, new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(routedJson);
        else await File.WriteAllTextAsync(outputPath, routedJson + Environment.NewLine);
        return;
    }

    if (scenario == "error-turn-usage")
    {
        async Task<object> RunErrorUsageSubcase(string name, long validUsageTokens, bool compactBeforeError)
        {
            var errorConversation = new ConversationSession(workingDirectory, "fixture", null);
            errorConversation.Append(new ChatMessage(ChatRole.User, "first history"));
            errorConversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
            errorConversation.Append(new ChatMessage(ChatRole.User, "second history"));
            errorConversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
            var errorClient = new LengthStopClient(scenario, validUsageTokens, 0, false);
            var errorAgent = new PiAgent(errorClient, new CodingTools(workingDirectory), noTools: true,
                maxOutputTokens: 100);
            var errorRun = await ConversationRun.OpenAsync(errorAgent, errorConversation,
                autoCompaction: new AutoCompactionPolicy(20_000, 10_000, KeepRecentTokens: 1),
                retryPolicy: AgentRunRetryPolicy.None);
            var errorEvents = new List<AgentLifecycleEvent>();
            if (compactBeforeError) errorRun.SetAutoCompactionEnabled(false);
            await foreach (var item in errorRun.RunEventsAsync("successful prior turn")) errorEvents.Add(item);
            if (compactBeforeError)
            {
                var priorCompaction = await errorRun.CompactWithResultAsync(publishEvent: item =>
                {
                    errorEvents.Add(item);
                    return Task.CompletedTask;
                });
                if (priorCompaction is null) throw new InvalidOperationException("stale-usage fixture did not compact");
                errorRun.SetAutoCompactionEnabled(true);
            }
            else errorRun.SetAutoCompactionEnabled(false);
            var compactionsBeforeError = errorConversation.Tree.ActivePath()
                .Count(entry => entry.Type == "compaction");
            errorClient.OnFailure = () => errorRun.SetAutoCompactionEnabled(true);
            await foreach (var item in errorRun.RunEventsAsync(new string('x', 320))) errorEvents.Add(item);
            var activePath = errorConversation.Tree.ActivePath().ToList();
            return new
            {
                name,
                providerAttemptCount = errorClient.Requests.Count,
                summaryRequestCount = errorClient.SummaryRequests,
                compactionCountBeforeError = compactionsBeforeError,
                compactionCount = activePath.Count(entry => entry.Type == "compaction"),
                thresholdCompactionEvents = errorEvents.Where(item => item.Type == "compaction_end" &&
                    item.CompactionReason == "threshold").Select(item => new
                    {
                        reason = item.CompactionReason,
                        willRetry = item.CompactionWillRetry ?? false
                    }),
                providerRequests = errorClient.Requests,
                events = errorEvents.Select(item => new
                {
                    item.Type,
                    item.Error,
                    item.CompactionReason,
                    item.CompactionWillRetry,
                    inputTokens = item.ProviderUsage?.InputTokenCount,
                    outputTokens = item.ProviderUsage?.OutputTokenCount
                }),
                activeMessages = errorConversation.ActiveMessages().Select(MessageSnapshot)
            };
        }

        var errorUsageOutput = new
        {
            scenario,
            errorTurnObservations = new
            {
                lastValidUsageTriggersThreshold = await RunErrorUsageSubcase("last-valid-usage", 9_936, false),
                failedResponseUsageDoesNotReplaceLastValid = await RunErrorUsageSubcase("failed-usage-is-stale", 1_000, false),
                keptPreCompactionUsageIsIgnored = await RunErrorUsageSubcase("kept-usage-is-stale", 19_000, true)
            }
        };
        var errorUsageJson = JsonSerializer.Serialize(errorUsageOutput, new JsonSerializerOptions { WriteIndented = true });
        if (outputPath is null) Console.WriteLine(errorUsageJson);
        else await File.WriteAllTextAsync(outputPath, errorUsageJson + Environment.NewLine);
        return;
    }

    var client = new LengthStopClient(scenario, reportedInputTokens, reportedOutputTokens, alwaysLength);
    var agent = new PiAgent(client, new CodingTools(workingDirectory), noTools: true, maxOutputTokens: 100);
    var conversation = new ConversationSession(workingDirectory, "fixture", null);
    conversation.Append(new ChatMessage(ChatRole.User,
        scenario == "pre-prompt-compaction" ? new string('x', 12_000) : "first"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
    conversation.Append(new ChatMessage(ChatRole.User, "second"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
    AutoCompactionPolicy? compactionPolicy = scenario == "queued-steering"
        ? new AutoCompactionPolicy(12_000, 2_000, KeepRecentTokens: 1)
        : thresholdScenario
        ? new AutoCompactionPolicy(10_000, 100, KeepRecentTokens: 1)
        : scenario == "pre-prompt-compaction" ? new AutoCompactionPolicy(1_800, 300, KeepRecentTokens: 1)
        : contextBoundaryLength || scenario == "silent-usage-overflow"
        ? new AutoCompactionPolicy(10_000, 0, KeepRecentTokens: 1)
        : staleOldModelOverflow ? new AutoCompactionPolicy(1_000, 0, KeepRecentTokens: 1)
        : scenario == "unknown-model-window" ? null
            : scenario == "disabled-setting" ? null
                : new AutoCompactionPolicy(50_000, 10_000, KeepRecentTokens: 1);
    var run = await ConversationRun.OpenAsync(agent, conversation, autoCompaction: compactionPolicy);
    var events = new List<AgentLifecycleEvent>();
    var queuedSteerAccepted = false;
    if (scenario == "queued-steering") client.OnSummary = () => queuedSteerAccepted = run.TrySteer("change direction");
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
        queuedSteerAccepted,
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
    public Action? OnSummary { get; set; }
    public Action? OnFailure { get; set; }
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
        if (scenario == "queued-steering" && SummaryRequestsCapture.Count == 1) OnSummary?.Invoke();
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
        if (scenario == "error-turn-usage" && Requests.Count == 2)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 19_000,
                    OutputTokenCount = 0,
                    TotalTokenCount = 19_000
                })]);
            OnFailure?.Invoke();
            throw new InvalidOperationException("fixture provider failure");
        }
        if (Requests.Count == 1 && scenario == "pre-content-overflow")
            throw new HttpRequestException("context_length_exceeded: prompt exceeds context window");
        if (Requests.Count == 1 && scenario == "partial-content-overflow")
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial output");
            throw new HttpRequestException("context_length_exceeded: prompt exceeds context window");
        }
        if (Requests.Count == 1)
        {
            var responseText = scenario is "silent-usage-overflow" or "unknown-model-window" or "threshold-after-turn" or "queued-steering" or
                "physical-route-change" or "error-turn-usage" or
                "threshold-equal" or "threshold-over" or "disabled-setting" or "pre-prompt-compaction"
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
                FinishReason = scenario is "silent-usage-overflow" or "unknown-model-window" or "threshold-after-turn" or "queued-steering" or
                    "physical-route-change" or "error-turn-usage" or
                    "threshold-equal" or "threshold-over" or "disabled-setting" or "pre-prompt-compaction"
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
        if (failSummary) throw new InvalidOperationException("summary provider failed");
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
