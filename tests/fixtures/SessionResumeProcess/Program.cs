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

return 9;

internal sealed class DeterministicClient : IChatClient
{
    public List<ChatMessage[]> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
