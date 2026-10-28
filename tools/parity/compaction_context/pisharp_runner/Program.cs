using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compaction-oracle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workingDirectory);
try
{
    var client = new LengthStopClient();
    var agent = new PiAgent(client, new CodingTools(workingDirectory), noTools: true, maxOutputTokens: 100);
    var conversation = new ConversationSession(workingDirectory, "fixture", null);
    conversation.Append(new ChatMessage(ChatRole.User, "first"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
    conversation.Append(new ChatMessage(ChatRole.User, "second"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
    var run = await ConversationRun.OpenAsync(agent, conversation,
        autoCompaction: new AutoCompactionPolicy(50_000, 10_000, KeepRecentTokens: 1));
    var events = new List<AgentLifecycleEvent>();
    await foreach (var item in run.RunEventsAsync("continue")) events.Add(item);

    var result = new
    {
        scenario = "C5.recoverable-length-stop-retries-once",
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

sealed class LengthStopClient : IChatClient
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
        if (Requests.Count == 1)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial output");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 80, OutputTokenCount = 16, TotalTokenCount = 96 })])
            {
                FinishReason = ChatFinishReason.Length
            };
        }
        else
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued response");
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 60, OutputTokenCount = 12, TotalTokenCount = 72 })])
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
        contentTypes = message.Contents.Select(content => content.GetType().Name)
    };
}
