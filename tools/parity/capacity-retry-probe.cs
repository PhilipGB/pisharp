using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

var results = new List<object>();
foreach (var scenario in JsonSerializer.Deserialize<JsonElement[]>(args[0])!)
{
    var client = new RecordingClient(scenario.GetProperty("message").GetString()!, scenario.GetProperty("errors").GetInt32());
    var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noTools: true, retryPolicy: ProviderRetryPolicy.None);
    var cancel = scenario.GetProperty("cancel").GetBoolean();
    var delay = TimeSpan.FromMilliseconds(cancel ? 100 : 0);
    var run = await ConversationRun.OpenAsync(agent, new ConversationSession(Path.GetTempPath(), "fixture", null),
        retryPolicy: new AgentRunRetryPolicy(scenario.GetProperty("enabled").GetBoolean(),
            scenario.GetProperty("retries").GetInt32(), delay, delay));
    var events = new List<Dictionary<string, object>>();
    var completed = false;
    await foreach (var item in run.RunEventsAsync("go"))
    {
        if (item.Type == "turn_completed") completed = true;
        if (item.Type == "auto_retry_start")
        {
            events.Add(new() { ["type"] = item.Type, ["attempt"] = item.RetryAttempt!,
                ["maxAttempts"] = item.RetryMaxAttempts!, ["delayMs"] = item.RetryDelayMs!, ["errorMessage"] = item.Error! });
            if (cancel) run.AbortRetry();
        }
        if (item.Type == "auto_retry_end")
        {
            var entry = new Dictionary<string, object> { ["type"] = item.Type,
                ["attempt"] = item.RetryAttempt!, ["success"] = item.RetrySuccess! };
            if (item.RetryFinalError is { } error) entry["finalError"] = error;
            events.Add(entry);
        }
    }
    results.Add(new { id = scenario.GetProperty("id").GetString(), requests = client.Contexts.Count,
        events, contexts = client.Contexts, completed, isRetrying = run.IsRetrying });
}
Console.WriteLine(JsonSerializer.Serialize(results));

sealed class RecordingClient(string message, int errors) : IChatClient
{
    public List<object[]> Contexts { get; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Contexts.Add(messages.Where(item => item.Role == ChatRole.User || item.Role == ChatRole.Assistant)
            .Select(item => (object)new { role = item.Role.Value, text = item.Text }).ToArray());
        if (Contexts.Count <= errors) throw new IOException(message);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        await Task.CompletedTask;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
