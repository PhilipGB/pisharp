using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;

namespace PiSharp.Runtime.Sessions;

internal static class NativeExtensionCompactionEventPublisher
{
    private static readonly TimeSpan AbortedObserverTimeout = TimeSpan.FromMilliseconds(250);

    public static Task PublishCompletedAsync(PiAgent agent, ConversationSession conversation,
        ConversationCompactionResult result, PiSharpExtensionCompactionReason reason, bool willRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(result);
        var entry = conversation.Tree.ActivePath().LastOrDefault(node => node.Type == "compaction") ??
            throw new InvalidOperationException("The completed compaction entry was not persisted on the active conversation branch.");
        var projection = new PiSharpExtensionCompactionResult(result.Summary, result.FirstKeptEntryId,
            result.TokensBefore, result.EstimatedTokensAfter,
            JsonSerializer.SerializeToElement(result.Details),
            result.Usage is null ? null : JsonSerializer.SerializeToElement(result.Usage));
        return agent.ExtensionEvents.DispatchSessionCompactAsync(new(projection, entry.Id, entry.Timestamp,
            reason, FromExtension: false, willRetry), cancellationToken);
    }

    public static Task PublishFailedAsync(PiAgent agent, PiSharpExtensionCompactionReason reason,
        bool willRetry, bool aborted, string? errorMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.ExtensionEvents.DispatchSessionCompactFailedAsync(new(reason, willRetry, aborted,
            aborted ? null : errorMessage, FromExtension: false), cancellationToken);
    }

    public static async Task PublishAbortedAsync(PiAgent agent, PiSharpExtensionCompactionReason reason)
    {
        using var observerTimeout = new CancellationTokenSource(AbortedObserverTimeout);
        try
        {
            await PublishFailedAsync(agent, reason, willRetry: false, aborted: true, errorMessage: null,
                observerTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (observerTimeout.IsCancellationRequested)
        {
            // Cancellation cleanup must not wait indefinitely for a terminal extension observer.
        }
    }
}
