using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;

namespace PiSharp.Runtime.Sessions;

/// <summary>Compacts the canonical branch for a chosen physical route before dispatch, without rerouting.</summary>
internal sealed class VirtualModelContextCoordinator(ConversationSession conversation, PiAgent agent,
    Func<PiSharpExtensionCompactionReason, bool, CancellationToken, Task<ConversationCompactionResult?>> compact,
    Func<bool> compactionFromExtension, Func<bool> compactionCancelledByExtension,
    Func<bool> autoCompactionEnabled, Action<AgentLifecycleEvent> publish, Action beforeCompaction)
{
    public bool HasCompacted { get; private set; }

    public async Task<IReadOnlyList<ChatMessage>> PrepareAsync(IReadOnlyList<ChatMessage> messages,
        VirtualModelRequestRoute route, bool force, CancellationToken cancellationToken)
    {
        var request = HasCompacted ? Project(messages) : messages;
        if (!autoCompactionEnabled() || route.ContextPolicy is not { TriggerTokens: > 0 } policy ||
            !force && AutoCompactionPolicy.Estimate(request, "") <= policy.TriggerTokens) return request;
        var reason = force ? PiSharpExtensionCompactionReason.Overflow : PiSharpExtensionCompactionReason.Threshold;
        publish(new("compaction_start") { CompactionReason = force ? "overflow" : "threshold" });
        var compactionCompleted = false;
        var completionPublished = false;
        ConversationCompactionResult? compactedResult = null;
        try
        {
            beforeCompaction();
            var result = await compact(reason, force, cancellationToken).ConfigureAwait(false);
            if (result is null) throw new InvalidOperationException("Physical model context exceeds its budget and no safe compaction boundary is available.");
            HasCompacted = true;
            compactedResult = result;
            compactionCompleted = true;
            await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(agent, conversation, result,
                reason, willRetry: force, cancellationToken).ConfigureAwait(false);
            publish(new("compaction_end")
            {
                CompactionReason = force ? "overflow" : "threshold",
                CompactionResult = result,
                CompactionWillRetry = force
            });
            completionPublished = true;
            request = Project(messages);
            if (AutoCompactionPolicy.Estimate(request, "") > policy.TriggerTokens)
                throw new InvalidOperationException("Physical model context still exceeds its budget after compaction.");
            return request;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!compactionCompleted)
            {
                publish(new("compaction_end") { CompactionReason = force ? "overflow" : "threshold", CompactionAborted = true });
                await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(agent,
                    reason, compactionFromExtension())
                    .ConfigureAwait(false);
            }
            else if (!completionPublished)
            {
                publish(new("compaction_end")
                {
                    CompactionReason = force ? "overflow" : "threshold",
                    CompactionResult = compactedResult,
                    CompactionWillRetry = force
                });
            }
            throw;
        }
        catch (Exception error)
        {
            if (!compactionCompleted)
            {
                var abortedByExtension = compactionCancelledByExtension();
                if (abortedByExtension)
                {
                    publish(new("compaction_end")
                    {
                        CompactionReason = force ? "overflow" : "threshold",
                        CompactionAborted = true
                    });
                    await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(agent, reason)
                        .ConfigureAwait(false);
                }
                else
                {
                    var errorMessage = (force ? "Context overflow recovery failed: " : "Auto-compaction failed: ") + error.Message;
                    publish(new("compaction_end", Error: errorMessage) { CompactionReason = force ? "overflow" : "threshold" });
                    await NativeExtensionCompactionEventPublisher.PublishFailedAsync(agent, reason,
                        willRetry: false, aborted: false, errorMessage, cancellationToken,
                        fromExtension: compactionFromExtension()).ConfigureAwait(false);
                }
            }
            else if (!completionPublished)
            {
                publish(new("compaction_end")
                {
                    CompactionReason = force ? "overflow" : "threshold",
                    CompactionResult = compactedResult,
                    CompactionWillRetry = force
                });
            }
            throw;
        }
    }

    private IReadOnlyList<ChatMessage> Project(IReadOnlyList<ChatMessage> messages) =>
        messages.TakeWhile(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer"))
            .Concat(conversation.ContextMessages()).ToArray();
}
