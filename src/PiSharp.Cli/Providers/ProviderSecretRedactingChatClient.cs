using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

internal sealed class ProviderSecretRedactingChatClient(IChatClient inner, string? secret)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try { return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ThrowIfSecretAppears(error);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken);
        await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try { hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                ThrowIfSecretAppears(error);
                throw;
            }
            if (!hasNext) yield break;
            yield return enumerator.Current;
        }
    }

    private void ThrowIfSecretAppears(Exception error)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        var safe = SecretRedactor.Redact(error.Message, secret);
        if (!string.Equals(safe, error.Message, StringComparison.Ordinal))
            throw new InvalidOperationException(safe);
    }
}
