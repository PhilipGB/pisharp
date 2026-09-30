using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

var flags = JsonSerializer.Deserialize<bool[][]>(args[0])!;
var root = Path.Combine(Path.GetTempPath(), "pisharp-tool-batch-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var counts = new int[2];
    var tools = Enumerable.Range(0, 2).Select(index => new PiSharpToolRegistration(
        AIFunctionFactory.Create(() => new PiSharpToolResult("result " + index,
            Terminate: flags[Interlocked.Increment(ref counts[index]) - 1][index]), name: "tool" + index), ToolExposure.ModelOnly)).ToArray();
    using var client = new BatchClient(flags.Length);
    var conversation = new ConversationSession(root, "fixture", null);
    var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noBuiltinTools: true,
        extensionToolRegistrations: tools), conversation, save: _ => Task.CompletedTask);
    var results = new List<object>();
    await foreach (var item in run.RunEventsAsync("batch"))
        if (item.Type == "tool_execution_finished") results.Add(new { name = item.Tool, terminate = item.Terminate ?? false, text = item.Text });
    Console.WriteLine(JsonSerializer.Serialize(new { requests = client.Requests, results }));
}
finally { Directory.Delete(root, recursive: true); }

sealed class BatchClient(int batches) : IChatClient
{
    public int Requests { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = Requests++;
        if (request < batches)
            yield return new(ChatRole.Assistant, Enumerable.Range(0, 2).Select(index => (AIContent)new FunctionCallContent(
                "call" + request + "-" + index, "tool" + index, new Dictionary<string, object?>())).ToArray());
        else yield return new(ChatRole.Assistant, "done");
        await Task.CompletedTask;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
