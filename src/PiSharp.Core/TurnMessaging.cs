using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Describes when a queued user message should be delivered.</summary>
public enum QueuedMessageKind
{
    /// <summary>Deliver after the current assistant response and its tool calls, before the next model call.</summary>
    Steering,

    /// <summary>Deliver after the current agent run would otherwise become idle.</summary>
    FollowUp,
}

/// <summary>Controls whether a queue drains one message or all messages at a delivery point.</summary>
public enum QueueDrainMode
{
    /// <summary>Deliver only the oldest pending message.</summary>
    OneAtATime,

    /// <summary>Deliver all pending messages in their enqueue order.</summary>
    All,
}

/// <summary>A user message waiting to be delivered to an active agent run.</summary>
public sealed record QueuedUserMessage(
    long Sequence,
    string Text,
    DateTimeOffset EnqueuedAtUtc,
    QueuedMessageKind Kind);

/// <summary>A consistent view of the pending steering and follow-up queues.</summary>
public sealed record TurnQueueSnapshot(
    IReadOnlyList<QueuedUserMessage> Steering,
    IReadOnlyList<QueuedUserMessage> FollowUp);

/// <summary>Messages removed from both queues by an explicit clear operation.</summary>
public sealed record ClearedTurnQueue(
    IReadOnlyList<QueuedUserMessage> Steering,
    IReadOnlyList<QueuedUserMessage> FollowUp);

/// <summary>
/// Thread-safe queues matching Pi's steering and follow-up delivery semantics.
/// The queue deliberately does not own model execution, so it can be reused by
/// terminal, JSON, and RPC hosts.
/// </summary>
public sealed class TurnMessageQueue
{
    private readonly object _sync = new();
    private readonly Queue<QueuedUserMessage> _steering = new();
    private readonly Queue<QueuedUserMessage> _followUp = new();
    private long _nextSequence;

    /// <summary>Gets or sets how steering messages are drained.</summary>
    public QueueDrainMode SteeringMode { get; set; } = QueueDrainMode.OneAtATime;

    /// <summary>Gets or sets how follow-up messages are drained.</summary>
    public QueueDrainMode FollowUpMode { get; set; } = QueueDrainMode.OneAtATime;

    /// <summary>Queues a message for delivery before the next model call.</summary>
    public QueuedUserMessage EnqueueSteering(string text) => Enqueue(text, QueuedMessageKind.Steering);

    /// <summary>Queues a message for delivery after the current run finishes.</summary>
    public QueuedUserMessage EnqueueFollowUp(string text) => Enqueue(text, QueuedMessageKind.FollowUp);

    /// <summary>Queues a message using the requested delivery semantics.</summary>
    public QueuedUserMessage Enqueue(QueuedMessageKind kind, string text) => Enqueue(text, kind);

    /// <summary>Gets a stable snapshot of all pending messages.</summary>
    public TurnQueueSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new TurnQueueSnapshot(_steering.ToArray(), _followUp.ToArray());
        }
    }

    /// <summary>Removes the next steering batch according to <see cref="SteeringMode"/>.</summary>
    public IReadOnlyList<QueuedUserMessage> DrainSteering() => Drain(_steering, SteeringMode);

    /// <summary>Removes the next follow-up batch according to <see cref="FollowUpMode"/>.</summary>
    public IReadOnlyList<QueuedUserMessage> DrainFollowUp() => Drain(_followUp, FollowUpMode);

    /// <summary>Clears both queues without losing the messages from the returned value.</summary>
    public ClearedTurnQueue Clear()
    {
        lock (_sync)
        {
            var cleared = new ClearedTurnQueue(_steering.ToArray(), _followUp.ToArray());
            _steering.Clear();
            _followUp.Clear();
            return cleared;
        }
    }

    private QueuedUserMessage Enqueue(string text, QueuedMessageKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_sync)
        {
            var message = new QueuedUserMessage(
                ++_nextSequence,
                text,
                DateTimeOffset.UtcNow,
                kind);
            GetQueue(kind).Enqueue(message);
            return message;
        }
    }

    private IReadOnlyList<QueuedUserMessage> Drain(
        Queue<QueuedUserMessage> queue,
        QueueDrainMode mode)
    {
        lock (_sync)
        {
            if (queue.Count == 0)
            {
                return [];
            }

            if (mode == QueueDrainMode.All)
            {
                var all = queue.ToArray();
                queue.Clear();
                return all;
            }

            return [queue.Dequeue()];
        }
    }

    private Queue<QueuedUserMessage> GetQueue(QueuedMessageKind kind) =>
        kind == QueuedMessageKind.Steering ? _steering : _followUp;
}

