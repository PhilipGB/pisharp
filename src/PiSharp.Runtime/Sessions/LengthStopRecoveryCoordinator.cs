using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Sessions;

/// <summary>Recovers one eligible length-stopped provider response through the existing compaction path.</summary>
internal sealed class LengthStopRecoveryCoordinator(
    ConversationSession conversation,
    PiAgent agent,
    ConversationCompactionCoordinator compactionCoordinator,
    Func<bool> isEnabled,
    Func<(string Model, string? Provider)> getCurrentModel,
    Func<string> getUnavailableError,
    int? keepRecentTokens,
    Func<string?, int, Action<AgentLifecycleEvent>, CancellationToken, Task> omitFailedContext,
    Func<CancellationToken, Task> restoreExecution)
{
    public async Task<bool> TryRecoverAsync(AgentLifecycleEvent response,
        bool recoveryAttempted, string? attemptStartHead, int attemptStartPathLength,
        Action<AgentLifecycleEvent> publish, CancellationToken cancellationToken)
    {
        if (!IsRecoverable(response)) return false;
        const string repeatedRecoveryError = "Truncated response recovery failed after one compact-and-retry attempt.";
        var reason = NativeExtensions.PiSharpExtensionCompactionReason.Overflow;
        if (recoveryAttempted)
        {
            publish(new AgentLifecycleEvent("compaction_end", Error: repeatedRecoveryError)
            {
                CompactionReason = "overflow",
                CompactionWillRetry = false
            });
            await NativeExtensionCompactionEventPublisher.PublishFailedAsync(agent, reason,
                willRetry: false, aborted: false, errorMessage: repeatedRecoveryError,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (!isEnabled()) return false;

        ConversationCompactionResult? result = null;
        var started = false;
        try
        {
            await omitFailedContext(attemptStartHead, attemptStartPathLength, publish,
                cancellationToken).ConfigureAwait(false);
            if (ConversationCompactionCoordinator.PreparePlan(conversation, keepRecentTokens) is null)
                return false;

            publish(new AgentLifecycleEvent("compaction_start") { CompactionReason = "overflow" });
            started = true;
            result = await compactionCoordinator.CompactAsync(null, cancellationToken,
                adoptExecutionHistory: false, reason, willRetry: true).ConfigureAwait(false);
            if (result is null)
            {
                var error = "Context overflow recovery failed: " + getUnavailableError();
                publish(new AgentLifecycleEvent("compaction_end", Error: error)
                {
                    CompactionReason = "overflow",
                    CompactionWillRetry = false
                });
                await NativeExtensionCompactionEventPublisher.PublishFailedAsync(agent, reason,
                    willRetry: false, aborted: false, errorMessage: error,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return false;
            }

            await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(agent, conversation, result,
                reason, willRetry: true, cancellationToken).ConfigureAwait(false);
            await restoreExecution(cancellationToken).ConfigureAwait(false);
            publish(new AgentLifecycleEvent("compaction_end")
            {
                CompactionReason = "overflow",
                CompactionResult = result,
                CompactionWillRetry = true
            });
            publish(new AgentLifecycleEvent("context_compacted",
                Text: "Automatic context summary saved; raw history retained."));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (started)
            {
                publish(result is null
                    ? new AgentLifecycleEvent("compaction_end")
                    {
                        CompactionReason = "overflow",
                        CompactionAborted = true,
                        CompactionWillRetry = false
                    }
                    : new AgentLifecycleEvent("compaction_end")
                    {
                        CompactionReason = "overflow",
                        CompactionResult = result,
                        CompactionWillRetry = false
                    });
            }
            if (result is null)
                await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(agent, reason,
                    compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (started)
            {
                var abortedByExtension = result is null && compactionCoordinator.LastCompactionCancelledByExtension;
                var message = result is null ? "Context overflow recovery failed: " + error.Message : null;
                publish(abortedByExtension
                    ? new AgentLifecycleEvent("compaction_end")
                    {
                        CompactionReason = "overflow",
                        CompactionAborted = true,
                        CompactionWillRetry = false
                    }
                    : message is null
                        ? new AgentLifecycleEvent("compaction_end")
                        {
                            CompactionReason = "overflow",
                            CompactionResult = result,
                            CompactionWillRetry = false
                        }
                        : new AgentLifecycleEvent("compaction_end", Error: message)
                        {
                            CompactionReason = "overflow",
                            CompactionWillRetry = false
                        });
                if (result is null)
                {
                    if (abortedByExtension)
                        await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(agent, reason,
                            compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                    else
                        await NativeExtensionCompactionEventPublisher.PublishFailedWithinDeadlineAsync(agent, reason,
                            willRetry: false, errorMessage: message!,
                            fromExtension: compactionCoordinator.LastCompactionFromExtension).ConfigureAwait(false);
                }
            }
            return false;
        }
    }

    private bool IsRecoverable(AgentLifecycleEvent response)
        => IsRecoverable(conversation, response, getCurrentModel);

    internal static bool IsRecoverable(ConversationSession conversation, AgentLifecycleEvent? response,
        Func<(string Model, string? Provider)> getCurrentModel)
    {
        if (response is null ||
            !string.Equals(response.ProviderFinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
            response.ProviderDesiredMaxOutputTokens is not > 0 ||
            response.ProviderUsage?.OutputTokenCount is not { } outputTokens)
            return false;
        if (outputTokens >= response.ProviderDesiredMaxOutputTokens.Value ||
            response.ProviderRequestedModelId is not { } requestedModel)
            return false;

        var currentModel = getCurrentModel();
        if (!string.Equals(requestedModel, currentModel.Model, StringComparison.Ordinal) ||
            !string.Equals(response.ProviderRequestedProviderId, currentModel.Provider, StringComparison.Ordinal))
            return false;

        if (response.ProviderResponse is not { } providerResponse || providerResponse.Role != ChatRole.Assistant)
            return false;
        var path = conversation.Tree.ActivePath();
        var assistantIndex = path.ToList().FindLastIndex(node => node.Type == "chat" &&
            ProviderTurnHistoryReconciler.AreEquivalent(providerResponse, ConversationMessageCodec.RestoreEntry(node)));
        var latestCompactionIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        return assistantIndex > latestCompactionIndex &&
            ConversationContextProjection.IsProjectedMessage(path, path[assistantIndex].Id, ChatRole.Assistant);
    }
}
