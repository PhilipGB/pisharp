using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Sessions;

internal sealed class PromptDeliveryController
{
    private readonly object _stateGate;
    private readonly PromptDeliveryQueue _queue = new();
    private readonly Queue<QueuedPrompt> _pendingNextTurn = new();
    private object? _owner;
    private object? _idleReservation;
    private Action<AgentLifecycleEvent>? _events;
    private bool _firstProviderRequestPending;

    public PromptDeliveryController(object stateGate, PromptDeliveryMode steeringMode, PromptDeliveryMode followUpMode)
    {
        _stateGate = stateGate;
        _queue.SetSteeringMode(steeringMode);
        _queue.SetFollowUpMode(followUpMode);
    }

    public bool IsActive
    {
        get { lock (_stateGate) return _owner is not null; }
    }

    public int Count
    {
        get { lock (_stateGate) return _queue.Count; }
    }

    public PromptDeliveryMode SteeringMode
    {
        get { lock (_stateGate) return _queue.SteeringMode; }
    }

    public PromptDeliveryMode FollowUpMode
    {
        get { lock (_stateGate) return _queue.FollowUpMode; }
    }

    public void SetSteeringMode(PromptDeliveryMode mode)
    {
        lock (_stateGate) _queue.SetSteeringMode(mode);
    }

    public void SetFollowUpMode(PromptDeliveryMode mode)
    {
        lock (_stateGate) _queue.SetFollowUpMode(mode);
    }

    public bool TryQueueActive(string prompt, bool steering, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return TryQueueActive(new QueuedPrompt(prompt, new ChatMessage(ChatRole.User, prompt)), steering, kind);
    }

    public bool TryQueueActive(ChatMessage message, string prompt, bool steering, string kind,
        bool skipBeforeAgentStart = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return TryQueueActive(new QueuedPrompt(prompt, message, skipBeforeAgentStart), steering, kind);
    }

