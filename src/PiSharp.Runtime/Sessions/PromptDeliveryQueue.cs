namespace PiSharp.Runtime.Sessions;

internal sealed record QueuedPromptBatch(bool IsSteering, IReadOnlyList<string> Messages);

internal sealed class PromptDeliveryQueue
{
    private readonly Queue<string> _steering = new();
    private readonly Queue<string> _followUp = new();

    public PromptDeliveryMode SteeringMode { get; private set; } = PromptDeliveryMode.OneAtATime;
    public PromptDeliveryMode FollowUpMode { get; private set; } = PromptDeliveryMode.OneAtATime;
    public int Count => _steering.Count + _followUp.Count;

    public void SetSteeringMode(PromptDeliveryMode mode) => SteeringMode = Validate(mode);

    public void SetFollowUpMode(PromptDeliveryMode mode) => FollowUpMode = Validate(mode);

    public void Enqueue(string prompt, bool steering)
    {
        (steering ? _steering : _followUp).Enqueue(prompt);
    }

    public PendingPrompts Snapshot() => new(_steering.ToArray(), _followUp.ToArray());

    public PendingPrompts Clear()
    {
        var pending = Snapshot();
        _steering.Clear();
        _followUp.Clear();
        return pending;
    }

    public IReadOnlyList<string> TakeSteeringForProvider() => Take(_steering, SteeringMode);

    public QueuedPromptBatch? TakeNextBatch()
    {
        if (_steering.Count > 0) return new(true, Take(_steering, SteeringMode));
        if (_followUp.Count > 0) return new(false, Take(_followUp, FollowUpMode));
        return null;
    }

    private static IReadOnlyList<string> Take(Queue<string> queue, PromptDeliveryMode mode)
    {
        if (queue.Count == 0) return [];
        var count = mode == PromptDeliveryMode.All ? queue.Count : 1;
        var messages = new string[count];
        for (var index = 0; index < count; index++) messages[index] = queue.Dequeue();
        return messages;
    }

    private static PromptDeliveryMode Validate(PromptDeliveryMode mode) => Enum.IsDefined(mode)
        ? mode
        : throw new ArgumentOutOfRangeException(nameof(mode));
}
