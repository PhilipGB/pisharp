using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

/// <summary>
/// Injects queued steering messages at the next provider request. MAF owns the
/// function-invocation loop; this adapter adds Pi's product-level queue semantics
/// at the boundary that is called once per model/tool iteration.
/// </summary>
internal sealed class SteeringChatClient : DelegatingChatClient
{
    private readonly TurnMessageQueue _queue;
    private readonly Func<string, string> _expandMessage;

    public SteeringChatClient(
        IChatClient innerClient,
        TurnMessageQueue queue,
        Func<string, string>? expandMessage = null)
        : base(innerClient)
    {
        _queue = queue;
        _expandMessage = expandMessage ?? (message => message);
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(InjectSteering(messages), options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetStreamingResponseAsync(InjectSteering(messages), options, cancellationToken);
    }

    private IReadOnlyList<ChatMessage> InjectSteering(IEnumerable<ChatMessage> messages)
    {
        var pending = _queue.DrainSteering();
        if (pending.Count == 0)
        {
            return messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        }

        var enriched = messages.ToList();
        enriched.AddRange(pending.Select(message =>
            new ChatMessage(ChatRole.User, _expandMessage(message.Text))));
        return enriched;
    }
}