    public bool TryDeliverExtensionMessage(ChatMessage message, string prompt,
        NativeExtensions.PiSharpMessageDeliveryMode deliveryMode, string kind,
        bool skipBeforeAgentStart = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var queuedPrompt = new QueuedPrompt(prompt, message, skipBeforeAgentStart);
        lock (_stateGate)
        {
            switch (deliveryMode)
            {
                case NativeExtensions.PiSharpMessageDeliveryMode.NextTurn:
                    _pendingNextTurn.Enqueue(queuedPrompt);
                    return true;
                case NativeExtensions.PiSharpMessageDeliveryMode.Steer:
                case NativeExtensions.PiSharpMessageDeliveryMode.FollowUp:
                    if (_owner is null) return false;
                    _queue.Enqueue(queuedPrompt, deliveryMode == NativeExtensions.PiSharpMessageDeliveryMode.Steer);
                    _events?.Invoke(new("prompt_queued", Text: queuedPrompt.Text, Tool: kind));
                    _events?.Invoke(QueueEvent(_queue.Snapshot()));
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(deliveryMode));
            }
        }
    }

    public bool TryReserveIdle(out IDisposable? reservation)
    {
        lock (_stateGate)
        {
            if (_owner is not null || _idleReservation is not null)
            {
                reservation = null;
                return false;
            }

            var token = new object();
            _idleReservation = token;
            reservation = new IdleReservation(this, token);
            return true;
        }
    }

    private bool TryQueueActive(QueuedPrompt prompt, bool steering, string kind)
    {
        lock (_stateGate)
        {
            if (_owner is null) return false;
            _queue.Enqueue(prompt, steering);
            _events?.Invoke(new("prompt_queued", Text: prompt.Text, Tool: kind));
            _events?.Invoke(QueueEvent(_queue.Snapshot()));
            return true;
        }
    }

    public void QueueRpcInput(string prompt, bool steering, Action<AgentLifecycleEvent> publishQueueUpdate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(publishQueueUpdate);
        lock (_stateGate)
        {
            _queue.Enqueue(prompt, steering);
            if (_owner is not null)
                _events?.Invoke(new("prompt_queued", Text: prompt, Tool: steering ? "steering" : "follow_up"));
            publishQueueUpdate(QueueEvent(_queue.Snapshot()));
        }
    }

    public PendingPrompts Snapshot()
    {
        lock (_stateGate) return _queue.Snapshot();
    }

    internal IReadOnlyList<ChatMessage> SnapshotMessages()
    {
        lock (_stateGate) return _queue.SnapshotMessages();
    }

    public PendingPrompts Clear(Action<AgentLifecycleEvent>? publishQueueUpdate = null)
    {
        lock (_stateGate)
        {
            var pending = _queue.Clear();
            if (publishQueueUpdate is not null) publishQueueUpdate(QueueEvent(PendingPrompts.Empty));
            else _events?.Invoke(QueueEvent(PendingPrompts.Empty));
            return pending;
        }
    }

    public object Begin(Action<AgentLifecycleEvent> publish, out IReadOnlyList<QueuedPrompt> nextTurnMessages)
    {
        lock (_stateGate)
        {
            while (_idleReservation is not null) Monitor.Wait(_stateGate);
            if (_owner is not null) throw new InvalidOperationException("An agent run is already active.");
            _owner = new object();
            _events = publish;
            _firstProviderRequestPending = true;
            nextTurnMessages = _pendingNextTurn.ToArray();
            _pendingNextTurn.Clear();
            return _owner;
        }
    }

    public IReadOnlyList<ChatMessage> TakeSteeringForProvider(bool firstProviderRequest, bool contextContinuation = false)
    {
        lock (_stateGate)
        {
            if (firstProviderRequest)
            {
                if (!_firstProviderRequestPending && !contextContinuation) return [];
                _firstProviderRequestPending = false;
            }
            var prompts = _queue.TakeSteeringForProvider();
            if (prompts.Count == 0) return [];
            _events?.Invoke(QueueEvent(_queue.Snapshot()));
            var messages = new ChatMessage[prompts.Count];
            for (var index = 0; index < prompts.Count; index++)
            {
                var prompt = prompts[index];
                messages[index] = prompt.Message;
                _events?.Invoke(new AgentLifecycleEvent("steering_message_accepted", Text: prompt.Text)
                {
                    PromptMessage = prompt.Message,
                    MessageTimestamp = DateTimeOffset.UtcNow
                });
            }
            return messages;
        }
    }

    public PromptDeliveryTransition TakeNextBatchOrClose(object owner, Func<bool> beforeTake, bool closeWhenEmpty = true)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_owner, owner)) return new(null, Closed: true, PendingContextMessagesAppended: false);
            var pendingContextMessagesAppended = beforeTake();
            if (_queue.TakeNextBatch() is { } batch)
            {
                _events?.Invoke(QueueEvent(_queue.Snapshot()));
                return new(batch, Closed: false, pendingContextMessagesAppended);
            }
            if (pendingContextMessagesAppended)
                return new(null, Closed: false, PendingContextMessagesAppended: true);
            if (!closeWhenEmpty) return new(null, Closed: true, PendingContextMessagesAppended: false);
            _owner = null;
            _events = null;
            _firstProviderRequestPending = false;
            return new(null, Closed: true, PendingContextMessagesAppended: false);
        }
    }

    public void End(object owner, Action beforeEnd)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            beforeEnd();
            _owner = null;
            _events = null;
            _firstProviderRequestPending = false;
        }
    }

    private static AgentLifecycleEvent QueueEvent(PendingPrompts pending) => new("queue_update",
        Text: System.Text.Json.JsonSerializer.Serialize(new { steering = pending.Steering, followUp = pending.FollowUp }));

    private void ReleaseIdle(object token)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_idleReservation, token)) return;
            _idleReservation = null;
            Monitor.PulseAll(_stateGate);
        }
    }

    private sealed class IdleReservation(PromptDeliveryController controller, object token) : IDisposable
    {
        private PromptDeliveryController? _controller = controller;

        public void Dispose() => Interlocked.Exchange(ref _controller, null)?.ReleaseIdle(token);
    }
}

internal sealed record PromptDeliveryTransition(QueuedPromptBatch? Batch, bool Closed,
    bool PendingContextMessagesAppended);
