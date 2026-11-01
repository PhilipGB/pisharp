using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

public sealed partial class ConversationRun
{
    /// <summary>Queue steering input with its already expanded display text.</summary>
    public bool TrySteer(ChatMessage message, string promptText) =>
        _promptDelivery.TryQueueActive(message, promptText, steering: true, "steering");

    /// <summary>Queue follow-up input with its already expanded display text.</summary>
    public bool TryFollowUp(ChatMessage message, string promptText) =>
        _promptDelivery.TryQueueActive(message, promptText, steering: false, "follow_up");

    /// <summary>Queue an RPC input message while preserving transformed image content.</summary>
    public void QueueRpcInput(ChatMessage message, string promptText, bool steering,
        Action<AgentLifecycleEvent> publishQueueUpdate) =>
        _promptDelivery.QueueRpcInput(message, promptText, steering, publishQueueUpdate);

    /// <summary>
    /// Queue an RPC input message once every tool that already finished its work has published its
    /// completion, so the accepted steer is never observed before it (see ToolCompletionGate).
    /// </summary>
    public async Task QueueRpcInputAsync(ChatMessage message, string promptText, bool steering,
        Action<AgentLifecycleEvent> publishQueueUpdate)
    {
        await _agent.ToolCompletions.WaitForIdleAsync().ConfigureAwait(false);
        await WaitForEventDrainAsync().ConfigureAwait(false);
        _promptDelivery.QueueRpcInput(message, promptText, steering, publishQueueUpdate);
    }
}
