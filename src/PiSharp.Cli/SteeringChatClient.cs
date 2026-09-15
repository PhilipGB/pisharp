using System.Runtime.CompilerServices;
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

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var enriched = await InjectSteeringAsync(messages, cancellationToken);
        return await base.GetResponseAsync(enriched, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StreamAsync(messages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enriched = await InjectSteeringAsync(messages, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(enriched, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> InjectSteeringAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var pending = _queue.DrainSteering();
        if (pending.Count == 0)
        {
            return messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        }

        await _queue.NotifyDeliveredAsync(pending, cancellationToken);
        var enriched = messages.ToList();
        enriched.AddRange(pending.Select(message =>
            new ChatMessage(ChatRole.User, _expandMessage(message.Text))));
        return enriched;
    }
}
