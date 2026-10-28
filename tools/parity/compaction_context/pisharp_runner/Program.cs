using System.Text.Json;
using Microsoft.Extensions.AI;
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
