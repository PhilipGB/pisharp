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
        var details = result.FromExtension
            ? result.ExtensionDetails
            : JsonSerializer.SerializeToElement(result.Details);
        var projection = new PiSharpExtensionCompactionResult(result.Summary, result.FirstKeptEntryId,
            result.TokensBefore, result.EstimatedTokensAfter,
            details,
            result.Usage is null ? null : JsonSerializer.SerializeToElement(result.Usage));
        return agent.ExtensionEvents.DispatchSessionCompactAsync(new(projection, entry.Id, entry.Timestamp,
            reason, result.FromExtension, willRetry), cancellationToken);
    }

    public static Task PublishFailedAsync(PiAgent agent, PiSharpExtensionCompactionReason reason,
        bool willRetry, bool aborted, string? errorMessage, CancellationToken cancellationToken,
        bool fromExtension = false)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.ExtensionEvents.DispatchSessionCompactFailedAsync(new(reason, willRetry, aborted,
            aborted ? null : errorMessage, fromExtension), cancellationToken);
    }

    public static async Task PublishAbortedAsync(PiAgent agent, PiSharpExtensionCompactionReason reason,
        bool fromExtension = false)
    {
        using var observerTimeout = new CancellationTokenSource(AbortedObserverTimeout);
        try
        {
            await PublishFailedAsync(agent, reason, willRetry: false, aborted: true, errorMessage: null,
                observerTimeout.Token, fromExtension).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (observerTimeout.IsCancellationRequested)
        {
            // Cancellation cleanup must not wait indefinitely for a terminal extension observer.
        }
    }

    public static async Task PublishFailedWithinDeadlineAsync(PiAgent agent,
        PiSharpExtensionCompactionReason reason, bool willRetry, string? errorMessage, bool fromExtension = false)
    {
        using var observerTimeout = new CancellationTokenSource(AbortedObserverTimeout);
        try
        {
            await PublishFailedAsync(agent, reason, willRetry, aborted: false, errorMessage,
                observerTimeout.Token, fromExtension).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (observerTimeout.IsCancellationRequested)
        {
            // A prior failure remains reportable, but its observer cannot delay cancellation cleanup.
        }
    }
}
