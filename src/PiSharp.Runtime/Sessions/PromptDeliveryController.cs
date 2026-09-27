using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal sealed class PromptDeliveryController
{
    private readonly object _stateGate;
    private readonly PromptDeliveryQueue _queue = new();
    private object? _owner;
    private Action<AgentLifecycleEvent>? _events;
    private int _initialSteeringCount;

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
        lock (_stateGate)
        {
            if (_owner is null) return false;
            _queue.Enqueue(prompt, steering);
            _events?.Invoke(new("prompt_queued", Text: prompt, Tool: kind));
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

    public PendingPrompts Clear(Action<AgentLifecycleEvent>? publishQueueUpdate = null)
    {
        lock (_stateGate)
        {
            var pending = _queue.Clear();
            _initialSteeringCount = 0;
            if (publishQueueUpdate is not null) publishQueueUpdate(QueueEvent(PendingPrompts.Empty));
            else _events?.Invoke(QueueEvent(PendingPrompts.Empty));
            return pending;
        }
    }

    public object Begin(Action<AgentLifecycleEvent> publish)
    {
        lock (_stateGate)
        {
            if (_owner is not null) throw new InvalidOperationException("An agent run is already active.");
            _owner = new object();
            _events = publish;
            _initialSteeringCount = _queue.SteeringCount;
            return _owner;
        }
    }

    public IReadOnlyList<ChatMessage> TakeSteeringForProvider(bool firstProviderRequest)
    {
        lock (_stateGate)
        {
            var prompts = firstProviderRequest
                ? TakeInitialSteering()
                : _queue.TakeSteeringForProvider();
            if (prompts.Count == 0) return [];
            _events?.Invoke(QueueEvent(_queue.Snapshot()));
            var messages = new ChatMessage[prompts.Count];
            for (var index = 0; index < prompts.Count; index++)
            {
                var message = new ChatMessage(ChatRole.User, prompts[index]);
                messages[index] = message;
                _events?.Invoke(new AgentLifecycleEvent("steering_message_accepted", Text: prompts[index])
                {
                    PromptMessage = message,
                    MessageTimestamp = DateTimeOffset.UtcNow
                });
            }
            return messages;
        }
    }

    public QueuedPromptBatch? TakeNextBatchOrClose(object owner, Action beforeTake)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_owner, owner)) return null;
            beforeTake();
            if (_queue.TakeNextBatch() is { } batch)
            {
                _events?.Invoke(QueueEvent(_queue.Snapshot()));
                return batch;
            }
            _initialSteeringCount = 0;
            _owner = null;
            _events = null;
            return null;
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
        }
    }

    private static AgentLifecycleEvent QueueEvent(PendingPrompts pending) => new("queue_update",
        Text: System.Text.Json.JsonSerializer.Serialize(new { steering = pending.Steering, followUp = pending.FollowUp }));

    private IReadOnlyList<string> TakeInitialSteering()
    {
        var initialCount = _initialSteeringCount;
        _initialSteeringCount = 0;
        return _queue.TakeSteeringForProvider(initialCount);
    }
}
