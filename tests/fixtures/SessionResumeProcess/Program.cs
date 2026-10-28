using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

if (args.Length != 3)
    return 2;

var mode = args[0];
var workingDirectory = Path.GetFullPath(args[1]);
var sessionPath = Path.GetFullPath(args[2]);
Directory.CreateDirectory(workingDirectory);
var store = new ConversationStore(workingDirectory, Path.GetDirectoryName(sessionPath));

if (mode == "create")
{
    var conversation = new ConversationSession(workingDirectory, "session-resume-process", null);
    conversation.Append(new ChatMessage(ChatRole.User, "seed question"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "seed answer"));
    var provider = new DeterministicClient();
    var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
        retryPolicy: ProviderRetryPolicy.None), conversation,
        save: cancellationToken => store.SaveAsync(conversation, sessionPath, cancellationToken));
    var events = new List<AgentLifecycleEvent>();
    await foreach (var item in run.RunCustomMessageEventsAsync(
                       new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                           "native-background", "extension work completed", Display: false),
                       "/extensions/session-resume.cs"))
        events.Add(item);

    if (!events.Any(item => item.Type == "turn_completed") || run.IsActive ||
        conversation.Tree.ActivePath().Count(node => node.Type == "custom_message") != 1)
        return 3;

    var plan = conversation.PrepareCompaction(keepRecentTokens: 1);
    if (plan is null) return 4;
    conversation.AppendCompaction(plan, "The earlier seed exchange was completed.", keepRecentTokens: 1);
    await store.SaveAsync(conversation, sessionPath);
    Console.WriteLine("custom-turn-saved-and-compacted");
    return 0;
}

if (mode == "resume")
{
    var conversation = await store.LoadAsync(sessionPath);
    var customEntry = conversation.Tree.ActivePath().Single(node => node.Type == "custom_message");
    if (customEntry.Payload.GetProperty("piOriginalEntry").GetProperty("customType").GetString() !=
        "native-background")
        return 5;

    var provider = new DeterministicClient();
    var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
        retryPolicy: ProviderRetryPolicy.None), conversation,
        save: cancellationToken => store.SaveAsync(conversation, sessionPath, cancellationToken));
    if (provider.Requests.Count != 0 || run.IsActive) return 6;

    var events = new List<AgentLifecycleEvent>();
    await foreach (var item in run.RunEventsAsync("ordinary follow-up")) events.Add(item);
    if (!events.Any(item => item.Type == "turn_completed") || run.IsActive || provider.Requests.Count != 1)
        return 7;
    var request = provider.Requests[0];
    if (!request.Any(message => message.Text.Contains("earlier seed exchange was completed", StringComparison.Ordinal)) ||
        !request.Any(message => message.Text == "ordinary follow-up") ||
        conversation.Tree.ActivePath().Count(node => node.Type == "custom_message") != 1)
        return 8;

    Console.WriteLine("resumed-without-trigger-replay");
    return 0;
}

if (mode == "pause-after-intent")
{
    var conversation = new ConversationSession(workingDirectory, "session-resume-process", null);
    var provider = new ToolCallClient(withToolCall: true);
    async Task SaveAndPause(CancellationToken cancellationToken)
    {
        await store.SaveAsync(conversation, sessionPath, cancellationToken);
        if (conversation.Tree.ActivePath().LastOrDefault()?.Type != "tool_intent") return;
        await File.WriteAllTextAsync(sessionPath + ".intent-ready", "intent persisted");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
        retryPolicy: ProviderRetryPolicy.None), conversation, save: SaveAndPause);
    await foreach (var _ in run.RunStreamingAsync("write once")) { }
    return 10;
}

if (mode == "pause-after-compaction-save")
{
    var conversation = new ConversationSession(workingDirectory, "session-resume-process", null);
    conversation.Append(new ChatMessage(ChatRole.User, "first compaction question"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "first compaction answer"));
    conversation.Append(new ChatMessage(ChatRole.User, "retained compaction question"));
    conversation.Append(new ChatMessage(ChatRole.Assistant, "retained compaction answer"));
    await store.SaveAsync(conversation, sessionPath);

    async Task SaveAndPause(CancellationToken cancellationToken)
    {
        await store.SaveAsync(conversation, sessionPath, cancellationToken);
        if (!conversation.Tree.ActivePath().Any(node => node.Type == "compaction")) return;
        await File.WriteAllTextAsync(sessionPath + ".compaction-ready", "compaction saved before publication");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    var run = await ConversationRun.OpenAsync(new PiAgent(new DeterministicClient(), new CodingTools(workingDirectory),
        retryPolicy: ProviderRetryPolicy.None), conversation, save: SaveAndPause, keepRecentTokens: 1,
        sessionFile: sessionPath);
    await run.CompactWithResultAsync();
    return 13;
}

if (mode == "recover-no-replay")
{
    var conversation = await store.LoadAsync(sessionPath);
    var provider = new ToolCallClient(withToolCall: false);
    var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
        retryPolicy: ProviderRetryPolicy.None), conversation,
        save: cancellationToken => store.SaveAsync(conversation, sessionPath, cancellationToken));
    await foreach (var _ in run.RunStreamingAsync("report recovery")) { }
    if (provider.ToolCalls != 0 || File.Exists(Path.Combine(workingDirectory, "tool-result.txt"))) return 11;
    if (!conversation.ActiveMessages().Any(message => message.Text?.Contains("Outcome UNKNOWN for write", StringComparison.Ordinal) == true))
        return 12;
    Console.WriteLine("recovered-no-replay");
    return 0;
}

return 9;

internal sealed class DeterministicClient : IChatClient
{
    public List<ChatMessage[]> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "process compaction summary")]));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(messages.ToArray());
        yield return new ChatResponseUpdate(ChatRole.Assistant, "deterministic response");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class ToolCallClient(bool withToolCall) : IChatClient
{
    public int ToolCalls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (withToolCall)
        {
            ToolCalls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-before-invocation", "write",
                    new Dictionary<string, object?> { ["path"] = "tool-result.txt", ["content"] = "should not be written" })]);
        }
        else yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered safely");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
