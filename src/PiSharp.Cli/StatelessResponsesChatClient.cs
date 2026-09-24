using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

/// <summary>Keep canonical MAF history authoritative rather than switching to provider-owned response IDs.</summary>
internal sealed class StatelessResponsesChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        response.ConversationId = null;
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            update.ConversationId = null;
            yield return update;
        }
    }
}