/// <summary>A durable summary of one tool call and its result.</summary>
public sealed record ToolExecutionRecord(
    string CallId,
    string Name,
    JsonElement Arguments,
    JsonElement? Result,
    bool IsError);

/// <summary>Result returned by one model-backed prompt execution.</summary>
public sealed record TurnExecutionResult(
    string AssistantText,
    bool Cancelled = false,
    IReadOnlyList<ToolExecutionRecord>? ToolExecutions = null)
{
    /// <summary>Gets tool records without requiring nullable checks at call sites.</summary>
    public IReadOnlyList<ToolExecutionRecord> ToolRecords => ToolExecutions ?? [];
}

/// <summary>Aggregated result for an initial prompt and any follow-up prompts it drained.</summary>
public sealed record LiveTurnResult(
    string AssistantText,
    IReadOnlyList<string> DeliveredPrompts,
    bool Cancelled,
    IReadOnlyList<ToolExecutionRecord>? ToolExecutions = null)
{
    /// <summary>Gets tool records without requiring nullable checks at call sites.</summary>
    public IReadOnlyList<ToolExecutionRecord> ToolRecords => ToolExecutions ?? [];
}

/// <summary>
/// Coordinates one active outer agent run. Steering is injected by the model
/// transport, while this type owns the Pi-compatible follow-up loop and abort
/// preservation semantics.
/// </summary>
public sealed class LiveTurnCoordinator
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private bool _running;

    /// <summary>Initializes a coordinator with an independent message queue.</summary>
    public LiveTurnCoordinator(TurnMessageQueue? queue = null)
    {
        Queue = queue ?? new TurnMessageQueue();
    }

    /// <summary>Gets the queue used by this coordinator and its transport adapter.</summary>
    public TurnMessageQueue Queue { get; }

    /// <summary>Gets whether an outer turn is currently executing.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Runs the initial prompt and then drains follow-ups one at a time (or in
    /// batches when configured). Follow-ups remain queued if execution is aborted.
    /// </summary>
    public async Task<LiveTurnResult> RunAsync(
        string initialPrompt,
        Func<string, CancellationToken, Task<TurnExecutionResult>> executeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPrompt);
        ArgumentNullException.ThrowIfNull(executeAsync);
        BeginRun(cancellationToken, out var linkedCancellation);

        var delivered = new List<string> { initialPrompt };
        var assistantText = new List<string>();
        var toolExecutions = new List<ToolExecutionRecord>();
        var cancelled = false;
        var prompts = new List<string> { initialPrompt };
        try
        {
            while (prompts.Count > 0)
            {
                foreach (var prompt in prompts)
                {
                    var result = await executeAsync(prompt, linkedCancellation.Token);
                    assistantText.Add(result.AssistantText);
                    toolExecutions.AddRange(result.ToolRecords);
                    if (result.Cancelled || linkedCancellation.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                }

                if (cancelled)
                {
                    break;
                }

                var steering = Queue.DrainSteering();
                if (steering.Count > 0)
                {
                    prompts = steering.Select(message => message.Text).ToList();
                    delivered.AddRange(prompts);
                    continue;
                }

                var followUps = Queue.DrainFollowUp();
                prompts = followUps.Select(message => message.Text).ToList();
                delivered.AddRange(prompts);
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            EndRun(linkedCancellation);
        }

        return new LiveTurnResult(string.Concat(assistantText), delivered, cancelled, toolExecutions);
    }

    /// <summary>Requests cancellation of the active execution without clearing queued input.</summary>
    public void Abort()
    {
        lock (_sync)
        {
            _activeCancellation?.Cancel();
        }
    }

    private void BeginRun(CancellationToken cancellationToken, out CancellationTokenSource linkedCancellation)
    {
        lock (_sync)
        {
            if (_running)
            {
                throw new InvalidOperationException("An agent turn is already running.");
            }

            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linkedCancellation;
            _running = true;
        }
    }

    private void EndRun(CancellationTokenSource linkedCancellation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeCancellation, linkedCancellation))
            {
                _activeCancellation = null;
                _running = false;
            }
        }

        linkedCancellation.Dispose();
    }
}
