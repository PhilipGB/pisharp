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
}
