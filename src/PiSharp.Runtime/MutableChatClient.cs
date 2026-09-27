using Microsoft.Extensions.AI;

namespace PiSharp.Runtime;

internal sealed class MutableChatClient(IChatClient client) : DelegatingChatClient(client)
{
    private IChatClient _client = client;

    public void SetClient(IChatClient client) => Interlocked.Exchange(ref _client, client);

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _client).GetResponseAsync(messages, options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _client).GetStreamingResponseAsync(messages, options, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        Volatile.Read(ref _client).GetService(serviceType, serviceKey);
}
