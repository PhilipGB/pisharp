using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Compacts the canonical branch for a chosen physical route before dispatch, without rerouting.</summary>
internal sealed class VirtualModelContextCoordinator(ConversationSession conversation,
    Func<CancellationToken, Task<ConversationCompactionResult?>> compact,
    Func<bool> autoCompactionEnabled, Action<AgentLifecycleEvent> publish, Action beforeCompaction)
{
    public bool HasCompacted { get; private set; }

    public async Task<IReadOnlyList<ChatMessage>> PrepareAsync(IReadOnlyList<ChatMessage> messages,
        VirtualModelRequestRoute route, bool force, CancellationToken cancellationToken)
    {
        var request = HasCompacted ? Project(messages) : messages;
        if (!autoCompactionEnabled() || route.ContextPolicy is not { TriggerTokens: > 0 } policy ||
            !force && AutoCompactionPolicy.Estimate(request, "") <= policy.TriggerTokens) return request;
        publish(new("compaction_start") { CompactionReason = force ? "overflow" : "threshold" });
        try
        {
            beforeCompaction();
            var result = await compact(cancellationToken).ConfigureAwait(false);
            if (result is null) throw new InvalidOperationException("Physical model context exceeds its budget and no safe compaction boundary is available.");
            HasCompacted = true;
            request = Project(messages);
            if (AutoCompactionPolicy.Estimate(request, "") > policy.TriggerTokens)
                throw new InvalidOperationException("Physical model context still exceeds its budget after compaction.");
            publish(new("compaction_end") { CompactionReason = force ? "overflow" : "threshold", CompactionResult = result });
            return request;
        }
        catch (OperationCanceledException)
        {
            publish(new("compaction_end") { CompactionReason = force ? "overflow" : "threshold", CompactionAborted = true });
            throw;
        }
        catch (Exception error)
        {
            publish(new("compaction_end", Error: error.Message) { CompactionReason = force ? "overflow" : "threshold" });
            throw;
        }
    }

    private IReadOnlyList<ChatMessage> Project(IReadOnlyList<ChatMessage> messages) =>
        messages.TakeWhile(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer"))
            .Concat(conversation.ContextMessages()).ToArray();
}
