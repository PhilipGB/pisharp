using System.Runtime.CompilerServices;
using System.Reflection;
using Microsoft.Agents.AI;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

const string search = "mcp__docs__search";
var results = new List<object>();
foreach (var id in JsonSerializer.Deserialize<string[]>(args[0])!)
{
    var firstRegistration = Registration(true);
    var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
    var first = await ConversationRun.OpenAsync(Agent(firstRegistration, new RecordingClient()), conversation,
        activeToolNamesOverride: ["base", search]);
    await foreach (var _ in first.RunEventsAsync("seed")) { }
    var registration = Registration(false);
    var client = new RecordingClient();
    var agent = Agent(registration, client, id);
    var run = await ConversationRun.OpenAsync(agent, conversation,
        activeToolNamesOverride: id == "reload" ? first.ActiveToolNames : null);
    var execution = (AgentSession)typeof(ConversationRun).GetField("_execution", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run)!;
    var before = run.ActiveToolNames.ToArray();
    if (id == "additive") agent.GetToolLoadout(execution).SetActiveTools(["base", "added"]);
    if (id == "replacement") agent.GetToolLoadout(execution).SetActiveTools(["added"]);
    if (id is "prompt" or "cancel")
    {
        using var cancellation = new CancellationTokenSource();
        if (id == "cancel") client.OnRequest = cancellation.Cancel;
        await foreach (var _ in run.RunEventsAsync("go", cancellation.Token)) { }
        client.OnRequest = null;
    }
    registration.AddTool(Tool(search, true));
    var registered = run.ActiveToolNames.ToArray();
    await foreach (var _ in run.RunEventsAsync("go")) { }
    results.Add(new { id, before, registered, requests = client.Requests, persisted = conversation.ActiveToolLoadout() });
}
Console.WriteLine(JsonSerializer.Serialize(results));

static PiSharpToolRegistration Tool(string name, bool deferred = false, bool defaultActive = true) =>
    new(AIFunctionFactory.Create(() => "found", name: name),
        deferred ? ToolExposure.Deferred : ToolExposure.Direct, DefaultActive: defaultActive);
static ExtensionRegistration Registration(bool search)
{
    var registration = new ExtensionRegistration();
    registration.AddTool(Tool("base"));
    registration.AddTool(Tool("added", defaultActive: false));
    if (search) registration.AddTool(Tool("mcp__docs__search", true));
    return registration;
}
static PiAgent Agent(ExtensionRegistration registration, IChatClient client, string? id = null) =>
    new(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
        extensionToolRegistrations: registration.ToolDefinitions, liveExtensionRegistration: registration,
        excludedTools: id == "excluded" ? ["mcp__docs__search"] : null,
        selectedTools: id == "allowed" ? ["base"] : null);

sealed class RecordingClient : IChatClient
{
    public List<string[]> Requests { get; } = [];
    public Action? OnRequest { get; set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray() ?? []);
        OnRequest?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        await Task.CompletedTask;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
