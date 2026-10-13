using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

internal sealed class UnroutedVirtualModelChatClient(ModelDescriptor model) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromException<ChatResponse>(Unrouted());

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw Unrouted();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    private InvalidOperationException Unrouted() =>
        new($"Virtual model {model.Provider}/{model.Id} must be routed before making a provider request.");
}
